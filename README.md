# unpdf

`unpdf` converts PDF documents to semantic or fixed-layout HTML on .NET. This
repository contains the `PdfBox.Net.Layout`, `PdfBox.Net.Html`, and tagged-first
`PdfBox.Net.Markdown` libraries, the cross-platform `unpdf` command-line
application, and the browser WebAssembly sample.

The conversion projects use the Apache PDFBox-derived .NET libraries published
as [PdfBox.Net packages on NuGet](https://www.nuget.org/profiles/erikbra).

## Build and test

```sh
dotnet restore Unpdf.slnx
dotnet build Unpdf.slnx --configuration Release --no-restore
dotnet test Unpdf.slnx --configuration Release --no-build
```

Run the command-line application with:

```sh
dotnet run --project apps/PdfBox.Net.Unpdf -- input.pdf --output output-directory
```

Install the framework-dependent .NET global tool with:

```sh
dotnet tool install --global unpdf.tool
```

The tool package includes the platform-native SkiaSharp, HarfBuzzSharp, and
ImageMagick assets. The GitHub release archives, Homebrew, WinGet, and APT use
the smaller RID-specific self-contained single-file executables instead.

Library consumers can reference `PdfBox.Net.Html` for HTML conversion,
`PdfBox.Net.Markdown` for tagged-first Markdown conversion, or
`PdfBox.Net.Layout` for layout extraction. Rendering-enabled applications also
reference `PdfBox.Net.SkiaSharp` and `PdfBox.Net.ImageMagick` and register those
providers before requesting image or raster-fallback output.

See [the CLI documentation](apps/PdfBox.Net.Unpdf/README.md) and
[release process](docs/unpdf-release-process.md) for distribution details.
