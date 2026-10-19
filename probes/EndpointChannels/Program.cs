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

            // Dump exactly what each endpoint and each audio session is set to. Used to
            // tell "no audio" apart from "audio going somewhere else" or "session muted".
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

                    // Samples the endpoint peak meter. Distinguishes "signal is reaching Windows but
                    // the device is silent" from "nothing is being rendered at all".
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

                        // Forces Windows to re-send master volume and mute to the device, which
                        // re-issues the AVRCP volume command a Bluetooth headset may have missed.
                        // Per-channel levels are saved and put back afterwards.
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

                            // set <leftPercent> <rightPercent> on the default endpoint.
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
