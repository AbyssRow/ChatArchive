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

    private const string MigrationV1ToV2Sql = """
        CREATE TABLE IF NOT EXISTS contacts (
            id INTEGER PRIMARY KEY,
            display_name TEXT NOT NULL,
            custom_avatar_path TEXT,
            note TEXT,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
        );

        CREATE INDEX IF NOT EXISTS ix_contacts_display_name ON contacts(display_name);

        CREATE TABLE IF NOT EXISTS contact_senders (
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            sender_id INTEGER NOT NULL REFERENCES senders(id) ON DELETE CASCADE,
            account_label TEXT,
            is_primary INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (contact_id, sender_id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_contact_senders_sender ON contact_senders(sender_id);
        CREATE INDEX IF NOT EXISTS ix_contact_senders_contact ON contact_senders(contact_id);

        UPDATE app_metadata SET value = '2' WHERE key = 'schema_version';
        """;

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        var hasMetadata = ExecuteScalar(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='app_metadata'") is not null;

        if (!hasMetadata)
        {
            foreach (var statement in SqlScriptSplitter.Split(LoadSchemaSql()))
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }
        else
        {
            var currentVersion = ExecuteScalar(connection, "SELECT value FROM app_metadata WHERE key='schema_version'") as string;
            if (currentVersion == "1")
            {
                MigrateV1ToV2(connection);
            }
        }

        var version = ExecuteScalar(connection, "SELECT value FROM app_metadata WHERE key='schema_version'") as string;
        if (version != "2")
        {
            throw new InvalidOperationException($"不支持的数据库 schema 版本: {version ?? "(缺失)"}");
        }
    }

    private static void MigrateV1ToV2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var statement in SqlScriptSplitter.Split(MigrationV1ToV2Sql))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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
