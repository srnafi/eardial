# Left Right Audio Balancer

[![build](https://github.com/srnafi/left-right-audio-balancer/actions/workflows/ci.yml/badge.svg)](https://github.com/srnafi/left-right-audio-balancer/actions/workflows/ci.yml)

Balancing left and right audio on Windows 10 is a pain. The setting exists,
but it is buried several dialogs deep in Control Panel, behind tabs most
people never open. When one ear is louder than the other, fixing it means
finding that panel again, every time.

This app puts both channels on a small card you keep on the desktop: two
sliders, one per ear, plus a master that scales both without breaking the
balance you set.

<img src="docs/screenshot.png" width="340" alt="The card on the desktop">

It also fixes the reason per-ear control is often missing. Many Bluetooth
headsets take volume over AVRCP absolute volume, so Windows hands the level to
the headset and the endpoint reports a single channel. There is nothing to
split. The app can turn absolute volume off, which makes Windows own the level
locally and gives you the left and right channels back.

## Install

Download `LeftRightAudioBalancer.exe` from the
[latest release](https://github.com/srnafi/left-right-audio-balancer/releases/latest)
and run it. It is self-contained, so there is no runtime to install.

The card opens in the bottom-right corner of the desktop the first time. Once
you drag it somewhere, it comes back there on the next start. Right-click it to
quit, or use **Quit** in the settings drawer.

## Using it

Pick your output device from the dropdown at the top. It shows what kind of
device it is under the name, and swaps in a Bluetooth glyph when the endpoint
is wireless. The sliders below show that device's left and right volume.

**Linked** is on by default. Dragging either slider moves the other by the same
amount, so the offset between the ears stays put. The footer shows that offset:
`R -12` means the right ear is twelve points below the left. Turn Linked off to
move each ear independently. **Centre** sets both to their average.

**VOL** is the master. It never goes below the loudest ear, and moving it
rescales both channels by the same ratio, so your balance survives.

Each ear slider carries a live level meter: the bright bar inside the fill
tracks what is actually coming out of that channel, so you can tell whether a
quiet side is the setting or the audio.

The **padlock** in the header locks the card in place. While it is locked the
card cannot be dragged, but every control still works, so clicking a slider
cannot move the whole window by accident. The lock survives a restart.

By default the card sits at the back of the desktop, out of the way of real
windows, and comes forward when you show the desktop.

## Bluetooth headsets

If a headset shows only one channel, the card says so above the sliders. On a
Bluetooth device the message carries a **Fix** button.

The fix writes `DisableAbsoluteVolume` under
`HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT`. It needs
administrator rights, so Windows will ask for permission. After it applies,
reconnect the headset or let the app restart the Bluetooth audio service.

This is a machine-wide setting. Revert it from the settings drawer if you would
rather let the headset control its own volume.

## Settings

Click the sliders icon in the header.

<img src="docs/settings.png" width="340" alt="The settings drawer">

- **Start with Windows** adds a shortcut to your Startup folder.
- **Bluetooth per-ear fix** applies or reverts the registry value above.
- **Quit** closes the app.

Settings live in `%APPDATA%\LeftRightAudioBalancer\settings.json`. That file
holds the selected device, the lock and linked states, the start-with-Windows
flag, and the last card position.

## Build

Requires the .NET 10 SDK.

```
dotnet build
dotnet run --project src/LeftRightAudioBalancer
```

To produce a single self-contained executable:

```
dotnet publish src/LeftRightAudioBalancer -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish
```

That writes `publish/LeftRightAudioBalancer.exe` at roughly 73 MB. Drop
`--self-contained` for a much smaller file, but the machine then needs the
.NET 10 desktop runtime.


## How it works

Per-ear volume is the per-channel volume of a render endpoint. Core Audio
exposes it through `IAudioEndpointVolume`; NAudio wraps that API.

Each slider's thumb is the same width as the knob it draws, with a transparent
overhang for the grab area. A wider thumb pushes the knob in from the track
ends and stops the fill at the thumb edge, which leaves a visible gap between
the line and the knob. Keep the two widths equal when restyling. The hover
halo is a separate ellipse that grows through a negative margin, so it never
changes the thumb's layout box.

The level meter is polled every 50 ms. It jumps straight to a new peak and
falls back by a fixed ratio on each tick, which reads as a decay rather than a
flicker.

## Development

The app is a WinExe, so it has no console. These flags write their result to a
file instead. They are compiled into every build, including release.

| Flag | Writes |
| --- | --- |
| `--dump <path>` | Window state, endpoint list, element geometry, visual tree |
| `--shot <path>` | PNG of the window at 2x |
| `--selftest <path>` | Master-scale check against the default endpoint |
| `--selftest-ui <path>` | Linked, unlinked, centre and master behaviour against the selected endpoint |
| `--drawer` | Opens the settings drawer before the above |
| `--chip` | Forces the warning chip before the above |
| `--opencombo` | Opens the device list before the above |
| `--select <n>` | Selects a device by list index before the above |
| `--ear <n> <value>` | Pins an ear slider for a capture without writing to the endpoint |
| `--shortcut-check <path>` | Creates the startup shortcut, reports what it points at, removes it |

`--selftest-ui` restores the device to its original levels when it finishes.

`probes/` holds throwaway code, one question per project, excluded from the
build.

```
dotnet run --project probes/EndpointChannels
```

## License

MIT
