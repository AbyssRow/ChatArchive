# Inputapp 导入格式对齐设计

## 1. 背景与目标

ChatArchive 只面向外部只读审计目录 `E:/AgentCode/ChatArchive/inputapp/` 中的三个上游源码快照：

- WeFlow `6f8e7e89f9b1`
- CipherTalk `6b886e682472`
- QQ Chat Exporter `888b51fab652`

本计划启动时，JSON/JSONL 主格式已基本对齐，但 CSV、Markdown、TXT、SQL、Excel、HTML 和媒体路径仍与实际 writer 不一致；当时的文本解析器主要来自提交 `bbaa9b1` 中的手写理想样例，不能证明是三个上游的真实历史协议。截至最终兼容性 characterization，这些缺口已按当前 writer 纠正或明确排除。本设计以用户本地只读审计文档 `E:/AgentCode/ChatArchive/docs/EXPORT_FORMATS_SPEC.md` 和上述外部源码快照的 writer 为格式依据；这些输入都不是本项目要复制、修改或提交的 artifact。

目标是让每个受支持状态同时满足：实际产物可以被发现、能够由唯一适配器识别、至少生成一条字段正确的消息，并对格式固有的信息损失采用明确且稳定的降级规则。

本设计取代 `docs/superpowers/specs/2026-08-26-multi-format-chat-import-design.md` 中的 HTML、CSV、Markdown、TXT 和 SQL 章节；旧设计的 JSON/JSONL 部分仅在与当前源码和本设计不冲突时继续有效。

## 2. 范围

### 2.1 保留并验证

- WeFlow Standard/Detailed JSON、ArkMe JSON
- CipherTalk Detailed JSON
- WeFlow/CipherTalk ChatLab 0.0.2 JSON 与 JSONL
- QQ Chat Exporter 单文件 JSON 与分块 JSONL

这些格式不重写现有解析架构，只补充当前 writer 字段的 golden fixture 和必要的缺口修复。

### 2.2 新增或纠正

- WeFlow WeClone CSV
- WeFlow Markdown
- WeFlow TXT
- WeFlow PostgreSQL SQL
- WeFlow Excel，包括紧凑列、私聊完整列和群聊完整列
- CipherTalk PostgreSQL SQL
- CipherTalk Excel，包括可选头像和聊天记录列
- QQ Chat Exporter TXT
- QQ Chat Exporter Excel，包括可选群头衔列和资源工作表
- WeFlow 写入布局 A 中受限的单层父目录媒体引用

### 2.3 删除或不实现

- 删除所有 HTML 导入支持。HTML 是供浏览器直接查看的展示产物，不是稳定的数据交换协议；部分上游 HTML 已经不可逆地丢失原始消息字段。
- 不实现 CipherTalk TXT。其 UI 类型中仍有 TXT，但当前 `exportService` 没有对应后端分支，不能产生有效产物。
- 不保留没有当前外部 `inputapp` writer 或 fixture 依据的通用/理想 CSV、Markdown、TXT 和 SQL 语法。
- 不执行 SQL、Excel 公式、宏、外部工作簿关系、HTML 或 JavaScript。

## 3. 架构

沿用 `IChatExportFormat`、`ExportFile` 和 `ParsedMessage` 适配器边界。发现器只按受支持扩展名选择候选文件；各适配器再使用上游专属、不可混淆的格式签名完成匹配。底层读取能力可以共享，但来源识别和字段映射必须按上游分开。

### 3.1 共享读取组件

- RFC 4180 CSV 记录读取器继续作为 WeFlow CSV 的底层能力。
- SQL 语句读取器负责处理注释、字符串转义、建表列顺序和多值 `INSERT`，但只向 WeFlow/CipherTalk 两个专属映射器暴露已知表的行。
- `.xlsx` 共享读取层已按后续迁移设计改用固定版本 `DocumentFormat.OpenXml` 3.5.1：由 SDK 处理 OPC/部件关系，并用 `OpenXmlReader` SAX API 流式读取工作表；项目只保留窄化 ZIP 安全预检和来源适配器所需的严格策略。该现状取代本设计最初“使用 `System.IO.Compression` 与 `XmlReader` 自研完整读取层”的历史选择，详见 `2026-08-30-openxml-sdk-reader-migration-design.md`。
- 文本块读取保持逐行流式处理，并在消息边界检查取消令牌。

### 3.2 来源专属适配器

- `WeFlowCsvExportFormat`
- `WeFlowMarkdownExportFormat`
- `WeFlowTextExportFormat`
- `QqTextExportFormat`
- `WeFlowSqlExportFormat`
- `CipherTalkSqlExportFormat`
- `WeFlowExcelExportFormat`
- `CipherTalkExcelExportFormat`
- `QqExcelExportFormat`

类名可以根据现有文件组织微调，但注册表中必须是来源专属适配器，不能再以 `Platform == "text"` 或 `Platform == "sql"` 代替真实平台。以上微信格式返回 `wechat`，QQ 格式返回 `qq`。

### 3.3 HTML 移除

- 从 `ExportFormats.Default` 删除 `ChatHtmlExportFormat`。
- 从 `ImportDiscovery.SupportedExtensions` 删除 `.html` 和 `.htm`。
- 删除 `HtmlDataExtractor.cs` 和专属测试。
- README 与兼容矩阵明确说明 HTML 是浏览产物，不作为导入格式。

## 4. 格式识别与字段映射

### 4.1 WeFlow CSV

格式签名是去除 UTF-8 BOM 后完全包含以下表头：

```text
id,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime
```

映射规则：

- `id` -> `LocalId`
- `MsgSvrID` -> `NativeId`
- `CreateTime` -> `TimestampMs`
- `is_sender` -> outgoing/incoming
- `talker` -> 发送者显示名；该字段不是稳定微信 ID
- `type_name` -> 标准消息类型
- `msg` -> 正文
- `src` -> 媒体声明路径或来源元数据，并在是安全本地路径时生成附件

该格式不保存会话 ID。标题取文件名；内部会话 ID 使用第 5 节定义的路径派生 ID。

### 4.2 WeFlow Markdown

格式签名同时要求文档头包含 `会话ID`、`会话类型` 和 `导出工具: WeFlow`。消息以 `## <时间> <发送者>` 开始，正文持续到下一消息标题或文件末尾。

- 文档一级标题 -> 会话标题
- `会话ID` -> 会话原生 ID
- `会话类型` -> group/private
- 消息标题 -> 时间和发送者
- 正文保留普通文本、引用块的可搜索文本，并解析图片/文件 Markdown 链接为附件
- 发送者为 `我` 时是 outgoing；其他发送者是 incoming

### 4.3 WeFlow TXT

消息头严格匹配 `<时间> '<发送者>'`，正文持续到空行后的下一合法消息头或文件末尾。解析后移除包裹发送者的单引号，保留正文内部换行。标题取文件名，内部会话 ID 使用路径派生 ID；仅发送者为 `我` 时标为 outgoing。

### 4.4 QQ TXT

格式签名要求文件头包含 `[QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]`、`聊天名称:` 和 `聊天类型:`。消息块支持 writer 选项控制的可选序号、发送者、类型和资源统计，但 `时间:` 与 `内容:` 是消息恢复的核心边界。

- 文件头 -> 标题和 group/private
- `[N]` -> `LocalId`（存在时）
- 发送者行 -> 显示名
- `时间:` -> 时间戳
- `类型:` -> 消息类型（存在时）
- `内容:` 及其后续正文 -> 内容
- `资源:` 与缩进的资源行 -> 可恢复的附件元数据

TXT 不保存 chat ID、本人 UID/UIN 或可靠方向，因此使用路径派生会话 ID，非系统消息按 incoming 导入，不按昵称猜测本人。

### 4.5 WeFlow SQL

格式签名要求 `weflow_messages` 表及实际十列结构。只解析该表的 `INSERT`：

- `session_id` -> 会话原生 ID
- `local_id`、`message_id` -> 消息 ID
- `create_time` -> 时间戳
- `sender` -> 发送者原生 ID
- `is_send` -> 方向
- `local_type`、`media_type` -> 标准类型
- `content` -> 正文
- `media_path` -> 安全附件路径

SQL 不保存会话显示名，标题取文件名。

### 4.6 CipherTalk SQL

格式签名要求 CipherTalk 文件头或 `sessions`、`messages` 的当前列结构。只解析这两个表的 `INSERT`，且不执行 `DELETE`、`CREATE INDEX` 或其他语句。

`sessions` 提供 `wxid`、`display_name`、`session_type` 和 `owner_id`；`messages` 提供 `session_wxid`、`local_id`、`create_time`、`formatted_time`、`msg_type`、`content`、`is_send`、`sender_username`、`sender_display_name`、`group_nickname`、`reply_to_message_id`。文本型 `msg_type` 按 CipherTalk 当前中文类型名和数值回退映射到标准类型。

### 4.7 WeFlow Excel

识别条件：工作簿中存在 `聊天记录` 工作表，前部元数据区包含 `会话信息`、`微信ID`、`导出工具` 和值 `WeFlow`。表头可能是：

- 紧凑：`序号, 时间, 发送者身份, 消息类型, 内容`
- 私聊完整：`序号, 时间, 发送者昵称, 发送者微信ID, 发送者备注, 发送者身份, 消息类型, 内容`
- 群聊完整：在私聊完整结构中增加 `群昵称`

常规非流式 writer 的元数据行包含会话 ID/昵称，并且只在群聊时增加备注；下一行包含导出工具、版本、平台和导出时间。流式 writer 使用更小的元数据布局：会话 ID/昵称以及导出工具/导出时间，因此不能假定每个布局都有备注或平台单元格。消息内容单元格的 hyperlink 关系可作为媒体路径声明；ExcelJS 会把当前 writer 的安全相对媒体目标序列化为 `TargetMode="External"`。读取器只把受边界约束的安全相对目标保留为惰性声明，绝不打开或获取目标；根路径、URI 和越界目标继续忽略或拒绝。紧凑结构和群聊完整结构没有可靠本人标志时，不根据显示名猜测 outgoing。

### 4.8 CipherTalk Excel

识别条件：某工作表首行包含固定核心列 `序号, 时间, 日期, 时刻, 星期, 发送者, 微信ID, 消息类型, 消息内容, 原始类型代码, 时间戳`。`头像链接` 与 `聊天记录详情` 是可选列。

消息以 `时间戳` 为主时间源，`时间` 为回退；`微信ID` 是发送者 ID。工作簿不保存会话 ID、本人 ID 或可靠方向，因此标题取工作表名，内部会话 ID 使用路径派生 ID，非系统消息按 incoming 导入。

### 4.9 QQ Excel

识别条件：`聊天记录` 工作表首行包含核心列 `序号, 时间, 发送者, 发送者QQ号, 消息类型, 消息内容, 是否撤回, 资源数量`；`群头衔` 是可选列。

`资源列表` 工作表存在时，读取 `时间, 发送者, 发送者QQ号, 资源类型, 文件名, 大小(字节), URL`。资源按时间、发送者 QQ 号和发送者名称的组合关联，且只有该组合在消息表中唯一时才挂载；无法唯一关联时不错误挂到消息上，而是只保留消息的资源数量元数据。

QQ Excel 不保存 chat info 或本人 UIN。标题取文件名，内部会话 ID 使用路径派生 ID，非系统消息按 incoming 导入。

## 5. 稳定 ID 与降级规则

当格式不保存会话原生 ID 时，使用规范化绝对文件路径的 SHA-256 摘要生成内部 ID，例如 `file:<digest>`。路径使用 `Path.GetFullPath` 后按 Windows 路径语义规范化大小写和目录分隔符。这样同名文件不会合并，同一文件重复导入仍能命中同一会话。

不允许使用显示名推断原生账号或本人身份。统一模型只接受 incoming、outgoing 和 system；缺少方向时使用 incoming，系统类型使用 system。格式未保存的字段保持空值或使用上述明确回退，不从其他无关文件猜测。

必需结构损坏、表头不匹配或无法生成任何合法消息时，抛出包含文件路径和具体原因的 `ImportFormatException`，不创建空会话。

## 6. 媒体路径安全

现有同目录、`resources`、`media` 和共享媒体目录探测继续保留。额外允许 WeFlow 写入布局 A 的以下形式：

```text
../images/<file>
../voices/<file>
../videos/<file>
../emojis/<file>
../file/<file>
```

该例外必须同时满足：

1. 只有一个前导 `..` 路径段；
2. 第二段是上述精确白名单之一；
3. 解析结果位于 `exportRoot` 的直接父目录内；
4. 目标是磁盘上真实存在的普通文件；
5. 后续路径不再包含 `.` 或 `..` 段。

其他绝对路径、UNC 路径、驱动器路径、任意父目录穿越、符号链接越界和不存在的父目录候选继续拒绝。普通安全相对路径在文件缺失时仍可保留声明路径；父目录例外只有目标存在时才返回物理路径。

## 7. Excel 安全与资源约束

- 只接受 `.xlsx` ZIP/OpenXML 包，不接受 `.xls`、`.xlsm` 或二进制工作簿。
- 只解析包内工作簿定义引用的工作表和共享字符串。
- 包根与工作簿部件的外部关系一律拒绝。工作表上只有精确标准 hyperlink 关系可进入当前 writer 所需的受限例外：ExcelJS 虽把安全相对媒体 Target 标记为 `TargetMode="External"`，读取器仍只把它作为惰性附件声明返回，绝不读取网络或外部文件；URI、根路径、驱动器路径、UNC 和越界 Target 被忽略或拒绝。该规则由 `2026-08-30-openxml-sdk-reader-migration-design.md` 取代了最初“拒绝所有 `TargetMode="External"`”的历史表述。
- 公式单元格只读取已有缓存值，不计算公式；没有缓存值则视为空。
- 不读取宏、自定义 XML、嵌入对象、图片二进制或外部数据连接。
- 工作表行通过 SDK `OpenXmlReader` SAX API 流式枚举，并在行/超长单元格循环中检查取消令牌；共享字符串在打开阶段以前向读取方式建立只读索引。
- ZIP 或 XML 损坏、关系缺失和非法单元格引用转换为可理解的 `ImportFormatException`。

## 8. 发现顺序与误识别控制

`ImportDiscovery.SupportedExtensions` 调整为：

```text
.json .jsonl .csv .md .txt .sql .xlsx
```

HTML 扩展名被移除。格式注册按强签名优先：JSON/JSONL、来源专属 CSV/Markdown/TXT、来源专属 SQL、来源专属 Excel。每种格式的 `Matches` 只在扩展名和必要签名同时满足时返回 true。普通 Markdown、日志 TXT、数据库 SQL 和业务 Excel 不应被发现为聊天记录。

QQ 分块 JSONL 的 manifest 剪枝规则保持不变。

## 9. 测试策略

测试使用从当前 writer 结构提取的最小 golden fixture，而不是手写理想协议。每个 fixture 记录上游、提交基线和对应 writer 文件。至少覆盖：

- 发现和唯一适配器匹配；
- 会话原生 ID或路径派生 ID、标题和类型；
- 消息数量、时间戳、发送者、方向、类型和正文；
- 一个媒体路径或附件；
- 导入到 SQLite 后的会话、消息、发送者和附件结果。

格式专项覆盖：

- WeFlow Excel 三种动态表头；
- CipherTalk Excel 两个可选列；
- QQ Excel 可选群头衔和资源工作表；
- Open XML SDK SAX 路径下的 shared string、inline string、数字、日期、缓存值，以及受限的内容单元格 hyperlink 关系声明；
- SQL 注释、转义单引号、多行和多值 `INSERT`；
- TXT/Markdown 多行正文和末条消息无尾随空行；
- JSON/JSONL 当前可选字段与版本标记；
- HTML 不再发现；
- 普通无关 `.txt`、`.md`、`.sql`、`.xlsx` 不误识别；
- 损坏 ZIP/XML、禁止的外部关系、公式无缓存、绝对路径和目录穿越安全失败；
- 长文件枚举过程中的取消。

实现遵循 TDD：每个格式先加入最小失败测试，确认因当前缺口失败，再实现最小修复并运行该格式测试。最终运行核心测试、应用测试和 `dotnet test ChatArchive.sln` 全套测试。

## 10. 文档与完成标准

用户本地 `E:/AgentCode/ChatArchive/docs/EXPORT_FORMATS_SPEC.md` 是本次已审计的只读输入，不在分支内新增或修改。项目跟踪的输出是：

- README 来源专属支持格式说明；
- 来源归属 fixture、注册表/发现/完整导入测试；
- 本设计与实施计划中对只读输入和已实现 SDK 迁移的状态说明；
- 必要的适配器与发现器注释。

完成标准：

1. 第 2.2 节每个真实格式都有源码对应 fixture 和通过的解析/导入测试；
2. HTML 导入不再出现在发现、注册、文件选择器或当前支持声明中；兼容旧归档的平台显示、负面测试和明确的移除说明不属于导入支持；
3. 没有无上游依据的通用文本格式分支；
4. WeFlow 布局 A 媒体可解析且安全负面测试通过；
5. 所有现有及新增测试无错误、失败或警告；
6. README、来源 fixture 与测试形成的兼容矩阵与实现结果一致。
