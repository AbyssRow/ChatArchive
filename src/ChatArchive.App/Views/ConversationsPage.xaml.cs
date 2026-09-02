using System.ComponentModel;
using ChatArchive.App.Navigation;
using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ChatArchive.App.Views;

public sealed partial class ConversationsPage : Page, IShellPage
{
    private IAppShell? _shell;
    private ConversationListViewModel? _conversations;
    private TimelineViewModel? _timeline;
    private bool _attached;
    private readonly LatestRequestGate _activationGate = new();
    private bool _isApplyingConversation;
    private readonly TimelineInitialPositionState _initialTimelinePosition = new();
    private CancellationTokenSource? _queryDebounce;
    private ScrollViewer? _messageScroll;
    private bool _messagePagingReady;
    private readonly ExclusiveInteractionGate _senderProfileGate = new();

    public ConversationsPage()
    {
        InitializeComponent();
    }

    void IShellPage.Attach(IAppShell shell)
    {
        _ = shell;
    }

    internal void Attach(
        IAppShell shell,
        ConversationListViewModel conversations,
        TimelineViewModel timeline)
    {
        if (_attached)
        {
            return;
        }

        _shell = shell;
        _conversations = conversations;
        _timeline = timeline;
        ConversationListControl.ItemsSource = conversations.Conversations;
        MessageListControl.ItemsSource = timeline.Entries;
        conversations.ConversationActivated += info => timeline.Load(info);
        timeline.InitialPageLoaded += PositionTimelineAtBottom;
        timeline.FocusMessageLoaded += FocusTimelineMessage;
        timeline.PropertyChanged += TimelineOnPropertyChanged;
        conversations.PropertyChanged += ConversationsOnPropertyChanged;
        HookMessageScroll();
        _attached = true;
    }

    public void OnShown()
    {
    }

    internal void ApplyConversation(ConversationNavigationArgs args)
    {
        var request = _activationGate.Next();
        _ = Task.Run(() => AppServices.Instance.Conversations.GetConversation(args.ConversationId))
            .ContinueWith(task =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_activationGate.IsCurrent(request))
                    {
                        return;
                    }

                    if (!task.IsCompletedSuccessfully || task.Result?.Conversation is not { } info)
                    {
                        var message = args.FocusMessageId.HasValue
                            ? "打开搜索结果失败：未找到对应会话"
                            : "打开会话失败：未找到对应会话";
                        _shell!.ShowError(
                            task.Exception is { } ex
                                ? $"{(args.FocusMessageId.HasValue ? "打开搜索结果失败" : "打开会话失败")}：{ex.GetBaseException().Message}"
                                : message);
                        return;
                    }

                    _isApplyingConversation = true;
                    try
                    {
                        _conversations!.Activate(info);
                        ConversationListControl.SelectedItem =
                            _conversations.Conversations.FirstOrDefault(c => c.Id == info.Id) ?? info;
                        if (args.FocusMessageId is { } messageId)
                        {
                            _timeline!.JumpToMessage(messageId);
                        }
                    }
                    finally
                    {
                        _isApplyingConversation = false;
                    }
                });
            });
    }

    private void ConversationsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationListViewModel.ErrorMessage)
            && _conversations is { ErrorMessage.Length: > 0 })
        {
            _shell!.ShowError(_conversations.ErrorMessage);
        }
    }

    private void TimelineOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_timeline is null)
        {
            return;
        }

        if (e.PropertyName == nameof(TimelineViewModel.IsLoading))
        {
            LoadMoreBar.Visibility = _timeline.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(TimelineViewModel.Title))
        {
            TimelineTitle.Text = _timeline.Title;
        }
        else if (e.PropertyName == nameof(TimelineViewModel.ErrorMessage)
                 && _timeline.ErrorMessage.Length > 0)
        {
            _shell!.ShowError(_timeline.ErrorMessage);
        }
    }

    private void ConversationQuery_TextChanged(object sender, TextChangedEventArgs e)
    {
        _queryDebounce?.Cancel();
        _queryDebounce = new CancellationTokenSource();
        var token = _queryDebounce.Token;
        _ = Task.Delay(300, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _conversations!.Query = ConversationQueryBox.Text;
                    _conversations.Reload();
                });
            }
        });
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_conversations is null)
        {
            return;
        }

        if (FilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tags)
        {
            var filter = UiInputParser.ParseConversationFilter(tags);
            _conversations.PlatformFilter = filter.Platform;
            _conversations.KindFilter = filter.Kind;
            _conversations.Reload();
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingConversation)
        {
            _activationGate.Invalidate();
        }

        if (_conversations is null)
        {
            return;
        }

        if (ConversationListControl.SelectedItem is ConversationInfo conversation)
        {
            _conversations.Activate(conversation);
        }
    }

    private void HookMessageScroll()
    {
        MessageListControl.Loaded += (_, _) =>
        {
            if (_messageScroll is null)
            {
                _messageScroll = FindScrollViewer(MessageListControl);
                if (_messageScroll is not null)
                {
                    _messageScroll.ViewChanged += MessageScroll_ViewChanged;
                }
            }

            TryPositionTimelineAtBottom();
        };
    }

    private void MessageScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_messageScroll is null || e.IsIntermediate || _timeline is null)
        {
            return;
        }

        if (_messagePagingReady
            && _messageScroll.VerticalOffset < 80
            && _timeline.HasMore
            && !_timeline.IsLoading)
        {
            _timeline.LoadMoreCommand.Execute(null);
        }
    }

    private void PositionTimelineAtBottom()
    {
        _initialTimelinePosition.RequestBottom();
        _messagePagingReady = false;
        TryPositionTimelineAtBottom();
    }

    private void TryPositionTimelineAtBottom()
    {
        var last = _timeline?.Entries.LastOrDefault();
        if (!_initialTimelinePosition.TryTakeBottomRequest(
                canPosition: _messageScroll is not null && last is not null))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            MessageListControl.UpdateLayout();
            MessageListControl.ScrollIntoView(last);

            DispatcherQueue.TryEnqueue(() =>
            {
                _messageScroll?.ChangeView(null, _messageScroll.ScrollableHeight, null, true);
                _messagePagingReady = true;
            });
        });
    }

    private void FocusTimelineMessage(long messageId)
    {
        _messagePagingReady = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            MessageListControl.UpdateLayout();
            var entry = _timeline!.Entries
                .OfType<MessageEntry>()
                .FirstOrDefault(item => item.Message.Id == messageId);
            if (entry is not null)
            {
                MessageListControl.ScrollIntoView(entry);
            }

            _messagePagingReady = true;
        });
    }

    private async void OnSenderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageEntry entry }
            && entry.Message.SenderId is long senderId
            && _senderProfileGate.TryEnter())
        {
            try
            {
                await SenderProfileDialog.ShowAsync(XamlRoot, senderId, _shell!, () => _conversations!.Reload());
            }
            catch (Exception ex)
            {
                _shell!.ShowError($"查看发送者信息失败: {ex.Message}");
            }
            finally
            {
                _senderProfileGate.Exit();
            }
        }
    }

    private async void OnImageAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AttachmentEntry entry }
            && entry.ResolvedPath is not null)
        {
            try
            {
                await ShowImagePreview(entry.ResolvedPath, entry.PreviewTitle);
            }
            catch (Exception ex)
            {
                _shell!.ShowError($"查看图片预览失败: {ex.Message}");
            }
        }
    }

    private async void OnAttachmentOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentEntry entry }
            || entry.ResolvedPath is null)
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(entry.ResolvedPath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                _shell!.ShowError("系统没有可打开此附件的应用");
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"打开附件失败：{ex.Message}");
        }
    }

    private async Task ShowImagePreview(string imagePath, string filename)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri(imagePath)),
            MaxHeight = 620,
            MaxWidth = 900,
            Stretch = Stretch.Uniform,
        };
        var saveButton = new Button { Content = "另存为…" };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(image);
        panel.Children.Add(saveButton);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = filename,
            CloseButtonText = "关闭",
            Content = panel,
        };

        saveButton.Click += async (_, _) =>
        {
            try
            {
                var extension = UiInputParser.PickerExtension(imagePath);
                var picker = new FileSavePicker
                {
                    SuggestedFileName = filename,
                    DefaultFileExtension = extension,
                };
                picker.FileTypeChoices.Add("图片", new List<string> { extension });
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker,
                    PickerInterop.RequireHandle(_shell!.WindowHandle));
                var file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    File.Copy(imagePath, file.Path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _shell!.ShowError($"另存图片失败：{ex.Message}");
            }
        };

        await dialog.ShowSafeAsync();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
