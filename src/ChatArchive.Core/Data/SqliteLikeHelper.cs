namespace ChatArchive.Core.Data;

public static class SqliteLikeHelper
{
    public static string EscapeLikePattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return string.Empty;
        }

        return pattern
            .Replace("/", "//")
            .Replace("%", "/%")
            .Replace("_", "/_");
    }
}
