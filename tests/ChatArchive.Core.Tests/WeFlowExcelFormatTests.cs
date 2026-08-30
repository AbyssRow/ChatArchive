using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class WeFlowExcelFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"chatarchive-weflow-excel-{Guid.NewGuid():N}");

    public WeFlowExcelFormatTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("private")]
    [InlineData("group")]
    public void WeFlowExcel_ParsesCurrentDynamicLayouts(string layout)
    {
        var path = CreateWeFlowWorkbook(layout);
        var format = new WeFlowExcelExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("wxid_session", export.Conversation.NativeId);
        Assert.Equal(layout == "group" ? "group" : "private", export.Conversation.Kind);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal(1700000123000, message.TimestampMs);
        Assert.Equal("image", message.MessageType);
        Assert.Equal(layout == "group" ? "../images/one.jpg" : "正文", message.Content);

        if (layout == "group")
        {
            var attachment = Assert.Single(message.Attachments);
            Assert.Equal("../images/one.jpg", attachment.DeclaredPath);
            Assert.Equal(Path.Combine(_directory, "images", "one.jpg"), attachment.SourcePath);
            Assert.Equal("wxid_sender", message.SenderNativeId);
            Assert.Equal("incoming", message.Direction);
        }
        else if (layout == "compact")
        {
            Assert.StartsWith("synthetic:", message.SenderNativeId, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WeFlowExcel_GroupMemberNamed我_RemainsIncoming()
    {
        var path = CreateWeFlowWorkbook("group", senderIdentity: "我");
        using var export = new WeFlowExcelExportFormat().Open(path);

        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal("我", message.RawPayload["发送者身份"]?.GetValue<string>());
        Assert.Equal("wxid_sender", message.SenderNativeId);
        Assert.Equal("incoming", message.Direction);
    }

    [Fact]
    public void WeFlowExcel_ParsesCurrentStreamingMetadataArrangement()
    {
        var path = CreateWeFlowWorkbook("private", streamingMetadata: true);
        var format = new WeFlowExcelExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("wxid_session", export.Conversation.NativeId);
        Assert.Equal("会话标题", export.Conversation.Title);
        Assert.Equal("wxid_sender", Assert.Single(export.EnumerateMessages()).SenderNativeId);
    }

    [Fact]
    public void WeFlowExcel_DoesNotMatchWorkbookWhoseGeneratorIsNotWeFlow()
    {
        var path = CreateWeFlowWorkbook("private", generator: "OtherTool");

        Assert.False(new WeFlowExcelExportFormat().Matches(path));
    }

    [Fact]
    public void WeFlowExcel_DuplicateChatSheetsFailWithoutEscapingDiscovery()
    {
        var path = CreateWeFlowWorkbook("private", duplicateChatSheet: true);
        var format = new WeFlowExcelExportFormat();

        Assert.False(format.Matches(path));
        var error = Assert.Throws<ImportFormatException>(() => format.Open(path));
        Assert.Contains(path, error.Message);
        Assert.DoesNotContain(
            ImportDiscovery.Discover([Path.GetDirectoryName(path)!]),
            item => item.FilePath == Path.GetFullPath(path));
    }

    private string CreateWeFlowWorkbook(
        string layout,
        string generator = "WeFlow",
        bool streamingMetadata = false,
        string senderIdentity = "对方",
        bool duplicateChatSheet = false)
    {
        var exportDirectory = Path.Combine(_directory, layout);
        Directory.CreateDirectory(exportDirectory);
        var path = Path.Combine(exportDirectory, "chat.xlsx");
        var headers = layout switch
        {
            "compact" => new[] { "序号", "时间", "发送者身份", "消息类型", "内容" },
            "private" => new[] { "序号", "时间", "发送者昵称", "发送者微信ID", "发送者备注", "发送者身份", "消息类型", "内容" },
            "group" => new[] { "序号", "时间", "发送者昵称", "发送者微信ID", "发送者备注", "群昵称", "发送者身份", "消息类型", "内容" },
            _ => throw new ArgumentOutOfRangeException(nameof(layout)),
        };
        var contentColumn = headers.Length;
        var headerRow = streamingMetadata ? 5 : 4;
        var messageRow = headerRow + 1;
        var messageValues = layout switch
        {
            "compact" => new[] { "1", "2023-11-15 06:15:23", senderIdentity, "图片消息", "正文" },
            "private" => new[] { "1", "2023-11-15 06:15:23", "昵称", "wxid_sender", "发送者备注", senderIdentity, "图片消息", "正文" },
            "group" => new[] { "1", "2023-11-15 06:15:23", "昵称", "wxid_sender", "发送者备注", "群昵称", senderIdentity, "图片消息", "../images/one.jpg" },
            _ => throw new ArgumentOutOfRangeException(nameof(layout)),
        };

        var rows = new List<IReadOnlyList<XlsxTestCell>>
        {
            new XlsxTestCell[] { new("A1", "会话信息") },
        };

        rows.Add(streamingMetadata
            ? new XlsxTestCell[] { new("A2", "微信ID"), new("B2", "wxid_session"), new("C2", "昵称"), new("D2", "会话标题") }
            : new XlsxTestCell[] { new("A2", "微信ID"), new("B2", "wxid_session"), new("D2", "昵称"), new("E2", "会话标题"), new("F2", "备注"), new("G2", "群备注") });

        rows.Add(streamingMetadata
            ? new XlsxTestCell[] { new("A3", "导出工具"), new("B3", generator), new("C3", "导出时间"), new("D3", "2023-11-15 06:20:00") }
            : new XlsxTestCell[] { new("A3", "导出工具"), new("B3", generator), new("C3", "导出版本"), new("D3", "1.0.3"), new("E3", "平台"), new("F3", "wechat"), new("G3", "导出时间"), new("H3", "2023-11-15 06:20:00") });

        if (streamingMetadata)
        {
            rows.Add([]);
        }

        rows.Add(headers.Select((value, index) => new XlsxTestCell($"{ColumnName(index + 1)}{headerRow}", value)).ToArray());
        rows.Add(messageValues.Select((value, index) => new XlsxTestCell(
            $"{ColumnName(index + 1)}{messageRow}",
            value,
            Hyperlink: layout == "group" && index + 1 == contentColumn ? "../images/one.jpg" : null,
            ExternalHyperlink: layout == "group" && index + 1 == contentColumn)).ToArray());

        if (layout == "group")
        {
            var parentImage = Path.Combine(_directory, "images", "one.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(parentImage)!);
            File.WriteAllText(parentImage, "image");
        }

        var sheet = new XlsxTestSheet("聊天记录", rows);
        if (duplicateChatSheet)
        {
            XlsxTestFile.Write(path, sheet, new XlsxTestSheet("聊天记录", rows));
        }
        else
        {
            XlsxTestFile.Write(path, sheet);
        }
        return path;
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }

        return name;
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
