# ChatArchive UI Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复时间线、日期、附件展示和审查确认的 WinUI 交互缺陷，补齐搜索筛选并建立 App 逻辑回归测试。

**Architecture:** 保持现有 Core/App 分层与数据库契约。把时间线合并、附件投影、筛选解析和请求版本判断提取为无窗口依赖的 App 逻辑；ViewModel 负责异步状态，`MainWindow` 只处理渲染、滚动与系统选择器。

**Tech Stack:** C# / .NET 10 / WinUI 3 / Windows App SDK 2.4 / CommunityToolkit.Mvvm / xUnit v3 / SQLite

**Spec:** `docs/superpowers/specs/2026-08-21-ui-reliability-design.md`

## Global Constraints

- 保持 schema_version 1 与 `ConversationRepository.ListMessages` 公开签名不变。
- 不直接修改或以测试方式打开 `E:\ChatArchive\chat_archive.db`；UI 冒烟使用隔离副本或临时库。
- 新增行为按 TDD：先看到针对缺陷的测试失败，再写最小实现并跑绿。
- 不做无关的页面重构；保留 x64、非打包、自包含部署配置。

---

### Task 1: App 逻辑测试边界与时间线投影

**Files:**
- Create: `tests/ChatArchive.App.Tests/ChatArchive.App.Tests.csproj`
- Create: `tests/ChatArchive.App.Tests/TimelineProjectionTests.cs`
- Create: `src/ChatArchive.App/ViewModels/TimelineProjection.cs`
- Modify: `ChatArchive.sln`

**Interfaces:**
- Produces `AttachmentEntry`（附件、解析路径、图片/缺失状态、展示标签）与 `TimelineProjection`（正文清洗、日期键、页条目生成/合并）。
- 技术正文仅在等于占位符、附件声明路径、源路径、托管路径或文件名时隐藏；普通文本保留。

- [ ] 写测试覆盖可用图片、可用普通文件、显式缺失、无附件媒体隐式缺失、技术正文隐藏、真实说明保留和多附件。
- [ ] 运行 App 测试并确认因投影类型不存在而失败。
- [ ] 实现最小投影类型并让附件测试通过。
- [ ] 写测试覆盖旧页头插、同日跨页单一分隔、跨日顺序和 `HH:mm:ss`。
- [ ] 运行确认失败，实现页合并并跑绿。

### Task 2: 时间线状态、向上分页与焦点跳转

**Files:**
- Modify: `src/ChatArchive.App/ViewModels/TimelineViewModel.cs`
- Modify: `src/ChatArchive.App/Views/TimelineTemplateSelector.cs`
- Modify: `src/ChatArchive.App/MainWindow.xaml`
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs`
- Test: `tests/ChatArchive.App.Tests/TimelineStateTests.cs`

**Interfaces:**
- `TimelineViewModel` 产生初始到底部与焦点定位事件；`JumpToMessage` 设置 `context.ConversationId` 和最旧上下文消息游标。
- `MessageEntry` 暴露 `DisplayContent`、`HasDisplayContent`、`Attachments`、`MissingMediaText` 和本地 `TimeText`。

- [ ] 写失败测试证明迟到页不能污染新会话、搜索上下文使用正确会话/游标。
- [ ] 用递增请求版本与异步 RelayCommand 实现状态保护并跑绿。
- [ ] 把更旧页插入头部，初始页触发到底部，焦点上下文触发 `ScrollIntoView`。
- [ ] 在 XAML 配置底部对齐/`KeepItemsInView`，顶部 80px 触发加载并保护初始定位。
- [ ] 统一收发附件模板，新增系统消息模板并验证 App 测试。

### Task 3: 搜索筛选、分页与请求一致性

**Files:**
- Modify: `src/ChatArchive.App/ViewModels/SearchViewModel.cs`
- Modify: `src/ChatArchive.App/MainWindow.xaml`
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs`
- Test: `tests/ChatArchive.App.Tests/SearchStateTests.cs`
- Test: `tests/ChatArchive.Core.Tests/SearchRepositoryTests.cs`

**Interfaces:**
- `SearchViewModel` 新增 `ConversationFilter`、`MessageTypeFilter`、`DateFrom`、`DateTo`、`HasMore`、`ErrorMessage`；筛选转成现有 `SearchFilter`。
- 具体会话选项来自会话仓储（最多 1000），消息类型来自 `GetFilterOptions`。

- [ ] 写失败测试覆盖 Core 会话/消息类型/日期组合筛选及本地日期右边界。
- [ ] 若仓储已有能力则只补测试；修正发现的实际差异并跑绿。
- [ ] 写失败测试覆盖末页 `HasMore=false`、空游标不重复加载和旧查询结果丢弃。
- [ ] 实现搜索状态保护与完整筛选映射并跑绿。
- [ ] 在搜索页增加具体会话、消息类型和两个 `CalendarDatePicker`，末页隐藏加载按钮。

### Task 4: 其余交互与错误反馈收口

**Files:**
- Modify: `src/ChatArchive.App/MainWindow.xaml`
- Modify: `src/ChatArchive.App/MainWindow.xaml.cs`
- Modify: `src/ChatArchive.App/ViewModels/ConversationListViewModel.cs`
- Modify: `src/ChatArchive.App/ViewModels/StatsViewModel.cs`
- Modify: `src/ChatArchive.App/ViewModels/ContactViewModel.cs`
- Test: `tests/ChatArchive.App.Tests/UiInputTests.cs`

**Interfaces:**
- 空会话筛选标签映射为 `(null, null)`；非空标签必须恰为 `platform|kind`。
- ViewModel 失败状态汇总到窗口级 `InfoBar`；成功的新操作清除旧错误。

- [ ] 写失败测试复现“全部”筛选越界并实现安全解析。
- [ ] 为会话列表增加请求版本与错误状态，统计查询增加错误状态。
- [ ] 联系人会话改为 `ItemClick` 单击跳转；清理无效身份字符串拼接。
- [ ] 修正 FileSavePicker 扩展名为 `.ext`，附件用系统默认应用打开并报告失败。
- [ ] 用可解除的命名委托管理导入对话框订阅，导入期间禁用入口。
- [ ] 修复 `dotnet format --verify-no-changes` 报告的现有空白问题。

### Task 5: 完整验证与审查

- [ ] 运行 `dotnet test ChatArchive.sln -c Release`，要求所有测试通过且无跳过。
- [ ] 运行 `dotnet build ChatArchive.sln -c Release -p:Platform=x64 --no-restore`，要求 0 警告 0 错误。
- [ ] 运行 `dotnet format ChatArchive.sln --verify-no-changes --no-restore`。
- [ ] 运行 `dotnet list ChatArchive.sln package --vulnerable --include-transitive`。
- [ ] 对照设计逐项检查 diff；UI 冒烟使用隔离数据，覆盖顶部翻页、日期、附件、搜索、联系人、另存为和导入状态。
- [ ] 提交最终变更并按 finishing-a-development-branch 流程交付。
