using ChatArchive.Core.Importing;
using System.Text.Json;
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
    public void ResolveChunkFiles_FileNameFallback_UsesDefaultChunksDirectory()
    {
        var chunk = WriteAt(Path.Combine(_root, "chunks", "a.jsonl"), "{}\n");
        var manifest = WriteManifest("""{"chunks":[{"fileName":"a.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_FileNameFallback_UsesCustomChunksDirectory()
    {
        var chunk = WriteAt(Path.Combine(_root, "custom", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunksDir":"custom","chunks":[{"fileName":"a.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RelativePath_DoesNotRequireImplicitChunksDirectory()
    {
        var chunk = WriteAt(Path.Combine(_root, "data", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"data/a.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_ExplicitChunksDir_IsValidatedEvenForRelativePathEntries()
    {
        _ = WriteAt(Path.Combine(_root, "data", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunksDir":"missing","chunks":[{"relativePath":"data/a.jsonl"}]}""");

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RelativePath_TakesPrecedenceOverFileName()
    {
        var chunk = WriteAt(Path.Combine(_root, "data", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"data/a.jsonl","fileName":"invalid/path.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

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

    [Fact]
    public void ResolveChunkFiles_RejectsMissingDeclaredFile()
    {
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"chunks/missing.jsonl"}]}""");

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsDeclaredDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "chunks", "a.jsonl"));
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"chunks/a.jsonl"}]}""");

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsDuplicateCanonicalPaths()
    {
        _ = WriteAt(Path.Combine(_root, "chunks", "a.jsonl"), "{}\n");
        var manifest = WriteManifest("""
            {"chunks":[
              {"relativePath":"chunks/a.jsonl"},
              {"relativePath":"chunks\\a.jsonl"}
            ]}
            """);

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsManifestFileReparsePoint()
    {
        var target = WriteAt(Path.Combine(_root, "targets", "manifest.json"),
            "{\"chunked\":{\"chunks\":[]}}");
        var exportRoot = Directory.CreateDirectory(Path.Combine(_root, "manifest-link")).FullName;
        var manifest = Path.Combine(exportRoot, "manifest.json");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(manifest, target));
        Assert.True(File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint));

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsReparsePointAtEveryChunkPathComponent()
    {
        var directoryTarget = Directory.CreateDirectory(
            Path.Combine(_root, "targets", "component-directory")).FullName;
        _ = WriteAt(Path.Combine(directoryTarget, "a.jsonl"), "{}\n");
        var fileTarget = WriteAt(
            Path.Combine(_root, "targets", "component-file.jsonl"), "{}\n");

        var topExport = Directory.CreateDirectory(Path.Combine(_root, "top-component")).FullName;
        var topLink = Path.Combine(topExport, "chunks");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(topLink, directoryTarget));
        Assert.True(File.GetAttributes(topLink).HasFlag(FileAttributes.ReparsePoint));
        var topManifest = WriteAt(
            Path.Combine(topExport, "manifest.json"),
            "{\"chunked\":{\"chunks\":[{\"relativePath\":\"chunks/a.jsonl\"}]}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(topManifest));

        var nestedExport = Directory.CreateDirectory(Path.Combine(_root, "nested-component")).FullName;
        Directory.CreateDirectory(Path.Combine(nestedExport, "chunks"));
        var nestedLink = Path.Combine(nestedExport, "chunks", "nested");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(nestedLink, directoryTarget));
        Assert.True(File.GetAttributes(nestedLink).HasFlag(FileAttributes.ReparsePoint));
        var nestedManifest = WriteAt(
            Path.Combine(nestedExport, "manifest.json"),
            "{\"chunked\":{\"chunks\":[{\"relativePath\":\"chunks/nested/a.jsonl\"}]}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(nestedManifest));

        var fileExport = Directory.CreateDirectory(Path.Combine(_root, "file-component")).FullName;
        Directory.CreateDirectory(Path.Combine(fileExport, "chunks"));
        var fileLink = Path.Combine(fileExport, "chunks", "a.jsonl");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(fileLink, fileTarget));
        Assert.True(File.GetAttributes(fileLink).HasFlag(FileAttributes.ReparsePoint));
        var fileManifest = WriteAt(
            Path.Combine(fileExport, "manifest.json"),
            "{\"chunked\":{\"chunks\":[{\"relativePath\":\"chunks/a.jsonl\"}]}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(fileManifest));
    }

    [Fact]
    public void ResolveChunkFiles_LegacyModeRejectsReparseChunksDirectoryAndFiles()
    {
        var directoryTarget = Directory.CreateDirectory(
            Path.Combine(_root, "targets", "legacy-directory")).FullName;
        _ = WriteAt(Path.Combine(directoryTarget, "a.jsonl"), "{}\n");
        var fileTarget = WriteAt(
            Path.Combine(_root, "targets", "legacy-file.jsonl"), "{}\n");

        var directoryExport = Directory.CreateDirectory(
            Path.Combine(_root, "legacy-directory-export")).FullName;
        var chunksLink = Path.Combine(directoryExport, "chunks");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(chunksLink, directoryTarget));
        Assert.True(File.GetAttributes(chunksLink).HasFlag(FileAttributes.ReparsePoint));
        var directoryManifest = WriteAt(
            Path.Combine(directoryExport, "manifest.json"), "{\"chatInfo\":{}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(directoryManifest));

        var fileExport = Directory.CreateDirectory(
            Path.Combine(_root, "legacy-file-export")).FullName;
        Directory.CreateDirectory(Path.Combine(fileExport, "chunks"));
        var chunkLink = Path.Combine(fileExport, "chunks", "a.jsonl");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(chunkLink, fileTarget));
        Assert.True(File.GetAttributes(chunkLink).HasFlag(FileAttributes.ReparsePoint));
        var fileManifest = WriteAt(
            Path.Combine(fileExport, "manifest.json"), "{\"chatInfo\":{}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(fileManifest));
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

    [Fact]
    public void QqChunkedExportFormat_Open_RejectsUnsafeManifestBeforeParsingItsContents()
    {
        var target = WriteAt(Path.Combine(_root, "outside", "manifest.json"), "{");
        var manifest = Path.Combine(_root, "manifest.json");
        try
        {
            File.CreateSymbolicLink(manifest, target);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"File symbolic links are unavailable on this platform: {ex.GetType().Name}");
            return;
        }

        Assert.True(File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint));

        var error = Assert.Throws<ImportFormatException>(
            () => new QqChunkedExportFormat().Open(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("重解析点", error.Message);
    }

    private static void CreateSymbolicLinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                or NotSupportedException)
        {
            Assert.Skip("当前环境不允许创建符号链接");
        }
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
