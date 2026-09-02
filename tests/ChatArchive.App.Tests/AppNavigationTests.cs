using ChatArchive.App.Navigation;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class AppNavigationTests
{
    [Fact]
    public void ForSidebar_same_section_does_not_navigate()
    {
        var decision = AppNavigation.ForSidebar(AppSection.Search, AppSection.Search);
        Assert.False(decision.ShouldNavigate);
        Assert.Equal(AppSection.Search, decision.Section);
        Assert.Equal(AppNavigation.SearchPageTypeName, decision.PageTypeName);
    }

    [Fact]
    public void ForSidebar_search_to_conversations_navigates()
    {
        var decision = AppNavigation.ForSidebar(AppSection.Search, AppSection.Conversations);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(AppSection.Conversations, decision.Section);
        Assert.Equal(AppNavigation.ConversationsPageTypeName, decision.PageTypeName);
    }

    [Fact]
    public void ForSidebar_any_section_to_settings_uses_settings_page_name()
    {
        var decision = AppNavigation.ForSidebar(AppSection.Stats, AppSection.Settings);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(AppNavigation.SettingsPageTypeName, decision.PageTypeName);
    }

    [Fact]
    public void ForOpenConversation_when_already_on_conversations_does_not_navigate()
    {
        var decision = AppNavigation.ForOpenConversation(AppSection.Conversations, 42, 99);
        Assert.False(decision.ShouldNavigate);
        Assert.Equal(AppSection.Conversations, decision.Section);
        Assert.Equal(AppNavigation.ConversationsPageTypeName, decision.PageTypeName);
        Assert.Equal(42, decision.Args.ConversationId);
        Assert.Equal(99, decision.Args.FocusMessageId);
    }

    [Fact]
    public void ForOpenConversation_from_search_navigates_with_ids()
    {
        var decision = AppNavigation.ForOpenConversation(AppSection.Search, 7, 8);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(7, decision.Args.ConversationId);
        Assert.Equal(8, decision.Args.FocusMessageId);
    }

    [Fact]
    public void ForOpenConversation_from_contacts_has_null_focus_message()
    {
        var decision = AppNavigation.ForOpenConversation(AppSection.Contacts, 3, null);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(3, decision.Args.ConversationId);
        Assert.Null(decision.Args.FocusMessageId);
    }
}
