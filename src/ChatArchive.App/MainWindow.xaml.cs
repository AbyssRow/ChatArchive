using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace ChatArchive.App;

public sealed partial class MainWindow : Window
{
    private readonly ConversationListViewModel _conversations;
    private readonly ContactsViewModel _contacts;
    private readonly TimelineViewModel _timeline;
    private readonly SearchViewModel _search;
    private readonly StatsViewModel _stats;
    private readonly ImportViewModel _import;
    private readonly TimelineInitialPositionState _initialTimelinePosition = new();
    private CancellationTokenSource? _queryDebounce;
    private CancellationTokenSource? _contactsQueryDebounce;
    private ScrollViewer? _messageScroll;
    private bool _messagePagingReady;
    private bool _statsLoaded;

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            // 标题栏与窗口背景：内容延伸进标题栏，Mica 材质与 Fluent 主题融合。
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            try
            {
                SystemBackdrop = new MicaBackdrop();
            }
            catch (Exception)
            {
                // 不支持 Mica 的系统回退默认背景。
            }

            var services = AppServices.Instance;
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _conversations = new ConversationListViewModel(services.Conversations, dispatcher);
            _contacts = new ContactsViewModel(services.Contacts, services.AvatarStorage);
            _timeline = new TimelineViewModel(services.Conversations, services.MediaLocator, dispatcher);
            _search = new SearchViewModel(services.Search, services.Conversations, dispatcher);
            _stats = new StatsViewModel(services.Stats, dispatcher);
            _import = new ImportViewModel(services.Database, dispatcher);

            _conversations.ConversationActivated += conversation =>
            {
                _messagePagingReady = false;
                _timeline.Load(conversation);
            };
            _conversations.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConversationListViewModel.ErrorMessage)
                    && _conversations.ErrorMessage.Length > 0)
                {
                    ShowError(_conversations.ErrorMessage);
                }
            };
            _contacts.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ContactsViewModel.IsLoading))
                {
                    ContactsProgressBar.Visibility = _contacts.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (e.PropertyName == nameof(ContactsViewModel.ErrorMessage)
                         && _contacts.ErrorMessage.Length > 0)
                {
                    ShowError(_contacts.ErrorMessage);
                }
            };
            _timeline.PropertyChanged += (_, e) =>
            {
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
                    ShowError(_timeline.ErrorMessage);
                }
            };
            _timeline.InitialPageLoaded += PositionTimelineAtBottom;
            _timeline.FocusMessageLoaded += FocusTimelineMessage;
            _search.ResultActivated += hit => DispatcherQueue.TryEnqueue(() =>
            {
                SelectNavItem("conversations");
                _messagePagingReady = false;
                _timeline.JumpToMessage(hit.MessageId);
            });
            _search.PropertyChanged += (_, e) =>
            {
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
                    ShowError(_search.ErrorMessage);
                }
            };
            _import.ImportFinished += () =>
            {
                _conversations.Reload();
                _search.LoadOptions();
            };
            _import.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportViewModel.IsRunning))
                {
                    ImportButton.IsEnabled = !_import.IsRunning;
                }
            };

            ConversationListControl.ItemsSource = _conversations.Conversations;
            ContactsListView.ItemsSource = _contacts.Contacts;
            MessageListControl.ItemsSource = _timeline.Entries;
            SearchResultsList.ItemsSource = _search.Results;
            SearchConversationCombo.ItemsSource = _search.ConversationOptions;
            SearchMessageTypeCombo.ItemsSource = _search.MessageTypeOptions;
            _search.LoadOptions();

            // 侧栏收起时隐藏导入按钮，展开时恢复。
            ImportButton.Visibility = Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            Nav.PaneOpened += (_, _) => ImportButton.Visibility = Visibility.Visible;
            Nav.PaneClosing += (_, args) =>
                ImportButton.Visibility = args.Cancel ? Visibility.Visible : Visibility.Collapsed;

            _conversations.Reload();
            HookMessageScroll();
        }
        catch (Exception ex)
        {
            WriteCrashLog("MainWindow ctor", ex);
            throw;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || TimelinePane is null)
        {
            return;
        }

        var tag = item.Tag as string ?? "conversations";
        TimelinePane.Visibility = tag == "conversations" ? Visibility.Visible : Visibility.Collapsed;
        ContactsRoot.Visibility = tag == "contacts" ? Visibility.Visible : Visibility.Collapsed;
        SearchPane.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        StatsPane.Visibility = tag == "stats" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "contacts")
        {
            _ = _contacts.LoadAsync();
        }
        if (tag == "stats" && !_statsLoaded)
        {
            _statsLoaded = true;
            _stats.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatsViewModel.SummaryLines))
                {
                    StatsText.Text = _stats.SummaryLines;
                }
                else if (e.PropertyName == nameof(StatsViewModel.ErrorMessage)
                         && _stats.ErrorMessage.Length > 0)
                {
                    ShowError(_stats.ErrorMessage);
                }
            };
            _stats.Load();
            StatsText.Text = _stats.SummaryLines;
        }

        if (tag == "search")
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }

    private void SelectNavItem(string tag)
    {
        foreach (var menuItem in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((menuItem.Tag as string) == tag)
            {
                Nav.SelectedItem = menuItem;
                return;
            }
        }
    }

    // ---------- 导入 ----------

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var pathsPanel = new StackPanel { Spacing = 10 };
        var list = new ListView { MaxHeight = 160, SelectionMode = ListViewSelectionMode.None };
        list.ItemsSource = _import.Paths;
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };

        var addFolder = new Button { Content = "添加文件夹…" };
        addFolder.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                _import.AddPath(folder.Path);
            }
        };
        var clear = new Button { Content = "清空列表" };
        clear.Click += (_, _) => _import.ClearPathsCommand.Execute(null);
        var start = new Button { Content = "开始导入", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        start.Click += (_, _) =>
        {
            _import.StartCommand.Execute(null);
            RefreshButtons();
        };

        void RefreshButtons()
        {
            status.Text = _import.StatusText;
            progress.IsIndeterminate = _import.IsRunning;
            progress.Visibility = _import.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            addFolder.IsEnabled = !_import.IsRunning;
            clear.IsEnabled = !_import.IsRunning;
            start.IsEnabled = !_import.IsRunning && _import.Paths.Count > 0;
        }

        pathsPanel.Children.Add(new TextBlock
        {
            Text = "选择包含 QQ Chat Exporter / WeFlow 导出 JSON 的文件夹；\n多次导出日期可重叠，应用会按内容自动去重。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });
        pathsPanel.Children.Add(list);
        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonsRow.Children.Add(addFolder);
        buttonsRow.Children.Add(clear);
        buttonsRow.Children.Add(start);
        pathsPanel.Children.Add(buttonsRow);
        pathsPanel.Children.Add(progress);
        pathsPanel.Children.Add(status);

        System.ComponentModel.PropertyChangedEventHandler importChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(ImportViewModel.StatusText) or nameof(ImportViewModel.IsRunning))
            {
                DispatcherQueue.TryEnqueue(RefreshButtons);
            }
        };
        System.Collections.Specialized.NotifyCollectionChangedEventHandler pathsChanged = (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshButtons);
        _import.PropertyChanged += importChanged;
        _import.Paths.CollectionChanged += pathsChanged;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "导入聊天记录",
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            Content = pathsPanel,
        };
        try
        {
            RefreshButtons();
            await dialog.ShowAsync();
        }
        finally
        {
            _import.PropertyChanged -= importChanged;
            _import.Paths.CollectionChanged -= pathsChanged;
        }

        if (!_import.IsRunning)
        {
            _conversations.Reload();
        }
    }

    // ---------- 会话侧栏 ----------

    private void ConversationQuery_TextChanged(object sender, TextChangedEventArgs e)
    {
        _queryDebounce?.Cancel();
        _queryDebounce = new CancellationTokenSource();
        var token = _queryDebounce.Token;
        _ = System.Threading.Tasks.Task.Delay(300, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _conversations.Query = ConversationQueryBox.Text;
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
            _conversations.SelectedConversation = conversation;
        }
    }

    // ---------- 时间线 ----------

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
        if (_messageScroll is null || e.IsIntermediate)
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
        var last = _timeline.Entries.LastOrDefault();
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
            var entry = _timeline.Entries
                .OfType<MessageEntry>()
                .FirstOrDefault(item => item.Message.Id == messageId);
            if (entry is not null)
            {
                MessageListControl.ScrollIntoView(entry);
            }

            _messagePagingReady = true;
        });
    }

    private async void OnSenderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageEntry entry } && entry.Message.SenderId is long senderId)
        {
            await ShowSenderProfile(senderId);
        }
    }

    private async void OnImageAttachmentTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AttachmentEntry entry }
            && entry.ResolvedPath is not null)
        {
            await ShowImagePreview(entry.ResolvedPath, entry.Filename ?? "图片");
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
                ShowError("系统没有可打开此附件的应用");
            }
        }
        catch (Exception ex)
        {
            ShowError($"打开附件失败：{ex.Message}");
        }
    }

    // ---------- 通讯录 ----------

    private void ContactsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _contactsQueryDebounce?.Cancel();
        _contactsQueryDebounce = new CancellationTokenSource();
        var token = _contactsQueryDebounce.Token;
        _ = Task.Delay(300, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _contacts.SearchKeyword = ContactsSearchBox.Text;
                    _ = _contacts.LoadAsync();
                });
            }
        });
    }

    private async void ContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactsListView.SelectedItem is ContactInfo contact)
        {
            await _contacts.SelectContactAsync(contact);
            UpdateContactDetailView();
        }
        else
        {
            await _contacts.SelectContactAsync(null);
            UpdateContactDetailView();
        }
    }

    private void UpdateContactDetailView()
    {
        var detail = _contacts.SelectedDetail;
        if (detail is null)
        {
            NoContactSelectedPrompt.Visibility = Visibility.Visible;
            ContactDetailPane.Visibility = Visibility.Collapsed;
            return;
        }

        NoContactSelectedPrompt.Visibility = Visibility.Collapsed;
        ContactDetailPane.Visibility = Visibility.Visible;

        DetailDisplayNameBox.Text = detail.DisplayName;
        DetailNoteBox.Text = detail.Note ?? string.Empty;
        DetailTotalMessagesText.Text = $"总消息数: {detail.TotalMessageCount:N0} 条";

        DetailAvatarPicture.DisplayName = detail.DisplayName;
        DetailAvatarPicture.Initials = string.IsNullOrWhiteSpace(detail.DisplayName) ? "?" : detail.DisplayName.Trim()[..1];
        if (!string.IsNullOrEmpty(detail.CustomAvatarPath))
        {
            var resolved = AppServices.Instance.AvatarStorage.ResolveAvatarFullPath(detail.CustomAvatarPath);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                DetailAvatarPicture.ProfilePicture = new BitmapImage(new Uri(resolved));
            }
            else
            {
                DetailAvatarPicture.ProfilePicture = null;
            }
        }
        else
        {
            DetailAvatarPicture.ProfilePicture = null;
        }

        BoundSendersListView.ItemsSource = detail.BoundSenders;
        ContactConversationsListView.ItemsSource = detail.Conversations;
    }

    private async void OnNewContactClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "姓名", PlaceholderText = "输入联系人姓名" };
        var noteBox = new TextBox { Header = "备注（可选）", PlaceholderText = "输入备注信息", AcceptsReturn = true };
        var panel = new StackPanel { Spacing = 10, Children = { nameBox, noteBox } };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "新建联系人",
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowError("联系人姓名不能为空");
                return;
            }

            try
            {
                await _contacts.CreateNewContactAsync(name, noteBox.Text);
                if (_contacts.SelectedContact is not null)
                {
                    ContactsListView.SelectedItem = _contacts.SelectedContact;
                }
                UpdateContactDetailView();
            }
            catch (Exception ex)
            {
                ShowError($"创建联系人失败: {ex.Message}");
            }
        }
    }

    private async void OnChangeAvatarClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                await detail.SaveAvatarFromFileAsync(file.Path);
                await _contacts.LoadAsync();
                UpdateContactDetailView();
            }
        }
        catch (Exception ex)
        {
            ShowError($"更换头像失败: {ex.Message}");
        }
    }

    private async void OnSaveContactClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        var newName = DetailDisplayNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            ShowError("姓名不能为空");
            return;
        }

        try
        {
            await detail.SaveBasicInfoAsync(newName, DetailNoteBox.Text);
            await _contacts.LoadAsync();
            UpdateContactDetailView();
        }
        catch (Exception ex)
        {
            ShowError($"保存失败: {ex.Message}");
        }
    }

    private async void OnDeleteContactClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        var confirmDialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "删除联系人",
            Content = $"确定要删除联系人【{detail.DisplayName}】吗？\n已绑定的账号不会被删除，仅解除关联关系。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirmDialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await _contacts.DeleteContactAsync(detail.ContactId);
                UpdateContactDetailView();
            }
            catch (Exception ex)
            {
                ShowError($"删除失败: {ex.Message}");
            }
        }
    }

    private async void OnAccountLabelLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is BoundSenderInfo info && _contacts.SelectedDetail is not null)
        {
            var newLabel = tb.Text?.Trim();
            if (newLabel != info.AccountLabel)
            {
                try
                {
                    await _contacts.SelectedDetail.UpdateAccountLabelAsync(info.SenderId, string.IsNullOrWhiteSpace(newLabel) ? null : newLabel);
                    UpdateContactDetailView();
                }
                catch (Exception ex)
                {
                    ShowError($"更新身份标签失败: {ex.Message}");
                }
            }
        }
    }

    private async void OnSetPrimarySenderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long senderId } && _contacts.SelectedDetail is not null)
        {
            try
            {
                await _contacts.SelectedDetail.SetPrimarySenderAsync(senderId);
                UpdateContactDetailView();
            }
            catch (Exception ex)
            {
                ShowError($"设置主账号失败: {ex.Message}");
            }
        }
    }

    private async void OnUnbindSenderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long senderId } && _contacts.SelectedDetail is not null)
        {
            try
            {
                await _contacts.SelectedDetail.UnbindSenderAsync(senderId);
                await _contacts.LoadAsync();
                UpdateContactDetailView();
            }
            catch (Exception ex)
            {
                ShowError($"解绑失败: {ex.Message}");
            }
        }
    }

    private async void OnAddBoundAccountClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        var searchBox = new TextBox { PlaceholderText = "搜索未绑定发送者 (姓名/平台ID/QQ号)..." };
        var list = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
        var labelBox = new TextBox { Header = "身份标签（可选，如：工作号、大号）", PlaceholderText = "输入身份标签" };
        var primaryCheck = new CheckBox { Content = "设为主账号", IsChecked = detail.BoundSenders.Count == 0 };

        var availableSenders = new List<BoundSenderInfo>();
        async Task RefreshAvailable(string? kw)
        {
            try
            {
                var items = await detail.LoadAvailableSendersAsync(kw);
                availableSenders.Clear();
                availableSenders.AddRange(items);
                list.Items.Clear();
                foreach (var item in availableSenders)
                {
                    var plat = item.Platform == "qq" ? "QQ" : "微信";
                    var idStr = item.Platform == "qq" ? (item.QQNumber ?? item.NativeId) : item.NativeId;
                    var status = !string.IsNullOrEmpty(item.BoundContactName)
                        ? $" [当前归属: {item.BoundContactName} (合并转移)]"
                        : " [未绑定]";
                    list.Items.Add(new ListViewItem
                    {
                        Content = $"{plat}: {item.OriginalName} ({idStr}) - {item.MessageCount:N0}条{status}",
                        Tag = item,
                    });
                }
            }
            catch (Exception ex)
            {
                ShowError($"加载发送者失败: {ex.Message}");
            }
        }

        await RefreshAvailable(null);

        searchBox.TextChanged += async (_, _) =>
        {
            await RefreshAvailable(searchBox.Text);
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 460,
            Children = { searchBox, list, labelBox, primaryCheck },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "绑定/合并账号",
            PrimaryButtonText = "确认绑定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (list.SelectedItem is ListViewItem { Tag: BoundSenderInfo selectedSender })
            {
                try
                {
                    await detail.BindSenderAsync(
                        selectedSender.SenderId,
                        string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim(),
                        primaryCheck.IsChecked == true,
                        forceRebind: true);
                    await _contacts.LoadAsync();
                    UpdateContactDetailView();
                }
                catch (Exception ex)
                {
                    ShowError($"绑定账号失败: {ex.Message}");
                }
            }
            else
            {
                ShowError("未选择要绑定的账号");
            }
        }
    }

    private async void ContactConversation_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SenderConversationInfo conv)
        {
            SelectNavItem("conversations");
            try
            {
                var detail = await Task.Run(() => AppServices.Instance.Conversations.GetConversation(conv.ConversationId));
                if (detail?.Conversation is { } info)
                {
                    _messagePagingReady = false;
                    _timeline.Load(info);
                    _conversations.SelectedConversation = info;
                }
            }
            catch (Exception ex)
            {
                ShowError($"打开会话失败: {ex.Message}");
            }
        }
    }

    private async System.Threading.Tasks.Task ShowSenderProfile(long senderId)
    {
        var contact = new ContactViewModel(
            AppServices.Instance.Senders,
            AppServices.Instance.Contacts,
            AppServices.Instance.AvatarStorage);
        try
        {
            if (!await contact.LoadAsync(senderId))
            {
                ShowError("未找到联系人资料");
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError($"加载联系人失败：{ex.Message}");
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
            Initials = string.IsNullOrWhiteSpace(contact.DisplayName) ? "?" : contact.DisplayName.Trim()[..1],
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
                    _conversations.Reload();
                }
                catch (Exception ex)
                {
                    ShowError($"解除关联失败: {ex.Message}");
                }
            };
            boundInfoStack.Children.Add(unbindBtn);
            panel.Children.Add(boundInfoStack);
        }
        else
        {
            var bindRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var createContactBtn = new Button { Content = "新建并绑定联系人", FontSize = 12 };
            createContactBtn.Click += async (_, _) =>
            {
                var nameBox = new TextBox { Header = "联系人姓名", Text = contact.OriginalName };
                var labelBox = new TextBox { Header = "身份标签(可选)", PlaceholderText = "如: 工作号" };
                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "新建联系人并绑定",
                    PrimaryButtonText = "创建",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    Content = new StackPanel { Spacing = 8, Children = { nameBox, labelBox } },
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    await contact.QuickCreateAndBindContactAsync(nameBox.Text.Trim(), labelBox.Text?.Trim());
                    _conversations.Reload();
                }
            };
            bindRow.Children.Add(createContactBtn);
            panel.Children.Add(bindRow);
        }

        panel.Children.Add(new TextBlock { Text = "名称记录", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
        var aliasList = new ListView { MaxHeight = 120, SelectionMode = ListViewSelectionMode.None };
        aliasList.Items.Add(contact.IdentityLine.Length == 0 ? "-" : contact.DisplayName);
        foreach (var alias in contact.Aliases.Take(30))
        {
            var seen = alias.LastSeenAt is long ts
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToString("yyyy-MM-dd")
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
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = contact.DisplayName,
            CloseButtonText = "关闭",
            Content = panel,
        };
        contact.ConversationActivated += async id =>
        {
            dialog.Hide();
            SelectNavItem("conversations");
            try
            {
                var detail = await Task.Run(() => AppServices.Instance.Conversations.GetConversation(id));
                if (detail?.Conversation is { } info)
                {
                    _messagePagingReady = false;
                    _timeline.Load(info);
                    _conversations.SelectedConversation = info;
                }
            }
            catch (Exception ex)
            {
                ShowError($"打开会话失败：{ex.Message}");
            }
        };
        await dialog.ShowAsync();
    }

    private async System.Threading.Tasks.Task ShowImagePreview(string imagePath, string filename)
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
            XamlRoot = Content.XamlRoot,
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
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    File.Copy(imagePath, file.Path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                ShowError($"另存图片失败：{ex.Message}");
            }
        };

        await dialog.ShowAsync();
    }

    // ---------- 搜索 ----------

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            RunSearch();
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => RunSearch();

    private void SearchFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_search is null || !_search.HasSearched)
        {
            return;
        }

        RunSearch();
    }

    private void SearchFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchFilter_Changed(sender, e);
    }

    private void SearchDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_search is not null && _search.HasSearched)
        {
            RunSearch();
        }
    }

    private void OnSearchLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _search.LoadMoreCommand.Execute(null);
    }

    private void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchHitProxy proxy)
        {
            _search.NotifyResultActivated(proxy.Hit);
        }
    }

    private void RunSearch()
    {
        _search.Query = SearchBox.Text;
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
        SearchModeLabel.Text = _search.Results.Count > 0
            ? $"已加载 {_search.Results.Count:N0} 条（{_search.ModeLabel}）"
            : _search.ModeLabel;
    }

    private static string ComboTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    }

    // ---------- 工具 ----------

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

    private void ShowError(string message)
    {
        AppInfoBar.Message = message;
        AppInfoBar.IsOpen = true;
    }

    private static void WriteCrashLog(string where, Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n\n");
        }
        catch
        {
        }
    }
}


