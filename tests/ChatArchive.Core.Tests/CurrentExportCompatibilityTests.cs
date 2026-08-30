using ChatArchive.Core.Importing;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class CurrentExportCompatibilityTests
{
    private static readonly IReadOnlySet<Type> ExpectedAdapterTypes = new HashSet<Type>
    {
        typeof(QqExportFormat),
        typeof(QqChunkedExportFormat),
        typeof(WeFlowExportFormat),
        typeof(CipherTalkDetailedJsonFormat),
        typeof(ChatLabJsonExportFormat),
        typeof(ChatLabJsonlExportFormat),
        typeof(WeFlowCsvExportFormat),
        typeof(WeFlowMarkdownExportFormat),
        typeof(QqTextExportFormat),
        typeof(WeFlowTextExportFormat),
        typeof(WeFlowSqlExportFormat),
        typeof(CipherTalkSqlExportFormat),
        typeof(WeFlowExcelExportFormat),
        typeof(CipherTalkExcelExportFormat),
        typeof(QqExcelExportFormat),
    };

    [Theory]
    [InlineData("weflow-standard.json", typeof(WeFlowExportFormat), "wechat", "你好")]
    [InlineData("weflow-arkme.json", typeof(WeFlowExportFormat), "wechat", "你好")]
    [InlineData("ciphertalk-detailed.json", typeof(CipherTalkDetailedJsonFormat), "wechat", "你好")]
    [InlineData("ciphertalk-chatlab.json", typeof(ChatLabJsonExportFormat), "wechat", "你好")]
    [InlineData("chatlab-current.jsonl", typeof(ChatLabJsonlExportFormat), "wechat", "你好")]
    [InlineData("qq-single.json", typeof(QqExportFormat), "qq", "你好")]
    [InlineData("qq-chunked/manifest.json", typeof(QqChunkedExportFormat), "qq", "你好")]
    [InlineData("weflow-current.csv", typeof(WeFlowCsvExportFormat), "wechat", "你好")]
    [InlineData("weflow-current.md", typeof(WeFlowMarkdownExportFormat), "wechat", "![图片消息]")]
    [InlineData("weflow-current.txt", typeof(WeFlowTextExportFormat), "wechat", "你好")]
    [InlineData("qq-current.txt", typeof(QqTextExportFormat), "qq", "你好")]
    [InlineData("weflow-current.sql", typeof(WeFlowSqlExportFormat), "wechat", "你好")]
    [InlineData("ciphertalk-current.sql", typeof(CipherTalkSqlExportFormat), "wechat", "你好")]
    public void CurrentFixture_HasExactlyOneSourceAdapterAndOneExpectedMessage(
        string relativePath,
        Type expectedAdapterType,
        string expectedPlatform,
        string expectedContent)
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
        Assert.Contains(expectedContent, message.Content, StringComparison.Ordinal);
        Assert.True(message.TimestampMs > 0);
        Assert.False(string.IsNullOrWhiteSpace(message.SenderNativeId));
    }

    [Fact]
    public void CurrentTextFixtures_MatchCurrentWriterFieldsAndFraming()
    {
        using var standard = JsonDocument.Parse(File.ReadAllText(Fixture("weflow-standard.json")));
        var standardMessage = standard.RootElement.GetProperty("messages")[0];
        Assert.Equal("810000000000001", standardMessage.GetProperty("platformMessageId").GetString());
        Assert.Equal(
            standardMessage.GetProperty("senderUsername").GetString(),
            standardMessage.GetProperty("senderAvatarKey").GetString());

        using var arkMe = JsonDocument.Parse(File.ReadAllText(Fixture("weflow-arkme.json")));
        var arkMeSession = arkMe.RootElement.GetProperty("session");
        Assert.Equal("ArkMe 会话", arkMeSession.GetProperty("nickname").GetString());
        Assert.Equal(string.Empty, arkMeSession.GetProperty("remark").GetString());
        var arkMeMessage = arkMe.RootElement.GetProperty("messages")[0];
        Assert.Equal(1, arkMeMessage.GetProperty("localId").GetInt32());
        Assert.Equal("810000000000002", arkMeMessage.GetProperty("platformMessageId").GetString());

        var chatLabMessage = File.ReadLines(Fixture("chatlab-current.jsonl"))
            .Select(line => JsonDocument.Parse(line))
            .Single(document => document.RootElement.GetProperty("_type").GetString() == "message");
        using (chatLabMessage)
        {
            Assert.Equal(
                "810000000000005",
                chatLabMessage.RootElement.GetProperty("platformMessageId").GetString());
        }

        using var cipherDetailed = JsonDocument.Parse(File.ReadAllText(Fixture("ciphertalk-detailed.json")));
        Assert.Equal(
            "820000000000003",
            cipherDetailed.RootElement.GetProperty("messages")[0].GetProperty("platformMessageId").GetString());
        using var cipherChatLab = JsonDocument.Parse(File.ReadAllText(Fixture("ciphertalk-chatlab.json")));
        Assert.Equal(
            "820000000000004",
            cipherChatLab.RootElement.GetProperty("messages")[0].GetProperty("platformMessageId").GetString());

        var csvBytes = File.ReadAllBytes(Fixture("weflow-current.csv"));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, csvBytes[..3]);
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.EndsWith("\r\n", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", csvText.Replace("\r\n", string.Empty), StringComparison.Ordinal);
        var csvLines = File.ReadAllLines(Fixture("weflow-current.csv"));
        Assert.Equal("1,810000000000008", string.Join(',', csvLines[1].Split(',').Take(2)));
        var markdownBytes = File.ReadAllBytes(Fixture("weflow-current.md"));
        Assert.DoesNotContain((byte)'\r', markdownBytes);
        var markdown = Encoding.UTF8.GetString(markdownBytes);
        Assert.EndsWith("![图片消息](../images/layout-a.jpg)\n\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("你好，WeFlow Markdown", markdown, StringComparison.Ordinal);
        Assert.Contains("'810000000000012'", File.ReadAllText(Fixture("weflow-current.sql")));

        var weFlowText = File.ReadAllBytes(Fixture("weflow-current.txt"));
        Assert.DoesNotContain((byte)'\r', weFlowText);
        Assert.EndsWith("\n\n", Encoding.UTF8.GetString(weFlowText), StringComparison.Ordinal);

        var qqText = File.ReadAllText(Fixture("qq-current.txt")).Replace("\r\n", "\n");
        Assert.Contains(
            "消息总数: 1\n时间范围: 2023-11-26 12:00:11 - 2023-11-26 12:00:11\n",
            qqText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QqChunkedManifest_MatchesExactChunkBytesAndLines()
    {
        var manifestPath = Fixture("qq-chunked/manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var chunkInfo = Assert.Single(
            manifest.RootElement.GetProperty("chunked").GetProperty("chunks").EnumerateArray());
        var chunkPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            chunkInfo.GetProperty("relativePath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(chunkPath);

        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(bytes.LongLength, chunkInfo.GetProperty("bytes").GetInt64());
        Assert.Equal(
            chunkInfo.GetProperty("count").GetInt64(),
            Encoding.UTF8.GetString(bytes).Split('\n').LongCount(line => line.Length > 0));
    }

    [Fact]
    public void RegisteredSourceFormats_MatchHardCodedCurrentWriterOracle()
    {
        var registeredFormats = RegisteredSourceFormats();
        var registeredTypes = registeredFormats.Select(format => format.GetType()).ToArray();

        Assert.Equal(15, registeredFormats.Count);
        Assert.Equal(registeredTypes.Length, registeredTypes.Distinct().Count());
        Assert.True(
            ExpectedAdapterTypes.SetEquals(registeredTypes),
            $"expected=[{string.Join(", ", ExpectedAdapterTypes.Select(type => type.Name).Order())}], " +
            $"registered=[{string.Join(", ", registeredTypes.Select(type => type.Name).Order())}]");
    }

    [Theory]
    [InlineData("weflow-current.xlsx", typeof(WeFlowExcelExportFormat), "wechat")]
    [InlineData("ciphertalk-current.xlsx", typeof(CipherTalkExcelExportFormat), "wechat")]
    [InlineData("qq-current.xlsx", typeof(QqExcelExportFormat), "qq")]
    public void CurrentXlsxFixture_HasExactlyOneExplicitSourceAdapter(
        string fileName,
        Type expectedAdapterType,
        string expectedPlatform)
    {
        using var tree = CurrentExportTestTree.Create();
        var path = Path.Combine(tree.Root, "texts", fileName);
        var matches = RegisteredSourceFormats().Where(format => format.Matches(path)).ToList();

        var format = Assert.Single(matches);
        Assert.IsType(expectedAdapterType, format);
        Assert.Equal(expectedPlatform, format.Platform);

        using var export = format.Open(path);
        Assert.Equal(expectedPlatform, export.Conversation.Platform);
        Assert.Single(export.EnumerateMessages());
    }

    [Fact]
    public void CurrentXlsxFixtures_MatchExactOneMessageWriterRows()
    {
        using var tree = CurrentExportTestTree.Create();
        var texts = Path.Combine(tree.Root, "texts");

        using (var workbook = OpenXmlWorkbookReader.Open(Path.Combine(texts, "weflow-current.xlsx")))
        {
            var rows = workbook.ReadRows(Assert.Single(workbook.Sheets), CancellationToken.None).ToList();
            AssertCells(rows[1], (0, "微信ID"), (1, "fixture-weflow-excel"), (3, "昵称"), (4, "WeFlow Excel 发送者"));
            AssertCells(
                rows[2],
                (0, "导出工具"), (1, "WeFlow"), (2, "导出版本"), (3, "1.0.3"),
                (4, "平台"), (5, "wechat"), (6, "导出时间"), (7, "2023-11-26 12:00:14"));
            AssertCells(
                rows[4],
                (0, "1"), (1, "2023-11-26 12:00:14"), (2, "WeFlow Excel 发送者"),
                (3, "fixture-weflow-excel"), (4, "Excel 发送者"), (5, "Excel 发送者"),
                (6, "文本消息"), (7, "你好，WeFlow Excel"));
        }

        using (var workbook = OpenXmlWorkbookReader.Open(Path.Combine(texts, "ciphertalk-current.xlsx")))
        {
            var rows = workbook.ReadRows(Assert.Single(workbook.Sheets), CancellationToken.None).ToList();
            AssertCells(
                rows[1],
                (0, "1"), (1, "2023/11/26 12:00:15"), (2, "2023/11/26"), (3, "12:00:15"),
                (4, "日"), (5, "CipherTalk Excel 发送者"), (6, "fixture-sender-ciphertalk-excel"),
                (7, "文本消息"), (8, "你好，CipherTalk Excel"), (9, "1"), (10, "1701000015"));
        }

        using (var workbook = OpenXmlWorkbookReader.Open(Path.Combine(texts, "qq-current.xlsx")))
        {
            var messageRows = workbook.ReadRows(workbook.Sheets.Single(sheet => sheet.Name == "聊天记录"), CancellationToken.None).ToList();
            AssertCells(
                messageRows[1],
                (0, "1"), (1, "2023-11-26 12:00:16"), (2, "QQ Excel 发送者"), (3, "930003"),
                (4, "群主"), (5, "图片"), (6, "你好，QQ Excel"), (7, "否"), (8, "1"));

            var resourceRows = workbook.ReadRows(workbook.Sheets.Single(sheet => sheet.Name == "资源列表"), CancellationToken.None).ToList();
            AssertCells(
                resourceRows[1],
                (0, "1"), (1, "2023-11-26 12:00:16"), (2, "QQ Excel 发送者"), (3, "930003"),
                (4, "image"), (5, "qq-current.jpg"), (6, "4"), (7, "qq-media/qq-current.jpg"));
        }
    }

    [Fact]
    public void CurrentExportTestTree_CreateDeletesItsExactRootWhenConstructionThrows()
    {
        string? createdRoot = null;

        var error = Assert.Throws<InvalidOperationException>(() => CurrentExportTestTree.Create(root =>
        {
            createdRoot = root;
            throw new InvalidOperationException("fixture construction failed");
        }));

        Assert.Equal("fixture construction failed", error.Message);
        Assert.NotNull(createdRoot);
        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "chatarchive-current-exports-"),
            createdRoot,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(createdRoot));
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

        Assert.True(
            ExpectedAdapterTypes.SetEquals(matchedTypes),
            $"expected=[{string.Join(", ", ExpectedAdapterTypes.Select(type => type.Name).Order())}], " +
            $"matched=[{string.Join(", ", matchedTypes.Select(type => type.Name).Order())}]");
    }

    private static void AssertCells(OpenXmlRow row, params (int Column, string Value)[] expected)
    {
        Assert.Equal(expected.Length, row.Cells.Count);
        foreach (var (column, value) in expected)
        {
            Assert.Equal(value, row.Cells[column + 1].Value);
        }
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

    internal static CurrentExportTestTree Create(Action<string>? afterRootCreated = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"chatarchive-current-exports-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            afterRootCreated?.Invoke(root);

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
        catch
        {
            DeleteOwnedRootBestEffort(root);
            throw;
        }
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
                        new XlsxTestCell("E2", "WeFlow Excel 发送者"),
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
                        new XlsxTestCell("A5", "1", "n"),
                        new XlsxTestCell("B5", "2023-11-26 12:00:14"),
                        new XlsxTestCell("C5", "WeFlow Excel 发送者"),
                        new XlsxTestCell("D5", "fixture-weflow-excel"),
                        new XlsxTestCell("E5", "Excel 发送者"),
                        new XlsxTestCell("F5", "Excel 发送者"),
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
                        new XlsxTestCell("A2", "1", "n"),
                        new XlsxTestCell("B2", "2023/11/26 12:00:15"),
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
                        new XlsxTestCell("A2", "1", "n"),
                        new XlsxTestCell("B2", "2023-11-26 12:00:16"),
                        new XlsxTestCell("C2", "QQ Excel 发送者"),
                        new XlsxTestCell("D2", "930003"),
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
                        new XlsxTestCell("D2", "930003"),
                        new XlsxTestCell("E2", "image"),
                        new XlsxTestCell("F2", "qq-current.jpg"),
                        new XlsxTestCell("G2", "4", "n"),
                        new XlsxTestCell("H2", "qq-media/qq-current.jpg"),
                    ],
                ]));
    }

    public void Dispose()
    {
        DeleteOwnedRootBestEffort(Root);
    }

    private static void DeleteOwnedRootBestEffort(string root)
    {
        if (!IsOwnedRoot(root))
        {
            return;
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsOwnedRoot(string root)
    {
        const string Prefix = "chatarchive-current-exports-";
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(fullRoot), tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(fullRoot);
        return name.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[Prefix.Length..], "N", out _);
    }
}
