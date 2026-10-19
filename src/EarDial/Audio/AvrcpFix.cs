using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace EarDial.Audio;

// Bluetooth headsets take volume over AVRCP "absolute volume": Windows sends a
// level and the headset applies it, so the endpoint exposes a single channel
// and per-ear writes are accepted but ignored. Turning absolute volume off
// makes Windows own the level locally, which is what gives L and R back.
//
// The setting is a machine-wide registry value and needs admin. A WinExe cannot
// raise its own token after start, so the write happens in an elevated child
// process and the parent confirms by reading the key back.
internal static class AvrcpFix
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT";
    private const string ValueName = "DisableAbsoluteVolume";
    private const string ServiceName = "BTAGService";

    public static bool IsApplied
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is int value && value == 1;
            }
            catch
            {
                return false;
            }
        }
    }

    // Called from the elevated child only. Returns 0 on success.
    public static int Write(bool enable)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
            if (key is null) return 2;

            key.SetValue(ValueName, enable ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            return 3;
        }

        RestartGateway();
        return 0;
    }

    // Returns true only when the registry actually holds the requested value.
    // The child's exit code alone is not trusted: a UAC-approved launch that
    // fails to write still exits normally.
    public static bool Apply(bool enable)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;

        var start = new ProcessStartInfo(exe, enable ? "--set-absolute-volume 1" : "--set-absolute-volume 0")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            using var child = Process.Start(start);
            if (child is null) return false;
            child.WaitForExit(30000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223 = the user dismissed the UAC prompt.
            return false;
        }
        catch
        {
            return false;
        }

        return IsApplied == enable;
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    // A new AVRCP context is negotiated when the audio gateway restarts, so this
    // saves an unplug. Failure is not fatal: a reconnect does the same job.
    private static void RestartGateway()
    {
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"start {ServiceName}");
    }

    private static void Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(8000);
        }
        catch
        {
            // Service control is best-effort.
        }
    }
}
