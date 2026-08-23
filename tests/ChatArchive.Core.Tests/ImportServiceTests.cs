using ChatArchive.Core.Importing;
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
              "QQChatExporter": {"version": 4},
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
              "QQChatExporter": {"version": 4},
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
            """{"QQChatExporter":{"version":4},"chatInfo":""");
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
    public void Unsupported_export_version_writes_nothing_and_does_not_abort_valid_file()
    {
        var root = ExportRoot();
        File.WriteAllText(
            Path.Combine(root, "a-unsupported.json"),
            Fixtures.QqExport.Replace("\"version\": 4", "\"version\": 5"));
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

    public void Dispose() => _archive.Dispose();
}

