using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Repositories;

/// <summary>微信发送者在不同会话中的展示名规则，从旧版 Python 实现移植。</summary>
internal static class SenderDisplayName
{
    private sealed record Candidate(long ConversationId, string Alias, long LastSeenAt, string? Kind);

    public static IReadOnlyDictionary<(long SenderId, long? ConversationId), string> Resolve(
        SqliteConnection connection,
        IEnumerable<(long SenderId, long? ConversationId)> keys)
    {
        var result = new Dictionary<(long SenderId, long? ConversationId), string>();
        var senderIds = keys.Select(k => k.SenderId).Distinct().OrderBy(x => x).ToList();
        if (senderIds.Count == 0)
        {
            return result;
        }

        var candidates = new Dictionary<long, List<Candidate>>();
        var placeholders = string.Join(",", senderIds.Select((_, i) => $"@p{i}"));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                SELECT sa.sender_id, sa.conversation_id, sa.alias, sa.last_seen_at,
                       c.kind, s.native_id, s.platform
                FROM sender_aliases sa
                JOIN senders s ON s.id = sa.sender_id
                LEFT JOIN conversations c ON c.id = sa.conversation_id
                WHERE sa.sender_id IN ({{placeholders}})
                """;
            for (var i = 0; i < senderIds.Count; i++)
            {
                command.Parameters.AddWithValue($"p{i}", senderIds[i]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var senderId = reader.GetInt64(0);
                var alias = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                var kind = reader.IsDBNull(4) ? null : reader.GetString(4);
                var nativeId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5).Trim();
                var platform = reader.GetString(6);
                if (!string.Equals(platform, "wechat", StringComparison.Ordinal) || alias.Length == 0)
                {
                    continue;
                }

                if (alias == nativeId || alias == "unknown")
                {
                    continue;
                }

                if (alias.StartsWith("wxid_", StringComparison.Ordinal) || alias.EndsWith("@chatroom", StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = new Candidate(
                    reader.IsDBNull(1) ? -1 : reader.GetInt64(1),
                    alias,
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    kind);
                if (!candidates.TryGetValue(senderId, out var list))
                {
                    list = new List<Candidate>();
                    candidates[senderId] = list;
                }

                list.Add(candidate);
            }
        }

        foreach (var key in keys)
        {
            if (!candidates.TryGetValue(key.SenderId, out var available) || available.Count == 0)
            {
                continue;
            }

            var best = available
                .Select(row => (
                    Scope: key.ConversationId is { } target && row.ConversationId == target
                        ? 0
                        : row.Kind == "private" ? 1 : 2,
                    NegSeen: -row.LastSeenAt,
                    Alias: row.Alias))
                .OrderBy(x => x.Scope)
                .ThenBy(x => x.NegSeen)
                .ThenBy(x => x.Alias, StringComparer.Ordinal)
                .First();
            result[key] = best.Alias;
        }

        return result;
    }
}
