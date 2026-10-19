# EarDial

A desktop widget for per-ear volume on Windows.

Windows can already set a device's left and right volume separately, but the
controls are buried in Control Panel. EarDial keeps a small card on the
desktop with a slider pair for the selected output device.

## Status

- [x] Enumerate render endpoints and read per-channel volume
- [x] Write per-channel volume, confirmed audible on a Bluetooth headset
- [x] Widget window with custom slider, switch and pill styling
- [x] Remember selected device and link state
- [x] Linked mode that preserves the L/R offset
- [x] Detect single-channel endpoints and explain them in place
- [x] Bluetooth absolute-volume fix, applied from the widget
- [x] Single-file release build
- [ ] Detect a driver that accepts the write and plays the same level anyway

## How it works

Per-ear volume is the per-channel volume of a render endpoint. Core Audio
exposes it through `IAudioEndpointVolume`, and NAudio wraps the same API.

### Linked

Linked holds the offset between the ears rather than their absolute values.
Dragging either slider moves the other by the same delta, so a balance you
dialed in survives the next volume change. The footer prints the offset it is
holding: `R -12` means the right ear sits twelve points below the left.
Unlink the ears to move them independently. Centre sets both to their average.

### Master

Master sets the ceiling for both ears and never reads below the loudest one.
Dragging it rescales both channels by the same ratio (`SetMasterScaled`), so
the balance does not move.

### Bluetooth headsets

Most Bluetooth headsets take volume over AVRCP absolute volume. Windows sends
a level and the headset applies it, so the endpoint reports a single channel
and per-ear writes are accepted but ignored.

The fix is the machine-wide `DisableAbsoluteVolume` value under
`HKLM\SYSTEM\CurrentControlSet\Control\Bluetooth\Audio\AVRCP\CT`, which makes
Windows own the level locally. It needs admin, so the write happens in an
elevated child process (`--set-absolute-volume 0|1`). The parent reads the key
back rather than trusting the exit code, then restarts `BTAGService` so no
unplug is needed.

When a single-channel endpoint is detected, a chip above the sliders says so.
On a Bluetooth endpoint the chip carries a one-click Fix; elsewhere it is a
plain explanation. The drawer has the same action plus Revert.

### Slider geometry

Each slider's thumb is exactly as wide as the knob it draws, with a
transparent overhang for the grab area. A wider thumb pushes the knob in from
the track ends and stops the fill at the thumb's edge instead of the dot's,
which shows up as a gap between the blue line and the knob. Keep the two
widths equal when restyling.

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
`--self-contained` gives a much smaller file but requires the .NET 10 desktop
runtime on the machine.

## Diagnostics

The app is a WinExe, so it has no console. Each flag writes its result to a
file instead. None of this ships in a release build.

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

`--selftest-ui` restores the device to its original levels when it finishes.

## Probes

Throwaway code, one question per project, excluded from the build.

```
dotnet run --project probes/EndpointChannels
```

## License

MIT
