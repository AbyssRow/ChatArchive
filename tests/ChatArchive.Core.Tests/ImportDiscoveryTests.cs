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

        // 7. sql_dump/backup.sql (SQL 导出的 messages)
        var sqlDir = Path.Combine(_tempDir, "sql_dump");
        Directory.CreateDirectory(sqlDir);
        var sqlPath = Path.Combine(sqlDir, "backup.sql");
        File.WriteAllText(sqlPath, """
            CREATE TABLE messages (id TEXT, talker TEXT, create_time INTEGER, is_send INTEGER, type INTEGER, content TEXT);
            INSERT INTO messages (id, talker, create_time, is_send, type, content) VALUES ('m_1', 'wxid_sql', 1700000000, 0, 1, 'hello sql');
            """);

        // 10. 媒体子目录与无关子目录，放置格式文件/垃圾文件，测试是否全部被跳过
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

        // 验证只发现了 7 个有效导出，无多余文件、无媒体目录污染、无 chunk_0.jsonl 独立识别
        Assert.Equal(7, discovered.Count);
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
        Assert.Equal("sql", discoveredDict[Path.GetFullPath(sqlPath)]);

        // 确保 chunk_0.jsonl 未被当作独立导出识别
        Assert.DoesNotContain(Path.GetFullPath(qqChunk0Path), discoveredDict.Keys);
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
