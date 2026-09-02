using ChatArchive.Core.Data;
using Xunit;

namespace ChatArchive.Core.Tests;

public class DateUtilTests
{
    [Fact]
    public void DateToStartMs_and_DateToExclusiveEndMs_work_for_valid_date()
    {
        var start = DateUtil.DateToStartMs("2026-01-15");
        var end = DateUtil.DateToExclusiveEndMs("2026-01-15");

        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.True(end > start);
        Assert.Equal(86400000L, end!.Value - start!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DateToStartMs_returns_null_for_empty(string? input)
    {
        Assert.Null(DateUtil.DateToStartMs(input));
        Assert.Null(DateUtil.DateToExclusiveEndMs(input));
    }

    [Theory]
    [InlineData("2026/01/15")]
    [InlineData("invalid")]
    [InlineData("2026-13-01")]
    public void DateToStartMs_throws_for_invalid(string input)
    {
        Assert.Throws<FormatException>(() => DateUtil.DateToStartMs(input));
        Assert.Throws<FormatException>(() => DateUtil.DateToExclusiveEndMs(input));
    }

    [Theory]
    [InlineData("2024-5-1 12:00:00")]
    [InlineData("2024/5/1 12:00:00")]
    [InlineData("2024.5.1 12:00:00")]
    [InlineData("2024-5-1")]
    [InlineData("2024/5/1")]
    [InlineData("2024.5.1")]
    public void ParseFlexibleTimestamp_Supports_SingleDigit_MonthAndDay(string input)
    {
        var ts = ChatArchive.Core.Importing.ImportText.ParseFlexibleTimestamp(input);
        Assert.True(ts > 0);
    }

    [Fact]
    public void ParseFlexibleTimestamp_ParsesEightDigitDateCorrectly()
    {
        var ts = ChatArchive.Core.Importing.ImportText.ParseFlexibleTimestamp("20240101");
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime();
        Assert.Equal(2024, dto.Year);
        Assert.Equal(1, dto.Month);
        Assert.Equal(1, dto.Day);
    }

    [Fact]
    public void ParseFlexibleTimestamp_NaiveWallClock_UsesLocalOffset()
    {
        var ts = ChatArchive.Core.Importing.ImportText.ParseFlexibleTimestamp(Fixtures.SampleLocalTimestamp);
        Assert.Equal(Fixtures.LocalUnixMs(Fixtures.SampleLocalTimestamp), ts);
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime();
        Assert.Equal(2023, local.Year);
        Assert.Equal(11, local.Month);
        Assert.Equal(15, local.Day);
        Assert.Equal(6, local.Hour);
        Assert.Equal(15, local.Minute);
        Assert.Equal(23, local.Second);
    }
}

