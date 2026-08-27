using System.Globalization;

namespace ChatArchive.Core.Data;

/// <summary>本地时区的 YYYY-MM-DD 与毫秒时间戳互转。</summary>
public static class DateUtil
{
    public static long? DateToStartMs(string? value)
    {
        return Parse(value, endOfDays: false);
    }

    public static long? DateToExclusiveEndMs(string? value)
    {
        return Parse(value, endOfDays: true);
    }

    private static long? Parse(string? value, bool endOfDays)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new FormatException($"日期格式无效: {value}");
        }

        var targetDate = endOfDays ? parsed.AddDays(1) : parsed;
        var local = TimeZoneInfo.Local;
        var unspecified = DateTime.SpecifyKind(targetDate, DateTimeKind.Unspecified);
        var offset = local.GetUtcOffset(unspecified);
        var utc = new DateTimeOffset(unspecified, offset);
        return utc.ToUnixTimeMilliseconds();
    }
}
