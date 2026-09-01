using ChatArchive.App.ViewModels;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
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

    [Fact]
    public void LoadOptions_newer_success_wins_when_older_success_finishes_last()
    {
        var first = new TaskCompletionSource<SearchOptionsSnapshot>();
        var second = new TaskCompletionSource<SearchOptionsSnapshot>();
        var loads = new Queue<Task<SearchOptionsSnapshot>>([first.Task, second.Task]);
        var viewModel = CreateOptionsViewModel(() => loads.Dequeue());
        var notifications = new List<(long Generation, bool Success)>();
        viewModel.OptionsReloaded += (generation, success) =>
            notifications.Add((generation, success));

        var generation1 = viewModel.LoadOptions();
        var generation2 = viewModel.LoadOptions();
        second.SetResult(OptionsSnapshot(2, "新会话", "image"));

        Assert.Equal(generation1 + 1, generation2);
        Assert.Equal((generation2, true), Assert.Single(notifications));
        Assert.Equal(new long?[] { null, 2 }, viewModel.ConversationOptions.Select(item => item.Id));

        first.SetResult(OptionsSnapshot(1, "旧会话", "text"));

        Assert.Single(notifications);
        Assert.Equal(new long?[] { null, 2 }, viewModel.ConversationOptions.Select(item => item.Id));
        Assert.Equal(new string?[] { null, "image" }, viewModel.MessageTypeOptions.Select(item => item.Value));
    }

    [Fact]
    public void LoadOptions_latest_failure_preserves_option_instances_and_notifies_failure()
    {
        var load = new TaskCompletionSource<SearchOptionsSnapshot>();
        var viewModel = CreateOptionsViewModel(() => load.Task);
        var conversation = new SearchConversationOption(7, "保留会话");
        var messageType = new SearchMessageTypeOption("image", "图片");
        viewModel.ConversationOptions.Add(conversation);
        viewModel.MessageTypeOptions.Add(messageType);
        (long Generation, bool Success)? notification = null;
        viewModel.OptionsReloaded += (generation, success) => notification = (generation, success);

        var generation = viewModel.LoadOptions();
        load.SetException(new InvalidOperationException("database unavailable"));

        Assert.Equal((generation, false), notification);
        Assert.Same(conversation, viewModel.ConversationOptions[1]);
        Assert.Same(messageType, viewModel.MessageTypeOptions[1]);
        Assert.Contains("database unavailable", viewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadOptions_stale_failure_does_not_overwrite_latest_state_or_notify()
    {
        var first = new TaskCompletionSource<SearchOptionsSnapshot>();
        var second = new TaskCompletionSource<SearchOptionsSnapshot>();
        var loads = new Queue<Task<SearchOptionsSnapshot>>([first.Task, second.Task]);
        var viewModel = CreateOptionsViewModel(() => loads.Dequeue());
        var notifications = new List<(long Generation, bool Success)>();
        viewModel.OptionsReloaded += (generation, success) =>
            notifications.Add((generation, success));

        _ = viewModel.LoadOptions();
        var latest = viewModel.LoadOptions();
        second.SetResult(OptionsSnapshot(2, "最新", "image"));
        first.SetException(new InvalidOperationException("stale failure"));

        Assert.Equal((latest, true), Assert.Single(notifications));
        Assert.Empty(viewModel.ErrorMessage);
        Assert.Equal(new long?[] { null, 2 }, viewModel.ConversationOptions.Select(item => item.Id));
    }

    private static SearchViewModel CreateOptionsViewModel(
        Func<Task<SearchOptionsSnapshot>> loader)
    {
        var database = new ArchiveDatabase(Path.Combine(
            Path.GetTempPath(),
            $"chatarchive-options-{Guid.NewGuid():N}.db"));
        return new SearchViewModel(new SearchRepository(database), loader);
    }

    private static SearchOptionsSnapshot OptionsSnapshot(long id, string title, string messageType)
    {
        var conversation = new ConversationInfo(
            id, "qq", "account", $"native-{id}", "private", title,
            null, null, 1, null, 0);
        return new SearchOptionsSnapshot(
            [conversation],
            new FilterOptions([new FilterOptionItem(messageType, 1)], []));
    }

    private static DateTimeOffset LocalDate(int year, int month, int day)
    {
        var value = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }
}
