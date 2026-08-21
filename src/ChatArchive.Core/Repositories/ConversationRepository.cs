using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Repositories;

public sealed class ConversationRepository
{
    private readonly ArchiveDatabase _db;

    public ConversationRepository(ArchiveDatabase db)
    {
        _db = db;
    }

    public IReadOnlyList<ConversationInfo> ListConversations(
        string? platform = null,
        string? kind = null,
        string? query = null,
        int limit = 300)
    {
        var where = new List<string>();
        var parameters = new List<SqliteParameter>();
        if (!string.IsNullOrEmpty(platform))
        {
            where.Add("c.platform = @platform");
            parameters.Add(new SqliteParameter("@platform", platform));
        }

        if (!string.IsNullOrEmpty(kind))
        {
            where.Add("c.kind = @kind");
            parameters.Add(new SqliteParameter("@kind", kind));
        }

        if (!string.IsNullOrEmpty(query))
        {
            where.Add("(c.title LIKE @query OR EXISTS(SELECT 1 FROM conversation_aliases ca "
                      + "WHERE ca.conversation_id = c.id AND ca.alias LIKE @query))");
            parameters.Add(new SqliteParameter("@query", $"%{query}%"));
        }

        parameters.Add(new SqliteParameter("@limit", Math.Clamp(limit, 1, 1000)));
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.platform, c.account_id, c.native_id, c.kind, c.title,
                   c.first_message_at, c.last_message_at, c.message_count,
                   (SELECT content FROM messages lm
                    WHERE lm.conversation_id = c.id
                    ORDER BY lm.timestamp_ms DESC, lm.id DESC LIMIT 1) AS last_message,
                   (SELECT COUNT(*) FROM attachments a
                    JOIN messages am ON am.id = a.message_id
                    WHERE am.conversation_id = c.id AND a.is_available = 0) AS missing_media
            FROM conversations c
            """ + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY c.last_message_at DESC, c.id DESC LIMIT @limit";

        command.Parameters.AddRange(parameters.ToArray());
        using var reader = command.ExecuteReader();
        var result = new List<ConversationInfo>();
        while (reader.Read())
        {
            result.Add(new ConversationInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10)));
        }

        return result;
    }

    public ConversationDetail? GetConversation(long conversationId)
    {
        using var connection = _db.OpenConnection();
        ConversationInfo? info = null;
        const string detailSql = """
            SELECT c.id, c.platform, c.account_id, c.native_id, c.kind, c.title,
                   c.first_message_at, c.last_message_at, c.message_count,
                   (SELECT COUNT(*) FROM attachments a
                          JOIN messages m ON m.id = a.message_id
                          WHERE m.conversation_id = c.id AND a.is_available = 0)
            FROM conversations c WHERE c.id = @id
            """;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = detailSql;
            command.Parameters.AddWithValue("@id", conversationId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                info = new ConversationInfo(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.GetInt64(8),
                    null,
                    reader.GetInt64(9));
            }
        }

        if (info is null)
        {
            return null;
        }

        var aliases = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT alias FROM conversation_aliases WHERE conversation_id = @id ORDER BY id";
            command.Parameters.AddWithValue("@id", conversationId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                aliases.Add(reader.GetString(0));
            }
        }

        return new ConversationDetail(info, aliases);
    }

    public PageResult<MessageItem> ListMessages(long conversationId, string? cursor = null, int limit = 80)
    {
        long? cursorTs = null;
        long? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            (cursorTs, cursorId) = CursorCodec.Decode(cursor);
        }

        var pageSize = Math.Clamp(limit, 1, 200);
        using var connection = _db.OpenConnection();

        List<MessageRow> rows;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.id, m.conversation_id, m.sender_id, m.timestamp_ms,
                       m.direction, m.message_type, m.media_type, m.content,
                       m.sender_name_snapshot, m.is_recalled, m.is_system
                FROM messages m
                WHERE m.conversation_id = @id
                  AND (@ts IS NULL OR m.timestamp_ms < @ts OR (m.timestamp_ms = @ts AND m.id < @cid))
                ORDER BY m.timestamp_ms DESC, m.id DESC
                LIMIT @limit
                """;
            command.Parameters.AddWithValue("@id", conversationId);
            command.Parameters.AddWithValue("@ts", cursorTs.HasValue ? cursorTs.Value : DBNull.Value);
            command.Parameters.AddWithValue("@cid", cursorId.HasValue ? cursorId.Value : DBNull.Value);
            command.Parameters.AddWithValue("@limit", pageSize + 1);
            rows = ReadMessageRows(command);
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            nextCursor = CursorCodec.Encode(rows[^1].TimestampMs, rows[^1].Id);
        }

        rows.Reverse();
        var items = Hydrate(connection, rows);
        return new PageResult<MessageItem>(items, nextCursor);
    }

    public MessageContext? GetMessageContext(long messageId, int radius = 24)
    {
        radius = Math.Clamp(radius, 1, 100);
        using var connection = _db.OpenConnection();

        const string columns = """
            SELECT id, conversation_id, sender_id, timestamp_ms,
                   direction, message_type, media_type, content,
                   sender_name_snapshot, is_recalled, is_system FROM messages
            """;

        long conversationId;
        long timestampMs;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, conversation_id, timestamp_ms FROM messages WHERE id = @id";
            command.Parameters.AddWithValue("@id", messageId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            conversationId = reader.GetInt64(1);
            timestampMs = reader.GetInt64(2);
        }

        var ordered = new List<MessageRow>();
        using (var before = connection.CreateCommand())
        {
            before.CommandText = columns + """
                 WHERE conversation_id = @id
                   AND (timestamp_ms < @ts OR (timestamp_ms = @ts AND id < @mid))
                 ORDER BY timestamp_ms DESC, id DESC LIMIT @radius
                """;
            before.Parameters.AddWithValue("@id", conversationId);
            before.Parameters.AddWithValue("@ts", timestampMs);
            before.Parameters.AddWithValue("@mid", messageId);
            before.Parameters.AddWithValue("@radius", radius);
            var list = ReadMessageRows(before);
            list.Reverse();
            ordered.AddRange(list);
        }

        using (var focus = connection.CreateCommand())
        {
            focus.CommandText = columns + " WHERE id = @id";
            focus.Parameters.AddWithValue("@id", messageId);
            ordered.AddRange(ReadMessageRows(focus));
        }

        using (var after = connection.CreateCommand())
        {
            after.CommandText = columns + """
                 WHERE conversation_id = @id
                   AND (timestamp_ms > @ts OR (timestamp_ms = @ts AND id > @mid))
                 ORDER BY timestamp_ms, id LIMIT @radius
                """;
            after.Parameters.AddWithValue("@id", conversationId);
            after.Parameters.AddWithValue("@ts", timestampMs);
            after.Parameters.AddWithValue("@mid", messageId);
            after.Parameters.AddWithValue("@radius", radius);
            ordered.AddRange(ReadMessageRows(after));
        }

        string title;
        using (var titleCommand = connection.CreateCommand())
        {
            titleCommand.CommandText = "SELECT title FROM conversations WHERE id = @id";
            titleCommand.Parameters.AddWithValue("@id", conversationId);
            title = titleCommand.ExecuteScalar() as string ?? string.Empty;
        }

        var items = Hydrate(connection, ordered);
        return new MessageContext(conversationId, title, messageId, items);
    }

    internal sealed record MessageRow(
        long Id,
        long ConversationId,
        long? SenderId,
        long TimestampMs,
        string Direction,
        string MessageType,
        string? MediaType,
        string Content,
        string SenderNameSnapshot,
        bool IsRecalled,
        bool IsSystem);

    internal static List<MessageRow> ReadMessageRows(SqliteCommand command)
    {
        var rows = new List<MessageRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MessageRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9) != 0,
                reader.GetInt64(10) != 0));
        }

        return rows;
    }

    internal static IReadOnlyList<MessageItem> Hydrate(SqliteConnection connection, IEnumerable<MessageRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return Array.Empty<MessageItem>();
        }

        var attachments = LoadAttachments(connection, list.Select(r => r.Id).ToList());
        var displayNames = SenderDisplayName.Resolve(
            connection,
            list
                .Where(r => r.SenderId.HasValue)
                .Select(r => (SenderId: r.SenderId!.Value, ConversationId: (long?)r.ConversationId))
                .Distinct());

        return list.Select(row =>
        {
            var displayName = row.SenderId.HasValue
                && displayNames.TryGetValue((row.SenderId.Value, row.ConversationId), out var name)
                ? name
                : row.SenderNameSnapshot;
            attachments.TryGetValue(row.Id, out var attachmentList);
            return new MessageItem(
                row.Id,
                row.ConversationId,
                row.SenderId,
                displayName,
                row.Direction,
                row.MessageType,
                row.MediaType,
                row.Content,
                row.IsRecalled,
                row.IsSystem,
                row.TimestampMs,
                attachmentList as IReadOnlyList<AttachmentInfo> ?? Array.Empty<AttachmentInfo>());
        }).ToList();
    }

    internal static Dictionary<long, List<AttachmentInfo>> LoadAttachments(SqliteConnection connection, IReadOnlyCollection<long> messageIds)
    {
        var grouped = new Dictionary<long, List<AttachmentInfo>>();
        if (messageIds.Count == 0)
        {
            return grouped;
        }

        var ids = messageIds.ToList();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@m{i}"));
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT a.id, a.message_id, a.ordinal, a.kind, a.filename, a.is_available,
                   a.mime_type, a.width, a.height, a.duration, a.declared_path,
                   mo.managed_path, a.source_path, mo.sha256
            FROM attachments a
            LEFT JOIN media_objects mo ON mo.id = a.media_object_id
            WHERE a.message_id IN ({{placeholders}})
            ORDER BY a.message_id, a.ordinal
            """;
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue($"@m{i}", ids[i]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new AttachmentInfo(
                reader.GetInt64(0),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13));

            var messageId = reader.GetInt64(1);
            if (!grouped.TryGetValue(messageId, out var list))
            {
                list = new List<AttachmentInfo>();
                grouped[messageId] = list;
            }

            list.Add(item);
        }

        return grouped;
    }
}
