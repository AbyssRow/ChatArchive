# Import Reliability and Version Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make imports stream large JSON safely, reject unverified exporter versions, isolate per-file failures, recover missing media, report logical missing media consistently, display QQ numbers, and keep local chat samples out of Git.

**Architecture:** Preserve schema version 1 and the single-account `wechat-default` identity. Add an internal forward-only JSON token reader, adapt the existing format/parser boundary to enumerate one message at a time, and move all source-file work inside a per-file result boundary. Centralize media existence checks around `MediaLocator` and represent pathless media as ordinary unavailable attachment rows.

**Tech Stack:** C# 14 / .NET 10, `System.Text.Json`, Microsoft.Data.Sqlite, WinUI 3, xUnit v3.

**Spec:** `docs/superpowers/specs/2026-08-23-import-reliability-design.md`

## Global Constraints

- Keep SQLite `schema_version=1`; no DDL or data migration.
- Keep WeFlow `account_id="wechat-default"`; this application stores one user's archive.
- Allow only QQ exporter version `4` and WeFlow exporter version `1.0.3`.
- Never open `E:\ChatArchive\chat_archive.db` from tests or diagnostics.
- Never commit files under repository-root `input\`.
- Preserve existing message payload hashes, semantic hashes, revision behavior, and repository public paging signatures.
- Every production behavior change follows a demonstrated RED → GREEN test cycle.

---

### Task 1: Forward-only nested JSON reader

**Files:**
- Create: `src/ChatArchive.Core/IO/ChunkedJsonReader.cs`
- Create: `tests/ChatArchive.Core.Tests/ChunkedJsonReaderTests.cs`

**Interfaces:**
- Produces: `internal static JsonObject ReadObjectProperty(string path, string propertyName, CancellationToken cancellationToken = default, int bufferSize = 16 * 1024)`
- Produces: `internal static IEnumerable<JsonObject> EnumerateObjectArray(string path, string propertyName, CancellationToken cancellationToken = default, int bufferSize = 16 * 1024)`
- Both methods throw `ImportFormatException` for malformed JSON, a missing required property, or the wrong JSON value type.

- [ ] **Step 1: Write failing tests for cross-buffer values and arbitrary root-property order**

Create a UTF-8 BOM fixture whose `messages` property comes before `session`, with a string and nested array much larger than a seven-byte buffer:

```csharp
[Fact]
public void Reads_object_and_array_across_tiny_buffers()
{
    var path = Write("stream.json", "\uFEFF" + """
        {"messages":[{"id":1,"content":"跨块😀\\\"文本","nested":[1,{"x":true}]}],
         "session":{"wxid":"wxid_peer","version":"meta-after-messages"}}
        """);

    var session = ChunkedJsonReader.ReadObjectProperty(path, "session", bufferSize: 7);
    var messages = ChunkedJsonReader.EnumerateObjectArray(path, "messages", bufferSize: 7).ToList();

    Assert.Equal("wxid_peer", ImportText.Clean(session["wxid"]));
    Assert.Single(messages);
    Assert.Equal("跨块😀\"文本", ImportText.Clean(messages[0]["content"]));
    Assert.True(messages[0]["nested"]![1]!["x"]!.GetValue<bool>());
}
```

Add separate assertions that a missing property, a non-object array member, and truncated JSON throw `ImportFormatException`.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ChunkedJsonReaderTests"
```

Expected: compile failure because `ChunkedJsonReader` does not exist.

- [ ] **Step 3: Implement a token stream over `Utf8JsonReader`**

Implement a private disposable token source that owns a sequential `FileStream`, byte buffer, `JsonReaderState`, unconsumed-byte window, BOM handling, and cancellation token. Each `ReadToken` call must:

```csharp
cancellationToken.ThrowIfCancellationRequested();
var reader = new Utf8JsonReader(remainingBytes, isFinalBlock, state);
if (reader.Read())
{
    var token = CopyToken(reader); // copy strings/scalars before the span is reused
    consumed += checked((int)reader.BytesConsumed);
    state = reader.CurrentState;
    return token;
}
```

When `Read()` returns false, preserve the unconsumed suffix, grow the buffer only when one token exceeds it, refill, and retry. Convert scalar tokens to independent `JsonValue` instances; recursively assemble only the selected object or current array element. Unknown root properties are skipped by depth without materializing their values. Catch `JsonException`, `DecoderFallbackException`, and premature EOF and wrap them as `ImportFormatException(path, ...)` while allowing `OperationCanceledException` through unchanged.

- [ ] **Step 4: Add and run a cancellation test**

```csharp
[Fact]
public void Array_enumeration_observes_cancellation_between_items()
{
    var path = Write("cancel.json", """{"messages":[{"id":1},{"id":2}]}""");
    using var cts = new CancellationTokenSource();
    using var iterator = ChunkedJsonReader
        .EnumerateObjectArray(path, "messages", cts.Token, bufferSize: 5)
        .GetEnumerator();

    Assert.True(iterator.MoveNext());
    cts.Cancel();
    Assert.Throws<OperationCanceledException>(() => iterator.MoveNext());
}
```

Run the focused test command again. Expected: all `ChunkedJsonReaderTests` pass.

- [ ] **Step 5: Run all Core tests and commit**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore
git add -- src/ChatArchive.Core/IO/ChunkedJsonReader.cs tests/ChatArchive.Core.Tests/ChunkedJsonReaderTests.cs
git commit -m "feat(import): stream nested JSON values"
```

---

### Task 2: Streaming export adapters and exact exporter-version gate

**Files:**
- Modify: `src/ChatArchive.Core/Importing/IChatExportFormat.cs`
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs`
- Modify: `src/ChatArchive.Core/Importing/QqParser.cs`
- Modify: `src/ChatArchive.Core/Importing/WeFlowParser.cs`
- Modify: `tests/ChatArchive.Core.Tests/Fixtures.cs`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs`

**Interfaces:**
- Changes: `IChatExportFormat.Open(string filePath, CancellationToken cancellationToken = default)`
- Changes: `ExportFile.EnumerateMessages(CancellationToken cancellationToken = default)`
- `ExportFile` owns only `ParsedConversation` plus `Func<CancellationToken, IEnumerable<ParsedMessage>>`; `Dispose()` remains a compatibility no-op.
- QQ accepts `QQChatExporter.version` equal to numeric/string `4`.
- WeFlow accepts `weflow.version` equal to string `1.0.3`.

- [ ] **Step 1: Update fixtures and write failing version tests**

Change WeFlow fixtures from `"weflow": true` to:

```json
"weflow": {"version": "1.0.3"}
```

Add tests:

```csharp
[Theory]
[InlineData("5")]
[InlineData("missing")]
public void Qq_rejects_unverified_export_versions(string version)
{
    var metadata = version == "missing" ? "{}" : $$"""{"version":{{version}}}""";
    var path = WriteJson("qq-version.json", $$"""
        {"QQChatExporter":{{metadata}},"chatInfo":{"selfUin":"1","peerUid":"p","name":"n"},"messages":[]}
        """);

    var error = Assert.Throws<ImportFormatException>(() => new QqExportFormat().Open(path));
    Assert.Contains("支持版本 4", error.Message);
}

[Theory]
[InlineData("1.0.4")]
[InlineData("")]
public void Weflow_rejects_unverified_export_versions(string version)
{
    var metadata = version.Length == 0 ? "{}" : $$"""{"version":"{{version}}"}""";
    var path = WriteJson("wx-version.json", $$"""
        {"weflow":{{metadata}},"session":{"wxid":"p","type":"私聊"},"messages":[]}
        """);

    var error = Assert.Throws<ImportFormatException>(() => new WeFlowExportFormat().Open(path));
    Assert.Contains("支持版本 1.0.3", error.Message);
}
```

Also add positive tests for QQ numeric `4`, QQ string `"4"`, and WeFlow `1.0.3`.

- [ ] **Step 2: Run parser tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ParserTests"
```

Expected: unsupported versions are currently accepted or fail with the wrong structural behavior.

- [ ] **Step 3: Refactor parser entry points without changing message semantics**

Extract overloads that consume `JsonObject` values while retaining existing `JsonDocument` public helpers for compatibility with focused parser tests:

```csharp
internal static ParsedConversation ReadConversation(JsonObject chat, string filePath);
internal static IEnumerable<ParsedMessage> IterateMessages(
    IEnumerable<JsonObject> messages,
    ParsedConversation conversation,
    string documentPath);
```

For WeFlow, split conversation construction and self-sender inference:

```csharp
internal static ParsedConversation ReadConversation(JsonObject session, string filePath);
internal static string? InferSelfSender(
    IEnumerable<JsonObject> messages,
    ParsedConversation conversation,
    CancellationToken cancellationToken);
```

Keep all existing payload/semantic object construction byte-compatible. Do not add `localId` or exporter version to hashes.

- [ ] **Step 4: Implement version validation and streaming `ExportFile` factories**

In each `Open`, read and validate the metadata object before reading conversation/message data:

```csharp
private const string SupportedVersion = "1.0.3";
var metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "weflow", cancellationToken);
var version = ImportText.Clean(metadata["version"]);
if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
{
    throw new ImportFormatException(
        filePath,
        $"不支持的 WeFlow 导出版本 {Display(version)}；支持版本 {SupportedVersion}，请先更新 ChatArchive");
}
```

Use an equivalent check for QQ version `4`. WeFlow `Open` scans messages once for self-sender inference; its returned factory reopens and streams the messages on enumeration. Change WeFlow `Matches` to lightweight extension/head markers so discovery never builds a complete `JsonDocument`; full validation belongs to `Open`.

- [ ] **Step 5: Verify parser compatibility and commit**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ParserTests"
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore
git add -- src/ChatArchive.Core/Importing/IChatExportFormat.cs src/ChatArchive.Core/Importing/ExportFormats.cs src/ChatArchive.Core/Importing/QqParser.cs src/ChatArchive.Core/Importing/WeFlowParser.cs tests/ChatArchive.Core.Tests/Fixtures.cs tests/ChatArchive.Core.Tests/ParserTests.cs
git commit -m "feat(import): validate and stream export formats"
```

---

### Task 3: Per-file failure isolation and cancellation

**Files:**
- Modify: `src/ChatArchive.Core/Importing/ImportService.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportServiceTests.cs`

**Interfaces:**
- Changes: `ImportFile(string filePath, string platform, long runId, CancellationToken cancellationToken = default)`
- `ParserVersion` becomes `5`.
- File-level source/format failures return `FileImportResult(Status="failed")`; cancellation still throws.

- [ ] **Step 1: Write a failing mixed-batch isolation test**

Create `a-broken.json` containing both QQ discovery markers but truncated JSON, plus a valid `b-good.json`. Assert:

```csharp
var result = service.Run(new[] { exportRoot });

Assert.Equal(2, result.FilesFound);
Assert.Equal(1, result.FilesImported);
Assert.Equal(1, result.FilesFailed);
Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM import_files WHERE status='importing'"));
```

Because version validation precedes file-row creation, the broken file must not add an `import_files` row.

- [ ] **Step 2: Write a failing unsupported-version no-write test**

Import a QQ file whose metadata version is `5`, then assert `FilesFailed == 1` and counts in `conversations`, `messages`, and `import_files` remain zero. Add a valid file to the same directory and assert it still imports.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportServiceTests"
```

Expected: the malformed or unsupported file aborts the run, or leaves the wrong row state.

- [ ] **Step 4: Move the complete source-file flow under one result boundary**

Restructure `ImportFile` in this order:

```text
check cancellation
hash file and inspect completed-hash row
return skipped when the completed row is healthy
open format (includes exporter-version validation)
create/touch import_files row
begin transaction
stream messages with cancellation checks
mark completed and commit
```

Use nullable `importFileId`. The outer file catch returns a failed result and marks the row failed only when the row exists. A nested transaction catch rolls back before the outer catch. Catch `OperationCanceledException` separately, mark an existing file row `interrupted`, and rethrow so `Run` marks the run interrupted. Pass the token from `Run` into `ImportFile`, `Open`, and `EnumerateMessages`.

Clear `_mediaCache` once after acquiring the process lock at the beginning of every `Run`. During the 120-second lock wait, check the cancellation token between retries.

- [ ] **Step 5: Add cancellation coverage and verify GREEN**

Register a test format whose message iterator yields one message, cancels the supplied token, and attempts a second yield. Assert `Run` throws `OperationCanceledException`, the run/file rows are interrupted when present, and no partial message transaction commits.

Run focused and full Core tests. Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add -- src/ChatArchive.Core/Importing/ImportService.cs tests/ChatArchive.Core.Tests/ImportServiceTests.cs
git commit -m "fix(import): isolate failed files and honor cancellation"
```

---

### Task 4: Real media availability, recovery, and logical missing attachments

**Files:**
- Modify: `src/ChatArchive.Core/Importing/ImportService.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportServiceTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/SenderAndStatsTests.cs`

**Interfaces:**
- Produces: private `readonly record struct FileStats(long ParserVersion, long MissingMedia)` and `CompletedFileNeedsReimport(SqliteConnection connection, long fileId, FileStats stats)` using `MediaLocator`.
- Produces: private media-type normalization shared by logical attachment insertion.
- Existing DB schema remains unchanged.

- [ ] **Step 1: Write a failing managed-media recovery test**

Import a fixture with an existing source image, capture the managed path, delete only that generated managed file, and run the same `ImportService` instance again. Assert the second run imports rather than skips and recreates the managed file:

```csharp
File.Delete(managed);
var second = service.Run(new[] { exportRoot });

Assert.Equal(1, second.FilesImported);
Assert.True(File.Exists(managed));
Assert.Equal(0, second.MissingMedia);
```

- [ ] **Step 2: Write a failing irrecoverable-media downgrade test**

After the first import, delete both source and managed files. Reimport and assert the attachment changes to `is_available=0`, the result reports one missing attachment, and subsequent runs continue to consider the completed file recoverable rather than silently healthy.

- [ ] **Step 3: Write a failing pathless-media consistency test**

Use a WeFlow `1.0.3` fixture with one `动画表情` message whose content is `[动画表情]`. Assert after import:

```csharp
Assert.Equal(1, result.Attachments);
Assert.Equal(1, result.MissingMedia);
Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM attachments WHERE is_available=0"));
var stats = new StatsRepository(archive.Db).GetStats();
Assert.Equal(1, stats.MissingAttachments);
```

- [ ] **Step 4: Run focused tests and verify RED**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportServiceTests|FullyQualifiedName~SenderAndStatsTests"
```

Expected: completed hashes skip deleted media, and pathless media creates no attachment row.

- [ ] **Step 5: Implement completed-file health checks**

For each completed candidate, retain parser-version and stats checks, add SQL that detects media messages without attachments, then load available attachment candidates through observations:

```sql
SELECT DISTINCT a.id, mo.sha256, mo.managed_path, a.source_path
FROM message_observations obs
JOIN attachments a ON a.message_id = obs.message_id
LEFT JOIN media_objects mo ON mo.id = a.media_object_id
WHERE obs.import_file_id = @file AND a.is_available = 1
```

Resolve each row with a `MediaLocator` rooted at `_mediaDir`. Any unresolved row requires reimport.

- [ ] **Step 6: Make attachment upsert use physical availability**

Extend the existing attachment lookup to include media SHA, managed path, source path, and media-object ID. Apply these cases:

```text
new source exists        -> StoreMedia, fully update row, is_available=1
no new source, old resolves -> preserve metadata/media link; repair flag to 1 if needed
neither resolves         -> preserve recovery clues; set is_available=0; count missing
no existing row          -> insert parsed row with availability based on source
```

Before `UpsertAttachments`, if the parsed attachment list is empty and the normalized message/media type is one of `image`, `file`, `video`, `audio`, `voice`, `emoji`, or `sticker`, supply one ordinal-zero `ParsedAttachment` with null paths and empty metadata.

- [ ] **Step 7: Verify GREEN and commit**

```powershell
dotnet test tests/ChatArchive.Core.Tests/ChatArchive.Core.Tests.csproj --no-restore
git add -- src/ChatArchive.Core/Importing/ImportService.cs tests/ChatArchive.Core.Tests/ImportServiceTests.cs tests/ChatArchive.Core.Tests/SenderAndStatsTests.cs
git commit -m "fix(media): verify and recover attachment availability"
```

---

### Task 5: QQ number propagation and local-sample privacy

**Files:**
- Modify: `src/ChatArchive.Core/Models/SenderProfile.cs`
- Modify: `src/ChatArchive.Core/Repositories/SenderRepository.cs`
- Modify: `src/ChatArchive.App/ViewModels/ContactViewModel.cs`
- Modify: `tests/ChatArchive.Core.Tests/SenderAndStatsTests.cs`
- Create: `tests/ChatArchive.App.Tests/ContactViewModelTests.cs`
- Modify: `.gitignore`

**Interfaces:**
- Changes: `SenderProfile` gains `string? QQNumber` immediately after `NativeId`.
- Contact identity for QQ is `QQ {QQNumber ?? NativeId}`; WeChat remains `微信 {NativeId}`.

- [ ] **Step 1: Strengthen the currently ineffective Core test and verify RED**

Extend `SenderProfile_qq_number_from_payload`:

```csharp
var profile = new SenderRepository(_archive.Db).GetSender(sender);
Assert.NotNull(profile);
Assert.Equal("123456789", profile!.QQNumber);
```

Run that one test. Expected: compile failure because `QQNumber` does not exist.

- [ ] **Step 2: Add an App-level identity test and verify RED**

Create a temporary schema-backed archive, insert a QQ sender/message whose raw sender payload contains `uin`, construct `ContactViewModel`, call `LoadAsync`, and assert:

```csharp
Assert.True(await viewModel.LoadAsync(senderId));
Assert.Equal("QQ 123456789", viewModel.IdentityLine);
```

Expected: current UI displays the internal native UID.

- [ ] **Step 3: Propagate and display the QQ number**

Add the model field, pass the already-computed `qqNumber` from `SenderRepository.GetSender`, and use it in `ContactViewModel`. Do not change WeChat identity formatting.

- [ ] **Step 4: Ignore local input and verify the rule**

Add this root-scoped entry to `.gitignore`:

```gitignore
/input/
```

Verify without staging user data:

```powershell
git check-ignore -v input/WX
git status --short input
```

Expected: `git check-ignore` identifies `.gitignore`; `git status --short input` prints nothing.

- [ ] **Step 5: Run Core/App tests and commit exact paths**

```powershell
dotnet test ChatArchive.sln --no-restore
git add -- .gitignore src/ChatArchive.Core/Models/SenderProfile.cs src/ChatArchive.Core/Repositories/SenderRepository.cs src/ChatArchive.App/ViewModels/ContactViewModel.cs tests/ChatArchive.Core.Tests/SenderAndStatsTests.cs tests/ChatArchive.App.Tests/ContactViewModelTests.cs
git commit -m "fix(contacts): show QQ numbers and ignore local archives"
```

---

### Task 6: End-to-end verification and review

**Files:**
- Modify only files required by failures directly caused by Tasks 1-5.

**Interfaces:**
- Consumes all prior task outputs.
- Produces a Release build/test result, dependency audit, clean diff check, and review summary.

- [ ] **Step 1: Run formatting and whitespace checks**

```powershell
dotnet format ChatArchive.sln --no-restore --verify-no-changes
git diff --check
```

Expected: both commands exit 0. If formatting reports only files changed in Tasks 1-5, run `dotnet format ChatArchive.sln --no-restore`, inspect the mechanical diff, and rerun verification.

- [ ] **Step 2: Run Release build and all tests**

```powershell
dotnet test ChatArchive.sln -c Release --no-restore --verbosity minimal
```

Expected: every Core and App test passes with zero warnings/errors.

- [ ] **Step 3: Audit dependencies**

```powershell
dotnet package list --project ChatArchive.sln --vulnerable --include-transitive --no-restore
```

Expected: no project reports a vulnerable package.

- [ ] **Step 4: Perform a read-only structural check against `input\WX`**

Confirm only that both files still advertise WeFlow `1.0.3`, contain 125 total messages, and remain ignored. Do not print message text, sender names, or file contents. Do not import into the real database.

- [ ] **Step 5: Review the complete change set**

```powershell
git status --short
git diff cd78c87..HEAD --stat
git diff cd78c87..HEAD -- src tests .gitignore
```

Check specifically for payload-hash changes, accidental schema edits, accidental `input\` staging, swallowed cancellation, and missing per-file status transitions.

- [ ] **Step 6: Commit any verification-only corrections and report**

If Step 1 required formatting corrections, stage only the exact affected source/test paths and commit:

```powershell
git commit -m "style: normalize import reliability changes"
```

Report test counts, dependency-audit result, commits, files changed, the retained single-account decision, and any remaining limitation. Do not claim the fixes complete without the fresh outputs from Steps 1-5.
