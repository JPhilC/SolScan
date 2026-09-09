# ZWO ASI native SDK

Drop **`ASICamera2.dll`** (x64) directly in this folder (`SolScan.Infrastructure/`, next to
`SolScan.Infrastructure.csproj`) to enable real ZWO ASI camera support - not committed to the repo,
see `.gitignore` and the licensing note below.

**Where to get it**: ZWO's software page (zwoastro.com/software/ → "Product SDK", also mirrored at
astronomy-imaging-camera.com/software-drivers) lists the ASI Camera SDK as a separate download from
the regular ASICap/ASIStudio end-user apps - it ships as a zip containing headers
(`ASICamera2.h`) and prebuilt libraries for Windows/Linux/macOS across architectures. Pull the
**x64 Windows** `ASICamera2.dll` specifically, since this whole solution builds/runs as x64 (see
CLAUDE.md). If the SDK download page is unavailable, any full ZWO ASI camera driver/software
install (e.g. ASICap) also places `ASICamera2.dll` in its own install folder - copying it from
there works identically.

Without this file present, `AsiCameraProvider.Discover()` returns an empty list rather than
failing - the rest of the app (simulator, Altair, everything else) works fine with it missing.

**Licensing**: this is ZWO's own proprietary compiled binary, not covered by SolScan's AGPL-3.0
license - it's a runtime dependency you supply yourself, not source code being distributed as part
of this repo. Check ZWO's own SDK license terms if you intend to redistribute a build of SolScan
that bundles it.
