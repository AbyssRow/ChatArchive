using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatArchive.Core.Importing;

public sealed class ImportFormatException : Exception
{
    public string FilePath { get; }

    public ImportFormatException(string filePath, string message)
        : base($"{filePath}: {message}")
    {
        FilePath = filePath;
    }
}

/// <summary>导入器共享的文本/路径工具，行为对齐旧版 Python。</summary>
public static class ImportText
{
    public static string Clean(string? value)
    {
        return value?.Replace("\0", "").Trim() ?? string.Empty;
    }

    private static readonly System.Text.Json.JsonSerializerOptions RawOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>取节点的“原始”文本：字符串节点去引号，其余用宽松转义序列化。</summary>
    public static string RawText(JsonNode? node)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var s))
        {
            return s;
        }

        return node?.ToJsonString(RawOptions) ?? string.Empty;
    }

    public static string Clean(JsonNode? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonValue scalar when scalar.TryGetValue<string>(out var s) => Clean(s),
            _ => Clean(value.ToJsonString()),
        };
    }

    public static long? AsLong(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        var raw = value.ToJsonString();
        if (long.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var doubled))
        {
            return (long)doubled;
        }

        return null;
    }

    public static double? AsDouble(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        var raw = value.ToJsonString();
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>把声明路径限制在导出目录内，拒绝绝对路径与目录穿越。</summary>
    public static string? SafeExportPath(string exportRoot, string declaredPath)
    {
        try
        {
            var rootFull = Path.GetFullPath(exportRoot);
            var normalized = declaredPath.Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                return null;
            }

            var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));
            var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".mkv"] = "video/x-matroska",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/x-wav",
        [".amr"] = "audio/amr",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".zip"] = "application/zip",
    };

    public static string? GuessMime(string? path, string? filename = null)
    {
        var target = path ?? filename ?? string.Empty;
        var extension = Path.GetExtension(target);
        return extension.Length > 0 && MimeByExtension.TryGetValue(extension, out var mime) ? mime : null;
    }

    public static JsonDocument ParseDocument(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ImportFormatException(filePath, $"JSON 解析失败（{ex.Message}）");
        }
        catch (IOException ex)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）");
        }
    }
}


