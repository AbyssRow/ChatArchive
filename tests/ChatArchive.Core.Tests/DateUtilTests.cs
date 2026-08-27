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
}

