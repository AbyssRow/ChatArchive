using System.Text.Json.Nodes;
using ChatArchive.Core.IO;

namespace ChatArchive.Core.Importing;

/// <summary>QQ Chat Exporter 格式适配器。</summary>
public sealed class QqExportFormat : IChatExportFormat
{
    private const string ExporterName = "QQChatExporter";

    public string Platform => "qq";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "chatInfo" },
                cancellationToken))
        {
            return false;
        }

        JsonObject? metadata = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "metadata" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "metadata", cancellationToken);
        }
        else if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "exporter" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "exporter", cancellationToken);
        }

        if (metadata == null)
        {
            return false;
        }

        return string.Equals(
            ImportText.Clean(metadata["name"]),
            ExporterName,
            StringComparison.OrdinalIgnoreCase);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        JsonObject? metadata = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "metadata" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(
                filePath,
                "metadata",
                cancellationToken);
        }
        else if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "exporter" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(
                filePath,
                "exporter",
                cancellationToken);
        }
        else
        {
            throw new ImportFormatException(filePath, "缺少 metadata 或 exporter 对象");
        }

        var exporterName = ImportText.Clean(metadata["name"]);
        if (!string.Equals(exporterName, ExporterName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(
                filePath,
                $"QQ 导出器标识无效：应为 {ExporterName}，实际为 {Display(exporterName)}");
        }

        var chat = ChunkedJsonReader.ReadObjectProperty(filePath, "chatInfo", cancellationToken);
        var conversation = QqParser.ReadConversation(chat, filePath);
        var selfUid = ImportText.Clean(chat["selfUid"]);
        var selfUin = ImportText.Clean(chat["selfUin"]);
        return new ExportFile(
            conversation,
            token => QqParser.IterateMessages(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", token),
                conversation,
                filePath,
                selfUid,
                selfUin));
    }

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";
}

/// <summary>QQ Chat Exporter 分块 JSONL (manifest.json + chunks/*.jsonl) 格式适配器。</summary>
public sealed class QqChunkedExportFormat : IChatExportFormat
{
    private const string ExporterName = "QQChatExporter";

    public string Platform => "qq";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var safeManifest = QqChunkManifest.ValidateManifestFile(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ChunkedJsonReader.ContainsRootProperties(
                safeManifest,
                new[] { "chatInfo" },
                cancellationToken))
        {
            return false;
        }

        JsonObject? metadata = null;
        if (ChunkedJsonReader.ContainsRootProperties(safeManifest, new[] { "metadata" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(safeManifest, "metadata", cancellationToken);
        }
        else if (ChunkedJsonReader.ContainsRootProperties(safeManifest, new[] { "exporter" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(safeManifest, "exporter", cancellationToken);
        }

        if (metadata == null)
        {
            return false;
        }

        var name = ImportText.Clean(metadata["name"]);
        if (!name.Contains(ExporterName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _ = QqChunkManifest.ResolveChunkFiles(filePath, cancellationToken);
        return true;
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var chunkFiles = QqChunkManifest.ResolveChunkFiles(filePath, cancellationToken);

        JsonObject? metadata = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "metadata" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "metadata", cancellationToken);
        }
        else if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "exporter" }, cancellationToken))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "exporter", cancellationToken);
        }
        else
        {
            throw new ImportFormatException(filePath, "缺少 metadata 或 exporter 对象");
        }

        var exporterName = ImportText.Clean(metadata["name"]);
        if (!exporterName.Contains(ExporterName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(
                filePath,
                $"QQ 导出器标识无效：应包含 {ExporterName}，实际为 {Display(exporterName)}");
        }

        var chat = ChunkedJsonReader.ReadObjectProperty(filePath, "chatInfo", cancellationToken);
        var conversation = QqParser.ReadConversation(chat, filePath);
        var selfUid = ImportText.Clean(chat["selfUid"]);
        var selfUin = ImportText.Clean(chat["selfUin"]);
        var selfSender = !string.IsNullOrEmpty(selfUid) ? selfUid : !string.IsNullOrEmpty(selfUin) ? selfUin : null;

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;

        return new ExportFile(
            conversation,
            token => IterateChunkedMessages(chunkFiles, conversation, selfSender, filePath, manifestDir, token));
    }

    private static IEnumerable<ParsedMessage> IterateChunkedMessages(
        IReadOnlyList<string> chunkFiles,
        ParsedConversation conversation,
        string? selfSender,
        string manifestPath,
        string exportRoot,
        CancellationToken cancellationToken)
    {
        var globalIndex = 0;
        foreach (var chunkFile in chunkFiles)
        {
            using var reader = OpenChunkReader(manifestPath, exportRoot, chunkFile);

            string? line;
            while ((line = ReadChunkLine(reader, manifestPath, exportRoot, chunkFile)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var message = QqParser.ParseChunkedLine(trimmed, conversation, selfSender, chunkFile, globalIndex, exportRoot);
                if (message != null)
                {
                    yield return message;
                    globalIndex++;
                }
            }
        }
    }

    private static StreamReader OpenChunkReader(
        string manifestPath,
        string exportRoot,
        string chunkPath)
    {
        var relative = Path.GetRelativePath(exportRoot, chunkPath).Replace('\\', '/');
        try
        {
            var stream = new FileStream(
                chunkPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            return new StreamReader(stream, System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(
                manifestPath,
                $"读取声明分块失败（{relative}：{ex.Message}）",
                ex);
        }
    }

    private static string? ReadChunkLine(
        StreamReader reader,
        string manifestPath,
        string exportRoot,
        string chunkPath)
    {
        var relative = Path.GetRelativePath(exportRoot, chunkPath).Replace('\\', '/');
        try
        {
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(
                manifestPath,
                $"读取声明分块失败（{relative}：{ex.Message}）",
                ex);
        }
    }

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";

}

/// <summary>WeFlow 格式适配器。</summary>
public sealed class WeFlowExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ChunkedJsonReader.ContainsRootProperties(
            filePath,
            new[] { "weflow", "session" },
            cancellationToken);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "weflow", cancellationToken);

        var session = ChunkedJsonReader.ReadObjectProperty(filePath, "session", cancellationToken);
        var conversation = WeFlowParser.ReadConversation(session, filePath);

        Dictionary<int, JsonObject>? senders = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "senders" }, cancellationToken))
        {
            senders = new Dictionary<int, JsonObject>();
            foreach (var senderObj in ChunkedJsonReader.EnumerateObjectArray(filePath, "senders", cancellationToken))
            {
                var id = ImportText.AsLong(senderObj["senderID"]) ?? ImportText.AsLong(senderObj["senderId"]);
                if (id.HasValue)
                {
                    senders[(int)id.Value] = senderObj;
                }
            }
        }

        var selfSender = WeFlowParser.InferSelfSender(
            ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", cancellationToken),
            conversation,
            cancellationToken,
            senders);

        return new ExportFile(
            conversation,
            token => WeFlowParser.IterateMessages(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", token),
                conversation,
                selfSender,
                filePath,
                senders));
    }
}

/// <summary>CipherTalk Detailed JSON 格式适配器。</summary>
public sealed class CipherTalkDetailedJsonFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "exportInfo", "session", "messages" },
                cancellationToken))
        {
            return false;
        }

        var exportInfo = ChunkedJsonReader.ReadObjectProperty(filePath, "exportInfo", cancellationToken);
        var generator = ImportText.Clean(exportInfo["generator"]);
        var format = ImportText.Clean(exportInfo["format"]);
        return string.Equals(generator, "CipherTalk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "detailed-json", StringComparison.OrdinalIgnoreCase);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var exportInfo = ChunkedJsonReader.ReadObjectProperty(filePath, "exportInfo", cancellationToken);
        var generator = ImportText.Clean(exportInfo["generator"]);
        var format = ImportText.Clean(exportInfo["format"]);
        if (!string.Equals(generator, "CipherTalk", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(format, "detailed-json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(
                filePath,
                $"CipherTalk 导出器标识无效：generator={generator}, format={format}");
        }

        var session = ChunkedJsonReader.ReadObjectProperty(filePath, "session", cancellationToken);
        var conversation = CipherTalkParser.ReadConversation(session, filePath);

        var ownerId = ImportText.Clean(session["ownerId"]);
        if (string.IsNullOrEmpty(ownerId))
        {
            ownerId = ImportText.Clean(session["ownerID"]);
        }

        var selfSender = !string.IsNullOrEmpty(ownerId)
            ? ownerId
            : CipherTalkParser.InferSelfSender(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", cancellationToken),
                conversation,
                cancellationToken);

        return new ExportFile(
            conversation,
            token => CipherTalkParser.IterateMessages(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", token),
                conversation,
                selfSender,
                filePath));
    }
}

/// <summary>ChatLab 0.0.2 Standard JSON 格式适配器。</summary>
public sealed class ChatLabJsonExportFormat : IChatExportFormat
{
    private const string SupportedVersion = "0.0.2";

    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "chatlab", "meta" },
                cancellationToken))
        {
            return false;
        }

        var chatlab = ChunkedJsonReader.ReadObjectProperty(filePath, "chatlab", cancellationToken);
        var version = ImportText.Clean(chatlab["version"]);
        return string.Equals(version, SupportedVersion, StringComparison.Ordinal);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var chatlab = ChunkedJsonReader.ReadObjectProperty(filePath, "chatlab", cancellationToken);
        var version = ImportText.Clean(chatlab["version"]);
        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            throw new ImportFormatException(
                filePath,
                $"不支持的 ChatLab 导出版本 {Display(version)}；支持版本 {SupportedVersion}");
        }

        var meta = ChunkedJsonReader.ReadObjectProperty(filePath, "meta", cancellationToken);

        List<JsonObject>? members = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "members" }, cancellationToken))
        {
            members = ChunkedJsonReader.EnumerateObjectArray(filePath, "members", cancellationToken).ToList();
        }

        var conversation = ChatLabParser.ReadConversation(meta, filePath, members);
        var mediaResolutionPolicy = string.Equals(
            ImportText.Clean(chatlab["generator"]),
            "WeFlow",
            StringComparison.Ordinal)
                ? MediaResolutionPolicy.WeFlowLayoutA
                : MediaResolutionPolicy.Strict;

        var ownerId = ImportText.Clean(FirstNonEmpty(
            ImportText.Clean(meta["ownerId"]),
            ImportText.Clean(meta["ownerID"]),
            ImportText.Clean(meta["selfWxid"]),
            ImportText.Clean(meta["selfId"]),
            ImportText.Clean(meta["accountId"])));

        var selfSender = !string.IsNullOrEmpty(ownerId)
            ? ownerId
            : ChatLabParser.InferSelfSender(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", cancellationToken),
                conversation,
                cancellationToken);

        return new ExportFile(
            conversation,
            token => ChatLabParser.IterateMessages(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", token),
                conversation,
                selfSender,
                filePath,
                members,
                mediaResolutionPolicy));
    }

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }
}

/// <summary>ChatLab 0.0.2 JSONL 格式适配器。</summary>
public sealed class ChatLabJsonlExportFormat : IChatExportFormat
{
    private const string SupportedVersion = "0.0.2";

    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                cancellationToken.ThrowIfCancellationRequested();
                if (line is null)
                {
                    return false;
                }

                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!trimmed.Contains("\"_type\"", StringComparison.Ordinal) || !trimmed.Contains("\"chatlab\"", StringComparison.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }

                var parsed = System.Text.Json.Nodes.JsonNode.Parse(trimmed);
                cancellationToken.ThrowIfCancellationRequested();
                if (parsed is System.Text.Json.Nodes.JsonObject obj)
                {
                    var typeTag = ImportText.Clean(obj["_type"]);
                    if (!string.Equals(typeTag, "header", StringComparison.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return false;
                    }

                    if (obj["chatlab"] is System.Text.Json.Nodes.JsonObject chatlab)
                    {
                        var version = ImportText.Clean(chatlab["version"]);
                        cancellationToken.ThrowIfCancellationRequested();
                        return string.Equals(version, SupportedVersion, StringComparison.Ordinal);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        JsonObject? header = null;
        var members = new List<JsonObject>();
        var memberDict = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    if (System.Text.Json.Nodes.JsonNode.Parse(trimmed) is not System.Text.Json.Nodes.JsonObject obj)
                    {
                        continue;
                    }

                    var typeTag = ImportText.Clean(obj["_type"]).ToLowerInvariant();
                    if (typeTag == "header")
                    {
                        header ??= obj;
                    }
                    else if (typeTag == "member")
                    {
                        members.Add(obj);
                        var mId = ChatLabParser.ExtractMemberPlatformId(obj);
                        if (mId.Length > 0)
                        {
                            memberDict[mId] = obj;
                        }
                    }
                    else if (typeTag == "message" && header != null)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }

        if (header == null)
        {
            throw new ImportFormatException(filePath, "ChatLab JSONL 缺少有效 header");
        }

        if (header["chatlab"] is not JsonObject chatlab)
        {
            throw new ImportFormatException(filePath, "ChatLab JSONL header 缺少 chatlab 对象");
        }

        var version = ImportText.Clean(chatlab["version"]);
        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            throw new ImportFormatException(
                filePath,
                $"不支持的 ChatLab 导出版本 {Display(version)}；支持版本 {SupportedVersion}");
        }

        var meta = header["meta"] as JsonObject ?? header;
        var conversation = ChatLabParser.ReadConversation(meta, filePath, members);
        var mediaResolutionPolicy = string.Equals(
            ImportText.Clean(chatlab["generator"]),
            "WeFlow",
            StringComparison.Ordinal)
                ? MediaResolutionPolicy.WeFlowLayoutA
                : MediaResolutionPolicy.Strict;

        var ownerId = ImportText.Clean(FirstNonEmpty(
            ImportText.Clean(meta["ownerId"]),
            ImportText.Clean(meta["ownerID"]),
            ImportText.Clean(meta["selfWxid"]),
            ImportText.Clean(meta["selfId"]),
            ImportText.Clean(meta["accountId"])));

        var selfSender = !string.IsNullOrEmpty(ownerId) ? ownerId : null;

        return new ExportFile(
            conversation,
            token => ChatLabParser.IterateJsonlMessages(
                filePath,
                conversation,
                selfSender,
                token,
                memberDict,
                mediaResolutionPolicy));
    }

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }
}

/// <summary>Current WeFlow CSV export adapter.</summary>
public sealed class WeFlowCsvExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        return WeFlowCsvParser.Matches(filePath, cancellationToken);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowCsvParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => WeFlowCsvParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>Current WeFlow Markdown export adapter.</summary>
public sealed class WeFlowMarkdownExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        return WeFlowMarkdownParser.Matches(filePath, cancellationToken);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowMarkdownParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => WeFlowMarkdownParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>Current QQ Chat Exporter TXT adapter.</summary>
public sealed class QqTextExportFormat : IChatExportFormat
{
    public string Platform => "qq";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        return QqTextParser.Matches(filePath, cancellationToken);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = QqTextParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => QqTextParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>Current WeFlow TXT export adapter.</summary>
public sealed class WeFlowTextExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath) => Matches(filePath, CancellationToken.None);

    public bool Matches(string filePath, CancellationToken cancellationToken)
    {
        return WeFlowTextParser.Matches(filePath, cancellationToken);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowTextParser.ReadConversation(filePath, cancellationToken);
        return new ExportFile(
            conversation,
            token => WeFlowTextParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>注册表：新增导出格式时在此追加实例。</summary>
public static class ExportFormats
{
    private static readonly object Gate = new();
    private static volatile IReadOnlyList<IChatExportFormat> _formats = CreateDefaultFormats();

    private static IChatExportFormat[] CreateDefaultFormats() => new IChatExportFormat[]
    {
        new QqExportFormat(),
        new QqChunkedExportFormat(),
        new WeFlowExportFormat(),
        new CipherTalkDetailedJsonFormat(),
        new ChatLabJsonExportFormat(),
        new ChatLabJsonlExportFormat(),
        new WeFlowCsvExportFormat(),
        new WeFlowMarkdownExportFormat(),
        new QqTextExportFormat(),
        new WeFlowTextExportFormat(),
        new WeFlowSqlExportFormat(),
        new CipherTalkSqlExportFormat(),
        new WeFlowExcelExportFormat(),
        new CipherTalkExcelExportFormat(),
        new QqExcelExportFormat(),
    };

    public static IReadOnlyList<IChatExportFormat> Default => _formats;

    /// <summary>运行时注册新格式（供测试或未来插件使用）。</summary>
    public static void Register(IChatExportFormat format)
    {
        if (format is null)
        {
            throw new ArgumentNullException(nameof(format));
        }

        lock (Gate)
        {
            var list = new List<IChatExportFormat>(_formats) { format };
            _formats = list.ToArray();
        }
    }
}
