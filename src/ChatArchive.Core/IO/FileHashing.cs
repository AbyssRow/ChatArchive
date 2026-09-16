using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using ChatArchive.Core.Importing;

namespace ChatArchive.Core.IO;

public static class FileHashing
{
    private const int BufferSize = 128 * 1024;

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
                BufferSize,
                FileOptions.SequentialScan);
            AppendStream(hash, manifestStream, destination: null, cancellationToken);
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
                hash.AppendData(Encoding.UTF8.GetBytes(
                    $"\nchunk:{relPath}:{chunkSize}:{chunkDigest}\n"));
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
            BufferSize,
            FileOptions.SequentialScan);
        return HashCopy(stream, destination: null, cancellationToken);
    }

    internal static (string Digest, long Size) CopyFileAndHash(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
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
            {
                destinationCreated = true;
                result = HashCopy(source, destination, cancellationToken);
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

    private static (string Digest, long Size) HashCopy(
        Stream source,
        FileStream? destination,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var size = AppendStream(hash, source, destination, cancellationToken);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }

    private static long AppendStream(
        IncrementalHash hash,
        Stream source,
        FileStream? destination,
        CancellationToken cancellationToken)
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

                destination?.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                size += read;
            }

            destination?.Flush(flushToDisk: true);
            return size;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
