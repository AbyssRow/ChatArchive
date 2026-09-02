# App Shell and Pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `MainWindow` 收成薄壳，用 `NavigationView` + `Frame` 打开五个缓存 `Page`，跨页打开会话只走 `IAppNavigator.OpenConversation`。

**Architecture:** 壳长期持有现有 ViewModel。`AppNavigation` 是无 UI 纯决策。壳用 `SelectSection` 改侧栏、用 `Frame.NavigateToType`（关闭导航栈）切页，`Navigate` 返回后再 `Attach` / `ApplyConversation` / `OnShown`。会话参数不走 `OnNavigatedTo`。各页 `NavigationCacheMode="Required"`，在 XAML 根上设置。

**Tech Stack:** C# / .NET 10 / WinUI 3 (Windows App SDK) / CommunityToolkit.Mvvm / xUnit.net v3 in-process runner。

**Spec:** `docs/superpowers/specs/2026-09-01-app-shell-pages-design.md`

## Global Constraints

- 不修改 SQLite schema、导入去重、解析器、媒体复制。
- 不读取、修改或提交未跟踪的 `inputapp/`。
- 不把导入改成独立页，不改设置热切换数据目录。
- 不引入 DI 容器，不替换 `AppServices` 单例。
- 不拆 ViewModel，不重写 Gate 语义，只把它们迁到所属页面。
- 不改视觉样式、中文文案、导航图标。
- 不做返回栈、不做多窗口。
- 不在本轮加 CI、不加 `inputapp/` gitignore、不清理 HTML 格式文档漂移。
- 不上 UI 自动化，不改 xUnit/MTP 包。
- 对能纯逻辑验证的行为先写失败测试；XAML 只编译验证 + 人工冒烟。
- 每次提交只暂存任务列出的文件，保留用户的其他工作区内容。`docs/` 被 gitignore，提交 spec/plan 时用 `git add -f`。

## WinUI notes (Context7 + Microsoft Learn)

- 默认每次 `Navigate` 都会 new 一个 `Page`。要复用实例，必须在 **XAML 根或构造函数** 设 `NavigationCacheMode`；之后再改无效。
- `Required` 无视 `Frame.CacheSize` 也缓存。本计划五个页都用 `Required`，`CacheSize="5"` 只作双保险。
- `OnNavigatedTo` 在 `Navigate` 返回前就会跑，此时壳还没 `Attach`。会话参数禁止走 `OnNavigatedTo`。
- Frame 在一次 Navigate 通知期间禁止重入。`OpenConversation` 不得先 `GoTo`（会 Navigate）再 Navigate。侧栏选中与 Frame 导航必须分开。
- WinUI Gallery：作为互斥内容区时用 `FrameNavigationOptions { IsNavigationStackEnabled = false }` 再 `NavigateToType`。本应用 `IsBackButtonVisible` 已是 `Collapsed`，按同样方式关掉返回栈。

## File Structure

- Create: `src/ChatArchive.App/Navigation/AppSection.cs`
- Create: `src/ChatArchive.App/Navigation/ConversationNavigationArgs.cs`
- Create: `src/ChatArchive.App/Navigation/AppNavigation.cs`
- Create: `src/ChatArchive.App/Navigation/IAppNavigator.cs`
- Create: `src/ChatArchive.App/Navigation/IAppShell.cs`
- Create: `src/ChatArchive.App/Navigation/IShellPage.cs`
- Create: `src/ChatArchive.App/Views/ConversationsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/ContactsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/SearchPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/StatsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/SettingsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/SenderProfileDialog.cs`
- Create: `tests/ChatArchive.App.Tests/AppNavigationTests.cs`
- Modify: `src/ChatArchive.App/App.xaml` — 共享转换器
- Modify: `src/ChatArchive.App/MainWindow.xaml` — 壳：标题栏、InfoBar、NavigationView、Frame
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs` — 壳 + `IAppShell`
- Modify: `AGENT.md` — 代码地图改为壳 + 五个 Page
- Keep: `ViewModels/*`、`Views/Converters.cs`、`Views/TimelineTemplateSelector.cs`

测试命令（本仓库不改 MTP；先 build 再跑 xUnit v3 exe）：

```powershell
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --nologo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -class 'ChatArchive.App.Tests.AppNavigationTests' -noLogo -automated
```

---

### Task 1: AppNavigation 纯决策

**Files:**
- Create: `src/ChatArchive.App/Navigation/AppSection.cs`
- Create: `src/ChatArchive.App/Navigation/ConversationNavigationArgs.cs`
- Create: `src/ChatArchive.App/Navigation/AppNavigation.cs`
- Create: `src/ChatArchive.App/Navigation/IAppNavigator.cs`
- Create: `src/ChatArchive.App/Navigation/IAppShell.cs`
- Create: `src/ChatArchive.App/Navigation/IShellPage.cs`
- Test: `tests/ChatArchive.App.Tests/AppNavigationTests.cs`

**Interfaces:**
- Consumes: 无。程序集已有 `[assembly: InternalsVisibleTo("ChatArchive.App.Tests")]`（`AppSettings.cs`）。
- Produces: `AppSection`、`ConversationNavigationArgs`、`AppSectionDecision`、`ConversationOpenDecision`、`AppNavigation.ForSidebar` / `ForOpenConversation` / `PageTypeName`、`IAppNavigator`、`IAppShell`、`IShellPage`。

- [ ] **Step 1: Write the failing tests**

Create `tests/ChatArchive.App.Tests/AppNavigationTests.cs`:

```csharp
using ChatArchive.App.Navigation;
using Xunit;

namespace ChatArchive.App.Tests;

public sealed class AppNavigationTests
{
    [Fact]
    public void ForSidebar_same_section_does_not_navigate()
    {
        var decision = AppNavigation.ForSidebar(AppSection.Search, AppSection.Search);
        Assert.False(decision.ShouldNavigate);
        Assert.Equal(AppSection.Search, decision.Section);
        Assert.Equal(AppNavigation.SearchPageTypeName, decision.PageTypeName);
    }

    [Fact]
    public void ForSidebar_search_to_conversations_navigates()
    {
        var decision = AppNavigation.ForSidebar(AppSection.Search, AppSection.Conversations);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(AppSection.Conversations, decision.Section);
        Assert.Equal(AppNavigation.ConversationsPageTypeName, decision.PageTypeName);
    }

    [Fact]
    public void ForSidebar_any_section_to_settings_uses_settings_page_name()
    {
        var decision = AppNavigation.ForSidebar(AppSection.Stats, AppSection.Settings);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(AppNavigation.SettingsPageTypeName, decision.PageTypeName);
    }

    [Fact]
    public void ForOpenConversation_when_already_on_conversations_does_not_navigate()
    {
        var decision = AppNavigation.ForOpenConversation(AppSection.Conversations, 42, 99);
        Assert.False(decision.ShouldNavigate);
        Assert.Equal(AppSection.Conversations, decision.Section);
        Assert.Equal(AppNavigation.ConversationsPageTypeName, decision.PageTypeName);
        Assert.Equal(42, decision.Args.ConversationId);
        Assert.Equal(99, decision.Args.FocusMessageId);
    }

    [Fact]
    public void ForOpenConversation_from_search_navigates_with_ids()
    {
        var decision = AppNavigation.ForOpenConversation(AppSection.Search, 7, 8);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(7, decision.Args.ConversationId);
        Assert.Equal(8, decision.Args.FocusMessageId);
    }

    [Fact]
    public void ForOpenConversation_from_contacts_has_null_focus_message()
    {
        var decision = AppNavigation.ForOpenConversation(AppSection.Contacts, 3, null);
        Assert.True(decision.ShouldNavigate);
        Assert.Equal(3, decision.Args.ConversationId);
        Assert.Null(decision.Args.FocusMessageId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run the build + exe commands above.

Expected: FAIL，找不到 `ChatArchive.App.Navigation`。

- [ ] **Step 3: Implement types exactly as the spec**

`AppSection.cs`:

```csharp
namespace ChatArchive.App.Navigation;

internal enum AppSection
{
    Conversations,
    Contacts,
    Search,
    Stats,
    Settings,
}
```

`ConversationNavigationArgs.cs`:

```csharp
namespace ChatArchive.App.Navigation;

internal readonly record struct ConversationNavigationArgs(
    long ConversationId,
    long? FocusMessageId);
```

`AppNavigation.cs`：把 spec「纯决策」整段原样放进去（`AppSectionDecision`、`ConversationOpenDecision`、`AppNavigation`，未知 `AppSection` 抛 `ArgumentOutOfRangeException`）。

`IAppNavigator.cs` / `IAppShell.cs` / `IShellPage.cs`：把 spec「导航契约」三个接口原样放进去。`IShellPage` 含 `Attach(IAppShell shell)` 和 `OnShown()`。

- [ ] **Step 4: Re-run tests**

Same commands. Expected: PASS（6 个 Fact）。

- [ ] **Step 5: Commit**

```powershell
git add src/ChatArchive.App/Navigation tests/ChatArchive.App.Tests/AppNavigationTests.cs
git commit -m "feat: add app navigation decision types"
```

---

### Task 2: 壳 Frame 与五个 Page 切过去

**Files:**
- Modify: `src/ChatArchive.App/App.xaml`
- Modify: `src/ChatArchive.App/MainWindow.xaml`
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs`
- Create: `src/ChatArchive.App/Views/ConversationsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/ContactsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/SearchPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/StatsPage.xaml` / `.xaml.cs`
- Create: `src/ChatArchive.App/Views/SettingsPage.xaml` / `.xaml.cs`

**Interfaces:**
- Consumes: Task 1 的全部 Navigation 类型。`ConversationListViewModel.Activate(ConversationInfo)`、`TimelineViewModel.JumpToMessage(long)`、`SearchViewModel.LoadOptions()`、`ConversationRepository.GetConversation(long)`。
- Produces: `MainWindow : Window, IAppShell`；五个 `Page, IShellPage`；`ConversationsPage.ApplyConversation(ConversationNavigationArgs)`；`SearchPage.ReloadOptions()`；`StatsPage.Invalidate()`。

- [ ] **Step 1: Move converters into App.xaml**

把 `MainWindow.xaml` 里 `NavigationView.Resources` 中的 8 个 converter 实例挪到 `App.xaml` 的 `Application.Resources`（`XamlControlsResources` 合并字典之后）：

```xml
xmlns:local="using:ChatArchive.App.Views"
...
<local:MsToDateTimeConverter x:Key="MsToDateTime" Format="yyyy-MM-dd HH:mm" />
<local:PlatformLabelConverter x:Key="PlatformLabel" />
<local:KindGlyphConverter x:Key="KindGlyph" />
<local:InverseBoolConverter x:Key="InverseBool" />
<local:BoolToVisibilityConverter x:Key="BoolToVis" />
<local:NullToCollapsedConverter x:Key="NullToCollapsed" />
<local:PathToImageSourceConverter x:Key="PathToImageSource" />
<local:CountTextConverter x:Key="CountText" />
```

各页 `DataTemplate` 跟页面走，不要留在壳上。

- [ ] **Step 2: Replace MainWindow content with Frame**

`NavigationView` 菜单项、Settings、`PaneFooter` 导入按钮保持原样。把五个 `Visibility` 面板从 `MainWindow.xaml` 删掉，换成：

```xml
<Frame x:Name="ContentFrame" CacheSize="5" />
```

`xmlns:local` 仍指向 `ChatArchive.App.Views`。

- [ ] **Step 3: Create five pages with NavigationCacheMode=Required**

每个页面根元素必须是：

```xml
<Page
    x:Class="ChatArchive.App.Views.ConversationsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:ChatArchive.App.Views"
    NavigationCacheMode="Required">
```

搬移（保持 `x:Name` 和 Click 名字，改 code-behind 归属）：

| 源（MainWindow.xaml） | 目标 |
|---|---|
| `TimelinePane` 整块 + 会话/时间线 DataTemplate（Conversation/Separator/MessageBody/Incoming/Outgoing/System + TimelineSelector） | `ConversationsPage` |
| `ContactsRoot` 整块 + 联系人相关 DataTemplate | `ContactsPage` |
| `SearchPane` 整块 + 搜索结果 DataTemplate | `SearchPage` |
| `StatsPane` | `StatsPage` |
| `SettingsPane` | `SettingsPage` |

时间线模板里的 `Click="OnImageAttachmentClick"` 等留在 ConversationsPage。

每个 `.xaml.cs`：`sealed partial class XPage : Page, IShellPage`，无参构造函数只 `InitializeComponent()`。`NavigationCacheMode` 不要在构造函数之后再赋值。

- [ ] **Step 4: Implement shell navigation helpers on MainWindow**

`MainWindow` 实现 `IAppShell`。增加字段：

```csharp
private AppSection _currentSection = AppSection.Conversations;
private bool _isApplyingConversationNavigation;
private bool _statsStale;
private SearchPage? _searchPage;
private StatsPage? _statsPage;
```

`WindowHandle`：

```csharp
public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);
```

`ShowError` 保持现有 `AppInfoBar` 逻辑。

页面类型映射：

```csharp
private static Type PageType(string pageTypeName) => pageTypeName switch
{
    AppNavigation.ConversationsPageTypeName => typeof(ConversationsPage),
    AppNavigation.ContactsPageTypeName => typeof(ContactsPage),
    AppNavigation.SearchPageTypeName => typeof(SearchPage),
    AppNavigation.StatsPageTypeName => typeof(StatsPage),
    AppNavigation.SettingsPageTypeName => typeof(SettingsPage),
    _ => throw new ArgumentOutOfRangeException(nameof(pageTypeName), pageTypeName, null),
};
```

关掉返回栈的导航（WinUI Gallery 互斥内容区写法）：

```csharp
private void NavigateToPage(Type pageType)
{
    var options = new FrameNavigationOptions { IsNavigationStackEnabled = false };
    ContentFrame.NavigateToType(pageType, null, options);
}

private void AttachVisiblePage()
{
    if (ContentFrame.Content is not IShellPage page)
    {
        return;
    }

    page.Attach(this);
    if (page is SearchPage searchPage)
    {
        _searchPage = searchPage;
    }

    if (page is StatsPage statsPage)
    {
        _statsPage = statsPage;
        if (_statsStale)
        {
            statsPage.Invalidate();
            _statsStale = false;
        }
    }
}
```

`SelectSection`：会话/通讯录/搜索/统计按 `Tag`（`conversations`/`contacts`/`search`/`stats`）选 `NavigationViewItem`；设置用 `Nav.SelectedItem = Nav.SettingsItem`（或现有 Settings 选中方式）。只改选中，不 Navigate。

`GoTo(AppSection section)`：

```csharp
public void GoTo(AppSection section)
{
    var decision = AppNavigation.ForSidebar(_currentSection, section);
    SelectSection(section);
    _currentSection = section;
    if (decision.ShouldNavigate)
    {
        NavigateToPage(PageType(decision.PageTypeName));
        AttachVisiblePage();
    }

    if (ContentFrame.Content is IShellPage page)
    {
        page.OnShown();
    }
}
```

`OpenConversation`：

```csharp
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
```

`Nav_SelectionChanged`：若 `_isApplyingConversationNavigation`，只根据选中项更新 `_currentSection`，**不** `Navigate`、**不** `OnShown`。否则：Settings → `GoTo(Settings)`；`NavigationViewItem.Tag` 映射到 `AppSection` 后 `GoTo`。删掉全部 `Visibility` 切换。

构造末尾：绑定 ViewModel 事件（错误 → `ShowError`，导入完成见下），然后 `GoTo(AppSection.Conversations)`。不要再对已删除的控件设 `ItemsSource`。

导入完成：

```csharp
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
```

导入对话框、`OnImportClick`、侧栏折叠隐藏导入按钮留在壳上。

- [ ] **Step 5: Implement IShellPage on each page**

共同模式：`Attach` 保存 `_shell`、接好 ViewModel（由壳传入——见下）、幂等（已 Attach 则 return）。壳在 `AttachVisiblePage` 之前无法把 VM 塞进无参构造函数。

用页面字段 + `Attach` 从壳取 VM。给 `IAppShell` **不要**加 VM getter（spec 禁止扩展 `IAppNavigator`；`IAppShell` 目前只有 `ShowError` 和 `WindowHandle`）。

因此：`Attach` 时页面从 `AppServices.Instance` **只读仓储**不够，因为 VM 必须是壳那一份。做法：壳在 `AttachVisiblePage` 里对具体类型调用强类型 `Attach` 重载，接口 `Attach(IAppShell)` 仍满足 spec：

```csharp
// ConversationsPage
public void Attach(IAppShell shell) =>
    throw new InvalidOperationException("Use Attach(IAppShell, ConversationListViewModel, TimelineViewModel).");

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
    _attached = true;
}
```

壳：

```csharp
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
        break;
    case SettingsPage settings:
        settings.Attach(this);
        break;
}
```

接口方法 `IShellPage.Attach(IAppShell)` 在各页转调对应 internal 重载会缺 VM。让接口 `Attach(IAppShell)` 在各页成为 no-op 或抛错，壳只走强类型重载。spec 的 `IShellPage.Attach` 仍由页面实现（空实现即可），壳用强类型 Attach。不要把 VM 放到 `IAppShell` 上。

把下列方法从 `MainWindow.xaml.cs` **原样搬**到对应页面（改 `ShowError(...)` 为 `_shell!.ShowError(...)`）：

ConversationsPage：`ConversationQuery_TextChanged`、`Filter_SelectionChanged`、`ConversationList_SelectionChanged`、`HookMessageScroll`、`MessageScroll_ViewChanged`、`PositionTimelineAtBottom`、`TryPositionTimelineAtBottom`、`FocusTimelineMessage`、`FindScrollViewer`、`OnSenderClick`、`OnImageAttachmentClick`、`OnAttachmentOpenClick`、`ShowSenderProfile`（本任务先整段搬过来）、`ShowImagePreview`、`_queryDebounce`、`_messageScroll`、`_messagePagingReady`、`_initialTimelinePosition`、`_senderProfileGate`。`OnShown`：空实现（不得重置滚动、不得 `ApplyConversation`）。

`ApplyConversation`：

```csharp
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
```

成功路径的异常文案保持现有「打开搜索结果失败：{ex.Message}」/「打开会话失败：{ex.Message}」。找不到会话的两句按是否有 `FocusMessageId` 分支。

ContactsPage：搬 `ReloadContactsAsync`、`ContactsSearchBox_TextChanged`、`ContactsListView_SelectionChanged`、`UpdateContactDetailView`、全部联系人按钮处理、账号转移对话框、`ContactConversation_Click` 改为 `_shell!.OpenConversation(conv.ConversationId)`。`OnShown` 调用 `ReloadContactsAsync(_contacts.SelectedContact?.Id)`。Folder/File picker 需要窗口句柄时用 `_shell.WindowHandle`。

SearchPage：搬 `ReloadSearchOptions`（改名为 `ReloadOptions`）、`SearchOptions_Reloaded`、`SetSearchInteractionEnabled`、`SearchBox_KeyDown`、`OnSearchClick`、筛选/日期、`OnSearchLoadMoreClick`、`SearchResult_Click`、`RunSearch`、`UpdateSearchSummary`、`ComboTag`、`SearchOptionsReloadGate`、pending record。`SearchResult_Click`：`_shell!.OpenConversation(proxy.Hit.ConversationId, proxy.Hit.MessageId)`。删除 `OpenSearchResult` 和 `_searchResultActivationGate`（过期请求改由 ConversationsPage 的 gate 处理）。`OnShown`：`SearchBox.Focus(FocusState.Programmatic)`。

StatsPage：`Invalidate()` 设 `_loaded = false`。`OnShown`：若 `!_loaded` 则 `_stats.Load(); StatsText.Text = _stats.SummaryLines; _loaded = true;`。错误走 `_shell.ShowError`。

SettingsPage：搬 `RefreshSettingsView`、三个设置按钮。`FolderPicker` 用 `_shell.WindowHandle`。`OnShown` 调 `RefreshSettingsView()`。

- [ ] **Step 6: Build and run App tests**

```powershell
dotnet build 'src\ChatArchive.App\ChatArchive.App.csproj' --nologo
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --nologo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -noLogo -automated
```

Expected: 编译 0 警告 0 错误；现有 App 测试 + `AppNavigationTests` 全过。

确认 `MainWindow.xaml.cs` 已没有这些名字：`ConversationQueryBox`、`SearchBox`、`ContactsListView`、`SettingsDataPathText`、`TimelinePane`。导入相关和 `ContentFrame`/`Nav` 可以留。

- [ ] **Step 7: Commit**

```powershell
git add src/ChatArchive.App/App.xaml src/ChatArchive.App/MainWindow.xaml src/ChatArchive.App/MainWindow.xaml.cs src/ChatArchive.App/Views
git commit -m "feat: host cached pages in the app shell"
```

---

### Task 3: 抽出 SenderProfileDialog

**Files:**
- Create: `src/ChatArchive.App/Views/SenderProfileDialog.cs`
- Modify: `src/ChatArchive.App/Views/ConversationsPage.xaml.cs`

**Interfaces:**
- Consumes: 现 ConversationsPage 里的 `ShowSenderProfile` 整段；`ContactViewModel.LoadAsync`；`IAppShell.ShowError`；`ShowSafeAsync`。
- Produces: `internal static Task ShowAsync(XamlRoot xamlRoot, long senderId, IAppShell shell, Action onConversationsChanged)`。

- [ ] **Step 1: Move the dialog builder**

把 `ShowSenderProfile` 从 ConversationsPage 挪到 `SenderProfileDialog.ShowAsync`。`XamlRoot` 用页面的 `XamlRoot`，不要用 Window。解除关联成功后调用 `onConversationsChanged`（壳持有的 `_conversations.Reload`）。若资料卡里有打开会话，走 `shell.OpenConversation`。

ConversationsPage.OnSenderClick 改为：

```csharp
await SenderProfileDialog.ShowAsync(XamlRoot, senderId, _shell!, () => _conversations!.Reload());
```

- [ ] **Step 2: Build**

```powershell
dotnet build 'src\ChatArchive.App\ChatArchive.App.csproj' --nologo
```

Expected: 0 警告 0 错误。

- [ ] **Step 3: Commit**

```powershell
git add src/ChatArchive.App/Views/SenderProfileDialog.cs src/ChatArchive.App/Views/ConversationsPage.xaml.cs
git commit -m "refactor: extract sender profile dialog from conversations page"
```

---

### Task 4: 文档与壳瘦身验收

**Files:**
- Modify: `AGENT.md`（本地文件，被 gitignore；改完不要 `git add`）
- Modify: `docs/superpowers/specs/2026-09-01-app-shell-pages-design.md`（状态改为已批准）

**Interfaces:**
- Consumes: 完成后的目录结构。
- Produces: AGENT.md 代码地图与开发准则指向壳 + 五个 Page。

- [ ] **Step 1: Update AGENT.md section 3**

把

```
│       ├── MainWindow.xaml              # 导航与主工作区布局
│       └── MainWindow.xaml.cs           # 导入对话框 (addFile / addFolder) 与交互逻辑
```

换成：

```
│       ├── Navigation\                  # AppSection, IAppNavigator, AppNavigation
│       ├── Views\                       # Conversations/Contacts/Search/Stats/Settings Page
│       ├── MainWindow.xaml              # 壳：标题栏、InfoBar、NavigationView、Frame、导入按钮
│       └── MainWindow.xaml.cs           # IAppShell：GoTo / OpenConversation / 导入对话框
```

在「注意事项」加一条：跨页打开会话只许走 `IAppNavigator.OpenConversation`，不要在 Page 里改其它页的控件。

- [ ] **Step 2: Mark the spec approved**

`docs/superpowers/specs/2026-09-01-app-shell-pages-design.md` 状态改为 `已批准`。

- [ ] **Step 3: Final verification**

```powershell
dotnet build --nologo
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --nologo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -noLogo -automated
```

Expected: 全绿。`MainWindow.xaml.cs` 不含会话列表/时间线/搜索框/联系人表单/设置用量的事件处理。

人工冒烟（临时数据或档案副本，**不要写** `E:\ChatArchive\chat_archive.db`）：

1. 会话 → 搜索 → 会话：选中项与时间线滚动仍在。
2. 搜索命中打开会话：侧栏到会话，时间线跳到该消息。
3. 通讯录关联会话打开：侧栏到会话，选中对应会话。
4. 导入对话框：运行中关闭再打开仍有进度/结果；完成后会话列表刷新。
5. 设置更改目录：文件夹选择器能弹出。
6. 发送者资料卡与账号转移确认仍可用。

- [ ] **Step 4: Commit tracked docs**

```powershell
git add -f docs/superpowers/specs/2026-09-01-app-shell-pages-design.md
git commit -m "docs: mark app shell pages spec approved"
```

不要提交 `AGENT.md`。

---

## Self-review

**Spec coverage:**
- 薄壳 + Frame + 五个 Required 缓存 Page → Task 2
- `IAppNavigator` / `IAppShell` / `IShellPage` / `AppNavigation` → Task 1
- 侧栏与 Navigate 分开、OpenConversation 不重入 → Task 2 Step 4
- 会话参数不走 OnNavigatedTo → Task 2
- ApplyConversation 文案与 LatestRequestGate → Task 2 Step 5
- 对话框跟页面；导入留壳 → Task 2 / Task 3
- SearchOptionsReloadGate 随 SearchPage；导入后 ReloadOptions → Task 2
- Stats Invalidate / stale → Task 2
- Settings WindowHandle → Task 2
- AppNavigationTests 六条 → Task 1
- AGENT.md → Task 4
- 明确不做全部写入 Global Constraints

**Placeholder scan:** 无 TBD/TODO；测试与关键实现都有代码。

**Type consistency:** `ForOpenConversation` 返回 `ConversationOpenDecision`；`ApplyConversation(ConversationNavigationArgs)`；`IAppShell.ShowError` / `WindowHandle`；页面强类型 `Attach` 与接口 `Attach(IAppShell)` 并存，壳只走强类型以免把 VM 塞进 `IAppShell`。
