# ChatArchive WinUI 重写实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 C#/WinUI 3 原生重写聊天档案应用，全功能对齐旧 Python/WebView 版，数据迁移到 `E:\ChatArchive`。

**Architecture:** 分层解决方案——Core 类库承载全部业务（SQLite 仓储、流式 JSON 导入、去重、媒体定位），WinUI App 仅做 MVVM 界面，Migrate 控制台工具做一次性数据迁移。

**Tech Stack:** .NET 10 / Windows App SDK 2.4.0 / Microsoft.Data.Sqlite 10.0.11 / CommunityToolkit.Mvvm 8.4.2 / xunit.v3 4.0.0

**Spec:** `docs/superpowers/specs/2026-08-21-winui-rewrite-design.md`

## Global Constraints

- 目标框架：类库与工具 `net10.0`；App `net10.0-windows10.0.19041.0`，MinVersion 10.0.17763.0
- App 必须设置：`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`、`AppxPackage=false`、`Platforms=x64`
- SQLite schema 保持 schema_version 1，禁止任何 CREATE/DROP 结构变更
- `F:\QQ+wx` 只读；一切写入仅限 `E:\AgentCode\ChatArchive` 与 `E:\ChatArchive`
- 中文注释/界面文案遵循旧版风格；代码无注释（按仓库规范），公共 API 用 XML doc 一句话说明
- 每个 Task 结束必须 `dotnet build` 通过 + 测试全绿 + git commit

---

### Task 1: 解决方案脚手架

**Files:**
- Create: `ChatArchive.sln`, `nuget.config`, `.gitignore`, `Directory.Build.props`
- Create: `src/ChatArchive.Core/ChatArchive.Core.csproj`（net10.0 类库）
- Create: `src/tools/ChatArchive.Migrate/ChatArchive.Migrate.csproj`（net10.0 exe）
- Create: `tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj`（xunit.v3）
- Create: `src/ChatArchive.App/ChatArchive.App.csproj`（WinUI，含最小 App.xaml/MainWindow.xaml，能启动空窗口）

**Interfaces (Produces):**
- 解决方案可 `dotnet build -c Release -p:Platform=x64` 全绿
- `dotnet test` 可运行（先放一个占位测试）

- [ ] 写入四个 csproj + sln + nuget.config + .gitignore + Directory.Build.props（Nullable=enable, ImplicitUsings=enable, TreatWarningsAsErrors=true）
- [ ] `dotnet build` 验证
- [ ] commit "chore: scaffold solution"

### Task 2: Core.Data — ArchiveDatabase

**Files:**
- Create: `src/ChatArchive.Core/Data/ArchiveDatabase.cs`
- Test: `tests/ChatArchive.Core.Tests/ArchiveDatabaseTests.cs`

**Interfaces (Produces):**
```csharp
public sealed class ArchiveDatabase
{
    public ArchiveDatabase(string databasePath, string? mediaDir = null);
    public string DatabasePath { get; }
    public SqliteConnection OpenConnection();      // WAL, foreign_keys=ON, busy_timeout=5000
    public void EnsureSchema();                    // 执行内嵌 schema.sql（资源），校验 schema_version==1
    public int CleanEmptyConversations();          // 启动清理，返回删除数
}
```
schema.sql 内容从 `F:\QQ+wx\chat-archive-app\backend\schema.sql` 原样复制为嵌入资源。

- [ ] 失败测试：EnsureSchema 后 app_metadata.schema_version 为 '1'；CleanEmptyConversations 删除零消息会话
- [ ] 实现 → 测试绿 → commit "feat(core): database access and schema"

### Task 3: Core Models + CursorCodec

**Files:**
- Create: `src/ChatArchive.Core/Models/*.cs`（record 类型）
- Create: `src/ChatArchive.Core/Data/CursorCodec.cs`
- Test: `tests/ChatArchive.Core.Tests/CursorCodecTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record PlatformKind // enum QQ, WeChat
public sealed record ConversationInfo(long Id, string Platform, string Kind, string Title,
    long? FirstMessageAt, long? LastMessageAt, long MessageCount);
public sealed record MessageItem(long Id, long ConversationId, string SenderName,
    string Direction, string MessageType, string? MediaType, string Content,
    bool IsRecalled, bool IsSystem, long TimestampMs,
    IReadOnlyList<AttachmentInfo> Attachments);
public sealed record AttachmentInfo(long Id, int Ordinal, string Kind, string? Filename,
    bool IsAvailable, string? MimeType, int? Width, int? Height, double? Duration);
public sealed record SenderProfile(long Id, string NativeId, string CurrentName, bool IsSelf,
    IReadOnlyList<AliasInfo> Aliases, IReadOnlyList<SenderConversationInfo> Conversations);
public sealed record SearchHit(long MessageId, long ConversationId, string ConversationTitle,
    string Platform, string Kind, string SenderName, string Snippet, long TimestampMs);
public sealed record SearchHitPage(IReadOnlyList<SearchHit> Items, long TotalMatches, string? NextCursor);
public sealed record FilterOptions(
    IReadOnlyList<(string Value, string Label)> Senders,
    IReadOnlyList<string> MessageTypes);
public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record MessageContext(long ConversationId, string ConversationTitle,
    long FocusMessageId, IReadOnlyList<MessageItem> Messages); // Messages 按时间升序含焦点
public sealed record AliasInfo(string Alias, long? ConversationId, long? FirstSeenAt, long? LastSeenAt);
public sealed record SenderConversationInfo(long ConversationId, string Title, string NameInConversation,
    long MessageCount, long? FirstMessageAt, long? LastMessageAt);
// CursorCodec: Encode(long timestampMs, long id) -> string; Decode(string) -> (long, long)
```

- [ ] CursorCodec 往返测试（边界值 0、long.MaxValue）
- [ ] commit "feat(core): models and cursor"

### Task 4: ConversationRepository + 时间线 + 上下文

**Files:**
- Create: `src/ChatArchive.Core/Repositories/ConversationRepository.cs`
- Test: `tests/ChatArchive.Core.Tests/ConversationRepositoryTests.cs`

**Interfaces (Produces):**
```csharp
public ConversationRepository(ArchiveDatabase db);
public IReadOnlyList<ConversationInfo> ListConversations(string? platform, string? kind, string? q, int limit = 300);
public ConversationInfo? GetConversation(long id);
public PageResult<MessageItem> ListMessages(long conversationId, string? cursor, int limit = 80);
public MessageContext? GetMessageContext(long messageId, int radius = 24); // 含焦点消息前后各 radius 条
```
SQL 从旧 repository.py 的 list_conversations/list_messages/message_context 移植：
时间线 `WHERE conversation_id=? AND (timestamp_ms,id) < cursor ORDER BY timestamp_ms DESC, id DESC LIMIT ?`；
上下文用 UNION(前 N 条 ASC) + 焦点 + (后 N 条 ASC)；附件随消息批量 IN 加载。

- [ ] 测试：建库造 5 条消息 → 分页 limit=2 两页取完且顺序正确、游标耗尽返回 null；
      context(radius=1) 返回 3 条且焦点居中；会话筛选 platform/kind/q 生效
- [ ] commit "feat(core): conversations timeline context"

### Task 5: SearchRepository（FTS trigram + LIKE 回退）

**Files:**
- Create: `src/ChatArchive.Core/Repositories/SearchRepository.cs`
- Test: `tests/ChatArchive.Core.Tests/SearchRepositoryTests.cs`

**Interfaces (Produces):**
```csharp
public SearchHitPage Search(string q, SearchFilter filter, string? cursor, int limit = 60);
public FilterOptions GetFilterOptions(long? conversationId);
public sealed record SearchFilter(string? Platform, string? Kind, long? ConversationId,
    string? Sender, string? MessageType, long? DateFromMs, long? DateToMs);
```
规则：查询串 ≥3 字符走 FTS5 trigram（匹配 content/search_text，转义双引号），≤2 字符走 LIKE；
snippet 由正文截取命中位置前后 ~40 字符生成；只匹配消息不匹配会话标题。
SQL 从旧 repository.py search() 移植。

- [ ] 测试：中文三字词命中 FTS；两字词走 LIKE 同样命中；筛选组合生效；游标翻页完整
- [ ] commit "feat(core): search"

### Task 6: SenderRepository + StatsRepository

**Files:**
- Create: `src/ChatArchive.Core/Repositories/SenderRepository.cs`, `StatsRepository.cs`
- Test: `tests/ChatArchive.Core.Tests/SenderAndStatsTests.cs`

**Interfaces (Produces):**
```csharp
public SenderProfile? GetSender(long senderId);   // 别名 + 跨会话（名称/消息数/日期范围）
public ArchiveStats GetStats();                    // 对齐旧 /api/stats 字段
// ArchiveStats: TotalMessages, QQMessages, WeChatMessages, TotalConversations,
//               PrivateConversations, GroupConversations, AttachmentCount, AvailableAttachments,
//               MediaFileCount, MediaTotalBytes, FirstMessageAt, LastMessageAt
```

- [ ] 测试：同一发送者两个会话两个别名聚合正确；stats 计数正确
- [ ] commit "feat(core): sender profile stats"

### Task 7: MediaLocator

**Files:**
- Create: `src/ChatArchive.Core/Media/MediaLocator.cs`
- Test: `tests/ChatArchive.Core.Tests/MediaLocatorTests.cs`

**Interfaces (Produces):**
```csharp
public MediaLocator(string mediaDir);
public string? Resolve(string sha256, string? managedPath, string? sourcePath); // 返回存在文件的路径或 null
```
顺序：`mediaDir/{sha[:2]}/{sha}{suffix}`（suffix 从 managedPath 文件名取）→ managedPath → sourcePath。

- [ ] 测试：sha 规则命中；managedPath 兜底；全缺失返回 null；无后缀场景
- [ ] commit "feat(core): media locator"

### Task 8: 导入器 — 发现/解析

**Files:**
- Create: `src/ChatArchive.Core/IO/ChunkedJson.cs`, `Hashing.cs`
- Create: `src/ChatArchive.Core/Importing/ImportDiscovery.cs`, `QqJsonParser.cs`, `WeFlowJsonParser.cs`, `ParsedMessage.cs`, `ParsedAttachment.cs`
- Test: `tests/ChatArchive.Core.Tests/ParserTests.cs`（手工 fixture）

**Interfaces (Produces):**
```csharp
public static class Hashing { public static string Sha256File(string path); }
public static class ImportDiscovery
{
    // 递归发现：QQ Chat Exporter JSON 与 WeFlow JSON 判定规则从旧 qq.py/wechat.py 移植
    public static IReadOnlyList<DiscoveredImport> Discover(IEnumerable<string> roots);
}
// DiscoveredImport: FilePath, Platform(QQ|WeChat), FileSize, Sha256(惰性)
// QqExport: AccountId, Conversations[](NativeId, Kind, Title, Messages[ParsedMessage])
// WeFlowSession: AccountId, NativeId, Kind, Title, Messages[ParsedMessage]
public sealed class ImportFormatException : Exception { public string FilePath { get; } }
public sealed class QqJsonParser  { public IEnumerable<QqExport> Parse(string path); }   // 流式
public sealed class WeFlowJsonParser { public IEnumerable<WeFlowSession> Parse(string path); }
// ParsedMessage: NativeId, LocalId, Sequence, TimestampMs, Direction, MessageType, MediaType,
//                Content, SearchText, SenderNativeId, SenderName, IsRecalled, IsSystem,
//                ReplyToNativeId, RawPayload(JsonElement→规范化字符串), Attachments[]
```
解析行为对齐旧版：顶层字段容错、消息数组逐条产出、媒体类型映射表移植。

- [ ] 测试：最小 QQ fixture 解析字段齐全；WeFlow fixture 会话+消息+附件路径正确；
      损坏文件抛 ImportFormatException 且带文件名
- [ ] commit "feat(core): import discovery parsers"

### Task 9: 导入器 — ImportService（去重/版本/别名/媒体复制/进度）

**Files:**
- Create: `src/ChatArchive.Core/Importing/ImportService.cs`, `ImportProgress.cs`
- Test: `tests/ChatArchive.Core.Tests/ImportServiceTests.cs`

**Interfaces (Produces):**
```csharp
public sealed class ImportService
{
    public ImportService(ArchiveDatabase db, string mediaDir, bool copyMedia = true);
    public async Task<ImportRunResult> RunAsync(IReadOnlyList<string> roots,
        IProgress<ImportProgress>? progress, CancellationToken ct = default);
}
// ImportProgress: Phase(enum Discover/Parsing/Storing/Finalizing), FilesDone, FilesTotal,
//                 MessagesAdded, DuplicatesSkipped, VersionsSaved, MediaCopied, MediaMissing, CurrentFile
// ImportRunResult: 写 import_runs/import_files 行 + 上述计数 + 缺失附件清单计数
```
去重事务逻辑从旧 service.py 移植（见 spec 去重规则节）：文件哈希跳过但缺失媒体文件重跑；
payload_hash/semantic_hash 计算（stable_json 规范化）；版本 revision_of_id；
别名 UPSERT 补充；媒体 SHA-256 复制到 media/{2位}/{sha}{后缀}，tmp+rename。

- [ ] 测试：QQ 目录两次导入第二次全部 duplicate；改内容重导产生版本行；
      微信变体共存；缺媒体的附件 is_available=0 且重跑补齐；空消息联系人不建会话
- [ ] commit "feat(core): import service dedup versions media"

### Task 10: Migrate 工具

**Files:**
- Create: `src/tools/ChatArchive.Migrate/Program.cs`
- Test: `tests/ChatArchive.Core.Tests/MigrateTests.cs`（调用共享的 MigrationRunner 放 Core）

**Interfaces (Produces):**
```csharp
// ChatArchive.Core/Migration/MigrationRunner.cs
public sealed class MigrationRunner(string sourceDir, string targetDir)
public MigrationReport Run(IProgress<string>? log, CancellationToken ct = default);
// 步骤：源校验(db 存在/media 存在) → db 复制(目标已存在则备份 .bak-<yyyyMMdd-HHmmss>)
//       → media 增量复制(目标已有同名 sha 文件跳过) → managed_path 前缀改写(E:\backup\...media|F:\...\data\media → target\media)
//       → 校验 counts(conversations/messages/attachments/media_objects) 源==目标 → 写 README.md
```
CLI 参数：`--from <dir> --to <dir>`，退出码 0 成功 / 非 0 失败并打印原因。

- [ ] 测试：临时目录构造迷你 db+media 跑迁移，断言复制、前缀改写、README 生成、二次运行备份行为
- [ ] commit "feat(migrate): one-time migration tool"

### Task 11: WinUI App — 骨架 + 会话/时间线

**Files:**
- Create: `src/ChatArchive.App/Services/AppSettings.cs`（exe 旁 settings.json：DataDirectory 默认 E:\ChatArchive）
- Create: ViewModels: `MainViewModel.cs`, `TimelineViewModel.cs`
- Modify: `Views/MainWindow.xaml(.cs)` → NavigationView 三栏布局
- Create: `Views/ConversationListView.xaml`(用户控件), `TimelineView.xaml`(用户控件)

**Interfaces:**
- 消费 Task 4 ConversationRepository
- Produces: 主窗口启动读 settings.json，加载会话列表；点击会话加载第一页时间线；
  滚动到底部触发 LoadMoreAsync（游标分页）
- 时间线气泡：incoming 左对齐、outgoing 右对齐、system 居中灰字；日期分隔符；
  图片缩略图经 MediaLocator 显示；缺失附件显示"[图片]（文件缺失）"样式文案

- [ ] 构建 + 手动启动验证窗口出现、会话列表渲染、时间线滚动加载
- [ ] commit "feat(app): shell conversations timeline"

### Task 12: WinUI App — 搜索页 + 筛选面板

**Files:**
- Create: `SearchViewModel.cs`, `SearchView.xaml`, `FilterPanel.xaml`
- 消费 Task 5；AutoSuggestBox 全局搜索框 + 结果列表（会话标题/发送者/摘要高亮命中词）+ 筛选面板（平台/类型/会话/发送者/消息类型/日期区间）+ 点击结果跳转到对应会话并定位消息（经 GetMessageContext）

- [ ] 构建通过；手动验证中文搜索、两字回退、筛选、结果跳转
- [ ] commit "feat(app): search filters"

### Task 13: WinUI App — 联系人弹窗 + 图片预览

**Files:**
- Create: `ContactViewModel.cs`, `SenderProfileDialog.xaml`（ContentDialog：头像首字、ID、别名历史表、跨会话列表点击跳转）
- Create: `ImagePreviewOverlay.xaml`（大图浮层，右上角"另存为" FileSavePicker、"关闭"）
- 消费 Task 6/7

- [ ] 构建通过；手动验证资料跳转、图片预览另存、缺失图不崩溃
- [ ] commit "feat(app): contact dialog image preview"

### Task 14: WinUI App — 导入对话框 + 统计页

**Files:**
- Create: `ImportViewModel.cs`, `ImportDialog.xaml`（选文件夹 FolderPicker 多次累加路径列表、开始导入、进度条+计数、完成摘要）、`StatsViewModel.cs`, `StatsView.xaml`
- 消费 Task 9；导入在后台 Task 单队列执行，进度经 DispatcherQueue 回 UI

- [ ] 构建通过；手动验证导入小目录计数展示、重复导入提示
- [ ] commit "feat(app): import dialog stats"

### Task 15: 终验 + 数据迁移执行

- [ ] `dotnet build -c Release -p:Platform=x64` 全解决方案 0 警告 0 错误
- [ ] `dotnet test` 全绿
- [ ] 运行 Migrate：`--from F:\QQ+wx\chat-archive-app\data --to E:\ChatArchive`
- [ ] 核对迁移报告：messages=36543（以源库实际为准）、conversations=129、attachments、media_objects 数一致；E:\ChatArchive\README.md 已生成
- [ ] 启动 App 冒烟：统计页数字、打开一个微信群滚动、搜索"的"、打开图片另存
- [ ] 手动验证清单过一遍（spec 测试策略节）
- [ ] commit "docs: final verification notes"
