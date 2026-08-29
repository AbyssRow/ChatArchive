using System.Globalization;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

internal static class CipherTalkExcelParser
{
    private static readonly string[] CoreHeaders =
    [
        "序号", "时间", "日期", "时刻", "星期", "发送者", "微信ID", "消息类型",
        "消息内容", "原始类型代码", "时间戳"
    ];

    private static readonly string[][] AllowedHeaders =
    [
        CoreHeaders,
        [.. CoreHeaders, "头像链接"],
        [.. CoreHeaders, "聊天记录详情"],
        [.. CoreHeaders, "头像链接", "聊天记录详情"],
    ];

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
        var profile = ReadProfile(workbook, filePath, cancellationToken);
        return new ParsedConversation(
            "wechat",
            "wechat-default",
            ImportText.StableFileNativeId(filePath),
            "private",
            profile.Sheet.Name);
    }

    internal static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var workbook = OpenXmlWorkbookReader.Open(filePath);
        var profile = ReadProfile(workbook, filePath, cancellationToken);
        var messageCount = 0;
        foreach (var row in workbook.ReadRows(profile.Sheet, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowIndex <= 1 || IsBlank(row))
            {
                continue;
            }

            var values = ReadValues(row, profile.Headers);
            if (!TryReadTimestamp(values["时间戳"], values["时间"], out var timestampMs))
            {
                throw new ImportFormatException(
                    filePath,
                    $"工作表 {profile.Sheet.Name} 第 {row.RowIndex} 行时间无效");
            }

            var senderName = FirstNonEmpty(values["发送者"], "unknown");
            var senderNativeId = FirstNonEmpty(
                values["微信ID"],
                FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, senderName));
            var messageType = MapType(values["原始类型代码"], values["消息类型"]);
            var content = values["消息内容"];
            var details = Value(values, "聊天记录详情");
            if (details.Length > 0)
            {
                content = content.Length == 0 ? details : string.Concat(content, "\n", details);
            }

            var raw = new JsonObject();
            foreach (var header in profile.Headers)
            {
                raw[header.Key] = values[header.Key];
            }

            var isSystem = messageType == "system";
            messageCount++;
            yield return FlatMessageFactory.Create(new FlatMessageData(
                NativeId: null,
                LocalId: NullIfEmpty(values["序号"]),
                TimestampMs: timestampMs,
                SenderNativeId: senderNativeId,
                SenderName: senderName,
                Direction: isSystem ? "system" : "incoming",
                MessageType: messageType,
                Content: content,
                SourceLocator: $"{profile.Sheet.Name}:{row.RowIndex}",
                RawPayload: raw,
                Sequence: row.RowIndex.ToString(CultureInfo.InvariantCulture),
                IsSystem: isSystem,
                MediaType: messageType is "text" or "system" ? null : messageType));
        }

        if (messageCount == 0)
        {
            throw new ImportFormatException(filePath, $"工作表 {profile.Sheet.Name} 中没有有效消息");
        }
    }

    private static Profile ReadProfile(
        OpenXmlWorkbookReader workbook,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!TryReadProfile(workbook, cancellationToken, out var profile))
        {
            throw new ImportFormatException(filePath, "不是当前 CipherTalk Excel 导出");
        }

        return profile;
    }

    private static bool TryReadProfile(
        OpenXmlWorkbookReader workbook,
        CancellationToken cancellationToken,
        out Profile profile)
    {
        profile = null!;
        foreach (var sheet in workbook.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rows = workbook.ReadRows(sheet, cancellationToken).GetEnumerator();
            if (!rows.MoveNext() || rows.Current.RowIndex != 1)
            {
                continue;
            }

            foreach (var headers in AllowedHeaders)
            {
                if (!HasExactHeaders(rows.Current, headers))
                {
                    continue;
                }

                profile = new Profile(sheet, HeaderMap(headers));
                return true;
            }
        }

        return false;
    }

    private static bool TryReadTimestamp(string numericSeconds, string displayedTime, out long timestampMs)
    {
        timestampMs = 0;
        if (decimal.TryParse(numericSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            try
            {
                timestampMs = decimal.ToInt64(decimal.Truncate(seconds * 1000m));
                return true;
            }
            catch (OverflowException)
            {
                // Fall back to the displayed value below.
            }
        }

        return ImportText.TryParseFlexibleTimestamp(displayedTime, out timestampMs);
    }

    private static string MapType(string rawType, string label)
    {
        var mapped = rawType.Trim() switch
        {
            "1" => "text",
            "3" => "image",
            "34" => "audio",
            "43" => "video",
            "47" => "emoji",
            "49" => "link",
            "10000" => "system",
            _ => null,
        };
        if (mapped is not null)
        {
            return mapped;
        }

        return label.Trim() switch
        {
            "文本消息" => "text",
            "图片消息" => "image",
            "语音消息" => "audio",
            "视频消息" => "video",
            "动画表情" or "表情消息" => "emoji",
            "文件消息" => "file",
            "链接消息" or "聊天记录" or "音乐分享" or "小程序消息" => "link",
            "引用消息" => "reply",
            "位置消息" => "location",
            "系统消息" or "群公告" => "system",
            _ => "other",
        };
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
        OpenXmlSheet Sheet,
        IReadOnlyDictionary<string, int> Headers);
}
