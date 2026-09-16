using System.Text.Json;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Repositories;

public sealed class SenderRepository
{
    private readonly ArchiveDatabase _db;

    public SenderRepository(ArchiveDatabase db)
    {
        _db = db;
    }

    public SenderProfile? GetSender(long senderId)
    {
        using var connection = _db.OpenConnection();

        string platform;
        string nativeId;
        string currentName;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT platform, native_id, current_name FROM senders WHERE id = @id";
            command.Parameters.AddWithValue("@id", senderId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            platform = reader.GetString(0);
            nativeId = reader.GetString(1);
            currentName = reader.GetString(2);
        }

        var aliases = LoadAliases(connection, senderId);
        var conversations = LoadConversations(connection, senderId);

        var displayNames = SenderDisplayName.Resolve(
            connection,
            new[] { (SenderId: senderId, (long?)null) });
        var profileDisplayName = displayNames.TryGetValue((senderId, null), out var dn)
            ? dn
            : currentName;

        var qqNumber = platform == "qq" ? FindQqNumber(connection, senderId, aliases) : null;

        return new SenderProfile(
            senderId,
            platform,
            nativeId,
            qqNumber,
            profileDisplayName,
            aliases,
            conversations);
    }

    private static IReadOnlyList<AliasInfo> LoadAliases(SqliteConnection connection, long senderId)
    {
        var result = new List<AliasInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alias, MAX(last_seen_at)
            FROM sender_aliases WHERE sender_id = @id
            GROUP BY alias ORDER BY MAX(last_seen_at) DESC, alias
            """;
        command.Parameters.AddWithValue("@id", senderId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AliasInfo(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }

        return result;
    }

    private static List<SenderConversationInfo> LoadConversations(
        SqliteConnection connection,
        long senderId)
    {
        var result = new List<SenderConversationInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.title, COUNT(*)
            FROM messages m JOIN conversations c ON c.id = m.conversation_id
            WHERE m.sender_id = @id
            GROUP BY c.id ORDER BY MAX(m.timestamp_ms) DESC, c.id DESC
            """;
        command.Parameters.AddWithValue("@id", senderId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SenderConversationInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }

        return result;
    }

    /// <summary>QQ 号：优先取最近消息 payload 中 sender.uin；否则用 5-12 位纯数字别名启发式。</summary>
    internal static string? FindQqNumber(SqliteConnection connection, long senderId, IReadOnlyList<AliasInfo> aliases)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT raw_payload_json FROM messages WHERE sender_id = @id
                ORDER BY timestamp_ms DESC, id DESC LIMIT 25
                """;
            command.Parameters.AddWithValue("@id", senderId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(0));
                    if (document.RootElement.TryGetProperty("sender", out var senderElement)
                        && senderElement.ValueKind == JsonValueKind.Object
                        && senderElement.TryGetProperty("uin", out var uin))
                    {
                        var candidate = uin.ToString().Trim();
                        if (candidate.Length > 0 && candidate.All(char.IsDigit))
                        {
                            return candidate;
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        var numericAliases = aliases
            .Where(a => a.Alias.All(char.IsDigit) && a.Alias.Length is >= 5 and <= 12)
            .ToList();

        return numericAliases.Count > 0
            ? numericAliases.MaxBy(a => a.LastSeenAt ?? 0)!.Alias
            : null;
    }
}
