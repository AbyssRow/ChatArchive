using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class QqTextFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"chatarchive-qq-text-{Guid.NewGuid():N}");

    public QqTextFormatTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void QqTxt_ParsesCurrentHeaderOptionalTypeAndResources()
    {
        var path = WriteFile("qq.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            [本软件是免费的开源项目~ 如果您是买来的，请立即退款！如果有帮助到您，欢迎给我点个Star~]

            ===============================================
                       QQ聊天记录导出文件
            ===============================================

            聊天名称: 示例群
            聊天类型: 群聊
            消息总数: 1

            [1]
            [群主] Alice:
            时间: 2023-11-15 06:15:23
            类型: image
            内容: [image消息]
            资源: 1 个文件
              - image: one.jpg

            ===============================================
                          导出完成
            ===============================================
            总计导出 1 条消息
            """);

        var format = new QqTextExportFormat();
        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("qq", export.Conversation.Platform);
        Assert.Equal("示例群", export.Conversation.Title);
        Assert.Equal("group", export.Conversation.Kind);
        Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);

        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal("1", message.LocalId);
        Assert.Equal("Alice", message.SenderName);
        Assert.Matches("^synthetic:[0-9a-f]{64}$", message.SenderNativeId);
        Assert.NotEqual("Alice", message.SenderNativeId);
        Assert.Equal("群主", message.RawPayload["senderTitle"]!.GetValue<string>());
        Assert.Equal("incoming", message.Direction);
        Assert.Equal("image", message.MessageType);
        Assert.True(message.RawPayload["resourceCount"]!.GetValue<int>() == 1);
    }

    [Fact]
    public void QqTxt_RejectsAPlainFileWithChineseLabels()
    {
        var path = WriteFile("plain.txt", "聊天名称: x\n时间: 2023-11-15 06:15:23\n内容: y");
        Assert.False(new QqTextExportFormat().Matches(path));
    }

    [Fact]
    public void QqTxt_RejectsNearMissExporterSignature()
    {
        var path = WriteFile("near-miss.txt", """
            [QQChatExporter V4 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊
            时间: 2023-11-15 06:15:23
            内容: y
            """);

        Assert.False(new QqTextExportFormat().Matches(path));
    }

    [Fact]
    public void QqTxt_ParsesOptionalOrdinalsTypesResourcesAndMultilineSenderBlocks()
    {
        var path = WriteFile("optional.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            [本软件是免费的开源项目~ 如果您是买来的，请立即退款！如果有帮助到您，欢迎给我点个Star~]

            ===============================================
                       QQ聊天记录导出文件
            ===============================================

            聊天名称: Alice
            聊天类型: 私聊

            Alice:
            时间: 0
            内容: 第一行
            第二行

            Bob:
            时间: 2023-11-15 06:16:23
            类型: 系统消息
            内容: 系统通知
            资源: 1 个文件
              - image: remote.jpg

            ===============================================
                          导出完成
            ===============================================
            总计导出 2 条消息
            """);

        using var export = new QqTextExportFormat().Open(path);
        var messages = export.EnumerateMessages().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal(0, messages[0].TimestampMs);
        Assert.Equal("Alice", messages[0].SenderName);
        Assert.Equal("第一行\n第二行", messages[0].Content);
        Assert.Null(messages[0].LocalId);
        Assert.Equal("incoming", messages[0].Direction);
        Assert.Equal("system", messages[1].Direction);
        Assert.True(messages[1].IsSystem);
        var attachment = Assert.Single(messages[1].Attachments);
        Assert.Equal("remote.jpg", attachment.Filename);
        Assert.Null(attachment.DeclaredPath);
        Assert.Null(attachment.SourcePath);
        Assert.Equal("image", attachment.Metadata["resourceType"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("text", "text")]
    [InlineData("文本", "text")]
    [InlineData("image", "image")]
    [InlineData("图片", "image")]
    [InlineData("video", "video")]
    [InlineData("视频", "video")]
    [InlineData("audio", "audio")]
    [InlineData("音频", "audio")]
    [InlineData("file", "file")]
    [InlineData("文件", "file")]
    [InlineData("face", "face")]
    [InlineData("表情", "face")]
    [InlineData("reply", "reply")]
    [InlineData("回复", "reply")]
    [InlineData("system", "system")]
    [InlineData("系统消息", "system")]
    public void QqTxt_MapsOnlyCurrentTypeLabels(string input, string expected)
    {
        var path = WriteFile($"type-{expected}-{input}.txt", $"""
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            [本软件是免费的开源项目~ 如果您是买来的，请立即退款！如果有帮助到您，欢迎给我点个Star~]

            聊天名称: 示例群
            聊天类型: 群聊

            时间: 2023-11-15 06:15:23
            类型: {input}
            内容: body
            """);

        using var export = new QqTextExportFormat().Open(path);
        Assert.Equal(expected, Assert.Single(export.EnumerateMessages()).MessageType);
    }

    [Fact]
    public void QqTxt_ReportsBlockWhenRequiredContentIsMissing()
    {
        var path = WriteFile("missing-content.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊

            [1]
            Alice:
            时间: 2023-11-15 06:15:23
            """);
        using var export = new QqTextExportFormat().Open(path);

        var exception = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());

        Assert.Contains(path, exception.Message);
        Assert.Contains("第 1 个", exception.Message);
    }

    [Fact]
    public void QqTxt_OrdinalContentPreservesTimeLookingLine()
    {
        var path = WriteFile("ordinal-time-content.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊

            [1]
            Alice:
            时间: 2023-11-15 06:15:23
            内容: 第一行
            时间: 这不是下一个消息
            第二行

            ===============================================
                          导出完成
            ===============================================
            """);

        using var export = new QqTextExportFormat().Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal("第一行\n时间: 这不是下一个消息\n第二行", message.Content);
    }

    [Fact]
    public void QqTxt_ReplaysIncompleteResourceCandidateAsLiteralContent()
    {
        var path = WriteFile("literal-resources.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊

            [1]
            Alice:
            时间: 2023-11-15 06:15:23
            内容: 正文
            资源: 2 个文件
              - image: one.jpg
            仍是正文

            ===============================================
                          导出完成
            ===============================================
            """);

        using var export = new QqTextExportFormat().Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal("正文\n资源: 2 个文件\n  - image: one.jpg\n仍是正文", message.Content);
        Assert.Empty(message.Attachments);
        Assert.Null(message.RawPayload["resourceCount"]);
    }

    [Fact]
    public void QqTxt_OpenHonorsPreCanceledToken()
    {
        var path = WriteFile("cancel-open.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊
            """);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new QqTextExportFormat().Open(path, cancellation.Token));
    }

    [Fact]
    public void QqTxt_ReportsBlockWhenRequiredTimeIsMissing()
    {
        var path = WriteFile("missing-time.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊

            [1]
            Alice:
            内容: text
            """);
        using var export = new QqTextExportFormat().Open(path);

        var exception = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());

        Assert.Contains(path, exception.Message);
        Assert.Contains("第 1 个", exception.Message);
        Assert.Contains("时间", exception.Message);
    }

    [Fact]
    public void QqTxt_EmptySignedExportHasNoValidMessages()
    {
        var path = WriteFile("empty.txt", """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            聊天名称: 示例群
            聊天类型: 群聊

            ===============================================
                          导出完成
            ===============================================
            总计导出 0 条消息
            """);
        using var export = new QqTextExportFormat().Open(path);

        var exception = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());

        Assert.Contains(path, exception.Message);
        Assert.Contains("未找到有效", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
