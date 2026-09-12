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

Past scaffolding now — mount control, manual camera capture, and a first real slice of the SHG
reconstruction pipeline all work end-to-end against real hardware. See `CLAUDE.md`'s phased build
plan for the full detail on what's real vs. placeholder.

- **Prepare** — connect/disconnect a mount over ASCOM Alpaca, Park/Unpark, a manual tracking toggle,
  live RA/Dec, and editable site settings that reconcile against the mount's own on connect. Also
  picks which SHG+telescope combination ("Equipment Setup") is in use for the session, plus a
  pop-out Hand Control window (modelled on GSServer's, but driven over Alpaca's own `MoveAxis`) for
  manually jogging the mount while watching the live preview on Capture.
- **Capture** — camera discovery (ZWO ASI via its native SDK, Altair, or a hardware-free simulator),
  a SharpCap-style live preview (gain/exposure/USB-bandwidth/contrast sliders, a histogram, ROI,
  zoom, colour space/binning), and manual start/stop recording to standard `.ser` files. A "Find
  Sun…" button slews to today's computed solar position (a ported low-precision analytic ephemeris)
  and offers a camera-brightness hill-climb fine-tune plus an Alpaca pointing sync. Connecting a
  camera auto-registers it in the equipment library (pixel size queried straight from the hardware);
  every recording gets a `.equipment.json` sidecar snapshotting the SHG/telescope/camera used, the
  camera settings dialled in, and the mount's pointing at the time.
- **Options** — a full equipment library (SHGs, telescopes, cameras, and saved SHG+telescope
  "Setups"), general settings (capture save location, ASCOM Alpaca connection, site location), and
  process parameters (which line was studied, geometry/contrast choices, which output images to
  generate) feeding the Process stage below.
- **Process** — pick a `.ser` file and run it through a real (not simplified) SHG reconstruction
  pipeline: spectral-line-curvature detection and disk reconstruction, both ported from astro4j/
  JSol'Ex, producing real `Raw`/`Reconstruction`/`Continuum` output images viewable right in the app.
  Geometry-corrected/contrast-enhanced output still needs ellipse-fitting geometry correction - a
  separate, not-yet-built piece of work - and is reported as not yet implemented rather than faked.

There's no automated acquisition pipeline yet (slewing and recording are both manually triggered),
and processing is kicked off by hand rather than automatically once a recording finishes - both are
still ahead, see `CLAUDE.md`.

## Building

```
git submodule update --init
dotnet build SolScan.slnx
dotnet run --project SolScan.App/SolScan.App.csproj
```

Platform is x64 (matches the ZWO ASI camera SDK's native binaries). Windows only (WPF).

Real camera hardware needs its vendor SDK DLL present in the `SolScan.External` git submodule
(private - ask for access) - see that submodule's own `README.md` for where to source each one.
Both are optional: the app runs fine with either or both absent (the simulator always works).
Mount control needs an ASCOM Alpaca-compatible endpoint reachable (typically the ASCOM Remote
Server in front of any ASCOM driver, real or simulated) - point Options > General's Alpaca
settings at it.

## Architecture

- **SolScan.Core** — domain models & interfaces only, no hardware/IO dependencies: `ITelescopeMount`,
  `ICameraDevice`, `ISerWriter`/`ISerReader`, the equipment library records (`SpectrographProfile`/
  `TelescopeProfile`/`CameraProfile`/`EquipmentSetup`), per-recording `CaptureMetadata`, and the
  process-parameter records (`ProcessParams`/`SpectrumParams`/`GeometryParams`/...).
- **SolScan.Infrastructure** — concrete implementations: an ASCOM Alpaca mount client, ZWO ASI and
  Altair camera wrappers (native SDK P/Invoke), `.ser` file reader/writer, and JSON-file-backed
  equipment/settings/process-parameter storage.
- **SolScan.Processing** — pure algorithms, no UI/hardware deps: the SHG reconstruction pipeline,
  natively ported from astro4j/JSol'Ex (spectral-line-curvature detection, disk reconstruction;
  ellipse-fitting geometry correction is still ahead).
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
