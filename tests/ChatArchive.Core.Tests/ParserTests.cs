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

    [Theory]
    [InlineData("1.0.4")]
    [InlineData("")]
    public void Weflow_rejects_unverified_export_versions(string version)
    {
        var metadata = version.Length == 0 ? "{}" : $$"""{"version":"{{version}}"}""";
        var path = Path.Combine(_dir, "wx-version.json");
        File.WriteAllText(path, $$"""
            {"weflow":{{metadata}},
             "session":{"wxid":"p","type":"私聊"},
             "messages":[]}
            """);

        var error = Assert.Throws<ImportFormatException>(() => new WeFlowExportFormat().Open(path));
        Assert.Contains("支持版本 1.0.3", error.Message);
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
