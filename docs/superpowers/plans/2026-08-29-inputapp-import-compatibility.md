# Inputapp Import Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make ChatArchive import exactly the current non-HTML exports produced by the three `inputapp` source snapshots, with strict detection, safe media resolution, Excel support, and source-derived regression coverage.

**Architecture:** Keep the existing `IChatExportFormat -> ExportFile -> ParsedMessage` pipeline. Replace generic text/SQL matching with source-specific adapters, share only low-level CSV/SQL/OpenXML readers, remove HTML from discovery, and use explicit degradation rules when an upstream format omits identity or direction.

**Tech Stack:** .NET 10, C# 14, `System.Text.Json`, `System.IO.Compression`, `System.Xml`, `Microsoft.Data.Sqlite`, xUnit v3.

**Spec:** `docs/superpowers/specs/2026-08-29-inputapp-import-compatibility-design.md`

## Global Constraints

- Format truth comes only from WeFlow `6f8e7e89f9b1`, CipherTalk `6b886e682472`, QQ Chat Exporter `888b51fab652`, and `docs/EXPORT_FORMATS_SPEC.md`.
- HTML is not an import format: remove `.html`/`.htm` discovery and all HTML adapters/parsers.
- Do not retain generic CSV, Markdown, TXT, or SQL syntaxes without a matching current `inputapp` writer.
- Do not add an Excel package or runtime; parse `.xlsx` with `System.IO.Compression` and streaming XML.
- Never execute SQL, formulas, macros, external workbook relationships, HTML, or JavaScript.
- WeFlow and CipherTalk formats produce platform `wechat`; QQ formats produce platform `qq`.
- Preserve streaming enumeration and cancellation checks for message rows.
- Permit only the explicit, existing WeFlow layout-A parent-media paths from the spec; keep all other traversal blocked.
- Every production behavior change starts with a focused failing test and ends with the focused test plus the complete relevant test project.
- Preserve the untracked `inputapp/` tree and unrelated user changes; never stage it.

---

### Task 1: Remove HTML import and narrow discovery

**Files:**
- Delete: `src/ChatArchive.Core/Importing/HtmlDataExtractor.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Modify: `src/ChatArchive.Core/Importing/ImportDiscovery.cs`
- Modify: `tests/ChatArchive.Core.Tests/ExportFormatsTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs`

**Interfaces:**
- Produces: `ImportDiscovery.SupportedExtensions` containing exactly `.json`, `.jsonl`, `.csv`, `.md`, `.txt`, `.sql`, `.xlsx`.
- Produces: `ExportFormats.Default` with no type named `ChatHtmlExportFormat`.
- Removes: `ChatHtmlExportFormat` and `HtmlDataExtractor`.

- [ ] **Step 1: Write failing behavior tests for HTML exclusion**

Add these assertions without referring to a type that will be deleted:

```csharp
[Fact]
public void Default_DoesNotRegisterHtmlImporter()
{
    Assert.DoesNotContain(
        ExportFormats.Default,
        format => format.GetType().Name.Contains("Html", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void ImportDiscovery_DoesNotDiscoverEmbeddedHtml()
{
    var html = Path.Combine(_tempDir, "chat.html");
    File.WriteAllText(html, """
        <script id="__DATA__" type="application/json">
        {"metadata":{"name":"QQChatExporter"},"chatInfo":{"name":"demo"},"messages":[]}
        </script>
        """);

    Assert.DoesNotContain(".html", ImportDiscovery.SupportedExtensions);
    Assert.DoesNotContain(".htm", ImportDiscovery.SupportedExtensions);
    Assert.Empty(ImportDiscovery.Discover(new[] { html }));
}
```

Delete the existing tests whose sole subject is `ChatHtmlExportFormat` or `HtmlDataExtractor`; retain JSON parser tests that happen to use the same payload shapes.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "Default_DoesNotRegisterHtmlImporter|ImportDiscovery_DoesNotDiscoverEmbeddedHtml"
```

Expected: both tests fail because HTML is currently registered and discovered.

- [ ] **Step 3: Remove HTML production paths**

Delete the `ChatHtmlExportFormat` class and its registration. Replace the extension set with:

```csharp
public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
{
    ".json",
    ".jsonl",
    ".csv",
    ".md",
    ".txt",
    ".sql",
    ".xlsx"
};
```

Delete `HtmlDataExtractor.cs`. Update the large mixed discovery test so it contains no HTML fixture, expects eight pre-Excel imports at this stage, and no longer expects the platform `html`.

- [ ] **Step 4: Run focused and core regression tests**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "ExportFormatsTests|ImportDiscoveryTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS; no compile reference to either deleted HTML type remains.

- [ ] **Step 5: Commit the HTML removal**

```powershell
git add src/ChatArchive.Core/Importing/HtmlDataExtractor.cs src/ChatArchive.Core/Importing/ExportFormats.cs src/ChatArchive.Core/Importing/ImportDiscovery.cs tests/ChatArchive.Core.Tests/ExportFormatsTests.cs tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs tests/ChatArchive.Core.Tests/ParserTests.cs
git commit -m "refactor(import): remove HTML import support"
```

---

### Task 2: Add stable file identities and constrained WeFlow parent media

**Files:**
- Modify: `src/ChatArchive.Core/Importing/ImportText.cs`
- Modify: `tests/ChatArchive.Core.Tests/MediaLocatorTests.cs`
- Test: `tests/ChatArchive.Core.Tests/ParserTests.cs`

**Interfaces:**
- Produces: `ImportText.StableFileNativeId(string filePath) -> string`, returning `file:<lowercase sha256>`.
- Modifies: `ImportText.SafeResolveMedia(string exportRoot, string declaredPath, string? sessionTitle = null)`.

- [ ] **Step 1: Write failing identity and media tests**

```csharp
[Fact]
public void StableFileNativeId_IsRepeatableAndPathSpecific()
{
    var first = Path.Combine(_dir, "a", "chat.txt");
    var second = Path.Combine(_dir, "b", "chat.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(first)!);
    Directory.CreateDirectory(Path.GetDirectoryName(second)!);

    var id = ImportText.StableFileNativeId(first);
    Assert.Matches("^file:[0-9a-f]{64}$", id);
    Assert.Equal(id, ImportText.StableFileNativeId(first));
    Assert.NotEqual(id, ImportText.StableFileNativeId(second));
}

[Fact]
public void SafeResolveMedia_AllowsOnlyExistingWeFlowLayoutAFile()
{
    var texts = Path.Combine(_dir, "export", "texts");
    var images = Path.Combine(_dir, "export", "images");
    Directory.CreateDirectory(texts);
    Directory.CreateDirectory(images);
    var image = Path.Combine(images, "one.jpg");
    File.WriteAllText(image, "image");

    Assert.Equal(image, ImportText.SafeResolveMedia(texts, "../images/one.jpg"));
    Assert.Null(ImportText.SafeResolveMedia(texts, "../images/missing.jpg"));
    Assert.Null(ImportText.SafeResolveMedia(texts, "../private.txt"));
    Assert.Null(ImportText.SafeResolveMedia(texts, "../images/../../private.txt"));
    Assert.Null(ImportText.SafeResolveMedia(texts, "../../images/one.jpg"));
}
```

Retain the existing tests that reject arbitrary parent files and all non-whitelisted traversal.

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "StableFileNativeId_IsRepeatableAndPathSpecific|SafeResolveMedia_AllowsOnlyExistingWeFlowLayoutAFile"
```

Expected: `StableFileNativeId` does not compile and the parent-media assertion fails after the compile error is resolved.

- [ ] **Step 3: Implement stable IDs and the one-level whitelist**

Add the identity helper:

```csharp
public static string StableFileNativeId(string filePath)
{
    var normalized = Path.GetFullPath(filePath)
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    if (OperatingSystem.IsWindows())
    {
        normalized = normalized.ToUpperInvariant();
    }

    var digest = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(normalized));
    return $"file:{Convert.ToHexStringLower(digest)}";
}
```

Before the current blanket `..` rejection, split the normalized path and allow exactly this branch:

```csharp
private static readonly HashSet<string> WeFlowParentMediaDirectories =
    new(StringComparer.OrdinalIgnoreCase) { "images", "voices", "videos", "emojis", "file" };

private static string? ResolveWeFlowParentMedia(string exportRoot, string normalized)
{
    var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length < 3
        || segments[0] != ".."
        || !WeFlowParentMediaDirectories.Contains(segments[1])
        || segments.Skip(2).Any(segment => segment is "." or ".."))
    {
        return null;
    }

    var root = Path.GetFullPath(exportRoot);
    var parent = Path.GetDirectoryName(root);
    if (parent is null)
    {
        return null;
    }

    var relative = Path.Combine(segments.Skip(1).ToArray());
    var candidate = SafeExportPath(parent, relative);
    if (candidate is null || !File.Exists(candidate))
    {
        return null;
    }

    var attributes = File.GetAttributes(candidate);
    return attributes.HasFlag(FileAttributes.Directory)
        || attributes.HasFlag(FileAttributes.ReparsePoint)
        ? null
        : candidate;
}
```

Call this only when `normalized.StartsWith("../", StringComparison.Ordinal)`; return its result directly so a missing parent candidate cannot fall through to a fabricated path.

- [ ] **Step 4: Run media and core regression tests**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "MediaLocatorTests|SafeResolveMedia|StableFileNativeId"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS, including all pre-existing traversal tests.

- [ ] **Step 5: Commit the identity and path behavior**

```powershell
git add src/ChatArchive.Core/Importing/ImportText.cs tests/ChatArchive.Core.Tests/MediaLocatorTests.cs tests/ChatArchive.Core.Tests/ParserTests.cs
git commit -m "fix(import): allow constrained WeFlow parent media"
```

---

### Task 3: Replace the imagined CSV format with current WeFlow CSV

**Files:**
- Modify: `src/ChatArchive.Core/Importing/DelimitedTextParsers.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Create: `src/ChatArchive.Core/Importing/FlatMessageFactory.cs`
- Create: `tests/ChatArchive.Core.Tests/WeFlowTextFormatTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs`

**Interfaces:**
- Produces: `WeFlowCsvParser.Matches`, `ReadConversation`, and `IterateMessages`.
- Produces: `WeFlowCsvExportFormat : IChatExportFormat`, with `Platform == "wechat"`.
- Produces: internal `FlatMessageFactory.Create(FlatMessageData data) -> ParsedMessage` for later text/SQL/Excel adapters.
- Removes: `WeCloneCsvParser` and `WeCloneCsvExportFormat`.

- [ ] **Step 1: Write a failing current-writer CSV test**

```csharp
[Fact]
public void WeFlowCsv_ParsesCurrentWriterColumnsAndMedia()
{
    var dir = NewDirectory();
    Directory.CreateDirectory(Path.Combine(dir, "images"));
    File.WriteAllText(Path.Combine(dir, "images", "one.jpg"), "image");
    var path = Path.Combine(dir, "项目群.csv");
    File.WriteAllText(path,
        "\uFEFFid,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime\r\n" +
        "1,9001,image,0,Alice,图片,images/one.jpg,2023-11-15T06:15:23.000Z\r\n",
        Encoding.UTF8);

    var format = new WeFlowCsvExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    Assert.Equal("wechat", export.Conversation.Platform);
    Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);
    Assert.Equal("项目群", export.Conversation.Title);

    var message = Assert.Single(export.EnumerateMessages());
    Assert.Equal("9001", message.NativeId);
    Assert.Equal("1", message.LocalId);
    Assert.Equal("Alice", message.SenderName);
    Assert.Equal("incoming", message.Direction);
    Assert.Equal("image", message.MessageType);
    Assert.Equal("图片", message.Content);
    Assert.Equal(Path.Combine(dir, "images", "one.jpg"), Assert.Single(message.Attachments).SourcePath);
}

[Fact]
public void WeFlowCsv_RejectsFormerImaginedHeaders()
{
    var path = WriteFile("old.csv", "is_sender,talker,content\n0,Alice,hello\n");
    Assert.False(new WeFlowCsvExportFormat().Matches(path));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowCsv_"
```

Expected: the new format type does not exist or the actual header does not match.

- [ ] **Step 3: Add the shared flat-message constructor**

Create this exact input contract:

```csharp
internal sealed record FlatMessageData(
    string? NativeId,
    string? LocalId,
    long TimestampMs,
    string SenderNativeId,
    string SenderName,
    string Direction,
    string MessageType,
    string Content,
    string SourceLocator,
    JsonObject RawPayload,
    IReadOnlyList<ParsedAttachment>? Attachments = null,
    string? Sequence = null,
    string? ReplyToNativeId = null,
    bool IsRecalled = false,
    bool IsSystem = false,
    string? MediaType = null);
```

`FlatMessageFactory.Create` must build aliases from the non-empty name and native ID, calculate `PayloadHash` from timestamp/sender/direction/type/content/search text, calculate `SemanticHash` from timestamp/sender/direction, use `Content` as `SearchText`, and default attachments/compatible hashes to empty arrays. Keep this factory internal and use it only for the new flat export formats.

- [ ] **Step 4: Implement strict WeFlow CSV mapping**

Use the exact ordered header signature:

```csharp
private static readonly string[] CurrentHeaders =
[
    "id", "MsgSvrID", "type_name", "is_sender", "talker", "msg", "src", "CreateTime"
];
```

Map current type strings with:

```csharp
private static string MapType(string value) => value.Trim().ToLowerInvariant() switch
{
    "image" => "image",
    "sticker" => "emoji",
    "video" => "video",
    "voice" => "audio",
    "location" => "location",
    "file" => "file",
    _ => "text"
};
```

Use `msg` for content, `CreateTime` through `ImportText.ParseFlexibleTimestamp`, `is_sender` for direction, and `src` for one `ParsedAttachment` only when non-empty. The attachment declared path is `src`; the source path is `ImportText.SafeResolveMedia(exportRoot, src, conversation.Title)`; kind and MIME derive from the mapped type/path.

Replace the old format registration with `new WeFlowCsvExportFormat()` and remove the former imagined CSV tests.

- [ ] **Step 5: Run focused and parser regression tests**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowCsv_|Rfc4180CsvReader"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "ParserTests|WeFlowTextFormatTests"
```

Expected: PASS; quoted commas/newlines continue to work through the RFC 4180 reader.

- [ ] **Step 6: Commit the CSV correction**

```powershell
git add src/ChatArchive.Core/Importing/DelimitedTextParsers.cs src/ChatArchive.Core/Importing/ExportFormats.cs src/ChatArchive.Core/Importing/FlatMessageFactory.cs tests/ChatArchive.Core.Tests/WeFlowTextFormatTests.cs tests/ChatArchive.Core.Tests/ParserTests.cs
git commit -m "fix(import): parse current WeFlow CSV exports"
```

---

### Task 4: Replace generic Markdown/TXT with current WeFlow text exports

**Files:**
- Modify: `src/ChatArchive.Core/Importing/DelimitedTextParsers.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Modify: `tests/ChatArchive.Core.Tests/WeFlowTextFormatTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs`

**Interfaces:**
- Produces: `WeFlowMarkdownParser` and `WeFlowTextParser`.
- Produces: `WeFlowMarkdownExportFormat` and `WeFlowTextExportFormat`, both with platform `wechat`.
- Removes: `MarkdownChatParser`, `TextChatParser`, `ChatMarkdownExportFormat`, and `ChatTextExportFormat`.

- [ ] **Step 1: Write failing tests from the current Markdown and TXT writers**

```csharp
[Fact]
public void WeFlowMarkdown_ParsesMetadataBlocksAndMedia()
{
    var dir = NewDirectory();
    Directory.CreateDirectory(Path.Combine(dir, "images"));
    var media = Path.Combine(dir, "images", "one.jpg");
    File.WriteAllText(media, "image");
    var path = WriteFile(dir, "chat.md", """
        # 项目群

        - 会话ID: `group@chatroom`
        - 会话类型: 群聊
        - 消息数量: 1
        - 导出时间: 2023-11-15 06:16:00
        - 导出工具: WeFlow

        ---

        ## 2023-11-15 06:15:23 Alice

        > Bob: 被引用内容

        ![图片](images/one.jpg)

        回复正文
        """);

    var format = new WeFlowMarkdownExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    Assert.Equal("group@chatroom", export.Conversation.NativeId);
    Assert.Equal("group", export.Conversation.Kind);
    var message = Assert.Single(export.EnumerateMessages());
    Assert.Equal("Alice", message.SenderName);
    Assert.Contains("被引用内容", message.SearchText);
    Assert.Contains("回复正文", message.Content);
    Assert.Equal(media, Assert.Single(message.Attachments).SourcePath);
}

[Fact]
public void WeFlowTxt_StripsWriterQuotesAndKeepsMultilineBody()
{
    var path = WriteFile("chat.txt", """
        2023-11-15 06:15:23 'Alice'
        第一行
        第二行

        2023-11-15 06:16:23 '我'
        回复
        """);

    var format = new WeFlowTextExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    var messages = export.EnumerateMessages().ToList();
    Assert.Equal(2, messages.Count);
    Assert.Equal("Alice", messages[0].SenderName);
    Assert.Equal("第一行\n第二行", messages[0].Content.Replace("\r\n", "\n"));
    Assert.Equal("outgoing", messages[1].Direction);
}

[Theory]
[InlineData("# Any title\n[2023-11-15 06:15:23] Alice: old", ".md")]
[InlineData("会话: old\n2023-11-15 06:15:23 Alice: old", ".txt")]
public void WeFlowText_RejectsFormerImaginedSyntax(string content, string extension)
{
    var path = WriteFile($"old{extension}", content);
    Assert.DoesNotContain(ExportFormats.Default, format => format.Matches(path));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowMarkdown_|WeFlowTxt_|WeFlowText_"
```

Expected: current Markdown does not produce a message, TXT retains quotes, and the old syntaxes still match.

- [ ] **Step 3: Implement the strict Markdown state machine**

Use these compiled expressions:

```csharp
private static readonly Regex SessionIdRegex = new(
    @"^- 会话ID:\s*`(?<id>.*)`\s*$", RegexOptions.Compiled);
private static readonly Regex SessionTypeRegex = new(
    @"^- 会话类型:\s*(?<type>群聊|私聊)\s*$", RegexOptions.Compiled);
private static readonly Regex MessageHeaderRegex = new(
    @"^##\s+(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+(?<sender>.+?)\s*$",
    RegexOptions.Compiled);
private static readonly Regex MarkdownLinkRegex = new(
    @"!?\[(?<label>[^\]]*)\]\((?<path>[^)]+)\)", RegexOptions.Compiled);
```

`Matches` must see `- 导出工具: WeFlow`, a valid session ID, a valid session type, and at least one valid message header in the first 100 non-empty lines. `ReadConversation` reads the first `# ` title plus metadata. `IterateMessages` flushes at the next message header, extracts link paths into ordered attachments, converts Markdown links and quote markers to readable search text, and preserves ordinary body line breaks. Use `FlatMessageFactory` and mark only sender `我` outgoing.

- [ ] **Step 4: Implement the strict WeFlow TXT state machine and remove generic branches**

Use this sole message-header expression:

```csharp
private static readonly Regex MessageHeaderRegex = new(
    @"^(?<time>\d{4}-\d{1,2}-\d{1,2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\s+'(?<sender>[^'\r\n]+)'\s*$",
    RegexOptions.Compiled);
```

`Matches` requires this header followed by at least one content line. `ReadConversation` uses `ImportText.StableFileNativeId(filePath)` and the filename title. `IterateMessages` treats blank lines as body separators unless the next non-empty line is a valid header, trims only trailing blank lines, and flushes the final message at EOF. Remove the old generic parser classes, adapters, registrations, and their tests.

- [ ] **Step 5: Run focused and core regression tests**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowMarkdown_|WeFlowTxt_|WeFlowText_"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS; no old ideal Markdown/TXT fixture is discovered.

- [ ] **Step 6: Commit the WeFlow text correction**

```powershell
git add src/ChatArchive.Core/Importing/DelimitedTextParsers.cs src/ChatArchive.Core/Importing/ExportFormats.cs tests/ChatArchive.Core.Tests/WeFlowTextFormatTests.cs tests/ChatArchive.Core.Tests/ParserTests.cs
git commit -m "fix(import): align WeFlow Markdown and TXT parsers"
```

---

### Task 5: Add current QQ Chat Exporter TXT

**Files:**
- Create: `src/ChatArchive.Core/Importing/QqTextParser.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Create: `tests/ChatArchive.Core.Tests/QqTextFormatTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs`

**Interfaces:**
- Produces: `QqTextParser.Matches`, `ReadConversation`, `IterateMessages`.
- Produces: `QqTextExportFormat : IChatExportFormat`, with platform `qq`.

- [ ] **Step 1: Write a failing test covering writer options**

```csharp
[Fact]
public void QqTxt_ParsesCurrentHeaderOptionalTypeAndResources()
{
    var path = WriteFile("qq.txt", """
        [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
        [本软件是免费的开源项目~ 如果您是买来的，请立即退款！如果有帮助到您，欢迎给我点个Star~]

        ===============================================
                   QQ聊天记录导出文件
        ===============================================

        聊天名称: 示例群
        聊天类型: 群聊
        消息总数: 1

        [1]
        [群主] Alice:
        时间: 2023-11-15 06:15:23
        类型: image
        内容: [image消息]
        资源: 1 个文件
          - image: one.jpg

        ===============================================
                      导出完成
        ===============================================
        总计导出 1 条消息
        """);

    var format = new QqTextExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    Assert.Equal("qq", export.Conversation.Platform);
    Assert.Equal("示例群", export.Conversation.Title);
    Assert.Equal("group", export.Conversation.Kind);
    Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);

    var message = Assert.Single(export.EnumerateMessages());
    Assert.Equal("1", message.LocalId);
    Assert.Equal("Alice", message.SenderName);
    Assert.Equal("incoming", message.Direction);
    Assert.Equal("image", message.MessageType);
    Assert.True(message.RawPayload["resourceCount"]!.GetValue<int>() == 1);
}

[Fact]
public void QqTxt_RejectsAPlainFileWithChineseLabels()
{
    var path = WriteFile("plain.txt", "聊天名称: x\n时间: 2023-11-15 06:15:23\n内容: y");
    Assert.False(new QqTextExportFormat().Matches(path));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "QqTxt_"
```

Expected: `QqTextExportFormat` does not exist.

- [ ] **Step 3: Implement exact QQ header and message parsing**

Require this header literal before accepting a file:

```csharp
private const string Signature =
    "[QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]";
```

Use these line expressions:

```csharp
private static readonly Regex NumberRegex = new(@"^\[(?<number>\d+)\]$", RegexOptions.Compiled);
private static readonly Regex TimeRegex = new(@"^时间:\s*(?<value>.+)$", RegexOptions.Compiled);
private static readonly Regex TypeRegex = new(@"^类型:\s*(?<value>.+)$", RegexOptions.Compiled);
private static readonly Regex ContentRegex = new(@"^内容:\s*(?<value>.*)$", RegexOptions.Compiled);
private static readonly Regex ResourceRegex = new(
    @"^\s{2}-\s+(?<type>[^:：]+)[:：]\s*(?<name>.*)$", RegexOptions.Compiled);
```

Read `聊天名称` and `聊天类型` from the header. A message begins at `[N]` when enabled, otherwise at a sender/time sequence; `时间:` establishes a required message timestamp and the footer terminates enumeration. Strip an optional `[群头衔] ` prefix from the sender while preserving it in `RawPayload`. Map `text/image/video/audio/file/face/reply/system` and their Chinese Excel labels to the standard types. Because this writer omits self UID/UIN, mark non-system rows incoming and system rows system. Record resource names as attachment metadata without inventing a local path.

- [ ] **Step 4: Register QQ TXT and update discovery coverage**

Register `QqTextExportFormat` before `WeFlowTextExportFormat`, because QQ has the stronger header signature. Update the mixed directory test to use the actual QQ fixture and assert platform `qq`.

- [ ] **Step 5: Run focused and core regression tests**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "QqTxt_|ImportDiscoveryTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS and plain TXT files remain undiscovered.

- [ ] **Step 6: Commit QQ TXT support**

```powershell
git add src/ChatArchive.Core/Importing/QqTextParser.cs src/ChatArchive.Core/Importing/ExportFormats.cs tests/ChatArchive.Core.Tests/QqTextFormatTests.cs tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs
git commit -m "feat(import): support QQ Chat Exporter TXT"
```

---

### Task 6: Replace generic SQL import with WeFlow and CipherTalk SQL profiles

**Files:**
- Modify: `src/ChatArchive.Core/Importing/SqlScriptParser.cs`
- Create: `src/ChatArchive.Core/Importing/SqlExportFormats.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Replace: `tests/ChatArchive.Core.Tests/SqlScriptParserTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs`

**Interfaces:**
- Produces: `SqlInsertReader.Enumerate(TextReader reader, CancellationToken token) -> IEnumerable<SqlInsertRow>`.
- Produces: `internal sealed record SqlInsertRow(string Table, IReadOnlyDictionary<string, string?> Values)`.
- Produces: `WeFlowSqlExportFormat` and `CipherTalkSqlExportFormat`, both with platform `wechat`.
- Removes: generic `ChatSqlExportFormat` and its platform `sql`.

- [ ] **Step 1: Write failing source-specific SQL tests**

```csharp
[Fact]
public void WeFlowSql_MapsCurrentTableAndMedia()
{
    var dir = NewDirectory();
    Directory.CreateDirectory(Path.Combine(dir, "images"));
    var image = Path.Combine(dir, "images", "one.jpg");
    File.WriteAllText(image, "image");
    var path = WriteFile(dir, "weflow.sql", """
        BEGIN;
        CREATE TABLE IF NOT EXISTS weflow_messages (
          session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
          create_time BIGINT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
          local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
        );
        INSERT INTO weflow_messages
          (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
        VALUES ('group@chatroom', '1', '9001', 1700000123, 'wxid_alice', FALSE, 3, 'image', '图片', 'images/one.jpg');
        COMMIT;
        """);

    var format = new WeFlowSqlExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    Assert.Equal("group@chatroom", export.Conversation.NativeId);
    var message = Assert.Single(export.EnumerateMessages());
    Assert.Equal("9001", message.NativeId);
    Assert.Equal("wxid_alice", message.SenderNativeId);
    Assert.Equal("image", message.MessageType);
    Assert.Equal(image, Assert.Single(message.Attachments).SourcePath);
}

[Fact]
public void CipherTalkSql_UsesSessionSenderTypeAndReplyColumns()
{
    var path = WriteFile("ciphertalk.sql", """
        -- 密语 CipherTalk - 聊天记录导出
        CREATE TABLE IF NOT EXISTS sessions (
          wxid TEXT PRIMARY KEY, display_name TEXT NOT NULL, session_type TEXT NOT NULL,
          owner_id TEXT, message_count INTEGER, first_message_time BIGINT,
          last_message_time BIGINT, exported_at BIGINT
        );
        CREATE TABLE IF NOT EXISTS messages (
          id SERIAL PRIMARY KEY, session_wxid TEXT NOT NULL, local_id INTEGER,
          create_time BIGINT NOT NULL, formatted_time TEXT, msg_type TEXT, content TEXT,
          is_send SMALLINT, sender_username TEXT, sender_display_name TEXT,
          group_nickname TEXT, reply_to_message_id TEXT
        );
        INSERT INTO sessions
          (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at)
        VALUES ('group@chatroom', '项目群', 'group', 'wxid_self', 1, 1700000123, 1700000123, 1700000200);
        INSERT INTO messages
          (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
        VALUES ('group@chatroom', 7, 1700000123, '2023-11-15 06:15:23', '图片消息', '图片', 0, 'wxid_alice', 'Alice', '群名片', '8999');
        """);

    var format = new CipherTalkSqlExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    Assert.Equal("项目群", export.Conversation.Title);
    var message = Assert.Single(export.EnumerateMessages());
    Assert.Equal("wxid_alice", message.SenderNativeId);
    Assert.Equal("Alice", message.SenderName);
    Assert.Equal("image", message.MessageType);
    Assert.Equal("8999", message.ReplyToNativeId);
}

[Fact]
public void SqlFormats_RejectAnUnrelatedMessagesTable()
{
    var path = WriteFile("generic.sql", "CREATE TABLE messages(id INT, content TEXT); INSERT INTO messages VALUES (1, 'x');");
    Assert.False(new WeFlowSqlExportFormat().Matches(path));
    Assert.False(new CipherTalkSqlExportFormat().Matches(path));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowSql_|CipherTalkSql_|SqlFormats_"
```

Expected: source-specific formats do not exist and generic SQL still matches the unrelated table.

- [ ] **Step 3: Refactor the SQL lexer to preserve table identity**

Keep the proven statement/string/comment/tuple state machines, but change the row output to:

```csharp
internal sealed record SqlInsertRow(
    string Table,
    IReadOnlyDictionary<string, string?> Values);

internal static class SqlInsertReader
{
    internal static IEnumerable<SqlInsertRow> Enumerate(
        TextReader reader,
        CancellationToken cancellationToken = default);
}
```

`ProcessStatement` must yield `new SqlInsertRow(insertTable, dict)`. Keep tests for doubled quotes, PostgreSQL booleans, nested parentheses, multi-line comments, columnless INSERT using the prior `CREATE TABLE`, and multiple tuples on one statement. Delete generic table-keyword matching; filtering belongs to the two adapters.

- [ ] **Step 4: Implement exact WeFlow and CipherTalk adapters**

WeFlow accepts only table `weflow_messages` with all ten current columns. CipherTalk accepts `sessions` and `messages` only when their column sets include the exact current keys. Use these type maps:

```csharp
private static string MapWeChatType(string? text, string? number) =>
    (text ?? string.Empty).Trim() switch
    {
        "文本消息" => "text",
        "图片消息" => "image",
        "语音消息" => "audio",
        "视频消息" => "video",
        "表情消息" => "emoji",
        "引用/文件/链接消息" => "link",
        "系统消息" => "system",
        _ => number switch
        {
            "1" => "text", "3" => "image", "34" => "audio", "43" => "video",
            "47" => "emoji", "49" => "link", "10000" => "system", _ => "other"
        }
    };
```

Read the CipherTalk session row once in `Open` to build its title/kind/owner context; reopen the file for streaming messages. Use `FlatMessageFactory` for messages and attach WeFlow `media_path` through `SafeResolveMedia`.

- [ ] **Step 5: Replace registration and run SQL regressions**

Register the two profiles, remove `ChatSqlExportFormat`, and replace generic SQL tests with low-level lexer tests plus the two source tests.

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "SqlScriptParserTests|WeFlowSql_|CipherTalkSql_|SqlFormats_"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS; unrelated SQL is not discovered.

- [ ] **Step 6: Commit SQL profile support**

```powershell
git add src/ChatArchive.Core/Importing/SqlScriptParser.cs src/ChatArchive.Core/Importing/SqlExportFormats.cs src/ChatArchive.Core/Importing/ExportFormats.cs tests/ChatArchive.Core.Tests/SqlScriptParserTests.cs tests/ChatArchive.Core.Tests/ParserTests.cs
git commit -m "fix(import): align SQL with WeFlow and CipherTalk"
```

---

### Task 7: Build a safe streaming OpenXML workbook reader

**Files:**
- Create: `src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs`
- Create: `tests/ChatArchive.Core.Tests/XlsxTestFile.cs`
- Create: `tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs`

**Interfaces:**
- Produces: `OpenXmlWorkbookReader.Open(string filePath) -> OpenXmlWorkbookReader`.
- Produces: `IReadOnlyList<OpenXmlSheet> Sheets`.
- Produces: `IEnumerable<OpenXmlRow> ReadRows(OpenXmlSheet sheet, CancellationToken token)`.
- Produces: `internal sealed record OpenXmlSheet(string Name, string EntryPath)`.
- Produces: `internal sealed record OpenXmlCell(int ColumnIndex, string Reference, string Value, string? Hyperlink)`.
- Produces: `internal sealed record OpenXmlRow(uint RowIndex, IReadOnlyDictionary<int, OpenXmlCell> Cells)`.

- [ ] **Step 1: Add a deterministic test-only XLSX package builder**

Create these test contracts:

```csharp
internal sealed record XlsxTestCell(
    string Reference,
    string? Value,
    string Type = "inlineStr",
    string? Formula = null,
    string? Hyperlink = null,
    bool ExternalHyperlink = false);

internal sealed record XlsxTestSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<XlsxTestCell>> Rows);

internal static class XlsxTestFile
{
    internal static void Write(string filePath, params XlsxTestSheet[] sheets);
}
```

The builder writes a minimal valid package containing `[Content_Types].xml`, `_rels/.rels`, `xl/workbook.xml`, `xl/_rels/workbook.xml.rels`, worksheet XML, worksheet relationships when links exist, and `xl/sharedStrings.xml` when a cell type is `s`. XML-escape every value through `XmlWriter`; never concatenate unescaped test values.

- [ ] **Step 2: Write failing OpenXML behavior and safety tests**

```csharp
[Fact]
public void OpenXmlReader_ReadsCellKindsFormulaCacheAndInternalHyperlink()
{
    var path = NewPath("cells.xlsx");
    XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
    [
        [
            new("A1", "shared", "s"),
            new("B1", "inline", "inlineStr"),
            new("C1", "42", "n"),
            new("D1", "1", "b"),
            new("E1", "cached", "str", Formula: "1+1"),
            new("F1", "media/one.jpg", Hyperlink: "media/one.jpg")
        ]
    ]));

    using var workbook = OpenXmlWorkbookReader.Open(path);
    var sheet = Assert.Single(workbook.Sheets);
    var row = Assert.Single(workbook.ReadRows(sheet, CancellationToken.None));
    Assert.Equal("shared", row.Cells[1].Value);
    Assert.Equal("inline", row.Cells[2].Value);
    Assert.Equal("42", row.Cells[3].Value);
    Assert.Equal("true", row.Cells[4].Value);
    Assert.Equal("cached", row.Cells[5].Value);
    Assert.Equal("media/one.jpg", row.Cells[6].Hyperlink);
}

[Fact]
public void OpenXmlReader_RejectsExternalRelationship()
{
    var path = NewPath("external.xlsx");
    XlsxTestFile.Write(path, new XlsxTestSheet("聊天记录",
    [[new XlsxTestCell("A1", "click", Hyperlink: "https://example.invalid", ExternalHyperlink: true)]]));

    using var workbook = OpenXmlWorkbookReader.Open(path);
    var error = Assert.Throws<ImportFormatException>(
        () => workbook.ReadRows(workbook.Sheets[0], CancellationToken.None).ToList());
    Assert.Contains("外部关系", error.Message);
}

[Fact]
public void OpenXmlReader_ReportsCorruptZipAsImportFormatError()
{
    var path = NewPath("broken.xlsx");
    File.WriteAllText(path, "not a zip");
    Assert.Throws<ImportFormatException>(() => OpenXmlWorkbookReader.Open(path));
}
```

Add cancellation coverage by cancelling before the second worksheet row and asserting `OperationCanceledException` is not wrapped.

- [ ] **Step 3: Run the reader tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_"
```

Expected: reader and fixture-builder types do not exist.

- [ ] **Step 4: Implement package, workbook, and shared-string loading**

Open with `ZipFile.OpenRead`. Normalize every relationship target with a helper that rejects rooted targets and any normalized result outside the owning package directory:

```csharp
private static string ResolvePackageTarget(string ownerEntry, string target, string filePath)
{
    if (Path.IsPathRooted(target) || target.Contains('\\'))
    {
        throw new ImportFormatException(filePath, "XLSX 包含非法关系路径");
    }

    var ownerDirectory = ownerEntry[..(ownerEntry.LastIndexOf('/') + 1)];
    var stack = new List<string>();
    foreach (var segment in (ownerDirectory + target).Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        if (segment == ".") continue;
        if (segment == "..")
        {
            if (stack.Count == 0) throw new ImportFormatException(filePath, "XLSX 关系越界");
            stack.RemoveAt(stack.Count - 1);
        }
        else
        {
            stack.Add(segment);
        }
    }
    return string.Join('/', stack);
}
```

Read `xl/workbook.xml` sheet name/relationship IDs, resolve them through `xl/_rels/workbook.xml.rels`, and verify each target entry exists. Load shared strings as the concatenation of all descendant spreadsheet `<t>` nodes per `<si>`, preserving rich text order.

- [ ] **Step 5: Implement streaming worksheet rows and hyperlinks**

Pre-scan worksheet `<hyperlink ref=... r:id=...>` nodes and its `.rels` file. Throw when a referenced relation has `TargetMode="External"`; resolve internal link targets only through `ResolvePackageTarget`. Reopen the worksheet entry and use `XmlReader` to stream `<row>` and `<c>` elements. Convert A1 references with:

```csharp
private static int ColumnIndex(string reference)
{
    var result = 0;
    foreach (var c in reference.TakeWhile(char.IsLetter))
    {
        result = checked(result * 26 + char.ToUpperInvariant(c) - 'A' + 1);
    }
    return result;
}
```

Resolve `t="s"`, `inlineStr`, `str`, `b`, and ordinary numeric/cache `<v>` values; map boolean `1/0` to `true/false`. A formula without `<v>` yields an empty value. Check cancellation before each row and every 256 cells. Let `OperationCanceledException` escape; wrap ZIP/XML/path failures in `ImportFormatException(filePath, ...)`.

- [ ] **Step 6: Run reader and full core tests**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "OpenXmlReader_"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS with no package or XML warnings.

- [ ] **Step 7: Commit the OpenXML reader**

```powershell
git add src/ChatArchive.Core/Importing/OpenXmlWorkbookReader.cs tests/ChatArchive.Core.Tests/XlsxTestFile.cs tests/ChatArchive.Core.Tests/OpenXmlWorkbookReaderTests.cs
git commit -m "feat(import): add safe OpenXML workbook reader"
```

---

### Task 8: Add all three current WeFlow Excel layouts

**Files:**
- Create: `src/ChatArchive.Core/Importing/WeFlowExcelParser.cs`
- Create: `src/ChatArchive.Core/Importing/ExcelExportFormats.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Create: `tests/ChatArchive.Core.Tests/WeFlowExcelFormatTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs`

**Interfaces:**
- Produces: `WeFlowExcelParser.Matches`, `ReadConversation`, `IterateMessages`.
- Produces: `WeFlowExcelExportFormat : IChatExportFormat`, with platform `wechat`.

- [ ] **Step 1: Write failing tests for compact, private, and group headers**

Use `XlsxTestFile` to create three workbooks whose first rows exactly follow the upstream writer. The group case must include a linked media cell:

```csharp
[Theory]
[InlineData("compact")]
[InlineData("private")]
[InlineData("group")]
public void WeFlowExcel_ParsesCurrentDynamicLayouts(string layout)
{
    var path = CreateWeFlowWorkbook(layout);
    var format = new WeFlowExcelExportFormat();
    Assert.True(format.Matches(path));
    using var export = format.Open(path);
    Assert.Equal("wxid_session", export.Conversation.NativeId);
    Assert.Equal(layout == "group" ? "group" : "private", export.Conversation.Kind);
    var message = Assert.Single(export.EnumerateMessages());
    Assert.Equal(1700000123000, message.TimestampMs);
    Assert.Equal("image", message.MessageType);
    Assert.Equal("正文", message.Content);
}
```

`CreateWeFlowWorkbook` writes:

```text
row 1: 会话信息
row 2: 微信ID | wxid_session | [merged blank] | 昵称 | 会话标题 | 备注 | 群备注
row 3: 导出工具 | WeFlow | 导出版本 | 1.0.3 | 平台 | wechat | 导出时间 | 2023-11-15 06:20:00
row 4+: one of the exact three header layouts from the design, followed by one row
```

For the group layout, make the content cell value and internal hyperlink `../images/one.jpg`, create the parent image on disk, and assert the attachment resolves. Add a negative workbook whose core header exists but metadata generator is not WeFlow; `Matches` must return false.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowExcel_"
```

Expected: format does not exist.

- [ ] **Step 3: Implement metadata and header detection**

Find a sheet named `聊天记录`, then scan the first 20 rows. Require all of:

```csharp
metadata["会话信息"] == "会话信息";
metadata["导出工具"] == "WeFlow";
metadata.ContainsKey("微信ID");
```

Detect a header row by its ordered normalized values:

```csharp
private static readonly string[] CompactHeaders =
    ["序号", "时间", "发送者身份", "消息类型", "内容"];
private static readonly string[] PrivateHeaders =
    ["序号", "时间", "发送者昵称", "发送者微信ID", "发送者备注", "发送者身份", "消息类型", "内容"];
private static readonly string[] GroupHeaders =
    ["序号", "时间", "发送者昵称", "发送者微信ID", "发送者备注", "群昵称", "发送者身份", "消息类型", "内容"];
```

Build conversation title from non-empty remark, nickname, then filename; type is group only for the group layout or a session ID ending `@chatroom`.

- [ ] **Step 4: Implement row mapping and media links**

Skip fully blank rows. Use the displayed `时间` through `ParseFlexibleTimestamp`, sender WeChat ID when present, otherwise `name:<sender identity>`. Only the exact writer label `我` is outgoing; system types are system; other rows are incoming. Map Chinese type names with containment:

```csharp
private static string MapType(string value) => value switch
{
    var v when v.Contains("图片", StringComparison.Ordinal) => "image",
    var v when v.Contains("语音", StringComparison.Ordinal) => "audio",
    var v when v.Contains("视频", StringComparison.Ordinal) => "video",
    var v when v.Contains("表情", StringComparison.Ordinal) => "emoji",
    var v when v.Contains("文件", StringComparison.Ordinal) => "file",
    var v when v.Contains("位置", StringComparison.Ordinal) => "location",
    var v when v.Contains("系统", StringComparison.Ordinal) => "system",
    _ => "text"
};
```

When the content cell has a non-empty internal hyperlink, create one attachment using the hyperlink as declared path and `SafeResolveMedia` as source path. Use `FlatMessageFactory` for the message.

- [ ] **Step 5: Register/discover WeFlow Excel and run regressions**

Register `WeFlowExcelExportFormat`, ensure `.xlsx` is already in discovery, add the generated workbook to discovery coverage, and assert platform `wechat`.

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "WeFlowExcel_|ImportDiscoveryTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS for all layouts and the media whitelist.

- [ ] **Step 6: Commit WeFlow Excel support**

```powershell
git add src/ChatArchive.Core/Importing/WeFlowExcelParser.cs src/ChatArchive.Core/Importing/ExcelExportFormats.cs src/ChatArchive.Core/Importing/ExportFormats.cs tests/ChatArchive.Core.Tests/WeFlowExcelFormatTests.cs tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs
git commit -m "feat(import): support current WeFlow Excel exports"
```

---

### Task 9: Add current CipherTalk and QQ Excel formats

**Files:**
- Create: `src/ChatArchive.Core/Importing/CipherTalkExcelParser.cs`
- Create: `src/ChatArchive.Core/Importing/QqExcelParser.cs`
- Modify: `src/ChatArchive.Core/Importing/ExcelExportFormats.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Create: `tests/ChatArchive.Core.Tests/CipherTalkExcelFormatTests.cs`
- Create: `tests/ChatArchive.Core.Tests/QqExcelFormatTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs`

**Interfaces:**
- Produces: `CipherTalkExcelExportFormat : IChatExportFormat`, platform `wechat`.
- Produces: `QqExcelExportFormat : IChatExportFormat`, platform `qq`.
- Consumes: `OpenXmlWorkbookReader`, `FlatMessageFactory`, and `ImportText.StableFileNativeId`.

- [ ] **Step 1: Write failing CipherTalk Excel tests**

Create a workbook whose first row is:

```text
序号 | 时间 | 日期 | 时刻 | 星期 | 发送者 | 微信ID | 消息类型 | 消息内容 | 原始类型代码 | 时间戳 | 头像链接 | 聊天记录详情
```

Then assert:

```csharp
var format = new CipherTalkExcelExportFormat();
Assert.True(format.Matches(path));
using var export = format.Open(path);
Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);
Assert.Equal("工作表标题", export.Conversation.Title);
var message = Assert.Single(export.EnumerateMessages());
Assert.Equal(1700000123000, message.TimestampMs);
Assert.Equal("wxid_alice", message.SenderNativeId);
Assert.Equal("Alice", message.SenderName);
Assert.Equal("image", message.MessageType);
Assert.Equal("incoming", message.Direction);
Assert.Contains("转发详情", message.SearchText);
```

Add a second fixture without the two optional columns and ensure it still matches.

- [ ] **Step 2: Write failing QQ Excel and resource-association tests**

Create `聊天记录` with the exact QQ headers, including optional `群头衔`, and `资源列表` with the exact resource headers. Include two messages with different time/sender keys and one resource that uniquely matches the first:

```csharp
var format = new QqExcelExportFormat();
Assert.True(format.Matches(path));
using var export = format.Open(path);
Assert.Equal("qq", export.Conversation.Platform);
Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);
var messages = export.EnumerateMessages().ToList();
Assert.Equal(2, messages.Count);
Assert.Equal("10002", messages[0].SenderNativeId);
Assert.True(messages[0].IsRecalled);
Assert.Single(messages[0].Attachments);
Assert.Empty(messages[1].Attachments);
```

Add an ambiguous case with two identical time/sender keys and assert the resource is attached to neither message.

- [ ] **Step 3: Run Excel profile tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "CipherTalkExcel_|QqExcel_"
```

Expected: both format types do not exist.

- [ ] **Step 4: Implement CipherTalk Excel mapping**

Match the fixed 11-column core prefix; allow only the two named optional columns after it. Use numeric `时间戳` first and displayed `时间` second. `微信ID` is sender native ID; `发送者` is name; `原始类型代码` takes precedence for mapping (`1,3,34,43,47,49,10000`), with the Chinese name as fallback. Append `聊天记录详情` to search/content text with one newline when non-empty. The workbook has no conversation ID or owner, so use path-derived ID and incoming except system rows.

- [ ] **Step 5: Implement QQ Excel mapping and safe resource joins**

Match these ordered message headers, allowing `群头衔` only between QQ number and message type:

```csharp
["序号", "时间", "发送者", "发送者QQ号", "消息类型", "消息内容", "是否撤回", "资源数量"]
```

Match the `资源列表` header exactly. Build a join key from normalized timestamp text, QQ number, and sender name:

```csharp
internal readonly record struct QqExcelJoinKey(string Time, string Uin, string Sender);
```

Count message occurrences per key first. Attach resource rows only when the key count equals one. Treat an internal/local `URL` as declared path and resolve it safely; preserve HTTP(S) only in attachment metadata, never as `SourcePath`. Map QQ Chinese labels `文本/图片/视频/音频/文件/表情/@提及/回复/系统消息`; read `是否撤回 == 是`; direction remains incoming unless system.

- [ ] **Step 6: Register both formats in unambiguous order**

Register Excel adapters in this order: WeFlow (generator metadata), CipherTalk (fixed 11-column header), QQ (QQ-specific headers). Extend discovery tests with one CipherTalk and one QQ workbook and assert their platforms.

- [ ] **Step 7: Run focused and core regression tests**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "CipherTalkExcel_|QqExcel_|ImportDiscoveryTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS, including the ambiguous resource negative case.

- [ ] **Step 8: Commit CipherTalk and QQ Excel support**

```powershell
git add src/ChatArchive.Core/Importing/CipherTalkExcelParser.cs src/ChatArchive.Core/Importing/QqExcelParser.cs src/ChatArchive.Core/Importing/ExcelExportFormats.cs src/ChatArchive.Core/Importing/ExportFormats.cs tests/ChatArchive.Core.Tests/CipherTalkExcelFormatTests.cs tests/ChatArchive.Core.Tests/QqExcelFormatTests.cs tests/ChatArchive.Core.Tests/ImportDiscoveryTests.cs
git commit -m "feat(import): support CipherTalk and QQ Excel exports"
```

---

### Task 10: Add source-provenance fixtures, end-to-end coverage, and truthful docs

**Files:**
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/README.md`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/weflow-standard.json`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/weflow-arkme.json`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/ciphertalk-detailed.json`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/chatlab-current.jsonl`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/qq-single.json`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/weflow-current.csv`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/weflow-current.md`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/weflow-current.txt`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/qq-current.txt`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/weflow-current.sql`
- Create: `tests/ChatArchive.Core.Tests/Fixtures/CurrentExports/ciphertalk-current.sql`
- Modify: `tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj`
- Create: `tests/ChatArchive.Core.Tests/CurrentExportCompatibilityTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportServiceTests.cs`
- Modify: `docs/EXPORT_FORMATS_SPEC.md`
- Modify: `README.md`

**Interfaces:**
- Produces: committed, source-attributed fixtures copied to the test output directory.
- Verifies: all registered current formats can be discovered, opened, and imported through `ImportService`.
- Documents: the final support matrix, excluding HTML and non-existent CipherTalk TXT.

- [ ] **Step 1: Commit current-writer textual fixtures with provenance**

Copy the smallest current structures already verified against the upstream writers. `README.md` must contain this exact provenance table:

```markdown
| Fixture | Upstream commit | Writer source |
| --- | --- | --- |
| weflow-standard.json / weflow-arkme.json | 6f8e7e89f9b1 | electron/services/export/formatters/JsonFormatter.ts |
| chatlab-current.jsonl | 6f8e7e89f9b1 | electron/services/export/formatters/ChatLabFormatter.ts |
| weflow-current.csv | 6f8e7e89f9b1 | electron/services/export/formatters/WeCloneFormatter.ts |
| weflow-current.md | 6f8e7e89f9b1 | electron/services/export/formatters/MarkdownFormatter.ts |
| weflow-current.txt | 6f8e7e89f9b1 | electron/services/export/formatters/TxtFormatter.ts |
| weflow-current.sql | 6f8e7e89f9b1 | electron/services/export/formatters/SqlFormatter.ts |
| ciphertalk-detailed.json | 6b886e682472 | electron/services/exportService.ts |
| ciphertalk-current.sql | 6b886e682472 | electron/services/exportService.ts |
| qq-single.json | 888b51fab652 | qq-chat-export-core/src/json_exporter.rs |
| qq-current.txt | 888b51fab652 | qq-chat-export-core/src/text_exporter.rs |
```

Also document that XLSX tests use `XlsxTestFile` to reproduce the exact writer rows because binary fixtures cannot be meaningfully reviewed as diffs. Add this project item:

```xml
<ItemGroup>
  <None Include="Fixtures\CurrentExports\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write a failing registry-to-fixture compatibility test**

```csharp
[Theory]
[InlineData("weflow-standard.json", "wechat", "你好")]
[InlineData("weflow-arkme.json", "wechat", "你好")]
[InlineData("ciphertalk-detailed.json", "wechat", "你好")]
[InlineData("chatlab-current.jsonl", "wechat", "你好")]
[InlineData("qq-single.json", "qq", "你好")]
[InlineData("weflow-current.csv", "wechat", "你好")]
[InlineData("weflow-current.md", "wechat", "你好")]
[InlineData("weflow-current.txt", "wechat", "你好")]
[InlineData("qq-current.txt", "qq", "你好")]
[InlineData("weflow-current.sql", "wechat", "你好")]
[InlineData("ciphertalk-current.sql", "wechat", "你好")]
public void CurrentFixture_HasExactlyOneAdapterAndOneExpectedMessage(
    string name,
    string platform,
    string expectedContent)
{
    var path = Fixture(name);
    var matches = ExportFormats.Default.Where(format => format.Matches(path)).ToList();
    var format = Assert.Single(matches);
    Assert.Equal(platform, format.Platform);
    using var export = format.Open(path);
    var message = Assert.Single(export.EnumerateMessages());
    Assert.Contains(expectedContent, message.Content);
    Assert.True(message.TimestampMs > 0);
    Assert.False(string.IsNullOrWhiteSpace(message.SenderNativeId));
}
```

The fixture helper is:

```csharp
private static string Fixture(string name) => Path.Combine(
    AppContext.BaseDirectory, "Fixtures", "CurrentExports", name);
```

- [ ] **Step 3: Run the fixture test and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "CurrentFixture_"
```

Expected: at least one fixture is absent or one corrected adapter is not yet uniquely matched until all preceding tasks are complete.

- [ ] **Step 4: Add mixed-directory discovery and end-to-end import tests**

Build a temporary directory containing copies of all textual fixtures plus generated WeFlow/CipherTalk/QQ XLSX files. Add one HTML file and unrelated TXT/SQL/XLSX files. Assert:

```csharp
var discovered = ImportDiscovery.Discover(new[] { root });
Assert.DoesNotContain(discovered, item => item.FilePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(discovered, item => Path.GetFileName(item.FilePath).StartsWith("unrelated-", StringComparison.Ordinal));
Assert.All(discovered, item => Assert.Null(item.Error));
Assert.All(discovered, item => Assert.Contains(item.Platform, new[] { "wechat", "qq" }));
```

Then import the directory:

```csharp
var service = new ImportService(_archive.Db, _mediaDir);
var result = service.Run(new[] { root });
Assert.Equal(discovered.Count, result.FilesImported);
Assert.Equal(discovered.Count, result.Added);

using var connection = _archive.Open();
Assert.Equal(discovered.Count, Scalar(connection, "SELECT COUNT(*) FROM messages"));
Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM conversations WHERE platform NOT IN ('wechat','qq')"));
```

Give every fixture a unique conversation ID/path and a unique timestamp/content so deduplication cannot merge them. Add one WeFlow layout-A image and assert the imported attachment has `is_available = 1`.

- [ ] **Step 5: Run compatibility and import-service regressions**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --filter "CurrentFixture_|CurrentFormats_EndToEnd|ImportDiscoveryTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
```

Expected: PASS with every fixture matched by exactly one adapter.

- [ ] **Step 6: Update the format specification to match the implementation**

In `docs/EXPORT_FORMATS_SPEC.md`:

- mark WeFlow CSV/Markdown/TXT/SQL/Excel supported according to the final tests;
- mark CipherTalk SQL/Excel supported and TXT not applicable;
- mark QQ TXT/Excel supported;
- state that HTML is intentionally excluded as a browser presentation artifact;
- list discovery extensions as `.json .jsonl .csv .md .txt .sql .xlsx`;
- replace the layout-A conflict section with the exact one-level whitelist and security conditions;
- retain upstream commit baselines and writer-source references.

Do not claim fields the upstream transport does not contain. Explicitly document path-derived conversation IDs and incoming fallback for directionless TXT/Excel.

- [ ] **Step 7: Replace the README support table**

Use source-specific rows only:

```markdown
| Platform | Exporter | Supported import formats |
| --- | --- | --- |
| WeChat | WeFlow | Standard/ArkMe/ChatLab JSON, ChatLab JSONL, WeClone CSV, Markdown, TXT, PostgreSQL SQL, Excel |
| WeChat | CipherTalk | Detailed/ChatLab JSON, ChatLab JSONL, PostgreSQL SQL, Excel |
| QQ | QQ Chat Exporter | Single JSON, chunked JSONL, TXT, Excel |
```

Add one sentence: HTML exports are intentionally not imported because they are browser presentation artifacts rather than stable data interchange formats. Remove the “通用导出” and “通用网页” rows.

- [ ] **Step 8: Run documentation consistency and complete solution verification**

Invoke `superpowers:verification-before-completion`, then run fresh commands:

```powershell
rg -n "ChatHtmlExportFormat|HtmlDataExtractor|平台.*html|通用网页|is_sender,talker,content" src tests README.md docs/EXPORT_FORMATS_SPEC.md
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj
dotnet test tests/ChatArchive.App.Tests/ChatArchive.App.Tests.csproj
dotnet test ChatArchive.sln
git diff --check
```

Expected:

- `rg` returns no stale production/test/support claims; historical explanation in the audited spec is allowed only when explicitly labeled removed;
- core, app, and solution tests all pass;
- `git diff --check` prints nothing.

- [ ] **Step 9: Request code review and resolve findings**

Invoke `superpowers:requesting-code-review` against the design, this plan, and the complete diff. Fix every correctness/safety/spec finding through a new RED/GREEN cycle, then rerun Step 8.

- [ ] **Step 10: Commit fixtures, integration tests, and docs**

```powershell
git add tests/ChatArchive.Core.Tests/Fixtures/CurrentExports tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj tests/ChatArchive.Core.Tests/CurrentExportCompatibilityTests.cs tests/ChatArchive.Core.Tests/ImportServiceTests.cs docs/EXPORT_FORMATS_SPEC.md README.md
git commit -m "test(import): verify current inputapp export compatibility"
```

---

## Plan Completion Checklist

- [ ] Every design requirement maps to a task above.
- [ ] Every production change has a test that was observed failing first.
- [ ] No registered generic text/SQL adapter remains.
- [ ] HTML is absent from discovery, registration, code, and support claims.
- [ ] All three Excel formats use the same safe OpenXML reader and no new package.
- [ ] Parent media traversal is limited to the exact WeFlow layout-A whitelist.
- [ ] All current fixtures match exactly one adapter.
- [ ] Core, app, and complete solution tests pass from fresh runs.
