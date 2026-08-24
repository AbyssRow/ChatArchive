using System.Buffers;
using System.Security.Cryptography;

namespace ChatArchive.Core.IO;

public static class FileHashing
{
    public static string Sha256File(string path, CancellationToken cancellationToken = default)
        => HashFile(path, cancellationToken).Digest;

    internal static (string Digest, long Size) HashFile(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                size += read;
            }

            return (
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                size);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static (string Digest, long Size) CopyFileAndHash(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        const int BufferSize = 128 * 1024;
        cancellationToken.ThrowIfCancellationRequested();
        var completed = false;
        try
        {
            (string Digest, long Size) result;
            using (var source = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       BufferSize,
                       FileOptions.SequentialScan))
            using (var destination = new FileStream(
                       destinationPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       BufferSize,
                       FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long size = 0;
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = source.Read(buffer);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (read == 0)
                        {
                            break;
                        }

                        destination.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                        size += read;
                    }

                    destination.Flush();
                    result = (
                        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                        size);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            completed = true;
            return result;
        }
        finally
        {
            if (!completed && File.Exists(destinationPath))
            {
                try
                {
                    File.Delete(destinationPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve the copy/cancellation exception; callers also clean their temp path.
                }
            }
        }
    }
}
