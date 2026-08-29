using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

internal sealed record SqlInsertRow(
    string Table,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Reads the inert, data-only SQL subset emitted by the current WeFlow and
/// CipherTalk writers. SQL is never executed or evaluated.
/// </summary>
internal static class SqlInsertReader
{
    private const int MaxIdentifierLength = 128;
    private const int MaxColumns = 256;
    private const int MaxFramingLength = 64 * 1024;

    private static readonly Regex NumericLiteralRegex = new(
        @"^[+-]?(?:(?:\d+(?:\.\d*)?)|(?:\.\d+))(?:[eE][+-]?\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CreateTableRegex = new(
        "^CREATE\\s+TABLE\\s+IF\\s+NOT\\s+EXISTS\\s+(?<table>\"(?:\"\"|[^\"])+\"|[A-Za-z_][A-Za-z0-9_]*)\\s*\\((?<body>[\\s\\S]*)\\)\\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CreateIndexRegex = new(
        @"^CREATE\s+INDEX\s+IF\s+NOT\s+EXISTS\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+ON\s+(?<table>weflow_messages|messages)\s*\((?<columns>\s*[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*\s*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeleteRegex = new(
        @"^DELETE\s+FROM\s+(?<table>messages|sessions)\s+WHERE\s+(?<column>session_wxid|wxid)\s*=\s*'(?:[^']|'')*'\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string[]> CreateTableDeclarations =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["weflow_messages"] =
            [
                "session_id TEXT NOT NULL",
                "local_id TEXT",
                "message_id TEXT",
                "create_time BIGINT NOT NULL",
                "sender TEXT",
                "is_send BOOLEAN NOT NULL",
                "local_type INTEGER",
                "media_type TEXT",
                "content TEXT",
                "media_path TEXT"
            ],
            ["sessions"] =
            [
                "wxid TEXT PRIMARY KEY",
                "display_name TEXT NOT NULL",
                "session_type TEXT NOT NULL",
                "owner_id TEXT",
                "message_count INTEGER DEFAULT 0",
                "first_message_time BIGINT",
                "last_message_time BIGINT",
                "exported_at BIGINT"
            ],
            ["messages"] =
            [
                "id SERIAL PRIMARY KEY",
                "session_wxid TEXT NOT NULL REFERENCES sessions(wxid)",
                "local_id INTEGER",
                "create_time BIGINT NOT NULL",
                "formatted_time TEXT",
                "msg_type TEXT",
                "content TEXT",
                "is_send SMALLINT DEFAULT 0",
                "sender_username TEXT",
                "sender_display_name TEXT",
                "group_nickname TEXT",
                "reply_to_message_id TEXT"
            ]
        };

    private static readonly IReadOnlyDictionary<string, (string Table, string[] Columns)> CreateIndexes =
        new Dictionary<string, (string Table, string[] Columns)>(StringComparer.OrdinalIgnoreCase)
        {
            ["idx_weflow_messages_session_time"] = ("weflow_messages", ["session_id", "create_time"]),
            ["idx_messages_session"] = ("messages", ["session_wxid"]),
            ["idx_messages_create_time"] = ("messages", ["create_time"]),
            ["idx_messages_sender"] = ("messages", ["sender_username"])
        };

    internal static IEnumerable<SqlInsertRow> Enumerate(
        TextReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var source = new CharacterSource(reader, cancellationToken);

        while (true)
        {
            source.SkipTrivia();
            if (source.Peek() < 0)
            {
                yield break;
            }

            var keyword = source.ReadIdentifier("statement keyword");
            switch (keyword.ToUpperInvariant())
            {
                case "BEGIN":
                case "COMMIT":
                    source.ExpectStatementEnd(keyword);
                    break;
                case "CREATE":
                    ValidateCreate(source.ReadFramingStatement("CREATE"));
                    break;
                case "DELETE":
                    ValidateDelete(source.ReadFramingStatement("DELETE"));
                    break;
                case "INSERT":
                    foreach (var row in ReadInsert(source))
                    {
                        yield return row;
                    }
                    break;
                default:
                    throw new FormatException($"Unsupported SQL statement {keyword}.");
            }
        }
    }

    private static IEnumerable<SqlInsertRow> ReadInsert(CharacterSource source)
    {
        source.ExpectKeyword("INTO");
        var table = source.ReadSqlIdentifier("INSERT table").PostgreSqlName;
        if (!CreateTableDeclarations.ContainsKey(table))
        {
            throw new FormatException($"Unsupported INSERT table {table}.");
        }

        source.SkipTrivia();
        if (source.Read() != '(')
        {
            throw new FormatException($"INSERT INTO {table} requires an explicit column list.");
        }

        var columns = ReadColumns(source, table);
        source.ExpectKeyword("VALUES");
        var rowNumber = 0;

        while (true)
        {
            source.SkipTrivia();
            if (source.Read() != '(')
            {
                throw new FormatException($"INSERT INTO {table} requires a VALUES tuple.");
            }

            rowNumber++;
            var values = ReadTuple(source, table, columns, rowNumber);
            yield return new SqlInsertRow(table, values);

            source.SkipTrivia();
            var separator = source.Read();
            if (separator == ',')
            {
                continue;
            }
            if (separator == ';')
            {
                yield break;
            }
            if (separator < 0)
            {
                throw new FormatException($"INSERT INTO {table} is missing its terminating semicolon.");
            }
            throw new FormatException($"INSERT INTO {table} has unexpected data after row {rowNumber}.");
        }
    }

    private static IReadOnlyList<string> ReadColumns(CharacterSource source, string table)
    {
        var columns = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            source.SkipTrivia();
            if (source.Peek() is ',' or ')')
            {
                throw new FormatException($"INSERT INTO {table} contains an empty column name.");
            }

            var column = source.ReadSqlIdentifier($"INSERT INTO {table} column").PostgreSqlName;
            if (!unique.Add(column))
            {
                throw new FormatException($"INSERT INTO {table} repeats column {column}.");
            }
            columns.Add(column);
            if (columns.Count > MaxColumns)
            {
                throw new FormatException($"INSERT INTO {table} has too many columns.");
            }

            source.SkipTrivia();
            var separator = source.Read();
            if (separator == ',')
            {
                continue;
            }
            if (separator == ')')
            {
                return columns;
            }
            throw new FormatException($"INSERT INTO {table} has a malformed column list.");
        }
    }

    private static IReadOnlyDictionary<string, string?> ReadTuple(
        CharacterSource source,
        string table,
        IReadOnlyList<string> columns,
        int rowNumber)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
        {
            source.CancellationToken.ThrowIfCancellationRequested();
            var column = columns[index];
            values[column] = ReadScalar(source, table, rowNumber, column);

            source.SkipTrivia();
            var separator = source.Read();
            if (index + 1 < columns.Count)
            {
                if (separator != ',')
                {
                    throw new FormatException(
                        $"INSERT INTO {table} row {rowNumber} has fewer values than columns.");
                }
            }
            else if (separator != ')')
            {
                throw new FormatException(
                    $"INSERT INTO {table} row {rowNumber} has more values than columns or is malformed.");
            }
        }
        return values;
    }

    private static string? ReadScalar(
        CharacterSource source,
        string table,
        int rowNumber,
        string column)
    {
        source.SkipTrivia();
        var first = source.Read();
        if (first < 0 || first is ',' or ')')
        {
            if (first >= 0)
            {
                source.Unread(first);
            }
            throw ScalarError(table, rowNumber, column, "empty value");
        }

        if (first == '\'')
        {
            return ReadQuotedString(source, table, rowNumber, column);
        }

        var token = new StringBuilder();
        token.Append((char)first);
        while (true)
        {
            source.CancellationToken.ThrowIfCancellationRequested();
            var current = source.Read();
            if (current < 0)
            {
                break;
            }
            if (char.IsWhiteSpace((char)current) || current is ',' or ')')
            {
                source.Unread(current);
                break;
            }
            token.Append((char)current);
        }

        var raw = token.ToString();
        if (string.Equals(raw, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (string.Equals(raw, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            return "TRUE";
        }
        if (string.Equals(raw, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            return "FALSE";
        }
        if (NumericLiteralRegex.IsMatch(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
        {
            return raw;
        }

        throw ScalarError(table, rowNumber, column, $"unsupported scalar {raw}");
    }

    private static string ReadQuotedString(
        CharacterSource source,
        string table,
        int rowNumber,
        string column)
    {
        var value = new StringBuilder();
        while (true)
        {
            source.CancellationToken.ThrowIfCancellationRequested();
            var current = source.Read();
            if (current < 0)
            {
                throw ScalarError(table, rowNumber, column, "unterminated quoted string");
            }
            if (current != '\'')
            {
                value.Append((char)current);
                continue;
            }

            var next = source.Read();
            if (next == '\'')
            {
                value.Append('\'');
                continue;
            }
            if (next >= 0)
            {
                source.Unread(next);
            }
            return value.ToString();
        }
    }

    private static void ValidateCreate(string statement)
    {
        var indexMatch = CreateIndexRegex.Match(statement);
        if (indexMatch.Success)
        {
            var name = indexMatch.Groups["name"].Value;
            var indexTable = indexMatch.Groups["table"].Value;
            var columns = indexMatch.Groups["columns"].Value
                .Split(',')
                .Select(column => column.Trim())
                .ToArray();
            if (CreateIndexes.TryGetValue(name, out var expectedIndex)
                && string.Equals(indexTable, expectedIndex.Table, StringComparison.OrdinalIgnoreCase)
                && columns.SequenceEqual(expectedIndex.Columns, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            throw new FormatException("CREATE INDEX does not match a current writer index.");
        }

        var match = CreateTableRegex.Match(statement);
        if (!match.Success)
        {
            throw new FormatException("Unsupported or malformed CREATE statement.");
        }

        var table = ReadCreateIdentifier(match.Groups["table"].Value, "CREATE TABLE name");
        if (!CreateTableDeclarations.TryGetValue(table, out var expected))
        {
            throw new FormatException($"Unsupported CREATE TABLE {table}.");
        }

        var actual = ReadCreateDeclarations(match.Groups["body"].Value);
        if (actual.Count != expected.Length)
        {
            throw new FormatException($"CREATE TABLE {table} does not match the current writer schema.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var expectedTokens = TokenizeCreateDeclaration(expected[index]);
            var actualTokens = actual[index];
            if (!CreateDeclarationMatches(actualTokens, expectedTokens))
            {
                throw new FormatException($"CREATE TABLE {table} does not match the current writer schema.");
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<CreateToken>> ReadCreateDeclarations(string body)
    {
        var declarations = new List<IReadOnlyList<CreateToken>>();
        var item = new StringBuilder();
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < body.Length; index++)
        {
            var current = body[index];
            if (quote != '\0')
            {
                item.Append(current);
                if (current == quote)
                {
                    if (index + 1 < body.Length && body[index + 1] == quote)
                    {
                        item.Append(body[++index]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (current is '\'' or '\"')
            {
                quote = current;
                item.Append(current);
            }
            else if (current == '(')
            {
                depth++;
                item.Append(current);
            }
            else if (current == ')')
            {
                if (depth == 0)
                {
                    throw new FormatException("CREATE TABLE has unbalanced parentheses.");
                }
                depth--;
                item.Append(current);
            }
            else if (current == ',' && depth == 0)
            {
                declarations.Add(TokenizeCreateDeclaration(item.ToString()));
                item.Clear();
            }
            else
            {
                item.Append(current);
            }
        }

        if (quote != '\0' || depth != 0)
        {
            throw new FormatException("CREATE TABLE has an unterminated quote or parentheses.");
        }
        declarations.Add(TokenizeCreateDeclaration(item.ToString()));
        return declarations;
    }

    private static IReadOnlyList<CreateToken> TokenizeCreateDeclaration(string declaration)
    {
        var tokens = new List<CreateToken>();
        var index = 0;
        while (index < declaration.Length)
        {
            var current = declaration[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '"')
            {
                var quoted = new StringBuilder();
                index++;
                var terminated = false;
                while (index < declaration.Length)
                {
                    current = declaration[index++];
                    if (current != '"')
                    {
                        quoted.Append(current);
                        continue;
                    }
                    if (index < declaration.Length && declaration[index] == '"')
                    {
                        quoted.Append('"');
                        index++;
                        continue;
                    }
                    terminated = true;
                    break;
                }
                if (!terminated)
                {
                    throw new FormatException("CREATE TABLE has an unterminated quoted identifier.");
                }
                tokens.Add(new CreateToken(
                    ReadCreateIdentifier(
                        quoted.ToString(),
                        "quoted column identifier",
                        isQuoted: true),
                    IsQuoted: true));
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < declaration.Length
                       && (char.IsLetterOrDigit(declaration[index]) || declaration[index] == '_'))
                {
                    index++;
                }
                tokens.Add(new CreateToken(declaration[start..index].ToLowerInvariant(), IsQuoted: false));
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index++;
                while (index < declaration.Length && char.IsDigit(declaration[index]))
                {
                    index++;
                }
                tokens.Add(new CreateToken(declaration[start..index], IsQuoted: false));
                continue;
            }

            if (current is '(' or ')')
            {
                tokens.Add(new CreateToken(current.ToString(), IsQuoted: false));
                index++;
                continue;
            }

            throw new FormatException($"CREATE TABLE contains unsupported declaration token {current}.");
        }

        return tokens;
    }

    private static bool CreateDeclarationMatches(
        IReadOnlyList<CreateToken> actual,
        IReadOnlyList<CreateToken> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!string.Equals(actual[index].Value, expected[index].Value, StringComparison.Ordinal)
                || (actual[index].IsQuoted && !CanQuoteCreateIdentifier(expected, index)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool CanQuoteCreateIdentifier(IReadOnlyList<CreateToken> tokens, int index) =>
        index == 0
        || (index > 0 && tokens[index - 1].Value == "references")
        || (index > 2
            && tokens[index - 1].Value == "("
            && tokens[index - 3].Value == "references");

    private static string ReadCreateIdentifier(
        string raw,
        string description,
        bool isQuoted = false)
    {
        var value = raw;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            isQuoted = true;
            value = raw[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }
        if (value.Length == 0
            || !(char.IsLetter(value[0]) || value[0] == '_')
            || value.Any(character => !(char.IsLetterOrDigit(character) || character == '_'))
            || value.Length > MaxIdentifierLength)
        {
            throw new FormatException($"CREATE TABLE contains an invalid {description}.");
        }
        return isQuoted ? value : value.ToLowerInvariant();
    }

    private readonly record struct CreateToken(string Value, bool IsQuoted);

    private readonly record struct SqlIdentifier(string Text, bool IsQuoted)
    {
        internal string PostgreSqlName => IsQuoted ? Text : Text.ToLowerInvariant();
    }

    private static void ValidateDelete(string statement)
    {
        var match = DeleteRegex.Match(statement);
        if (!match.Success)
        {
            throw new FormatException("Unsupported or malformed DELETE statement.");
        }

        var table = match.Groups["table"].Value;
        var column = match.Groups["column"].Value;
        if ((table.Equals("messages", StringComparison.OrdinalIgnoreCase)
             && column.Equals("session_wxid", StringComparison.OrdinalIgnoreCase))
            || (table.Equals("sessions", StringComparison.OrdinalIgnoreCase)
                && column.Equals("wxid", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        throw new FormatException("DELETE table and identity column do not match current CipherTalk output.");
    }

    private static FormatException ScalarError(
        string table,
        int rowNumber,
        string column,
        string message) =>
        new($"INSERT INTO {table} row {rowNumber} column {column}: {message}.");

    private sealed class CharacterSource(TextReader reader, CancellationToken cancellationToken)
    {
        private readonly Stack<int> _pushback = new();

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal int Read()
        {
            CancellationToken.ThrowIfCancellationRequested();
            return _pushback.Count > 0 ? _pushback.Pop() : reader.Read();
        }

        internal int Peek()
        {
            var value = Read();
            if (value >= 0)
            {
                Unread(value);
            }
            return value;
        }

        internal void Unread(int value)
        {
            if (value >= 0)
            {
                _pushback.Push(value);
            }
        }

        internal void SkipTrivia()
        {
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var first = Read();
                if (first < 0)
                {
                    return;
                }
                if (char.IsWhiteSpace((char)first))
                {
                    continue;
                }
                if (first is not ('-' or '/'))
                {
                    Unread(first);
                    return;
                }

                var second = Read();
                if (first == '-' && second == '-')
                {
                    SkipLineComment();
                    continue;
                }
                if (first == '/' && second == '*')
                {
                    SkipBlockComment();
                    continue;
                }
                Unread(second);
                Unread(first);
                return;
            }
        }

        internal string ReadIdentifier(string description)
        {
            SkipTrivia();
            var value = new StringBuilder();
            var first = Read();
            if (first < 0 || !(char.IsLetter((char)first) || first == '_'))
            {
                throw new FormatException($"Expected {description}.");
            }
            value.Append((char)first);
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var current = Read();
                if (current < 0)
                {
                    break;
                }
                if (!(char.IsLetterOrDigit((char)current) || current == '_'))
                {
                    Unread(current);
                    break;
                }
                value.Append((char)current);
                if (value.Length > MaxIdentifierLength)
                {
                    throw new FormatException($"{description} is too long.");
                }
            }
            return value.ToString();
        }

        internal SqlIdentifier ReadSqlIdentifier(string description)
        {
            SkipTrivia();
            if (Peek() != '"')
            {
                return new SqlIdentifier(ReadIdentifier(description), IsQuoted: false);
            }

            _ = Read();
            var value = new StringBuilder();
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var current = Read();
                if (current < 0)
                {
                    throw new FormatException($"Unterminated {description}.");
                }
                if (current == '\0')
                {
                    throw new FormatException($"{description} contains a NUL character.");
                }
                if (current != '"')
                {
                    value.Append((char)current);
                }
                else
                {
                    var next = Read();
                    if (next == '"')
                    {
                        value.Append('"');
                    }
                    else
                    {
                        Unread(next);
                        break;
                    }
                }

                if (value.Length > MaxIdentifierLength)
                {
                    throw new FormatException($"{description} is too long.");
                }
            }

            if (value.Length == 0)
            {
                throw new FormatException($"Expected {description}.");
            }
            return new SqlIdentifier(value.ToString(), IsQuoted: true);
        }

        internal void ExpectKeyword(string expected)
        {
            var actual = ReadIdentifier(expected);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"Expected {expected}, found {actual}.");
            }
        }

        internal void ExpectStatementEnd(string statement)
        {
            SkipTrivia();
            if (Read() != ';')
            {
                throw new FormatException($"{statement} must end with a semicolon.");
            }
        }

        internal string ReadFramingStatement(string firstKeyword)
        {
            var statement = new StringBuilder(firstKeyword);
            var quote = '\0';
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var current = Read();
                if (current < 0)
                {
                    throw new FormatException($"{firstKeyword} statement is missing its terminating semicolon.");
                }

                if (quote != '\0')
                {
                    statement.Append((char)current);
                    if (current == quote)
                    {
                        var next = Read();
                        if (next == quote)
                        {
                            statement.Append((char)next);
                        }
                        else
                        {
                            quote = '\0';
                            Unread(next);
                        }
                    }
                }
                else if (current is '\'' or '\"')
                {
                    quote = (char)current;
                    statement.Append((char)current);
                }
                else if (current == ';')
                {
                    return statement.ToString();
                }
                else
                {
                    statement.Append((char)current);
                }

                if (statement.Length > MaxFramingLength)
                {
                    throw new FormatException($"{firstKeyword} framing statement is too large.");
                }
            }
        }

        private void SkipLineComment()
        {
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var current = Read();
                if (current < 0 || current is '\r' or '\n')
                {
                    return;
                }
            }
        }

        private void SkipBlockComment()
        {
            var previous = -1;
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var current = Read();
                if (current < 0)
                {
                    throw new FormatException("Unterminated SQL block comment.");
                }
                if (previous == '*' && current == '/')
                {
                    return;
                }
                previous = current;
            }
        }
    }
}
