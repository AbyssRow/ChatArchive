using ChatArchive.Core.Data;
using ChatArchive.Core.Importing;
using ChatArchive.Core.Repositories;
using System.Text.Json.Nodes;
using Xunit;

namespace ChatArchive.Core.Tests;

public class ImportServiceTests : IDisposable
{
    private readonly TestArchive _archive = new();
    private readonly string _mediaDir;

    public ImportServiceTests()
    {
        _mediaDir = Path.Combine(Path.GetDirectoryName(_archive.DatabasePath)!, "media");
        Directory.CreateDirectory(_mediaDir);
    }

    [Fact]
    public void Reimport_same_file_skips_and_counts_duplicates()
    {
        const string singleMessage = """
            {
              "metadata": {"name": "QQChatExporter", "version": "0.1.0"},
              "chatInfo": {"selfUin": "10001", "peerUid": "uPEER", "name": "老张", "type": "private"},
              "messages": [
                {"id": "m1", "timestamp": 1700000000000, "type": "text",
                 "sender": {"uid": "uPEER", "uin": "12345", "groupCard": "小李"},
                 "content": {"text": "你好"}}
              ]
            }
            """;
        var root = WriteExport("qq1.json", singleMessage);
        var service = new ImportService(_archive.Db, _mediaDir);

        var first = service.Run(new[] { root });
        Assert.Equal(1, first.FilesImported);
        Assert.Equal(1, first.MessagesSeen);
        Assert.Equal(1, first.Added);

        var second = service.Run(new[] { root });
        Assert.Equal(1, second.FilesSkipped);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.MessagesSeen);

        using var connection = _archive.Open();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
    }

    [Fact]
    public void Changed_content_creates_revision_not_overwrite()
    {
        var path = Path.Combine(ExportRoot(), "qq.json");
        var root = Path.GetDirectoryName(path)!;
        File.WriteAllText(path, Fixtures.QqExport.Replace("你好", "早啊"));

        var service = new ImportService(_archive.Db, _mediaDir);
        var first = service.Run(new[] { root });
        Assert.Equal(2, first.Added);

        File.WriteAllText(path, Fixtures.QqExport);
        var second = service.Run(new[] { root });
        Assert.True(second.Revised >= 1, $"revised={second.Revised}");

        using var connection = _archive.Open();
        Assert.Equal(3L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE revision_of_id IS NOT NULL"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE native_id='m1' AND content='你好'"));
    }

    [Fact]
    public void Wechat_same_local_id_different_content_becomes_variant_or_duplicate()
    {
        var path = Path.Combine(ExportRoot(), "wx.json");
        var root = Path.GetDirectoryName(path)!;
        var modified = Fixtures.WeFlowExport.Replace("在吗", "睡了吗");
        File.WriteAllText(path, modified);

        var service = new ImportService(_archive.Db, _mediaDir);
        var first = service.Run(new[] { root });
        Assert.Equal(2, first.Added);

        // 相同 localId/platformMessageId、不同内容：微信按 local_id/semantic 归并为版本
        File.WriteAllText(path, Fixtures.WeFlowExport);
        var second = service.Run(new[] { root });
        Assert.True(second.Revised + second.Duplicates + second.Variants >= 1,
            $"revised={second.Revised} dup={second.Duplicates} variant={second.Variants}");
    }

    [Fact]
    public void Missing_media_then_reimport_fills_media()
    {
        var exportRoot = ExportRoot();
        var mediaSourceDir = Path.Combine(exportRoot, "resources", "images");
        Directory.CreateDirectory(mediaSourceDir);
        var imagePath = Path.Combine(mediaSourceDir, "pic.jpg");

        var path = Path.Combine(exportRoot, "qq.json");
        File.WriteAllText(path, Fixtures.QqExport);

        var service = new ImportService(_archive.Db, _mediaDir);
        var first = service.Run(new[] { exportRoot });
        Assert.Equal(1, first.MissingMedia);

        using (var connection = _archive.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM attachments WHERE is_available=1"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM media_objects"));
            Assert.Equal("completed", Text(connection, "SELECT status FROM import_files LIMIT 1"));
        }

        // 补齐媒体后重导：同文件因缺失媒体被允许重跑，附件转可用并复制入库。
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3, 4 });
        var second = service.Run(new[] { exportRoot });
        Assert.Equal(1, second.FilesImported);
        Assert.Equal(0, second.MissingMedia);

        using (var connection = _archive.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM attachments WHERE is_available=1"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM media_objects"));
            var managed = Text(connection, "SELECT managed_path FROM media_objects LIMIT 1");
            Assert.True(File.Exists(managed));
            Assert.StartsWith(_mediaDir, managed, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Zero_message_export_creates_no_conversation()
    {
        var json = """
            {
              "metadata": {"name": "QQChatExporter", "version": "0.1.0"},
              "chatInfo": {"selfUin": "10001", "peerUid": "uEMPTY", "name": "空会话", "type": "private"},
              "messages": []
            }
            """;
        var root = WriteExport("empty.json", json);
        var service = new ImportService(_archive.Db, _mediaDir);
        var result = service.Run(new[] { root });

        Assert.Equal(1, result.FilesImported);
        using var connection = _archive.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
    }

    [Fact]
    public void Aliases_merge_across_duplicate_exports()
    {
        var path = Path.Combine(ExportRoot(), "w.json");
        var root = Path.GetDirectoryName(path)!;
        File.WriteAllText(path, Fixtures.WeFlowExport);
        var service = new ImportService(_archive.Db, _mediaDir);
        service.Run(new[] { root });

        // 第二次导出同一会话但对方换了显示名 → 别名补充而非新会话。
        File.WriteAllText(path, Fixtures.WeFlowExport.Replace("\"senderDisplayName\": \"张三\"", "\"senderDisplayName\": \"老张\""));
        service.Run(new[] { root });

        using var connection = _archive.Open();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(DISTINCT alias) FROM sender_aliases WHERE alias IN ('张三','老张')"));
    }

    [Fact]
    public void Malformed_file_does_not_abort_other_files_or_leave_importing_row()
    {
        var root = ExportRoot();
        File.WriteAllText(
            Path.Combine(root, "a-broken.json"),
            """{"metadata":{"name":"QQChatExporter","version":"0.1.0"},"chatInfo":""");
        File.WriteAllText(Path.Combine(root, "b-good.json"), Fixtures.QqExport);
        var service = new ImportService(_archive.Db, _mediaDir);

        var result = service.Run(new[] { root });

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(1, result.FilesImported);
        Assert.Equal(1, result.FilesFailed);
        using var connection = _archive.Open();
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM import_files WHERE status='importing'"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM import_files"));
    }

    [Fact]
    public void Unreadable_file_is_reported_and_does_not_abort_valid_file()
    {
        var root = ExportRoot();
        var lockedPath = Path.Combine(root, "a-locked.json");
        File.WriteAllText(lockedPath, Fixtures.QqExport);
        File.WriteAllText(Path.Combine(root, "b-good.json"), Fixtures.QqExport);
        using var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var service = new ImportService(_archive.Db, _mediaDir);

        var result = service.Run(new[] { root });

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(1, result.FilesImported);
        Assert.Equal(1, result.FilesFailed);
        var failed = Assert.Single(result.Files, file => file.Status == "failed");
        Assert.Contains("无法检查导出格式", failed.Error);
    }

    [Fact]
    public void Unsupported_export_version_writes_nothing_and_does_not_abort_valid_file()
    {
        var root = ExportRoot();
        File.WriteAllText(
            Path.Combine(root, "a-unsupported.json"),
            Fixtures.QqExport.Replace("\"chatInfo\": {\"selfUin\": \"10001\", \"selfUid\": \"uSELF\", \"peerUid\": \"uPEER\", \"peerUin\": \"12345\", \"name\": \"老张\", \"type\": \"private\"}", "\"chatInfo\": \"invalid\""));
        File.WriteAllText(Path.Combine(root, "b-good.json"), Fixtures.QqExport);
        var service = new ImportService(_archive.Db, _mediaDir);

        var result = service.Run(new[] { root });

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(1, result.FilesImported);
        Assert.Equal(1, result.FilesFailed);
        using var connection = _archive.Open();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM import_files"));
    }

    [Fact]
    public void Cancellation_rolls_back_file_transaction_and_marks_rows_interrupted()
    {
        var root = ExportRoot();
        File.WriteAllText(Path.Combine(root, "cancel.json"), "{}");
        using var cancellation = new CancellationTokenSource();
        var format = new CancellingExportFormat(cancellation);
        var service = new ImportService(_archive.Db, _mediaDir, formats: new[] { format });

        Assert.Throws<OperationCanceledException>(
            () => service.Run(new[] { root }, cancellationToken: cancellation.Token));

        using var connection = _archive.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal("interrupted", Text(connection, "SELECT status FROM import_files LIMIT 1"));
        Assert.Equal("interrupted", Text(connection, "SELECT status FROM import_runs LIMIT 1"));
    }

    [Fact]
    public void Database_constraint_failure_records_failed_file_result()
    {
        var root = ExportRoot();
        File.WriteAllText(Path.Combine(root, "invalid-database.json"), "{}");
        var service = new ImportService(
            _archive.Db,
            _mediaDir,
            formats: new[] { new InvalidDatabaseExportFormat() });

        var result = service.Run(new[] { root });
        Assert.Equal(1, result.FilesFailed);
        var failed = Assert.Single(result.Files);
        Assert.Equal("failed", failed.Status);
        using var connection = _archive.Open();
        Assert.Equal("completed_with_errors", Text(connection, "SELECT status FROM import_runs LIMIT 1"));
    }

    [Fact]
    public void Source_file_changed_during_import_rolls_back_the_file_transaction()
    {
        var root = ExportRoot();
        var path = Path.Combine(root, "changing.json");
        File.WriteAllText(path, "{}");
        var service = new ImportService(
            _archive.Db,
            _mediaDir,
            formats: new[] { new MutatingExportFormat(path) });

        var result = service.Run(new[] { root });

        Assert.Equal(1, result.FilesFailed);
        var failed = Assert.Single(result.Files);
        Assert.Contains("导入期间文件发生变化", failed.Error);
        using var connection = _archive.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal("failed", Text(connection, "SELECT status FROM import_files LIMIT 1"));
    }

    [Fact]
    public void Failed_file_removes_new_unreferenced_managed_media()
    {
        var root = ExportRoot();
        var exportPath = Path.Combine(root, "failing-media.json");
        var sourcePath = Path.Combine(root, "source-media.bin");
        File.WriteAllText(exportPath, "{}");
        File.WriteAllBytes(sourcePath, new byte[] { 1, 2, 3, 4 });
        var service = new ImportService(
            _archive.Db,
            _mediaDir,
            formats: new[] { new FailingAfterMediaExportFormat(exportPath, sourcePath) });

        var result = service.Run(new[] { root });

        Assert.Equal(1, result.FilesFailed);
        Assert.Empty(Directory.EnumerateFiles(_mediaDir, "*", SearchOption.AllDirectories));
        using var connection = _archive.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM media_objects"));
    }

    private string? _exportRootField;

    /// <summary>导出文件目录：与数据目录分开（服务会按前缀排除整个数据目录）。</summary>
    private string ExportRoot()
    {
        if (_exportRootField is null)
        {
            _exportRootField = Path.Combine(
                Directory.GetParent(_dir())!.FullName,
                "exports-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_exportRootField);
        }

        return _exportRootField;
    }

    private string _dir() => Path.GetDirectoryName(_archive.DatabasePath)!;

    private string WriteExport(string fileName, string content)
    {
        var path = Path.Combine(ExportRoot(), fileName);
        File.WriteAllText(path, content);
        return ExportRoot();
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Text(Microsoft.Data.Sqlite.SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (string?)cmd.ExecuteScalar() ?? string.Empty;
    }

    private sealed class CancellingExportFormat(CancellationTokenSource cancellation) : IChatExportFormat
    {
        public string Platform => "qq";

        public bool Matches(string filePath) =>
            string.Equals(Path.GetFileName(filePath), "cancel.json", StringComparison.Ordinal);

        public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conversation = new ParsedConversation(
                Platform,
                "test-account",
                "test-conversation",
                "private",
                "测试会话");
            return new ExportFile(conversation, Enumerate);
        }

        private IEnumerable<ParsedMessage> Enumerate(CancellationToken cancellationToken)
        {
            yield return Message("first", 1);
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield return Message("second", 2);
        }

        private static ParsedMessage Message(string id, long timestamp)
        {
            var raw = new JsonObject { ["id"] = id };
            return new ParsedMessage(
                NativeId: id,
                LocalId: null,
                TimestampMs: timestamp,
                Sequence: null,
                SenderNativeId: "sender",
                SenderName: "发送者",
                SenderAliases: new[] { "发送者" },
                Direction: "incoming",
                MessageType: "text",
                MediaType: null,
                Content: id,
                SearchText: id,
                IsRecalled: false,
                IsSystem: false,
                ReplyToNativeId: null,
                PayloadHash: $"payload-{id}",
                SemanticHash: $"semantic-{id}",
                SourceLocator: $"message:{id}",
                RawPayload: raw,
                Attachments: Array.Empty<ParsedAttachment>(),
                CompatiblePayloadHashes: Array.Empty<string>());
        }
    }

    private sealed class InvalidDatabaseExportFormat : IChatExportFormat
    {
        public string Platform => "qq";

        public bool Matches(string filePath) =>
            string.Equals(Path.GetFileName(filePath), "invalid-database.json", StringComparison.Ordinal);

        public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
        {
            var conversation = new ParsedConversation(
                "invalid-platform",
                "account",
                "conversation",
                "private",
                "测试会话");
            return new ExportFile(conversation, _ => new[] { Message() });
        }

        private static ParsedMessage Message()
        {
            return new ParsedMessage(
                NativeId: "message",
                LocalId: null,
                TimestampMs: 1,
                Sequence: null,
                SenderNativeId: "sender",
                SenderName: "发送者",
                SenderAliases: new[] { "发送者" },
                Direction: "incoming",
                MessageType: "text",
                MediaType: null,
                Content: "content",
                SearchText: "content",
                IsRecalled: false,
                IsSystem: false,
                ReplyToNativeId: null,
                PayloadHash: "payload",
                SemanticHash: "semantic",
                SourceLocator: "message:0",
                RawPayload: new JsonObject(),
                Attachments: Array.Empty<ParsedAttachment>(),
                CompatiblePayloadHashes: Array.Empty<string>());
        }
    }

    private sealed class MutatingExportFormat(string sourcePath) : IChatExportFormat
    {
        public string Platform => "qq";

        public bool Matches(string filePath) =>
            string.Equals(filePath, sourcePath, StringComparison.OrdinalIgnoreCase);

        public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
        {
            var conversation = new ParsedConversation(
                Platform,
                "account",
                "conversation",
                "private",
                "测试会话");
            return new ExportFile(conversation, Enumerate);
        }

        private IEnumerable<ParsedMessage> Enumerate(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ParsedMessage(
                NativeId: "message",
                LocalId: null,
                TimestampMs: 1,
                Sequence: null,
                SenderNativeId: "sender",
                SenderName: "发送者",
                SenderAliases: new[] { "发送者" },
                Direction: "incoming",
                MessageType: "text",
                MediaType: null,
                Content: "content",
                SearchText: "content",
                IsRecalled: false,
                IsSystem: false,
                ReplyToNativeId: null,
                PayloadHash: "payload",
                SemanticHash: "semantic",
                SourceLocator: "message:0",
                RawPayload: new JsonObject(),
                Attachments: Array.Empty<ParsedAttachment>(),
                CompatiblePayloadHashes: Array.Empty<string>());
            File.AppendAllText(sourcePath, " ");
        }
    }

    private sealed class FailingAfterMediaExportFormat(string exportPath, string mediaPath)
        : IChatExportFormat
    {
        public string Platform => "qq";

        public bool Matches(string filePath) =>
            string.Equals(filePath, exportPath, StringComparison.OrdinalIgnoreCase);

        public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
        {
            var conversation = new ParsedConversation(
                Platform,
                "account",
                "conversation",
                "private",
                "测试会话");
            return new ExportFile(conversation, Enumerate);
        }

        private IEnumerable<ParsedMessage> Enumerate(CancellationToken cancellationToken)
        {
            yield return new ParsedMessage(
                NativeId: "media-message",
                LocalId: null,
                TimestampMs: 1,
                Sequence: null,
                SenderNativeId: "sender",
                SenderName: "发送者",
                SenderAliases: new[] { "发送者" },
                Direction: "incoming",
                MessageType: "image",
                MediaType: "image",
                Content: "",
                SearchText: "",
                IsRecalled: false,
                IsSystem: false,
                ReplyToNativeId: null,
                PayloadHash: "payload-media",
                SemanticHash: "semantic-media",
                SourceLocator: "message:0",
                RawPayload: new JsonObject(),
                Attachments: new[]
                {
                    new ParsedAttachment(
                        Ordinal: 0,
                        Kind: "image",
                        Filename: "source-media.bin",
                        DeclaredPath: "source-media.bin",
                        SourcePath: mediaPath,
                        DeclaredSize: 4,
                        MimeType: "application/octet-stream",
                        Width: null,
                        Height: null,
                        Duration: null,
                        Metadata: new JsonObject()),
                },
                CompatiblePayloadHashes: Array.Empty<string>());
            cancellationToken.ThrowIfCancellationRequested();
            throw new ImportFormatException(exportPath, "synthetic failure");
        }
    }

    [Fact]
    public void Deleted_managed_media_is_recreated_from_existing_source()
    {
        var exportRoot = ExportRoot();
        var sourceDirectory = Path.Combine(exportRoot, "resources", "images");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "pic.jpg");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(Path.Combine(exportRoot, "qq.json"), Fixtures.QqExport);
        var service = new ImportService(_archive.Db, _mediaDir);

        var first = service.Run(new[] { exportRoot });
        Assert.Equal(0, first.MissingMedia);
        string managed;
        using (var connection = _archive.Open())
        {
            managed = Text(connection, "SELECT managed_path FROM media_objects LIMIT 1");
        }

        File.Delete(managed);
        var second = service.Run(new[] { exportRoot });

        Assert.Equal(1, second.FilesImported);
        Assert.Equal(0, second.MissingMedia);
        Assert.True(File.Exists(managed));
    }

    [Fact]
    public void Missing_source_and_managed_media_downgrades_attachment_availability()
    {
        var exportRoot = ExportRoot();
        var sourceDirectory = Path.Combine(exportRoot, "resources", "images");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "pic.jpg");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(Path.Combine(exportRoot, "qq.json"), Fixtures.QqExport);
        var service = new ImportService(_archive.Db, _mediaDir);

        service.Run(new[] { exportRoot });
        string managed;
        using (var connection = _archive.Open())
        {
            managed = Text(connection, "SELECT managed_path FROM media_objects LIMIT 1");
        }

        File.Delete(source);
        File.Delete(managed);
        var second = service.Run(new[] { exportRoot });

        Assert.Equal(1, second.FilesImported);
        Assert.Equal(1, second.MissingMedia);
        using (var connection = _archive.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM attachments WHERE is_available=0"));
        }

        var third = service.Run(new[] { exportRoot });
        Assert.Equal(1, third.FilesImported);
        Assert.Equal(1, third.MissingMedia);
    }

    [Fact]
    public void Pathless_media_creates_missing_attachment_and_consistent_stats()
    {
        var export = """
            {
              "weflow": {"version": "1.0.3"},
              "session": {"wxid": "wxid_peer", "type": "私聊", "remark": "联系人"},
              "messages": [
                {"localId": 1, "createTime": 1700000000, "isSend": false,
                 "senderUsername": "wxid_peer", "senderDisplayName": "联系人",
                 "type": "动画表情", "localType": 47, "content": "[动画表情]"}
              ]
            }
            """;
        var root = WriteExport("pathless-weflow.json", export);
        var service = new ImportService(_archive.Db, _mediaDir);

        var result = service.Run(new[] { root });

        Assert.Equal(1, result.Attachments);
        Assert.Equal(1, result.MissingMedia);
        using (var connection = _archive.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM attachments WHERE is_available=0"));
        }

        var stats = new StatsRepository(_archive.Db).GetStats();
        Assert.Equal(1, stats.MissingAttachments);
    }

    [Fact]
    public void CurrentFormats_EndToEnd_ImportsEveryDiscoveredSourceThroughImportService()
    {
        using var tree = CurrentExportTestTree.Create();
        var discovered = ImportDiscovery.Discover([tree.Root]);
        var service = new ImportService(_archive.Db, _mediaDir);

        var result = service.Run([tree.Root]);

        Assert.Equal(16, discovered.Count);
        Assert.Equal(discovered.Count, result.FilesFound);
        Assert.Equal(discovered.Count, result.FilesImported);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(0, result.FilesFailed);
        Assert.Equal(discovered.Count, result.MessagesSeen);
        Assert.Equal(discovered.Count, result.Added);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Revised);
        Assert.Equal(0, result.Variants);

        using var connection = _archive.Open();
        Assert.Equal((long)discovered.Count, Scalar(connection, "SELECT COUNT(*) FROM messages"));
        Assert.Equal((long)discovered.Count, Scalar(connection, "SELECT COUNT(*) FROM conversations"));
        Assert.Equal((long)discovered.Count, Scalar(connection, "SELECT COUNT(*) FROM senders"));
        Assert.Equal(
            0L,
            Scalar(
                connection,
                "SELECT COUNT(*) FROM conversations WHERE platform NOT IN ('wechat', 'qq')"));
        Assert.Equal(
            0L,
            Scalar(
                connection,
                "SELECT COUNT(*) FROM messages WHERE sender_id IS NULL OR content NOT LIKE '%你好%'"));
        Assert.Equal(
            0L,
            Scalar(
                connection,
                "SELECT COUNT(*) FROM senders WHERE TRIM(native_id) = '' OR TRIM(current_name) = ''"));
        Assert.Equal(
            1L,
            Scalar(
                connection,
                "SELECT COUNT(*) FROM attachments WHERE declared_path = '../images/layout-a.jpg' AND is_available = 1"));
        Assert.True(
            Scalar(connection, "SELECT COUNT(*) FROM attachments WHERE is_available = 1") >= 2,
            "The WeFlow layout-A image and QQ Excel resource should both be stored.");
    }

    [Fact]
    public async Task ImportService_ImportsAllSupportedFormats_EndToEnd_IntoDatabase()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"chatarchive-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        var exportsDir = Path.Combine(testDir, "exports");
        Directory.CreateDirectory(exportsDir);
        var dbDir = Path.Combine(testDir, "db");
        Directory.CreateDirectory(dbDir);
        var mediaDir = Path.Combine(testDir, "media");
        Directory.CreateDirectory(mediaDir);

        try
        {
            // 1. WeFlow Arkme JSON
            var arkmeJson = """
                {
                  "weflow": { "version": "1.0.9", "format": "arkme-json" },
                  "session": { "wxid": "weflow_group@chatroom", "displayName": "ArkMe微信群", "type": "群聊" },
                  "senders": [
                    { "senderID": 1, "wxid": "wx_user1", "displayName": "张三", "nickname": "张三昵称", "groupNickname": "张三名片" },
                    { "senderID": 2, "wxid": "wx_user2", "displayName": "李四", "nickname": "李四昵称" }
                  ],
                  "messages": [
                    { "localId": 1, "createTime": 1700000000, "type": "文本消息", "localType": 1, "content": "来自ArkMe群消息", "isSend": 0, "senderID": 1 },
                    { "localId": 2, "createTime": 1700000010, "type": "位置消息", "localType": 48, "content": "[位置] 东方明珠", "locationPoiname": "东方明珠", "isSend": 1, "senderID": 2 }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(exportsDir, "01_weflow_arkme.json"), arkmeJson);

            // 2. CipherTalk Detailed JSON
            var cipherTalkJson = """
                {
                  "exportInfo": { "version": "0.0.2", "exportedAt": 1700000000, "generator": "CipherTalk", "format": "detailed-json" },
                  "session": { "wxid": "wxid_ciphertalk_user", "displayName": "CipherTalk私聊", "type": "私聊", "platform": "wechat", "isGroup": false, "ownerId": "wxid_self" },
                  "messages": [
                    { "localId": 101, "platformMessageId": "ct_m1", "createTime": 1700000100, "type": "文本消息", "localType": 1, "content": "来自CipherTalk私聊消息", "isSend": 0, "senderUsername": "wxid_ciphertalk_user", "senderDisplayName": "好友CT" },
                    { "localId": 102, "platformMessageId": "ct_m2", "createTime": 1700000110, "type": "文本消息", "localType": 1, "content": "CipherTalk回复", "isSend": 1, "senderUsername": "wxid_self", "senderDisplayName": "我自己" }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(exportsDir, "02_ciphertalk_detailed.json"), cipherTalkJson);

            // 3. ChatLab 0.0.2 JSON
            var chatLabJson = """
                {
                  "chatlab": { "version": "0.0.2", "exportedAt": 1700000000, "generator": "ChatLab" },
                  "meta": { "name": "ChatLabJSON会话", "platform": "wechat", "type": "private", "ownerId": "wxid_self" },
                  "members": [
                    { "platformId": "wxid_cl_json", "accountName": "ChatLab好友" }
                  ],
                  "messages": [
                    { "id": "cl_j1", "sender": "wxid_cl_json", "accountName": "ChatLab好友", "timestamp": 1700000200, "type": 0, "content": "来自ChatLab标准JSON消息" }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(exportsDir, "03_chatlab.json"), chatLabJson);

            // 4. ChatLab 0.0.2 JSONL
            var chatLabJsonl = string.Join("\n", new[]
            {
                """{"_type":"header","chatlab":{"version":"0.0.2","exportedAt":1700000000,"generator":"ChatLab"},"meta":{"name":"ChatLabJSONL群","platform":"wechat","type":"group","groupId":"cl_jsonl@chatroom","ownerId":"wxid_self"}}""",
                """{"_type":"member","platformId":"wxid_cl_jsonl_user","accountName":"JSONL用户","groupNickname":"群管"}""",
                """{"_type":"message","id":"cl_l1","sender":"wxid_cl_jsonl_user","accountName":"JSONL用户","timestamp":1700000300,"type":0,"content":"来自ChatLab JSONL流式消息"}"""
            });
            File.WriteAllText(Path.Combine(exportsDir, "04_chatlab.jsonl"), chatLabJsonl);

            // 5. QQ Chat Exporter Chunked (`manifest.json` + `chunks/*.jsonl`)
            var qqChunkedDir = Path.Combine(exportsDir, "05_qq_chunked");
            var qqChunksDir = Path.Combine(qqChunkedDir, "chunks");
            Directory.CreateDirectory(qqChunksDir);
            var qqManifest = """
                {
                  "metadata": { "name": "QQChatExporter", "version": "0.2.0" },
                  "chatInfo": { "selfUid": "u_self", "peerUid": "u_qq_chunk_group", "name": "QQ分块群聊", "type": "group" }
                }
                """;
            File.WriteAllText(Path.Combine(qqChunkedDir, "manifest.json"), qqManifest);
            var qqChunk0 = """{"id":"qq_c1","timestamp":1700000400000,"sender":{"uid":"u_qq_friend","name":"QQ群友"},"content":{"type":"text","text":"来自QQ分块消息1"}}""" + "\n"
                         + """{"id":"qq_c2","timestamp":1700000410000,"sender":{"uid":"u_self","name":"我自己"},"content":{"type":"text","text":"来自QQ分块消息2"}}""" + "\n";
            File.WriteAllText(Path.Combine(qqChunksDir, "chunk_0.jsonl"), qqChunk0);
            // 7. Current WeFlow CSV
            var weflowCsv = """
                id,MsgSvrID,type_name,is_sender,talker,msg,src,CreateTime
                1,9001,text,0,CSV好友,来自WeFlow CSV消息,,2023-11-15T10:00:00.000Z
                2,9002,text,1,我,CSV回复消息,,2023-11-15T10:00:05.000Z
                """;
            File.WriteAllText(Path.Combine(exportsDir, "07_weflow.csv"), weflowCsv);

            // 8. Current WeFlow SQL export
            var sqlExport = """
                CREATE TABLE IF NOT EXISTS weflow_messages (
                  session_id TEXT NOT NULL, local_id TEXT, message_id TEXT,
                  create_time BIGINT NOT NULL, sender TEXT, is_send BOOLEAN NOT NULL,
                  local_type INTEGER, media_type TEXT, content TEXT, media_path TEXT
                );
                INSERT INTO weflow_messages
                  (session_id, local_id, message_id, create_time, sender, is_send, local_type, media_type, content, media_path)
                VALUES
                  ('wxid_sql_user', 'sql_1', 'sql_msg_1', 1700000600, 'wxid_sql_user', FALSE, 1, NULL, '来自SQL导出消息1', NULL),
                  ('wxid_sql_user', 'sql_2', 'sql_msg_2', 1700000610, 'wxid_self', TRUE, 1, NULL, '来自SQL导出消息2', NULL);
                """;
            File.WriteAllText(Path.Combine(exportsDir, "10_chat_dump.sql"), sqlExport);

            // 干净的 SQLite 测试数据库
            var dbPath = Path.Combine(dbDir, "e2e_test.db");
            var db = new ArchiveDatabase(dbPath);
            db.EnsureSchema();

            var service = new ImportService(db, mediaDir);

            // 执行全量异步导入 RunAsync
            var result = await service.RunAsync(new[] { exportsDir });

            // 断言导入统计
            Assert.Equal(7, result.FilesFound);
            Assert.Equal(7, result.FilesImported);
            Assert.Equal(0, result.FilesFailed);
            Assert.True(result.MessagesSeen > 0);
            Assert.True(result.Added > 0);
            Assert.Equal(0, result.Duplicates);

            // 查询数据库确认各会话、联系人/发件人、消息与 FTS 搜索索引
            using (var connection = db.OpenConnection())
            {
                var convCount = Scalar(connection, "SELECT COUNT(*) FROM conversations");
                Assert.True(convCount >= 7, $"conversations count: {convCount}");

                var senderCount = Scalar(connection, "SELECT COUNT(*) FROM senders");
                Assert.True(senderCount >= 7, $"senders count: {senderCount}");

                var msgCount = Scalar(connection, "SELECT COUNT(*) FROM messages");
                Assert.Equal(result.Added, msgCount);

                // 断言各类特征消息确实已入库
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自ArkMe群消息%'"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自CipherTalk私聊消息%'"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自ChatLab标准JSON消息%'"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自ChatLab JSONL流式消息%'"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自QQ分块消息1%'"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自WeFlow CSV消息%'"));
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE content LIKE '%来自SQL导出消息1%'"));

                // 断言 FTS 全文搜索索引正确同步触发写入
                var ftsCount = Scalar(connection, "SELECT COUNT(*) FROM messages_fts");
                Assert.Equal(msgCount, ftsCount);
            }

            // 使用 SearchRepository 进行检索验证
            var searchRepo = new SearchRepository(db);
            var searchResult = searchRepo.Search("ArkMe");
            Assert.NotEmpty(searchResult.Items);
            Assert.Contains(searchResult.Items, item => item.Snippet.Contains("ArkMe"));

            var searchCsv = searchRepo.Search("WeFlow");
            Assert.NotEmpty(searchCsv.Items);
            Assert.Contains(searchCsv.Items, item => item.Snippet.Contains("WeFlow"));

            // 使用 ContactRepository 验证联系人绑定支持
            var contactRepo = new ContactRepository(db);
            using (var connection = db.OpenConnection())
            {
                var firstSenderId = Scalar(connection, "SELECT id FROM senders LIMIT 1");
                var createdContactId = contactRepo.CreateContact(
                    "测试绑定联系人",
                    initialBindings: new[] { (SenderId: firstSenderId, Label: (string?)"主要身份", IsPrimary: true) });
                Assert.True(createdContactId > 0);
                var contactDetail = contactRepo.GetContactDetail(createdContactId);
                Assert.NotNull(contactDetail);
                Assert.Single(contactDetail.Senders);
            }

            // 二次运行导入，断言所有消息基于 NativeId 与 PayloadHash 完美去重（重复消息数为 0，跳过计数正常）
            var secondResult = await service.RunAsync(new[] { exportsDir });
            Assert.Equal(7, secondResult.FilesFound);
            Assert.Equal(7, secondResult.FilesSkipped);
            Assert.Equal(0, secondResult.FilesImported);
            Assert.Equal(0, secondResult.Added);
            Assert.Equal(0, secondResult.FilesFailed);

            using (var connection = db.OpenConnection())
            {
                var msgCountAfter = Scalar(connection, "SELECT COUNT(*) FROM messages");
                Assert.Equal(result.Added, msgCountAfter);
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try
                {
                    Directory.Delete(testDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void Cross_format_import_same_conversation_merges_without_duplication()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"chatarchive_merge_test_{Guid.NewGuid():N}");
        var exportsDir1 = Path.Combine(testDir, "batch1");
        var exportsDir2 = Path.Combine(testDir, "batch2");
        var dbDir = Path.Combine(testDir, "db");
        var mediaDir = Path.Combine(testDir, "media");
        Directory.CreateDirectory(exportsDir1);
        Directory.CreateDirectory(exportsDir2);
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(mediaDir);

        try
        {
            // Batch 1: WeFlow export (account_id = wechat-default)
            var weflowExport = """
                {
                  "weflow": { "version": "1.0.3", "generator": "WeFlow" },
                  "session": { "wxid": "wxid_friend123", "displayName": "好友小明", "type": "私聊" },
                  "messages": [
                    { "localId": 1, "localType": 1, "createTime": 1700000000, "type": "文本消息", "content": "第一条消息来自WeFlow", "isSend": 0, "senderUsername": "wxid_friend123", "senderDisplayName": "好友小明", "platformMessageId": "msg_w1" },
                    { "localId": 2, "localType": 1, "createTime": 1700000005, "type": "文本消息", "content": "第二条回复来自我", "isSend": 1, "senderUsername": "wxid_myaccount", "senderDisplayName": "我", "platformMessageId": "msg_w2" }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(exportsDir1, "weflow_chat.json"), weflowExport);

            // Batch 2: ChatLab export for SAME conversation (ownerId = wxid_myaccount)
            var chatlabExport = """
                {
                  "chatlab": { "version": "0.0.2", "generator": "CipherTalk" },
                  "meta": { "name": "好友小明", "platform": "wechat", "type": "private", "ownerId": "wxid_myaccount", "chatId": "wxid_friend123" },
                  "members": [
                    { "platformId": "wxid_friend123", "accountName": "好友小明" },
                    { "platformId": "wxid_myaccount", "accountName": "我" }
                  ],
                  "messages": [
                    { "sender": "wxid_friend123", "accountName": "好友小明", "timestamp": 1700000000, "localType": 1, "type": 0, "content": "第一条消息来自WeFlow", "platformMessageId": "msg_w1" },
                    { "sender": "wxid_friend123", "accountName": "好友小明", "timestamp": 1700000010, "localType": 1, "type": 0, "content": "第三条新消息来自ChatLab", "platformMessageId": "msg_c3" }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(exportsDir2, "chatlab_chat.json"), chatlabExport);

            var dbPath = Path.Combine(dbDir, "merge_test.db");
            var db = new ArchiveDatabase(dbPath);
            db.EnsureSchema();
            var service = new ImportService(db, mediaDir);

            // Import batch 1
            var res1 = service.Run(new[] { exportsDir1 });
            Assert.Equal(1, res1.FilesImported);
            Assert.Equal(2, res1.Added);

            // Import batch 2
            var res2 = service.Run(new[] { exportsDir2 });
            Assert.Equal(1, res2.FilesImported);
            Assert.Equal(1, res2.Added); // Only msg_c3 is added, msg_w1 is deduplicated
            Assert.Equal(1, res2.Duplicates);

            using (var connection = db.OpenConnection())
            {
                // MUST have exactly 1 conversation (NOT split into 2)
                var convCount = Scalar(connection, "SELECT COUNT(*) FROM conversations");
                Assert.Equal(1L, convCount);

                // AccountId should be upgraded to wxid_myaccount
                var accountId = ScalarText(connection, "SELECT account_id FROM conversations WHERE native_id = 'wxid_friend123'");
                Assert.Equal("wxid_myaccount", accountId);

                // Total messages in conversation must be 3
                var msgCount = Scalar(connection, "SELECT COUNT(*) FROM messages");
                Assert.Equal(3L, msgCount);

                // Total senders must be 2 (wxid_friend123 and wxid_myaccount), NOT duplicated
                var senderCount = Scalar(connection, "SELECT COUNT(*) FROM senders");
                Assert.Equal(2L, senderCount);
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                try { Directory.Delete(testDir, recursive: true); } catch (IOException) { }
            }
        }
    }

    private static string? ScalarText(Microsoft.Data.Sqlite.SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public void ImportFile_ReturnsFailed_WhenPlatformHasNoMatchingFormat()
    {
        var dummyFile = Path.Combine(ExportRoot(), "unknown.xyz");
        File.WriteAllText(dummyFile, "dummy");
        var service = new ImportService(_archive.Db, _mediaDir);

        var result = service.ImportFile(dummyFile, "non_existent_platform", 1L);
        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains("未找到支持的导出格式解析器", result.Error);
        Assert.Contains("non_existent_platform", result.Error);
    }

    [Fact]
    public void CanonicalJson_FormatsNegativeZeroAndDecimalCorrectly()
    {
        Assert.Equal("-0.0", CanonicalJson.FormatDouble(-0.0));
        Assert.Equal("1234567890123456789.12", CanonicalJson.Serialize(System.Text.Json.Nodes.JsonValue.Create(1234567890123456789.12m)));
    }

    public void Dispose()
    {
        if (_exportRootField is not null && Directory.Exists(_exportRootField))
        {
            try
            {
                Directory.Delete(_exportRootField, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _archive.Dispose();
    }
}

