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
  pattern; `focus_analyzer.py`'s disk-edge gradient detection is the basis for auto
  start/stop-on-slit-crossing detection. `locate_lines.py` is a disabled prototype (imported but
  commented out at `main.py:972-973`) for live line identification in the wide view - cross-correlates
  the current profile against a reference spectrum image (`sun_spectre.png`) and a small hardcoded
  13-line atlas. Superseded as a design reference by astro4j's `DeepLineIdentifier` below - same core
  idea, but statistically gated rather than a single naive template match.
- **sunscan-app** (`C:\Source\Repos\JPhilC\sunscan-app`) — the Sunscan mobile UI, source of several
  Capture-stage UX features SolScan should reproduce (see `screens/ScanScreen.js`):
  - **Gain/exposure controls** — live sliders (`GAIN`, exposure time) sent to the backend as the
    preview updates, not just a settings screen buried elsewhere.
  - **Two distinct focus aids, both already present** (initially misread as one aid plus a gap -
    corrected after re-checking against the real app):
    - **Camera focus** — the "Spectrum" toggle (`toggleSpectrum('vertical')`, `align-horizontal-middle`
      icon, `ScanScreen.js` ~607-616), which streams a live FWHM (full-width-half-maximum) of the
      spectral line profile (`main.py`'s `calculate_fwhm`, over the `spectrum` websocket channel). A
      narrower line = sharper camera focus - this reflects the camera sensor's own optical focus,
      independent of the spectrograph's alignment.
    - **Collimator focus** — the "Focus" toggle (prism icon, `toggleFocus`, gated to crop/ROI mode),
      backed by `focus_analyzer.py`'s `measure_focus_two_edges` (gradient-based sharpness of the solar
      disk's edges built up during a scan, over the `focus` websocket channel, with a running
      "best so far" high-water mark). This reflects the collimator's alignment, not the camera's.
    Both need porting to SolScan; neither is a stand-in for the other.
  - **Wide view / clipped (ROI) view toggle** — `toggleCrop`/`updatePosYCrop` swap between a wide
    preview (for finding/framing the disk and the slit) and a cropped, vertically-positionable
    region-of-interest view used during actual capture. SolScan's Capture view needs the same two
    modes, not just one fixed preview.
- **astro4j / JSolex** (`C:\Source\Repos\JPhilC\astro4j`) — the mature, offline SHG reconstruction
  pipeline (`jsolex-core`'s `SolexVideoProcessor` and friends) this app's Process stage is meant to
  eventually match in flexibility. Apache-2.0 licensed; `jsolex-cli` is a headless entry point
  usable as an external process before any native port exists. Two pieces worth calling out
  specifically:
  - **`DeepLineIdentifier`** (`jsolex-core/.../spectrum/DeepLineIdentifier.java`) — identifies which
    line a captured profile is centred on by correlating it against a real solar flux atlas
    (3900-6800 Å, 0.01 Å resolution) across several instrumental-broadening hypotheses, and reports
    nothing unless one hypothesis clearly wins (score ≥ 0.70, margin ≥ 30% of headroom over the
    runner-up). Designed for a whole captured file, but the core method (1D profile → correlate
    against a reference atlas) is the right basis for SolScan's *live* wide-view line-highlighting
    feature too - see Phase 4 - rather than reviving `sunscan-backend`'s disabled `locate_lines.py`
    prototype (see above).
  - **`SpectralLineCatalog`** (same package) — a curated "other interesting lines in this window"
    lookup, backed by a bundled `interesting-lines.txt` resource (`wavelength;element;identifier;
    difficulty`), used once the studied line's wavelength is known to label secondary lines nearby.
  - **`SpectralLineDetectedEvent`/`GeometryDetectedEvent`** — the two halves of JSolex's results
    "info view": which line was detected (a `SpectralRay`), and the geometry-correction step's own
    findings (`tiltDegrees`, `xyRatio`) from ellipse-fitting the reconstructed disk. Two unrelated
    pipeline stages that happen to be reported together - SolScan's Process stage should adopt the
    same two-part results panel (see Phase 7).

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
   - a camera focus aid (port `calculate_fwhm`'s spectral-line-width measurement - narrower FWHM
     means sharper camera focus)
   - a collimator focus aid (port `focus_analyzer.py`'s disk-edge-sharpness measurement - sharper
     disk edges mean better collimator alignment)
   - a live line-identification overlay for the wide view, so labeled Fraunhofer lines scroll into
     place as the diffraction grating is rotated - built on `DeepLineIdentifier`'s confidence-gated
     correlation approach (needs its own reference solar-flux atlas and dispersion calibration for
     the Sol'ex + ASI678MM combination, not Sunscan's atlas/constants), plus `SpectralLineCatalog`'s
     `interesting-lines.txt` data for secondary-line labels once roughly on target
5. **Automated acquisition** — background capture pipeline (`System.Threading.Channels`), auto-detect
   the disk entering/centred on/leaving the slit from the live preview (port `focus_analyzer.py`'s
   edge-detection technique), tie into step 3's slew-ahead-and-drift logic as one "Capture" action
   requiring no further manual intervention.
6. **Processing v1 (shell-out)** — `JSolexCliProcessor` in `SolScan.Processing`, invokes `jsolex-cli`
   against a finished SER file as a background `Task`, parses/display results, progress surfaced in
   the Process view.
7. **Processing v2 (native, incremental)** — port `SolexVideoProcessor`'s workflow steps into
   `SolScan.Processing` one at a time, behind the same `IShgProcessor`-shaped interface, validated
   against the CLI's output as ground truth for each. Goal: processing running concurrently with
   capture on its own thread, not waiting on a full external pass over the finished file. Includes
   porting `DeepLineIdentifier`/`SpectralLineCatalog` for the Process stage's own line
   identification, and a results panel mirroring JSolex's two-part info view (detected line +
   geometry tilt/xyRatio).
8. **Polish** — output styles/palettes, dark/flat calibration, session/plan management, installer
   (WiX, mirroring RASTA's `Setup`/`Bundle` projects), `SolScan.Simulators` fleshed out for offline
   dev/tests.
