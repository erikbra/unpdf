# PdfBox.Net.Layout

`PdfBox.Net.Layout` extracts a PDF into a renderer-independent page-layout
model containing text, reading-order structures, links, forms, vector paths,
images, optional browser assets, and conversion diagnostics.

## Install

```console
dotnet add package PdfBox.Net.Layout
```

## Basic usage

```csharp
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

using PDDocument pdf = Loader.LoadPDF("input.pdf");
PdfLayoutDocument layout = PdfLayoutExtractor.Extract(pdf, new PdfLayoutOptions
{
    IncludeText = true,
    IncludeLinks = true,
    IncludeImages = true,
    IncludePaths = true
});

foreach (PdfLayoutPage page in layout.Pages)
{
    Console.WriteLine($"Page {page.PageNumber}: {page.Text}");
}
```

Image placements can be collected without exporting image assets. When
`IncludeImageAssets` is enabled, `ImageExportPolicy` controls whether an
unsupported image degrades with a diagnostic, fails strictly, or requires a
registered rendering backend.

This package does not register a rendering backend. Applications that need
image conversion or raster fallbacks can reference `PdfBox.Net.SkiaSharp` or
`PdfBox.Net.ImageMagick` and register that provider during startup.

Use `PdfBox.Net.Html` when the desired result is HTML rather than the layout
model itself.

Source and issues are available at
[github.com/erikbra/unpdf](https://github.com/erikbra/unpdf).
