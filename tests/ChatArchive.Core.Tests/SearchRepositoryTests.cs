using ChatArchive.Core.Data;
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
    [InlineData("😀😀", false)]
    [InlineData("😀😀😀", true)]
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

    [Fact]
    public void Search_WithTrailingBackslash_DoesNotThrow()
    {
        using var archive = new TestArchive();
        var qq = TestArchive.AddConversation(archive.Open(), "qq_bs", "Backslash Test");
        archive.AddMessage(qq, null, 1_700_000_400_000, @"test\file");
        var repo = new SearchRepository(archive.Db);
        var result = repo.Search(@"test\");
        Assert.NotNull(result);
        var hit = Assert.Single(result.Items);
        Assert.Contains(@"test\file", hit.Snippet);
    }

    [Fact]
    public void Search_WithBackslashAndQuotes_DoesNotThrow()
    {
        var (qq, _) = Seed();
        _archive.AddMessage(qq, null, 1_700_000_500_000, "hello \"world\" test\\path");
        var result = _repository.Search("\"world\" test\\");
        Assert.NotNull(result);
        var hit = Assert.Single(result.Items);
        Assert.Contains("test\\path", hit.Snippet);
    }

    [Fact]
    public void Search_WithBackslashInPath_ReturnsExpectedHit()
    {
        var (qq, _) = Seed();
        _archive.AddMessage(qq, null, 1_700_000_550_000, @"C:\Users\bob\photo.jpg");
        var result = _repository.Search(@"Users\bob");
        Assert.Equal(SearchMode.Fts, result.Mode);
        var hit = Assert.Single(result.Items);
        Assert.Contains(@"Users\bob", hit.Snippet);
    }

    [Fact]
    public void Search_WithTwoEmojis_FallsBackToLikeAndMatches()
    {
        var (qq, _) = Seed();
        _archive.AddMessage(qq, null, 1_700_000_560_000, "开心 😀😀 好啊");
        var result = _repository.Search("😀😀");
        Assert.Equal(SearchMode.Substring, result.Mode);
        var hit = Assert.Single(result.Items);
        Assert.Contains("😀😀", hit.Snippet);
    }

    [Fact]
    public void Search_WithThreeEmojis_UsesFtsAndMatches()
    {
        var (qq, _) = Seed();
        _archive.AddMessage(qq, null, 1_700_000_570_000, "开心 😀😀😀 好啊");
        var result = _repository.Search("😀😀😀");
        Assert.Equal(SearchMode.Fts, result.Mode);
        var hit = Assert.Single(result.Items);
        Assert.Contains("😀😀😀", hit.Snippet);
    }

    [Fact]
    public void MakeSnippet_WhenQueryNotInContentOrSearchText_DoesNotWipeContent()
    {
        var snippet = SearchRepository.MakeSnippet("正文消息内容在此", "搜索元数据", "未匹配词");
        Assert.Equal("正文消息内容在此", snippet);
    }

    [Fact]
    public void MakeSnippet_WhenQueryInSearchTextOnly_UsesSearchText()
    {
        var snippet = SearchRepository.MakeSnippet("[图片]", "文件名 screenshot_2024.png", "screenshot");
        Assert.Contains("screenshot_2024.png", snippet);
    }

    [Fact]
    public void MakeSnippet_WhenBothEmpty_ReturnsEmpty()
    {
        var snippet = SearchRepository.MakeSnippet("", "", "test");
        Assert.Equal(string.Empty, snippet);
    }

    [Fact]
    public void SqliteLikeHelper_EscapesSpecialCharacters()
    {
        Assert.Equal("//100/%/_", SqliteLikeHelper.EscapeLikePattern("/100%_"));
        Assert.Equal("", SqliteLikeHelper.EscapeLikePattern(null));
        Assert.Equal("", SqliteLikeHelper.EscapeLikePattern(""));
    }

    [Fact]
    public void Search_WithPercentAndUnderscore_MatchesLiterally()
    {
        var (qq, _) = Seed();
        _archive.AddMessage(qq, null, 1_700_000_600_000, "达成 100% 目标");
        _archive.AddMessage(qq, null, 1_700_000_700_000, "达成 1000 目标");
        _archive.AddMessage(qq, null, 1_700_000_800_000, "user_name_test");
        _archive.AddMessage(qq, null, 1_700_000_900_000, "username-test");

        // 2-char query "0%" falls back to LIKE search
        var percentPage = _repository.Search("0%");
        var percentHit = Assert.Single(percentPage.Items);
        Assert.Contains("100%", percentHit.Snippet);

        // 2-char query "r_" falls back to LIKE search
        var underscorePage = _repository.Search("r_");
        var underscoreHit = Assert.Single(underscorePage.Items);
        Assert.Contains("user_name_test", underscoreHit.Snippet);
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
