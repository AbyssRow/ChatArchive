namespace ChatArchive.App.Navigation;

internal readonly record struct AppSectionDecision(
    bool ShouldNavigate,
    AppSection Section,
    string PageTypeName);

internal readonly record struct ConversationOpenDecision(
    bool ShouldNavigate,
    AppSection Section,
    string PageTypeName,
    ConversationNavigationArgs Args);

internal static class AppNavigation
{
    public const string ConversationsPageTypeName = "ConversationsPage";
    public const string ContactsPageTypeName = "ContactsPage";
    public const string SearchPageTypeName = "SearchPage";
    public const string StatsPageTypeName = "StatsPage";
    public const string SettingsPageTypeName = "SettingsPage";

    public static AppSectionDecision ForSidebar(AppSection current, AppSection target)
    {
        var pageTypeName = PageTypeName(target);
        return new AppSectionDecision(
            ShouldNavigate: current != target,
            Section: target,
            PageTypeName: pageTypeName);
    }

    public static ConversationOpenDecision ForOpenConversation(
        AppSection current,
        long conversationId,
        long? focusMessageId)
    {
        var args = new ConversationNavigationArgs(conversationId, focusMessageId);
        return new ConversationOpenDecision(
            ShouldNavigate: current != AppSection.Conversations,
            Section: AppSection.Conversations,
            PageTypeName: ConversationsPageTypeName,
            Args: args);
    }

    public static string PageTypeName(AppSection section) => section switch
    {
        AppSection.Conversations => ConversationsPageTypeName,
        AppSection.Contacts => ContactsPageTypeName,
        AppSection.Search => SearchPageTypeName,
        AppSection.Stats => StatsPageTypeName,
        AppSection.Settings => SettingsPageTypeName,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };
}
