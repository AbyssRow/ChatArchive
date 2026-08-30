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

    [Fact]
    public void WeFlowCsv_ImportsIsoEpochCreateTime()
    {
        var path = WriteCurrentCsv("epoch.csv", "1,9001,text,0,Alice,hello,,1970-01-01T00:00:00.000Z\r\n");
        using var export = new WeFlowCsvExportFormat().Open(path);

        Assert.Equal(0, Assert.Single(export.EnumerateMessages()).TimestampMs);
    }

    [Fact]
    public void WeFlowMarkdown_ParsesMetadataBlocksAndMedia()
    {
        var dir = NewDirectory();
        Directory.CreateDirectory(Path.Combine(dir, "images"));
        var media = Path.Combine(dir, "images", "one.jpg");
        File.WriteAllText(media, "image");
        var path = WriteFile(dir, "chat.md", """
            # 项目群

            - 会话ID: `group@chatroom`
            - 会话类型: 群聊
            - 消息数量: 1
            - 导出时间: 2023-11-15 06:16:00
            - 导出工具: WeFlow

            ---

            ## 2023-11-15 06:15:23 Alice

            > Bob: 被引用内容

            ![图片](images/one.jpg)

            回复正文
            """);

        var format = new WeFlowMarkdownExportFormat();
        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("group@chatroom", export.Conversation.NativeId);
        Assert.Equal("group", export.Conversation.Kind);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal("Alice", message.SenderName);
        Assert.Contains("被引用内容", message.SearchText);
        Assert.Contains("回复正文", message.Content);
        Assert.Equal(media, Assert.Single(message.Attachments).SourcePath);
    }

    [Fact]
    public void WeFlowMarkdown_TrimsOnlyTrailingSeparatorLinesAtMessageBoundaryAndEof()
    {
        var path = WriteFile("boundary.md",
            "# 项目群\n\n" +
            "- 会话ID: `group@chatroom`\n" +
            "- 会话类型: 群聊\n" +
            "- 消息数量: 2\n" +
            "- 导出时间: 2023-11-15 06:16:00\n" +
            "- 导出工具: WeFlow\n\n" +
            "---\n\n" +
            "## 2023-11-15 06:15:23 Alice\n\n" +
            "第一行\n\n" +
            "第二行\n\n\n" +
            "## 2023-11-15 06:16:23 Bob\n\n" +
            "末尾正文\n\n");

        using var export = new WeFlowMarkdownExportFormat().Open(path);
        var messages = export.EnumerateMessages().ToList();

        Assert.Equal("\n第一行\n\n第二行", messages[0].Content);
        Assert.Equal("\n末尾正文", messages[1].Content);
    }

    [Fact]
    public void WeFlowTxt_StripsWriterQuotesAndKeepsMultilineBody()
    {
        var path = WriteFile("chat.txt", """
            2023-11-15 06:15:23 'Alice'
            第一行
            第二行

            2023-11-15 06:16:23 '我'
            回复
            """);

        var format = new WeFlowTextExportFormat();
        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        var messages = export.EnumerateMessages().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("Alice", messages[0].SenderName);
        Assert.Equal("第一行\n第二行", messages[0].Content.Replace("\r\n", "\n"));
        Assert.Equal("outgoing", messages[1].Direction);
    }

    [Fact]
    public void WeFlowTxt_PreservesHeaderLikeBodyLineUntilBlankSeparatedHeader()
    {
        var path = WriteFile("header-like-body.txt",
            "2023-11-15 06:15:23 'Alice'\n" +
            "第一行\n" +
            "2023-11-15 06:16:23 'Bob'\n" +
            "仍是正文\n\n" +
            "2023-11-15 06:17:23 'Carol'\n" +
            "第二条\n\n");
        using var export = new WeFlowTextExportFormat().Open(path);

        var messages = export.EnumerateMessages().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("Alice", messages[0].SenderName);
        Assert.Equal("第一行\n2023-11-15 06:16:23 'Bob'\n仍是正文", messages[0].Content);
        Assert.Equal("Carol", messages[1].SenderName);
        Assert.Equal("第二条", messages[1].Content);
    }

    [Fact]
    public void WeFlowTxt_AcceptsWriterProducedEmptyBody()
    {
        var path = WriteFile(
            "empty-body.txt",
            "2023-11-15 06:15:23 'Alice'\n\n\n");
        var format = new WeFlowTextExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal("Alice", message.SenderName);
        Assert.Equal(string.Empty, message.Content);
    }

    [Fact]
    public void WeFlowTxt_InvalidTimestampHeaderLikeFirstBodyLineRemainsContent()
    {
        const string HeaderLikeBody = "2023-99-99 25:61:00 '这只是正文'";
        var path = WriteFile(
            "invalid-header-like-body.txt",
            "2023-11-15 06:15:23 'Alice'\n" + HeaderLikeBody + "\n\n");
        var format = new WeFlowTextExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal(HeaderLikeBody, message.Content);
    }

    [Fact]
    public void WeFlowTxt_RejectsFinalHeaderWithoutBody()
    {
        var path = WriteFile("missing-final-body.txt",
            "2023-11-15 06:15:23 'Alice'\n" +
            "有效正文\n\n" +
            "2023-11-15 06:16:23 'Bob'\n");
        using var export = new WeFlowTextExportFormat().Open(path);

        var exception = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());

        Assert.Contains(path, exception.Message);
        Assert.Contains("第 2 个", exception.Message);
    }

    [Theory]
    [InlineData("# Any title\n[2023-11-15 06:15:23] Alice: old", ".md")]
    [InlineData("会话: old\n2023-11-15 06:15:23 Alice: old", ".txt")]
    public void WeFlowText_RejectsFormerImaginedSyntax(string content, string extension)
    {
        var path = WriteFile($"old{extension}", content);
        Assert.DoesNotContain(ExportFormats.Default, format => format.Matches(path));
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

    private static string WriteFile(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
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
