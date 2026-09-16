namespace ChatArchive.App.Navigation;

internal interface IAppShell
{
    void GoTo(AppSection section);
    void OpenConversation(long conversationId, long? focusMessageId = null);
    void ShowError(string message);
    nint WindowHandle { get; }
    bool IsPickerReady { get; }
}
