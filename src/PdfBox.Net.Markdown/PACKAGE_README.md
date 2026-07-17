# PdfBox.Net.Markdown

`PdfBox.Net.Markdown` converts `PdfLayoutDocument` instances into conservative
Markdown. It prefers authored tagged-PDF structure and uses the shared semantic
layout model only when tagged structure is unavailable.

```csharp
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
paragraph-grouping, and basic heading inference. Untagged table reconstruction,
OCR, and aggressive multi-column reconstruction are intentionally excluded.

Every result reports a source and confidence category. Page diagnostics use
stable codes such as `markdown-semantic-structure-used`,
`markdown-layout-fallback-used`, and `markdown-mixed-source-used`, so callers
can distinguish authored semantics from heuristic output.
