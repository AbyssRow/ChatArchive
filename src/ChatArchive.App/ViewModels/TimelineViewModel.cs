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
    public DateSeparatorEntry(DateOnly date, string label)
    {
        Date = date;
        Label = label;
    }

    public DateOnly Date { get; }
    public string Label { get; }
}

public sealed class MessageEntry : TimelineEntry
{
    public MessageEntry(MessageItem message, MediaLocator locator)
    {
        Message = message;
        TimeText = DateTimeOffset
            .FromUnixTimeMilliseconds(message.TimestampMs)
            .LocalDateTime.ToString("HH:mm:ss");
        IsIncoming = message.Direction == "incoming";
        IsOutgoing = message.Direction == "outgoing";

        var projection = TimelineProjection.ProjectMessage(message, locator);
        DisplayContent = projection.DisplayContent;
        Attachments = projection.Attachments;
        Images = projection.Images;
        OpenableAttachments = projection.OpenableAttachments;
        MissingAttachments = projection.MissingAttachments;
    }

    public MessageItem Message { get; }
    public string TimeText { get; }
    public bool IsIncoming { get; }
    public bool IsOutgoing { get; }
    public string DisplayContent { get; }
    public bool HasDisplayContent => DisplayContent.Length > 0;
    public bool ShowContent => HasDisplayContent && !Message.IsRecalled;
    public IReadOnlyList<AttachmentEntry> Attachments { get; }
    public IReadOnlyList<AttachmentEntry> Images { get; }
    public IReadOnlyList<AttachmentEntry> OpenableAttachments { get; }
    public IReadOnlyList<AttachmentEntry> MissingAttachments { get; }
    public string? ImagePath => Images.FirstOrDefault()?.ResolvedPath;
    public int MissingMediaCount => MissingAttachments.Count;
    public string MissingMediaText => string.Join("\n", MissingAttachments.Select(item => item.MissingText));
    public bool HasMissingMedia => MissingMediaCount > 0;
    public bool HasAttachments => Attachments.Count > 0;

    public string? AvatarPath => Message.CustomAvatarPath;
    public string? AccountBadge => string.IsNullOrWhiteSpace(Message.AccountLabel) ? null : Message.AccountLabel;
    public string Initials => string.IsNullOrWhiteSpace(Message.SenderName) ? "?" : Message.SenderName.Trim().Substring(0, 1);
    public string DisplaySenderName => string.IsNullOrWhiteSpace(AccountBadge) ? Message.SenderName : $"{Message.SenderName} · {AccountBadge}";
}

public partial class TimelineViewModel : ObservableObject
{
    private readonly ConversationRepository _repository;
    private readonly MediaLocator _mediaLocator;
    private readonly DispatcherQueue _dispatcher;
    private readonly TimelineRequestState _requestState = new();

    public ObservableCollection<TimelineEntry> Entries { get; } = new();

    [ObservableProperty]
    public partial string Title { get; set; } = "选择一个会话";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public event Action<MessageEntry>? MessageActivated;
    public event Action? InitialPageLoaded;
    public event Action<long>? FocusMessageLoaded;

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
        ErrorMessage = string.Empty;
        HasMore = false;
        Entries.Clear();
        var request = _requestState.StartConversation(conversation.Id);
        LoadPage(request, initial: true);
    }

    public void Clear()
    {
        _requestState.Clear();
        Title = "选择一个会话";
        ErrorMessage = string.Empty;
        Entries.Clear();
        HasMore = false;
        IsLoading = false;
    }

    [RelayCommand]
    private void LoadMore()
    {
        var request = _requestState.Current;
        if (HasMore && !IsLoading && request.ConversationId != 0)
        {
            LoadPage(request, initial: false);
        }
    }

    public void Activate(MessageEntry entry) => MessageActivated?.Invoke(entry);

    /// <summary>搜索跳转：定位到某条消息并展示其上下文。</summary>
    public void JumpToMessage(long messageId)
    {
        _requestState.Clear();
        var lookupRequest = _requestState.Current;
        IsLoading = true;
        ErrorMessage = string.Empty;
        Task.Run(() => _repository.GetMessageContext(messageId)).ContinueWith(task =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!_requestState.IsCurrent(lookupRequest))
                {
                    return;
                }

                IsLoading = false;
                if (!task.IsCompletedSuccessfully)
                {
                    var message = task.Exception?.GetBaseException().Message ?? "未知错误";
                    ErrorMessage = $"定位消息失败：{message}";
                    return;
                }

                var context = task.Result;
                if (context is null)
                {
                    ErrorMessage = "未找到目标消息";
                    return;
                }

                var contextRequest = _requestState.StartContext(context);
                HasMore = contextRequest.Cursor is not null;
                Title = context.ConversationTitle + "（定位消息）";
                Entries.Clear();
                AppendWithSeparators(context.Messages);
                FocusMessageLoaded?.Invoke(context.FocusMessageId);
            });
        });
    }

    private void LoadPage(TimelineRequest request, bool initial)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        Task.Run(() =>
        {
            PageResult<MessageItem> page;
            try
            {
                page = _repository.ListMessages(request.ConversationId, request.Cursor, 80);
            }
            catch (Exception ex)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_requestState.IsCurrent(request))
                    {
                        ErrorMessage = $"加载聊天记录失败：{ex.Message}";
                        IsLoading = false;
                    }
                });
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (!_requestState.IsCurrent(request))
                {
                    return;
                }

                if (initial)
                {
                    Entries.Clear();
                    AppendWithSeparators(page.Items);
                }
                else
                {
                    TimelineProjection.PrependOlder(Entries, page.Items, _mediaLocator);
                }

                _requestState.UpdateCursor(page.NextCursor);
                HasMore = page.NextCursor is not null;
                IsLoading = false;
                if (initial)
                {
                    InitialPageLoaded?.Invoke();
                }
            });
        });
    }

    private void AppendWithSeparators(IEnumerable<MessageItem> items)
    {
        foreach (var entry in TimelineProjection.BuildEntries(items, _mediaLocator))
        {
            Entries.Add(entry);
        }
    }
}

