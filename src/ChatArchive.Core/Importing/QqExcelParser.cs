using System.Globalization;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

internal readonly record struct QqExcelJoinKey(string Time, string Uin, string Sender);

internal static class QqExcelParser
{
    private static readonly string[] MessageHeaders =
        ["序号", "时间", "发送者", "发送者QQ号", "消息类型", "消息内容", "是否撤回", "资源数量"];
    private static readonly string[] TitledMessageHeaders =
        ["序号", "时间", "发送者", "发送者QQ号", "群头衔", "消息类型", "消息内容", "是否撤回", "资源数量"];
    private static readonly string[] ResourceHeaders =
        ["序号", "时间", "发送者", "发送者QQ号", "资源类型", "文件名", "大小(字节)", "URL"];

    internal static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var workbook = OpenXmlWorkbookReader.Open(filePath);
            return TryReadProfile(workbook, CancellationToken.None, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportFormatException)
        {
            return false;
        }
    }

    internal static ParsedConversation ReadConversation(string filePath, CancellationToken cancellationToken)
    {
        using var workbook = OpenXmlWorkbookReader.Open(filePath);
        _ = ReadProfile(workbook, filePath, cancellationToken);
        return new ParsedConversation(
            "qq",
            "qq-default",
            ImportText.StableFileNativeId(filePath),
            "private",
            Path.GetFileNameWithoutExtension(filePath));
    }

    internal static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var workbook = OpenXmlWorkbookReader.Open(filePath);
        var profile = ReadProfile(workbook, filePath, cancellationToken);
        var keyCounts = CountMessageKeys(workbook, profile, filePath, cancellationToken);
        if (keyCounts.Count == 0)
        {
            throw new ImportFormatException(filePath, "聊天记录中没有有效消息");
        }

        var attachments = ReadAttachments(
            workbook,
            profile,
            keyCounts,
            Path.GetDirectoryName(Path.GetFullPath(filePath))!,
            filePath,
            cancellationToken);
        var messageCount = 0;
        foreach (var row in workbook.ReadRows(profile.MessageSheet, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowIndex <= 1 || IsBlank(row))
            {
                continue;
            }

            var message = ReadMessageRow(row, profile.MessageHeaders, filePath);
            var key = JoinKey(message.Time, message.Uin, message.Sender);
            var senderName = FirstNonEmpty(message.Sender, "unknown");
            var senderNativeId = FirstNonEmpty(
                message.Uin,
                FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, senderName));
            var messageType = MapMessageType(message.Type);
            var isSystem = messageType == "system";
            var raw = RawPayload(message.Values);
            var messageAttachments = keyCounts[key] == 1 && attachments.TryGetValue(key, out var joined)
                ? joined
                : Array.Empty<ParsedAttachment>();

            messageCount++;
            yield return FlatMessageFactory.Create(new FlatMessageData(
                NativeId: null,
                LocalId: NullIfEmpty(message.Number),
                TimestampMs: message.TimestampMs,
                SenderNativeId: senderNativeId,
                SenderName: senderName,
                Direction: isSystem ? "system" : "incoming",
                MessageType: messageType,
                Content: message.Content,
                SourceLocator: $"聊天记录:{row.RowIndex}",
                RawPayload: raw,
                Attachments: messageAttachments,
                Sequence: row.RowIndex.ToString(CultureInfo.InvariantCulture),
                IsRecalled: message.Recalled,
                IsSystem: isSystem,
                MediaType: messageAttachments.FirstOrDefault()?.Kind));
        }

        if (messageCount == 0)
        {
            throw new ImportFormatException(filePath, "聊天记录中没有有效消息");
        }
    }

    private static Dictionary<QqExcelJoinKey, int> CountMessageKeys(
        OpenXmlWorkbookReader workbook,
        Profile profile,
        string filePath,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<QqExcelJoinKey, int>();
        var messageCount = 0;
        foreach (var row in workbook.ReadRows(profile.MessageSheet, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowIndex <= 1 || IsBlank(row))
            {
                continue;
            }

            var message = ReadMessageRow(row, profile.MessageHeaders, filePath);
            var key = JoinKey(message.Time, message.Uin, message.Sender);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            messageCount++;
        }

        if (messageCount == 0)
        {
            throw new ImportFormatException(filePath, "聊天记录中没有有效消息");
        }

        return counts;
    }

    private static Dictionary<QqExcelJoinKey, IReadOnlyList<ParsedAttachment>> ReadAttachments(
        OpenXmlWorkbookReader workbook,
        Profile profile,
        IReadOnlyDictionary<QqExcelJoinKey, int> keyCounts,
        string exportRoot,
        string filePath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<QqExcelJoinKey, List<ParsedAttachment>>();
        if (profile.ResourceSheet is null || profile.ResourceHeaders is null)
        {
            return result.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ParsedAttachment>)pair.Value);
        }

        foreach (var row in workbook.ReadRows(profile.ResourceSheet, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowIndex <= 1 || IsBlank(row))
            {
                continue;
            }

            var values = ReadValues(row, profile.ResourceHeaders);
            var key = JoinKey(values["时间"], values["发送者QQ号"], values["发送者"]);
            if (!keyCounts.TryGetValue(key, out var occurrences) || occurrences != 1)
            {
                continue;
            }

            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            var url = values["URL"];
            var localPath = IsRelativeLocalPath(url) ? url : null;
            var size = ParseSize(values["大小(字节)"], filePath, row.RowIndex);
            var filename = NullIfEmpty(values["文件名"])
                ?? (localPath is null ? null : Path.GetFileName(localPath));
            list.Add(new ParsedAttachment(
                Ordinal: list.Count,
                Kind: MapResourceKind(values["资源类型"]),
                Filename: filename,
                DeclaredPath: localPath,
                SourcePath: localPath is null ? null : ImportText.SafeResolveMedia(exportRoot, localPath),
                DeclaredSize: size,
                MimeType: ImportText.GuessMime(localPath, filename),
                Width: null,
                Height: null,
                Duration: null,
                Metadata: new JsonObject
                {
                    ["resourceType"] = values["资源类型"],
                    ["url"] = url,
                    ["resourceNumber"] = values["序号"],
                }));
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ParsedAttachment>)pair.Value);
    }

    private static Profile ReadProfile(
        OpenXmlWorkbookReader workbook,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!TryReadProfile(workbook, cancellationToken, out var profile))
        {
            throw new ImportFormatException(filePath, "不是当前 QQ Chat Exporter Excel 导出");
        }

        return profile;
    }

    private static bool TryReadProfile(
        OpenXmlWorkbookReader workbook,
        CancellationToken cancellationToken,
        out Profile profile)
    {
        profile = null!;
        var messageSheets = workbook.Sheets.Where(sheet => sheet.Name == "聊天记录").ToList();
        var resourceSheets = workbook.Sheets.Where(sheet => sheet.Name == "资源列表").ToList();
        if (messageSheets.Count != 1 || resourceSheets.Count > 1)
        {
            return false;
        }

        var messageSheet = messageSheets[0];
        var messageHeaders = ReadFirstRowHeaders(
            workbook,
            messageSheet,
            [MessageHeaders, TitledMessageHeaders],
            cancellationToken);
        if (messageHeaders is null)
        {
            return false;
        }

        OpenXmlSheet? resourceSheet = resourceSheets.SingleOrDefault();
        IReadOnlyDictionary<string, int>? resourceHeaders = null;
        if (resourceSheet is not null)
        {
            resourceHeaders = ReadFirstRowHeaders(
                workbook,
                resourceSheet,
                [ResourceHeaders],
                cancellationToken);
            if (resourceHeaders is null)
            {
                return false;
            }
        }

        profile = new Profile(messageSheet, messageHeaders, resourceSheet, resourceHeaders);
        return true;
    }

    private static IReadOnlyDictionary<string, int>? ReadFirstRowHeaders(
        OpenXmlWorkbookReader workbook,
        OpenXmlSheet sheet,
        IReadOnlyList<string[]> candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var rows = workbook.ReadRows(sheet, cancellationToken).GetEnumerator();
        if (!rows.MoveNext() || rows.Current.RowIndex != 1)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (HasExactHeaders(rows.Current, candidate))
            {
                return HeaderMap(candidate);
            }
        }

        return null;
    }

    private static MessageRow ReadMessageRow(
        OpenXmlRow row,
        IReadOnlyDictionary<string, int> headers,
        string filePath)
    {
        var values = ReadValues(row, headers);
        var time = values["时间"];
        if (!ImportText.TryParseFlexibleTimestamp(time, out var timestampMs))
        {
            throw new ImportFormatException(filePath, $"聊天记录第 {row.RowIndex} 行时间无效：{time}");
        }

        return new MessageRow(
            values["序号"],
            time,
            values["发送者"],
            values["发送者QQ号"],
            Value(values, "群头衔"),
            values["消息类型"],
            values["消息内容"],
            values["是否撤回"] == "是",
            values)
        {
            TimestampMs = timestampMs,
        };
    }

    private static QqExcelJoinKey JoinKey(string time, string uin, string sender) =>
        new(ImportText.Clean(time), ImportText.Clean(uin), ImportText.Clean(sender));

    private static string MapMessageType(string value) => value.Trim() switch
    {
        "文本" => "text",
        "图片" => "image",
        "视频" => "video",
        "音频" => "audio",
        "文件" => "file",
        "表情" => "face",
        "@提及" => "at",
        "回复" => "reply",
        "系统消息" => "system",
        "" => "unknown",
        var other => other.ToLowerInvariant(),
    };

    private static string MapResourceKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "image" or "图片" => "image",
        "video" or "视频" => "video",
        "audio" or "音频" or "voice" => "audio",
        "file" or "文件" => "file",
        "face" or "emoji" or "表情" => "emoji",
        _ => "file",
    };

    private static bool IsRelativeLocalPath(string value)
    {
        if (value.Length == 0 || value[0] is '/' or '\\' || Path.IsPathRooted(value))
        {
            return false;
        }

        return !Uri.TryCreate(value, UriKind.Absolute, out _);
    }

    private static long? ParseSize(string value, string filePath, uint rowIndex)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
            && size >= 0
            && size <= long.MaxValue)
        {
            return decimal.ToInt64(decimal.Truncate(size));
        }

        throw new ImportFormatException(filePath, $"资源列表第 {rowIndex} 行大小无效：{value}");
    }

    private static IReadOnlyDictionary<string, int> HeaderMap(IReadOnlyList<string> headers) =>
        headers.Select((header, index) => new KeyValuePair<string, int>(header, index + 1))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> ReadValues(
        OpenXmlRow row,
        IReadOnlyDictionary<string, int> headers) =>
        headers.ToDictionary(
            pair => pair.Key,
            pair => CellValue(row, pair.Value),
            StringComparer.Ordinal);

    private static JsonObject RawPayload(IReadOnlyDictionary<string, string> values)
    {
        var raw = new JsonObject();
        foreach (var value in values)
        {
            raw[value.Key] = value.Value;
        }

        return raw;
    }

    private static bool HasExactHeaders(OpenXmlRow row, IReadOnlyList<string> expected)
    {
        if (row.Cells.Values.Any(cell =>
            cell.ColumnIndex > expected.Count && ImportText.Clean(cell.Value).Length > 0))
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!row.Cells.TryGetValue(index + 1, out var cell)
                || !string.Equals(ImportText.Clean(cell.Value), expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBlank(OpenXmlRow row) =>
        row.Cells.Values.All(cell => ImportText.Clean(cell.Value).Length == 0);

    private static string CellValue(OpenXmlRow row, int column) =>
        row.Cells.TryGetValue(column, out var cell) ? ImportText.Clean(cell.Value) : string.Empty;

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record Profile(
        OpenXmlSheet MessageSheet,
        IReadOnlyDictionary<string, int> MessageHeaders,
        OpenXmlSheet? ResourceSheet,
        IReadOnlyDictionary<string, int>? ResourceHeaders);

    private sealed record MessageRow(
        string Number,
        string Time,
        string Sender,
        string Uin,
        string Title,
        string Type,
        string Content,
        bool Recalled,
        IReadOnlyDictionary<string, string> Values)
    {
        internal long TimestampMs { get; init; }
    }
}
