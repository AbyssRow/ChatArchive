using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Repositories;

public sealed class StatsRepository
{
    private readonly ArchiveDatabase _db;

    public StatsRepository(ArchiveDatabase db)
    {
        _db = db;
    }

    public ArchiveStats GetStats()
    {
        using var connection = _db.OpenConnection();

        long messages;
        long conversations;
        long senders;
        long attachments;
        long missingMedia;
        long mediaObjects;
        long mediaBytes;
        long importedFiles;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT (SELECT COUNT(*) FROM messages),
                       (SELECT COUNT(*) FROM conversations),
                       (SELECT COUNT(*) FROM senders),
                       (SELECT COUNT(*) FROM attachments),
                       (SELECT COUNT(*) FROM attachments WHERE is_available = 0),
                       (SELECT COUNT(*) FROM media_objects),
                       (SELECT COALESCE(SUM(size), 0) FROM media_objects),
                       (SELECT COUNT(*) FROM import_files WHERE status = 'completed')
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            messages = reader.GetInt64(0);
            conversations = reader.GetInt64(1);
            senders = reader.GetInt64(2);
            attachments = reader.GetInt64(3);
            missingMedia = reader.GetInt64(4);
            mediaObjects = reader.GetInt64(5);
            mediaBytes = reader.GetInt64(6);
            importedFiles = reader.GetInt64(7);
        }

        long qqMessages = 0;
        long weChatMessages = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT platform, COUNT(*) FROM messages GROUP BY platform";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var platform = reader.GetString(0);
                if (platform == "qq")
                {
                    qqMessages = reader.GetInt64(1);
                }
                else if (platform == "wechat")
                {
                    weChatMessages = reader.GetInt64(1);
                }
            }
        }

        long privateCount = 0;
        long groupCount = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, COUNT(*) FROM conversations GROUP BY kind";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.GetString(0);
                if (kind == "private")
                {
                    privateCount = reader.GetInt64(1);
                }
                else if (kind == "group")
                {
                    groupCount = reader.GetInt64(1);
                }
            }
        }

        long? firstMessageAt = null;
        long? lastMessageAt = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MIN(timestamp_ms), MAX(timestamp_ms) FROM messages";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                firstMessageAt = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                lastMessageAt = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        return new ArchiveStats(
            messages,
            qqMessages,
            weChatMessages,
            conversations,
            privateCount,
            groupCount,
            senders,
            attachments,
            attachments - missingMedia,
            missingMedia,
            mediaObjects,
            mediaBytes,
            firstMessageAt,
            lastMessageAt);
    }
}
