using System.Collections.ObjectModel;
using ChatArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

public partial class ConversationListViewModel : ObservableObject
{
    private readonly ChatArchive.Core.Repositories.ConversationRepository _repository;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ConversationInfo> Conversations { get; } = new();

    [ObservableProperty]
    public partial string? PlatformFilter { get; set; }

    [ObservableProperty]
    public partial string? KindFilter { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ConversationInfo? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public event Action<ConversationInfo>? ConversationActivated;

    public ConversationListViewModel(
        ChatArchive.Core.Repositories.ConversationRepository repository,
        DispatcherQueue dispatcher)
    {
        _repository = repository;
        _dispatcher = dispatcher;
    }

    public void Reload()
    {
        var platform = PlatformFilter;
        var kind = KindFilter;
        var query = Query;
        Task.Run(() =>
        {
            var items = _repository.ListConversations(
                Normalize(platform), Normalize(kind),
                string.IsNullOrWhiteSpace(query) ? null : query.Trim());
            _dispatcher.TryEnqueue(() =>
            {
                Conversations.Clear();
                foreach (var item in items)
                {
                    Conversations.Add(item);
                }
            });
        });
    }

    partial void OnSelectedConversationChanged(ConversationInfo? value)
    {
        if (value is not null)
        {
            ConversationActivated?.Invoke(value);
        }
    }

    [RelayCommand]
    private void Refresh() => Reload();

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }
}
