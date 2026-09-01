# ChatArchive App Safety and Accessibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 防止账号被静默转移和导入状态失联，保证搜索选项并发刷新不改变有效筛选，并让时间线发送者与图片预览可由键盘和辅助技术操作。

**Architecture:** 保持现有 ViewModel 与 `MainWindow` 分工：可自动测试的默认值、请求代际、筛选恢复和自动化名称放入 ViewModel/纯逻辑类型，ContentDialog 与焦点顺序留在窗口交互层。所有搜索选项后台结果通过 generation 判定“最后发起者获胜”，窗口仅处理与当前快照匹配的完成事件。

**Tech Stack:** C# 13、.NET 10、WinUI 3 / Windows App SDK、CommunityToolkit.Mvvm、xUnit.net v3 in-process runner。

**Spec:** `docs/superpowers/specs/2026-08-31-chatarchive-safety-usability-design.md`

## Global Constraints

- 不修改 SQLite schema、联系人仓储事务结构或消息去重语义。
- 不新增网络访问、第三方依赖、导入取消或完整 WinUI UI 自动化基础设施。
- 不修改 xUnit/MTP 包、测试项目属性或 runner 配置；测试先构建，再直接运行 xUnit v3 可执行文件。
- 不读取、修改或暂存未跟踪的 `inputapp/`。
- 对能纯逻辑验证的行为先写失败测试；XAML 与 ContentDialog 事件接线使用编译验证和人工冒烟。
- 每次提交只暂存任务列出的文件，保留用户的其他工作区内容。

## File Structure

- Modify: `src/ChatArchive.App/ViewModels/ContactDetailViewModel.cs` — 账号绑定的安全默认值。
- Modify: `src/ChatArchive.App/ViewModels/SearchViewModel.cs` — 选项快照加载、generation 与完成事件。
- Create: `src/ChatArchive.App/ViewModels/SearchOptionRefresh.cs` — 与 WinUI 控件无关的筛选恢复决策。
- Modify: `src/ChatArchive.App/ViewModels/TimelineProjection.cs` — 图片预览自动化名称。
- Modify: `src/ChatArchive.App/ViewModels/TimelineViewModel.cs` — 发送者自动化名称。
- Modify: `src/ChatArchive.App/MainWindow.xaml` — 可聚焦的透明按钮及焦点顺序。
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs` — 二次确认、导入对话框生命周期、搜索恢复接线和统一 Click 处理器。
- Modify: `tests/ChatArchive.App.Tests/ContactsViewModelTests.cs` — 默认拒绝账号转移。
- Modify: `tests/ChatArchive.App.Tests/SearchStateTests.cs` — generation 与纯恢复逻辑。
- Modify: `tests/ChatArchive.App.Tests/TimelineProjectionTests.cs` — 自动化名称及空值回退。

---

### Task 1: Make account rebinding opt-in in the ViewModel

**Files:**
- Modify: `src/ChatArchive.App/ViewModels/ContactDetailViewModel.cs:170-184`
- Test: `tests/ChatArchive.App.Tests/ContactsViewModelTests.cs:238-260`

**Interfaces:**
- Consumes: `ContactRepository.BindSender(long, long, string?, bool, bool forceRebind = false)`.
- Produces: `ContactDetailViewModel.BindSenderAsync(long senderId, string? accountLabel = null, bool isPrimary = false, bool forceRebind = false)`.

- [ ] **Step 1: Add a failing regression test for the safe default**

Add this test beside the existing bind/unbind ViewModel test:

```csharp
[Fact]
public async Task ContactDetailViewModel_BindSenderAsync_does_not_transfer_without_explicit_force()
{
    var senderId = InsertSender("10086", "已有归属账号");
    var oldContactId = _contactRepository.CreateContact(
        "旧联系人",
        initialBindings: [(senderId, "原账号", true)]);
    var newContactId = _contactRepository.CreateContact("新联系人");
    var detailVm = new ContactDetailViewModel(_contactRepository, _avatarStorage);
    await detailVm.LoadAsync(newContactId);

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => detailVm.BindSenderAsync(senderId));

    Assert.Contains(
        _contactRepository.GetContactDetail(oldContactId)!.Senders,
        sender => sender.SenderId == senderId);
    Assert.Empty(_contactRepository.GetContactDetail(newContactId)!.Senders);
}
```

- [ ] **Step 2: Build to verify the new test exposes the current unsafe default**

Run:

```powershell
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -class 'ChatArchive.App.Tests.ContactsViewModelTests' -noLogo -automated
```

Expected: the new test fails because the current `forceRebind = true` silently moves the sender and no `InvalidOperationException` is thrown.

- [ ] **Step 3: Change only the ViewModel default**

Change the signature to:

```csharp
public async Task BindSenderAsync(
    long senderId,
    string? accountLabel = null,
    bool isPrimary = false,
    bool forceRebind = false)
```

Keep the repository call and subsequent `LoadAsync(ContactId)` unchanged.

- [ ] **Step 4: Run the contact ViewModel tests**

Run the two commands from Step 2 again.

Expected: `ContactsViewModelTests` passes, including the existing ordinary bind/unbind test and the new refusal test.

- [ ] **Step 5: Commit the safe default**

```powershell
git add -- 'src/ChatArchive.App/ViewModels/ContactDetailViewModel.cs' 'tests/ChatArchive.App.Tests/ContactsViewModelTests.cs'
git commit -m "fix: require explicit account rebind"
```

---

### Task 2: Require a second confirmation before transferring an account

**Files:**
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs:876-991`

**Interfaces:**
- Consumes: the Task 1 `BindSenderAsync(..., forceRebind = false)` safe default and `BoundSenderInfo.BoundContactName`.
- Produces: a two-dialog sequence in `OnAddBoundAccountClick`; only the explicit transfer path passes `forceRebind: true`.

- [ ] **Step 1: Capture the first dialog result without mutating the database**

Replace the binding call inside the first dialog with local capture. Declare the values before `try`, then preserve the existing search cancellation cleanup:

```csharp
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
    searchCts?.Cancel();
    searchCts?.Dispose();
}
```

This ensures `dialog.ShowSafeAsync()` has returned and `DialogExtensions.DialogGate` has been released before another dialog is shown.

- [ ] **Step 2: Add the fail-safe transfer confirmation**

Immediately after the `finally`, add:

```csharp
var forceRebind = false;
if (!string.IsNullOrWhiteSpace(selectedSender.BoundContactName))
{
    var oldContactName = selectedSender.BoundContactName.Trim();
    var confirm = new ContentDialog
    {
        XamlRoot = Content.XamlRoot,
        Title = "确认转移账号",
        PrimaryButtonText = "确认转移",
        CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Close,
        Content = $"账号“{selectedSender.OriginalName}”当前属于“{oldContactName}”。\n"
                  + $"确认将其转移到“{detail.DisplayName}”吗？\n\n"
                  + "旧联系人如果没有其他账号、备注或自定义头像，可能会被自动清理。",
    };

    if (await confirm.ShowSafeAsync() != ContentDialogResult.Primary)
    {
        return;
    }

    forceRebind = true;
}

var currentId = detail.ContactId;
await detail.BindSenderAsync(
    selectedSender.SenderId,
    selectedLabel,
    selectedPrimary,
    forceRebind);
await ReloadContactsAsync(currentId);
```

Do not catch `ContentDialogResult.None` as approval; `ShowSafeAsync` failures and user cancellation both return without a repository call.

- [ ] **Step 3: Compile the WinUI event code**

Run:

```powershell
dotnet build 'src\ChatArchive.App\ChatArchive.App.csproj' --no-restore --nologo
```

Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 4: Perform the focused transfer smoke test**

Run the app with a disposable test database and verify:

1. Selecting an unbound account shows no second dialog and binds once.
2. Selecting an account whose `BoundContactName` is populated closes the first dialog before showing “确认转移账号”.
3. Pressing Esc or “取消” leaves both contacts unchanged.
4. The confirmation initially focuses the “取消” action because `DefaultButton` is `Close`.
5. Pressing “确认转移” moves the account once and reloads the original target contact.

- [ ] **Step 5: Commit the UI confirmation**

```powershell
git add -- 'src/ChatArchive.App/MainWindow.xaml.cs'
git commit -m "fix: confirm destructive account transfers"
```

---

### Task 3: Keep the import dialog attached while an import is running

**Files:**
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs:254-390`

**Interfaces:**
- Consumes: `ImportViewModel.IsRunning`, `StatusText`, `Paths`, and the existing `ImportFinished` event.
- Produces: one `ContentDialog.Closing` guard plus synchronized primary-button state; no cancellation or hidden background dialog.

- [ ] **Step 1: Make `RefreshButtons` own the primary button state**

Declare the dialog before the local function so the function can capture it:

```csharp
ContentDialog? dialog = null;

void RefreshButtons()
{
    status.Text = _import.StatusText;
    progress.IsIndeterminate = _import.IsRunning;
    progress.Visibility = _import.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    addFile.IsEnabled = !_import.IsRunning;
    addFolder.IsEnabled = !_import.IsRunning;
    clear.IsEnabled = !_import.IsRunning;
    start.IsEnabled = !_import.IsRunning && _import.Paths.Count > 0;
    if (dialog is not null)
    {
        dialog.IsPrimaryButtonEnabled = !_import.IsRunning;
        dialog.PrimaryButtonText = _import.IsRunning ? "正在导入…" : "关闭";
    }
}
```

Assign `dialog = new ContentDialog { ... }` where the dialog is currently declared.

- [ ] **Step 2: Guard every ContentDialog close route and unsubscribe reliably**

Remove the current `_import.PropertyChanged += importChanged` and
`_import.Paths.CollectionChanged += pathsChanged` statements from before the
dialog show. Add a named local handler before the `ShowSafeAsync` call, then
subscribe to each of the three events exactly once inside the same `try`:

```csharp
void ImportDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
{
    args.Cancel = _import.IsRunning;
}

try
{
    _import.PropertyChanged += importChanged;
    _import.Paths.CollectionChanged += pathsChanged;
    dialog.Closing += ImportDialogClosing;
    RefreshButtons();
    await dialog.ShowSafeAsync();
}
finally
{
    dialog.Closing -= ImportDialogClosing;
    _import.PropertyChanged -= importChanged;
    _import.Paths.CollectionChanged -= pathsChanged;
}
```

Do not call `dialog.Hide()` and do not add a second close API. Keep the existing post-dialog conversation reload guarded by `!_import.IsRunning`.

- [ ] **Step 3: Compile the dialog lifecycle changes**

Run:

```powershell
dotnet build 'src\ChatArchive.App\ChatArchive.App.csproj' --no-restore --nologo
```

Expected: build succeeds with 0 warnings and 0 errors; `ContentDialogClosingEventArgs.Cancel`, `IsPrimaryButtonEnabled`, and mutable `PrimaryButtonText` compile against the current Windows App SDK.

- [ ] **Step 4: Perform the focused import smoke test**

Use a disposable valid export large enough to leave `IsRunning` true for observation:

1. Start import and verify add-file, add-folder, clear, start, and primary buttons are disabled.
2. Verify the primary text changes to “正在导入…”.
3. Press Esc, Alt+Left/system back if available, and the disabled primary button; the dialog remains visible.
4. Let the import succeed or fail; verify status keeps the final summary, primary text becomes “关闭”, and the button becomes enabled.
5. Close the dialog, reopen it, and verify progress/status updates occur once, demonstrating that old subscriptions were removed.

- [ ] **Step 5: Commit the lifecycle guard**

```powershell
git add -- 'src/ChatArchive.App/MainWindow.xaml.cs'
git commit -m "fix: retain import progress dialog"
```

---

### Task 4: Add generation ownership to search option loading

**Files:**
- Modify: `src/ChatArchive.App/ViewModels/SearchViewModel.cs:11-112`
- Test: `tests/ChatArchive.App.Tests/SearchStateTests.cs`

**Interfaces:**
- Consumes: `ConversationRepository.ListConversations(limit: 1000)`, `SearchRepository.GetFilterOptions()`, the existing query repository, and an optional `DispatcherQueue`.
- Produces: `internal sealed record SearchOptionsSnapshot(...)`, an internal `Func<Task<SearchOptionsSnapshot>>` test seam, `public event Action<long, bool>? OptionsReloaded`, and `public long LoadOptions()`.

- [ ] **Step 1: Add deterministic failing tests for latest-request ownership**

Add these tests to `SearchStateTests.cs`. `TaskCompletionSource` controls completion order and requires no sleeps:

```csharp
[Fact]
public void LoadOptions_newer_success_wins_when_older_success_finishes_last()
{
    var first = new TaskCompletionSource<SearchOptionsSnapshot>();
    var second = new TaskCompletionSource<SearchOptionsSnapshot>();
    var loads = new Queue<Task<SearchOptionsSnapshot>>([first.Task, second.Task]);
    var viewModel = CreateOptionsViewModel(() => loads.Dequeue());
    var notifications = new List<(long Generation, bool Success)>();
    viewModel.OptionsReloaded += (generation, success) =>
        notifications.Add((generation, success));

    var generation1 = viewModel.LoadOptions();
    var generation2 = viewModel.LoadOptions();
    second.SetResult(OptionsSnapshot(2, "新会话", "image"));

    Assert.Equal(generation1 + 1, generation2);
    Assert.Equal((generation2, true), Assert.Single(notifications));
    Assert.Equal(new long?[] { null, 2 }, viewModel.ConversationOptions.Select(item => item.Id));

    first.SetResult(OptionsSnapshot(1, "旧会话", "text"));

    Assert.Single(notifications);
    Assert.Equal(new long?[] { null, 2 }, viewModel.ConversationOptions.Select(item => item.Id));
    Assert.Equal(new string?[] { null, "image" }, viewModel.MessageTypeOptions.Select(item => item.Value));
}

[Fact]
public void LoadOptions_latest_failure_preserves_option_instances_and_notifies_failure()
{
    var load = new TaskCompletionSource<SearchOptionsSnapshot>();
    var viewModel = CreateOptionsViewModel(() => load.Task);
    var conversation = new SearchConversationOption(7, "保留会话");
    var messageType = new SearchMessageTypeOption("image", "图片");
    viewModel.ConversationOptions.Add(conversation);
    viewModel.MessageTypeOptions.Add(messageType);
    (long Generation, bool Success)? notification = null;
    viewModel.OptionsReloaded += (generation, success) => notification = (generation, success);

    var generation = viewModel.LoadOptions();
    load.SetException(new InvalidOperationException("database unavailable"));

    Assert.Equal((generation, false), notification);
    Assert.Same(conversation, viewModel.ConversationOptions[1]);
    Assert.Same(messageType, viewModel.MessageTypeOptions[1]);
    Assert.Contains("database unavailable", viewModel.ErrorMessage, StringComparison.Ordinal);
}

[Fact]
public void LoadOptions_stale_failure_does_not_overwrite_latest_state_or_notify()
{
    var first = new TaskCompletionSource<SearchOptionsSnapshot>();
    var second = new TaskCompletionSource<SearchOptionsSnapshot>();
    var loads = new Queue<Task<SearchOptionsSnapshot>>([first.Task, second.Task]);
    var viewModel = CreateOptionsViewModel(() => loads.Dequeue());
    var notifications = new List<(long Generation, bool Success)>();
    viewModel.OptionsReloaded += (generation, success) =>
        notifications.Add((generation, success));

    _ = viewModel.LoadOptions();
    var latest = viewModel.LoadOptions();
    second.SetResult(OptionsSnapshot(2, "最新", "image"));
    first.SetException(new InvalidOperationException("stale failure"));

    Assert.Equal((latest, true), Assert.Single(notifications));
    Assert.Empty(viewModel.ErrorMessage);
    Assert.Equal(new long?[] { null, 2 }, viewModel.ConversationOptions.Select(item => item.Id));
}
```

Add the exact helpers inside `SearchStateTests`:

```csharp
private static SearchViewModel CreateOptionsViewModel(
    Func<Task<SearchOptionsSnapshot>> loader)
{
    var database = new ArchiveDatabase(Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-options-{Guid.NewGuid():N}.db"));
    return new SearchViewModel(new SearchRepository(database), loader);
}

private static SearchOptionsSnapshot OptionsSnapshot(long id, string title, string messageType)
{
    var conversation = new ConversationInfo(
        id, "qq", "account", $"native-{id}", "private", title,
        null, null, 1, null, 0);
    return new SearchOptionsSnapshot(
        [conversation],
        new FilterOptions([new FilterOptionItem(messageType, 1)], []));
}
```

Add `using ChatArchive.Core.Repositories;`. The repository is retained for the query path but the tests do not call it, so no database file or schema is created.

- [ ] **Step 2: Build to verify the generation API is missing**

```powershell
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --no-restore --nologo
```

Expected: compilation fails because `SearchOptionsSnapshot`, the internal loader constructor, `OptionsReloaded`, and the `long` return value do not exist.

- [ ] **Step 3: Introduce the immutable snapshot and asynchronous loader seam**

Add above `SearchViewModel`:

```csharp
internal sealed record SearchOptionsSnapshot(
    IReadOnlyList<ConversationInfo> Conversations,
    FilterOptions Filters);
```

Use these fields; `_repository` and `_dispatcher` continue serving normal search execution:

```csharp
private readonly SearchRepository _repository;
private readonly DispatcherQueue? _dispatcher;
private readonly Func<Task<SearchOptionsSnapshot>> _optionsLoader;
private long _optionsGeneration;
```

Keep the public constructor compatible and add an internal constructor:

```csharp
public SearchViewModel(
    SearchRepository repository,
    ConversationRepository conversations,
    DispatcherQueue? dispatcher = null)
    : this(
        repository,
        () => Task.Run(() => new SearchOptionsSnapshot(
            conversations.ListConversations(limit: 1000),
            repository.GetFilterOptions())),
        dispatcher)
{
}

internal SearchViewModel(
    SearchRepository repository,
    Func<Task<SearchOptionsSnapshot>> optionsLoader,
    DispatcherQueue? dispatcher = null)
{
    _repository = repository;
    _optionsLoader = optionsLoader;
    _dispatcher = dispatcher;
    ConversationOptions.Add(new SearchConversationOption(null, "全部会话"));
    MessageTypeOptions.Add(new SearchMessageTypeOption(null, "全部消息类型"));
}
```

Remove the now-unused `_conversations` field. Do not wrap `_optionsLoader` in another `Task.Run`; only the production constructor moves synchronous repository work to the pool.

- [ ] **Step 4: Implement latest-generation application and completion events**

Add `public event Action<long, bool>? OptionsReloaded;`, then replace `LoadOptions` and add `PostOptionsResult`:

```csharp
public long LoadOptions()
{
    var generation = Interlocked.Increment(ref _optionsGeneration);
    Task<SearchOptionsSnapshot> loadTask;
    try
    {
        loadTask = _optionsLoader();
    }
    catch (Exception ex)
    {
        loadTask = Task.FromException<SearchOptionsSnapshot>(ex);
    }

    _ = loadTask.ContinueWith(
        completed => PostOptionsResult(generation, completed),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
    return generation;
}

private void PostOptionsResult(long generation, Task<SearchOptionsSnapshot> completed)
{
    void Apply()
    {
        if (generation != Interlocked.Read(ref _optionsGeneration))
        {
            return;
        }

        if (!completed.IsCompletedSuccessfully)
        {
            var message = completed.Exception?.GetBaseException().Message
                          ?? (completed.IsCanceled ? "操作已取消" : "未知错误");
            ErrorMessage = $"加载搜索筛选项失败：{message}";
            OptionsReloaded?.Invoke(generation, false);
            return;
        }

        var conversations = new List<SearchConversationOption> { new(null, "全部会话") };
        foreach (var conversation in completed.Result.Conversations)
        {
            var platform = conversation.Platform?.ToLowerInvariant() switch
            {
                "qq" => "QQ",
                "wechat" => "微信",
                "text" => "文本",
                "html" => "网页",
                "sql" => "SQL",
                _ => conversation.Platform ?? string.Empty,
            };
            conversations.Add(new(conversation.Id, $"{platform} · {conversation.Title}"));
        }

        var messageTypes = new List<SearchMessageTypeOption> { new(null, "全部消息类型") };
        foreach (var option in completed.Result.Filters.MessageTypes)
        {
            messageTypes.Add(new(option.Value, MessageTypeLabel(option.Value, option.Amount)));
        }

        ConversationOptions.Clear();
        foreach (var option in conversations)
        {
            ConversationOptions.Add(option);
        }
        MessageTypeOptions.Clear();
        foreach (var option in messageTypes)
        {
            MessageTypeOptions.Add(option);
        }

        ErrorMessage = string.Empty;
        OptionsReloaded?.Invoke(generation, true);
    }

    if (_dispatcher is null)
    {
        Apply();
    }
    else
    {
        _ = _dispatcher.TryEnqueue(Apply);
    }
}
```

Build temporary lists before clearing observable collections so a projection exception cannot leave half-applied options. The generation check must precede exception-message access, error mutation, collection mutation, and event notification.

- [ ] **Step 5: Run search and constructor-compatibility tests**

```powershell
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -class 'ChatArchive.App.Tests.SearchStateTests' -class 'ChatArchive.App.Tests.UiInputTests' -noLogo -automated
```

Expected: generation tests, all existing search state tests, and existing production-constructor tests pass deterministically.

- [ ] **Step 6: Commit generation ownership**

```powershell
git add -- 'src/ChatArchive.App/ViewModels/SearchViewModel.cs' 'tests/ChatArchive.App.Tests/SearchStateTests.cs'
git commit -m "fix: version search option reloads"
```

---

### Task 5: Make search-filter restoration a pure tested decision

**Files:**
- Create: `src/ChatArchive.App/ViewModels/SearchOptionRefresh.cs`
- Test: `tests/ChatArchive.App.Tests/SearchStateTests.cs`

**Interfaces:**
- Consumes: current selected values, `HasSearched`, and the newly loaded option sequences.
- Produces: `SearchOptionRefreshResult` and `SearchOptionRefresh.Restore(...)`.

- [ ] **Step 1: Add failing restoration tests**

Add:

```csharp
[Fact]
public void SearchOptionRefresh_preserves_values_that_still_exist()
{
    var result = SearchOptionRefresh.Restore(
        42,
        "image",
        hasSearched: true,
        [new SearchConversationOption(null, "全部"), new SearchConversationOption(42, "目标")],
        [new SearchMessageTypeOption(null, "全部"), new SearchMessageTypeOption("image", "图片")]);

    Assert.Equal(42, result.ConversationId);
    Assert.Equal("image", result.MessageType);
    Assert.False(result.ShouldRunSearch);
}

[Fact]
public void SearchOptionRefresh_falls_back_when_conversation_disappears()
{
    var result = SearchOptionRefresh.Restore(
        42,
        "image",
        hasSearched: true,
        [new SearchConversationOption(null, "全部")],
        [new SearchMessageTypeOption(null, "全部"), new SearchMessageTypeOption("image", "图片")]);

    Assert.Null(result.ConversationId);
    Assert.Equal("image", result.MessageType);
    Assert.True(result.ShouldRunSearch);
}

[Fact]
public void SearchOptionRefresh_falls_back_when_message_type_disappears()
{
    var result = SearchOptionRefresh.Restore(
        42,
        "image",
        hasSearched: true,
        [new SearchConversationOption(null, "全部"), new SearchConversationOption(42, "目标")],
        [new SearchMessageTypeOption(null, "全部")]);

    Assert.Equal(42, result.ConversationId);
    Assert.Null(result.MessageType);
    Assert.True(result.ShouldRunSearch);
}

[Fact]
public void SearchOptionRefresh_does_not_search_before_the_first_query()
{
    var result = SearchOptionRefresh.Restore(
        42,
        "image",
        hasSearched: false,
        [new SearchConversationOption(null, "全部")],
        [new SearchMessageTypeOption(null, "全部")]);

    Assert.Null(result.ConversationId);
    Assert.Null(result.MessageType);
    Assert.False(result.ShouldRunSearch);
}
```

- [ ] **Step 2: Build to verify the pure types are missing**

Run the App test build from Task 4 Step 2.

Expected: compilation fails with missing `SearchOptionRefresh` and `SearchOptionRefreshResult`.

- [ ] **Step 3: Implement the pure restoration helper**

Create `SearchOptionRefresh.cs`:

```csharp
namespace ChatArchive.App.ViewModels;

internal readonly record struct SearchOptionRefreshResult(
    long? ConversationId,
    string? MessageType,
    bool ShouldRunSearch);

internal static class SearchOptionRefresh
{
    internal static SearchOptionRefreshResult Restore(
        long? conversationId,
        string? messageType,
        bool hasSearched,
        IReadOnlyList<SearchConversationOption> conversations,
        IReadOnlyList<SearchMessageTypeOption> messageTypes)
    {
        var restoredConversationId = conversations.Any(option => option.Id == conversationId)
            ? conversationId
            : null;
        var restoredMessageType = messageTypes.Any(option => string.Equals(
                option.Value,
                messageType,
                StringComparison.Ordinal))
            ? messageType
            : null;
        var changed = restoredConversationId != conversationId
                      || !string.Equals(restoredMessageType, messageType, StringComparison.Ordinal);
        return new SearchOptionRefreshResult(
            restoredConversationId,
            restoredMessageType,
            hasSearched && changed);
    }
}
```

- [ ] **Step 4: Run the search state tests**

Run the build and direct `SearchStateTests` executable commands from Task 4 Step 5.

Expected: all search state and option restoration tests pass.

- [ ] **Step 5: Commit the pure restore decision**

```powershell
git add -- 'src/ChatArchive.App/ViewModels/SearchOptionRefresh.cs' 'tests/ChatArchive.App.Tests/SearchStateTests.cs'
git commit -m "fix: preserve search option selections"
```

---

### Task 6: Wire generation-aware option restoration into MainWindow

**Files:**
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs:18-30,113-175,1281-1294`

**Interfaces:**
- Consumes: Task 4 `LoadOptions()`/`OptionsReloaded` and Task 5 `SearchOptionRefresh.Restore`.
- Produces: `ReloadSearchOptions()` and `SearchOptions_Reloaded(long, bool)`; all option reload call sites use the wrapper.

- [ ] **Step 1: Add current-generation state**

Add fields beside the other window state:

```csharp
private bool _suppressSearchFilterRefresh;
private PendingSearchOptionsReload? _pendingSearchOptionsReload;

private sealed record PendingSearchOptionsReload(
    long Generation,
    long? ConversationId,
    string? MessageType,
    bool HasSearched);
```

- [ ] **Step 2: Subscribe before the first reload and replace direct calls**

After `_search` is created and before its first option load, subscribe:

```csharp
_search.OptionsReloaded += SearchOptions_Reloaded;
```

Replace both constructor/import-finished `_search.LoadOptions()` calls with:

```csharp
ReloadSearchOptions();
```

The subscription must appear before the first wrapper call.

- [ ] **Step 3: Implement the wrapper and completion handler**

Add near the search handlers:

```csharp
private void ReloadSearchOptions()
{
    var conversationId = SearchConversationCombo.SelectedValue is long id ? id : null;
    var messageType = SearchMessageTypeCombo.SelectedValue as string;
    _suppressSearchFilterRefresh = true;
    try
    {
        var generation = _search.LoadOptions();
        _pendingSearchOptionsReload = new PendingSearchOptionsReload(
            generation,
            conversationId,
            messageType,
            _search.HasSearched);
    }
    catch
    {
        _pendingSearchOptionsReload = null;
        _suppressSearchFilterRefresh = false;
        throw;
    }
}

private void SearchOptions_Reloaded(long generation, bool success)
{
    if (_pendingSearchOptionsReload is not { } pending
        || pending.Generation != generation)
    {
        return;
    }

    var shouldRunSearch = false;
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
        _pendingSearchOptionsReload = null;
        _suppressSearchFilterRefresh = false;
    }

    if (shouldRunSearch)
    {
        RunSearch();
    }
}
```

- [ ] **Step 4: Suppress intermediate SelectionChanged searches**

Change the first guard in `SearchFilter_Changed` to:

```csharp
if (_suppressSearchFilterRefresh || _search is null || !_search.HasSearched)
{
    return;
}
```

Also short-circuit the forwarding handler so an intermediate collection event never reaches the shared method:

```csharp
private void SearchFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressSearchFilterRefresh)
    {
        return;
    }

    SearchFilter_Changed(sender, e);
}
```

- [ ] **Step 5: Build and perform the option-refresh smoke test**

Run:

```powershell
dotnet build 'src\ChatArchive.App\ChatArchive.App.csproj' --no-restore --nologo
```

Then verify in the app:

1. Search with a concrete conversation and image type.
2. Trigger an import-completion refresh while both values still exist; selections and results remain unchanged.
3. Remove the selected value in a disposable database, refresh, and verify both ComboBoxes visibly fall back to “全部” and exactly one replacement search runs.
4. Trigger two refreshes close together; the older completion never overwrites the newest option list or releases suppression early.
5. Force the latest option load to fail; old options, selections, and results remain, the InfoBar reports the error, and later user selection still triggers search.

- [ ] **Step 6: Commit MainWindow search coordination**

```powershell
git add -- 'src/ChatArchive.App/MainWindow.xaml.cs'
git commit -m "fix: restore filters after option reload"
```

---

### Task 7: Add stable automation names to timeline projections

**Files:**
- Modify: `src/ChatArchive.App/ViewModels/TimelineProjection.cs:7-24`
- Modify: `src/ChatArchive.App/ViewModels/TimelineViewModel.cs:62-65`
- Test: `tests/ChatArchive.App.Tests/TimelineProjectionTests.cs`

**Interfaces:**
- Produces: `AttachmentEntry.PreviewAutomationName` and `MessageEntry.SenderAutomationName`.
- Consumes: `Filename` and `DisplaySenderName`; no WinUI dependency.

- [ ] **Step 1: Add failing fallback tests**

Add:

```csharp
[Theory]
[InlineData("photo.png", "预览图片：photo.png")]
[InlineData(null, "预览图片")]
[InlineData("", "预览图片")]
[InlineData("   ", "预览图片")]
public void AttachmentEntry_preview_automation_name_has_a_stable_fallback(
    string? filename,
    string expected)
{
    var entry = new AttachmentEntry("image", filename, "C:/media/photo.png", true, false);

    Assert.Equal(expected, entry.PreviewAutomationName);
}

[Theory]
[InlineData("张总", "工作号", "查看发送者：张总 · 工作号")]
[InlineData("", null, "查看发送者")]
[InlineData("   ", null, "查看发送者")]
public void MessageEntry_sender_automation_name_has_a_stable_fallback(
    string senderName,
    string? accountLabel,
    string expected)
{
    var message = new MessageItem(
        1, 1, 100, senderName, "incoming", "text", null,
        "消息", false, false, 1700000000000,
        Array.Empty<AttachmentInfo>(),
        AccountLabel: accountLabel);

    var entry = new MessageEntry(message, new MediaLocator(_directory));

    Assert.Equal(expected, entry.SenderAutomationName);
}
```

- [ ] **Step 2: Build to verify the properties are absent**

Run the App test build from Task 4 Step 2.

Expected: compilation fails with `CS1061` for `PreviewAutomationName` and `SenderAutomationName`.

- [ ] **Step 3: Implement both projection properties**

Add to `AttachmentEntry`:

```csharp
public string PreviewAutomationName => string.IsNullOrWhiteSpace(Filename)
    ? "预览图片"
    : $"预览图片：{Filename.Trim()}";
```

Add to `MessageEntry`:

```csharp
public string SenderAutomationName => string.IsNullOrWhiteSpace(DisplaySenderName)
    ? "查看发送者"
    : $"查看发送者：{DisplaySenderName.Trim()}";
```

- [ ] **Step 4: Run timeline projection tests**

```powershell
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -class 'ChatArchive.App.Tests.TimelineProjectionTests' -noLogo -automated
```

Expected: all existing and new timeline projection tests pass.

- [ ] **Step 5: Commit automation labels**

```powershell
git add -- 'src/ChatArchive.App/ViewModels/TimelineProjection.cs' 'src/ChatArchive.App/ViewModels/TimelineViewModel.cs' 'tests/ChatArchive.App.Tests/TimelineProjectionTests.cs'
git commit -m "feat: label timeline actions for accessibility"
```

---

### Task 8: Replace pointer-only timeline targets with one Click path

**Files:**
- Modify: `src/ChatArchive.App/MainWindow.xaml:70-152`
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs:524-553`

**Interfaces:**
- Consumes: Task 7 automation-name properties and existing `ShowSenderProfile`/`ShowImagePreview` methods.
- Produces: `OnSenderClick(object, RoutedEventArgs)` and `OnImageAttachmentClick(object, RoutedEventArgs)` used by mouse, Enter, Space, and UI Automation.

- [ ] **Step 1: Wrap each image preview in a transparent Button**

Replace the image template with:

```xml
<DataTemplate>
    <Button Background="Transparent" BorderThickness="0" Padding="0"
            HorizontalAlignment="Left"
            AutomationProperties.Name="{Binding PreviewAutomationName}"
            ToolTipService.ToolTip="{Binding PreviewAutomationName}"
            Click="OnImageAttachmentClick">
        <Image Source="{Binding ResolvedPath, Converter={StaticResource PathToImageSource}}"
               MaxHeight="220" Stretch="Uniform" IsHitTestVisible="False" />
    </Button>
</DataTemplate>
```

- [ ] **Step 2: Give incoming messages exactly one sender Tab stop**

Replace the incoming `PersonPicture` with a transparent `Button` containing the same picture and `IsTabStop="False"`. Replace the sender `TextBlock` with the keyboard target:

```xml
<Button Grid.Column="0" Width="32" Height="32" VerticalAlignment="Top" Margin="0,2,0,0"
        Background="Transparent" BorderThickness="0" Padding="0" IsTabStop="False"
        AutomationProperties.Name="{Binding SenderAutomationName}"
        Click="OnSenderClick">
    <PersonPicture Width="32" Height="32" IsHitTestVisible="False"
                   ProfilePicture="{Binding AvatarPath, Converter={StaticResource PathToImageSource}}"
                   Initials="{Binding Initials}"
                   DisplayName="{Binding DisplaySenderName}" />
</Button>
```

```xml
<Button Content="{Binding DisplaySenderName}" FontSize="11" Opacity="0.55"
        HorizontalAlignment="Left" Background="Transparent" BorderThickness="0" Padding="0"
        AutomationProperties.Name="{Binding SenderAutomationName}"
        Click="OnSenderClick" />
```

The avatar remains pointer-clickable but is not a duplicate Tab stop.

- [ ] **Step 3: Make the outgoing avatar the sender Tab stop**

Replace the outgoing `PersonPicture` with the same wrapper as Step 2 but omit `IsTabStop="False"`. Keep it after the message stack in XAML so image buttons precede the avatar in keyboard order.

- [ ] **Step 4: Unify event handlers on RoutedEventArgs**

Replace the two Tapped handlers with:

```csharp
private async void OnSenderClick(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MessageEntry entry }
        && entry.Message.SenderId is long senderId)
    {
        try
        {
            await ShowSenderProfile(senderId);
        }
        catch (Exception ex)
        {
            ShowError($"查看发送者信息失败: {ex.Message}");
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
            await ShowImagePreview(entry.ResolvedPath, entry.Filename ?? "图片");
        }
        catch (Exception ex)
        {
            ShowError($"查看图片预览失败: {ex.Message}");
        }
    }
}
```

Remove all `Tapped="OnSenderTapped"` and `Tapped="OnImageAttachmentTapped"` references; do not add `KeyDown` handlers.

- [ ] **Step 5: Compile XAML and verify focus behavior**

Run:

```powershell
dotnet build 'src\ChatArchive.App\ChatArchive.App.csproj' --no-restore --nologo
```

Then use a timeline containing incoming/outgoing messages and multiple images:

1. Incoming order is sender-name button, then each image; the avatar does not receive a second Tab stop but remains clickable.
2. Outgoing order is each image, then avatar.
3. Enter and Space invoke the same profile/preview as a pointer click.
4. Narrator reads “查看发送者” or “预览图片” even when source data is blank; populated data includes the display name or filename.
5. Focus visuals are visible and the original avatar, bubble, and image dimensions remain aligned.

- [ ] **Step 6: Commit the accessible controls**

```powershell
git add -- 'src/ChatArchive.App/MainWindow.xaml' 'src/ChatArchive.App/MainWindow.xaml.cs'
git commit -m "feat: make timeline actions keyboard accessible"
```

---

### Task 9: Verify the complete App work package

**Files:**
- Verify only: all App files and tests listed above.

**Interfaces:**
- Consumes: Tasks 1-8.
- Produces: evidence that Debug/Release build, test discovery, full App suite, and the four manual workflows agree with the approved spec.

- [ ] **Step 1: Build Debug and Release**

```powershell
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --no-restore --nologo -c Debug
dotnet build 'tests\ChatArchive.App.Tests\ChatArchive.App.Tests.csproj' --no-restore --nologo -c Release
```

Expected: both commands finish with 0 warnings and 0 errors.

- [ ] **Step 2: Verify non-zero test discovery, then run both App suites**

```powershell
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -list tests -noLogo
& 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -noLogo -automated
& 'tests\ChatArchive.App.Tests\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -list tests -noLogo
& 'tests\ChatArchive.App.Tests\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -noLogo -automated
```

Expected: each discovery lists at least one test and both complete suites exit 0; record discovered/passed/skipped counts.

- [ ] **Step 3: Repeat all focused manual workflows**

Repeat Task 2 Step 4, Task 3 Step 4, Task 6 Step 5, and Task 8 Step 5 against a disposable database. Do not use `inputapp/` as test input.

- [ ] **Step 4: Record the known dotnet-test host result without changing configuration**

```powershell
dotnet test 'ChatArchive.sln' --no-restore --nologo
```

Record the exit code and discovered count. If the known SDK/MTP zero-test behavior remains, do not change `.csproj`, packages, generated entry points, or runner properties in this work package.

- [ ] **Step 5: Check the final diff and worktree boundary**

```powershell
git diff --check
git status --short
git diff --stat
```

Expected: no whitespace errors; only planned source/test files are modified or committed; `inputapp/` remains untracked and untouched.
