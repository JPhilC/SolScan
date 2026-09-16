# SolScan Release Notes

A record of what each installed release (`SolScan-Setup-<version>.exe`, built via
`scripts\Build-Release.ps1` and published from `.github\workflows\release.yml`) actually
contains. One section per version, newest first. The version number matches `<Version>` in
`Directory.Build.props` and the corresponding `vX.Y.Z` git tag - add a new `## vX.Y.Z` section
here (and bump `Directory.Build.props`) before tagging a release, since the release workflow reads
its GitHub Release description straight from this file.

## v0.2.0

Capture and Process both gained substantial new features, and the SHG reconstruction pipeline is
noticeably faster - a real capture that took ~41s in v0.1.0 now finishes in ~16s, ahead of JSol'Ex
processing the same file with the same settings.

### Features

- **Live spectral line identification (Capture)** — labeled Fraunhofer lines (H-alpha, Calcium K/H,
  the sodium/magnesium/iron/helium lines, etc.) hover into place over the live preview as the
  diffraction grating is turned, plus a colour gradient reference band, so finding the right line no
  longer relies on guesswork. A confident live identification is now saved with the recording and
  used automatically when that file is processed later. A "Load Test Image…" option lets this be
  tried out with no camera connected.
- **Crop SER utility** — trims an existing full-frame `.ser` recording down to a chosen percentage of
  its height, for old captures made before setting up a tighter region of interest.
- **New Process output images** — Colorized (a wavelength-tinted or H-alpha-curve colour rendering)
  and Virtual Eclipse (a coronagraph-style view revealing faint detail near the limb) join the
  existing Raw/Reconstruction/Continuum/GeometryCorrected/GeometryCorrectedProcessed set.
  Automatic spectral-line identification can also fill in which line was studied when a recording
  doesn't already carry that answer from the live overlay above, and every detected fact (line,
  distortion polynomial, disk tilt/ratio) now has a dedicated "Processing Results" panel.
- **Per-run processing log** — a JSol'Ex-style timestamped log file is written alongside every
  processed recording (in a new `log` subfolder), recording exactly what happened at each stage.
- **Capture view refinements** — an ASICap-style Exposure control (range dropdown + spinner) and a
  numeric Gain box, a collimator-focus aid, an on-screen reticule (crosshair/rotation guides), a
  display-brightness slider, and a VS-tool-window-style pin/dock option for the settings panel (so it
  can share space with the live preview instead of always overlaying it). Capture view controls are
  now grouped into collapsible sections with icon-based toolbars throughout Capture, Process, and the
  Hand Control window.
- **Significantly faster processing** — the reconstruction pipeline now reads each recording from
  disk once instead of up to three times, and processes frames in parallel across CPU cores, roughly
  halving (or better) real processing time with no change to the output images produced.

### Fixes

- A camera settings change made by rapidly dragging a slider could crash the app.
- The Full Frame ROI option didn't always reset correctly.
- The Processing Results panel and status bar could under-count how many images were written by one
  whenever a Colorized image was produced.

### Known limitations

- No automated acquisition pipeline yet — slewing and recording are both manually triggered.
- Processing is kicked off by hand from a saved `.ser` file rather than automatically once a
  recording finishes.
- Real camera hardware needs its vendor SDK DLL present in the `SolScan.External` git submodule
  at build time to be included in the installer — see that submodule's own `README.md`.

## v0.1.0

First installable build. Mount control, manual camera capture, and the first slices of the SHG
reconstruction pipeline all work end-to-end against real hardware; automated acquisition and
automatic processing are still ahead. See `CLAUDE.md`'s phased build plan for the full detail on
what's real vs. placeholder.

### Features

- **Prepare** — connect/disconnect a mount over ASCOM Alpaca, Park/Unpark, a manual tracking
  toggle, live RA/Dec, and editable site settings that reconcile against the mount's own on
  connect. Picks which SHG+telescope "Equipment Setup" is in use, plus a pop-out Hand Control
  window for manually jogging the mount.
- **Capture** — camera discovery (ZWO ASI, Altair, or a hardware-free simulator), a SharpCap-style
  live preview (gain/exposure/USB-bandwidth/contrast sliders, a histogram, ROI, zoom, colour
  space/binning, a collimator-focus aid, an on-screen reticule) in a hideable icon-toolbar/drawer
  panel, and manual start/stop recording to standard `.ser` files. "Find Sun…" slews to today's
  computed solar position and offers a camera-brightness fine-tune plus an Alpaca pointing sync; a
  pop-out Hand Control window jogs the mount manually. Connecting a camera auto-registers it in the
  equipment library; every recording gets a `.equipment.json` sidecar snapshotting the equipment,
  camera settings, and mount pointing used.
- **Options** — a full equipment library (SHGs, telescopes, cameras, saved Setups) and general
  settings (capture save location, ASCOM Alpaca connection, site location).
- **Process** — pick a `.ser` file and run it through a real SHG reconstruction pipeline: spectral
  line-curvature detection, disk reconstruction, ellipse-fitting geometry correction, and contrast
  enhancement (AutoStretch/CLAHE/CLAHE2), all ported from astro4j/JSol'Ex, producing
  `Raw`/`Reconstruction`/`Continuum`/`GeometryCorrected`/`GeometryCorrectedProcessed` output images
  viewable in the app. Process parameters live in a dockable panel on the Process view itself.

### Known limitations

- No automated acquisition pipeline yet — slewing and recording are both manually triggered.
- Processing is kicked off by hand from a saved `.ser` file rather than automatically once a
  recording finishes.
- Real camera hardware needs its vendor SDK DLL present in the `SolScan.External` git submodule
  at build time to be included in the installer — see that submodule's own `README.md`.
