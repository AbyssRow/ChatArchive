using System.Buffers;
using System.Security.Cryptography;

namespace ChatArchive.Core.IO;

public sealed class AvatarStorageService
{
    private const int BufferSize = 64 * 1024;

    public string AvatarDirectory { get; }

    public AvatarStorageService(string avatarDirectory)
    {
        AvatarDirectory = avatarDirectory ?? throw new ArgumentNullException(nameof(avatarDirectory));
        Directory.CreateDirectory(AvatarDirectory);
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

                tempFs.Flush();
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

        if (Path.IsPathRooted(relativeOrHashPath))
        {
            return File.Exists(relativeOrHashPath) ? Path.GetFullPath(relativeOrHashPath) : null;
        }

        var combined = Path.Combine(AvatarDirectory, relativeOrHashPath);
        return File.Exists(combined) ? Path.GetFullPath(combined) : null;
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

        return trimmed.ToLowerInvariant();
    }
}
