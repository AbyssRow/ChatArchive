using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using ChatArchive.Core.Importing;

namespace ChatArchive.Core.IO;

public static class FileHashing
{
    public static string Sha256File(string path, CancellationToken cancellationToken = default)
        => HashFile(path, cancellationToken).Digest;

    public static string ComputeImportDigest(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return ComputeChunkedManifestDigest(filePath, cancellationToken);
        }

        return Sha256File(filePath, cancellationToken);
    }

    private static string ComputeChunkedManifestDigest(string manifestPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var chunkFiles = QqChunkManifest.ResolveChunkFiles(manifestPath, cancellationToken);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var manifestStream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = manifestStream.Read(buffer);
                    if (read == 0)
                    {
                        break;
                    }
                    hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFormatException(
                manifestPath,
                $"读取清单失败（{ex.Message}）",
                ex);
        }

        foreach (var chunkPath in chunkFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(manifestDir, chunkPath).Replace('\\', '/');
            try
            {
                var (chunkDigest, chunkSize) = HashFile(chunkPath, cancellationToken);
                var header = Encoding.UTF8.GetBytes(
                    $"\nchunk:{relPath}:{chunkSize}:{chunkDigest}\n");
                hash.AppendData(header);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ImportFormatException(
                    manifestPath,
                    $"读取声明分块失败（{relPath}：{ex.Message}）",
                    ex);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

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
        var destinationCreated = false;
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
                destinationCreated = true;
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

                    destination.Flush(flushToDisk: true);
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
            if (!completed && destinationCreated && File.Exists(destinationPath))
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
