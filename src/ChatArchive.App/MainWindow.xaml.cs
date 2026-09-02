using ChatArchive.App.Navigation;
using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.App.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace ChatArchive.App;

public sealed partial class MainWindow : Window, IAppShell
{
    private readonly ConversationListViewModel _conversations;
    private readonly ContactsViewModel _contacts;
    private readonly TimelineViewModel _timeline;
    private readonly SearchViewModel _search;
    private readonly StatsViewModel _stats;
    private readonly ImportViewModel _import;
    private AppSection _currentSection = AppSection.Conversations;
    private bool _isApplyingConversationNavigation;
    private bool _statsStale;
    private bool _shellReady;
    private SearchPage? _searchPage;
    private StatsPage? _statsPage;
    private nint _windowHandle;

    public nint WindowHandle
    {
        get
        {
            if (!PickerInterop.IsUsableHandle(_windowHandle))
            {
                _windowHandle = CaptureWindowHandle();
            }

            return _windowHandle;
        }
    }

    public bool IsPickerReady => PickerInterop.IsUsableHandle(WindowHandle);

    private nint CaptureWindowHandle()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (PickerInterop.IsUsableHandle(hwnd))
        {
            return hwnd;
        }

        try
        {
            hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        }
        catch (Exception)
        {
        }

        return hwnd;
    }

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated)
                {
                    _windowHandle = CaptureWindowHandle();
                }
            };

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
            _stats = new StatsViewModel(services.Stats, dispatcher);
            _import = new ImportViewModel(services.Database, dispatcher);

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
                if (e.PropertyName == nameof(ContactsViewModel.ErrorMessage)
                    && _contacts.ErrorMessage.Length > 0)
                {
                    ShowError(_contacts.ErrorMessage);
                }
            };
            _timeline.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TimelineViewModel.ErrorMessage)
                    && _timeline.ErrorMessage.Length > 0)
                {
                    ShowError(_timeline.ErrorMessage);
                }
            };
            _search.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchViewModel.ErrorMessage)
                    && _search.ErrorMessage.Length > 0)
                {
                    ShowError(_search.ErrorMessage);
                }
            };
            _stats.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatsViewModel.ErrorMessage)
                    && _stats.ErrorMessage.Length > 0)
                {
                    ShowError(_stats.ErrorMessage);
                }
            };

            _import.ImportFinished += () =>
            {
                _conversations.Reload();
                if (_searchPage is not null)
                {
                    _searchPage.ReloadOptions();
                }
                else
                {
                    _ = _search.LoadOptions();
                }

                if (_statsPage is not null)
                {
                    _statsPage.Invalidate();
                    if (_currentSection == AppSection.Stats)
                    {
                        _statsPage.OnShown();
                    }
                }
                else
                {
                    _statsStale = true;
                }
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

            // 侧栏收起时隐藏导入按钮，展开时恢复。
            ImportButton.Visibility = Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
            Nav.PaneOpened += (_, _) => ImportButton.Visibility = Visibility.Visible;
            Nav.PaneClosing += (_, args) =>
                ImportButton.Visibility = args.Cancel ? Visibility.Visible : Visibility.Collapsed;

            _shellReady = true;
            GoTo(AppSection.Conversations);
            _conversations.Reload();
        }
        catch (Exception ex)
        {
            WriteCrashLog("MainWindow ctor", ex);
            throw;
        }
    }

    public void ShowError(string message)
    {
        AppInfoBar.Message = message;
        AppInfoBar.IsOpen = true;
    }

    void IAppNavigator.GoTo(AppSection section) => GoTo(section);

    internal void GoTo(AppSection section)
    {
        var decision = AppNavigation.ForSidebar(_currentSection, section);
        SelectSection(section);
        _currentSection = section;
        if (decision.ShouldNavigate || ContentFrame.Content is null)
        {
            NavigateToPage(PageType(decision.PageTypeName));
            AttachVisiblePage();
        }

        if (ContentFrame.Content is IShellPage page)
        {
            page.OnShown();
        }
    }

    public void OpenConversation(long conversationId, long? focusMessageId = null)
    {
        var decision = AppNavigation.ForOpenConversation(
            _currentSection, conversationId, focusMessageId);
        _isApplyingConversationNavigation = true;
        try
        {
            SelectSection(AppSection.Conversations);
            _currentSection = AppSection.Conversations;
            if (decision.ShouldNavigate)
            {
                NavigateToPage(typeof(ConversationsPage));
                AttachVisiblePage();
            }

            if (ContentFrame.Content is ConversationsPage conversations)
            {
                conversations.ApplyConversation(decision.Args);
                conversations.OnShown();
            }
        }
        finally
        {
            _isApplyingConversationNavigation = false;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_shellReady)
        {
            return;
        }

        if (_isApplyingConversationNavigation)
        {
            _currentSection = ResolveSection(args);
            return;
        }

        if (args.IsSettingsSelected)
        {
            GoTo(AppSection.Settings);
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            GoTo(SectionFromTag(item.Tag as string));
        }
    }

    private void SelectSection(AppSection section)
    {
        if (section == AppSection.Settings)
        {
            if (!ReferenceEquals(Nav.SelectedItem, Nav.SettingsItem))
            {
                Nav.SelectedItem = Nav.SettingsItem;
            }

            return;
        }

        var tag = section switch
        {
            AppSection.Contacts => "contacts",
            AppSection.Search => "search",
            AppSection.Stats => "stats",
            _ => "conversations",
        };

        foreach (var menuItem in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((menuItem.Tag as string) == tag)
            {
                if (!ReferenceEquals(Nav.SelectedItem, menuItem))
                {
                    Nav.SelectedItem = menuItem;
                }

                return;
            }
        }
    }

    private static AppSection ResolveSection(NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            return AppSection.Settings;
        }

        return args.SelectedItem is NavigationViewItem item
            ? SectionFromTag(item.Tag as string)
            : AppSection.Conversations;
    }

    private static AppSection SectionFromTag(string? tag) => tag switch
    {
        "contacts" => AppSection.Contacts,
        "search" => AppSection.Search,
        "stats" => AppSection.Stats,
        _ => AppSection.Conversations,
    };

    private static Type PageType(string pageTypeName) => pageTypeName switch
    {
        AppNavigation.ConversationsPageTypeName => typeof(ConversationsPage),
        AppNavigation.ContactsPageTypeName => typeof(ContactsPage),
        AppNavigation.SearchPageTypeName => typeof(SearchPage),
        AppNavigation.StatsPageTypeName => typeof(StatsPage),
        AppNavigation.SettingsPageTypeName => typeof(SettingsPage),
        _ => throw new ArgumentOutOfRangeException(nameof(pageTypeName), pageTypeName, null),
    };

    private void NavigateToPage(Type pageType)
    {
        var options = new FrameNavigationOptions { IsNavigationStackEnabled = false };
        ContentFrame.NavigateToType(pageType, null, options);
    }

    private void AttachVisiblePage()
    {
        switch (ContentFrame.Content)
        {
            case ConversationsPage conversations:
                conversations.Attach(this, _conversations, _timeline);
                break;
            case ContactsPage contacts:
                contacts.Attach(this, _contacts);
                break;
            case SearchPage search:
                search.Attach(this, _search);
                _searchPage = search;
                break;
            case StatsPage stats:
                stats.Attach(this, _stats);
                _statsPage = stats;
                if (_statsStale)
                {
                    stats.Invalidate();
                    _statsStale = false;
                }

                break;
            case SettingsPage settings:
                settings.Attach(this);
                break;
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
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerInterop.RequireHandle(WindowHandle));
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
                    ShowError(PickerInterop.FormatFailure("选择文件", ex));
                }
            };

            var addFolder = new Button { Content = "添加文件夹…" };
            addFolder.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerInterop.RequireHandle(WindowHandle));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder is not null)
                    {
                        _import.AddPath(folder.Path);
                    }
                }
                catch (Exception ex)
                {
                    ShowError(PickerInterop.FormatFailure("选择文件夹", ex));
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
                addFile.IsEnabled = !_import.IsRunning && IsPickerReady;
                addFolder.IsEnabled = !_import.IsRunning && IsPickerReady;
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
