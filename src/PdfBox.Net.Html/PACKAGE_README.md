# PdfBox.Net.Html

`PdfBox.Net.Html` converts a `PdfBox.Net.Layout` document into fixed-layout or
semantic HTML, CSS, fonts, and image assets.

## Install

```console
dotnet add package PdfBox.Net.Html
```

## Convert a PDF

```csharp
using PdfBox.Net;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

using PDDocument pdf = Loader.LoadPDF("input.pdf");
PdfLayoutDocument layout = PdfLayoutExtractor.Extract(pdf, new PdfLayoutOptions
{
    IncludeFontAssets = true,
    IncludeImageAssets = true
});

PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
{
    Title = "Converted document",
    TextMode = PdfHtmlTextMode.Semantic,
    SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
});

html.WriteToDirectory("output");
```

The default text mode preserves source positioning. Semantic mode infers
reading order and document structure and can use fixed pages or a continuous
flow. For tagged PDFs, authored headings, paragraphs, nested lists, rectangular
tables, reading order, `ActualText`, language/title metadata, and figure alt
text take precedence over geometric inference. Stable authored two-column text
pages use measured CSS grid tracks when every body element has unambiguous
column ownership; forms, spanning ruled tables, and mixed diagram/prose pages
remain on their conservative layout paths. The generated `PdfHtmlDocument`
exposes the HTML, CSS, and binary assets directly as well as `WriteToDirectory`.

Sparse tagged slide pages with a page-spanning graphic backdrop stay on the
fixed-layout path even in continuous semantic output, preserving the slide
composition while keeping its text selectable. Fixed-layout and cover
decoration layers emit images, browser-safe vectors, and SVG shadings in source
paint order. Bounded complex-artwork fallbacks cover only unsupported
shape-alpha, pattern-fill, or non-rectangular shading-clip regions; text over
those regions remains live HTML.

For images and raster fallbacks, register a rendering provider such as
`PdfBox.Net.SkiaSharp` or `PdfBox.Net.ImageMagick` before extraction. Without a
suitable backend, the layout image policy determines whether unsupported
assets degrade with diagnostics or fail conversion.

PDF-to-HTML conversion is approximate; inspect `PdfLayoutDocument.Diagnostics`
when fidelity matters.

This package depends on `PdfBox.Net.Layout` and does not reference
`PdfBox.Net.Markdown`. OCR and lossless reconstruction of arbitrary PDF
semantics are outside its scope.

Source and issues are available at
[github.com/erikbra/unpdf](https://github.com/erikbra/unpdf).
