# ChatArchive WinUI 重写设计文档

日期：2026-08-21
状态：已批准（用户确认全部关键决策）

## 背景

旧版聊天档案应用位于 `F:\QQ+wx\chat-archive-app`（FastAPI + React + pywebview）。
目录迁移和系统更换后已不可用：venv 的基础解释器失效、数据库中 2377 个 `managed_path`
全部指向旧位置 `E:\backup\QQ+wx\...`。用户要求用原生技术重写，旧目录**只读不动作
为原始数据源**。

## 已确认决策

| 决策点 | 结论 |
|---|---|
| 技术栈 | C# / WinUI 3（Windows App SDK 2.4.0）/ .NET 10 |
| 数据来源 | 复制现有 `chat_archive.db`（库内含从其他位置导入的数据，不能只靠重导原始 JSON） |
| 数据位置 | `E:\ChatArchive\`（chat_archive.db + media\ + README.md 结构说明） |
| 功能范围 | 全功能对齐旧版 |
| 分发方式 | 非打包自包含（WindowsPackageType=None + WindowsAppSDKSelfContained=true） |
| 架构 | 分层：Core 类库 + WinUI App + Migrate 工具 + Tests |
| 迁移方式 | 独立控制台工具，一次性执行 |
| schema | 完全兼容现有 schema_version 1，零改动 |

## 目录结构

```
E:\AgentCode\ChatArchive\
├── ChatArchive.sln
├── Directory.Build.props            # 公共编译属性
├── nuget.config                     # nuget.org 源
├── docs\superpowers\
│   ├── specs\2026-08-21-winui-rewrite-design.md   # 本文档
│   └── plans\2026-08-21-winui-rewrite.md          # 实施计划
├── src\
│   ├── ChatArchive.Core\            # net10.0 类库，无 UI 依赖
│   │   ├── Models\                  # ConversationInfo, MessageItem, SenderProfile,
│   │   │                            # AttachmentInfo, ArchiveStats, SearchHit ...
│   │   ├── Data\                    # ArchiveDatabase(连接/WAL/PRAGMA), CursorCodec
│   │   ├── Repositories\            # ConversationRepository, SearchRepository,
│   │   │                            # SenderRepository, StatsRepository
│   │   ├── Media\                   # MediaLocator(sha256 寻址优先+库内路径回退)
│   │   ├── Importing\               # ImportDiscovery, QqJsonParser, WeFlowJsonParser,
│   │   │                            # ImportService(去重/版本/别名/媒体复制), ImportProgress
│   │   └── IO\                      # ChunkedJson(流式大 JSON), Hashing(SHA-256)
│   ├── ChatArchive.App\             # WinUI 3 非打包自包含，MVVM
│   │   ├── ViewModels\              # MainViewModel, TimelineViewModel, SearchViewModel,
│   │   │                            # ContactViewModel, ImportViewModel, StatsViewModel
│   │   ├── Views\                   # MainWindow(NavigationView 三栏), 各页面/对话框
│   │   └── Services\                # AppSettings(settings.json), MediaService
│   └── tools\ChatArchive.Migrate\   # 控制台迁移工具
└── tests\ChatArchive.Core.Tests\    # xUnit
```

## 数据布局

```
E:\ChatArchive\
├── chat_archive.db    # 从 F:\QQ+wx 复制，schema_version 1 不变
├── media\<sha前2位>\<sha256><后缀>   # 内容寻址，从 F:\QQ+wx 复制
└── README.md          # Migrate 工具生成，说明各文件用途与备份方法
```

## 媒体定位规则（双保险）

1. 优先按内容寻址规则推导：`mediaDir/{sha256[:2]}/{sha256}{suffix}`，
   suffix 取自库内 managed_path 文件名后缀；无后缀则尝试无后缀名。
2. 推导失败时回退库内 `managed_path`、`source_path`（逐个检查文件存在）。
3. Migrate 工具一次性改写 `managed_path` 前缀：
   `E:\backup\QQ+wx\chat-archive-app\data\media` → `E:\ChatArchive\media`
   （同时处理指向 F 盘的同类路径），`first_source_path` 保持原值不动。

## 功能对齐清单

| 旧版功能 | 新实现 |
|---|---|
| 会话列表 + 平台/类型筛选 + 标题搜索 | NavigationView 左栏 ListView |
| 时间线游标分页 `(timestamp_ms,id)` | 虚拟化 ListView，滚动加载 |
| 全局搜索 FTS5 trigram ≥3字 / LIKE ≤2字回退；发送者/平台/群私聊/类型/日期筛选；正文匹配不含标题 | SearchRepository 同 SQL 移植 |
| 消息上下文前后 radius 条 | ContextRepository |
| 联系人资料：QQ号/微信ID、别名历史、跨会话出现 | ContentDialog |
| 应用内图片预览 + 另存为 | 大图浮层 + FileSavePicker |
| 导入：递归发现两类 JSON、文件哈希跳过、原生ID去重、载荷哈希版本化、别名补充、媒体内容寻址复制、缺失媒体统计、后台进度 | ImportService 后台 Task 单队列 |
| 统计页 | StatsRepository |
| 空会话启动清理 | 启动时执行同规则 DELETE |

## 去重规则（从旧版移植，行为不变）

- 完成态导入文件按 SHA-256 唯一索引跳过；有缺失附件的文件允许重跑补媒体。
- QQ：(platform, account_id, native_id) 定位会话；(conversation_id, native_id, payload_hash)
  唯一定位消息；native_id 为空走 (conversation_id, semantic_hash, payload_hash)。
- 微信：同一消息 ID 的合法变体（撤回等）各自成行；JSON 精确消息 ID 去重；
  重复导出的新群名片补充 sender_aliases/conversation_aliases。
- 内容变化 → `revision_of_id` 指向旧行保存版本，不静默覆盖。
- 附件按 SHA-256 入 `media_objects`，同一文件仅存一份；`is_available=0` 表示缺失。

## 迁移工具契约

```
ChatArchive.Migrate --from <dir> [--to E:\ChatArchive]
--from 指向含 chat_archive.db 与 media\ 的目录
步骤：校验源 → 复制 db（若目标存在先备份为 .bak-<ts>）→ 复制 media（增量，按 sha 文件存在跳过）
     → 改写 managed_path 前缀 → 完整性校验（消息数/会话数/附件数/media_objects 数与源一致）
     → 生成 README.md → 输出摘要
F:\QQ+wx 全程只读。
```

## 测试策略

- xUnit + 内存 SQLite（`:memory:` 或临时文件建 schema），覆盖：
  游标分页边界、trigram/LIKE 切换、上下文半径、联系人跨会话聚合、
  QQ 重叠导入、相同文件重导、微信消息 ID 变体保留、内容版本、缺失媒体、
  媒体定位回退、空会话清理。
- 导入器测试用最小手工构造的 QQ / WeFlow JSON fixture。
- UI 手动验证清单：中文关键词搜索、打开图片预览并另存、联系人弹窗跳转、
  导入小目录核对计数、窗口缩放布局。

## 明确不做

- 不修改 `F:\QQ+wx` 任何文件；不做 MSIX 打包；不做跨平台；
  不引入 HTTP 服务层；不引入 WebView2。
