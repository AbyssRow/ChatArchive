# Open XML SDK SAX Reader Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 Open XML SDK 3.5.1 的包 API 与 SAX `OpenXmlReader` 替换自研 XLSX/OPC 解析核心，同时保持 WeFlow、CipherTalk、QQ Excel 导入结果和现有安全契约不变。

**Architecture:** `XlsxPackagePreflight` 只负责 ZIP 条目、内容类型和禁止载荷的窄化安全预检，并把同一只读流交给 `SpreadsheetDocument`。`OpenXmlWorkbookReader` 保持现有门面，使用 SDK 部件/关系 API 发现工作簿，用 SDK `OpenXmlReader` 前向读取共享字符串、工作表、行、单元格和 hyperlink 声明；三个来源适配器不接触 SDK 类型。

**Tech Stack:** .NET 10、C#、DocumentFormat.OpenXml 3.5.1、System.IO.Compression、xUnit v3、Microsoft.Testing.Platform

**Spec:** `docs/superpowers/specs/2026-08-30-openxml-sdk-reader-migration-design.md`

## Global Constraints

- 固定使用 `DocumentFormat.OpenXml` 3.5.1，不引入第二个 Excel 读取包。
- 只接受扩展名大小写不敏感的 `.xlsx`；不新增 `.xls`、`.xlsb`、`.xlsm`、HTML、通用 Excel 或旧语法。
- 保持 `OpenXmlWorkbookReader.Open(string)`、`Sheets`、`ReadRows(OpenXmlSheet, CancellationToken)` 和三个 record 的调用方式不变。
- WeFlow、CipherTalk、QQ 三个 Excel 适配器不得引用 `DocumentFormat.OpenXml` 类型。
- 工作表必须使用 `DocumentFormat.OpenXml.OpenXmlReader.Create(OpenXmlPart)` 前向读取，不加载完整 worksheet DOM。
- 只允许按单个 `Sheet`、`SharedStringItem` 或 `Cell` 调用 `LoadCurrentElement()`；不得对 `Workbook`、`Worksheet`、`SheetData` 或 `Row` 调用它。
- 公式只读取缓存值；不计算公式、不执行宏、不读取嵌入对象或外部数据连接。
- 不访问网络，不打开任何 hyperlink；不安全外部 hyperlink 只忽略 Target，仍保留单元格文本。
- 包根/工作簿外部关系和工作表外部非 hyperlink 关系拒绝；工作表中类型精确匹配的 hyperlink 关系按设计中的 ExcelJS 规则处理。
- 每张工作表最多 10,000 个 hyperlink 声明和 10,000 个关系声明；第 10,001 个在加入集合前拒绝。
- 现有恶意包、结构、取消和资源释放测试是不可回退的行为契约。
- `OperationCanceledException` 原样传播；SDK、ZIP、XML 和格式错误归一化为带文件/部件上下文的 `ImportFormatException`。

## File Structure

- Create `src/ChatArchive.Core/Importing/XlsxPackagePreflight.cs`: 扩展名、单文件流、ZIP 条目身份、内容类型和禁止载荷预检；不解析工作簿或工作表业务结构。
- Modify `src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs`: 保留门面；持有 SDK 文档和工作表部件；实现工作簿、共享字符串、关系、hyperlink 和行的 SAX 读取。
- Modify `src/ChatArchive.Core/ChatArchive.Core.csproj`: 固定 Open XML SDK 3.5.1 依赖。
- Create `tests/ChatArchive.Core.Tests/XlsxPackagePreflightTests.cs`: 直接验证预检流所有权、内容类型索引和 ZIP 身份安全。
- Modify `tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs`: 增加门面扩展名与 SDK 所有权回归测试；保留现有正常/恶意包样例。
- Modify `docs/superpowers/specs/2026-08-30-openxml-sdk-reader-migration-design.md`: 全量验证后把状态改为“已实现”。
- Modify `docs/superpowers/plans/2026-08-29-inputapp-import-compatibility.md`: 注明原 Task 7 的“不得新增 Excel 包”约束已被批准的新设计取代。

官方接口依据：

- `SpreadsheetDocument.Open(Stream, bool)` 以 `false` 打开只读文档。
- `OpenXmlReader.Create(OpenXmlPart)` 创建部件 SAX reader；使用 `Read()`、`Depth`、`ElementType`、`IsStartElement`、`IsEndElement` 和 `LoadCurrentElement()`。
- `OpenXmlPartContainer.Parts`、`ExternalRelationships`、`HyperlinkRelationships`、`DataPartReferenceRelationships` 分别枚举内部部件、普通外部关系、hyperlink 关系和数据部件关系；普通外部关系集合不包含 hyperlink。

---

### Task 1: Add the SDK dependency and narrow package preflight

**Files:**
- Modify: `src/ChatArchive.Core/ChatArchive.Core.csproj:7-11`
- Create: `src/ChatArchive.Core/Importing/XlsxPackagePreflight.cs`
- Create: `tests/ChatArchive.Core.Tests/XlsxPackagePreflightTests.cs`
- Reuse: `tests/ChatArchive.Core.Tests/XlsxTestFile.cs`

**Interfaces:**
- Produces: `XlsxPackagePreflight.OpenValidated(string filePath) -> XlsxPackageHandle`.
- Produces: `XlsxPackageHandle.Stream -> FileStream`, positioned at `0` after preflight.
- Produces: `XlsxPackageHandle.EntryPaths -> IReadOnlySet<string>` using ordinal package paths.
- Produces: `XlsxPackageHandle.GetContentType(string entryPath) -> string?`.
- Produces: `XlsxPackageHandle.Dispose()` releasing the stream idempotently.
- Consumes later: Task 2 passes `XlsxPackageHandle.Stream` to `SpreadsheetDocument.Open` and uses entry/content-type metadata only for project security policy.

- [ ] **Step 1: Write failing preflight tests**

Create the test class with deterministic disposal and these three cases:

```csharp
using System.IO.Compression;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class XlsxPackagePreflightTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"chatarchive-xlsx-preflight-{Guid.NewGuid():N}");

    public XlsxPackagePreflightTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void OpenValidated_RewindsStreamAndIndexesContentTypes()
    {
        var path = Path.Combine(_directory, "valid.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));

        using var package = XlsxPackagePreflight.OpenValidated(path);

        Assert.Equal(0, package.Stream.Position);
        Assert.Contains("xl/workbook.xml", package.EntryPaths);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            package.GetContentType("xl/workbook.xml"));
    }

    [Fact]
    public void OpenValidated_RejectsNonXlsxBeforeRetainingAFileHandle()
    {
        var path = Path.Combine(_directory, "renamed.xlsm");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));

        var error = Assert.Throws<ImportFormatException>(
            () => XlsxPackagePreflight.OpenValidated(path));

        Assert.Contains(".xlsx", error.Message);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void OpenValidated_RejectsCaseAmbiguousPackageEntries()
    {
        var path = Path.Combine(_directory, "ambiguous.xlsx");
        XlsxTestFile.Write(path, new XlsxTestSheet(
            "聊天记录", [[new XlsxTestCell("A1", "one")]]));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.CreateEntry("XL/workbook.xml");
        }

        var error = Assert.Throws<ImportFormatException>(
            () => XlsxPackagePreflight.OpenValidated(path));

        Assert.Contains("重复或歧义", error.Message);
        Assert.Contains("XL/workbook.xml", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the new tests and verify RED**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "FullyQualifiedName~XlsxPackagePreflightTests"
```

Expected: build fails because `XlsxPackagePreflight` does not exist.

- [ ] **Step 3: Pin DocumentFormat.OpenXml 3.5.1**

Add beside `Microsoft.Data.Sqlite`:

```xml
<PackageReference Include="DocumentFormat.OpenXml" Version="3.5.1" />
```

Run:

```powershell
dotnet restore ChatArchive.sln
dotnet list src/ChatArchive.Core/ChatArchive.Core.csproj package --include-transitive
```

Expected: restore succeeds and the top-level list contains exactly `DocumentFormat.OpenXml 3.5.1`.

- [ ] **Step 4: Implement the preflight ownership boundary**

Use these exact public-internal shapes:

```csharp
internal sealed class XlsxPackageHandle : IDisposable
{
    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly IReadOnlyDictionary<string, string> _defaults;
    private bool _disposed;

    internal XlsxPackageHandle(
        FileStream stream,
        IReadOnlySet<string> entryPaths,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> defaults)
    {
        Stream = stream;
        EntryPaths = entryPaths;
        _overrides = overrides;
        _defaults = defaults;
    }

    internal FileStream Stream { get; }
    internal IReadOnlySet<string> EntryPaths { get; }

    internal string? GetContentType(string entryPath)
    {
        if (_overrides.TryGetValue(entryPath, out var contentType))
        {
            return contentType;
        }

        var separator = entryPath.LastIndexOf('/');
        var dot = entryPath.LastIndexOf('.');
        if (dot <= separator || dot == entryPath.Length - 1)
        {
            return null;
        }

        return _defaults.TryGetValue(entryPath[(dot + 1)..], out contentType)
            ? contentType
            : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stream.Dispose();
    }
}

internal static class XlsxPackagePreflight
{
    internal static XlsxPackageHandle OpenValidated(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(filePath, "XLSX 导入只接受 .xlsx 文件");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            IReadOnlySet<string> entries;
            PackageContentTypes contentTypes;
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                entries = IndexEntries(archive, filePath);
                contentTypes = ReadContentTypes(archive, entries, filePath);
            }

            stream.Position = 0;
            var result = new XlsxPackageHandle(
                stream, entries, contentTypes.Overrides, contentTypes.Defaults);
            stream = null;
            return result;
        }
        catch (ImportFormatException)
        {
            stream?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Xml.XmlException
            or ArgumentException
            or NotSupportedException)
        {
            stream?.Dispose();
            throw new ImportFormatException(filePath, $"XLSX 包预检失败（{ex.Message}）");
        }
    }
}
```

Implement `GetContentType` with override-first lookup and then extension default lookup, preserving case-insensitive content-type keys.

- [ ] **Step 5: Port ZIP entry identity validation**

Copy `IndexEntries` and `ValidateEntryPath` from `OpenXmlWorkbookReader` into the preflight file. Retain ordinal entry paths plus a separate case-insensitive identity set, reject duplicate/case-ambiguous names before reading XML, and reject absolute, rooted, URI, backslash, empty, `.` and `..` segments. Keep the original methods until Task 2 switches the façade.

- [ ] **Step 6: Port content-type and forbidden-payload validation**

Copy the remaining test-backed package policy from `OpenXmlWorkbookReader` without changing accepted/rejected inputs:

- `[Content_Types].xml` root/direct-child validation with DTD prohibited and `XmlResolver = null`;
- duplicate `Default`/`Override` rejection and `PartName` normalization checks;
- `RejectForbiddenContentType`;
- `IsForbiddenPayloadPath`, including VBA, ActiveX and `xl/embeddings` entries.

Do not move workbook, worksheet, shared-string, cell or relationship navigation into this file. Retain the original methods until Task 2 switches the façade, then remove the duplicates.

- [ ] **Step 7: Run preflight and existing package-security tests**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "FullyQualifiedName~XlsxPackagePreflightTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_RejectsDuplicatePackageEntries|OpenXmlReader_RejectsForbidden|OpenXmlReader_RejectsMalformedContentTypesManifest"
```

Expected: all selected tests pass; the legacy reader still supplies the second command's behavior until Task 2 wires the preflight into the façade.

- [ ] **Step 8: Commit the dependency and preflight**

```powershell
git add src/ChatArchive.Core/ChatArchive.Core.csproj src/ChatArchive.Core/Importing/XlsxPackagePreflight.cs tests/ChatArchive.Core.Tests/XlsxPackagePreflightTests.cs
git commit -m "refactor(import): add SDK-ready XLSX package preflight"
```

### Task 2: Move package ownership, workbook metadata, relationships, and shared strings to the SDK

**Files:**
- Modify: `src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs:1-1425`
- Modify: `tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs:1-990`
- Consume: `src/ChatArchive.Core/Importing/XlsxPackagePreflight.cs`

**Interfaces:**
- Consumes: `XlsxPackagePreflight.OpenValidated(string) -> XlsxPackageHandle`.
- Consumes: `SpreadsheetDocument.Open(Stream stream, bool isEditable)` with `isEditable: false`.
- Consumes: `OpenXmlReader.Create(OpenXmlPart)` for workbook and shared-string SAX reads.
- Produces unchanged: `OpenXmlWorkbookReader.Open`, `Sheets`, `ReadRows`, `Dispose`.
- Produces internal mapping: `IReadOnlyDictionary<OpenXmlSheet, WorksheetPart>`.
- Produces relationship snapshot: `SdkRelationship(string Id, string Type, Uri Target, bool IsExternal, OpenXmlPart? Part)`.

- [ ] **Step 1: Add a failing façade extension test**

Append to `OpenXmlWorkbookReaderTests`:

```csharp
[Fact]
public void OpenXmlReader_RejectsNonXlsxExtensionThroughFacade()
{
    var path = NewPath("renamed.xlsm");
    XlsxTestFile.Write(path, new XlsxTestSheet(
        "聊天记录", [[new XlsxTestCell("A1", "one")]]));
    OpenXmlWorkbookReader? opened = null;

    try
    {
        var error = Assert.Throws<ImportFormatException>(
            () => opened = OpenXmlWorkbookReader.Open(path));
        Assert.Contains(".xlsx", error.Message);
    }
    finally
    {
        opened?.Dispose();
    }
}
```

- [ ] **Step 2: Run the façade test and verify RED**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_RejectsNonXlsxExtensionThroughFacade"
```

Expected: FAIL because the current façade opens a valid ZIP regardless of extension.

- [ ] **Step 3: Replace reader ownership with one package handle and one SDK document**

Add the SDK namespaces and alias to prevent name confusion:

```csharp
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SdkOpenXmlReader = DocumentFormat.OpenXml.OpenXmlReader;
```

Replace the archive/entry fields with:

```csharp
private readonly XlsxPackageHandle _package;
private readonly SpreadsheetDocument _document;
private readonly IReadOnlyDictionary<OpenXmlSheet, WorksheetPart> _worksheetParts;
private readonly IReadOnlyList<string> _sharedStrings;
private bool _disposed;
```

Use this ownership pattern in `Open`:

```csharp
XlsxPackageHandle? package = null;
SpreadsheetDocument? document = null;
try
{
    package = XlsxPackagePreflight.OpenValidated(filePath);
    document = SpreadsheetDocument.Open(package.Stream, isEditable: false);
    var workbookPart = RequireWorkbookPart(document, package, filePath);
    ValidateContainerRelationships(document, filePath, "_rels/.rels", allowExternalHyperlinks: false);
    ValidateContainerRelationships(workbookPart, filePath, "xl/_rels/workbook.xml.rels", allowExternalHyperlinks: false);
    var workbookMap = ReadSheets(workbookPart, package, filePath);
    var sharedStrings = ReadSharedStrings(workbookPart, package, filePath);

    var result = new OpenXmlWorkbookReader(
        filePath, package, document, workbookMap.Sheets,
        workbookMap.Parts, sharedStrings);
    package = null;
    document = null;
    return result;
}
catch (ImportFormatException)
{
    document?.Dispose();
    package?.Dispose();
    throw;
}
catch (Exception ex) when (IsPackageFailure(ex))
{
    document?.Dispose();
    package?.Dispose();
    throw new ImportFormatException(filePath, $"XLSX 包读取失败（{ex.Message}）");
}
```

`Dispose` must set `_disposed`, dispose `_document` first, then `_package`; both disposals are idempotent.

- [ ] **Step 4: Discover the exact workbook and sheets with SDK APIs**

`RequireWorkbookPart` must enforce:

- exactly one `WorkbookPart` at package root;
- URI exactly `/xl/workbook.xml`;
- content type exactly `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml` both on the SDK part and in `XlsxPackageHandle`;
- no external relationship at package root or workbook level;
- forbidden relation types/targets continue to report their relationship ID and relationship entry context.

Define the sheet map:

```csharp
private sealed record WorkbookMap(
    IReadOnlyList<OpenXmlSheet> Sheets,
    IReadOnlyDictionary<OpenXmlSheet, WorksheetPart> Parts);

private static WorkbookMap ReadSheets(
    WorkbookPart workbookPart,
    XlsxPackageHandle package,
    string filePath)
```

Inside `ReadSheets`, use `SdkOpenXmlReader.Create(workbookPart)` and enforce `Workbook` at depth `0`, one `Sheets` at depth `1`, and each `Sheet` at depth `2`. Load only the current `Sheet` element:

```csharp
var sheetElement = (Sheet)reader.LoadCurrentElement();
var name = sheetElement.Name?.Value;
var relationshipId = sheetElement.Id?.Value;
if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relationshipId))
{
    throw Error(filePath, WorkbookEntry, "工作表缺少名称或关系 ID");
}

var relatedPart = workbookPart.GetPartById(relationshipId);
if (relatedPart is not WorksheetPart worksheetPart)
{
    throw Error(filePath, WorkbookEntry, $"关系 {relationshipId} 不是工作表关系");
}

var entryPath = worksheetPart.Uri.OriginalString.TrimStart('/');
RequireContentType(package, entryPath, WorksheetContentType, filePath);
var sheet = new OpenXmlSheet(name, entryPath);
```

Reject blank names/IDs, duplicate relationship IDs, repeated sheet declarations, missing parts and non-worksheet targets. Store each `OpenXmlSheet` and `WorksheetPart` in the same insertion order as the workbook.

- [ ] **Step 5: Read shared strings with SDK SAX**

Use exactly one optional `SharedStringTablePart`, validate URI/content type through the preflight metadata, and implement:

```csharp
private static IReadOnlyList<string> ReadSharedStrings(
    WorkbookPart workbookPart,
    XlsxPackageHandle package,
    string filePath)
```

Scan with `SdkOpenXmlReader.Create(sharedStringPart)`. Require `SharedStringTable` at depth `0`; accept `SharedStringItem` only at depth `1`. Load one item at a time:

```csharp
var item = (SharedStringItem)reader.LoadCurrentElement();
if (item.Descendants<SharedStringItem>().Any())
{
    throw Error(filePath, entryPath, "si 不能嵌套，si 必须是 sst 的直接子元素");
}

values.Add(string.Concat(
    item.Descendants<Text>().Select(text => text.Text ?? string.Empty)));
```

Text nodes outside a direct `SharedStringItem` remain ignored. Do not access `sharedStringPart.SharedStringTable`, which would materialize the full DOM.

- [ ] **Step 6: Replace raw relationship parsing with SDK relationship collections**

Keep relationship policy in `OpenXmlWorkbookReader` and define:

```csharp
private sealed record SdkRelationship(
    string Id,
    string Type,
    Uri Target,
    bool IsExternal,
    OpenXmlPart? Part);

private static IReadOnlyDictionary<string, SdkRelationship> SnapshotRelationships(
    OpenXmlPartContainer owner,
    string filePath,
    string relationshipEntry,
    CancellationToken token,
    int? maximumRelationships,
    bool allowExternalHyperlinks)
```

Populate one ordinal-ID dictionary from:

- `owner.Parts`, using `IdPartPair.RelationshipId`, `OpenXmlPart.RelationshipType`, `OpenXmlPart.Uri`, `IsExternal = false`, and the target part;
- `owner.ExternalRelationships`, using its `Id`, `RelationshipType`, and `Uri`;
- `owner.HyperlinkRelationships`, using the exact hyperlink relationship type constant, `Id`, `Uri`, and `IsExternal`.
- `owner.DataPartReferenceRelationships`, using `Id`, `RelationshipType`, `Uri`, `IsExternal`, and `Part = null`.

Before each insertion, enforce the optional 10,000 relationship limit and reject duplicate IDs. Reject every ordinary external relationship. An external hyperlink is retained only when `allowExternalHyperlinks` is true for a worksheet snapshot; package-root and workbook external hyperlinks remain errors. Internal hyperlink relationships remain valid in any container subject to safe-target checks. Apply `IsForbiddenRelationshipType` to SDK relationship types and `IsForbiddenPayloadPath` to internal target URIs.

Use one insertion path so the count, cancellation and security checks cannot diverge:

```csharp
var result = new Dictionary<string, SdkRelationship>(StringComparer.Ordinal);

void Add(SdkRelationship relationship, bool isHyperlink)
{
    token.ThrowIfCancellationRequested();
    if (maximumRelationships is int maximum && result.Count == maximum)
    {
        throw Error(
            filePath,
            relationshipEntry,
            $"每个工作表最多允许 {maximum} 个 Relationship 声明");
    }

    if (relationship.IsExternal && (!isHyperlink || !allowExternalHyperlinks))
    {
        throw Error(filePath, relationshipEntry, $"关系 {relationship.Id} 是外部关系");
    }

    if (IsForbiddenRelationshipType(relationship.Type))
    {
        throw Error(
            filePath,
            relationshipEntry,
            $"关系 {relationship.Id} 声明了禁止的宏或二进制类型：{relationship.Type}");
    }

    var internalTarget = relationship.IsExternal
        ? null
        : relationship.Part?.Uri ?? ResolveInternalTargetUri(owner, relationship.Target);
    if (internalTarget is not null
        && IsForbiddenPayloadPath(internalTarget.OriginalString.TrimStart('/')))
    {
        throw Error(
            filePath,
            relationshipEntry,
            $"关系 {relationship.Id} 指向禁止的宏或二进制负载：{relationship.Target}");
    }

    if (!result.TryAdd(relationship.Id, relationship))
    {
        throw Error(filePath, relationshipEntry, $"关系 ID 重复：{relationship.Id}");
    }
}

foreach (var pair in owner.Parts)
{
    Add(new SdkRelationship(
        pair.RelationshipId,
        pair.OpenXmlPart.RelationshipType,
        pair.OpenXmlPart.Uri,
        IsExternal: false,
        Part: pair.OpenXmlPart), isHyperlink: false);
}

foreach (var relationship in owner.ExternalRelationships)
{
    Add(new SdkRelationship(
        relationship.Id,
        relationship.RelationshipType,
        relationship.Uri,
        IsExternal: true,
        Part: null), isHyperlink: false);
}

foreach (var relationship in owner.HyperlinkRelationships)
{
    Add(new SdkRelationship(
        relationship.Id,
        HyperlinkRelationship,
        relationship.Uri,
        relationship.IsExternal,
        Part: null), isHyperlink: true);
}

foreach (var relationship in owner.DataPartReferenceRelationships)
{
    Add(new SdkRelationship(
        relationship.Id,
        relationship.RelationshipType,
        relationship.Uri,
        relationship.IsExternal,
        Part: null), isHyperlink: false);
}

return result;
```

Normalize internal reference targets with the framework's OPC URI helper, not a custom segment stack:

```csharp
private static Uri ResolveInternalTargetUri(OpenXmlPartContainer owner, Uri target)
{
    if (target.OriginalString.StartsWith("/", StringComparison.Ordinal))
    {
        return target;
    }

    var source = owner is OpenXmlPart ownerPart
        ? ownerPart.Uri
        : new Uri("/", UriKind.Relative);
    return System.IO.Packaging.PackUriHelper.ResolvePartUri(source, target);
}
```

Root/workbook validation uses the same snapshot code with cancellation disabled and no relationship-count cap:

```csharp
private static void ValidateContainerRelationships(
    OpenXmlPartContainer owner,
    string filePath,
    string relationshipEntry,
    bool allowExternalHyperlinks)
{
    _ = SnapshotRelationships(
        owner,
        filePath,
        relationshipEntry,
        CancellationToken.None,
        maximumRelationships: null,
        allowExternalHyperlinks: allowExternalHyperlinks);
}
```

- [ ] **Step 7: Apply the worksheet hyperlink policy and normalize the count fixture**

For worksheet hyperlinks, keep the current `XmlReader` scan of worksheet `hyperlink` declarations temporarily, but obtain Targets from the SDK snapshot. Resolve an internal hyperlink URI with `System.IO.Packaging.PackUriHelper.ResolvePartUri(worksheetPart.Uri, relationship.Target)` and require its trimmed path in `XlsxPackageHandle.EntryPaths`. For an external hyperlink, return `relationship.Target.OriginalString` only when `IsSafeExternalHyperlinkTarget` accepts it. Exact unreferenced hyperlink relationships are retained then discarded; unreferenced ordinary external relationships fail during snapshot creation.

Make the existing relationship-count fixture OPC-valid without changing its assertion. At the end of `WriteWorkbookWithWorksheetRelationships`, create the one shared target and declare its content type:

```csharp
AddTextEntry(filePath, "unused.bin", "unused");
RewriteEntry(filePath, "[Content_Types].xml", xml => InsertBeforeRequired(
    xml,
    "</Types>",
    "<Override PartName=\"/unused.bin\" ContentType=\"application/octet-stream\" />"));
```

The test remains solely about accepting exactly 10,000 declarations and rejecting the 10,001st; it must not rely on a dangling internal target that the SDK correctly treats as an invalid package part.

- [ ] **Step 8: Adapt the existing row parser to WorksheetPart streams**

Keep the current `XmlReader` row/cell code for this intermediate commit, but replace ZIP entry access with:

```csharp
var worksheetPart = _worksheetParts[sheet];
using var stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read);
using var reader = CreateXmlReader(stream);
```

Use the same part lookup in the temporary hyperlink declaration scan. Delete raw OPC navigation methods that no longer have callers: `ReadRelationships*`, `ResolveRelationship`, `ResolvePackageTarget`, `RelationshipEntryFor`, `RequireEntry`, and the old package relationship record. Keep cell parsing, `CreateXmlReader`, `ParseCellReference`, value resolution and error helpers until Task 3.

Add `OpenXmlPackageException` to `IsPackageFailure` so SDK parse failures retain `ImportFormatException` context.

- [ ] **Step 9: Run workbook, relationship, shared-string, and ownership tests**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_RejectsNonXlsxExtensionThroughFacade|OpenXmlReader_ReadsSparseCellsDateTextRichTextAndEmptyFormulaCache|OpenXmlReader_RejectsSharedString|OpenXmlReader_RejectsExternal|OpenXmlReader_IgnoresUnreferencedExternalHyperlink|OpenXmlReader_EarlyIteratorDisposalReleasesWorkbookFile"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "FullyQualifiedName~OpenXmlWorkbookReaderTests|FullyQualifiedName~XlsxPackagePreflightTests"
```

Expected: all selected tests pass, including nested structure, exact relationship-type, 10,000-boundary and file-release cases.

- [ ] **Step 10: Commit SDK package and metadata cutover**

```powershell
git add src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs
git commit -m "refactor(import): open XLSX packages with Open XML SDK"
```

### Task 3: Replace worksheet and cell XML parsing with SDK SAX

**Files:**
- Modify: `src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs`
- Verify: `tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs`

**Interfaces:**
- Consumes: `IReadOnlyDictionary<OpenXmlSheet, WorksheetPart>` from Task 2.
- Consumes: `SdkOpenXmlReader.Create(WorksheetPart)`.
- Produces unchanged: lazy `IEnumerable<OpenXmlRow> ReadRows(OpenXmlSheet, CancellationToken)`.
- Preserves: `OpenXmlCell(int ColumnIndex, string Reference, string Value, string? Hyperlink)`.

- [ ] **Step 1: Run the complete characterization suite before refactoring**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "FullyQualifiedName~OpenXmlWorkbookReaderTests"
```

Expected: PASS. Record the executed/passed count in the task notes before editing. This is a behavior-preserving refactor; the existing suite is the red/green boundary for every subsequent edit.

- [ ] **Step 2: Convert hyperlink declaration scanning to SDK SAX**

Replace the temporary worksheet `XmlReader` pass with:

```csharp
private IReadOnlyDictionary<string, string> ReadHyperlinks(
    OpenXmlSheet sheet,
    WorksheetPart worksheetPart,
    CancellationToken token)
```

Use `SdkOpenXmlReader.Create(worksheetPart)` and track `Worksheet` depth `0`, the unique `Hyperlinks` container at depth `1`, and each `Hyperlink` at depth `2`. Load only the current `Hyperlink` element and read `Reference?.Value` and `Id?.Value`. Preserve cell-reference normalization, duplicate reference rejection, the 10,000 declaration limit, cancellation per declaration, and the SDK relationship snapshot mapping from Task 2.

Any `SheetData` not at depth `1`, repeated `Hyperlinks`, or `Hyperlink` outside its direct container must raise the same contextual `ImportFormatException` meaning as the current tests assert.

- [ ] **Step 3: Convert the worksheet/row loop to SDK SAX**

Use this shape; the iterator owns and disposes the SDK reader:

```csharp
private IEnumerable<OpenXmlRow> ReadRowsCore(OpenXmlSheet sheet, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    var worksheetPart = _worksheetParts[sheet];
    var hyperlinks = ReadHyperlinks(sheet, worksheetPart, token);
    using var reader = SdkOpenXmlReader.Create(worksheetPart);

    var sawWorksheet = false;
    var sawSheetData = false;
    var insideSheetData = false;
    uint previousRowIndex = 0;

    while (reader.Read())
    {
        if (reader.IsStartElement && reader.Depth == 0)
        {
            if (reader.ElementType != typeof(Worksheet))
            {
                throw Error(_filePath, sheet.EntryPath, "worksheet 根元素无效");
            }

            sawWorksheet = true;
            continue;
        }

        if (reader.IsStartElement && reader.ElementType == typeof(SheetData))
        {
            if (reader.Depth != 1 || sawSheetData)
            {
                throw Error(
                    _filePath,
                    sheet.EntryPath,
                    "sheetData 必须是 worksheet 的唯一直接子元素");
            }

            sawSheetData = true;
            insideSheetData = true;
            continue;
        }

        if (reader.IsEndElement
            && reader.ElementType == typeof(SheetData)
            && reader.Depth == 1)
        {
            insideSheetData = false;
            continue;
        }

        if (reader.IsStartElement && reader.ElementType == typeof(Cell))
        {
            throw Error(_filePath, sheet.EntryPath, "c 必须是 row 的直接子元素");
        }

        if (!reader.IsStartElement || reader.ElementType != typeof(Row))
        {
            continue;
        }

        if (!insideSheetData || reader.Depth != 2)
        {
            throw Error(_filePath, sheet.EntryPath, "row 必须是 sheetData 的直接子元素");
        }

        token.ThrowIfCancellationRequested();
        var rowText = reader.Attributes
            .FirstOrDefault(attribute => attribute.LocalName == "r" && attribute.NamespaceUri.Length == 0)
            .Value;
        if (!uint.TryParse(
                rowText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rowIndex)
            || rowIndex is 0 or > MaximumRowIndex)
        {
            throw Error(_filePath, sheet.EntryPath, $"行号无效：{rowText ?? "<缺失>"}");
        }

        if (rowIndex <= previousRowIndex)
        {
            throw Error(
                _filePath,
                sheet.EntryPath,
                $"行 {rowIndex} 必须严格递增（上一行 {previousRowIndex}）");
        }

        previousRowIndex = rowIndex;
        yield return ReadRow(reader, sheet.EntryPath, rowIndex, hyperlinks, token);
    }

    if (!sawWorksheet)
    {
        throw Error(_filePath, sheet.EntryPath, "缺少 worksheet 根元素");
    }
}
```

Implement the loop concretely with `reader.IsStartElement`, `reader.IsEndElement`, `reader.ElementType`, and `reader.Depth`. A `Row` is valid only at depth `2` while inside the unique direct `SheetData`. Parse `r` with `NumberStyles.None`, enforce 1..1,048,576 and strict increase, check cancellation before each row, then call:

```csharp
private OpenXmlRow ReadRow(
    SdkOpenXmlReader reader,
    string entryPath,
    uint rowIndex,
    IReadOnlyDictionary<string, string> hyperlinks,
    CancellationToken token)
{
    var rowDepth = reader.Depth;
    var cells = new Dictionary<int, OpenXmlCell>();
    while (reader.Read())
    {
        if (reader.IsEndElement
            && reader.ElementType == typeof(Row)
            && reader.Depth == rowDepth)
        {
            return new OpenXmlRow(rowIndex, cells);
        }

        if (reader.IsStartElement && reader.ElementType == typeof(Row))
        {
            throw Error(
                _filePath,
                entryPath,
                $"行 {rowIndex} 中 row 不能嵌套，row 必须是 sheetData 的直接子元素");
        }

        if (!reader.IsStartElement || reader.ElementType != typeof(Cell))
        {
            continue;
        }

        if (reader.Depth != rowDepth + 1)
        {
            throw Error(_filePath, entryPath, $"行 {rowIndex} 中 c 必须是 row 的直接子元素");
        }

        token.ThrowIfCancellationRequested();
        var element = (Cell)reader.LoadCurrentElement();
        var cell = ReadCell(element, entryPath, rowIndex, hyperlinks);
        if (!cells.TryAdd(cell.ColumnIndex, cell))
        {
            throw CellError(
                _filePath,
                entryPath,
                cell.Reference,
                $"第 {cell.ColumnIndex} 列重复");
        }
    }

    throw Error(_filePath, entryPath, $"行 {rowIndex} 未正常结束");
}
```

`ReadRow` advances until the matching row end. It rejects nested `Row`, rejects every `Cell` not exactly one depth below the row, checks cancellation before every cell (which is stronger than the required once per 256 cells), and adds cells by column with duplicate-column rejection.

- [ ] **Step 4: Load and interpret one Cell at a time**

Replace `ReadCell(XmlReader, ...)` with:

```csharp
private OpenXmlCell ReadCell(
    Cell cell,
    string entryPath,
    uint rowIndex,
    IReadOnlyDictionary<string, string> hyperlinks)
{
    var reference = cell.GetAttribute("r", string.Empty).Value ?? string.Empty;
    var type = cell.GetAttribute("t", string.Empty).Value;
    if (cell.Descendants<Cell>().Any())
    {
        throw CellError(_filePath, entryPath, reference, "c 不能嵌套");
    }

    var (columnIndex, referencedRow, normalizedReference) =
        ParseCellReference(reference, entryPath);
    if (referencedRow != rowIndex)
    {
        throw CellError(
            _filePath,
            entryPath,
            reference,
            $"引用行 {referencedRow} 与所在行 {rowIndex} 不一致");
    }

    var cachedValues = cell.Descendants<CellValue>().ToArray();
    if (cachedValues.Length > 1)
    {
        throw CellError(_filePath, entryPath, reference, "包含重复缓存值");
    }

    var hasFormula = cell.Descendants<CellFormula>().Any();
    var hasCachedValue = cachedValues.Length == 1;
    var cachedValue = hasCachedValue ? cachedValues[0].Text ?? string.Empty : string.Empty;
    var inlineText = type == "inlineStr"
        ? string.Concat(cell.Descendants<Text>().Select(text => text.Text ?? string.Empty))
        : string.Empty;
    var value = hasFormula && !hasCachedValue
        ? string.Empty
        : ResolveCellValue(
            type,
            hasCachedValue,
            cachedValue,
            inlineText,
            entryPath,
            reference);

    hyperlinks.TryGetValue(normalizedReference, out var hyperlink);
    return new OpenXmlCell(columnIndex, reference, value, hyperlink);
}
```

At the direct `Cell` start, call `(Cell)reader.LoadCurrentElement()` and pass it to `ReadCell`. This preserves `ParseCellReference`, row agreement, Excel bounds, shared-string index validation, boolean normalization, unknown-type errors, cached formula values, formula-without-cache as empty, sparse columns, normalized hyperlink lookup and original reference in the returned record.

- [ ] **Step 5: Remove worksheet XmlReader code**

Delete `CreateXmlReader`, `ReadSubtree` paths and `System.Xml` reader usage from `OpenXmlWorkbookReader.cs`. `System.Xml` remains allowed only in `XlsxPackagePreflight.cs` for the content-type manifest security check.

Confirm source boundaries:

```powershell
rg -n "ZipArchive|ZipArchiveEntry|XmlReader|ReadSubtree|ResolvePackageTarget|RelationshipEntryFor" src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs
rg -n "SpreadsheetDocument|SdkOpenXmlReader|WorksheetPart|HyperlinkRelationships" src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs
```

Expected: the first command has no matches; the second shows SDK ownership and SAX usage.

- [ ] **Step 6: Run structure, value, cancellation, and iterator tests**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_ReadsCellKinds|OpenXmlReader_ReadsSparseCells|OpenXmlReader_RejectsWorksheet|OpenXmlReader_RejectsSheetData|OpenXmlReader_RejectsRow|OpenXmlReader_RejectsCell|OpenXmlReader_ObservesCancellation|OpenXmlReader_EarlyIteratorDisposal"
```

Expected: all selected tests pass. A failure on malformed nesting is a migration defect; do not weaken or delete the characterization test.

- [ ] **Step 7: Run the complete reader suite**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "FullyQualifiedName~OpenXmlWorkbookReaderTests|FullyQualifiedName~XlsxPackagePreflightTests"
```

Expected: PASS with zero skipped safety cases.

- [ ] **Step 8: Commit the SAX worksheet cutover**

```powershell
git add src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs
git commit -m "refactor(import): stream XLSX rows with Open XML SDK"
```

### Task 4: Remove obsolete parser code and verify all three importers

**Files:**
- Modify: `src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs`
- Modify: `src/ChatArchive.Core/Importing/XlsxPackagePreflight.cs`
- Modify: `tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs`
- Verify unchanged: `src/ChatArchive.Core/Importing/WeFlowExcelParser.cs`
- Verify unchanged: `src/ChatArchive.Core/Importing/CipherTalkExcelParser.cs`
- Verify unchanged: `src/ChatArchive.Core/Importing/QqExcelParser.cs`
- Modify: `docs/superpowers/specs/2026-08-30-openxml-sdk-reader-migration-design.md`
- Modify: `docs/superpowers/plans/2026-08-29-inputapp-import-compatibility.md`

**Interfaces:**
- Consumes: final SDK-backed `OpenXmlWorkbookReader` from Tasks 2–3.
- Produces: no API changes; this task proves all three current writer formats still consume the same records.
- Produces: documentation that resolves the old “no Excel package” constraint.

- [ ] **Step 1: Add a failed-open file-release regression**

Append to `OpenXmlWorkbookReaderTests`:

```csharp
[Fact]
public void OpenXmlReader_FailedSdkOpenReleasesWorkbookFile()
{
    var path = NewPath("failed-open-release.xlsx");
    XlsxTestFile.Write(path, new XlsxTestSheet(
        "聊天记录", [[new XlsxTestCell("A1", "one")]]));
    RewriteEntry(path, "xl/workbook.xml", _ => "<broken>");

    Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));

    using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Assert.True(exclusive.CanWrite);
}
```

- [ ] **Step 2: Run ownership tests before cleanup**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_FailedSdkOpenReleasesWorkbookFile|OpenXmlReader_EarlyIteratorDisposalReleasesWorkbookFile|OpenValidated_RejectsNonXlsxBeforeRetainingAFileHandle"
```

Expected: PASS. If the new failed-open case exposes a leak, fix the `Open` catch paths by disposing `SpreadsheetDocument` before `XlsxPackageHandle`, then rerun this exact command.

- [ ] **Step 3: Delete obsolete helpers and audit the dependency boundary**

Delete uncalled constants/types/methods left from the custom parser, including raw OPC relationship namespaces, raw ZIP entry lookup, package-target stack normalization, and XML subtree readers. Keep only:

- content-type/forbidden-payload XML preflight in `XlsxPackagePreflight`;
- cell-reference and safe external-target validation in `OpenXmlWorkbookReader`;
- SDK package, part, relationship and SAX element handling in `OpenXmlWorkbookReader`.

Run:

```powershell
rg -n "DocumentFormat\.OpenXml" src/ChatArchive.Core/Importing -g '*.cs'
rg -n "ZipArchive|XmlReader" src/ChatArchive.Core/Importing -g '*.cs'
dotnet build src/ChatArchive.Core/ChatArchive.Core.csproj -c Release
```

Expected: SDK references occur only in `OpenXmlWorkbookReader.cs`; ZIP/XML parser references for XLSX occur only in `XlsxPackagePreflight.cs`; Core builds with warnings treated as errors.

- [ ] **Step 4: Run all Excel adapters and discovery tests**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowExcel_|CipherTalkExcel_|QqExcel_|FullyQualifiedName~ImportDiscoveryTests"
```

Expected: PASS for all current WeFlow layouts, CipherTalk optional columns, QQ optional group-title/resource sheet cases, strict media policy and format discovery.

- [ ] **Step 5: Run full project verification**

Run in order:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj -c Release
dotnet test tests/ChatArchive.App.Tests/ChatArchive.App.Tests.csproj -c Release
dotnet build ChatArchive.sln -c Release --no-restore
git diff --check
git status --short
```

Expected: both test projects pass, the Release solution build succeeds, `git diff --check` reports no whitespace errors, and status lists only this task's intended files.

- [ ] **Step 6: Update the approved design and original compatibility plan**

In the migration design, change:

```markdown
**状态：** 待用户审阅
```

to:

```markdown
**状态：** 已实现并验证
```

Near the top of `2026-08-29-inputapp-import-compatibility.md`, add:

```markdown
> **2026-08-30 XLSX reader update:** Task 7 的自研解析器和“不得新增 Excel 包”约束已由 `docs/superpowers/specs/2026-08-30-openxml-sdk-reader-migration-design.md` 取代；格式与安全验收条件不变。
```

- [ ] **Step 7: Commit cleanup and verification metadata**

```powershell
git add src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs src/ChatArchive.Core/Importing/XlsxPackagePreflight.cs tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs docs/superpowers/specs/2026-08-30-openxml-sdk-reader-migration-design.md docs/superpowers/plans/2026-08-29-inputapp-import-compatibility.md
git commit -m "test(import): verify SDK-backed Excel imports"
```

- [ ] **Step 8: Confirm the migration branch is ready to resume Task 10**

Run:

```powershell
git status --short
git log --oneline -5
```

Expected: worktree is clean and the four migration commits are present. Return to Task 10 of `docs/superpowers/plans/2026-08-29-inputapp-import-compatibility.md`; do not broaden that task to additional import formats.

## Reference Documentation

- Microsoft Learn: `SpreadsheetDocument.Open(Stream, Boolean)` and read-only mode: <https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-open-a-spreadsheet-document-for-read-only-access>
- Microsoft Learn: SAX reading for large spreadsheets: <https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-parse-and-read-a-large-spreadsheet>
- Microsoft Learn: `OpenXmlReader` API: <https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.openxmlreader>
- Microsoft Learn: `OpenXmlPartContainer` relationship collections: <https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.packaging.openxmlpartcontainer>
- NuGet: DocumentFormat.OpenXml 3.5.1: <https://www.nuget.org/packages/DocumentFormat.OpenXml/3.5.1>
