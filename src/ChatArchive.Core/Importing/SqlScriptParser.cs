using System.Text;
using System.Text.RegularExpressions;

namespace ChatArchive.Core.Importing;

internal sealed record SqlInsertRow(
    string Table,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Reads data-only INSERT rows from SQL writer output. SQL is never executed and
/// scalar expressions are returned as inert text.
/// </summary>
internal static class SqlInsertReader
{
    private static readonly Regex InsertHeaderRegex = new(
        @"^\s*INSERT\s+INTO\s+(?:(?:\""[^\""\r\n]+\""|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<table>\""[^\""\r\n]+\""|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\((?<columns>[^)]*)\))?\s+VALUES\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CreateTablePrefixRegex = new(
        @"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:\""[^\""\r\n]+\""|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<table>\""[^\""\r\n]+\""|[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IEnumerable<SqlInsertRow> Enumerate(
        TextReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var statement = new StringBuilder();
        var tableSchemas = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var inQuote = false;
        var quote = '\0';
        var inBlockComment = false;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < line.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = line[i];

                if (inBlockComment)
                {
                    if (current == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                        statement.Append(' ');
                    }
                    continue;
                }

                if (inQuote)
                {
                    statement.Append(current);
                    if (current == quote)
                    {
                        if (i + 1 < line.Length && line[i + 1] == quote)
                        {
                            statement.Append(line[++i]);
                        }
                        else
                        {
                            inQuote = false;
                        }
                    }
                    continue;
                }

                if (current == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    statement.Append(' ');
                }
                else if (current == '-' && i + 1 < line.Length && line[i + 1] == '-')
                {
                    break;
                }
                else if (current == '#')
                {
                    break;
                }
                else if (current is '\'' or '\"' or '`')
                {
                    inQuote = true;
                    quote = current;
                    statement.Append(current);
                }
                else if (current == ';')
                {
                    foreach (var row in ProcessStatement(statement.ToString(), tableSchemas, cancellationToken))
                    {
                        yield return row;
                    }
                    statement.Clear();
                }
                else
                {
                    statement.Append(current);
                }
            }

            if (!inBlockComment && statement.Length > 0)
            {
                statement.Append('\n');
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (inQuote || inBlockComment)
        {
            throw new FormatException("SQL contains an unterminated quote or block comment.");
        }

        if (!string.IsNullOrWhiteSpace(statement.ToString()))
        {
            foreach (var row in ProcessStatement(statement.ToString(), tableSchemas, cancellationToken))
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<SqlInsertRow> ProcessStatement(
        string statement,
        IDictionary<string, IReadOnlyList<string>> tableSchemas,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var create = MatchCreateTable(statement, cancellationToken);
        if (create is not null)
        {
            var (createdTable, body) = create.Value;
            var createdColumns = ParseCreateTableColumns(body, cancellationToken);
            if (createdColumns.Count > 0)
            {
                tableSchemas[createdTable] = createdColumns;
            }
            yield break;
        }

        var match = InsertHeaderRegex.Match(statement);
        if (!match.Success)
        {
            yield break;
        }

        var table = UnquoteIdentifier(match.Groups["table"].Value);
        IReadOnlyList<string>? columns = null;
        var explicitColumns = match.Groups["columns"];
        if (explicitColumns.Success)
        {
            columns = ParseInsertColumns(explicitColumns.Value);
        }
        else if (tableSchemas.TryGetValue(table, out var schemaColumns))
        {
            columns = schemaColumns;
        }

        if (columns is null || columns.Count == 0)
        {
            throw new FormatException($"INSERT INTO {table} has no usable column list.");
        }

        var tuples = ParseValueTuples(statement[(match.Index + match.Length)..], cancellationToken);
        foreach (var tuple in tuples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tuple.Count != columns.Count)
            {
                throw new FormatException(
                    $"INSERT INTO {table} has {tuple.Count} values for {columns.Count} columns.");
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < columns.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!values.TryAdd(columns[index], DecodeScalar(tuple[index])))
                {
                    throw new FormatException($"INSERT INTO {table} repeats column {columns[index]}.");
                }
            }
            yield return new SqlInsertRow(table, values);
        }
    }

    private static (string Table, string Body)? MatchCreateTable(
        string statement,
        CancellationToken cancellationToken)
    {
        var match = CreateTablePrefixRegex.Match(statement);
        if (!match.Success)
        {
            return null;
        }

        var open = statement.IndexOf('(', match.Index + match.Length - 1);
        var depth = 0;
        var inQuote = false;
        var quote = '\0';
        for (var index = open; index < statement.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = statement[index];
            if (inQuote)
            {
                if (current == quote)
                {
                    if (index + 1 < statement.Length && statement[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
                continue;
            }

            if (current is '\'' or '\"' or '`')
            {
                inQuote = true;
                quote = current;
            }
            else if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return (
                    UnquoteIdentifier(match.Groups["table"].Value),
                    statement[(open + 1)..index]);
            }
        }

        throw new FormatException("CREATE TABLE contains an unterminated column list.");
    }

    private static IReadOnlyList<string> ParseCreateTableColumns(
        string body,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        foreach (var item in SplitTopLevel(body, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = item.Trim();
            if (trimmed.Length == 0 || IsTableConstraint(trimmed))
            {
                continue;
            }

            var identifier = ReadLeadingIdentifier(trimmed);
            if (identifier.Length > 0)
            {
                columns.Add(identifier);
            }
        }
        return columns;
    }

    private static IReadOnlyList<string> ParseInsertColumns(string text)
    {
        var columns = new List<string>();
        foreach (var item in text.Split(','))
        {
            var column = UnquoteIdentifier(item.Trim());
            if (column.Length == 0 || !IsIdentifier(column))
            {
                throw new FormatException("INSERT contains an invalid column name.");
            }
            columns.Add(column);
        }
        return columns;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseValueTuples(
        string text,
        CancellationToken cancellationToken)
    {
        var tuples = new List<IReadOnlyList<string>>();
        var tuple = new List<string>();
        var field = new StringBuilder();
        var depth = 0;
        var inQuote = false;
        var quote = '\0';
        var needTupleAfterComma = false;

        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = text[index];
            if (inQuote)
            {
                field.Append(current);
                if (current == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote)
                    {
                        field.Append(text[++index]);
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
                continue;
            }

            if (depth > 0)
            {
                if (current is '\'' or '\"')
                {
                    inQuote = true;
                    quote = current;
                    field.Append(current);
                }
                else if (current == '(')
                {
                    depth++;
                    field.Append(current);
                }
                else if (current == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        tuple.Add(field.ToString());
                        field.Clear();
                        tuples.Add(tuple);
                        tuple = new List<string>();
                        needTupleAfterComma = false;
                    }
                    else
                    {
                        field.Append(current);
                    }
                }
                else if (current == ',' && depth == 1)
                {
                    tuple.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(current);
                }
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                continue;
            }
            if (current == ',' && tuples.Count > 0 && !needTupleAfterComma)
            {
                needTupleAfterComma = true;
                continue;
            }
            if (current == '(' && (tuples.Count == 0 || needTupleAfterComma))
            {
                depth = 1;
                needTupleAfterComma = false;
                continue;
            }

            throw new FormatException("INSERT VALUES contains unexpected data outside a tuple.");
        }

        if (inQuote || depth != 0 || needTupleAfterComma || tuples.Count == 0)
        {
            throw new FormatException("INSERT VALUES contains a malformed tuple list.");
        }
        return tuples;
    }

    private static IReadOnlyList<string> SplitTopLevel(
        string text,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var depth = 0;
        var inQuote = false;
        var quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = text[index];
            if (inQuote)
            {
                value.Append(current);
                if (current == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote)
                    {
                        value.Append(text[++index]);
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
            }
            else if (current is '\'' or '\"' or '`')
            {
                inQuote = true;
                quote = current;
                value.Append(current);
            }
            else if (current == '(')
            {
                depth++;
                value.Append(current);
            }
            else if (current == ')')
            {
                depth--;
                value.Append(current);
            }
            else if (current == ',' && depth == 0)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(current);
            }
        }
        values.Add(value.ToString());
        return values;
    }

    private static string? DecodeScalar(string raw)
    {
        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            var decoded = new StringBuilder(trimmed.Length - 2);
            for (var index = 1; index < trimmed.Length - 1; index++)
            {
                var current = trimmed[index];
                if (current == '\'' && index + 1 < trimmed.Length - 1 && trimmed[index + 1] == '\'')
                {
                    decoded.Append('\'');
                    index++;
                }
                else if (current == '\'')
                {
                    return trimmed;
                }
                else
                {
                    decoded.Append(current);
                }
            }
            return decoded.ToString();
        }
        return trimmed;
    }

    private static bool IsTableConstraint(string text)
    {
        var first = ReadLeadingIdentifier(text);
        return first.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
            || first.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase)
            || first.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || first.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
            || first.Equals("CHECK", StringComparison.OrdinalIgnoreCase)
            || first.Equals("EXCLUDE", StringComparison.OrdinalIgnoreCase)
            || first.Equals("KEY", StringComparison.OrdinalIgnoreCase)
            || first.Equals("INDEX", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLeadingIdentifier(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        if (trimmed[0] is '\"' or '`')
        {
            var end = trimmed.IndexOf(trimmed[0], 1);
            return end > 1 ? trimmed[1..end] : string.Empty;
        }
        var length = 0;
        while (length < trimmed.Length && (char.IsLetterOrDigit(trimmed[length]) || trimmed[length] == '_'))
        {
            length++;
        }
        return trimmed[..length];
    }

    private static string UnquoteIdentifier(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2
            && ((trimmed[0] == '\"' && trimmed[^1] == '\"') || (trimmed[0] == '`' && trimmed[^1] == '`'))
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
}
