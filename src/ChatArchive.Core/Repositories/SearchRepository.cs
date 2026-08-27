using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Microsoft.Data.Sqlite;

namespace ChatArchive.Core.Repositories;

public sealed class SearchRepository
{
    private readonly ArchiveDatabase _db;

    public SearchRepository(ArchiveDatabase db)
    {
        _db = db;
    }

    public SearchHitPage Search(string query, SearchFilter? filter = null, string? cursor = null, int limit = 60)
    {
        query = query.Trim();
        filter ??= new SearchFilter();
        if (query.Length == 0)
        {
            return new SearchHitPage(Array.Empty<SearchHit>(), null, SearchMode.Empty);
        }

        long? cursorTs = null;
        long? cursorId = null;
        if (!string.IsNullOrEmpty(cursor) && CursorCodec.TryDecode(cursor, out var ts, out var id))
        {
            cursorTs = ts;
            cursorId = id;
        }

        var useFts = SupportsTrigram(query);
        var mode = useFts ? SearchMode.Fts : SearchMode.Substring;
        var pageSize = Math.Clamp(limit, 1, 200);

        var where = new List<string>();
        using var connection = _db.OpenConnection();
        using var command = connection.CreateCommand();

        const string plainFrom = "messages m JOIN conversations c ON c.id = m.conversation_id";
        const string ftsFrom = "messages_fts JOIN messages m ON m.id = messages_fts.rowid "
            + "JOIN conversations c ON c.id = m.conversation_id";
        var matchClause = useFts ? "messages_fts MATCH @match" : "m.search_text LIKE @pattern ESCAPE '/'";
        command.CommandText = $$"""
            SELECT m.id, m.conversation_id, c.title, c.platform, c.kind,
                   m.sender_id, m.sender_name_snapshot, m.timestamp_ms,
                   m.content, m.search_text, m.message_type, m.direction
            FROM {{(useFts ? ftsFrom : plainFrom)}}
            WHERE {{matchClause}}
            """ + BuildFilterSql(filter);
        if (cursorTs.HasValue)
        {
            command.CommandText += """
                 AND (m.timestamp_ms < @cts OR (m.timestamp_ms = @cts AND m.id < @cid))
                """;
        }

        command.CommandText += " ORDER BY m.timestamp_ms DESC, m.id DESC LIMIT @limit";

        if (useFts)
        {
            var escaped = query.Replace("\\", "\\\\").Replace("\"", "\"\"");
            command.Parameters.AddWithValue("@match", $"search_text : \"{escaped}\"");
        }
        else
        {
            command.Parameters.AddWithValue("@pattern", $"%{SqliteLikeHelper.EscapeLikePattern(query)}%");
        }

        BindFilter(command, filter);
        command.Parameters.AddWithValue("@cts", cursorTs.HasValue ? cursorTs.Value : DBNull.Value);
        command.Parameters.AddWithValue("@cid", cursorId.HasValue ? cursorId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@limit", pageSize + 1);

        var hits = new List<SearchHit>();
        List<(string Content, string SearchText)> snippetSources = new();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var content = reader.GetString(8);
                var searchText = reader.GetString(9);
                snippetSources.Add((content, searchText));
                hits.Add(new SearchHit(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetString(6),
                    string.Empty,
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetInt64(7)));
            }
        }

        var hasMore = hits.Count > pageSize;
        if (hasMore)
        {
            hits.RemoveAt(hits.Count - 1);
            snippetSources.RemoveAt(snippetSources.Count - 1);
        }

        for (var i = 0; i < hits.Count; i++)
        {
            hits[i] = hits[i] with { Snippet = MakeSnippet(snippetSources[i].Content, snippetSources[i].SearchText, query) };
        }

        string? nextCursor = null;
        if (hasMore && hits.Count > 0)
        {
            nextCursor = CursorCodec.Encode(hits[^1].TimestampMs, hits[^1].MessageId);
        }

        return new SearchHitPage(hits, nextCursor, mode);
    }

    public FilterOptions GetFilterOptions(long? conversationId = null)
    {
        using var connection = _db.OpenConnection();
        var types = ReadOptionRows(connection, "message_type", conversationId, limit: null);
        var senders = ReadOptionRows(connection, "sender_name_snapshot", conversationId, limit: 500);
        return new FilterOptions(types, senders);
    }

    private static IReadOnlyList<FilterOptionItem> ReadOptionRows(
        SqliteConnection connection, string column, long? conversationId, int? limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {column} AS value, COUNT(*) AS amount FROM messages
            """ + (conversationId.HasValue ? " WHERE conversation_id = @id" : "")
            + " GROUP BY value ORDER BY amount DESC, value"
            + (limit.HasValue ? " LIMIT @limit" : "");
        if (conversationId.HasValue)
        {
            command.Parameters.AddWithValue("@id", conversationId.Value);
        }

        if (limit.HasValue)
        {
            command.Parameters.AddWithValue("@limit", limit.Value);
        }

        var result = new List<FilterOptionItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FilterOptionItem(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    private static string BuildFilterSql(SearchFilter filter)
    {
        var sql = string.Empty;
        if (!string.IsNullOrEmpty(filter.Platform))
        {
            sql += " AND m.platform = @platform";
        }

        if (!string.IsNullOrEmpty(filter.Kind))
        {
            sql += " AND c.kind = @kind";
        }

        if (filter.ConversationId is not null)
        {
            sql += " AND m.conversation_id = @conversation";
        }

        if (!string.IsNullOrEmpty(filter.Sender))
        {
            sql += " AND (m.sender_name_snapshot LIKE @sender ESCAPE '/' OR EXISTS("
                + "SELECT 1 FROM sender_aliases sa WHERE sa.sender_id = m.sender_id AND sa.alias LIKE @sender ESCAPE '/'))";
        }

        if (!string.IsNullOrEmpty(filter.MessageType))
        {
            sql += " AND m.message_type = @type";
        }

        if (filter.DateFromMs is not null)
        {
            sql += " AND m.timestamp_ms >= @dateFrom";
        }

        if (filter.DateToExclusiveMs is not null)
        {
            sql += " AND m.timestamp_ms < @dateTo";
        }

        return sql;
    }

    private static void BindFilter(SqliteCommand command, SearchFilter filter)
    {
        void Add(string name, object? value)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        Add("@platform", filter.Platform);
        Add("@kind", filter.Kind);
        Add("@conversation", filter.ConversationId);
        Add("@sender", string.IsNullOrEmpty(filter.Sender) ? null : $"%{SqliteLikeHelper.EscapeLikePattern(filter.Sender)}%");
        Add("@type", filter.MessageType);
        Add("@dateFrom", filter.DateFromMs);
        Add("@dateTo", filter.DateToExclusiveMs);
    }

    /// <summary>
    /// trigram 分词器只能命中连续三个及以上字母数字的串；若包含短词（如 "hi alice", "好的 谢谢"）则必须全部 token 长度 >= 3，否则退回 LIKE。
    /// </summary>
    internal static bool SupportsTrigram(string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens.All(t => t.Length >= 3);
    }

    internal static string MakeSnippet(string content, string searchText, string query)
    {
        string source;
        int index;

        if (!string.IsNullOrEmpty(content) && (index = content.IndexOf(query, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            source = content;
        }
        else if (!string.IsNullOrEmpty(searchText) && (index = searchText.IndexOf(query, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            source = searchText;
        }
        else
        {
            source = !string.IsNullOrEmpty(content) ? content : searchText;
            index = 0;
        }

        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        const int window = 40;
        var start = Math.Max(0, index - window / 2);
        var length = Math.Min(source.Length - start, window * 2);
        var snippet = source.Substring(start, length).Replace("\n", " ").Trim();
        return start > 0 ? "…" + snippet : snippet;
    }
}
