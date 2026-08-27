using ChatArchive.Core.Data;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Migration;

public sealed record MigrationReport(
    string SourceDb,
    string TargetDb,
    long MediaFilesCopied,
    long MediaFilesSkipped,
    long ManagedPathsRewritten,
    long Conversations,
    long Messages,
    long Attachments,
    long MediaObjects,
    bool Verified);

/// <summary>
/// 一次性数据迁移：把旧档案库与媒体库复制到新位置，改写 managed_path
/// 前缀，校验行数一致并生成结构说明 README.md。源目录全程只读。
/// </summary>
public sealed class MigrationRunner
{
    private readonly string _sourceDir;
    private readonly string _targetDir;

    public MigrationRunner(string sourceDir, string targetDir)
    {
        _sourceDir = Path.GetFullPath(sourceDir);
        _targetDir = Path.GetFullPath(targetDir);
    }

    public MigrationReport Run(Action<string>? log = null)
    {
        void Say(string message) => log?.Invoke(message);

        var sourceDb = Path.Combine(_sourceDir, "chat_archive.db");
        if (!File.Exists(sourceDb))
        {
            throw new FileNotFoundException($"源目录缺少 chat_archive.db: {_sourceDir}");
        }

        Directory.CreateDirectory(_targetDir);
        var targetDb = Path.Combine(_targetDir, "chat_archive.db");
        if (File.Exists(targetDb))
        {
            var backup = targetDb + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            using (var targetConn = new SqliteConnection(
                       new SqliteConnectionStringBuilder { DataSource = targetDb, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            using (var backupConn = new SqliteConnection(
                       new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                targetConn.Open();
                backupConn.Open();
                targetConn.BackupDatabase(backupConn);
            }
            Say($"已备份现有目标库 → {backup}");
        }

        Say("复制数据库（SQLite Backup API，自动包含 WAL 内容，源库只读）…");
        using (var source = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = sourceDb, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var target = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = targetDb, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }

        Say("复制媒体文件（按内容寻址增量复制）…");
        var (copied, skipped) = CopyMedia(Path.Combine(_sourceDir, "media"), Path.Combine(_targetDir, "media"));
        Say($"媒体：新增 {copied}，已有跳过 {skipped}");

        Say("改写 managed_path 前缀 …");
        var rewritten = RewriteManagedPaths(targetDb, Path.Combine(_targetDir, "media"));

        Say("校验行数一致性 …");
        var counts = VerifyCounts(sourceDb, targetDb);

        WriteReadme();

        return new MigrationReport(sourceDb, targetDb, copied, skipped, rewritten,
            counts.Conversations, counts.Messages, counts.Attachments, counts.MediaObjects,
            true);
    }

    private (long Copied, long Skipped) CopyMedia(string sourceMedia, string targetMedia)
    {
        long copied = 0;
        long skipped = 0;
        if (!Directory.Exists(sourceMedia))
        {
            return (0, 0);
        }

        foreach (var file in Directory.EnumerateFiles(sourceMedia, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceMedia, file);
            var destination = Path.Combine(targetMedia, relative);
            if (File.Exists(destination))
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            copied++;
        }

        return (copied, skipped);
    }

    /// <summary>把 managed_path 统一重写为目标媒体库的内容寻址路径；first_source_path 保持原值。</summary>
    private static long RewriteManagedPaths(string targetDb, string targetMediaDir)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = targetDb, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();

        var updates = new List<(long Id, string NewPath)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, sha256, managed_path FROM media_objects WHERE managed_path IS NOT NULL";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var sha = reader.GetString(1);
                var oldPath = reader.GetString(2);
                var suffix = Path.GetExtension(oldPath);
                if (suffix.Length > 12 || !(suffix.Length > 1 && suffix[1..].All(char.IsAsciiLetterOrDigit)))
                {
                    suffix = string.Empty;
                }

                updates.Add((reader.GetInt64(0), Path.Combine(targetMediaDir, sha[..2], sha + suffix)));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (id, newPath) in updates)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE media_objects SET managed_path=@p WHERE id=@id";
            update.Parameters.AddWithValue("@p", newPath);
            update.Parameters.AddWithValue("@id", id);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return updates.Count;
    }

    private static (long Conversations, long Messages, long Attachments, long MediaObjects) VerifyCounts(string sourceDb, string targetDb)
    {
        static Dictionary<string, long> CountAll(string db)
        {
            var result = new Dictionary<string, long>();
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            foreach (var table in new[] { "conversations", "messages", "attachments", "media_objects" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table}";
                result[table] = (long)command.ExecuteScalar()!;
            }

            return result;
        }

        var sourceCounts = CountAll(sourceDb);
        var targetCounts = CountAll(targetDb);
        foreach (var key in sourceCounts.Keys)
        {
            if (sourceCounts[key] != targetCounts[key])
            {
                throw new InvalidOperationException($"校验失败：{key} 行数不一致 源={sourceCounts[key]} 目标={targetCounts[key]}");
            }
        }

        return (sourceCounts["conversations"], sourceCounts["messages"],
            sourceCounts["attachments"], sourceCounts["media_objects"]);
    }

    private void WriteReadme()
    {
        var path = Path.Combine(_targetDir, "README.md");
        File.WriteAllText(path, $"""
            # ChatArchive 数据目录

            本目录由 ChatArchive.Migrate 于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 迁移生成，是 WinUI 版聊天档案应用的全部数据。

            ## 结构

            | 路径 | 说明 |
            |---|---|
            | `chat_archive.db` | SQLite 主库：消息、会话、联系人、别名、附件元数据、FTS5 中文全文索引 |
            | `chat_archive.db-wal` / `-shm` | SQLite WAL 临时文件，应用运行时出现，属正常现象 |
            | `media\\<sha前两位>\\<sha256><后缀>` | 内容寻址媒体库；同一文件全库只存一份 |

            ## 备份

            日常只需整目录复制本文件夹（应用关闭后复制即可）。恢复时放回原位置。

            ## 注意

            - 原始导出目录（QQexports / 微信json 等）不会被本应用修改；确认本目录备份完好前建议继续保留原始导出。
            - `media_objects.managed_path` 已统一指向本目录的 media 子目录；移动本目录位置后无需手工修库，应用会按 sha256 自动重新定位。
            """);
    }
}
