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
- [ ] Write per-channel volume and detect devices that ignore it
- [ ] Widget window
- [ ] Pin widget to the desktop layer
- [ ] Remember settings per device
- [ ] Single-file release build

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

## Probes

Throwaway code that answers one question each. Not shipped.

```
dotnet run --project probes/001-device-channels
```

## License

MIT