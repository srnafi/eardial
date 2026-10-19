using NAudio.CoreAudioApi;

namespace EarDial.Audio;

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

    public static void SetChannel(string id, int index, float value)
    {
        var volume = Open(id).AudioEndpointVolume;
        if (index >= volume.Channels.Count) return;

        volume.Channels[index].VolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    public static void SetMaster(string id, float value)
    {
        var volume = Open(id).AudioEndpointVolume;
        volume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    // Setting master volume alone leaves the channel levels where they were.
    // Scaling them by the same ratio keeps the left/right relationship intact
    // when the master is dragged down or up.
    public static void SetMasterScaled(string id, float value)
    {
        var volume = Open(id).AudioEndpointVolume;
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

    public static void SetMute(string id, bool muted)
    {
        Open(id).AudioEndpointVolume.Mute = muted;
    }

    public static EndpointState? Reload(string id)
    {
        try
        {
            return Read(Open(id));
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
            var volume = Open(id).AudioEndpointVolume;
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