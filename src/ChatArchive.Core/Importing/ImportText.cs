using System.Globalization;
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

    public ImportFormatException(
        string filePath,
        string message,
        Exception innerException)
        : base($"{filePath}: {message}", innerException)
    {
        FilePath = filePath;
    }
}

public enum MediaResolutionPolicy
{
    Strict,
    WeFlowLayoutA,
}

/// <summary>导入器共享的文本/路径工具，行为对齐旧版 Python。</summary>
public static class ImportText
{
    public static string Clean(string? value)
    {
        return value?.Replace("\0", "").Trim() ?? string.Empty;
    }

    public static string StableFileNativeId(string filePath)
    {
        var normalized = Path.GetFullPath(filePath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return $"file:{Convert.ToHexStringLower(digest)}";
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

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var s))
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
            {
                return parsedLong;
            }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
            {
                return (long)parsedDouble;
            }

            return null;
        }

        var raw = value.ToJsonString();
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubled))
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

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var s))
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
            {
                return parsedDouble;
            }

            return null;
        }

        var raw = value.ToJsonString();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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
            return SafeExportPathCore(exportRoot, declaredPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? SafeExportPathCore(string exportRoot, string declaredPath)
    {
        var rootFull = Path.GetFullPath(exportRoot);
        var normalized = declaredPath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(rootFull, normalized));
        return TryGetExactRelativePathUnderRoot(rootFull, candidate, out _)
            ? candidate
            : null;
    }

    private static bool TryGetExactRelativePathUnderRoot(
        string rootFull,
        string candidateFull,
        out string relativePath)
    {
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!candidateFull.StartsWith(prefix, StringComparison.Ordinal))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = candidateFull[prefix.Length..];
        return relativePath.Length > 0;
    }

    private static readonly HashSet<string> WeFlowParentMediaDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "images", "voices", "videos", "emojis", "file" };

    private static string? ResolveWeFlowParentMedia(string exportRoot, string normalized)
    {
        var segments = normalized.Split('/');
        if (segments.Length < 3
            || segments[0] != ".."
            || !WeFlowParentMediaDirectories.Contains(segments[1])
            || segments.Skip(2).Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(exportRoot);
            var parent = Path.GetDirectoryName(root);
            if (parent is null)
            {
                return null;
            }

            var relativeSegments = segments.Skip(1).ToArray();
            var relative = Path.Combine(relativeSegments);
            return ResolveExistingRegularFile(parent, relative, out _);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? ResolveExistingRegularFileUnderRoot(
        string root,
        string declaredRelativePath)
    {
        var candidate = SafeExportPath(root, declaredRelativePath);
        return candidate is not null
               && File.Exists(candidate)
               && HasSafePathComponents(root, candidate, SafePathTarget.ExistingRegularFile)
            ? candidate
            : null;
    }

    internal static string? ResolveExistingDirectoryUnderRoot(
        string root,
        string declaredRelativePath)
    {
        var candidate = SafeExportPath(root, declaredRelativePath);
        return candidate is not null
               && Directory.Exists(candidate)
               && HasSafePathComponents(root, candidate, SafePathTarget.ExistingDirectory)
            ? candidate
            : null;
    }

    internal static string? ResolveManifestRegularFileUnderRoot(
        string root,
        string declaredRelativePath) =>
        ResolveManifestTargetUnderRoot(
            root,
            declaredRelativePath,
            SafePathTarget.ExistingRegularFile);

    internal static string? ResolveManifestDirectoryUnderRoot(
        string root,
        string declaredRelativePath) =>
        ResolveManifestTargetUnderRoot(
            root,
            declaredRelativePath,
            SafePathTarget.ExistingDirectory);

    private static string? ResolveManifestTargetUnderRoot(
        string root,
        string declaredRelativePath,
        SafePathTarget target)
    {
        var candidate = SafeExportPathCore(root, declaredRelativePath);
        return candidate is not null
               && HasSafePathComponents(
                   root,
                   candidate,
                   target,
                   preserveProbeExceptions: true)
            ? candidate
            : null;
    }

    private enum SafePathTarget
    {
        ExistingRegularFile,
        ExistingDirectory,
        PotentialRegularFile,
    }

    private static string? ResolveExistingRegularFile(
        string root,
        string relativePath,
        out bool unsafeExistingCandidate)
    {
        var candidate = SafeExportPath(root, relativePath);
        var exists = candidate is not null && File.Exists(candidate);
        unsafeExistingCandidate = exists
            && !HasSafePathComponents(root, candidate!, SafePathTarget.ExistingRegularFile);
        return exists && !unsafeExistingCandidate
            ? candidate
            : null;
    }

    private static bool HasSafePathComponents(
        string root,
        string candidate,
        SafePathTarget target,
        bool preserveProbeExceptions = false)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            if (!TryGetPathAttributes(
                    rootFull,
                    out var rootAttributes,
                    out var rootExists,
                    preserveProbeExceptions)
                || !rootExists
                || rootAttributes.HasFlag(FileAttributes.ReparsePoint)
                || !rootAttributes.HasFlag(FileAttributes.Directory))
            {
                return false;
            }

            if (!TryGetExactRelativePathUnderRoot(
                    rootFull,
                    Path.GetFullPath(candidate),
                    out var relative))
            {
                return false;
            }
            var segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            {
                return false;
            }

            var current = rootFull;
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                if (!TryGetPathAttributes(
                        current,
                        out var attributes,
                        out var exists,
                        preserveProbeExceptions))
                {
                    return false;
                }

                if (!exists)
                {
                    return target == SafePathTarget.PotentialRegularFile;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                var isLast = index == segments.Length - 1;
                if (isLast)
                {
                    return target switch
                    {
                        SafePathTarget.ExistingRegularFile => !attributes.HasFlag(FileAttributes.Directory),
                        SafePathTarget.ExistingDirectory => attributes.HasFlag(FileAttributes.Directory),
                        SafePathTarget.PotentialRegularFile => !attributes.HasFlag(FileAttributes.Directory),
                        _ => false,
                    };
                }

                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    return false;
                }
            }

            return false;
        }
        catch (Exception ex) when (
            !preserveProbeExceptions
            && ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetPathAttributes(
        string path,
        out FileAttributes attributes,
        out bool exists,
        bool preserveProbeExceptions)
    {
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            exists = false;
            return true;
        }
        catch (Exception ex) when (
            !preserveProbeExceptions
            && ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            attributes = default;
            exists = false;
            return false;
        }
    }

    private static bool IsRootedOrUriLikeDeclaration(string declaredPath)
    {
        return declaredPath[0] is '/' or '\\'
            || Path.IsPathRooted(declaredPath)
            || Uri.TryCreate(declaredPath, UriKind.Absolute, out _);
    }

    /// <summary>
    /// 安全解析媒体文件路径。默认拒绝父目录；只有显式 WeFlow policy 允许受限 layout-A 父目录。
    /// 普通相对路径支持多级 fallback 探测：
    /// 1) exportRoot 下同级
    /// 2) exportRoot/resources/
    /// 3) exportRoot/media/
    /// 4) exportRoot 上级目录 parentDir 及 parentDir/media/<sessionTitle>/
    /// 任何已存在候选都必须是普通文件，且根目录和每个已存在路径组件都不能是 reparse point。
    /// 若没有已存在候选，安全的同级声明仍返回 SafeExportPath(exportRoot, normalized)。
    /// </summary>
    public static string? SafeResolveMedia(
        string exportRoot,
        string declaredPath,
        string? sessionTitle = null,
        MediaResolutionPolicy policy = MediaResolutionPolicy.Strict)
    {
        if (string.IsNullOrWhiteSpace(declaredPath) || IsRootedOrUriLikeDeclaration(declaredPath))
        {
            return null;
        }

        var normalized = declaredPath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            return null;
        }

        if (normalized.StartsWith("../", StringComparison.Ordinal))
        {
            return policy == MediaResolutionPolicy.WeFlowLayoutA
                ? ResolveWeFlowParentMedia(exportRoot, normalized)
                : null;
        }

        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            return null;
        }

        var direct = SafeExportPath(exportRoot, normalized);
        var unsafeExistingCandidate = false;
        var existingDirect = ResolveExistingRegularFile(exportRoot, normalized, out var unsafeDirect);
        unsafeExistingCandidate |= unsafeDirect;
        if (existingDirect is not null)
        {
            return existingDirect;
        }

        var inResources = ResolveExistingRegularFile(
            exportRoot,
            Path.Combine("resources", normalized),
            out var unsafeResources);
        unsafeExistingCandidate |= unsafeResources;
        if (inResources is not null)
        {
            return inResources;
        }

        var inMedia = ResolveExistingRegularFile(
            exportRoot,
            Path.Combine("media", normalized),
            out var unsafeMedia);
        unsafeExistingCandidate |= unsafeMedia;
        if (inMedia is not null)
        {
            return inMedia;
        }

        try
        {
            var parentDir = Path.GetDirectoryName(Path.GetFullPath(exportRoot));
            if (!string.IsNullOrEmpty(parentDir))
            {
                var cleanTitle = SanitizeSessionTitle(sessionTitle);
                if (!string.IsNullOrWhiteSpace(cleanTitle))
                {
                    var inParentMediaSession = ResolveExistingRegularFile(
                        parentDir,
                        Path.Combine("media", cleanTitle, normalized),
                        out var unsafeSharedMedia);
                    unsafeExistingCandidate |= unsafeSharedMedia;
                    if (inParentMediaSession is not null)
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

        return !unsafeExistingCandidate
            && direct is not null
            && HasSafePathComponents(exportRoot, direct, SafePathTarget.PotentialRegularFile)
                ? direct
                : null;
    }

    public static string SanitizeSessionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = title.Where(ch => !invalidChars.Contains(ch) && ch != '/' && ch != '\\').ToArray();
        var sanitized = new string(chars).Trim();
        return (sanitized == "." || sanitized == "..") ? string.Empty : sanitized;
    }

    public static long ParseFlexibleTimestamp(string? timeStr)
    {
        return TryParseFlexibleTimestamp(timeStr, out var timestampMs) ? timestampMs : 0;
    }

    public static bool TryParseFlexibleTimestamp(string? timeStr, out long timestampMs)
    {
        timestampMs = 0;
        if (string.IsNullOrWhiteSpace(timeStr))
        {
            return false;
        }

        timeStr = timeStr.Trim();
        if (long.TryParse(timeStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rawLong))
        {
            if (timeStr.Length == 8 && rawLong is >= 19700101 and <= 20991231
                && DateTimeOffset.TryParseExact(timeStr, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dto8))
            {
                timestampMs = dto8.ToUnixTimeMilliseconds();
                return true;
            }

            timestampMs = rawLong >= 10_000_000_000L ? rawLong : rawLong * 1000L;
            return true;
        }

        if (double.TryParse(timeStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rawDouble))
        {
            var asLong = (long)rawDouble;
            timestampMs = asLong >= 10_000_000_000L ? asLong : asLong * 1000L;
            return true;
        }

        var normalized = System.Text.RegularExpressions.Regex.Replace(timeStr, @"(\d{4})年(\d{1,2})月(\d{1,2})日?", "$1-$2-$3");

        string[] formats = [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm",
            "yyyy-M-d HH:mm:ss",
            "yyyy-M-d HH:mm:ss.fff",
            "yyyy-M-d HH:mm",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss.fff",
            "yyyy/MM/dd HH:mm",
            "yyyy/M/d HH:mm:ss",
            "yyyy/M/d HH:mm",
            "yyyy.MM.dd HH:mm:ss",
            "yyyy.MM.dd HH:mm:ss.fff",
            "yyyy.MM.dd HH:mm",
            "yyyy.M.d HH:mm:ss",
            "yyyy.M.d HH:mm",
            "yyyy-MM-ddTHH:mm:sszzz",
            "yyyy-MM-ddTHH:mm:ss.fffzzz",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyy-MM-dd",
            "yyyy-M-d",
            "yyyy/MM/dd",
            "yyyy/M/d",
            "yyyy.MM.dd",
            "yyyy.M.d"
        ];

        if (DateTimeOffset.TryParseExact(normalized, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dtoExact))
        {
            timestampMs = dtoExact.ToUnixTimeMilliseconds();
            return true;
        }

        if (DateTimeOffset.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var dto))
        {
            timestampMs = dto.ToUnixTimeMilliseconds();
            return true;
        }

        if (DateTime.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
        {
            var local = TimeZoneInfo.Local;
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var offset = local.GetUtcOffset(unspecified);
            timestampMs = new DateTimeOffset(unspecified, offset).ToUnixTimeMilliseconds();
            return true;
        }

        return false;
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

    public static JsonDocument ParseDocument(string filePath) =>
        ParseDocument(filePath, CancellationToken.None);

    public static JsonDocument ParseDocument(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            return document;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ImportFormatException(filePath, $"JSON 解析失败（{ex.Message}）", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(filePath, $"读取失败（{ex.Message}）", ex);
        }
    }
}


