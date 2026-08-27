using ChatArchive.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatArchive.Core.Tests;

public class ArchiveDatabaseTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public ArchiveDatabaseTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"chatarchive-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "test.db");
    }

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

    [Fact]
    public void EnsureSchema_CreatesContactsAndContactSendersTables()
    {
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key='schema_version'";
        Assert.Equal("2", command.ExecuteScalar());

        // Verify contacts and contact_senders tables
        Execute(connection, """
            INSERT INTO contacts(id, display_name, custom_avatar_path, note)
            VALUES (1, '张三', 'avatars/zhangsan.png', '重要联系人');
            """);

        Execute(connection, """
            INSERT INTO senders(id, platform, account_id, native_id, current_name)
            VALUES (10, 'qq', 'acc', 'user_10', 'Sender Alice');
            """);

        Execute(connection, """
            INSERT INTO contact_senders(contact_id, sender_id, account_label, is_primary)
            VALUES (1, 10, '大号', 1);
            """);

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM contacts WHERE id = 1"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM contact_senders WHERE contact_id = 1 AND sender_id = 10"));

        // Verify unique index on sender_id
        Assert.Throws<SqliteException>(() =>
        {
            Execute(connection, """
                INSERT INTO contacts(id, display_name) VALUES (2, '李四');
                INSERT INTO contact_senders(contact_id, sender_id) VALUES (2, 10);
                """);
        });

        // Verify cascade delete on contact
        Execute(connection, "DELETE FROM contacts WHERE id = 1");
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM contact_senders WHERE contact_id = 1"));
    }

    [Fact]
    public void EnsureSchema_UpgradesFromVersion1ToVersion2()
    {
        // 1. Manually create v1 database with existing data
        using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, """
                CREATE TABLE app_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO app_metadata(key, value) VALUES ('schema_version', '1');

                CREATE TABLE senders (
                    id INTEGER PRIMARY KEY,
                    platform TEXT NOT NULL CHECK(platform IN ('qq', 'wechat')),
                    account_id TEXT NOT NULL,
                    native_id TEXT NOT NULL,
                    current_name TEXT NOT NULL,
                    is_self INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(platform, account_id, native_id)
                );

                CREATE TABLE conversations (
                    id INTEGER PRIMARY KEY,
                    platform TEXT NOT NULL CHECK(platform IN ('qq', 'wechat')),
                    account_id TEXT NOT NULL,
                    native_id TEXT NOT NULL,
                    kind TEXT NOT NULL CHECK(kind IN ('private', 'group')),
                    title TEXT NOT NULL,
                    first_message_at INTEGER,
                    last_message_at INTEGER,
                    message_count INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(platform, account_id, native_id)
                );

                CREATE TABLE messages (
                    id INTEGER PRIMARY KEY,
                    conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                    sender_id INTEGER REFERENCES senders(id) ON DELETE SET NULL,
                    platform TEXT NOT NULL CHECK(platform IN ('qq', 'wechat')),
                    native_id TEXT,
                    local_id TEXT,
                    timestamp_ms INTEGER NOT NULL,
                    sequence TEXT,
                    direction TEXT NOT NULL CHECK(direction IN ('incoming', 'outgoing', 'system')),
                    message_type TEXT NOT NULL,
                    media_type TEXT,
                    content TEXT NOT NULL,
                    search_text TEXT NOT NULL,
                    sender_name_snapshot TEXT NOT NULL,
                    conversation_title_snapshot TEXT NOT NULL,
                    is_recalled INTEGER NOT NULL DEFAULT 0,
                    is_system INTEGER NOT NULL DEFAULT 0,
                    reply_to_native_id TEXT,
                    payload_hash TEXT NOT NULL,
                    semantic_hash TEXT NOT NULL,
                    revision_of_id INTEGER REFERENCES messages(id) ON DELETE SET NULL,
                    raw_payload_json TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                INSERT INTO senders(id, platform, account_id, native_id, current_name)
                VALUES (1, 'qq', 'acc1', 'u1', 'Alice');

                INSERT INTO conversations(id, platform, account_id, native_id, kind, title)
                VALUES (10, 'qq', 'acc1', 'c10', 'private', 'Alice Chat');

                INSERT INTO messages(id, conversation_id, sender_id, platform, timestamp_ms, direction,
                    message_type, content, search_text, sender_name_snapshot, conversation_title_snapshot,
                    payload_hash, semantic_hash, raw_payload_json)
                VALUES (100, 10, 1, 'qq', 1700000000000, 'incoming', 'text', 'Hello', 'Hello',
                    'Alice', 'Alice Chat', 'ph', 'sh', '{}');
                """);
        }

        // 2. Perform EnsureSchema()
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        // 3. Verify upgraded to v2 and existing data intact
        using (var connection = db.OpenConnection())
        {
            var version = ScalarText(connection, "SELECT value FROM app_metadata WHERE key='schema_version'");
            Assert.Equal("2", version);

            // Existing data intact
            Assert.Equal("Alice", ScalarText(connection, "SELECT current_name FROM senders WHERE id = 1"));
            Assert.Equal("Alice Chat", ScalarText(connection, "SELECT title FROM conversations WHERE id = 10"));
            Assert.Equal("Hello", ScalarText(connection, "SELECT content FROM messages WHERE id = 100"));

            // New tables exist and work
            Execute(connection, """
                INSERT INTO contacts(id, display_name, note) VALUES (1, 'Alice Contact', 'Test note');
                INSERT INTO contact_senders(contact_id, sender_id, account_label, is_primary) VALUES (1, 1, 'QQ', 1);
                """);
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM contact_senders WHERE contact_id = 1 AND sender_id = 1"));
        }

        // 4. Ensure idempotent
        db.EnsureSchema();
    }

    [Fact]
    public void OpenConnection_enables_foreign_keys()
    {
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void CleanEmptyConversations_removes_only_empty()
    {
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        using (var connection = db.OpenConnection())
        {
            Execute(connection, """
                INSERT INTO conversations(id, platform, account_id, native_id, kind, title)
                VALUES (1, 'qq', 'acc', 'empty', 'private', '空会话'),
                       (2, 'qq', 'acc', 'full', 'private', '有消息')
                """);
            Execute(connection, """
                INSERT INTO senders(id, platform, account_id, native_id, current_name)
                VALUES (10, 'qq', 'acc', 'alice', 'Alice')
                """);
            Execute(connection, """
                INSERT INTO messages(id, conversation_id, sender_id, platform, timestamp_ms,
                    direction, message_type, content, search_text, sender_name_snapshot,
                    conversation_title_snapshot, payload_hash, semantic_hash, raw_payload_json)
                VALUES (100, 2, 10, 'qq', 1700000000000, 'incoming', 'text', '你好',
                        '你好', 'Alice', '有消息', 'ph1', 'sh1', '{}')
                """);
        }

        var removed = db.CleanEmptyConversations();

        Assert.Equal(1, removed);
        using var connection2 = db.OpenConnection();
        Assert.Equal(1L, Scalar(connection2, "SELECT COUNT(*) FROM conversations"));
        using var command = connection2.CreateCommand();
        command.CommandText = "SELECT title FROM conversations";
        Assert.Equal("有消息", command.ExecuteScalar() as string);
    }

    [Fact]
    public void Split_handles_triggers_and_batches()
    {
        var sql = ArchiveDatabase.LoadSchemaSql();
        var statements = SqlScriptSplitter.Split(sql);

        Assert.Contains(statements, s => s.StartsWith("CREATE TABLE IF NOT EXISTS app_metadata", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.StartsWith("CREATE TABLE IF NOT EXISTS contacts", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.StartsWith("CREATE TABLE IF NOT EXISTS contact_senders", StringComparison.Ordinal));
        var triggers = statements.Where(s => s.StartsWith("CREATE TRIGGER", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, triggers.Count);
        Assert.All(triggers, s => Assert.EndsWith("END;", s));
        Assert.All(statements.Except(triggers), s => Assert.False(s.EndsWith(";", StringComparison.Ordinal)));
        Assert.Equal(34, statements.Count);
    }

    [Fact]
    public void Split_handles_line_and_block_comments()
    {
        var sql = """
            -- This is a comment with a semicolon;
            SELECT 1;
            /* Block comment with semicolon; and multiple
               lines */
            SELECT 2;
            SELECT '-- not a comment; inside string' AS val;
            """;
        var statements = SqlScriptSplitter.Split(sql);
        Assert.Equal(3, statements.Count);
        Assert.StartsWith("-- This is a comment with a semicolon;\nSELECT 1", statements[0].Replace("\r\n", "\n"));
        Assert.StartsWith("/* Block comment with semicolon;", statements[1]);
        Assert.Equal("SELECT '-- not a comment; inside string' AS val", statements[2]);
    }

    [Fact]
    public void RepairDuplicateConversationsAndSenders_MergesDuplicatesCleanly()
    {
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        using (var connection = db.OpenConnection())
        {
            // Insert duplicate senders (one with wechat-default, one with wxid_rpz...)
            Execute(connection, """
                INSERT INTO senders(id, platform, account_id, native_id, current_name, is_self)
                VALUES (1, 'wechat', 'wechat-default', 'wxid_user1', 'wxid_user1', 0),
                       (2, 'wechat', 'wxid_myaccount', 'wxid_user1', '用户一', 0);
                """);

            // Insert duplicate conversations (one with wechat-default, one with wxid_myaccount)
            Execute(connection, """
                INSERT INTO conversations(id, platform, account_id, native_id, kind, title, message_count)
                VALUES (10, 'wechat', 'wechat-default', 'wxid_user1', 'private', 'wxid_user1', 1),
                       (20, 'wechat', 'wxid_myaccount', 'wxid_user1', 'private', '用户一', 2);
                """);

            // Messages: msg 100 in conv 10, msg 200 in conv 20 (distinct message), msg 201 in conv 20 (duplicate of 100)
            Execute(connection, """
                INSERT INTO messages(id, conversation_id, sender_id, platform, native_id, timestamp_ms,
                    direction, message_type, content, search_text, sender_name_snapshot,
                    conversation_title_snapshot, payload_hash, semantic_hash, raw_payload_json)
                VALUES (100, 10, 1, 'wechat', 'm1', 1700000000000, 'incoming', 'text', '你好', '你好', '用户一', '用户一', 'ph1', 'sh1', '{}'),
                       (200, 20, 2, 'wechat', 'm2', 1700000005000, 'incoming', 'text', '在吗', '在吗', '用户一', '用户一', 'ph2', 'sh2', '{}'),
                       (201, 20, 2, 'wechat', 'm1', 1700000000000, 'incoming', 'text', '你好', '你好', '用户一', '用户一', 'ph1', 'sh1', '{}');
                """);

            // Contact binding to duplicate sender
            Execute(connection, """
                INSERT INTO contacts(id, display_name) VALUES (1, '好友联系人');
                INSERT INTO contact_senders(contact_id, sender_id, account_label) VALUES (1, 2, '微信');
                """);
        }

        var merged = db.RepairDuplicateConversationsAndSenders();
        Assert.True(merged >= 2, $"merged: {merged}");

        using (var connection = db.OpenConnection())
        {
            // Only 1 sender should remain
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM senders"));
            Assert.Equal("wxid_myaccount", ScalarText(connection, "SELECT account_id FROM senders WHERE native_id = 'wxid_user1'"));
            Assert.Equal("用户一", ScalarText(connection, "SELECT current_name FROM senders WHERE native_id = 'wxid_user1'"));

            // Only 1 conversation should remain
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
            Assert.Equal("wxid_myaccount", ScalarText(connection, "SELECT account_id FROM conversations WHERE native_id = 'wxid_user1'"));
            Assert.Equal("用户一", ScalarText(connection, "SELECT title FROM conversations WHERE native_id = 'wxid_user1'"));
            Assert.Equal(2L, Scalar(connection, "SELECT message_count FROM conversations WHERE native_id = 'wxid_user1'"));

            // Messages: duplicate m1 was merged, m2 was preserved
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE conversation_id = 20 OR conversation_id = 10"));

            // Contact sender was cleanly updated
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM contact_senders WHERE contact_id = 1"));
        }
    }

    [Fact]
    public void RepairDuplicateConversations_PreservesAttachmentsFromDuplicateMessages()
    {
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        using (var connection = db.OpenConnection())
        {
            Execute(connection, """
                INSERT INTO senders(id, platform, account_id, native_id, current_name, is_self)
                VALUES (1, 'wechat', 'wechat-default', 'wxid_user1', 'wxid_user1', 0),
                       (2, 'wechat', 'wxid_myaccount', 'wxid_user1', '用户一', 0);
                """);

            Execute(connection, """
                INSERT INTO conversations(id, platform, account_id, native_id, kind, title, message_count)
                VALUES (10, 'wechat', 'wechat-default', 'wxid_user1', 'private', 'wxid_user1', 1),
                       (20, 'wechat', 'wxid_myaccount', 'wxid_user1', 'private', '用户一', 1);
                """);

            // Canonical will be conv 20 (has non-default account_id).
            // Msg 100 is in dup conv 10. Msg 200 is in canonical conv 20.
            // Msg 100 has an attachment. Msg 200 does not.
            Execute(connection, """
                INSERT INTO messages(id, conversation_id, sender_id, platform, native_id, timestamp_ms,
                    direction, message_type, content, search_text, sender_name_snapshot,
                    conversation_title_snapshot, payload_hash, semantic_hash, raw_payload_json)
                VALUES (100, 10, 1, 'wechat', 'm1', 1700000000000, 'incoming', 'image', '[图片]', '[图片]', '用户一', '用户一', 'ph1', 'sh1', '{}'),
                       (200, 20, 2, 'wechat', 'm1', 1700000000000, 'incoming', 'image', '[图片]', '[图片]', '用户一', '用户一', 'ph1', 'sh1', '{}');
                """);

            Execute(connection, """
                INSERT INTO attachments(id, message_id, ordinal, kind, filename, is_available, metadata_json)
                VALUES (1, 100, 0, 'image', 'photo.jpg', 1, '{}');
                """);
        }

        var merged = db.RepairDuplicateConversationsAndSenders();
        Assert.True(merged >= 1);

        using (var connection = db.OpenConnection())
        {
            // The surviving message should be 200
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE id = 200"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE id = 100"));

            // Attachment should now be attached to 200
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM attachments"));
            Assert.Equal(200L, Scalar(connection, "SELECT message_id FROM attachments WHERE id = 1"));
        }
    }

    private static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string? ScalarText(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar() as string;
    }
}
