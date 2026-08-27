using System.Text.Json;

namespace ChatArchive.App.Services;

/// <summary>exe 旁 settings.json：数据目录等配置。</summary>
public sealed class AppSettings
{
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatArchive");

    public string DataDirectory { get; set; } = DefaultDataDirectory;

    public string GetValidDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
        {
            try
            {
                var full = Path.GetFullPath(DataDirectory);
                var root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    return full;
                }
            }
            catch
            {
            }
        }

        return DefaultDataDirectory;
    }

    public static StorageUsageInfo GetStorageUsage(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir))
        {
            return new StorageUsageInfo(0, 0, 0, 0);
        }

        long dbBytes = 0;
        var dbPath = Path.Combine(dataDir, "chat_archive.db");
        if (File.Exists(dbPath))
        {
            try { dbBytes += new FileInfo(dbPath).Length; } catch { }
            var walPath = dbPath + "-wal";
            if (File.Exists(walPath))
            {
                try { dbBytes += new FileInfo(walPath).Length; } catch { }
            }
        }

        long mediaBytes = GetDirectorySize(Path.Combine(dataDir, "media"));
        long avatarBytes = GetDirectorySize(Path.Combine(dataDir, "avatars"));

        return new StorageUsageInfo(dbBytes, mediaBytes, avatarBytes, dbBytes + mediaBytes + avatarBytes);
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try { return new FileInfo(file).Length; }
                    catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }

    public static void CopyDataDirectory(string sourceDir, string targetDir, bool overwrite = false)
    {
        if (!Directory.Exists(sourceDir)) return;
        Directory.CreateDirectory(targetDir);

        foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dirPath);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, filePath);
            var destPath = Path.Combine(targetDir, relative);
            if (overwrite || !File.Exists(destPath))
            {
                File.Copy(filePath, destPath, overwrite);
            }
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(settings.DataDirectory))
                {
                    settings.DataDirectory = DefaultDataDirectory;
                }

                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(this, options));
    }

    private static string SettingsPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "settings.json");
    }
}

public sealed record StorageUsageInfo(
    long DatabaseBytes,
    long MediaBytes,
    long AvatarBytes,
    long TotalBytes)
{
    public string FormattedDatabaseSize => FormatBytes(DatabaseBytes);
    public string FormattedMediaSize => FormatBytes(MediaBytes);
    public string FormattedAvatarSize => FormatBytes(AvatarBytes);
    public string FormattedTotalSize => FormatBytes(TotalBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
