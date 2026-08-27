namespace ChatArchive.Core.Data;

/// <summary>
/// SQLite LIKE 查询通配符与转义字符辅助类。
/// </summary>
public static class SqliteLikeHelper
{
    /// <summary>
    /// 默认 LIKE 转义字符 ('/')。
    /// </summary>
    public const char EscapeChar = '/';

    /// <summary>
    /// 转义 SQLite LIKE 子句中的特殊字符 ('/', '%', '_')，配合 ESCAPE '/' 使用。
    /// </summary>
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
