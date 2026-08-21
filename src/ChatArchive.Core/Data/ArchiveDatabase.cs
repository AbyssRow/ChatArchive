using Microsoft.Data.Sqlite;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ChatArchive.Core.Tests")]

namespace ChatArchive.Core.Data;

public sealed class ArchiveDatabase
{
    private readonly string _databasePath;

    public ArchiveDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        ExecuteScalar(connection, "PRAGMA journal_mode=WAL");
        ExecuteScalar(connection, "PRAGMA foreign_keys=ON");
        ExecuteScalar(connection, "PRAGMA busy_timeout=5000");
        return connection;
    }

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        foreach (var statement in SqlScriptSplitter.Split(LoadSchemaSql()))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        var version = ExecuteScalar(connection, "SELECT value FROM app_metadata WHERE key='schema_version'") as string;
        if (version != "1")
        {
            throw new InvalidOperationException($"不支持的数据库 schema 版本: {version ?? "(缺失)"}");
        }
    }

    public int CleanEmptyConversations()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM conversations
            WHERE NOT EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = conversations.id)
            """;
        return command.ExecuteNonQuery();
    }

    internal static string LoadSchemaSql()
    {
        var assembly = typeof(ArchiveDatabase).Assembly;
        var resourceName = "ChatArchive.Core.Data.schema.sql";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"缺少嵌入资源 {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static object? ExecuteScalar(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar();
    }
}
