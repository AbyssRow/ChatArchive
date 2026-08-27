using System.Security.Cryptography;
using System.Text;
using ChatArchive.Core.IO;
using Xunit;

namespace ChatArchive.Core.Tests;

public sealed class AvatarStorageServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _avatarDir;
    private readonly AvatarStorageService _service;

    public AvatarStorageServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"chatarchive-avatar-tests-{Guid.NewGuid():N}");
        _avatarDir = Path.Combine(_testDir, "avatars");
        _service = new AvatarStorageService(_avatarDir);
    }

    [Fact]
    public void Constructor_CreatesAvatarDirectory_IfNotExist()
    {
        var newDir = Path.Combine(_testDir, "sub_avatars");
        Assert.False(Directory.Exists(newDir));

        var service = new AvatarStorageService(newDir);

        Assert.True(Directory.Exists(newDir));
        Assert.Equal(Path.GetFullPath(newDir), Path.GetFullPath(service.AvatarDirectory));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenDirectoryIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new AvatarStorageService(null!));
    }

    [Fact]
    public void SaveAvatarFromStream_SavesFileWithSha256AndNormalizedExtension()
    {
        var data = Encoding.UTF8.GetBytes("sample avatar image content");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        using var stream = new MemoryStream(data);

        var savedPath = _service.SaveAvatarFromStream(stream, "PNG");

        Assert.Equal($"{expectedSha256}.png", savedPath);

        var fullPath = Path.Combine(_avatarDir, savedPath);
        Assert.True(File.Exists(fullPath));
        Assert.Equal(data, File.ReadAllBytes(fullPath));
    }

    [Fact]
    public void SaveAvatarFromStream_HandlesExtensionWithOrWithoutDot()
    {
        var data = Encoding.UTF8.GetBytes("avatar content with dot");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        using (var stream = new MemoryStream(data))
        {
            var result1 = _service.SaveAvatarFromStream(stream, ".JPG");
            Assert.Equal($"{expectedSha256}.jpg", result1);
        }

        using (var stream = new MemoryStream(data))
        {
            var result2 = _service.SaveAvatarFromStream(stream, "jpg");
            Assert.Equal($"{expectedSha256}.jpg", result2);
        }
    }

    [Fact]
    public void SaveAvatarFromStream_DeduplicatesIdenticalContent()
    {
        var data = Encoding.UTF8.GetBytes("duplicate stream avatar content");
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        using var stream1 = new MemoryStream(data);
        var result1 = _service.SaveAvatarFromStream(stream1, ".png");

        var fullPath = Path.Combine(_avatarDir, result1);

        // Save again with same content
        using var stream2 = new MemoryStream(data);
        var result2 = _service.SaveAvatarFromStream(stream2, ".png");

        Assert.Equal(result1, result2);
        Assert.Equal($"{expectedSha256}.png", result2);
        Assert.Single(Directory.GetFiles(_avatarDir));
    }

    [Fact]
    public void SaveAvatarFromStream_ThrowsWhenStreamOrExtensionInvalid()
    {
        Assert.Throws<ArgumentNullException>(() => _service.SaveAvatarFromStream(null!, ".png"));
        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Throws<ArgumentException>(() => _service.SaveAvatarFromStream(stream, ""));
        Assert.Throws<ArgumentException>(() => _service.SaveAvatarFromStream(stream, "   "));
        Assert.Throws<ArgumentException>(() => _service.SaveAvatarFromStream(stream, "../malicious.ext"));
        Assert.Throws<ArgumentException>(() => _service.SaveAvatarFromStream(stream, ".png;exe"));
        Assert.Throws<ArgumentException>(() => _service.SaveAvatarFromStream(stream, ".verylongextensionexceedinglimit"));
        Assert.Throws<ArgumentException>(() => _service.SaveAvatarFromStream(stream, ".!@#"));
    }

    [Fact]
    public void ResolveAvatarFullPath_Prevents_Sandbox_Escape()
    {
        var outsideFile = Path.Combine(_testDir, "outside.png");
        File.WriteAllBytes(outsideFile, [1, 2, 3]);

        Assert.Null(_service.ResolveAvatarFullPath("../outside.png"));
        Assert.Null(_service.ResolveAvatarFullPath("../../outside.png"));
    }

    [Fact]
    public void SaveAvatarFromFile_SavesFileWithSha256AndPreservesExtension()
    {
        var sourceFile = Path.Combine(_testDir, "test_source.WEBP");
        var data = Encoding.UTF8.GetBytes("source file avatar content");
        File.WriteAllBytes(sourceFile, data);
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        var savedPath = _service.SaveAvatarFromFile(sourceFile);

        Assert.Equal($"{expectedSha256}.webp", savedPath);
        var fullPath = Path.Combine(_avatarDir, savedPath);
        Assert.True(File.Exists(fullPath));
        Assert.Equal(data, File.ReadAllBytes(fullPath));
    }

    [Fact]
    public void SaveAvatarFromFile_DeduplicatesIdenticalContent()
    {
        var data = Encoding.UTF8.GetBytes("identical file content");
        var fileA = Path.Combine(_testDir, "fileA.png");
        var fileB = Path.Combine(_testDir, "fileB.png");
        File.WriteAllBytes(fileA, data);
        File.WriteAllBytes(fileB, data);

        var resultA = _service.SaveAvatarFromFile(fileA);
        var resultB = _service.SaveAvatarFromFile(fileB);

        Assert.Equal(resultA, resultB);
        Assert.Single(Directory.GetFiles(_avatarDir));
    }

    [Fact]
    public void SaveAvatarFromFile_ThrowsWhenFileNotFound()
    {
        Assert.Throws<ArgumentNullException>(() => _service.SaveAvatarFromFile(null!));
        Assert.Throws<FileNotFoundException>(() => _service.SaveAvatarFromFile(Path.Combine(_testDir, "non_existent.png")));
    }

    [Fact]
    public void ResolveAvatarFullPath_ReturnsNull_ForNullOrWhitespace()
    {
        Assert.Null(_service.ResolveAvatarFullPath(null));
        Assert.Null(_service.ResolveAvatarFullPath(""));
        Assert.Null(_service.ResolveAvatarFullPath("   "));
    }

    [Fact]
    public void ResolveAvatarFullPath_ReturnsNull_WhenRelativeFileDoesNotExist()
    {
        Assert.Null(_service.ResolveAvatarFullPath("non_existent_avatar.png"));
        Assert.Null(_service.ResolveAvatarFullPath("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff.jpg"));
    }

    [Fact]
    public void ResolveAvatarFullPath_ResolvesExistingRelativePath()
    {
        var data = Encoding.UTF8.GetBytes("avatar to resolve");
        using var stream = new MemoryStream(data);
        var relativeName = _service.SaveAvatarFromStream(stream, ".png");

        var resolvedPath = _service.ResolveAvatarFullPath(relativeName);

        Assert.NotNull(resolvedPath);
        Assert.True(File.Exists(resolvedPath));
        Assert.Equal(Path.GetFullPath(Path.Combine(_avatarDir, relativeName)), Path.GetFullPath(resolvedPath!));
    }

    [Fact]
    public void ResolveAvatarFullPath_ResolvesExistingAbsolutePath_InsideAvatarDirectory()
    {
        var data = Encoding.UTF8.GetBytes("avatar in avatar dir");
        using var stream = new MemoryStream(data);
        var fileName = _service.SaveAvatarFromStream(stream, ".png");
        var internalFile = Path.Combine(_avatarDir, fileName);

        var resolvedPath = _service.ResolveAvatarFullPath(internalFile);

        Assert.NotNull(resolvedPath);
        Assert.Equal(Path.GetFullPath(internalFile), Path.GetFullPath(resolvedPath!));
    }

    [Fact]
    public void ResolveAvatarFullPath_ReturnsNull_ForAbsolutePath_OutsideAvatarDirectory()
    {
        var externalFile = Path.Combine(_testDir, "external_avatar.png");
        File.WriteAllBytes(externalFile, [10, 20, 30]);

        var resolvedPath = _service.ResolveAvatarFullPath(externalFile);

        Assert.Null(resolvedPath);
    }

    [Fact]
    public void ResolveAvatarFullPath_ReturnsNull_ForNonExistentAbsolutePath()
    {
        var nonExistentAbsolute = Path.Combine(_avatarDir, "does_not_exist_abs.png");
        Assert.False(File.Exists(nonExistentAbsolute));

        var resolvedPath = _service.ResolveAvatarFullPath(nonExistentAbsolute);

        Assert.Null(resolvedPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
