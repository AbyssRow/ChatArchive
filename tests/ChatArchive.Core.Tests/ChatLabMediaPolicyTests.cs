using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class ChatLabMediaPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-chatlab-media-policy-{Guid.NewGuid():N}");

    public ChatLabMediaPolicyTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("json", "WeFlow", true)]
    [InlineData("json", "weflow", false)]
    [InlineData("json", "ChatLab", false)]
    [InlineData("jsonl", "WeFlow", true)]
    [InlineData("jsonl", "weflow", false)]
    [InlineData("jsonl", "ChatLab", false)]
    public void ChatLabMediaPolicy_EnablesLayoutAOnlyForExactWeFlowGenerator(
        string format,
        string generator,
        bool resolves)
    {
        var exportDirectory = Path.Combine(_directory, format, generator, "texts");
        var images = Path.Combine(_directory, format, generator, "images");
        Directory.CreateDirectory(exportDirectory);
        Directory.CreateDirectory(images);
        var image = Path.Combine(images, "one.jpg");
        File.WriteAllText(image, "image");
        var path = Path.Combine(exportDirectory, $"chat.{format}");
        IChatExportFormat adapter;
        if (format == "json")
        {
            File.WriteAllText(path, $$"""
                {
                  "chatlab": { "version": "0.0.2", "generator": "{{generator}}" },
                  "meta": { "name": "会话", "platform": "wechat", "type": "private", "ownerId": "wxid_self" },
                  "messages": [
                    { "id": "m1", "timestamp": 1700000123, "sender": "wxid_alice", "type": 1,
                      "content": "[图片] ../images/one.jpg", "mediaPath": "../images/one.jpg" }
                  ]
                }
                """);
            adapter = new ChatLabJsonExportFormat();
        }
        else
        {
            File.WriteAllLines(path,
            [
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    _type = "header",
                    chatlab = new { version = "0.0.2", generator },
                    meta = new { name = "会话", platform = "wechat", type = "private", ownerId = "wxid_self" },
                }),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    _type = "message",
                    id = "m1",
                    timestamp = 1700000123,
                    sender = "wxid_alice",
                    type = 1,
                    content = "[图片] ../images/one.jpg",
                    mediaPath = "../images/one.jpg",
                }),
            ]);
            adapter = new ChatLabJsonlExportFormat();
        }

        Assert.True(adapter.Matches(path));
        using var export = adapter.Open(path);
        var attachment = Assert.Single(Assert.Single(export.EnumerateMessages()).Attachments);

        Assert.Equal("../images/one.jpg", attachment.DeclaredPath);
        Assert.Equal(resolves ? image : null, attachment.SourcePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
