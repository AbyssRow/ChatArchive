using System.Collections.ObjectModel;
using ChatArchive.Core.Media;
using ChatArchive.Core.Models;
using ChatArchive.Core.Repositories;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ChatArchive.App.ViewModels;

/// <summary>时间线条目：消息或日期分隔符。</summary>
public abstract class TimelineEntry;

public sealed class DateSeparatorEntry : TimelineEntry
{
    public DateSeparatorEntry(string label)
    {
        Label = label;
    }

    public string Label { get; }
}

public sealed class MessageEntry : TimelineEntry
{
    public MessageEntry(MessageItem message, MediaLocator locator)
    {
        Message = message;
        TimeText = DateTimeOffset
            .FromUnixTimeMilliseconds(message.TimestampMs)
            .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        IsIncoming = message.Direction == "incoming";
        IsOutgoing = message.Direction == "outgoing";
        ImagePath = message.Attachments
            .Where(a => a.Kind is "image" or "sticker" or "emoji" && a.IsAvailable)
            .Select(a => locator.Resolve(a.MediaSha256, a.ManagedPath, a.SourcePath))
            .FirstOrDefault(p => p is not null);
        MissingMediaCount = message.Attachments.Count(a => !a.IsAvailable);
    }

    public MessageItem Message { get; }
    public string TimeText { get; }
    public bool IsIncoming { get; }
    public bool IsOutgoing { get; }
    public string? ImagePath { get; }
    public int MissingMediaCount { get; }
    public string MissingMediaText => $"缺失媒体 ×{MissingMediaCount}";
    public bool HasAttachments => Message.Attachments.Count > 0;
}

public partial class TimelineViewModel : ObservableObject
{
    private readonly ConversationRepository _repository;
    private readonly MediaLocator _mediaLocator;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<TimelineEntry> Entries { get; } = new();

    [ObservableProperty]
    public partial string Title { get; set; } = "选择一个会话";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    private long _conversationId;
    private string? _cursor;
    private string? _lastDateLabel;

    public event Action<MessageEntry>? MessageActivated;

    public TimelineViewModel(
        ConversationRepository repository,
        MediaLocator mediaLocator,
        DispatcherQueue dispatcher)
    {
        _repository = repository;
        _mediaLocator = mediaLocator;
        _dispatcher = dispatcher;
    }

    public void Load(ConversationInfo conversation)
    {
        Title = conversation.Title;
        _conversationId = conversation.Id;
        _cursor = null;
        Entries.Clear();
        _lastDateLabel = null;
        LoadPage(initial: true);
    }

    public void Clear()
    {
        _conversationId = 0;
        Title = "选择一个会话";
        Entries.Clear();
        HasMore = false;
    }

    [RelayCommand]
    private void LoadMore()
    {
        if (HasMore && !IsLoading && _conversationId != 0)
        {
            LoadPage(initial: false);
        }
    }

    public void Activate(MessageEntry entry) => MessageActivated?.Invoke(entry);

    /// <summary>搜索跳转：定位到某条消息并展示其上下文。</summary>
    public void JumpToMessage(long messageId)
    {
        var context = Task.Run(() => _repository.GetMessageContext(messageId)).GetAwaiter().GetResult();
        if (context is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            _cursor = null;
            HasMore = true;
            Title = context.ConversationTitle + "（定位消息）";
            Entries.Clear();
            _lastDateLabel = null;
            AppendWithSeparators(context.Messages);
        });
    }

    private void LoadPage(bool initial)
    {
        var conversationId = _conversationId;
        var cursor = _cursor;
        IsLoading = true;

        Task.Run(() =>
        {
            PageResult<MessageItem> page;
            try
            {
                page = _repository.ListMessages(conversationId, cursor, 80);
            }
            catch (Exception)
            {
                _dispatcher.TryEnqueue(() => IsLoading = false);
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (initial)
                {
                    Entries.Clear();
                }

                AppendWithSeparators(page.Items);
                _cursor = page.NextCursor;
                HasMore = page.NextCursor is not null;
                IsLoading = false;
            });
        });
    }

    private void AppendWithSeparators(IEnumerable<MessageItem> items)
    {
        foreach (var item in items)
        {
            var dateLabel = DateTimeOffset
                .FromUnixTimeMilliseconds(item.TimestampMs)
                .LocalDateTime.ToString("yyyy年M月d日 ddd");
            if (_lastDateLabel != dateLabel)
            {
                Entries.Add(new DateSeparatorEntry(dateLabel));
                _lastDateLabel = dateLabel;
            }

            Entries.Add(new MessageEntry(item, _mediaLocator));
        }
    }
}

