using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class CipherTalkExcelFormatTests : IDisposable
{
    private static readonly string[] CoreHeaders =
    [
        "序号", "时间", "日期", "时刻", "星期", "发送者", "微信ID", "消息类型",
        "消息内容", "原始类型代码", "时间戳"
    ];

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-ciphertalk-excel-{Guid.NewGuid():N}");

    public CipherTalkExcelFormatTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void CipherTalkExcel_ParsesCurrentWorkbookAndPrefersNumericSecondsTimestamp()
    {
        var path = WriteWorkbook(
            "current.xlsx",
            [.. CoreHeaders, "头像链接", "聊天记录详情"],
            timestamp: "1700000123",
            time: "2000-01-01 00:00:00",
            messageType: "文本消息",
            rawType: "3",
            avatar: "https://example.test/alice.jpg",
            details: "转发详情");
        var format = new CipherTalkExcelExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("wechat", export.Conversation.Platform);
        Assert.Equal(ImportText.StableFileNativeId(path), export.Conversation.NativeId);
        Assert.Equal("工作表标题", export.Conversation.Title);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal(1700000123000, message.TimestampMs);
        Assert.Equal("wxid_alice", message.SenderNativeId);
        Assert.Equal("Alice", message.SenderName);
        Assert.Equal("image", message.MessageType);
        Assert.Equal("incoming", message.Direction);
        Assert.Equal("正文\n转发详情", message.Content);
        Assert.Contains("转发详情", message.SearchText);
        Assert.Equal("https://example.test/alice.jpg", message.RawPayload["头像链接"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("头像链接")]
    [InlineData("聊天记录详情")]
    [InlineData("头像链接|聊天记录详情")]
    public void CipherTalkExcel_MatchesOnlyMeaningfulOptionalColumnCombinations(string suffix)
    {
        var optionalHeaders = suffix.Length == 0 ? [] : suffix.Split('|');
        var path = WriteWorkbook(
            $"optional-{optionalHeaders.Length}-{suffix.GetHashCode():x}.xlsx",
            [.. CoreHeaders, .. optionalHeaders],
            avatar: "https://example.test/avatar.jpg",
            details: "一条转发记录");

        var format = new CipherTalkExcelExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal(optionalHeaders.Contains("聊天记录详情") ? "正文\n一条转发记录" : "正文", message.Content);
    }

    [Fact]
    public void CipherTalkExcel_FallsBackToDisplayedTimeAndSyntheticSender()
    {
        var path = WriteWorkbook(
            "fallback.xlsx",
            CoreHeaders,
            timestamp: string.Empty,
            time: "2023-11-15 06:15:23",
            senderId: string.Empty,
            messageType: "图片消息",
            rawType: "999");

        using var export = new CipherTalkExcelExportFormat().Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal(1700000123000, message.TimestampMs);
        Assert.StartsWith("synthetic:", message.SenderNativeId, StringComparison.Ordinal);
        Assert.Equal("image", message.MessageType);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reversed-optionals")]
    [InlineData("reordered-core")]
    [InlineData("header-not-first-row")]
    public void CipherTalkExcel_RejectsNonProducerHeaders(string mutation)
    {
        var headers = mutation switch
        {
            "unknown" => [.. CoreHeaders, "未知列"],
            "reversed-optionals" => [.. CoreHeaders, "聊天记录详情", "头像链接"],
            "reordered-core" => CoreHeaders.Select((header, index) => index switch
            {
                5 => CoreHeaders[6],
                6 => CoreHeaders[5],
                _ => header,
            }).ToArray(),
            "header-not-first-row" => CoreHeaders,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var path = WriteWorkbook(
            $"negative-{mutation}.xlsx",
            headers,
            leadingBlankRow: mutation == "header-not-first-row");

        Assert.False(new CipherTalkExcelExportFormat().Matches(path));
    }

    [Fact]
    public void CipherTalkExcel_HonorsCancellationWhileOpening()
    {
        var path = WriteWorkbook("cancel.xlsx", CoreHeaders);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new CipherTalkExcelExportFormat().Open(path, cancellation.Token));
    }

    [Fact]
    public void CipherTalkExcel_EarlyIteratorDisposalReleasesWorkbookFile()
    {
        var path = WriteWorkbook("dispose.xlsx", CoreHeaders);
        using (var export = new CipherTalkExcelExportFormat().Open(path))
        using (var messages = export.EnumerateMessages().GetEnumerator())
        {
            Assert.True(messages.MoveNext());
        }

        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    private string WriteWorkbook(
        string filename,
        IReadOnlyList<string> headers,
        string timestamp = "1700000123",
        string time = "2023-11-15 06:15:23",
        string senderId = "wxid_alice",
        string messageType = "文本消息",
        string rawType = "1",
        string avatar = "",
        string details = "",
        bool leadingBlankRow = false)
    {
        var path = Path.Combine(_directory, filename);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["序号"] = "1",
            ["时间"] = time,
            ["日期"] = "2023/11/15",
            ["时刻"] = "06:15:23",
            ["星期"] = "三",
            ["发送者"] = "Alice",
            ["微信ID"] = senderId,
            ["消息类型"] = messageType,
            ["消息内容"] = "正文",
            ["原始类型代码"] = rawType,
            ["时间戳"] = timestamp,
            ["头像链接"] = avatar,
            ["聊天记录详情"] = details,
            ["未知列"] = "unexpected",
        };
        var headerRow = leadingBlankRow ? 2 : 1;
        var messageRow = headerRow + 1;
        var rows = new List<IReadOnlyList<XlsxTestCell>>();
        if (leadingBlankRow)
        {
            rows.Add([]);
        }

        rows.Add(headers.Select((header, index) =>
            new XlsxTestCell($"{ColumnName(index + 1)}{headerRow}", header)).ToArray());
        rows.Add(headers.Select((header, index) =>
            new XlsxTestCell(
                $"{ColumnName(index + 1)}{messageRow}",
                values[header],
                header is "序号" or "原始类型代码" or "时间戳" && values[header].Length > 0 ? "n" : "inlineStr"))
            .ToArray());
        XlsxTestFile.Write(path, new XlsxTestSheet("工作表标题", rows));
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
