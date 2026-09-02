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

                    _conversations!.Activate(info);
                    ConversationListControl.SelectedItem =
                        _conversations.Conversations.FirstOrDefault(c => c.Id == info.Id) ?? info;
                    if (args.FocusMessageId is { } messageId)
                    {
                        _timeline!.JumpToMessage(messageId);
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
                await ShowSenderProfile(senderId);
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

    private async Task ShowSenderProfile(long senderId)
    {
        var contact = new ContactViewModel(
            AppServices.Instance.Senders,
            AppServices.Instance.Contacts,
            AppServices.Instance.AvatarStorage);
        try
        {
            if (!await contact.LoadAsync(senderId))
            {
                _shell!.ShowError("未找到联系人资料");
                return;
            }
        }
        catch (Exception ex)
        {
            _shell!.ShowError($"加载联系人失败：{ex.Message}");
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };

        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pic = new PersonPicture
        {
            Width = 48,
            Height = 48,
            DisplayName = contact.DisplayName,
            Initials = string.IsNullOrWhiteSpace(contact.DisplayName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(contact.DisplayName.Trim()),
        };
        if (!string.IsNullOrEmpty(contact.CustomAvatarPath))
        {
            var resolved = AppServices.Instance.AvatarStorage.ResolveAvatarFullPath(contact.CustomAvatarPath);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                pic.ProfilePicture = new BitmapImage(new Uri(resolved));
            }
        }
        Grid.SetColumn(pic, 0);
        headerGrid.Children.Add(pic);

        var headerTextStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        headerTextStack.Children.Add(new TextBlock { Text = contact.DisplayName, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        headerTextStack.Children.Add(new TextBlock { Text = contact.IdentityLine, FontSize = 12, Opacity = 0.7 });
        Grid.SetColumn(headerTextStack, 1);
        headerGrid.Children.Add(headerTextStack);
        panel.Children.Add(headerGrid);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = contact.DisplayName,
            CloseButtonText = "关闭",
            Content = panel,
        };

        // Bound status / actions
        if (contact.IsBound && contact.BoundContact is not null)
        {
            var boundInfoStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            boundInfoStack.Children.Add(new TextBlock { Text = $"已关联联系人: {contact.BoundContact.DisplayName}", FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });

            var unbindBtn = new Button { Content = "解除关联", FontSize = 11 };
            unbindBtn.Click += async (_, _) =>
            {
                try
                {
                    await contact.QuickUnbindContactAsync();
                    _conversations!.Reload();
                    dialog.Hide();
                }
                catch (Exception ex)
                {
                    _shell!.ShowError($"解除关联失败: {ex.Message}");
                }
            };
            boundInfoStack.Children.Add(unbindBtn);
            panel.Children.Add(boundInfoStack);
        }
        else
        {
            var bindRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var createContactBtn = new Button { Content = "新建并绑定联系人", FontSize = 12 };

            var inlineCreatePanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
            var nameBox = new TextBox { Header = "联系人姓名", Text = contact.OriginalName };
            var labelBox = new TextBox { Header = "身份标签(可选)", PlaceholderText = "如: 工作号" };
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var confirmBtn = new Button { Content = "确认创建", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var cancelBtn = new Button { Content = "取消" };
            actionRow.Children.Add(confirmBtn);
            actionRow.Children.Add(cancelBtn);
            inlineCreatePanel.Children.Add(nameBox);
            inlineCreatePanel.Children.Add(labelBox);
            inlineCreatePanel.Children.Add(actionRow);

            createContactBtn.Click += (_, _) =>
            {
                bindRow.Visibility = Visibility.Collapsed;
                inlineCreatePanel.Visibility = Visibility.Visible;
            };

            cancelBtn.Click += (_, _) =>
            {
                inlineCreatePanel.Visibility = Visibility.Collapsed;
                bindRow.Visibility = Visibility.Visible;
            };

            confirmBtn.Click += async (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    try
                    {
                        await contact.QuickCreateAndBindContactAsync(nameBox.Text.Trim(), labelBox.Text?.Trim());
                        _conversations!.Reload();
                        dialog.Hide();
                    }
                    catch (Exception ex)
                    {
                        _shell!.ShowError($"创建联系人失败: {ex.Message}");
                    }
                }
            };

            bindRow.Children.Add(createContactBtn);
            panel.Children.Add(bindRow);
            panel.Children.Add(inlineCreatePanel);
        }

        panel.Children.Add(new TextBlock { Text = "名称记录", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        var aliasList = new ListView { MaxHeight = 120, SelectionMode = ListViewSelectionMode.None };
        aliasList.Items.Add(contact.IdentityLine.Length == 0 ? "-" : contact.DisplayName);
        foreach (var alias in contact.Aliases.Take(30))
        {
            var seen = alias.LastSeenAt is long ts
                ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ts, 0, 253402300799000L)).LocalDateTime.ToString("yyyy-MM-dd")
                : string.Empty;
            aliasList.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = alias.Alias, FontSize = 13 },
                    new TextBlock { Text = seen, FontSize = 11, Opacity = 0.5 },
                },
            });
        }

        panel.Children.Add(aliasList);
        panel.Children.Add(new TextBlock { Text = "出现过的会话（点击跳转）", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        var conversationList = new ListView { MaxHeight = 160, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
        foreach (var conversation in contact.Conversations)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = conversation.Title, FontSize = 13 },
                    new TextBlock { Text = $"{conversation.MessageCount:N0} 条", FontSize = 11, Opacity = 0.5 },
                },
            };
            var wrap = new ListViewItem
            {
                Content = row,
                Tag = conversation.ConversationId,
            };
            conversationList.Items.Add(wrap);
        }
        conversationList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is ListViewItem { Tag: long conversationId })
            {
                contact.ActivateConversation(conversationId);
            }
        };

        panel.Children.Add(conversationList);
        contact.ConversationActivated += async id =>
        {
            dialog.Hide();
            try
            {
                var detail = await Task.Run(() => AppServices.Instance.Conversations.GetConversation(id));
                if (detail?.Conversation is { } info)
                {
                    _conversations!.Activate(info);
                    ConversationListControl.SelectedItem = _conversations.Conversations.FirstOrDefault(c => c.Id == info.Id) ?? info;
                }
            }
            catch (Exception ex)
            {
                _shell!.ShowError($"打开会话失败：{ex.Message}");
            }
        };
        await dialog.ShowSafeAsync();
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
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _shell!.WindowHandle);
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
