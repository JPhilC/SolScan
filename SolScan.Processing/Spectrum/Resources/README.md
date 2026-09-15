# reference-windows.bin

A small (~38KB) bundled excerpt of the BASS2000 solar flux atlas - narrow (±8Å) reference windows
around each of the 12 named `SolScan.Core.Processing.SpectralRay` lines only, used by
`SolScan.Processing.Spectrum.SpectralLineIdentifier` to correlate an observed spectral profile against.
Not the full atlas (14MB, and not something SolScan needs to recognise an arbitrary wavelength - only
these 12 named lines matter).

## License

The source data (`atlasvi.dat`) is published by BASS2000 under Creative Commons
**CC-BY-SA-NC 4.0** (Attribution - ShareAlike - NonCommercial).

    https://bass2000.obspm.fr/solar_spect.php

Original work: Delbouille L., Neven L., Roland G. (1972), "PHOTOMETRIC ATLAS OF THE SOLAR SPECTRUM
FROM λ 3000 TO λ 10000".

This derived excerpt inherits that licence - non-commercial use only, share-alike, with attribution -
same as the source data itself. See the repo's own `NOTICE` file for SolScan's summary of this.

## How this file is produced

`SolScan.Tools`' `extract-atlas` command (`AtlasExtractor.cs`) reads a local copy of the raw
`atlasvi.dat` file directly - an independently-written reader for BASS2000's own published fixed
layout (not a port of astro4j's own converter, though that converter was read once to understand the
format before writing this one - see `AtlasExtractor`'s own doc comment) - and extracts a narrow window
around each named line:

```
dotnet run --project SolScan.Tools -- extract-atlas <path-to-atlasvi.dat> [output-path]
```

Regenerate this file only if the extraction logic itself changes (e.g. a wider window, a different
output resolution) - it isn't part of SolScan's normal build.
