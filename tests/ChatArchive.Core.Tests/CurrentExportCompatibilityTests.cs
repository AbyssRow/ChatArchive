using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class CurrentExportCompatibilityTests
{
    [Theory]
    [InlineData("weflow-standard.json", typeof(WeFlowExportFormat), "wechat")]
    [InlineData("weflow-arkme.json", typeof(WeFlowExportFormat), "wechat")]
    [InlineData("ciphertalk-detailed.json", typeof(CipherTalkDetailedJsonFormat), "wechat")]
    [InlineData("ciphertalk-chatlab.json", typeof(ChatLabJsonExportFormat), "wechat")]
    [InlineData("chatlab-current.jsonl", typeof(ChatLabJsonlExportFormat), "wechat")]
    [InlineData("qq-single.json", typeof(QqExportFormat), "qq")]
    [InlineData("qq-chunked/manifest.json", typeof(QqChunkedExportFormat), "qq")]
    [InlineData("weflow-current.csv", typeof(WeFlowCsvExportFormat), "wechat")]
    [InlineData("weflow-current.md", typeof(WeFlowMarkdownExportFormat), "wechat")]
    [InlineData("weflow-current.txt", typeof(WeFlowTextExportFormat), "wechat")]
    [InlineData("qq-current.txt", typeof(QqTextExportFormat), "qq")]
    [InlineData("weflow-current.sql", typeof(WeFlowSqlExportFormat), "wechat")]
    [InlineData("ciphertalk-current.sql", typeof(CipherTalkSqlExportFormat), "wechat")]
    public void CurrentFixture_HasExactlyOneSourceAdapterAndOneExpectedMessage(
        string relativePath,
        Type expectedAdapterType,
        string expectedPlatform)
    {
        var path = Fixture(relativePath);
        var matches = RegisteredSourceFormats()
            .Where(format => format.Matches(path))
            .ToList();

        var format = Assert.Single(matches);
        Assert.IsType(expectedAdapterType, format);
        Assert.Equal(expectedPlatform, format.Platform);
        Assert.DoesNotContain("Html", format.GetType().Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Generic", format.GetType().Name, StringComparison.OrdinalIgnoreCase);

        using var export = format.Open(path);
        Assert.Equal(expectedPlatform, export.Conversation.Platform);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Contains("你好", message.Content, StringComparison.Ordinal);
        Assert.True(message.TimestampMs > 0);
        Assert.False(string.IsNullOrWhiteSpace(message.SenderNativeId));
    }

    [Fact]
    public void CurrentFormats_MixedDirectoryDiscoversEveryRegisteredAdapterExactlyOnce()
    {
        using var tree = CurrentExportTestTree.Create();
        var registeredFormats = RegisteredSourceFormats();

        var discovered = ImportDiscovery.Discover([tree.Root], registeredFormats);

        Assert.Equal(16, discovered.Count);
        Assert.All(discovered, item => Assert.Null(item.Error));
        Assert.All(discovered, item => Assert.Contains(item.Platform, new[] { "wechat", "qq" }));
        Assert.DoesNotContain(
            discovered,
            item => tree.NonExportCandidates.Contains(
                Path.GetFullPath(item.FilePath),
                StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            discovered,
            item => item.FilePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase));

        var matchedTypes = new HashSet<Type>();
        foreach (var item in discovered)
        {
            var matches = registeredFormats
                .Where(format => format.Matches(item.FilePath))
                .ToList();
            var match = Assert.Single(matches);
            Assert.Equal(item.Platform, match.Platform);
            matchedTypes.Add(match.GetType());
        }

        var registeredTypes = registeredFormats
            .Select(format => format.GetType())
            .ToHashSet();
        Assert.True(
            registeredTypes.SetEquals(matchedTypes),
            $"registered=[{string.Join(", ", registeredTypes.Select(type => type.Name).Order())}], " +
            $"matched=[{string.Join(", ", matchedTypes.Select(type => type.Name).Order())}]");
    }

    private static string Fixture(string relativePath) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "CurrentExports",
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<IChatExportFormat> RegisteredSourceFormats() => ExportFormats.Default
        .Where(format => format.GetType().Assembly == typeof(ExportFormats).Assembly)
        .ToArray();
}

internal sealed class CurrentExportTestTree : IDisposable
{
    private CurrentExportTestTree(string root, IReadOnlyList<string> nonExportCandidates)
    {
        Root = root;
        NonExportCandidates = nonExportCandidates;
    }

    internal string Root { get; }

    internal IReadOnlyList<string> NonExportCandidates { get; }

    internal static CurrentExportTestTree Create()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"chatarchive-current-exports-{Guid.NewGuid():N}");
        var texts = Path.Combine(root, "texts");
        Directory.CreateDirectory(texts);
        CopyDirectory(FixtureRoot(), texts);

        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "layout-a.jpg"), [1, 2, 3, 4]);

        WriteWeFlowWorkbook(Path.Combine(texts, "weflow-current.xlsx"));
        WriteCipherTalkWorkbook(Path.Combine(texts, "ciphertalk-current.xlsx"));
        WriteQqWorkbook(Path.Combine(texts, "qq-current.xlsx"));

        var html = Path.Combine(texts, "presentation.html");
        File.WriteAllText(html, "<html><body>browser presentation only</body></html>");
        var unrelatedText = Path.Combine(texts, "unrelated-notes.txt");
        File.WriteAllText(unrelatedText, "This is not a chat export.");
        var unrelatedSql = Path.Combine(texts, "unrelated-database.sql");
        File.WriteAllText(
            unrelatedSql,
            "CREATE TABLE notes (id INTEGER, body TEXT); INSERT INTO notes VALUES (1, 'not chat');");
        var unrelatedXlsx = Path.Combine(texts, "unrelated-workbook.xlsx");
        XlsxTestFile.Write(
            unrelatedXlsx,
            new XlsxTestSheet(
                "Data",
                [[new XlsxTestCell("A1", "name"), new XlsxTestCell("B1", "value")]]));

        return new CurrentExportTestTree(
            root,
            [
                Path.GetFullPath(Path.Combine(texts, "README.md")),
                Path.GetFullPath(html),
                Path.GetFullPath(unrelatedText),
                Path.GetFullPath(unrelatedSql),
                Path.GetFullPath(unrelatedXlsx),
                Path.GetFullPath(Path.Combine(texts, "qq-chunked", "chunks", "c000001.jsonl")),
            ]);
    }

    private static string FixtureRoot() => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "CurrentExports");

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void WriteWeFlowWorkbook(string path)
    {
        XlsxTestFile.Write(
            path,
            new XlsxTestSheet(
                "聊天记录",
                [
                    [new XlsxTestCell("A1", "会话信息")],
                    [
                        new XlsxTestCell("A2", "微信ID"),
                        new XlsxTestCell("B2", "fixture-weflow-excel"),
                        new XlsxTestCell("D2", "昵称"),
                        new XlsxTestCell("E2", "WeFlow Excel"),
                        new XlsxTestCell("F2", "备注"),
                        new XlsxTestCell("G2", "WeFlow Excel"),
                    ],
                    [
                        new XlsxTestCell("A3", "导出工具"),
                        new XlsxTestCell("B3", "WeFlow"),
                        new XlsxTestCell("C3", "导出版本"),
                        new XlsxTestCell("D3", "1.0.3"),
                        new XlsxTestCell("E3", "平台"),
                        new XlsxTestCell("F3", "wechat"),
                        new XlsxTestCell("G3", "导出时间"),
                        new XlsxTestCell("H3", "2023-11-26 12:00:14"),
                    ],
                    [
                        new XlsxTestCell("A4", "序号"),
                        new XlsxTestCell("B4", "时间"),
                        new XlsxTestCell("C4", "发送者昵称"),
                        new XlsxTestCell("D4", "发送者微信ID"),
                        new XlsxTestCell("E4", "发送者备注"),
                        new XlsxTestCell("F4", "发送者身份"),
                        new XlsxTestCell("G4", "消息类型"),
                        new XlsxTestCell("H4", "内容"),
                    ],
                    [
                        new XlsxTestCell("A5", "14", "n"),
                        new XlsxTestCell("B5", "2023-11-26 12:00:14"),
                        new XlsxTestCell("C5", "WeFlow Excel 发送者"),
                        new XlsxTestCell("D5", "fixture-sender-weflow-excel"),
                        new XlsxTestCell("E5", "Excel 发送者"),
                        new XlsxTestCell("F5", "对方"),
                        new XlsxTestCell("G5", "文本消息"),
                        new XlsxTestCell("H5", "你好，WeFlow Excel"),
                    ],
                ]));
    }

    private static void WriteCipherTalkWorkbook(string path)
    {
        XlsxTestFile.Write(
            path,
            new XlsxTestSheet(
                "CipherTalk Excel",
                [
                    [
                        new XlsxTestCell("A1", "序号"),
                        new XlsxTestCell("B1", "时间"),
                        new XlsxTestCell("C1", "日期"),
                        new XlsxTestCell("D1", "时刻"),
                        new XlsxTestCell("E1", "星期"),
                        new XlsxTestCell("F1", "发送者"),
                        new XlsxTestCell("G1", "微信ID"),
                        new XlsxTestCell("H1", "消息类型"),
                        new XlsxTestCell("I1", "消息内容"),
                        new XlsxTestCell("J1", "原始类型代码"),
                        new XlsxTestCell("K1", "时间戳"),
                    ],
                    [
                        new XlsxTestCell("A2", "15", "n"),
                        new XlsxTestCell("B2", "2023-11-26 12:00:15"),
                        new XlsxTestCell("C2", "2023/11/26"),
                        new XlsxTestCell("D2", "12:00:15"),
                        new XlsxTestCell("E2", "日"),
                        new XlsxTestCell("F2", "CipherTalk Excel 发送者"),
                        new XlsxTestCell("G2", "fixture-sender-ciphertalk-excel"),
                        new XlsxTestCell("H2", "文本消息"),
                        new XlsxTestCell("I2", "你好，CipherTalk Excel"),
                        new XlsxTestCell("J2", "1", "n"),
                        new XlsxTestCell("K2", "1701000015", "n"),
                    ],
                ]));
    }

    private static void WriteQqWorkbook(string path)
    {
        var media = Path.Combine(Path.GetDirectoryName(path)!, "qq-media");
        Directory.CreateDirectory(media);
        File.WriteAllBytes(Path.Combine(media, "qq-current.jpg"), [5, 6, 7, 8]);

        XlsxTestFile.Write(
            path,
            new XlsxTestSheet(
                "聊天记录",
                [
                    [
                        new XlsxTestCell("A1", "序号"),
                        new XlsxTestCell("B1", "时间"),
                        new XlsxTestCell("C1", "发送者"),
                        new XlsxTestCell("D1", "发送者QQ号"),
                        new XlsxTestCell("E1", "群头衔"),
                        new XlsxTestCell("F1", "消息类型"),
                        new XlsxTestCell("G1", "消息内容"),
                        new XlsxTestCell("H1", "是否撤回"),
                        new XlsxTestCell("I1", "资源数量"),
                    ],
                    [
                        new XlsxTestCell("A2", "16", "n"),
                        new XlsxTestCell("B2", "2023-11-26 12:00:16"),
                        new XlsxTestCell("C2", "QQ Excel 发送者"),
                        new XlsxTestCell("D2", "fixture-sender-qq-excel"),
                        new XlsxTestCell("E2", "群主"),
                        new XlsxTestCell("F2", "图片"),
                        new XlsxTestCell("G2", "你好，QQ Excel"),
                        new XlsxTestCell("H2", "否"),
                        new XlsxTestCell("I2", "1", "n"),
                    ],
                ]),
            new XlsxTestSheet(
                "资源列表",
                [
                    [
                        new XlsxTestCell("A1", "序号"),
                        new XlsxTestCell("B1", "时间"),
                        new XlsxTestCell("C1", "发送者"),
                        new XlsxTestCell("D1", "发送者QQ号"),
                        new XlsxTestCell("E1", "资源类型"),
                        new XlsxTestCell("F1", "文件名"),
                        new XlsxTestCell("G1", "大小(字节)"),
                        new XlsxTestCell("H1", "URL"),
                    ],
                    [
                        new XlsxTestCell("A2", "1", "n"),
                        new XlsxTestCell("B2", "2023-11-26 12:00:16"),
                        new XlsxTestCell("C2", "QQ Excel 发送者"),
                        new XlsxTestCell("D2", "fixture-sender-qq-excel"),
                        new XlsxTestCell("E2", "image"),
                        new XlsxTestCell("F2", "qq-current.jpg"),
                        new XlsxTestCell("G2", "4", "n"),
                        new XlsxTestCell("H2", "qq-media/qq-current.jpg"),
                    ],
                ]));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
