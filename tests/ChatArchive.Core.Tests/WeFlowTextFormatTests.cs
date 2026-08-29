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
        WriteUtf8WithBom(path,
            "id,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime\r\n" +
            "1,9001,image,0,Alice,图片,images/one.jpg,2023-11-15T06:15:23.000Z\r\n");

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

    [Fact]
    public void WeFlowCsv_UsesConversationScopedSyntheticSenderId()
    {
        var firstPath = WriteCurrentCsv("first.csv", "1,9001,text,0,Alice,hello,,2023-11-15T06:15:23.000Z\r\n");
        var secondPath = WriteCurrentCsv("second.csv", "1,9001,text,0,Alice,hello,,2023-11-15T06:15:23.000Z\r\n");
        var format = new WeFlowCsvExportFormat();

        using var firstExport = format.Open(firstPath);
        using var sameConversationExport = format.Open(firstPath);
        using var secondExport = format.Open(secondPath);
        var first = Assert.Single(firstExport.EnumerateMessages());
        var repeated = Assert.Single(sameConversationExport.EnumerateMessages());
        var second = Assert.Single(secondExport.EnumerateMessages());

        Assert.Matches("^synthetic:[0-9a-f]{64}$", first.SenderNativeId);
        Assert.NotEqual("Alice", first.SenderNativeId);
        Assert.Equal(first.SenderNativeId, repeated.SenderNativeId);
        Assert.NotEqual(first.SenderNativeId, second.SenderNativeId);
        Assert.Contains("Alice", first.SenderAliases);
    }

    [Fact]
    public void WeFlowCsv_MatchesOnlyNoBomOrOnePhysicalUtf8Bom()
    {
        const string header = "id,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime\r\n";
        var body = Encoding.UTF8.GetBytes(header);
        var preamble = Encoding.UTF8.GetPreamble();
        var noBom = Path.Combine(_dir, "no-bom.csv");
        var oneBom = Path.Combine(_dir, "one-bom.csv");
        var duplicateBom = Path.Combine(_dir, "duplicate-bom.csv");
        File.WriteAllBytes(noBom, body);
        File.WriteAllBytes(oneBom, preamble.Concat(body).ToArray());
        File.WriteAllBytes(duplicateBom, preamble.Concat(preamble).Concat(body).ToArray());

        var format = new WeFlowCsvExportFormat();
        Assert.True(format.Matches(noBom));
        Assert.True(format.Matches(oneBom));
        Assert.False(format.Matches(duplicateBom));
    }

    [Theory]
    [InlineData("1,9001,text,0,Alice,hello,\r\n")]
    [InlineData("1,9001,text,0,Alice,hello,,2023-11-15T06:15:23.000Z,extra\r\n")]
    public void WeFlowCsv_RejectsRowsWithoutExactlyEightCells(string row)
    {
        var path = WriteCurrentCsv("malformed.csv", row);
        using var export = new WeFlowCsvExportFormat().Open(path);

        var exception = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());
        Assert.Contains(path, exception.Message);
        Assert.Contains("第 2 行", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-time")]
    public void WeFlowCsv_RejectsMissingOrUnparseableCreateTime(string createTime)
    {
        var path = WriteCurrentCsv("invalid-time.csv", $"1,9001,text,0,Alice,hello,,{createTime}\r\n");
        using var export = new WeFlowCsvExportFormat().Open(path);

        var exception = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());
        Assert.Contains(path, exception.Message);
        Assert.Contains("第 2 行", exception.Message);
        Assert.Contains("CreateTime", exception.Message);
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

    private string WriteCurrentCsv(string name, string row)
    {
        return WriteFile(name, "id,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime\r\n" + row);
    }

    private static void WriteUtf8WithBom(string path, string content)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray());
    }
}
