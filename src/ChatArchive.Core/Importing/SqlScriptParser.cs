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
        @"INSERT\s+INTO\s+(?:[`""'\[]?)(?<table>\w+)(?:[`""'\]]?)\s*(?:\((?<columns>[^)]+)\))?\s*VALUES",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

    private static IEnumerable<Dictionary<string, string?>> EnumerateRows(string filePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var statementBuilder = new StringBuilder();
        var inString = false;
        var stringChar = '\0';
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = line.Trim();
            if (!inString && (trimmed.StartsWith("--", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal)))
            {
                continue;
            }

            statementBuilder.AppendLine(line);

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    if (c == stringChar)
                    {
                        if (i + 1 < line.Length && line[i + 1] == stringChar)
                        {
                            i++; // Skip escaped quote
                        }
                        else
                        {
                            inString = false;
                        }
                    }
                    else if (c == '\\' && i + 1 < line.Length)
                    {
                        i++; // Skip backslash escape
                    }
                }
                else
                {
                    if (c == '\'' || c == '"' || c == '`')
                    {
                        inString = true;
                        stringChar = c;
                    }
                    else if (c == ';')
                    {
                        var stmt = statementBuilder.ToString().Trim();
                        statementBuilder.Clear();
                        foreach (var row in ParseInsertStatement(stmt))
                        {
                            yield return row;
                        }
                    }
                }
            }
        }

        var remaining = statementBuilder.ToString().Trim();
        if (remaining.Length > 0)
        {
            foreach (var row in ParseInsertStatement(remaining))
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<Dictionary<string, string?>> ParseInsertStatement(string statement)
    {
        var match = InsertHeaderRegex.Match(statement);
        if (!match.Success)
        {
            yield break;
        }

        var columnsGroup = match.Groups["columns"];
        List<string>? columns = null;
        if (columnsGroup.Success && !string.IsNullOrWhiteSpace(columnsGroup.Value))
        {
            columns = columnsGroup.Value
                .Split(',')
                .Select(c => c.Trim().Trim('`', '"', '\'', '[', ']'))
                .ToList();
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

    private static List<List<string?>> ParseSqlValuesTuples(string valuesText)
    {
        var result = new List<List<string?>>();
        var currentTuple = new List<string?>();
        var currentField = new StringBuilder();
        var inTuple = false;
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
                    inTuple = true;
                    currentTuple = new List<string?>();
                    currentField.Clear();
                    isQuoted = false;
                }
                else if (c == ')' && inTuple)
                {
                    AddCurrentField(currentTuple, currentField, isQuoted);
                    inTuple = false;
                    result.Add(currentTuple);
                    isQuoted = false;
                }
                else if (c == ',' && inTuple)
                {
                    AddCurrentField(currentTuple, currentField, isQuoted);
                    isQuoted = false;
                }
                else if ((c == '\'' || c == '"') && inTuple)
                {
                    inString = true;
                    stringQuote = c;
                    isQuoted = true;
                    currentField.Clear();
                }
                else if (inTuple)
                {
                    if (!isQuoted)
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
