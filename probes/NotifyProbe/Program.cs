// Probes whether AudioEndpointVolume.OnVolumeNotification fires when the
// endpoint's volume is changed externally. Answers: is push notification
// deliverable on this machine, or must the widget rely on polling only?
using NAudio.CoreAudioApi;

using var enumerator = new MMDeviceEnumerator();
var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
var volume = device.AudioEndpointVolume;

float master = volume.MasterVolumeLevelScalar;
float[] channels = Enumerable.Range(0, volume.Channels.Count)
    .Select(i => volume.Channels[i].VolumeLevelScalar)
    .ToArray();

int fired = 0;
volume.OnVolumeNotification += data => { fired++; Console.WriteLine($"  fired: vol={data.MasterVolume * 100:F0}% muted={data.Muted}"); };

Console.WriteLine($"hooking {device.FriendlyName}, changing volume externally now...");

volume.MasterVolumeLevelScalar = Math.Max(0.05f, master - 0.30f);
Thread.Sleep(600);
volume.MasterVolumeLevelScalar = master;
Thread.Sleep(600);

Console.WriteLine(fired > 0 ? $"PUSH WORKS: {fired} notifications received" : "PUSH DEAD: 0 notifications, polling is the only option");

for (int i = 0; i < volume.Channels.Count; i++)
    volume.Channels[i].VolumeLevelScalar = channels[i];
