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
  - **ROI height** (the *other* crop axis from `ExposureCalculator` above - easy to conflate the two,
    so worth being explicit): in an SHG frame, width is the *spatial* axis (position along the slit,
    across the disk - what `ExposureCalculator` sizes) and height is the *wavelength/dispersion*
    axis (pixel-shift from the line centre) - confirmed from `PixelShiftRange.java`'s doc comment
    ("the shift is expressed in pixels relative to the middle of the detected spectral line") and
    from `jsolex.adoc`'s "Trimming SER files" section, which works on this exact quantity after the
    fact (post-hoc "pixels up"/"pixels down" trimming once the line's "smile" curvature is already
    known) rather than recommending it up front - JSolex never actually prescribes a capture-time
    height, only warns about getting it wrong: *"reducing the number of pixels up/down will remove
    information from the video (you won't be able to compute images with larger pixel shifts)... If
    you are selecting too low pixel up/down values, you may not be able to generate a continuum image
    anymore."* SolScan's own `RecommendedRoiHeight` (Phase 4) is new - a capture-time equivalent of
    that same trade-off, built from `SpectrumAnalyzer.computeSpectralDispersion`'s Å/pixel plus how
    far out you want reconstruction to reach (line + wings + enough continuum margin, plus a small
    allowance for smile curvature) - directly opposed to `ExposureCalculator`'s own fps target, since
    a taller ROI costs frame-rate/USB-bandwidth budget the scan-sampling requirement also needs, so
    the right answer is the *smallest* height that reaches the reconstruction goal, not the largest
    one that fits.
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
  (see "Video vs. long-exposure stills" under Phase 4). Also confirmed concretely: N.I.N.A.'s
  discovery pattern, `IEquipmentProvider<ICamera>.GetEquipment()` - one per vendor, enumerates
  attached devices, returns ready-to-connect (not-yet-connected) device objects - which is what
  `ICameraProvider`/`ICameraDiscoveryService` in `SolScan.Core.Camera` are modelled on; and that ASI
  (poll/blocking `ASIGetVideoData`) and ToupTek-alike brands like Altair (callback-driven
  `StartPullModeWithCallback` + `PullImage`) genuinely need different capture-loop shapes
  internally, even though both normalise into `ICameraDevice`'s single push-style `FrameCaptured`
  event - see `SolScan.Infrastructure.Camera.Asi`/`.Camera.Altair`. SharpCap (closed-source, not
  clonable) was
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
  of the sidebar. `PrepareViewModel`/`ProcessViewModel` remain placeholders, each currently just a
  `StatusText` string bound into its view. `CaptureViewModel` is real as of the camera
  discovery/live-view/recording work below. `OptionsViewModel` is real: it's
  SolScan's equivalent of JSolex's "Equipment" menu (`SpectroHeliographEditor.java` +
  `SetupEditor.java`) plus a General tab for app-wide settings that aren't equipment at all,
  embedded as four tabs (General / Spectrographs / Telescopes & Cameras / Setups) rather than
  separate modal dialogs. The three equipment tabs are each backed by their own list-view-model
  (`SpectrographLibraryViewModel`, `EquipmentProfileLibraryViewModel`, `EquipmentSetupLibraryViewModel`
  under `ViewModels/Equipment`) editing `SolScan.Core.Equipment` records through `IEquipmentLibrary`.
  General is backed by `GeneralSettingsViewModel` editing `SolScan.Core.Capture.AppSettings` through
  `IAppSettingsStore` (`SolScan.Infrastructure.Capture.JsonAppSettingsStore`, one JSON file under
  `%LocalAppData%\SolScan\`, same per-machine-data rationale as `JsonEquipmentLibrary`/
  `JsonCameraSettingsStore`) - currently just where new recordings are saved
  (`AppSettings.CapturesRootFolder`), picked via a native `Microsoft.Win32.OpenFolderDialog` (no
  WinForms reference needed - that type's been part of WPF itself since .NET 8). Defaults to
  `Documents\SolScan\Captures` (`CaptureLocations.DefaultCapturesRootFolder`) - stored as `null`,
  not that literal path, whenever the field still equals the current default, so a future change to
  the default is picked up automatically for anyone who's never actually customized it.
  `CaptureViewModel.StartRecording` reads the setting fresh (a cheap JSON read) each time a
  recording starts, so a change in Options takes effect on the very next recording without a
  restart; a customized folder is used exactly as chosen at that level (no further
  `SolScan\Captures` appended) - the one thing always added underneath it, custom or default, is a
  `yyyyMMdd` date subfolder (one `DateTime.Now` reused for both that and the filename's own
  timestamp, so the two can't disagree across a midnight boundary) - matching sunscan-backend's own
  `storage/scans/<date>/` layout, researched specifically for this (see `camera_controller.py`'s
  `_initSerFile`), though sunscan's own SER file is always literally named `scan.ser` with
  uniqueness coming entirely from its enclosing per-scan folder name, unlike SolScan's own
  `SolScan_<timestamp>.ser` naming.

### What's real vs. placeholder right now

Real: the solution/project scaffolding, the three-project dependency layering, the DI composition
root, the nav shell (Prepare/Capture/Process/Options buttons swap the content pane), the
`ITelescopeMount`/`ICameraDevice`/`ISerWriter` contracts in Core, and the Options view's equipment
library (`SpectrographProfile`/`EquipmentProfile`/`EquipmentSetup` + `IEquipmentLibrary`, backed by
`JsonEquipmentLibrary`). Also real: camera discovery/live view/manual SER recording (the first slice
of Phase 4, "Manual capture" below) - `ICameraProvider`/`ICameraDiscoveryService` in Core;
`SolScan.Infrastructure.Camera.Asi`/`.Camera.Altair` (hand-written P/Invoke against each vendor's
native SDK - no vendor DLLs committed, see `SolScan.Infrastructure/ASICamera2.README.md`/
`altaircam.README.md`) and
`SolScan.Infrastructure.Capture.SerWriter`; `SolScan.Simulators`' `SimulatedCameraDevice`/
`SimulatedCameraProvider` (a hardware-free camera so the above works with zero hardware attached);
and `CaptureViewModel`/`CaptureView.xaml` wiring it all into a SharpCap-style live view (camera
picker, connect/play, gain/exposure/contrast sliders, histogram, start/stop recording). Also real:
per-camera-model settings persistence (`ICameraSettingsStore`/`JsonCameraSettingsStore`) and a
RASTA-style `StatusBarViewModel`/`StatusBar.xaml` docked at the bottom of `MainWindow.xaml`,
currently just showing the live capture frame rate. Also real: a centred, width/height-only ROI
(`CaptureViewModel.RoiWidth`/`RoiHeight`, defaulting to the full sensor) - a genuine *hardware*
reconfiguration via `ICameraDevice.SetOutputFormatAsync` (which now takes `roiWidth`/`roiHeight`
alongside colour space/binning - ASI's `ASISetROIFormat` sets all of these in one native call, and
per its own SDK manual centres the ROI on the sensor automatically), not a post-capture software
crop. That distinction isn't cosmetic: an earlier version of this feature *was* a software-only crop
applied after the frame had already been captured, and confirmed on real ASI678MM hardware it left
live-view frame rate completely unchanged from full-frame capture (~19-24fps at Mono16 3840x500
regardless of Server GC, `ASI_HIGH_SPEED_MODE`, or removing a redundant per-loop-iteration native
exposure query - none of which were the actual cause) - the camera was transferring the full sensor
over USB the entire time no matter what the UI showed. Switching to a real `ASISetROIFormat`-driven
ROI (`AsiCameraDevice.ApplyRoiFormat`, using `FramePreview.ComputeCenteredRoi` to resolve the
request against the full binned sensor size, then rounding down to the SDK's iWidth%8=0/iHeight%2=0
requirement) let ASICap sustain ~102.5fps at the same settings once *it* was given the same real
ROI - confirming the fix. `AsiCameraDevice.CaptureLoop` also rotates through 8 pre-allocated frame
buffers instead of a fresh allocation-plus-clone every frame, matching ZWO's own bundled C/C++
reference demo's approach (`demo/MFC2/demoDlg.cpp`'s `CaptureVideo` thread writes each frame into
one shared, reused buffer) - a real, if secondary, throughput improvement found while investigating
the same fps gap, independent of the ROI fix itself. A first version of the ROI feature also had a
dimming-mask overlay showing the full sensor with everything outside the ROI darkened, but that
only made sense for a software crop (where the full frame was still being captured and displayed) -
removed once ROI became a real hardware setting, since the camera then only ever delivers the
cropped region and there's no wider "full sensor" data left to show or mask. `SimulatedCameraDevice`
mirrors the real ASI behaviour (`FramePreview.ComputeCenteredRoi` resolves the requested ROI against
its own fixed sensor size) so the feature is exercisable without hardware; `AltairCameraDevice`
accepts the same `roiWidth`/`roiHeight` parameters but doesn't yet apply them (no
`Altaircam_put_Roi`-equivalent P/Invoke binding exists, and there's no real Altair hardware in this
environment to develop one against) - always streams the full frame regardless of what's requested,
same honesty-over-guessing stance as the rest of that class. This ROI mechanism is *simpler* than
the "wide/ROI view toggle" described under Phase 4 below - no vertical positioning, no switching
between a wide framing view and a separate tight recording view, always centred - so that toggle
item is still outstanding; this is a complementary piece of it, not a replacement.

Placeholder: everything else behind those contracts. No ASCOM client, no solar ephemeris, no
processing pipeline, and within Phase 4 itself: no exposure/fps calculator, no wide/ROI *view toggle*
(see the centred ROI note above for what's real there instead), no camera-focus/collimator-focus
aids, no live line-ID overlay yet (see the Phase 4 sub-items below).

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
4. **Manual capture** *(core landed)* — camera discovery + ZWO ASI/Altair SDK wrappers implementing
   `ICameraDevice` (plus a hardware-free `SolScan.Simulators` camera), live preview in the Capture
   view, manual start/stop recording through a real `ISerWriter` implementation, gain/exposure/
   contrast sliders and a histogram. This alone matches what SharpCap does today for these two
   vendors - real ZWO/Altair hardware still needs their native SDK DLL dropped in manually (see
   `SolScan.Infrastructure/ASICamera2.README.md`/`altaircam.README.md`), and the sub-items below (exposure
   calculator, wide/ROI view, focus aids, live line-ID overlay) are still outstanding. Carries over
   the sunscan-app UX features noted above:

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
   - live gain/exposure/USB-bandwidth sliders driving the camera in real time (each with a SharpCap-
     style "Auto" toggle handing that control to the camera's own algorithm), not a separate
     settings dialog. Ranges are calibrated to the ASI678MM specifically, matching SharpCap: gain
     0-600 linear, USB Turbo 40-100 linear, exposure 0.032ms-5s on a *log* scale (`SolScan.Core.
     Camera.ExposureScale` - a linear slider is useless across five decades of range) - other
     sensors' real control ranges may differ, not yet queried from the SDK per-camera. Also matching
     SharpCap: a Colour Space dropdown (Mono8/Mono16 - `CameraOutputFormat`) and a Binning dropdown
     populated from the connected camera's own `SupportedBinning` list (ASI's is read from the SDK's
     real reported capability, e.g. [1,2,3,4] for the ASI678MM; Altair's is a hardcoded guess, not
     queried - see `AltairCameraDevice`'s remarks). Both are reconfigured together via
     `ICameraDevice.SetOutputFormatAsync` - a real native operation, not a cheap value push like
     Gain/Exposure, since it changes frame geometry/bit depth and (on ASI) needs streaming briefly
     stopped/restarted around it; `CaptureViewModel` blocks changing either while a recording is in
     progress, since a SER file's fixed header can't represent a mid-file geometry change.

     Live preview rendering went through a few rounds of tuning against real ASI678MM hardware,
     compared side-by-side against SharpCap and ZWO's own ASICap (which sustains ~47fps, 0 dropped
     frames, at full 3840x2160 - the working proof that the camera/USB link itself isn't the
     bottleneck):
     - **Auto-stretch is off by default** (`CaptureViewModel.IsContrastAuto`). It exists
       (`FramePreview.ComputeAutoStretch` - percentile-clips a small fraction at each histogram
       extreme rather than true min/max, so a couple of hot/dead pixels can't skew the whole
       result) for just eyeballing a scene, but dialing in real Gain/Exposure settings needs the
       preview to faithfully show the actual exposure, not a software-brightened stand-in for it -
       matches ASICap/SharpCap's own default behaviour once their own gamma/stretch is set to
       neutral. For the same reason `FramePreview`'s display gamma constant defaults to 1 (a
       no-op) rather than a brightening curve.
     - **The histogram is drawn as a stepped bar chart**, not a line connecting bucket-centre
       points - an isolated spike (e.g. a heavily overexposed frame, virtually every pixel in one
       bucket) drawn as a line has a literally zero-width peak, which can render as invisible
       rather than a small sliver; a bar always has real width. That alone turned out not to be
       enough, though - two further rounds of tuning, both against real ASI678MM hardware:
       - **Bar heights are on a log scale** (`FramePreview.ComputeHistogramBarHeights` -
         log(count+1)/log(maxCount+1) per bucket), not linear. A live SHG frame is overwhelmingly
         background/sky pixels around whatever's actually interesting (the slit's bright band, a
         clipping disk edge); as exposure rises toward overexposed the dominant background bucket's
         count grows faster than the smaller "interesting" bucket's, so under linear normalization
         the interesting bucket's *relative* height kept shrinking even as its raw count (and
         `ComputeHistogramStats`' own Max/Avg readout) correctly climbed - on the histogram panel's
         compact 60px height, anything under ~1.7% of the peak's count renders under a pixel tall,
         i.e. invisible. Confirmed on real hardware: a real overexposure sweep showed the graph
         flattening to nothing instead of building a hump on the right, while the Min/Max/Avg text
         tracked correctly the whole time - proof the underlying data was fine and only the *scale*
         used to draw it was the problem. Log scale is the same fix SharpCap/PixInsight/ASICap use
         for this exact "background massively outnumbers signal" shape of problem.
       - **The plotted area has a small margin on each side**
         (`CaptureViewModel.HistogramEdgeMargin`/`HistogramPlotWidth`/`HistogramPlotHeight`, with
         CaptureView.xaml's histogram `Canvas` sized to those constants via `x:Static` instead of
         letting its `Viewbox` size itself off the `Path`'s own data-dependent geometry bounds). Even
         after the log-scale fix, a *fully* saturated frame (every sampled pixel identical, e.g.
         Min:255 Max:255 Avg:255) still rendered a completely blank graph on real hardware - that
         single 100%-height bar sits exactly flush against the plotted area's own edge (the very
         last bucket), where WPF's layout rounding could snap its already-thin fill area away to
         nothing. The margin keeps a bar at bucket 0 or the last bucket comfortably inside the
         plotted area instead of flush against it.
     - **`ComputeHistogram`/`Stretch` sample a downsampled (nearest-neighbour strided) grid of the
       frame, not every pixel** (`FramePreview.DefaultMaxPreviewDimension`, 960 on the longest
       side) - a live preview redrawn ~20x/sec has no business scanning every one of several
       million sensor pixels each time just to end up displayed in a window nowhere near that
       size; recording (`SerWriter.WriteFrame`) always gets the full, untouched frame regardless.
     - **This scanning work never runs on the thread the connected device raises `FrameCaptured`
       on** - that's the *camera's own capture thread* (the loop calling `ASIGetVideoData`, or
       Altair's native callback), and blocking it on preview work throttles the actual camera
       capture rate, which is what was showing up as both dropped frames and a laggy live view
       even before the downsampling fix above. It's offloaded to the thread pool instead, guarded
       (`Interlocked`) so at most one preview frame is ever mid-processing at once - a slow redraw
       silently drops that frame for *preview* purposes only, never for recording, which happens
       synchronously before this throttle/offload point.

     Resolved via `FramePreview.ComputeHistogramStats`' Max/Min/AVG readout: on real ASI678MM
     hardware, a saturated Mono16 pixel reads back as exactly 65520 = 4095 << 4. That means ZWO's
     RAW16 output **left-shifts** the 12-bit ADC reading into the *upper* 12 bits of the 16-bit
     word - the opposite of what an earlier version of this code assumed (that it sat unscaled in
     the low bits, matching the doc comment ZWO's own header carries for `BitDepth`, "the actual
     ADC depth of image sensor" - true of the sensor, not of how the 16-bit container packs it).
     So the correct normalization range for Mono16 is the **container size** (65535), not the
     sensor's own ADC depth (4095) - `AsiCameraDevice.ApplyRoiFormat` sets `_bitDepth` from
     `_bytesPerPixel * 8` accordingly. Mono8 was never affected either way, since it always
     hardcodes BitDepth=8 regardless of anything read from the SDK - which is exactly why
     comparing the two side-by-side against ASICap (Mono8 matching, Mono16 not) is what isolated
     this. Confirmed the `AsiCameraInfo` struct layout itself is *not* the culprit by checking it
     field-by-field against ZWO's real `ASICamera2.h` (via indilib/indi-3rdparty's mirror of it) -
     it matches exactly. `AltairCameraDevice` already assumed left-shift-to-fill-container for
     Altair's own RAW16 (it was never "fixed" the wrong way in the first place) - this finding is
     one more data point supporting that being correct there too, though still unverified against
     real Altair hardware.

     Binning defaults to `SupportedBinning[0]` (each device's own connect logic), not a hardcoded
     1 - always the same value in practice for cameras seen so far, but asks the camera rather than
     assuming.

     Capture settings (Gain/Exposure/USB Turbo and their Auto flags, Colour Space, Binning,
     Contrast stretch) are remembered per camera *model* across sessions -
     `SolScan.Core.Camera.ICameraSettingsStore`/`CameraSettings`, backed by
     `SolScan.Infrastructure.Camera.JsonCameraSettingsStore` (one JSON file, keyed by
     `ICameraDevice.Name` such as "ZWO ASI678MM" - deliberately not `Id`, a discovery-session-local
     index/serial not worth keying saved preferences on - see the interface's own doc comment).
     `CaptureViewModel` loads a camera's saved settings (if any) right after `ConnectAsync`
     succeeds and applies them before the live view starts, and saves again on every user-driven
     change via `PersistSettingsIfConnected` - guarded to skip both the load-and-apply pass itself
     and live Auto-readback ticks, so a continuously-varying auto-exposure value isn't rewritten to
     disk every frame; only what the user actually set is persisted.

     A `StatusBarViewModel`/`StatusBar.xaml` pair now exists too, mirroring RASTA's own
     StatusBarViewModel/StatusBar.xaml pattern exactly: a singleton, injected into
     `NavigationViewModel` and docked full-width at the bottom of `MainWindow.xaml`
     (`Grid.ColumnSpan="2"`, below both the sidebar and content area), for cross-cutting state any
     active stage view model can push into. Starts with just `CaptureViewModel` reporting the
     camera's actual capture rate (every frame arriving via `FrameCaptured`, not the throttled
     ~20fps preview redraw) once a second - future sections (mount status, etc.) should follow the
     same pattern once those exist.
   - a wide/ROI ("crop") view toggle, with the ROI vertically positionable, for framing during setup
     vs. the tighter view actually used while recording. *(Partially landed: a centred, width/height
     ROI - see the "What's real" note above - already drives the histogram, a dimming mask overlay,
     and what's cropped while recording; still outstanding is the actual view-toggle UX itself and
     vertical positioning of the ROI, rather than always-centred.)* The ROI's *height* has its own
     recommended value, independent of `ExposureCalculator`'s sizing (which governs width/fps) - see
     "ROI height" under astro4j above: `RecommendedRoiHeight` = dispersion (Å/pixel, from
     `SpectrumAnalyzer.computeSpectralDispersion`) converted from a wanted Å range (line + wings +
     continuum margin) into pixels, plus a small smile-curvature allowance - sized to the *smallest*
     value that reaches the reconstruction goal, since every extra row costs frame-rate/USB-bandwidth
     budget the scan-sampling fps target also needs
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
