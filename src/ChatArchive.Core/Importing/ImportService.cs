using System.Text.Json.Nodes;
using ChatArchive.Core.Data;
using ChatArchive.Core.IO;
using ChatArchive.Core.Media;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Importing;

/// <summary>
/// 导入服务：文件发现、事务、三层去重（文件哈希/原生ID/载荷哈希）、
/// 版本保留与媒体复制。规则从旧版 service.py 移植，解析器版本 5。
/// </summary>
public sealed class ImportService
{
    public const int ParserVersion = 5;

    private readonly ArchiveDatabase _db;
    private readonly string _mediaDir;
    private readonly bool _copyMedia;
    private readonly IReadOnlyList<IChatExportFormat> _formats;
    private readonly Dictionary<string, (string Digest, long Size, string? Managed)> _mediaCache = new();

    public ImportService(
        ArchiveDatabase db,
        string mediaDir,
        bool copyMedia = true,
        IReadOnlyList<IChatExportFormat>? formats = null)
    {
        _db = db;
        _mediaDir = Path.GetFullPath(mediaDir);
        _copyMedia = copyMedia;
        _formats = formats ?? ExportFormats.Default;
    }

    public async Task<ImportRunResult> RunAsync(
        IReadOnlyList<string> roots,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Run(roots, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public ImportRunResult Run(
        IReadOnlyList<string> roots,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dataDir = Path.GetDirectoryName(_db.DatabasePath)!;
        using var processLock = AcquireCrossProcessLock(
            Path.Combine(dataDir, ".import.lock"),
            cancellationToken);
        _mediaCache.Clear();
        RecoverStaleRuns();

        var files = ImportDiscovery.Discover(
            roots,
            _formats,
            new[] { dataDir, AppContext.BaseDirectory },
            cancellationToken);
        var totals = new Counters();
        var fileResults = new List<FileImportResult>();
        var runId = CreateRun(roots);

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var discovered = files[index];
                var result = discovered.Error is { } discoveryError
                    ? MakeResult(
                        discovered.FilePath,
                        discovered.Platform,
                        "failed",
                        new Counters(),
                        discoveryError)
                    : ImportFile(
                        discovered.FilePath,
                        discovered.Platform,
                        runId,
                        cancellationToken);
                fileResults.Add(result);
                totals.Add(result);
                progress?.Report(new ImportProgress(
                    ImportPhase.Importing, index + 1, files.Count, discovered.FilePath,
                    totals.MessagesSeen, totals.Added, totals.Duplicates, totals.Revised,
                    totals.Variants, totals.Attachments, totals.MissingMedia));
            }

            var status = fileResults.Count(r => r.Status == "failed") > 0
                ? "completed_with_errors"
                : "completed";
            FinishRun(runId, status, totals, fileResults, error: null);
            progress?.Report(new ImportProgress(
                ImportPhase.Done, files.Count, files.Count, string.Empty,
                totals.MessagesSeen, totals.Added, totals.Duplicates, totals.Revised,
                totals.Variants, totals.Attachments, totals.MissingMedia));

            return BuildRunResult(files.Count, fileResults, totals);
        }
        catch (OperationCanceledException)
        {
            FinishRun(runId, "interrupted", totals, fileResults, "导入被用户中止");
            throw;
        }
        catch (Exception ex)
        {
            FinishRun(runId, "failed", totals, fileResults, ex.Message);
            throw;
        }
    }

    private static FileStream AcquireCrossProcessLock(
        string lockPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return stream;
            }
            catch (IOException)
            {
                stream?.Dispose();
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("另一个导入正在进行，请稍后再试");
                }

                Thread.Sleep(500);
            }
        }
    }

    private void RecoverStaleRuns()
    {
        using var connection = _db.OpenConnection();
        Execute(connection, """
            UPDATE import_runs SET status='interrupted', finished_at=CURRENT_TIMESTAMP,
                error='interrupted by another process or restart' WHERE status='running'
            """);
        Execute(connection, """
            UPDATE import_files SET status='interrupted',
                error='interrupted by another process or restart' WHERE status='importing'
            """);
    }

    private long CreateRun(IReadOnlyList<string> roots)
    {
        var array = new JsonArray();
        foreach (var root in roots)
        {
            array.Add(root);
        }

        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO import_runs(root_paths_json) VALUES (@p); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@p", CanonicalJson.Serialize(array));
        return (long)command.ExecuteScalar()!;
    }

    private void FinishRun(long runId, string status, Counters totals, List<FileImportResult> files, string? error)
    {
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE import_runs SET status=@status, finished_at=CURRENT_TIMESTAMP,
                stats_json=@stats, error=@error WHERE id=@id
            """;
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@stats", CanonicalJson.Serialize(totals.ToJson(files.Count)));
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", runId);
        command.ExecuteNonQuery();
    }

    internal FileImportResult ImportFile(
        string filePath,
        string platform,
        long runId,
        CancellationToken cancellationToken = default)
    {
        var counters = new Counters();
        var createdMedia = new HashSet<CreatedManagedMedia>();
        long? importFileId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = FileHashing.ComputeImportDigest(filePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(filePath);
            long? completedFileId = null;

            using (var connection = _db.OpenConnection())
            {
                using var dupCommand = connection.CreateCommand();
                dupCommand.CommandText = """
                    SELECT f.id
                    FROM import_files f
                    WHERE f.sha256 = @sha AND f.status = 'completed'
                    """;
                dupCommand.Parameters.AddWithValue("@sha", digest);
                using var reader = dupCommand.ExecuteReader();
                if (reader.Read())
                {
                    completedFileId = reader.GetInt64(0);
                    reader.Close();
                    var previous = ReadFileStats(connection, completedFileId.Value);
                    if (!CompletedFileNeedsReimport(connection, completedFileId.Value, previous))
                    {
                        return MakeResult(filePath, platform, "skipped", counters, null);
                    }
                }
            }

            var format = _formats.FirstOrDefault(f => f.Matches(filePath))
                ?? _formats.FirstOrDefault(f => string.Equals(f.Platform, platform, StringComparison.OrdinalIgnoreCase))
                ?? throw new ImportFormatException(filePath, $"未找到支持的导出格式解析器（平台: {platform}）");
            using var exportFile = format.Open(filePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            using (var connection = _db.OpenConnection())
            {
                if (completedFileId.HasValue)
                {
                    TouchImportFileRow(
                        connection,
                        completedFileId.Value,
                        runId,
                        platform,
                        filePath,
                        fileInfo);
                    importFileId = completedFileId.Value;
                }
                else
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO import_files(import_run_id, platform, source_path, sha256, file_size, modified_at_ns, status)
                        VALUES (@run, @platform, @path, @sha, @size, @mtime, 'importing');
                        SELECT last_insert_rowid();
                        """;
                    insert.Parameters.AddWithValue("@run", runId);
                    insert.Parameters.AddWithValue("@platform", platform);
                    insert.Parameters.AddWithValue("@path", filePath);
                    insert.Parameters.AddWithValue("@sha", digest);
                    insert.Parameters.AddWithValue("@size", fileInfo.Length);
                    insert.Parameters.AddWithValue("@mtime", fileInfo.LastWriteTimeUtc.Ticks);
                    importFileId = (long)insert.ExecuteScalar()!;
                }
            }

            using var transactionConnection = _db.OpenConnection();
            using var transaction = transactionConnection.BeginTransaction();
            try
            {
                long? conversationId = null;
                foreach (var message in exportFile.EnumerateMessages(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    conversationId ??= UpsertConversation(transactionConnection, exportFile.Conversation);
                    counters.MessagesSeen++;
                    var (messageId, state) = UpsertMessage(
                        transactionConnection,
                        conversationId.Value,
                        exportFile.Conversation,
                        message);
                    if (state == "duplicate")
                    {
                        counters.Duplicates++;
                    }
                    else
                    {
                        counters.Added++;
                        if (state == "revision")
                        {
                            counters.Revised++;
                        }
                        else if (state == "variant")
                        {
                            counters.Variants++;
                        }
                    }

                    RecordObservation(
                        transactionConnection,
                        messageId,
                        importFileId.Value,
                        message.SourceLocator,
                        message.PayloadHash);
                    var attachments = AttachmentsFor(message);
                    var (attachmentCount, missing) = UpsertAttachments(
                        transactionConnection,
                        messageId,
                        attachments,
                        cancellationToken,
                        createdMedia);
                    counters.Attachments += attachmentCount;
                    counters.MissingMedia += missing;
                }

                if (counters.MessagesSeen == 0)
                {
                    throw new ImportFormatException(filePath, "导出文件中没有有效消息");
                }

                var finalDigest = FileHashing.ComputeImportDigest(filePath, cancellationToken);
                if (!string.Equals(digest, finalDigest, StringComparison.Ordinal))
                {
                    throw new ImportFormatException(filePath, "导入期间文件发生变化，请重试");
                }

                cancellationToken.ThrowIfCancellationRequested();
                MarkImportFile(
                    transactionConnection,
                    importFileId.Value,
                    "completed",
                    counters,
                    error: null);
                transaction.Commit();
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                finally
                {
                    CleanupUnreferencedMedia(createdMedia);
                    _mediaCache.Clear();
                }

                throw;
            }

            return MakeResult(filePath, platform, "completed", counters, null);
        }
        catch (OperationCanceledException)
        {
            counters.Reset();
            if (importFileId.HasValue)
            {
                using var interruptedConnection = _db.OpenConnection();
                MarkImportFile(
                    interruptedConnection,
                    importFileId.Value,
                    "interrupted",
                    counters,
                    "导入被用户中止");
            }

            throw;
        }
        catch (SqliteException ex)
        {
            counters.Reset();
            if (importFileId.HasValue)
            {
                TryMarkImportFile(importFileId.Value, "failed", counters, ex.Message);
            }

            return MakeResult(filePath, platform, "failed", counters, ex.Message);
        }
        catch (Exception ex)
        {
            counters.Reset();
            if (importFileId.HasValue)
            {
                using var failureConnection = _db.OpenConnection();
                MarkImportFile(failureConnection, importFileId.Value, "failed", counters, ex.Message);
            }

            return MakeResult(filePath, platform, "failed", counters, ex.Message);
        }
    }

    private void TryMarkImportFile(
        long importFileId,
        string status,
        Counters counters,
        string error)
    {
        try
        {
            using var connection = _db.OpenConnection();
            MarkImportFile(connection, importFileId, status, counters, error);
        }
        catch (SqliteException)
        {
            // Preserve the original database exception when status persistence also fails.
        }
    }

    private static FileStats ReadFileStats(SqliteConnection connection, long fileId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stats_json FROM import_files WHERE id = @id";
        command.Parameters.AddWithValue("@id", fileId);
        var json = command.ExecuteScalar() as string;

        long parserVersion = 0;
        long missingMedia = 0;
        if (!string.IsNullOrEmpty(json)
            && JsonNode.Parse(json) is JsonObject obj)
        {
            parserVersion = obj.TryGetPropertyValue("parser_version", out var pv) && pv is not null
                ? ImportText.AsLong(pv) ?? 0
                : 0;
            missingMedia = obj.TryGetPropertyValue("missing_media", out var mm) && mm is not null
                ? ImportText.AsLong(mm) ?? 0
                : 0;
        }

        return new FileStats(parserVersion, missingMedia);
    }

    private bool CompletedFileNeedsReimport(
        SqliteConnection connection,
        long fileId,
        FileStats stats)
    {
        if (stats.ParserVersion < ParserVersion || stats.MissingMedia > 0)
        {
            return true;
        }

        using (var missing = connection.CreateCommand())
        {
            missing.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM message_observations obs
                    JOIN messages m ON m.id = obs.message_id
                    WHERE obs.import_file_id = @file
                      AND (
                          EXISTS(
                              SELECT 1 FROM attachments a
                              WHERE a.message_id = m.id AND a.is_available = 0)
                          OR (
                              (LOWER(COALESCE(m.media_type, ''))
                                   IN ('image', 'file', 'video', 'audio', 'voice', 'emoji', 'sticker')
                               OR LOWER(COALESCE(m.message_type, ''))
                                   IN ('image', 'file', 'video', 'audio', 'voice', 'emoji', 'sticker'))
                              AND NOT EXISTS(
                                  SELECT 1 FROM attachments a WHERE a.message_id = m.id)
                          )
                      )
                )
                """;
            missing.Parameters.AddWithValue("@file", fileId);
            if (Convert.ToInt64(missing.ExecuteScalar()) != 0)
            {
                return true;
            }
        }

        var locator = new MediaLocator(_mediaDir);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT mo.sha256, mo.managed_path, a.source_path
            FROM message_observations obs
            JOIN attachments a ON a.message_id = obs.message_id
            LEFT JOIN media_objects mo ON mo.id = a.media_object_id
            WHERE obs.import_file_id = @file AND a.is_available = 1
            """;
        command.Parameters.AddWithValue("@file", fileId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sha = reader.IsDBNull(0) ? null : reader.GetString(0);
            var managedPath = reader.IsDBNull(1) ? null : reader.GetString(1);
            var sourcePath = reader.IsDBNull(2) ? null : reader.GetString(2);
            var safeSha = sha is { Length: >= 2 } ? sha : null;
            var resolved = locator.Resolve(safeSha, managedPath, sourcePath);
            if (resolved is null)
            {
                return true;
            }

            if (_copyMedia
                && !string.IsNullOrEmpty(sourcePath)
                && File.Exists(sourcePath)
                && (string.IsNullOrEmpty(managedPath) || !File.Exists(managedPath))
                && string.Equals(
                    Path.GetFullPath(resolved),
                    Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct FileStats(long ParserVersion, long MissingMedia);

    private static void TouchImportFileRow(
        SqliteConnection connection, long fileId, long runId, string platform, string path, FileInfo info)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE import_files SET import_run_id=@run, platform=@platform, source_path=@path,
                file_size=@size, modified_at_ns=@mtime, status='importing', started_at=CURRENT_TIMESTAMP,
                finished_at=NULL, stats_json='{}', error=NULL WHERE id=@id
            """;
        command.Parameters.AddWithValue("@run", runId);
        command.Parameters.AddWithValue("@platform", platform);
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@size", info.Length);
        command.Parameters.AddWithValue("@mtime", info.LastWriteTimeUtc.Ticks);
        command.Parameters.AddWithValue("@id", fileId);
        command.ExecuteNonQuery();
    }

    private static long UpsertConversation(SqliteConnection connection, ParsedConversation conversation)
    {
        long conversationId;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, account_id, title FROM conversations
                WHERE platform=@platform AND native_id=@native
                ORDER BY (account_id = @account) DESC, (account_id NOT LIKE '%-default') DESC, id ASC
                LIMIT 1
                """;
            select.Parameters.AddWithValue("@platform", conversation.Platform);
            select.Parameters.AddWithValue("@native", conversation.NativeId);
            select.Parameters.AddWithValue("@account", conversation.AccountId);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                conversationId = reader.GetInt64(0);
                var existingAccountId = reader.GetString(1);
                var existingTitle = reader.GetString(2);
                reader.Close();

                var shouldUpgradeAccount = existingAccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase)
                    && !conversation.AccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase);

                using var update = connection.CreateCommand();
                if (shouldUpgradeAccount)
                {
                    update.CommandText = """
                        UPDATE conversations SET
                            account_id = @account,
                            title = CASE WHEN @title <> '' AND (title = native_id OR @title <> title) THEN @title ELSE title END,
                            kind = @kind,
                            updated_at = CURRENT_TIMESTAMP
                        WHERE id = @id
                        """;
                    update.Parameters.AddWithValue("@account", conversation.AccountId);
                }
                else
                {
                    update.CommandText = """
                        UPDATE conversations SET
                            title = CASE WHEN @title <> '' AND title = native_id THEN @title ELSE title END,
                            kind = @kind,
                            updated_at = CURRENT_TIMESTAMP
                        WHERE id = @id AND (kind <> @kind OR (title = native_id AND @title <> ''))
                        """;
                }

                update.Parameters.AddWithValue("@title", conversation.Title);
                update.Parameters.AddWithValue("@kind", conversation.Kind);
                update.Parameters.AddWithValue("@id", conversationId);
                update.ExecuteNonQuery();
            }
            else
            {
                reader.Close();
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO conversations(platform, account_id, native_id, kind, title)
                    VALUES (@platform, @account, @native, @kind, @title);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("@platform", conversation.Platform);
                insert.Parameters.AddWithValue("@account", conversation.AccountId);
                insert.Parameters.AddWithValue("@native", conversation.NativeId);
                insert.Parameters.AddWithValue("@kind", conversation.Kind);
                insert.Parameters.AddWithValue("@title", conversation.Title);
                conversationId = (long)insert.ExecuteScalar()!;
            }
        }

        using var alias = connection.CreateCommand();
        alias.CommandText = """
            INSERT INTO conversation_aliases(conversation_id, alias) VALUES (@id, @alias)
            ON CONFLICT(conversation_id, alias) DO NOTHING
            """;
        alias.Parameters.AddWithValue("@id", conversationId);
        alias.Parameters.AddWithValue("@alias", conversation.Title);
        alias.ExecuteNonQuery();
        return conversationId;
    }

    private static long UpsertSender(
        SqliteConnection connection, long conversationId, ParsedConversation conversation, ParsedMessage message)
    {
        var isSelf = message.Direction == "outgoing" ? 1L : 0L;
        long senderId;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, account_id, current_name, is_self FROM senders
                WHERE platform=@platform AND native_id=@native
                ORDER BY (account_id = @account) DESC, (account_id NOT LIKE '%-default') DESC, id ASC
                LIMIT 1
                """;
            select.Parameters.AddWithValue("@platform", conversation.Platform);
            select.Parameters.AddWithValue("@native", message.SenderNativeId);
            select.Parameters.AddWithValue("@account", conversation.AccountId);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                senderId = reader.GetInt64(0);
                var existingAccountId = reader.GetString(1);
                var existingName = reader.GetString(2);
                var existingIsSelf = reader.GetInt64(3);
                reader.Close();

                var shouldUpgradeAccount = existingAccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase)
                    && !conversation.AccountId.EndsWith("-default", StringComparison.OrdinalIgnoreCase);

                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE senders SET
                        account_id = CASE WHEN @upgrade = 1 THEN @account ELSE account_id END,
                        current_name = CASE
                            WHEN @platform = 'wechat' AND @name = @native AND current_name <> @native THEN current_name
                            WHEN @name <> '' THEN @name
                            ELSE current_name END,
                        is_self = MAX(is_self, @self),
                        updated_at = CURRENT_TIMESTAMP
                    WHERE id = @id
                    """;
                update.Parameters.AddWithValue("@upgrade", shouldUpgradeAccount ? 1L : 0L);
                update.Parameters.AddWithValue("@account", conversation.AccountId);
                update.Parameters.AddWithValue("@platform", conversation.Platform);
                update.Parameters.AddWithValue("@native", message.SenderNativeId);
                update.Parameters.AddWithValue("@name", message.SenderName);
                update.Parameters.AddWithValue("@self", isSelf);
                update.Parameters.AddWithValue("@id", senderId);
                update.ExecuteNonQuery();
            }
            else
            {
                reader.Close();
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO senders(platform, account_id, native_id, current_name, is_self)
                    VALUES (@platform, @account, @native, @name, @self);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("@platform", conversation.Platform);
                insert.Parameters.AddWithValue("@account", conversation.AccountId);
                insert.Parameters.AddWithValue("@native", message.SenderNativeId);
                insert.Parameters.AddWithValue("@name", message.SenderName);
                insert.Parameters.AddWithValue("@self", isSelf);
                senderId = (long)insert.ExecuteScalar()!;
            }
        }

        foreach (var alias in message.SenderAliases.Prepend(message.SenderName).Distinct())
        {
            if (string.IsNullOrEmpty(alias))
            {
                continue;
            }

            using var aliasUpsert = connection.CreateCommand();
            aliasUpsert.CommandText = """
                INSERT INTO sender_aliases(sender_id, conversation_id, alias, first_seen_at, last_seen_at)
                VALUES (@sender, @conv, @alias, @ts, @ts)
                ON CONFLICT(sender_id, conversation_id, alias) DO UPDATE SET
                    first_seen_at = MIN(COALESCE(first_seen_at, excluded.first_seen_at), excluded.first_seen_at),
                    last_seen_at = MAX(COALESCE(last_seen_at, excluded.last_seen_at), excluded.last_seen_at)
                """;
            aliasUpsert.Parameters.AddWithValue("@sender", senderId);
            aliasUpsert.Parameters.AddWithValue("@conv", conversationId);
            aliasUpsert.Parameters.AddWithValue("@alias", alias);
            aliasUpsert.Parameters.AddWithValue("@ts", message.TimestampMs);
            aliasUpsert.ExecuteNonQuery();
        }

        return senderId;
    }

    private (long MessageId, string State) UpsertMessage(
        SqliteConnection connection, long conversationId, ParsedConversation conversation, ParsedMessage message)
    {
        var senderId = UpsertSender(connection, conversationId, conversation, message);

        var candidates = new List<(long Id, string? LocalId, string PayloadHash, string SemanticHash)>();
        using (var select = connection.CreateCommand())
        {
            if (message.NativeId is not null)
            {
                select.CommandText = """
                    SELECT id, local_id, payload_hash, semantic_hash FROM messages
                    WHERE conversation_id=@conv AND native_id=@native ORDER BY id
                    """;
                select.Parameters.AddWithValue("@conv", conversationId);
                select.Parameters.AddWithValue("@native", message.NativeId);
            }
            else
            {
                var hashes = message.CompatiblePayloadHashes.Prepend(message.PayloadHash).Distinct().ToList();
                var placeholders = string.Join(",", hashes.Select((_, i) => $"@h{i}"));
                select.CommandText = $$"""
                    SELECT id, local_id, payload_hash, semantic_hash FROM messages
                    WHERE conversation_id=@conv AND native_id IS NULL
                      AND (semantic_hash=@sem OR payload_hash IN ({{placeholders}}))
                    ORDER BY id
                    """;
                select.Parameters.AddWithValue("@conv", conversationId);
                select.Parameters.AddWithValue("@sem", message.SemanticHash);
                for (var i = 0; i < hashes.Count; i++)
                {
                    select.Parameters.AddWithValue($"@h{i}", hashes[i]);
                }
            }

            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.PayloadHash == message.PayloadHash)
            {
                RefreshDuplicateMessage(connection, candidate.Id, senderId, message);
                return (candidate.Id, "duplicate");
            }
        }

        var compatible = message.CompatiblePayloadHashes.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (compatible.Contains(candidate.PayloadHash))
            {
                RefreshDuplicateMessage(connection, candidate.Id, senderId, message);
                return (candidate.Id, "duplicate");
            }
        }

        long? revisionOf = null;
        var state = "new";
        if (candidates.Count > 0)
        {
            if (conversation.Platform == "qq")
            {
                revisionOf = candidates[0].Id;
            }
            else
            {
                foreach (var candidate in candidates)
                {
                    var sameLocal = !string.IsNullOrEmpty(message.LocalId)
                        && candidate.LocalId == message.LocalId;
                    var sameSignature = candidate.SemanticHash == message.SemanticHash;
                    if (sameLocal || sameSignature)
                    {
                        revisionOf = candidate.Id;
                        break;
                    }
                }
            }

            state = revisionOf.HasValue ? "revision" : "variant";
        }

        long messageId;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO messages(
                    conversation_id, sender_id, platform, native_id, local_id, timestamp_ms, sequence,
                    direction, message_type, media_type, content, search_text, sender_name_snapshot,
                    conversation_title_snapshot, is_recalled, is_system, reply_to_native_id,
                    payload_hash, semantic_hash, revision_of_id, raw_payload_json)
                VALUES (@conv, @sender, @platform, @native, @local, @ts, @seq,
                    @direction, @type, @media, @content, @search, @senderName,
                    @convTitle, @recalled, @system, @reply,
                    @payload, @semantic, @revision, @raw);
                SELECT last_insert_rowid();
                """;
            BindMessage(insert, conversationId, senderId, conversation, message, revisionOf);
            messageId = (long)insert.ExecuteScalar()!;
        }

        BumpConversationWindow(connection, conversationId, message.TimestampMs);
        return (messageId, state);
    }

    private static void BindMessage(
        SqliteCommand command, long conversationId, long senderId,
        ParsedConversation conversation, ParsedMessage message, long? revisionOf)
    {
        command.Parameters.AddWithValue("@conv", conversationId);
        command.Parameters.AddWithValue("@sender", senderId);
        command.Parameters.AddWithValue("@platform", conversation.Platform);
        command.Parameters.AddWithValue("@native", (object?)message.NativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@local", (object?)message.LocalId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ts", message.TimestampMs);
        command.Parameters.AddWithValue("@seq", (object?)message.Sequence ?? DBNull.Value);
        command.Parameters.AddWithValue("@direction", message.Direction);
        command.Parameters.AddWithValue("@type", message.MessageType);
        command.Parameters.AddWithValue("@media", (object?)message.MediaType ?? DBNull.Value);
        command.Parameters.AddWithValue("@content", message.Content);
        command.Parameters.AddWithValue("@search", message.SearchText);
        command.Parameters.AddWithValue("@senderName", message.SenderName);
        command.Parameters.AddWithValue("@convTitle", conversation.Title);
        command.Parameters.AddWithValue("@recalled", message.IsRecalled ? 1L : 0L);
        command.Parameters.AddWithValue("@system", message.IsSystem ? 1L : 0L);
        command.Parameters.AddWithValue("@reply", (object?)message.ReplyToNativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", message.PayloadHash);
        command.Parameters.AddWithValue("@semantic", message.SemanticHash);
        command.Parameters.AddWithValue("@revision", revisionOf.HasValue ? revisionOf.Value : DBNull.Value);
        command.Parameters.AddWithValue("@raw", CanonicalJson.Serialize(message.RawPayload));
    }

    private static void RefreshDuplicateMessage(
        SqliteConnection connection, long messageId, long senderId, ParsedMessage message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE messages SET sender_id=@sender, direction=@direction, message_type=@type,
                media_type=@media, content=@content, search_text=@search,
                sender_name_snapshot=@senderName, is_recalled=@recalled, is_system=@system,
                reply_to_native_id=@reply, payload_hash=@payload, semantic_hash=@semantic,
                raw_payload_json=@raw
            WHERE id=@id
            """;
        command.Parameters.AddWithValue("@sender", senderId);
        command.Parameters.AddWithValue("@direction", message.Direction);
        command.Parameters.AddWithValue("@type", message.MessageType);
        command.Parameters.AddWithValue("@media", (object?)message.MediaType ?? DBNull.Value);
        command.Parameters.AddWithValue("@content", message.Content);
        command.Parameters.AddWithValue("@search", message.SearchText);
        command.Parameters.AddWithValue("@senderName", message.SenderName);
        command.Parameters.AddWithValue("@recalled", message.IsRecalled ? 1L : 0L);
        command.Parameters.AddWithValue("@system", message.IsSystem ? 1L : 0L);
        command.Parameters.AddWithValue("@reply", (object?)message.ReplyToNativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", message.PayloadHash);
        command.Parameters.AddWithValue("@semantic", message.SemanticHash);
        command.Parameters.AddWithValue("@raw", CanonicalJson.Serialize(message.RawPayload));
        command.Parameters.AddWithValue("@id", messageId);
        command.ExecuteNonQuery();
    }

    private static void BumpConversationWindow(SqliteConnection connection, long conversationId, long timestampMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversations SET
                first_message_at = CASE WHEN first_message_at IS NULL THEN @ts ELSE MIN(first_message_at, @ts) END,
                last_message_at = CASE WHEN last_message_at IS NULL THEN @ts ELSE MAX(last_message_at, @ts) END,
                message_count = message_count + 1,
                updated_at = CURRENT_TIMESTAMP
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@ts", timestampMs);
        command.Parameters.AddWithValue("@id", conversationId);
        command.ExecuteNonQuery();
    }

    private static void RecordObservation(
        SqliteConnection connection, long messageId, long importFileId, string locator, string payloadHash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO message_observations(message_id, import_file_id, source_locator, observed_payload_hash)
            VALUES (@message, @file, @locator, @hash)
            """;
        command.Parameters.AddWithValue("@message", messageId);
        command.Parameters.AddWithValue("@file", importFileId);
        command.Parameters.AddWithValue("@locator", locator);
        command.Parameters.AddWithValue("@hash", payloadHash);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<ParsedAttachment> AttachmentsFor(ParsedMessage message)
    {
        if (message.Attachments.Count > 0)
        {
            return message.Attachments;
        }

        var kind = NormalizeMediaKind(message.MediaType) ?? NormalizeMediaKind(message.MessageType);
        if (kind is null)
        {
            return message.Attachments;
        }

        return new[]
        {
            new ParsedAttachment(
                Ordinal: 0,
                Kind: kind,
                Filename: null,
                DeclaredPath: null,
                SourcePath: null,
                DeclaredSize: null,
                MimeType: null,
                Width: null,
                Height: null,
                Duration: null,
                Metadata: new JsonObject()),
        };
    }

    private static string? NormalizeMediaKind(string? value)
    {
        return ImportText.Clean(value).ToLowerInvariant() switch
        {
            "image" => "image",
            "file" => "file",
            "video" => "video",
            "audio" or "voice" => "audio",
            "emoji" or "sticker" => "emoji",
            _ => null,
        };
    }

    private (int Count, int Missing) UpsertAttachments(
        SqliteConnection connection,
        long messageId,
        IReadOnlyList<ParsedAttachment> attachments,
        CancellationToken cancellationToken,
        ISet<CreatedManagedMedia> createdMedia)
    {
        var missing = 0;
        var locator = new MediaLocator(_mediaDir);
        foreach (var item in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExistingAttachment? existing = null;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT a.id, a.is_available, a.media_object_id, a.source_path,
                           mo.sha256, mo.managed_path
                    FROM attachments a
                    LEFT JOIN media_objects mo ON mo.id = a.media_object_id
                    WHERE a.message_id=@m AND a.ordinal=@o
                    """;
                select.Parameters.AddWithValue("@m", messageId);
                select.Parameters.AddWithValue("@o", item.Ordinal);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    existing = new ExistingAttachment(
                        Id: reader.GetInt64(0),
                        IsAvailable: reader.GetInt64(1) != 0,
                        MediaObjectId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        SourcePath: reader.IsDBNull(3) ? null : reader.GetString(3),
                        Sha256: reader.IsDBNull(4) ? null : reader.GetString(4),
                        ManagedPath: reader.IsDBNull(5) ? null : reader.GetString(5));
                }
            }

            var sourceExists = !string.IsNullOrEmpty(item.SourcePath) && File.Exists(item.SourcePath);
            if (sourceExists)
            {
                var stored = StoreMedia(
                    connection,
                    item.SourcePath!,
                    item.MimeType,
                    cancellationToken,
                    createdMedia);
                WriteParsedAttachment(
                    connection,
                    messageId,
                    item,
                    existing?.Id,
                    isAvailable: true,
                    stored.Id,
                    stored.Size);
                continue;
            }

            if (existing is { } previous)
            {
                var safeSha = previous.Sha256 is { Length: >= 2 } ? previous.Sha256 : null;
                var resolved = locator.Resolve(
                    safeSha,
                    previous.ManagedPath,
                    previous.SourcePath ?? item.SourcePath);
                if (resolved is not null)
                {
                    if (!previous.IsAvailable)
                    {
                        using var repair = connection.CreateCommand();
                        repair.CommandText = "UPDATE attachments SET is_available=1 WHERE id=@id";
                        repair.Parameters.AddWithValue("@id", previous.Id);
                        repair.ExecuteNonQuery();
                    }

                    continue;
                }

                missing++;
                using var downgrade = connection.CreateCommand();
                downgrade.CommandText = """
                    UPDATE attachments SET is_available=0,
                        source_path=COALESCE(source_path, @source),
                        declared_path=COALESCE(declared_path, @declared)
                    WHERE id=@id
                    """;
                downgrade.Parameters.AddWithValue("@source", (object?)item.SourcePath ?? DBNull.Value);
                downgrade.Parameters.AddWithValue("@declared", (object?)item.DeclaredPath ?? DBNull.Value);
                downgrade.Parameters.AddWithValue("@id", previous.Id);
                downgrade.ExecuteNonQuery();
                continue;
            }

            missing++;
            WriteParsedAttachment(
                connection,
                messageId,
                item,
                existingId: null,
                isAvailable: false,
                mediaObjectId: null,
                actualSize: item.DeclaredSize ?? 0);
        }

        return (attachments.Count, missing);
    }

    private static void WriteParsedAttachment(
        SqliteConnection connection,
        long messageId,
        ParsedAttachment item,
        long? existingId,
        bool isAvailable,
        long? mediaObjectId,
        long actualSize)
    {
        using var write = connection.CreateCommand();
        if (existingId.HasValue)
        {
            write.CommandText = """
                UPDATE attachments SET kind=@kind, filename=@filename, declared_path=@declared,
                    source_path=@source, is_available=@available, media_object_id=@media,
                    declared_size=@size, mime_type=@mime, width=@width, height=@height,
                    duration=@duration, metadata_json=@metadata
                WHERE id=@id
                """;
            write.Parameters.AddWithValue("@id", existingId.Value);
        }
        else
        {
            write.CommandText = """
                INSERT INTO attachments(message_id, ordinal, kind, filename, declared_path, source_path,
                    is_available, media_object_id, declared_size, mime_type, width, height, duration, metadata_json)
                VALUES (@m, @ordinal, @kind, @filename, @declared, @source,
                    @available, @media, @size, @mime, @width, @height, @duration, @metadata)
                """;
            write.Parameters.AddWithValue("@m", messageId);
            write.Parameters.AddWithValue("@ordinal", item.Ordinal);
        }

        write.Parameters.AddWithValue("@kind", item.Kind);
        write.Parameters.AddWithValue("@filename", (object?)item.Filename ?? DBNull.Value);
        write.Parameters.AddWithValue("@declared", (object?)item.DeclaredPath ?? DBNull.Value);
        write.Parameters.AddWithValue("@source", (object?)item.SourcePath ?? DBNull.Value);
        write.Parameters.AddWithValue("@available", isAvailable ? 1L : 0L);
        write.Parameters.AddWithValue("@media", mediaObjectId.HasValue ? mediaObjectId.Value : DBNull.Value);
        write.Parameters.AddWithValue("@size", actualSize);
        write.Parameters.AddWithValue("@mime", (object?)item.MimeType ?? DBNull.Value);
        write.Parameters.AddWithValue("@width", item.Width.HasValue ? item.Width.Value : DBNull.Value);
        write.Parameters.AddWithValue("@height", item.Height.HasValue ? item.Height.Value : DBNull.Value);
        write.Parameters.AddWithValue("@duration", item.Duration.HasValue ? item.Duration.Value : DBNull.Value);
        write.Parameters.AddWithValue("@metadata", CanonicalJson.Serialize(item.Metadata));
        write.ExecuteNonQuery();
    }

    private readonly record struct ExistingAttachment(
        long Id,
        bool IsAvailable,
        long? MediaObjectId,
        string? SourcePath,
        string? Sha256,
        string? ManagedPath);

    private readonly record struct CreatedManagedMedia(string Path, string Digest);

    private (long Id, long Size) StoreMedia(
        SqliteConnection connection,
        string sourcePath,
        string? mimeType,
        CancellationToken cancellationToken,
        ISet<CreatedManagedMedia> createdMedia)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = sourcePath.ToLowerInvariant();
        string digest;
        long size;
        string? managed;
        if (_mediaCache.TryGetValue(cacheKey, out var cached))
        {
            (digest, size, managed) = cached;
        }
        else
        {
            (digest, size) = FileHashing.HashFile(sourcePath, cancellationToken);
            managed = null;
            if (_copyMedia)
            {
                var suffix = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (suffix.Length > 12 || !(suffix.Length > 1 && suffix[1..].All(char.IsAsciiLetterOrDigit)))
                {
                    suffix = string.Empty;
                }

                var destination = Path.Combine(_mediaDir, digest[..2], $"{digest}{suffix}");
                if (!File.Exists(destination))
                {
                    Directory.CreateDirectory(_mediaDir);
                    var temporary = Path.Combine(
                        _mediaDir,
                        $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        var copied = FileHashing.CopyFileAndHash(
                            sourcePath,
                            temporary,
                            cancellationToken);
                        digest = copied.Digest;
                        size = copied.Size;
                        destination = Path.Combine(_mediaDir, digest[..2], $"{digest}{suffix}");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!File.Exists(destination))
                        {
                            try
                            {
                                File.Move(temporary, destination, overwrite: false);
                                createdMedia.Add(new CreatedManagedMedia(
                                    Path.GetFullPath(destination),
                                    digest));
                            }
                            catch (IOException) when (File.Exists(destination))
                            {
                                // Another writer completed the same content-addressed file first.
                            }
                        }
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                        {
                            try
                            {
                                File.Delete(temporary);
                            }
                            catch (Exception ex) when (
                                ex is IOException or UnauthorizedAccessException)
                            {
                                // Temporary cleanup is best effort and must not replace the import outcome.
                            }
                        }
                    }
                }

                managed = Path.GetFullPath(destination);
            }

            _mediaCache[cacheKey] = (digest, size, managed);
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO media_objects(sha256, size, mime_type, managed_path, first_source_path)
                VALUES (@sha, @size, @mime, @managed, @source)
                ON CONFLICT(sha256) DO UPDATE SET
                    managed_path = COALESCE(media_objects.managed_path, excluded.managed_path),
                    mime_type = COALESCE(media_objects.mime_type, excluded.mime_type)
                """;
            upsert.Parameters.AddWithValue("@sha", digest);
            upsert.Parameters.AddWithValue("@size", size);
            upsert.Parameters.AddWithValue("@mime", (object?)mimeType ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@managed", (object?)managed ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@source", sourcePath);
            upsert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM media_objects WHERE sha256=@sha";
        select.Parameters.AddWithValue("@sha", digest);
        var id = (long)select.ExecuteScalar()!;
        return (id, size);
    }

    private void CleanupUnreferencedMedia(IEnumerable<CreatedManagedMedia> createdMedia)
    {
        try
        {
            using var connection = _db.OpenConnection();
            foreach (var item in createdMedia)
            {
                try
                {
                    using var referenced = connection.CreateCommand();
                    referenced.CommandText = "SELECT EXISTS(SELECT 1 FROM media_objects WHERE sha256=@sha OR managed_path=@path)";
                    referenced.Parameters.AddWithValue("@sha", item.Digest);
                    referenced.Parameters.AddWithValue("@path", item.Path);
                    if (Convert.ToInt64(referenced.ExecuteScalar()) == 0 && File.Exists(item.Path))
                    {
                        File.Delete(item.Path);
                    }
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or SqliteException)
                {
                    // Cleanup is best effort and must not mask the original import failure.
                }
            }
        }
        catch (SqliteException)
        {
            // Preserve the original import failure if cleanup cannot inspect references.
        }
    }

    private static void MarkImportFile(
        SqliteConnection connection, long importFileId, string status, Counters counters, string? error)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE import_files SET status=@status, finished_at=CURRENT_TIMESTAMP,
                stats_json=@stats, error=@error WHERE id=@id
            """;
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@stats", CanonicalJson.Serialize(counters.ToJson(ParserVersion)));
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", importFileId);
        command.ExecuteNonQuery();
    }

    private static ImportRunResult BuildRunResult(int found, List<FileImportResult> files, Counters totals)
    {
        return new ImportRunResult(
            files,
            found,
            files.Count(f => f.Status == "completed"),
            files.Count(f => f.Status == "skipped"),
            files.Count(f => f.Status == "failed"),
            totals.MessagesSeen, totals.Added, totals.Duplicates, totals.Revised,
            totals.Variants, totals.Attachments, totals.MissingMedia);
    }

    private static FileImportResult MakeResult(
        string path, string platform, string status, Counters counters, string? error)
    {
        return new FileImportResult(path, platform, status,
            counters.MessagesSeen, counters.Added, counters.Duplicates,
            counters.Revised, counters.Variants, counters.Attachments, counters.MissingMedia, error);
    }

    private static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    internal sealed class Counters
    {
        public long MessagesSeen { get; internal set; }
        public long Added { get; internal set; }
        public long Duplicates { get; internal set; }
        public long Revised { get; internal set; }
        public long Variants { get; internal set; }
        public long Attachments { get; internal set; }
        public long MissingMedia { get; internal set; }

        internal void Add(FileImportResult result)
        {
            MessagesSeen += result.MessagesSeen;
            Added += result.Added;
            Duplicates += result.Duplicates;
            Revised += result.Revised;
            Variants += result.Variants;
            Attachments += result.Attachments;
            MissingMedia += result.MissingMedia;
        }

        internal void Reset()
        {
            MessagesSeen = Added = Duplicates = Revised = Variants = Attachments = MissingMedia = 0;
        }

        internal JsonObject ToJson(int parserVersion)
        {
            return new JsonObject
            {
                ["parser_version"] = parserVersion,
                ["messages_seen"] = MessagesSeen,
                ["added"] = Added,
                ["duplicates"] = Duplicates,
                ["revised"] = Revised,
                ["variants"] = Variants,
                ["attachments"] = Attachments,
                ["missing_media"] = MissingMedia,
            };
        }
    }
}
