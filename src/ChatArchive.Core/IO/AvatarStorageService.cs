using System.Security.Cryptography;

namespace ChatArchive.Core.IO;

public sealed class AvatarStorageService
{
    private const int BufferSize = 64 * 1024;

    private readonly string _avatarDirectory;

    public AvatarStorageService(string avatarDirectory)
    {
        ArgumentNullException.ThrowIfNull(avatarDirectory);
        _avatarDirectory = Path.GetFullPath(avatarDirectory);
        Directory.CreateDirectory(_avatarDirectory);
        CleanupOrphanedTempFiles();
    }

    private void CleanupOrphanedTempFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_avatarDirectory, ".tmp_*", SearchOption.TopDirectoryOnly))
            {
                TryDelete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string SaveAvatarFromStream(Stream stream, string extension)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var normalizedExt = NormalizeExtension(extension);
        var tempFile = Path.Combine(_avatarDirectory, $".tmp_{Guid.NewGuid():N}");
        try
        {
            string digest;
            using (var tempFs = new FileStream(
                       tempFile,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       BufferSize,
                       FileOptions.SequentialScan))
            {
                stream.CopyTo(tempFs);
                tempFs.Flush(flushToDisk: true);
                tempFs.Position = 0;
                digest = Convert.ToHexString(SHA256.HashData(tempFs)).ToLowerInvariant();
            }

            var fileName = $"{digest}{normalizedExt}";
            var targetPath = Path.Combine(_avatarDirectory, fileName);
            if (!File.Exists(targetPath))
            {
                try
                {
                    File.Move(tempFile, targetPath);
                }
                catch (IOException) when (File.Exists(targetPath))
                {
                }
            }

            return fileName;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    public string SaveAvatarFromFile(string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePath);
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Source avatar file not found.", sourceFilePath);
        }

        var extension = Path.GetExtension(sourceFilePath);
        using var stream = new FileStream(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);

        return SaveAvatarFromStream(stream, extension);
    }

    public string? ResolveAvatarFullPath(string? relativeOrHashPath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrHashPath))
        {
            return null;
        }

        string fullPath;
        if (Path.IsPathRooted(relativeOrHashPath))
        {
            fullPath = Path.GetFullPath(relativeOrHashPath);
        }
        else
        {
            fullPath = Path.GetFullPath(Path.Combine(_avatarDirectory, relativeOrHashPath));
        }

        var normalizedDir = Path.GetFullPath(_avatarDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, Path.GetFullPath(_avatarDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("Extension cannot be null or whitespace.", nameof(extension));
        }

        var trimmed = extension.Trim();
        if (!trimmed.StartsWith('.'))
        {
            trimmed = "." + trimmed;
        }

        var withoutDot = trimmed.Substring(1);
        if (withoutDot.Length == 0 || withoutDot.Length > 10 || !withoutDot.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException($"Invalid extension: {extension}", nameof(extension));
        }

        return trimmed.ToLowerInvariant();
    }
}
