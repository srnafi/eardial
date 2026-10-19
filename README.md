# EarDial

A small desktop widget that puts per-ear volume control one click away.

Windows can already set the left and right volume of a sound device
separately, but the setting is buried in Control Panel. EarDial keeps a
widget on the desktop with a slider pair for each output device, so
balancing headphones takes a second instead of a minute.

## Status

Early. The audio layer is being proven against real hardware before any
UI is written.

- [x] Enumerate render endpoints and read per-channel volume
- [x] Write per-channel volume, confirmed audible on a Bluetooth headset
- [ ] Detect devices that accept the write and ignore it
- [x] Widget window
- [x] Pin widget to the desktop layer
- [x] Remember selected device
- [x] Single-file release build

## How it works

Per-ear volume is the per-channel volume of a render endpoint, exposed by
Core Audio through `IAudioEndpointVolume`. NAudio wraps the same API.

A device that reports one channel has nothing to split, so it is shown as
unsupported rather than given a slider that does nothing.

Not every driver honours the write. Some accept the value and keep playing
the same level on both sides. EarDial detects this and says so.

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

## Probes

Throwaway code that answers one question each. Not shipped.

```
dotnet run --project probes/001-device-channels
```

## License

MIT