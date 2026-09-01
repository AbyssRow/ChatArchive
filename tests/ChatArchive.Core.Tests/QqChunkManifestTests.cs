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
