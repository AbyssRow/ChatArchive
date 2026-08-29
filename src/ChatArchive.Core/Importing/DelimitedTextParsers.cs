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

/// <summary>Current WeFlow WeClone CSV parser (platform: wechat).</summary>
public static class WeFlowCsvParser
{
    private static readonly string[] CurrentHeaders =
    [
        "id", "MsgSvrID", "type_name", "is_sender", "talker", "msg", "src", "CreateTime"
    ];

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return HeadersMatch(Rfc4180CsvReader.ReadRecords(reader).FirstOrDefault());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        if (!Matches(filePath)) throw new ImportFormatException(filePath, "不是当前 WeFlow CSV 导出");

        return new ParsedConversation(
            "wechat", "wechat-default", ImportText.StableFileNativeId(filePath), "private", Path.GetFileNameWithoutExtension(filePath));
    }

    public static IEnumerable<ParsedMessage> IterateMessages(string filePath, ParsedConversation conversation, CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (!HeadersMatch(Rfc4180CsvReader.ReadRecords(reader).FirstOrDefault()))
        {
            throw new ImportFormatException(filePath, "不是当前 WeFlow CSV 导出");
        }

        var rowNumber = 1;
        foreach (var row in Rfc4180CsvReader.ReadRecords(reader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            if (row.Count == 0 || (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))) continue;

            if (row.Count != CurrentHeaders.Length)
            {
                throw new ImportFormatException(filePath, $"第 {rowNumber} 行 CSV 列数必须为 {CurrentHeaders.Length}，实际为 {row.Count}");
            }

            var values = new string[CurrentHeaders.Length];
            for (var i = 0; i < values.Length; i++) values[i] = row[i];

            var rawPayload = new JsonObject();
            for (var i = 0; i < CurrentHeaders.Length; i++) rawPayload[CurrentHeaders[i]] = values[i];

            var messageType = MapType(values[2]);
            var declaredPath = values[6];
            var attachments = string.IsNullOrWhiteSpace(declaredPath)
                ? Array.Empty<ParsedAttachment>()
                : new[]
                {
                    new ParsedAttachment(0, AttachmentKind(messageType, declaredPath), Path.GetFileName(declaredPath), declaredPath,
                        ImportText.SafeResolveMedia(exportRoot, declaredPath, conversation.Title), null, ImportText.GuessMime(declaredPath),
                        null, null, null, new JsonObject())
                };

            var talker = values[4];
            var timestampMs = ImportText.ParseFlexibleTimestamp(values[7]);
            if (string.IsNullOrWhiteSpace(values[7]) || timestampMs == 0)
            {
                throw new ImportFormatException(filePath, $"第 {rowNumber} 行 CreateTime 无效");
            }

            yield return FlatMessageFactory.Create(new FlatMessageData(
                EmptyToNull(values[1]), EmptyToNull(values[0]), timestampMs,
                FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, talker), talker,
                values[3].Trim() == "1" ? "outgoing" : "incoming", messageType, values[5], $"row:{rowNumber}", rawPayload,
                attachments, MediaType: messageType == "text" ? null : messageType));
        }
    }

    private static bool HeadersMatch(IReadOnlyList<string>? headers) => headers is not null
        && headers.Count == CurrentHeaders.Length
        && string.Equals(headers[0], CurrentHeaders[0], StringComparison.Ordinal)
        && headers.Skip(1).SequenceEqual(CurrentHeaders.Skip(1), StringComparer.Ordinal);

    private static string MapType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "image" => "image", "sticker" => "emoji", "video" => "video", "voice" => "audio", "location" => "location", "file" => "file", _ => "text"
    };

    private static string AttachmentKind(string messageType, string declaredPath)
    {
        if (messageType != "text") return messageType;
        return ImportText.GuessMime(declaredPath)?.Split('/')[0] switch
        {
            "image" => "image", "video" => "video", "audio" => "audio", _ => "file"
        };
    }

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
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
        @"^\[(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s*(?<sender>[^:：\n]+)[:：]\s*(?<content>.*)$",
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
            if (currentSender == null && trimmed.StartsWith("#", StringComparison.Ordinal))
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
        var timestampMs = ImportText.ParseFlexibleTimestamp(timeStr);
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
        @"^(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+(?<sender>[^\n:：]+)[:：]?\s*(?<content>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex HeaderRegex2 = new(
        @"^\[(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s*(?<sender>[^:：\n]+)[:：]?\s*(?<content>.*)$",
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
                if (currentSender != null)
                {
                    contentBuilder.AppendLine();
                }
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
        var timestampMs = ImportText.ParseFlexibleTimestamp(timeStr);
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
