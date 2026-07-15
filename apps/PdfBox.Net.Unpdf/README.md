# unpdf

`unpdf` converts a PDF to HTML from the command line.

```console
unpdf input.pdf --output output-directory
```

The executable includes the PdfBox.Net SkiaSharp and ImageMagick providers. It
exports images and enables raster fallbacks for annotation appearances and
compact transparency groups by default; there is no reduced `lite` CLI profile.

This improves coverage but is not a claim of complete PDF
feature support. Unsupported or degraded operations remain diagnostics, and
semantic HTML cannot reproduce arbitrary PDF painting without raster fallbacks.

Run `unpdf --help` for all options and exit-code behavior.

## .NET tool

The framework-dependent CLI is also packaged as a .NET global/local tool:

```console
dotnet tool install --global unpdf.tool
```

This form requires a compatible .NET runtime and includes native runtime assets
for every supported platform. Prefer the RID-specific release archive or OS
package manager when download size matters.

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

This produces one approximately 34 MB self-contained distribution file on
macOS ARM64. The SkiaSharp, HarfBuzzSharp, and ImageMagick native libraries are
embedded in that file and extracted to the .NET bundle extraction location when
the program runs. No separately installed .NET runtime or native packages are
required, but this is not an extraction-free executable.

Run `eng/verify-unpdf-single-file.sh <rid>` to compare the single executable
with an untrimmed self-contained baseline. The gate converts the same PDF with
both executables, requires byte-identical HTML/CSS output, and writes size and
timing measurements under `artifacts/unpdf-publish/<rid>`.

NativeAOT and the cross-platform release RID matrix remain separate,
quality-gated milestones.

## NativeAOT publish

The complete rendering-enabled dependency graph can be compiled directly to
native code:

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

NativeAOT publication has been verified on macOS ARM64 with the SkiaSharp and
ImageMagick native libraries copied beside the executable. Each release RID
still requires a native smoke test because those libraries are platform-specific.
Setting `PublishSingleFile` and `IncludeNativeLibrariesForSelfExtract` on the
NativeAOT publish currently leaves those native libraries beside the executable;
it does not create a true one-file NativeAOT deployment.

Cross-platform archives, checksums, SBOMs, provenance, and signing policy are
documented in [`docs/unpdf-release-process.md`](../../docs/unpdf-release-process.md).
