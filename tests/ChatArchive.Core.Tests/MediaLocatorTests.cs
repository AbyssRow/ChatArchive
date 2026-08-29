using ChatArchive.Core.Media;
using Xunit;

namespace ChatArchive.Core.Tests;

public class MediaLocatorTests : IDisposable
{
    private readonly string _dir;

    public MediaLocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"chatarchive-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Resolves_by_sha256_layout_first()
    {
        var sha = new string('a', 64);
        var sub = Path.Combine(_dir, sha[..2]);
        Directory.CreateDirectory(sub);
        var managed = Path.Combine(sub, sha + ".jpg");
        File.WriteAllText(managed, "x");

        var locator = new MediaLocator(_dir);
        Assert.Equal(managed, locator.Resolve(sha));
        Assert.Equal(
            managed,
            locator.Resolve(sha, managedPath: @"E:\backup\old\other.jpg"));
    }

    [Fact]
    public void Falls_back_to_managed_then_source()
    {
        var locator = new MediaLocator(_dir);
        var sourceFile = Path.Combine(_dir, "src.bin");
        File.WriteAllText(sourceFile, "x");

        Assert.Null(locator.Resolve(new string('b', 64)));
        Assert.Null(locator.Resolve(null));

        var managedFile = Path.Combine(_dir, "managed.png");
        File.WriteAllText(managedFile, "x");
        Assert.Equal(managedFile, locator.Resolve("missing", managedFile, sourceFile));
        Assert.Equal(sourceFile, locator.Resolve("missing", null, sourceFile));
    }

    [Fact]
    public void Weird_suffix_skips_derivation()
    {
        var sha = new string('c', 64);
        Assert.Null(new MediaLocator(_dir).Resolve(sha, managedPath: @"E:\x\y.we!rd"));
    }

    [Fact]
    public void SingleDot_suffix_skips_derivation()
    {
        var sha = new string('d', 64);
        Assert.Null(new MediaLocator(_dir).Resolve(sha, managedPath: @"E:\x\y."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("xyz!@#")]
    [InlineData("../malicious")]
    public void Invalid_sha256_returns_null_or_fallback(string invalidSha)
    {
        var locator = new MediaLocator(_dir);
        Assert.Null(locator.Resolve(invalidSha));

        var fallbackFile = Path.Combine(_dir, "fallback.jpg");
        File.WriteAllText(fallbackFile, "fb");
        Assert.Equal(fallbackFile, locator.Resolve(invalidSha, sourcePath: fallbackFile));
    }

    [Fact]
    public void SafeResolveMedia_DoesNotLeakArbitraryParentFiles()
    {
        var exportRoot = Path.Combine(_dir, "session_export");
        Directory.CreateDirectory(exportRoot);
        var parentSecretFile = Path.Combine(_dir, "secret.png");
        File.WriteAllText(parentSecretFile, "secret");

        var resolved = ChatArchive.Core.Importing.ImportText.SafeResolveMedia(exportRoot, "secret.png");
        Assert.Equal(Path.Combine(exportRoot, "secret.png"), resolved);
        Assert.NotEqual(parentSecretFile, resolved);
    }

    [Fact]
    public void SafeResolveMedia_AllowsOnlyExistingWeFlowLayoutAFile()
    {
        var texts = Path.Combine(_dir, "export", "texts");
        var images = Path.Combine(_dir, "export", "images");
        Directory.CreateDirectory(texts);
        Directory.CreateDirectory(images);
        var image = Path.Combine(images, "one.jpg");
        File.WriteAllText(image, "image");

        Assert.Equal(image, ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../images/one.jpg"));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../images/missing.jpg"));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../private.txt"));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../images/../../private.txt"));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../../images/one.jpg"));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../image/one.jpg"));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../images"));
    }

    [Theory]
    [InlineData("images")]
    [InlineData("voices")]
    [InlineData("videos")]
    [InlineData("emojis")]
    [InlineData("file")]
    public void SafeResolveMedia_AllowsNestedFilesInEveryWeFlowLayoutACategory(string category)
    {
        var texts = Path.Combine(_dir, "export", "texts");
        var nested = Path.Combine(_dir, "export", category, "nested");
        Directory.CreateDirectory(texts);
        Directory.CreateDirectory(nested);
        var media = Path.Combine(nested, "one.bin");
        File.WriteAllText(media, category);

        Assert.Equal(media, ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, $"../{category}/nested/one.bin"));
    }

    [Fact]
    public void SafeResolveMedia_RejectsIntermediateDirectoryReparsePoints()
    {
        var texts = Path.Combine(_dir, "export", "texts");
        var outside = Path.Combine(_dir, "outside");
        var linkedImages = Path.Combine(_dir, "export", "images");
        Directory.CreateDirectory(texts);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "one.jpg"), "outside image");

        try
        {
            Directory.CreateSymbolicLink(linkedImages, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Directory symbolic links are unavailable on this platform: {ex.GetType().Name}");
            return;
        }

        Assert.True(File.GetAttributes(linkedImages).HasFlag(FileAttributes.ReparsePoint));
        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(texts, "../images/one.jpg"));
    }

    [Theory]
    [InlineData("/image1.png")]
    [InlineData("\\image1.png")]
    [InlineData("\\\\server\\share\\image1.png")]
    [InlineData("C:\\outside\\image1.png")]
    [InlineData("https://example.test/image1.png")]
    [InlineData("file:///outside/image1.png")]
    public void SafeResolveMedia_RejectsRootedAndUriLikeDeclarations(string declaredPath)
    {
        var exportRoot = Path.Combine(_dir, "export");
        Directory.CreateDirectory(exportRoot);
        File.WriteAllText(Path.Combine(exportRoot, "image1.png"), "image");

        Assert.Null(ChatArchive.Core.Importing.ImportText.SafeResolveMedia(exportRoot, declaredPath));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("sub/../../outside.png")]
    [InlineData("a/b/../../../secret.txt")]
    [InlineData("..\\outside.png")]
    public void SafeResolveMedia_RejectsPathTraversalWithParentSegments(string declaredPath)
    {
        var exportRoot = Path.Combine(_dir, "session_export_traversal");
        Directory.CreateDirectory(exportRoot);
        var outsideFile = Path.Combine(_dir, "outside.png");
        File.WriteAllText(outsideFile, "outside content");

        var resolved = ChatArchive.Core.Importing.ImportText.SafeResolveMedia(exportRoot, declaredPath);
        Assert.Null(resolved);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
