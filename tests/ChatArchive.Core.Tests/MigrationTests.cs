using ChatArchive.Core.Data;
using ChatArchive.Core.Migration;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatArchive.Core.Tests;

public class MigrationTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private const string Sha = "ab" + "cd" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789ab";

    public MigrationTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"chatarchive-migrate-{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_baseDir, "src-data");
        _targetDir = Path.Combine(_baseDir, "dst");
        Directory.CreateDirectory(Path.Combine(_sourceDir, "media", Sha[..2]));
        Directory.CreateDirectory(_targetDir);
    }

    private void SeedSource()
    {
        var db = new ArchiveDatabase(Path.Combine(_sourceDir, "chat_archive.db"));
        db.EnsureSchema();
        using var connection = db.OpenConnection();

        Insert(connection, """
            INSERT INTO conversations(id, platform, account_id, native_id, kind, title)
            VALUES (1, 'qq', 'acc', 'c1', 'private', '会话')
            """);
        Insert(connection, """
            INSERT INTO messages(id, conversation_id, platform, timestamp_ms, direction,
                message_type, content, search_text, sender_name_snapshot, conversation_title_snapshot,
                payload_hash, semantic_hash, raw_payload_json)
            VALUES (100, 1, 'qq', 1700000000000, 'incoming', 'text', '你好', '你好', 'Alice', '会话',
                    'ph', 'sh', '{"a":1}')
            """);
        Insert(connection, $"""
            INSERT INTO media_objects(id, sha256, size, mime_type, managed_path, first_source_path)
            VALUES (10, '{Sha}', 2, 'image/jpeg',
                    'E:\backup\QQ+wx\chat-archive-app\data\media\{Sha}.jpg',
                    'E:\backup\QQexports\orig.jpg')
            """);
        Insert(connection, """
            INSERT INTO attachments(message_id, ordinal, kind, is_available, media_object_id, source_path)
            VALUES (100, 0, 'image', 1, 10, 'E:\backup\QQexports\orig.jpg')
            """);

        File.WriteAllBytes(Path.Combine(_sourceDir, "media", Sha[..2], $"{Sha}.jpg"), new byte[] { 9, 9 });
    }

    [Fact]
    public void Migrates_copies_rewrites_and_verifies()
    {
        SeedSource();
        var report = new MigrationRunner(_sourceDir, _targetDir).Run();

        Assert.Equal(1L, report.Conversations);
        Assert.Equal(1L, report.Messages);
        Assert.Equal(1L, report.Attachments);
        Assert.Equal(1L, report.MediaObjects);
        Assert.Equal(1L, report.MediaFilesCopied);
        Assert.Equal(1L, report.ManagedPathsRewritten);
        Assert.True(report.Verified);

        var expectedMedia = Path.Combine(_targetDir, "media", Sha[..2], $"{Sha}.jpg");
        Assert.True(File.Exists(expectedMedia));

        using var connection = OpenReadOnly(Path.Combine(_targetDir, "chat_archive.db"));
        Assert.Equal(expectedMedia, Text(connection, $"SELECT managed_path FROM media_objects WHERE id=10"));
        Assert.Equal(@"E:\backup\QQexports\orig.jpg", Text(connection, "SELECT first_source_path FROM media_objects WHERE id=10"));

        Assert.True(File.Exists(Path.Combine(_targetDir, "README.md")));
    }

    [Fact]
    public void Second_run_backups_target_and_skips_existing_media()
    {
        SeedSource();
        var runner = new MigrationRunner(_sourceDir, _targetDir);
        runner.Run();

        var second = runner.Run();
        Assert.Equal(0L, second.MediaFilesCopied);
        Assert.Equal(1L, second.MediaFilesSkipped);

        var backups = Directory.GetFiles(_targetDir, "chat_archive.db.bak-*");
        Assert.Single(backups);
    }

    [Fact]
    public void Missing_source_db_throws()
    {
        Assert.Throws<FileNotFoundException>(() => new MigrationRunner(_sourceDir, _targetDir).Run());
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        return connection;
    }

    private static void Insert(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_baseDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
