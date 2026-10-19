// Lists render endpoints, their channel counts, and whether a channel write is
// accepted.
//
// Run: dotnet run --project probes/EndpointChannels
using NAudio.CoreAudioApi;

Console.WriteLine($"Windows {Environment.OSVersion.Version}");
Console.WriteLine();

using var enumerator = new MMDeviceEnumerator();

if (args.Contains("listen"))
{
    ListenTest(enumerator);
    return;
}

if (args.Contains("status"))
{
    Status(enumerator);
    return;
}

if (args.Contains("meter"))
{
    Meter(enumerator);
    return;
}

if (args.Contains("resync"))
{
    Resync(enumerator);
    return;
}

if (args.Contains("set"))
{
    SetBalance(enumerator, args);
    return;
}

if (args.Contains("desktop"))
{
    DesktopScan();
    return;
}

var devices = enumerator
    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
    .ToList();

Console.WriteLine($"Active render endpoints: {devices.Count}");
Console.WriteLine();

foreach (var device in devices)
{
    var volume = device.AudioEndpointVolume;
    int channels = volume.Channels.Count;

    Console.WriteLine(device.FriendlyName);
    Console.WriteLine($"  id       {device.ID}");
    Console.WriteLine($"  channels {channels}");
    Console.WriteLine($"  master   {volume.MasterVolumeLevelScalar:P0}  mute {volume.Mute}");

    if (channels > 1)
    {
        var levels = Enumerable
            .Range(0, channels)
            .Select(i => $"{i}:{volume.Channels[i].VolumeLevelScalar:P0}");
        Console.WriteLine($"  per-chan {string.Join("  ", levels)}");
    }
    else
    {
        Console.WriteLine("  per-chan none, single channel");
    }

    Console.WriteLine();
}

Console.WriteLine("write test");
foreach (var device in devices)
{
    var volume = device.AudioEndpointVolume;
    int channels = volume.Channels.Count;

    if (channels < 2)
    {
        Console.WriteLine($"{device.FriendlyName}: skipped, {channels} channel(s)");
        continue;
    }

    var before = Enumerable
        .Range(0, channels)
        .Select(i => volume.Channels[i].VolumeLevelScalar)
        .ToArray();

    try
    {
        volume.Channels[0].VolumeLevelScalar = 0.10f;
        volume.Channels[1].VolumeLevelScalar = 0.90f;

        float left = volume.Channels[0].VolumeLevelScalar;
        float right = volume.Channels[1].VolumeLevelScalar;

        bool accepted = Math.Abs(left - 0.10f) < 0.02f
                        && Math.Abs(right - 0.90f) < 0.02f;

        Console.WriteLine(
            $"{device.FriendlyName}: wrote 10/90, read back {left:P0}/{right:P0}, "
            + (accepted ? "accepted" : "ignored"));
    }
    finally
    {
        for (int i = 0; i < channels; i++)
            volume.Channels[i].VolumeLevelScalar = before[i];

        Console.WriteLine($"  restored {string.Join("/", before.Select(v => v.ToString("P0")))}");
    }
}

// Applies a hard left/right split on the default device and restores the
// previous levels afterwards.
static void ListenTest(MMDeviceEnumerator enumerator)
{
    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var volume = device.AudioEndpointVolume;
    int channels = volume.Channels.Count;

    Console.WriteLine($"target: {device.FriendlyName}");
    Console.WriteLine($"channels: {channels}");

    if (channels < 2)
    {
        Console.WriteLine("single channel, nothing to split");
        return;
    }

    var before = Enumerable
        .Range(0, channels)
        .Select(i => volume.Channels[i].VolumeLevelScalar)
        .ToArray();

    Console.WriteLine("start music now");
    Console.WriteLine("  8s  right only");
    Console.WriteLine("  8s  left only");
    Console.WriteLine("  8s  normal");
    Console.WriteLine();

    try
    {
        Thread.Sleep(8000);
        Phase(volume, 0f, 1f, "right only");
        Phase(volume, 1f, 0f, "left only");
        Phase(volume, 1f, 1f, "normal");
    }
    finally
    {
        for (int i = 0; i < channels; i++)
            volume.Channels[i].VolumeLevelScalar = before[i];

        Console.WriteLine($"restored {string.Join("/", before.Select(v => v.ToString("P0")))}");

        // Per-channel writes on a Bluetooth endpoint push AVRCP volume to the
        // headset. Nudge master so the device cannot be left at hardware zero.
        float master = volume.MasterVolumeLevelScalar;
        volume.MasterVolumeLevelScalar = Math.Max(0.05f, master - 0.25f);
        Thread.Sleep(400);
        volume.MasterVolumeLevelScalar = master;
        Thread.Sleep(200);

        for (int i = 0; i < channels; i++)
            volume.Channels[i].VolumeLevelScalar = before[i];

        Console.WriteLine($"master re-sent at {master:P0}");
    }
}

static void Phase(AudioEndpointVolume volume, float left, float right, string label)
{
    volume.Channels[0].VolumeLevelScalar = left;
    volume.Channels[1].VolumeLevelScalar = right;

    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {label}");
    Console.Out.Flush();

    Thread.Sleep(8000);
}

// Prints the volume state of every endpoint and session.
static void Status(MMDeviceEnumerator enumerator)
{
    foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
    {
        try
        {
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            Console.WriteLine($"default {role,-15} {device.FriendlyName}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"default {role,-15} none ({e.GetType().Name})");
        }
    }

    Console.WriteLine();

    var devices = enumerator
        .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
        .ToList();

    foreach (var device in devices)
    {
        var volume = device.AudioEndpointVolume;
        int channels = volume.Channels.Count;

        string perChannel = channels > 1
            ? string.Join("/", Enumerable.Range(0, channels)
                .Select(i => volume.Channels[i].VolumeLevelScalar.ToString("P0")))
            : "n/a";

        Console.WriteLine(device.FriendlyName);
        Console.WriteLine($"  master {volume.MasterVolumeLevelScalar:P0}  mute {volume.Mute}  per-chan {perChannel}");

        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var simple = session.SimpleAudioVolume;
                Console.WriteLine(
                    $"  session {session.DisplayName,-42} "
                    + $"vol {simple.Volume:P0} mute {simple.Mute} state {session.State}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  sessions unavailable ({e.GetType().Name})");
        }

        Console.WriteLine();
    }
}

// Samples the endpoint peak meter for ten seconds.
static void Meter(MMDeviceEnumerator enumerator)
{
    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var meter = device.AudioMeterInformation;

    Console.WriteLine($"metering {device.FriendlyName} for 10s");

    float peak = 0f;
    for (int i = 0; i < 50; i++)
    {
        float value = meter.MasterPeakValue;
        if (value > peak) peak = value;
        Console.WriteLine($"  {DateTime.Now:ss.fff}  {value:F3}");
        Console.Out.Flush();
        Thread.Sleep(200);
    }

    Console.WriteLine();
    Console.WriteLine(peak > 0.001f ? $"peak seen {peak:F3}, signal is reaching the endpoint" : "peak 0.000, nothing rendered to this endpoint");
}

// Re-sends master volume and mute, then restores the channel levels.
static void Resync(MMDeviceEnumerator enumerator)
{
    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var volume = device.AudioEndpointVolume;
    int channels = volume.Channels.Count;

    var before = Enumerable
        .Range(0, channels)
        .Select(i => volume.Channels[i].VolumeLevelScalar)
        .ToArray();

    Console.WriteLine($"target {device.FriendlyName}");
    Console.WriteLine($"before master {volume.MasterVolumeLevelScalar:P0} mute {volume.Mute}");

    volume.Mute = true;
    Thread.Sleep(400);
    volume.Mute = false;
    Console.WriteLine("mute toggled");

    volume.MasterVolumeLevelScalar = 0.40f;
    Thread.Sleep(400);
    volume.MasterVolumeLevelScalar = 1.00f;
    Thread.Sleep(400);
    Console.WriteLine("volume 40% then 100%, command re-sent");

    for (int i = 0; i < channels; i++)
        volume.Channels[i].VolumeLevelScalar = before[i];

    Console.WriteLine($"after  master {volume.MasterVolumeLevelScalar:P0} mute {volume.Mute} "
        + $"per-chan {string.Join("/", before.Select(v => v.ToString("P0")))}");
    Console.WriteLine("play something and check");
}

// Applies a left/right split to the default endpoint.
static void SetBalance(MMDeviceEnumerator enumerator, string[] args)
{
    int at = Array.IndexOf(args, "set");
    if (at + 2 >= args.Length || !float.TryParse(args[at + 1], out float left) || !float.TryParse(args[at + 2], out float right))
    {
        Console.WriteLine("usage: set <leftPercent> <rightPercent>");
        return;
    }

    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var volume = device.AudioEndpointVolume;

    Console.WriteLine($"target {device.FriendlyName} channels {volume.Channels.Count}");
    if (volume.Channels.Count < 2)
    {
        Console.WriteLine("single channel, cannot set a split");
        return;
    }

    volume.Channels[0].VolumeLevelScalar = Math.Clamp(left / 100f, 0f, 1f);
    volume.Channels[1].VolumeLevelScalar = Math.Clamp(right / 100f, 0f, 1f);

    Console.WriteLine($"set {volume.Channels[0].VolumeLevelScalar:P0} / {volume.Channels[1].VolumeLevelScalar:P0}");
}

// Enumerates windows the widget's DesktopInView check would treat as a visible app.
static void DesktopScan()
{
    foreach (System.Collections.Generic.KeyValuePair<string, string> _ in new[]
             {
                 new System.Collections.Generic.KeyValuePair<string, string>("a", "b")
             })
    {
        break;
    }

    var list = new System.Collections.Generic.List<string>();
    var sb = new System.Text.StringBuilder(128);

    Native.EnumWindows((h, _) =>
    {
        if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
        if (Native.DwmGetWindowAttribute(h, 13, out int cloaked, 4) == 0 && cloaked != 0)
        {
            list.Add($"CLOAKED {Native.Name(h, sb)}");
            return true;
        }
        if (Native.GetWindow(h, 4) != IntPtr.Zero)
        {
            list.Add($"OWNED  {Native.Name(h, sb)}");
            return true;
        }
        if (Native.GetWindowTextLength(h) == 0)
        {
            list.Add($"NOTITLE {Native.Name(h, sb)}");
            return true;
        }

        int style = Native.GetWindowLong(h, -20);
        if ((style & 0x80) != 0 && (style & 0x40000) == 0)
        {
            list.Add($"TOOLWIN {Native.Name(h, sb)}");
            return true;
        }

        string cls = Native.Name(h, sb);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            list.Add($"SHELL   {cls}");
            return true;
        }

        list.Add($"APP!!   {cls}  title={Native.Title(h)}");
        return true;
    }, IntPtr.Zero);

    Console.WriteLine("windows the widget treats as covering the desktop:");
    foreach (string line in list) Console.WriteLine("  " + line);
}

static class Native
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr h, int cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr h, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    public static string Name(IntPtr h, System.Text.StringBuilder sb)
    {
        sb.Clear();
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Title(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(128);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
