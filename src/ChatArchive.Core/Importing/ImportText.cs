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

    /// <summary>
    /// 安全解析媒体文件路径，支持多级 fallback 探测：
    /// 1) exportRoot 下同级
    /// 2) exportRoot/resources/
    /// 3) exportRoot/media/
    /// 4) exportRoot 上级目录 parentDir 及 parentDir/media/<sessionTitle>/
    /// 如果任何一个路径存在于磁盘（File.Exists），立即返回该有效物理路径。
    /// 若均未在磁盘发现，返回 SafeExportPath(exportRoot, normalized)（保持原语义）。
    /// </summary>
    public static string? SafeResolveMedia(string exportRoot, string declaredPath, string? sessionTitle = null)
    {
        if (string.IsNullOrWhiteSpace(declaredPath))
        {
            return null;
        }

        var normalized = declaredPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            return null;
        }

        var direct = SafeExportPath(exportRoot, normalized);
        if (direct != null && File.Exists(direct))
        {
            return direct;
        }

        var inResources = SafeExportPath(exportRoot, Path.Combine("resources", normalized));
        if (inResources != null && File.Exists(inResources))
        {
            return inResources;
        }

        var inMedia = SafeExportPath(exportRoot, Path.Combine("media", normalized));
        if (inMedia != null && File.Exists(inMedia))
        {
            return inMedia;
        }

        try
        {
            var parentDir = Path.GetDirectoryName(Path.GetFullPath(exportRoot));
            if (!string.IsNullOrEmpty(parentDir))
            {
                var inParent = SafeExportPath(parentDir, normalized);
                if (inParent != null && File.Exists(inParent))
                {
                    return inParent;
                }

                if (!string.IsNullOrWhiteSpace(sessionTitle))
                {
                    var inParentMediaSession = SafeExportPath(parentDir, Path.Combine("media", sessionTitle, normalized));
                    if (inParentMediaSession != null && File.Exists(inParentMediaSession))
                    {
                        return inParentMediaSession;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // Ignore path resolution errors when probing parent directory
        }

        return direct;
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


