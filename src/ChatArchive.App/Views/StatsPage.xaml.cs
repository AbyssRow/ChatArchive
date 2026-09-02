using System.ComponentModel;
using ChatArchive.App.Navigation;
using ChatArchive.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ChatArchive.App.Views;

public sealed partial class StatsPage : Page, IShellPage
{
    private IAppShell? _shell;
    private StatsViewModel? _stats;
    private bool _attached;
    private bool _loaded;

    public StatsPage()
    {
        InitializeComponent();
    }

    void IShellPage.Attach(IAppShell shell)
    {
        _ = shell;
    }

    internal void Attach(IAppShell shell, StatsViewModel stats)
    {
        if (_attached)
        {
            return;
        }

        _shell = shell;
        _stats = stats;
        stats.PropertyChanged += StatsOnPropertyChanged;
        _attached = true;
    }

    internal void Invalidate()
    {
        _loaded = false;
    }

    public void OnShown()
    {
        if (_loaded || _stats is null)
        {
            return;
        }

        _stats.Load();
        StatsText.Text = _stats.SummaryLines;
        _loaded = true;
    }

    private void StatsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_stats is null)
        {
            return;
        }

        if (e.PropertyName == nameof(StatsViewModel.SummaryLines))
        {
            StatsText.Text = _stats.SummaryLines;
        }
        else if (e.PropertyName == nameof(StatsViewModel.ErrorMessage)
                 && _stats.ErrorMessage.Length > 0)
        {
            _shell!.ShowError(_stats.ErrorMessage);
        }
    }
}
