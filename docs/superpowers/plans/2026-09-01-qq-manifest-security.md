# QQ Chunk Manifest Security Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 QQ 分块 manifest 成为解析与复合摘要共同使用的可信文件边界，并拒绝路径逃逸、损坏的显式清单和解析时可见的 reparse point。

**Architecture:** 新增一个内部 `QqChunkManifest` resolver，先验证 manifest 本身，再严格解释 `chunked.chunks` 或显式进入 legacy 扫描。`QqChunkedExportFormat` 与 `FileHashing` 只消费 resolver 返回的同一有序列表；共享路径检查从 `ImportText` 暴露窄接口，媒体现有 fallback 行为不变。

**Tech Stack:** C# 13、.NET 10、`System.Text.Json`、Win32/.NET `FileAttributes.ReparsePoint`、xUnit.net v3 in-process runner。

**Spec:** `docs/superpowers/specs/2026-08-31-chatarchive-safety-usability-design.md`

## Global Constraints

- 不修改 SQLite schema、消息哈希算法结构、消息级去重语义或现有脱敏 fixture。
- strict 模式保持 `chunks` 数组顺序；不得扫描、排序或补充未声明 `.jsonl`。
- legacy 模式只在 manifest 根对象完全没有 `chunked` 属性时启用，只扫描根目录和直接 `chunks/`。
- 所有 manifest JSON、路径和文件读取错误转换为带 manifest 路径的 `ImportFormatException`；`OperationCanceledException` 原样传播。
- 不读取、修改或暂存未跟踪的 `inputapp/`。
- 不引入 token/行大小限制、平台句柄 API 或宣称消除检查到打开之间的 TOCTOU 窗口。
- 不修改 xUnit/MTP 包、测试项目属性或 runner 配置；先构建，再直接运行测试可执行文件。

## File Structure

- Create: `src/ChatArchive.Core/Importing/QqChunkManifest.cs` — manifest 验证、strict/legacy 判定、声明解析与稳定文件顺序。
- Modify: `src/ChatArchive.Core/Importing/ImportText.cs` — 内部安全文件/目录 helper 与带 inner exception 的格式异常。
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs` — QQ 分块适配器消费 resolver，并包装延迟读取 IO。
- Modify: `src/ChatArchive.Core/IO/FileHashing.cs` — 复合摘要消费 resolver，移除独立扫描/排序。
- Create: `tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs` — resolver 字段、路径、顺序与 reparse 测试。
- Modify: `tests/ChatArchive.Core.Tests/FileHashingTests.cs` — strict/legacy 摘要语义。
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs` — 适配器权威列表与延迟删除。
- Modify: `tests/ChatArchive.Core.Tests/ImportServiceTests.cs` — strict 空清单与批次失败隔离。
- Verify only: `tests/ChatArchive.Core.Tests/MediaLocatorTests.cs` and `CurrentExportCompatibilityTests.cs`.

---

### Task 1: Expose shared safe-path primitives without changing media fallback

**Files:**
- Modify: `src/ChatArchive.Core/Importing/ImportText.cs:7-16,193-294,313-400,534-548`
- Create: `tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs`
- Verify: `tests/ChatArchive.Core.Tests/MediaLocatorTests.cs`

**Interfaces:**
- Produces: `ImportText.ResolveExistingRegularFileUnderRoot(string, string)`, `ImportText.ResolveExistingDirectoryUnderRoot(string, string)`, and `ImportFormatException(string, string, Exception)`.
- Consumes: existing `SafeExportPath`, path-attribute probing, and `SafeResolveMedia` fallback behavior.

- [ ] **Step 1: Add failing tests for shared targets and inner exceptions**

Create the test class with a disposable directory, then add:

```csharp
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class QqChunkManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-qq-manifest-{Guid.NewGuid():N}");

    public QqChunkManifestTests() => Directory.CreateDirectory(_root);

    private string WriteAt(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteManifest(string chunkedJson) => WriteAt(
        Path.Combine(_root, "manifest.json"),
        $"{{\"chunked\":{chunkedJson}}}");

    [Fact]
    public void ResolveExistingTargets_distinguishes_regular_files_and_directories()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "a.jsonl");
        File.WriteAllText(file, "{}\n");

        Assert.Equal(
            file,
            ImportText.ResolveExistingRegularFileUnderRoot(_root, "nested/a.jsonl"));
        Assert.Null(ImportText.ResolveExistingRegularFileUnderRoot(_root, "nested"));
        Assert.Equal(
            nested,
            ImportText.ResolveExistingDirectoryUnderRoot(_root, "nested"));
        Assert.Null(ImportText.ResolveExistingDirectoryUnderRoot(_root, "nested/a.jsonl"));
        Assert.Null(ImportText.ResolveExistingRegularFileUnderRoot(_root, "../outside.jsonl"));
    }

    [Fact]
    public void ImportFormatException_preserves_inner_exception()
    {
        var inner = new IOException("disk error");

        var error = new ImportFormatException("manifest.json", "读取失败", inner);

        Assert.Same(inner, error.InnerException);
        Assert.Equal("manifest.json", error.FilePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
```

- [ ] **Step 2: Build to verify the new interfaces are absent**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
```

Expected: compilation fails for the two missing `ImportText` methods and missing three-argument `ImportFormatException` constructor.

- [ ] **Step 3: Add the exception constructor and safe target enum**

Add:

```csharp
public ImportFormatException(
    string filePath,
    string message,
    Exception innerException)
    : base($"{filePath}: {message}", innerException)
{
    FilePath = filePath;
}
```

Replace the boolean target flag in the private component walker with:

```csharp
private enum SafePathTarget
{
    ExistingRegularFile,
    ExistingDirectory,
    PotentialRegularFile,
}
```

At the final path component, return exactly:

```csharp
return target switch
{
    SafePathTarget.ExistingRegularFile => !attributes.HasFlag(FileAttributes.Directory),
    SafePathTarget.ExistingDirectory => attributes.HasFlag(FileAttributes.Directory),
    SafePathTarget.PotentialRegularFile => !attributes.HasFlag(FileAttributes.Directory),
    _ => false,
};
```

If a component does not exist, return `target == SafePathTarget.PotentialRegularFile`; existing intermediate components must remain ordinary non-reparse directories.

- [ ] **Step 4: Add the two narrow shared resolvers**

Keep the private `out bool unsafeExistingCandidate` overload for media fallback, and add:

```csharp
internal static string? ResolveExistingRegularFileUnderRoot(
    string root,
    string declaredRelativePath)
{
    var candidate = SafeExportPath(root, declaredRelativePath);
    return candidate is not null
           && File.Exists(candidate)
           && HasSafePathComponents(root, candidate, SafePathTarget.ExistingRegularFile)
        ? candidate
        : null;
}

internal static string? ResolveExistingDirectoryUnderRoot(
    string root,
    string declaredRelativePath)
{
    var candidate = SafeExportPath(root, declaredRelativePath);
    return candidate is not null
           && Directory.Exists(candidate)
           && HasSafePathComponents(root, candidate, SafePathTarget.ExistingDirectory)
        ? candidate
        : null;
}
```

Update existing private callers to use `ExistingRegularFile`; update only the final lexical fallback in `SafeResolveMedia` to use `PotentialRegularFile`.

- [ ] **Step 5: Preserve original exceptions in `ParseDocument`**

Use the new overload:

```csharp
catch (JsonException ex)
{
    throw new ImportFormatException(filePath, $"JSON 解析失败（{ex.Message}）", ex);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）", ex);
}
```

- [ ] **Step 6: Run focused and media regression tests**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -class 'ChatArchive.Core.Tests.QqChunkManifestTests' -class 'ChatArchive.Core.Tests.MediaLocatorTests' -noLogo -automated
```

Expected: new primitive tests and all existing media path/reparse/fallback tests pass.

- [ ] **Step 7: Commit shared path primitives**

```powershell
git add -- 'src/ChatArchive.Core/Importing/ImportText.cs' 'tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs'
git commit -m "refactor: share safe export path checks"
```

---

### Task 2: Resolve strict and legacy chunk sets with fail-closed shape checks

**Files:**
- Create: `src/ChatArchive.Core/Importing/QqChunkManifest.cs`
- Modify: `tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs`

**Interfaces:**
- Consumes: Task 1 safe file/directory resolvers and `ImportText.ParseDocument`.
- Produces: `internal static IReadOnlyList<string> QqChunkManifest.ResolveChunkFiles(string manifestPath, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Add failing authority, legacy, malformed-shape, and cancellation tests**

Add the following tests. They deliberately put valid sidecars beside malformed
or empty strict manifests so an accidental legacy fallback is observable:

```csharp
[Fact]
public void ResolveChunkFiles_AuthoritativeManifest_ReturnsOnlyDeclaredFilesInDeclarationOrder()
{
    var chunks = Directory.CreateDirectory(Path.Combine(_root, "chunks")).FullName;
    var b = WriteAt(Path.Combine(chunks, "b.jsonl"), "{\"id\":\"b\"}\n");
    var a = WriteAt(Path.Combine(chunks, "a.jsonl"), "{\"id\":\"a\"}\n");
    _ = WriteAt(Path.Combine(chunks, "old.jsonl"), "{}\n");
    _ = WriteAt(Path.Combine(_root, "old.jsonl"), "{}\n");
    var manifest = WriteManifest("""
        {"chunksDir":"chunks","chunks":[
          {"relativePath":"chunks/b.jsonl"},
          {"relativePath":"chunks/a.jsonl"}
        ]}
        """);

    Assert.Equal(new[] { b, a }, QqChunkManifest.ResolveChunkFiles(manifest));
}

[Fact]
public void ResolveChunkFiles_AuthoritativeEmptyChunks_ReturnsEmpty()
{
    _ = WriteAt(Path.Combine(_root, "sidecar.jsonl"), "{}\n");
    _ = WriteAt(Path.Combine(_root, "chunks", "sidecar.jsonl"), "{}\n");
    var manifest = WriteManifest("""{"chunks":[]}""");

    Assert.Empty(QqChunkManifest.ResolveChunkFiles(manifest));
}

[Fact]
public void ResolveChunkFiles_LegacyManifest_ScansOnlyConventionalLocationsInNaturalOrder()
{
    var chunk2 = WriteAt(Path.Combine(_root, "chunk2.jsonl"), "{}\n");
    var chunk10 = WriteAt(Path.Combine(_root, "chunks", "chunk10.jsonl"), "{}\n");
    _ = WriteAt(Path.Combine(_root, "chunks", "nested", "chunk1.jsonl"), "{}\n");
    var manifest = WriteAt(Path.Combine(_root, "manifest.json"), "{\"chatInfo\":{}}");

    Assert.Equal(
        new[] { chunk2, chunk10 },
        QqChunkManifest.ResolveChunkFiles(manifest));
}

[Theory]
[InlineData("null")]
[InlineData("\"text\"")]
[InlineData("0")]
[InlineData("[]")]
[InlineData("{}")]
[InlineData("{\"chunkz\":[]}")]
[InlineData("{\"chunks\":null}")]
[InlineData("{\"chunks\":{}}")]
public void ResolveChunkFiles_ExplicitChunkedShape_DoesNotFallBackToLegacyScan(
    string chunkedJson)
{
    _ = WriteAt(Path.Combine(_root, "sidecar.jsonl"), "{}\n");
    var manifest = WriteManifest(chunkedJson);

    var error = Assert.Throws<ImportFormatException>(
        () => QqChunkManifest.ResolveChunkFiles(manifest));

    Assert.Equal(manifest, error.FilePath);
}

[Theory]
[InlineData("{")]
[InlineData("[]")]
[InlineData("null")]
[InlineData("\"scalar\"")]
public void ResolveChunkFiles_InvalidJsonOrNonObjectRoot_ThrowsManifestScopedError(
    string json)
{
    var manifest = WriteAt(Path.Combine(_root, "manifest.json"), json);

    var error = Assert.Throws<ImportFormatException>(
        () => QqChunkManifest.ResolveChunkFiles(manifest));

    Assert.Equal(manifest, error.FilePath);
}

[Fact]
public void ResolveChunkFiles_PropagatesCancellationWithoutWrapping()
{
    var manifest = WriteAt(Path.Combine(_root, "manifest.json"), "{}");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    Assert.Throws<OperationCanceledException>(
        () => QqChunkManifest.ResolveChunkFiles(manifest, cancellation.Token));
}
```

- [ ] **Step 2: Build to verify the resolver is missing**

Run the Core test build from Task 1 Step 2.

Expected: compilation fails because `QqChunkManifest` does not exist.

- [ ] **Step 3: Add the resolver shell and validate manifest before JSON reads**

Create `QqChunkManifest.cs` with:

```csharp
using System.Text.Json;

namespace ChatArchive.Core.Importing;

internal static class QqChunkManifest
{
    internal static IReadOnlyList<string> ResolveChunkFiles(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fullManifest = Path.GetFullPath(manifestPath);
            var exportRoot = Path.GetDirectoryName(fullManifest)
                ?? throw InvalidManifest(manifestPath, "manifest 缺少父目录");
            var safeManifest = ImportText.ResolveExistingRegularFileUnderRoot(
                exportRoot,
                Path.GetFileName(fullManifest));
            if (!PathEquals(safeManifest, fullManifest))
            {
                throw InvalidManifest(
                    manifestPath,
                    "manifest 不存在、不是普通文件或包含重解析点");
            }

            using var document = ImportText.ParseDocument(fullManifest);
            cancellationToken.ThrowIfCancellationRequested();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidManifest(manifestPath, "JSON 根节点必须是对象");
            }

            return root.TryGetProperty("chunked", out var chunked)
                ? ResolveAuthoritativeChunks(manifestPath, exportRoot, chunked, cancellationToken)
                : ResolveLegacyChunks(manifestPath, exportRoot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
                                   or UnauthorizedAccessException or NotSupportedException)
        {
            throw InvalidManifest(manifestPath, $"清单读取失败（{ex.Message}）", innerException: ex);
        }
    }

    private static bool PathEquals(string? left, string right) =>
        left is not null && string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static ImportFormatException InvalidManifest(
        string manifestPath,
        string reason,
        string? declaredPath = null,
        Exception? innerException = null)
    {
        var message = declaredPath is null
            ? reason
            : $"{reason}（声明路径：{declaredPath}）";
        return innerException is null
            ? new ImportFormatException(manifestPath, message)
            : new ImportFormatException(manifestPath, message, innerException);
    }
}
```

- [ ] **Step 4: Implement strict presence/type checks and legacy scanning**

Use these exact entry conditions:

```csharp
private static IReadOnlyList<string> ResolveAuthoritativeChunks(
    string manifestPath,
    string exportRoot,
    JsonElement chunked,
    CancellationToken cancellationToken)
{
    if (chunked.ValueKind != JsonValueKind.Object)
    {
        throw InvalidManifest(manifestPath, "chunked 必须是对象");
    }
    if (!chunked.TryGetProperty("chunks", out var chunks)
        || chunks.ValueKind != JsonValueKind.Array)
    {
        throw InvalidManifest(manifestPath, "chunked.chunks 必须是数组");
    }

    var result = new List<string>();
    var seen = new HashSet<string>(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);
    string? validatedChunksDir = null;
    string? explicitChunksDir = null;
    if (chunked.TryGetProperty("chunksDir", out var chunksDirElement))
    {
        if (chunksDirElement.ValueKind != JsonValueKind.String)
        {
            throw InvalidManifest(manifestPath, "chunked.chunksDir 必须是字符串");
        }
        explicitChunksDir = chunksDirElement.GetString();
        validatedChunksDir = ValidateChunksDirectory(
            manifestPath,
            exportRoot,
            explicitChunksDir ?? string.Empty);
    }

    var index = 0;
    foreach (var entry in chunks.EnumerateArray())
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveChunkDeclaration(
            manifestPath,
            exportRoot,
            entry,
            index++,
            explicitChunksDir,
            ref validatedChunksDir);
        if (!seen.Add(path))
        {
            var relative = Path.GetRelativePath(exportRoot, path).Replace('\\', '/');
            throw InvalidManifest(manifestPath, "chunks 含重复的规范路径", relative);
        }
        result.Add(path);
    }
    return result;
}
```

For legacy mode, move the existing `NaturalStringComparer` from `QqChunkedExportFormat` into this class without changing its numeric comparison algorithm, then implement:

```csharp
private static IReadOnlyList<string> ResolveLegacyChunks(
    string manifestPath,
    string exportRoot,
    CancellationToken cancellationToken)
{
    var candidates = new List<string>();
    var chunksPath = Path.Combine(exportRoot, "chunks");
    if (Path.Exists(chunksPath))
    {
        var safeChunks = ImportText.ResolveExistingDirectoryUnderRoot(exportRoot, "chunks");
        if (safeChunks is null)
        {
            throw InvalidManifest(
                manifestPath,
                "legacy chunks 目录不是普通目录或包含重解析点",
                "chunks");
        }
        candidates.AddRange(Directory.GetFiles(
            safeChunks,
            "*.jsonl",
            SearchOption.TopDirectoryOnly));
    }
    candidates.AddRange(Directory.GetFiles(
        exportRoot,
        "*.jsonl",
        SearchOption.TopDirectoryOnly));

    var comparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    var validated = new List<string>();
    foreach (var candidate in candidates)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = Path.GetRelativePath(exportRoot, candidate).Replace('\\', '/');
        var safe = ImportText.ResolveExistingRegularFileUnderRoot(exportRoot, relative);
        if (safe is null)
        {
            throw InvalidManifest(
                manifestPath,
                "legacy 分块不是普通文件或包含重解析点",
                relative);
        }
        if (!validated.Contains(safe, comparer))
        {
            validated.Add(safe);
        }
    }

    return validated
        .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
        .ToList();
}
```

- [ ] **Step 5: Run resolver shape/order tests**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -class 'ChatArchive.Core.Tests.QqChunkManifestTests' -noLogo -automated
```

Expected: authority, empty, legacy, malformed shape, invalid root, and cancellation tests pass.

- [ ] **Step 6: Commit strict/legacy mode selection**

```powershell
git add -- 'src/ChatArchive.Core/Importing/QqChunkManifest.cs' 'src/ChatArchive.Core/Importing/ExportFormats.cs' 'tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs'
git commit -m "feat: resolve authoritative QQ chunk manifests"
```

---

### Task 3: Validate chunksDir, fileName, relativePath, duplicates, and reparse points

**Files:**
- Modify: `src/ChatArchive.Core/Importing/QqChunkManifest.cs`
- Modify: `tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs`

**Interfaces:**
- Consumes: Task 1 safe file/directory resolvers and Task 2 strict array traversal.
- Produces: fail-closed declaration parsing; `relativePath` wins when present, `fileName` is basename-only fallback.

- [ ] **Step 1: Add the complete field/path test matrix**

Add tests with these names:

```csharp
[Fact] public void ResolveChunkFiles_FileNameFallback_UsesDefaultChunksDirectory()
[Fact] public void ResolveChunkFiles_FileNameFallback_UsesCustomChunksDirectory()
[Fact] public void ResolveChunkFiles_RelativePath_DoesNotRequireImplicitChunksDirectory()
[Fact] public void ResolveChunkFiles_ExplicitChunksDir_IsValidatedEvenForRelativePathEntries()
[Fact] public void ResolveChunkFiles_RelativePath_TakesPrecedenceOverFileName()
[Theory] public void ResolveChunkFiles_RejectsUnsafeChunksDir(string declared)
[Theory] public void ResolveChunkFiles_RejectsInvalidFileNameFallback(string fileNameJson)
[Theory] public void ResolveChunkFiles_RejectsInvalidRelativePath(string relativePathJson)
[Fact] public void ResolveChunkFiles_RejectsMissingDeclaredFile()
[Fact] public void ResolveChunkFiles_RejectsDeclaredDirectory()
[Fact] public void ResolveChunkFiles_RejectsDuplicateCanonicalPaths()
[Fact] public void ResolveChunkFiles_RejectsManifestFileReparsePoint()
[Fact] public void ResolveChunkFiles_RejectsReparsePointAtEveryChunkPathComponent()
[Fact] public void ResolveChunkFiles_LegacyModeRejectsReparseChunksDirectoryAndFiles()
```

Use `[InlineData]` values from the approved spec: empty/whitespace/URI/drive/UNC/rooted/empty segment/`.`/`..` for `chunksDir`; empty/whitespace/`.`/`..`/slash/backslash/wrong extension/non-string for `fileName`; empty/null/non-string/URI/absolute/UNC/escaping/wrong extension for `relativePath`. Duplicate `chunks/a.jsonl` and `chunks\a.jsonl` must fail after canonicalization.

Use these concrete theory bodies (add `using System.Text.Json;`):

```csharp
[Theory]
[InlineData("")]
[InlineData("   ")]
[InlineData("https://example.test/chunks")]
[InlineData("C:\\outside")]
[InlineData("\\\\server\\share")]
[InlineData("/outside")]
[InlineData("chunks//nested")]
[InlineData(".")]
[InlineData("..")]
[InlineData("chunks/./nested")]
[InlineData("chunks/../other")]
public void ResolveChunkFiles_RejectsUnsafeChunksDir(string declared)
{
    var json = JsonSerializer.Serialize(declared);
    var manifest = WriteManifest($"{{\"chunksDir\":{json},\"chunks\":[]}}");

    Assert.Throws<ImportFormatException>(
        () => QqChunkManifest.ResolveChunkFiles(manifest));
}

[Theory]
[InlineData("null")]
[InlineData("123")]
[InlineData("\"\"")]
[InlineData("\"   \"")]
[InlineData("\".\"")]
[InlineData("\"..\"")]
[InlineData("\"sub/a.jsonl\"")]
[InlineData("\"sub\\\\a.jsonl\"")]
[InlineData("\"a.json\"")]
[InlineData("\"a.jsonl.tmp\"")]
public void ResolveChunkFiles_RejectsInvalidFileNameFallback(string fileNameJson)
{
    Directory.CreateDirectory(Path.Combine(_root, "chunks"));
    var manifest = WriteManifest($"{{\"chunks\":[{{\"fileName\":{fileNameJson}}}]}}");

    Assert.Throws<ImportFormatException>(
        () => QqChunkManifest.ResolveChunkFiles(manifest));
}

[Theory]
[InlineData("null")]
[InlineData("123")]
[InlineData("\"\"")]
[InlineData("\"https://example.test/a.jsonl\"")]
[InlineData("\"C:\\\\outside\\\\a.jsonl\"")]
[InlineData("\"\\\\\\\\server\\\\share\\\\a.jsonl\"")]
[InlineData("\"/outside/a.jsonl\"")]
[InlineData("\"../outside/a.jsonl\"")]
[InlineData("\"chunks/a.json\"")]
public void ResolveChunkFiles_RejectsInvalidRelativePath(string relativePathJson)
{
    var manifest = WriteManifest($"{{\"chunks\":[{{\"relativePath\":{relativePathJson}}}]}}");

    Assert.Throws<ImportFormatException>(
        () => QqChunkManifest.ResolveChunkFiles(manifest));
}
```

For link tests, create the outside target first, call `Directory.CreateSymbolicLink` or `File.CreateSymbolicLink`, assert the link has `FileAttributes.ReparsePoint`, and call `Assert.Skip("当前环境不允许创建符号链接")` on `UnauthorizedAccessException`, `IOException`, `PlatformNotSupportedException`, or `NotSupportedException`.

- [ ] **Step 2: Run the new tests against the incomplete resolver**

Run the `QqChunkManifestTests` command from Task 2 Step 5.

Expected: field fallback, malicious path, missing file, duplicate, and reparse cases fail until declaration validation is implemented.

- [ ] **Step 3: Implement exact chunksDir syntax and directory resolution**

Add:

```csharp
private static string ValidateChunksDirectory(
    string manifestPath,
    string exportRoot,
    string declaredChunksDir)
{
    if (string.IsNullOrWhiteSpace(declaredChunksDir)
        || IsRootedOrUriLike(declaredChunksDir))
    {
        throw InvalidManifest(manifestPath, "chunksDir 必须是普通相对目录", declaredChunksDir);
    }

    var normalized = declaredChunksDir.Replace('\\', '/');
    var segments = normalized.Split('/');
    if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                || segment is "." or ".."))
    {
        throw InvalidManifest(manifestPath, "chunksDir 含空段或点路径段", declaredChunksDir);
    }

    var resolved = ImportText.ResolveExistingDirectoryUnderRoot(exportRoot, normalized);
    if (resolved is null)
    {
        throw InvalidManifest(
            manifestPath,
            "chunksDir 不存在、越界、不是普通目录或包含重解析点",
            declaredChunksDir);
    }
    return Path.GetRelativePath(exportRoot, resolved).Replace('\\', '/');
}

private static bool IsRootedOrUriLike(string value) =>
    value[0] is '/' or '\\'
    || Path.IsPathRooted(value)
    || Uri.TryCreate(value, UriKind.Absolute, out _);
```

Explicit `chunksDir` is validated before array iteration, including `chunks: []`. Missing `chunksDir` is not validated unless a `fileName` fallback needs the default `chunks` directory.

- [ ] **Step 4: Implement entry precedence and regular-file resolution**

Implement `ResolveChunkDeclaration` with these branches:

```csharp
if (entry.ValueKind != JsonValueKind.Object)
{
    throw InvalidManifest(manifestPath, $"chunks[{index}] 必须是对象");
}

string declaredPath;
if (entry.TryGetProperty("relativePath", out var relativePath))
{
    if (relativePath.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(relativePath.GetString()))
    {
        throw InvalidManifest(manifestPath, $"chunks[{index}].relativePath 必须是非空字符串");
    }
    declaredPath = relativePath.GetString()!;
}
else
{
    if (!entry.TryGetProperty("fileName", out var fileNameElement)
        || fileNameElement.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(fileNameElement.GetString()))
    {
        throw InvalidManifest(manifestPath, $"chunks[{index}] 缺少 relativePath 或有效 fileName");
    }

    var fileName = fileNameElement.GetString()!;
    if (fileName is "." or ".."
        || fileName.Contains('/')
        || fileName.Contains('\\')
        || !string.Equals(Path.GetExtension(fileName), ".jsonl", StringComparison.OrdinalIgnoreCase))
    {
        throw InvalidManifest(manifestPath, $"chunks[{index}].fileName 必须是 .jsonl basename", fileName);
    }

    validatedChunksDir ??= ValidateChunksDirectory(
        manifestPath,
        exportRoot,
        explicitChunksDir ?? "chunks");
    declaredPath = $"{validatedChunksDir}/{fileName}";
}

if (IsRootedOrUriLike(declaredPath)
    || !string.Equals(Path.GetExtension(declaredPath), ".jsonl", StringComparison.OrdinalIgnoreCase))
{
    throw InvalidManifest(manifestPath, $"chunks[{index}] 路径必须是相对 .jsonl 文件", declaredPath);
}

var resolved = ImportText.ResolveExistingRegularFileUnderRoot(exportRoot, declaredPath);
if (resolved is null)
{
    throw InvalidManifest(
        manifestPath,
        $"chunks[{index}] 文件不存在、越界、不是普通文件或包含重解析点",
        declaredPath);
}
return resolved;
```

- [ ] **Step 5: Run field, path, link, and media regression tests**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -class 'ChatArchive.Core.Tests.QqChunkManifestTests' -class 'ChatArchive.Core.Tests.MediaLocatorTests' -noLogo -automated
```

Expected: all resolver cases pass; unsupported link creation is explicitly skipped; existing media path behavior remains green.

- [ ] **Step 6: Commit declaration hardening**

```powershell
git add -- 'src/ChatArchive.Core/Importing/QqChunkManifest.cs' 'tests/ChatArchive.Core.Tests/QqChunkManifestTests.cs'
git commit -m "fix: validate QQ chunk declarations"
```

---

### Task 4: Make the adapter and composite digest consume the same list

**Files:**
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs:136-224`
- Modify: `src/ChatArchive.Core/IO/FileHashing.cs:1-78`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs:400-445`
- Modify: `tests/ChatArchive.Core.Tests/FileHashingTests.cs:75-99`

**Interfaces:**
- Consumes: Task 3 `QqChunkManifest.ResolveChunkFiles` ordered paths.
- Produces: no second scan/sort in either consumer; manifest bytes plus the same ordered declared files form the composite digest.

- [ ] **Step 1: Add failing adapter and hashing integration tests**

Add these tests:

```csharp
// ParserTests
[Fact] public void QqChunkedExportFormat_AuthoritativeManifest_ImportsOnlyDeclaredChunksInManifestOrder()

// FileHashingTests
[Fact] public void ComputeImportDigest_AuthoritativeManifest_IgnoresUndeclaredChunks()
[Fact] public void ComputeImportDigest_AuthoritativeManifest_ChangesWhenDeclaredChunkChanges()
[Fact] public void ComputeImportDigest_AuthoritativeEmptyChunks_HashesOnlyManifestBytes()
[Fact] public void ComputeImportDigest_LegacyManifest_StillIncorporatesConventionChunks()
[Fact] public void QqChunkedOpenAndDigest_RejectManifestFileReparsePoint()
```

For the adapter test, declare `b.jsonl` then `a.jsonl`, put a valid undeclared `old.jsonl` beside them, enumerate, and assert native IDs are exactly `b`, `a`. For the strict digest test, compute once, modify root and `chunks/` sidecars, compute again and assert equal; then modify a declared file and assert not equal. For empty strict chunks, compare `ComputeImportDigest(manifest)` with `FileHashing.Sha256File(manifest)`.

- [ ] **Step 2: Run tests to expose independent scanning**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -class 'ChatArchive.Core.Tests.ParserTests' -class 'ChatArchive.Core.Tests.FileHashingTests' -noLogo -automated
```

Expected: strict parser imports the undeclared sidecar; undeclared changes alter the digest; declared array order is ignored; manifest-link calls follow the link.

- [ ] **Step 3: Resolve chunks before any manifest read in `Open`**

Make the first line after cancellation in `QqChunkedExportFormat.Open`:

```csharp
var chunkFiles = QqChunkManifest.ResolveChunkFiles(filePath, cancellationToken);
```

Delete lines that enumerate `manifestDir` and `chunks/`, and pass `chunkFiles` directly to `IterateChunkedMessages`. Keep metadata/exporter validation, conversation parsing, self sender selection, and export root unchanged. Move/remove the old nested `NaturalStringComparer` after Task 2 has made it private to the resolver.

- [ ] **Step 4: Replace hashing scan/sort with the resolver result**

Add `using ChatArchive.Core.Importing;`, then start `ComputeChunkedManifestDigest` with:

```csharp
cancellationToken.ThrowIfCancellationRequested();
var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
var chunkFiles = QqChunkManifest.ResolveChunkFiles(manifestPath, cancellationToken);
```

Keep the existing incremental SHA-256 of manifest raw bytes. Iterate `chunkFiles` exactly as returned:

```csharp
foreach (var chunkPath in chunkFiles)
{
    cancellationToken.ThrowIfCancellationRequested();
    var relPath = Path.GetRelativePath(manifestDir, chunkPath).Replace('\\', '/');
    try
    {
        var (chunkDigest, chunkSize) = HashFile(chunkPath, cancellationToken);
        var header = Encoding.UTF8.GetBytes(
            $"\nchunk:{relPath}:{chunkSize}:{chunkDigest}\n");
        hash.AppendData(header);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        throw new ImportFormatException(
            manifestPath,
            $"读取声明分块失败（{relPath}：{ex.Message}）",
            ex);
    }
}
```

Wrap manifest stream open/read IO with the same manifest-scoped exception constructor. Do not change `Sha256File`, `HashFile`, or non-manifest import hashing.

- [ ] **Step 5: Run adapter, hashing, legacy, and current-fixture tests**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -class 'ChatArchive.Core.Tests.QqChunkManifestTests' -class 'ChatArchive.Core.Tests.FileHashingTests' -class 'ChatArchive.Core.Tests.CurrentExportCompatibilityTests' -noLogo -automated
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -method 'ChatArchive.Core.Tests.ParserTests.QqChunkedExportFormat_AuthoritativeManifest_ImportsOnlyDeclaredChunksInManifestOrder' -method 'ChatArchive.Core.Tests.ParserTests.QqChunkedExportFormat_ParsesManifestAndChunks_Correctly' -noLogo -automated
```

Expected: strict sidecars are ignored, declared changes affect digest, legacy scan still works, and the current fixture imports its one expected message.

- [ ] **Step 6: Commit shared consumer wiring**

```powershell
git add -- 'src/ChatArchive.Core/Importing/ExportFormats.cs' 'src/ChatArchive.Core/IO/FileHashing.cs' 'tests/ChatArchive.Core.Tests/ParserTests.cs' 'tests/ChatArchive.Core.Tests/FileHashingTests.cs'
git commit -m "fix: share QQ chunk list for parse and hash"
```

---

### Task 5: Wrap delayed chunk deletion and enforce empty/batch import semantics

**Files:**
- Modify: `src/ChatArchive.Core/Importing/ExportFormats.cs:185-222`
- Modify: `tests/ChatArchive.Core.Tests/ParserTests.cs`
- Modify: `tests/ChatArchive.Core.Tests/ImportServiceTests.cs:130-195`

**Interfaces:**
- Consumes: resolver-validated paths and existing `ImportService` zero-message transaction behavior.
- Produces: delayed file-open/read failures as manifest-scoped `ImportFormatException`; strict empty lists never import sidecars.

- [ ] **Step 1: Add failing delayed deletion and import-service tests**

Add:

```csharp
// ParserTests
[Fact]
public void QqChunkedExportFormat_Enumeration_WhenDeclaredChunkDeletedAfterOpen_ThrowsManifestScopedError()
{
    var (manifest, chunk) = WriteStrictQqChunkedExport("chunks/a.jsonl", oneValidMessage: true);
    using var export = new QqChunkedExportFormat().Open(manifest);
    File.Delete(chunk);

    var error = Assert.Throws<ImportFormatException>(
        () => export.EnumerateMessages().ToList());

    Assert.Equal(manifest, error.FilePath);
    Assert.Contains("chunks/a.jsonl", error.Message, StringComparison.Ordinal);
    Assert.IsAssignableFrom<IOException>(error.InnerException);
}
```

Add this helper to `ParserTests` so the test does not depend on an external fixture:

```csharp
private (string Manifest, string Chunk) WriteStrictQqChunkedExport(
    string relativePath,
    bool oneValidMessage)
{
    var root = Path.Combine(_dir, $"strict-{Guid.NewGuid():N}");
    var chunk = Path.Combine(
        root,
        relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(chunk)!);
    File.WriteAllText(
        chunk,
        oneValidMessage
            ? "{\"id\":\"q1\",\"timestamp\":1700000000,\"sender\":{\"uid\":\"peer\",\"name\":\"成员\"},\"content\":{\"type\":\"text\",\"text\":\"消息\"}}\n"
            : string.Empty);
    var manifest = Path.Combine(root, "manifest.json");
    File.WriteAllText(manifest, $$"""
        {
          "metadata":{"name":"QQChatExporter","version":"0.2.0"},
          "chatInfo":{"selfUid":"self","peerUid":"group","name":"测试群","type":"group"},
          "chunked":{"chunks":[{"relativePath":"{{relativePath}}"}]}
        }
        """);
    return (manifest, chunk);
}
```

Add two `ImportServiceTests` using existing `ExportRoot`, `Fixtures.QqExport`, and SQL count helpers:

```csharp
[Fact]
public void QqChunked_EmptyAuthoritativeManifest_FailsImportEvenWhenSidecarContainsMessage()
{
    var root = ExportRoot();
    var chunks = Path.Combine(root, "chunks");
    Directory.CreateDirectory(chunks);
    File.WriteAllText(Path.Combine(root, "manifest.json"), """
        {
          "metadata":{"name":"QQChatExporter","version":"0.2.0"},
          "chatInfo":{"selfUid":"self","peerUid":"empty","name":"空清单","type":"group"},
          "chunked":{"chunks":[]}
        }
        """);
    File.WriteAllText(
        Path.Combine(chunks, "old.jsonl"),
        "{\"id\":\"sidecar\",\"timestamp\":1700000000,\"sender\":{\"uid\":\"peer\",\"name\":\"成员\"},\"content\":{\"type\":\"text\",\"text\":\"不应导入\"}}\n");

    var result = new ImportService(_archive.Db, _mediaDir).Run([root]);

    Assert.Equal(0, result.FilesImported);
    Assert.Equal(1, result.FilesFailed);
    Assert.Contains("没有有效消息", Assert.Single(result.Files).Error);
    using var connection = _archive.Open();
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
}

[Fact]
public void InvalidExplicitQqManifest_FailsOnlyThatFileAndDoesNotAbortBatch()
{
    var root = ExportRoot();
    var broken = Path.Combine(root, "broken");
    Directory.CreateDirectory(broken);
    File.WriteAllText(Path.Combine(broken, "manifest.json"), """
        {
          "metadata":{"name":"QQChatExporter","version":"0.2.0"},
          "chatInfo":{"selfUid":"self","peerUid":"broken","name":"损坏","type":"group"},
          "chunked":null
        }
        """);
    File.WriteAllText(Path.Combine(root, "good.json"), Fixtures.QqExport);

    var result = new ImportService(_archive.Db, _mediaDir).Run([root]);

    Assert.Equal(1, result.FilesImported);
    Assert.Equal(1, result.FilesFailed);
    Assert.Contains(result.Files, file => file.Status == "failed");
    Assert.Contains(result.Files, file => file.Status == "completed");
}
```

- [ ] **Step 2: Run the tests to expose raw lazy IO and sidecar import**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -method 'ChatArchive.Core.Tests.ParserTests.QqChunkedExportFormat_Enumeration_WhenDeclaredChunkDeletedAfterOpen_ThrowsManifestScopedError' -method 'ChatArchive.Core.Tests.ImportServiceTests.QqChunked_EmptyAuthoritativeManifest_FailsImportEvenWhenSidecarContainsMessage' -method 'ChatArchive.Core.Tests.ImportServiceTests.InvalidExplicitQqManifest_FailsOnlyThatFileAndDoesNotAbortBatch' -noLogo -automated
```

Expected before implementation: deletion throws raw `FileNotFoundException`; the old scanner imports the sidecar from `chunks: []`; malformed explicit `chunked` may fall back instead of recording one failed file.

- [ ] **Step 3: Wrap stream creation and line reads without buffering the chunk**

Pass `filePath` into `IterateChunkedMessages` as `manifestPath`. Add non-iterator helpers so `yield return` is never placed inside a `try/catch`:

```csharp
private static StreamReader OpenChunkReader(
    string manifestPath,
    string exportRoot,
    string chunkPath)
{
    var relative = Path.GetRelativePath(exportRoot, chunkPath).Replace('\\', '/');
    try
    {
        var stream = new FileStream(
            chunkPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return new StreamReader(stream, System.Text.Encoding.UTF8);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        throw new ImportFormatException(
            manifestPath,
            $"读取声明分块失败（{relative}：{ex.Message}）",
            ex);
    }
}

private static string? ReadChunkLine(
    StreamReader reader,
    string manifestPath,
    string exportRoot,
    string chunkPath)
{
    var relative = Path.GetRelativePath(exportRoot, chunkPath).Replace('\\', '/');
    try
    {
        return reader.ReadLine();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        throw new ImportFormatException(
            manifestPath,
            $"读取声明分块失败（{relative}：{ex.Message}）",
            ex);
    }
}
```

Inside the iterator use `using var reader = OpenChunkReader(...)` and `while ((line = ReadChunkLine(...)) is not null)`. Keep streaming, cancellation checks, `globalIndex`, parser calls, and message order unchanged.

- [ ] **Step 4: Run focused and full import-service tests**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -class 'ChatArchive.Core.Tests.ImportServiceTests' -noLogo -automated
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -method 'ChatArchive.Core.Tests.ParserTests.QqChunkedExportFormat_Enumeration_WhenDeclaredChunkDeletedAfterOpen_ThrowsManifestScopedError' -noLogo -automated
```

Expected: empty strict export is one failed import with no conversation, a bad manifest does not abort a good file, delayed deletion has a manifest-scoped error and IO inner exception, and all existing service tests pass.

- [ ] **Step 5: Commit delayed IO and service semantics**

```powershell
git add -- 'src/ChatArchive.Core/Importing/ExportFormats.cs' 'tests/ChatArchive.Core.Tests/ParserTests.cs' 'tests/ChatArchive.Core.Tests/ImportServiceTests.cs'
git commit -m "fix: fail closed on missing QQ chunks"
```

---

### Task 6: Verify the complete Core work package

**Files:**
- Verify only: all Core files and tests listed above.

**Interfaces:**
- Consumes: Tasks 1-5.
- Produces: Debug/Release build, non-zero test discovery, full Core suite, legacy/current compatibility, and a documented `dotnet test` host result.

- [ ] **Step 1: Build Core tests in Debug and Release**

```powershell
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo -c Debug
dotnet build 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo -c Release
```

Expected: both builds finish with 0 warnings and 0 errors.

- [ ] **Step 2: Verify non-zero discovery, then run both full Core suites**

```powershell
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -list tests -noLogo
& 'tests\ChatArchive.Core.Tests\bin\Debug\net10.0\ChatArchive.Core.Tests.exe' -noLogo -automated
& 'tests\ChatArchive.Core.Tests\bin\Release\net10.0\ChatArchive.Core.Tests.exe' -list tests -noLogo
& 'tests\ChatArchive.Core.Tests\bin\Release\net10.0\ChatArchive.Core.Tests.exe' -noLogo -automated
```

Expected: discovery is non-zero and both complete suites exit 0; record discovered/passed/skipped counts, including explicit link-test skips where the platform denies link creation.

- [ ] **Step 3: Record the known dotnet-test host result without project changes**

```powershell
dotnet test 'tests\ChatArchive.Core.Tests\ChatArchive.Core.Tests.csproj' --no-restore --nologo -c Debug
```

Record exit code and discovered count. If the known SDK/MTP zero-test behavior remains, do not edit `.csproj`, dependencies, runner properties, or generated entry points.

- [ ] **Step 4: Check diff hygiene and scope**

```powershell
git diff --check
git status --short
git diff --stat
```

Expected: no whitespace errors; only planned Core source/test files are changed or committed; `inputapp/` remains untracked and untouched.

The final report must state the TOCTOU boundary: checks reject reparse points visible during resolution and wrap deletion before open, but do not claim protection against an attacker concurrently replacing a checked path before the subsequent open.
