using ChatArchive.App.Services;
using Xunit;

namespace ChatArchive.App.Tests;

public class AppSettingsTests
{
    [Fact]
    public void GetValidDataDirectory_ReturnsDefaultWhenNullOrWhitespace()
    {
        var settings = new AppSettings { DataDirectory = "" };
        Assert.Equal(AppSettings.DefaultDataDirectory, settings.GetValidDataDirectory());

        settings.DataDirectory = "   ";
        Assert.Equal(AppSettings.DefaultDataDirectory, settings.GetValidDataDirectory());
    }

    [Fact]
    public void GetValidDataDirectory_ReturnsValidPathWhenDriveExists()
    {
        var tempDir = Path.GetTempPath();
        var settings = new AppSettings { DataDirectory = tempDir };
        Assert.Equal(Path.GetFullPath(tempDir), settings.GetValidDataDirectory());
    }

    [Fact]
    public void GetStorageUsage_CalculatesSizesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"chatarchive_storage_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var mediaDir = Path.Combine(tempDir, "media");
            var avatarDir = Path.Combine(tempDir, "avatars");
            Directory.CreateDirectory(mediaDir);
            Directory.CreateDirectory(avatarDir);

            // DB file: 100 bytes
            File.WriteAllBytes(Path.Combine(tempDir, "chat_archive.db"), new byte[100]);
            // Media file: 200 bytes
            File.WriteAllBytes(Path.Combine(mediaDir, "sample.jpg"), new byte[200]);
            // Avatar file: 300 bytes
            File.WriteAllBytes(Path.Combine(avatarDir, "avatar.png"), new byte[300]);

            var usage = AppSettings.GetStorageUsage(tempDir);
            Assert.Equal(100L, usage.DatabaseBytes);
            Assert.Equal(200L, usage.MediaBytes);
            Assert.Equal(300L, usage.AvatarBytes);
            Assert.Equal(600L, usage.TotalBytes);
            Assert.Equal("600 B", usage.FormattedTotalSize);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void CopyDataDirectory_CopiesFilesAndSubdirectories()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"chatarchive_src_{Guid.NewGuid():N}");
        var targetDir = Path.Combine(Path.GetTempPath(), $"chatarchive_dst_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(sourceDir, "media", "sub"));
            File.WriteAllText(Path.Combine(sourceDir, "chat_archive.db"), "database content");
            File.WriteAllText(Path.Combine(sourceDir, "media", "sub", "test.jpg"), "media content");

            AppSettings.CopyDataDirectory(sourceDir, targetDir, overwrite: true);

            Assert.True(File.Exists(Path.Combine(targetDir, "chat_archive.db")));
            Assert.Equal("database content", File.ReadAllText(Path.Combine(targetDir, "chat_archive.db")));
            Assert.True(File.Exists(Path.Combine(targetDir, "media", "sub", "test.jpg")));
            Assert.Equal("media content", File.ReadAllText(Path.Combine(targetDir, "media", "sub", "test.jpg")));
        }
        finally
        {
            if (Directory.Exists(sourceDir)) try { Directory.Delete(sourceDir, true); } catch { }
            if (Directory.Exists(targetDir)) try { Directory.Delete(targetDir, true); } catch { }
        }
    }
}
