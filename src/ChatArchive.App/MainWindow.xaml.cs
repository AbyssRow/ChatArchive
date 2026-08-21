using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace ChatArchive.App;

public sealed partial class MainWindow : Window
{
    private readonly ConversationListViewModel _conversations;
    private readonly TimelineViewModel _timeline;
    private readonly SearchViewModel _search;
    private readonly StatsViewModel _stats;
    private readonly ImportViewModel _import;
    private CancellationTokenSource? _queryDebounce;
    private ScrollViewer? _messageScroll;
    private bool _statsLoaded;

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            var services = AppServices.Instance;
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _conversations = new ConversationListViewModel(services.Conversations, dispatcher);
            _timeline = new TimelineViewModel(services.Conversations, services.MediaLocator, dispatcher);
            _search = new SearchViewModel(services.Search, dispatcher);
            _stats = new StatsViewModel(services.Stats);
            _import = new ImportViewModel(services.Database, dispatcher);

            _conversations.ConversationActivated += conversation => _timeline.Load(conversation);
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
            };
            _search.ResultActivated += hit => DispatcherQueue.TryEnqueue(() =>
            {
                SelectNavItem("conversations");
                _timeline.JumpToMessage(hit.MessageId);
            });
            _search.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchViewModel.IsLoading))
                {
                    SearchProgress.Visibility = _search.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (e.PropertyName == nameof(SearchViewModel.HasSearched))
                {
                    SearchLoadMore.Visibility =
                        _search.HasSearched && !string.IsNullOrEmpty(_search.ModeLabel)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    SearchModeLabel.Text = _search.Results.Count > 0
                        ? $"共 {_search.Results.Count:N0} 条（{_search.ModeLabel}）"
                        : _search.ModeLabel;
                }
            };
            _import.ImportFinished += () => _conversations.Reload();
            _import.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportViewModel.IsRunning))
                {
                    SearchLoadMore.IsEnabled = !_import.IsRunning;
                }
            };

            ConversationListControl.ItemsSource = _conversations.Conversations;
            MessageListControl.ItemsSource = _timeline.Entries;
            SearchResultsList.ItemsSource = _search.Results;

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
        SearchPane.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        StatsPane.Visibility = tag == "stats" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "stats" && !_statsLoaded)
        {
            _statsLoaded = true;
            _stats.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatsViewModel.SummaryLines))
                {
                    StatsText.Text = _stats.SummaryLines;
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

        void RefreshButtons()
        {
            status.Text = _import.StatusText;
            progress.IsIndeterminate = _import.IsRunning;
            progress.Visibility = _import.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        }

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

        _import.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ImportViewModel.StatusText) or nameof(ImportViewModel.IsRunning))
            {
                DispatcherQueue.TryEnqueue(RefreshButtons);
            }
        };
        _import.Paths.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { });

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "导入聊天记录",
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            Content = pathsPanel,
        };
        await dialog.ShowAsync();
        _conversations.Reload();
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
        if (FilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tags)
        {
            var parts = tags.Split('|');
            _conversations.PlatformFilter = parts[0];
            _conversations.KindFilter = parts[1];
            _conversations.Reload();
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
        };
    }

    private void MessageScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_messageScroll is null || e.IsIntermediate)
        {
            return;
        }

        var distanceFromBottom = _messageScroll.ExtentHeight
            - _messageScroll.VerticalOffset - _messageScroll.ViewportHeight;
        if (distanceFromBottom < 80 && _timeline.HasMore && !_timeline.IsLoading)
        {
            _timeline.LoadMoreCommand.Execute(null);
        }
    }

    private async void OnSenderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageEntry entry } && entry.Message.SenderId is long senderId)
        {
            await ShowSenderProfile(senderId);
        }
    }

    private async void OnImageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MessageEntry entry } && entry.ImagePath is not null)
        {
            await ShowImagePreview(entry.ImagePath, entry.Message.Attachments.FirstOrDefault()?.Filename ?? "图片");
        }
    }

    private async System.Threading.Tasks.Task ShowSenderProfile(long senderId)
    {
        var contact = new ContactViewModel(AppServices.Instance.Senders);
        if (!contact.Load(senderId))
        {
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = contact.IdentityLine, FontSize = 13, Opacity = 0.8 });

        panel.Children.Add(new TextBlock { Text = "名称记录", FontSize = 12, Opacity = 0.6 });
        var aliasList = new ListView { MaxHeight = 140, SelectionMode = ListViewSelectionMode.None };
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
        panel.Children.Add(new TextBlock { Text = "出现过的会话（点击跳转）", FontSize = 12, Opacity = 0.6 });
        var conversationList = new ListView { MaxHeight = 200, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None };
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
            var wrap = new ListViewItem { Content = row };
            var targetId = conversation.ConversationId;
            wrap.DoubleTapped += (_, _) =>
            {
                contact.ActivateConversation(targetId);
            };
            conversationList.Items.Add(wrap);
        }

        panel.Children.Add(conversationList);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = contact.DisplayName,
            CloseButtonText = "关闭",
            Content = panel,
        };
        contact.ConversationActivated += id => DispatcherQueue.TryEnqueue(() =>
        {
            dialog.Hide();
            SelectNavItem("conversations");
            var info = AppServices.Instance.Conversations.GetConversation(id)?.Conversation;
            if (info is not null)
            {
                _timeline.Load(info);
            }
        });
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
            var picker = new FileSavePicker { SuggestedFileName = filename };
            picker.FileTypeChoices.Add("图片", new List<string> { Path.GetExtension(imagePath).TrimStart('.') is { Length: > 0 } ext ? ext : "png" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                File.Copy(imagePath, file.Path, overwrite: true);
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
        if (_search.HasSearched)
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
        _search.ExecuteCommand.Execute(null);
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


