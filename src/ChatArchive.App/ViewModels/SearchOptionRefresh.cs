namespace ChatArchive.App.ViewModels;

internal readonly record struct SearchOptionRefreshResult(
    long? ConversationId,
    string? MessageType,
    bool ShouldRunSearch);

internal static class SearchOptionRefresh
{
    internal static SearchOptionRefreshResult Restore(
        long? conversationId,
        string? messageType,
        bool hasSearched,
        IReadOnlyList<SearchConversationOption> conversations,
        IReadOnlyList<SearchMessageTypeOption> messageTypes)
    {
        var restoredConversationId = conversations.Any(option => option.Id == conversationId)
            ? conversationId
            : null;
        var restoredMessageType = messageTypes.Any(option => string.Equals(
                option.Value,
                messageType,
                StringComparison.Ordinal))
            ? messageType
            : null;
        var changed = restoredConversationId != conversationId
                      || !string.Equals(restoredMessageType, messageType, StringComparison.Ordinal);
        return new SearchOptionRefreshResult(
            restoredConversationId,
            restoredMessageType,
            hasSearched && changed);
    }
}
