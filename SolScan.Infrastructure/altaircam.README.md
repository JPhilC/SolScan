# Altair native SDK

Drop **`altaircam.dll`** (x64) directly in this folder (`SolScan.Infrastructure/`, next to
`SolScan.Infrastructure.csproj`) to enable real Altair camera support - not committed to the repo,
see `.gitignore` and the licensing note below.

**Where to get it**: altairastro.help/downloads lists a standalone "Altair Camera SDK" package
(covers cameras, the AAF focuser and filter wheels) - it requires a free account/login on that
site. If you'd rather not register just for the SDK, installing the regular **AltairCapture**
end-user app gets you the same DLL: it ships inside the app's own install folder, typically
`C:\AstroPhotography\Apps\AltairCapture\x64\altaircam.dll` - copy it from there.

Altair cameras share the same "ToupTek-alike" native ABI as ToupTek/OGMA/Levenhuk, but the
exported function names and the DLL itself are brand-specific - use Altair's own `altaircam.dll`,
not another brand's DLL.

`AltairNative`'s struct layouts were written from general knowledge of this ABI, not verified
against real hardware or the vendor's own header in this environment (see the doc comment on
`AltairNative` itself) - if enumeration or frames come back garbled once real hardware is attached,
that's the first place to check against the SDK's own header/docs.

Without this file present, `AltairCameraProvider.Discover()` returns an empty list rather than
failing - the rest of the app (simulator, ASI, everything else) works fine with it missing.

**Licensing**: this is Altair's own proprietary compiled binary, not covered by SolScan's AGPL-3.0
license - it's a runtime dependency you supply yourself, not source code being distributed as part
of this repo. Check Altair's own SDK license terms if you intend to redistribute a build of SolScan
that bundles it.
