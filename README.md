# SolScan

SolScan is a .NET 10 WPF/MVVM application for spectroheliograph (SHG) solar imaging — driving an
ASCOM mount and a ZWO ASI camera through a **Prepare → Capture → Process** workflow: find the sun,
slew slightly ahead of it, auto-detect the disk crossing the slit to start/stop recording, and run
the SHG reconstruction pipeline (SER video → per-wavelength solar disk images) in the background,
in the same app.

It combines ideas from two prior systems: a Sunscan-style automated capture/near-live-processing
loop, and JSolex's mature offline SHG reconstruction pipeline — see `CLAUDE.md` for the detailed
architecture and phased build plan.

## Status

Early scaffolding. The Prepare/Capture/Process navigation shell runs; none of the actual hardware
or processing logic is wired up yet.

## Building

```
dotnet build SolScan.slnx
dotnet run --project SolScan.App/SolScan.App.csproj
```

Platform is x64 (matches the ZWO ASI camera SDK's native binaries). Windows only (WPF).

## Architecture

- **SolScan.Core** — domain models & interfaces only: `ITelescopeMount`, `ICameraDevice`,
  `ISerWriter`. No hardware/IO dependencies.
- **SolScan.Infrastructure** — concrete implementations: ASCOM (Alpaca) mount client, ZWO ASI
  camera wrapper, SER file writer/reader.
- **SolScan.Processing** — pure algorithms: the SHG reconstruction pipeline. Starts as a wrapper
  around JSolex's `jsolex-cli`, incrementally replaced with native ports.
- **SolScan.App** — the WPF MVVM shell.
- **SolScan.Simulators** — fake mount/camera implementations for development without hardware.
- **SolScan.Tests** — xUnit tests (unlike some sibling projects, this one starts real from day one).

## License

GNU AGPL 3.0 — see [LICENSE.md](LICENSE.md). Chosen for compatibility with the projects this one
ports code and ideas from: RASTA (AGPL-3.0), sunscan-backend/sunscan-app (GPL-3.0), and astro4j/
JSolex (Apache-2.0) — AGPL-3.0 is the one license compatible with combining all three. See
[NOTICE](NOTICE) for attribution details.

## Author

Phil Crompton
