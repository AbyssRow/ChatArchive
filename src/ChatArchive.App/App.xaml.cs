using Microsoft.UI.Xaml;

namespace ChatArchive.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}\n{e.Exception}\n\n");
        }
        catch
        {
            // 日志失败时忽略，避免二次异常。
        }
    }
}
