namespace ChatArchive.App.Navigation;

internal readonly record struct ConversationNavigationArgs(
    long ConversationId,
    long? FocusMessageId);
