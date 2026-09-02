using System.ComponentModel;
using ChatArchive.App.Navigation;
using ChatArchive.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ChatArchive.App.Views;

public sealed partial class SearchPage : Page, IShellPage
{
    private IAppShell? _shell;
    private SearchViewModel? _search;
    private bool _attached;
    private readonly SearchOptionsReloadGate _searchOptionsReloadGate = new();
    private PendingSearchOptionsReload? _pendingSearchOptionsReload;

    private sealed record PendingSearchOptionsReload(
        long Generation,
        long? ConversationId,
        string? MessageType,
        bool HasSearched);

    public SearchPage()
    {
        InitializeComponent();
    }

    void IShellPage.Attach(IAppShell shell)
    {
        _ = shell;
    }

    internal void Attach(IAppShell shell, SearchViewModel search)
    {
        if (_attached)
        {
            return;
        }

        _shell = shell;
        _search = search;
        SearchResultsList.ItemsSource = search.Results;
        SearchConversationCombo.ItemsSource = search.ConversationOptions;
        SearchMessageTypeCombo.ItemsSource = search.MessageTypeOptions;
        search.OptionsReloaded += SearchOptions_Reloaded;
        search.PropertyChanged += SearchOnPropertyChanged;
        ReloadOptions();
        _attached = true;
    }

    public void OnShown()
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    internal void ReloadOptions()
    {
        long? conversationId = SearchConversationCombo.SelectedValue is long id ? id : null;
        var messageType = SearchMessageTypeCombo.SelectedValue as string;
        _searchOptionsReloadGate.Begin();
        SetSearchInteractionEnabled(false);

        long generation;
        try
        {
            generation = _search!.LoadOptions();
        }
        catch
        {
            _pendingSearchOptionsReload = null;
            _searchOptionsReloadGate.CancelPending();
            SetSearchInteractionEnabled(true);
            throw;
        }

        _searchOptionsReloadGate.Own(generation);
        _pendingSearchOptionsReload = new PendingSearchOptionsReload(
            generation,
            conversationId,
            messageType,
            _search.HasSearched);
    }

    private void SearchOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_search is null)
        {
            return;
        }

        if (e.PropertyName == nameof(SearchViewModel.IsLoading))
        {
            SearchProgress.Visibility = _search.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            if (!_search.IsLoading && _search.HasSearched)
            {
                UpdateSearchSummary();
            }
        }
        else if (e.PropertyName is nameof(SearchViewModel.HasSearched)
                 or nameof(SearchViewModel.HasMore))
        {
            SearchLoadMore.Visibility = _search.HasMore
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateSearchSummary();
        }
        else if (e.PropertyName == nameof(SearchViewModel.ErrorMessage)
                 && _search.ErrorMessage.Length > 0)
        {
            _shell!.ShowError(_search.ErrorMessage);
        }
    }

    private void SearchOptions_Reloaded(long generation, bool success)
    {
        if (_pendingSearchOptionsReload is not { } pending
            || pending.Generation != generation)
        {
            return;
        }

        var shouldRunSearch = false;
        var interactionReleased = false;
        try
        {
            if (!success)
            {
                return;
            }

            var restored = SearchOptionRefresh.Restore(
                pending.ConversationId,
                pending.MessageType,
                pending.HasSearched,
                _search!.ConversationOptions,
                _search.MessageTypeOptions);
            SearchConversationCombo.SelectedItem = _search.ConversationOptions.First(option =>
                option.Id == restored.ConversationId);
            SearchMessageTypeCombo.SelectedItem = _search.MessageTypeOptions.First(option =>
                string.Equals(option.Value, restored.MessageType, StringComparison.Ordinal));
            shouldRunSearch = restored.ShouldRunSearch;
        }
        finally
        {
            interactionReleased = _searchOptionsReloadGate.TryRelease(generation);
            if (interactionReleased)
            {
                _pendingSearchOptionsReload = null;
                SetSearchInteractionEnabled(true);
            }
        }

        if (interactionReleased && shouldRunSearch)
        {
            RunSearch();
        }
    }

    private void SetSearchInteractionEnabled(bool isEnabled)
    {
        SearchBox.IsEnabled = isEnabled;
        SearchButton.IsEnabled = isEnabled;
        SearchPlatformCombo.IsEnabled = isEnabled;
        SearchKindCombo.IsEnabled = isEnabled;
        SearchSenderBox.IsEnabled = isEnabled;
        SearchConversationCombo.IsEnabled = isEnabled;
        SearchMessageTypeCombo.IsEnabled = isEnabled;
        SearchDateFromPicker.IsEnabled = isEnabled;
        SearchDateToPicker.IsEnabled = isEnabled;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_searchOptionsReloadGate.IsLocked)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            RunSearch();
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_searchOptionsReloadGate.IsLocked)
        {
            return;
        }

        RunSearch();
    }

    private void SearchFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_searchOptionsReloadGate.IsLocked || _search is null || !_search.HasSearched)
        {
            return;
        }

        RunSearch();
    }

    private void SearchFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_searchOptionsReloadGate.IsLocked)
        {
            return;
        }

        SearchFilter_Changed(sender, e);
    }

    private void SearchDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_searchOptionsReloadGate.IsLocked)
        {
            return;
        }

        if (_search is not null && _search.HasSearched)
        {
            RunSearch();
        }
    }

    private void OnSearchLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _search!.LoadMoreCommand.Execute(null);
    }

    private void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchHitProxy proxy)
        {
            _shell!.OpenConversation(proxy.Hit.ConversationId, proxy.Hit.MessageId);
        }
    }

    private void RunSearch()
    {
        if (_searchOptionsReloadGate.IsLocked)
        {
            return;
        }

        _search!.Query = SearchBox.Text;
        _search.PlatformFilter = ComboTag(SearchPlatformCombo);
        _search.KindFilter = ComboTag(SearchKindCombo);
        _search.SenderFilter = SearchSenderBox.Text;
        _search.ConversationFilter = SearchConversationCombo.SelectedValue is long conversationId
            ? conversationId
            : null;
        _search.MessageTypeFilter = SearchMessageTypeCombo.SelectedValue as string;
        _search.DateFrom = SearchDateFromPicker.Date;
        _search.DateTo = SearchDateToPicker.Date;
        _search.ExecuteCommand.Execute(null);
    }

    private void UpdateSearchSummary()
    {
        SearchModeLabel.Text = _search!.Results.Count > 0
            ? $"已加载 {_search.Results.Count:N0} 条（{_search.ModeLabel}）"
            : _search.ModeLabel;
    }

    private static string ComboTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    }
}
