using System.Text;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class WeFlowTextFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"chatarchive-weflow-text-{Guid.NewGuid():N}");

    public WeFlowTextFormatTests()
    {
        Directory.CreateDirectory(_dir);
    }

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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
