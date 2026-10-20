using NAudio.CoreAudioApi;

namespace LeftRightAudioBalancer.Audio;

public sealed record EndpointState(
    string Id,
    string Name,
    float Master,
    bool Muted,
    float[] Levels)
{
    public int Channels => Levels.Length;
    public bool CanSplit => Levels.Length > 1;
}

public static class EndpointAudio
{
    // Amount the master is briefly lowered by to make a Bluetooth device accept
    // a fresh volume command.
    private const float NudgeStep = 0.25f;

    // Fires on the UI thread when this endpoint's volume or mute changes from
    // anywhere (system Settings, keyboard keys, other apps). Registered in
    // MainWindow.Bind for the selected device; the callback ID is checked by
    // the caller because the hook can outlive a rebind for a moment.
    public static event Action<string>? EndpointChanged;

    private static readonly Dictionary<string, MMDevice> _hooks = new();
    private static readonly object _hookLock = new();

    public static List<EndpointState> List()
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(Read)
            .OrderByDescending(state => state.CanSplit)
            .ThenBy(state => state.Name)
            .ToList();
    }

    // ID of the endpoint Windows routes sound to right now. Volume changed in
    // system Settings targets this device, so the widget follows it.
    public static string? DefaultId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            return null;
        }
    }

    // Hooks the device for volume/mute callbacks and returns a snapshot.
    // Callbacks arrive on a threadpool thread, so they marshal to the UI via
    // Dispatcher. A stale callback for a device no longer bound is dropped.
    public static EndpointState? Hook(string id, System.Windows.Threading.Dispatcher dispatcher)
    {
        lock (_hookLock)
        {
            if (!_hooks.TryGetValue(id, out var device))
            {
                device = new MMDeviceEnumerator().GetDevice(id);
                device.AudioEndpointVolume.OnVolumeNotification += data =>
                {
                    try
                    {
                        dispatcher.BeginInvoke(() => EndpointChanged?.Invoke(id));
                    }
                    catch
                    {
                        // Window closed or app shutting down; nothing to update.
                    }
                };
                _hooks[id] = device;
            }
            return Read(device);
        }
    }

    private static EndpointState Read(MMDevice device)
    {
        var volume = device.AudioEndpointVolume;
        int channels = volume.Channels.Count;

        return new EndpointState(
            device.ID,
            device.FriendlyName,
            volume.MasterVolumeLevelScalar,
            volume.Mute,
            Enumerable.Range(0, channels)
                .Select(i => volume.Channels[i].VolumeLevelScalar)
                .ToArray());
    }

    private static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }

    // Bluetooth endpoints report a single channel while absolute volume is on,
    // so the transport decides whether the fix in the drawer applies to them.
    private static readonly PropertyKey EnumeratorNameKey = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    public static bool IsBluetooth(string id)
    {
        try
        {
            using var device = Open(id);

            // PKEY_Device_EnumeratorName reports the bus, not the product name,
            // so a rename in Sound settings cannot break the check.
            if (device.Properties.TryGetValue<string>(EnumeratorNameKey, out var transport))
            {
                if (!string.IsNullOrEmpty(transport))
                {
                    return transport.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                        || transport.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                        || transport.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Property store unavailable; fall through to the name check.
        }

        try
        {
            using var device = Open(id);
            string name = device.FriendlyName;

            return name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Headset", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Buds", StringComparison.OrdinalIgnoreCase)
                || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetChannel(string id, int index, float value)
    {
        using var device = Open(id);
        var volume = device.AudioEndpointVolume;
        if (index >= volume.Channels.Count) return;

        volume.Channels[index].VolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    public static void SetMaster(string id, float value)
    {
        using var device = Open(id);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    // Setting master volume alone leaves the channel levels where they were.
    // Scaling them by the same ratio keeps the left/right relationship intact
    // when the master is dragged down or up.
    public static void SetMasterScaled(string id, float value)
    {
        using var device = Open(id);
        var volume = device.AudioEndpointVolume;
        int channels = volume.Channels.Count;

        float current = volume.MasterVolumeLevelScalar;
        float target = Math.Clamp(value, 0f, 1f);

        if (current <= 0.001f || Math.Abs(target - current) < 0.001f)
        {
            volume.MasterVolumeLevelScalar = target;
            return;
        }

        float[] levels = Enumerable.Range(0, channels)
            .Select(i => volume.Channels[i].VolumeLevelScalar)
            .ToArray();

        float ratio = target / current;

        volume.MasterVolumeLevelScalar = target;

        for (int i = 0; i < channels; i++)
            volume.Channels[i].VolumeLevelScalar = Math.Clamp(levels[i] * ratio, 0f, 1f);
    }

    public static EndpointState? Reload(string id)
    {
        try
        {
            using var device = Open(id);
            return Read(device);
        }
        catch
        {
            return null;
        }
    }

    public static void NudgeMaster(string id)
    {
        try
        {
            using var device = Open(id);
            var volume = device.AudioEndpointVolume;
            int channels = volume.Channels.Count;

            var levels = Enumerable.Range(0, channels)
                .Select(i => volume.Channels[i].VolumeLevelScalar)
                .ToArray();

            float master = volume.MasterVolumeLevelScalar;

            volume.MasterVolumeLevelScalar = Math.Max(0.05f, master - NudgeStep);
            Thread.Sleep(150);
            volume.MasterVolumeLevelScalar = master;

            // Master changes rescale channel levels, so the balance is restored
            // once the volume command has been sent.
            for (int i = 0; i < channels; i++)
                volume.Channels[i].VolumeLevelScalar = levels[i];
        }
        catch
        {
            // Device disappeared between drag and release.
        }
    }
}