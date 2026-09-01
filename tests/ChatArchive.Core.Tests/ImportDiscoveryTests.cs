using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public class ImportDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public ImportDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"chatarchive-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ImportDiscovery_DoesNotDiscoverEmbeddedHtml()
    {
        var html = Path.Combine(_tempDir, "chat.html");
        File.WriteAllText(html, """
            <script id="__DATA__" type="application/json">
            {"metadata":{"name":"QQChatExporter"},"chatInfo":{"name":"demo"},"messages":[]}
            </script>
            """);

        Assert.DoesNotContain(".html", ImportDiscovery.SupportedExtensions);
        Assert.DoesNotContain(".htm", ImportDiscovery.SupportedExtensions);
        Assert.Empty(ImportDiscovery.Discover(new[] { html }));
    }

    [Fact]
    public void ImportDiscovery_ScansAllSupportedFormats_And_SkipsMediaSubdirectories()
    {
        // 1. weflow/session.json (WeFlow JSON)
        var weflowDir = Path.Combine(_tempDir, "weflow");
        Directory.CreateDirectory(weflowDir);
        var weflowPath = Path.Combine(weflowDir, "session.json");
        File.WriteAllText(weflowPath, """
            {
              "weflow": {"version": "1.0.3"},
              "session": {"wxid": "wxid_weflow", "type": "私聊", "remark": "WeFlow测试"},
              "messages": [
                {"localId": 1, "createTime": 1700000000, "isSend": true, "type": "文本消息", "localType": 1, "content": "hello weflow"}
              ]
            }
            """);

        // 2. ciphertalk/export.json (CipherTalk JSON)
        var cipherTalkDir = Path.Combine(_tempDir, "ciphertalk");
        Directory.CreateDirectory(cipherTalkDir);
        var cipherTalkPath = Path.Combine(cipherTalkDir, "export.json");
        File.WriteAllText(cipherTalkPath, """
            {
              "exportInfo": {
                "version": "0.0.2",
                "generator": "CipherTalk",
                "format": "detailed-json"
              },
              "session": {
                "wxid": "wxid_ciphertalk",
                "displayName": "CipherTalk测试",
                "type": "私聊"
              },
              "messages": [
                {"localId": 1, "createTime": 1700000000, "type": "文本消息", "localType": 1, "content": "hello ciphertalk", "isSend": 0}
              ]
            }
            """);

        // 3. chatlab/export.jsonl (ChatLab JSONL)
        var chatlabDir = Path.Combine(_tempDir, "chatlab");
        Directory.CreateDirectory(chatlabDir);
        var chatlabPath = Path.Combine(chatlabDir, "export.jsonl");
        File.WriteAllLines(chatlabPath, new[]
        {
            """{"_type":"header","chatlab":{"version":"0.0.2","generator":"ChatLab"},"meta":{"name":"ChatLab测试","platform":"wechat","type":"private","ownerId":"wxid_self"}}""",
            """{"_type":"message","id":"m1","sender":"wxid_friend","timestamp":1700000000,"type":0,"content":"hello chatlab"}"""
        });

        // 4. qq_chunked/manifest.json & qq_chunked/chunks/chunk_0.jsonl
        var qqChunkedDir = Path.Combine(_tempDir, "qq_chunked");
        var qqChunksSubdir = Path.Combine(qqChunkedDir, "chunks");
        Directory.CreateDirectory(qqChunksSubdir);
        var qqManifestPath = Path.Combine(qqChunkedDir, "manifest.json");
        File.WriteAllText(qqManifestPath, """
            {
              "metadata": {"name": "QQChatExporter", "version": "0.2.0"},
              "chatInfo": {"selfUid": "u_self", "peerUid": "u_peer", "name": "QQ分块测试", "type": "group"}
            }
            """);
        var qqChunk0Path = Path.Combine(qqChunksSubdir, "chunk_0.jsonl");
        File.WriteAllText(qqChunk0Path, """{"id":"q1","timestamp":1700000000,"sender":{"uid":"u_self","name":"我自己"},"content":{"type":"text","text":"分块内容"}}""" + "\n");

        // 5. csv_export/records.csv (current WeFlow CSV)
        var csvDir = Path.Combine(_tempDir, "csv_export");
        Directory.CreateDirectory(csvDir);
        var csvPath = Path.Combine(csvDir, "records.csv");
        File.WriteAllText(csvPath, """
            id,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime
            1,9001,text,0,张三,hello csv,,2023-11-15T10:00:00.000Z
            """);

        // 6. qq_text/chat.txt (current QQ Chat Exporter V5 TXT)
        var qqTextDir = Path.Combine(_tempDir, "qq_text");
        Directory.CreateDirectory(qqTextDir);
        var qqTextPath = Path.Combine(qqTextDir, "chat.txt");
        File.WriteAllText(qqTextPath, """
            [QQChatExporter V5 / https://github.com/shuakami/qq-chat-exporter]
            [本软件是免费的开源项目~ 如果您是买来的，请立即退款！如果有帮助到您，欢迎给我点个Star~]

            ===============================================
                       QQ聊天记录导出文件
            ===============================================

            聊天名称: QQ TXT测试
            聊天类型: 私聊

            [1]
            Alice:
            时间: 2023-11-15 06:15:23
            内容: hello qq txt

            ===============================================
                          导出完成
            ===============================================
            总计导出 1 条消息
            """);

        // 7. sql_dump/backup.sql (current WeFlow SQL export)
        var sqlDir = Path.Combine(_tempDir, "sql_dump");
        Directory.CreateDirectory(sqlDir);
        var sqlPath = Path.Combine(sqlDir, "backup.sql");
        File.WriteAllText(sqlPath, """
            CREATE TABLE IF NOT EXISTS weflow_messages (
              session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
              create_time BIGINT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
              local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
            );
            INSERT INTO weflow_messages
              (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('wxid_sql', '1', 'm_1', 1700000000, 'wxid_sql', FALSE, 1, NULL, 'hello sql', NULL);
            """);

        // 8. excel_export/chat.xlsx (current WeFlow compact Excel export)
        var excelDir = Path.Combine(_tempDir, "excel_export");
        Directory.CreateDirectory(excelDir);
        var excelPath = Path.Combine(excelDir, "chat.xlsx");
        XlsxTestFile.Write(excelPath, new XlsxTestSheet("聊天记录", new IReadOnlyList<XlsxTestCell>[]
        {
            new XlsxTestCell[] { new("A1", "会话信息") },
            new XlsxTestCell[] { new("A2", "微信ID"), new("B2", "wxid_excel"), new("D2", "昵称"), new("E2", "Excel测试") },
            new XlsxTestCell[] { new("A3", "导出工具"), new("B3", "WeFlow"), new("C3", "导出版本"), new("D3", "1.0.3"), new("E3", "平台"), new("F3", "wechat") },
            new XlsxTestCell[] { new("A4", "序号"), new("B4", "时间"), new("C4", "发送者身份"), new("D4", "消息类型"), new("E4", "内容") },
            new XlsxTestCell[] { new("A5", "1"), new("B5", "2023-11-15 06:15:23"), new("C5", "对方"), new("D5", "文本消息"), new("E5", "hello excel") },
        }));

        // 9. ciphertalk_excel/chat.xlsx (current CipherTalk Excel export)
        var cipherTalkExcelDir = Path.Combine(_tempDir, "ciphertalk_excel");
        Directory.CreateDirectory(cipherTalkExcelDir);
        var cipherTalkExcelPath = Path.Combine(cipherTalkExcelDir, "chat.xlsx");
        XlsxTestFile.Write(cipherTalkExcelPath, new XlsxTestSheet("CipherTalk测试", new IReadOnlyList<XlsxTestCell>[]
        {
            new XlsxTestCell[]
            {
                new("A1", "序号"), new("B1", "时间"), new("C1", "日期"), new("D1", "时刻"),
                new("E1", "星期"), new("F1", "发送者"), new("G1", "微信ID"), new("H1", "消息类型"),
                new("I1", "消息内容"), new("J1", "原始类型代码"), new("K1", "时间戳")
            },
            new XlsxTestCell[]
            {
                new("A2", "1", "n"), new("B2", "2023-11-15 06:15:23"), new("C2", "2023/11/15"),
                new("D2", "06:15:23"), new("E2", "三"), new("F2", "Alice"), new("G2", "wxid_alice"),
                new("H2", "文本消息"), new("I2", "hello ciphertalk excel"), new("J2", "1", "n"),
                new("K2", "1700000123", "n")
            },
        }));

        // 10. qq_excel/chat.xlsx (current QQ Chat Exporter Excel export)
        var qqExcelDir = Path.Combine(_tempDir, "qq_excel");
        Directory.CreateDirectory(qqExcelDir);
        var qqExcelPath = Path.Combine(qqExcelDir, "chat.xlsx");
        XlsxTestFile.Write(qqExcelPath, new XlsxTestSheet("聊天记录", new IReadOnlyList<XlsxTestCell>[]
        {
            new XlsxTestCell[]
            {
                new("A1", "序号"), new("B1", "时间"), new("C1", "发送者"), new("D1", "发送者QQ号"),
                new("E1", "消息类型"), new("F1", "消息内容"), new("G1", "是否撤回"), new("H1", "资源数量")
            },
            new XlsxTestCell[]
            {
                new("A2", "1", "n"), new("B2", "2023-11-15 06:15:23"), new("C2", "Alice"),
                new("D2", "10002"), new("E2", "文本"), new("F2", "hello qq excel"),
                new("G2", "否"), new("H2", "0", "n")
            },
        }));

        // 媒体子目录与无关子目录，放置格式文件/垃圾文件，测试是否全部被跳过
        var mediaDirs = new[]
        {
            Path.Combine(_tempDir, "media"),
            Path.Combine(_tempDir, "resources"),
            Path.Combine(_tempDir, "images"),
            Path.Combine(_tempDir, "voices"),
            Path.Combine(_tempDir, "videos"),
            Path.Combine(_tempDir, "emojis"),
            Path.Combine(_tempDir, "files"),
            Path.Combine(_tempDir, "avatars"),
            Path.Combine(_tempDir, "node_modules"),
            Path.Combine(_tempDir, ".git"),
            Path.Combine(weflowDir, "media"),
            Path.Combine(cipherTalkDir, "resources")
        };

        foreach (var dir in mediaDirs)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "junk.txt"), "2023-11-15 10:00:00 张三: fake text");
            File.WriteAllText(Path.Combine(dir, "junk.json"), """{"weflow":{"version":"1.0.3"},"session":{"wxid":"fake"},"messages":[]}""");
            File.WriteAllText(Path.Combine(dir, "junk.sql"), "INSERT INTO messages (id, talker, content) VALUES ('1','2','fake');");
            File.WriteAllText(Path.Combine(dir, "junk.csv"), "is_sender,talker,content\n1,fake,fake");
        }

        // 执行扫描嗅探
        var discovered = ImportDiscovery.Discover(new[] { _tempDir });

        // 验证只发现了 10 个有效导出，无多余文件、无媒体目录污染、无 chunk_0.jsonl 独立识别
        Assert.Equal(10, discovered.Count);
        Assert.All(discovered, d => Assert.Null(d.Error));

        var discoveredDict = discovered.ToDictionary(
            d => Path.GetFullPath(d.FilePath),
            d => d.Platform,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(weflowPath)]);
        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(cipherTalkPath)]);
        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(chatlabPath)]);
        Assert.Equal("qq", discoveredDict[Path.GetFullPath(qqManifestPath)]);
        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(csvPath)]);
        Assert.Equal("qq", discoveredDict[Path.GetFullPath(qqTextPath)]);
        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(sqlPath)]);
        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(excelPath)]);
        Assert.Equal("wechat", discoveredDict[Path.GetFullPath(cipherTalkExcelPath)]);
        Assert.Equal("qq", discoveredDict[Path.GetFullPath(qqExcelPath)]);

        // 确保 chunk_0.jsonl 未被当作独立导出识别
        Assert.DoesNotContain(Path.GetFullPath(qqChunk0Path), discoveredDict.Keys);
    }

    [Fact]
    public void ImportDiscovery_LinkedQqManifest_DoesNotPruneValidSiblingJsonl()
    {
        var exportRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "linked-export")).FullName;
        var target = Path.Combine(_tempDir, "linked-target", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(
            target,
            """
            {
              "metadata":{"name":"QQChatExporter","version":"0.2.0"},
              "chatInfo":{"selfUid":"self","peerUid":"linked","name":"linked","type":"group"},
              "chunked":{"chunks":[]}
            }
            """);
        var manifest = Path.Combine(exportRoot, "manifest.json");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(manifest, target));
        Assert.True(File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint));
        var sibling = WriteValidChatLabJsonl(exportRoot, "sibling.jsonl", "linked-sibling");

        var discovered = ImportDiscovery.Discover([exportRoot]);

        var manifestResult = Assert.Single(
            discovered,
            item => string.Equals(item.FilePath, manifest, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(manifestResult.Error);
        Assert.Contains("链接文件", manifestResult.Error, StringComparison.Ordinal);
        var siblingResult = Assert.Single(
            discovered,
            item => string.Equals(item.FilePath, sibling, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("wechat", siblingResult.Platform);
        Assert.Null(siblingResult.Error);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"chunks\":[{\"relativePath\":\"../outside.jsonl\"}]}")]
    public void ImportDiscovery_ForeignManifestWithInvalidQqChunks_ContinuesToItsOwnMatcher(
        string chunkedJson)
    {
        var exportRoot = Directory.CreateDirectory(
            Path.Combine(_tempDir, "foreign-manifest")).FullName;
        var manifest = Path.Combine(exportRoot, "manifest.json");
        File.WriteAllText(
            manifest,
            $$"""
            {
              "weflow":{"version":"1.0.3"},
              "session":{"wxid":"wxid_foreign","type":"私聊","remark":"Foreign manifest"},
              "messages":[],
              "chunked":{{chunkedJson}}
            }
            """);

        var discovered = ImportDiscovery.Discover([exportRoot]);

        var result = Assert.Single(
            discovered,
            item => string.Equals(item.FilePath, manifest, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("wechat", result.Platform);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ImportDiscovery_MalformedStrictQqManifest_DoesNotPruneValidSiblingJsonl()
    {
        var exportRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "malformed-export")).FullName;
        var manifest = Path.Combine(exportRoot, "manifest.json");
        File.WriteAllText(
            manifest,
            """
            {
              "metadata":{"name":"QQChatExporter","version":"0.2.0"},
              "chatInfo":{"selfUid":"self","peerUid":"broken","name":"broken","type":"group"},
              "chunked":null
            }
            """);
        var sibling = WriteValidChatLabJsonl(exportRoot, "sibling.jsonl", "malformed-sibling");

        var discovered = ImportDiscovery.Discover([exportRoot]);

        var manifestResult = Assert.Single(
            discovered,
            item => string.Equals(item.FilePath, manifest, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(manifestResult.Error);
        Assert.Contains("chunked", manifestResult.Error, StringComparison.OrdinalIgnoreCase);
        var siblingResult = Assert.Single(
            discovered,
            item => string.Equals(item.FilePath, sibling, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("wechat", siblingResult.Platform);
        Assert.Null(siblingResult.Error);
    }

    private static string WriteValidChatLabJsonl(string directory, string fileName, string id)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllLines(path,
        [
            "{\"_type\":\"header\",\"chatlab\":{\"version\":\"0.0.2\",\"generator\":\"ChatLab\"},\"meta\":{\"name\":\"Sibling\",\"platform\":\"wechat\",\"type\":\"private\",\"ownerId\":\"self\"}}",
            $"{{\"_type\":\"message\",\"id\":\"{id}\",\"sender\":\"peer\",\"timestamp\":1700000000,\"type\":0,\"content\":\"sibling\"}}",
        ]);
        return path;
    }

    private static void CreateSymbolicLinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                or NotSupportedException)
        {
            Assert.Skip($"File symbolic links are unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
