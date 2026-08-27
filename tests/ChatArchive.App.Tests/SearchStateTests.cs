using ChatArchive.App.ViewModels;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class SearchStateTests
{
    [Fact]
    public void Exhausted_page_has_no_continuation_and_cannot_restart()
    {
        var state = new SearchRequestState();
        var first = state.Start("天气", new SearchFilter());

        Assert.True(state.ApplyPage(first, "next"));
        var continuation = state.Continue();
        Assert.NotNull(continuation);
        Assert.Equal("next", continuation.Value.Cursor);

        Assert.True(state.ApplyPage(continuation.Value, null));
        Assert.False(state.HasMore);
        Assert.Null(state.Continue());
    }

    [Fact]
    public void New_search_invalidates_previous_result()
    {
        var state = new SearchRequestState();
        var previous = state.Start("旧查询", new SearchFilter());
        var current = state.Start("新查询", new SearchFilter(Platform: "wechat"));

        Assert.False(state.IsCurrent(previous));
        Assert.True(state.IsCurrent(current));
        Assert.False(state.ApplyPage(previous, "stale"));
        Assert.False(state.HasMore);
    }

    [Fact]
    public void Filter_builder_maps_all_search_fields_and_local_dates()
    {
        var from = LocalDate(2026, 8, 1);
        var to = LocalDate(2026, 8, 20);

        var filter = SearchFilterBuilder.Build(
            "qq", "group", 42, " Alice ", "image", from, to);

        Assert.Equal("qq", filter.Platform);
        Assert.Equal("group", filter.Kind);
        Assert.Equal(42, filter.ConversationId);
        Assert.Equal("Alice", filter.Sender);
        Assert.Equal("image", filter.MessageType);
        Assert.Equal(DateUtil.DateToStartMs("2026-08-01"), filter.DateFromMs);
        Assert.Equal(DateUtil.DateToExclusiveEndMs("2026-08-20"), filter.DateToExclusiveMs);
    }

    [Theory]
    [InlineData("qq", "QQ")]
    [InlineData("wechat", "微信")]
    [InlineData("text", "文本")]
    [InlineData("html", "网页")]
    [InlineData("sql", "SQL")]
    [InlineData("telegram", "telegram")]
    [InlineData(null, "")]
    public void SearchHitProxy_MapsPlatformLabelCorrectly(string? platform, string expectedLabel)
    {
        var hit = new SearchHit(
            1, 10, "Test Title", platform ?? string.Empty, "group", 100L,
            "Alice", "Hello snippet", "text", "incoming", 1700000000000L);
        var proxy = new SearchHitProxy(hit);
        Assert.Equal(expectedLabel, proxy.PlatformLabel);
    }

    [Theory]
    [InlineData(-1000L)]
    [InlineData(999999999999999L)]
    public void SearchHitProxy_ClampsOutOfRangeTimestamp(long timestampMs)
    {
        var hit = new SearchHit(
            1, 10, "Test Title", "qq", "group", 100L,
            "Alice", "Hello snippet", "text", "incoming", timestampMs);
        var proxy = new SearchHitProxy(hit);
        Assert.NotNull(proxy.TimeText);
        Assert.NotEmpty(proxy.TimeText);
    }

    private static DateTimeOffset LocalDate(int year, int month, int day)
    {
        var value = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }
}
