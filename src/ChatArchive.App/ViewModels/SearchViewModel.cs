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
    private readonly ConversationRepository _conversations;
    private readonly DispatcherQueue _dispatcher;
    private readonly SearchRequestState _requestState = new();

    public ObservableCollection<SearchHitProxy> Results { get; } = new();
    public ObservableCollection<SearchConversationOption> ConversationOptions { get; } = new();
    public ObservableCollection<SearchMessageTypeOption> MessageTypeOptions { get; } = new();

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

    [ObservableProperty]
    public partial long? ConversationFilter { get; set; }

    [ObservableProperty]
    public partial string? MessageTypeFilter { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DateFrom { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DateTo { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public event Action<SearchHit>? ResultActivated;

    public SearchViewModel(
        SearchRepository repository,
        ConversationRepository conversations,
        DispatcherQueue dispatcher)
    {
        _repository = repository;
        _conversations = conversations;
        _dispatcher = dispatcher;
        ConversationOptions.Add(new SearchConversationOption(null, "全部会话"));
        MessageTypeOptions.Add(new SearchMessageTypeOption(null, "全部消息类型"));
    }

    public void LoadOptions()
    {
        Task.Run(() => (
            Conversations: _conversations.ListConversations(limit: 1000),
            Filters: _repository.GetFilterOptions())).ContinueWith(task =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    ErrorMessage = "加载搜索筛选项失败";
                    return;
                }

                ConversationOptions.Clear();
                ConversationOptions.Add(new SearchConversationOption(null, "全部会话"));
                foreach (var conversation in task.Result.Conversations)
                {
                    var platform = conversation.Platform == "wechat" ? "微信" : "QQ";
                    ConversationOptions.Add(new SearchConversationOption(
                        conversation.Id,
                        $"{platform} · {conversation.Title}"));
                }

                MessageTypeOptions.Clear();
                MessageTypeOptions.Add(new SearchMessageTypeOption(null, "全部消息类型"));
                foreach (var option in task.Result.Filters.MessageTypes)
                {
                    MessageTypeOptions.Add(new SearchMessageTypeOption(
                        option.Value,
                        MessageTypeLabel(option.Value, option.Amount)));
                }
            });
        });
    }

    [RelayCommand]
    private void Execute()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        Results.Clear();
        HasMore = false;
        ErrorMessage = string.Empty;
        var filter = SearchFilterBuilder.Build(
            PlatformFilter,
            KindFilter,
            ConversationFilter,
            SenderFilter,
            MessageTypeFilter,
            DateFrom,
            DateTo);
        var request = _requestState.Start(Query.Trim(), filter);
        RunPage(request);
    }

    [RelayCommand]
    private void LoadMore()
    {
        if (!IsLoading && _requestState.Continue() is { } request)
        {
            RunPage(request);
        }
    }

    public void NotifyResultActivated(SearchHit hit) => ResultActivated?.Invoke(hit);

    partial void OnQueryChanged(string value)
    {
        if (value.Length == 0 && HasSearched)
        {
            HasSearched = false;
            Results.Clear();
            ModeLabel = string.Empty;
            HasMore = false;
            ErrorMessage = string.Empty;
            _requestState.Clear();
        }
    }

    private void RunPage(SearchRequest request)
    {
        IsLoading = true;

        Task.Run(() =>
        {
            SearchHitPage page;
            try
            {
                page = _repository.Search(request.Query, request.Filter, request.Cursor, 60);
            }
            catch (Exception ex)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_requestState.IsCurrent(request))
                    {
                        ErrorMessage = $"搜索失败：{ex.Message}";
                        IsLoading = false;
                    }
                });
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (!_requestState.ApplyPage(request, page.NextCursor))
                {
                    return;
                }

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
                HasMore = _requestState.HasMore;
                IsLoading = false;
            });
        });
    }

    private static string MessageTypeLabel(string value, long amount)
    {
        var label = value switch
        {
            "text" => "文本",
            "image" => "图片",
            "file" => "文件",
            "video" => "视频",
            "audio" or "voice" => "语音",
            "emoji" or "sticker" => "表情",
            "reply" => "引用",
            "system" => "系统消息",
            _ => value,
        };
        return $"{label}（{amount:N0}）";
    }
}

public sealed record SearchConversationOption(long? Id, string Label);

public sealed record SearchMessageTypeOption(string? Value, string Label);

