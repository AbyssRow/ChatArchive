using ChatArchive.Core.IO;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class FileHashingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-hashing-{Guid.NewGuid():N}");

    [Fact]
    public void Sha256_file_matches_known_digest()
    {
        var path = Write("abc.bin", "abc");

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            FileHashing.Sha256File(path));
    }

    [Fact]
    public void Sha256_file_observes_cancellation()
    {
        var path = Write("cancel.bin", new string('x', 1024));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FileHashing.Sha256File(path, cancellation.Token));
    }

    [Fact]
    public void Copy_file_and_hash_reports_the_bytes_actually_copied()
    {
        var source = Write("source.bin", "abc");
        var destination = Path.Combine(_directory, "destination.bin");

        var result = FileHashing.CopyFileAndHash(source, destination);

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            result.Digest);
        Assert.Equal(3, result.Size);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
    }

    [Fact]
    public void Copy_file_and_hash_observes_precancellation_without_creating_output()
    {
        var source = Write("source-cancel.bin", "abc");
        var destination = Path.Combine(_directory, "destination-cancel.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FileHashing.CopyFileAndHash(source, destination, cancellation.Token));
        Assert.False(File.Exists(destination));
    }

    private string Write(string filename, string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, filename);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup for test artifacts.
        }
    }
}
