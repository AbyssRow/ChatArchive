using ChatArchive.Core.Importing;
using ChatArchive.Core.IO;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class ChunkedJsonReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-chunked-json-{Guid.NewGuid():N}");

    public ChunkedJsonReaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Reads_object_and_array_across_tiny_buffers()
    {
        var path = Write("stream.json", "\uFEFF" + """
            {"messages":[{"id":1,"content":"跨块😀\"文本","nested":[1,{"x":true}]}],
             "session":{"wxid":"wxid_peer","version":"meta-after-messages"}}
            """);

        var session = ChunkedJsonReader.ReadObjectProperty(path, "session", bufferSize: 7);
        var messages = ChunkedJsonReader.EnumerateObjectArray(path, "messages", bufferSize: 7).ToList();

        Assert.Equal("wxid_peer", ImportText.Clean(session["wxid"]));
        Assert.Single(messages);
        Assert.Equal("跨块😀\"文本", ImportText.Clean(messages[0]["content"]));
        Assert.True(messages[0]["nested"]![1]!["x"]!.GetValue<bool>());
    }

    [Fact]
    public void Missing_property_is_rejected()
    {
        var path = Write("missing.json", """{"other":{}}""");

        Assert.Throws<ImportFormatException>(
            () => ChunkedJsonReader.ReadObjectProperty(path, "session", bufferSize: 5));
    }

    [Fact]
    public void Non_object_array_member_is_rejected()
    {
        var path = Write("wrong-item.json", """{"messages":[{"id":1},42]}""");

        Assert.Throws<ImportFormatException>(
            () => ChunkedJsonReader.EnumerateObjectArray(path, "messages", bufferSize: 5).ToList());
    }

    [Fact]
    public void Truncated_json_is_rejected()
    {
        var path = Write("truncated.json", """{"messages":[{"id":1}""");

        Assert.Throws<ImportFormatException>(
            () => ChunkedJsonReader.EnumerateObjectArray(path, "messages", bufferSize: 5).ToList());
    }

    [Fact]
    public void Missing_root_suffix_after_selected_object_is_rejected()
    {
        var path = Write("object-suffix.json", """{"session":{"id":1}""");

        Assert.Throws<ImportFormatException>(
            () => ChunkedJsonReader.ReadObjectProperty(path, "session", bufferSize: 5));
    }

    [Fact]
    public void Missing_root_suffix_after_selected_array_is_rejected()
    {
        var path = Write("array-suffix.json", """{"messages":[]""");

        Assert.Throws<ImportFormatException>(
            () => ChunkedJsonReader.EnumerateObjectArray(path, "messages", bufferSize: 5).ToList());
    }

    [Fact]
    public void Trailing_json_content_is_rejected()
    {
        var path = Write("trailing.json", """{"messages":[]} true""");

        Assert.Throws<ImportFormatException>(
            () => ChunkedJsonReader.EnumerateObjectArray(path, "messages", bufferSize: 5).ToList());
    }

    [Fact]
    public void Root_property_sniffer_finds_markers_after_large_leading_values()
    {
        var padding = new string('x', 20_000);
        var path = Write("markers.json", $$"""
            {"padding":"{{padding}}","metadata":{},"chatInfo":{},"messages":[]}
            """);

        Assert.True(ChunkedJsonReader.ContainsRootProperties(
            path,
            new[] { "metadata", "chatInfo" },
            bufferSize: 7));
        Assert.False(ChunkedJsonReader.ContainsRootProperties(
            path,
            new[] { "metadata", "session" },
            bufferSize: 7));
    }

    [Fact]
    public void ContainsRootProperties_ReturnsFalse_ForJsonArray_WithoutThrowing()
    {
        var path = Write("root_array.json", """[{"id": 1}, {"id": 2}]""");
        var result = ChunkedJsonReader.ContainsRootProperties(path, new[] { "metadata", "chatInfo" }, bufferSize: 7);
        Assert.False(result);
    }

    [Fact]
    public void Array_enumeration_observes_cancellation_between_items()
    {
        var path = Write("cancel.json", """{"messages":[{"id":1},{"id":2}]}""");
        using var cancellation = new CancellationTokenSource();
        using var iterator = ChunkedJsonReader
            .EnumerateObjectArray(path, "messages", cancellation.Token, bufferSize: 5)
            .GetEnumerator();

        Assert.True(iterator.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => iterator.MoveNext());
    }

    private string Write(string filename, string content)
    {
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
