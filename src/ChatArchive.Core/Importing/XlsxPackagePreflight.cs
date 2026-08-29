using System.IO.Compression;
using System.Xml;

namespace ChatArchive.Core.Importing;

internal sealed class XlsxPackageHandle : IDisposable
{
    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly IReadOnlyDictionary<string, string> _defaults;
    private bool _disposed;

    internal XlsxPackageHandle(
        FileStream stream,
        IReadOnlySet<string> entryPaths,
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> defaults)
    {
        Stream = stream;
        EntryPaths = entryPaths;
        _overrides = overrides;
        _defaults = defaults;
    }

    internal FileStream Stream { get; }
    internal IReadOnlySet<string> EntryPaths { get; }

    internal string? GetContentType(string entryPath)
    {
        if (_overrides.TryGetValue(entryPath, out var contentType))
        {
            return contentType;
        }

        var separator = entryPath.LastIndexOf('/');
        var dot = entryPath.LastIndexOf('.');
        if (dot <= separator || dot == entryPath.Length - 1)
        {
            return null;
        }

        return _defaults.TryGetValue(entryPath[(dot + 1)..], out contentType)
            ? contentType
            : null;
    }

    internal IReadOnlyDictionary<string, ZipArchiveEntry> OpenValidatedEntryMap(out ZipArchive archive)
    {
        archive = new ZipArchive(Stream, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            return archive.Entries
                .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal))
                .ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stream.Dispose();
    }
}

internal static class XlsxPackagePreflight
{
    private const string ContentTypesEntry = "[Content_Types].xml";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    internal static XlsxPackageHandle OpenValidated(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportFormatException(filePath, "XLSX 导入只接受 .xlsx 文件");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            IReadOnlySet<string> entries;
            PackageContentTypes contentTypes;
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                entries = IndexEntries(archive, filePath);
                contentTypes = ReadContentTypes(archive, entries, filePath);
            }

            stream.Position = 0;
            var result = new XlsxPackageHandle(
                stream, entries, contentTypes.Overrides, contentTypes.Defaults);
            stream = null;
            return result;
        }
        catch (ImportFormatException)
        {
            stream?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or XmlException
            or ArgumentException
            or NotSupportedException)
        {
            stream?.Dispose();
            throw new ImportFormatException(filePath, $"XLSX 包预检失败（{ex.Message}）");
        }
    }

    internal static bool IsForbiddenPayloadPath(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..];
        return fileName.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("vbaProjectSignature.bin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> IndexEntries(ZipArchive archive, string filePath)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            CancellationToken.None.ThrowIfCancellationRequested();
            var path = entry.FullName;
            ValidateEntryPath(path, filePath);
            var identity = path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
            if (IsForbiddenPayloadPath(identity))
            {
                throw new ImportFormatException(
                    filePath,
                    $"XLSX 包含禁止的宏或二进制负载条目：{identity}");
            }

            if (!entryNames.Add(identity))
            {
                throw new ImportFormatException(filePath, $"XLSX 包含重复或歧义条目：{path}");
            }

            if (!path.EndsWith("/", StringComparison.Ordinal))
            {
                entries.Add(path);
            }
        }

        return entries;
    }

    private static void ValidateEntryPath(string path, string filePath)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] == '/'
            || path.Contains('\\')
            || Path.IsPathRooted(path)
            || IsDrivePath(path)
            || Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            throw new ImportFormatException(filePath, $"XLSX 包含非法条目路径：{path}");
        }

        var isDirectory = path.EndsWith("/", StringComparison.Ordinal);
        var segments = path.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (isDirectory && index == segments.Length - 1 && segment.Length == 0)
            {
                continue;
            }

            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ImportFormatException(filePath, $"XLSX 包含非法条目路径：{path}");
            }
        }
    }

    private static PackageContentTypes ReadContentTypes(
        ZipArchive archive,
        IReadOnlySet<string> entryPaths,
        string filePath)
    {
        return WithEntryFormatErrors(filePath, ContentTypesEntry, () =>
        {
            if (!entryPaths.Contains(ContentTypesEntry) || archive.GetEntry(ContentTypesEntry) is not { } entry)
            {
                throw Error(filePath, ContentTypesEntry, $"找不到 XLSX 条目 {ContentTypesEntry}");
            }

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sawTypes = false;
            using var stream = entry.Open();
            using var reader = CreateXmlReader(stream);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                {
                    if (reader.LocalName != "Types" || reader.NamespaceURI != ContentTypesNamespace)
                    {
                        throw Error(filePath, ContentTypesEntry, "Types 根元素无效");
                    }

                    sawTypes = true;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != ContentTypesNamespace)
                {
                    continue;
                }

                CancellationToken.None.ThrowIfCancellationRequested();
                var contentType = reader.GetAttribute("ContentType");
                if (reader.Depth != 1 || reader.LocalName is not ("Default" or "Override"))
                {
                    throw Error(filePath, ContentTypesEntry, $"{reader.LocalName} 元素位置或类型无效");
                }

                if (string.IsNullOrWhiteSpace(contentType))
                {
                    throw Error(filePath, ContentTypesEntry, $"{reader.LocalName} 缺少 ContentType");
                }

                RejectForbiddenContentType(contentType, filePath);
                if (reader.LocalName == "Default")
                {
                    var extension = reader.GetAttribute("Extension");
                    if (string.IsNullOrWhiteSpace(extension)
                        || extension.Contains('/')
                        || extension.Contains('\\')
                        || extension[0] == '.')
                    {
                        throw Error(filePath, ContentTypesEntry, "Default 缺少有效 Extension");
                    }

                    if (!defaults.TryAdd(extension, contentType))
                    {
                        throw Error(filePath, ContentTypesEntry, $"Extension 重复或歧义：{extension}");
                    }

                    continue;
                }

                var declaredPart = reader.GetAttribute("PartName");
                if (string.IsNullOrWhiteSpace(declaredPart) || declaredPart[0] != '/')
                {
                    throw Error(filePath, ContentTypesEntry, "Override 缺少有效 PartName");
                }

                var partName = declaredPart[1..];
                try
                {
                    ValidateEntryPath(partName, filePath);
                }
                catch (ImportFormatException)
                {
                    throw Error(filePath, ContentTypesEntry, $"PartName 无效：{declaredPart}");
                }

                if (!overrides.TryAdd(partName, contentType))
                {
                    throw Error(filePath, ContentTypesEntry, $"PartName 重复或歧义：{declaredPart}");
                }
            }

            if (!sawTypes)
            {
                throw Error(filePath, ContentTypesEntry, "缺少 Types 根元素");
            }

            return new PackageContentTypes(overrides, defaults);
        });
    }

    private static void RejectForbiddenContentType(string contentType, string filePath)
    {
        if (contentType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("activeX", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("macrosheet", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("intlmacrosheet", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals(
                "application/vnd.openxmlformats-officedocument.oleObject",
                StringComparison.OrdinalIgnoreCase))
        {
            throw Error(filePath, ContentTypesEntry, $"禁止的宏或二进制 ContentType：{contentType}");
        }
    }

    private static XmlReader CreateXmlReader(Stream stream)
    {
        return XmlReader.Create(stream, new XmlReaderSettings
        {
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });
    }

    private static T WithEntryFormatErrors<T>(string filePath, string entryPath, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (ImportFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or XmlException
            or ArgumentException
            or NotSupportedException)
        {
            throw Error(filePath, entryPath, $"解析失败（{ex.Message}）");
        }
    }

    private static ImportFormatException Error(string filePath, string entryPath, string message)
    {
        return new ImportFormatException(filePath, $"XLSX 条目 {entryPath}：{message}");
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsDrivePath(string path) => path.Length >= 2 && IsAsciiLetter(path[0]) && path[1] == ':';

    private sealed record PackageContentTypes(
        IReadOnlyDictionary<string, string> Overrides,
        IReadOnlyDictionary<string, string> Defaults);
}
