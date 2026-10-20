# Left Right Audio Balancer

[![build](https://github.com/srnafi/left-right-audio-balancer/actions/workflows/ci.yml/badge.svg)](https://github.com/srnafi/left-right-audio-balancer/actions/workflows/ci.yml)

Balancing left and right audio on Windows 10 is a pain. The setting exists,
but it is buried several dialogs deep in Control Panel, behind tabs most
people never open. When one ear is louder than the other, fixing it means
finding that panel again, every time.

This app puts both channels on a small card you keep on the desktop: two
sliders, one per ear, plus a master that scales both without breaking the
balance you set.

![The card on the desktop](docs/screenshot.png)

It also fixes the reason per-ear control is often missing. Many Bluetooth
headsets take volume over AVRCP absolute volume, so Windows hands the level to
the headset and the endpoint reports a single channel. There is nothing to
split. The app can turn absolute volume off, which makes Windows own the level
locally and gives you the left and right channels back.

## Install

Download `LeftRightAudioBalancer.exe` from the
[latest release](https://github.com/srnafi/left-right-audio-balancer/releases/latest)
and run it. It is self-contained, so there is no runtime to install.

Until the first release is tagged, build it from source with the publish
command under [Build](#build).

The card opens in the bottom-right corner of the desktop. Right-click it to
quit.

## Using it

Pick your output device from the dropdown at the top. The sliders below show
that device's left and right volume.

**Linked** is on by default. Dragging either slider moves the other by the same
amount, so the offset between the ears stays put. The footer shows that offset:
`R -12` means the right ear is twelve points below the left. Turn Linked off to
move each ear independently. **Centre** sets both to their average.

**VOL** is the master. It never goes below the loudest ear, and moving it
rescales both channels by the same ratio, so your balance survives.

The pin in the header keeps the card above other windows. Without it, the card
sits at the back and comes forward when you show the desktop.

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

- **Start with Windows** adds a shortcut to your Startup folder.
- **Bluetooth per-ear fix** applies or reverts the registry value above.

Settings live in `%APPDATA%\LeftRightAudioBalancer\settings.json`.

## Limitations

- Per-ear volume depends on the driver. Some drivers accept the write and keep
  playing the same level on both sides. The app cannot detect that case,
  because Core Audio reports the value you wrote, not what the hardware does.
- Devices that report one channel cannot be split. That is what the Bluetooth
  fix is for.
- Windows only. It uses Core Audio through NAudio.
- The registry fix is machine-wide and needs admin. It also affects other
  Bluetooth audio devices on the same machine.

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

## Releasing

Two workflows run on GitHub Actions.

`ci.yml` runs on every push to `main` and on pull requests. It restores,
builds with warnings as errors, publishes the single-file exe, then runs that
exe and checks it can still resolve its own path. Warnings are errors only in
CI: the single-file publish breaks `Assembly.Location`, and that surfaced as a
warning rather than a failure, so the setting is there to catch the next one.

`release.yml` runs when you push a tag:

```
git tag v0.1.0
git push origin v0.1.0
```

It stamps the version from the tag, publishes, smoke tests the exe, writes a
SHA256 file, and creates a GitHub Release with both attached.

The exe is unsigned. Windows will warn on first run until it is signed with a
code-signing certificate.

## How it works

Per-ear volume is the per-channel volume of a render endpoint. Core Audio
exposes it through `IAudioEndpointVolume`; NAudio wraps that API.

Each slider's thumb is the same width as the knob it draws, with a transparent
overhang for the grab area. A wider thumb pushes the knob in from the track
ends and stops the fill at the thumb edge, which leaves a visible gap between
the line and the knob. Keep the two widths equal when restyling.

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
