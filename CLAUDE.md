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

**Scope for v1**: equatorial mounts only (no AltAz coordinate conversion) - `ITelescopeMount`'s
RA/Dec-only shape reflects this deliberately, not an oversight. ASCOM **Alpaca** only (REST, via the
ASCOM Remote Server), matching RASTA - not direct COM.

**Licensing**: AGPL-3.0 (`LICENSE.md`), chosen for compatibility with the three projects this one
ports code and ideas from (RASTA - AGPL-3.0, sunscan-backend/sunscan-app - GPL-3.0, astro4j/JSolex -
Apache-2.0). When actually porting a specific astro4j file (a close translation, not just an idea),
add a short header comment on the new C# file pointing back to the original Java source file and
its Apache-2.0 status, and add the new file to the `NOTICE` file's "Specifically adapted" list under
astro4j - that list currently only reflects what's been *discussed*, not what's actually been
ported, so it will need updating as Phases 2+ land real code.

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
  usable as an external process before any native port exists. Several pieces worth calling out
  specifically:
  - **`DeepLineIdentifier`** (`jsolex-core/.../spectrum/DeepLineIdentifier.java`) — identifies which
    line a captured profile is centred on by correlating it against a real solar flux atlas
    (3900-6800 Å, 0.01 Å resolution) across several instrumental-broadening hypotheses, and reports
    nothing unless one hypothesis clearly wins (score ≥ 0.70, margin ≥ 30% of headroom over the
    runner-up). Designed for a whole captured file, but the core method (1D profile → correlate
    against a reference atlas) is the right basis for SolScan's *live* wide-view line-highlighting
    feature too - see Phase 4 - rather than reviving `sunscan-backend`'s disabled `locate_lines.py`
    prototype (see above). This is the *identification* logic to reuse; `SpectrumBrowser` below is
    the *rendering* to reuse - the two aren't the same code.
  - **`SpectrumBrowser`** (`jsolex/.../app/jfx/SpectrumBrowser.java`) — a standalone reference-atlas
    viewer, and the closest existing match to the live wide-view feature itself: renders a synthetic
    spectrum image sampled from `ReferenceIntensities` (the bundled full-resolution reference solar
    spectrum) around a chosen centre wavelength, with pixel↔wavelength mapping computed from the
    *real* instrument optics (`SpectrumAnalyzer.computeSpectralDispersion(shg, centerWavelength,
    pixelSize)` - the same `SpectroHeliograph` preset data noted below), so "Adjust dispersion" ties
    on-screen zoom to actual dispersion rather than a free zoom. Labels lines from
    `SpectralLineCatalog.defaults()` directly (`loadDefaultLines()`) - same catalog as below, nothing
    new there. Its own "Identify" button runs a *third*, simpler algorithm
    (`performWavelengthIdentification()`) - distinct from `DeepLineIdentifier` - that corrects
    spectral-line curvature on a *loaded* captured image first (`DistortionCorrection`, driven by a
    `SpectrumFrameAnalyzer` detection pass), then brute-force scans the whole reference range scoring
    each candidate by profile difference plus local-minima agreement, always returning its best match
    with no confidence gate. For SolScan: build the live overlay's rendering/dispersion/labeling on
    this browser's approach, but keep `DeepLineIdentifier`'s confidence gating for the identification
    step rather than this browser's ungated brute-force scan - a live view shouldn't confidently
    mislabel an ambiguous stretch of spectrum.
  - **`SpectralLineCatalog`** (same package) — a curated "other interesting lines in this window"
    lookup, backed by a bundled `interesting-lines.txt` resource (`wavelength;element;identifier;
    difficulty`), used once the studied line's wavelength is known to label secondary lines nearby.
  - **`SpectralLineDetectedEvent`/`GeometryDetectedEvent`** — the two halves of JSolex's results
    "info view": which line was detected (a `SpectralRay`), and the geometry-correction step's own
    findings (`tiltDegrees`, `xyRatio`) from ellipse-fitting the reconstructed disk. Two unrelated
    pipeline stages that happen to be reported together - SolScan's Process stage should adopt the
    same two-part results panel (see Phase 7).
  - **`ExposureCalculator`** (`jsolex/.../app/jfx/ExposureCalculator.java`) — recommends a starting
    exposure/fps from real optics, not a guess: apparent solar disk size today (a low-precision
    analytic solar ephemeris - mean anomaly → true anomaly → Earth-Sun distance in AU → apparent
    angular diameter, since it varies ~3% over the year) → disk image size at the slit (telescope
    focal length) → disk image size on the sensor (scaled by the SHG's own `cameraFocalLength /
    collimatorFocalLength` re-imaging ratio) → pixels (sensor pixel size × binning) → divided by
    scan time available (`apparent size arcmin × 4 / scanSpeed`, where `scanSpeed` is a multiplier
    of the sun's ~15"/sec apparent motion - Sol'ex users commonly drive RA well above sidereal to
    control scan speed directly rather than relying only on natural drift, worth keeping in mind for
    `ITelescopeMount`'s tracking design in Phase 2/3 - with a `cos(declination)` correction for RA
    scans). All static methods, `java.time`/`Math` only - no JavaFX dependency in the algorithm
    itself, so it ports directly. The apparent-disk-size/declination part is the same ephemeris
    Phase 3's `SunPosition` needs - build it once in `SolScan.Core`, share it between "find the sun"
    and this calculator. `SpectroHeliograph.java` (same `params` package) already carries a
    `SOLEX`/`SOLEX_10`/`SOLEX_7` preset matching real Sol'ex hardware (34° total angle, 125mm camera
    focal length, 80mm collimator, 2400 lines/mm, order 1) and `Setup.java` is the telescope/camera
    equivalent (focal length, aperture, pixel size, site lat/long, mount) - both worth adopting as
    SolScan.Core equipment-profile records rather than reinventing the shape.
- **N.I.N.A.** (`C:\Source\Repos\JPhilC\nina`, third-party, MPL-2.0 - studied for reference, nothing
  ported) — validated `ICameraDevice`'s shape rather than supplying code to port. Multi-vendor camera
  support is layered `NINA.Equipment/SDK/CameraSDKs/<Vendor>SDK/` (raw P/Invoke, ~1:1 with the
  vendor's native C API) → `NINA.Equipment/Interfaces/ICamera.cs` (the one common interface the rest
  of NINA talks to) → `NINA.Equipment/Equipment/MyCamera/<Vendor>Camera.cs` (the adapter implementing
  `ICamera` against that vendor's SDK class) - the same shape `ICameraDevice`/a future ASI SDK wrapper
  already follows. Confirms two things concretely (not inferred): `IGenericCameraSDK.cs` exposes
  `StartExposure`/`GetExposure` *and* a separate `StartVideoCapture`/`StopVideoCapture`/
  `GetVideoCapture` - native astro-camera SDKs really do treat single-shot and streaming capture as
  distinct API families, not one generalized "capture" call - and `ICamera.cs` exposes
  `CanSetUSBLimit`/`USBLimit`, a streaming-specific throttle with no equivalent for a one-shot
  download, which is what motivated adding `ICameraDevice.UsbBandwidthPercent`/`DroppedFrameCount`
  (see "Video vs. long-exposure stills" under Phase 4). SharpCap (closed-source, not clonable) was
  also researched for its camera-support model via its own public docs/forum posts rather than
  source - a three-tier native-SDK/ASCOM/DirectShow fallback with no public extension point, useful
  for its documented *behaviour* (prefer native > ASCOM > DirectShow when more than one applies to a
  camera) but not something to model `ICameraDevice`'s actual structure on the way NINA's code is.

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
  lat/lon/elevation - RA/Dec-only by design, see "Scope for v1" above), `ICameraDevice` (streaming
  frame capture only, no long-exposure-still path by design - see "Video vs. long-exposure stills"
  under Phase 4; gain/exposure, `UsbBandwidthPercent`, `DroppedFrameCount`), `ISerWriter`
  (writes a live frame stream to a `.ser` file). References `CommunityToolkit.Mvvm` for
  `ObservableObject`/`RelayCommand` base classes on domain models that need change notification
  (e.g. live capture/session state), without pulling in WPF itself. Also carries the `Equipment`
  namespace's equipment-profile records, adapted from astro4j's shape: `SpectrographProfile`
  (Sol'ex-equivalent to `SpectroHeliograph.java`: total angle, camera/collimator focal lengths,
  grating density/order, slit size) and `EquipmentProfile` (telescope-equivalent to `Setup.java`:
  telescope/camera names, focal length, aperture, pixel size, mount, site lat/long). Unlike astro4j,
  where `SpectroHeliograph` and `Setup` are two independently-selected libraries with no persisted
  link between them, SolScan adds a third record, `EquipmentSetup` - a saved "this SHG is mounted
  behind this telescope/camera" combination, referencing the other two by `Id` - plus
  `IEquipmentLibrary`, the persistence abstraction for all three libraries (implemented by
  `SolScan.Infrastructure`'s `JsonEquipmentLibrary`, one JSON file per library under
  `%LocalAppData%\SolScan\equipment\`, mirroring astro4j's `SpectroHeliographsIO`/`SetupsIO`). Backs the
  Options view - see below.
- **SolScan.Infrastructure** — concrete implementations: an ASCOM Alpaca telescope client (to be
  ported from RASTA's `AscomAlpacaClient`/`AscomTelescopeMount`), a ZWO ASI camera wrapper (native
  SDK P/Invoke), a `.ser` file writer/reader - none of those three implemented yet. `JsonEquipmentLibrary`
  (see above) is implemented.
- **SolScan.Processing** — pure algorithms, no UI/hardware: the SHG reconstruction pipeline. Not
  yet implemented — starts as a wrapper shelling out to `jsolex-cli`, then incrementally replaced
  with native ports of `SolexVideoProcessor`'s individual workflow steps (spectral line detection,
  ellipse fitting/geometry correction, disk reconstruction, banding/jagging/distortion corrections).
- **SolScan.App** — WPF MVVM shell. `App.xaml.cs` is the single composition root - one
  `ServiceCollection` built once at startup (no scopes created afterward), same pattern RASTA uses.
  `MainWindow` binds to `NavigationViewModel.CurrentViewModel`, swapped via
  `NavigationViewModel.NavigateTo<TViewModel>()` (resolves from the DI container) - no
  router/framework, deliberately, same as RASTA. The left-hand nav sidebar (mirroring RASTA's
  `MainWindow.xaml`) has four buttons - Prepare/Capture/Process plus Options, docked to the bottom
  of the sidebar. `PrepareViewModel`, `CaptureViewModel`, `ProcessViewModel` remain placeholders,
  each currently just a `StatusText` string bound into its view. `OptionsViewModel` is real: it's
  SolScan's equivalent of JSolex's "Equipment" menu (`SpectroHeliographEditor.java` +
  `SetupEditor.java`), embedded as three tabs (Spectrographs / Telescopes & Cameras / Setups) rather
  than separate modal dialogs, each tab backed by its own list-view-model
  (`SpectrographLibraryViewModel`, `EquipmentProfileLibraryViewModel`, `EquipmentSetupLibraryViewModel`
  under `ViewModels/Equipment`) editing `SolScan.Core.Equipment` records through `IEquipmentLibrary`.

### What's real vs. placeholder right now

Real: the solution/project scaffolding, the three-project dependency layering, the DI composition
root, the nav shell (Prepare/Capture/Process/Options buttons swap the content pane), the
`ITelescopeMount`/`ICameraDevice`/`ISerWriter` contracts in Core, and the Options view's equipment
library (`SpectrographProfile`/`EquipmentProfile`/`EquipmentSetup` + `IEquipmentLibrary`, backed by
`JsonEquipmentLibrary`).

Placeholder: everything else behind those contracts. No ASCOM client, no camera wrapper, no SER I/O,
no solar ephemeris, no processing pipeline. `SolScan.Simulators` is still an empty class library with
only a project reference to `Core`.

## Phased build plan

1. **Scaffolding** *(done)* — solution + projects, DI composition root, nav shell.
2. **Mount control** — port RASTA's Alpaca client into `SolScan.Infrastructure`, implement
   `ITelescopeMount`, wire a real Prepare-stage connect/disconnect/site-settings UI.
3. **Find the sun** — add a solar ephemeris (`SunPosition`, low-precision analytic, arc-minute
   accuracy is enough for a slit-width offset) to `SolScan.Core`, add a "slew to sun + lead offset"
   command to the Capture stage. Build the apparent-disk-size/declination part so Phase 4's exposure
   calculator can reuse it rather than duplicating the ephemeris. The ephemeris slew is always the
   starting point - it's what gets the mount close enough for anything below to have signal to work
   with in the first place.
   - **Visual fine-centering** (refinement, depends on Phase 4's camera capture landing first -
     either build a minimal frame-streaming capability ahead of the rest of Phase 4's UI, or treat
     this as a Phase 3 addition once Phase 4 exists rather than a hard blocker): an SHG only ever
     sees whatever light passes through its slit, so total brightness in the live frame is a direct,
     unimodal proxy for how well the sun's disk currently overlaps the slit - no risk of locking onto
     the wrong source, since nothing else in a daytime sky is remotely as bright as the sun. Two
     phases: an expanding-spiral/raster search first, in case the ephemeris slew leaves the frame at
     zero signal (gradient-climbing needs *some* nonzero signal to follow - it can't recover from a
     flat-zero frame on its own); then a hill-climb/P-controller phase nudging both mount axes to
     maximize total frame brightness once signal exists. Needs a deliberately low search-phase
     exposure/gain, separate from the capture-ready exposure the Phase 4 calculator recommends, so
     the signal doesn't saturate and flatten out near the peak. Keep tracking on throughout (ideally
     already at a rate accounting for the sun's faster-than-sidereal motion) so the target doesn't
     drift away mid-search. Refines pointing precision only - the ephemeris still supplies the lead-
     offset direction/distance, which brightness-peaking alone can't tell you. No prior art for this
     one in RASTA/sunscan-backend/astro4j - genuinely new to SolScan, not a port.
4. **Manual capture** — ZWO ASI SDK wrapper implementing `ICameraDevice`, live preview in the
   Capture view, manual start/stop recording through a real `ISerWriter` implementation. This alone
   matches what SharpCap does today. Carries over the sunscan-app UX features noted above:

   **Video vs. long-exposure stills**: SolScan is deliberately scoped to streaming/video capture
   only - continuous frames for live preview and SER recording - never NINA's single-shot
   `StartExposure`/`DownloadExposure` long-exposure-still model, and `ICameraDevice` has no such
   path. These are genuinely different SDK API families (confirmed from NINA's own
   `IGenericCameraSDK.cs`, not inferred - see the N.I.N.A. entry above), not just "the same call with
   a different duration": a request/response cycle (start, wait seconds-to-minutes, download one
   frame) versus a continuous producer/consumer stream (sub-ms-to-low-ms exposures, frames arriving
   continuously until told to stop). That difference is why `ICameraDevice` carries
   `UsbBandwidthPercent` (throttles sustained throughput against max achievable fps - irrelevant to a
   single occasional download) and `DroppedFrameCount` (the SDK's ring buffer overwrites frames the
   app doesn't pull fast enough - a failure mode a one-shot download has no equivalent of). Wire the
   ASI SDK wrapper to its video-capture API family (`ASIStartVideoCapture`/`ASIGetVideoData`), not
   its single-exposure one (`ASIStartExposure`/`ASIGetDataAfterExp`).
   - an exposure/fps calculator (port `ExposureCalculator.java`'s physics - apparent disk size at
     the slit, through the SHG's camera/collimator focal-length ratio, to pixels on the sensor,
     divided by scan time from the mount's scan-rate multiplier) suggesting a starting point before
     the sliders below are hand-tuned; needs `SpectrographProfile`/`EquipmentProfile` data from Core
   - live gain/exposure sliders driving the camera in real time, not a separate settings dialog
   - a wide/ROI ("crop") view toggle, with the ROI vertically positionable, for framing during setup
     vs. the tighter view actually used while recording
   - a camera focus aid (port `calculate_fwhm`'s spectral-line-width measurement - narrower FWHM
     means sharper camera focus)
   - a collimator focus aid (port `focus_analyzer.py`'s disk-edge-sharpness measurement - sharper
     disk edges mean better collimator alignment)
   - a live line-identification overlay for the wide view, so labeled Fraunhofer lines scroll into
     place as the diffraction grating is rotated. Rendering/dispersion-matching/labeling modelled on
     `SpectrumBrowser` (real-optics dispersion via `SpectrumAnalyzer.computeSpectralDispersion`,
     labels from `SpectralLineCatalog`'s `interesting-lines.txt`), but identification logic modelled
     on `DeepLineIdentifier`'s confidence-gated correlation rather than `SpectrumBrowser`'s own
     ungated brute-force scan - needs its own reference solar-flux atlas and dispersion calibration
     for the Sol'ex + ASI678MM combination, not Sunscan's atlas/constants
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
