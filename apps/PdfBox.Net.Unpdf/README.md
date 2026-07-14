# unpdf

`unpdf` converts a PDF to HTML from the command line.

```console
unpdf input.pdf --output output-directory
```

The initial executable is a lite build. It converts text, links, vector paths,
semantic forms, and browser-safe embedded fonts without loading
`PdfBox.Net.Rendering`, SkiaSharp, or ImageMagick. Image export and raster
fallbacks are planned as explicit optional capabilities.

Run `unpdf --help` for all options and exit-code behavior.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Conversion or informational command succeeded. |
| 2 | Command-line usage was invalid. |
| 3 | The input was missing, unreadable, or not a valid PDF. |
| 4 | The output directory could not be used or written. |
| 5 | PDF conversion failed after the input was opened. |

## Self-contained publish

The baseline executable can already be published without a system .NET runtime:

```console
dotnet publish apps/PdfBox.Net.Unpdf/PdfBox.Net.Unpdf.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true
```

For a trimmed, compressed single executable, specify the `SingleFile` profile
and the target runtime identifier:

```console
dotnet publish apps/PdfBox.Net.Unpdf/PdfBox.Net.Unpdf.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  -p:PublishProfile=SingleFile
```

Run `eng/verify-unpdf-single-file.sh <rid>` to compare the single executable
with an untrimmed self-contained baseline. The gate converts the same PDF with
both executables, requires byte-identical HTML/CSS output, and writes size and
timing measurements under `artifacts/unpdf-publish/<rid>`.

NativeAOT and the cross-platform release RID matrix remain separate,
quality-gated milestones.

## NativeAOT publish

The lite dependency graph can also be compiled directly to native code:

```console
dotnet publish apps/PdfBox.Net.Unpdf/PdfBox.Net.Unpdf.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  -p:PublishProfile=NativeAot
```

Run `eng/verify-unpdf-nativeaot.sh <rid>` on the target operating system to
compare NativeAOT with the managed compressed single-file build. NativeAOT does
not support general cross-OS compilation, so the release matrix must use native
Linux, Windows, and macOS runners. `osx-arm64` and `osx-x64` have been verified
locally; `linux-x64` is enforced in CI. Linux ARM64 and Windows x64 are release
matrix targets pending their native hosted-runner gates.

Cross-platform archives, checksums, SBOMs, provenance, and signing policy are
documented in [`docs/unpdf-release-process.md`](../../docs/unpdf-release-process.md).
