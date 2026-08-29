using System.Text;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

/// <summary>Current WeFlow PostgreSQL export.</summary>
public sealed class WeFlowSqlExportFormat : IChatExportFormat
{
    private const string Table = "weflow_messages";
    private static readonly string[] Columns =
    [
        "session_id", "local_id", "message_id", "create_time", "sender",
        "is_send", "local_type", "media_type", "content", "media_path"
    ];

    public string Platform => "wechat";

    public bool Matches(string filePath)
    {
        if (!SqlExportSupport.IsSql(filePath))
        {
            return false;
        }

        try
        {
            var found = false;
            foreach (var row in SqlExportSupport.ReadRows(filePath, CancellationToken.None))
            {
                if (!string.Equals(row.Table, Table, StringComparison.OrdinalIgnoreCase)
                    || !SqlExportSupport.HasExactColumns(row, Columns))
                {
                    return false;
                }
                found = true;
            }
            return found;
        }
        catch (Exception ex) when (SqlExportSupport.IsMatchFailure(ex))
        {
            return false;
        }
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var first = SqlExportSupport.ReadRows(filePath, cancellationToken).FirstOrDefault();
        if (first is null
            || !string.Equals(first.Table, Table, StringComparison.OrdinalIgnoreCase)
            || !SqlExportSupport.HasExactColumns(first, Columns))
        {
            throw new ImportFormatException(filePath, $"未找到当前 WeFlow {Table} INSERT 数据");
        }

        var sessionId = SqlExportSupport.Required(first, "session_id", filePath, Table, 1);
        var kind = sessionId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) ? "group" : "private";
        var conversation = new ParsedConversation("wechat", "wechat-default", sessionId, kind, sessionId);
        return new ExportFile(
            conversation,
            token => IterateMessages(filePath, conversation, token));
    }

    private static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var rowNumber = 0;
        foreach (var row in SqlExportSupport.ReadRows(filePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            SqlExportSupport.RequireProfile(row, Table, Columns, filePath, rowNumber);

            var sessionId = SqlExportSupport.Required(row, "session_id", filePath, Table, rowNumber);
            if (!string.Equals(sessionId, conversation.NativeId, StringComparison.Ordinal))
            {
                throw SqlExportSupport.RowError(
                    filePath,
                    Table,
                    rowNumber,
                    $"session_id “{sessionId}” 与会话 “{conversation.NativeId}” 不一致");
            }

            var timestampText = SqlExportSupport.Required(row, "create_time", filePath, Table, rowNumber);
            if (!ImportText.TryParseFlexibleTimestamp(timestampText, out var timestampMs))
            {
                throw SqlExportSupport.RowError(filePath, Table, rowNumber, "create_time 无效");
            }

            var isSend = SqlExportSupport.ParseBoolean(
                SqlExportSupport.Required(row, "is_send", filePath, Table, rowNumber),
                filePath,
                Table,
                rowNumber);
            var sender = SqlExportSupport.Value(row, "sender");
            var senderName = sender ?? (isSend ? "我" : conversation.Title);
            var senderNativeId = sender
                ?? FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, senderName);
            var localType = SqlExportSupport.Value(row, "local_type");
            var mediaType = SqlExportSupport.Value(row, "media_type");
            var messageType = SqlExportSupport.MapWeFlowType(mediaType, localType);
            var content = SqlExportSupport.RawValue(row, "content") ?? string.Empty;
            var mediaPath = SqlExportSupport.Value(row, "media_path");
            var attachments = string.IsNullOrWhiteSpace(mediaPath)
                ? Array.Empty<ParsedAttachment>()
                :
                [
                    new ParsedAttachment(
                        0,
                        SqlExportSupport.AttachmentKind(messageType, mediaPath),
                        Path.GetFileName(mediaPath),
                        mediaPath,
                        ImportText.SafeResolveMedia(exportRoot, mediaPath, conversation.Title),
                        null,
                        ImportText.GuessMime(mediaPath),
                        null,
                        null,
                        null,
                        new JsonObject())
                ];
            var isSystem = messageType == "system";

            yield return FlatMessageFactory.Create(new FlatMessageData(
                SqlExportSupport.Value(row, "message_id"),
                SqlExportSupport.Value(row, "local_id"),
                timestampMs,
                senderNativeId,
                senderName,
                isSystem ? "system" : isSend ? "outgoing" : "incoming",
                messageType,
                content,
                $"{Table}:{rowNumber}",
                SqlExportSupport.RawPayload(row),
                attachments,
                IsSystem: isSystem,
                MediaType: messageType is "text" or "system" ? null : messageType));
        }

        if (rowNumber == 0)
        {
            throw new ImportFormatException(filePath, $"未找到当前 WeFlow {Table} INSERT 数据");
        }
    }
}

/// <summary>Current CipherTalk PostgreSQL export.</summary>
public sealed class CipherTalkSqlExportFormat : IChatExportFormat
{
    private const string SessionsTable = "sessions";
    private const string MessagesTable = "messages";
    private static readonly string[] SessionColumns =
    [
        "wxid", "display_name", "session_type", "owner_id", "message_count",
        "first_message_time", "last_message_time", "exported_at"
    ];
    private static readonly string[] MessageColumns =
    [
        "session_wxid", "local_id", "create_time", "formatted_time", "msg_type",
        "content", "is_send", "sender_username", "sender_display_name",
        "group_nickname", "reply_to_message_id"
    ];

    public string Platform => "wechat";

    public bool Matches(string filePath)
    {
        if (!SqlExportSupport.IsSql(filePath))
        {
            return false;
        }

        try
        {
            _ = ReadProfile(filePath, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (SqlExportSupport.IsMatchFailure(ex))
        {
            return false;
        }
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var profile = ReadProfile(filePath, cancellationToken);
        var session = profile.Session;
        var wxid = SqlExportSupport.Required(session, "wxid", filePath, SessionsTable, 1);
        var title = SqlExportSupport.Required(session, "display_name", filePath, SessionsTable, 1);
        var sessionType = SqlExportSupport.Required(session, "session_type", filePath, SessionsTable, 1);
        var kind = sessionType switch
        {
            "group" => "group",
            "private" => "private",
            _ => throw SqlExportSupport.RowError(filePath, SessionsTable, 1, "session_type 无效")
        };
        var owner = SqlExportSupport.Value(session, "owner_id") ?? "wechat-default";
        var conversation = new ParsedConversation("wechat", owner, wxid, kind, title);
        return new ExportFile(
            conversation,
            token => IterateMessages(filePath, conversation, token));
    }

    private static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        var rowNumber = 0;
        var sessionCount = 0;
        foreach (var row in SqlExportSupport.ReadRows(filePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(row.Table, SessionsTable, StringComparison.OrdinalIgnoreCase))
            {
                sessionCount++;
                SqlExportSupport.RequireProfile(row, SessionsTable, SessionColumns, filePath, sessionCount);
                var wxid = SqlExportSupport.Required(row, "wxid", filePath, SessionsTable, sessionCount);
                if (sessionCount != 1 || !string.Equals(wxid, conversation.NativeId, StringComparison.Ordinal))
                {
                    throw SqlExportSupport.RowError(filePath, SessionsTable, sessionCount, "必须且只能包含当前会话行");
                }
                continue;
            }

            rowNumber++;
            SqlExportSupport.RequireProfile(row, MessagesTable, MessageColumns, filePath, rowNumber);
            var sessionWxid = SqlExportSupport.Required(row, "session_wxid", filePath, MessagesTable, rowNumber);
            if (!string.Equals(sessionWxid, conversation.NativeId, StringComparison.Ordinal))
            {
                throw SqlExportSupport.RowError(
                    filePath,
                    MessagesTable,
                    rowNumber,
                    $"session_wxid “{sessionWxid}” 与会话 “{conversation.NativeId}” 不一致");
            }

            var timestampText = SqlExportSupport.Required(row, "create_time", filePath, MessagesTable, rowNumber);
            if (!ImportText.TryParseFlexibleTimestamp(timestampText, out var timestampMs))
            {
                throw SqlExportSupport.RowError(filePath, MessagesTable, rowNumber, "create_time 无效");
            }

            var isSend = SqlExportSupport.ParseSmallIntBoolean(
                SqlExportSupport.Required(row, "is_send", filePath, MessagesTable, rowNumber),
                filePath,
                MessagesTable,
                rowNumber);
            var senderName = SqlExportSupport.Value(row, "sender_display_name")
                ?? SqlExportSupport.Value(row, "group_nickname")
                ?? (isSend ? "我" : conversation.Title);
            var senderNativeId = SqlExportSupport.Value(row, "sender_username")
                ?? FlatMessageFactory.SyntheticSenderNativeId(conversation.NativeId, senderName);
            var messageType = SqlExportSupport.MapWeChatType(
                SqlExportSupport.Value(row, "msg_type"),
                null);
            var content = SqlExportSupport.RawValue(row, "content") ?? string.Empty;
            var isSystem = messageType == "system";

            yield return FlatMessageFactory.Create(new FlatMessageData(
                null,
                SqlExportSupport.Value(row, "local_id"),
                timestampMs,
                senderNativeId,
                senderName,
                isSystem ? "system" : isSend ? "outgoing" : "incoming",
                messageType,
                content,
                $"{MessagesTable}:{rowNumber}",
                SqlExportSupport.RawPayload(row),
                ReplyToNativeId: SqlExportSupport.Value(row, "reply_to_message_id"),
                IsSystem: isSystem,
                MediaType: messageType is "text" or "system" ? null : messageType));
        }

        if (sessionCount != 1)
        {
            throw new ImportFormatException(filePath, "CipherTalk SQL 必须包含且只包含一个 sessions 行");
        }
        if (rowNumber == 0)
        {
            throw SqlExportSupport.RowError(
                filePath,
                MessagesTable,
                Math.Max(rowNumber, 1),
                $"未找到与会话 “{conversation.NativeId}” 关联的消息");
        }
    }

    private static CipherProfile ReadProfile(string filePath, CancellationToken cancellationToken)
    {
        SqlInsertRow? session = null;
        string? sessionId = null;
        string? messageSessionId = null;
        var sessionCount = 0;
        var messageCount = 0;

        foreach (var row in SqlExportSupport.ReadRows(filePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(row.Table, SessionsTable, StringComparison.OrdinalIgnoreCase))
            {
                sessionCount++;
                SqlExportSupport.RequireProfile(row, SessionsTable, SessionColumns, filePath, sessionCount);
                if (sessionCount != 1)
                {
                    throw SqlExportSupport.RowError(filePath, SessionsTable, sessionCount, "当前 CipherTalk 文件只能包含一个会话行");
                }
                session = row;
                sessionId = SqlExportSupport.Required(row, "wxid", filePath, SessionsTable, sessionCount);
                if (messageSessionId is not null
                    && !string.Equals(messageSessionId, sessionId, StringComparison.Ordinal))
                {
                    throw SqlExportSupport.RowError(
                        filePath,
                        MessagesTable,
                        messageCount,
                        $"session_wxid “{messageSessionId}” 与会话 “{sessionId}” 不一致");
                }
                continue;
            }

            messageCount++;
            SqlExportSupport.RequireProfile(row, MessagesTable, MessageColumns, filePath, messageCount);
            var currentSessionId = SqlExportSupport.Required(
                row,
                "session_wxid",
                filePath,
                MessagesTable,
                messageCount);
            if (messageSessionId is null)
            {
                messageSessionId = currentSessionId;
            }
            else if (!string.Equals(messageSessionId, currentSessionId, StringComparison.Ordinal))
            {
                throw SqlExportSupport.RowError(
                    filePath,
                    MessagesTable,
                    messageCount,
                    $"session_wxid “{currentSessionId}” 与先前消息不一致");
            }
            if (sessionId is not null && !string.Equals(sessionId, currentSessionId, StringComparison.Ordinal))
            {
                throw SqlExportSupport.RowError(
                    filePath,
                    MessagesTable,
                    messageCount,
                    $"session_wxid “{currentSessionId}” 与会话 “{sessionId}” 不一致");
            }
        }

        if (sessionCount != 1 || session is null || sessionId is null)
        {
            throw new ImportFormatException(filePath, "CipherTalk SQL 必须包含且只包含一个 sessions 行");
        }
        if (messageCount == 0)
        {
            throw new ImportFormatException(filePath, "CipherTalk SQL 必须包含至少一个 messages 行");
        }
        if (!string.Equals(sessionId, messageSessionId, StringComparison.Ordinal))
        {
            throw SqlExportSupport.RowError(
                filePath,
                MessagesTable,
                1,
                $"消息不属于会话 “{sessionId}”");
        }
        return new CipherProfile(session);
    }

    private sealed record CipherProfile(SqlInsertRow Session);
}

internal static class SqlExportSupport
{
    internal static bool IsSql(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".sql", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<SqlInsertRow> ReadRows(
        string filePath,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }

        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        using (var rows = SqlInsertReader.Enumerate(reader, cancellationToken).GetEnumerator())
        {
            while (MoveNext(rows, filePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return rows.Current;
            }
        }
    }

    internal static bool HasExactColumns(SqlInsertRow row, IReadOnlyCollection<string> expected) =>
        row.Values.Count == expected.Count
        && expected.All(row.Values.ContainsKey);

    internal static void RequireProfile(
        SqlInsertRow row,
        string table,
        IReadOnlyCollection<string> columns,
        string filePath,
        int rowNumber)
    {
        if (!string.Equals(row.Table, table, StringComparison.OrdinalIgnoreCase)
            || !HasExactColumns(row, columns))
        {
            throw RowError(filePath, table, rowNumber, "INSERT 表或列与当前导出格式不匹配");
        }
    }

    internal static string Required(
        SqlInsertRow row,
        string column,
        string filePath,
        string table,
        int rowNumber)
    {
        var value = Value(row, column);
        return value ?? throw RowError(filePath, table, rowNumber, $"缺少必填列 {column}");
    }

    internal static string? Value(SqlInsertRow row, string column) =>
        row.Values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    internal static string? RawValue(SqlInsertRow row, string column) =>
        row.Values.TryGetValue(column, out var value) ? value : null;

    internal static bool ParseBoolean(
        string value,
        string filePath,
        string table,
        int rowNumber) => value.ToUpperInvariant() switch
    {
        "TRUE" => true,
        "FALSE" => false,
        _ => throw RowError(filePath, table, rowNumber, "is_send 不是 PostgreSQL BOOLEAN")
    };

    internal static bool ParseSmallIntBoolean(
        string value,
        string filePath,
        string table,
        int rowNumber) => value switch
    {
        "1" => true,
        "0" => false,
        _ => throw RowError(filePath, table, rowNumber, "is_send 不是 0 或 1")
    };

    internal static string MapWeChatType(string? text, string? number) =>
        (text ?? string.Empty).Trim() switch
        {
            "文本消息" => "text",
            "图片消息" => "image",
            "语音消息" => "audio",
            "视频消息" => "video",
            "表情消息" => "emoji",
            "引用/文件/链接消息" => "link",
            "系统消息" => "system",
            _ => number switch
            {
                "1" => "text", "3" => "image", "34" => "audio", "43" => "video",
                "47" => "emoji", "49" => "link", "10000" => "system", _ => "other"
            }
        };

    internal static string MapWeFlowType(string? mediaType, string? localType) =>
        (mediaType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "image" => "image",
            "voice" or "audio" => "audio",
            "video" => "video",
            "emoji" => "emoji",
            "file" => "file",
            _ => MapWeChatType(null, localType)
        };

    internal static string AttachmentKind(string messageType, string declaredPath) =>
        messageType is "image" or "audio" or "video" or "emoji" or "file"
            ? messageType
            : ImportText.GuessMime(declaredPath)?.Split('/')[0] switch
            {
                "image" => "image",
                "audio" => "audio",
                "video" => "video",
                _ => "file"
            };

    internal static JsonObject RawPayload(SqlInsertRow row)
    {
        var raw = new JsonObject();
        foreach (var pair in row.Values)
        {
            raw[pair.Key] = pair.Value;
        }
        return raw;
    }

    internal static ImportFormatException RowError(
        string filePath,
        string table,
        int rowNumber,
        string message) =>
        new(filePath, $"表 {table} 第 {rowNumber} 行：{message}");

    internal static bool IsMatchFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ImportFormatException
            or FormatException
            or ArgumentException;

    private static bool MoveNext(IEnumerator<SqlInsertRow> rows, string filePath)
    {
        try
        {
            return rows.MoveNext();
        }
        catch (FormatException ex)
        {
            throw new ImportFormatException(filePath, $"SQL INSERT 结构无效（{ex.Message}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }
}
