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
    public void EnsureSchema_creates_version_1()
    {
        var db = new ArchiveDatabase(_databasePath);
        db.EnsureSchema();

        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key='schema_version'";
        Assert.Equal("1", command.ExecuteScalar());
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
        var triggers = statements.Where(s => s.StartsWith("CREATE TRIGGER", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, triggers.Count);
        Assert.All(triggers, s => Assert.EndsWith("END;", s));
        Assert.All(statements.Except(triggers), s => Assert.False(s.EndsWith(";", StringComparison.Ordinal)));
        Assert.Equal(29, statements.Count);
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
}
