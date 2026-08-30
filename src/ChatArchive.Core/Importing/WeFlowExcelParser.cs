using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

internal static class WeFlowExcelParser
{
    private static readonly string[] CompactHeaders = ["序号", "时间", "发送者身份", "消息类型", "内容"];
    private static readonly string[] PrivateHeaders = ["序号", "时间", "发送者昵称", "发送者微信ID", "发送者备注", "发送者身份", "消息类型", "内容"];
    private static readonly string[] GroupHeaders = ["序号", "时间", "发送者昵称", "发送者微信ID", "发送者备注", "群昵称", "发送者身份", "消息类型", "内容"];

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
        var nativeId = MetadataValue(profile.Metadata, "微信ID");
        if (nativeId.Length == 0)
        {
            nativeId = ImportText.StableFileNativeId(filePath);
        }

        var title = FirstNonEmpty(
            MetadataValue(profile.Metadata, "备注"),
            MetadataValue(profile.Metadata, "昵称"),
            Path.GetFileNameWithoutExtension(filePath));
        var kind = profile.Layout == ExcelLayout.Group || nativeId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)
            ? "group"
            : "private";
        return new ParsedConversation("wechat", "wechat-default", nativeId, kind, title);
    }

    internal static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        using var workbook = OpenXmlWorkbookReader.Open(filePath);
        var profile = ReadProfile(workbook, filePath, cancellationToken);
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var messageCount = 0;
        foreach (var row in workbook.ReadRows(profile.Sheet, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowIndex <= profile.HeaderRow)
            {
                continue;
            }

            if (IsBlank(row))
            {
                continue;
            }

            var values = profile.Headers.ToDictionary(
                pair => pair.Key,
                pair => Value(row, pair.Value),
                StringComparer.Ordinal);
            var timestampText = values["时间"];
            if (!ImportText.TryParseFlexibleTimestamp(timestampText, out var timestampMs))
            {
                throw new ImportFormatException(filePath, $"聊天记录第 {row.RowIndex} 行时间无效：{timestampText}");
            }

            var senderIdentity = FirstNonEmpty(
                values["发送者身份"],
                Value(values, "发送者昵称"),
                Value(values, "群昵称"));
            var senderName = FirstNonEmpty(
                Value(values, "发送者昵称"),
                Value(values, "群昵称"),
                senderIdentity,
                "unknown");
            var senderWechatId = Value(values, "发送者微信ID");
            var senderNativeId = senderWechatId.Length > 0
                ? senderWechatId
                : FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, senderIdentity);
            var messageType = MapType(values["消息类型"]);
            var contentColumn = profile.Headers["内容"];
            var contentCell = row.Cells.TryGetValue(contentColumn, out var cell) ? cell : null;
            var declaredPath = contentCell?.Hyperlink;
            var attachments = string.IsNullOrWhiteSpace(declaredPath)
                ? Array.Empty<ParsedAttachment>()
                :
                [new ParsedAttachment(
                    Ordinal: 0,
                    Kind: AttachmentKind(messageType),
                    Filename: Path.GetFileName(declaredPath),
                    DeclaredPath: declaredPath,
                    SourcePath: ImportText.SafeResolveMedia(
                        exportRoot,
                        declaredPath,
                        conversation.Title,
                        MediaResolutionPolicy.WeFlowLayoutA),
                    DeclaredSize: null,
                    MimeType: ImportText.GuessMime(declaredPath),
                    Width: null,
                    Height: null,
                    Duration: null,
                    Metadata: new JsonObject())];
            var isSystem = messageType == "system";
            var raw = new JsonObject();
            foreach (var header in profile.Headers)
            {
                raw[header.Key] = values[header.Key];
            }

            messageCount++;
            yield return FlatMessageFactory.Create(new FlatMessageData(
                NativeId: null,
                LocalId: NullIfEmpty(values["序号"]),
                TimestampMs: timestampMs,
                SenderNativeId: senderNativeId,
                SenderName: senderName,
                Direction: isSystem ? "system" : conversation.Kind == "private" && senderIdentity == "我" ? "outgoing" : "incoming",
                MessageType: messageType,
                Content: values["内容"],
                SourceLocator: $"聊天记录:{row.RowIndex}",
                RawPayload: raw,
                Attachments: attachments,
                Sequence: row.RowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsSystem: isSystem,
                MediaType: messageType is "text" or "system" ? null : messageType));
        }

        if (messageCount == 0)
        {
            throw new ImportFormatException(filePath, "聊天记录中没有有效消息");
        }
    }

    private static Profile ReadProfile(OpenXmlWorkbookReader workbook, string filePath, CancellationToken cancellationToken)
    {
        if (!TryReadProfile(workbook, cancellationToken, out var profile))
        {
            throw new ImportFormatException(filePath, "不是当前 WeFlow Excel 导出");
        }

        return profile;
    }

    private static bool TryReadProfile(OpenXmlWorkbookReader workbook, CancellationToken cancellationToken, out Profile profile)
    {
        profile = null!;
        var sheets = workbook.Sheets.Where(candidate => candidate.Name == "聊天记录").ToList();
        if (sheets.Count != 1)
        {
            return false;
        }
        var sheet = sheets[0];

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in workbook.ReadRows(sheet, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.RowIndex > 20)
            {
                break;
            }

            AddMetadata(row, metadata);
            if (TryGetLayout(row, out var layout, out var headers))
            {
                if (!string.Equals(MetadataValue(metadata, "会话信息"), "会话信息", StringComparison.Ordinal)
                    || !string.Equals(MetadataValue(metadata, "导出工具"), "WeFlow", StringComparison.Ordinal)
                    || !metadata.ContainsKey("微信ID"))
                {
                    return false;
                }

                profile = new Profile(sheet, row.RowIndex, layout, headers, metadata);
                return true;
            }
        }

        return false;
    }

    private static void AddMetadata(OpenXmlRow row, IDictionary<string, string> metadata)
    {
        foreach (var cell in row.Cells.Values)
        {
            var key = ImportText.Clean(cell.Value);
            if (key == "会话信息")
            {
                metadata[key] = key;
            }
            else if (key is "微信ID" or "昵称" or "备注" or "导出工具" or "导出版本" or "平台" or "导出时间")
            {
                metadata[key] = Value(row, cell.ColumnIndex + 1);
            }
        }
    }

    private static bool TryGetLayout(OpenXmlRow row, out ExcelLayout layout, out IReadOnlyDictionary<string, int> headers)
    {
        foreach (var candidate in new[]
        {
            (ExcelLayout.Compact, CompactHeaders),
            (ExcelLayout.Private, PrivateHeaders),
            (ExcelLayout.Group, GroupHeaders),
        })
        {
            if (!HasExactHeaders(row, candidate.Item2))
            {
                continue;
            }

            layout = candidate.Item1;
            headers = candidate.Item2
                .Select((name, index) => new KeyValuePair<string, int>(name, index + 1))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return true;
        }

        layout = default;
        headers = null!;
        return false;
    }

    private static bool HasExactHeaders(OpenXmlRow row, IReadOnlyList<string> expected)
    {
        if (row.Cells.Values.Any(cell => cell.ColumnIndex > expected.Count && ImportText.Clean(cell.Value).Length > 0))
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

    private static bool IsBlank(OpenXmlRow row) => row.Cells.Values.All(cell => ImportText.Clean(cell.Value).Length == 0);

    private static string Value(OpenXmlRow row, int columnIndex) =>
        row.Cells.TryGetValue(columnIndex, out var cell) ? ImportText.Clean(cell.Value) : string.Empty;

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string MetadataValue(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value : string.Empty;

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string AttachmentKind(string messageType) => messageType is "image" or "audio" or "video" or "emoji" or "file"
        ? messageType
        : "file";

    private static string MapType(string value) => value switch
    {
        var v when v.Contains("图片", StringComparison.Ordinal) => "image",
        var v when v.Contains("语音", StringComparison.Ordinal) => "audio",
        var v when v.Contains("视频", StringComparison.Ordinal) => "video",
        var v when v.Contains("表情", StringComparison.Ordinal) => "emoji",
        var v when v.Contains("文件", StringComparison.Ordinal) => "file",
        var v when v.Contains("位置", StringComparison.Ordinal) => "location",
        var v when v.Contains("系统", StringComparison.Ordinal) => "system",
        _ => "text"
    };

    private enum ExcelLayout
    {
        Compact,
        Private,
        Group,
    }

    private sealed record Profile(
        OpenXmlSheet Sheet,
        uint HeaderRow,
        ExcelLayout Layout,
        IReadOnlyDictionary<string, int> Headers,
        IReadOnlyDictionary<string, string> Metadata);
}
