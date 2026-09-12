# SolScan Release Notes

A record of what each installed release (`SolScan-Setup-<version>.exe`, built via
`scripts\Build-Release.ps1` and published from `.github\workflows\release.yml`) actually
contains. One section per version, newest first. The version number matches `<Version>` in
`Directory.Build.props` and the corresponding `vX.Y.Z` git tag - add a new `## vX.Y.Z` section
here (and bump `Directory.Build.props`) before tagging a release, since the release workflow reads
its GitHub Release description straight from this file.

## v0.1.0 — Unreleased

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
  space/binning), and manual start/stop recording to standard `.ser` files. "Find Sun…" slews to
  today's computed solar position and offers a camera-brightness fine-tune plus an Alpaca pointing
  sync. Connecting a camera auto-registers it in the equipment library; every recording gets a
  `.equipment.json` sidecar snapshotting the equipment, camera settings, and mount pointing used.
- **Options** — a full equipment library (SHGs, telescopes, cameras, saved Setups), general
  settings (capture save location, ASCOM Alpaca connection, site location), and process parameters
  feeding the Process stage.
- **Process** — pick a `.ser` file and run it through a real SHG reconstruction pipeline: spectral
  line-curvature detection, disk reconstruction, and ellipse-fitting geometry correction, all
  ported from astro4j/JSol'Ex, producing `Raw`/`Reconstruction`/`Continuum`/`GeometryCorrected`
  output images viewable in the app. Contrast-enhanced output is not yet implemented.

### Known limitations

- No automated acquisition pipeline yet — slewing and recording are both manually triggered.
- Processing is kicked off by hand from a saved `.ser` file rather than automatically once a
  recording finishes.
- Real camera hardware needs its vendor SDK DLL present at build time to be included in the
  installer — see `SolScan.Infrastructure\ASICamera2.README.md` / `altaircam.README.md`.
