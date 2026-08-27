using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using Xunit;

namespace ChatArchive.Core.Tests;

public class SearchRepositoryTests : IDisposable
{
    private readonly TestArchive _archive = new();
    private readonly SearchRepository _repository;

    public SearchRepositoryTests()
    {
        _repository = new SearchRepository(_archive.Db);
    }

    private (long Qq, long Group) Seed()
    {
        var qq = TestArchive.AddConversation(_archive.Open(), "qq1", "老张");
        var group = TestArchive.AddConversation(_archive.Open(), "wx1", "工作群", kind: "group", platform: "wechat");

        _archive.AddMessage(qq, null, 1_700_000_000_000, "今天天气不错");
        _archive.AddMessage(qq, null, 1_700_000_100_000, "明天可能下雨", direction: "outgoing");
        _archive.AddMessage(group, null, 1_700_000_200_000, "天气预报说明天下雨", messageType: "image", platform: "wechat");
        _archive.AddMessage(group, null, 1_700_000_300_000, "好的收到", platform: "wechat");
        _archive.RefreshCounts(_archive.Open(), qq);
        _archive.RefreshCounts(_archive.Open(), group);
        return (qq, group);
    }

    [Fact]
    public void Chinese_three_char_query_uses_fts()
    {
        Seed();
        var page = _repository.Search("下雨");
        Assert.Equal(SearchMode.Substring, page.Mode);

        var fts = _repository.Search("天气预报");
        Assert.Equal(SearchMode.Fts, fts.Mode);
        Assert.Contains(fts.Items, h => h.Snippet.Contains("天气预报"));
    }

    [Fact]
    public void Two_char_query_falls_back_to_like_and_matches()
    {
        Seed();
        var page = _repository.Search("下雨");
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, h => Assert.Contains("下雨", h.Snippet));
    }

    [Fact]
    public void Filters_apply()
    {
        Seed();
        var wechat = _repository.Search("下雨", new SearchFilter(Platform: "wechat"));
        Assert.Single(wechat.Items);

        var groups = _repository.Search("下雨", new SearchFilter(Kind: "group"));
        Assert.Single(groups.Items);

        var bySender = _repository.Search("下雨", new SearchFilter(Sender: "Alice"));
        Assert.Equal(2, bySender.Items.Count);
    }

    [Fact]
    public void Conversation_message_type_and_date_filters_combine()
    {
        var (_, group) = Seed();
        const long timestamp = 1_700_000_200_000;

        var page = _repository.Search("下雨", new SearchFilter(
            ConversationId: group,
            MessageType: "image",
            DateFromMs: timestamp,
            DateToExclusiveMs: timestamp + 1));

        var hit = Assert.Single(page.Items);
        Assert.Equal(group, hit.ConversationId);
        Assert.Equal("image", hit.MessageType);
        Assert.Equal(timestamp, hit.TimestampMs);

        Assert.Empty(_repository.Search("下雨", new SearchFilter(
            ConversationId: group,
            DateToExclusiveMs: timestamp)).Items);
    }

    [Fact]
    public void Cursor_paginates_exhaustively()
    {
        Seed();
        var seen = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = _repository.Search("消息", cursor: cursor);
            foreach (var hit in page.Items)
            {
                seen.Add(hit.Snippet);
            }

            if (page.NextCursor is null)
            {
                break;
            }

            cursor = page.NextCursor;
            Assert.True(seen.Count < 100, "游标未收敛");
        }
    }

    [Fact]
    public void Empty_query_returns_empty_page()
    {
        Seed();
        var page = _repository.Search("   ");
        Assert.Empty(page.Items);
        Assert.Equal(SearchMode.Empty, page.Mode);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("天气预报", true)]
    [InlineData("a b c", false)]
    [InlineData("ab", false)]
    [InlineData("ab!", true)]
    [InlineData("hi alice", false)]
    [InlineData("好的 谢谢", false)]
    [InlineData("好的 谢谢啊", false)]
    [InlineData("hello world", true)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    public void SupportsTrigram_rules(string query, bool expected)
    {
        Assert.Equal(expected, SearchRepository.SupportsTrigram(query));
    }

    [Fact]
    public void MakeSnippet_is_case_insensitive()
    {
        var snippet = SearchRepository.MakeSnippet("Hello Alice World", "hello alice world", "alice");
        Assert.Contains("Alice", snippet);
    }

    [Theory]
    [InlineData("invalid_cursor")]
    [InlineData("-1_abc")]
    [InlineData("abc")]
    [InlineData("_123")]
    public void Search_WithInvalidCursor_FallsBackGracefully(string invalidCursor)
    {
        Seed();
        var page = _repository.Search("下雨", cursor: invalidCursor);
        Assert.NotEmpty(page.Items);
    }

    public void Dispose() => _archive.Dispose();
}
