using ChatArchive.Core.IO;

namespace ChatArchive.Core.Importing;

/// <summary>QQ Chat Exporter 格式适配器。</summary>
public sealed class QqExportFormat : IChatExportFormat
{
    private const string SupportedVersion = "4";

    public string Platform => "qq";

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var head = ReadHead(filePath, 4096);
        return head.Contains("QQChatExporter", StringComparison.Ordinal)
            && head.Contains("\"chatInfo\"", StringComparison.Ordinal);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var metadata = ChunkedJsonReader.ReadObjectProperty(
            filePath,
            "QQChatExporter",
            cancellationToken);
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

    internal static string ReadHead(string path, int charCount)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[charCount];
            var read = reader.Read(buffer, 0, charCount);
            return new string(buffer, 0, read);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}

/// <summary>WeFlow 格式适配器。</summary>
public sealed class WeFlowExportFormat : IChatExportFormat
{
    private const string SupportedVersion = "1.0.3";

    public string Platform => "wechat";

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var head = QqExportFormat.ReadHead(filePath, 8192);
        return head.Contains("\"weflow\"", StringComparison.Ordinal)
            && head.Contains("\"session\"", StringComparison.Ordinal);
    }

    public ExportFile Open(string filePath, CancellationToken cancellationToken = default)
    {
        var metadata = ChunkedJsonReader.ReadObjectProperty(filePath, "weflow", cancellationToken);
        var version = ImportText.Clean(metadata["version"]);
        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            throw new ImportFormatException(
                filePath,
                $"不支持的 WeFlow 导出版本 {Display(version)}；支持版本 {SupportedVersion}，请先更新 ChatArchive");
        }

        var session = ChunkedJsonReader.ReadObjectProperty(filePath, "session", cancellationToken);
        var conversation = WeFlowParser.ReadConversation(session, filePath);
        var selfSender = WeFlowParser.InferSelfSender(
            ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", cancellationToken),
            conversation,
            cancellationToken);
        return new ExportFile(
            conversation,
            token => WeFlowParser.IterateMessages(
                ChunkedJsonReader.EnumerateObjectArray(filePath, "messages", token),
                conversation,
                selfSender,
                filePath));
    }

    private static string Display(string version) => version.Length == 0 ? "（缺失）" : $"“{version}”";
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
