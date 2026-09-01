using System.Collections.ObjectModel;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

internal sealed record SearchOptionsSnapshot(
    IReadOnlyList<ConversationInfo> Conversations,
    FilterOptions Filters);

public partial class SearchViewModel : ObservableObject
{
    private readonly SearchRepository _repository;
    private readonly DispatcherQueue? _dispatcher;
    private readonly Func<Task<SearchOptionsSnapshot>> _optionsLoader;
    private long _optionsGeneration;
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
    public event Action<long, bool>? OptionsReloaded;

    public SearchViewModel(
        SearchRepository repository,
        ConversationRepository conversations,
        DispatcherQueue? dispatcher = null)
        : this(
            repository,
            () => Task.Run(() => new SearchOptionsSnapshot(
                conversations.ListConversations(limit: 1000),
                repository.GetFilterOptions())),
            dispatcher)
    {
    }

    internal SearchViewModel(
        SearchRepository repository,
        Func<Task<SearchOptionsSnapshot>> optionsLoader,
        DispatcherQueue? dispatcher = null)
    {
        _repository = repository;
        _optionsLoader = optionsLoader;
        _dispatcher = dispatcher;
        ConversationOptions.Add(new SearchConversationOption(null, "全部会话"));
        MessageTypeOptions.Add(new SearchMessageTypeOption(null, "全部消息类型"));
    }

    public long LoadOptions()
    {
        var generation = Interlocked.Increment(ref _optionsGeneration);
        Task<SearchOptionsSnapshot> loadTask;
        try
        {
            loadTask = _optionsLoader();
        }
        catch (Exception ex)
        {
            loadTask = Task.FromException<SearchOptionsSnapshot>(ex);
        }

        _ = loadTask.ContinueWith(
            completed => PostOptionsResult(generation, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return generation;
    }

    private void PostOptionsResult(long generation, Task<SearchOptionsSnapshot> completed)
    {
        void Apply()
        {
            if (generation != Interlocked.Read(ref _optionsGeneration))
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }
                return;
            }

            if (!completed.IsCompletedSuccessfully)
            {
                var message = completed.Exception?.GetBaseException().Message
                              ?? (completed.IsCanceled ? "操作已取消" : "未知错误");
                ErrorMessage = $"加载搜索筛选项失败：{message}";
                OptionsReloaded?.Invoke(generation, false);
                return;
            }

            var conversations = new List<SearchConversationOption> { new(null, "全部会话") };
            foreach (var conversation in completed.Result.Conversations)
            {
                var platform = conversation.Platform?.ToLowerInvariant() switch
                {
                    "qq" => "QQ",
                    "wechat" => "微信",
                    "text" => "文本",
                    "html" => "网页",
                    "sql" => "SQL",
                    _ => conversation.Platform ?? string.Empty,
                };
                conversations.Add(new(conversation.Id, $"{platform} · {conversation.Title}"));
            }

            var messageTypes = new List<SearchMessageTypeOption> { new(null, "全部消息类型") };
            foreach (var option in completed.Result.Filters.MessageTypes)
            {
                messageTypes.Add(new(option.Value, MessageTypeLabel(option.Value, option.Amount)));
            }

            ConversationOptions.Clear();
            foreach (var option in conversations)
            {
                ConversationOptions.Add(option);
            }
            MessageTypeOptions.Clear();
            foreach (var option in messageTypes)
            {
                MessageTypeOptions.Add(option);
            }

            ErrorMessage = string.Empty;
            OptionsReloaded?.Invoke(generation, true);
        }

        if (_dispatcher is null)
        {
            Apply();
        }
        else
        {
            _ = _dispatcher.TryEnqueue(Apply);
        }
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
        if (value.Length == 0)
        {
            IsLoading = false;
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
                void OnError()
                {
                    if (_requestState.IsCurrent(request))
                    {
                        ErrorMessage = $"搜索失败：{ex.Message}";
                        IsLoading = false;
                    }
                }

                if (_dispatcher is not null)
                {
                    _dispatcher.TryEnqueue(OnError);
                }
                else
                {
                    OnError();
                }
                return;
            }

            void OnSuccess()
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
            }

            if (_dispatcher is not null)
            {
                _dispatcher.TryEnqueue(OnSuccess);
            }
            else
            {
                OnSuccess();
            }
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

