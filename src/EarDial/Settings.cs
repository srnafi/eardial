using System.IO;
using System.Text.Json;

namespace EarDial;

internal sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool StartOnBoot { get; set; }

    // Linked keeps the L-R offset when either ear moves. On by default: most
    // people want the balance they dialed in to survive the next volume change.
    public bool Linked { get; set; } = true;
}

internal static class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EarDial",
        "settings.json");

    // Startup-folder shortcut: visible in Task Manager's Startup apps, no
    // registry surgery. Name must match the shortcut so the toggle can find
    // and delete it.
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "EarDial.lnk");

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

    public static void SetStartOnBoot(bool enabled)
    {
        if (enabled) CreateShortcut();
        else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }

    private static void CreateShortcut()
    {
        string? exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(exe)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);

        var type = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"))!;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            var link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exe;
            link.WorkingDirectory = AppContext.BaseDirectory;
            link.Description = "EarDial per-ear volume widget";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}