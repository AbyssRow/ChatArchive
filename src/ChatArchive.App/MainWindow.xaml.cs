using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
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
    private readonly SearchOptionsReloadGate _searchOptionsReloadGate = new();
    private readonly LatestRequestGate _searchResultActivationGate = new();
    private readonly LatestRequestGate _contactSelectionGate = new();
    private readonly ExclusiveInteractionGate _senderProfileGate = new();
    private bool _isAddingBoundAccount;
    private PendingSearchOptionsReload? _pendingSearchOptionsReload;

    private sealed record PendingSearchOptionsReload(
        long Generation,
        long? ConversationId,
        string? MessageType,
        bool HasSearched);

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
            _contacts = new ContactsViewModel(services.Contacts, services.AvatarStorage, dispatcher);
            _timeline = new TimelineViewModel(services.Conversations, services.MediaLocator, dispatcher);
            _search = new SearchViewModel(services.Search, services.Conversations, dispatcher);
            _search.OptionsReloaded += SearchOptions_Reloaded;
            _stats = new StatsViewModel(services.Stats, dispatcher);
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
            _search.ResultActivated += hit =>
            {
                var request = _searchResultActivationGate.Next();
                DispatcherQueue.TryEnqueue(() => OpenSearchResult(hit, request));
            };
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
                _statsLoaded = false;
                _conversations.Reload();
                ReloadSearchOptions();
            };
            _import.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportViewModel.IsRunning))
                {
                    ImportButton.IsEnabled = true;
                    ImportButton.Content = _import.IsRunning
                        ? "查看导入进度…"
                        : "导入聊天记录";
                }
            };

            ConversationListControl.ItemsSource = _conversations.Conversations;
            ContactsListView.ItemsSource = _contacts.Contacts;
            MessageListControl.ItemsSource = _timeline.Entries;
            SearchResultsList.ItemsSource = _search.Results;
            SearchConversationCombo.ItemsSource = _search.ConversationOptions;
            SearchMessageTypeCombo.ItemsSource = _search.MessageTypeOptions;
            ReloadSearchOptions();

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
        if (TimelinePane is null)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            TimelinePane.Visibility = Visibility.Collapsed;
            ContactsRoot.Visibility = Visibility.Collapsed;
            SearchPane.Visibility = Visibility.Collapsed;
            StatsPane.Visibility = Visibility.Collapsed;
            SettingsPane.Visibility = Visibility.Visible;
            RefreshSettingsView();
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var tag = item.Tag as string ?? "conversations";
        TimelinePane.Visibility = tag == "conversations" ? Visibility.Visible : Visibility.Collapsed;
        ContactsRoot.Visibility = tag == "contacts" ? Visibility.Visible : Visibility.Collapsed;
        SearchPane.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        StatsPane.Visibility = tag == "stats" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;

        if (tag == "contacts")
        {
            _ = ReloadContactsAsync(_contacts.SelectedContact?.Id);
        }
        if (tag == "stats" && !_statsLoaded)
        {
            _statsLoaded = true;
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
        try
        {
            var pathsPanel = new StackPanel { Spacing = 10 };
            var list = new ListView { MaxHeight = 160, SelectionMode = ListViewSelectionMode.None };
            list.ItemsSource = _import.Paths;
            var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };

            var addFile = new Button { Content = "添加文件…" };
            addFile.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FileOpenPicker
                    {
                        SuggestedStartLocation = PickerLocationId.ComputerFolder,
                        ViewMode = PickerViewMode.List,
                    };
                    foreach (var extension in ImportViewModel.PickerExtensions)
                    {
                        picker.FileTypeFilter.Add(extension);
                    }
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var files = await picker.PickMultipleFilesAsync();
                    if (files is not null)
                    {
                        foreach (var file in files)
                        {
                            _import.AddPath(file.Path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"选择文件失败: {ex.Message}");
                }
            };

            var addFolder = new Button { Content = "添加文件夹…" };
            addFolder.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder is not null)
                    {
                        _import.AddPath(folder.Path);
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"选择文件夹失败: {ex.Message}");
                }
            };
            var clear = new Button { Content = "清空列表" };
            clear.Click += (_, _) => _import.ClearPathsCommand.Execute(null);
            var start = new Button { Content = "开始导入", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var cancel = new Button { Content = "取消导入" };
            cancel.Click += (_, _) => _import.CancelCommand.Execute(null);
            ContentDialog? dialog = null;
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
                addFile.IsEnabled = !_import.IsRunning;
                addFolder.IsEnabled = !_import.IsRunning;
                clear.IsEnabled = !_import.IsRunning;
                start.IsEnabled = !_import.IsRunning && _import.Paths.Count > 0;
                cancel.IsEnabled = _import.IsRunning && !_import.IsCancellationRequested;
                cancel.Visibility = _import.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                if (dialog is not null)
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    dialog.PrimaryButtonText = _import.IsRunning ? "后台运行" : "关闭";
                }
            }

            pathsPanel.Children.Add(new TextBlock
            {
                Text = ImportViewModel.HelpText,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
                FontSize = 12,
                LineHeight = 18,
            });
            pathsPanel.Children.Add(list);
            var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttonsRow.Children.Add(addFile);
            buttonsRow.Children.Add(addFolder);
            buttonsRow.Children.Add(clear);
            buttonsRow.Children.Add(start);
            buttonsRow.Children.Add(cancel);
            pathsPanel.Children.Add(buttonsRow);
            pathsPanel.Children.Add(progress);
            pathsPanel.Children.Add(status);

            System.ComponentModel.PropertyChangedEventHandler importChanged = (_, e) =>
            {
                if (e.PropertyName is nameof(ImportViewModel.StatusText)
                    or nameof(ImportViewModel.IsRunning)
                    or nameof(ImportViewModel.IsCancellationRequested))
                {
                    DispatcherQueue.TryEnqueue(RefreshButtons);
                }
            };
            System.Collections.Specialized.NotifyCollectionChangedEventHandler pathsChanged = (_, _) =>
                DispatcherQueue.TryEnqueue(RefreshButtons);

            dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "导入聊天记录",
                PrimaryButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                Content = pathsPanel,
            };

            try
            {
                _import.PropertyChanged += importChanged;
                _import.Paths.CollectionChanged += pathsChanged;
                RefreshButtons();
                await dialog.ShowSafeAsync();
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
        catch (Exception ex)
        {
            ShowError($"导入失败: {ex.Message}");
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
            _conversations.Activate(conversation);
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
            MessageListControl.UpdateLayout();
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
                ShowError($"查看发送者信息失败: {ex.Message}");
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
                ShowError($"查看图片预览失败: {ex.Message}");
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
                ShowError("系统没有可打开此附件的应用");
            }
        }
        catch (Exception ex)
        {
            ShowError($"打开附件失败：{ex.Message}");
        }
    }

    private bool _isRefreshingContacts = false;

    private async Task ReloadContactsAsync(long? preserveContactId = null)
    {
        _isRefreshingContacts = true;
        try
        {
            var targetId = preserveContactId ?? _contacts.SelectedContact?.Id ?? (_contacts.SelectedDetail?.ContactId);
            await _contacts.LoadAsync(preferredSelectedContactId: targetId);
            ContactsListView.SelectedItem = _contacts.SelectedContact;
            UpdateContactDetailView();
        }
        catch (Exception ex)
        {
            ShowError($"刷新联系人列表失败: {ex.Message}");
        }
        finally
        {
            _isRefreshingContacts = false;
        }
    }

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
                    _ = ReloadContactsAsync(_contacts.SelectedContact?.Id);
                });
            }
        });
    }

    private async void ContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _contactSelectionGate.Next();

        if (_isRefreshingContacts)
        {
            return;
        }

        try
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
        catch (Exception ex)
        {
            ShowError($"选择联系人失败: {ex.Message}");
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
        DetailAvatarPicture.Initials = string.IsNullOrWhiteSpace(detail.DisplayName) ? "?" : System.Globalization.StringInfo.GetNextTextElement(detail.DisplayName.Trim());
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
        try
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

            if (await dialog.ShowSafeAsync() == ContentDialogResult.Primary)
            {
                var name = nameBox.Text?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    ShowError("联系人姓名不能为空");
                    return;
                }

                var newDetail = await _contacts.CreateNewContactAsync(name, noteBox.Text);
                await ReloadContactsAsync(newDetail.ContactId);
            }
        }
        catch (Exception ex)
        {
            ShowError($"创建联系人失败: {ex.Message}");
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
                var currentId = detail.ContactId;
                await detail.SaveAvatarFromFileAsync(file.Path);
                await ReloadContactsAsync(currentId);
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
            var currentId = detail.ContactId;
            await detail.SaveBasicInfoAsync(newName, DetailNoteBox.Text);
            await ReloadContactsAsync(currentId);
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

        try
        {
            var confirmDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "删除联系人",
                Content = $"确定要删除联系人【{detail.DisplayName}】吗？\n已绑定的账号不会被删除，仅解除关联关系。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirmDialog.ShowSafeAsync() == ContentDialogResult.Primary)
            {
                await _contacts.DeleteContactAsync(detail.ContactId);
                await ReloadContactsAsync(null);
            }
        }
        catch (Exception ex)
        {
            ShowError($"删除失败: {ex.Message}");
        }
    }

    private async void OnAccountLabelLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            var detail = _contacts.SelectedDetail;
            if (sender is TextBox tb && tb.DataContext is BoundSenderInfo info && detail is not null)
            {
                var newLabel = tb.Text?.Trim();
                if (newLabel != info.AccountLabel)
                {
                    await detail.UpdateAccountLabelAsync(info.SenderId, string.IsNullOrWhiteSpace(newLabel) ? null : newLabel);
                    if (_contacts.SelectedDetail?.ContactId == detail.ContactId)
                    {
                        UpdateContactDetailView();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"更新身份标签失败: {ex.Message}");
        }
    }

    private async void OnSetPrimarySenderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var detail = _contacts.SelectedDetail;
            if (sender is Button { Tag: long senderId } && detail is not null)
            {
                await detail.SetPrimarySenderAsync(senderId);
                UpdateContactDetailView();
            }
        }
        catch (Exception ex)
        {
            ShowError($"设置主账号失败: {ex.Message}");
        }
    }

    private async void OnUnbindSenderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var detail = _contacts.SelectedDetail;
            if (sender is Button { Tag: long senderId } && detail is not null)
            {
                var currentId = detail.ContactId;
                await detail.UnbindSenderAsync(senderId);
                await ReloadContactsAsync(currentId);
            }
        }
        catch (Exception ex)
        {
            ShowError($"解绑失败: {ex.Message}");
        }
    }

    private async void OnAddBoundAccountClick(object sender, RoutedEventArgs e)
    {
        var detail = _contacts.SelectedDetail;
        if (detail is null)
        {
            return;
        }

        if (_isAddingBoundAccount)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(detail.IdentityToken))
        {
            ShowError("无法确认目标联系人身份，请刷新联系人后重试");
            return;
        }

        var target = new ContactTargetSnapshot(
            detail.ContactId,
            detail.IdentityToken,
            detail.DisplayName);
        var targetSelectionVersion = _contactSelectionGate.Next();
        bool EnsureTargetIsCurrent()
        {
            if (_contactSelectionGate.IsCurrent(targetSelectionVersion)
                && target.IsCurrent(
                    _contacts.SelectedContact?.Id,
                    _contacts.SelectedContact?.IdentityToken,
                    _contacts.SelectedDetail?.ContactId,
                    _contacts.SelectedDetail?.IdentityToken))
            {
                return true;
            }

            ShowError("当前联系人已更改，请在目标联系人上重新打开“绑定账号”");
            return false;
        }

        _isAddingBoundAccount = true;
        AddBoundAccountButton.IsEnabled = false;

        try
        {
            var searchBox = new TextBox { PlaceholderText = "搜索未绑定发送者 (姓名/平台ID/QQ号)..." };
            var list = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
            var labelBox = new TextBox { Header = "身份标签（可选，如：工作号、大号）", PlaceholderText = "输入身份标签" };
            var primaryCheck = new CheckBox { Content = "设为主账号", IsChecked = detail.BoundSenders.Count == 0 };

            var availableSenders = new List<BoundSenderInfo>();
            var isSenderPickerOpen = true;
            async Task<bool> RefreshAvailable(string? kw, CancellationToken cancellationToken = default)
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested || !isSenderPickerOpen)
                    {
                        return false;
                    }

                    var items = await detail.LoadAvailableSendersAsync(kw);
                    if (cancellationToken.IsCancellationRequested || !isSenderPickerOpen)
                    {
                        return false;
                    }

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

                    return true;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested && isSenderPickerOpen)
                    {
                        ShowError($"加载发送者失败: {ex.Message}");
                    }

                    return false;
                }
            }

            if (!await RefreshAvailable(null) || !EnsureTargetIsCurrent())
            {
                return;
            }

            CancellationTokenSource? searchCts = null;
            searchBox.TextChanged += (_, _) =>
            {
                searchCts?.Cancel();
                searchCts = new CancellationTokenSource();
                var token = searchCts.Token;
                var query = searchBox.Text;
                _ = Task.Delay(250, token).ContinueWith(_ =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            if (!token.IsCancellationRequested && isSenderPickerOpen)
                            {
                                await RefreshAvailable(query, token);
                            }
                        });
                    }
                });
            };

            var panel = new StackPanel
            {
                Spacing = 10,
                MinWidth = 460,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"目标联系人：{target.DisplayName}",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                    searchBox,
                    list,
                    labelBox,
                    primaryCheck,
                },
            };

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"绑定/合并账号到“{target.DisplayName}”",
                PrimaryButtonText = "确认绑定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                Content = panel,
            };
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem
                {
                    Tag: BoundSenderInfo,
                };
            };

            BoundSenderInfo selectedSender;
            string? selectedLabel;
            bool selectedPrimary;
            try
            {
                if (await dialog.ShowSafeAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                if (list.SelectedItem is not ListViewItem { Tag: BoundSenderInfo item })
                {
                    ShowError("未选择要绑定的账号");
                    return;
                }

                selectedSender = item;
                selectedLabel = string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim();
                selectedPrimary = primaryCheck.IsChecked == true;
            }
            finally
            {
                isSenderPickerOpen = false;
                searchCts?.Cancel();
                searchCts?.Dispose();
            }

            if (!EnsureTargetIsCurrent())
            {
                return;
            }

            long? expectedSourceContactId = null;
            string? expectedSourceIdentityToken = null;
            var hasBoundContactName = !string.IsNullOrWhiteSpace(selectedSender.BoundContactName);
            var hasBoundContactIdentityToken =
                !string.IsNullOrWhiteSpace(selectedSender.BoundContactIdentityToken);
            if (selectedSender.BoundContactId.HasValue
                || hasBoundContactName
                || hasBoundContactIdentityToken)
            {
                if (!selectedSender.BoundContactId.HasValue
                    || !hasBoundContactName
                    || !hasBoundContactIdentityToken)
                {
                    ShowError("账号归属信息已发生变化，请重新选择账号后重试");
                    return;
                }

                expectedSourceContactId = selectedSender.BoundContactId.Value;
                expectedSourceIdentityToken = selectedSender.BoundContactIdentityToken;
                var oldContactName = selectedSender.BoundContactName!.Trim();
                var confirm = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "确认转移账号",
                    PrimaryButtonText = "确认转移",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    Content = $"账号“{selectedSender.OriginalName}”当前属于“{oldContactName}”。\n"
                              + $"确认将其转移到“{target.DisplayName}”吗？\n\n"
                              + "旧联系人如果没有其他账号、备注或自定义头像，可能会被自动清理。",
                };

                if (await confirm.ShowSafeAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            if (!EnsureTargetIsCurrent())
            {
                return;
            }

            var currentId = target.ContactId;
            if (expectedSourceContactId.HasValue)
            {
                await detail.TransferSenderFromExpectedContactAsync(
                    selectedSender.SenderId,
                    target.IdentityToken,
                    expectedSourceContactId.Value,
                    expectedSourceIdentityToken!,
                    selectedLabel,
                    selectedPrimary);
            }
            else
            {
                await detail.BindSenderAsync(
                    selectedSender.SenderId,
                    selectedLabel,
                    selectedPrimary,
                    forceRebind: false);
            }
            await ReloadContactsAsync(currentId);
        }
        catch (Exception ex)
        {
            ShowError($"绑定账号失败: {ex.Message}");
        }
        finally
        {
            _isAddingBoundAccount = false;
            AddBoundAccountButton.IsEnabled = true;
        }
    }

    private async void ContactConversation_Click(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is SenderConversationInfo conv)
            {
                SelectNavItem("conversations");
                var detail = await Task.Run(() => AppServices.Instance.Conversations.GetConversation(conv.ConversationId));
                if (detail?.Conversation is { } info)
                {
                    _conversations.Activate(info);
                    ConversationListControl.SelectedItem = _conversations.Conversations.FirstOrDefault(c => c.Id == info.Id) ?? info;
                }
            }
        }
        catch (Exception ex)
        {
            ShowError($"打开会话失败: {ex.Message}");
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
            XamlRoot = Content.XamlRoot,
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
                    _conversations.Reload();
                    dialog.Hide();
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
                        _conversations.Reload();
                        dialog.Hide();
                    }
                    catch (Exception ex)
                    {
                        ShowError($"创建联系人失败: {ex.Message}");
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
            SelectNavItem("conversations");
            try
            {
                var detail = await Task.Run(() => AppServices.Instance.Conversations.GetConversation(id));
                if (detail?.Conversation is { } info)
                {
                    _conversations.Activate(info);
                    ConversationListControl.SelectedItem = _conversations.Conversations.FirstOrDefault(c => c.Id == info.Id) ?? info;
                }
            }
            catch (Exception ex)
            {
                ShowError($"打开会话失败：{ex.Message}");
            }
        };
        await dialog.ShowSafeAsync();
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

        await dialog.ShowSafeAsync();
    }

    // ---------- 搜索 ----------

    private void ReloadSearchOptions()
    {
        long? conversationId = SearchConversationCombo.SelectedValue is long id ? id : null;
        var messageType = SearchMessageTypeCombo.SelectedValue as string;
        _searchOptionsReloadGate.Begin();
        SetSearchInteractionEnabled(false);

        long generation;
        try
        {
            generation = _search.LoadOptions();
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
                _search.ConversationOptions,
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
        _search.LoadMoreCommand.Execute(null);
    }

    private void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchHitProxy proxy)
        {
            _search.NotifyResultActivated(proxy.Hit);
        }
    }

    private async void OpenSearchResult(SearchHit hit, long request)
    {
        try
        {
            var detail = await Task.Run(() =>
                AppServices.Instance.Conversations.GetConversation(hit.ConversationId));
            if (!_searchResultActivationGate.IsCurrent(request))
            {
                return;
            }

            if (detail?.Conversation is not { } info)
            {
                ShowError("打开搜索结果失败：未找到对应会话");
                return;
            }

            SelectNavItem("conversations");
            _messagePagingReady = false;
            _conversations.Activate(info);
            ConversationListControl.SelectedItem =
                _conversations.Conversations.FirstOrDefault(c => c.Id == info.Id) ?? info;
            _timeline.JumpToMessage(hit.MessageId);
        }
        catch (Exception ex)
        {
            if (_searchResultActivationGate.IsCurrent(request))
            {
                ShowError($"打开搜索结果失败：{ex.Message}");
            }
        }
    }

    private void RunSearch()
    {
        if (_searchOptionsReloadGate.IsLocked)
        {
            return;
        }

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

    // ---------- 设置页面 ----------

    private async void RefreshSettingsView()
    {
        try
        {
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            SettingsDataPathText.Text = currentDir;
            SettingsTotalSizeText.Text = "计算中…";
            SettingsDbSizeText.Text = "计算中…";
            SettingsMediaSizeText.Text = "计算中…";
            SettingsAvatarSizeText.Text = "计算中…";

            var usage = await Task.Run(() => AppSettings.GetStorageUsage(currentDir));
            SettingsTotalSizeText.Text = usage.FormattedTotalSize;
            SettingsDbSizeText.Text = usage.FormattedDatabaseSize;
            SettingsMediaSizeText.Text = usage.FormattedMediaSize;
            SettingsAvatarSizeText.Text = usage.FormattedAvatarSize;
        }
        catch (Exception ex)
        {
            ShowError($"读取设置信息失败: {ex.Message}");
        }
    }

    private void OnSettingsOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            if (Directory.Exists(currentDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentDir,
                    UseShellExecute = true
                });
            }
            else
            {
                ShowError($"目录不存在: {currentDir}");
            }
        }
        catch (Exception ex)
        {
            ShowError($"无法打开目录: {ex.Message}");
        }
    }

    private async void OnSettingsChangeDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var targetPath = folder.Path;
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            if (string.Equals(targetPath, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "更改数据存储目录",
                PrimaryButtonText = "保存并迁移数据",
                SecondaryButtonText = "仅保存新路径",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"您选择的新存储目录为：\n{targetPath}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "提示：\n• 「保存并迁移数据」会将当前数据库、媒体附件与头像复制到新目录。\n• 「仅保存新路径」仅修改配置文件，在新目录创建全新空白数据库。\n• 保存后请重启应用以完全切换至新存储目录。", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap }
                    }
                }
            };

            var result = await dialog.ShowSafeAsync();
            if (result == ContentDialogResult.None)
            {
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                await Task.Run(() => AppSettings.CopyDataDirectory(currentDir, targetPath, overwrite: false));
            }

            var settings = AppServices.Instance.Settings;
            settings.DataDirectory = targetPath;
            settings.Save();

            RefreshSettingsView();

            var successDlg = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "存储目录已更改",
                Content = "数据存储目录已成功更改为新路径！\n请重启 ChatArchive 应用程序以加载新目录的数据。",
                CloseButtonText = "知道了"
            };
            await successDlg.ShowSafeAsync();
        }
        catch (Exception ex)
        {
            ShowError($"更改存储目录失败: {ex.Message}");
        }
    }

    private async void OnSettingsResetDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var defaultDir = AppSettings.DefaultDataDirectory;
            var currentDir = AppServices.Instance.Settings.GetValidDataDirectory();
            if (string.Equals(defaultDir, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("当前已经是默认存储目录。");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "恢复默认存储目录",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = $"确定要将存储目录恢复为默认路径吗？\n{defaultDir}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "保存后需重启应用以完全切换生效。", FontSize = 12, Opacity = 0.75 }
                    }
                },
                PrimaryButtonText = "恢复默认并保存",
                CloseButtonText = "取消"
            };

            var result = await dialog.ShowSafeAsync();
            if (result == ContentDialogResult.Primary)
            {
                var settings = AppServices.Instance.Settings;
                settings.DataDirectory = defaultDir;
                settings.Save();

                RefreshSettingsView();

                var successDlg = new ContentDialog
                {
                    XamlRoot = this.Content.XamlRoot,
                    Title = "已恢复默认目录",
                    Content = "已成功恢复为默认数据目录，请重启应用生效。",
                    CloseButtonText = "知道了"
                };
                await successDlg.ShowSafeAsync();
            }
        }
        catch (Exception ex)
        {
            ShowError($"恢复默认目录失败: {ex.Message}");
        }
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
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatArchive");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}\n\n");
        }
        catch
        {
        }
    }
}


