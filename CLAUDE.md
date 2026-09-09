# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## What this is

SolScan is a .NET 10 WPF/MVVM app for spectroheliograph (SHG) solar imaging. It drives an
ASCOM-controlled mount and a ZWO ASI678MM (or similar) camera through a **Prepare → Capture →
Process** workflow: find the sun's current position, slew slightly ahead of it, let the disk drift
across the SHG's entrance slit, auto-detect that crossing to start/stop recording a SER video, and
run the SHG reconstruction pipeline (spectral line detection → geometry correction → per-wavelength
disk reconstruction → corrections → output images) on a background thread within the same app —
rather than the current manual workflow of slewing by hand, capturing in SharpCap, and processing
later in JSolex.

It's early-stage scaffolding as of this writing — see "Phased build plan" below for what's actually
implemented vs. still a placeholder. Don't assume a component is wired up just because a project or
interface exists.

## Prior art this design draws on

- **RASTA** (`C:\Source\Repos\JPhilC\RASTA`) — a sibling hobby project (1420 MHz radio astronomy)
  that already proved out this exact layering (`Core`/`Infrastructure`/`Processing`/`App`), an
  ASCOM Alpaca mount client, and a Prepare→Plan→Capture→Visualise workflow with cancellable,
  progress-reporting background operations. SolScan's `ITelescopeMount` and navigation shell are
  deliberately shaped to match.
- **sunscan-backend** (`C:\Source\Repos\JPhilC\sunscan-backend`) — the Raspberry Pi backend for a
  Sunscan SHG: `camera_controller.py` shows the continuous-capture-thread-writing-SER-frames
  pattern; `locate_lines.py`/`focus_analyzer.py` show template-matching/gradient-edge techniques for
  finding the solar disk and spectral line position in a live frame - the basis for auto
  start/stop-on-slit-crossing detection.
- **sunscan-app** (`C:\Source\Repos\JPhilC\sunscan-app`) — the Sunscan mobile UI, source of several
  Capture-stage UX features SolScan should reproduce (see `screens/ScanScreen.js`):
  - **Gain/exposure controls** — live sliders (`GAIN`, exposure time) sent to the backend as the
    preview updates, not just a settings screen buried elsewhere.
  - **Camera focus aid** — a toggleable "focus assistant" overlay; on the backend this is
    `focus_analyzer.py`'s `measure_focus_two_edges` (gradient-based sharpness of the solar disk's
    edges in a live frame, smoothed/normalized over recent frames). SolScan needs the equivalent for
    the ASI678MM's own optical focus.
  - **Collimator focus aid** — a related but distinct concern specific to a slit spectrograph
    (Sol'ex) that Sunscan's simpler design doesn't have an exact equivalent for: judging the
    sharpness/quality of the spectral line image at the slit itself, as opposed to the camera's
    optical focus. Likely built on the same live-frame-gradient technique as the camera focus aid,
    but measuring the spectral line profile rather than the disk edge - needs its own aid, not
    reuse-as-is.
  - **Wide view / clipped (ROI) view toggle** — `toggleCrop`/`updatePosYCrop` swap between a wide
    preview (for finding/framing the disk and the slit) and a cropped, vertically-positionable
    region-of-interest view used during actual capture. SolScan's Capture view needs the same two
    modes, not just one fixed preview.
- **astro4j / JSolex** (`C:\Source\Repos\JPhilC\astro4j`) — the mature, offline SHG reconstruction
  pipeline (`jsolex-core`'s `SolexVideoProcessor` and friends) this app's Process stage is meant to
  eventually match in flexibility. Apache-2.0 licensed; `jsolex-cli` is a headless entry point
  usable as an external process before any native port exists.

## Commands

The solution file is **`SolScan.slnx`** (the newer XML solution format), not a `.sln`.

```
dotnet build SolScan.slnx                          # build everything
dotnet build SolScan.App/SolScan.App.csproj         # build just the WPF app
dotnet run --project SolScan.App/SolScan.App.csproj # run the app (Windows only)
dotnet test SolScan.Tests/SolScan.Tests.csproj      # run tests
```

Platform is x64 throughout (`PlatformTarget`/`Platforms` set on every project) — the ZWO ASI camera
SDK ships x64 native binaries, so building `AnyCPU` risks a mismatch once that dependency lands.

## Architecture

### Project layering

Dependencies flow one way: `SolScan.Core` ← `SolScan.Infrastructure` ← `SolScan.Processing` ←
`SolScan.App`. (`SolScan.Simulators` and `SolScan.Tests` both depend on `Core` only, so they can
exercise the domain contracts without pulling in real hardware or the WPF app.)

- **SolScan.Core** — domain models and interfaces only, no concrete hardware/IO deps:
  `ITelescopeMount` (connect/disconnect, current position, slew, tracking, park/unpark, site
  lat/lon/elevation), `ICameraDevice` (streaming frame capture, gain/exposure), `ISerWriter`
  (writes a live frame stream to a `.ser` file). References `CommunityToolkit.Mvvm` for
  `ObservableObject`/`RelayCommand` base classes on domain models that need change notification
  (e.g. live capture/session state), without pulling in WPF itself.
- **SolScan.Infrastructure** — concrete implementations: an ASCOM Alpaca telescope client (to be
  ported from RASTA's `AscomAlpacaClient`/`AscomTelescopeMount`), a ZWO ASI camera wrapper (native
  SDK P/Invoke), a `.ser` file writer/reader. Not yet implemented.
- **SolScan.Processing** — pure algorithms, no UI/hardware: the SHG reconstruction pipeline. Not
  yet implemented — starts as a wrapper shelling out to `jsolex-cli`, then incrementally replaced
  with native ports of `SolexVideoProcessor`'s individual workflow steps (spectral line detection,
  ellipse fitting/geometry correction, disk reconstruction, banding/jagging/distortion corrections).
- **SolScan.App** — WPF MVVM shell. `App.xaml.cs` is the single composition root - one
  `ServiceCollection` built once at startup (no scopes created afterward), same pattern RASTA uses.
  `MainWindow` binds to `NavigationViewModel.CurrentViewModel`, swapped via
  `NavigationViewModel.NavigateTo<TViewModel>()` (resolves from the DI container) - no
  router/framework, deliberately, same as RASTA. Three stage view models exist as placeholders:
  `PrepareViewModel`, `CaptureViewModel`, `ProcessViewModel`, each currently just a `StatusText`
  string bound into its view.

### What's real vs. placeholder right now

Real: the solution/project scaffolding, the three-project dependency layering, the DI composition
root, the nav shell (Prepare/Capture/Process buttons swap the content pane), and the `ITelescopeMount`
/`ICameraDevice`/`ISerWriter` contracts in Core.

Placeholder: everything behind those contracts. No ASCOM client, no camera wrapper, no SER I/O, no
solar ephemeris, no processing pipeline. `SolScan.Simulators` and `SolScan.Infrastructure` are both
empty class libraries with only a project reference to `Core` so far.

## Phased build plan

1. **Scaffolding** *(done)* — solution + projects, DI composition root, nav shell.
2. **Mount control** — port RASTA's Alpaca client into `SolScan.Infrastructure`, implement
   `ITelescopeMount`, wire a real Prepare-stage connect/disconnect/site-settings UI.
3. **Find the sun** — add a solar ephemeris (`SunPosition`, low-precision analytic, arc-minute
   accuracy is enough for a slit-width offset) to `SolScan.Core`, add a "slew to sun + lead offset"
   command to the Capture stage.
4. **Manual capture** — ZWO ASI SDK wrapper implementing `ICameraDevice`, live preview in the
   Capture view, manual start/stop recording through a real `ISerWriter` implementation. This alone
   matches what SharpCap does today. Carries over the sunscan-app UX features noted above:
   - live gain/exposure sliders driving the camera in real time, not a separate settings dialog
   - a wide/ROI ("crop") view toggle, with the ROI vertically positionable, for framing during setup
     vs. the tighter view actually used while recording
   - a camera focus aid (port `focus_analyzer.py`'s disk-edge-sharpness measurement)
   - a collimator focus aid (new - spectral-line-sharpness-at-the-slit, not disk-edge-based)
5. **Automated acquisition** — background capture pipeline (`System.Threading.Channels`), auto-detect
   the disk entering/centred on/leaving the slit from the live preview (port the technique from
   `locate_lines.py`/`focus_analyzer.py`), tie into step 3's slew-ahead-and-drift logic as one
   "Capture" action requiring no further manual intervention.
6. **Processing v1 (shell-out)** — `JSolexCliProcessor` in `SolScan.Processing`, invokes `jsolex-cli`
   against a finished SER file as a background `Task`, parses/display results, progress surfaced in
   the Process view.
7. **Processing v2 (native, incremental)** — port `SolexVideoProcessor`'s workflow steps into
   `SolScan.Processing` one at a time, behind the same `IShgProcessor`-shaped interface, validated
   against the CLI's output as ground truth for each. Goal: processing running concurrently with
   capture on its own thread, not waiting on a full external pass over the finished file.
8. **Polish** — output styles/palettes, dark/flat calibration, session/plan management, installer
   (WiX, mirroring RASTA's `Setup`/`Bundle` projects), `SolScan.Simulators` fleshed out for offline
   dev/tests.
