using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using ChatArchive.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ChatArchive.App;

public sealed partial class MainWindow : Window
{
    private readonly ConversationListViewModel _conversations;
    private readonly TimelineViewModel _timeline;
    private readonly SearchViewModel _search;
    private readonly StatsViewModel _stats;
    private readonly ImportViewModel _import;
    private CancellationTokenSource? _queryDebounce;
    private bool _loadingMessages;
    private ScrollViewer? _messageScroll;

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

            ConversationListControl.ItemsSource = _conversations.Conversations;
            MessageListControl.ItemsSource = _timeline.Entries;

            _conversations.Reload();
            HookMessageScroll();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow ctor: {ex}\n\n");
            }
            catch
            {
            }

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
        SearchPlaceholder.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        StatsPane.Visibility = tag == "stats" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "stats")
        {
            StatsText.Text = _stats.SummaryLines == "加载中…" ? "加载中…" : _stats.SummaryLines;
            _stats.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatsViewModel.SummaryLines))
                {
                    StatsText.Text = _stats.SummaryLines;
                }
            };
            _stats.Load();
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "导入聊天记录",
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text = "导入功能将在下一阶段接入。\n\n届时在此选择包含 QQ / 微信导出 JSON 的文件夹，应用会递归发现并按内容去重导入。",
                TextWrapping = TextWrapping.Wrap,
            },
        };
        _ = dialog.ShowAsync();
    }

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
        if (FilterCombo.SelectedItem is ComboBoxItem item && item.Tag is string tags && TimelinePane is not null)
        {
            var parts = tags.Split('|');
            _conversations.PlatformFilter = parts[0];
            _conversations.KindFilter = parts[1];
            _conversations.Reload();
        }
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMessages)
        {
            return;
        }

        if (ConversationListControl.SelectedItem is ConversationInfo conversation)
        {
            _loadingMessages = true;
            _conversations.SelectedConversation = conversation;
            _loadingMessages = false;
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

    private void TryScrollToBottomIfNearEnd()
    {
        // 初次加载时把视图停在顶部（最新消息在顶部），无需滚动处理。
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
