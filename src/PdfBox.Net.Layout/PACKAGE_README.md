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
using PdfBox.Net;
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

When the source PDF is tagged, `PdfLayoutDocument.TaggedStructure` exposes the
authored structure tree in original child order. Standard roles, role maps,
`ActualText`, figure alternative text, language, title, list/table attributes,
and page/MCID correlations are retained for downstream HTML and Markdown
converters. Tagged-content problems are reported through stable
`tagged-structure-*` diagnostics rather than being silently discarded.

Image placements can be collected without exporting image assets. When
`IncludeImageAssets` is enabled, `ImageExportPolicy` controls whether an
unsupported image degrades with a diagnostic, fails strictly, or requires a
registered rendering backend. Browser-safe encoded assets, such as ordinary
RGB JPEG streams, are preserved byte-for-byte in `Degraded` and `Strict` modes
without consulting `RenderingBackend.Current`.

- `Degraded` omits an unavailable asset and records a stable diagnostic.
- `Strict` throws when any requested image asset cannot be exported.
- `BackendRequired` rejects asset extraction before passthrough or conversion
  when no backend is registered.

Codec and color failures use the stable
`image-asset-{cmyk,jpx,tiff,icc}-backend-required` families. Requested
annotation appearances and transparency-group raster fallbacks report
`annotation-appearance-backend-missing` and
`transparency-group-rasterization-backend-missing` when no backend is
available in degraded mode; strict and backend-required modes fail
deterministically instead.

When `IncludeTransparencyGroupFallbacks` and image assets are enabled, compact
shape-alpha, pattern-fill, and non-rectangular shading-clip regions are
detected before direct SVG export and captured as bounded
`ComplexArtworkFallback` images. The rest of the page—including selectable
text—remains in the layout model. Image, vector-path, and shading paint
operations retain their source content-stream order.

This package does not register a rendering backend. Applications that need
image conversion or raster fallbacks can reference `PdfBox.Net.SkiaSharp` or
`PdfBox.Net.ImageMagick` and register that provider during startup.

Use `PdfBox.Net.Html` or `PdfBox.Net.Markdown` when the desired result is a
document format rather than the layout model itself. Those packages depend on
Layout; they do not depend on each other.

Source and issues are available at
[github.com/erikbra/unpdf](https://github.com/erikbra/unpdf).
