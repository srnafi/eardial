using System.IO;
using System.Text.Json;

namespace EarDial;

internal sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
}

internal static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EarDial",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string? folder = Path.GetDirectoryName(FilePath);
            if (folder is not null) Directory.CreateDirectory(folder);

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are optional; the widget runs on defaults when this fails.
        }
    }
}