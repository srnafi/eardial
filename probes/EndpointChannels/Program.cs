// Answers one question: which active render endpoints expose more than one
// channel, and do they honour a write to it?
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

        // Holds a hard left/right split on the default device so it can be verified by
        // ear. Not a measurement: only the listener can tell whether the driver obeys.
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
