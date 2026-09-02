# ChatArchive 应用壳与页面拆分设计

日期：2026-09-01
状态：待批准

## 目标

把 `MainWindow` 从「所有页面的 code-behind」收成长期可维护的应用壳：侧栏用
`NavigationView` + `Frame` 打开真实 `Page`，跨页跳转走显式导航契约，会话列表与
时间线滚动位置在切走再回来时仍然保留。外观与现有交互大体不变。导入仍是壳上的
对话框。

这是「长期可维护」三块工作的第一块。仓库卫生（`inputapp/` gitignore、格式清单
去漂移）和 CI 另开 spec，不并进本轮。

## 背景

当前 `MainWindow.xaml` 用 `Visibility` 切换五个栏（会话/通讯录/搜索/统计/设置），
`MainWindow.xaml.cs` 约 1700 行，同时拥有：

- 窗口壳（标题栏、Mica、`InfoBar`、导入按钮）
- 五个栏的事件与控件接线
- 搜索命中 → 时间线、联系人会话 → 时间线的跳转
- 发送者资料卡、账号转移确认、存储目录选择等对话框
- 若干请求代际 Gate，用来堵住切页和异步完成之间的竞态

2026-08-21 的 UI 收口设计明确推迟了「拆 `MainWindow` 为新页面体系」。此后安全与
无障碍修复继续往窗口里堆状态。再加功能会先在这一层失控。

## 已确认决策

| 决策点 | 结论 |
|---|---|
| 成功标准 | 结构提取 + 显式导航；外观大体不变 |
| ViewModel 寿命 | 壳长期持有现有 ViewModel；Page 只绑定 |
| 页面保活 | `Frame` + 各页 `NavigationCacheMode.Required` |
| 跨页接口 | 壳实现 `IAppNavigator`；页面通过 `IShellPage.Attach` 拿到壳 |
| 导入 | 仍是壳 `PaneFooter` 上的对话框，不改成独立页 |
| 对话框 | 跟触发它的页面走；壳只留导入和窗口级 `InfoBar` |
| 测试 | 导航决策抽纯函数；不上 WinUI UI 自动化 |
| 文档 | 本轮更新 `AGENT.md` 的窗口结构说明 |
| 不做 | schema、导入语义、DI、换外观、CI、gitignore、拆 ViewModel |

## 方案选择

采用 **薄壳 + 缓存 Page + 显式 `IAppNavigator`**。

不采用「只抽 UserControl、窗口继续调度」：窗口仍是中心，维护问题原样保留。
不采用「页面自持 ViewModel + 每次重建」：会丢时间线滚动位置，和现行为不一致。
不采用 Microsoft.Extensions.DependencyInjection / Template Studio 导航服务：
本应用已有 `AppServices` 单例，本轮不替换。

## 架构

```text
MainWindow (壳, IAppShell)
  ├─ 标题栏 / Mica / InfoBar
  ├─ NavigationView
  │    ├─ Menu: 会话 / 通讯录 / 搜索 / 统计
  │    ├─ Settings 项
  │    └─ PaneFooter: 导入按钮
  ├─ Frame ContentFrame
  │    ├─ ConversationsPage  (缓存)  会话列表 + 时间线
  │    ├─ ContactsPage       (缓存)
  │    ├─ SearchPage         (缓存)
  │    ├─ StatsPage          (缓存)
  │    └─ SettingsPage       (缓存)
  ├─ 导入 ContentDialog
  └─ ViewModels（长期持有）
       ConversationList / Timeline / Contacts / Search / Stats / Import
```

数据流：

1. 侧栏点菜单 → 壳 `GoTo(section)`。已在目标栏则不 `Navigate`。
2. 搜索命中或联系人关联会话 → 页面调 `OpenConversation(conversationId, messageId?)`。
3. 壳按 `AppNavigation` 的纯决策切到会话页（或留在会话页），把参数交给
   `ConversationsPage.ApplyConversation`。
4. `ConversationsPage` 按 id 加载会话、调用已有 `ConversationListViewModel.Activate`
   与 `TimelineViewModel.JumpToMessage`，并用本页 `LatestRequestGate` 丢掉过期请求。
5. 页面错误调 `IAppShell.ShowError`，显示窗口 `InfoBar`。

Page 默认构造函数仍无参（`Frame.Navigate(typeof(T))` 需要）。WinUI 会在
`Navigate` 返回前调用 `OnNavigatedTo`，此时 `Attach` 还没发生，所以**会话参数不走
`OnNavigatedTo`**。壳在 `Navigate` 返回后同步执行：`Attach`（幂等）→ 若本次是
`OpenConversation` 则 `ApplyConversation` → `OnShown()`。

构造结束时 `GoTo(AppSection.Conversations)`，对应现在侧栏默认选中「会话」。

## 导航契约

新增内部类型，放在 `src/ChatArchive.App/Navigation/`。

```csharp
internal enum AppSection
{
    Conversations,
    Contacts,
    Search,
    Stats,
    Settings,
}

internal readonly record struct ConversationNavigationArgs(
    long ConversationId,
    long? FocusMessageId);

internal interface IAppNavigator
{
    void GoTo(AppSection section);
    void OpenConversation(long conversationId, long? focusMessageId = null);
}

internal interface IAppShell : IAppNavigator
{
    void ShowError(string message);
    nint WindowHandle { get; }
}

internal interface IShellPage
{
    void Attach(IAppShell shell);
    void OnShown();
}
```

`IAppNavigator` 不再增加方法。窗口句柄只给设置页的 `FolderPicker` 用。

### 纯决策

`AppNavigation` 是无 UI 静态类，供壳调用，也供测试。

```csharp
internal readonly record struct AppSectionDecision(
    bool ShouldNavigate,
    AppSection Section,
    string PageTypeName);

internal readonly record struct ConversationOpenDecision(
    bool ShouldNavigate,
    AppSection Section,
    string PageTypeName,
    ConversationNavigationArgs Args);

internal static class AppNavigation
{
    public const string ConversationsPageTypeName = "ConversationsPage";
    public const string ContactsPageTypeName = "ContactsPage";
    public const string SearchPageTypeName = "SearchPage";
    public const string StatsPageTypeName = "StatsPage";
    public const string SettingsPageTypeName = "SettingsPage";

    public static AppSectionDecision ForSidebar(AppSection current, AppSection target)
    {
        var pageTypeName = PageTypeName(target);
        return new AppSectionDecision(
            ShouldNavigate: current != target,
            Section: target,
            PageTypeName: pageTypeName);
    }

    public static ConversationOpenDecision ForOpenConversation(
        AppSection current,
        long conversationId,
        long? focusMessageId)
    {
        var args = new ConversationNavigationArgs(conversationId, focusMessageId);
        return new ConversationOpenDecision(
            ShouldNavigate: current != AppSection.Conversations,
            Section: AppSection.Conversations,
            PageTypeName: ConversationsPageTypeName,
            Args: args);
    }

    public static string PageTypeName(AppSection section) => section switch
    {
        AppSection.Conversations => ConversationsPageTypeName,
        AppSection.Contacts => ContactsPageTypeName,
        AppSection.Search => SearchPageTypeName,
        AppSection.Stats => StatsPageTypeName,
        AppSection.Settings => SettingsPageTypeName,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };
}
```

决策层返回页面类型**名字**而不是 `System.Type`，避免测试项目引用 WinUI `Page`
类型。壳把名字映射到 `typeof(ConversationsPage)` 等。

### 壳实现规则

侧栏选中与 `Frame.Navigate` 必须分开，避免 `OpenConversation` 先无参导航再带参导航。

- `SelectSection(AppSection)`：只改 `NavigationView` 选中项（会话/通讯录/搜索/统计用
  `Tag`，设置用 `IsSettingsSelected`）。
- `GoTo`：`SelectSection`，再按 `ForSidebar` 决定是否 `Navigate`。需要导航时
  `ContentFrame.Navigate(pageType)`（不传会话参数），返回后 `Attach` + `OnShown`。
  已在目标页则不 `Navigate`，仍调用 `OnShown`。
- `OpenConversation`：计算 `ForOpenConversation`。设置
  `_isApplyingConversationNavigation = true`，再 `SelectSection(Conversations)`。
  `SelectionChanged` 看到该标志时**只更新当前栏记录，不 `Navigate`、不 `OnShown`**。
  然后壳自己执行一次：需要则 `Navigate(typeof(ConversationsPage))`，`Attach`，
  `ApplyConversation(args)`，`OnShown`。已在会话页则不 `Navigate`，只
  `ApplyConversation` + `OnShown`。`OnShown` 不得重置时间线滚动。
- 侧栏 `SelectionChanged` 在无上述标志时只调用 `GoTo`，不再改五个 `Visibility`。
- `Frame.CacheSize` ≥ 5。不启用返回栈；`IsBackButtonVisible` 保持 `Collapsed`。
- 导入完成后由壳调用 `_conversations.Reload()`、现有 `SearchViewModel.LoadOptions()`，
  以及已 Attach 的 `StatsPage.Invalidate()`（尚未创建则记下 `_statsStale`，该页
  第一次 `OnShown` 时加载）。不把导入完成事件散到五个页面各自订阅。

### 会话页应用跳转

`ConversationsPage.ApplyConversation(ConversationNavigationArgs args)`：

1. 用本页 `LatestRequestGate.Next()` 记代际。
2. 后台 `ConversationRepository.GetConversation(args.ConversationId)`。
3. 完成时若代际已过期则丢弃。
4. 找不到会话则 `shell.ShowError`：有 `FocusMessageId` 时用现句
   「打开搜索结果失败：未找到对应会话」，否则用「打开会话失败：未找到对应会话」。
5. 找到则 `_conversations.Activate(info)`，列表 `SelectedItem` 对齐该会话，
   若 `FocusMessageId` 有值则 `_timeline.JumpToMessage`。
6. 时间线分页滚动钩子留在本页（现 `HookMessageScroll` /
   `PositionTimelineAtBottom` / `FocusTimelineMessage` 整段迁移）。

无参数进入会话页（用户点侧栏「会话」）只走 `OnShown`，**不得**调用
`ApplyConversation`，不得重载时间线，不得改滚动位置。

## 页面职责

共享转换器从 `MainWindow.xaml` 的 `NavigationView.Resources` 挪到 `App.xaml` 的
`Application.Resources`，五个页面都能用。各页自己的 `DataTemplate` 跟页面走，
不再堆在壳上。

### ConversationsPage

对应现 `TimelinePane`。绑定 `ConversationListViewModel` 与 `TimelineViewModel`。
负责：标题搜索、筛选、会话列表选择、时间线虚拟化滚动、加载更多、图片预览/另存为、
打开附件、发送者资料卡。

发送者资料卡从 `MainWindow.ShowSenderProfile` 抽到
`src/ChatArchive.App/Views/SenderProfileDialog.cs`。页面调用它，传入已有
`ContactViewModel` 加载结果与 `XamlRoot`。资料卡里「查看会话」仍走
`IAppNavigator.OpenConversation`。

### ContactsPage

对应现 `ContactsRoot`。绑定 `ContactsViewModel`。负责：搜索联系人、新建/保存/删除、
头像、绑定账号、账号转移确认对话框、关联会话列表。点关联会话调用
`OpenConversation(conversationId)`（无 `FocusMessageId`）。

`OnShown` 保留现有 `ReloadContactsAsync(selectedId)` 行为。

### SearchPage

对应现 `SearchPane`。绑定 `SearchViewModel`。`SearchOptionsReloadGate`、
选项刷新恢复、筛选控件锁定随本页迁移，不再留在壳上。点命中后调用
`OpenConversation(hit.ConversationId, hit.MessageId)`，不再由窗口直接操作会话列表。

`OnShown` 把焦点放到搜索框。

### StatsPage

对应现 `StatsPane`。绑定 `StatsViewModel`。提供 `Invalidate()`：把本页「已加载」
清掉。`OnShown` 在未加载时调用 `_stats.Load()`。壳 Attach 时记住该页实例。导入
完成后调用 `Invalidate()`；若当前栏已是统计，再立刻 `OnShown()`，否则等下次进入。

### SettingsPage

对应现 `SettingsPane`。负责存储用量展示、打开目录、改目录、恢复默认。
`OnShown` 刷新用量展示。`FolderPicker` 通过 `IAppShell.WindowHandle` 做
`InitializeWithWindow`。改目录后仍提示重启，不在本轮改成热切换 `AppServices`。

## 壳职责（完成后的 MainWindow）

只保留：

- 构造 ViewModel、`Attach` 接线、导入 `ImportFinished` 刷新
- `NavigationView` + `Frame` + `IAppShell` 实现
- 导入按钮与导入对话框（现有进度保留、关闭不丢运行状态的行为不变）
- 窗口级 `InfoBar`
- 标题栏 / Mica
- 侧栏折叠时隐藏导入按钮（现行为）

`MainWindow.xaml.cs` 不应再出现会话列表、时间线、搜索框、联系人表单的事件处理。
这些名字若还在，就是拆分不彻底。

## 错误处理

- 页面与对话框失败统一 `IAppShell.ShowError`。找不到会话的两句现有文案按是否带
  `FocusMessageId` 保留；其余中文句子不改。
- `OpenConversation` 本身不 catch：加载失败由 `ConversationsPage.ApplyConversation` 报告。
- `ContentDialog` 继续走现有 `ShowSafeAsync`，避免叠对话框崩溃。
- 构造失败仍写崩溃日志并抛出。

## 测试

新增 `tests/ChatArchive.App.Tests/AppNavigationTests.cs`，不启动窗口：

1. `ForSidebar` 当前等于目标 → `ShouldNavigate == false`。
2. `ForSidebar` 搜索 → 会话 → `ShouldNavigate == true`，`PageTypeName == "ConversationsPage"`。
3. `ForSidebar` 任意栏 → 设置 → `PageTypeName == "SettingsPage"`。
4. `ForOpenConversation` 当前已是会话 → `ShouldNavigate == false`，参数带上 id 与可选 message id。
5. `ForOpenConversation` 当前是搜索 → `ShouldNavigate == true`，目标会话页，参数完整。
6. `ConversationNavigationArgs` 无焦点消息时 `FocusMessageId` 为 null（通讯录跳转）。

现有 ViewModel / 转换器 / 搜索恢复 / 导入呈现测试保持不动。本轮不改 xUnit/MTP
项目配置，不引入 WinUI 自动化。

人工冒烟（临时数据或档案副本，不写 `E:\ChatArchive\chat_archive.db`）：

1. 会话 → 搜索 → 会话：列表选中项与时间线滚动位置仍在。
2. 搜索命中打开会话：侧栏切到会话，时间线跳到该消息。
3. 通讯录关联会话打开：侧栏切到会话，选中对应会话。
4. 导入对话框：运行中关闭再打开仍能看到进度/结果；完成后会话列表刷新。
5. 设置更改目录：文件夹选择器能弹出；确认后提示重启。
6. 发送者资料卡与账号转移确认仍可用。

终验：`dotnet build` 零警告零错误（`TreatWarningsAsErrors` 已开）；现有 App/Core
测试按本仓库惯用方式跑通（xUnit v3 可执行文件，不把 MTP 配置问题扩进本轮）。

## 文件结构

```text
src/ChatArchive.App/
  App.xaml                         # 合并共享转换器
  MainWindow.xaml                  # 壳：标题栏、InfoBar、NavigationView、Frame
  MainWindow.xaml.cs               # 壳 + IAppShell
  Navigation/
    AppSection.cs
    ConversationNavigationArgs.cs
    IAppNavigator.cs
    IAppShell.cs
    IShellPage.cs
    AppNavigation.cs
  Views/
    ConversationsPage.xaml / .cs
    ContactsPage.xaml / .cs
    SearchPage.xaml / .cs
    StatsPage.xaml / .cs
    SettingsPage.xaml / .cs
    SenderProfileDialog.cs
    Converters.cs                  # 保持
    TimelineTemplateSelector.cs    # 保持
  ViewModels/                      # 不拆类型，只改引用方
AGENT.md                           # 窗口结构改为壳 + 五个 Page
tests/ChatArchive.App.Tests/
  AppNavigationTests.cs            # 新增
```

`ChatArchive.App.csproj` 按 WinUI 默认包含新 XAML，不手写特殊编译项。

## 明确不做

- 不修改 SQLite schema、导入去重、解析器、媒体复制。
- 不读取、修改或提交未跟踪的 `inputapp/`。
- 不把导入改成独立页，不改设置热切换数据目录。
- 不引入 DI 容器，不替换 `AppServices` 单例。
- 不拆 ViewModel，不重写 Gate 语义，只把它们迁到所属页面。
- 不改视觉样式、中文文案、导航图标。
- 不做返回栈、不做多窗口。
- 不在本轮加 CI、不加 `inputapp/` gitignore、不清理 HTML 格式文档漂移。
- 不上 UI 自动化，不改 xUnit/MTP 包。

## 后续子项目（本 spec 不实施）

2. **仓库卫生**：`inputapp/` 与发布目录忽略规则；README / `AGENT.md` 格式清单与代码一致；删除过期 HTML 适配器描述。
3. **CI**：GitHub Actions 跑测试与发布探测。

## 成功标准

- 五个栏都是 `Page`，侧栏通过 `Frame` 打开，而不是 `Visibility` 切换。
- 跨页打开会话只经过 `IAppNavigator.OpenConversation`。
- 切到搜索或通讯录再回会话，选中会话与时间线滚动位置仍在。
- `MainWindow.xaml.cs` 不再处理会话/时间线/搜索/通讯录/设置控件事件。
- `AppNavigationTests` 与现有测试通过；上述冒烟清单人工走过。
- `AGENT.md` 的代码地图写的是壳 + 五个 Page。
