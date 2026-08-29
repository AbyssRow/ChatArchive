namespace ChatArchive.Core.Importing;

internal static class OpenXmlRowSupport
{
    internal static IReadOnlyDictionary<string, int> HeaderMap(IReadOnlyList<string> headers) =>
        headers.Select((header, index) => new KeyValuePair<string, int>(header, index + 1))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal static Dictionary<string, string> ReadValues(
        OpenXmlRow row,
        IReadOnlyDictionary<string, int> headers) =>
        headers.ToDictionary(
            pair => pair.Key,
            pair => CellValue(row, pair.Value),
            StringComparer.Ordinal);

    internal static bool HasExactHeaders(OpenXmlRow row, IReadOnlyList<string> expected)
    {
        if (row.Cells.Values.Any(cell =>
            cell.ColumnIndex > expected.Count && ImportText.Clean(cell.Value).Length > 0))
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!row.Cells.TryGetValue(index + 1, out var cell)
                || !string.Equals(ImportText.Clean(cell.Value), expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsBlank(OpenXmlRow row) =>
        row.Cells.Values.All(cell => ImportText.Clean(cell.Value).Length == 0);

    internal static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string CellValue(OpenXmlRow row, int column) =>
        row.Cells.TryGetValue(column, out var cell) ? ImportText.Clean(cell.Value) : string.Empty;
}
