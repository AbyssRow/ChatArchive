using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class QqExcelFormatTests : IDisposable
{
    private static readonly string[] MessageHeaders =
        ["序号", "时间", "发送者", "发送者QQ号", "消息类型", "消息内容", "是否撤回", "资源数量"];
    private static readonly string[] TitledMessageHeaders =
        ["序号", "时间", "发送者", "发送者QQ号", "群头衔", "消息类型", "消息内容", "是否撤回", "资源数量"];
    private static readonly string[] ResourceHeaders =
        ["序号", "时间", "发送者", "发送者QQ号", "资源类型", "文件名", "大小(字节)", "URL"];

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-qq-excel-{Guid.NewGuid():N}");

    public QqExcelFormatTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QqExcel_ParsesCurrentOptionalGroupTitleLayout(bool includeTitle)
    {
        var path = WriteWorkbook(
            $"title-{includeTitle}.xlsx",
            includeTitle,
            [new("2023-11-15 06:15:23", "Alice", "10002", "文本", "hello", false, "群主", 0)]);
        var format = new QqExcelExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("qq", export.Conversation.Platform);
        Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), export.Conversation.Title);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal(1700000123000, message.TimestampMs);
        Assert.Equal("10002", message.SenderNativeId);
        Assert.Equal("incoming", message.Direction);
        Assert.Equal(includeTitle ? "群主" : string.Empty, message.RawPayload["群头衔"]?.GetValue<string>() ?? string.Empty);
    }

    [Fact]
    public void QqExcel_AttachesOnlyUniqueResourceKeyAndResolvesLocalPathSafely()
    {
        var localPath = Path.Combine(_directory, "media", "one.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, "image");
        var path = WriteWorkbook(
            "unique.xlsx",
            includeTitle: true,
            [
                new("2023-11-15 06:15:23", "Alice", "10002", "图片", "first", true, "群主", 1),
                new("2023-11-15 06:16:23", "Bob", "10003", "文本", "second", false, "", 0),
            ],
            [new("2023-11-15 06:15:23", "Alice", "10002", "image", "one.jpg", 42, "media/one.jpg")]);
        var format = new QqExcelExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        var messages = export.EnumerateMessages().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("10002", messages[0].SenderNativeId);
        Assert.True(messages[0].IsRecalled);
        var attachment = Assert.Single(messages[0].Attachments);
        Assert.Equal("image", attachment.Kind);
        Assert.Equal("media/one.jpg", attachment.DeclaredPath);
        Assert.Equal(localPath, attachment.SourcePath);
        Assert.Equal(42, attachment.DeclaredSize);
        Assert.Empty(messages[1].Attachments);
    }

    [Fact]
    public void QqExcel_DoesNotAttachResourcesWhenMessageJoinKeyIsAmbiguous()
    {
        var messages = new[]
        {
            new QqMessage("2023-11-15 06:15:23", "Alice", "10002", "图片", "first", false, "", 1),
            new QqMessage("2023-11-15 06:15:23", "Alice", "10002", "图片", "second", false, "", 1),
        };
        var path = WriteWorkbook(
            "ambiguous.xlsx",
            includeTitle: false,
            messages,
            [new("2023-11-15 06:15:23", "Alice", "10002", "image", "one.jpg", 42, "media/one.jpg")]);

        using var export = new QqExcelExportFormat().Open(path);
        var parsed = export.EnumerateMessages().ToList();

        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, message => Assert.Empty(message.Attachments));
        Assert.All(parsed, message => Assert.Equal("1", message.RawPayload["资源数量"]?.GetValue<string>()));
    }

    [Fact]
    public void QqExcel_PreservesHttpResourceAsMetadataWithoutSourcePath()
    {
        var path = WriteWorkbook(
            "remote.xlsx",
            includeTitle: false,
            [new("2023-11-15 06:15:23", "Alice", "10002", "图片", "first", false, "", 1)],
            [new("2023-11-15 06:15:23", "Alice", "10002", "image", "remote.jpg", 99, "https://cdn.example.test/remote.jpg")]);

        using var export = new QqExcelExportFormat().Open(path);
        var attachment = Assert.Single(Assert.Single(export.EnumerateMessages()).Attachments);

        Assert.Null(attachment.DeclaredPath);
        Assert.Null(attachment.SourcePath);
        Assert.Equal("https://cdn.example.test/remote.jpg", attachment.Metadata["url"]?.GetValue<string>());
    }

    [Fact]
    public void QqExcel_UsesSyntheticSenderWhenUinIsEmpty()
    {
        var path = WriteWorkbook(
            "synthetic.xlsx",
            includeTitle: false,
            [new("2023-11-15 06:15:23", "Alice", "", "文本", "hello", false, "", 0)]);

        using var export = new QqExcelExportFormat().Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.StartsWith("synthetic:", message.SenderNativeId, StringComparison.Ordinal);
        Assert.Equal("Alice", message.SenderName);
    }

    [Fact]
    public void QqExcel_MapsAllCurrentProducerMessageTypeLabels()
    {
        var labels = new[] { "文本", "图片", "视频", "音频", "文件", "表情", "@提及", "回复", "系统消息" };
        var expected = new[] { "text", "image", "video", "audio", "file", "face", "at", "reply", "system" };
        var messages = labels.Select((label, index) => new QqMessage(
            $"2023-11-15 06:{15 + index:00}:23",
            "Alice",
            "10002",
            label,
            label,
            false,
            "",
            0)).ToArray();
        var path = WriteWorkbook("types.xlsx", includeTitle: false, messages);

        using var export = new QqExcelExportFormat().Open(path);
        var parsed = export.EnumerateMessages().ToList();

        Assert.Equal(expected, parsed.Select(message => message.MessageType));
        Assert.Equal("system", parsed[^1].Direction);
        Assert.True(parsed[^1].IsSystem);
    }

    [Theory]
    [InlineData("unknown-message-column")]
    [InlineData("misplaced-title")]
    [InlineData("unknown-resource-column")]
    [InlineData("reordered-resource-column")]
    public void QqExcel_RejectsNonProducerHeaders(string mutation)
    {
        var messageHeaders = mutation switch
        {
            "unknown-message-column" => [.. MessageHeaders, "未知列"],
            "misplaced-title" => ["序号", "时间", "发送者", "群头衔", "发送者QQ号", "消息类型", "消息内容", "是否撤回", "资源数量"],
            _ => MessageHeaders,
        };
        var resourceHeaders = mutation switch
        {
            "unknown-resource-column" => [.. ResourceHeaders, "未知列"],
            "reordered-resource-column" => ResourceHeaders.Select((header, index) => index switch
            {
                4 => ResourceHeaders[5],
                5 => ResourceHeaders[4],
                _ => header,
            }).ToArray(),
            _ => ResourceHeaders,
        };
        var path = WriteWorkbook(
            $"negative-{mutation}.xlsx",
            includeTitle: false,
            [new("2023-11-15 06:15:23", "Alice", "10002", "文本", "hello", false, "", 0)],
            resources: [],
            messageHeaders: messageHeaders,
            resourceHeaders: resourceHeaders);

        Assert.False(new QqExcelExportFormat().Matches(path));
    }

    [Fact]
    public void QqExcel_HonorsCancellationAndReleasesAfterEarlyIteratorDisposal()
    {
        var path = WriteWorkbook(
            "lifetime.xlsx",
            includeTitle: false,
            [
                new("2023-11-15 06:15:23", "Alice", "10002", "文本", "first", false, "", 0),
                new("2023-11-15 06:16:23", "Bob", "10003", "文本", "second", false, "", 0),
            ]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new QqExcelExportFormat().Open(path, cancellation.Token));

        using (var export = new QqExcelExportFormat().Open(path))
        using (var messages = export.EnumerateMessages().GetEnumerator())
        {
            Assert.True(messages.MoveNext());
        }

        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    private string WriteWorkbook(
        string filename,
        bool includeTitle,
        IReadOnlyList<QqMessage> messages,
        IReadOnlyList<QqResource>? resources = null,
        IReadOnlyList<string>? messageHeaders = null,
        IReadOnlyList<string>? resourceHeaders = null)
    {
        messageHeaders ??= includeTitle ? TitledMessageHeaders : MessageHeaders;
        resourceHeaders ??= ResourceHeaders;
        var path = Path.Combine(_directory, filename);
        var messageRows = new List<IReadOnlyList<XlsxTestCell>>
        {
            Cells(messageHeaders, 1, header => header),
        };
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            messageRows.Add(Cells(messageHeaders, index + 2, header => header switch
            {
                "序号" => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "时间" => message.Time,
                "发送者" => message.Sender,
                "发送者QQ号" => message.Uin,
                "群头衔" => message.Title,
                "消息类型" => message.Type,
                "消息内容" => message.Content,
                "是否撤回" => message.Recalled ? "是" : "否",
                "资源数量" => message.ResourceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => "unexpected",
            }));
        }

        var sheets = new List<XlsxTestSheet>
        {
            new("聊天记录", messageRows),
        };
        if (resources is not null)
        {
            var resourceRows = new List<IReadOnlyList<XlsxTestCell>>
            {
                Cells(resourceHeaders, 1, header => header),
            };
            for (var index = 0; index < resources.Count; index++)
            {
                var resource = resources[index];
                resourceRows.Add(Cells(resourceHeaders, index + 2, header => header switch
                {
                    "序号" => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "时间" => resource.Time,
                    "发送者" => resource.Sender,
                    "发送者QQ号" => resource.Uin,
                    "资源类型" => resource.Type,
                    "文件名" => resource.Filename,
                    "大小(字节)" => resource.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "URL" => resource.Url,
                    _ => "unexpected",
                }));
            }

            sheets.Add(new("资源列表", resourceRows));
        }

        XlsxTestFile.Write(path, sheets.ToArray());
        return path;
    }

    private static XlsxTestCell[] Cells(
        IReadOnlyList<string> headers,
        int row,
        Func<string, string> value)
    {
        return headers.Select((header, index) => new XlsxTestCell(
            $"{ColumnName(index + 1)}{row}",
            value(header),
            header is "序号" or "资源数量" or "大小(字节)" && row > 1 ? "n" : "inlineStr")).ToArray();
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

    private sealed record QqMessage(
        string Time,
        string Sender,
        string Uin,
        string Type,
        string Content,
        bool Recalled,
        string Title,
        int ResourceCount);

    private sealed record QqResource(
        string Time,
        string Sender,
        string Uin,
        string Type,
        string Filename,
        long Size,
        string Url);

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
