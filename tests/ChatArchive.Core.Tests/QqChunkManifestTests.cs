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
