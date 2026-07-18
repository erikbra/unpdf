# PdfBox.Net.Markdown

`PdfBox.Net.Markdown` converts `PdfLayoutDocument` instances into conservative
Markdown. It prefers authored tagged-PDF structure and uses the shared semantic
layout model only when tagged structure is unavailable.

## Install

```console
dotnet add package PdfBox.Net.Markdown
```

## Convert a PDF

```csharp
using PdfBox.Net;
using PdfBox.Net.Layout;
using PdfBox.Net.Markdown;
using PdfBox.Net.PDModel;

using PDDocument pdf = Loader.LoadPDF("input.pdf");
PdfLayoutDocument layout = PdfLayoutExtractor.Extract(pdf, new PdfLayoutOptions
{
    IncludeImageAssets = true
});
PdfMarkdownDocument result = PdfMarkdownConverter.Convert(layout);

result.WriteToDirectory("markdown-output"); // document.md, diagnostics.json, and image assets
Console.WriteLine($"{result.Source}: {result.Confidence}");
```

The MVP supports headings, paragraphs, nested ordered and unordered lists,
links, tagged figures with alternate text, and simple rectangular tagged
tables. Untagged text uses the layout package's conservative reading-order,
paragraph-grouping, basic heading/list inference, and simple rectangular table
grids. Ambiguous untagged tables degrade to text with a diagnostic. OCR and
aggressive multi-column reconstruction are intentionally excluded.

Every result reports a source and confidence category. Page diagnostics use
stable codes such as `markdown-semantic-structure-used`,
`markdown-layout-fallback-used`, `markdown-untagged-table-inferred`, and
`markdown-untagged-multicolumn-ambiguous`, so callers can distinguish authored
semantics from heuristic output and review ambiguous cases.

This package depends on `PdfBox.Net.Layout` and does not reference
`PdfBox.Net.Html`. Image conversion may require a separately registered
rendering backend. OCR and lossless reconstruction of arbitrary untagged,
multi-column, or complex-table PDFs are outside its scope.

Source and issues are available at
[github.com/erikbra/unpdf](https://github.com/erikbra/unpdf).
