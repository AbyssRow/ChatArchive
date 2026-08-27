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

    public static bool TryDecode(string? cursor, out long timestampMs, out long id)
    {
        timestampMs = 0;
        id = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf('_');
        if (separator <= 0 || separator == cursor.Length - 1)
        {
            return false;
        }

        return long.TryParse(cursor.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out timestampMs)
            && long.TryParse(cursor.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    public static (long TimestampMs, long Id) Decode(string cursor)
    {
        if (TryDecode(cursor, out var timestampMs, out var id))
        {
            return (timestampMs, id);
        }

        throw new FormatException("游标格式无效");
    }
}
