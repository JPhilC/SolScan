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

Past scaffolding now — mount control and manual camera capture both work end-to-end against real
hardware; the SHG reconstruction pipeline itself hasn't started. See `CLAUDE.md`'s phased build plan
for the full detail on what's real vs. placeholder.

- **Prepare** — connect/disconnect a mount over ASCOM Alpaca, Park/Unpark, a manual tracking toggle,
  live RA/Dec, and editable site settings that reconcile against the mount's own on connect. Also
  picks which SHG+telescope combination ("Equipment Setup") is in use for the session, plus a
  pop-out Hand Control window (modelled on GSServer's, but driven over Alpaca's own `MoveAxis`) for
  manually jogging the mount while watching the live preview on Capture.
- **Capture** — camera discovery (ZWO ASI via its native SDK, Altair, or a hardware-free simulator),
  a SharpCap-style live preview (gain/exposure/USB-bandwidth/contrast sliders, a histogram, ROI,
  zoom, colour space/binning), and manual start/stop recording to standard `.ser` files. Connecting a
  camera auto-registers it in the equipment library (pixel size queried straight from the hardware);
  every recording gets a `.equipment.json` sidecar snapshotting the SHG/telescope/camera used, for
  the processing stage to read back later.
- **Options** — a full equipment library (SHGs, telescopes, cameras, and saved SHG+telescope
  "Setups"), plus general settings (capture save location, ASCOM Alpaca connection, site location).
- **Process** — not started yet; still a placeholder view.

There's no solar ephemeris ("find the sun") or automated acquisition pipeline yet, so slewing/
recording are both entirely manual for now - that's the next piece of work.

## Building

```
dotnet build SolScan.slnx
dotnet run --project SolScan.App/SolScan.App.csproj
```

Platform is x64 (matches the ZWO ASI camera SDK's native binaries). Windows only (WPF).

Real camera hardware needs its vendor SDK DLL dropped in manually - see
`SolScan.Infrastructure/ASICamera2.README.md`/`altaircam.README.md`. Mount control needs an ASCOM
Alpaca-compatible endpoint reachable (typically the ASCOM Remote Server in front of any ASCOM driver,
real or simulated) - point Options > General's Alpaca settings at it.

## Architecture

- **SolScan.Core** — domain models & interfaces only, no hardware/IO dependencies: `ITelescopeMount`,
  `ICameraDevice`, `ISerWriter`, and the equipment library records (`SpectrographProfile`/
  `TelescopeProfile`/`CameraProfile`/`EquipmentSetup`) plus per-recording equipment metadata.
- **SolScan.Infrastructure** — concrete implementations: an ASCOM Alpaca mount client, ZWO ASI and
  Altair camera wrappers (native SDK P/Invoke), a `.ser` file writer, and JSON-file-backed equipment/
  settings storage.
- **SolScan.Processing** — pure algorithms: the SHG reconstruction pipeline. Not started yet - will
  begin as a wrapper around JSolex's `jsolex-cli`, incrementally replaced with native ports.
- **SolScan.App** — the WPF MVVM shell (Prepare/Capture/Process/Options).
- **SolScan.Simulators** — a hardware-free camera implementation for development without real
  hardware.
- **SolScan.Tests** — xUnit tests (unlike some sibling projects, this one starts real from day one).

## License

GNU AGPL 3.0 — see [LICENSE.md](LICENSE.md). Chosen for compatibility with the projects this one
ports code and ideas from: RASTA (AGPL-3.0), sunscan-backend/sunscan-app (GPL-3.0), and astro4j/
JSolex (Apache-2.0) — AGPL-3.0 is the one license compatible with combining all three. See
[NOTICE](NOTICE) for attribution details.

## Author

Phil Crompton
