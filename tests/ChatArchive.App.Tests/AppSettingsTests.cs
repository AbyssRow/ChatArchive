using ChatArchive.App.Services;
using ChatArchive.App.ViewModels;
using Microsoft.Data.Sqlite;
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

            var dbPath = Path.Combine(sourceDir, "chat_archive.db");
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                       new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                       {
                           DataSource = dbPath,
                           Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
                       }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "CREATE TABLE test (id INTEGER PRIMARY KEY, content TEXT); INSERT INTO test VALUES (1, 'hello');";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(Path.Combine(sourceDir, "chat_archive.db-wal"), "wal content");
            File.WriteAllText(Path.Combine(sourceDir, "chat_archive.db-shm"), "shm content");
            File.WriteAllText(Path.Combine(sourceDir, ".tmp_tempfile"), "tmp content");
            File.WriteAllText(Path.Combine(sourceDir, "media", "sub", "test.jpg"), "media content");

            AppSettings.CopyDataDirectory(sourceDir, targetDir, overwrite: true);

            Assert.True(File.Exists(Path.Combine(targetDir, "chat_archive.db")));
            Assert.False(File.Exists(Path.Combine(targetDir, "chat_archive.db-wal")));
            Assert.False(File.Exists(Path.Combine(targetDir, "chat_archive.db-shm")));
            Assert.False(File.Exists(Path.Combine(targetDir, ".tmp_tempfile")));

            using (var destConn = new Microsoft.Data.Sqlite.SqliteConnection(
                       new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                       {
                           DataSource = Path.Combine(targetDir, "chat_archive.db"),
                           Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                       }.ToString()))
            {
                destConn.Open();
                using var cmd = destConn.CreateCommand();
                cmd.CommandText = "SELECT content FROM test WHERE id = 1";
                Assert.Equal("hello", (string)cmd.ExecuteScalar()!);
            }

            Assert.True(File.Exists(Path.Combine(targetDir, "media", "sub", "test.jpg")));
            Assert.Equal("media content", File.ReadAllText(Path.Combine(targetDir, "media", "sub", "test.jpg")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(sourceDir)) try { Directory.Delete(sourceDir, true); } catch { }
            if (Directory.Exists(targetDir)) try { Directory.Delete(targetDir, true); } catch { }
        }
    }

    [Fact]
    public void CopyDataDirectory_ThrowsWhenTargetIsSubDirectoryOfSource()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"chatarchive_sub_test_{Guid.NewGuid():N}");
        var targetDir = Path.Combine(sourceDir, "backup");
        try
        {
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "test.txt"), "hello");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                AppSettings.CopyDataDirectory(sourceDir, targetDir));
            Assert.Contains("子目录", ex.Message);
        }
        finally
        {
            if (Directory.Exists(sourceDir)) try { Directory.Delete(sourceDir, true); } catch { }
        }
    }

    [Fact]
    public void CopyDataDirectory_ThrowsWhenTargetIsSameAsSource()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"chatarchive_same_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "test.txt"), "hello");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                AppSettings.CopyDataDirectory(sourceDir, sourceDir));
            Assert.Contains("相同", ex.Message);
        }
        finally
        {
            if (Directory.Exists(sourceDir)) try { Directory.Delete(sourceDir, true); } catch { }
        }
    }

    [Fact]
    public void Settings_SaveAndLoad_UsesSettingsPathOverride()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"chatarchive_settings_test_{Guid.NewGuid():N}");
        var tempFile = Path.Combine(tempDir, "settings.json");
        AppSettings.SettingsPathOverride = tempFile;

        try
        {
            var settings = new AppSettings { DataDirectory = @"D:\MyChatData" };
            settings.Save();

            Assert.True(File.Exists(tempFile));
            var loaded = AppSettings.Load();
            Assert.Equal(@"D:\MyChatData", loaded.DataDirectory);
        }
        finally
        {
            AppSettings.SettingsPathOverride = null;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void CopyDataDirectory_OverwriteTrue_ReplacesExistingDatabase()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"chatarchive_ow_src_{Guid.NewGuid():N}");
        var targetDir = Path.Combine(Path.GetTempPath(), $"chatarchive_ow_dst_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(targetDir);
            CreateTestDatabase(Path.Combine(sourceDir, "chat_archive.db"), "source-bytes");
            CreateTestDatabase(Path.Combine(targetDir, "chat_archive.db"), "dest-old-bytes");

            AppSettings.CopyDataDirectory(sourceDir, targetDir, overwrite: true);

            Assert.Equal("source-bytes", ReadTestDatabaseContent(Path.Combine(targetDir, "chat_archive.db")));

            var pageSource = ReadSettingsPageSource();
            Assert.Contains("CopyDataDirectory(currentDir, targetPath, overwrite: true)", pageSource);
            Assert.DoesNotContain("CopyDataDirectory(currentDir, targetPath, overwrite: false)", pageSource);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(sourceDir)) try { Directory.Delete(sourceDir, true); } catch { }
            if (Directory.Exists(targetDir)) try { Directory.Delete(targetDir, true); } catch { }
        }
    }

    [Fact]
    public void CopyDataDirectory_OverwriteFalse_LeavesExistingDatabase()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"chatarchive_skip_src_{Guid.NewGuid():N}");
        var targetDir = Path.Combine(Path.GetTempPath(), $"chatarchive_skip_dst_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(targetDir);
            CreateTestDatabase(Path.Combine(sourceDir, "chat_archive.db"), "source-bytes");
            CreateTestDatabase(Path.Combine(targetDir, "chat_archive.db"), "dest-old-bytes");

            AppSettings.CopyDataDirectory(sourceDir, targetDir, overwrite: false);

            Assert.Equal("dest-old-bytes", ReadTestDatabaseContent(Path.Combine(targetDir, "chat_archive.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(sourceDir)) try { Directory.Delete(sourceDir, true); } catch { }
            if (Directory.Exists(targetDir)) try { Directory.Delete(targetDir, true); } catch { }
        }
    }

    [Fact]
    public void StorageRefresh_IgnoresStaleGeneration()
    {
        var gate = new LatestRequestGate();
        var first = gate.Next();
        var second = gate.Next();

        string? applied = null;
        if (gate.IsCurrent(first))
        {
            applied = "old-dir-sizes";
        }

        if (gate.IsCurrent(second))
        {
            applied = "new-dir-sizes";
        }

        Assert.Equal("new-dir-sizes", applied);
        Assert.False(gate.IsCurrent(first));

        var pageSource = ReadSettingsPageSource();
        Assert.Contains("var generation = _refreshGate.Next();", pageSource);
        Assert.Contains("if (!_refreshGate.IsCurrent(generation))", pageSource);
    }

    private static void CreateTestDatabase(string path, string content)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE test (id INTEGER PRIMARY KEY, content TEXT); INSERT INTO test VALUES (1, @content);";
        cmd.Parameters.AddWithValue("@content", content);
        cmd.ExecuteNonQuery();
    }

    private static string ReadTestDatabaseContent(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT content FROM test WHERE id = 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private static string ReadSettingsPageSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ChatArchive.App", "Views", "SettingsPage.xaml.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("SettingsPage.xaml.cs");
    }
}
