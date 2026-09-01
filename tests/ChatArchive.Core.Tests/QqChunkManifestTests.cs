using ChatArchive.Core.Importing;
using ChatArchive.Core.IO;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class QqChunkManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"chatarchive-qq-manifest-{Guid.NewGuid():N}");

    public QqChunkManifestTests() => Directory.CreateDirectory(_root);

    private string WriteAt(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteManifest(string chunkedJson) => WriteAt(
        Path.Combine(_root, "manifest.json"),
        $"{{\"chunked\":{chunkedJson}}}");

    [Fact]
    public void ResolveExistingTargets_distinguishes_regular_files_and_directories()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "a.jsonl");
        File.WriteAllText(file, "{}\n");

        Assert.Equal(
            file,
            ImportText.ResolveExistingRegularFileUnderRoot(_root, "nested/a.jsonl"));
        Assert.Null(ImportText.ResolveExistingRegularFileUnderRoot(_root, "nested"));
        Assert.Equal(
            nested,
            ImportText.ResolveExistingDirectoryUnderRoot(_root, "nested"));
        Assert.Null(ImportText.ResolveExistingDirectoryUnderRoot(_root, "nested/a.jsonl"));
        Assert.Null(ImportText.ResolveExistingRegularFileUnderRoot(_root, "../outside.jsonl"));
    }

    [Fact]
    public void ImportFormatException_preserves_inner_exception()
    {
        var inner = new IOException("disk error");

        var error = new ImportFormatException("manifest.json", "读取失败", inner);

        Assert.Same(inner, error.InnerException);
        Assert.Equal("manifest.json", error.FilePath);
    }

    [Fact]
    public void ResolveChunkFiles_AuthoritativeManifest_ReturnsOnlyDeclaredFilesInDeclarationOrder()
    {
        var chunks = Directory.CreateDirectory(Path.Combine(_root, "chunks")).FullName;
        var b = WriteAt(Path.Combine(chunks, "b.jsonl"), "{\"id\":\"b\"}\n");
        var a = WriteAt(Path.Combine(chunks, "a.jsonl"), "{\"id\":\"a\"}\n");
        _ = WriteAt(Path.Combine(chunks, "old.jsonl"), "{}\n");
        _ = WriteAt(Path.Combine(_root, "old.jsonl"), "{}\n");
        var manifest = WriteManifest("""
            {"chunksDir":"chunks","chunks":[
              {"relativePath":"chunks/b.jsonl"},
              {"relativePath":"chunks/a.jsonl"}
            ]}
            """);

        Assert.Equal(new[] { b, a }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_AuthoritativeEmptyChunks_ReturnsEmpty()
    {
        _ = WriteAt(Path.Combine(_root, "sidecar.jsonl"), "{}\n");
        _ = WriteAt(Path.Combine(_root, "chunks", "sidecar.jsonl"), "{}\n");
        var manifest = WriteManifest("""{"chunks":[]}""");

        Assert.Empty(QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_FileNameFallback_UsesDefaultChunksDirectory()
    {
        var chunk = WriteAt(Path.Combine(_root, "chunks", "a.jsonl"), "{}\n");
        var manifest = WriteManifest("""{"chunks":[{"fileName":"a.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_FileNameFallback_UsesCustomChunksDirectory()
    {
        var chunk = WriteAt(Path.Combine(_root, "custom", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunksDir":"custom","chunks":[{"fileName":"a.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RelativePath_DoesNotRequireImplicitChunksDirectory()
    {
        var chunk = WriteAt(Path.Combine(_root, "data", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"data/a.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_ExplicitChunksDir_IsValidatedEvenForRelativePathEntries()
    {
        _ = WriteAt(Path.Combine(_root, "data", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunksDir":"missing","chunks":[{"relativePath":"data/a.jsonl"}]}""");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("chunksDir 不存在", error.Message);
    }

    [Fact]
    public void ResolveChunkFiles_ExplicitChunksDir_IsFullyValidatedForEmptyChunks()
    {
        var manifest = WriteManifest("""{"chunksDir":"missing","chunks":[]}""");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("chunksDir 不存在", error.Message);
    }

    [Fact]
    public void ResolveChunkFiles_RelativePath_TakesPrecedenceOverFileName()
    {
        var chunk = WriteAt(Path.Combine(_root, "data", "a.jsonl"), "{}\n");
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"data/a.jsonl","fileName":"invalid/path.jsonl"}]}""");

        Assert.Equal(new[] { chunk }, QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void ResolveChunkFiles_InvalidExplicitRelativePath_DoesNotFallBackToValidFileName(
        string relativePathJson)
    {
        _ = WriteAt(Path.Combine(_root, "chunks", "fallback.jsonl"), "{}\n");
        var manifest = WriteManifest(
            $"{{\"chunks\":[{{\"relativePath\":{relativePathJson},\"fileName\":\"fallback.jsonl\"}}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("relativePath", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"entry\"")]
    [InlineData("[]")]
    public void ResolveChunkFiles_RejectsNonObjectChunkEntry(string entryJson)
    {
        var manifest = WriteManifest($"{{\"chunks\":[{entryJson}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("chunks[0] 必须是对象", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveChunkFiles_RejectsObjectEntryMissingBothPathFields()
    {
        var manifest = WriteManifest("""{"chunks":[{}]}""");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("缺少 relativePath 或有效 fileName", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.test/chunks")]
    [InlineData("C:\\outside")]
    [InlineData("\\\\server\\share")]
    [InlineData("/outside")]
    [InlineData("chunks//nested")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("chunks/./nested")]
    [InlineData("chunks/../other")]
    public void ResolveChunkFiles_RejectsUnsafeChunksDir(string declared)
    {
        Directory.CreateDirectory(Path.Combine(_root, "chunks", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        var json = JsonSerializer.Serialize(declared);
        var manifest = WriteManifest($"{{\"chunksDir\":{json},\"chunks\":[]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("chunksDir", error.Message);
        if (declared is "chunks//nested" or "chunks/./nested" or "chunks/../other")
        {
            Assert.Contains("chunksDir 含空段或点路径段", error.Message);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\".\"")]
    [InlineData("\"..\"")]
    [InlineData("\"sub/a.jsonl\"")]
    [InlineData("\"sub\\\\a.jsonl\"")]
    [InlineData("\"a.json\"")]
    [InlineData("\"a.jsonl.tmp\"")]
    public void ResolveChunkFiles_RejectsInvalidFileNameFallback(string fileNameJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "chunks"));
        if (fileNameJson is "\"sub/a.jsonl\"" or "\"sub\\\\a.jsonl\"")
        {
            _ = WriteAt(Path.Combine(_root, "chunks", "sub", "a.jsonl"), "{}\n");
        }
        else if (fileNameJson == "\"a.json\"")
        {
            _ = WriteAt(Path.Combine(_root, "chunks", "a.json"), "{}\n");
        }
        else if (fileNameJson == "\"a.jsonl.tmp\"")
        {
            _ = WriteAt(Path.Combine(_root, "chunks", "a.jsonl.tmp"), "{}\n");
        }
        var manifest = WriteManifest($"{{\"chunks\":[{{\"fileName\":{fileNameJson}}}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("fileName", error.Message);
        if (fileNameJson is "null" or "123" or "\"\"" or "\"   \"")
        {
            Assert.Contains("缺少 relativePath 或有效 fileName", error.Message);
        }
        else
        {
            Assert.Contains("fileName 必须是 .jsonl basename", error.Message);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("\"https://example.test/a.jsonl\"")]
    [InlineData("\"C:\\\\outside\\\\a.jsonl\"")]
    [InlineData("\"\\\\\\\\server\\\\share\\\\a.jsonl\"")]
    [InlineData("\"/outside/a.jsonl\"")]
    [InlineData("\"../outside/a.jsonl\"")]
    [InlineData("\"chunks/a.json\"")]
    public void ResolveChunkFiles_RejectsInvalidRelativePath(string relativePathJson)
    {
        if (relativePathJson == "\"chunks/a.json\"")
        {
            _ = WriteAt(Path.Combine(_root, "chunks", "a.json"), "{}\n");
        }
        var manifest = WriteManifest($"{{\"chunks\":[{{\"relativePath\":{relativePathJson}}}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        if (relativePathJson is "null" or "123" or "\"\"")
        {
            Assert.Contains("relativePath 必须是非空字符串", error.Message);
        }
        else if (relativePathJson == "\"../outside/a.jsonl\"")
        {
            Assert.Contains("点路径段", error.Message);
        }
        else
        {
            Assert.Contains("路径必须是相对 .jsonl 文件", error.Message);
        }
    }

    [Theory]
    [InlineData("./a.jsonl")]
    [InlineData("chunks/../a.jsonl")]
    [InlineData(".\\a.jsonl")]
    [InlineData("chunks\\..\\a.jsonl")]
    public void ResolveChunkFiles_RejectsDotSegmentsEvenWhenTheyNormalizeInsideRoot(
        string declaredPath)
    {
        _ = WriteAt(Path.Combine(_root, "a.jsonl"), "{}\n");
        var json = JsonSerializer.Serialize(declaredPath);
        var manifest = WriteManifest($"{{\"chunks\":[{{\"relativePath\":{json}}}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("点路径段", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveChunkFiles_PathNormalizationFailure_PreservesOriginalInnerException()
    {
        var declaredPath = "chunks/invalid\0.jsonl";
        var json = JsonSerializer.Serialize(declaredPath);
        var manifest = WriteManifest($"{{\"chunks\":[{{\"relativePath\":{json}}}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
    }

    [Theory]
    [InlineData("sub/a.jsonl")]
    [InlineData("sub\\a.jsonl")]
    public void ResolveChunkFiles_RejectsNestedFileNameEvenWhenTargetExists(string declared)
    {
        _ = WriteAt(Path.Combine(_root, "chunks", "sub", "a.jsonl"), "{}\n");
        var json = JsonSerializer.Serialize(declared);
        var manifest = WriteManifest($"{{\"chunks\":[{{\"fileName\":{json}}}]}}");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("fileName 必须是 .jsonl basename", error.Message);
    }

    [Fact]
    public void ResolveChunkFiles_RejectsEscapingRelativePathEvenWhenTargetExists()
    {
        var exportRoot = Directory.CreateDirectory(Path.Combine(_root, "export")).FullName;
        _ = WriteAt(Path.Combine(_root, "outside", "a.jsonl"), "{}\n");
        var manifest = WriteAt(
            Path.Combine(exportRoot, "manifest.json"),
            """{"chunked":{"chunks":[{"relativePath":"../outside/a.jsonl"}]}}""");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("点路径段", error.Message);
    }

    [Fact]
    public void ResolveChunkFiles_RejectsWrongExtensionRelativePathEvenWhenTargetExists()
    {
        _ = WriteAt(Path.Combine(_root, "chunks", "a.json"), "{}\n");
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"chunks/a.json"}]}""");

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("路径必须是相对 .jsonl 文件", error.Message);
    }

    [Fact]
    public void ResolveChunkFiles_RejectsMissingDeclaredFile()
    {
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"chunks/missing.jsonl"}]}""");

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsDeclaredDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "chunks", "a.jsonl"));
        var manifest = WriteManifest(
            """{"chunks":[{"relativePath":"chunks/a.jsonl"}]}""");

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsDuplicateCanonicalPaths()
    {
        _ = WriteAt(Path.Combine(_root, "chunks", "a.jsonl"), "{}\n");
        var manifest = WriteManifest("""
            {"chunks":[
              {"relativePath":"chunks/a.jsonl"},
              {"relativePath":"chunks\\a.jsonl"}
            ]}
            """);

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsManifestFileReparsePoint()
    {
        var target = WriteAt(Path.Combine(_root, "targets", "manifest.json"),
            "{\"chunked\":{\"chunks\":[]}}");
        var exportRoot = Directory.CreateDirectory(Path.Combine(_root, "manifest-link")).FullName;
        var manifest = Path.Combine(exportRoot, "manifest.json");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(manifest, target));
        Assert.True(File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint));

        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Fact]
    public void QqChunkedExportFormat_Matches_RejectsManifestFileReparsePointBeforeSniffing()
    {
        var target = WriteAt(
            Path.Combine(_root, "matches-target", "manifest.json"),
            """
            {
              "metadata":{"name":"QQChatExporter"},
              "chatInfo":{"peerUid":"peer","name":"linked","type":"group"},
              "chunked":{"chunks":[]}
            }
            """);
        var exportRoot = Directory.CreateDirectory(Path.Combine(_root, "matches-link")).FullName;
        var manifest = Path.Combine(exportRoot, "manifest.json");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(manifest, target));
        Assert.True(File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint));

        var error = Assert.Throws<ImportFormatException>(
            () => new QqChunkedExportFormat().Matches(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("重解析点", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QqChunkedExportFormat_Matches_RejectsMalformedExplicitChunkedShape()
    {
        var manifest = WriteAt(
            Path.Combine(_root, "matches-malformed", "manifest.json"),
            """
            {
              "metadata":{"name":"QQChatExporter"},
              "chatInfo":{"peerUid":"peer","name":"broken","type":"group"},
              "chunked":null
            }
            """);

        var error = Assert.Throws<ImportFormatException>(
            () => new QqChunkedExportFormat().Matches(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("chunked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NtfsCaseSensitiveSiblingEscape_IsRejectedByResolverOpenAndDigest()
    {
        var parent = Directory.CreateDirectory(
            Path.Combine(_root, "case-sensitive-parent")).FullName;
        EnableNtfsCaseSensitivityOrSkip(parent);

        var exportRoot = Directory.CreateDirectory(Path.Combine(parent, "export")).FullName;
        var siblingRoot = Directory.CreateDirectory(Path.Combine(parent, "EXPORT")).FullName;
        if (string.Equals(exportRoot, siblingRoot, StringComparison.Ordinal)
            || Directory.GetDirectories(parent)
                .Select(Path.GetFileName)
                .Distinct(StringComparer.Ordinal)
                .Count(name => name is "export" or "EXPORT") != 2)
        {
            Assert.Skip("NTFS did not preserve distinct export/EXPORT sibling directories after enabling case sensitivity");
        }

        _ = WriteAt(
            Path.Combine(exportRoot, "shadow.jsonl"),
            "{\"id\":\"decoy\",\"timestamp\":1700000000,\"sender\":{\"uid\":\"peer\",\"name\":\"Decoy\"},\"content\":{\"type\":\"text\",\"text\":\"decoy\"}}\n");
        var escapedChunk = WriteAt(
            Path.Combine(siblingRoot, "shadow.jsonl"),
            "{\"id\":\"escaped\",\"timestamp\":1700000000,\"sender\":{\"uid\":\"peer\",\"name\":\"Outside\"},\"content\":{\"type\":\"text\",\"text\":\"outside\"}}\n");
        var manifest = WriteAt(
            Path.Combine(exportRoot, "manifest.json"),
            """
            {
              "metadata":{"name":"QQChatExporter","version":"0.2.0"},
              "chatInfo":{"selfUid":"self","peerUid":"group","name":"case escape","type":"group"},
              "chunked":{"chunks":[{"relativePath":"../EXPORT/shadow.jsonl"}]}
            }
            """);

        var directResolution = ImportText.ResolveExistingRegularFileUnderRoot(
            exportRoot,
            "../EXPORT/shadow.jsonl");
        IReadOnlyList<string>? resolved = null;
        var resolverError = Record.Exception(
            () => resolved = QqChunkManifest.ResolveChunkFiles(manifest));
        string? nativeId = null;
        var openError = Record.Exception(() =>
        {
            using var export = new QqChunkedExportFormat().Open(manifest);
            nativeId = Assert.Single(export.EnumerateMessages()).NativeId;
        });
        string? digest = null;
        var digestError = Record.Exception(
            () => digest = FileHashing.ComputeImportDigest(manifest));

        Assert.Null(directResolution);
        Assert.IsType<ImportFormatException>(resolverError);
        Assert.IsType<ImportFormatException>(openError);
        Assert.IsType<ImportFormatException>(digestError);
        Assert.Null(resolved);
        Assert.Null(nativeId);
        Assert.Null(digest);
        Assert.True(File.Exists(escapedChunk));
    }

    [Fact]
    public void ResolveChunkFiles_RejectsReparsePointAtEveryChunkPathComponent()
    {
        var directoryTarget = Directory.CreateDirectory(
            Path.Combine(_root, "targets", "component-directory")).FullName;
        _ = WriteAt(Path.Combine(directoryTarget, "a.jsonl"), "{}\n");
        var fileTarget = WriteAt(
            Path.Combine(_root, "targets", "component-file.jsonl"), "{}\n");

        var topExport = Directory.CreateDirectory(Path.Combine(_root, "top-component")).FullName;
        var topLink = Path.Combine(topExport, "chunks");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(topLink, directoryTarget));
        Assert.True(File.GetAttributes(topLink).HasFlag(FileAttributes.ReparsePoint));
        var topManifest = WriteAt(
            Path.Combine(topExport, "manifest.json"),
            "{\"chunked\":{\"chunks\":[{\"relativePath\":\"chunks/a.jsonl\"}]}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(topManifest));

        var nestedExport = Directory.CreateDirectory(Path.Combine(_root, "nested-component")).FullName;
        Directory.CreateDirectory(Path.Combine(nestedExport, "chunks"));
        var nestedLink = Path.Combine(nestedExport, "chunks", "nested");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(nestedLink, directoryTarget));
        Assert.True(File.GetAttributes(nestedLink).HasFlag(FileAttributes.ReparsePoint));
        var nestedManifest = WriteAt(
            Path.Combine(nestedExport, "manifest.json"),
            "{\"chunked\":{\"chunks\":[{\"relativePath\":\"chunks/nested/a.jsonl\"}]}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(nestedManifest));

        var fileExport = Directory.CreateDirectory(Path.Combine(_root, "file-component")).FullName;
        Directory.CreateDirectory(Path.Combine(fileExport, "chunks"));
        var fileLink = Path.Combine(fileExport, "chunks", "a.jsonl");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(fileLink, fileTarget));
        Assert.True(File.GetAttributes(fileLink).HasFlag(FileAttributes.ReparsePoint));
        var fileManifest = WriteAt(
            Path.Combine(fileExport, "manifest.json"),
            "{\"chunked\":{\"chunks\":[{\"relativePath\":\"chunks/a.jsonl\"}]}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(fileManifest));
    }

    [Fact]
    public void ResolveChunkFiles_LegacyModeRejectsReparseChunksDirectoryAndFiles()
    {
        var directoryTarget = Directory.CreateDirectory(
            Path.Combine(_root, "targets", "legacy-directory")).FullName;
        _ = WriteAt(Path.Combine(directoryTarget, "a.jsonl"), "{}\n");
        var fileTarget = WriteAt(
            Path.Combine(_root, "targets", "legacy-file.jsonl"), "{}\n");

        var directoryExport = Directory.CreateDirectory(
            Path.Combine(_root, "legacy-directory-export")).FullName;
        var chunksLink = Path.Combine(directoryExport, "chunks");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(chunksLink, directoryTarget));
        Assert.True(File.GetAttributes(chunksLink).HasFlag(FileAttributes.ReparsePoint));
        var directoryManifest = WriteAt(
            Path.Combine(directoryExport, "manifest.json"), "{\"chatInfo\":{}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(directoryManifest));

        var fileExport = Directory.CreateDirectory(
            Path.Combine(_root, "legacy-file-export")).FullName;
        Directory.CreateDirectory(Path.Combine(fileExport, "chunks"));
        var chunkLink = Path.Combine(fileExport, "chunks", "a.jsonl");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(chunkLink, fileTarget));
        Assert.True(File.GetAttributes(chunkLink).HasFlag(FileAttributes.ReparsePoint));
        var fileManifest = WriteAt(
            Path.Combine(fileExport, "manifest.json"), "{\"chatInfo\":{}}");
        Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(fileManifest));
    }

    [Fact]
    public void ResolveChunkFiles_LegacyManifest_ScansOnlyConventionalLocationsInNaturalOrder()
    {
        var chunk2 = WriteAt(Path.Combine(_root, "chunks", "chunk2.jsonl"), "{}\n");
        var chunk10 = WriteAt(Path.Combine(_root, "chunks", "chunk10.jsonl"), "{}\n");
        _ = WriteAt(Path.Combine(_root, "chunks", "nested", "chunk1.jsonl"), "{}\n");
        var manifest = WriteAt(Path.Combine(_root, "manifest.json"), "{\"chatInfo\":{}}");

        Assert.Equal(
            new[] { chunk2, chunk10 },
            QqChunkManifest.ResolveChunkFiles(manifest));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("0")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"chunkz\":[]}")]
    [InlineData("{\"chunks\":null}")]
    [InlineData("{\"chunks\":{}}")]
    public void ResolveChunkFiles_ExplicitChunkedShape_DoesNotFallBackToLegacyScan(
        string chunkedJson)
    {
        _ = WriteAt(Path.Combine(_root, "sidecar.jsonl"), "{}\n");
        var manifest = WriteManifest(chunkedJson);

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"scalar\"")]
    public void ResolveChunkFiles_InvalidJsonOrNonObjectRoot_ThrowsManifestScopedError(
        string json)
    {
        var manifest = WriteAt(Path.Combine(_root, "manifest.json"), json);

        var error = Assert.Throws<ImportFormatException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest));

        Assert.Equal(manifest, error.FilePath);
    }

    [Fact]
    public void ResolveChunkFiles_PropagatesCancellationWithoutWrapping()
    {
        var manifest = WriteAt(Path.Combine(_root, "manifest.json"), "{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => QqChunkManifest.ResolveChunkFiles(manifest, cancellation.Token));
    }

    [Fact]
    public void QqChunkedExportFormat_Open_RejectsUnsafeManifestBeforeParsingItsContents()
    {
        var target = WriteAt(Path.Combine(_root, "outside", "manifest.json"), "{");
        var manifest = Path.Combine(_root, "manifest.json");
        try
        {
            File.CreateSymbolicLink(manifest, target);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"File symbolic links are unavailable on this platform: {ex.GetType().Name}");
            return;
        }

        Assert.True(File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint));

        var error = Assert.Throws<ImportFormatException>(
            () => new QqChunkedExportFormat().Open(manifest));

        Assert.Equal(manifest, error.FilePath);
        Assert.Contains("重解析点", error.Message);
    }

    private static void CreateSymbolicLinkOrSkip(Action create)
    {
        try
        {
            create();
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                or NotSupportedException)
        {
            Assert.Skip("当前环境不允许创建符号链接");
        }
    }

    private static void EnableNtfsCaseSensitivityOrSkip(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("NTFS per-directory case sensitivity is only available on Windows");
        }

        string driveFormat;
        try
        {
            driveFormat = new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Unable to query the test volume format: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!string.Equals(driveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"The test temp directory is on {driveFormat}, not NTFS");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("fsutil.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("file");
            process.StartInfo.ArgumentList.Add("SetCaseSensitiveInfo");
            process.StartInfo.ArgumentList.Add(directory);
            process.StartInfo.ArgumentList.Add("enable");
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Assert.Skip(
                    $"fsutil could not enable NTFS case sensitivity (exit {process.ExitCode}): {error} {output}".Trim());
            }
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Unable to run fsutil for NTFS case sensitivity: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
