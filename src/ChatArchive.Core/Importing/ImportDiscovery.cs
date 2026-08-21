using System.Text.Json;

namespace ChatArchive.Core.Importing;

public sealed record DiscoveredImport(string FilePath, string Platform, long FileSize);

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
        var found = new List<(string Path, string Platform, long Size)>();

        foreach (var rawRoot in roots)
        {
            var root = Path.GetFullPath(rawRoot);
            if (File.Exists(root))
            {
                Consider(root);
            }
            else if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.json", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                         }))
                {
                    Consider(file);
                }
            }
        }

        return found
            .OrderBy(item => item.Path.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(item => new DiscoveredImport(item.Path, item.Platform, item.Size))
            .ToList();

        void Consider(string path)
        {
            if (!seen.Add(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            foreach (var prefix in excluded)
            {
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            long size;
            try
            {
                size = new FileInfo(full).Length;
            }
            catch (IOException)
            {
                return;
            }

            foreach (var format in formats)
            {
                bool matches;
                try
                {
                    matches = format.Matches(full);
                }
                catch (Exception ex) when (ex is IOException or ImportFormatException or JsonException)
                {
                    matches = false;
                }

                if (matches)
                {
                    found.Add((full, format.Platform, size));
                    return;
                }
            }
        }
    }
}
