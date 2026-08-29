# Open XML SDK SAX 读取器迁移设计

**日期：** 2026-08-30
**状态：** 待用户审阅
**范围：** 仅替换 `.xlsx` 共享读取层的实现，不改变 WeFlow、CipherTalk、QQ 三个来源的格式支持范围

## 1. 背景

当前分支已经实现 `OpenXmlWorkbookReader`，并由 WeFlow、CipherTalk 和 QQ 三个 Excel 适配器共享。它使用 `ZipArchive` 与 `XmlReader` 手工处理 OPC 路径、内容类型、关系、工作簿、共享字符串、工作表、单元格和超链接，约 1,425 行。现有测试已经覆盖流式行读取、ExcelJS 媒体超链接、安全边界、结构校验、取消和资源释放。

Microsoft Open XML SDK 已提供面向大工作表的前向 SAX API `OpenXmlReader`。继续维护完整的自研 OPC/OpenXML 解析层没有必要；本迁移让 SDK 负责标准包、部件和元素读取，同时保留项目特有的严格输入策略。

本设计取代 [原兼容性设计](./2026-08-29-inputapp-import-compatibility-design.md) 第 3.1 节中“使用 `System.IO.Compression` 与流式 XML 自行读取”的实现选择，也取代第 7 节“所有 `TargetMode=External` 关系均拒绝”的旧表述。后者更新为第 6 节所述的、已经由当前 writer 和测试验证的工作表媒体 hyperlink 例外；除此之外，支持格式、字段映射和安全语义仍以原设计为准。

## 2. 目标与非目标

### 2.1 目标

- 引入固定版本 `DocumentFormat.OpenXml` 3.5.1。
- 使用 SDK 的 `SpreadsheetDocument`、部件/关系 API 和 `OpenXmlReader` SAX API 读取 `.xlsx`。
- 保持现有内部门面不变：`Open`、`Sheets`、`ReadRows` 以及 `OpenXmlSheet`、`OpenXmlRow`、`OpenXmlCell` 的调用方式不变。
- 保持 WeFlow、CipherTalk、QQ Excel 适配器的识别、映射、方向判断和媒体解析行为不变。
- 保持现有测试所定义的安全边界、错误归一化、取消和资源释放行为。
- 删除被 SDK 取代的自研 OPC 路径解析、关系解析和通用 XML 读取代码，降低维护面。

### 2.2 非目标

- 不新增 `.xls`、`.xlsb`、`.xlsm` 或其他表格格式。
- 不新增 HTML、通用 Excel 或旧版导出语法兼容。
- 不使用 DOM 方式把完整工作表载入内存。
- 不计算公式，不执行宏，不读取嵌入对象或外部数据连接。
- 不访问网络，也不跟随外部超链接。
- 不改变导入器的公共产品行为或扩展名发现顺序。

## 3. 总体架构

迁移后的读取路径分成两层：

1. **窄化的 ZIP 安全预检。** 在 SDK 打开文档前，仅检查 SDK 不保证满足的项目策略，例如重复或大小写冲突的 ZIP 条目、禁止载荷以及当前测试覆盖的包清单约束。
2. **SDK 驱动的 OpenXML 读取。** 使用 `SpreadsheetDocument` 解析包和部件关系，使用 `OpenXmlReader` 顺序读取共享字符串和工作表元素。

保留窄化预检不是继续维护第二套 OpenXML 解析器。预检不得读取工作簿、工作表或共享字符串的业务结构；标准 OPC 部件发现、关系导航和元素读取均交给 SDK。

`OpenXmlWorkbookReader` 继续作为适配器与第三方包之间的防腐层。三个来源适配器不直接引用 SDK 类型，因此未来升级 SDK 或调整安全策略不会扩散到格式映射代码。

## 4. 打开与包预检

`OpenXmlWorkbookReader.Open(filePath)` 按以下顺序执行：

1. 验证扩展名严格为 `.xlsx`。
2. 创建一个只读 `FileStream`，在其上用 `ZipArchive(..., leaveOpen: true)` 完成窄化 ZIP 预检；关闭 ZIP 视图后把流位置复位。
3. 拒绝现有测试定义的重复条目、大小写歧义条目、宏/VBA/二进制工作簿载荷、伪装成通用二进制内容类型的禁止载荷，以及非法内容类型清单结构。
4. 使用 `SpreadsheetDocument.Open(stream, false)` 在同一文件句柄上打开文档，避免预检与正式解析之间出现文件替换竞态。
5. 验证文档为普通 SpreadsheetML 工作簿，并且存在唯一、类型正确的 `WorkbookPart`。
6. 通过 `WorkbookPart` 解析 `Sheets`、`WorksheetPart` 和可选 `SharedStringTablePart`；拒绝缺失、重复、类型不符或越界的关系。

ZIP 预检只保留可被现有安全测试证明有价值的规则。若 SDK 已可靠覆盖某项校验，则删除重复实现；若 SDK 会宽容接受而当前安全契约要求拒绝，则保留最小的清单或条目检查。

打开过程中任何失败都必须释放文件流、ZIP 视图和 `SpreadsheetDocument`。ZIP、包、关系或 XML 错误统一转换为带文件上下文的 `ImportFormatException`；`OperationCanceledException` 不包装。

## 5. SAX 数据读取

### 5.1 共享字符串

共享字符串通过 `OpenXmlReader.Create(SharedStringTablePart)` 顺序扫描，并构建只读索引表，因为工作表单元格会按索引随机引用它们。读取时：

- 只接受 `sst` 的直接 `si` 子元素；
- 拼接同一 `si` 中合法的直接文本与富文本运行；
- 拒绝当前测试覆盖的错误嵌套和非法结构；
- 不保留完整 OpenXML DOM。

`Open` 当前没有取消令牌，因此共享字符串装载维持现有同步契约；不在本迁移中改变 API。后续如需支持打开阶段取消，应单独设计带取消令牌的 `Open` 重载。

### 5.2 工作表与行

`ReadRows(sheet, token)` 使用 `OpenXmlReader.Create(WorksheetPart)` 前向枚举，只在正确的 `worksheet/sheetData/row/c` 层级接受数据。它继续强制：

- 行号严格递增且不重复；
- 行号范围为 1 到 1,048,576；
- 列号范围为 1 到 16,384；
- 单元格引用中的行号与所属行一致；
- 同一行内列号不重复；
- 错误嵌套的 `sheetData`、`row`、`c` 或 `hyperlink` 被拒绝。

每行开始时检查取消令牌；对超长行，每读取最多 256 个单元格再检查一次。枚举提前停止或取消时，迭代器持有的 SDK reader 必须立即释放。

### 5.3 单元格值

保持现有值语义：

- shared string：按索引取值，非法索引报错；
- inline string：拼接合法文本和富文本；
- number/date/text：返回缓存文本，不做地区化格式转换；
- boolean：只接受合法缓存值并归一化为当前结果；
- formula：只读取已有缓存值，没有缓存值时返回空字符串；
- 空单元格：返回空字符串。

SDK 负责 XML token 化和已知元素类型，项目代码仍负责上述严格值约束和错误上下文。

## 6. 关系与超链接策略

关系通过 SDK 的包/部件关系 API 获取，不再自行规范化普通部件路径。所有关系仍按现有策略验证：

- 包根和工作簿部件上的外部关系一律拒绝。
- 工作表上的外部关系只有**精确的标准 hyperlink 关系类型**可以进入例外分支；大小写近似或其他关系类型仍拒绝。
- 该例外用于兼容 ExcelJS 4.4 生成的媒体路径：安全的相对 Target 可作为单元格附件声明返回，即使 ZIP 内没有对应文件。
- URL、根路径、驱动器路径、UNC、父目录越界和其他不安全 Target 被忽略，不发起任何 I/O；单元格文本仍可读取。
- 内部 hyperlink 目标必须解析到包内存在的安全部件，否则拒绝。
- 未被工作表 hyperlink 声明引用的、类型精确匹配的外部 hyperlink 关系允许存在但不返回；未引用的外部非 hyperlink 关系仍拒绝。
- 每张工作表最多接受 10,000 个 hyperlink 声明，工作表关系清单也最多接受 10,000 个 `Relationship` 声明；任一清单的第 10,001 个在保留前拒绝。

SDK 的 `HyperlinkRelationships`/外部关系枚举用于关系元数据，SAX 扫描的 `hyperlink` 元素用于单元格引用绑定。读取器只返回字符串元数据，不下载或打开链接。

## 7. 所有权、错误与取消

- `OpenXmlWorkbookReader` 独占其只读文件句柄、`SpreadsheetDocument` 和 SDK 部件 reader。
- `Dispose` 必须幂等，并释放所有底层资源。
- `ReadRows` 的迭代器拥有本次枚举创建的 SAX reader；正常完成、异常、取消和提前 `break` 均释放它。
- SDK 抛出的 `OpenXmlPackageException`、`InvalidDataException`、XML/格式异常转换为 `ImportFormatException`，保留清晰的中文阶段与单元格/工作表上下文。
- `OperationCanceledException` 原样传播。
- 读取过程不得解析外部实体、访问 URI 或执行任何工作簿内容。

## 8. 测试与迁移顺序

采用特征测试优先的替换方式：

1. 把现有 `OpenXmlWorkbookReaderTests` 作为不可回退的行为契约，先补充仅在 SDK 迁移中暴露的资源所有权或异常归一化缺口。
2. 添加 `DocumentFormat.OpenXml` 3.5.1 包引用。
3. 在保持门面和记录类型不变的前提下替换内部实现。
4. 先运行 OpenXML 读取器测试，再运行 WeFlow/CipherTalk/QQ Excel 与发现测试。
5. 运行 Core 全量测试、应用测试和解决方案构建。
6. 只有全部行为测试通过后，才删除被取代的自研解析帮助代码。

现有由 `XlsxTestFile` 生成的测试包继续使用，因为它们能以可审阅的文本代码精确构造正常与恶意包。迁移不以 SDK 自己写出的“理想工作簿”替代这些边界样例。

允许因 SDK 异常类型不同而调整未作为契约断言的内部消息，但已有测试断言的用户可见错误含义必须保留。任何行为断言的变化都需要单独说明，不能以“SDK 默认行为”为理由放宽安全策略。

## 9. 完成标准

- 三个当前来源的 Excel 导入结果与迁移前一致。
- 现有 OpenXML、Excel、发现和 Core 全量测试通过；新增测试覆盖迁移发现的真实差异。
- 工作簿、工作表和共享字符串不再由项目代码使用 `XmlReader`/`ZipArchive` 自行解析。
- 保留的 ZIP 代码仅执行文档化的安全预检，不形成第二套 OPC 导航层。
- 读取工作表仍是前向流式的，不构造完整 worksheet DOM。
- 外部关系、宏/二进制载荷、恶意结构、路径越界、取消和提前释放策略无回退。
- `OpenXmlWorkbookReader` 公共内部门面不变，三个来源适配器无需修改 SDK 相关代码。

## 10. 权衡与回滚

迁移增加一个第三方 NuGet 依赖及相应发布体积，但换来标准 OPC/OpenXML 解析、官方维护的 SAX API，以及明显更小的自研协议实现。Sylvan.Data.Excel 和 ExcelDataReader 更适合表格行抽象，但本项目还需要精确的部件关系、超链接及恶意包策略；混合使用它们仍需保留大量自研 OPC 代码，因此不采用。MiniExcel 同样会把读取器门面提升到高层表格映射，不符合本项目当前的严格关系控制需求。

迁移保持内部门面不变，并应作为独立实现提交。若出现无法接受的兼容性或发布体积问题，可直接回退该实现提交，不影响三个来源适配器。

## 11. 参考资料

- [Microsoft Learn：使用 SAX 方法读取大型电子表格](https://learn.microsoft.com/office/open-xml/spreadsheet/how-to-parse-and-read-a-large-spreadsheet)
- [Open XML SDK 源代码与文档](https://github.com/dotnet/Open-XML-SDK)
- [NuGet：DocumentFormat.OpenXml 3.5.1](https://www.nuget.org/packages/DocumentFormat.OpenXml/)
- [Sylvan.Data.Excel](https://github.com/MarkPflug/Sylvan.Data.Excel)
- [ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader)
