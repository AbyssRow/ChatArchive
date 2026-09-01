using ChatArchive.App.ViewModels;
using ChatArchive.Core.Data;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class UiInputTests
{
    [Theory]
    [InlineData("", null, null)]
    [InlineData("qq|private", "qq", "private")]
    [InlineData("wechat|group", "wechat", "group")]
    [InlineData("qq", null, null)]
    [InlineData("qq|group|extra", null, null)]
    public void Conversation_filter_tag_is_parsed_without_index_errors(
        string tag,
        string? expectedPlatform,
        string? expectedKind)
    {
        var result = UiInputParser.ParseConversationFilter(tag);

        Assert.Equal(expectedPlatform, result.Platform);
        Assert.Equal(expectedKind, result.Kind);
    }

    [Theory]
    [InlineData(@"E:\media\photo.jpg", ".jpg")]
    [InlineData(@"E:\media\image", ".png")]
    [InlineData(@"E:\media\photo.", ".png")]
    public void Picker_extension_always_starts_with_dot(string path, string expected)
    {
        Assert.Equal(expected, UiInputParser.PickerExtension(path));
    }

    [Fact]
    public void Latest_request_gate_rejects_older_versions()
    {
        var gate = new LatestRequestGate();
        var first = gate.Next();
        var second = gate.Next();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void Latest_request_gate_can_invalidate_a_pending_action_without_replacing_it()
    {
        var gate = new LatestRequestGate();
        var pending = gate.Next();

        gate.Invalidate();

        Assert.False(gate.IsCurrent(pending));
    }

    [Fact]
    public void SearchViewModel_QueryCleared_ResetsIsLoadingAndState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"chatarchive-searchvm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var db = new ArchiveDatabase(Path.Combine(tempDir, "test.db"));
            db.EnsureSchema();
            var searchRepo = new SearchRepository(db);
            var convRepo = new ConversationRepository(db);

            var vm = new SearchViewModel(searchRepo, convRepo)
            {
                Query = "initial query",
                IsLoading = true,
                HasSearched = true,
                HasMore = true,
                ErrorMessage = "Previous error",
                ModeLabel = "全文索引"
            };
            vm.Results.Add(new SearchHitProxy(new SearchHit(1, 1, "t", "qq", "private", null, "s", "snip", "text", "incoming", 1700000000000)));

            // Clearing query resets IsLoading and state
            vm.Query = string.Empty;

            Assert.False(vm.IsLoading);
            Assert.False(vm.HasSearched);
            Assert.False(vm.HasMore);
            Assert.Empty(vm.ErrorMessage);
            Assert.Empty(vm.ModeLabel);
            Assert.Empty(vm.Results);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void SearchViewModel_QueryClearedWhileInFlight_ResetsIsLoading()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"chatarchive-searchvm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var db = new ArchiveDatabase(Path.Combine(tempDir, "test.db"));
            db.EnsureSchema();
            var searchRepo = new SearchRepository(db);
            var convRepo = new ConversationRepository(db);

            var vm = new SearchViewModel(searchRepo, convRepo)
            {
                Query = "some query",
                IsLoading = true,
                HasSearched = false
            };

            vm.Query = "";

            Assert.False(vm.IsLoading);
            Assert.False(vm.HasSearched);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
