using System.Buffers;
using System.Security.Cryptography;

namespace ChatArchive.Core.IO;

public sealed class AvatarStorageService
{
    private const int BufferSize = 64 * 1024;

    public string AvatarDirectory { get; }

    public AvatarStorageService(string avatarDirectory)
    {
        AvatarDirectory = Path.GetFullPath(avatarDirectory ?? throw new ArgumentNullException(nameof(avatarDirectory)));
        Directory.CreateDirectory(AvatarDirectory);
        CleanupOrphanedTempFiles();
    }

    public void CleanupOrphanedTempFiles()
    {
        if (!Directory.Exists(AvatarDirectory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(AvatarDirectory, ".tmp_*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
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
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var normalizedExt = NormalizeExtension(extension);
        var tempFile = Path.Combine(AvatarDirectory, $".tmp_{Guid.NewGuid():N}");
        var moved = false;

        try
        {
            string digest;
            using (var tempFs = new FileStream(
                       tempFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       BufferSize,
                       FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        tempFs.Write(buffer, 0, bytesRead);
                        hash.AppendData(buffer, 0, bytesRead);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                tempFs.Flush(flushToDisk: true);
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var fileName = $"{digest}{normalizedExt}";
            var targetPath = Path.Combine(AvatarDirectory, fileName);

            if (File.Exists(targetPath))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            else
            {
                try
                {
                    File.Move(tempFile, targetPath);
                    moved = true;
                }
                catch (IOException) when (File.Exists(targetPath))
                {
                    // Handled race condition if another thread or process moved the exact file concurrently
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            return fileName;
        }
        finally
        {
            if (!moved && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public string SaveAvatarFromFile(string sourceFilePath)
    {
        if (sourceFilePath is null)
        {
            throw new ArgumentNullException(nameof(sourceFilePath));
        }

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
            var combined = Path.Combine(AvatarDirectory, relativeOrHashPath);
            fullPath = Path.GetFullPath(combined);
        }

        var fullAvatarDir = Path.GetFullPath(AvatarDirectory);
        var normalizedDir = fullAvatarDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase) && !string.Equals(fullPath, fullAvatarDir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
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
