# ChatArchive 安全与易用性修复设计

日期：2026-08-31
状态：待用户书面复核

## 目标

在不修改 SQLite schema、不改变既有消息去重语义、不读取或提交 `inputapp/`
真实导出样本的前提下，完成本轮代码审查中确认的五项高价值修复：

1. 让 QQ 分块 manifest 成为导入文件集合的可信边界，并阻止路径逃逸和链接穿透。
2. 账号从旧联系人转移到新联系人前要求用户明确确认。
3. 导入运行期间保留唯一的进度与结果界面。
4. 搜索筛选项刷新时保留有效选择，避免无意扩大结果集。
5. 让消息发送者和图片预览同时支持鼠标、键盘和辅助技术。

本轮保持现有 Core/App 分层，不新增数据库表，不引入网络访问，也不重构整个
`MainWindow`。

## 已确认问题

### QQ 分块清单未形成信任边界

`QqChunkedExportFormat.Open` 与 `FileHashing.ComputeChunkedManifestDigest` 当前都直接枚举
manifest 同目录及 `chunks/` 下的全部 `.jsonl`，没有使用
`chunked.chunks[].relativePath`。结果是未在 manifest 声明的旧分块也会被导入并改变
导入摘要；若 manifest、`chunks` 或分块文件是 symlink/junction/reparse point，还可能读取
用户所选导出根目录之外的内容。

### 联系人转移缺少明确同意

账号绑定界面只在列表文本中提示“合并转移”，确认后却固定传入
`forceRebind: true`。仓储层在转移后还可能清理没有剩余账号、备注和自定义头像的旧
联系人，因此这不是普通绑定操作。

### 后台导入状态可能失联

导入对话框运行期间仍允许按“关闭”。关闭后后台任务继续执行，但临时状态监听被解除，
用户无法再看到进度、完成统计或失败原因，主界面的导入按钮又保持禁用，表现类似卡死。

### 搜索选项刷新会改变当前查询

导入完成后 `SearchViewModel.LoadOptions` 清空并重建会话和消息类型集合。
`ComboBox.SelectionChanged` 会在重建期间触发搜索，把原筛选写成 `null`，从而无提示地
扩大搜索结果。

### 关键交互只支持指针点击

时间线头像、发送者名称和图片附件仅绑定 `Tapped`。这些展示控件不是可靠的 Tab 焦点，
也缺少稳定的自动化名称，键盘和屏幕阅读器用户无法等价使用。

## 方案选择

采用“安全边界与防误操作优先”的局部加固方案：

- Core 新增一个 QQ 分块清单解析器，导入和复合哈希共享同一份已验证的文件列表。
- App 在现有 ViewModel 和 `MainWindow` 交互层补充显式状态与确认，不引入新的页面框架。
- 对能纯逻辑验证的行为先写失败测试；XAML 交互补充构建验证和人工冒烟。

没有在本轮加入大 JSON token/JSONL 行长度限制、媒体预处理或 SQLite 分批事务。这些改动
会改变超大导出的接受策略、原子性和恢复语义，应单独设计。也不修改 xUnit/MTP 项目配置：
当前 `dotnet test` 的零测试现象已确认停在本机 SDK/MTP 初始化链，现有证据不足以归因到
仓库配置，试改 `UseMicrosoftTestingPlatformRunner` 也未解决问题。

## QQ 分块文件解析

新增内部组件 `QqChunkManifest`，公开给本程序集的唯一入口为
`internal static IReadOnlyList<string> ResolveChunkFiles(string manifestPath,
CancellationToken cancellationToken = default)`。

该入口在读取 JSON 前先规范化并验证 manifest 本身：文件必须存在、不是目录、不是 reparse
point；其父目录即导出根，必须存在、是普通目录且不是 reparse point。直接调用格式适配器或
摘要函数时也执行这一步，不能只依赖上游 `ImportDiscovery` 的过滤。

### 权威清单模式

只要 manifest 根对象含有 `chunked` 属性，就进入严格清单模式。此时 `chunked` 必须是对象，
且必须含有数组类型的 `chunks`；`chunked` 为 `null`、字符串或其他非对象值，或对象缺少
`chunks`，都属于损坏的显式清单并失败，不允许回退扫描。合法的 `chunks` 数组是唯一权威
来源：

- 按数组顺序解析每个条目的 `relativePath`，不再扫描目录补充文件。
- `chunks: []` 表示结构上合法的空文件集合，磁盘上的旁路 `.jsonl` 不导入也不参与复合哈希
  的分块输入；随后现有 `ImportService` 仍会按“导出中没有消息”拒绝整次空导入，不把它
  误报为成功。
- `chunks` 不是数组、条目同时缺少 `relativePath` 与可兼容的 `fileName`、显式
  `relativePath` 为空、扩展名不是 `.jsonl`、文件不存在、两个条目解析到同一规范路径时，
  抛出包含 manifest 路径和原因的
  `ImportFormatException`。
- `relativePath` 必须是普通相对路径；拒绝 URI、盘符路径、UNC、根路径和规范化后逃出导出
  根的路径。
- manifest 根目录、每一级中间目录和最终文件必须存在且不是 reparse point，最终项必须是
  普通文件。

为兼容只提供 `fileName` 的历史条目，可在 `relativePath` 缺失时接受不含任何目录分隔符的
单一文件名，并把它安全映射到 manifest 声明的安全 `chunksDir`。具体契约如下：

- `fileName` 必须是 basename：不能为空，不得为 `.` 或 `..`，不得含 `/` 或 `\`，扩展名
  必须以不区分大小写的方式等于 `.jsonl`。
- `chunksDir` 缺失时默认使用 `chunks`；一旦显式提供，或 `fileName` 回退需要默认目录，就
  验证它非空、非 URI、非 rooted path，不含空段、`.` 或 `..` 段，规范化后仍在 manifest
  根内，并且目录存在且整个目录链没有 reparse point。
- `chunksDir` 即使当前条目使用 `relativePath`，只要 manifest 显式声明也执行上述验证，避免
  同一清单因条目形态不同而出现两种安全解释。
- 自由路径仍只允许来自 `relativePath`；`fileName` 不得携带或覆盖目录。

### 旧格式兼容模式

只有当 manifest 根对象完全没有 `chunked` 属性时，才保留原有约定目录回退：扫描 manifest
同目录和直接 `chunks/` 子目录中的 `.jsonl`，自然排序后返回。根 JSON 无效、根值不是对象、
`chunked` 类型错误或 `chunked` 对象缺少/误写 `chunks` 都失败，不视为旧格式。回退扫描同样
执行根目录、父目录和文件 reparse point 检查，不跟随链接。

这一区分保证已有无清单旧导出继续可用，同时不允许损坏或恶意的显式清单静默退回宽松
扫描。

### 共享安全路径逻辑

把 `ImportText` 中已有的 `SafeExportPath`、逐级属性检查和普通文件验证整理为
`internal static string? ResolveExistingRegularFileUnderRoot(string root,
string declaredRelativePath)`。媒体解析与 QQ 分块解析复用同一套“规范化路径 + 根包含检查 +
逐级拒绝 reparse point”规则，不在哈希和格式适配器中各复制一份。

`QqChunkedExportFormat.Open` 和 `FileHashing.ComputeChunkedManifestDigest` 都在自行打开 manifest
前调用 `QqChunkManifest.ResolveChunkFiles`。因此 manifest 文件边界、消息实际读取集合、读取
顺序和去重摘要输入完全一致；未声明文件的新增、删除或修改不会再影响权威清单导入。

除主动取消外，manifest JSON 解析、字段形态、路径规范化、文件属性和文件打开/读取错误都
转换为带 manifest 路径、声明相对路径及可读原因的 `ImportFormatException`，保留原异常作为
内部原因。导入流程当前先计算 `FileHashing` 再调用格式适配器，因此哈希阶段也必须使用同一
解析器和错误契约；已声明文件在解析前缺失，或解析后、实际打开前被删除，都会明确失败。

这里的安全承诺是“不跟随解析时可见的 reparse point，且不接受解析时落在根外的路径”。
属性检查与后续打开之间仍存在 TOCTOU 窗口；本轮不宣称能抵御具有本机写权限的攻击者在这
一瞬间并发替换目录或文件。若未来把这种敌对并发修改纳入威胁模型，需要另行采用平台级
handle-relative 打开与最终句柄验证，而不是继续叠加字符串路径检查。

## 联系人账号转移

`ContactDetailViewModel.BindSenderAsync` 的 `forceRebind` 默认值从 `true` 改为 `false`，与
`ContactRepository.BindSender` 的安全默认值一致。

在绑定对话框中：

- 未绑定账号直接以 `forceRebind: false` 绑定。
- 第一个选择对话框已经关闭、`ShowSafeAsync` 已释放全局对话框门闩后，才根据所选项继续；
  禁止在第一个对话框尚显示时嵌套打开确认框。
- `BoundContactName` 非空时，第二个确认对话框明确写出“从旧联系人转移至当前联系人”，并
  说明旧联系人若没有其他账号、备注或自定义头像可能被自动清理。其
  `PrimaryButtonText="确认转移"`、`CloseButtonText="取消"`，且
  `DefaultButton=ContentDialogButton.Close`。
- 只有用户选择“确认转移”后才传 `forceRebind: true`。
- 取消、关闭或对话框失败均不执行数据库修改。

仓储层现有拒绝转移和原子事务语义保持不变。尤其是列表中原本未绑定的账号仍以
`forceRebind: false` 提交；若列表已过期且该账号刚被其他联系人绑定，仓储层的最终检查会
拒绝操作，不能静默转移。

## 导入对话框生命周期

导入运行时：

- `RefreshButtons` 同步设置 `IsPrimaryButtonEnabled` 和 `PrimaryButtonText`：运行时禁用主按钮
  并显示“正在导入…”，结束后启用并恢复“关闭”。
- 在调用 `ShowSafeAsync` 前订阅 `ContentDialog.Closing`，处理器只执行
  `args.Cancel = _import.IsRunning`；不调用 `Hide`，也不制造第二个关闭路径。
- 文件选择、清空和开始按钮继续禁用，避免改变正在执行的输入集合。
- 完成或失败后保留最终摘要，用户主动关闭时再解除临时监听。

`Closing`、`PropertyChanged` 和路径集合监听都在同一个 `finally` 中解除。即使对话框显示失败，
也不遗留对窗口或 ViewModel 的订阅。

本轮不提供“取消导入”按钮，因为 `ImportService` 当前没有贯穿 UI 到解析、哈希和数据库事务
的可用取消令牌。显示一个不能可靠停止工作的取消按钮会造成错误预期。

## 搜索筛选状态

`SearchViewModel.LoadOptions()` 改为返回单调递增的 `long generation`，并新增
`event Action<long, bool> OptionsReloaded`，其中布尔值表示是否成功应用。每次调用先递增并
捕获 generation，再后台读取一个不可变的 `SearchOptionsSnapshot`（会话列表与
`FilterOptions`）。回到 UI 线程后先比较当前 generation：只有最新请求可以替换集合、设置
错误并触发一次 `OptionsReloaded(generation, success)`；过期请求不修改任何状态，也不发
通知。为可重复测试乱序完成，ViewModel 增加仅供程序集测试使用的选项加载委托注入点，生产
构造函数仍组合现有两个仓储调用。

`MainWindow` 增加搜索选项刷新保护状态，所有 `_search.LoadOptions()` 调用统一通过一个包装
流程：

1. 在刷新前保存两个 ComboBox 当前的会话 ID 和消息类型值，并开启
   `_suppressSearchFilterRefresh`；调用返回后，把 generation 与这份快照保存为唯一的当前
   刷新状态。
2. `OptionsReloaded` 只有 generation 与当前刷新状态相同时才处理；即使将来事件来源改变，
   MainWindow 也忽略过期通知。失败时不清空现有集合，保留选择和结果并解除抑制。
3. 成功后由一个内部纯函数按值在新集合中恢复选择。值仍存在时不执行新搜索；值已不存在时
   显式回退到
   “全部”。
4. 恢复完成后解除抑制。若原值已不存在且此前已经执行过搜索，只触发一次使用回退条件的
   新搜索。

`SearchFilter_Changed` 和具体会话/消息类型的 SelectionChanged 处理器在刷新保护期间直接
返回，避免集合重建过程产生中间查询。

完成通知必须在 UI 线程、集合更新之后触发。最新请求无论成功或失败都解除刷新保护，防止
搜索永久失去响应；多个并发刷新按“最后发起者获胜”，不会被较慢的旧快照覆盖。

## 键盘与辅助技术

把以下展示型点击目标替换或包裹为透明无边框 `Button`，并控制每条消息的 Tab 数量：

- 收到消息的发送者名称是唯一的发送者 Tab 停靠点；头像仍支持指针点击，但其包装按钮设置
  `IsTabStop=false`，避免两个相邻控件执行完全相同的动作。
- 发出消息没有可见名称按钮，因此头像包装按钮必须是 Tab 停靠点。
- 每个可预览图片按钮都是 Tab 停靠点。

按钮使用 `Click` 进入与现有鼠标路径相同的资料或图片预览逻辑，从而自动获得 Tab 焦点及
Enter/Space 激活行为。`MessageEntry` 新增 `SenderAutomationName`：展示名称有效时为
“查看发送者：{去除首尾空白后的展示名称}”，空白时稳定回退为“查看发送者”。
`AttachmentEntry` 新增 `PreviewAutomationName`：文件名有效时为
“预览图片：{去除首尾空白后的文件名}”，缺失或空白时回退为“预览图片”。按钮把这些属性
绑定到 `AutomationProperties.Name`，图片按钮同时使用同值作为 ToolTip。按钮显式清零边框、
背景和多余 padding，保持原有气泡对齐和图片尺寸。

单条收到消息的焦点顺序是“发送者名称 → 气泡内各图片”；单条发出消息是“气泡内各图片 →
发送者头像”，与 XAML 的视觉/声明顺序保持一致。附件不存在时自然跳过对应按钮。

不在原 `Image`/`TextBlock` 上单独拼接 KeyDown 逻辑，以免鼠标、键盘和辅助技术形成三套
不同事件路径。

## 错误处理

- 显式 QQ 清单无效时失败关闭，不退回目录扫描；错误由现有单文件导入边界记录为该文件
  失败，同批其他文件继续。
- 路径验证只报告声明的相对路径和原因，不读取、记录或展示根目录外文件内容。
- 联系人二次确认取消时不调用仓储；仓储异常继续由现有窗口级错误提示显示。
- 搜索选项加载失败保留旧选择和旧结果，并通过现有 `ErrorMessage`/InfoBar 告知用户。
- 导入窗口禁止关闭只持续到 `IsRunning=false`；异常路径的 `finally` 仍负责恢复状态。

## 测试策略

遵循测试驱动开发，每项可自动化行为先建立失败测试。

### Core 自动测试

- 权威 manifest 只声明 `chunks/a.jsonl`，旁路的 `chunks/old.jsonl` 和根目录
  `old.jsonl` 不被导入。
- 修改未声明文件不改变复合摘要；修改声明文件会改变摘要。
- `chunks: []` 不读取或哈希磁盘旁路文件，端到端导入仍以零消息失败。
- 只有根对象完全缺少 `chunked` 的旧 manifest 才扫描约定位置并保留现有旧格式摘要行为。
- 覆盖 `chunked: null`、错误标量类型、`chunked: {}`、对象缺少 `chunks`、非数组 `chunks`，
  均抛 `ImportFormatException` 而不回退。
- 覆盖默认 `chunksDir` 和一个安全的自定义 `chunksDir`；恶意 `chunksDir` 的空值、URI、绝对
  路径、空段、`.`、`..`、越界和链接目录全部失败。
- 覆盖 `fileName` 的合法 basename，以及含 `/`、`\`、`.`、`..` 或错误扩展名的拒绝路径。
- 缺失/解析后删除的声明文件、重复规范路径，以及 `relativePath` 的非 `.jsonl`、URI、绝对
  路径、UNC 和越界路径全部失败关闭，并断言错误包含 manifest 路径和可读原因。
- 在平台允许创建链接时，覆盖导出根内的 `manifest.json` 文件链接、`chunks` 目录链接、
  中间目录链接和分块文件链接；manifest 链接场景分别断言
  `QqChunkedExportFormat.Open` 与 `FileHashing.ComputeImportDigest` 在读取根外有效内容前失败。
  不能创建链接的环境按现有媒体安全测试模式跳过。
- 当前脱敏 QQ chunked fixture 继续导入一条预期消息。

### App 自动测试

- `ContactDetailViewModel.BindSenderAsync` 未显式要求强制转移时拒绝已归属账号，原归属保持
  不变；显式强制路径继续由现有仓储测试覆盖。
- 使用可控加载委托让 generation 1 晚于 generation 2 完成，断言只有 generation 2 能更新
  集合并发出通知；最新加载失败会通知失败且不清空已有选项，过期失败也不覆盖错误状态。
- 选项恢复纯函数覆盖“值仍存在”“会话已删除”“消息类型已删除”和“尚未执行搜索”，输出
  恢复值及是否需要恰好一次搜索，不直接依赖 WinUI 控件。
- `SenderAutomationName` 覆盖正常、空字符串和纯空白发送者名称；
  `PreviewAutomationName` 覆盖正常、`null`、空字符串和纯空白文件名。

### 构建与人工冒烟

- WinUI XAML 编译验证透明 Button 的属性、事件签名与绑定均有效。
- 开始导入后关闭按钮不可用；任务结束后按钮恢复且摘要仍可见。
- 取消联系人转移后数据库不变；确认后只执行一次转移。
- 搜索选择具体会话与图片类型，连续触发两次刷新并让旧请求后完成，选择和结果仍以最新刷新
  为准；MainWindow 的事件订阅、generation 匹配和 ComboBox 恢复作为人工集成冒烟项。
- 收到消息的 Tab 只聚焦一次发送者名称后进入图片，发出消息先经过图片再到头像；
  Enter/Space 与鼠标打开相同内容，Narrator 在姓名/文件名缺失时仍能读出稳定动作名称。

## 终验

- 运行 Debug 与 Release 构建，要求 0 warning、0 error。
- 先分别运行以下发现命令并确认列出的测试数非零，再去掉 `-list tests` 运行完整套件并记录
  测试数量与退出码：

  ```powershell
  & 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -list tests -noLogo
  & 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -list tests -noLogo
  & 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -noLogo -automated
  & 'tests\ChatArchive.App.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ChatArchive.App.Tests.exe' -noLogo -automated
  ```

  Release 使用相同相对结构下的 `Release` 路径重复执行。
- 再运行一次 `dotnet test` 记录已知 MTP 环境结果，但不把未经证实的项目配置改动混入本轮。
- 运行 `git diff --check`，确认无空白错误。
- 检查 `git status --short`，确认只包含本轮文件且 `inputapp/` 未被暂存或修改。

## 预计文件范围

- 新增：`src/ChatArchive.Core/Importing/QqChunkManifest.cs`
- 修改：`src/ChatArchive.Core/Importing/ImportText.cs`
- 修改：`src/ChatArchive.Core/Importing/ExportFormats.cs`
- 修改：`src/ChatArchive.Core/IO/FileHashing.cs`
- 修改：`src/ChatArchive.App/ViewModels/ContactDetailViewModel.cs`
- 修改：`src/ChatArchive.App/ViewModels/SearchViewModel.cs`
- 新增：`src/ChatArchive.App/ViewModels/SearchOptionRefresh.cs`
- 修改：`src/ChatArchive.App/ViewModels/TimelineProjection.cs`
- 修改：`src/ChatArchive.App/ViewModels/TimelineViewModel.cs`
- 修改：`src/ChatArchive.App/MainWindow.xaml`
- 修改：`src/ChatArchive.App/MainWindow.xaml.cs`
- 修改：相关 Core/App 测试文件。

若实现中发现必须改变数据库 schema、导入原子性或公开仓储接口，立即停止并升级设计，不在
本方案内顺带重构。

## 非目标

- 不修改 schema_version、消息哈希算法或联系人仓储事务结构。
- 不为 JSON token、JSONL 单行或整体输入增加新大小限制。
- 不拆分长导入事务、不增加断点恢复或媒体预处理阶段。
- 不实现导入取消、撤销联系人转移或完整的 WinUI UI 自动化基础设施。
- 不修改 xUnit/MTP 依赖和测试项目属性，不新增 CI 或包锁定方案。
- 不修复所有现存对话框异常吞噬问题；本轮只保证新增交互的失败不被当成用户确认。
