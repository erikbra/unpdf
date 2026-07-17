# PDF Conversion Package Plan

This document captures the proposed package split and the quality plan for new
PDF-to-HTML and PDF-to-Markdown functionality.

The main product goal is not just to emit files. The goal is to build conversion
packages whose output quality can improve under measurable tests and ratchets:
visual fidelity for fixed-layout HTML, structural fidelity for semantic HTML,
and readable document-structure fidelity for Markdown.

## Package Decision

Create the conversion work in this repository, but publish it as separate NuGet
packages.

Proposed projects:

| Project | Package | Purpose |
|---|---|---|
| `src/PdfBox.Net.Layout` | `PdfBox.Net.Layout` | Shared page layout and tagged-PDF extraction model. |
| `src/PdfBox.Net.Html` | `PdfBox.Net.Html` | PDF to fixed-layout HTML first, semantic/reflowed HTML later. |
| `src/PdfBox.Net.Markdown` | `PdfBox.Net.Markdown` | PDF to Markdown using tagged structure first and layout heuristics second. |

Keep `PdfBox.Net.Core` focused on the PDFBox port and low-level PDF features.
Only add small enabling APIs to core when a converter genuinely needs access to
internal primitives, such as image export or safer content-stream collection.

## Why Add `PdfBox.Net.Layout` Now

HTML and Markdown will both need the same hard parts:

- normalized page geometry
- positioned glyphs and text runs
- reading-order grouping
- image placement and transform data
- inline-image handling
- vector path collection
- link annotation placement
- marked-content and MCID capture
- structure-tree correlation
- conversion diagnostics

If HTML and Markdown each grow their own versions, the duplication will be hard
to unwind. The shared package should start now, but stay intentionally narrow:
it should describe what is on the page before it makes strong claims about what
the content means.

## Shared Layout Model

Initial public API shape:

```csharp
using PDDocument document = Loader.LoadPDF("input.pdf");

PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
{
    IncludeText = true,
    IncludeImages = true,
    IncludePaths = true,
    IncludeLinks = true,
    IncludeTaggedPdf = true
});
```

Initial model concepts:

| Type | Role |
|---|---|
| `PdfLayoutDocument` | Whole-document layout result plus optional structure tree view. |
| `PdfLayoutPage` | Page number, size, crop box, rotation, and page elements. |
| `PdfTextGlyph` | Raw positioned glyph or decoded text position. |
| `PdfTextRun` | Consecutive glyphs with compatible font, size, direction, color, and baseline. |
| `PdfTextLine` | Reading-order line assembled from runs. |
| `PdfTextBlock` | Paragraph-like or column-like group assembled from lines. |
| `PdfImagePlacement` | Image asset id, intrinsic size, page transform, bounds, and source kind. |
| `PdfPathElement` | Filled/stroked vector path with style and transform. |
| `PdfLinkElement` | Annotation or structure-derived link target and page rectangle/quads. |
| `PdfStructureNode` | Tagged-PDF structure tree projection with MCID references. |
| `PdfLayoutDiagnostic` | Warnings, confidence scores, and fallback reasons. |

The first version should support physical layout well. Semantic block labels
such as heading, list item, table cell, caption, or figure should be optional
annotations with confidence, not irreversible assumptions.

## Quality Strategy

The conversion packages should be developed with a fixture manifest and quality
ratchets. Each fixture should declare what it is meant to prove.

Example fixture metadata:

```json
{
  "file": "simple-two-column.pdf",
  "categories": ["text", "columns", "reading-order"],
  "expected": {
    "pages": 1,
    "minTextCoverage": 0.99,
    "maxReadingOrderEditDistance": 0.03,
    "htmlVisualCategory": "foreground-shape-match",
    "markdownCategory": "semantic-text-match"
  }
}
```

Every test category should support:

- deterministic output paths
- machine-readable comparison results
- human-readable summaries
- a known-divergence ledger with owner and reason
- ratchet ceilings so new regressions fail CI

## Layout Tests

`tests/PdfBox.Net.Layout.Tests` should verify the shared extractor before any
converter-specific assertions.

| Area | Test Goal | Suggested Assertions |
|---|---|---|
| Page geometry | Crop box, rotation, and coordinate normalization are stable. | Page width/height, origin convention, rotated page bounds. |
| Text capture | Glyphs and runs preserve text and coordinates. | Unicode text, font name, font size, bounding boxes, direction. |
| Reading order | Lines and blocks are grouped predictably. | Expected line count, text order, block order, column separation. |
| Image placement | XObject images are captured with placement transforms. | Intrinsic size, page bounds, CTM-derived transform, asset id. |
| Inline images | Inline image operators become image placements. | Decoded data exists, placement bounds, source kind is inline. |
| Paths | Filled and stroked vector paths are captured. | Path command count, fill/stroke colors, line width, bounds. |
| Links | Link annotations map to page rectangles or quads. | URI/destination, rectangle bounds, target text overlap. |
| Marked content | BMC/BDC/EMC structure is retained. | Tags, MCIDs, ActualText, Alt text, nested content. |
| Structure tree | Tagged-PDF nodes can be traversed and resolved. | Standard structure type, kids, MCID references, page references. |

The layout tests should include hand-authored synthetic PDFs for exact geometry
and real-world PDFs for robustness.

## HTML Quality Tests

HTML has two distinct goals and should test them separately.

### Fixed-Layout HTML

Fixed-layout HTML should preserve the page visually. This mode can use page
containers, absolutely positioned text, images with CSS transforms, SVG/vector
overlays, and transparent link anchors.

Required test families:

| Test Family | Measurement |
|---|---|
| DOM invariants | Page count, page dimensions, text spans, image tags, SVG paths, anchors. |
| Asset coverage | Number of extracted image assets and referenced assets matches layout model. |
| Text coverage | Extracted DOM text covers expected PDF text after normalization. |
| Visual screenshot comparison | Render original PDF page and HTML page at matched scale, then compare images. |
| Foreground mask comparison | Compare foreground masks with dilation tolerance for antialiasing differences. |
| Element overlap checks | Ensure positioned text and images do not create obvious impossible bounds. |

Suggested visual metrics:

- same page screenshot dimensions after scaling
- non-background pixel ratio within tolerance
- foreground mask intersection-over-union above threshold
- bounded mean and RMS color drift
- text bounding-box overlap above threshold for simple fixtures
- no missing assets or broken URLs

The fixed-layout HTML ratchet should start with simple pages and grow toward
multi-column pages, images, links, forms, rotations, and vector-heavy pages.

### Semantic/Reflowed HTML

Semantic HTML should be structure-first. For tagged PDFs, it should map standard
structure types to HTML elements. For untagged PDFs, it should use layout
heuristics with confidence.

Required test families:

| Test Family | Measurement |
|---|---|
| Tagged structure mapping | Expected tags such as `h1`, `p`, `ul`, `li`, `table`, `figure`, `figcaption`, `a`. |
| Reading order | DOM text compared to expected logical text with whitespace normalization. |
| Link preservation | URI/destination link count and target text match expected fixtures. |
| Alternative text | Figure/image Alt text and ActualText are preserved when present. |
| Tables | Tagged table fixtures preserve rows, columns, header cells, and cell text. |

Semantic HTML tests should report precision/recall for detected structural
blocks when fixture expectations are available.

## Markdown Quality Tests

Markdown output should be evaluated as a document representation, not as a page
visual. The first reliable path is tagged PDF; the fallback path is layout
heuristics.

Required test families:

| Test Family | Measurement |
|---|---|
| Golden Markdown | Exact or normalized Markdown match for synthetic fixtures. |
| Text coverage | Expected text appears in correct order after Markdown normalization. |
| Heading detection | Tagged headings map to `#` levels; layout fallback detects obvious headings. |
| Paragraph grouping | Lines are joined into paragraphs without losing intentional breaks. |
| Lists | Tagged lists and simple bullet/numbered layout patterns map to Markdown lists. |
| Links | URI links are emitted as Markdown links when target text can be identified. |
| Images | Figures emit image references and alt text when available. |
| Tables | Tagged tables emit Markdown tables when rectangular; complex tables degrade visibly. |
| Diagnostics | Unsupported structures produce warnings instead of silent lossy output. |

Suggested Markdown metrics:

- normalized text coverage ratio
- reading-order edit distance
- heading/list/table precision and recall on labeled fixtures
- link count and URI accuracy
- image alt-text preservation rate
- table cell text accuracy for tagged rectangular tables

Markdown should expose a confidence/diagnostic result so callers can distinguish
"high-confidence tagged conversion" from "heuristic fallback."

## Development Phases

### Phase 1: Layout Text Slice

Deliver:

- `PdfBox.Net.Layout` project
- text-only `PdfLayoutExtractor`
- page geometry and text run model
- line/block grouping helpers
- `tests/PdfBox.Net.Layout.Tests`

Acceptance tests:

- synthetic single-page text fixture has exact glyph/run/line positions within tolerance
- multi-line fixture preserves reading order
- rotated/cropped page fixture normalizes page geometry correctly
- simple real-world PDF extracts non-empty text with diagnostics only for known issues

### Phase 2: Fixed-Layout HTML Text MVP

Deliver:

- `PdfBox.Net.Html` project
- fixed-layout HTML writer for pages and positioned text
- deterministic CSS and asset directory layout
- Playwright or equivalent browser screenshot harness

Acceptance tests:

- DOM page count and dimensions match layout model
- DOM text coverage is at least 99% on simple fixtures
- screenshot foreground mask match passes simple text fixtures
- generated HTML has no broken local asset references

### Phase 3: Images, Links, And Inline Images

Deliver:

- image placements in `PdfBox.Net.Layout`
- public/reusable image export path from core or layout
- image tags and CSS transforms in HTML
- link overlay anchors
- initial Markdown image/link support

Acceptance tests:

- XObject and inline-image fixtures produce expected image placements
- HTML references every emitted image asset exactly once unless reused
- link annotation fixtures preserve URI/destination count and geometry
- Markdown emits image references and links for tagged/simple fixtures

### Phase 4: Tagged-PDF Structure Bridge

Deliver:

- structure tree projection in `PdfBox.Net.Layout`
- MCID to collected content correlation
- ActualText, Alt, Lang, and standard structure type support
- tagged-PDF-first semantic HTML conversion
- a converter-neutral structure model for the Markdown package to consume

Acceptance tests:

- tagged heading/paragraph/list fixtures map to the shared semantic model and expected HTML
- tagged figure fixtures preserve alt text
- tagged table fixtures preserve rectangular cell matrix
- ActualText replaces glyph text where expected

The dependent Markdown package owns Markdown serialization and golden-output
tests; it consumes this shared bridge instead of rebuilding PDF structure-tree
correlation.

### Phase 5: Vector Paths And Better Visual HTML

Deliver:

- path collection from fill/stroke hooks
- SVG overlay emission for fixed-layout HTML
- clipping/style support where practical

Acceptance tests:

- simple shape fixtures preserve path count, bounds, fill, and stroke
- fixed-layout HTML screenshot foreground masks improve or ratchet
- vector-heavy pages degrade with diagnostics when unsupported features appear

### Phase 6: Markdown Heuristic Improvements

Deliver:

- untagged heading detection
- paragraph/list heuristics
- basic table detection from text alignment and ruling lines
- header/footer suppression experiments

Acceptance tests:

- labeled untagged fixtures report heading/list/table precision and recall
- Markdown text coverage and reading-order metrics ratchet upward
- ambiguous tables either pass expected structure or emit explicit diagnostics

## Fixture Corpus

Start with three fixture tiers:

| Tier | Purpose |
|---|---|
| Synthetic exact fixtures | Small generated PDFs where coordinates and structure are known exactly. |
| Tagged semantic fixtures | PDFs with known structure trees for semantic HTML and Markdown. |
| Real-world regression fixtures | Existing corpus PDFs used to prevent crashes and track quality trends. |

The fixture manifest should classify each file by features:

- plain text
- rotated page
- crop box
- multi-column
- XObject image
- inline image
- link annotation
- vector paths
- tagged headings
- tagged lists
- tagged tables
- figures/alt text
- forms/annotations
- complex scripts

## CI And Ratchets

Normal CI should run fast unit tests and a small deterministic conversion
fixture set. A heavier scheduled or opt-in job can run browser screenshots and
larger real-world fixtures.

Recommended outputs:

- layout extraction JSON for selected fixtures
- generated HTML/Markdown artifacts
- screenshot PNGs for HTML comparison
- comparison JSON with per-fixture metrics
- summary Markdown with current ratchet categories

Ratchets should fail on:

- new extraction crashes
- lower text coverage
- new broken assets
- worse visual category counts
- worse structural precision/recall where labeled fixtures exist
- unexpected diagnostics in fixtures that previously converted cleanly

## Open Design Questions

- Whether fixed-layout HTML should use positioned text spans, SVG text, or a
  hybrid. Start with positioned HTML text because it is selectable and easy to
  inspect.
- Whether image export should live in core, layout, or an optional backend
  package. The likely answer is a narrow public core API that does not expose
  backend-specific types.
- How much path/SVG fidelity is required before fixed-layout HTML is useful.
  The first useful milestone can ignore complex paths if text/images/links are
  correct and diagnostics are honest.
- How to represent confidence in Markdown output. This should be included early
  so callers can decide whether heuristic output is acceptable.

## Non-Goals For The First Milestone

- OCR for scanned PDFs.
- Perfect table reconstruction for arbitrary untagged PDFs.
- Pixel-perfect browser rendering of every PDF feature.
- Embedding HTML/Markdown conversion logic into `PdfBox.Net.Core`.
- A separate repository before the shared layout model stabilizes.
