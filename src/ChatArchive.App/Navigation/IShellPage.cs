namespace ChatArchive.App.Navigation;

internal interface IShellPage
{
    void Attach(IAppShell shell);
    void OnShown();
}
