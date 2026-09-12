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
astro4j - that list is now split into two groups: items still only *discussed* (not yet ported -
e.g. `DeepLineIdentifier`, `SolexVideoProcessor`) and items actually landed (`SpectralRay`/
`SpectrumParams`/`GeometryParams`/etc. from the Processing v1 work below, each also carrying its own
per-file header comment) - keep both groups accurate as more phases land real code.

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
  under Phase 4; gain/exposure, `UsbBandwidthPercent`, `DroppedFrameCount`, `PixelSizeMicrons` -
  sensor pixel size where the vendor SDK exposes one, feeding the camera-profile auto-add described
  below), `ISerWriter` (writes a live frame stream to a `.ser` file). References
  `CommunityToolkit.Mvvm` for `ObservableObject`/`RelayCommand` base classes on domain models that
  need change notification (e.g. live capture/session state), without pulling in WPF itself. Also
  carries the `Equipment` namespace's equipment records, adapted from astro4j's shape:
  `SpectrographProfile` (Sol'ex-equivalent to `SpectroHeliograph.java`: total angle, camera/
  collimator focal lengths, grating density/order, slit size), `TelescopeProfile` (name, focal
  length, aperture, energy rejection filter - partial translation of `Setup.java`, scoped to just the
  fields it shares with this record), and `CameraProfile` (name, pixel size). Telescope and camera are two separate
  library entries, not one combined record - unlike astro4j's `Setup.java` (telescope+camera+mount+
  site as one record), SolScan splits them because the camera isn't picked from a library by hand at
  all: `SolScan.App`'s `CaptureViewModel` auto-adds a `CameraProfile` the first time a given camera
  model connects (matched by `Label` against `ICameraDevice.Name` - the same "key by Name, not Id"
  reasoning as `ICameraSettingsStore`'s own doc comment), filling in `PixelSizeMicrons` from the
  connected hardware rather than asking the user to type it in; `TelescopeProfile` still is a normal
  hand-edited library entry. Neither carries `Setup.java`'s old mount/site fields - `Mount` was
  unused free text, and site geometry now lives on `AppSettings`/`Telescope.MountState` (see the
  mount-control work below) rather than a second, unsynced copy here. Unlike astro4j, where
  `SpectroHeliograph` and `Setup` are two independently-selected libraries with no persisted link
  between them, SolScan adds a fourth record, `EquipmentSetup` - a saved "this SHG is mounted on
  this telescope" combination, referencing `SpectrographProfile`/`TelescopeProfile` by `Id` (no
  camera reference - see above) - plus `IEquipmentLibrary`, the persistence abstraction for all four
  libraries (implemented by `SolScan.Infrastructure`'s `JsonEquipmentLibrary`, one JSON file per
  library under `%LocalAppData%\SolScan\equipment\`, mirroring astro4j's
  `SpectroHeliographsIO`/`SetupsIO`). Backs the Options view - see below - and Prepare's Equipment
  Setup picker. Also carries `CaptureMetadata`/`ICaptureMetadataStore` (`Capture` namespace,
  originally named `CaptureEquipmentMetadata`/`ICaptureMetadataWriter` before it grew beyond just
  equipment - see the Processing entries below): a snapshot (real field values, not IDs/live
  references, so it survives the source data later being edited/deleted/changing) of the SHG/
  telescope/camera used, the camera dial-in settings actually in effect, and the mount's pointing/
  site location at the moment recording started, written by `CaptureViewModel` alongside every `.ser`
  file so a future `SolScan.Processing` phase can read back what produced it.
- **SolScan.Infrastructure** — concrete implementations: an ASCOM Alpaca telescope client
  (`AscomAlpacaClient`/`AscomTelescopeMount`, ported from RASTA - see the mount-control entry below),
  a ZWO ASI/Altair camera wrapper (native SDK P/Invoke), a `.ser` file writer (`SerWriter`) - all
  implemented. `JsonEquipmentLibrary` (see above) and `JsonCaptureMetadataStore` (writes
  `CaptureMetadata` to a `<recording>.equipment.json` sidecar) are implemented too.
- **SolScan.Processing** — pure algorithms, no UI/hardware: the SHG reconstruction pipeline, ported
  natively from `SolexVideoProcessor`'s individual workflow steps rather than shelling out to
  `jsolex-cli` (see Phase 6 below for why that original plan was skipped). Spectral line detection,
  disk reconstruction, and ellipse fitting/geometry correction are all implemented; banding/jagging/
  distortion corrections and contrast enhancement are not yet.
- **SolScan.App** — WPF MVVM shell. `App.xaml.cs` is the single composition root - one
  `ServiceCollection` built once at startup (no scopes created afterward), same pattern RASTA uses.
  `MainWindow` binds to `NavigationViewModel.CurrentViewModel`, swapped via
  `NavigationViewModel.NavigateTo<TViewModel>()` (resolves from the DI container) - no
  router/framework, deliberately, same as RASTA. The left-hand nav sidebar (mirroring RASTA's
  `MainWindow.xaml`) has four buttons - Prepare/Capture/Process plus Options, docked to the bottom
  of the sidebar. `ProcessViewModel` remains a placeholder, just a `StatusText` string bound into its
  view. `PrepareViewModel`/`CaptureViewModel` are real - see the mount-control and camera-discovery/
  live-view/recording entries below. `OptionsViewModel` is real: it's SolScan's equivalent of
  JSolex's "Equipment" menu (`SpectroHeliographEditor.java` + `SetupEditor.java`) plus a General tab
  for app-wide settings that aren't equipment at all, embedded as five tabs (General / SHGs /
  Telescopes / Cameras / Setups - "SHGs" rather than "Spectrographs" as the tab header, though the
  underlying view-model/type names still say Spectrograph throughout) rather than separate modal
  dialogs. The four equipment tabs are
  each backed by their own list-view-model (`SpectrographLibraryViewModel`,
  `TelescopeLibraryViewModel`, `CameraLibraryViewModel`, `EquipmentSetupLibraryViewModel` under
  `ViewModels/Equipment`) editing `SolScan.Core.Equipment` records through `IEquipmentLibrary` - the
  Cameras tab is mostly populated by `CaptureViewModel`'s auto-add rather than typed in by hand (see
  above), though it still supports adding/editing/removing entries manually too, same as the other
  three. General is backed by `GeneralSettingsViewModel` editing `SolScan.Core.Capture.AppSettings` through
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
library (`SpectrographProfile`/`TelescopeProfile`/`CameraProfile`/`EquipmentSetup` +
`IEquipmentLibrary`, backed by `JsonEquipmentLibrary`) - Prepare's own Equipment Setup picker (see
the mount-control entry below) and Capture's camera auto-add (see the manual-capture entry below)
both real too. Also real: camera discovery/live view/manual SER recording (the first slice
of Phase 4, "Manual capture" below) - `ICameraProvider`/`ICameraDiscoveryService` in Core;
`SolScan.Infrastructure.Camera.Asi`/`.Camera.Altair` (hand-written P/Invoke against each vendor's
native SDK - the DLLs themselves live in the `SolScan.External` git submodule, not this repo, see
"Vendor camera SDK binaries: the SolScan.External submodule" below) and
`SolScan.Infrastructure.Capture.SerWriter`; `SolScan.Simulators`' `SimulatedCameraDevice`/
`SimulatedCameraProvider` (a hardware-free camera so the above works with zero hardware attached);
and `CaptureViewModel`/`CaptureView.xaml` wiring it all into a SharpCap-style live view (camera
picker, connect/play, gain/exposure/contrast sliders, histogram, start/stop recording). Also real:
camera-profile auto-add - the first time a given camera model connects, `CaptureViewModel.
ResolveCameraProfile` matches it against `IEquipmentLibrary.LoadCameras()` by `Label`/`Name` (adding
a new `CameraProfile`, filled in from `ICameraDevice.PixelSizeMicrons`, if none matches yet -
backfilling that field on an existing entry that's missing it, but never overwriting a non-null,
possibly hand-corrected value) - visible afterwards in Options > Cameras like any other entry there.
The resolved `CameraProfile` plus whatever `EquipmentSetup` is picked on Prepare (see the mount-
control entry above) are snapshotted by `CaptureViewModel.WriteCaptureMetadata` into a
`CaptureMetadata`, written via `ICaptureMetadataStore` to a `<recording>.equipment.json`
sidecar right when a recording starts - real field values, not IDs, so a later `SolScan.Processing`
phase can read back what equipment produced a recording independent of whether those library entries
still exist/are unchanged by then. Also real: per-camera-model settings persistence
(`ICameraSettingsStore`/`JsonCameraSettingsStore`) and a
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

Also real: a SharpCap-style Zoom dropdown (`CaptureViewModel.AvailableZoomOptions`/
`SelectedZoomOption`, a new `ZoomOption`/`ZoomKind` in `SolScan.App.ViewModels`) - Auto, Fit Width,
Fit Height, then fixed percentages 16%-800%, matching SharpCap's own list. The preview `Image` sits
in a `ScrollViewer` (`PreviewScrollViewer`) for pan scrollbars once zoomed past the available space.
The actual pixel width/height math lives in `CaptureView.xaml.cs` (`UpdateImageSize`), not the view
model - it needs the ScrollViewer's live viewport size, a View-layer concern - recomputed whenever
`SelectedZoomOption` changes, the ScrollViewer resizes, or a differently-sized `PreviewBitmap`
arrives. Important interaction with `FramePreview`'s downsampling (see below): a *fixed* percentage
means the user is deliberately pixel-peeping to check focus on the spectral line itself - the actual
point of this app - so `CaptureViewModel.ProcessPreviewFrame` passes the frame's own full size as
`FramePreview.Stretch`'s `maxDimension` whenever `SelectedZoomOption.Kind == ZoomKind.Fixed`
(yielding a downsample scale of exactly 1, i.e. true native resolution), rather than the default
960px-longest-side cap - zooming into an already-downsampled bitmap would otherwise just magnify a
blurry copy of detail that's already been thrown away, defeating the purpose. Auto/Fit Width/Fit
Height keep the cheap default, since their job is fitting the viewport, not inspecting native
pixels. This only runs on the already-offloaded preview thread, never the capture thread, so a
deliberate zoom-in can make the *preview* redraw feel slower but can't affect capture/recording fps.
`ComputeHistogramStats`' own sampling is untouched by zoom - it's a statistical summary, not a
spatial one, so the downsampled default remains accurate and cheap regardless.

Also real: Phase 2, mount control - `SolScan.Infrastructure.Telescope.AscomAlpacaClient`/
`AscomTelescopeMount` (ported from RASTA's own Alpaca client/`ITelescopeMount` implementation,
trimmed to SolScan's equatorial-only interface - no coordinate-mode detection/AltAz slewing) connect
over ASCOM Alpaca (REST, via the ASCOM Remote Server) to a real mount, registered as `ITelescopeMount`
in `App.xaml.cs`. `SolScan.Core.Telescope.MountState` is the live, poll-refreshed status singleton
(`SolScan.App.Services.MountService`, 1s cadence) that `PrepareViewModel`/`StatusBarViewModel` read
reactively; `PrepareView.xaml` is a real Connect/Disconnect + Park/Unpark + manual tracking-toggle UI
with live RA/Dec, alongside an editable, persisted Site Settings panel (latitude/longitude/elevation)
that reconciles against the mount's own site settings on connect (prompting to decide which side
wins when they disagree, mirroring RASTA's `SettingsViewModel.ConnectTelescopeAsync`). `PrepareViewModel`
is registered `AddSingleton` (unlike the other, still-transient stage view models) so this connection
state survives navigating away and back. The ASCOM Alpaca base URL/device number live in Options >
General (`AppSettings.AlpacaBaseUrl`/`AlpacaDeviceNumber`, same null-means-default convention as
`CapturesRootFolder`), read fresh at connect time rather than cached. A live poll call throwing
(`MountService.ConnectionLost`) is handled once, at the `App.xaml.cs` composition root - marks the
mount locally disconnected and shows an informational `MessageBox`, deliberately without RASTA's own
capture-cancel/forced-navigation coupling, since SolScan's capture pipeline doesn't depend on mount
state yet (that's Phase 5).

Also real: Prepare's Equipment section - an `EquipmentSetup` picker (`PrepareViewModel.
AvailableEquipmentSetups`/`SelectedEquipmentSetup`, loaded from `IEquipmentLibrary`, reloadable via a
`RefreshEquipment` command since `PrepareViewModel`'s own singleton lifetime means it isn't
naturally re-constructed on navigating back here after an Options edit) resolving to a read-only
SHG/telescope label pair for confirmation. The picked Setup's `Id` is persisted on
`AppSettings.SelectedEquipmentSetupId`, read fresh by `CaptureViewModel` when a recording starts to
build that recording's `CaptureMetadata` (see the manual-capture entry below for the camera
side of that same metadata). This is a single global "current rig" choice, not per-`TelescopeProfile`
site data - `TelescopeProfile` carries no site fields at all (unlike astro4j's `Setup.java`), so
there's no risk of it disagreeing with `AppSettings`' own site geometry.

Also real: a pop-out, modeless Hand Control window (`Views/HandControlWindow.xaml`/
`ViewModels/HandControlViewModel.cs`) for manually jogging the mount - a "Hand Control…" button on
the Capture view (not Prepare - the user will typically be watching the live preview while jogging),
enabled only once the mount is connected. Visually modelled on GSServer's `HandControlV`/
`HandController` (`C:\Source\Repos\JPhilC\GSServer`) - a compass of Up/Down/Left/Right buttons around
a central Stop, plus a vertical 1-8 speed slider - but GSServer *is* the ASCOM driver talking to a
mount's motor controller directly, so only its UI *layout* carries over; the actual mechanism is
`ITelescopeMount`'s new manual-hand-control primitives (`GetMaxSlewRateDegPerSecAsync`/
`MoveAxisAsync`/`AbortSlewAsync`, backed by Alpaca's own standard `axisrates`/`moveaxis`/`abortslew`
endpoints in `AscomTelescopeMount`), not a port of GSServer's internal motor-timing code. The 1-8
speed levels themselves *are* a direct port, though: traced through GSServer's
`SkyServer.SetSlewRates`/`HcMoves`, each level is a fixed fraction of the mount's own maximum slew
rate - 0.34%, 0.68%, 4.7%, 6.8%, 20%, 40%, 80%, 100% for levels 1-8 (`HandControlViewModel.
SpeedLevelFractions`), defaulting to level 7 (GSServer's own default). Where GSServer gets "max rate"
from a user-configurable setting (defaulting to 3.5°/s), SolScan queries it live from the mount's own
Alpaca `AxisRates` instead (same "ask the hardware, don't assume" ethos as `ICameraDevice.
SupportedBinning`/`PixelSizeMicrons`), falling back to GSServer's own 3.5°/s default if that fails.
Directional buttons jog only while held (`Mouse.Capture` on press/release so a drag off the button
before releasing still stops it), and closing the window - even via Alt+F4 mid-press - always calls
`AbortSlewAsync` plus zeroes both axes as a safety net, so it can never leave a motor running.

Also real: Phase 3's first slice, "Find Sun" - a `SolScan.Core.Astronomy.SunPosition` low-precision
analytic solar ephemeris (Meeus ch. 25, ~0.01° accuracy 1950-2050, geocentric - the Sun's parallax is
below this algorithm's own error margin, so site lat/long/elevation add nothing) plus a "Find Sun…"
button on the Capture view (`CaptureViewModel.FindSunAsync`) - lives there, not Prepare, since the
camera-fine-tune step needs whatever camera is already live in that view. Slews to today's computed
RA/Dec first (no lead-offset yet - that needs Phase 4's still-outstanding exposure/scan-speed
calculator to know how far "ahead" means, so it's deferred rather than guessed at); then, only if a
camera is actually live, offers (`MessageBox`) to fine-tune pointing using it. The fine-tune itself is
a simple brightness hill-climb, not yet the full two-phase spiral-search-then-hill-climb the build
plan below still describes: `ClimbAxisAsync` nudges one axis at a time (`ITelescopeMount.MoveAxisAsync`
pulses, ~1% of the mount's own max slew rate, then a finer ~0.3%-of-that second pass) while total
live-preview frame brightness (`FramePreview.ComputeHistogramStats.AverageValue`, sampled off the
existing throttled preview pipeline - see `_lastFrameAverageBrightness`) keeps improving, backing off
the final non-improving step so each axis ends up at its peak rather than one step past it. Compares
brightness *relatively* (±0.5%), not by a fixed absolute delta - Mono16's raw average sits ~256x
higher than Mono8's for the same scene, so a fixed threshold would be wrong for one format or the
other. No zero-signal spiral-search phase - the ephemeris slew is assumed to already land the Sun
somewhere in frame - and no dedicated low search-phase exposure/gain; both remain real gaps versus the
full design below. Once the fine-tune finishes (whether or not it found any improvement), offers
(`MessageBox`) to sync the mount's pointing model via the new `ITelescopeMount.SyncToCoordinatesAsync`
(Alpaca's `synctocoordinates`, `AscomTelescopeMount` - instant, no physical motion, unlike
`SlewToCoordinatesAsync`) - a quick substitute for a manual star-alignment routine, since the Sun's
position is already known precisely from the ephemeris. Synced to a *freshly recomputed*
`SunPosition.GetApparentRaDecJNow(DateTime.UtcNow)` at that moment, not the value from the top of this
method (fine-tuning can take long enough for the real position to have moved meaningfully) and
deliberately not `ITelescopeMount.GetCurrentPositionAsync()`'s own readback - syncing the mount to its
own existing belief about where it's pointed would be a no-op, since that belief already differs from
the truth by whatever error a sync is meant to correct. `IsFindingSun` gates re-entry and disables
live-view toggle/disconnect/start-recording for the duration, since the fine-tune loop depends on the
live view staying exactly as it is mid-run.

A standalone "Sync" button sits alongside it (`CaptureViewModel.SyncMountAsync`, sharing the same
`SyncMountToSunPositionAsync` helper `FindSunAsync` uses) - the manual counterpart, for when the user
aligns the Sun in the live preview themselves (e.g. via Hand Control) rather than through the automatic
fine-tune, and just wants to sync to that alignment. Gated on the mount being connected only - not on
a camera being live, since it doesn't look at the preview itself at all, just recomputes the ephemeris
and syncs.

`AscomTelescopeMount.SlewToCoordinatesAsync` also now switches tracking on first if it isn't already
(see `ITelescopeMount.SlewToCoordinatesAsync`'s own doc comment) - found via `FindSunAsync` landing a
real mount motionless at its previous position when tracking was off, since most mounts refuse (or
silently no-op) an equatorial slew in that state, same as a person would just switch tracking on by
hand before a goto.

Also real: Processing's first slice - a manual SER file picker on the Process view, ported/native
code rather than a stub (see Phase 6/7 below, which now skips the `jsolex-cli` shell-out step
entirely and starts native porting straight away). `SolScan.Core.Capture.ISerReader`/
`SolScan.Infrastructure.Capture.SerReader` are the read-side counterpart to the existing
`ISerWriter`/`SerWriter` - same 178-byte-header layout, byte-for-byte inverse of what `SerWriter`
writes, but deliberately tolerant of real-world files it didn't produce (verified against an old real
Sunscan capture: `PixelDepth` isn't assumed to be exactly 8 or 16, and a missing per-frame timestamp
trailer - which that Sunscan file genuinely doesn't have - falls back to `DateTime.MinValue` per
frame rather than throwing). `ICaptureMetadataStore` (then still named `ICaptureMetadataWriter`)
gained a `TryRead` alongside its existing `Write`, so a `.equipment.json` sidecar can be read back
the same way it's written (never throws -
`null` on a missing or corrupt sidecar). `ProcessViewModel.BrowseForSerFile` wires both together:
pick a `.ser` file, see its header (dimensions/bit depth/frame count/recorded time) and its equipment
sidecar's SHG/telescope/camera labels if one exists alongside it. No reconstruction (spectral line
detection, geometry correction, disk reconstruction, etc.) exists yet - this is groundwork only, and
processing is entirely manual (pick a file, inspect it) rather than kicked off automatically when a
capture finishes, which is real future work, not yet built.

Also real: enough process-parameters groundwork to make a "which images to generate" checklist mean
something, even before a real pipeline exists to act on it - added as three new Options tabs (Process
Parameters/Image Enhancement/Image Selection) rather than a separate modal dialog like JSolex's own
"Process parameters" dialog, since SolScan persists one global default the same way JSolex's dialog
does on every OK anyway, and that fits Options' existing tabbed layout directly. `SolScan.Core.Processing`
carries the ported types: `SpectralRay` (JSolex's 12 predefined lines + "Other"), `SpectrumParams`
(line/detection mode/pixel/Doppler/continuum shift), a deliberately trimmed `GeometryParams`
(rotation/autocrop/fixed-width/mirror flags only - forced tilt/XY-ratio overrides and other
Advanced-tab fields are still excluded, now that ellipse fitting is real, as deliberately deferred
Advanced-tab UI surface rather than a blocked dependency; `spectrumVFlip` is
excluded because it's already captured at the equipment level, `SpectrographProfile.SpectrumVFlip`),
`ContrastEnhancementMode` (just the method choice - Auto/CLAHE/CLAHE2/AutoStretch - not their tuning
parameters), `RequestedImages` (the 5 Basic Images kinds only - Advanced Images/Debug/scripts/presets
are all still out of scope), and the top-level `ProcessParams` + `IProcessParamsStore`
(`JsonProcessParamsStore`, one JSON file under `%LocalAppData%\SolScan\`, same single-record shape as
`IAppSettingsStore`). Because all three new Options tabs edit different slices of that one shared
record, they don't each save through the store independently the way the equipment tabs do -
`OptionsViewModel` loads `ProcessParams` once, seeds all three child view models from it, and
reassembles + saves one new record when Save is clicked (see `OptionsViewModel`'s own doc comment).
Also real: `SolScan.Core.Processing.ProcessingLocations.GetOutputFolder` - the fixed convention that
processing output goes in a folder next to the source `.ser` file, named after it.

Also real: `CaptureMetadata` (`Capture` namespace, renamed from `CaptureEquipmentMetadata` -
`ICaptureMetadataWriter` similarly renamed `ICaptureMetadataStore` - once it grew beyond just
equipment) now also snapshots what a recording's `.equipment.json` sidecar genuinely couldn't say
before: `CameraSettingsUsed` (a `CameraSettings` snapshot of the actual Gain/Exposure/Binning/output
format/ROI/contrast dial-in at the moment recording started - `ICameraSettingsStore` only remembers a
camera *model*'s current settings, not what a specific past recording used) and `MountPointing` (a
new `MountPointingSnapshot` - RA/Dec plus site lat/long/elevation - read from the already
poll-refreshed `MountState` rather than a fresh Alpaca query, so it costs nothing extra at
recording-start time; null if the mount wasn't connected then). `CaptureViewModel.BuildCurrentCameraSettings`
is shared by both this and the existing per-camera-model settings persistence
(`PersistSettingsIfConnected`), so the two field lists can't drift apart. Also carries a `StudiedRay`
field (`SolScan.Core.Processing.SpectralRay`) - always null today, reserved for once Capture's live
line-identification overlay (Phase 4) can set it automatically rather than it only ever being picked
by hand in Options > Process Parameters. A sidecar written before any of this landed still reads back
fine - the new fields just come back null, same as any other field a given recording never had a
value for.

Also real: a genuine (not simplified/placeholder) processing pipeline - `SolScan.Processing.Shg`'s
first real content, and the biggest single port in the project so far. `IShgProcessor`/`ShgProcessor`
orchestrate: `FrameAverager` (averages frames whose mean intensity beats half the brightest frame's -
ported from `AverageImageCreator.computeAverageImage`, sequential two-pass rather than the original's
parallel-lane/sampled-max version) → `SpectralLineCurvatureDetector` (a real 2nd-order polynomial fit
to how the studied line's row position curves/"smiles" across the frame's width, with sigma-clipped
robust refitting and sub-pixel centroid refinement - method-for-method port of
`SpectrumFrameAnalyzer`'s curve-fitting pipeline; disk left/right border detection isn't ported, using
the full frame width instead - the original algorithm's own signature already treats borders as
optional, so this is a real supported fallback mode, not an invented shortcut) → `DiskReconstructor`
(the actual reconstruction - 5-tap Gaussian-weighted anti-aliased row extraction per frame, direct
port of `SolexVideoProcessor.processSingleFrame`). Verified against the user's real Sunscan capture
(2028×110, 1533 frames): detection lands on a genuinely curved line (row ~16 at the left edge, ~52 at
mid-frame, ~7 at the right edge - real "smile" distortion, not noise) in ~11 seconds. Produces real
`Raw`/`Continuum` output images (`Reconstruction` is saved as the same pixel data as `Raw` - in JSolex
it's actually a progressive *live-display* variant of the same reconstruction, not a separately
computed image, and SolScan has no live progress view yet to make that distinction meaningful).
`GeometryCorrectedProcessed` still needs contrast enhancement (a deliberately separate, not-yet-built
piece of work) - requesting it is reported back as "not yet implemented" rather than silently skipped
or faked; `GeometryCorrected` itself is now real too, see below. `SolScan.Processing` itself has zero file IO (returns in-memory
`ushort[,]` pixel buffers, native sensor range scaled up to a 16-bit container) - the actual PNG
encode/save (`raw.png`/`reconstruction.png`/`continuum.png`) happens in `ProcessViewModel` via WPF's
own `PngBitmapEncoder`/`PixelFormats.Gray16`, not `System.Drawing.Common` (GDI+'s 16-bit-grayscale
*save* path is a well-known reliability problem) - verified byte-for-byte correct via three
independent checks (the file's own IHDR chunk, WPF's own decoder via `FormatConvertedBitmap`, and a
completely independent GDI+ read-back) after an initial false alarm from decoder-side gamma/
color-management reinterpretation that turned out not to reflect the actual on-disk bytes. `Process`/
`Cancel` buttons on the Process view mirror `CaptureViewModel.FindSunAsync`'s busy-flag shape
(`IsProcessing`, disables Browse mid-run) plus real `CancellationToken` support (worth having given a
650MB+ file can take a while) that `FindSunAsync` itself doesn't have.

Also real: ellipse fitting and geometry correction, producing a genuine `GeometryCorrected` image
(not a placeholder) - the second-biggest port in the project so far, landing all of
`SolScan.Processing.Math.Ellipse`/`EllipseRegression`/`GeometryTransform` and
`SolScan.Processing.Shg.DiskEdgeDetector`/`BackgroundNeutralizer`/`ImageStatistics`/
`GeometryResampler`/`DiskCropper`/`DiskGeometryCorrector`. `EllipseRegression` is astro4j's
Halir-Flusser direct least-squares ellipse fit, but its eigensystem solve is re-derived from scratch
as a closed-form 3x3 characteristic-cubic solver (`SolScan.Processing.Math.Matrix3x3`) rather than
porting astro4j's own Apache-Commons-Math-backed `DoubleMatrix` - SolScan.Processing has no such
dependency and the fixed 3x3 case doesn't need a general eigendecomposition library. Likewise,
`BackgroundNeutralizer`'s 6-term (`1, x, y, x², y², xy`) background-model fit is solved via a new
`SolScan.Processing.Math.LinearSystem` (plain Gaussian elimination) in place of Apache Commons Math's
`OLSMultipleLinearRegression`. `DiskEdgeDetector` is a method-for-method port of
`EllipseFittingTask`'s sample-finding pipeline (blur → background neutralization → contrast stretch →
threshold-crossing scan, sub-pixel interpolated, in both directions → outlier filtering → decimation →
iterative refit) - one step was found to be genuinely dead code in the original (a contrast-boost
squaring step that mutates an array nothing downstream reads, confirmed by tracing `ImageMath.convolve`
always allocating a new output buffer) and is skipped rather than faithfully reproduced as a no-op,
noted in `DiskEdgeDetector`'s own header comment. `DiskGeometryCorrector` applies the user's
mirror/rotation choice first (exact pixel permutations, ported from `SolexVideoProcessor`'s own
`maybePerformFlips`/`maybePerformRotation` - without the `rotateLeft` re-orientation step that precedes
them in the original, since SolScan's own `DiskReconstructor` output doesn't need it - see
`DiskGeometryCorrector`'s own header comment for why), then fits the disk edge, warps it circular via a
separable Catmull-Rom resample (`GeometryResampler`, ported from `GeometryUtils.
applyGeometryCorrection`), then autocrops per `AutocropMode` (`DiskCropper`, ported from `Cropper.
cropToSquare`/`cropToRectangle` - using the ellipse re-expressed in the *corrected* image's coordinate
system via an analytic conic transform, `GeometryResampler.ComputeCorrectedCircle`, not the
pre-correction one - confirmed against astro4j's own `Crop`/`AbstractFunctionImpl.getEllipse`, which
resolves the cropping ellipse from the image's own post-correction metadata rather than the value
originally passed to `Crop`'s constructor). A failed fit (too few clear disk-edge samples) throws
rather than silently skipping or faking the image, matching `FrameAverager`'s own "no frames exceeded
the brightness threshold" stance on a similarly degenerate source. `ShgProcessingResult` gained
`DetectedTiltDegrees`/`DetectedXyRatio` (surfaced in `ProcessViewModel`'s status text) - astro4j's own
`GeometryDetectedEvent` figures; SolScan has no richer results panel yet (see Phase 6 below) but the
values cost nothing extra to carry. Covered by `EllipseRegressionTests` (the core math, against known
circles/ellipses/tilts) and `DiskGeometryCorrectorTests` (the full pipeline, against a synthetic
elliptical "disk" reconstructed from a synthetic SER file, including a low-dynamic-range variant - see
next).

A real bug was found and fixed against the user's own old Sunscan capture (the geometry-corrected
output initially came back still visibly tilted/un-circularized, not actually corrected):
`DiskEdgeDetector`'s prepare step only stretched contrast to fill `[0, maxPixelValue]` *after* the
background-neutralization loop, matching astro4j's own ordering - but that capture's raw reconstructed
pixel values occupy well under 10% of the full 16-bit range (roughly 900-3500 out of 65535, a
low-gain/low-contrast real recording), and `ImageStatistics.EstimateBackgroundLevel`'s histogram always
bins over the *full* `[0, maxPixelValue]` range regardless of what part of it the data actually
occupies - so with real signal crammed into the first few histogram buckets, it returned a wildly
overestimated background level. The neutralization loop then kept subtracting a shrinking-but-still-
substantial fraction of the *signal itself* every iteration (confirmed by tracing each of the 16
iterations directly against the real file: the image's average value decayed geometrically, never
converging within the loop's own 2% threshold) and wiped out large regions of the image entirely by the
end - `DiskEdgeDetector.Prepare` now stretches *before* neutralizing too, not just after, which fixed
it (confirmed the same way: re-traced against the same real file, no more zeroed-out regions, and the
detected tilt went from an implausible -6.6° to a plausible 120.5° matching the visible disk edge to
within single-digit pixels at every column checked). `DiskGeometryCorrectorTests` gained a permanent
regression test reproducing this with a synthetic disk confined to a similarly narrow value band. Real
remaining limitation, not yet fixed: that same real capture's brightness falls off sharply toward one
side of the frame (a genuine property of the recording, not an artifact), so the disk-edge sample cloud
still doesn't fully reach the low-contrast side - the fit uses only part of the true disk edge, giving
a correctly-*oriented* but not fully-sized correction for that particular file. A single global
sensitivity threshold (astro4j's own design, `FindSamplesUsingDynamicSensitivity`) can't adapt to
strongly non-uniform contrast within one frame; fixing that would need a real design change (e.g.
per-region sensitivity), not another one-line fix, so it's left as a known gap.

Also real: a WiX installer and an automated GitHub release pipeline, both mirroring RASTA's own
`Setup`/`Bundle` split (`RASTA.Setup`/`RASTA.Bundle`, `scripts\Build-Release.ps1`) with several
SolScan-specific additions. `SolScan.Setup` (`Package.wxs`) publishes `SolScan.App`
(framework-dependent, win-x64) and harvests the publish output wildcard-style into an MSI, with a
Start Menu *and* Desktop shortcut both created unconditionally on install (no opt-out checkbox,
matching RASTA) - the Desktop shortcut is there from the very first install, not added later.
`SolScan.Bundle` (`Bundle.wxs`) chains that MSI behind two downloaded prerequisites via WiX Burn:
the .NET 10 Desktop Runtime (x64) - same `netfx:DotNetCoreSearch`-gated pattern as RASTA - and,
new versus RASTA, the Microsoft Visual C++ x64 Redistributable, gated on a `util:RegistrySearch`
of the well-known VC++ 2015-2022 runtime detection key so the ~25MB download is skipped on a
machine that already has one. The VC++ Redist is there because ZWO's `ASICamera2.dll` and
Altair's `altaircam.dll` are native MSVC binaries and a missing runtime is a known cause of a
camera silently failing to load on a clean install - unlike RASTA's RTL-SDR/libusb dependency,
which needed no equivalent. It's chained as a real installer rather than xcopy-deployed as loose
DLLs (see "Vendor camera SDK binaries" below for why that's the one piece of N.I.N.A.'s own
approach this project didn't copy). `Directory.Build.props` (new - SolScan had none before) is the
single `<Version>` every project and the bundle's own `Bundle/@Version` read from, exactly as in
RASTA. `scripts\Build-Release.ps1` is a close port of RASTA's own script (same
version-from-props/build/copy-to-`Releases\`/`WIX0350`-retry shape), with one addition: a
non-fatal warning if either camera DLL is missing from the `SolScan.External` submodule at build
time, so a release that silently lacks real-hardware support isn't produced unnoticed.

Genuinely new versus RASTA (which has no CI/release automation at all - its own releases are a
purely local, by-hand `Build-Release.ps1` run plus a manually-maintained `ReleaseNotes.md`):
`.github\workflows\release.yml` publishes an actual GitHub Release with `SolScan-Setup-<version>.exe`
attached, triggered by pushing a `vX.Y.Z` tag (or manually via `workflow_dispatch`). It runs on a
plain GitHub-hosted `windows-latest` runner - no self-hosted machine to register or keep online,
now that the camera SDK DLLs live in their own `SolScan.External` submodule rather than needing to
already be sitting on whichever machine builds the release (an earlier version of this workflow
*did* require a self-hosted runner for exactly that reason, before the submodule existed - see
"Vendor camera SDK binaries" below for the full story). Checking out a private submodule still
needs its own credential though, since the default `GITHUB_TOKEN` only covers this repo - the
checkout step's `token` is a fine-grained PAT (`SOLSCAN_EXTERNAL_PAT` repo secret, scoped to just
`Contents: Read` on `JPhilC/SolScan.External`) rather than the default token, and `submodules:
recursive`/`lfs: true` pull the submodule and its real LFS-tracked binary content in the same
step. The workflow fails fast if the pushed tag disagrees with `Directory.Build.props`, warns (but
doesn't fail) if either camera DLL is absent from the checked-out submodule, runs
`Build-Release.ps1`, then hands the release notes and installer to `softprops/action-gh-release`
(a community action, chosen over shelling out to the `gh` CLI since that isn't installed on this
machine and the action needs only the workflow's own default `GITHUB_TOKEN`) - its release body is
extracted directly from `ReleaseNotes.md`'s matching `## vX.Y.Z` section, so that file is the
single source of truth for both the local record and the published release description, never
typed twice. `ReleaseNotes.md` itself is seeded with a `v0.1.0` entry summarizing current status;
add a new `## vX.Y.Z` section (and bump `Directory.Build.props`) before tagging each future
release.

**Vendor camera SDK binaries: the `SolScan.External` submodule.** ZWO's `ASICamera2.dll` and
Altair's `altaircam.dll` originally lived flat in `SolScan.Infrastructure\` itself, gitignored -
so an end user's *installer* never needed them sourced separately (per above), but anyone
*building* SolScan (including a release build) had to have manually downloaded and dropped each
one in first, and no CI runner could ever produce a real-hardware-capable build at all. Modelled
directly on how N.I.N.A. solves the identical problem for a much longer camera-vendor list: its
own public [`nina.external`](https://github.com/isbeorn/nina.external) submodule holds every
vendor SDK binary it supports, Git-LFS-tracked, referenced unconditionally from `NINA.csproj`'s
plain `<Content Include="External\x64\...\XXX.dll">` items - no gitignore, no `Condition="Exists"`
guard, because `git clone --recurse-submodules` guarantees the file is always there. SolScan now
has its own equivalent, `SolScan.External` (a separate repo, added as a git submodule at
`SolScan.External\x64\ASI\`/`SolScan.External\x64\Altair\`, LFS-tracked via its own
`.gitattributes`) - but **private**, unlike NINA's public one: each vendor's actual redistribution
terms haven't been individually confirmed (the same caveat the old flat-file READMEs already
carried), so the more conservative default was chosen until that's actually checked, not because
the mechanism itself needs to differ. `SolScan.Infrastructure.csproj`'s two `<None Include>` items
now point at `..\SolScan.External\x64\<Vendor>\<dll>` with `Link` flattening them onto the output
folder (P/Invoke's DllImport search doesn't look in subfolders) - still `Condition="Exists(...)"`
guarded, unlike NINA's unconditional version, because the submodule as committed is currently
*empty* (each vendor folder holds only its own README saying where to source the real DLL - see
`SolScan.External\README.md`) - once it's actually populated the condition is simply always true,
nothing else needs to change. The one deliberate divergence from N.I.N.A.'s own approach: NINA
also ships the VC++ Redistributable's own runtime DLLs as loose xcopy-deployed files inside that
same submodule, rather than chaining an installer - this project looked into doing the same
(the redistributable's own `vc_redist.x64.exe` supports a documented `/layout` extraction switch
for exactly this purpose) but couldn't reliably complete that extraction in this environment (no
interactive Windows install session, no `7z` available to unpack the bootstrapper directly), so
`SolScan.Bundle` still chains the real `vc_redist.x64.exe` installer instead (see the Bundle.wxs
entry above) - worth revisiting by hand later if xcopy deployment of those specific runtime files
turns out to matter.

Placeholder: within Phase 4 itself: no exposure/fps calculator, no wide/ROI *view
toggle* (see the centred ROI note above for what's real there instead), no camera-focus/collimator-
focus aids, no live line-ID overlay yet (see the Phase 4 sub-items below). Phase 2's mount control also
doesn't yet cover Az/Alt slewing or custom tracking rates/FindHome/AtHome; Phase 3's ephemeris slew
doesn't yet include a lead-offset, and its fine-tune is the simple hill-climb described above, not yet
the full spiral-search-then-hill-climb design - all later phases per the build plan below.

## Phased build plan

1. **Scaffolding** *(done)* — solution + projects, DI composition root, nav shell.
2. **Mount control** *(done)* — port RASTA's Alpaca client into `SolScan.Infrastructure`, implement
   `ITelescopeMount`, wire a real Prepare-stage connect/disconnect/site-settings UI.
3. **Find the sun** *(core landed)* — `SolScan.Core.Astronomy.SunPosition` (low-precision analytic,
   ~0.01° accuracy) plus `CaptureViewModel.FindSunAsync`/its "Find Sun…" button, slewing to today's
   computed position, then (only if a camera is live) a simple brightness hill-climb fine-tune, then
   an optional Alpaca sync - see the "Also real" note above for what's actually implemented and what
   of the below remains outstanding. Still missing: the lead-offset itself (needs Phase 4's exposure/
   scan-speed calculator to know how far "ahead" means), and the apparent-disk-size/declination part
   that calculator is meant to share with this same ephemeris rather than duplicating it.
   - **Visual fine-centering, full design** (the simple hill-climb above is a first cut of this, not
     the complete version): an SHG only ever sees whatever light passes through its slit, so total
     brightness in the live frame is a direct, unimodal proxy for how well the sun's disk currently
     overlaps the slit - no risk of locking onto the wrong source, since nothing else in a daytime sky
     is remotely as bright as the sun. Two phases: an expanding-spiral/raster search first, in case
     the ephemeris slew leaves the frame at zero signal (gradient-climbing needs *some* nonzero signal
     to follow - it can't recover from a flat-zero frame on its own - not yet built, since the
     ephemeris slew is assumed to already land the Sun in frame); then a hill-climb/P-controller phase
     nudging both mount axes to maximize total frame brightness once signal exists (the part that's
     landed, as a simpler per-axis step-and-check rather than a true P-controller). Still needed: a
     deliberately low search-phase exposure/gain, separate from the capture-ready exposure the Phase 4
     calculator recommends, so the signal doesn't saturate and flatten out near the peak. Keep tracking
     on throughout (ideally already at a rate accounting for the sun's faster-than-sidereal motion) so
     the target doesn't drift away mid-search. Refines pointing precision only - the ephemeris still
     supplies the lead-offset direction/distance, which brightness-peaking alone can't tell you. No
     prior art for this one in RASTA/sunscan-backend/astro4j - genuinely new to SolScan, not a port.
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
     the sliders below are hand-tuned; needs `SpectrographProfile`/`TelescopeProfile` data from Core
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

     `CaptureViewModel.AvailableBinningOptions` is repopulated per-connect via
     `AddMissingBinningOptions`/`RemoveStaleBinningOptions` (add whatever's newly-supported first,
     only remove what's stale *after* `SelectedBinning` has moved to a value in the new set) rather
     than a plain `Clear()` + re-`Add()` - confirmed on real hardware that naively clearing first
     momentarily left the ComboBox's bound `ItemsSource` empty while `SelectedBinning` (a plain,
     non-nullable `int`) still pointed at the old value, and WPF's `SelectedItem` binding reacting to
     that by trying to push `null` back into a non-nullable `int` threw and showed up as the
     ComboBox's default red validation-error adorner on every single connect/Play.

     `CaptureViewModel._recordingLock` covers the *actual* `SerWriter.WriteFrame`/`Close`/`Dispose`
     calls now, not just the `_activeWriter` reference grab - `SerWriter` has no synchronization of
     its own (`_frameTimestampsUtcTicks`, a plain `List<long>`, is `Add`-ed to by `WriteFrame` and
     `foreach`-enumerated by `Close`), so with the narrower lock a frame arriving on the capture
     thread at the same moment Stop Recording is pressed (UI thread) could call `WriteFrame`
     concurrently with `Close`, throwing `InvalidOperationException: Collection was modified;
     enumeration operation may not execute` - hit on real hardware. Widening the lock to cover the
     I/O calls themselves only adds contention once per recording (when it stops), not per frame, so
     it doesn't touch the throughput work above.

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
     ROI - see the "What's real" note above - is now a real hardware setting (`ASISetROIFormat`)
     driving what the camera itself reads out, the histogram, the preview, and what's recorded, all
     directly, with no separate crop/mask step; still outstanding is the actual view-toggle UX
     itself and vertical positioning of the ROI, rather than always-centred - there's currently no
     way to see the full sensor at all once a narrower ROI is applied, since the camera simply never
     delivers that data.)* The ROI's *height* has its own
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
     for the Sol'ex + ASI678MM combination, not Sunscan's atlas/constants. Once this exists, it should
     also be able to *drive* `SpectrumParams.Ray` (Phase 6, Options > Process Parameters) rather than
     that staying a manual-only pick - whichever labeled line sits nearest the ROI's vertical centre
     is the one actually being studied, so the overlay could set it automatically instead of asking
     the user to also tell SolScan what it just showed them - not yet wired, noted for when the
     overlay itself is built
5. **Automated acquisition** — background capture pipeline (`System.Threading.Channels`), auto-detect
   the disk entering/centred on/leaving the slit from the live preview (port `focus_analyzer.py`'s
   edge-detection technique), tie into step 3's slew-ahead-and-drift logic as one "Capture" action
   requiring no further manual intervention.
6. **Processing v1 (native, incremental)** — originally planned as a `jsolex-cli` shell-out step
   first; skipped in favour of porting/translating JSolex's own Java straight into C#, since the
   shell-out would've just been thrown away once native code landed anyway. First slice (real - see
   "What's real vs. placeholder" above): `ISerReader`/`SerReader` (read-side counterpart to
   `ISerWriter`), `ICaptureMetadataStore.TryRead`, and a manual SER file picker on the Process view
   showing header + equipment info for a chosen file - no actual reconstruction yet. Second slice
   (also real): process parameters - `SpectralRay`/`SpectrumParams`/`GeometryParams`/
   `ContrastEnhancementMode`/`RequestedImages`/`ProcessParams` in `SolScan.Core.Processing`, persisted
   via `IProcessParamsStore` and edited across three new Options tabs (Process Parameters/Image
   Enhancement/Image Selection) rather than a separate dialog, plus `ProcessingLocations.GetOutputFolder`'s
   fixed "output folder sits next to the source .ser file, named after it" convention - still no
   reconstruction, just enough of the parameter surface to make a future one's output meaningful.
   Third slice (real - see "What's real vs. placeholder" above for the full picture): `IShgProcessor`/
   `ShgProcessor` in `SolScan.Processing.Shg` - real spectral-line-curvature detection
   (`SpectralLineCurvatureDetector`, ported from `SpectrumFrameAnalyzer`) and real reconstruction
   (`DiskReconstructor`, ported from `SolexVideoProcessor.processSingleFrame`), producing real
   `Raw`/`Reconstruction`/`Continuum` output images from a Process button, verified against the user's
   real Sunscan capture. Fourth slice (also real, its own genuinely separate algorithm from
   line-curvature detection, as planned): ellipse fitting and geometry correction
   (`DiskEdgeDetector`/`DiskGeometryCorrector` and friends - see "Also real" above for the full list),
   producing a real `GeometryCorrected` image. `GeometryCorrectedProcessed` still needs contrast
   enhancement - deliberately deferred as its own next slice, reported as "not yet implemented" rather
   than faked. Also still needed: porting `DeepLineIdentifier`/`SpectralLineCatalog` for the Process
   stage's own line identification, and a results panel mirroring JSolex's two-part info view (detected
   line + geometry tilt/xyRatio) - `ShgProcessingResult.DetectedLinePolynomial`/`DetectedTiltDegrees`/
   `DetectedXyRatio` all exist but aren't shown anywhere richer than a one-line status-text summary yet.
7. **Automatic processing** — once a real `IShgProcessor` exists, kick it off automatically on its own
   background thread as soon as a capture finishes recording (rather than the current manual file
   picker), so a new capture can start immediately without waiting on the previous one's processing to
   finish. `CaptureViewModel.StopRecording` doesn't currently raise any "recording finished" event to
   hook this from - that's part of this phase's own work, not yet built.
8. **Polish** — output styles/palettes, dark/flat calibration, session/plan management,
   `SolScan.Simulators` fleshed out for offline dev/tests. The installer (WiX `Setup`/`Bundle`
   projects, chaining the .NET runtime and VC++ Redistributable) and an automated GitHub release
   pipeline are both done already - see "Also real" above - ahead of the rest of this phase,
   since they were requested directly rather than waiting for this phase's turn.

## Future ideas (not yet scheduled)

Captured here so they aren't lost, not yet assigned to a phase above or designed in any depth.

- **ASCOM-controlled lens cap / slit cover.** An SHG's slit sits at (or very near) a real focal
  point - exactly where the telescope concentrates the Sun's light and heat - so leaving it uncovered
  whenever a scan isn't actively in progress risks heat damage, the same reason Sol'ex users keep a
  physical cap on by hand between scans. ASCOM already has a standard device type for this -
  `CoverCalibrator` (Alpaca `covercalibrator`: `opencover`/`closecover`/`haltcover`, a `coverstate`
  property) - so this would likely be a new `SolScan.Core.Telescope.ICoverDevice` (or similar)
  implemented the same Alpaca-REST way as `ITelescopeMount`, rather than inventing a bespoke
  protocol. The interesting part isn't the device wrapper itself but *when* SolScan would drive it
  automatically - open just before a "Find Sun"/slew-and-scan sequence actually needs light through
  the slit, and close again afterward, on error, or on cancellation - a safety interlock tied into
  the Prepare/Capture lifecycle rather than a manual button only.
