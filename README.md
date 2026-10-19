# EarDial

A small desktop widget that puts per-ear volume control one click away.

Windows can already set the left and right volume of a sound device
separately, but the setting is buried in Control Panel. EarDial keeps a
widget on the desktop with a slider pair for each output device, so
balancing headphones takes a second instead of a minute.

## Status

- [x] Enumerate render endpoints and read per-channel volume
- [x] Write per-channel volume, confirmed audible on a Bluetooth headset
- [x] Widget window
- [x] Custom slider, switch and pill styling
- [x] Remember selected device and link state
- [x] Linked mode that preserves the L/R offset
- [x] Detect single-channel endpoints and explain them in place
- [x] Bluetooth absolute-volume fix, applied from the widget
- [x] Single-file release build
- [ ] Detect a driver that accepts the write and plays the same level anyway

## How it works

Per-ear volume is the per-channel volume of a render endpoint, exposed by
Core Audio through `IAudioEndpointVolume`. NAudio wraps the same API.

### Linked

Linked keeps the *offset* between the ears, not their absolute values.
Dragging either slider moves the other by the same delta, so a balance
dialed in for one pair of headphones survives every later volume change.
The footer prints the offset it is holding (`R -12` means the right ear is
twelve points below the left). Unlink the ears to move them independently;
Centre snaps both back to their average.

### Master

Master is the ceiling, not a third channel. It never reads below the
loudest ear. Dragging it rescales both channels by the same ratio
(`SetMasterScaled`), so the balance is untouched by a volume change.

### Bluetooth headsets

Most Bluetooth headsets take volume over AVRCP "absolute volume": Windows
sends a level and the headset applies it. The endpoint then reports a
single channel and per-ear writes are accepted but ignored, so the widget
has nothing to split.

The fix is the machine-wide `DisableAbsoluteVolume` value under
`HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT`, which
makes Windows own the level locally. It needs admin, so the write happens
in an elevated child process (`--set-absolute-volume 0|1`); the parent
confirms by reading the key back rather than trusting the exit code, and
bounces `BTAGService` so no unplug is needed.

When a single-channel endpoint is detected, the widget says so in a chip
above the sliders. On a Bluetooth endpoint the chip carries a one-click
Fix; elsewhere it is a plain explanation. The drawer has the same action
plus Revert.

## Requirements

- Windows 10 build 19041 or newer, or Windows 11
- .NET 10 SDK to build

## Build

```
dotnet build
dotnet run --project src/EarDial
```

## Release

Single self-contained executable, no runtime to install:

```
dotnet publish src/EarDial -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish
```

That produces `publish/EarDial.exe` at roughly 73 MB. Dropping
`--self-contained` gives a much smaller file but requires the .NET 10
desktop runtime on the machine.

## Diagnostics

The app is a WinExe, so it has no console. Each flag writes its result to a
file instead. Not for release builds.

| Flag | Writes |
| --- | --- |
| `--dump <path>` | Window state, endpoint list, element geometry, visual tree |
| `--shot <path>` | PNG of the window at 2x |
| `--selftest <path>` | Master-scale check against the default endpoint |
| `--selftest-ui <path>` | Linked, unlinked, centre and master behaviour against the selected endpoint |
| `--drawer` | Opens the settings drawer before the above |
| `--chip` | Forces the warning chip before the above |
| `--select <n>` | Selects a device by list index before the above |

`--selftest-ui` restores the device to its original levels when it finishes.

## Probes

Throwaway code that answers one question each. Not shipped.

```
dotnet run --project probes/EndpointChannels
```

## License

MIT
