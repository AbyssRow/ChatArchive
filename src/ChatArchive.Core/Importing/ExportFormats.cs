using System.Text.Json.Nodes;
using ChatArchive.Core.IO;

namespace ChatArchive.Core.Importing;

/// <summary>QQ Chat Exporter 格式适配器。</summary>
public sealed class QqExportFormat : IChatExportFormat
{
    private const string ExporterName = "QQChatExporter";

    public string Platform => "qq";

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "chatInfo" }))
        {
            return false;
        }

        JsonObject? metadata = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "metadata" }))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "metadata");
        }
        else if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "exporter" }))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "exporter");
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

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "chatInfo" }))
        {
            return false;
        }

        JsonObject? metadata = null;
        if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "metadata" }))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "metadata");
        }
        else if (ChunkedJsonReader.ContainsRootProperties(filePath, new[] { "exporter" }))
        {
            metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "exporter");
        }

        if (metadata == null)
        {
            return false;
        }

        var name = ImportText.Clean(metadata["name"]);
        return name.Contains(ExporterName, StringComparison.OrdinalIgnoreCase);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
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
        var chunkFiles = new List<string>();
        var chunksSubdir = Path.Combine(manifestDir, "chunks");
        if (Directory.Exists(chunksSubdir))
        {
            chunkFiles.AddRange(Directory.GetFiles(chunksSubdir, "*.jsonl"));
        }
        chunkFiles.AddRange(Directory.GetFiles(manifestDir, "*.jsonl"));

        var sortedChunks = chunkFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
            .ToList();

        return new ExportFile(
            conversation,
            token => IterateChunkedMessages(sortedChunks, conversation, selfSender, manifestDir, token));
    }

    private static IEnumerable<ParsedMessage> IterateChunkedMessages(
        IReadOnlyList<string> chunkFiles,
        ParsedConversation conversation,
        string? selfSender,
        string exportRoot,
        CancellationToken cancellationToken)
    {
        var globalIndex = 0;
        foreach (var chunkFile in chunkFiles)
        {
            using var stream = new FileStream(
                chunkFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
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

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";

    private sealed class NaturalStringComparer : IComparer<string?>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int startX = i;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    int startY = j;
                    while (j < y.Length && char.IsDigit(y[j])) j++;

                    var spanX = x.AsSpan(startX, i - startX);
                    var spanY = y.AsSpan(startY, j - startY);

                    if (ulong.TryParse(spanX, out var numX) && ulong.TryParse(spanY, out var numY))
                    {
                        var numCmp = numX.CompareTo(numY);
                        if (numCmp != 0) return numCmp;
                    }
                    else
                    {
                        var lenCmp = spanX.Length.CompareTo(spanY.Length);
                        if (lenCmp != 0) return lenCmp;
                        var cmp = spanX.SequenceCompareTo(spanY);
                        if (cmp != 0) return cmp;
                    }
                }
                else
                {
                    int cmp = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (cmp != 0) return cmp;
                    i++;
                    j++;
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}

/// <summary>WeFlow 格式适配器。</summary>
public sealed class WeFlowExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ChunkedJsonReader.ContainsRootProperties(
            filePath,
            new[] { "weflow", "session" });
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

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "exportInfo", "session", "messages" }))
        {
            return false;
        }

        var exportInfo = ChunkedJsonReader.ReadObjectProperty(filePath, "exportInfo");
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

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "chatlab", "meta" }))
        {
            return false;
        }

        var chatlab = ChunkedJsonReader.ReadObjectProperty(filePath, "chatlab");
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
                members));
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

    public bool Matches(string filePath)
    {
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

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!trimmed.Contains("\"_type\"", StringComparison.Ordinal) || !trimmed.Contains("\"chatlab\"", StringComparison.Ordinal))
                {
                    return false;
                }

                if (System.Text.Json.Nodes.JsonNode.Parse(trimmed) is System.Text.Json.Nodes.JsonObject obj)
                {
                    var typeTag = ImportText.Clean(obj["_type"]);
                    if (!string.Equals(typeTag, "header", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (obj["chatlab"] is System.Text.Json.Nodes.JsonObject chatlab)
                    {
                        var version = ImportText.Clean(chatlab["version"]);
                        return string.Equals(version, SupportedVersion, StringComparison.Ordinal);
                    }
                }

                return false;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
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

        var ownerId = ImportText.Clean(FirstNonEmpty(
            ImportText.Clean(meta["ownerId"]),
            ImportText.Clean(meta["ownerID"]),
            ImportText.Clean(meta["selfWxid"]),
            ImportText.Clean(meta["selfId"]),
            ImportText.Clean(meta["accountId"])));

        var selfSender = !string.IsNullOrEmpty(ownerId) ? ownerId : null;

        return new ExportFile(
            conversation,
            token => ChatLabParser.IterateJsonlMessages(filePath, conversation, selfSender, token, memberDict));
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

    public bool Matches(string filePath)
    {
        return WeFlowCsvParser.Matches(filePath);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowCsvParser.ReadConversation(filePath);
        return new ExportFile(
            conversation,
            token => WeFlowCsvParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>Current WeFlow Markdown export adapter.</summary>
public sealed class WeFlowMarkdownExportFormat : IChatExportFormat
{
    public string Platform => "wechat";

    public bool Matches(string filePath)
    {
        return WeFlowMarkdownParser.Matches(filePath);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowMarkdownParser.ReadConversation(filePath);
        return new ExportFile(
            conversation,
            token => WeFlowMarkdownParser.IterateMessages(filePath, conversation, token));
    }
}

/// <summary>Current QQ Chat Exporter TXT adapter.</summary>
public sealed class QqTextExportFormat : IChatExportFormat
{
    public string Platform => "qq";

    public bool Matches(string filePath)
    {
        return QqTextParser.Matches(filePath);
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

    public bool Matches(string filePath)
    {
        return WeFlowTextParser.Matches(filePath);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var conversation = WeFlowTextParser.ReadConversation(filePath);
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
