using System.Text.Json;

namespace ChatArchive.Core.Importing;

public sealed record DiscoveredImport(
    string FilePath,
    string Platform,
    long FileSize,
    string? Error = null);

/// <summary>递归发现受支持导出工具的 JSON 文件；格式嗅探由各 IChatExportFormat 提供。</summary>
public static class ImportDiscovery
{
    public static IReadOnlyList<DiscoveredImport> Discover(
        IEnumerable<string> roots,
        IReadOnlyList<IChatExportFormat>? formats = null,
        IEnumerable<string>? excludedRoots = null)
    {
        formats ??= ExportFormats.Default;
        var excluded = (excludedRoots ?? Array.Empty<string>())
            .Select(Path.GetFullPath)
            .Select(p => p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar)
            .ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Path, string Platform, long Size, string? Error)>();

        foreach (var rawRoot in roots)
        {
            var root = Path.GetFullPath(rawRoot);
            if (IsExcluded(root))
            {
                continue;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordError(root, $"无法访问导入路径（{ex.Message}）");
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                RecordError(root, "已跳过链接导入路径，避免扫描选定目录之外的文件");
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                EnumerateDirectory(root);
            }
            else
            {
                Consider(root);
            }
        }

        return found
            .OrderBy(item => item.Path.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(item => new DiscoveredImport(item.Path, item.Platform, item.Size, item.Error))
            .ToList();

        void EnumerateDirectory(string root)
        {
            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                var fullDirectory = Path.GetFullPath(directory);
                if (!visited.Add(fullDirectory) || IsExcluded(fullDirectory))
                {
                    continue;
                }

                string[] files;
                string[] directories;
                try
                {
                    files = Directory.GetFiles(fullDirectory);
                    directories = Directory.GetDirectories(fullDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RecordError(fullDirectory, $"无法枚举导入目录（{ex.Message}）");
                    continue;
                }

                foreach (var file in files)
                {
                    if (string.Equals(
                            Path.GetExtension(file),
                            ".json",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Path.GetExtension(file),
                            ".jsonl",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Path.GetExtension(file),
                            ".html",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Path.GetExtension(file),
                            ".htm",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Consider(file);
                    }
                }

                foreach (var child in directories)
                {
                    if (IsExcluded(child))
                    {
                        continue;
                    }

                    try
                    {
                        if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                        {
                            RecordError(child, "已跳过链接目录，避免扫描选定目录之外的文件");
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        RecordError(child, $"无法检查导入目录（{ex.Message}）");
                        continue;
                    }

                    pending.Push(child);
                }
            }
        }

        bool IsExcluded(string path)
        {
            var full = Path.GetFullPath(path);
            var directoryForm = full.EndsWith(Path.DirectorySeparatorChar)
                ? full
                : full + Path.DirectorySeparatorChar;
            return excluded.Any(prefix =>
                full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || directoryForm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        void RecordError(string path, string error)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                found.Add((full, "unknown", 0, error));
            }
        }

        void Consider(string path)
        {
            var full = Path.GetFullPath(path);
            if (!seen.Add(full))
            {
                return;
            }

            if (IsExcluded(full))
            {
                return;
            }

            try
            {
                if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
                {
                    found.Add((full, "unknown", 0, "已跳过链接文件，避免读取选定目录之外的内容"));
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                found.Add((full, "unknown", 0, $"无法读取文件信息（{ex.Message}）"));
                return;
            }

            long size;
            try
            {
                size = new FileInfo(full).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                found.Add((full, "unknown", 0, $"无法读取文件信息（{ex.Message}）"));
                return;
            }

            foreach (var format in formats)
            {
                bool matches;
                try
                {
                    matches = format.Matches(full);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or ImportFormatException or JsonException)
                {
                    found.Add((full, "unknown", size, $"无法检查导出格式（{ex.Message}）"));
                    return;
                }

                if (matches)
                {
                    found.Add((full, format.Platform, size, null));
                    return;
                }
            }
        }
    }
}
