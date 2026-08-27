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
            using var transaction = connection.BeginTransaction();
            foreach (var statement in SqlScriptSplitter.Split(LoadSchemaSql()))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
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

    public int RepairDuplicateConversationsAndSenders(SqliteConnection? externalConnection = null)
    {
        var closeConnection = false;
        var connection = externalConnection;
        if (connection == null)
        {
            connection = OpenConnection();
            closeConnection = true;
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            var mergedCount = 0;

            // 1. Merge duplicate senders by (platform, native_id)
            var duplicateSenderGroups = new List<(string Platform, string NativeId)>();
            using (var findDupSenders = connection.CreateCommand())
            {
                findDupSenders.Transaction = transaction;
                findDupSenders.CommandText = """
                    SELECT platform, native_id FROM senders
                    GROUP BY platform, native_id
                    HAVING COUNT(*) > 1
                    """;
                using var reader = findDupSenders.ExecuteReader();
                while (reader.Read())
                {
                    duplicateSenderGroups.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (platform, nativeId) in duplicateSenderGroups)
            {
                var sendersInGroup = new List<(long Id, string AccountId, string Name, long IsSelf)>();
                using (var getSenders = connection.CreateCommand())
                {
                    getSenders.Transaction = transaction;
                    getSenders.CommandText = """
                        SELECT id, account_id, current_name, is_self FROM senders
                        WHERE platform = @platform AND native_id = @native
                        ORDER BY (account_id NOT LIKE '%-default') DESC, id ASC
                        """;
                    getSenders.Parameters.AddWithValue("@platform", platform);
                    getSenders.Parameters.AddWithValue("@native", nativeId);
                    using var reader = getSenders.ExecuteReader();
                    while (reader.Read())
                    {
                        sendersInGroup.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
                    }
                }

                if (sendersInGroup.Count <= 1) continue;

                var canonical = sendersInGroup[0];
                var duplicates = sendersInGroup.Skip(1).ToList();

                var bestAccountId = canonical.AccountId;
                var bestName = canonical.Name;
                var maxIsSelf = canonical.IsSelf;

                foreach (var dup in duplicates)
                {
                    if (bestAccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase) && !dup.AccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase))
                    {
                        bestAccountId = dup.AccountId;
                    }
                    if (bestName == nativeId && dup.Name != nativeId && !string.IsNullOrEmpty(dup.Name))
                    {
                        bestName = dup.Name;
                    }
                    maxIsSelf = Math.Max(maxIsSelf, dup.IsSelf);

                    using (var reassign = connection.CreateCommand())
                    {
                        reassign.Transaction = transaction;
                        reassign.CommandText = "UPDATE messages SET sender_id = @canonical WHERE sender_id = @dup";
                        reassign.Parameters.AddWithValue("@canonical", canonical.Id);
                        reassign.Parameters.AddWithValue("@dup", dup.Id);
                        reassign.ExecuteNonQuery();
                    }

                    using (var moveAliases = connection.CreateCommand())
                    {
                        moveAliases.Transaction = transaction;
                        moveAliases.CommandText = """
                            INSERT INTO sender_aliases(sender_id, conversation_id, alias, first_seen_at, last_seen_at)
                            SELECT @canonical, conversation_id, alias, first_seen_at, last_seen_at
                            FROM sender_aliases WHERE sender_id = @dup
                            ON CONFLICT(sender_id, conversation_id, alias) DO UPDATE SET
                                first_seen_at = MIN(COALESCE(first_seen_at, excluded.first_seen_at), excluded.first_seen_at),
                                last_seen_at = MAX(COALESCE(last_seen_at, excluded.last_seen_at), excluded.last_seen_at);
                            DELETE FROM sender_aliases WHERE sender_id = @dup;
                            """;
                        moveAliases.Parameters.AddWithValue("@canonical", canonical.Id);
                        moveAliases.Parameters.AddWithValue("@dup", dup.Id);
                        moveAliases.ExecuteNonQuery();
                    }

                    using (var moveContact = connection.CreateCommand())
                    {
                        moveContact.Transaction = transaction;
                        moveContact.CommandText = """
                            UPDATE OR IGNORE contact_senders SET sender_id = @canonical WHERE sender_id = @dup;
                            DELETE FROM contact_senders WHERE sender_id = @dup;
                            """;
                        moveContact.Parameters.AddWithValue("@canonical", canonical.Id);
                        moveContact.Parameters.AddWithValue("@dup", dup.Id);
                        moveContact.ExecuteNonQuery();
                    }

                    using (var del = connection.CreateCommand())
                    {
                        del.Transaction = transaction;
                        del.CommandText = "DELETE FROM senders WHERE id = @dup";
                        del.Parameters.AddWithValue("@dup", dup.Id);
                        del.ExecuteNonQuery();
                    }
                    mergedCount++;
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE senders SET account_id = @account, current_name = @name, is_self = @self, updated_at = CURRENT_TIMESTAMP
                        WHERE id = @id
                        """;
                    update.Parameters.AddWithValue("@account", bestAccountId);
                    update.Parameters.AddWithValue("@name", bestName);
                    update.Parameters.AddWithValue("@self", maxIsSelf);
                    update.Parameters.AddWithValue("@id", canonical.Id);
                    update.ExecuteNonQuery();
                }
            }

            // 2. Merge duplicate conversations by (platform, native_id)
            var duplicateConvGroups = new List<(string Platform, string NativeId)>();
            using (var findDupConvs = connection.CreateCommand())
            {
                findDupConvs.Transaction = transaction;
                findDupConvs.CommandText = """
                    SELECT platform, native_id FROM conversations
                    GROUP BY platform, native_id
                    HAVING COUNT(*) > 1
                    """;
                using var reader = findDupConvs.ExecuteReader();
                while (reader.Read())
                {
                    duplicateConvGroups.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (platform, nativeId) in duplicateConvGroups)
            {
                var convsInGroup = new List<(long Id, string AccountId, string Kind, string Title, long MessageCount)>();
                using (var getConvs = connection.CreateCommand())
                {
                    getConvs.Transaction = transaction;
                    getConvs.CommandText = """
                        SELECT id, account_id, kind, title, message_count FROM conversations
                        WHERE platform = @platform AND native_id = @native
                        ORDER BY message_count DESC, (account_id NOT LIKE '%-default') DESC, id ASC
                        """;
                    getConvs.Parameters.AddWithValue("@platform", platform);
                    getConvs.Parameters.AddWithValue("@native", nativeId);
                    using var reader = getConvs.ExecuteReader();
                    while (reader.Read())
                    {
                        convsInGroup.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
                    }
                }

                if (convsInGroup.Count <= 1) continue;

                var canonical = convsInGroup[0];
                var duplicates = convsInGroup.Skip(1).ToList();

                var bestAccountId = canonical.AccountId;
                var bestTitle = canonical.Title;
                var bestKind = canonical.Kind;

                foreach (var dup in duplicates)
                {
                    if (bestAccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase) && !dup.AccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase))
                    {
                        bestAccountId = dup.AccountId;
                    }
                    if (bestTitle == nativeId && dup.Title != nativeId && !string.IsNullOrEmpty(dup.Title))
                    {
                        bestTitle = dup.Title;
                    }

                    using (var moveAliases = connection.CreateCommand())
                    {
                        moveAliases.Transaction = transaction;
                        moveAliases.CommandText = """
                            INSERT INTO conversation_aliases(conversation_id, alias, first_seen_at, last_seen_at)
                            SELECT @canonical, alias, first_seen_at, last_seen_at FROM conversation_aliases WHERE conversation_id = @dup
                            ON CONFLICT(conversation_id, alias) DO UPDATE SET
                                first_seen_at = MIN(COALESCE(first_seen_at, excluded.first_seen_at), excluded.first_seen_at),
                                last_seen_at = MAX(COALESCE(last_seen_at, excluded.last_seen_at), excluded.last_seen_at);
                            DELETE FROM conversation_aliases WHERE conversation_id = @dup;
                            """;
                        moveAliases.Parameters.AddWithValue("@canonical", canonical.Id);
                        moveAliases.Parameters.AddWithValue("@dup", dup.Id);
                        moveAliases.ExecuteNonQuery();
                    }

                    using (var moveSenderAliases = connection.CreateCommand())
                    {
                        moveSenderAliases.Transaction = transaction;
                        moveSenderAliases.CommandText = """
                            INSERT INTO sender_aliases(sender_id, conversation_id, alias, first_seen_at, last_seen_at)
                            SELECT sender_id, @canonical, alias, first_seen_at, last_seen_at FROM sender_aliases WHERE conversation_id = @dup
                            ON CONFLICT(sender_id, conversation_id, alias) DO UPDATE SET
                                first_seen_at = MIN(COALESCE(first_seen_at, excluded.first_seen_at), excluded.first_seen_at),
                                last_seen_at = MAX(COALESCE(last_seen_at, excluded.last_seen_at), excluded.last_seen_at);
                            DELETE FROM sender_aliases WHERE conversation_id = @dup;
                            """;
                        moveSenderAliases.Parameters.AddWithValue("@canonical", canonical.Id);
                        moveSenderAliases.Parameters.AddWithValue("@dup", dup.Id);
                        moveSenderAliases.ExecuteNonQuery();
                    }

                    var dupMessages = new List<(long MsgId, string? NativeId, string PayloadHash, string SemanticHash)>();
                    using (var getMsgs = connection.CreateCommand())
                    {
                        getMsgs.Transaction = transaction;
                        getMsgs.CommandText = "SELECT id, native_id, payload_hash, semantic_hash FROM messages WHERE conversation_id = @dup ORDER BY id ASC";
                        getMsgs.Parameters.AddWithValue("@dup", dup.Id);
                        using var reader = getMsgs.ExecuteReader();
                        while (reader.Read())
                        {
                            dupMessages.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                        }
                    }

                    foreach (var msg in dupMessages)
                    {
                        long? existingTargetMsgId = null;
                        using (var findTarget = connection.CreateCommand())
                        {
                            findTarget.Transaction = transaction;
                            if (msg.NativeId != null)
                            {
                                findTarget.CommandText = "SELECT id FROM messages WHERE conversation_id = @canonical AND native_id = @native AND payload_hash = @ph LIMIT 1";
                                findTarget.Parameters.AddWithValue("@canonical", canonical.Id);
                                findTarget.Parameters.AddWithValue("@native", msg.NativeId);
                                findTarget.Parameters.AddWithValue("@ph", msg.PayloadHash);
                            }
                            else
                            {
                                findTarget.CommandText = "SELECT id FROM messages WHERE conversation_id = @canonical AND semantic_hash = @sem AND payload_hash = @ph LIMIT 1";
                                findTarget.Parameters.AddWithValue("@canonical", canonical.Id);
                                findTarget.Parameters.AddWithValue("@sem", msg.SemanticHash);
                                findTarget.Parameters.AddWithValue("@ph", msg.PayloadHash);
                            }
                            var res = findTarget.ExecuteScalar();
                            if (res is not null and long id)
                            {
                                existingTargetMsgId = id;
                            }
                        }

                        if (existingTargetMsgId.HasValue)
                        {
                            using (var moveObs = connection.CreateCommand())
                            {
                                moveObs.Transaction = transaction;
                                moveObs.CommandText = """
                                    INSERT OR IGNORE INTO message_observations(message_id, import_file_id, source_locator, observed_payload_hash, observed_at)
                                    SELECT @target, import_file_id, source_locator, observed_payload_hash, observed_at
                                    FROM message_observations WHERE message_id = @old;
                                    DELETE FROM message_observations WHERE message_id = @old;
                                    """;
                                moveObs.Parameters.AddWithValue("@target", existingTargetMsgId.Value);
                                moveObs.Parameters.AddWithValue("@old", msg.MsgId);
                                moveObs.ExecuteNonQuery();
                            }

                            using (var moveAtt = connection.CreateCommand())
                            {
                                moveAtt.Transaction = transaction;
                                moveAtt.CommandText = """
                                    UPDATE OR IGNORE attachments SET message_id = @target WHERE message_id = @old;
                                    DELETE FROM attachments WHERE message_id = @old;
                                    """;
                                moveAtt.Parameters.AddWithValue("@target", existingTargetMsgId.Value);
                                moveAtt.Parameters.AddWithValue("@old", msg.MsgId);
                                moveAtt.ExecuteNonQuery();
                            }

                            using (var delMsg = connection.CreateCommand())
                            {
                                delMsg.Transaction = transaction;
                                delMsg.CommandText = "DELETE FROM messages WHERE id = @id";
                                delMsg.Parameters.AddWithValue("@id", msg.MsgId);
                                delMsg.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            using (var reassignMsg = connection.CreateCommand())
                            {
                                reassignMsg.Transaction = transaction;
                                reassignMsg.CommandText = "UPDATE messages SET conversation_id = @canonical WHERE id = @id";
                                reassignMsg.Parameters.AddWithValue("@canonical", canonical.Id);
                                reassignMsg.Parameters.AddWithValue("@id", msg.MsgId);
                                reassignMsg.ExecuteNonQuery();
                            }
                        }
                    }

                    using (var delConv = connection.CreateCommand())
                    {
                        delConv.Transaction = transaction;
                        delConv.CommandText = "DELETE FROM conversations WHERE id = @dup";
                        delConv.Parameters.AddWithValue("@dup", dup.Id);
                        delConv.ExecuteNonQuery();
                    }
                    mergedCount++;
                }

                using (var updateConv = connection.CreateCommand())
                {
                    updateConv.Transaction = transaction;
                    updateConv.CommandText = """
                        UPDATE conversations SET
                            account_id = @account,
                            title = @title,
                            kind = @kind,
                            message_count = (SELECT COUNT(*) FROM messages WHERE conversation_id = @canonical),
                            first_message_at = (SELECT MIN(timestamp_ms) FROM messages WHERE conversation_id = @canonical),
                            last_message_at = (SELECT MAX(timestamp_ms) FROM messages WHERE conversation_id = @canonical),
                            updated_at = CURRENT_TIMESTAMP
                        WHERE id = @canonical
                        """;
                    updateConv.Parameters.AddWithValue("@account", bestAccountId);
                    updateConv.Parameters.AddWithValue("@title", bestTitle);
                    updateConv.Parameters.AddWithValue("@kind", bestKind);
                    updateConv.Parameters.AddWithValue("@canonical", canonical.Id);
                    updateConv.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return mergedCount;
        }
        finally
        {
            if (closeConnection)
            {
                connection.Dispose();
            }
        }
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
