using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

/// <summary>RFC 4180 compatible streaming CSV reader.</summary>
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
                yield break;
            }

            var character = (char)next;
            if (inQuotes)
            {
                if (character == '"' && reader.Peek() == '"')
                {
                    reader.Read();
                    field.Append('"');
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(character);
                }
                continue;
            }

            switch (character)
            {
                case '"': inQuotes = true; hasField = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); hasField = true; break;
                case '\r':
                    if (reader.Peek() == '\n') reader.Read();
                    record.Add(field.ToString()); field.Clear(); hasField = false;
                    yield return record; record = new List<string>(); break;
                case '\n':
                    record.Add(field.ToString()); field.Clear(); hasField = false;
                    yield return record; record = new List<string>(); break;
                default: field.Append(character); hasField = true; break;
            }
        }
    }
}

/// <summary>Current WeFlow WeClone CSV parser (platform: wechat).</summary>
public static class WeFlowCsvParser
{
    private static readonly string[] CurrentHeaders = ["id", "MsgSvrID", "type_name", "is_sender", "talker", "msg", "src", "CreateTime"];

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return HeadersMatch(Rfc4180CsvReader.ReadRecords(reader).FirstOrDefault());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        if (!Matches(filePath)) throw new ImportFormatException(filePath, "不是当前 WeFlow CSV 导出");
        return new ParsedConversation("wechat", "wechat-default", ImportText.StableFileNativeId(filePath), "private", Path.GetFileNameWithoutExtension(filePath));
    }

    public static IEnumerable<ParsedMessage> IterateMessages(string filePath, ParsedConversation conversation, CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        if (!HeadersMatch(Rfc4180CsvReader.ReadRecords(reader).FirstOrDefault())) throw new ImportFormatException(filePath, "不是当前 WeFlow CSV 导出");

        var rowNumber = 1;
        foreach (var row in Rfc4180CsvReader.ReadRecords(reader))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            if (row.Count == 0 || row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
            if (row.Count != CurrentHeaders.Length) throw new ImportFormatException(filePath, $"第 {rowNumber} 行 CSV 列数必须为 {CurrentHeaders.Length}，实际为 {row.Count}");

            var values = row.ToArray();
            var raw = new JsonObject();
            for (var i = 0; i < CurrentHeaders.Length; i++) raw[CurrentHeaders[i]] = values[i];
            if (!ImportText.TryParseFlexibleTimestamp(values[7], out var timestampMs)) throw new ImportFormatException(filePath, $"第 {rowNumber} 行 CreateTime 无效");

            var messageType = MapType(values[2]);
            var declaredPath = values[6];
            var attachments = string.IsNullOrWhiteSpace(declaredPath) ? Array.Empty<ParsedAttachment>() :
            [new ParsedAttachment(0, AttachmentKind(messageType, declaredPath), Path.GetFileName(declaredPath), declaredPath,
                ImportText.SafeResolveMedia(
                    exportRoot,
                    declaredPath,
                    conversation.Title,
                    MediaResolutionPolicy.WeFlowLayoutA),
                null, ImportText.GuessMime(declaredPath), null, null, null, new JsonObject())];
            var sender = values[4];
            yield return FlatMessageFactory.Create(new FlatMessageData(
                string.IsNullOrEmpty(values[1]) ? null : values[1], string.IsNullOrEmpty(values[0]) ? null : values[0], timestampMs,
                FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, sender), sender,
                values[3].Trim() == "1" ? "outgoing" : "incoming", messageType, values[5], $"row:{rowNumber}", raw, attachments,
                MediaType: messageType == "text" ? null : messageType));
        }
    }

    private static bool HeadersMatch(IReadOnlyList<string>? headers) => headers is not null && headers.Count == CurrentHeaders.Length
        && string.Equals(headers[0], CurrentHeaders[0], StringComparison.Ordinal)
        && headers.Skip(1).SequenceEqual(CurrentHeaders.Skip(1), StringComparer.Ordinal);

    private static string MapType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "image" => "image", "sticker" => "emoji", "video" => "video", "voice" => "audio", "location" => "location", "file" => "file", _ => "text"
    };

    private static string AttachmentKind(string messageType, string declaredPath) => messageType != "text" ? messageType : ImportText.GuessMime(declaredPath)?.Split('/')[0] switch
    {
        "image" => "image", "video" => "video", "audio" => "audio", _ => "file"
    };
}

/// <summary>Current WeFlow Markdown export parser (platform: wechat).</summary>
public static class WeFlowMarkdownParser
{
    private static readonly Regex SessionIdRegex = new(@"^- 会话ID:\s*`(?<id>.*)`\s*$", RegexOptions.Compiled);
    private static readonly Regex SessionTypeRegex = new(@"^- 会话类型:\s*(?<type>群聊|私聊)\s*$", RegexOptions.Compiled);
    private static readonly Regex MessageHeaderRegex = new(@"^##\s+(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+(?<sender>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(@"!?\[(?<label>[^\]]*)\]\((?<path>[^)]+)\)", RegexOptions.Compiled);

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".md", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var exporter = false;
            var sessionId = false;
            var sessionType = false;
            var messageHeader = false;
            var nonEmptyLines = 0;
            string? line;
            while (nonEmptyLines < 100 && (line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                nonEmptyLines++;
                exporter |= string.Equals(line, "- 导出工具: WeFlow", StringComparison.Ordinal);
                var id = SessionIdRegex.Match(line);
                sessionId |= id.Success && !string.IsNullOrWhiteSpace(id.Groups["id"].Value);
                sessionType |= SessionTypeRegex.IsMatch(line);
                var header = MessageHeaderRegex.Match(line);
                messageHeader |= header.Success && ImportText.TryParseFlexibleTimestamp(header.Groups["time"].Value, out _);
            }
            return exporter && sessionId && sessionType && messageHeader;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        if (!Matches(filePath)) throw new ImportFormatException(filePath, "不是当前 WeFlow Markdown 导出");
        var metadata = ReadMetadata(filePath);
        return new ParsedConversation("wechat", "wechat-default", metadata.SessionId, metadata.Kind, metadata.Title);
    }

    public static IEnumerable<ParsedMessage> IterateMessages(string filePath, ParsedConversation conversation, CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? sender = null;
        string? timestamp = null;
        var body = new List<string>();
        var index = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = MessageHeaderRegex.Match(line);
            if (!header.Success)
            {
                if (sender is not null) body.Add(line);
                continue;
            }
            if (!ImportText.TryParseFlexibleTimestamp(header.Groups["time"].Value, out _)) throw new ImportFormatException(filePath, $"第 {index + 1} 个 Markdown 消息时间无效");
            if (sender is not null && timestamp is not null)
            {
                TrimTrailingBlankLines(body);
                yield return BuildMessage(filePath, exportRoot, conversation, index++, timestamp, sender, body);
                body.Clear();
            }
            timestamp = header.Groups["time"].Value;
            sender = header.Groups["sender"].Value.Trim();
        }
        if (sender is null || timestamp is null) throw new ImportFormatException(filePath, "未找到有效的 WeFlow Markdown 消息");
        TrimTrailingBlankLines(body);
        yield return BuildMessage(filePath, exportRoot, conversation, index, timestamp, sender, body);
    }

    private static Metadata ReadMetadata(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            string? title = null;
            string? sessionId = null;
            string? kind = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (title is null && line.StartsWith("# ", StringComparison.Ordinal)) title = line[2..].Trim();
                var id = SessionIdRegex.Match(line);
                if (id.Success) sessionId = id.Groups["id"].Value.Trim();
                var type = SessionTypeRegex.Match(line);
                if (type.Success) kind = type.Groups["type"].Value == "群聊" ? "group" : "private";
            }
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(kind)) throw new ImportFormatException(filePath, "WeFlow Markdown 元数据不完整");
            return new Metadata(title, sessionId, kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }

    private static ParsedMessage BuildMessage(string filePath, string exportRoot, ParsedConversation conversation, int index, string time, string sender, IReadOnlyList<string> body)
    {
        if (!ImportText.TryParseFlexibleTimestamp(time, out var timestampMs)) throw new ImportFormatException(filePath, $"第 {index + 1} 个 Markdown 消息时间无效");
        var content = string.Join("\n", body);
        var raw = new JsonObject { ["time"] = time, ["sender"] = sender, ["content"] = content };
        var message = FlatMessageFactory.Create(new FlatMessageData(
            null, index.ToString(), timestampMs, FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, sender), sender,
            sender == "我" ? "outgoing" : "incoming", "text", content, $"message:{index}", raw, ExtractAttachments(exportRoot, conversation.Title, content)));
        return message with { SearchText = ReadableSearchText(content) };
    }

    private static IReadOnlyList<ParsedAttachment> ExtractAttachments(string exportRoot, string title, string content)
    {
        var attachments = new List<ParsedAttachment>();
        foreach (Match match in MarkdownLinkRegex.Matches(content))
        {
            var declaredPath = match.Groups["path"].Value.Trim();
            if (declaredPath.Length == 0) continue;
            var label = match.Groups["label"].Value.Trim();
            var mime = ImportText.GuessMime(declaredPath);
            var kind = match.Value.StartsWith('!') || mime?.StartsWith("image/", StringComparison.Ordinal) == true ? "image"
                : mime?.StartsWith("video/", StringComparison.Ordinal) == true ? "video"
                : mime?.StartsWith("audio/", StringComparison.Ordinal) == true ? "audio" : "file";
            attachments.Add(new ParsedAttachment(attachments.Count, kind, label.Length > 0 ? label : Path.GetFileName(declaredPath), declaredPath,
                ImportText.SafeResolveMedia(
                    exportRoot,
                    declaredPath,
                    title,
                    MediaResolutionPolicy.WeFlowLayoutA),
                null, mime, null, null, null, new JsonObject()));
        }
        return attachments;
    }

    private static string ReadableSearchText(string content)
    {
        var withoutLinks = MarkdownLinkRegex.Replace(content, match =>
        {
            var label = match.Groups["label"].Value.Trim();
            return label.Length > 0 ? label : match.Groups["path"].Value.Trim();
        });
        return string.Join("\n", withoutLinks.Split('\n').Select(line =>
        {
            var leadingTrimmed = line.TrimStart();
            return leadingTrimmed.StartsWith(">", StringComparison.Ordinal) ? leadingTrimmed[1..].TrimStart() : line;
        }));
    }

    private static void TrimTrailingBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
    }

    private sealed record Metadata(string Title, string SessionId, string Kind);
}

/// <summary>Current WeFlow TXT export parser (platform: wechat).</summary>
public static class WeFlowTextParser
{
    private static readonly Regex MessageHeaderRegex = new(@"^(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+'(?<sender>[^'\r\n]+)'\s*$", RegexOptions.Compiled);

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var awaitingContent = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (awaitingContent && !MessageHeaderRegex.IsMatch(line)) return true;
                var header = MessageHeaderRegex.Match(line);
                awaitingContent = header.Success && ImportText.TryParseFlexibleTimestamp(header.Groups["time"].Value, out _);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        if (!Matches(filePath)) throw new ImportFormatException(filePath, "不是当前 WeFlow TXT 导出");
        return new ParsedConversation("wechat", "wechat-default", ImportText.StableFileNativeId(filePath), "private", Path.GetFileNameWithoutExtension(filePath));
    }

    public static IEnumerable<ParsedMessage> IterateMessages(string filePath, ParsedConversation conversation, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? sender = null;
        string? timestamp = null;
        var body = new List<string>();
        var index = 0;
        var hasCurrentContentLine = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = MessageHeaderRegex.Match(line);
            var isValidHeader = header.Success
                && ImportText.TryParseFlexibleTimestamp(header.Groups["time"].Value, out _);
            var isMessageBoundary = sender is null
                || body.Count > 0 && string.IsNullOrWhiteSpace(body[^1]);
            if (!isValidHeader || !isMessageBoundary)
            {
                if (sender is not null)
                {
                    body.Add(line);
                    hasCurrentContentLine |= !string.IsNullOrWhiteSpace(line);
                }
                continue;
            }
            if (sender is not null && timestamp is not null)
            {
                if (!hasCurrentContentLine) throw new ImportFormatException(filePath, $"第 {index + 1} 个 TXT 消息缺少正文");
                yield return BuildMessage(filePath, conversation, index++, timestamp, sender, body);
                body.Clear();
                hasCurrentContentLine = false;
            }
            timestamp = header.Groups["time"].Value;
            sender = header.Groups["sender"].Value;
        }
        if (sender is null || timestamp is null) throw new ImportFormatException(filePath, "未找到有效的 WeFlow TXT 消息");
        if (!hasCurrentContentLine) throw new ImportFormatException(filePath, $"第 {index + 1} 个 TXT 消息缺少正文");
        yield return BuildMessage(filePath, conversation, index, timestamp, sender, body);
    }

    private static ParsedMessage BuildMessage(string filePath, ParsedConversation conversation, int index, string time, string sender, List<string> body)
    {
        if (!ImportText.TryParseFlexibleTimestamp(time, out var timestampMs)) throw new ImportFormatException(filePath, $"第 {index + 1} 个 TXT 消息时间无效");
        while (body.Count > 0 && string.IsNullOrWhiteSpace(body[^1])) body.RemoveAt(body.Count - 1);
        var content = string.Join("\n", body);
        var raw = new JsonObject { ["time"] = time, ["sender"] = sender, ["content"] = content };
        return FlatMessageFactory.Create(new FlatMessageData(
            null, index.ToString(), timestampMs, FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, sender), sender,
            sender == "我" ? "outgoing" : "incoming", "text", content, $"message:{index}", raw));
    }
}
