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
            Fixtures.QqExport.Replace("\"version\": \"0.1.0\"", "\"version\": \"0.1.1\""));
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
    public void Database_constraint_failure_terminates_the_run()
    {
        var root = ExportRoot();
        File.WriteAllText(Path.Combine(root, "invalid-database.json"), "{}");
        var service = new ImportService(
            _archive.Db,
            _mediaDir,
            formats: new[] { new InvalidDatabaseExportFormat() });

        var error = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => service.Run(new[] { root }));

        Assert.Equal(19, error.SqliteErrorCode);
        using var connection = _archive.Open();
        Assert.Equal("failed", Text(connection, "SELECT status FROM import_runs LIMIT 1"));
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

    public void Dispose() => _archive.Dispose();
}

