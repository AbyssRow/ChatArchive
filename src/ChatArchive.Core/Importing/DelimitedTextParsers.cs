using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

/// <summary>
/// RFC 4180 兼容的 CSV 流式读取器。
/// </summary>
public static class Rfc4180CsvReader
{
    public static IEnumerable<IReadOnlyList<string>> ReadRecords(TextReader reader)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var hasField = false;

        while (true)
        {
            var next = reader.Read();
            if (next == -1)
            {
                if (hasField || record.Count > 0)
                {
                    record.Add(field.ToString());
                    yield return record;
                }
                break;
            }

            var c = (char)next;

            if (inQuotes)
            {
                if (c == '"')
                {
                    var peek = reader.Peek();
                    if (peek == '"')
                    {
                        reader.Read(); // Consume second quote
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    hasField = true;
                }
                else if (c == ',')
                {
                    record.Add(field.ToString());
                    field.Clear();
                    hasField = true;
                }
                else if (c == '\r')
                {
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    record.Add(field.ToString());
                    field.Clear();
                    hasField = false;
                    yield return record;
                    record = new List<string>();
                }
                else if (c == '\n')
                {
                    record.Add(field.ToString());
                    field.Clear();
                    hasField = false;
                    yield return record;
                    record = new List<string>();
                }
                else
                {
                    field.Append(c);
                    hasField = true;
                }
            }
        }
    }
}

/// <summary>
/// WeClone CSV 格式解析器（平台：wechat）。
/// </summary>
public static class WeCloneCsvParser
{
    private static readonly HashSet<string> RequiredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "is_sender", "talker", "content"
    };

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return false;
            }

            using var stringReader = new StringReader(firstLine);
            var records = Rfc4180CsvReader.ReadRecords(stringReader).FirstOrDefault();
            if (records == null || records.Count == 0)
            {
                return false;
            }

            var headerSet = new HashSet<string>(records.Select(r => r.Trim()), StringComparer.OrdinalIgnoreCase);
            return RequiredHeaders.IsSubsetOf(headerSet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var headers = Rfc4180CsvReader.ReadRecords(reader).FirstOrDefault();
            if (headers == null)
            {
                throw new ImportFormatException(filePath, "WeClone CSV 缺少表头");
            }

            var colMap = MapHeaders(headers);
            string? firstTalker = null;
            string? firstPeerSenderName = null;

            foreach (var row in Rfc4180CsvReader.ReadRecords(reader))
            {
                var talker = GetCol(row, colMap, "talker");
                if (!string.IsNullOrEmpty(talker) && firstTalker == null)
                {
                    firstTalker = talker;
                }

                var isSenderStr = GetCol(row, colMap, "is_sender") ?? GetCol(row, colMap, "issend");
                var isSender = isSenderStr == "1" || string.Equals(isSenderStr, "true", StringComparison.OrdinalIgnoreCase);
                var senderName = GetCol(row, colMap, "sender_name") ?? GetCol(row, colMap, "sender");

                if (!isSender && !string.IsNullOrEmpty(senderName) && firstPeerSenderName == null)
                {
                    firstPeerSenderName = senderName;
                }

                if (firstTalker != null && firstPeerSenderName != null)
                {
                    break;
                }
            }

            var nativeId = firstTalker ?? Path.GetFileNameWithoutExtension(filePath);
            var isGroup = nativeId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) || nativeId.Contains("群");
            var kind = isGroup ? "group" : "private";
            var title = firstPeerSenderName ?? nativeId;

            return new ParsedConversation("wechat", "wechat-default", nativeId, kind, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headers = Rfc4180CsvReader.ReadRecords(reader).FirstOrDefault();
        if (headers == null)
        {
            yield break;
        }

        var colMap = MapHeaders(headers);
        var rowIndex = 0;

        foreach (var row in Rfc4180CsvReader.ReadRecords(reader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Count == 0 || (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])))
            {
                continue;
            }

            var isSenderStr = GetCol(row, colMap, "is_sender") ?? GetCol(row, colMap, "issend");
            var isSender = isSenderStr == "1" || string.Equals(isSenderStr, "true", StringComparison.OrdinalIgnoreCase);
            var senderName = GetCol(row, colMap, "sender_name") ?? GetCol(row, colMap, "sender") ?? (isSender ? "我" : conversation.Title);
            var talker = GetCol(row, colMap, "talker") ?? conversation.NativeId;
            var timeStr = GetCol(row, colMap, "time") ?? GetCol(row, colMap, "create_time") ?? string.Empty;
            var typeStr = GetCol(row, colMap, "type") ?? "1";
            var content = GetCol(row, colMap, "content") ?? string.Empty;

            var timestampMs = ParseTimestamp(timeStr);
            var typeInt = int.TryParse(typeStr, out var parsedType) ? parsedType : 1;
            var messageType = ResolveMessageType(typeInt);
            var isSystem = typeInt == 10000 || messageType == "system";
            var isRecalled = isSystem && content.Contains("撤回", StringComparison.Ordinal);
            var direction = isSystem ? "system" : (isSender ? "outgoing" : "incoming");
            var senderNative = isSender ? "wxid_self" : talker;

            var rawPayload = new JsonObject();
            for (var i = 0; i < headers.Count && i < row.Count; i++)
            {
                rawPayload[headers[i]] = row[i];
            }

            var semantic = new JsonObject
            {
                ["timestamp_ms"] = timestampMs,
                ["sender"] = senderNative,
                ["direction"] = direction,
                ["message_type"] = messageType,
                ["content"] = content,
                ["search_text"] = content,
            };

            var payloadHash = CanonicalJson.HashHex(semantic);
            var semanticHash = CanonicalJson.HashHex(new JsonObject
            {
                ["timestamp_ms"] = timestampMs,
                ["sender"] = senderNative,
                ["direction"] = direction,
            });

            var senderAliases = new List<string>();
            if (!string.IsNullOrEmpty(senderName))
            {
                senderAliases.Add(senderName);
            }
            if (!senderAliases.Contains(senderNative, StringComparer.OrdinalIgnoreCase))
            {
                senderAliases.Add(senderNative);
            }

            yield return new ParsedMessage(
                NativeId: null,
                LocalId: rowIndex.ToString(),
                TimestampMs: timestampMs,
                Sequence: null,
                SenderNativeId: senderNative,
                SenderName: senderName,
                SenderAliases: senderAliases,
                Direction: direction,
                MessageType: messageType,
                MediaType: messageType != "text" && messageType != "system" ? messageType : null,
                Content: content,
                SearchText: content,
                IsRecalled: isRecalled,
                IsSystem: isSystem,
                ReplyToNativeId: null,
                PayloadHash: payloadHash,
                SemanticHash: semanticHash,
                SourceLocator: $"row:{rowIndex}",
                RawPayload: rawPayload,
                Attachments: Array.Empty<ParsedAttachment>(),
                CompatiblePayloadHashes: Array.Empty<string>());

            rowIndex++;
        }
    }

    private static Dictionary<string, int> MapHeaders(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var clean = headers[i].Trim();
            if (!map.ContainsKey(clean))
            {
                map[clean] = i;
            }
        }
        return map;
    }

    private static string? GetCol(IReadOnlyList<string> row, Dictionary<string, int> map, string colName)
    {
        if (map.TryGetValue(colName, out var idx) && idx < row.Count)
        {
            return row[idx];
        }
        return null;
    }

    private static string ResolveMessageType(int type)
    {
        return type switch
        {
            1 => "text",
            3 => "image",
            34 => "audio",
            43 => "video",
            47 => "emoji",
            49 => "link",
            10000 => "system",
            _ => "text"
        };
    }

    public static long ParseTimestamp(string timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr))
        {
            return 0;
        }

        timeStr = timeStr.Trim();
        if (long.TryParse(timeStr, out var rawLong))
        {
            return rawLong >= 10_000_000_000L ? rawLong : rawLong * 1000L;
        }

        if (DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }

        if (DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var local = TimeZoneInfo.Local;
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var offset = local.GetUtcOffset(unspecified);
            return new DateTimeOffset(unspecified, offset).ToUnixTimeMilliseconds();
        }

        return 0;
    }
}

/// <summary>
/// Markdown 聊天记录解析器（平台：text）。
/// </summary>
public static class MarkdownChatParser
{
    private static readonly Regex TitleRegex = new(
        @"^#\s*(?:聊天记录[:：]\s*|Chat[:：]\s*)?(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MessageRegex = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s*(?<sender>[^:：\n]+)[:：]\s*(?<content>.*)$",
        RegexOptions.Compiled);

    public static bool Matches(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var lineCount = 0;
            string? line;
            while ((line = reader.ReadLine()) != null && lineCount < 50)
            {
                lineCount++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (TitleRegex.IsMatch(trimmed) || MessageRegex.IsMatch(trimmed))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? title = null;
            string? line;
            var lineCount = 0;
            while ((line = reader.ReadLine()) != null && lineCount < 50)
            {
                lineCount++;
                var match = TitleRegex.Match(line.Trim());
                if (match.Success)
                {
                    title = match.Groups[1].Value.Trim();
                    break;
                }
            }

            title ??= Path.GetFileNameWithoutExtension(filePath);
            var isGroup = title.Contains("群") || title.Contains("group", StringComparison.OrdinalIgnoreCase);
            var kind = isGroup ? "group" : "private";

            return new ParsedConversation("text", "text-default", title, kind, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? currentSender = null;
        string? currentTimeStr = null;
        var contentBuilder = new StringBuilder();
        var messageIndex = 0;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var match = MessageRegex.Match(trimmed);
            if (match.Success)
            {
                if (currentSender != null && currentTimeStr != null)
                {
                    yield return BuildMessage(messageIndex++, currentSender, currentTimeStr, contentBuilder.ToString().Trim(), conversation);
                    contentBuilder.Clear();
                }

                currentTimeStr = match.Groups["time"].Value.Trim();
                currentSender = match.Groups["sender"].Value.Trim();
                var inlineContent = match.Groups["content"].Value;
                if (!string.IsNullOrWhiteSpace(inlineContent))
                {
                    contentBuilder.AppendLine(inlineContent.Trim());
                }
            }
            else if (currentSender != null)
            {
                if (trimmed.Length > 0)
                {
                    contentBuilder.AppendLine(trimmed);
                }
            }
        }

        if (currentSender != null && currentTimeStr != null)
        {
            yield return BuildMessage(messageIndex++, currentSender, currentTimeStr, contentBuilder.ToString().Trim(), conversation);
        }
    }

    private static ParsedMessage BuildMessage(
        int index,
        string sender,
        string timeStr,
        string content,
        ParsedConversation conversation)
    {
        var timestampMs = WeCloneCsvParser.ParseTimestamp(timeStr);
        var isSend = sender == "我"
            || string.Equals(sender, "me", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sender, "self", StringComparison.OrdinalIgnoreCase);
        var direction = isSend ? "outgoing" : "incoming";
        var senderNative = isSend ? "self" : sender;

        var rawPayload = new JsonObject
        {
            ["time"] = timeStr,
            ["sender"] = sender,
            ["content"] = content,
        };

        var semantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
            ["message_type"] = "text",
            ["content"] = content,
            ["search_text"] = content,
        };

        var payloadHash = CanonicalJson.HashHex(semantic);
        var semanticHash = CanonicalJson.HashHex(new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
        });

        return new ParsedMessage(
            NativeId: null,
            LocalId: index.ToString(),
            TimestampMs: timestampMs,
            Sequence: null,
            SenderNativeId: senderNative,
            SenderName: sender,
            SenderAliases: new[] { sender },
            Direction: direction,
            MessageType: "text",
            MediaType: null,
            Content: content,
            SearchText: content,
            IsRecalled: false,
            IsSystem: false,
            ReplyToNativeId: null,
            PayloadHash: payloadHash,
            SemanticHash: semanticHash,
            SourceLocator: $"message:{index}",
            RawPayload: rawPayload,
            Attachments: Array.Empty<ParsedAttachment>(),
            CompatiblePayloadHashes: Array.Empty<string>());
    }
}

/// <summary>
/// TXT 纯文本聊天记录解析器（平台：text）。
/// </summary>
public static class TextChatParser
{
    private static readonly Regex HeaderRegex1 = new(
        @"^(?<time>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+(?<sender>[^\n:：]+)[:：]?\s*(?<content>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex HeaderRegex2 = new(
        @"^\[(?<time>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s*(?<sender>[^:：\n]+)[:：]?\s*(?<content>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new(
        @"^(?:消息对象|会话|Chat)[:：]\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var lineCount = 0;
            string? line;
            while ((line = reader.ReadLine()) != null && lineCount < 50)
            {
                lineCount++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (TitleRegex.IsMatch(trimmed) || HeaderRegex1.IsMatch(trimmed) || HeaderRegex2.IsMatch(trimmed))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? title = null;
            string? line;
            var lineCount = 0;
            while ((line = reader.ReadLine()) != null && lineCount < 50)
            {
                lineCount++;
                var match = TitleRegex.Match(line.Trim());
                if (match.Success)
                {
                    title = match.Groups[1].Value.Trim();
                    break;
                }
            }

            title ??= Path.GetFileNameWithoutExtension(filePath);
            var isGroup = title.Contains("群") || title.Contains("group", StringComparison.OrdinalIgnoreCase);
            var kind = isGroup ? "group" : "private";

            return new ParsedConversation("text", "text-default", title, kind, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? currentSender = null;
        string? currentTimeStr = null;
        var contentBuilder = new StringBuilder();
        var messageIndex = 0;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("===", StringComparison.Ordinal) || TitleRegex.IsMatch(trimmed))
            {
                continue;
            }

            var match1 = HeaderRegex1.Match(trimmed);
            var match2 = !match1.Success ? HeaderRegex2.Match(trimmed) : null;
            var match = match1.Success ? match1 : match2;

            if (match != null && match.Success)
            {
                if (currentSender != null && currentTimeStr != null)
                {
                    yield return BuildMessage(messageIndex++, currentSender, currentTimeStr, contentBuilder.ToString().Trim(), conversation);
                    contentBuilder.Clear();
                }

                currentTimeStr = match.Groups["time"].Value.Trim();
                currentSender = match.Groups["sender"].Value.Trim();
                var inlineContent = match.Groups["content"].Value;
                if (!string.IsNullOrWhiteSpace(inlineContent))
                {
                    contentBuilder.AppendLine(inlineContent.Trim());
                }
            }
            else if (currentSender != null)
            {
                contentBuilder.AppendLine(trimmed);
            }
        }

        if (currentSender != null && currentTimeStr != null)
        {
            yield return BuildMessage(messageIndex++, currentSender, currentTimeStr, contentBuilder.ToString().Trim(), conversation);
        }
    }

    private static ParsedMessage BuildMessage(
        int index,
        string sender,
        string timeStr,
        string content,
        ParsedConversation conversation)
    {
        var timestampMs = WeCloneCsvParser.ParseTimestamp(timeStr);
        var isSend = sender == "我"
            || string.Equals(sender, "me", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sender, "self", StringComparison.OrdinalIgnoreCase);
        var direction = isSend ? "outgoing" : "incoming";
        var senderNative = isSend ? "self" : sender;

        var rawPayload = new JsonObject
        {
            ["time"] = timeStr,
            ["sender"] = sender,
            ["content"] = content,
        };

        var semantic = new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
            ["message_type"] = "text",
            ["content"] = content,
            ["search_text"] = content,
        };

        var payloadHash = CanonicalJson.HashHex(semantic);
        var semanticHash = CanonicalJson.HashHex(new JsonObject
        {
            ["timestamp_ms"] = timestampMs,
            ["sender"] = senderNative,
            ["direction"] = direction,
        });

        return new ParsedMessage(
            NativeId: null,
            LocalId: index.ToString(),
            TimestampMs: timestampMs,
            Sequence: null,
            SenderNativeId: senderNative,
            SenderName: sender,
            SenderAliases: new[] { sender },
            Direction: direction,
            MessageType: "text",
            MediaType: null,
            Content: content,
            SearchText: content,
            IsRecalled: false,
            IsSystem: false,
            ReplyToNativeId: null,
            PayloadHash: payloadHash,
            SemanticHash: semanticHash,
            SourceLocator: $"message:{index}",
            RawPayload: rawPayload,
            Attachments: Array.Empty<ParsedAttachment>(),
            CompatiblePayloadHashes: Array.Empty<string>());
    }
}
