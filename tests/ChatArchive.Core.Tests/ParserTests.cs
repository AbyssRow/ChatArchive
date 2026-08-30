using System.Text.Json;
using System.Text.Json.Nodes;
using ChatArchive.Core.Importing;
using Xunit;

namespace ChatArchive.Core.Tests;

public class ParserTests : IDisposable
{
    private readonly string _dir;

    public ParserTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"chatarchive-parse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void StableFileNativeId_IsRepeatableAndPathSpecific()
    {
        var first = Path.Combine(_dir, "a", "chat.txt");
        var second = Path.Combine(_dir, "b", "chat.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);

        var id = ImportText.StableFileNativeId(first);
        Assert.Matches("^file:[0-9a-f]{64}$", id);
        Assert.Equal(id, ImportText.StableFileNativeId(first));
        Assert.NotEqual(id, ImportText.StableFileNativeId(second));
    }

    // ---------- CanonicalJson ----------

    [Fact]
    public void CanonicalJson_sorts_keys_and_compacts()
    {
        var node = JsonNode.Parse("""{"b":1,"a":{"d":true,"c":[1,2]},"e":null}""");
        Assert.Equal("""{"a":{"c":[1,2],"d":true},"b":1,"e":null}""", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void CanonicalJson_matches_python_number_and_string_rules()
    {
        var node = new JsonObject
        {
            ["f1"] = 3.0d,
            ["f2"] = 3.2d,
            ["i"] = 42L,
            ["s"] = "引\"号\\斜杠\n换行",
            ["tab"] = "a\tb",
        };

        var json = CanonicalJson.Serialize(node);
        Assert.Contains("\"f1\":3.0", json);
        Assert.Contains("\"f2\":3.2", json);
        Assert.Contains("\"i\":42", json);
        Assert.Contains("\"s\":\"引\\\"号\\\\斜杠\\n换行\"", json);
        Assert.Contains("\"tab\":\"a\\tb\"", json);
    }

    [Fact]
    public void Canonical_hash_is_stable()
    {
        var a = JsonNode.Parse("""{"x":"值","y":[1,2.50]}""");
        var b = JsonNode.Parse("""{ "y" : [ 1, 2.5 ], "x" : "\u503C" }""");
        Assert.Equal(CanonicalJson.HashHex(a), CanonicalJson.HashHex(b));
    }

    [Fact]
    public void CanonicalJson_FormatsNegativeZeroAndDecimalCorrectly()
    {
        Assert.Equal("-0.0", CanonicalJson.FormatDouble(-0.0));
        Assert.Equal("1234567890123456789.12", CanonicalJson.Serialize(System.Text.Json.Nodes.JsonValue.Create(1234567890123456789.12m)));
    }

    // ---------- QQ ----------

    private const string QqFixture = """
        {
          "metadata": {"name": "QQChatExporter", "version": "0.1.0"},
          "chatInfo": {"selfUin": "10001", "selfUid": "uSELF", "peerUid": "uPEER", "peerUin": "12345", "name": "老张", "type": "private"},
          "messages": [
            {
              "id": "m1", "timestamp": 1700000000000, "type": "text", "seq": 5,
              "sender": {"uid": "uPEER", "uin": "12345", "groupCard": "小李", "nickname": "Li"},
              "content": {"text": "你好",
                          "elements": [{"type": "reply", "data": {"referencedMessageId": "m0"}}],
                          "summary": "你好摘要"},
              "recalled": false
            },
            {
              "id": "m2", "timestamp": 1700000005000, "type": "image",
              "sender": {"uid": "uSELF", "uin": "10001", "nickname": "我"},
              "content": {"text": "",
                          "resources": [{"type": "image", "localPath": "resources/images/pic.jpg",
                                          "width": 800, "height": 600, "md5": "abc"}]}
            }
          ]
        }
        """;

    [Fact]
    public void Qq_parses_conversation_messages_attachments()
    {
        var path = Path.Combine(_dir, "qq.json");
        File.WriteAllText(path, QqFixture);

        using var document = ImportText.ParseDocument(path);
        var conversation = QqParser.ReadConversation(document, path);
        Assert.Equal("qq", conversation.Platform);
        Assert.Equal("10001", conversation.AccountId);
        Assert.Equal("uPEER", conversation.NativeId);
        Assert.Equal("private", conversation.Kind);
        Assert.Equal("老张", conversation.Title);

        var messages = QqParser.IterateMessages(document, conversation, path).ToList();
        Assert.Equal(2, messages.Count);

        var first = messages[0];
        Assert.Equal("m1", first.NativeId);
        Assert.Equal("小李", first.SenderName);
        Assert.Equal(new[] { "小李", "Li", "12345" }, first.SenderAliases);
        Assert.Equal("incoming", first.Direction);
        Assert.Equal("你好", first.Content);
        Assert.Contains("你好摘要", first.SearchText);
        Assert.Equal("m0", first.ReplyToNativeId);
        Assert.Single(first.CompatiblePayloadHashes);

        var second = messages[1];
        Assert.Equal("outgoing", second.Direction);
        Assert.Equal("image", second.MediaType);
        var attachment = Assert.Single(second.Attachments);
        Assert.Equal("image", attachment.Kind);
        Assert.Null(attachment.Filename);
        Assert.Equal(800, attachment.Width);
        Assert.Equal(600, attachment.Height);
        Assert.Equal("abc", ImportText.Clean(attachment.Metadata["md5"]));
        Assert.EndsWith($"resources{Path.DirectorySeparatorChar}images{Path.DirectorySeparatorChar}pic.jpg", attachment.SourcePath ?? string.Empty);
    }

    // ---------- WeFlow ----------

    private const string WeFlowFixture = """
        {
          "weflow": {"version": "1.0.3"},
          "session": {"wxid": "wxid_zhang", "type": "私聊", "remark": "张三"},
          "messages": [
            {"localId": 1, "createTime": 1700000000, "isSend": true,
             "senderUsername": "wxid_me", "senderDisplayName": "",
             "type": "文本消息", "localType": 1, "content": "在吗"},
            {"localId": 2, "createTime": 1700000060, "isSend": false,
             "senderUsername": "wxid_zhang", "senderDisplayName": "张三",
             "type": "图片消息", "content": "MSG/images/cat.jpg"},
            {"localId": 3, "createTime": 1700000120, "isSend": false,
             "senderUsername": "wxid_zhang",
             "type": "系统消息", "localType": 10000, "content": "你撤回了一条消息"}
          ]
        }
        """;

    [Fact]
    public void Weflow_parses_session_self_sender_media()
    {
        var path = Path.Combine(_dir, "weflow.json");
        File.WriteAllText(path, WeFlowFixture);

        using var document = ImportText.ParseDocument(path);
        var (conversation, selfSender) = WeFlowParser.ReadConversation(document, path);
        Assert.Equal("wechat-default", conversation.AccountId);
        Assert.Equal("wxid_zhang", conversation.NativeId);
        Assert.Equal("private", conversation.Kind);
        Assert.Equal("张三", conversation.Title);
        Assert.Equal("wxid_me", selfSender);

        var messages = WeFlowParser.IterateMessages(document, conversation, selfSender, path).ToList();
        Assert.Equal(3, messages.Count);

        var sent = messages[0];
        Assert.Equal("我", sent.SenderName);
        Assert.Equal("outgoing", sent.Direction);
        Assert.Equal("text", sent.MessageType);
        Assert.Equal("在吗", sent.Content);

        var received = messages[1];
        Assert.Equal("incoming", received.Direction);
        Assert.Equal("image", received.MessageType);
        Assert.Equal("cat.jpg", Assert.Single(received.Attachments).Filename);
        Assert.Equal("MSG/images/cat.jpg", received.Attachments[0].DeclaredPath);

        var system = messages[2];
        Assert.True(system.IsSystem);
        Assert.True(system.IsRecalled);
        Assert.Equal("system", system.Direction);
        Assert.StartsWith("message:2:local:", system.SourceLocator);
    }

    [Fact]
    public void Weflow_xml_payment_message_summarized()
    {
        var xml = """<msg><appmsg><wcpayinfo><scenetext><![CDATA[转账]]></scenetext><receivertitle><![CDATA[给你转账]]></receivertitle><receiverdes><![CDATA[¥10.00]]></receiverdes></wcpayinfo></appmsg></msg>""";
        var result = WeFlowParser.SummarizeXml(xml);
        Assert.NotNull(result);
        Assert.Equal("transfer", result!.Value.Type);
        Assert.Equal("[转账] 给你转账 - ¥10.00", result.Value.Summary);
        Assert.Equal(new[] { "转账", "给你转账", "¥10.00" }, result.Value.ExtraSearch);
    }

    [Fact]
    public void Weflow_title_falls_back_to_folder_name()
    {
        var sub = Path.Combine(_dir, "群聊_开发组_全部时间");
        Directory.CreateDirectory(sub);
        var path = Path.Combine(sub, "dump.json");
        File.WriteAllText(path, WeFlowFixture.Replace("\"remark\": \"张三\"", "\"remark\": \"\""));

        using var document = ImportText.ParseDocument(path);
        var (conversation, _) = WeFlowParser.ReadConversation(document, path);
        Assert.Equal("开发组", conversation.Title);
    }

    // ---------- Discovery ----------

    [Fact]
    public void Discovery_finds_supported_files_only()
    {
        var root = Path.Combine(_dir, "exports");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var excluded = Path.Combine(_dir, "keep-out");
        Directory.CreateDirectory(excluded);

        File.WriteAllText(Path.Combine(root, "a_qq.json"), QqFixture);
        File.WriteAllText(Path.Combine(root, "nested", "b_weflow.json"), WeFlowFixture);
        File.WriteAllText(Path.Combine(root, "notes.txt"), QqFixture);
        File.WriteAllText(Path.Combine(root, "random.json"), "{\"hello\":1}");
        File.WriteAllText(Path.Combine(excluded, "skip.json"), QqFixture);

        var found = ImportDiscovery.Discover(new[] { root, excluded }, excludedRoots: new[] { excluded });
        Assert.Equal(2, found.Count);
        Assert.Equal("qq", found[0].Platform);
        Assert.Equal("wechat", found[1].Platform);
    }

    [Fact]
    public void Discovery_finds_format_markers_after_large_leading_property()
    {
        var root = Path.Combine(_dir, "late-markers");
        Directory.CreateDirectory(root);
        var padding = new string('x', 12_000);
        File.WriteAllText(Path.Combine(root, "qq.json"), $$"""
            {"padding":"{{padding}}","metadata":{"name":"QQChatExporter","version":"0.1.0"},
             "chatInfo":{"selfUin":"1","peerUid":"p","name":"n"},"messages":[]}
            """);
        File.WriteAllText(Path.Combine(root, "weflow.json"), $$"""
            {"padding":"{{padding}}","weflow":{"version":"1.0.3"},
             "session":{"wxid":"p","type":"私聊"},"messages":[]}
            """);

        var found = ImportDiscovery.Discover(new[] { root });

        Assert.Equal(new[] { "qq", "wechat" }, found.Select(file => file.Platform));
    }

    [Fact]
    public void Discovery_reports_unreadable_json_instead_of_silently_omitting_it()
    {
        var root = Path.Combine(_dir, "locked-export");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "locked.json");
        File.WriteAllText(path, QqFixture);
        using var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var found = ImportDiscovery.Discover(new[] { root });

        var candidate = Assert.Single(found);
        Assert.Equal("unknown", candidate.Platform);
        Assert.NotNull(candidate.Error);
    }

    [Fact]
    public void Discovery_reports_an_explicit_missing_root()
    {
        var missing = Path.Combine(_dir, "missing-root");

        var found = ImportDiscovery.Discover(new[] { missing });

        var candidate = Assert.Single(found);
        Assert.Equal("unknown", candidate.Platform);
        Assert.Contains("无法访问导入路径", candidate.Error);
    }

    [Fact]
    public void Discovery_ignores_json_with_wrong_qq_exporter_name()
    {
        var root = Path.Combine(_dir, "other-exporter");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "other.json"), """
            {"metadata":{"name":"OtherExporter","version":"0.1.0"},
             "chatInfo":{"selfUin":"1","peerUid":"p","name":"n"},
             "messages":[]}
            """);

        var found = ImportDiscovery.Discover(new[] { root });

        Assert.Empty(found);
    }

    [Fact]
    public void Discovery_does_not_follow_directory_symbolic_links()
    {
        var root = Path.Combine(_dir, "link-root");
        var outside = Path.Combine(_dir, "outside-root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.json"), QqFixture);
        var link = Path.Combine(root, "linked-directory");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var found = ImportDiscovery.Discover(new[] { root });

        Assert.DoesNotContain(found, item => item.Platform == "qq");
        Assert.Contains(found, item => item.Error?.Contains("链接目录") == true);
    }

    [Fact]
    public void ExportFile_factory_routes_by_format()
    {
        var formats = ExportFormats.Default;
        var qqPath = Path.Combine(_dir, "x.json");
        File.WriteAllText(qqPath, QqFixture);

        var format = formats.Single(f => f.Matches(qqPath));
        Assert.Equal("qq", format.Platform);
        using var exportFile = format.Open(qqPath);
        Assert.Equal("老张", exportFile.Conversation.Title);
        Assert.Single(exportFile.EnumerateMessages().Take(1));
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("1.0.0")]
    public void QqExportFormat_AcceptsNewerVersions_And_RelaxedMetadata(string version)
    {
        var path = Path.Combine(_dir, $"qq-v{version}.json");
        File.WriteAllText(path, $$"""
            {
              "metadata": {"name": "QQChatExporter", "version": "{{version}}"},
              "chatInfo": {"selfUin": "10001", "selfUid": "uSELF", "peerUid": "uPEER", "name": "测试群", "type": "group"},
              "messages": [
                {
                  "id": "m1", "timestamp": 1700000000000, "type": "text",
                  "sender": {"uid": "uPEER", "name": "好友"},
                  "content": {"text": "hello"}
                }
              ]
            }
            """);

        var format = new QqExportFormat();
        Assert.True(format.Matches(path));

        using var exportFile = format.Open(path);
        Assert.Equal("qq", exportFile.Conversation.Platform);
        Assert.Equal("uPEER", exportFile.Conversation.NativeId);
        Assert.Equal("group", exportFile.Conversation.Kind);
        Assert.Equal("测试群", exportFile.Conversation.Title);

        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Single(messages);
        Assert.Equal("m1", messages[0].NativeId);
        Assert.Equal("hello", messages[0].Content);
    }

    [Fact]
    public void Qq_accepts_real_export_metadata_version()
    {
        var path = Path.Combine(_dir, "qq-supported-version.json");
        File.WriteAllText(path, """
            {"metadata":{"name":"QQChatExporter","version":"0.1.0"},
             "chatInfo":{"selfUin":"1","peerUid":"p","name":"n"},
             "messages":[]}
            """);

        using var exportFile = new QqExportFormat().Open(path);
        Assert.Equal("p", exportFile.Conversation.NativeId);
    }

    [Fact]
    public void QqChunkedExportFormat_ParsesManifestAndChunks_Correctly()
    {
        var chunkedDir = Path.Combine(_dir, "qq-chunked");
        var chunksDir = Path.Combine(chunkedDir, "chunks");
        Directory.CreateDirectory(chunksDir);

        var manifestPath = Path.Combine(chunkedDir, "manifest.json");
        File.WriteAllText(manifestPath, """
            {
              "metadata": {"name": "QQChatExporter", "version": "0.2.0"},
              "chatInfo": {"selfUid": "u_self", "peerUid": "u_group", "name": "QQ交流群", "type": "group"}
            }
            """);

        var chunk0Path = Path.Combine(chunksDir, "chunk_0.jsonl");
        File.WriteAllText(chunk0Path, """{"id":"q1","timestamp":1700000000,"sender":{"uid":"u_self","name":"我自己"},"content":{"type":"text","text":"第一条消息"}}""" + "\n");

        var chunk1Path = Path.Combine(chunksDir, "chunk_1.jsonl");
        File.WriteAllText(chunk1Path, """{"id":"q2","timestamp":1700000005,"sender":{"uid":"u_peer","name":"群友"},"content":{"type":"image","resources":[{"url":"resources/images/img.jpg"}]}}""" + "\n");

        var format = new QqChunkedExportFormat();
        Assert.True(format.Matches(manifestPath));

        using var exportFile = format.Open(manifestPath);
        Assert.Equal("qq", exportFile.Conversation.Platform);
        Assert.Equal("u_group", exportFile.Conversation.NativeId);
        Assert.Equal("group", exportFile.Conversation.Kind);
        Assert.Equal("QQ交流群", exportFile.Conversation.Title);

        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Equal(2, messages.Count);

        var msg1 = messages[0];
        Assert.Equal("q1", msg1.NativeId);
        Assert.Equal("我自己", msg1.SenderName);
        Assert.Equal("outgoing", msg1.Direction);
        Assert.Equal("第一条消息", msg1.Content);

        var msg2 = messages[1];
        Assert.Equal("q2", msg2.NativeId);
        Assert.Equal("群友", msg2.SenderName);
        Assert.Equal("incoming", msg2.Direction);
        Assert.Equal("image", msg2.MediaType);
        var attachment = Assert.Single(msg2.Attachments);
        Assert.Equal("resources/images/img.jpg", attachment.DeclaredPath);
    }

    [Fact]
    public void Qq_rejects_wrong_exporter_metadata_name()
    {
        var path = Path.Combine(_dir, "qq-wrong-exporter.json");
        File.WriteAllText(path, """
            {"metadata":{"name":"OtherExporter","version":"0.1.0"},
             "chatInfo":{"selfUin":"1","peerUid":"p","name":"n"},
             "messages":[]}
            """);

        var error = Assert.Throws<ImportFormatException>(() => new QqExportFormat().Open(path));
        Assert.Contains("QQChatExporter", error.Message);
    }

    private const string ArkmeJsonFixture = """
        {
          "weflow": {
            "version": "1.0.9",
            "format": "arkme-json"
          },
          "session": {
            "wxid": "group_123@chatroom",
            "type": "群聊",
            "remark": "项目讨论群"
          },
          "senders": [
            {
              "senderID": 1,
              "wxid": "wxid_self",
              "displayName": "",
              "nickname": "我自己的昵称",
              "groupNickname": "群名片-我"
            },
            {
              "senderID": 2,
              "wxid": "wxid_alice",
              "displayName": "爱丽丝备注",
              "nickname": "Alice",
              "groupNickname": "群名片-Alice"
            },
            {
              "senderID": 3,
              "wxid": "wxid_bob",
              "displayName": "",
              "nickname": "Bob",
              "groupNickname": "群名片-Bob"
            }
          ],
          "messages": [
            {
              "localId": 1,
              "createTime": 1700000000,
              "isSend": true,
              "senderID": 1,
              "type": "文本消息",
              "localType": 1,
              "content": "大家好"
            },
            {
              "localId": 2,
              "createTime": 1700000060,
              "isSend": false,
              "senderID": 2,
              "type": "文本消息",
              "localType": 1,
              "content": "收到"
            },
            {
              "localId": 3,
              "createTime": 1700000120,
              "isSend": false,
              "senderID": 3,
              "type": "位置消息",
              "localType": 48,
              "content": "[位置] 东方明珠",
              "locationPoiname": "东方明珠广播电视塔",
              "locationLabel": "上海市浦东新区世纪大道1号"
            }
          ]
        }
        """;

    [Fact]
    public void WeFlowExportFormat_ParsesArkmeJson_And_RelaxedVersions()
    {
        var path = Path.Combine(_dir, "arkme_weflow.json");
        File.WriteAllText(path, ArkmeJsonFixture);

        var format = new WeFlowExportFormat();
        Assert.True(format.Matches(path));

        using var exportFile = format.Open(path);
        Assert.Equal("wechat", format.Platform);
        Assert.Equal("group_123@chatroom", exportFile.Conversation.NativeId);
        Assert.Equal("group", exportFile.Conversation.Kind);
        Assert.Equal("项目讨论群", exportFile.Conversation.Title);

        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Equal(3, messages.Count);

        var msg1 = messages[0];
        Assert.Equal("wxid_self", msg1.SenderNativeId);
        Assert.Equal("我", msg1.SenderName);
        Assert.Equal("outgoing", msg1.Direction);
        Assert.Equal("text", msg1.MessageType);
        Assert.Equal("大家好", msg1.Content);
        Assert.Contains("大家好", msg1.SearchText);

        var msg2 = messages[1];
        Assert.Equal("wxid_alice", msg2.SenderNativeId);
        Assert.Equal("爱丽丝备注", msg2.SenderName);
        Assert.Equal("incoming", msg2.Direction);
        Assert.Equal("text", msg2.MessageType);
        Assert.Equal("收到", msg2.Content);
        Assert.Contains("收到", msg2.SearchText);

        var msg3 = messages[2];
        Assert.Equal("wxid_bob", msg3.SenderNativeId);
        Assert.Equal("群名片-Bob", msg3.SenderName);
        Assert.Equal("incoming", msg3.Direction);
        Assert.Equal("location", msg3.MessageType);
        Assert.Equal("[位置] 东方明珠", msg3.Content);
        Assert.Contains("东方明珠广播电视塔", msg3.SearchText);
        Assert.Contains("上海市浦东新区世纪大道1号", msg3.SearchText);
    }

    [Theory]
    [InlineData("1.0.4")]
    [InlineData("1.0.9")]
    [InlineData("")]
    public void Weflow_accepts_relaxed_export_versions(string version)
    {
        var metadata = version.Length == 0 ? "{}" : $$"""{"version":"{{version}}"}""";
        var path = Path.Combine(_dir, "wx-version.json");
        File.WriteAllText(path, $$"""
            {"weflow":{{metadata}},
             "session":{"wxid":"p","type":"私聊"},
             "messages":[]}
            """);

        using var exportFile = new WeFlowExportFormat().Open(path);
        Assert.Equal("p", exportFile.Conversation.NativeId);
    }

    [Fact]
    public void Weflow_rejects_missing_weflow()
    {
        var path = Path.Combine(_dir, "wx-missing-weflow.json");
        File.WriteAllText(path, """
            {"session":{"wxid":"p","type":"私聊"},
             "messages":[]}
            """);

        var error = Assert.Throws<ImportFormatException>(() => new WeFlowExportFormat().Open(path));
        Assert.Contains("weflow", error.Message);
    }

    [Fact]
    public void Weflow_accepts_supported_version()
    {
        var path = Path.Combine(_dir, "wx-supported-version.json");
        File.WriteAllText(path, """
            {"weflow":{"version":"1.0.3"},
             "session":{"wxid":"p","type":"私聊"},
             "messages":[]}
            """);

        using var exportFile = new WeFlowExportFormat().Open(path);
        Assert.Equal("p", exportFile.Conversation.NativeId);
    }

    [Fact]
    public void Streaming_qq_adapter_preserves_parser_hashes()
    {
        var path = Path.Combine(_dir, "qq-hash-compatibility.json");
        File.WriteAllText(path, QqFixture);

        using var document = ImportText.ParseDocument(path);
        var conversation = QqParser.ReadConversation(document, path);
        var expected = QqParser.IterateMessages(document, conversation, path)
            .Select(message => (message.PayloadHash, message.SemanticHash, message.SourceLocator))
            .ToList();
        using var exportFile = new QqExportFormat().Open(path);
        var actual = exportFile.EnumerateMessages()
            .Select(message => (message.PayloadHash, message.SemanticHash, message.SourceLocator))
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Streaming_weflow_adapter_preserves_parser_hashes()
    {
        var path = Path.Combine(_dir, "weflow-hash-compatibility.json");
        File.WriteAllText(path, WeFlowFixture);

        using var document = ImportText.ParseDocument(path);
        var (conversation, selfSender) = WeFlowParser.ReadConversation(document, path);
        var expected = WeFlowParser.IterateMessages(document, conversation, selfSender, path)
            .Select(message => (message.PayloadHash, message.SemanticHash, message.SourceLocator))
            .ToList();
        using var exportFile = new WeFlowExportFormat().Open(path);
        var actual = exportFile.EnumerateMessages()
            .Select(message => (message.PayloadHash, message.SemanticHash, message.SourceLocator))
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SafeResolveMedia_ResolvesMultiLevelFallbackPaths_Correctly()
    {
        var exportRoot = Path.Combine(_dir, "session_export");
        Directory.CreateDirectory(exportRoot);

        // 1. 同级文件探测
        var directFile = Path.Combine(exportRoot, "image1.png");
        File.WriteAllText(directFile, "img1");
        Assert.Equal(directFile, ImportText.SafeResolveMedia(exportRoot, "image1.png"));
        Assert.Null(ImportText.SafeResolveMedia(exportRoot, "/image1.png"));
        Assert.Null(ImportText.SafeResolveMedia(exportRoot, "\\image1.png"));

        // 2. resources/ 子目录探测
        var resourcesDir = Path.Combine(exportRoot, "resources", "images");
        Directory.CreateDirectory(resourcesDir);
        var resourcesFile = Path.Combine(resourcesDir, "image2.png");
        File.WriteAllText(resourcesFile, "img2");
        Assert.Equal(resourcesFile, ImportText.SafeResolveMedia(exportRoot, "images/image2.png"));

        // 3. media/ 子目录探测
        var mediaDir = Path.Combine(exportRoot, "media");
        Directory.CreateDirectory(mediaDir);
        var mediaFile = Path.Combine(mediaDir, "image3.png");
        File.WriteAllText(mediaFile, "img3");
        Assert.Equal(mediaFile, ImportText.SafeResolveMedia(exportRoot, "image3.png"));

        // 4. 上级目录 SafeExportPath(parentDir, Path.Combine("media", sessionTitle, normalized))
        var parentMediaSessionDir = Path.Combine(_dir, "media", "ChatSession1");
        Directory.CreateDirectory(parentMediaSessionDir);
        var parentSessionMediaFile = Path.Combine(parentMediaSessionDir, "image4.png");
        File.WriteAllText(parentSessionMediaFile, "img4");
        Assert.Equal(parentSessionMediaFile, ImportText.SafeResolveMedia(exportRoot, "image4.png", "ChatSession1"));

        var parentDirectFile = Path.Combine(_dir, "image5.png");
        File.WriteAllText(parentDirectFile, "img5");
        // SafeResolveMedia does not probe unconstrained parent dir, so it resolves to exportRoot fallback
        Assert.Equal(Path.Combine(exportRoot, "image5.png"), ImportText.SafeResolveMedia(exportRoot, "image5.png"));
        Assert.NotEqual(parentDirectFile, ImportText.SafeResolveMedia(exportRoot, "image5.png"));

        // 5. 若均未在磁盘发现，返回 SafeExportPath(exportRoot, normalized)
        var nonExistentPath = Path.Combine(exportRoot, "not_exist.png");
        Assert.Equal(nonExistentPath, ImportText.SafeResolveMedia(exportRoot, "not_exist.png"));

        // 6. 根路径/越界穿越返回 null
        Assert.Null(ImportText.SafeResolveMedia(exportRoot, "C:/outside/file.png"));
        Assert.Null(ImportText.SafeResolveMedia(exportRoot, "/"));
        Assert.Null(ImportText.SafeResolveMedia(exportRoot, ""));
        Assert.Null(ImportText.SafeResolveMedia(exportRoot, "../../outside.png"));
    }

    // ---------- CipherTalk ----------

    private const string CipherTalkDetailedJsonFixture = """
        {
          "exportInfo": {
            "version": "0.0.2",
            "exportedAt": 1700000000,
            "generator": "CipherTalk",
            "format": "detailed-json"
          },
          "session": {
            "wxid": "wxid_friend",
            "displayName": "好友A",
            "type": "私聊",
            "platform": "wechat",
            "isGroup": false,
            "ownerId": "wxid_self"
          },
          "messages": [
            {
              "localId": 101,
              "platformMessageId": "987654321",
              "createTime": 1700000000,
              "type": "文本消息",
              "localType": 1,
              "content": "你好呀",
              "isSend": 0,
              "senderUsername": "wxid_friend",
              "senderDisplayName": "好友A"
            },
            {
              "localId": 102,
              "createTime": 1700000005,
              "type": "图片消息",
              "localType": 3,
              "content": "[图片] images/20231115/102_abc.jpg",
              "isSend": 1,
              "senderUsername": "wxid_self",
              "senderDisplayName": "我"
            }
          ]
        }
        """;

    [Fact]
    public void CipherTalkParser_ParsesDetailedJson_Correctly()
    {
        var path = Path.Combine(_dir, "ciphertalk_detailed.json");
        File.WriteAllText(path, CipherTalkDetailedJsonFixture);

        var format = new CipherTalkDetailedJsonFormat();
        Assert.True(format.Matches(path));

        using var exportFile = format.Open(path);
        Assert.Equal("wechat", exportFile.Conversation.Platform);
        Assert.Equal("wxid_friend", exportFile.Conversation.NativeId);
        Assert.Equal("private", exportFile.Conversation.Kind);
        Assert.Equal("好友A", exportFile.Conversation.Title);

        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Equal(2, messages.Count);

        var msg1 = messages[0];
        Assert.Equal("incoming", msg1.Direction);
        Assert.Equal("text", msg1.MessageType);
        Assert.Equal("你好呀", msg1.Content);
        Assert.Equal("wxid_friend", msg1.SenderNativeId);
        Assert.Equal("好友A", msg1.SenderName);

        var msg2 = messages[1];
        Assert.Equal("outgoing", msg2.Direction);
        Assert.Equal("image", msg2.MessageType);
        Assert.Equal("[图片] images/20231115/102_abc.jpg", msg2.Content);
        Assert.Equal("wxid_self", msg2.SenderNativeId);
        Assert.Equal("我", msg2.SenderName);
        var attachment = Assert.Single(msg2.Attachments);
        Assert.Equal("image", attachment.Kind);
        Assert.Equal("102_abc.jpg", attachment.Filename);
        Assert.Equal("images/20231115/102_abc.jpg", attachment.DeclaredPath);
    }

    [Fact]
    public void CipherTalkParser_ParsesGroupAndQuotesAndSpecialTypes()
    {
        var json = """
            {
              "exportInfo": {
                "version": "0.0.2",
                "generator": "CipherTalk",
                "format": "detailed-json"
              },
              "session": {
                "wxid": "12345678@chatroom",
                "displayName": "技术讨论群",
                "type": "群聊",
                "platform": "wechat",
                "isGroup": true,
                "ownerId": "wxid_self"
              },
              "messages": [
                {
                  "localId": 201,
                  "platformMessageId": "msg_quote_1",
                  "createTime": 1700000100,
                  "type": "引用消息",
                  "localType": 1,
                  "content": "收到你的方案",
                  "isSend": 0,
                  "senderUsername": "wxid_bob",
                  "senderDisplayName": "Bob",
                  "quote": {
                    "sourceMessageId": "987654321",
                    "content": "你好呀"
                  }
                },
                {
                  "localId": 202,
                  "createTime": 1700000200,
                  "type": "语音消息",
                  "localType": 34,
                  "content": "[语音] voices/20231115/202.amr",
                  "voiceDuration": 5.2,
                  "isSend": 1,
                  "senderUsername": "wxid_self",
                  "senderDisplayName": "我"
                },
                {
                  "localId": 203,
                  "createTime": 1700000300,
                  "type": "转账消息",
                  "localType": 2000,
                  "content": "[微信转账] 给你转账 100 元",
                  "isSend": 0,
                  "senderUsername": "wxid_bob",
                  "senderDisplayName": "Bob"
                },
                {
                  "localId": 204,
                  "createTime": 1700000400,
                  "type": "系统消息",
                  "localType": 10000,
                  "content": "对方撤回了一条消息",
                  "isSend": 0,
                  "senderUsername": "wxid_bob"
                }
              ]
            }
            """;

        var path = Path.Combine(_dir, "ciphertalk_group.json");
        File.WriteAllText(path, json);

        var format = new CipherTalkDetailedJsonFormat();
        Assert.True(format.Matches(path));

        using var exportFile = format.Open(path);
        Assert.Equal("wechat", exportFile.Conversation.Platform);
        Assert.Equal("wxid_self", exportFile.Conversation.AccountId);
        Assert.Equal("12345678@chatroom", exportFile.Conversation.NativeId);
        Assert.Equal("group", exportFile.Conversation.Kind);
        Assert.Equal("技术讨论群", exportFile.Conversation.Title);

        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Equal(4, messages.Count);

        var quoteMsg = messages[0];
        Assert.Equal("987654321", quoteMsg.ReplyToNativeId);
        Assert.Equal("incoming", quoteMsg.Direction);
        Assert.Contains("你好呀", quoteMsg.SearchText);

        var audioMsg = messages[1];
        Assert.Equal("audio", audioMsg.MessageType);
        Assert.Equal("outgoing", audioMsg.Direction);
        var audioAttachment = Assert.Single(audioMsg.Attachments);
        Assert.Equal("audio", audioAttachment.Kind);
        Assert.Equal(5.2, audioAttachment.Duration);

        var transferMsg = messages[2];
        Assert.Equal("transfer", transferMsg.MessageType);

        var systemMsg = messages[3];
        Assert.Equal("system", systemMsg.MessageType);
        Assert.True(systemMsg.IsSystem);
        Assert.True(systemMsg.IsRecalled);
        Assert.Equal("system", systemMsg.Direction);
    }

    [Fact]
    public void CipherTalkDetailedJsonFormat_Discovery_And_StreamingHashes()
    {
        var path = Path.Combine(_dir, "ciphertalk_stream.json");
        File.WriteAllText(path, CipherTalkDetailedJsonFixture);

        using var document = ImportText.ParseDocument(path);
        var (conversation, selfSender) = CipherTalkParser.ReadConversation(document, path);
        var expected = CipherTalkParser.IterateMessages(document, conversation, selfSender, path)
            .Select(message => (message.PayloadHash, message.SemanticHash, message.SourceLocator))
            .ToList();

        using var exportFile = new CipherTalkDetailedJsonFormat().Open(path);
        var actual = exportFile.EnumerateMessages()
            .Select(message => (message.PayloadHash, message.SemanticHash, message.SourceLocator))
            .ToList();

        Assert.Equal(expected, actual);

        var discovered = ImportDiscovery.Discover(new[] { _dir });
        Assert.Contains(discovered, d => d.FilePath == Path.GetFullPath(path) && d.Platform == "wechat");
    }

    [Fact]
    public void CipherTalkDetailedJsonFormat_Rejects_Invalid_Generator_And_Format()
    {
        var invalid = """
            {
              "exportInfo": { "generator": "Unknown", "format": "unknown" },
              "session": { "wxid": "wxid_1" },
              "messages": []
            }
            """;
        var path = Path.Combine(_dir, "ciphertalk_invalid.json");
        File.WriteAllText(path, invalid);

        var format = new CipherTalkDetailedJsonFormat();
        Assert.False(format.Matches(path));
        Assert.Throws<ImportFormatException>(() => format.Open(path));
    }

    [Fact]
    public void ChatLabParser_ParsesJsonAndJsonl_Correctly()
    {
        // 1. JSONL 测试用例
        var jsonlLines = new[]
        {
            """{"_type":"header","chatlab":{"version":"0.0.2","exportedAt":1700000000,"generator":"ChatLab"},"meta":{"name":"开源群","platform":"wechat","type":"group","groupId":"open@chatroom"}}""",
            """{"_type":"member","platformId":"wxid_u1","accountName":"张三","groupNickname":"群主"}""",
            """{"_type":"message","id":"m1","sender":"wxid_u1","accountName":"张三","timestamp":1700000000,"type":0,"content":"欢迎加入"}"""
        };
        var pathJsonl = Path.Combine(_dir, "chatlab_test.jsonl");
        File.WriteAllLines(pathJsonl, jsonlLines);

        var formatJsonl = new ChatLabJsonlExportFormat();
        Assert.True(formatJsonl.Matches(pathJsonl));

        using (var exportFileJsonl = formatJsonl.Open(pathJsonl))
        {
            Assert.Equal("wechat", exportFileJsonl.Conversation.Platform);
            Assert.Equal("open@chatroom", exportFileJsonl.Conversation.NativeId);
            Assert.Equal("group", exportFileJsonl.Conversation.Kind);
            Assert.Equal("开源群", exportFileJsonl.Conversation.Title);

            var messagesJsonl = exportFileJsonl.EnumerateMessages().ToList();
            Assert.Single(messagesJsonl);

            var msg1 = messagesJsonl[0];
            Assert.Equal("m1", msg1.NativeId);
            Assert.Equal("wxid_u1", msg1.SenderNativeId);
            Assert.Equal("群主", msg1.SenderName);
            Assert.Equal("text", msg1.MessageType);
            Assert.Equal("欢迎加入", msg1.Content);
        }

        // 2. Standard JSON 测试用例
        var jsonContent = """
            {
              "chatlab": {"version": "0.0.2", "exportedAt": 1700000000, "generator": "WeFlow"},
              "meta": {"name": "私聊测试", "platform": "wechat", "type": "private", "ownerId": "wxid_self"},
              "members": [
                {"platformId": "wxid_bob", "accountName": "Bob"}
              ],
              "messages": [
                {"id": "m2", "sender": "wxid_bob", "accountName": "Bob", "timestamp": 1700000000, "type": 1, "content": "media/images/pic.jpg"}
              ]
            }
            """;
        var pathJson = Path.Combine(_dir, "chatlab_test.json");
        File.WriteAllText(pathJson, jsonContent);

        var formatJson = new ChatLabJsonExportFormat();
        Assert.True(formatJson.Matches(pathJson));

        using (var exportFileJson = formatJson.Open(pathJson))
        {
            Assert.Equal("wechat", exportFileJson.Conversation.Platform);
            Assert.Equal("wxid_bob", exportFileJson.Conversation.NativeId);
            Assert.Equal("private", exportFileJson.Conversation.Kind);
            Assert.Equal("私聊测试", exportFileJson.Conversation.Title);

            var messagesJson = exportFileJson.EnumerateMessages().ToList();
            Assert.Single(messagesJson);

            var msg2 = messagesJson[0];
            Assert.Equal("m2", msg2.NativeId);
            Assert.Equal("wxid_bob", msg2.SenderNativeId);
            Assert.Equal("Bob", msg2.SenderName);
            Assert.Equal("image", msg2.MessageType);
            Assert.Equal("media/images/pic.jpg", msg2.Content);
            var attachment = Assert.Single(msg2.Attachments);
            Assert.Equal("image", attachment.Kind);
            Assert.Equal("pic.jpg", attachment.Filename);
            Assert.Equal("media/images/pic.jpg", attachment.DeclaredPath);
        }
    }

    [Fact]
    public void ChatLabParser_ParsesAllIntegerTypes_AndQuotes_Correctly()
    {
        var jsonContent = """
            {
              "chatlab": {"version": "0.0.2", "exportedAt": 1700000000, "generator": "ChatLab"},
              "meta": {"name": "多类型测试群", "platform": "wechat", "type": "group", "groupId": "group_types@chatroom"},
              "members": [
                {"platformId": "wxid_self", "accountName": "我", "groupNickname": "群主-我"},
                {"platformId": "wxid_alice", "accountName": "Alice", "groupNickname": "爱丽丝"}
              ],
              "messages": [
                {"id": "t0", "sender": "wxid_alice", "timestamp": 1700000000, "type": 0, "content": "文本消息"},
                {"id": "t1", "sender": "wxid_alice", "timestamp": 1700000001, "type": 1, "content": "img.png"},
                {"id": "t2", "sender": "wxid_alice", "timestamp": 1700000002, "type": 2, "content": "voice.amr", "voiceDuration": 4.5},
                {"id": "t3", "sender": "wxid_alice", "timestamp": 1700000003, "type": 3, "content": "video.mp4"},
                {"id": "t4", "sender": "wxid_alice", "timestamp": 1700000004, "type": 4, "content": "doc.pdf"},
                {"id": "t5", "sender": "wxid_alice", "timestamp": 1700000005, "type": 5, "content": "sticker.gif"},
                {"id": "t7", "sender": "wxid_alice", "timestamp": 1700000007, "type": 7, "content": "https://example.com", "title": "链接标题"},
                {"id": "t8", "sender": "wxid_alice", "timestamp": 1700000008, "type": 8, "content": "东方明珠", "locationLabel": "世纪大道1号"},
                {"id": "t23", "sender": "wxid_alice", "timestamp": 1700000023, "type": 23, "content": "语音通话结束"},
                {"id": "t24", "sender": "wxid_alice", "timestamp": 1700000024, "type": 24, "content": "小程序分享"},
                {"id": "t25", "sender": "wxid_alice", "timestamp": 1700000025, "type": 25, "content": "回复消息", "quote": {"sourceMessageId": "t0", "content": "文本消息"}},
                {"id": "t27", "sender": "wxid_alice", "timestamp": 1700000027, "type": 27, "content": "名片消息"},
                {"id": "t80", "sender": "wxid_alice", "timestamp": 1700000080, "type": 80, "content": "对方撤回了一条消息"},
                {"id": "t99", "sender": "wxid_alice", "timestamp": 1700000099, "type": 99, "content": "未知消息"}
              ]
            }
            """;
        var path = Path.Combine(_dir, "chatlab_types.json");
        File.WriteAllText(path, jsonContent);

        var format = new ChatLabJsonExportFormat();
        using var exportFile = format.Open(path);
        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Equal(14, messages.Count);

        Assert.Equal("text", messages[0].MessageType);
        Assert.Equal("image", messages[1].MessageType);
        Assert.Equal("audio", messages[2].MessageType);
        Assert.Equal(4.5, messages[2].Attachments[0].Duration);
        Assert.Equal("video", messages[3].MessageType);
        Assert.Equal("file", messages[4].MessageType);
        Assert.Equal("emoji", messages[5].MessageType);
        Assert.Equal("link", messages[6].MessageType);
        Assert.Equal("location", messages[7].MessageType);
        Assert.Contains("世纪大道1号", messages[7].SearchText);
        Assert.Equal("call", messages[8].MessageType);
        Assert.Equal("mini_program", messages[9].MessageType);
        Assert.Equal("reply", messages[10].MessageType);
        Assert.Equal("t0", messages[10].ReplyToNativeId);
        Assert.Contains("文本消息", messages[10].SearchText);
        Assert.Equal("contact", messages[11].MessageType);
        Assert.Equal("system", messages[12].MessageType);
        Assert.True(messages[12].IsSystem);
        Assert.True(messages[12].IsRecalled);
        Assert.Equal("other", messages[13].MessageType);
    }

    [Fact]
    public void ChatLabExportFormat_RejectsInvalidVersions_And_DiscoversCorrectly()
    {
        var invalidJson = """
            {
              "chatlab": {"version": "0.0.1", "generator": "ChatLab"},
              "meta": {"name": "旧版本", "platform": "wechat", "groupId": "old@chatroom"},
              "messages": []
            }
            """;
        var pathInvalidJson = Path.Combine(_dir, "chatlab_old.json");
        File.WriteAllText(pathInvalidJson, invalidJson);

        var formatJson = new ChatLabJsonExportFormat();
        Assert.False(formatJson.Matches(pathInvalidJson));
        Assert.Throws<ImportFormatException>(() => formatJson.Open(pathInvalidJson));

        var invalidJsonl = """{"_type":"header","chatlab":{"version":"0.0.3"},"meta":{"name":"未来版本"}}""";
        var pathInvalidJsonl = Path.Combine(_dir, "chatlab_future.jsonl");
        File.WriteAllText(pathInvalidJsonl, invalidJsonl);

        var formatJsonl = new ChatLabJsonlExportFormat();
        Assert.False(formatJsonl.Matches(pathInvalidJsonl));
        Assert.Throws<ImportFormatException>(() => formatJsonl.Open(pathInvalidJsonl));

        var validJsonl = """
            {"_type":"header","chatlab":{"version":"0.0.2"},"meta":{"name":"有效群","groupId":"valid@chatroom"}}
            {"_type":"message","id":"m_v1","sender":"wxid_1","timestamp":1700000000,"type":0,"content":"hello"}
            """;
        var pathValidJsonl = Path.Combine(_dir, "chatlab_valid.jsonl");
        File.WriteAllText(pathValidJsonl, validJsonl);

        var discovered = ImportDiscovery.Discover(new[] { _dir });
        Assert.Contains(discovered, d => d.FilePath == Path.GetFullPath(pathValidJsonl) && d.Platform == "wechat");
    }
    [Fact]
    public void Rfc4180CsvReader_ParsesQuotedCommasEscapedQuotesAndNewlines()
    {
        using var reader = new StringReader("id,msg\r\n1,\"first, \"\"quoted\"\"\r\nsecond\"\r\n");
        var records = Rfc4180CsvReader.ReadRecords(reader).ToList();

        Assert.Equal(new[] { "id", "msg" }, records[0]);
        Assert.Equal(new[] { "1", "first, \"quoted\"\r\nsecond" }, records[1]);
    }

    [Fact]
    public void WeFlowSql_MapsCurrentTableAndMedia()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "images"));
        var image = Path.Combine(_dir, "images", "one.jpg");
        File.WriteAllText(image, "image");
        var path = Path.Combine(_dir, "weflow.sql");
        File.WriteAllText(path, """
            BEGIN;
            CREATE TABLE IF NOT EXISTS weflow_messages (
              session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
              create_time BIGINT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
              local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
            );
            INSERT INTO weflow_messages
              (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('group@chatroom', '1', '9001', 1700000123, 'wxid_alice', FALSE, 3, 'image', '图片', 'images/one.jpg');
            COMMIT;
            """);

        var format = new WeFlowSqlExportFormat();
        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("wechat", export.Conversation.Platform);
        Assert.Equal("wechat-default", export.Conversation.AccountId);
        Assert.Equal("group@chatroom", export.Conversation.NativeId);
        Assert.Equal("group", export.Conversation.Kind);
        Assert.Equal("weflow", export.Conversation.Title);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal("9001", message.NativeId);
        Assert.Equal("1", message.LocalId);
        Assert.Equal(1700000123000L, message.TimestampMs);
        Assert.Equal("wxid_alice", message.SenderNativeId);
        Assert.Equal("incoming", message.Direction);
        Assert.Equal("image", message.MessageType);
        Assert.Equal(image, Assert.Single(message.Attachments).SourcePath);
    }

    [Fact]
    public void CipherTalkSql_UsesSessionSenderTypeAndReplyColumns()
    {
        var path = Path.Combine(_dir, "ciphertalk.sql");
        File.WriteAllText(path, """
            -- 密语 CipherTalk - 聊天记录导出
            DELETE FROM messages WHERE session_wxid = 'group@chatroom';
            DELETE FROM sessions WHERE wxid = 'group@chatroom';
            CREATE TABLE IF NOT EXISTS sessions (
              wxid TEXT PRIMARY KEY, display_name TEXT NOT NULL, session_type TEXT NOT NULL,
              owner_id TEXT, message_count INTEGER DEFAULT 0, first_message_time BIGINT,
              last_message_time BIGINT, exported_at BIGINT
            );
            CREATE TABLE IF NOT EXISTS messages (
              id SERIAL PRIMARY KEY, session_wxid TEXT NOT NULL REFERENCES sessions(wxid), local_id INTEGER,
              create_time BIGINT NOT NULL, formatted_time TEXT, msg_type TEXT, content TEXT,
              is_send SMALLINT DEFAULT 0, sender_username TEXT, sender_display_name TEXT,
              group_nickname TEXT, reply_to_message_id TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_wxid);
            CREATE INDEX IF NOT EXISTS idx_messages_create_time ON messages(create_time);
            CREATE INDEX IF NOT EXISTS idx_messages_sender ON messages(sender_username);
            INSERT INTO sessions
              (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at)
            VALUES ('group@chatroom', '项目群', 'group', 'wxid_self', 1, 1700000123, 1700000123, 1700000200);
            INSERT INTO messages
              (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
            VALUES ('group@chatroom', 7, 1700000123, '2023-11-15 06:15:23', '图片消息', '图片', 0, 'wxid_alice', 'Alice', '群名片', '8999');
            """);

        var format = new CipherTalkSqlExportFormat();
        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("wechat", export.Conversation.Platform);
        Assert.Equal("wxid_self", export.Conversation.AccountId);
        Assert.Equal("group@chatroom", export.Conversation.NativeId);
        Assert.Equal("项目群", export.Conversation.Title);
        Assert.Equal("group", export.Conversation.Kind);
        var message = Assert.Single(export.EnumerateMessages());
        Assert.Equal("7", message.LocalId);
        Assert.Equal("wxid_alice", message.SenderNativeId);
        Assert.Equal("Alice", message.SenderName);
        Assert.Equal("image", message.MessageType);
        Assert.Equal("8999", message.ReplyToNativeId);
    }

    [Fact]
    public void WeFlowSql_PreservesWriterContentWhitespaceAndQuotes()
    {
        var path = Path.Combine(_dir, "weflow_content.sql");
        File.WriteAllText(path, """
            INSERT INTO weflow_messages
              (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('wxid_friend', '1', '2', 0, 'wxid_friend', FALSE, 1, NULL, '  It''s; (x,y)  ', NULL);
            """);

        using var export = new WeFlowSqlExportFormat().Open(path);
        var message = Assert.Single(export.EnumerateMessages());

        Assert.Equal("  It's; (x,y)  ", message.Content);
        Assert.Equal(0, message.TimestampMs);
    }

    [Fact]
    public void WeFlowSql_AcceptsExactLowercaseQuotedInsertIdentifiers()
    {
        var path = Path.Combine(_dir, "weflow_quoted_insert.sql");
        File.WriteAllText(path, """
            INSERT INTO "weflow_messages"
              ("session_id", "local_id", "message_id", "create_time", "sender", "is_send", "local_type", "media_type", "content", "media_path")
            VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
            """);

        var format = new WeFlowSqlExportFormat();

        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        Assert.Equal("hello", Assert.Single(export.EnumerateMessages()).Content);
    }

    [Theory]
    [InlineData("\"WEFLOW_MESSAGES\"", "session_id")]
    [InlineData("\"Weflow_Messages\"", "session_id")]
    [InlineData("weflow_messages", "\"SESSION_ID\"")]
    [InlineData("weflow_messages", "\"Session_Id\"")]
    public void WeFlowSql_RejectsCaseChangedQuotedInsertIdentifiers(string table, string sessionColumn)
    {
        var path = Path.Combine(_dir, "weflow_case_changed_insert.sql");
        File.WriteAllText(path, $$"""
            INSERT INTO {{table}}
              ({{sessionColumn}}, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
            """);

        var format = new WeFlowSqlExportFormat();

        Assert.False(format.Matches(path));
        Assert.Throws<ImportFormatException>(() => format.Open(path));
    }

    [Fact]
    public void WeFlowSql_MalformedDdlCannotCamouflageValidRow()
    {
        var path = Path.Combine(_dir, "weflow_bad_ddl.sql");
        File.WriteAllText(path, """
            CREATE TABLE IF NOT EXISTS weflow_messages (
              session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
              create_time TEXT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
              local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
            );
            INSERT INTO weflow_messages
              (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('session-a', '1', '101', 0, 'alice', FALSE, 1, NULL, 'hello', NULL);
            """);

        var format = new WeFlowSqlExportFormat();
        Assert.False(format.Matches(path));
        var error = Assert.Throws<ImportFormatException>(() => format.Open(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("weflow_messages", error.Message);
    }

    [Fact]
    public void CipherTalkSql_RejectsMessagesForADifferentSessionWithoutEmptyImport()
    {
        var path = Path.Combine(_dir, "ciphertalk_mismatch.sql");
        File.WriteAllText(path, """
            INSERT INTO sessions
              (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at)
            VALUES ('session-a', 'A', 'private', NULL, 1, 0, 0, 0);
            INSERT INTO messages
              (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
            VALUES ('session-b', 7, 0, '1970-01-01 00:00:00', '文本消息', 'x', 0, NULL, 'Alice', NULL, NULL);
            """);

        var format = new CipherTalkSqlExportFormat();
        Assert.False(format.Matches(path));
        var error = Assert.Throws<ImportFormatException>(() => format.Open(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("messages", error.Message);
        Assert.Contains("session-a", error.Message);
    }

    [Fact]
    public void CipherTalkSql_RejectsCombinedSessionRows()
    {
        var path = Path.Combine(_dir, "ciphertalk_combined.sql");
        File.WriteAllText(path, """
            INSERT INTO sessions
              (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at)
            VALUES
              ('session-a', 'A', 'private', NULL, 1, 0, 0, 0),
              ('session-b', 'B', 'private', NULL, 1, 0, 0, 0);
            INSERT INTO messages
              (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
            VALUES ('session-a', 1, 0, '1970-01-01 00:00:00', '文本消息', 'a', 0, NULL, 'Alice', NULL, NULL);
            """);

        var format = new CipherTalkSqlExportFormat();
        Assert.False(format.Matches(path));
        var error = Assert.Throws<ImportFormatException>(() => format.Open(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("sessions", error.Message);
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void CipherTalkSql_RejectsMixedMessageSessions()
    {
        var path = Path.Combine(_dir, "ciphertalk_mixed.sql");
        File.WriteAllText(path, """
            INSERT INTO sessions
              (wxid, display_name, session_type, owner_id, message_count, first_message_time, last_message_time, exported_at)
            VALUES ('session-a', 'A', 'private', NULL, 2, 0, 0, 0);
            INSERT INTO messages
              (session_wxid, local_id, create_time, formatted_time, msg_type, content, is_send, sender_username, sender_display_name, group_nickname, reply_to_message_id)
            VALUES
              ('session-a', 1, 0, '1970-01-01 00:00:00', '文本消息', 'a', 0, NULL, 'Alice', NULL, NULL),
              ('session-b', 2, 1, '1970-01-01 00:00:01', '文本消息', 'b', 0, NULL, 'Bob', NULL, NULL);
            """);

        var format = new CipherTalkSqlExportFormat();
        Assert.False(format.Matches(path));
        var error = Assert.Throws<ImportFormatException>(() => format.Open(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("messages", error.Message);
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void SqlFormats_RejectAnUnrelatedMessagesTableAndNearMissProfiles()
    {
        var generic = Path.Combine(_dir, "generic.sql");
        File.WriteAllText(generic, "CREATE TABLE messages(id INT, content TEXT); INSERT INTO messages (id, content) VALUES (1, 'x');");
        var nearWeFlow = Path.Combine(_dir, "near_weflow.sql");
        File.WriteAllText(nearWeFlow, "INSERT INTO weflow_messages (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content) VALUES ('x', '1', '2', 0, 's', TRUE, 1, NULL, 'x');");

        Assert.False(new WeFlowSqlExportFormat().Matches(generic));
        Assert.False(new CipherTalkSqlExportFormat().Matches(generic));
        Assert.False(new WeFlowSqlExportFormat().Matches(nearWeFlow));
        Assert.False(new CipherTalkSqlExportFormat().Matches(nearWeFlow));

        var discovered = ImportDiscovery.Discover(new[] { _dir });
        Assert.DoesNotContain(discovered, d => d.FilePath == Path.GetFullPath(generic));
    }

    [Theory]
    [InlineData("2024年03月15日 14:30:00")]
    [InlineData("2024-03-15 14:30:00")]
    [InlineData("2024/03/15 14:30:00")]
    [InlineData("2024.03.15 14:30:00")]
    [InlineData("2024-5-1 12:00:00")]
    [InlineData("2024-5-1 12:00")]
    [InlineData("2024/5/1 12:00:00")]
    [InlineData("2024/5/1 12:00")]
    [InlineData("2024.5.1 12:00:00")]
    [InlineData("2024.5.1 12:00")]
    [InlineData("2024-5-1")]
    [InlineData("2024/5/1")]
    [InlineData("2024.5.1")]
    public void ParseFlexibleTimestamp_Parses_Various_Date_Formats(string timeStr)
    {
        var ts = ImportText.ParseFlexibleTimestamp(timeStr);
        Assert.True(ts > 0);
    }

    [Fact]
    public void TryParseFlexibleTimestamp_DistinguishesEpochFromFailure()
    {
        Assert.True(ImportText.TryParseFlexibleTimestamp("0", out var numericEpoch));
        Assert.Equal(0, numericEpoch);
        Assert.True(ImportText.TryParseFlexibleTimestamp("1970-01-01T00:00:00.000Z", out var isoEpoch));
        Assert.Equal(0, isoEpoch);
        Assert.False(ImportText.TryParseFlexibleTimestamp("not-a-time", out var invalid));
        Assert.Equal(0, invalid);
        Assert.Equal(0, ImportText.ParseFlexibleTimestamp("not-a-time"));
    }

    [Fact]
    public void WeFlowSql_InvalidRequiredTimestampReportsPathTableAndRow()
    {
        var path = Path.Combine(_dir, "invalid_weflow.sql");
        File.WriteAllText(path, """
            INSERT INTO weflow_messages
              (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
            VALUES ('wxid_friend', '1', '2', 'not-a-time', 'wxid_friend', FALSE, 1, NULL, 'x', NULL);
            """);

        var format = new WeFlowSqlExportFormat();
        Assert.True(format.Matches(path));
        using var export = format.Open(path);
        var error = Assert.Throws<ImportFormatException>(() => export.EnumerateMessages().ToList());
        Assert.Contains(path, error.Message);
        Assert.Contains("weflow_messages", error.Message);
        Assert.Contains("1", error.Message);
    }

    [Fact]
    public void QqParser_IterateMessages_ThrowsImportFormatException_WhenMissingChatInfo()
    {
        var json = """
            {
              "messages": [
                {"id": "m1", "timestamp": 1700000000000, "type": "text", "content": {"text": "hello"}}
              ]
            }
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var conv = new ParsedConversation("qq", "self", "peer", "private", "Peer");
        var ex = Assert.Throws<ImportFormatException>(() => QqParser.IterateMessages(doc, conv, "invalid_qq.json").ToList());
        Assert.Contains("缺少 chatInfo 节点", ex.Message);
    }

    [Fact]
    public void ExportFormats_Register_Preserves_Default_Formats()
    {
        var dummyFormat = new NeverMatchesExportFormat();
        ExportFormats.Register(dummyFormat);
        Assert.True(ExportFormats.Default.Count >= 12);
    }

    [Fact]
    public void SqlFormats_AreRegisteredInDisjointOrder()
    {
        var formats = ExportFormats.Default.ToList();
        var weFlow = formats.FindIndex(format => format is WeFlowSqlExportFormat);
        var cipherTalk = formats.FindIndex(format => format is CipherTalkSqlExportFormat);

        Assert.True(weFlow >= 0);
        Assert.Equal(weFlow + 1, cipherTalk);
        Assert.Equal("wechat", formats[weFlow].Platform);
        Assert.Equal("wechat", formats[cipherTalk].Platform);
    }

    [Fact]
    public void CipherTalkParser_PlaintextWithSlashes_DoesNotCreateFakeAttachments()
    {
        var json = """
            {
              "exportInfo": {
                "version": "0.0.2",
                "generator": "CipherTalk",
                "format": "detailed-json"
              },
              "session": {
                "wxid": "wxid_friend",
                "displayName": "好友",
                "type": "私聊",
                "platform": "wechat"
              },
              "messages": [
                {
                  "localId": 101,
                  "createTime": 1700000100,
                  "type": "文本消息",
                  "localType": 1,
                  "content": "进度大约 3/4 and/or N/A，请查收 C:\\temp\\notes",
                  "isSend": 0,
                  "senderUsername": "wxid_friend"
                }
              ]
            }
            """;

        var path = Path.Combine(_dir, "ciphertalk_slash.json");
        File.WriteAllText(path, json);

        var format = new CipherTalkDetailedJsonFormat();
        using var exportFile = format.Open(path);
        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Single(messages);
        Assert.Equal("text", messages[0].MessageType);
        Assert.Null(messages[0].MediaType);
        Assert.Empty(messages[0].Attachments);
    }

    [Fact]
    public void QqChunked_ResolvesMediaUnderExportRootResources_WhenChunksInSubdir()
    {
        var exportRoot = Path.Combine(_dir, "qq_chunked_export");
        var chunksDir = Path.Combine(exportRoot, "chunks");
        var resDir = Path.Combine(exportRoot, "resources", "images");
        Directory.CreateDirectory(chunksDir);
        Directory.CreateDirectory(resDir);

        var imageFile = Path.Combine(resDir, "photo.jpg");
        File.WriteAllText(imageFile, "fake image bytes");

        var manifestPath = Path.Combine(exportRoot, "manifest.json");
        var manifestJson = """
            {
              "metadata": {"name": "QQChatExporter", "version": "0.1.0"},
              "chatInfo": {"selfUin": "10001", "selfUid": "uSELF", "peerUid": "uPEER", "peerUin": "12345", "name": "老张", "type": "private"}
            }
            """;
        File.WriteAllText(manifestPath, manifestJson);

        var chunkFile = Path.Combine(chunksDir, "part1.jsonl");
        var chunkLine = """
            {"id": "m1", "timestamp": 1700000000000, "type": "image", "sender": {"uid": "uPEER", "uin": "12345", "nickname": "Li"}, "content": {"text": "", "resources": [{"type": "image", "localPath": "resources/images/photo.jpg", "width": 100, "height": 100}]}}
            """;
        File.WriteAllText(chunkFile, chunkLine);

        var format = new QqChunkedExportFormat();
        Assert.True(format.Matches(manifestPath));
        using var exportFile = format.Open(manifestPath);
        var messages = exportFile.EnumerateMessages().ToList();
        Assert.Single(messages);
        var msg = messages[0];
        Assert.Single(msg.Attachments);
        Assert.Equal(imageFile, msg.Attachments[0].SourcePath);
    }

    [Fact]
    public void ChatLabParser_EmptyMemberPlatformId_FallsBackToOwnerOrTitle()
    {
        var json = """
            {
              "chatlab": {
                "version": "0.0.2",
                "generator": "ChatLab"
              },
              "meta": {
                "sessionType": "private",
                "ownerId": "wxid_owner_123",
                "title": "测试会话"
              },
              "members": [
                {
                  "nickname": "无ID成员"
                }
              ],
              "messages": []
            }
            """;

        var path = Path.Combine(_dir, "chatlab_empty_member.json");
        File.WriteAllText(path, json);

        var format = new ChatLabJsonExportFormat();
        Assert.True(format.Matches(path));
        using var exportFile = format.Open(path);
        Assert.Equal("wxid_owner_123", exportFile.Conversation.NativeId);
        Assert.Equal("测试会话", exportFile.Conversation.Title);
    }

    [Fact]
    public void ChatLabParser_EmptyMemberAndNoOwner_FallsBackToTitleFromPath()
    {
        var json = """
            {
              "chatlab": {
                "version": "0.0.2",
                "generator": "ChatLab"
              },
              "meta": {
                "sessionType": "private"
              },
              "members": [
                {
                  "nickname": "无ID成员"
                }
              ],
              "messages": []
            }
            """;

        var chatDir = Path.Combine(_dir, "my_custom_chat");
        Directory.CreateDirectory(chatDir);
        var path = Path.Combine(chatDir, "export.json");
        File.WriteAllText(path, json);

        var format = new ChatLabJsonExportFormat();
        Assert.True(format.Matches(path));
        using var exportFile = format.Open(path);
        Assert.Equal("my_custom_chat", exportFile.Conversation.NativeId);
    }

    [Fact]
    public void ImportText_AsLong_And_AsDouble_ParseStringifiedNumbers()
    {
        var nodeLong = JsonValue.Create("1700000010");
        Assert.Equal(1700000010L, ImportText.AsLong(nodeLong));

        var nodeDouble = JsonValue.Create("123.456");
        Assert.Equal(123.456d, ImportText.AsDouble(nodeDouble));

        var nodeLongFromFloat = JsonValue.Create("1700000010.5");
        Assert.Equal(1700000010L, ImportText.AsLong(nodeLongFromFloat));

        var numLong = JsonValue.Create(1700000010L);
        Assert.Equal(1700000010L, ImportText.AsLong(numLong));

        var numDouble = JsonValue.Create(123.456d);
        Assert.Equal(123.456d, ImportText.AsDouble(numDouble));

        var invalidStr = JsonValue.Create("not_a_number");
        Assert.Null(ImportText.AsLong(invalidStr));
        Assert.Null(ImportText.AsDouble(invalidStr));
    }
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NeverMatchesExportFormat : IChatExportFormat
    {
        public string Platform => "test";

        public bool Matches(string filePath) => false;

        public ExportFile Open(string filePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
