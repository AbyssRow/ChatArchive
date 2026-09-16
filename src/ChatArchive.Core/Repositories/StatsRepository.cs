using ChatArchive.Core.Data;
using ChatArchive.Core.Models;

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
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM messages),
                (SELECT COUNT(*) FROM messages WHERE platform = 'qq'),
                (SELECT COUNT(*) FROM messages WHERE platform = 'wechat'),
                (SELECT COUNT(*) FROM conversations),
                (SELECT COUNT(*) FROM conversations WHERE kind = 'private'),
                (SELECT COUNT(*) FROM conversations WHERE kind = 'group'),
                (SELECT COUNT(*) FROM senders),
                (SELECT COUNT(*) FROM attachments),
                (SELECT COUNT(*) FROM attachments WHERE is_available = 0),
                (SELECT COUNT(*) FROM media_objects),
                (SELECT COALESCE(SUM(size), 0) FROM media_objects),
                (SELECT MIN(timestamp_ms) FROM messages),
                (SELECT MAX(timestamp_ms) FROM messages)
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        var attachments = reader.GetInt64(7);
        var missingMedia = reader.GetInt64(8);
        return new ArchiveStats(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            attachments,
            attachments - missingMedia,
            missingMedia,
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12));
    }
}
