using System.Text.Json.Nodes;
using ChatArchive.Core.IO;

namespace ChatArchive.Core.Importing;

/// <summary>QQ Chat Exporter 格式适配器。</summary>
public sealed class QqExportFormat : IChatExportFormat
{
    private const string ExporterName = "QQChatExporter";
    private const string SupportedVersion = "0.1.0";

    public string Platform => "qq";

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ChunkedJsonReader.ContainsRootProperties(
                filePath,
                new[] { "metadata", "chatInfo" }))
        {
            return false;
        }

        var metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "metadata");
        return string.Equals(
            ImportText.Clean(metadata["name"]),
            ExporterName,
            StringComparison.Ordinal);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var metadata = ChunkedJsonReader.ReadObjectProperty(
            filePath,
            "metadata",
            cancellationToken);
        var exporterName = ImportText.Clean(metadata["name"]);
        if (!string.Equals(exporterName, ExporterName, StringComparison.Ordinal))
        {
            throw new ImportFormatException(
                filePath,
                $"QQ 导出器标识无效：应为 {ExporterName}，实际为 {Display(exporterName)}");
        }

        var version = ImportText.Clean(metadata["version"]);
        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            throw new ImportFormatException(
                filePath,
                $"不支持的 QQ Chat Exporter 导出版本 {Display(version)}；支持版本 {SupportedVersion}，请先更新 ChatArchive");
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

/// <summary>注册表：新增导出格式时在此追加实例。</summary>
public static class ExportFormats
{
    private static readonly object Gate = new();
    private static List<IChatExportFormat>? _formats;

    public static IReadOnlyList<IChatExportFormat> Default
    {
        get
        {
            lock (Gate)
            {
                return _formats ??= new List<IChatExportFormat>
                {
                    new QqExportFormat(),
                    new WeFlowExportFormat(),
                    new CipherTalkDetailedJsonFormat(),
                };
            }
        }
    }

    /// <summary>运行时注册新格式（供测试或未来插件使用）。</summary>
    public static void Register(IChatExportFormat format)
    {
        lock (Gate)
        {
            _formats ??= new List<IChatExportFormat>();
            _formats.Add(format);
        }
    }
}
