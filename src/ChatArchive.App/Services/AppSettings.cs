using System.Text.Json;

namespace ChatArchive.App.Services;

/// <summary>exe 旁 settings.json：数据目录等配置。</summary>
public sealed class AppSettings
{
    public string DataDirectory { get; set; } = "E:\\ChatArchive";

    public static AppSettings Load()
    {
        try
        {
            var path = SettingsPath();
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
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
