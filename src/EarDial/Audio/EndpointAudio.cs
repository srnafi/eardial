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
    // Bluetooth endpoints forward volume over AVRCP. A per-channel write can
    // leave the headset at hardware zero while Windows still reports 100%, so
    // every write is followed by a master nudge that re-sends the command.
    // ponytail: nudge on drag-end only, not per slider tick.
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

            // A master change rescales channel levels on most endpoints, so the
            // balance has to be put back after the nudge re-sends the command.
            for (int i = 0; i < channels; i++)
                volume.Channels[i].VolumeLevelScalar = levels[i];
        }
        catch
        {
            // Device vanished mid-drag. Reload catches it on the next tick.
        }
    }
}