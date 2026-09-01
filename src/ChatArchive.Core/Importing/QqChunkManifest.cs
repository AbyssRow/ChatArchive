using System.Text.Json;

namespace ChatArchive.Core.Importing;

internal static class QqChunkManifest
{
    internal static IReadOnlyList<string> ResolveChunkFiles(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fullManifest = Path.GetFullPath(manifestPath);
            var exportRoot = Path.GetDirectoryName(fullManifest)
                ?? throw InvalidManifest(manifestPath, "manifest 缺少父目录");
            var safeManifest = ImportText.ResolveManifestRegularFileUnderRoot(
                exportRoot,
                Path.GetFileName(fullManifest));
            if (!PathEquals(safeManifest, fullManifest))
            {
                throw InvalidManifest(
                    manifestPath,
                    "manifest 不存在、不是普通文件或包含重解析点");
            }

            using var document = ImportText.ParseDocument(fullManifest);
            cancellationToken.ThrowIfCancellationRequested();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidManifest(manifestPath, "JSON 根节点必须是对象");
            }

            return root.TryGetProperty("chunked", out var chunked)
                ? ResolveAuthoritativeChunks(manifestPath, exportRoot, chunked, cancellationToken)
                : ResolveLegacyChunks(manifestPath, exportRoot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
                                   or UnauthorizedAccessException or NotSupportedException)
        {
            throw InvalidManifest(manifestPath, $"清单读取失败（{ex.Message}）", innerException: ex);
        }
    }

    private static IReadOnlyList<string> ResolveAuthoritativeChunks(
        string manifestPath,
        string exportRoot,
        JsonElement chunked,
        CancellationToken cancellationToken)
    {
        if (chunked.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest(manifestPath, "chunked 必须是对象");
        }
        if (!chunked.TryGetProperty("chunks", out var chunks)
            || chunks.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest(manifestPath, "chunked.chunks 必须是数组");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        string? validatedChunksDir = null;
        string? explicitChunksDir = null;
        if (chunked.TryGetProperty("chunksDir", out var chunksDirElement))
        {
            if (chunksDirElement.ValueKind != JsonValueKind.String)
            {
                throw InvalidManifest(manifestPath, "chunked.chunksDir 必须是字符串");
            }
            explicitChunksDir = chunksDirElement.GetString();
            validatedChunksDir = ValidateChunksDirectory(
                manifestPath,
                exportRoot,
                explicitChunksDir ?? string.Empty);
        }

        var index = 0;
        foreach (var entry in chunks.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveChunkDeclaration(
                manifestPath,
                exportRoot,
                entry,
                index++,
                explicitChunksDir,
                ref validatedChunksDir);
            if (!seen.Add(path))
            {
                var relative = Path.GetRelativePath(exportRoot, path).Replace('\\', '/');
                throw InvalidManifest(manifestPath, "chunks 含重复的规范路径", relative);
            }
            result.Add(path);
        }
        return result;
    }

    private static IReadOnlyList<string> ResolveLegacyChunks(
        string manifestPath,
        string exportRoot,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        var chunksPath = Path.Combine(exportRoot, "chunks");
        if (Path.Exists(chunksPath))
        {
            var safeChunks = ImportText.ResolveManifestDirectoryUnderRoot(exportRoot, "chunks");
            if (safeChunks is null)
            {
                throw InvalidManifest(
                    manifestPath,
                    "legacy chunks 目录不是普通目录或包含重解析点",
                    "chunks");
            }
            candidates.AddRange(Directory.GetFiles(
                safeChunks,
                "*.jsonl",
                SearchOption.TopDirectoryOnly));
        }
        candidates.AddRange(Directory.GetFiles(
            exportRoot,
            "*.jsonl",
            SearchOption.TopDirectoryOnly));

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var validated = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(exportRoot, candidate).Replace('\\', '/');
            var safe = ImportText.ResolveManifestRegularFileUnderRoot(exportRoot, relative);
            if (safe is null)
            {
                throw InvalidManifest(
                    manifestPath,
                    "legacy 分块不是普通文件或包含重解析点",
                    relative);
            }
            if (!validated.Contains(safe, comparer))
            {
                validated.Add(safe);
            }
        }

        return validated
            .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
            .ToList();
    }

    private static string ResolveChunkDeclaration(
        string manifestPath,
        string exportRoot,
        JsonElement entry,
        int index,
        string? explicitChunksDir,
        ref string? validatedChunksDir)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest(manifestPath, $"chunks[{index}] 必须是对象");
        }

        string declaredPath;
        if (entry.TryGetProperty("relativePath", out var relativePath))
        {
            if (relativePath.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(relativePath.GetString()))
            {
                throw InvalidManifest(
                    manifestPath,
                    $"chunks[{index}].relativePath 必须是非空字符串");
            }
            declaredPath = relativePath.GetString()!;
        }
        else
        {
            if (!entry.TryGetProperty("fileName", out var fileNameElement)
                || fileNameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(fileNameElement.GetString()))
            {
                throw InvalidManifest(
                    manifestPath,
                    $"chunks[{index}] 缺少 relativePath 或有效 fileName");
            }

            var fileName = fileNameElement.GetString()!;
            if (fileName is "." or ".."
                || fileName.Contains('/')
                || fileName.Contains('\\')
                || !string.Equals(
                    Path.GetExtension(fileName),
                    ".jsonl",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidManifest(
                    manifestPath,
                    $"chunks[{index}].fileName 必须是 .jsonl basename",
                    fileName);
            }

            validatedChunksDir ??= ValidateChunksDirectory(
                manifestPath,
                exportRoot,
                explicitChunksDir ?? "chunks");
            declaredPath = $"{validatedChunksDir}/{fileName}";
        }

        var normalizedDeclaredPath = declaredPath.Replace('\\', '/');
        if (IsRootedOrUriLike(declaredPath)
            || normalizedDeclaredPath.Split('/').Any(segment => segment is "." or "..")
            || !string.Equals(
                Path.GetExtension(declaredPath),
                ".jsonl",
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidManifest(
                manifestPath,
                $"chunks[{index}] 路径必须是相对 .jsonl 文件且不得包含点路径段",
                declaredPath);
        }

        var resolved = ImportText.ResolveManifestRegularFileUnderRoot(exportRoot, declaredPath);
        if (resolved is null)
        {
            throw InvalidManifest(
                manifestPath,
                $"chunks[{index}] 文件不存在、越界、不是普通文件或包含重解析点",
                declaredPath);
        }

        return resolved;
    }

    private static string ValidateChunksDirectory(
        string manifestPath,
        string exportRoot,
        string declaredChunksDir)
    {
        if (string.IsNullOrWhiteSpace(declaredChunksDir)
            || IsRootedOrUriLike(declaredChunksDir))
        {
            throw InvalidManifest(
                manifestPath,
                "chunksDir 必须是普通相对目录",
                declaredChunksDir);
        }

        var normalized = declaredChunksDir.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                                    || segment is "." or ".."))
        {
            throw InvalidManifest(
                manifestPath,
                "chunksDir 含空段或点路径段",
                declaredChunksDir);
        }

        var resolved = ImportText.ResolveManifestDirectoryUnderRoot(exportRoot, normalized);
        if (resolved is null)
        {
            throw InvalidManifest(
                manifestPath,
                "chunksDir 不存在、越界、不是普通目录或包含重解析点",
                declaredChunksDir);
        }
        return Path.GetRelativePath(exportRoot, resolved).Replace('\\', '/');
    }

    private static bool IsRootedOrUriLike(string value) =>
        value[0] is '/' or '\\'
        || Path.IsPathRooted(value)
        || Uri.TryCreate(value, UriKind.Absolute, out _);

    private static bool PathEquals(string? left, string right) =>
        left is not null && string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static ImportFormatException InvalidManifest(
        string manifestPath,
        string reason,
        string? declaredPath = null,
        Exception? innerException = null)
    {
        var message = declaredPath is null
            ? reason
            : $"{reason}（声明路径：{declaredPath}）";
        return innerException is null
            ? new ImportFormatException(manifestPath, message)
            : new ImportFormatException(manifestPath, message, innerException);
    }

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
