namespace ChatArchive.Core.Media;

/// <summary>
/// 附件文件定位：优先按 sha256 内容寻址规则推导，失败时回退库内记录的路径。
/// </summary>
public sealed class MediaLocator
{
    private readonly string _mediaDir;

    public MediaLocator(string mediaDir)
    {
        _mediaDir = Path.GetFullPath(mediaDir);
    }

    public string? Resolve(string? sha256, string? managedPath = null, string? sourcePath = null)
    {
        if (!string.IsNullOrEmpty(sha256))
        {
            var prefixDir = Path.Combine(_mediaDir, sha256[..2]);
            var suffix = Path.GetExtension(managedPath ?? string.Empty);
            if (suffix.Length > 0 && suffix.Length <= 12 && IsPlainExtension(suffix))
            {
                var exact = Path.Combine(prefixDir, sha256 + suffix);
                if (File.Exists(exact))
                {
                    return exact;
                }
            }

            var bare = Path.Combine(prefixDir, sha256);
            if (File.Exists(bare))
            {
                return bare;
            }

            if (Directory.Exists(prefixDir))
            {
                var match = Directory
                    .EnumerateFiles(prefixDir, sha256 + ".*")
                    .OrderBy(f => f.Length)
                    .FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }
        }

        foreach (var fallback in new[] { managedPath, sourcePath })
        {
            if (!string.IsNullOrEmpty(fallback) && File.Exists(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private static bool IsPlainExtension(string suffix)
    {
        foreach (var ch in suffix.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
