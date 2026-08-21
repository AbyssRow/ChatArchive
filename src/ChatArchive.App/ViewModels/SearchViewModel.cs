using System.Collections.ObjectModel;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

public partial class SearchViewModel : ObservableObject
{
    private readonly SearchRepository _repository;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<SearchHitProxy> Results { get; } = new();

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSearched { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ModeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? PlatformFilter { get; set; }

    [ObservableProperty]
    public partial string? KindFilter { get; set; }

    [ObservableProperty]
    public partial string? SenderFilter { get; set; }

    private string? _cursor;

    public event Action<SearchHit>? ResultActivated;

    public SearchViewModel(SearchRepository repository, DispatcherQueue dispatcher)
    {
        _repository = repository;
        _dispatcher = dispatcher;
    }

    [RelayCommand]
    private void Execute()
    {
        _cursor = null;
        Results.Clear();
        RunPage(reset: true);
    }

    [RelayCommand]
    private void LoadMore() => RunPage(reset: false);

    public void NotifyResultActivated(SearchHit hit) => ResultActivated?.Invoke(hit);

    partial void OnQueryChanged(string value)
    {
        if (value.Length == 0 && HasSearched)
        {
            HasSearched = false;
            Results.Clear();
            ModeLabel = string.Empty;
            _cursor = null;
        }
    }

    private void RunPage(bool reset)
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        IsLoading = true;
        var queryText = Query.Trim();
        var filter = new SearchFilter(
            Platform: EmptyToNull(PlatformFilter),
            Kind: EmptyToNull(KindFilter),
            Sender: EmptyToNull(SenderFilter));
        var cursor = reset ? null : _cursor;

        Task.Run(() =>
        {
            SearchHitPage page;
            try
            {
                page = _repository.Search(queryText, filter, cursor, 60);
            }
            catch (Exception)
            {
                _dispatcher.TryEnqueue(() => IsLoading = false);
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                foreach (var hit in page.Items)
                {
                    Results.Add(new SearchHitProxy(hit));
                }

                ModeLabel = page.Mode switch
                {
                    SearchMode.Fts => "全文索引",
                    SearchMode.Substring => "子串匹配",
                    _ => string.Empty,
                };
                HasSearched = true;
                _cursor = page.NextCursor;
                IsLoading = false;
            });
        });
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

