using System.Text.Json.Nodes;
using ChatArchive.Core.Data;
using ChatArchive.Core.IO;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Importing;

/// <summary>
/// 导入服务：文件发现、事务、三层去重（文件哈希/原生ID/载荷哈希）、
/// 版本保留与媒体复制。规则从旧版 service.py 移植，解析器版本 4。
/// </summary>
public sealed class ImportService
{
    public const int ParserVersion = 4;

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
        using var processLock = AcquireCrossProcessLock(Path.Combine(dataDir, ".import.lock"));
        RecoverStaleRuns();

        var files = ImportDiscovery.Discover(roots, _formats, new[] { dataDir, AppContext.BaseDirectory });
        var totals = new Counters();
        var fileResults = new List<FileImportResult>();
        var runId = CreateRun(roots);

        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var discovered = files[index];
                var result = ImportFile(discovered.FilePath, discovered.Platform, runId);
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

    private static FileStream AcquireCrossProcessLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
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

    internal FileImportResult ImportFile(string filePath, string platform, long runId)
    {
        var counters = new Counters();
        var digest = FileHashing.Sha256File(filePath);
        var fileInfo = new FileInfo(filePath);
        long importFileId;

        using (var connection = _db.OpenConnection())
        {
            using var dupCommand = connection.CreateCommand();
            dupCommand.CommandText = """
                SELECT f.id,
                       EXISTS(SELECT 1 FROM message_observations mo
                              JOIN attachments a ON a.message_id = mo.message_id
                              WHERE mo.import_file_id = f.id AND a.is_available = 0) AS has_missing_media
                FROM import_files f
                WHERE f.sha256 = @sha AND f.status = 'completed'
                """;
            dupCommand.Parameters.AddWithValue("@sha", digest);
            using var reader = dupCommand.ExecuteReader();
            if (reader.Read())
            {
                var fileId = reader.GetInt64(0);
                var hasMissingMedia = reader.GetInt64(1) != 0;
                var previous = ReadFileStats(connection, fileId);
                var needsReimport =
                    previous.ParserVersion < ParserVersion
                    || previous.MissingMedia > 0
                    || hasMissingMedia;
                if (!needsReimport)
                {
                    return MakeResult(filePath, platform, "skipped", counters, null);
                }

                TouchImportFileRow(connection, fileId, runId, platform, filePath, fileInfo);
                importFileId = fileId;
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

        var format = _formats.First(f => f.Platform == platform);
        using var exportFile = format.Open(filePath);
        using var transactionConnection = _db.OpenConnection();
        using var transaction = transactionConnection.BeginTransaction();
        try
        {
            long? conversationId = null;
            foreach (var message in exportFile.EnumerateMessages())
            {
                conversationId ??= UpsertConversation(transactionConnection, exportFile.Conversation);
                counters.MessagesSeen++;
                var (messageId, state) = UpsertMessage(transactionConnection, conversationId.Value, exportFile.Conversation, message);
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

                RecordObservation(transactionConnection, messageId, importFileId, message.SourceLocator, message.PayloadHash);
                var (attachmentCount, missing) = UpsertAttachments(transactionConnection, messageId, message.Attachments);
                counters.Attachments += attachmentCount;
                counters.MissingMedia += missing;
            }

            MarkImportFile(transactionConnection, importFileId, "completed", counters, error: null);
            transaction.Commit();
            return MakeResult(filePath, platform, "completed", counters, null);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            counters.Reset();
            using var failureConnection = _db.OpenConnection();
            MarkImportFile(failureConnection, importFileId, "failed", counters, ex.Message);
            return MakeResult(filePath, platform, "failed", counters, ex.Message);
        }
    }

    private static (long ParserVersion, long MissingMedia) ReadFileStats(SqliteConnection connection, long fileId)
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

        return (parserVersion, missingMedia);
    }

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
                SELECT id FROM conversations
                WHERE platform=@platform AND account_id=@account AND native_id=@native
                """;
            select.Parameters.AddWithValue("@platform", conversation.Platform);
            select.Parameters.AddWithValue("@account", conversation.AccountId);
            select.Parameters.AddWithValue("@native", conversation.NativeId);
            var existing = select.ExecuteScalar();
            if (existing is not null and long id)
            {
                conversationId = id;
                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE conversations SET title=@title, kind=@kind, updated_at=CURRENT_TIMESTAMP
                    WHERE id=@id AND title<>@title
                    """;
                update.Parameters.AddWithValue("@title", conversation.Title);
                update.Parameters.AddWithValue("@kind", conversation.Kind);
                update.Parameters.AddWithValue("@id", conversationId);
                update.ExecuteNonQuery();
            }
            else
            {
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
        using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO senders(platform, account_id, native_id, current_name, is_self)
                VALUES (@platform, @account, @native, @name, @self)
                ON CONFLICT(platform, account_id, native_id) DO UPDATE SET
                    current_name = CASE
                        WHEN excluded.platform = 'wechat'
                             AND excluded.current_name = excluded.native_id
                             AND senders.current_name <> senders.native_id THEN senders.current_name
                        WHEN excluded.current_name <> '' THEN excluded.current_name
                        ELSE senders.current_name END,
                    is_self = MAX(senders.is_self, excluded.is_self),
                    updated_at = CURRENT_TIMESTAMP
                """;
            upsert.Parameters.AddWithValue("@platform", conversation.Platform);
            upsert.Parameters.AddWithValue("@account", conversation.AccountId);
            upsert.Parameters.AddWithValue("@native", message.SenderNativeId);
            upsert.Parameters.AddWithValue("@name", message.SenderName);
            upsert.Parameters.AddWithValue("@self", isSelf);
            upsert.ExecuteNonQuery();
        }

        long senderId;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id FROM senders
                WHERE platform=@platform AND account_id=@account AND native_id=@native
                """;
            select.Parameters.AddWithValue("@platform", conversation.Platform);
            select.Parameters.AddWithValue("@account", conversation.AccountId);
            select.Parameters.AddWithValue("@native", message.SenderNativeId);
            senderId = (long)select.ExecuteScalar()!;
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

    private (int Count, int Missing) UpsertAttachments(
        SqliteConnection connection, long messageId, IReadOnlyList<ParsedAttachment> attachments)
    {
        var missing = 0;
        foreach (var item in attachments)
        {
            var available = !string.IsNullOrEmpty(item.SourcePath) && File.Exists(item.SourcePath!);
            long? existingId = null;
            var existingAvailable = false;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT id, is_available FROM attachments WHERE message_id=@m AND ordinal=@o
                    """;
                select.Parameters.AddWithValue("@m", messageId);
                select.Parameters.AddWithValue("@o", item.Ordinal);
                using var reader = select.ExecuteReader();
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    existingAvailable = reader.GetInt64(1) != 0;
                }
            }

            long? mediaObjectId = null;
            long actualSize = item.DeclaredSize ?? 0;
            if (available && item.SourcePath is not null)
            {
                var stored = StoreMedia(connection, item.SourcePath, item.MimeType);
                mediaObjectId = stored.Id;
                actualSize = stored.Size;
            }
            else if (existingId is null || !existingAvailable)
            {
                missing++;
            }

            using (var write = connection.CreateCommand())
            {
                if (existingId.HasValue)
                {
                    if (available || !existingAvailable)
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
                        continue;
                    }
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
                write.Parameters.AddWithValue("@available", available ? 1L : 0L);
                write.Parameters.AddWithValue("@media", mediaObjectId.HasValue ? mediaObjectId.Value : DBNull.Value);
                write.Parameters.AddWithValue("@size", actualSize);
                write.Parameters.AddWithValue("@mime", (object?)item.MimeType ?? DBNull.Value);
                write.Parameters.AddWithValue("@width", item.Width.HasValue ? item.Width.Value : DBNull.Value);
                write.Parameters.AddWithValue("@height", item.Height.HasValue ? item.Height.Value : DBNull.Value);
                write.Parameters.AddWithValue("@duration", item.Duration.HasValue ? item.Duration.Value : DBNull.Value);
                write.Parameters.AddWithValue("@metadata", CanonicalJson.Serialize(item.Metadata));
                write.ExecuteNonQuery();
            }
        }

        return (attachments.Count, missing);
    }

    private (long Id, long Size) StoreMedia(SqliteConnection connection, string sourcePath, string? mimeType)
    {
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
            digest = FileHashing.Sha256File(sourcePath);
            size = new FileInfo(sourcePath).Length;
            managed = null;
            if (_copyMedia)
            {
                var suffix = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (suffix.Length > 12 || !(suffix.Length > 1 && suffix[1..].All(char.IsAsciiLetterOrDigit)))
                {
                    suffix = string.Empty;
                }

                var destination = Path.Combine(_mediaDir, digest[..2], $"{digest}{suffix}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination))
                {
                    var temporary = destination + $".{Environment.ProcessId}.tmp";
                    File.Copy(sourcePath, temporary, overwrite: true);
                    File.Move(temporary, destination, overwrite: true);
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
