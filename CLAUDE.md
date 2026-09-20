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

`ADDITIONAL-PERMISSIONS.md` (2026-09-14, prompted by Cedric Champeau's reply to a courtesy email about
SolScan - see below) additionally licenses all of SolScan's own code under Apache-2.0 too, at Phil
Crompton's discretion as its copyright holder, *except* the Sunscan-derived (GPL-3.0) portions, which
aren't his to relicense unilaterally. Not a change to SolScan's own overall license (still AGPL-3.0) -
a standing additional permission on top of it, so fixes/extensions to the astro4j-derived pipeline (or
anything else in SolScan without third-party GPL lineage) can flow back into JSol'Ex or any other
Apache-2.0 project without asking case by case. Prompted directly: emailed Cedric, Guillaume Bertrand
(sunscan-app/sunscan-backend, crediting the wider STAROS team), and this repo as a courtesy once the
astro4j sync above turned up real prior art relevant to SolScan's own roadmap (see
[[astro4j-sync-2026-09-14]] in memory) - Cedric replied pointing out AGPL-3.0 code can't flow back into
his own Apache-2.0 project without relicensing, which is correct (copyleft-to-permissive is a one-way
door; the ASF formally classifies AGPL as "Category X", banned from Apache projects). This file is the
fix - scoped to exclude the GPL-3.0 Sunscan material (which really isn't Phil's to grant) and phrased
as a standing permission for anyone relying on it, not just Cedric.

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
    mislabel an ambiguous stretch of spectrum. **Superseded as the design basis for this by
    `SpectralWindowIdentifier` below** (landed in astro4j after the above was written) - the
    `DeepLineIdentifier`-core-plus-`SpectrumBrowser`-rendering combination was always an assembly of
    two pieces not built for this together; astro4j has since shipped the purpose-built live/narrow-
    window equivalent directly.
  - **`SpectralWindowIdentifier`** (`jsolex-core/.../spectrum/SpectralWindowIdentifier.java`, new in
    astro4j's "identify spectral lines live while tuning the spectroheliograph" commit, 2026-09-13,
    v5.4.3→5.5.0) — a real, shipped answer to the exact problem SolScan's own still-unbuilt live
    wide-view line-ID overlay (Phase 4) describes: identifying which line is being looked at from a
    live, narrow capture window while the grating is being turned, rather than `DeepLineIdentifier`'s
    own whole-recorded-file design. Companioned by two new types: `WavelengthSolution` (the per-frame
    curvature-polynomial/pixel↔wavelength fit) and `TelluricTransmission` (a bundled NSO/Kitt Peak
    ground-based atmospheric-absorption atlas, `jsolex-core/src/nso/visatl-telluric.dat`) - added
    because telluric lines near H-alpha are as deep as the solar ones and were throwing off
    identification for anything observed through Earth's atmosphere; `DeepLineIdentifier` and
    `SpectrumBrowser` were both reworked in the same commit to share this same telluric-aware
    reference, so a future SolScan port of either should pull in the telluric atlas too, not just the
    solar one. Exposed as a real HTTP API on JSolex's own embedded server -
    `jsolex-server/.../SpectrumController.java`'s `POST /api/spectrum/identify` (raw pixel bytes +
    width/height/format, or an image/FITS file; optional `pixelSize`/`binning`/`instrument`/`average`
    query params; returns JSON: curvature polynomial, identified line + score/confidence, Å/pixel
    dispersion, and every line found in the window with its position/depth relative to frame centre;
    `DELETE` resets the running frame-average) - already consumed by a real capture-software
    integration, `scripts/sharpcap/jsolex_spectral_lines.py`, doing for SharpCap exactly what this
    project's own Phase 4 live-overlay item wants for SolScan's Capture view. Not yet acted on in
    SolScan as of this writing - two integration paths exist (a native C# port of
    `SpectralWindowIdentifier`/`WavelengthSolution`/`TelluricTransmission`, matching this project's
    established porting precedent and keeping SolScan self-contained; or driving JSolex's own server
    API from `CaptureViewModel`, far less work but a runtime dependency on a separate JSolex install/
    process) - deliberately deferred rather than picked, pending Phase 4's own turn.
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

**Always benchmark/compare performance in a Release build (`dotnet build -c Release`, or the built
`.exe` launched directly), never Debug (a plain `dotnet run`/Visual Studio F5 launch defaults to it).**
Confirmed concretely, not assumed: the exact same code processing the exact same real file measured
45.60s in Debug vs. 15.91s in Release - a 2.87x difference from build configuration alone, on top of
(and easily large enough to hide or invert) whatever an actual code change's own real effect is - see
the Process-pipeline-performance entries below for the full investigation this came out of.

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
  disk reconstruction, ellipse fitting/geometry correction, AutoStretch/CLAHE contrast enhancement
  (`SolScan.Processing.Stretching`, all three modes now including CLAHE2/multi-scale CLAHE),
  colorization (`SolScan.Processing.Color`), and the virtual eclipse/coronagraph view
  (`SolScan.Processing.Shg.Coronagraph`) are all implemented - v1's full image set, now that JSolex's
  own Basic/Advanced Images split (and everything beyond it) is deliberately out of scope, see the
  "Basic/Advanced Images split is gone" entry below; banding/jagging/distortion corrections are not
  part of that set at all.
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
  dialogs. (It briefly grew three more tabs - Process Parameters/Image Enhancement/Image Selection -
  before those moved onto the Process view itself; see that entry further down for where they live
  now and why.) The four equipment tabs are
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
picker, connect/play, gain/exposure/contrast sliders, histogram, start/stop recording).
`CaptureViewModel` is registered `AddSingleton`, same reasoning/precedent as `PrepareViewModel`'s own
doc comment above - it used to be transient, which meant navigating away from Capture and back
constructed a brand-new instance (a fresh `RefreshCameras()` call, itself creating new `ICameraDevice`
instances via discovery - see `AsiCameraProvider.Discover`) while the *previous* instance's
already-connected/streaming `ICameraDevice` was simply abandoned rather than disconnected: nothing
ever unsubscribed its `FrameCaptured` handler or called `DisconnectAsync` on it, so the old native
camera handle (and its capture thread) leaked on in the background - still pushing frame-rate updates
into the shared `StatusBarViewModel` - while the *new* `CaptureViewModel` the user actually saw looked
fully disconnected. Reported as "the camera gets disconnected when you click off Capture"; fixed by
the same singleton-lifetime treatment `PrepareViewModel` already got for the identical problem with
the mount connection. One accepted side effect, matching `PrepareViewModel`'s own already-established
trade-off: a camera plugged in after Capture's first visit won't appear in the picker until the
existing "Refresh" button is clicked, since `RefreshCameras()` now only runs once (at first
construction) rather than on every navigation. Also real: camera-profile auto-add - the first time a
given camera model connects, `CaptureViewModel.
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
is registered `AddSingleton` (like `CaptureViewModel`, unlike the still-transient `ProcessViewModel`/
`OptionsViewModel`) so this connection state survives navigating away and back. The ASCOM Alpaca base URL/device number live in Options >
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
`ContrastEnhancementMode` (the method choice - Auto/CLAHE/CLAHE2/AutoStretch - each method's own tuning
parameters, `ClaheParams`/`Clahe2Params`/`AutoStretchParams`, landed later the same day the panel below
moved onto Process - see that entry), `RequestedImages` (the 5 Basic Images kinds only - Advanced
Images/Debug/scripts/presets are all still out of scope), and the top-level `ProcessParams` +
`IProcessParamsStore` (`JsonProcessParamsStore`, one JSON file under `%LocalAppData%\SolScan\`, same
single-record shape as `IAppSettingsStore`). Originally edited across three Options tabs sharing one
Save button (since all three edit different slices of the one persisted record) - see the "Process
pipeline options move onto the Process view" entry below for where that editing surface moved to and
why the reassembly logic this paragraph used to describe now lives on `ProcessViewModel` instead of
`OptionsViewModel`. Also real: `SolScan.Core.Processing.ProcessingLocations.GetOutputFolder` - the fixed
convention that processing output goes in a folder next to the source `.ser` file, named after it.

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
by hand on the Process view's own Process Parameters panel. A sidecar written before any of this landed
still reads back fine - the new fields just come back null, same as any other field a given recording
never had a value for.

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
`GeometryCorrectedProcessed` is now real too (see the AutoStretch/CLAHE entry further down) -
`GeometryCorrected` itself is real, see below. `SolScan.Processing` itself has zero file IO (returns in-memory
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

Also real: contrast enhancement, producing a genuine `GeometryCorrectedProcessed` image for
`ContrastEnhancementMode.AutoStretch`/`.Clahe` (`.Auto` resolves to `AutoStretch`, except when
`SpectrumParams.Ray` is `CalciumK`/`CalciumH`, where it resolves to `CLAHE` instead - matching astro4j's
own `AUTO` exactly; this isn't image-analysis "detection" at all, just a check against whichever line is
already selected, so it needed no new infrastructure once someone asked for it by name) -
`SolScan.Processing.Stretching`'s
`AutoStretchStrategy`/`ClaheStrategy`/`GammaStrategy`/`RangeExpansionStrategy`/`Histogram`, wired into
`ShgProcessor.ApplyContrastEnhancement`. Investigating astro4j's real `ContrastEnhancement.AUTOSTRETCH`
turned up something the initial "AutoStretch first, it's the cheap one" plan had backwards: astro4j's own
`AutohistogramStrategy` (what `AUTOSTRETCH` actually runs) is not simpler than CLAHE - it *depends on*
CLAHE internally (two differently-tuned passes, one for the disk and one - "eclipse" in the original's own
naming - for the region outside it, so prominences get their own contrast treatment without the disk's
own brightness dominating the tile statistics), on top of iterative ellipse-aware background
neutralization, a gamma curve, and an asinh brightness-matching stretch anchored to a target disk midtone
(solved by bisection), finished with a cubic contrast S-curve that fades to identity via the ellipse's own
implicit conic equation so it doesn't crush prominences/the limb. Ported faithfully rather than
simplified, once that was known - the user's own call when asked, matching this project's established
"no shortcuts" porting precedent (ellipse fitting was the same size of undertaking). Two deliberate
simplifications, both because `ContrastEnhancementMode` has no tunable `AutoStretchParams` yet:
`adjustBrightness` is always true (the only value astro4j's own one real call site ever passes), and
`protusStretch` defaults to 0 - at that value the "expand" curve applied to the prominence pass is
provably the identity (three collinear points), so it's still ported (via an exact 3-point Lagrange
interpolation in place of astro4j's whole `ColorCurve`/`LinearRegression` regression-and-caching
machinery, since 3 points/3 coefficients has no residual to minimize either way) but short-circuited at
that value rather than assumed away. `BackgroundNeutralizer` gained a second fitting method,
`NeutralizeMasked` (astro4j's `backgroundModel`'s degree-2 case + its own `neutralizeBg` wrapper combined
into one, with the never-non-null-in-practice subtraction-exclusion ellipse parameter dropped), alongside
the existing `BlindNeutralize` used by `DiskEdgeDetector`'s ellipse-less first-fit - genuinely different
algorithms (sigma-clipped + ellipse-aware vs. threshold-sampled + blind), not a refactor of one into the
other. `Ellipse` gained `Translate`/`Rescale`/`BoundingBox` (needed for the disk-exclusion zones and the
contrast S-curve's own fade), and `DiskGeometryCorrector.Result` gained `CorrectedEllipse` - the disk's
ellipse re-expressed in the *final* (post-warp, post-crop) image's coordinates via
`GeometryResampler.ComputeCorrectedCircle` plus an `Ellipse.Translate` by the crop's own origin, since
AutoStretch needs to know where the disk actually sits in the specific image it's asked to stretch, not
where it sat before cropping. Covered
by `StretchingTests` (CLAHE keeps output in range and meaningfully spreads a low-contrast per-tile ramp;
AutoStretch handles the no-ellipse case without throwing, and measurably brightens a dim disk toward its
midtone target) and new `DiskGeometryCorrectorTests` cases (a real end-to-end `GeometryCorrectedProcessed`
for each of Auto/AutoStretch/Clahe against the existing synthetic elliptical-disk file). NOT YET VALIDATED
against a real capture - the synthetic disk these tests use is a clean, high-contrast one, unlike the real
low-gain Sunscan file the geometry-correction bug above was found and fixed against; worth checking once
better real test data exists (see that same file's "Why"/"How to apply" note in memory) whether
AutoStretch's own fixed internal thresholds (background threshold, gamma, the CLAHE tile/clip constants)
hold up as well as the geometry-correction fix did.

`ContrastEnhancementMode.Clahe2` (astro4j's "multi-scale CLAHE") landed the same session, right after -
`Clahe.applyMultiScaleClahe`/`computeTileSizesForImage`/`computeTileSizes`/`multiScaleClaheChannel`
(`SolScan.Processing.Stretching.MultiScaleClaheStrategy`): runs the already-ported `ClaheStrategy` several
times at different tile sizes (starting from one derived from the disk's own diameter via its ellipse -
or the image size if there's none - and halving down through up to 6 levels) and averages the results, a
much smaller port than AutoStretch turned out to be since it reuses `ClaheStrategy` entirely rather than
introducing new machinery. The original's `RGBImage` per-channel branch is dropped - SolScan.Processing
has no colour image type at all. `ShgProcessor`'s `NotSupportedException` for this mode is gone; all three
`ContrastEnhancementMode` values are now real, and `ShgProcessingResult.SkippedKinds` is currently always
empty as a result. Also landed at the same time: the `Auto` mode's own calcium-line check
(`SpectrumParams.Ray == CalciumK/CalciumH → CLAHE`, else `AutoStretch`, matching astro4j's `AUTO` exactly)
- prompted by the user asking about "calcium-line detection" as a future want, which turned out to
already be almost entirely built (`SpectralRay`/`SpectrumParams.Ray` both already existed) rather than
needing anything new; worth remembering that name could *also* mean the much bigger
`DeepLineIdentifier`/live line-ID overlay work (see the astro4j entry above), still unstarted - don't
assume which one a future request means without checking. Covered by two new `DiskGeometryCorrectorTests`
cases: `GeometryCorrectedProcessed` for `Clahe2` folded into the existing per-mode theory test, and a
dedicated calcium-routing test confirming `Auto` on `CalciumK`/`CalciumH` produces byte-identical output
to an explicit `Clahe` request (and different output from `AutoStretch`) on the same input. Two new
`StretchingTests` cover `MultiScaleClaheStrategy` directly (with and without an ellipse).

Also real: in-app explanatory tooltips across the three Process-related Options tabs (Process
Parameters/Image Enhancement/Image Selection) - prompted directly by the user wanting somewhere for
someone new to SHG processing (their own words: "people, like me, who are just learning") to learn the
background behind each setting, rather than needing to already know what a pixel shift or CLAHE is. A
new implicit `Style TargetType="ToolTip"` in `App.xaml` (`MaxWidth="340"` plus a `ContentTemplate`
wrapping the tooltip's own string content in a `TextBlock` with `TextWrapping="Wrap"`) makes every plain
`ToolTip="..."` string anywhere in the app wrap at a readable width instead of rendering as one very long
unwrapped line - WPF's own default `ToolTip` template doesn't wrap a plain string on its own. Every
meaningful control across the three views now carries one: what each `SpectralRay`/`LineDetectionMode`
choice means (and, honestly, that `DetectionMode` isn't actually wired into the pipeline yet - it's a
real parameter with no consumer, same for `DopplerShift`/`SwitchRedBlueChannels`), what a pixel shift
is and why it's measured relative to the line's own centre, what `RotationKind`/`AutocropMode`/mirror
flags do to the image, a full explanation of all four `ContrastEnhancementMode` values (what CLAHE
stands for, and concretely how CLAHE2 differs from plain CLAHE - several tile sizes averaged together
rather than one - a point raised directly in conversation before this landed), and what each of the five
`GeneratedImageKind` outputs actually represents, including which ones are still identical placeholders
(`Reconstruction` = `Raw`) or otherwise incomplete. Deliberately scoped to tooltips only, not a separate
glossary/help screen - agreed directly with the user as the lower-effort first step, discoverable exactly
where someone is already looking (hovering a confusing control) rather than a whole new piece of UI to
build and keep in sync. A dedicated glossary view, and/or a "?" info-button-plus-popup for even fuller
explanations, were both discussed and set aside for if tooltips alone turn out not to be enough.

Also real: **process pipeline options move onto the Process view.** Having used the Options-tab version
above, the user asked for Process Parameters/Image Enhancement/Image Selection to live directly on the
Process view instead - where they're actually used - as a right-hand dockable panel of Expanders (the
same `Expander`-grouping convention as `CaptureView.xaml`'s own right-hand panel), plus real CLAHE/
AutoStretch tuning parameters surfaced "like JSolex" (its own desktop UI, not just the method choice).
Planned via `EnterPlanMode` given the size (new domain types, a persistence-model change, and a WPF
layout pattern - a resizable/collapsible side panel - this app had never used before) - two scope
questions were asked and confirmed before implementing: the three tabs move out of Options entirely
(not duplicated), and edits auto-save as you go rather than through a Save button.

`SolScan.Core.Processing` gained `ClaheParams`(TileSize/Bins/Clipping)/`Clahe2Params`(Clipping)/
`AutoStretchParams`(Gamma/BackgroundThreshold/ProtusStretch) - astro4j's own tuning records, ported now
that they're actually exposed in the UI (`ContrastEnhancementMode`'s own doc comment used to say these
"aren't ported yet"). Each carries a `Default` matching the constant SolScan already used internally
(`ClaheStrategy.DefaultTileSize/DefaultBins/DefaultClip`, `MultiScaleClaheStrategy.DefaultClip`,
`AutoStretchStrategy.DefaultGamma/DefaultBackgroundThreshold/DefaultProtusStretch`) - duplicated as
literals rather than referenced directly, since `SolScan.Core` has no dependency on `SolScan.Processing`
(the same one-way layering every other Core/Processing split already respects). `ProcessParams` grew
three corresponding fields; `JsonProcessParamsStore.Load()` backfills them from each type's own
`Default` when reading an older file that predates them (a missing JSON property deserializes as
`null` regardless of the record's non-nullable declared type, and a nested-object default can't be a
compile-time-constant optional-parameter default the way `AppSettings`' own primitive fields use -
see that record's doc comment for the pattern this genuinely can't reuse). `AutoStretchStrategy.Stretch`
gained the validation guards astro4j's own `AutohistogramStrategy` constructor has and this port
originally skipped (gamma > 1, background threshold in (0, 1], prominence stretch >= 0) - now that
these are free-text user input for the first time, worth a clear thrown `ArgumentOutOfRangeException`
rather than silently-wrong output. `ShgProcessor.ApplyContrastEnhancement` now takes the whole
`ProcessParams` rather than just the mode/studied-ray, threading the real tuning values into
`AutoStretchStrategy`/`ClaheStrategy`/`MultiScaleClaheStrategy` instead of their own hardcoded defaults.

`SolScan.Core.Capture.AppSettings` gained four more persisted UI-state bools, same
`CaptureSettingsExpanded`-shaped optional-parameter-with-default fields as Capture's own Expanders:
`ProcessOptionsPanelExpanded` (the whole panel's dock/undock state) plus one per Expander inside it.
`ImageEnhancementViewModel` gained the seven tuning properties plus a `ResetToDefaultsCommand`; CLAHE's
`TileSize`/`Bins` are `ComboBox`es over JSolex's own fixed discrete option lists
(`ImageEnhancementPanel.java`: tile size from 8 to 1024, bins from 32 to 1024) with the same
bins-must-not-exceed-tileSize² cross-validation JSolex enforces (clamping `Bins` down when it stops
being valid for a smaller `TileSize`); the other five values are free-text `TextBox`es, matching
JSolex's own panel (no sliders, no declared ranges beyond `AutoStretchStrategy`'s own guards).
`ProcessParametersViewModel`/`ImageEnhancementViewModel`/`ImageSelectionViewModel` moved from being
owned by `OptionsViewModel` (one shared Save button reassembling `ProcessParams` from all three) to
being owned by `ProcessViewModel` instead: its constructor seeds all three from one `Load()` exactly
like `OptionsViewModel`'s old constructor did, then subscribes to each child's own
`PropertyChanged` to (re)start a 300ms debounce timer (same rapid-fire-write rationale as
`CaptureViewModel.PersistSettingsIfConnected`'s own timer - antivirus-scan `IOException`s were
observed from calling a JSON-file save too fast) that rebuilds and saves one `ProcessParams` - the
exact reassembly `OptionsViewModel.Save` used to do on a click, just automatic now. `ProcessAsync`
itself no longer calls `IProcessParamsStore.Load()` - it builds `ProcessParams` from the three live
child view models directly and flushes the debounce timer synchronously first, so clicking Process
within that 300ms window can never run against stale, pre-edit values reloaded from disk.
`OptionsViewModel` lost its `IProcessParamsStore` dependency and the three child properties/
reassembly-on-Save lines entirely - Options is back to just equipment libraries + General.

`ProcessView.xaml`'s panel went through two designs the same day. The first was a permanent two-column
`Grid` (status/preview content in a `*`-width column, a `GridSplitter`, the panel itself in a 320px
column that collapsed to zero width via a `Style`/`DataTrigger` on `ColumnDefinition.Width`) - reported
back by the user as not really working, plus the Expander list not using the view's full height, and a
request to instead do what GSServer does: a hamburger button revealing a slide-in panel of options, but
from the right rather than GSServer's own left. Investigated `C:\Source\Repos\JPhilC\GSServer` directly
rather than guessing at the mechanism - confirmed GSServer's own hamburger drawers aren't hand-rolled at
all, they're the third-party MaterialDesignInXamlToolkit library's `DrawerHost` control
(`GS.Server/Focuser/FocuserV.xaml`, `SkyTelescopeV.xaml`; `MaterialDesignThemes`/`MaterialDesignColors`
4.8.0/2.1.4 per `GS.Server.csproj`), used near-identically across every GSServer view: `md:DrawerHost`
wraps the whole view, `LeftDrawerContent`/`IsLeftDrawerOpen` for the panel, and two
`MaterialDesignHamburgerToggleButton`-styled `ToggleButton`s (one in the main content to open, one
inside the drawer to close) driving `DrawerHost.OpenDrawerCommand`/`CloseDrawerCommand` with
`CommandParameter="{x:Static Dock.Left}"`. SolScan now does the same with `Dock.Right`/
`RightDrawerContent`/`IsRightDrawerOpen` instead - asked the user directly whether to add this same
third-party dependency (matching GSServer exactly) or hand-roll an equivalent slide-in overlay with no
new dependency, since SolScan had used zero UI component libraries until now; they chose adding
`MaterialDesignThemes`. Scoped to just this one view, though, unlike GSServer's own app-wide
`App.xaml` merge (`MaterialDesignTheme.Dark.xaml` there, restyling every control in the whole
application): the four required resource dictionaries (`MaterialDesignTheme.Light.xaml` - Light, not
Dark, so this one view doesn't look jarringly different from SolScan's other plain-WPF-styled
views - `MaterialDesignTheme.Defaults.xaml`, and a Grey primary/Blue accent colour dictionary) are
merged into `ProcessView.xaml`'s own `UserControl.Resources` instead of `App.xaml.Resources` - WPF
resolves `DynamicResource`/implicit-style lookups by walking up from the element that needs them, so a
closer-scoped merge still satisfies `DrawerHost`/`MaterialDesignHamburgerToggleButton`/
`MaterialDesignDivider` without reskinning Prepare/Capture/Options too. `IsRightDrawerOpen` and both
hamburger `ToggleButton`s all bind to the same `ProcessViewModel.IsProcessOptionsPanelExpanded` bool
(unchanged from the first design) so persistence didn't need to change - `DrawerHost`'s own drawer
content is a full-height overlay by construction (it doesn't share column space with the main content
the way the abandoned `GridSplitter` design did), which also resolves the "doesn't use full height"
complaint for free. `ImageEnhancementView.xaml` gained three `StackPanel`s (one per mode's tuning
fields) shown/hidden via the same `Style`/`DataTrigger` idiom keyed off `SelectedContrastEnhancement`,
plus the "Reset to Defaults" button, all with the same explanatory-tooltip treatment the rest of this
panel already has. Covered by: `StretchingTests` (the three new `AutoStretchStrategy` validation-guard
cases), a new `DiskGeometryCorrectorTests` case proving non-default `ClaheParams`/`Clahe2Params`/
`AutoStretchParams` values actually change `GeometryCorrectedProcessed`'s output rather than being
accepted and silently ignored, and a new `JsonProcessParamsStoreTests` case proving an old
process-params.json (built by stripping the three new properties back out of a real serialized
default, rather than hand-typing brittle nested JSON) backfills real defaults rather than nulls.
No automated UI tests exist in this repo for the panel/toggle/Expander behaviour itself (WPF, no
existing UI test framework) - the `DrawerHost` design above is confirmed build-clean but, as of this
writing, not yet confirmed by the user running the app (the `GridSplitter` design it replaced *looked*
fine the same way and turned out not to actually work - see this same entry's own history above - so
"builds" isn't being claimed as "verified" here again).

The same `md:DrawerHost` pattern was then applied to `CaptureView.xaml` too, one message later - its
own right-hand column (the six `Expander`s: Capture Settings/Camera Settings/Histogram/Focus Aid/
Reticule/Display Settings) moved into a `RightDrawerContent` drawer the same way, with
`CaptureViewModel.IsCaptureOptionsPanelExpanded` (new, persisted via a new `AppSettings.
CaptureOptionsPanelExpanded` field, same `OnXChanged → PersistAppSetting` pattern as its six sibling
Expander bools) as the toggle. Deliberately different from Process's version in one way, per the user's
own explicit request: Start/Stop Recording and the frame-count/dropped-frame-count readouts stay on the
main view, in a new always-visible row below the live preview, rather than moving into the drawer with
everything else - unlike the six settings groups, these need to stay usable/visible regardless of
whether the drawer is open. The shared MaterialDesignThemes resource-dictionary merge (Light theme +
Defaults + Grey/Blue colours) was factored out of `ProcessView.xaml`'s own `UserControl.Resources` into
a new `Themes/MaterialDesignScoped.xaml` file once a second view needed the identical merge, rather than
duplicating the same four `<ResourceDictionary Source="...">` lines a second time - both views now just
merge that one file into their own `UserControl.Resources`, keeping the scoping (not `App.xaml`)
rationale in one place.

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
point at `..\SolScan.External\x64\<Vendor>\<dll>` with `Link` flattening them onto the output
folder (P/Invoke's DllImport search doesn't look in subfolders), still `Condition="Exists(...)"`
guarded, unlike NINA's unconditional version, in case the submodule is ever checked out without
its LFS content pulled (`git submodule update --init` without Git LFS installed leaves pointer
files, not real DLLs) - a build should degrade to no-camera-support in that case, not fail outright.
The submodule itself now holds the real `ASICamera2.dll` (2,852,352 bytes, its own version
resource reporting `ProductName: ASICamera SDK`) and `altaircam.dll` (13,675,520 bytes), committed
in `da02119 Add the real ASICamera2.dll and altaircam.dll binaries` - not just the placeholder
per-vendor READMEs the submodule started out with. Confirmed actually reaching a real release, not
just present on disk: querying `SolScan.Setup.msi`'s own `File` table directly (via the Windows
Installer COM API) lists both DLLs at their exact real sizes, and the published `v0.1.0` GitHub
release asset matches a from-scratch local rebuild byte-for-byte in size (~7.67MB either way) -
the installer's small size versus ~16.5MB of raw DLL content is WiX's own cabinet compression, not
evidence the files are missing. The one deliberate divergence from N.I.N.A.'s own approach: NINA
also ships the VC++ Redistributable's own runtime DLLs as loose xcopy-deployed files inside that
same submodule, rather than chaining an installer - this project looked into doing the same
(the redistributable's own `vc_redist.x64.exe` supports a documented `/layout` extraction switch
for exactly this purpose) but couldn't reliably complete that extraction in this environment (no
interactive Windows install session, no `7z` available to unpack the bootstrapper directly), so
`SolScan.Bundle` still chains the real `vc_redist.x64.exe` installer instead (see the Bundle.wxs
entry above) - worth revisiting by hand later if xcopy deployment of those specific runtime files
turns out to matter.

Also real: the collimator-focus aid - `SolScan.Core.Camera.FocusAnalyzer.MeasureEdgeSteepness` (see
CLAUDE.md's sunscan-app entry above for why this is a *second*, distinct focus aid from the
camera-focus/FWHM one, `SpectralLineFocusAnalyzer`, described in its own entry further down), now on its fourth design, each revision driven by a concrete
real-hardware failure rather than by guesswork:

1. A method-for-method port of sunscan-backend's `focus_analyzer.py`'s `measure_focus_two_edges` - a
   raw two-point-gradient peak over a 40-row sample, deliberately cheap to fit that code's real-time
   Python/Raspberry-Pi budget.
2. Replaced immediately (SolScan has no equivalent hardware constraint) with a **sub-pixel edge-width**
   design: average many more rows into one horizontal (spatial-axis) profile, then measure the 10%-90%
   threshold-crossing width of its steepest transition(s) - the same technique real optical MTF/
   edge-response testing uses, and the same sub-pixel threshold-crossing approach
   `SolScan.Processing.Shg.DiskEdgeDetector` already uses for the offline geometry-correction
   pipeline. A physical pixel distance rather than an intensity-gradient number, so (unlike the first
   version) it's stable across Gain/Exposure changes and interpretable on its own.
3. **Reworked again** after real-hardware testing under a genuinely difficult scene (an overcast sky
   forcing a very high Gain, hence very noisy frames) produced wildly unstable and sometimes
   physically-impossible readings - an "edge width" reported as *larger than the frame itself*. Two
   design flaws in version 2 caused this: thresholds were derived from the *whole profile's* observed
   min/max, so anything else unusual anywhere in a wide frame (a faint unrelated line, one very dark/
   bright column) skewed the calibration for the edge actually being measured; and the threshold-
   crossing search itself had no distance limit, so on a noisy frame where the (mis-calibrated)
   threshold was never cleanly crossed nearby, it kept walking regardless - sometimes most of the way
   across the frame. Fixed by deriving the low/high reference levels from small *fixed-distance*
   windows near the candidate edge itself rather than the whole profile, only trusting an edge if
   those local levels spanned enough of the profile's real overall range, and capping the threshold-
   crossing walk at a fixed distance. The profile itself was also changed to the *median* of each
   column's sampled rows rather than the mean, and lightly median-smoothed along its width afterward
   (`SmoothingRadius`) - both edge-preserving denoising steps still in place today (a median resists an
   isolated noisy/hot pixel or a thin unrelated feature grazing a few sampled rows, without smearing a
   genuinely sharp transition the way a mean/box blur would).
4. **Reworked a third time** after a *different* real-hardware session reported "no edge detected" on
   a capture that looked reasonably close to focus. Two further design flaws, both fundamentally about
   version 3's constants being *fixed pixel counts*, which don't generalize across resolutions/fields
   of view - a real optical edge's width in pixels scales with sensor resolution, not a universal
   constant, so a perfectly reasonable, close-to-focus edge at a high native resolution can legitimately
   span far more pixels than a small fixed window/search-distance was ever built to reach:
   - The fixed-distance local reference windows (15px gap, 20px window) couldn't reach a real edge's
     true flat plateaus once the transition itself was wider than that. Replaced by `FindPlateau`:
     search *outward* from the edge, in growing steps, until a window of consecutive columns is
     verified genuinely flat (low internal variance relative to the profile's overall range) -
     however far that takes, bounded only by `MaxPlateauSearchFraction` (30% of the frame's own
     width, so the bound itself scales with resolution instead of needing a bigger fixed guess).
   - Even once (this) let a genuinely wide-but-real edge reach the confidence-gating step, version
     3's *gate itself* rejected it: it required each candidate's peak two-point gradient to clear a
     fraction of the profile's range (and the weaker candidate to clear a fraction of the stronger
     one) - but peak two-point gradient is inherently *smaller* the more pixels the same total
     amplitude is spread over, so that gate structurally penalized exactly the wide edges the
     `FindPlateau` fix above was just built to reach. Removed entirely - `IsSeparationTrustworthy`,
     comparing the *actual achieved* low/high plateau levels once found (`MinPlateauSeparationFraction`,
     25% of the profile's overall range), is already a strictly better confidence gate for this: it's
     width/scale-invariant, unlike a derivative, so it doesn't need a second, redundant gate on top.

Reports whichever of the rising/falling transitions have *some* slope in that direction at all (an
edge with none - the genuine single-slit-edge case - has an exact-zero-or-negative peak in that
direction, not just a small one, so this cheaply skips a direction with nothing there before ever
attempting a plateau search on it) and then pass `IsSeparationTrustworthy` - one edge for the
slit-edge case, two (averaged) for a full disk crossing, or "no edge" for a flat/blank frame, a
transition too close to the frame's own border, or one with no flat plateau reachable within
`MaxPlateauSearchFraction` on either side. Wired into `CaptureViewModel`'s existing throttled preview
pipeline (`ProcessPreviewFrame`, alongside the histogram/stretch work already computed there) rather
than a separate compute path, shown in a "Focus Aid" Expander on the Capture view (`EdgeWidthText`)
alongside a running `BestEdgeWidthText` low-water mark - the same "(Best: …)" readout sunscan-app's
own Focus assistant keeps (CLAUDE.md's sunscan-app entry), just tracking a *minimum* rather than a
maximum since smaller pixel widths are sharper (the same "smaller is better" convention as an
autofocus routine's HFD/HFR reading). `CaptureViewModel` additionally keeps a short
(`RecentEdgeWidthWindowSize` = 5) rolling *median* across frames (`SmoothEdgeWidth`) before updating
either the displayed text or the Best tracker - the UI-level counterpart to `FocusAnalyzer`'s own
per-column median, so one remaining bad frame can't flash a wrong number on screen or falsely set a
new "Best" even after the per-frame fixes above. Both the per-frame Best tracker and this rolling
window reset via a "Reset Best" button or automatically whenever live view (re)starts, since a best
(or a smoothing history) carried over from a previous session/camera/ROI isn't meaningful for a new
one. Covered by `FocusAnalyzerTests` - sharper-vs-softer synthetic edges scoring a smaller width, a
two-edge disk crossing, a single slit edge, a fully flat frame, a frame narrower than the algorithm's
own minimum usable width, an edge too close to the frame border, a transition too gradual to reach a
trustworthy plateau separation, an unrelated distant feature that must not affect a real edge's
measurement, a genuinely sharp edge staying bounded and confident under heavy synthetic noise, a
moderately soft (150px) edge at full-sensor-scale width measuring correctly rather than being rejected
(the regression test for this specific reported bug), 16-bit samples, and a frame shorter than the
default row-sample count. No graph/profile visualization was added - the whole point of this feature
is that the numeric readout replaces needing one. NOT YET VALIDATED against real hardware/optics
beyond the two sessions that prompted these reworks - the remaining threshold fractions (10%/90%,
`PlateauFlatnessFraction`, `MaxPlateauSearchFraction`, `MinPlateauSeparationFraction`) are reasonable
choices, not calibrated against a real SHG/collimator across varied conditions, and a genuinely very
gradual (not just wide) real transition could in principle still find a spuriously "flat" window
partway through a slow, steady change rather than reaching the true plateau - `MinPlateauSeparationFraction`
catches this in practice (a falsely-early plateau found partway up a ramp won't differ enough from the
other side to pass), but it's a secondary safety net catching a primary check's imprecision, not a
first-choice design.

One follow-up question worth recording since it was raised and checked rather than assumed: does the
darker, slightly curved ("smile") spectral-line band an SHG wide view *always* shows - not just an
occasional unrelated feature, but a structural, ever-present part of the scene - need explicit
detection/exclusion? Verified it doesn't, rather than just reasoning it through: `BuildProfile`'s
per-column *median* already ignores it for free, because at any single column the line only occupies
its own local thickness out of however many rows are sampled there, and the curvature only changes
*which* rows that is per column, not *how many* - so it stays a small minority of samples everywhere
(median tolerates any minority below 50%) as long as the line's own thickness stays under half the
sampled row height, true for any reasonably tall framing view. Confirmed directly by
`FocusAnalyzerTests.MeasureEdgeSteepness_IgnoresACurvedSpectralLineRunningThroughTheWholeFrame` - a
genuinely 2D synthetic frame (unlike every other test here, which just repeats one row down the whole
height) with a curved band running straight through the real edge's own local reference windows, not
just somewhere spatially separate from it the way the distant-feature regression test above is. No
explicit line-detection/exclusion step was added on top of the existing median - it wasn't needed, and
would have added real complexity (essentially porting some version of `SpectralLineCurvatureDetector`
to run live) for no measurable accuracy gain over what the median already provides for free.

Also real: icon-based toolbar buttons across Capture and the Hand Control window, replacing plain
text buttons, once a set of PNGs landed under `Assets/Icons/` (8 for CaptureView - Refresh/Connect/
Disconnect/FindSun/Sync/HandControl/StartRecording/StopRecording - plus 5 for HandControlWindow's
compass - Up/Down/Left/Right/Stop). A shared `ToolbarIconButtonStyle`
(`SolScan.App/Themes/IconButtonStyles.xaml`, merged into both `CaptureView.xaml` and
`HandControlWindow.xaml` - factored out once the second view needed the identical style, same
"factor out on second use" precedent as `MaterialDesignScoped.xaml`'s own doc comment) gives every
icon button a dark background (`#FF2A2A2A`, lighter on hover/pressed, dimmed when disabled) -
deliberate, not just decorative: several of the supplied icons are drawn white/light-grey
(`RefreshCameraList.png`, the HandControl compass icons) and disappear against a plain light
control/window background otherwise. Every icon button carries an explanatory `ToolTip`.

Connect/Disconnect and Start/Stop Recording each collapse into a single toolbar slot - two
`Button`s occupying the same `Grid` cell, `Visibility` toggled by a `DataTrigger` on
`IsConnected`/`IsRecording` respectively, so only the relevant one is ever visible. This is a
deliberate simplification of the underlying 3-state model (disconnected / connected-and-paused /
connected-and-live - see `CaptureViewModel.ToggleLiveViewAsync`/`DisconnectAsync`'s own doc
comments), confirmed with the user rather than assumed (that mid-state was itself a deliberate
earlier design choice - `ToggleLiveViewAsync`'s own comment already called it out as "deliberately
distinct from DisconnectAsync"): once connected, the toolbar now always shows Disconnect rather than
a way to pause the live view while staying connected - that state is no longer reachable from the
toolbar.

The Capture view's top-bar container went through a few iterations the same session, each a direct
answer to a follow-up question rather than a single upfront design: originally a `DockPanel`
(hamburger docked right, the rest of the toolbar also right-aligned) from the original
hamburger-drawer work; simplified to a single right-aligned `StackPanel` once asked whether the
`DockPanel` was actually needed - true at the time, since both children were already right-aligned
and nothing needed dock/fill behaviour; then left-aligned (`HorizontalAlignment="Right"` removed)
once a vestigial "title on the left" comment/expectation was dropped along with an unused title
`TextBlock`; then finally back to a `DockPanel` once the ask became "hamburger on the right,
everything else left" - a genuine dock+fill need this time (unlike the first iteration), with the
hamburger `ToggleButton` docked right and the rest of the toolbar in an inner `StackPanel` filling
the remainder.

`HandControlWindow.xaml`'s fixed `Height="250"` was replaced with `SizeToContent="Height"` after the
Speed column's "Speed"/value `TextBlock`s were reported showing only half - the fixed height genuinely
wasn't enough for the compass grid (3×56px) plus the speed slider column (label + 130px slider + value
label, ~178px) plus the status text row once title-bar chrome and margins were accounted for. Letting
the window size itself to its actual content is more robust than a bigger guessed pixel value, since
it no longer depends on Windows theme/DPI/title-bar-height assumptions.

Also real: the same icon-toolbar/dark-button treatment landed on `ProcessView.xaml` too - Browse/
Process/Cancel now use `BrowseForFile.png`/`ProcessRun.png`/`CancelProcessing.png` via the same shared
`ToolbarIconButtonStyle`, each with an explanatory tooltip, and Cancel keeps its existing
`IsProcessing`-gated visibility rather than gaining a stacked-button treatment (unlike Capture's
Connect/Disconnect and Start/Stop pairs, Process's own "disabled Process + appears-while-running
Cancel" shape was already a deliberate, working design, not something this pass needed to change).
The view's main content was also restructured from a plain `StackPanel` into a `Grid`, matching
`CaptureView`'s own shape: every control (Browse/Process/Cancel/the processed-image picker) now lives
in one top toolbar row (a `DockPanel`, hamburger pinned right, same reasoning as CaptureView's own
toolbar) instead of being split across two separate button rows with file info sandwiched between
them; and the image preview now fills all remaining vertical space (`Grid.RowDefinition Height="*"` -
a `StackPanel` can't stretch a child to fill leftover space the way a `Grid` row can) rather than
being capped to a fixed `MaxWidth="640" MaxHeight="640"` box, wrapped in a `ScrollViewer` for parity
with Capture's own preview container (in practice this rarely if ever actually needs to scroll, since
`Stretch="Uniform"` into a sized container never overflows it - flagged to the user as a design
trade-off rather than silently assumed, in case native-resolution-with-real-scrolling turns out to be
what's actually wanted instead).

`ProcessViewModel`'s own status narration (previously a local `StatusText` bound into a `TextBlock` at
the top of this view) moved into the shared `StatusBarViewModel` instead - a new `ProcessStatusText`
field (+ matching `StatusBar.xaml` entry), following the exact same pattern `CaptureFrameRateText`/
`MountStatusText` already established. `ProcessViewModel` now takes a `StatusBarViewModel` constructor
dependency (already a DI singleton, so no registration change needed) and every former
`StatusText = ...` assignment became a call to a new `SetStatus(message)` helper that writes
`_statusBar.ProcessStatusText = $"Process: {message}"` - including the `Progress<string>` callback
threaded into `IShgProcessor.ProcessAsync`, which collapses to `new Progress<string>(SetStatus)` since
`SetStatus`'s signature already matches `Action<string>` exactly. Status now stays visible regardless
of which stage is on screen, matching Capture/Mount's own status-bar fields.

Also real: a **"Processing Results" panel** - the results panel mirroring JSolex's two-part info view
(detected line + geometry tilt/xyRatio) called out as still-needed in Phase 6 below, now landed.
Requested as a natural follow-on once the icon-toolbar/layout pass above freed up room: the row
between the toolbar and the image preview split into two columns inside one `ScrollViewer`
(`MaxHeight="220"`, so a long info panel can't crowd out the preview below the way an unbounded
`Auto`-height row would - typical content is short enough the scrollbar essentially never actually
appears) - the existing file/equipment info stayed in the left column unchanged, and the right column
is the new panel. Three new `ProcessViewModel` properties back it - `DetectedLineText`/
`DetectedGeometryText`/`ResultImagesText` - populated by a new `UpdateResultInfoPanel` method that
reuses the same `FormatDetectedLine`/`FormatDetectedGeometry` fragment-formatters `BuildResultSummary`
(the transient status-bar line) already needed, refactored out so the two presentations - one a
terse single line omitting empty fragments, the other three always-shown labeled fields with a
placeholder - can't drift apart. Unlike `AvailableProcessedImages` (deliberately disk-based, per this
class's own doc comment), none of these three are persisted anywhere - `ShgProcessingResult` is a
purely in-memory return value with no sidecar file - so there's no way to recover them for a file
whose output already exists from an earlier session without running Process again: `LoadSelectedFile`
resets all three to "not yet processed" placeholders whenever a different file is picked, and they're
only ever updated on a *successful* `ProcessAsync` completion - left untouched (not blanked) on
cancel/failure, so a previous successful run's numbers stay visible rather than being wiped by an
unrelated error on a later attempt.

**Follow-up bug, found once the processing log (below) let the user cross-check the two against each
other**: `ResultImagesText`/`BuildResultSummary` both counted `result.Images.Count` only, undercounting
by however many entries `result.ColorImages` had (e.g. one Colorized image) - a real Colorized run would
show "Wrote 2 image(s)" on the panel/status bar while the log correctly said "Wrote 3". The processing
log's own line (added later, see below) already added the two counts together correctly, which is what
made the mismatch visible at all. Fixed by extracting a single `TotalImageCount(ShgProcessingResult)`
helper (`result.Images.Count + (result.ColorImages?.Count ?? 0)`), now the one and only place this sum
is computed - used by `BuildResultSummary`, `UpdateResultInfoPanel`, and the log line alike, so the
three can't drift apart again the way they just did.

Also real: **the Colorized image** - `GeneratedImageKind.Colorized`, the first output landed from
JSolex's separate "Advanced Images" section rather than "Basic Images" (astro4j's own
`ImageSelectionPanel.java` puts its checkbox in `advancedGrid`, not the basic one - confirmed by
reading that file directly rather than assuming from `RequestedImages.FULL_MODE`'s flat list, which
doesn't distinguish the two). Requested directly by the user as "the other images JSolex can produce,
starting with the colourised image" once the Process view's DrawerHost redesign was confirmed working
in the app - a deliberate, acknowledged scope expansion past the "Basic Images only" framing
`GeneratedImageKind`/`RequestedImages`'s own doc comments used to carry (both updated to reflect it).
`SolScan.Core.Processing.SpectralRay` gained the two pieces astro4j's own `SpectralRay` carries for
this and SolScan's port had previously dropped as out-of-scope: a nullable `ColorCurve` field (backed
by a new plain-data `ColorCurveParams` record - only `HAlpha` sets one, matching astro4j exactly) and
`ToRgb()`/`ToSimpleRgb()` (a wavelength-to-display-colour approximation plus an HSL desaturate/lighten
pass, `improveEsthetics` in the original) - kept in Core as dependency-free math on the record itself,
same precedent as `SolScan.Core.Astronomy.SunPosition`, rather than needing Core to depend on
Processing. The actual per-pixel colorization math is new in `SolScan.Processing.Color`: `ColorCurve`
(fits each channel's mono-to-output quadratic via the already-ported
`SolScan.Processing.Math.LinearRegression.SecondOrderRegression`, rather than porting astro4j's own
hand-derived polynomial solve/cache - a 3-point regression is cheap enough that per-instance caching
saves nothing measurable), `RgbHsl` (array-based RGB↔HSL conversion, the whole-image counterpart to
`SpectralRay`'s own single-pixel HSL helpers), and `Colorize` (`WithCurve` for H-alpha's fixed curve,
`WithWavelengthRgb` for every other named line's approximated tint - gamma-stretches a copy of the mono
data, tints it by the wavelength colour, then re-stretches the result's lightness toward white via a
direct specialization of astro4j's generic `StretchingStrategy.stretch(RGBImage)` default method for
the one stretch strategy this pipeline actually drives through it, a plain min/max linear stretch, not
a general RGBImage-stretch dispatch mechanism SolScan.Processing has no other use for). Two more
stretching strategies were ported to feed this: `ArcsinhStretchingStrategy` (CPU path only, matching
every other GPU-capable port here) and `PercentileStretchStrategy` - both applied to a copy of the
`GeometryCorrectedProcessed` buffer before colorizing, anchored to a black-point estimate
(`ImageStatistics.EstimateBlackPoint`, already computed internally by `DiskGeometryCorrector.Correct`
for its own warp/crop fill colour, now also surfaced via a new `Result.BlackPoint` field rather than
re-derived from scratch on a different image). `SpectralRay.Other` (no wavelength) produces no
Colorized image at all, matching astro4j's own silent no-op for that case - not reported via
`ShgProcessingResult.SkippedKinds`, since the kind *is* implemented, it's simply inapplicable to that
particular ray. `ShgProcessingResult` gained a new `ColorImages` list alongside the existing mono
`Images` - kept as a genuinely separate, single-purpose type (`ProcessedColorImage`, three `ushort[,]`
channels) rather than making `ProcessedImage.Pixels` nullable-plus-an-optional-colour-payload, since
every consumer already needs to branch on "is this mono or colour" by file content anyway.
`ProcessViewModel` gained a `SaveColorPng` (16-bit-per-channel `PixelFormats.Rgb48`, alongside the
existing mono `SavePng`/`Gray16`) and the disk-based preview loader now peeks a candidate PNG's own
decoded pixel format to decide which path to take: a colour PNG is shown as-is (no auto-stretch - the
colorization pipeline already stretched/tinted it, so re-auto-stretching would just wash out its own
colour curve), converted straight to `Bgra32` for display, while a mono PNG keeps the existing
auto-stretch/downsample treatment. Image Selection's panel gained a "Colorized" checkbox under a new
"Advanced Images" sub-heading (with its own explanatory tooltip, matching this panel's existing
in-app-learning-tooltips goal), and the Process Parameters panel's own "Line" tooltip was updated to
mention it now also drives Colorized's tint, not just Auto contrast enhancement's calcium-line check.
Covered by `ColorizeTests` (the curve/wavelength math directly - anchor-point exactness, known-hue
sanity checks for a couple of real wavelengths, tint proportionality) and two new
`DiskGeometryCorrectorTests` cases (a real end-to-end Colorized image for both the H-alpha-curve and
wavelength-tint paths, proving the three channels genuinely differ and use a meaningful part of the
16-bit range; and the `SpectralRay.Other` no-op case).

**Follow-up, after the user actually tried it**: three fixes/tweaks, all confirmed by evaluating the
fitted curves/checking the actual XAML behaviour rather than guessing:
- **H-alpha's colour curve was retuned toward orange.** astro4j's own `KnownCurves.H_ALPHA` values
  (`84,139, 95,20, 218,65`) render a deep crimson red through most of the mid-tone range - evaluating
  the fitted quadratics directly (not just eyeballing the raw input numbers) showed green staying under
  ~25% of red until close to full white. `ColorCurveParams.HAlpha` is now `84,150, 60,55, 220,40` -
  green's anchor moved from 95→20 to 60→55 (ramping up much earlier and further), red/blue nudged to
  match - verified across the full mono range to still be smooth/monotonic, landing on a genuine
  orange progression (dark orange → solid orange → golden orange → pale highlight) rather than red →
  pale orange. A deliberate customization per the user's own taste, no longer astro4j's stock curve -
  noted as such in `ColorCurveParams.HAlpha`'s own doc comment so a future astro4j-parity check doesn't
  mistake this for drift.
- **The Colorized image is now the default preview shown right after a Process run that produced
  one.** `RefreshProcessedImages` gained a `preferColorized` parameter (true only from `ProcessAsync`'s
  own call site, and only when `result.ColorImages` is non-empty) that makes `colorized.png` win the
  default-selection race even over the previously-selected file - the browse-a-file call site (picking
  a `.ser` to inspect, not just finished processing it) still defaults to "raw.png"/keep-previous as
  before.
- **The image preview now actually zooms to fit the view**, which it wasn't doing before despite the
  `Image`'s own `Stretch="Uniform"`: it was wrapped in a `ScrollViewer` ("matching CaptureView's own
  preview container"), and a `ScrollViewer` measures its content with infinite available size - which
  defeats `Stretch="Uniform"` entirely, since there's nothing to shrink-to-fit against. A well-known WPF
  gotcha, and exactly why the image was rendering at native resolution requiring manual scroll bars
  instead of fitting the view. Fixed by removing the `ScrollViewer` - the `Image` now sits directly in
  its `Border`, whose size is properly bounded by the surrounding `Grid` row, so `Stretch="Uniform"`
  can actually do its job. (CaptureView's own zoom feature works around the identical gotcha a different
  way - an explicit code-behind-computed `Width`/`Height`, see `CaptureView.xaml.cs`'s
  `UpdateImageSize` - needed there because CaptureView also supports pixel-peeping at fixed zoom
  percentages, a use case Process has no equivalent of; Process's own fix is the simpler one.)

NOT YET VALIDATED against a real capture beyond this round of user testing - like the AutoStretch/CLAHE
work before it, the underlying algorithm is only exercised against the existing synthetic
elliptical-disk test fixture in automated tests.

**One more follow-up, reported after the above three**: the drawer's own hamburger/close toggle button
pointed the wrong way once opened - `MaterialDesignHamburgerToggleButton`'s built-in animation morphs
its hamburger icon into a back-arrow that always points *left* when checked, correct for GSServer's own
left-hand drawers (what this pattern was modelled on) but backwards for SolScan's own right-hand ones.
Fixed with a new shared style, `MaterialDesignHamburgerToggleButtonRightDrawer`
(`Themes/MaterialDesignScoped.xaml`, based on the stock style plus a horizontal `ScaleTransform
ScaleX="-1"` - the hamburger icon itself is symmetric, so only the arrow it becomes when checked is
actually affected), applied to all four hamburger `ToggleButton`s across both views with a right-hand
drawer (Process and Capture, each with an open button and a close button) for a consistent direction
everywhere, not just the one instance reported.

Also real: **the Basic/Advanced Images split is gone, and the virtual eclipse image landed.** The
user decided v1's image set is done: anyone wanting JSolex's fuller catalogue (Doppler, redshift,
active regions, Debug Options, custom ImageMath scripts, ...) still has the original SER file to hand
JSol'Ex itself - no need for SolScan to keep growing toward parity with it, or to keep presenting its
own smaller set as a "Basic" tier implying a missing "Advanced" one. `GeneratedImageKind`/
`RequestedImages`' own doc comments, `ImageSelectionViewModel`, and `ImageSelectionView.xaml` (now one
flat checklist, no "Basic Images"/"Advanced Images" `TextBlock` headers) were all updated accordingly;
earlier CLAUDE.md entries above that used astro4j's own Basic/Advanced terminology to describe what
wasn't ported *yet* are left as the historical record they are, not rewritten. Alongside that,
`GeneratedImageKind.VirtualEclipse` - astro4j's "virtual eclipse"/coronagraph view - landed as a real,
not placeholder, output: the solar disk itself is blanked out (`SolScan.Processing.Shg.DiskFill`,
ported from `DiskFill.doFillWithGradient` - a 4x4-subpixel-sampled antialiased fill, not a hard per-
pixel cutoff), then the surrounding region is neutralized (two rounds of an ellipse-aware
`BackgroundNeutralizer.BlindNeutralize` - that method previously only ported astro4j's ellipse-*less*
code path, since `DiskEdgeDetector`'s own first-fit was its only caller; now extended with an optional
`Ellipse?` that switches the initial background estimate from the histogram-based
`EstimateBackgroundLevel` to a new `ImageStatistics.EstimateBackground` - the plain off-disk mean,
matching astro4j's own `AnalysisUtils.estimateBackground` - and restricts sampling to pixels outside
it, matching astro4j's own `blindBackgroundNeutralization2` exactly) and arcsinh-stretched
(`SolScan.Processing.Shg.Coronagraph`, direct port of `CoronagraphTask.doCall`), so faint prominences/
streamers near the limb - normally buried by the disk's own far greater brightness - become visible
the way they would during a real eclipse. `CoronagraphTask`'s own constructor `blackPoint` parameter
is dropped - confirmed dead in astro4j itself (stored but never read by `doCall`), matching this
project's established "confirmed dead code isn't faithfully reproduced" precedent. Its input is the
plain `GeometryCorrected` image, not the contrast-enhanced `GeometryCorrectedProcessed`/`Colorized`
one, so `ShgProcessor` produces it off the ellipse fit alone, independent of whether either of those
was separately requested (mirroring astro4j's own `ProcessingWorkflow.produceCoronagraph`, which runs
unconditionally off `WorkflowResults.GEOMETRY_CORRECTION`) - a small scaling helper
(`ShgProcessor.ScaleToContainerRange`) was factored out of the existing contrast-enhancement code path
so both it and the new coronagraph call site share the same native-ADC-to-16-bit-container rescale.
Covered by a new `ProcessingLocationsTests` case (`VirtualEclipse` maps to the `Processed` directory,
alongside a `Colorized` case that had been missing too) and a new `DiskGeometryCorrectorTests` case
proving it's a real, separate transformation against the existing synthetic elliptical-disk fixture -
requested alongside `GeometryCorrected` to prove the two differ, and asserting the blanked disk reads
back as a sizeable block of exact-zero pixels (traced through the pipeline: `DiskFill` writes literal
zeros, each neutralization pass' background subtraction clamps at zero rather than going negative,
`ArcsinhStretchingStrategy` short-circuits `v==0` to `0` outright, and the final min/max renormalization
maps the image's own minimum - already zero - back to zero) far outnumbering the un-enhanced
`GeometryCorrected` image's own near-zero count. NOT YET VALIDATED against a real capture - same
caveat as AutoStretch/CLAHE/Colorized before it.

Also real: a **"Crop SER" utility** for old full-frame captures - requested directly by the user
after recording several real SHG scans without first setting up a hardware ROI (see the centred-ROI
note above), leaving them at full sensor height with no way to re-capture. `SolScan.Processing.Capture`
gained `ISerCropper`/`SerCropper` (same "resolve `ISerReader`/`ISerWriter` through a DI factory
delegate, never reference SolScan.Infrastructure directly" shape as `ShgProcessor`'s own constructor):
reads every frame of a source `.ser` file and re-writes a vertically centred slice of it - full
original width and frame count unchanged, only the height narrowed - to a new file, leaving the
original untouched. The kept height is specified as a percentage (0, 100] of the source height, not a
raw pixel count, per the user's own framing of the request ("a fraction of the height"); centring
rounds the kept-height calculation to the nearest row (`SerCropper.ComputeCroppedHeight`, shared
between the actual crop and `SerCropViewModel`'s own live preview text so the two can never disagree)
and floor-divides the remainder evenly above/below. A pop-out, modeless "Crop SER" window
(`Views/SerCropWindow.xaml`/`ViewModels/SerCropViewModel.cs`) opens from a `CropVideo.png` icon button
on the Process view's toolbar (`ToolbarIconButtonStyle`, same as its Browse/Process/Cancel siblings -
this button briefly shipped as plain text before the icon asset landed) - same
window-per-open/`Closed`-clears-the-reference shape as `CaptureViewModel.OpenHandControl`,
and the same `Closing`-cancels-any-in-flight-work safety net as `HandControlWindow`'s own Closing
handler, just cancelling a background crop instead of a live mount jog. Lets the user pick a source
file (reads its header immediately - dimensions/bit depth/frame count - and suggests an output path
alongside it with a `_cropped` suffix), type a height percentage (with a live "cropped height: Npx
(rows X-Y of Z)" preview), confirm/browse the output path, then Crop with progress narration and
Cancel support; a cropped/cancelled-partway file is deleted on cancellation or failure
(`SerCropViewModel.TryDeletePartialOutput`, best-effort) so a failed run never leaves behind something
that looks like a valid, if incomplete, crop. The source recording's `.equipment.json` sidecar (if
any) is copied alongside the cropped output too (`SerCropViewModel.CopyEquipmentSidecarIfPresent`) -
cropping doesn't change which SHG/telescope/camera/mount-pointing/camera-settings produced the
recording, so Process's own file picker should still show the same equipment info for the cropped
file as the original. Registered transient in `App.xaml.cs`, same "stateless, no reason to share an
instance" reasoning as `IShgProcessor`. Covered by `SerCropperTests` - a centred crop verified by
exact per-row byte values (not just dimensions), a Mono16 (2-bytes-per-pixel) case, invalid
height-fraction rejection, and `ComputeCroppedHeight`'s own clamping at both ends of its range. NOT
YET VALIDATED against a real full-frame capture - built and unit-tested against synthetic SER data
only, same caveat every recent Processing addition above carries at this point.

Also real: a first slice of Phase 4's still-outstanding **live line-identification overlay** -
`SolScan.Processing.Spectrum`'s `SpectralProfileExtractor`/`SpectralLineIdentifier`, identifying which
of the 12 named `SpectralRay` lines an observed profile is centred on. Prompted by wanting to develop
and validate that overlay against the user's own real full-frame `.ser` captures (the same files the
Crop SER utility above exists for) rather than live hardware - which led first to investigating
astro4j's `DeepLineIdentifier`/`SpectralWindowIdentifier` as a porting source, then, at the user's own
request ("nice not to just plagiarise Cedric's code"), to designing an original, deliberately leaner
algorithm instead of a faithful port. Planned via `EnterPlanMode` given the size (new Core/Processing
types, a new bundled reference dataset with its own licensing, a new console project). `DeepLineIdentifier`
and `SpectralLineIdentifier` share the same basic idea - matched-filter correlation against a reference
solar atlas, confidence-gated - because that idea is standard spectroscopy technique (radial-velocity
cross-correlation spectroscopy, arc-lamp wavelength calibration), not really astro4j's own invention;
SolScan's own version is leaner because its scope is narrower (only 12 named lines matter, not an
arbitrary point in 3900-6800Å), so it skips `DeepLineIdentifier`'s own "curate the deepest N% of the
atlas as candidate hypotheses" step (every named line is just tested directly) and its six fixed
instrumental-broadening hypotheses (deferred - a single data-driven blur estimate is enough for v1);
telluric correction is deferred the same way. See `SpectralLineIdentifier`'s own doc comment for the
full reasoning.

`SolScan.Core.Processing.SpectralDispersion` (Å-per-pixel from a `SpectrographProfile`'s own optics)
is new too, and independently re-derived from the diffraction grating equation rather than ported from
astro4j's `SpectrumAnalyzer.computeSpectralDispersion` - standard grating-spectrometer physics
published well beyond astro4j itself (e.g. in Christian Buil's own Sol'Ex documentation), so re-deriving
it costs little and keeps the "original design, not a port" line real for this piece too. Cross-checked
(not copied) against astro4j's own published formula by working the algebra through for order 1 - the
two agree exactly, the expected result for two independent derivations of the same physics. Guards
against wavelength/instrument combinations with no real diffraction solution (`asin`'s domain), rather
than silently returning `NaN`. This is also the dispersion calculation the still-outstanding exposure
calculator needs (CLAUDE.md's own astro4j/`ExposureCalculator` note already flagged these as one
calculation to share) - landed here first, reusable once that calculator's own turn comes.

`SpectralProfileExtractor` turns the same `float[,]` `FrameAverager` already produces and the
`QuadraticPolynomial` `SpectralLineCurvatureDetector` already fits (both real, already validated
against a real capture, reused as-is) into a 1D intensity profile indexed by pixel-shift from the
fitted line's own centre - new, original code, a cousin of `DiskReconstructor`'s own row-extraction
but built for a correlation profile, not image reconstruction (no 5-tap anti-aliasing - see its own
test's note on why a plain 2-tap linear interpolation has real, expected quantization error against a
narrow synthetic test dip, without that mattering for real spectral profiles). `SpectralLineIdentifier`
correlates that profile against a small bundled reference dataset - one `ReferenceWindow` per named
line (±8Å around its catalog wavelength, at 0.01Å resolution, matching astro4j's own atlas resolution -
a reasonable choice since real SHG dispersion, tens of mÅ/pixel, resolves several reference samples per
observed pixel) - via a plain normalized (Pearson) cross-correlation with a small ±3px lag search to
absorb curvature-fit slop, requiring the winner to clear both an absolute score threshold and a margin
over the runner-up before being reported (both starting values - `MinScoreThreshold`/
`MinMarginOverRunnerUp` - not yet tuned against real data). "No confident match" is a valid, expected
answer, not a failure mode.

The bundled reference dataset (`SolScan.Processing/Spectrum/Resources/reference-windows.bin`, ~38KB -
a small derived excerpt, not the 14MB source atlas) is itself real, not a placeholder: extracted from
the user's own local BASS2000 `atlasvi.dat` checkout (`C:\Source\Repos\JPhilC\astro4j\jsolex-core\src\bass2000\atlasvi.dat`)
by a new, independently-written reader (`SolScan.Tools/AtlasExtractor.cs`) for BASS2000's own published
raw layout - understood by reading astro4j's own converter once, then confirmed directly against the
real file's actual bytes (8 header lines; each data line's first token is an integer Å wavelength
incrementing by exactly 1 per line, last token a fixed 2000-character digit blob = 500 four-digit
intensity samples spaced 0.002Å apart) rather than trusted from the Java source alone - "how to read a
public file's own fixed layout" isn't the algorithmic design worth keeping independent, unlike the
identification algorithm itself. `NOTICE` gained a new top-level "Third-party reference data" section
for this (CC-BY-SA-NC, attribution, non-commercial) - a real licensing wrinkle worth being explicit
about, mirrored into a new `Resources/README.md` (same per-resource provenance-documentation precedent
as `SolScan.External`'s own vendor READMEs) - and lost its stale claim that `DeepLineIdentifier` itself
is still on the astro4j-porting roadmap, since this work deliberately isn't that.

`SolScan.Tools` is a new project - the first standalone console tool in this solution, confirmed
genuinely novel (no prior dev-utility precedent existed anywhere in the repo before this). Two
commands, neither shipped to end users (not referenced by `SolScan.Setup`/`SolScan.Bundle`):
`extract-atlas` (above, a one-off/reproducible regenerate-the-bundled-resource command) and `annotate`
(`dotnet run --project SolScan.Tools -- annotate <full-frame.ser> [--expected <RayLabel>]`) - averages
a real recording (`SerReader`/`FrameAverager`, already real), extracts a profile, runs
`SpectralLineIdentifier`, and prints the winning line plus every candidate's score to the console, for
validating the identifier against the user's own real captures (reading equipment info from the file's
`.equipment.json` sidecar via the existing `ICaptureMetadataStore` where present, `--pixel-size`/
`--binning` as manual overrides otherwise) - text output only for v1, no graphical overlay, matching
the "leanest thing that validates the pipeline first" approach the whole feature takes.

Covered by `SpectralDispersionTests` (a hand-worked H-alpha/Sol'Ex example, positivity across every
named ray, linear scaling with pixel size/binning, and a real "no valid diffraction solution" guard
case), `SpectralProfileExtractorTests` (a known Gaussian dip recovered at the right shift, a curved
line correctly tracked rather than assuming a fixed row, out-of-frame shifts reported as no-value
rather than clamped, and the extractor's own input validation), and `SpectralLineIdentifierTests` (two
deliberately distinctive synthetic candidate shapes correctly disambiguated with a real confidence
margin - single symmetric Gaussians were tried first and found to correlate too well against each other
regardless of width, a genuine finding about the algorithm's own limits with under-distinctive test
shapes, not just a test-construction detail - a flat/noisy profile correctly reported as no confident
match, and, most importantly, a real regression test matching a profile sampled from the *actual*
bundled H-alpha window against the real embedded resource end-to-end). **Now validated against two of
the user's own real full-frame captures** via `annotate` (both real ASI678MM recordings, full
3840x2160 sensor frames): the first, an isolated H-alpha line, scored 0.901 vs. a 0.753 runner-up
(margin 0.148) - a clean win that nonetheless missed the original `MinMarginOverRunnerUp` (0.15) by
0.002, so that constant was lowered to 0.10 (`MinScoreThreshold`, 0.6, was untouched - nothing in
either file implicated it). The second file's best guess (Sodium D1, score only 0.449 - nowhere near
either threshold) turned out, once the user overlaid JSol'Ex's own Spectrum Browser on the paused live
video to check, to be centred in the gap between the Na D1/D2 doublet, not a single isolated line -
correctly reported as no confident match either way, and a genuine, now-documented limitation (see
`SpectralLineIdentifier`'s own doc comment update): each candidate is scored assuming it's the *only*
line near the profile's centre, so two real lines within the same ±8Å reference window dilute each
other's own correlation. `SpectralLineIdentifier.cs`'s `MinMarginOverRunnerUp` doc comment records this
whole finding in full. Still only two real data points behind the current threshold values - worth
more real-file validation before treating them as settled.

Also real: the actual **live Capture-view spectral overlay** - line labels hovering over the 12 named
lines currently visible in the live preview, rolling in/out as the grating is turned, plus a coloured
gradient band on the preview's left edge - the live piece the offline identification core above was
always meant to feed into. Planned via `EnterPlanMode`; one design question - what to show when the
identifier can't confidently lock onto a single line - was put to the user directly rather than assumed:
confirmed to show the current best guess dimmed (not hidden), sharpening to full opacity on a confident
lock, since a novice searching for a line needs help most exactly while still searching, and a dimmed
possibly-wrong guess is more useful than nothing.

`SolScan.Core.Processing.SpectralColor` is new - `SpectralRay.ToRgb`/`ToSimpleRgb`'s wavelength-band
approximation, generalised from one ray's own fixed wavelength into a reusable `ToRgb(double)`/
`ToSimpleRgb(double)` pair callable at any wavelength (`SpectralRay`'s own two methods now just
delegate to it - behaviour-identical, confirmed via a new `SpectralColorTests` case asserting byte-for-
byte equality across all 12 named rays) - what the gradient band samples continuously across the
visible range, since the 12 named rays alone aren't enough coverage for a continuous strip.
`SolScan.Core.Camera.FramePreview` gained a public `ComputeDownsampleScale(frameWidth, frameHeight,
maxDimension)`, extracted from its own previously-private `ComputeDownsampleGrid` (which now calls it
internally rather than duplicating the math) - the first missing link in mapping a spectral line's raw-
frame row onto anything the live preview UI can use.

`SolScan.Processing.Spectrum.SpectralOverlayAnalyzer` (new, static) is the live single-frame
orchestrator - the live-preview counterpart to `SolScan.Tools`' own `annotate` command, chaining the
same already-real pieces (`SpectralLineCurvatureDetector` → `SpectralProfileExtractor` →
`SpectralLineIdentifier`) against **one frame at a time** rather than a whole file's own
`FrameAverager`-cleaned average, since live use can't wait for a whole recording - noisier than every
prior validation's data, a real, explicitly acknowledged trade-off. Takes the winning (best-guess, not
necessarily confident) candidate as the anchor at pixel-shift 0, computes dispersion at its own
wavelength, then projects every other one of the 12 named rays onto a pixel-shift via
`(ray.WavelengthAngstroms - anchor.WavelengthAngstroms) / dispersion`, keeping only the ones that land
within the frame's own sampled range - returning each visible line's position both as a pixel-shift and
already resolved to a raw-frame row (`VisibleSpectralLine.RowInFrame`, via the fitted curvature
polynomial evaluated at the frame's own horizontal centre - one representative row per line, not one
per column, since a live overlay places one label per line). Pure, no WPF dependency - covered by
`SpectralOverlayAnalyzerTests` against a synthetic single frame (packed into a real `CameraFrame`'s
byte layout, exercising `FrameConversion.ToFloatArray` too, not just the pieces downstream of it).

`CaptureViewModel` gained a live-resolved `SpectrographProfile?` field (`_connectedInstrument`,
resolved via a new shared `ResolveSpectrographProfile` helper - refactored out of `WriteCaptureMetadata`,
which now calls it too rather than duplicating the same `AppSettings.SelectedEquipmentSetupId` →
`IEquipmentLibrary` lookup chain) - previously this lookup only ever ran once, at recording start, never
kept live for the preview loop to use. Inside `ProcessPreviewFrame` (the same already-throttled,
already-offloaded-to-the-thread-pool step `FocusAnalyzer.MeasureEdgeSteepness` already established as
the precedent for non-trivial full-resolution per-frame analysis - see the collimator-focus-aid entry
above), the spectral overlay runs on its *own*, slower cadence (`SpectralOverlayUpdateInterval`, ~400ms - since replaced by the shared 300ms `SpectralAnalysisInterval` and moved onto its own worker, see the camera-focus-aid entry -
a person turning a grating by hand doesn't need 20fps responsiveness, and this is real work given the
real 3840x2160 frame size confirmed on the user's own hardware) rather than every throttled preview tick,
gated by a plain (not `Interlocked`) `_lastSpectralOverlayUtc` field - safe without extra locking since
`_previewProcessingInFlight`'s existing single-flight guarantee already ensures at most one
`ProcessPreviewFrame` call is ever running at once. Two new persisted toggles,
`ShowSpectralLineLabels`/`ShowSpectralColorBand`, follow the exact same `AppSettings` field →
`[ObservableProperty]` + constructor load → `partial void OnXChanged` → `PersistAppSetting` → bound
control shape `ShowCrosshairReticule` already established - both default **on** (unlike every Reticule
toggle, which defaults off) since the target audience - someone still learning to find a line - benefits
from seeing this without first discovering a settings toggle. `BuildSpectralLineLabels`/
`BuildSpectralGradientStops` (new private helpers) run on the same background thread as the rest of
`ProcessPreviewFrame`'s work; the gradient stops are built as a real WPF `GradientStopCollection` there
too, but explicitly `.Freeze()`d before being handed back to the UI thread, since an unfrozen
`Freezable` is thread-affine to whichever thread created it and would otherwise throw once the UI
thread (which didn't create it) tried to use it.

`CaptureView.xaml` gained a `Canvas` (`SpectralLabelOverlay`) for the line labels - but unlike
`ReticuleOverlay` (deliberately fixed against the viewport, ignoring zoom/pan), this one sits *inside*
the same `ScrollViewer` as `PreviewImage`, as a sibling in a shared `Grid` cell, so it automatically
renders at the exact same size and scrolls/zooms together with the actual image content with no extra
plumbing - a label only needs `SpectralLabelOverlay.ActualHeight / PreviewBitmap.PixelHeight` (computed
in `CaptureView.xaml.cs`'s new `MapPreviewBitmapYToControlY`) to convert its already-downsampled
`SpectralLineLabel.PreviewBitmapY` into an actual on-screen row. The colour gradient band, by contrast,
*is* fixed against the viewport like the Reticule (a narrow `Border` docked to the left edge, outside
the scrolled `Grid`) - it's a reference strip, not something meant to track image content pixel-for-
pixel - filled by a `LinearGradientBrush` whose `GradientStops` binds directly to
`CaptureViewModel.SpectralGradientStops`; since those stops use relative (0-1) offsets, the brush scales
to whatever height the `Border` renders at with no extra code needed. Labels themselves are persistent
`TextBlock`s keyed by `SpectralRay` (`CaptureView.xaml.cs`'s new `_spectralLabelElements` dictionary),
kept across updates rather than recreated, so a position/opacity change can be animated
(`DoubleAnimation` on `Canvas.Top`/`Opacity` - **this app's first use of WPF animation**, confirmed no
existing `Storyboard`/`DoubleAnimation` precedent anywhere before this) instead of flickering; a label no
longer in the current visible set fades out and is removed only once the fade completes, giving the
"roll in/out" effect asked for. `IsConfident` (whether a line is the identifier's own
`IdentifiedRay`, not just its best guess) drives full-opacity vs. a dimmed 0.45, matching the confirmed
UX decision above. Positioned along the overlay's right edge (`Canvas.Right`) with a fixed upward offset
so the label hovers above its target row rather than centring on it, per the feature's own "hovering
above" framing - no dashed connector line back to the exact row (unlike JSol'Ex's own Spectrum Browser,
which the user's own screenshot showed using one) - a real, deliberate v1 simplification, not an
oversight; worth adding later if a plain label position turns out not to be clear enough on its own.

Also real: a **"Load Test Image…" dev feature** (Capture view's new "Spectral Overlay" drawer section) -
loads a PNG/TIFF file and feeds it through the exact same pipeline a live camera frame would go through
(`ProcessPreviewFrame`: histogram, contrast stretch, focus aid, and the spectral overlay itself), so the
overlay can be tried and tuned with zero hardware connected - requested directly once the user started
trying the overlay live and ran into exactly the kind of coordinate/positioning bugs below that are far
faster to iterate on against a static file than a live camera feed. `SolScan.App.Services.TestImageLoader`
does the actual loading (WPF's `PngBitmapDecoder`/`TiffBitmapDecoder` → `FormatConvertedBitmap` to
Gray16 - same approach `ProcessViewModel`'s own PNG preview loader already uses, generalised to an
arbitrary source file/format rather than only SolScan's own previously-saved output). Surfaced a real
gap while wiring it in: the spectral overlay previously only worked once a real camera was connected
(needed its own resolved `CameraProfile.PixelSizeMicrons`), which a loaded test image has no equivalent
of. Fixed properly rather than special-cased: `_connectedInstrument` (the resolved SHG) is now resolved
in the constructor too, not just at camera connect - it was only ever an Equipment Setup concept, never
actually dependent on a camera being connected, so tying it to camera-connect was an unnecessary
shortcut from when the live overlay first landed - and a new `AppSettings.SpectralOverlayFallbackPixelSizeMicrons`
(default 2.0µm, editable, persisted, same 4-step pattern as every other Capture toggle) is used whenever
no connected camera's own pixel size is known, the normal case for a test image but also a graceful
degrade if a connected camera's SDK never reports one.

**Two real coordinate-mapping bugs were found and fixed** using this new feature, both confirmed against
the user's own real loaded test images before and after:
1. `SpectralLabelOverlay` (the label `Canvas`) was assumed to automatically end up exactly the same size
   as `PreviewImage` since they shared one `Grid` cell - true in general, but not once `PreviewImage` is
   centred and *smaller* than the ScrollViewer's viewport (e.g. Auto zoom on a smaller source image, the
   user's own real case), where the Canvas ended up sized/positioned against the wider `Grid` cell
   instead of the actual displayed image bounds. Fixed by explicitly binding `SpectralLabelOverlay`'s
   `Width`/`Height`/alignment to `PreviewImage`'s own, removing the assumption entirely.
2. A genuine layout-timing race: setting `PreviewImage.Width`/`Height` (in `UpdateImageSize`) only
   *schedules* a WPF layout pass, it doesn't run one synchronously - so `UpdateSpectralLabelOverlay`,
   which can run immediately afterward in the same property-changed cascade (a new `PreviewBitmap`
   triggers both), was reading `SpectralLabelOverlay.ActualHeight` before that pending layout had
   actually happened, using the *previous* frame's size. Fixed with an explicit `SpectralLabelOverlay.UpdateLayout()`
   call before reading `ActualHeight`, forcing the pending pass to resolve first.

A new `CaptureViewModel.SpectralOverlayDiagnosticsText` (shown in the same drawer section, monospace) was
what actually diagnosed both of these - real numbers (best-guess ray/score/confidence, the curvature-
detected centre row, dispersion, visible-line count) each time the overlay runs, same "check real
numbers instead of guessing from a screenshot" approach that worked for tuning `SpectralLineIdentifier`'s
own thresholds earlier. It's what showed the identifier *was* correctly re-analysing each loaded image
(different best-guess ray, different score, different centre row per file - ruling out a "stuck on stale
data" theory) rather than a genuine bug, and what explained an otherwise-confusing observation (the same
four named lines - Helium D3/Iron Fe I/Sodium D2/Sodium D1 - kept appearing as "visible" regardless of
which one won as anchor): confirmed by hand-checking the catalog, those four real lines sit within ~20Å
of each other, comfortably inside the wide (±51Å) search window the user's own test images' dispersion/
frame-height happened to produce - correct behaviour, not a bug, once the actual numbers were checked
rather than assumed. `Math.Max(0, ...)` also now clamps a label's target `Canvas.Top` to never go
negative - a line detected close to the frame's own top edge has no room above it to hover into anyway,
and `ClipToBounds="True"` would otherwise just cut the label off, which looks identical to "pinned to the
top" from the earlier bugs even once those were fixed.

**Definitively verified**, not just plausible: a purpose-built clean test image (a single smooth Gaussian
dip, no curvature, at a precisely known row - 300 of 1200 - generated via the same reliable WPF
`PngBitmapEncoder`/`Gray16` path this project already established over `System.Drawing`'s own unreliable
16-bit save for exactly this reason) came back with `centreRow=300.0/1200` - an *exact* match - and the
label visibly sitting right on the line in the running app. The user's own earlier grid-pattern test
images (multiple lines, no clean single dip) were the wrong tool for this specific check, not evidence of
a remaining bug - confirmed by process of elimination once a properly-controlled test image ruled the
coordinate math in.

Also settled directly with the user: the colour gradient band's near-solid appearance on any single real
capture window is **correct, physically expected behaviour, not a bug** - real SHG dispersion means any
realistic window only spans a few Å to a few tens of Å, and common lines like H-alpha sit deep inside a
~135nm-wide band of `SpectralColor`'s wavelength-to-colour approximation where it returns a genuinely
constant colour (that function approximates "which broad region of the visible spectrum", not fine
sub-nm variation). Asked directly whether to keep it true-to-life or exaggerate/stretch the mapping into
an artistic (not physically accurate) always-visible gradient instead - the user chose to keep it
true-to-life, so no change was made; a near-solid band is the expected, correct result for most real
capture windows, not something to "fix".

Still genuinely outstanding: real camera/telescope hardware validation - everything above is confirmed
against loaded still images (synthetic and real-ish TIFF/PNG test files), not a live streaming camera.
Two real, explicitly acknowledged gaps going into that first live-hardware try: single-frame curvature
detection is noisier than every prior validation's `FrameAverager`-cleaned data (live use can't average
across a whole recording the way offline validation could), and the ~400ms overlay cadence / 250ms label
animation duration / 0.45 dimmed-opacity are all starting values, not measured/tuned ones - expect real-
hardware behaviour to drive further changes here the same way it already has for every other live-
hardware feature in this project (the collimator-focus-aid's own four-revision history is the clearest
precedent for this).

Also real: **an ASICap-style Exposure control**, replacing the single slider CaptureView's own Camera
Settings Expander used to have (0.032ms-5s mapped onto one 0-1 log-scale position, see
`ExposureScale.ToSliderPosition`/`FromSliderPosition` - since removed). Requested directly from a
screenshot of ASICap's own real control: a dropdown (`32µs ~ 10ms`/`1ms ~ 100ms`/`100ms ~ 1000ms`/
`1s ~ 5s` - `SolScan.Core.Camera.ExposureScale.Ranges`, matching ASICap's own fixed list except the
last range is capped at 5s rather than its 2000s, since LX mode isn't supported here - see
`ICameraDevice.ExposureMicroseconds`'s own doc comment) picks which sub-range a numeric up/down box
(with spinner `RepeatButton`s) and a plain linear `Slider` operate over, each in that range's own
unit - a new `SolScan.Core.Camera.ExposureRangeOption` record carries the range's bounds/unit/
spinner-step/decimal-places. A same-day follow-up request ("all ranges except 1-2000s [i.e. the
seconds one] should only allow integer values or whole decimals") added `ExposureRangeOption.
DecimalPlaces` (0 for every range but the last) and `CaptureViewModel.QuantizeToRange`, applied to
`ExposureMicroseconds` itself (not just how it's displayed) so a slider drag can't leave the camera
set to some arbitrary fractional-µs/ms exposure the box could never have been used to enter.
`ExposureScale.FindRange` re-selects whichever dropdown range actually contains a value that lands
outside the currently-selected one (auto-readback while Auto is on, a loaded/saved setting, or a
spinner click at a range's own edge). Covered by `ExposureScaleTests` (range-boundary matching,
whole-domain coverage, and that only the last range has `DecimalPlaces > 0`).

Also real: **flat Expander headers**. MaterialDesignThemes' own implicit `Expander` style (merged via
`MaterialDesignScoped.xaml` into both CaptureView.xaml/ProcessView.xaml) indents the header text a
chevron-arrow's width in from the left while the body content below sits flush at the edge - reported
directly by the user as looking wrong once several Expanders were stacked in either view's drawer
panel. `FlatExpanderStyle`/`FlatExpanderHeaderToggleStyle` (in `MaterialDesignScoped.xaml`) replace the
whole header chrome: bold text starting at the same left edge as the body, a bottom divider rule
(`MaterialDesignDivider`) marking it as a header instead of the indent, and the expand/collapse
chevron (▸/▾, flipped via a `ControlTemplate.Triggers`/`TargetName` `Setter` - no new `IValueConverter`,
this app's own established preference) moved to the row's right edge. Applied to all ten Expanders
across both views.

Also real: **VS-tool-window-style pin/dock for the Capture and Process options panels**. Prompted by
the user noticing `md:DrawerHost`'s right-hand drawer (used by both views' settings panels) is
overlay-only by design - it has no way to keep a panel open *and* see the whole preview, since it
always slides in on top rather than sharing space. Planned via `EnterPlanMode` given the size (new
persisted state on both view models, two new `UserControl`s, and a WPF layout pattern - a collapsible,
resizable `Grid` column - this app hadn't combined with `DrawerHost` before). A Pin `ToggleButton`
(`PanelPinToggleButtonStyle` in `MaterialDesignScoped.xaml` - a `md:PackIcon` flipping `Kind="Pin"`/
`"PinOff"` the same `TargetName`-`Setter`-in-a-`Trigger` way the Expander chevron above does; this
app's first use of `PackIcon`, confirmed present in the already-referenced MaterialDesignThemes 4.8.0
package rather than assumed) now sits in each panel's own header, next to the existing close hamburger.
Unpinned (the default - unchanged behavior) is exactly today's floating overlay; pinned docks the
identical panel content into a real, resizable `Grid` column via a `GridSplitter`, shrinking the main
content area so the preview/status area is never covered. Each view's settings content (previously
inline inside `md:DrawerHost.RightDrawerContent`) was extracted into its own `UserControl` -
`CaptureOptionsPanel.xaml`/`ProcessOptionsPanel.xaml`, no `DataContext` override, so the identical
markup/bindings can be instantiated in either the floating drawer or the docked column - and each is
now shown twice (drawer content + docked column), toggled by a new pair of `AppSettings` fields per
view (`CaptureOptionsPanelPinned`/`Width`, `ProcessOptionsPanelPinned`/`Width`, the latter persisted
debounced the same way `CaptureViewModel.PersistSettingsIfConnected`'s own camera-settings writes
already are) plus two derived, non-persisted view-model properties: `IsCaptureDrawerOpen` (settable -
proxies straight through to `IsCaptureOptionsPanelExpanded`, since `DrawerHost.IsRightDrawerOpen`
binds `Mode=TwoWay` - true only while open *and not* pinned) and `IsCaptureOptionsPanelDocked` (get-only,
true only while open *and* pinned) - `ProcessViewModel` carries the identical pair for its own panel.
`CaptureView.xaml.cs`/`ProcessView.xaml.cs`'s new `UpdatePanelDockState` drives the docked column's
`GridLength` directly (a `GridSplitter`-resizable column needs a real pixel `GridLength`, which WPF
doesn't make bindable from a view model - the same "code-behind owns layout math" precedent as
`CaptureView.xaml.cs`'s pre-existing `UpdateImageSize`), clamped to 260-700px; the splitter's own
column, by contrast, needs no code-behind at all - `Width="Auto"` sizing to a `Collapsed` `GridSplitter`
already collapses to 0 for free. A plain `FrameworkElement.SizeChanged` on the docked column's own
host `Border` reports a user's splitter drag back into the persisted width, guarded against
misinterpreting `UpdatePanelDockState`'s own programmatic resizes as a drag. NOT YET VALIDATED against
the running app - confirmed to build clean and pass the existing test suite only; the pin/dock/resize/
persist-across-restart behavior itself hasn't been exercised by the user yet.

Also real: **offline automatic spectral-line identification on the Process view**, wiring up
`LineDetectionMode` (previously a real but entirely unconsumed parameter - see the in-app-tooltips
entry above, which said so plainly at the time) for the first time. Prompted directly by the user, now
that real capture files exist, running into having to hand-pick both Line and Detection Mode on every
file even when there's no `.equipment.json` sidecar to say what was actually studied. Reuses the exact
identification core the live Capture-view overlay and `SolScan.Tools`' own `annotate` command already
validate against real captures (`SpectralLineCurvatureDetector` → `SpectralProfileExtractor` →
`SpectralLineIdentifier`) rather than building anything new - the only genuinely new code is the
wiring: `IShgProcessor.ProcessAsync` gained an optional `LineIdentificationEquipment?
identificationEquipment` parameter (the SHG/pixel-size/binning needed to compute dispersion), and
`ShgProcessor.Process` now runs identification right where it already computes the averaged frame and
curvature-fit polynomial for the curvature-detection step - reusing both rather than re-averaging the
file a second time, since that averaging pass is the expensive part of the whole pipeline (~11 seconds
on the user's own real Sunscan capture, per the reconstruction entry above). `Auto`/`FreeSearch` now
run identification whenever `identificationEquipment` is supplied; `Manual` never does, matching its
own doc comment exactly, and no `identificationEquipment` at all skips identification regardless of
mode (there's nothing to compute dispersion against without it) - the same additive-default-parameter
shape means every pre-existing `ProcessAsync` call site/test needed no changes at all. A confident
identification overrides `SpectrumParams.Ray` for that run only (the calcium-routing check in
`ApplyContrastEnhancement` and the colorization tint/curve choice - reconstruction itself is unaffected
either way, since the curvature fit locks onto whichever line is most prominent regardless of what it
turns out to be named); an unconfident one falls back to whichever Line was configured, same as before
this feature existed. `FreeSearch` is not yet actually distinguished from `Auto` - a true whole-spectrum
search would need a much larger reference atlas than the 12 narrow, named-line windows
`SpectralLineIdentifier` bundles, so it's a documented simplification, not an oversight, matching that
enum's own updated doc comment. `ShgProcessingResult` gained `IdentifiedRay`/`IdentificationScore`/
`IdentificationConfident` so a caller can report the outcome either way - not just when it won.

`ProcessViewModel.BuildIdentificationEquipment` is where "no metadata for the scan" is actually
handled: it reads the already-loaded file's `.equipment.json` sidecar (kept around in a new
`_loadedMetadata` field once `LoadSelectedFile` reads it, rather than re-reading the sidecar a second
time) and falls back field-by-field when any part of it is missing - a Sol'Ex-standard SHG
(`SpectrographProfile.CreateSolEx()`), the existing `AppSettings.SpectralOverlayFallbackPixelSizeMicrons`
(2.0µm default - already the same fallback `SolScan.Tools`' own `annotate` command uses, so this reuses
that one shared, user-editable setting rather than introducing a second one that could disagree with
it), and no binning - so identification still runs, with a documented, possibly-less-accurate
assumption, for a `.ser` file that never had a sidecar at all (a file recorded outside SolScan, or from
before equipment metadata was written), rather than being silently skipped. Always builds and supplies
this (never null) regardless of `DetectionMode` - `ShgProcessor` itself is what decides whether to
actually use it, so there's no cost to supplying it when Manual is selected. A third "Processing
Results" panel field, `IdentifiedLineText` (next to the existing `DetectedLineText`/`DetectedGeometryText`
- a third fragment, though not one of astro4j's own two-part info view, since JSolex has no equivalent
identifier), reports the outcome after a run: which line was identified and its score when confident,
or the best guess plus which configured line was kept instead when it wasn't - reusing
`ProcessParametersViewModel.SelectedRay` to name that fallback rather than re-deriving it. The
`ProcessParametersView.xaml` tooltips for Line/Detection Mode were rewritten to describe the real
behaviour, replacing wording that (accurately, at the time) said Detection Mode did nothing.

Covered by three new `ShgProcessorTests` cases: Auto mode with no `identificationEquipment` supplied
skips identification and still produces images (the regression guard for `ProcessParams.CreateDefault()`'s
own `DetectionMode` already defaulting to `Auto`, so every pre-existing test needed to keep working
unchanged); Manual mode never attempts identification even when equipment *is* supplied, against a
file built from the real bundled H-alpha reference window (so a confident match would happen if it were
attempted - proving the skip is real, not just a coincidental non-match); and Auto mode against that
same file confidently identifies H-alpha and overrides a deliberately-wrong configured Ray
(`CalciumK`), reusing the same "sample the real bundled window to build a genuinely solar-atlas-shaped
test profile" technique `SpectralLineIdentifierTests`' own regression test already established. NOT YET
VALIDATED against a real capture beyond the user's own two `annotate`-tool runs the identification core
itself was already checked against (see that entry above) - this session's own work is new plumbing
around already-validated math, not a change to the identification algorithm itself.

**Follow-up, the same session, once the user actually tried it against a real good-quality scan**: the
offline auto-detect above turned out to be a near-dead end for realistic files, confirmed empirically
rather than just theorized. A real full-frame-but-tightly-cropped capture (4656x130 - width the spatial
axis, only 130 rows of dispersion-axis height, deliberately minimal per this file's own "ROI height"
guidance elsewhere in this document) ran through `annotate` and came back "No confident match (best
guess: Sodium (D1), score 0.477)" - safe (correctly declined, `MinScoreThreshold` is 0.6), but the
user's actually-expected line, Calcium K, ranked 5th out of 12 at 0.393. Not a threshold-tuning problem:
a ±3-4Å crop simply doesn't carry enough of a line's distinguishing shape (companion dips, wing
asymmetry) to tell it apart from several others by correlation alone - `SpectralLineIdentifierTests`'
own pre-existing comment about plain symmetric shapes correlating too well against each other had
already flagged the mechanism, this is just the first real-file confirmation of it actually happening.

The user then found the same file's own JSolex processing log carried a *seemingly* confident answer -
"Detected spectral line Sodium (D2)" - after first reporting "Free search: the profile is too short to
identify a line". Investigated directly against the local `astro4j` checkout (`DeepLineIdentifier.java`/
`SolexVideoProcessor.java`/`SpectrumAnalyzer.java`) rather than assumed: astro4j's "Free search"
(`DeepLineIdentifier`, matching lines across the *whole* 3900-6800Å atlas) has the exact same
`MIN_PROFILE_ANGSTROMS = 3` gate SolScan's own finding just confirmed empirically, and declined for
the same reason. What actually produced "Sodium (D2)" was a *second*, different fallback method,
`SolexVideoProcessor.autoDetectSpectralLine` → `SpectrumAnalyzer.findBestMatch` - restricted to the
user's own configured/known lines (the same idea as SolScan's 12 named rays), but using a different
distance metric (z-score-normalized "area between curves" plus a variation-coefficient term, not
Pearson correlation with a lag search) and, critically, **no confidence gate at all** - it always
returns a best-scoring candidate, falling back to "whichever known line is closest to H-alpha" if
nothing scores well, and its own log line never reports a score because there is no threshold to
report against. So JSolex didn't solve the underlying information-limit problem here - it just always
commits to a guess for this fallback path rather than ever admitting uncertainty, which is a real,
legitimate but different design choice from SolScan's own "no confident match is a valid, expected
answer" stance used everywhere else in this codebase (and Na D1 vs. D2 specifically is exactly the kind
of doublet-gap confusion `SpectralLineIdentifier.MinMarginOverRunnerUp`'s own doc comment already
documents from an earlier real-file case - JSolex's differently-computed answer isn't independently
validated to be *correct*, just differently confident about being unconfident).

Put to the user directly as two design questions rather than assumed: whether SolScan should also
always-guess like JSolex's fallback does (rejected - "keep declining when unconfident", matching this
codebase's existing philosophy everywhere else), and then what to do given that decision confirms
offline auto-detect will rarely fire on real, well-cropped files (chosen: fix the root cause instead of
either backing the feature out or leaving it as a rarely-useful no-op).

**The actual fix**: close the "no metadata for the scan" gap at its source rather than trying to
recover it after the fact from an already-cropped file. `CaptureMetadata.StudiedRay` (previously always
null - "not yet populated by anything") is now genuinely populated: a new `CaptureViewModel.
_lastConfidentSpectralRay` field is updated, from the background preview-processing thread, whenever
the *live* overlay (`SpectralOverlayAnalyzer`, already running against the wide, uncropped preview
where there's real spectral context - not the eventual cropped recording) reaches a confident
identification (`overlay.Identification.IdentifiedRay is not null`) - deliberately never cleared just
because a later tick turns unconfident again (a few noisy frames shouldn't erase a good answer), but
reset to null whenever live view (re)starts (`ToggleLiveViewAsync`, same "not meaningful carried over
from a previous session" reasoning as `_bestEdgeWidthPixels`/`ResetBestEdgeWidth`). `WriteCaptureMetadata`
now reads it into `StudiedRay` instead of a hardcoded null. This can go stale if the user changes lines
and starts recording before a fresh confident read arrives - a known, accepted gap, still strictly
better than always writing null.

On the Process side, `ProcessViewModel.LoadSelectedFile` now prefills `ProcessParametersViewModel.
SelectedRay` directly from `metadata.StudiedRay` when a file's sidecar carries one, and `ProcessAsync`
skips building `LineIdentificationEquipment` entirely for that file (`_loadedMetadata?.StudiedRay is
null ? BuildIdentificationEquipment() : null`) - there's no reason to let the much weaker offline
guesser second-guess an already-known-good, live-confirmed answer. Offline auto-detection
(`LineDetectionMode.Auto`/`FreeSearch`) remains exactly as landed above for the files that still need
it: recordings made before this fix existed, or a StudiedRay that was never confidently reached live -
harmless, occasionally useful, but no longer the primary mechanism this feature relies on for new
captures going forward.

Also real: **two processing-performance fixes**, prompted by the user comparing SolScan's own
processing time against JSol'Ex processing the exact same Calcium K file (2914 frames, 4656x130,
8-bit) with the same options - JSol'Ex's own log reported 29.86s total, "Reconstruction performance:
1062.6 MB/s". Investigated by reading `FrameAverager`/`DiskReconstructor`/`SerReader` directly rather
than guessing, which turned up two concrete, unforced inefficiencies (a third, parallelizing the
per-frame work the way JSol'Ex's own batched/multi-core reconstruction clearly does, was deliberately
deferred - see the "Placeholder" note below - since it needs real architectural care, not a quick fix):

1. **Every single frame read seeked to the far end of the file and back.** `SerReader.ReadFrame`
   unconditionally read the per-frame timestamp trailer (stored *after* every frame's own pixel data,
   at the very end of the file) even though neither `FrameAverager` nor `DiskReconstructor` - both
   hot, whole-file-scanning loops - ever look at a frame's `TimestampUtc` at all (confirmed by reading
   `FrameConversion.ToFloatArray`/`MeanOf`, which only ever touch `Data`/`Width`/`Height`/`BitDepth`).
   So every frame read jumped the file position from "wherever this frame's pixel data is" to "the
   very end of the file" and back, defeating any chance of sequential I/O across the whole
   reconstruction pipeline - a real, measured-pattern cost, not a theoretical one. Fixed with a new
   `ISerReader.ReadFrame(int index, bool includeTimestamp = true)` overload (default preserves the
   original behaviour for every other caller, e.g. `SerCropper`, which genuinely needs real
   timestamps) - `FrameAverager`'s two loops and `DiskReconstructor`'s single loop now all pass
   `includeTimestamp: false`.
2. **Every separately-requested reconstruction image re-read the entire file from scratch.** A
   Raw + Continuum request (exactly what the user's own comparison used) opened the file, read every
   frame for Raw, then opened it *again* and read every frame a second time for Continuum - literally
   doubling this stage's I/O for a 2-image request, tripling for a hypothetical 3rd shift, and so on.
   JSol'Ex's own log ("Processing batch 1", one throughput figure for the whole reconstruction) implies
   it derives every requested pixel-shift image from one shared pass over loaded frames, not a
   separate pass per shift. Fixed by generalizing `DiskReconstructor.Reconstruct` (single pixel shift,
   kept as-is, existing callers/tests unchanged) with a new `ReconstructMultiple` (takes a list of
   pixel shifts, decodes each frame exactly once, and computes every requested shift's own 5-tap blend
   from that one decoded frame per iteration) - `Reconstruct` now just calls `ReconstructMultiple` with
   a one-element list, so it's a genuine generalization, not a parallel code path that could drift.
   `ShgProcessor.Process` now opens one reader for both the "raw" pixel shift (needed for
   Raw/Reconstruction/geometry correction) and the continuum shift (if requested) and reconstructs
   both - or however many are actually needed - in that single pass, instead of the old two
   separately-opened-and-read blocks.

Covered by two new `DiskReconstructorTests` cases: `ReconstructMultiple` given several distinct shifts
against a linear-ramp fixture returns independently correct values for each one (the regression check
that merging Raw/Continuum into one pass didn't mix up which output belongs to which requested shift -
exactly the kind of subtle bug this refactor could otherwise introduce silently), and
`ReconstructMultiple` with a single shift produces byte-identical output to `Reconstruct` (proving the
delegation is behaviour-preserving, on top of `Reconstruct`'s own two pre-existing tests continuing to
pass unchanged). Benchmarked against a real file three sessions later, alongside the other two rounds of
I/O work below - see the "now actually benchmarked" entry further down for the real combined numbers
(41.34s → 15.91s, a 2.6x speedup, beating JSol'Ex's own 34.63s on the same file).

Also real (planned via `EnterPlanMode`, same session): **parallelizing the per-frame work across CPU
cores** - `FrameAverager`'s two loops and `DiskReconstructor.ReconstructMultiple`'s reconstruction loop,
the way JSol'Ex's own "Reconstruction performance: 1062.6 MB/s" implied its batched, multi-core pipeline
does. The blocker this was originally deferred for - a single `ISerReader` instance couldn't safely be
read from multiple threads at once, since `SerReader`'s old `FileStream`/`BinaryReader` pair shared one
mutable position field - is what got fixed, at the user's own explicit request when asked to choose
between a lower-risk workaround (a separate reader instance per worker thread) and "doing it properly":
`SerReader` was rewritten to read via `RandomAccess.Read` against one shared `SafeFileHandle` instead -
every call carries its own explicit file offset, touching no shared mutable state at all, so `ReadFrame`
is now genuinely thread-safe on a single already-open instance. This turned out to be the *smaller*
change, not the bigger one: because no per-caller reader-management changed, `FrameAverager`/
`DiskReconstructor`/`ShgProcessor` needed **no signature or call-site changes whatsoever** - only their
inner `for` loops became `Parallel.For`, each still working against the exact same single `ISerReader`
`ShgProcessor` already opens and passes in today.

`SerReader.Open` now reads the 178-byte header in one `RandomAccess.Read` call and parses fields with
`System.Buffers.Binary.BinaryPrimitives.ReadInt32/Int64LittleEndian` in place of `BinaryReader.
ReadInt32/ReadInt64` - a byte-identical replacement, not a behaviour change, since `BinaryReader` always
reads little-endian regardless of host platform anyway. The file's length is queried once via
`RandomAccess.GetLength` and cached (removing a per-frame-timestamp-trailer-call syscall the old
`_stream.Length` check made every time) rather than re-queried on every `TryReadFrameTimestampUtc` call.
A new `ReadExactly` helper loops until a `RandomAccess.Read` call's requested buffer is fully filled
(defensive - a positional read is permitted to return fewer bytes than requested, even though a single
call reading well within a local file's own bounds essentially always returns the full amount in
practice). Every existing error path (too-short-file throws `InvalidDataException`, bad index throws
`ArgumentOutOfRangeException`, calling before `Open` throws `InvalidOperationException`, a missing
trailer falls back to `DateTime.MinValue`) is unchanged - confirmed by all three pre-existing
`SerReaderTests` cases passing without modification, which is what actually proves the rewrite is
byte-identical in behaviour rather than "probably fine." A new fourth `SerReaderTests` case - the one
genuinely new behaviour being introduced - calls `ReadFrame` concurrently from many threads (20 passes
over 64 distinctly-byte-filled frames via `Parallel.For`) against one already-open reader and asserts
every call still returns the correct, uncorrupted data for its own index; the full suite was also run
four times in a row with no flakiness, since a race condition bug wouldn't necessarily reproduce on
every single run.

`DiskReconstructor.ReconstructMultiple`'s frame loop is now a `Parallel.For(0, frameCount, ...)` -
safe because every iteration only ever reads its own `frameIndex` and writes only to
`outputs[s][frameIndex, x]` for each requested shift `s`, so no two iterations ever touch the same
memory and no locking is needed for the reconstruction work itself (`QuadraticPolynomial.Evaluate` was
confirmed to be a pure call on an immutable `readonly record struct`, safe to call concurrently too).
Progress reporting switched from `frameIndex % 100` to an `Interlocked.Increment`-based completed
counter, since frames now finish out of order. `FrameAverager`'s two passes both use the
`Parallel.For<TLocal>` thread-local-reduction overload (`localInit`/`body`/`localFinally`) rather than
per-iteration locking - pass 1's thread-local `double` max and pass 2's thread-local
`double[height,width]` partial-sum array are each only merged into the shared result once per *thread*
in `localFinally`, not once per *frame*, so parallelizing added no per-frame lock contention. Per the
user's own explicit choice, `FrameAverager`'s two-pass *algorithm* itself (exact max-brightness, then
accumulate) was deliberately left unchanged - JSol'Ex's own cheaper sampled-max approach remains a
separate, still-deferred idea, not folded into this work, since it would change results, not just speed.
Cancellation flows through `ParallelOptions.CancellationToken` in all three loops; since the loop bodies
also call `cancellationToken.ThrowIfCancellationRequested()` with that same token, `Parallel.For`
rethrows a bare (unwrapped) `OperationCanceledException` on cancellation, so existing
`catch (OperationCanceledException)` call sites (`ProcessViewModel.ProcessAsync`) needed no changes.

All 161 tests pass (the pre-existing `FrameAveragerTests`/`DiskReconstructorTests`/`ShgProcessorTests`
cases use tolerant `precision:` assertions on simple constant/ramp fixtures, not exact bit-level checks,
so the different accumulation order from parallelization doesn't trip them). Benchmarked three sessions
later alongside the rest of this I/O work - see the "now actually benchmarked" entry further down: real
combined result 41.34s → 15.91s, comfortably past JSol'Ex's own 34.63s on the same file, not just
matching it (matching it was never the bar anyway - JSol'Ex is a mature, long-optimized pipeline;
meaningful improvement was always the actual goal).

Also real: a **per-run processing log**, requested directly by the user after seeing JSol'Ex's own
`.log` output for the same Calcium K file and asking for something along the same lines. `ProcessingLocations`
gained `GetLogsFolder` (a peer of the existing `GetImagesFolder`'s "raw"/"processed" subfolders, named
"log" - not itself a `DirectoryKind`, since no `GeneratedImageKind` lives there) and a new
`SolScan.App.Services.ProcessingLog` writes JSol'Ex-style timestamped lines (`HH:mm:ss.fff [LEVEL]
message`) to a numbered file there - `<NNNN>_<basename>.log`, sequence starting at `0000` and
incrementing one past whatever's already there each time the same file is re-processed (not
"count of files present", so a manually deleted log in the middle of the sequence doesn't get its
number reused - confirmed with a throwaway scratch script exercising three successive runs against the
same file before landing this in the real codebase). Deliberately not a Core-interface/Infrastructure-
implementation pair the way `ICaptureMetadataStore`/etc. are - a single sequential text-file writer
with no swappable implementation or hardware dependency anywhere, so that ceremony would be pure
abstraction with nothing to abstract over; it writes files directly the same way `ProcessViewModel`'s
own pre-existing `SavePng`/`SaveColorPng` already do.

`ProcessViewModel.ProcessAsync` opens the log (inside the `try` block, not before it - a failure to
create it, e.g. a permissions issue, is reported as an ordinary processing failure by the existing
catch block rather than crashing the command outright) and writes: the output directory, source
filename, recording date, and a fresh, cheap SER header read (frame count, colour mode/bit depth,
width/height - JSol'Ex's own Carrington rotation/B0/L0/P solar-ephemeris line has no SolScan equivalent,
so it's simply absent rather than faked); every `IProgress<string>` message `ShgProcessor` reports
(minus "Reconstructing... N/M" - see the follow-up below), via a progress wrapper that also logs each
message it forwards to the status bar; a final image count/output folder line, "Processing done", and
"Finished in Ns" (a `Stopwatch` wrapping the whole run). JSol'Ex's own per-batch memory-pressure/
throughput lines and parallactic-angle/diameter figures have no SolScan equivalent either (no per-stage
instrumentation to report throughput honestly, and no angular-diameter calculation at all) and are
likewise just absent - the log's content is scoped to what SolScan's own pipeline genuinely computes,
not padded to look like a complete match. Covered by a new `ProcessingLocationsTests` case for
`GetLogsFolder`'s own path convention; `ProcessingLog` itself (in `SolScan.App`, which `SolScan.Tests`
doesn't reference) has no automated test, matching this codebase's existing precedent of not
unit-testing the WPF view-model/service layer directly (`CaptureViewModel`, `SavePng`, `TestImageLoader`
are all in the same position).

**Follow-up, the same session, once the user shared a real side-by-side JSol'Ex log for the same file**:
two real fixes, plus a genuinely useful reframing of the earlier "speeding up processing" findings.

The log's own ordering didn't match JSol'Ex's: the distortion polynomial, spectral-line identification
outcome, and geometry tilt/XY ratio were all originally logged only at the very end (read off the final
`ShgProcessingResult`, after the whole pipeline - including reconstruction, geometry correction, and
contrast enhancement/colorization - had already finished), whereas JSol'Ex logs each fact right when
it's discovered. Fixed by moving these into `ShgProcessor.Process` itself as three new `progress.Report(...)`
calls, right where each value becomes known - immediately after `SpectralLineCurvatureDetector.Detect`
(the polynomial), immediately after `SpectralLineIdentifier.Identify` (confident-match or best-guess
wording, self-contained rather than reusing `ProcessViewModel`'s own panel-formatting helpers, since
`ShgProcessor` already has every value the message needs), and immediately after
`DiskGeometryCorrector.Correct` (tilt/XY ratio). `ProcessViewModel.ProcessAsync`'s own post-hoc
formatting/logging of the same three facts was removed as redundant (the "Processing Results" panel's
own `FormatLineIdentification`/`FormatDetectedGeometry` calls are untouched - a different, independently
-worded rendering for a different UI surface, not the log). A new `Date {header.DateTimeUtc:...}` line
was added to `LogFileHeader` (JSol'Ex's own line was there; SolScan's first cut had simply missed it).
`"Reconstructing... N/M"` is now excluded from the persistent log (filtered in the progress wrapper,
matched by string prefix - still shown live on the status bar) - JSol'Ex's own log has no equivalent
per-frame-batch spam, and roughly 30 near-identical lines per run added noise without adding anything a
reader could act on.

The real log comparison also reframed the previous "speeding up processing" session's own findings.
Breaking down both logs stage-by-stage: SolScan's geometry-correction step (21.40s) is actually in the
same ballpark as JSol'Ex's own equivalent work (ellipse detection 12.14s + geometry resample 8.05s ≈
20.15s) - not a new regression, just genuinely expensive work for a 2832x2832 output both apps pay for.
The real, still-open gap is in the stage already touched this session: SolScan's averaging+reconstruction
took 13.0s moving ~1.76GB (twice, for the two averaging passes) at roughly 200-490MB/s, versus JSol'Ex's
own reconstruction alone hitting 1220MB/s - accounting for nearly the entire ~11.27s difference in total
run time (41.34s vs. 30.07s). So the `RandomAccess.Read`/`Parallel.For` work was a real improvement (no
more double-seeks, no more redundant re-reads) but doesn't come close to the throughput JSol'Ex achieves
for the same I/O-bound work - worth a controlled back-to-back re-run to rule out cold-vs-warm OS page
cache as a confound before concluding further, and worth reconsidering the still-deferred sampled-max
single-pass `FrameAverager` algorithm (currently reads the whole file twice) as the next real lever,
rather than assuming more parallelism alone will close the gap. Not yet investigated further this
session - a real, separate follow-up.

Also real: **closing the I/O throughput gap** - the real, separate follow-up promised above, planned
via `EnterPlanMode` given the size (a new `ISerReader` implementation and a real restructure of
`ShgProcessor`'s own control flow). Investigated JSol'Ex's own "Processing will require approximatively
X MB of disk space"/"Memory pressure factor" log lines directly (`SolexVideoProcessor.java` -
`checkAvailableDiskSpace`, `Runtime.getRuntime().maxMemory()`) before assuming they were the same kind
of input-caching idea being considered here - they aren't: that mechanism sizes how many simultaneously-
held *output* reconstructed images (`width × frameCount × 4 bytes × 3` each) fit in the JVM heap when
many pixel-shift images are requested at once, spilling the rest to temp-folder disk files via
`FileBackedImage`/`MemoryAwareStreams`. SolScan has no equivalent problem today (it never holds many
large output buffers at once), so that code wasn't the model for what actually landed - a genuinely
SolScan-specific design instead, informed by but not copied from it.

Three real changes:

1. **`SerReader`'s own `FileOptions` hint reconsidered**: `Open` used `FileOptions.RandomAccess`
   (`FILE_FLAG_RANDOM_ACCESS`), which explicitly disables Windows' own sequential read-ahead
   prefetching - the right hint for literally-random single-frame lookups, but `FrameAverager`/
   `DiskReconstructor`'s actual access pattern (per-thread, under `Parallel.For`'s own range
   partitioning) is locally sequential, not truly random. Switched to `FileOptions.SequentialScan`
   (`FILE_FLAG_SEQUENTIAL_SCAN`) instead - a one-line change, existing `SerReaderTests` (including the
   concurrent-read case) prove correctness is unaffected either way, since only the OS hint changed,
   not the read semantics.
2. **Real per-stage throughput reporting**, closing the exact gap flagged in the previous entry ("no
   per-stage instrumentation to report throughput honestly") - a new `FrameConversion.FormatThroughput`/
   `FormatBytes` pair (shared by `FrameAverager`, `DiskReconstructor`, and the new type below) formats a
   real, measured data rate (e.g. "1.76GB in 4.13s (426.3 MB/s)") from an actual `Stopwatch` and known
   byte count, reported via `progress` - a genuine SolScan equivalent of JSol'Ex's own "Reconstruction
   performance: N MB/s" line, not a guess.
3. **Eliminate redundant disk re-reads, gated on available memory** - the core lever. A recording's raw
   frames were being read from disk up to three times (`FrameAverager`'s own two passes, plus
   `DiskReconstructor`'s own pass) - real, measured cost identified from the real JSol'Ex comparison
   above. New `SolScan.Processing.Shg.InMemorySerReader`: a from-scratch `ISerReader` implementation
   backed by an already-populated `byte[][]` instead of a file handle - placed in `SolScan.Processing`
   itself (not `SolScan.Infrastructure`, where the real disk-backed `SerReader` lives), since it touches
   no file at all, matching the same "hardware-free supporting type" precedent `FrameConversion` already
   sets there. `ReadFrame` just wraps an already-resident array slot - trivially thread-safe (nothing
   ever mutates after construction), unlike the real `SerReader`, which needed a deliberate rewrite
   earlier this session to become safe for the same purpose. Never carries real per-frame timestamps
   (always `DateTime.MinValue`) since its only consumers already pass `includeTimestamp: false` anyway -
   a deliberate simplification, not an oversight. `Open(path)` throws `NotSupportedException` - it's
   constructed pre-populated via the static `LoadFrom(ISerReader realReader, ...)`, which does the one
   real disk pass (in parallel across cores, same pattern as `DiskReconstructor`'s own loop) and reports
   its own throughput via the same formatter.

   **Crucially, `FrameAverager`/`DiskReconstructor` needed zero changes** - they already accepted a
   plain `ISerReader`, and `InMemorySerReader` satisfies that same contract. All the new logic is
   orchestration inside `ShgProcessor.Process`: after opening the real reader and reading its header,
   the raw video's total byte size (known instantly from the header, no read needed) is compared against
   a new `Func<long> _availableMemoryBytesProvider` (defaults to `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
   - the .NET-native, cross-platform, no-P/Invoke-needed answer to "how much memory can this process
   safely use"; overridable via a new optional `ShgProcessor` constructor parameter, so a test can
   simulate "plenty of memory" or "none at all" cheaply) times a conservative `InMemoryCacheSafetyFraction`
   constant (0.5 - leaves headroom for the rest of the app/OS/the smaller working buffers the later
   stages still need). Comfortably fits → report "Loading frames into memory for faster processing...",
   build an `InMemorySerReader` via `LoadFrom`, and reuse *that same instance* for both `FrameAverager`
   and `DiskReconstructor` - three disk passes become one. Too large → keep today's exact original
   behaviour (a fresh disk reader opened per stage) completely unchanged, plus a warning - per the user's
   own explicit request, not a silent fallback: *"Recording is large (X, available memory Y) - processing
   will re-read it from disk at each stage rather than caching it, which is slower. If this wasn't
   intentional, consider a tighter ROI height at capture time or the Crop SER utility to trim this
   file."* `ShgProcessor.Process`'s reconstruction stage needed a modest restructure to support reusing
   the shared reader (a manual try/finally that either reuses the cached in-memory reader, undisposed
   until both stages are done with it, or opens+owns+disposes a fresh disk reader exactly as before) -
   the only place this branches; every other line is untouched.

Covered by four new `InMemorySerReaderTests` (round-trips real frame data read back from a real
`SerReader`, `Open` throws, never carries a real timestamp, and - matching the real `SerReader`'s own
concurrency test - `ReadFrame` is safe under `Parallel.For` from many threads at once) and two new
`ShgProcessorTests` cases: forcing the caching path (`availableMemoryBytesProvider: () => long.MaxValue`)
and the fallback path (`() => 0`) against the same synthetic file produces byte-identical `Raw`/
`Continuum` output and the same detected polynomial either way (the regression check that this is purely
an execution-strategy change), and the fallback path reports the expected warning message and never the
"loading into memory" one. All 168 tests pass, run three times in a row with no flakiness.

**Now actually benchmarked against the real file**, closing out the "NOT YET benchmarked" caveat this
and the previous two sessions all carried - and turning up a real, worth-recording lesson about Debug
vs. Release along the way. The same real Calcium K file (2914 frames, 4656x130, 8-bit) went from 41.34s
before any of this three-session investigation to **15.91s Release** after it - against JSol'Ex's own
34.63s on the same file (a slightly different run than the 29.86s/30.07s figures earlier entries quote -
real machine/cache variance run to run, not a discrepancy worth chasing) - so SolScan now finishes in
46% of JSol'Ex's own time.

A follow-up, fully-controlled same-code Debug-vs-Release run (Visual Studio F5 vs. the built `.exe`
launched directly) turned up something the raw 2.6x headline number would have hidden: **45.60s Debug
vs. 15.91s Release for the exact same, already-fully-optimized code - a 2.87x difference from build
configuration alone.** Compared against the 41.34s pre-session baseline (whose own build configuration
was never recorded, but is most likely Debug too, since a plain F5 launch defaults to it), this
session's own code, run in Debug, is actually **4.26s slower** than the code before any of this
three-session investigation (45.60s vs. 41.34s) - the 2.6x/2.87x speedup story only appears once JIT
optimizations are actually engaged. Breaking down the new log's own real per-stage figures (all real
measurements, not estimates, thanks to the throughput-reporting work) explains why:

- **Averaging** (in-memory, parallelized): ~9.47s pre-session (disk, parallelized, no caching) → ~10.03s
  in this session's own Debug run - flat to slightly worse. Debug's unoptimized JIT output mutes
  `Parallel.For`'s own gains, and the new "load into memory" step's own overhead (~1.7s) isn't fully
  paid back when the per-thread work itself is still running unoptimized.
- **Reconstruction**: ~3.60s → 2.96s in Debug - a genuine, if modest, improvement even unoptimized.
- **Geometry correction** (never touched by any of this session's work, in any of the three rounds): ~21.4s
  → ~25.2s - most likely ordinary run-to-run variance (background load, thermal, whatever), not a
  regression, since literally no code there changed.

So in Debug, the real per-stage wins are small and get swamped by noise elsewhere in the pipeline; the
real speedup only shows up once optimizations are actually engaged, which makes sense specifically for
`Parallel.For`-based work - Debug's unoptimized IL adds real per-iteration overhead that eats into
exactly the kind of gain parallelism is meant to deliver. Practically this changes nothing about the
real-world value of the work - actual users run the Release-built installer (`SolScan.Setup`'s own
publish pipeline already targets Release, never Debug) - but it's a genuinely useful, non-obvious lesson
worth keeping on record: **future performance testing/comparisons in this codebase should always use a
Release build** - Debug can hide, or even invert, a real improvement, particularly for parallelized/
CPU-bound work like this.

Also real: the **camera-focus aid** - `SolScan.Processing.Spectrum.SpectralLineFocusAnalyzer`, the
second of the two focus aids sunscan-app has (see its entry above), measuring how sharply the *camera*
has resolved a spectral line rather than the collimator's edge sharpness. Reports the FWHM (full width
at half depth, in pixels) of the line the frame is centred on - the number to *minimize*, with a running
"Best" low-water mark, in the same "Focus Aid" Expander as the collimator's edge width (now split into
"Collimator"/"Camera" sub-sections; one "Reset Best" button resets both, `ResetBestFocusCommand`,
renamed from `ResetBestEdgeWidthCommand`). Deliberately not a port of sunscan-backend's `calculate_fwhm`,
which takes the span of samples at or above half the profile's *maximum* - right for a bright peak, wrong
for a Fraunhofer *absorption* dip, and with no allowance for curvature. Instead it reuses the already-
validated `SpectralLineCurvatureDetector` + `SpectralProfileExtractor` (the same pair the live overlay
uses) to *straighten* the line first - the "smile" is an optics property, not a focus one, and a plain
per-row average across a curved line would inflate the width - then finds the dip minimum near the fitted
centre, takes the local continuum as the *lower* of the highest values either side (so half-depth is
always crossable on both), and linearly interpolates the two half-depth crossings for a sub-pixel width.
Depth-relative, so stable across Gain/Exposure; comparable only at the same binning and on the same line
(the line locked onto is whichever the curvature detector picks - the darkest at the centre column).
Reports "no line" rather than a doubtful number: a flat frame, a dip under 3% of its continuum, a missing
crossing, or - found while writing the tests, not anticipated - a sampled window truncated close to the
dip (line near the frame's top/bottom edge), where the shrunken "continuum" gives a plausible but far too
narrow width that would set a false "Best"; each side must extend at least 2x as far as its own half-depth
crossing (`MinSideExtentOverHalfWidth`). Only measured while the Focus Aid Expander is open, smoothed by a
3-reading rolling median (shorter than the collimator's 5, since it updates less often).

**Preview-lag bug found on first real use, and its fix.** The first version ran this inline in
`ProcessPreviewFrame`, and the user reported a very laggy preview while adjusting focus (even though the
capture rate read ~9.3fps). Measured in a Release build on a 3840x2160 Mono16 frame: the plain
`SpectralLineCurvatureDetector.Detect` costs ~790ms (it walks every column, allocating lists and striding
down a 2D array - fine for the offline pipeline's one averaged frame, not for live use), and
`ProcessPreviewFrame` is single-flight, so that stalled every preview frame arriving meanwhile. The
spectral overlay ran the same fit inline too, so it had the same latent problem. Two fixes:
1. `SolScan.Processing.Spectrum.LiveCurvatureFitter` fits on a column-decimated copy (stride ~width/960, so
   4 on the real sensor; every row kept, since the sub-pixel line centre depends on vertical resolution) and
   rescales the polynomial back to full-frame columns - ~143ms vs ~808ms, and a stride of 1 (narrow frames)
   is exactly the plain detector's fit. `SpectralOverlayAnalyzer.Analyze` uses it now too (new optional
   `curvature` parameter to share a fit; the offline pipeline is untouched and still uses the full detector).
2. `CaptureViewModel.TryStartSpectralAnalysis`/`RunSpectralAnalysis` move both consumers onto their own
   single-flight worker (`_spectralAnalysisInFlight`, separate from `_previewProcessingInFlight`), on a
   *copy* of the frame (the ASI driver reuses a ring of 8 buffers, so a long analysis could otherwise read one
   the camera has since overwritten), sharing one fit per pass and one cadence (`SpectralAnalysisInterval`,
   300ms - replaces the overlay's old separate 400ms interval). A slow analysis can now only delay its own
   readouts, never the preview. Measured after: fit ~143ms, a full line-focus measurement ~170ms (~33ms given
   a shared fit). `LoadTestImage` now refuses with a status message if a pass is still running (previously it
   could rely on the inline path).
The collimator's `FocusAnalyzer.MeasureEdgeSteepness` is still inline on the preview thread (~60-80ms per frame
in Release), but is now *gated* (see the follow-up below) so it only costs that while something is showing it.

**Follow-up: gating, separate resets, and the focus-graph window.** Requested after the above:
- **Gating.** Both focus aids now run only while their readout is visible: the Focus Aid Expander is open *or*
  the pop-out graph window is (`IsFocusAidExpanded || _isFocusGraphOpen`). Before this the collimator maths ran
  on every preview frame regardless. Note the Expander's state is persisted and closing the options drawer doesn't
  collapse it, so an Expander left open still runs with the panel hidden - gating on real panel visibility is a
  possible further step, not done. The spectral *overlay* is independent of all this (its own toggles).
- **Separate resets.** `ResetBestEdgeWidthCommand`/`ResetBestLineWidthCommand` replace the single
  `ResetBestFocusCommand` - the two aids are adjusted independently, so resetting one shouldn't discard the other's
  best. Live-view start still resets both (`ResetAllBestFocus`).
- **Focus-graph window** (`Views/FocusGraphWindow.xaml`, opened by "Graphs…" in the Focus Aid Expander via
  `CaptureViewModel.OpenFocusGraph`) - the counterpart of sunscan-app's `Spectrum.js` chart, which plots a live 1D
  profile with its FWHM (the vertical/dispersion-axis profile for camera focus; the horizontal/intensity profile for
  the spatial axis). Relevant to SolScan's approach because both aids already build exactly those profiles
  internally; the graph exposes them and, beyond sunscan's, overlays what each number was derived from: for the
  camera aid the continuum level and the half-depth bar between the two FWHM crossings; for the collimator each
  measured edge's 10%/90% crossing points and its low/high plateau levels. A no-line/no-edge result still plots the
  trace (plus whatever reference levels were found), so you can see *why* it was rejected. To support this,
  `EdgeFocusStats`/`SpectralLineFocusStats` gained an optional trailing `Detail` (`EdgeFocusDetail`/
  `SpectralLineFocusDetail`); existing positional construction and every prior test are unaffected. Drawn by a small
  dependency-free `ProfilePlot` control (`OnRender`, same draw-our-own-geometry approach as the Capture histogram -
  no charting library added) from `ProfilePlotData`/`PlotLine` in `SolScan.App.ViewModels`. Modeless, `Topmost`
  like Hand Control, DataContext is the singleton `CaptureViewModel` itself (no separate view model - it's a pure
  view over properties that already existed plus `LineProfilePlot`/`EdgeProfilePlot`, populated only while the
  window is open). Covered by new tests asserting the detail agrees with the reported numbers (crossings' distance
  = width, half level midway between floor and continuum) and that no-line/no-edge results still carry the profile.
  NOT YET VALIDATED visually against the running app or real hardware - `ProfilePlot`'s layout/labels are
  build-verified only. Also shows the line's depth as context (a very shallow line makes the
width less reliable). Covered by `SpectralLineFocusAnalyzerTests`: analytic Gaussian FWHM recovered for
three widths, sharper-vs-softer ordering, brightness-scale independence, an off-centre dip, and the
no-line cases above, plus a full-frame case proving a strongly curved line measures the same as a
straight one. NOT YET VALIDATED against real hardware/optics - synthetic data only; the 3% depth floor,
window size (a fraction of frame height, min 48 rows) and the 300ms cadence are starting values, not calibrated
ones - expect the same real-hardware iteration the collimator aid went through. The overlay and this aid share one curvature fit per pass (see above).

Also real: **app-wide scrollbar style.** The Capture preview's scrollbars (shown when zoomed past the viewport)
were invisible: the preview is dark but the MaterialDesign *Light* theme merged into that view draws the thumb
dark grey. Fixed first for that ScrollViewer alone (confirmed by the user in the running app), then promoted to
every scrollbar so the app is consistent: `Themes/ScrollBarStyles.xaml` - orange `#FFFFB347` thumb (same accent
as the focus graphs), own templates for both orientations, deliberately not based on MaterialDesign's style.
The track is a semi-transparent *grey* (`#26808080`), not the white tint of the preview-only version, so it reads
on the app's light views as well as its dark surfaces. It's merged in **two** places, both required: `App.xaml`
(plain-WPF views, windows, popups) and `MaterialDesignScoped.xaml` after the MaterialDesign dictionaries - a view
merging that theme into its own `UserControl.Resources` resolves MaterialDesign's implicit `ScrollBar` style
first (closer scope), so App-level alone would leave Capture/Process on the old look. The all-scrollbars version
on the light views is build-verified only.

Placeholder: within Phase 4 itself: no exposure/fps calculator, no wide/ROI *view
toggle* (see the centred ROI note above for what's real there instead). (The live line-ID overlay
and both focus aids are real now - see their "Also real" entries.) Phase 2's mount control also
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
   vendors - real ZWO/Altair hardware needs the `SolScan.External` git submodule checked out with
   its real (Git-LFS-tracked) binary content pulled (`git submodule update --init`, with Git LFS
   installed - see `SolScan.External\README.md`, and "Vendor camera SDK binaries" below), which a
   plain `git clone` doesn't do on its own. The sub-items below (exposure calculator, wide/ROI
   view, focus aids, live line-ID overlay) are still outstanding. Carries over the sunscan-app UX
   features noted above:

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
   - a camera focus aid (spectral-line-width measurement - narrower FWHM means sharper camera focus) -
     **done**, see the "camera-focus aid" entry under "Also real" (`SpectralLineFocusAnalyzer`)
   - a collimator focus aid (port `focus_analyzer.py`'s disk-edge-sharpness measurement - sharper
     disk edges mean better collimator alignment) - **done**, see the "Also real" note above
     (`FocusAnalyzer`/CaptureView.xaml's "Focus Aid" panel)
   - a live line-identification overlay for the wide view, so labeled Fraunhofer lines scroll into
     place as the diffraction grating is rotated - **done**, see the "Also real" entries above
     (offline identification core, then the live Capture-view overlay itself: labels + colour
     gradient band). The astro4j-native-port-vs-drive-JSolex's-server trade-off this bullet used to
     carry as undecided settled in favour of a native implementation - needs to work fully offline,
     ruling out any runtime dependency on a separate JSolex process - and, differently than first
     expected, as a deliberately independent, leaner design rather than a port of astro4j's own
     `SpectralWindowIdentifier`/`WavelengthSolution`/`TelluricTransmission` (at the user's own
     request, "nice not to just plagiarise Cedric's code" - see `SpectralLineIdentifier`'s own doc
     comment for the full reasoning). Real, deliberately deferred gaps, per `SpectralLineIdentifier`'s
     own scope: no tracking/confidence-building across successive frames the way astro4j's own
     `SpectralWindowIdentifier` does (this version re-scores fresh each throttled tick), no telluric
     correction, no multiple instrumental-broadening hypotheses - add only if real validation shows
     they're actually needed. `SpectrumParams.Ray` still isn't auto-driven by the overlay (Phase 6,
     the Process view's Process Parameters panel) - whichever labeled line sits nearest the ROI's
     vertical centre is the one actually being studied, so the overlay could set it automatically
     instead of asking the user to also tell SolScan what it just showed them - not yet wired, still a
     real future wiring change
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
   via `IProcessParamsStore` and originally edited across three new Options tabs (Process Parameters/
   Image Enhancement/Image Selection) rather than a separate dialog - later moved onto the Process
   view itself as a dockable panel, see "Process pipeline options move onto the Process view" below -
   plus `ProcessingLocations.GetOutputFolder`'s fixed "output folder sits next to the source .ser file,
   named after it" convention - still no reconstruction at this point, just enough of the parameter
   surface to make a future one's output meaningful.
   Third slice (real - see "What's real vs. placeholder" above for the full picture): `IShgProcessor`/
   `ShgProcessor` in `SolScan.Processing.Shg` - real spectral-line-curvature detection
   (`SpectralLineCurvatureDetector`, ported from `SpectrumFrameAnalyzer`) and real reconstruction
   (`DiskReconstructor`, ported from `SolexVideoProcessor.processSingleFrame`), producing real
   `Raw`/`Reconstruction`/`Continuum` output images from a Process button, verified against the user's
   real Sunscan capture. Fourth slice (also real, its own genuinely separate algorithm from
   line-curvature detection, as planned): ellipse fitting and geometry correction
   (`DiskEdgeDetector`/`DiskGeometryCorrector` and friends - see "Also real" above for the full list),
   producing a real `GeometryCorrected` image. Fifth slice (also real): contrast enhancement -
   `SolScan.Processing.Stretching`'s `AutoStretchStrategy`/`ClaheStrategy`/`MultiScaleClaheStrategy`,
   producing a real `GeometryCorrectedProcessed` image for every `ContrastEnhancementMode` value - see
   "Also real" above for the full writeup, including why AutoStretch turned out to be the *bigger* of
   AutoStretch/CLAHE (it depends on CLAHE internally), not the cheaper first cut originally assumed, and
   why CLAHE2 turned out to be the smaller one (it just reuses `ClaheStrategy` at several tile sizes).
   Sixth slice (also real): the Colorized image - `SolScan.Processing.Color`'s `ColorCurve`/`RgbHsl`/
   `Colorize`, producing a real `GeneratedImageKind.Colorized` output (H-alpha's fixed colour curve, or
   a wavelength-approximated tint for every other named line) - see "Also real" above ("the Colorized
   image") for the full writeup, including why this was the first kind ported from JSolex's own
   *Advanced* Images section rather than Basic Images - a distinction SolScan itself no longer carries
   (see below). Seventh slice (also real): the virtual eclipse image -
   `SolScan.Processing.Shg.Coronagraph`/`DiskFill`, producing a real
   `GeneratedImageKind.VirtualEclipse` coronagraph-style output - see the "Basic/Advanced Images split
   is gone, and the virtual eclipse image landed" entry above for the full writeup. That same entry is
   also where v1's image set was decided closed: JSolex's fuller catalogue (Doppler, redshift, active
   regions, Debug Options, custom ImageMath scripts, `DeepLineIdentifier`/`SpectralLineCatalog`-driven
   line identification, ...) is out of scope for SolScan by choice now, not "still needed" - anyone
   wanting it has the original SER file to hand JSol'Ex itself. A results panel mirroring JSolex's
   two-part info view (detected line + geometry tilt/xyRatio) is real too - see the "Processing Results
   panel" entry further down for the full writeup; `ShgProcessingResult.DetectedLinePolynomial`/
   `DetectedTiltDegrees`/`DetectedXyRatio` are no longer only ever shown as a one-line status-text
   summary. Eighth slice (also real): automatic spectral-line identification, finally wiring up
   `LineDetectionMode` (previously a real but unconsumed parameter) - see the "offline automatic
   spectral-line identification on the Process view" entry above for the full writeup. Note this isn't
   the `DeepLineIdentifier`-driven line identification the previous paragraph just declared out of
   scope for v1 - that was about a *loaded* JSolex-catalogue feature set; this is SolScan's own
   already-real, already-validated identification core (built for the live Capture-view overlay, per
   Phase 4's own line below) finally reused for the offline per-file case too.
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
