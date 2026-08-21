using System.Text.Json;

namespace ChatArchive.Core.Importing;

/// <summary>QQ Chat Exporter 格式适配器。</summary>
public sealed class QqExportFormat : IChatExportFormat
{
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

    public ExportFile Open(string filePath)
    {
        var document = ImportText.ParseDocument(filePath);
        var conversation = QqParser.ReadConversation(document, filePath);
        return new ExportFile(
            document,
            conversation,
            hint => QqParser.IterateMessages(document, conversation, filePath));
    }

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
    public string Platform => "wechat";

    public bool Matches(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var head = QqExportFormat.ReadHead(filePath, 8192);
        if (!head.Contains("\"weflow\"", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = ImportText.ParseDocument(filePath);
            return HasKey(document, "session") && HasKey(document, "messages");
        }
        catch (ImportFormatException)
        {
            return false;
        }
    }

    public ExportFile Open(string filePath)
    {
        var document = ImportText.ParseDocument(filePath);
        var (conversation, selfSender) = WeFlowParser.ReadConversation(document, filePath);
        return new ExportFile(
            document,
            conversation,
            hint => WeFlowParser.IterateMessages(document, conversation, hint ?? selfSender, filePath));
    }

    private static bool HasKey(JsonDocument document, string key)
    {
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(key, out _);
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
