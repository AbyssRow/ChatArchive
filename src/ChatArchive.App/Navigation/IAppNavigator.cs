namespace ChatArchive.App.Navigation;

internal interface IAppNavigator
{
    void GoTo(AppSection section);
    void OpenConversation(long conversationId, long? focusMessageId = null);
}
