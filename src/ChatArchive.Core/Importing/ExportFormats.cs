using System.Text.Json.Nodes;
using ChatArchive.Core.IO;

namespace ChatArchive.Core.Importing;

file static class ExportFormatText
{
    public static string Display(string value) =>
        value.Length == 0 ? "（缺失）" : $"“{value}”";
}

file static class QqExporter
{
    public const string Name = "QQChatExporter";

    public static JsonObject? TryReadMetadata(string path, CancellationToken cancellationToken) =>
        ChunkedJsonReader.TryReadObjectProperty(path, "metadata", cancellationToken)
        ?? ChunkedJsonReader.TryReadObjectProperty(path, "exporter", cancellationToken);
}

file static class ChatLabHeader
{
    public const string SupportedVersion = "0.0.2";

    public static bool VersionMatches(JsonObject chatlab) =>
        string.Equals(ImportText.Clean(chatlab["version"]), SupportedVersion, StringComparison.Ordinal);

    public static void EnsureVersion(JsonObject chatlab, string filePath)
    {
        if (!VersionMatches(chatlab))
        {
            throw new ImportFormatException(
                filePath,
                $"不支持的 ChatLab 导出版本 {ExportFormatText.Display(ImportText.Clean(chatlab["version"]))}；支持版本 {SupportedVersion}");
        }
    }

    public static MediaResolutionPolicy MediaPolicy(JsonObject chatlab) =>
        string.Equals(ImportText.Clean(chatlab["generator"]), "WeFlow", StringComparison.Ordinal)
            ? MediaResolutionPolicy.WeFlowLayoutA
            : MediaResolutionPolicy.Strict;

    public static string OwnerId(JsonObject meta) =>
        ImportText.Clean(ImportText.FirstNonEmpty(
            ImportText.Clean(meta["ownerId"]),
            ImportText.Clean(meta["ownerID"]),
            ImportText.Clean(meta["selfWxid"]),
            ImportText.Clean(meta["selfId"]),
            ImportText.Clean(meta["accountId"])));
}

public class ParserExportFormat(
    string platform,
    Func<string, CancellationToken, bool> matches,
    Func<string, CancellationToken, ParsedConversation> readConversation,
    Func<string, ParsedConversation, CancellationToken, IEnumerable<ParsedMessage>> iterateMessages)
    : IChatExportFormat
{
    public string Platform => platform;

    public bool Matches(string filePath, CancellationToken cancellationToken = default) =>
        matches(filePath, cancellationToken);

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = readConversation(filePath, cancellationToken);
        return new ExportFile(conversation, token => iterateMessages(filePath, conversation, token));
    }
}

/// <summary>QQ Chat Exporter 格式适配器。</summary>
public sealed class QqExportFormat : IChatExportFormat
{
    public string Platform => "qq";

    public bool Matches(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(filePath, ["chatInfo"], cancellationToken))
        {
            return false;
        }

        var metadata = QqExporter.TryReadMetadata(filePath, cancellationToken);
        return metadata is not null
            && string.Equals(
                ImportText.Clean(metadata["name"]),
                QqExporter.Name,
                StringComparison.OrdinalIgnoreCase);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var metadata = QqExporter.TryReadMetadata(filePath, cancellationToken)
            ?? throw new ImportFormatException(filePath, "缺少 metadata 或 exporter 对象");

        var exporterName = ImportText.Clean(metadata["name"]);
        if (!string.Equals(exporterName, QqExporter.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(
                filePath,
                $"QQ 导出器标识无效：应为 {QqExporter.Name}，实际为 {ExportFormatText.Display(exporterName)}");
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
}

/// <summary>QQ Chat Exporter 分块 JSONL (manifest.json + chunks/*.jsonl) 格式适配器。</summary>
public sealed class QqChunkedExportFormat : IChatExportFormat
{
    public string Platform => "qq";

    public bool Matches(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var safeManifest = QqChunkManifest.ValidateManifestFile(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ChunkedJsonReader.ContainsRootProperties(safeManifest, ["chatInfo"], cancellationToken))
        {
            return false;
        }

        var metadata = QqExporter.TryReadMetadata(safeManifest, cancellationToken);
        if (metadata is null
            || !ImportText.Clean(metadata["name"]).Contains(QqExporter.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _ = QqChunkManifest.ResolveChunkFiles(filePath, cancellationToken);
        return true;
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var chunkFiles = QqChunkManifest.ResolveChunkFiles(filePath, cancellationToken);
        var metadata = QqExporter.TryReadMetadata(filePath, cancellationToken)
            ?? throw new ImportFormatException(filePath, "缺少 metadata 或 exporter 对象");

        var exporterName = ImportText.Clean(metadata["name"]);
        if (!exporterName.Contains(QqExporter.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(
                filePath,
                $"QQ 导出器标识无效：应包含 {QqExporter.Name}，实际为 {ExportFormatText.Display(exporterName)}");
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
}

/// <summary>WeFlow 格式适配器。</summary>
public sealed class WeFlowExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ChunkedJsonReader.ContainsRootProperties(
            filePath,
            ["weflow", "session"],
            cancellationToken);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        _ = ChunkedJsonReader.ReadObjectProperty(filePath, "weflow", cancellationToken);
        var session = ChunkedJsonReader.ReadObjectProperty(filePath, "session", cancellationToken);
        var conversation = WeFlowParser.ReadConversation(session, filePath);

        Dictionary<int, JsonObject>? senders = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, ["senders"], cancellationToken))
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

    public bool Matches(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                ["exportInfo", "session", "messages"],
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
    public string Platform => "wechat";

    public bool Matches(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(filePath, ["chatlab", "meta"], cancellationToken))
        {
            return false;
        }

        var chatlab = ChunkedJsonReader.ReadObjectProperty(filePath, "chatlab", cancellationToken);
        return ChatLabHeader.VersionMatches(chatlab);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var chatlab = ChunkedJsonReader.ReadObjectProperty(filePath, "chatlab", cancellationToken);
        ChatLabHeader.EnsureVersion(chatlab, filePath);

        var meta = ChunkedJsonReader.ReadObjectProperty(filePath, "meta", cancellationToken);
        List<JsonObject>? members = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, ["members"], cancellationToken))
        {
            members = ChunkedJsonReader.EnumerateObjectArray(filePath, "members", cancellationToken).ToList();
        }

        var conversation = ChatLabParser.ReadConversation(meta, filePath, members);
        var mediaResolutionPolicy = ChatLabHeader.MediaPolicy(chatlab);
        var ownerId = ChatLabHeader.OwnerId(meta);
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
}

/// <summary>ChatLab 0.0.2 JSONL 格式适配器。</summary>
public sealed class ChatLabJsonlExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath, CancellationToken cancellationToken = default)
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

                var parsed = JsonNode.Parse(trimmed);
                cancellationToken.ThrowIfCancellationRequested();
                if (parsed is JsonObject obj)
                {
                    var typeTag = ImportText.Clean(obj["_type"]);
                    if (!string.Equals(typeTag, "header", StringComparison.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return false;
                    }

                    if (obj["chatlab"] is JsonObject chatlab)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return ChatLabHeader.VersionMatches(chatlab);
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

                    if (JsonNode.Parse(trimmed) is not JsonObject obj)
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

        ChatLabHeader.EnsureVersion(chatlab, filePath);

        var meta = header["meta"] as JsonObject ?? header;
        var conversation = ChatLabParser.ReadConversation(meta, filePath, members);
        var mediaResolutionPolicy = ChatLabHeader.MediaPolicy(chatlab);
        var ownerId = ChatLabHeader.OwnerId(meta);
        var selfSender = !string.IsNullOrEmpty(ownerId)
            ? ownerId
            : ChatLabParser.InferSelfSender(
                ChatLabParser.EnumerateJsonlMessageObjects(filePath, cancellationToken),
                conversation,
                cancellationToken);

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
}

public sealed class WeFlowCsvExportFormat() : ParserExportFormat(
    "wechat",
    WeFlowCsvParser.Matches,
    WeFlowCsvParser.ReadConversation,
    WeFlowCsvParser.IterateMessages);

public sealed class WeFlowMarkdownExportFormat() : ParserExportFormat(
    "wechat",
    WeFlowMarkdownParser.Matches,
    WeFlowMarkdownParser.ReadConversation,
    WeFlowMarkdownParser.IterateMessages);

public sealed class QqTextExportFormat() : ParserExportFormat(
    "qq",
    QqTextParser.Matches,
    QqTextParser.ReadConversation,
    QqTextParser.IterateMessages);

public sealed class WeFlowTextExportFormat() : ParserExportFormat(
    "wechat",
    WeFlowTextParser.Matches,
    WeFlowTextParser.ReadConversation,
    WeFlowTextParser.IterateMessages);

public sealed class WeFlowExcelExportFormat() : ParserExportFormat(
    "wechat",
    WeFlowExcelParser.Matches,
    WeFlowExcelParser.ReadConversation,
    WeFlowExcelParser.IterateMessages);

public sealed class CipherTalkExcelExportFormat() : ParserExportFormat(
    "wechat",
    CipherTalkExcelParser.Matches,
    CipherTalkExcelParser.ReadConversation,
    CipherTalkExcelParser.IterateMessages);

public sealed class QqExcelExportFormat() : ParserExportFormat(
    "qq",
    QqExcelParser.Matches,
    QqExcelParser.ReadConversation,
    QqExcelParser.IterateMessages);

/// <summary>注册表：新增导出格式时在此追加实例。</summary>
public static class ExportFormats
{
    private static readonly object Gate = new();
    private static volatile IReadOnlyList<IChatExportFormat> _formats = CreateDefaultFormats();

    private static IChatExportFormat[] CreateDefaultFormats() =>
    [
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
    ];

    public static IReadOnlyList<IChatExportFormat> Default => _formats;

    /// <summary>运行时注册新格式（供测试或未来插件使用）。</summary>
    public static void Register(IChatExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        lock (Gate)
        {
            var list = new List<IChatExportFormat>(_formats) { format };
            _formats = list.ToArray();
        }
    }
}
