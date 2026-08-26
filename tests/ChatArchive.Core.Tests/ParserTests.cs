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
    [InlineData("0.1.1")]
    [InlineData("")]
    public void Qq_rejects_unverified_export_versions(string version)
    {
        var metadata = version.Length == 0
            ? "{\"name\":\"QQChatExporter\"}"
            : $$"""{"name":"QQChatExporter","version":"{{version}}"}""";
        var path = Path.Combine(_dir, "qq-version.json");
        File.WriteAllText(path, $$"""
            {"metadata":{{metadata}},
             "chatInfo":{"selfUin":"1","peerUid":"p","name":"n"},
             "messages":[]}
            """);

        var error = Assert.Throws<ImportFormatException>(() => new QqExportFormat().Open(path));
        Assert.Contains("支持版本 0.1.0", error.Message);
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
        Assert.Equal(directFile, ImportText.SafeResolveMedia(exportRoot, "/image1.png"));
        Assert.Equal(directFile, ImportText.SafeResolveMedia(exportRoot, "\\image1.png"));

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

        // 4. 上级目录 SafeExportPath(parentDir, normalized) 与 SafeExportPath(parentDir, Path.Combine("media", sessionTitle, normalized))
        var parentMediaSessionDir = Path.Combine(_dir, "media", "ChatSession1");
        Directory.CreateDirectory(parentMediaSessionDir);
        var parentSessionMediaFile = Path.Combine(parentMediaSessionDir, "image4.png");
        File.WriteAllText(parentSessionMediaFile, "img4");
        Assert.Equal(parentSessionMediaFile, ImportText.SafeResolveMedia(exportRoot, "image4.png", "ChatSession1"));

        var parentDirectFile = Path.Combine(_dir, "image5.png");
        File.WriteAllText(parentDirectFile, "img5");
        Assert.Equal(parentDirectFile, ImportText.SafeResolveMedia(exportRoot, "image5.png"));

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
}
