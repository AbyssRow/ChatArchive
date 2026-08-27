using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

/// <summary>
/// SQL 脚本转储解析器（平台：sql）。
/// 支持流式解析 INSERT INTO 语句，提取聊天消息。
/// </summary>
public static class SqlScriptParser
{
    private static readonly Regex InsertHeaderRegex = new(
        @"INSERT\s+(?:(?:OR\s+REPLACE|OR\s+IGNORE|IGNORE|LOW_PRIORITY|DELAYED)\s+)?INTO\s+(?:(?:[`""'\[]?\w+[`""'\]]?\.)?)(?:[`""'\[]?)(?<table>[\w\-]+)(?:[`""'\]]?)\s*(?:\((?<columns>[^)]+)\))?\s*VALUES",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreateTablePrefixRegex = new(
        @"^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:[`""'\[]?\w+[`""'\]]?\.)?)(?:[`""'\[]?)(?<table>[\w\-]+)(?:[`""'\]]?)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> KnownNonChatTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "contacts", "contact", "rcontact", "users", "user", "friends", "friend",
        "groups", "group_info", "chatroom", "members", "member",
        "sqlite_sequence", "sqlite_master", "sqlite_stat1", "sqlite_temp_master",
        "settings", "config", "sessions", "session", "app_metadata",
        "schema_migrations", "senders", "conversations", "schema_version",
        "media", "attachments", "emojis", "emoji_info", "fav_info"
    };

    private static readonly string[] ChatColumnKeywords =
    {
        "content", "text", "message", "body", "msg_content",
        "talker", "chat_id", "chatid", "peer_uid", "session_id",
        "create_time", "createtime", "timestamp", "msg_time",
        "is_send", "issend", "is_sender", "is_self",
        "msg_id", "msgid", "svrid", "sender", "sender_name"
    };

    private static readonly string[] ChatTableKeywords =
    {
        "message", "messages", "msg", "chat", "weixin_msg", "talker", "chat_history", "chat_record", "chat_data"
    };

    public static bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".sql", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var lineCount = 0;
            var containsInsert = false;
            var containsChatKeywords = false;
            string? line;

            while ((line = reader.ReadLine()) != null && lineCount < 100)
            {
                lineCount++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase))
                {
                    containsInsert = true;
                }

                if (trimmed.Contains("talker", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("messages", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("create_time", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("is_send", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("content", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("chat", StringComparison.OrdinalIgnoreCase))
                {
                    containsChatKeywords = true;
                }

                if (containsInsert && containsChatKeywords)
                {
                    return true;
                }
            }

            return containsInsert && containsChatKeywords;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ParsedConversation ReadConversation(string filePath)
    {
        try
        {
            string? firstTalker = null;

            foreach (var row in EnumerateRows(filePath, CancellationToken.None))
            {
                var talker = GetValue(row, "talker", "chat_id", "chatid", "peer_uid", "session_id");
                if (!string.IsNullOrEmpty(talker))
                {
                    firstTalker = talker;
                    break;
                }
            }

            var nativeId = firstTalker ?? Path.GetFileNameWithoutExtension(filePath);
            var isGroup = nativeId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) || nativeId.Contains("群");
            var kind = isGroup ? "group" : "private";
            var title = nativeId;

            return new ParsedConversation("sql", "sql-default", nativeId, kind, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }

    public static IEnumerable<ParsedMessage> IterateMessages(
        string filePath,
        ParsedConversation conversation,
        CancellationToken cancellationToken)
    {
        var rowIndex = 0;
        foreach (var row in EnumerateRows(filePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var nativeId = GetValue(row, "id", "msg_id", "msgid", "svrid", "platform_id");
            var localId = GetValue(row, "local_id", "localid", "id") ?? rowIndex.ToString();
            var talker = GetValue(row, "talker", "chat_id", "chatid", "peer_uid") ?? conversation.NativeId;
            var sender = GetValue(row, "sender", "sender_name", "from_user", "from_username");
            var isSendStr = GetValue(row, "is_send", "issend", "is_sender", "is_self");
            var isSend = isSendStr == "1" || string.Equals(isSendStr, "true", StringComparison.OrdinalIgnoreCase);

            var timeVal = GetValue(row, "create_time", "createtime", "timestamp", "time", "msg_time");
            var timestampMs = ParseTimestamp(timeVal);

            var typeVal = GetValue(row, "type", "msg_type", "local_type");
            var typeInt = int.TryParse(typeVal, out var t) ? t : 1;
            var messageType = ResolveMessageType(typeInt);

            var content = GetValue(row, "content", "text", "message", "body", "msg_content") ?? string.Empty;

            if (string.IsNullOrEmpty(content) && timestampMs == 0 && string.IsNullOrEmpty(GetValue(row, "talker", "sender", "content", "msg_content")))
            {
                continue;
            }

            var isSystem = typeInt == 10000 || messageType == "system";
            var isRecalled = isSystem && content.Contains("撤回", StringComparison.Ordinal);
            var direction = isSystem ? "system" : (isSend ? "outgoing" : "incoming");

            var senderNative = isSend ? "self" : (string.IsNullOrEmpty(sender) ? talker : sender);
            var senderName = isSend ? "我" : (string.IsNullOrEmpty(sender) ? talker : sender);

            var rawPayload = new JsonObject();
            foreach (var kvp in row)
            {
                rawPayload[kvp.Key] = kvp.Value;
            }

            var semantic = new JsonObject
            {
                ["timestamp_ms"] = timestampMs,
                ["sender"] = senderNative,
                ["direction"] = direction,
                ["message_type"] = messageType,
                ["content"] = content,
                ["search_text"] = content,
            };

            var payloadHash = CanonicalJson.HashHex(semantic);
            var semanticHash = CanonicalJson.HashHex(new JsonObject
            {
                ["timestamp_ms"] = timestampMs,
                ["sender"] = senderNative,
                ["direction"] = direction,
            });

            var senderAliases = new List<string>();
            if (!string.IsNullOrEmpty(senderName))
            {
                senderAliases.Add(senderName);
            }
            if (!senderAliases.Contains(senderNative, StringComparer.OrdinalIgnoreCase))
            {
                senderAliases.Add(senderNative);
            }

            yield return new ParsedMessage(
                NativeId: nativeId,
                LocalId: localId,
                TimestampMs: timestampMs,
                Sequence: null,
                SenderNativeId: senderNative,
                SenderName: senderName,
                SenderAliases: senderAliases,
                Direction: direction,
                MessageType: messageType,
                MediaType: messageType != "text" && messageType != "system" ? messageType : null,
                Content: content,
                SearchText: content,
                IsRecalled: isRecalled,
                IsSystem: isSystem,
                ReplyToNativeId: null,
                PayloadHash: payloadHash,
                SemanticHash: semanticHash,
                SourceLocator: $"row:{rowIndex}",
                RawPayload: rawPayload,
                Attachments: Array.Empty<ParsedAttachment>(),
                CompatiblePayloadHashes: Array.Empty<string>());

            rowIndex++;
        }
    }

    public static IEnumerable<Dictionary<string, string?>> EnumerateRows(string filePath, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        foreach (var row in EnumerateRows(reader, cancellationToken))
        {
            yield return row;
        }
    }

    internal static IEnumerable<Dictionary<string, string?>> EnumerateRows(TextReader reader, CancellationToken cancellationToken = default)
    {
        var statementBuilder = new StringBuilder();
        var tableSchemas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var inString = false;
        var stringChar = '\0';
        var inBlockComment = false;
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!inString && !inBlockComment)
            {
                var trimmedLine = line.TrimStart();
                if (trimmedLine.StartsWith("--", StringComparison.Ordinal) || trimmedLine.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                }
                else if (inString)
                {
                    statementBuilder.Append(c);
                    if (c == stringChar)
                    {
                        if (i + 1 < line.Length && line[i + 1] == stringChar)
                        {
                            statementBuilder.Append(line[i + 1]);
                            i++;
                        }
                        else
                        {
                            inString = false;
                        }
                    }
                    else if (c == '\\' && i + 1 < line.Length)
                    {
                        statementBuilder.Append(line[i + 1]);
                        i++;
                    }
                }
                else
                {
                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i++;
                    }
                    else if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
                    {
                        break;
                    }
                    else if (c == '#')
                    {
                        break;
                    }
                    else if (c == '\'' || c == '"' || c == '`')
                    {
                        inString = true;
                        stringChar = c;
                        statementBuilder.Append(c);
                    }
                    else if (c == ';')
                    {
                        var stmt = statementBuilder.ToString().Trim();
                        statementBuilder.Clear();

                        if (stmt.Length > 0)
                        {
                            foreach (var row in ProcessStatement(stmt, tableSchemas))
                            {
                                yield return row;
                            }
                        }
                    }
                    else
                    {
                        statementBuilder.Append(c);
                    }
                }
            }

            if (inString)
            {
                statementBuilder.Append('\n');
            }
            else if (!inBlockComment && statementBuilder.Length > 0)
            {
                statementBuilder.Append('\n');
            }
        }

        var remaining = statementBuilder.ToString().Trim();
        if (remaining.Length > 0)
        {
            foreach (var row in ProcessStatement(remaining, tableSchemas))
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<Dictionary<string, string?>> ProcessStatement(
        string statement,
        Dictionary<string, List<string>> tableSchemas)
    {
        var createTable = MatchCreateTable(statement);
        if (createTable != null)
        {
            var (tableName, body) = createTable.Value;
            var parsedCols = ParseCreateTableColumns(body);
            if (parsedCols.Count > 0)
            {
                tableSchemas[tableName] = parsedCols;
            }
            yield break;
        }

        var match = InsertHeaderRegex.Match(statement);
        if (!match.Success)
        {
            yield break;
        }

        var insertTable = match.Groups["table"].Value;
        var columnsGroup = match.Groups["columns"];
        List<string>? columns = null;

        if (columnsGroup.Success && !string.IsNullOrWhiteSpace(columnsGroup.Value))
        {
            columns = columnsGroup.Value
                .Split(',')
                .Select(c => c.Trim().Trim('`', '"', '\'', '[', ']'))
                .ToList();
        }
        else if (tableSchemas.TryGetValue(insertTable, out var cachedCols))
        {
            columns = cachedCols;
        }

        if (!IsChatTableOrColumns(insertTable, columns))
        {
            yield break;
        }

        var valuesIndex = match.Index + match.Length;
        var valuesPart = statement.Substring(valuesIndex);

        var tuples = ParseSqlValuesTuples(valuesPart);
        foreach (var tuple in tuples)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tuple.Count; i++)
            {
                var colName = columns != null && i < columns.Count ? columns[i] : $"col_{i}";
                dict[colName] = tuple[i];
            }
            yield return dict;
        }
    }

    private static (string tableName, string body)? MatchCreateTable(string statement)
    {
        var match = CreateTablePrefixRegex.Match(statement);
        if (!match.Success)
        {
            return null;
        }

        var tableName = match.Groups["table"].Value;
        var openParenIdx = statement.IndexOf('(', match.Index + match.Length - 1);
        if (openParenIdx < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var quoteChar = '\0';
        var closeParenIdx = -1;

        for (var i = openParenIdx; i < statement.Length; i++)
        {
            var c = statement[i];
            if (inString)
            {
                if (c == quoteChar)
                {
                    if (i + 1 < statement.Length && statement[i + 1] == quoteChar)
                    {
                        i++;
                    }
                    else
                    {
                        inString = false;
                    }
                }
                else if (c == '\\' && i + 1 < statement.Length)
                {
                    i++;
                }
            }
            else
            {
                if (c == '\'' || c == '"' || c == '`')
                {
                    inString = true;
                    quoteChar = c;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeParenIdx = i;
                        break;
                    }
                }
            }
        }

        if (closeParenIdx > openParenIdx)
        {
            var body = statement.Substring(openParenIdx + 1, closeParenIdx - openParenIdx - 1);
            return (tableName, body);
        }

        return null;
    }

    private static List<string> ParseCreateTableColumns(string body)
    {
        var columns = new List<string>();
        var items = SplitByTopLevelCommas(body);

        foreach (var item in items)
        {
            var trimmed = item.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (IsConstraint(trimmed))
            {
                continue;
            }

            var colName = ExtractColumnName(trimmed);
            if (!string.IsNullOrEmpty(colName))
            {
                columns.Add(colName);
            }
        }

        return columns;
    }

    private static List<string> SplitByTopLevelCommas(string text)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inString = false;
        var quoteChar = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                current.Append(c);
                if (c == quoteChar)
                {
                    if (i + 1 < text.Length && text[i + 1] == quoteChar)
                    {
                        current.Append(quoteChar);
                        i++;
                    }
                    else
                    {
                        inString = false;
                    }
                }
                else if (c == '\\' && i + 1 < text.Length)
                {
                    current.Append(text[i + 1]);
                    i++;
                }
            }
            else
            {
                if (c == '\'' || c == '"' || c == '`')
                {
                    inString = true;
                    quoteChar = c;
                    current.Append(c);
                }
                else if (c == '(')
                {
                    depth++;
                    current.Append(c);
                }
                else if (c == ')')
                {
                    if (depth > 0) depth--;
                    current.Append(c);
                }
                else if (c == ',' && depth == 0)
                {
                    items.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        if (current.Length > 0)
        {
            items.Add(current.ToString());
        }

        return items;
    }

    private static bool IsConstraint(string item)
    {
        var upper = item.ToUpperInvariant();
        return upper.StartsWith("PRIMARY KEY", StringComparison.Ordinal)
            || upper.StartsWith("FOREIGN KEY", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE KEY", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE INDEX", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE (", StringComparison.Ordinal)
            || upper.StartsWith("UNIQUE(", StringComparison.Ordinal)
            || upper.StartsWith("KEY ", StringComparison.Ordinal)
            || upper.StartsWith("INDEX ", StringComparison.Ordinal)
            || upper.StartsWith("CONSTRAINT ", StringComparison.Ordinal)
            || upper.StartsWith("CHECK ", StringComparison.Ordinal)
            || upper.StartsWith("CHECK(", StringComparison.Ordinal)
            || upper.StartsWith("FULLTEXT ", StringComparison.Ordinal)
            || upper.StartsWith("SPATIAL ", StringComparison.Ordinal);
    }

    private static string? ExtractColumnName(string item)
    {
        var trimmed = item.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed[0] == '`' || trimmed[0] == '"' || trimmed[0] == '\'' || trimmed[0] == '[')
        {
            var closing = trimmed[0] == '[' ? ']' : trimmed[0];
            var endIdx = trimmed.IndexOf(closing, 1);
            if (endIdx > 1)
            {
                return trimmed.Substring(1, endIdx - 1);
            }
        }

        var parts = trimmed.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            return parts[0].Trim('`', '"', '\'', '[', ']');
        }

        return null;
    }

    private static bool IsChatTableOrColumns(string tableName, List<string>? columns)
    {
        if (KnownNonChatTables.Contains(tableName))
        {
            if (columns != null && columns.Any(c => c.Equals("content", StringComparison.OrdinalIgnoreCase)
                                                 || c.Equals("msg_content", StringComparison.OrdinalIgnoreCase)
                                                 || c.Equals("message", StringComparison.OrdinalIgnoreCase)
                                                 || c.Equals("text", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return false;
        }

        if (columns != null && columns.Count > 0)
        {
            var hasChatColumn = columns.Any(col => ChatColumnKeywords.Any(k => col.Equals(k, StringComparison.OrdinalIgnoreCase) || col.Contains(k, StringComparison.OrdinalIgnoreCase)));
            if (hasChatColumn)
            {
                return true;
            }
        }

        var isChatTable = ChatTableKeywords.Any(k => tableName.Equals(k, StringComparison.OrdinalIgnoreCase) || tableName.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (isChatTable)
        {
            return true;
        }

        return false;
    }

    private static List<List<string?>> ParseSqlValuesTuples(string valuesText)
    {
        var result = new List<List<string?>>();
        var currentTuple = new List<string?>();
        var currentField = new StringBuilder();
        var tupleDepth = 0;
        var inString = false;
        var isQuoted = false;
        var stringQuote = '\0';

        for (var i = 0; i < valuesText.Length; i++)
        {
            var c = valuesText[i];

            if (inString)
            {
                if (c == stringQuote)
                {
                    if (i + 1 < valuesText.Length && valuesText[i + 1] == stringQuote)
                    {
                        currentField.Append(stringQuote);
                        i++;
                    }
                    else
                    {
                        inString = false;
                        if (tupleDepth > 1)
                        {
                            currentField.Append(stringQuote);
                        }
                    }
                }
                else if (c == '\\' && i + 1 < valuesText.Length)
                {
                    var next = valuesText[i + 1];
                    if (next == '\'' || next == '"' || next == '\\')
                    {
                        currentField.Append(next);
                        i++;
                    }
                    else if (next == 'n')
                    {
                        currentField.Append('\n');
                        i++;
                    }
                    else if (next == 'r')
                    {
                        currentField.Append('\r');
                        i++;
                    }
                    else if (next == 't')
                    {
                        currentField.Append('\t');
                        i++;
                    }
                    else
                    {
                        currentField.Append(c);
                    }
                }
                else
                {
                    currentField.Append(c);
                }
            }
            else
            {
                if (c == '(')
                {
                    tupleDepth++;
                    if (tupleDepth == 1)
                    {
                        currentTuple = new List<string?>();
                        currentField.Clear();
                        isQuoted = false;
                    }
                    else
                    {
                        currentField.Append('(');
                    }
                }
                else if (c == ')' && tupleDepth > 0)
                {
                    tupleDepth--;
                    if (tupleDepth == 0)
                    {
                        AddCurrentField(currentTuple, currentField, isQuoted);
                        result.Add(currentTuple);
                        isQuoted = false;
                    }
                    else
                    {
                        currentField.Append(')');
                    }
                }
                else if (c == ',' && tupleDepth == 1)
                {
                    AddCurrentField(currentTuple, currentField, isQuoted);
                    isQuoted = false;
                }
                else if (c == ',' && tupleDepth > 1)
                {
                    currentField.Append(',');
                }
                else if ((c == '\'' || c == '"') && tupleDepth > 0)
                {
                    inString = true;
                    stringQuote = c;
                    if (tupleDepth == 1)
                    {
                        isQuoted = true;
                        currentField.Clear();
                    }
                    else
                    {
                        currentField.Append(c);
                    }
                }
                else if (tupleDepth > 0)
                {
                    if (!isQuoted || tupleDepth > 1)
                    {
                        currentField.Append(c);
                    }
                }
            }
        }

        return result;
    }

    private static void AddCurrentField(List<string?> tuple, StringBuilder field, bool isQuoted)
    {
        if (isQuoted)
        {
            tuple.Add(field.ToString());
        }
        else
        {
            var raw = field.ToString().Trim();
            if (string.Equals(raw, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                tuple.Add(null);
            }
            else
            {
                tuple.Add(raw);
            }
        }

        field.Clear();
    }

    private static string? GetValue(Dictionary<string, string?> row, params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
        {
            if (row.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
            {
                return val;
            }
        }
        return null;
    }

    private static string ResolveMessageType(int type)
    {
        return type switch
        {
            1 => "text",
            3 => "image",
            34 => "audio",
            43 => "video",
            47 => "emoji",
            49 => "link",
            10000 => "system",
            _ => "text"
        };
    }

    private static long ParseTimestamp(string? timeVal)
    {
        if (string.IsNullOrWhiteSpace(timeVal))
        {
            return 0;
        }

        timeVal = timeVal.Trim();
        if (long.TryParse(timeVal, out var rawLong))
        {
            return rawLong >= 10_000_000_000L ? rawLong : rawLong * 1000L;
        }

        if (DateTimeOffset.TryParse(timeVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }

        if (DateTime.TryParse(timeVal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var local = TimeZoneInfo.Local;
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var offset = local.GetUtcOffset(unspecified);
            return new DateTimeOffset(unspecified, offset).ToUnixTimeMilliseconds();
        }

        return 0;
    }
}
