using System.Globalization;

namespace ChatArchive.Core.Data;

/// <summary>
/// 时间线/搜索游标：把 (timestamp_ms, id) 编码为不透明字符串。
/// </summary>
public static class CursorCodec
{
    public static string Encode(long timestampMs, long id)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{timestampMs}_{id}");
    }

    public static (long TimestampMs, long Id) Decode(string cursor)
    {
        var separator = cursor.IndexOf('_');
        if (separator <= 0 || separator == cursor.Length - 1)
        {
            throw new FormatException("游标格式无效");
        }

        if (!long.TryParse(cursor.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestampMs)
            || !long.TryParse(cursor.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new FormatException("游标格式无效");
        }

        return (timestampMs, id);
    }
}
