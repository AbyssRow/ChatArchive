using ChatArchive.Core.Data;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Tests;

/// <summary>构造带最小合法数据的临时档案库。</summary>
internal sealed class TestArchive : IDisposable
{
    private readonly string _directory;
    private int _hashCounter;

    public ArchiveDatabase Db { get; }
    public string DatabasePath { get; }

    public TestArchive()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"chatarchive-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "test.db");
        Db = new ArchiveDatabase(DatabasePath);
        Db.EnsureSchema();
    }

    public SqliteConnection Open() => Db.OpenConnection();

    public long AddConversation(
        string nativeId,
        string title,
        string kind = "private",
        string platform = "qq",
        string accountId = "acc",
        long? lastMessageAt = null)
    {
        using var connection = Open();
        return AddConversation(connection, nativeId, title, kind, platform, accountId, lastMessageAt);
    }

    public static long AddConversation(
        SqliteConnection connection,
        string nativeId,
        string title,
        string kind = "private",
        string platform = "qq",
        string accountId = "acc",
        long? lastMessageAt = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(platform, account_id, native_id, kind, title, first_message_at, last_message_at, message_count)
            VALUES (@platform, @account, @native, @kind, @title, @last, @last, 0);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@platform", platform);
        command.Parameters.AddWithValue("@account", accountId);
        command.Parameters.AddWithValue("@native", nativeId);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@last", lastMessageAt.HasValue ? lastMessageAt.Value : DBNull.Value);
        var id = Convert.ToInt64(command.ExecuteScalar());

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE conversations SET message_count = (SELECT COUNT(*) FROM messages WHERE conversation_id = @id) WHERE id = @id";
        update.Parameters.AddWithValue("@id", id);
        update.ExecuteNonQuery();
        return id;
    }

    public long AddSender(string nativeId, string currentName, string platform = "qq", bool isSelf = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO senders(platform, account_id, native_id, current_name, is_self)
            VALUES (@platform, 'acc', @native, @name, @self);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@platform", platform);
        command.Parameters.AddWithValue("@native", nativeId);
        command.Parameters.AddWithValue("@name", currentName);
        command.Parameters.AddWithValue("@self", isSelf ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public long AddMessage(
        long conversationId,
        long? senderId,
        long timestampMs,
        string content,
        string direction = "incoming",
        string messageType = "text",
        string senderName = "Alice",
        bool isRecalled = false,
        bool isSystem = false,
        string? nativeId = null,
        string platform = "qq")
    {
        using var connection = Open();
        var id = AddMessage(connection, conversationId, senderId, timestampMs, content, direction, messageType, senderName, isRecalled, isSystem, nativeId, platform);
        RefreshCounts(connection, conversationId);
        return id;
    }

    public static long AddMessage(
        SqliteConnection connection,
        long conversationId,
        long? senderId,
        long timestampMs,
        string content,
        string direction = "incoming",
        string messageType = "text",
        string senderName = "Alice",
        bool isRecalled = false,
        bool isSystem = false,
        string? nativeId = null,
        string platform = "qq")
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages(conversation_id, sender_id, platform, native_id, timestamp_ms,
                direction, message_type, content, search_text, sender_name_snapshot,
                conversation_title_snapshot, is_recalled, is_system, payload_hash, semantic_hash, raw_payload_json)
            VALUES (@conv, @sender, @platform, @native, @ts,
                @direction, @type, @content, @search, @name,
                '', @recalled, @system, @phash, @shash, '{}');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@conv", conversationId);
        command.Parameters.AddWithValue("@sender", senderId.HasValue ? senderId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@platform", platform);
        command.Parameters.AddWithValue("@native", nativeId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ts", timestampMs);
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@type", messageType);
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@search", content);
        command.Parameters.AddWithValue("@name", senderName);
        command.Parameters.AddWithValue("@recalled", isRecalled ? 1 : 0);
        command.Parameters.AddWithValue("@system", isSystem ? 1 : 0);
        command.Parameters.AddWithValue("@phash", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@shash", Guid.NewGuid().ToString("N"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void AddAlias(long conversationId, string alias)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversation_aliases(conversation_id, alias) VALUES (@c, @a)";
        command.Parameters.AddWithValue("@c", conversationId);
        command.Parameters.AddWithValue("@a", alias);
        command.ExecuteNonQuery();
    }

    public void AddSenderAlias(long senderId, string alias, long? conversationId = null, long? lastSeenAt = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sender_aliases(sender_id, conversation_id, alias, first_seen_at, last_seen_at)
            VALUES (@s, @c, @a, @seen, @seen)
            """;
        command.Parameters.AddWithValue("@s", senderId);
        command.Parameters.AddWithValue("@c", conversationId.HasValue ? conversationId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@a", alias);
        command.Parameters.AddWithValue("@seen", lastSeenAt.HasValue ? lastSeenAt.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public long AddMediaObject(string sha256, long size, string? mimeType, string? managedPath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_objects(sha256, size, mime_type, managed_path)
            VALUES (@sha, @size, @mime, @managed);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@sha", sha256);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@mime", mimeType ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@managed", managedPath ?? (object)DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>插入附件；sha256 为 null 时保持缺失（media_object_id 为 NULL）。</summary>
    public void AddAttachment(
        long messageId,
        int ordinal,
        string kind = "image",
        string? filename = null,
        bool isAvailable = false,
        long? mediaObjectId = null,
        string? declaredPath = null,
        string? sourcePath = null,
        string? mimeType = "image/jpeg")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO attachments(message_id, ordinal, kind, filename, is_available, mime_type,
                declared_path, source_path, media_object_id)
            VALUES (@m, @o, @k, @f, @av, @mime, @dp, @sp, @mo)
            """;
        command.Parameters.AddWithValue("@m", messageId);
        command.Parameters.AddWithValue("@o", ordinal);
        command.Parameters.AddWithValue("@k", kind);
        command.Parameters.AddWithValue("@f", filename ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@av", isAvailable ? 1 : 0);
        command.Parameters.AddWithValue("@mime", mimeType ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@dp", declaredPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@sp", sourcePath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@mo", mediaObjectId.HasValue ? mediaObjectId.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void RefreshCounts(SqliteConnection connection, long conversationId)
    {
        using var min = connection.CreateCommand();
        min.CommandText = """
            UPDATE conversations SET
                message_count = (SELECT COUNT(*) FROM messages WHERE conversation_id = @id),
                first_message_at = (SELECT MIN(timestamp_ms) FROM messages WHERE conversation_id = @id),
                last_message_at = (SELECT MAX(timestamp_ms) FROM messages WHERE conversation_id = @id),
                updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """;
        min.Parameters.AddWithValue("@id", conversationId);
        min.ExecuteNonQuery();
    }

    public string NextHash() => $"{(++_hashCounter):x8}".PadLeft(64, '0');

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
