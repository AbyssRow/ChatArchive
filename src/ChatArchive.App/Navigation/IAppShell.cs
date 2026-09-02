namespace ChatArchive.App.Navigation;

internal interface IAppShell : IAppNavigator
{
    void ShowError(string message);
    nint WindowHandle { get; }
    bool IsPickerReady { get; }
}
