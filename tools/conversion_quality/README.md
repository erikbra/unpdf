# Conversion Quality Harness

This harness is the shared measuring stick for the PDF-to-HTML and
PDF-to-Markdown package work. It is intentionally converter-independent: a
converter can write files into a result directory, then this script evaluates
those files against a fixture manifest and writes machine-readable and
human-readable reports.

## Local Smoke Run

```bash
python3 tools/conversion_quality/run_conversion_quality.py \
  --manifest tools/conversion_quality/smoke/manifest.json \
  --results-dir tools/conversion_quality/smoke/results \
  --out-dir artifacts/conversion-quality-smoke \
  --known-divergences tools/conversion_quality/smoke/known-divergences.json \
  --ratchet-baseline tools/conversion_quality/smoke/ratchet-baseline.json \
  --fail-on-unexpected \
  --fail-on-regression
```

The command writes:

- `comparison.json`, with per-fixture metrics, failure categories, and ratchet
  status. Each fixture also includes `qualityChecks` entries for DOM,
  text-coverage, and visual categories when applicable.
- `summary.md`, with the same result in a compact table suitable for CI step
  summaries.

CI also writes and uploads `artifacts/conversion-quality-smoke/html-examples`.
That directory is a human review bundle for real PDF fixtures: each example
contains the original `source.pdf`, generated `index.html`, CSS/assets,
`summary.md`, diagnostics, and a `compare.html` page that shows the PDF and
continuous semantic HTML side by side. Each example also contains `quality/quality-report.md`
and `quality/quality-report.json`, plus page-level PNG artifacts from the
source PDF render, browser-rendered continuous semantic HTML, a foreground-mask diff, and a
perceptual color heatmap. These
quality probe findings are non-gating: `needs-review` means the artifact found
a likely visual or structural conversion issue for humans to inspect, not that
the CI step failed. Download the `conversion-quality-smoke-*` workflow artifact
and open `html-examples/index.html` to browse the examples.

## Remote Public Corpus

The pinned remote corpus covers eleven freely available public PDFs. It combines
academic papers from JMLR, ACL Anthology, and arXiv with IRS and USCIS forms, a
long NIST publication, and National Park Service brochure and map layouts.
Together they exercise long-form text, formulas, dense two-column layout,
government forms, tables, figures, photographs, maps, dense vector geometry,
and links. Canonical source pages, direct HTTPS PDF URLs, categories, and
SHA-256 hashes are recorded in `remote-corpus-manifest.json`.

Run the complete download, verification, conversion, expectation, and combined
review artifact path for all eleven documents locally with one command. Omitting
selection options always selects the full manifest:

```bash
python3 tools/conversion_quality/run_remote_corpus.py --build
```

Normal PR CI runs a smaller category-balanced selection through the same
manifest and artifact path:

```bash
python3 tools/conversion_quality/run_remote_corpus.py \
  --id jmlr-lda \
  --id arxiv-adam \
  --id uscis-i9 \
  --id nps-mount-rainier \
  --build
```

The four CI documents cover long-form text, formula-heavy notation, a semantic
government form, and a graphic-heavy brochure. Use repeatable `--id` options for
specific documents or repeatable `--category` options for any document in one
of several categories, for example `--category forms --build`. Multiple IDs or
categories are OR selections within that option; when both options are present,
a document must match both filters. Unknown values, empty values, and selections
that match no documents fail before any download starts.

PDFs are downloaded atomically with retries and timeouts into the ignored
`artifacts/cache/conversion-quality/remote-pdfs` directory. Every cached or
downloaded file must match its pinned SHA-256 hash before it is used. The script
then combines the checked-in fixtures with the remote corpus and writes one
overview to `artifacts/html-examples`. CI runs its four-document selection over
the same networked path after the Release build, writing the combined overview to
`artifacts/conversion-quality-smoke/html-examples` beneath the existing uploaded
artifact root.

The checked-in review examples also include two Ghent Output Suite test-page
PDFs. CI downloads the official test-pages ZIP with
`scripts/download-ghent-pdf.mjs --test-pages`, verifies both extracted PDFs by
SHA-256, and keeps the ZIP and PDFs under ignored `artifacts` storage.

The remote manifest's expectations deliberately cover stable structural
invariants only: exact page count, normalized required title words, minimum
text runs, and category-specific minimum image, vector-path, link, and form-control counts.
Known visual and text-reconstruction shortcomings remain review findings owned
by issues #728 through #733 rather than brittle expected failures.

The HTML quality probe currently checks:

- browser page dimensions and text-run counts against extracted layout data
- word-boundary loss by comparing browser-visible HTML text with `pdftotext`
  when available, otherwise the PdfBox.Net layout text fallback
- text overlaps with rendered image boxes and large vector boxes
- foreground-mask deltas between a source PDF page render and the browser page
  screenshot, with visual report pages to make mismatches easy to inspect
- CIE Lab color deltas over shared foreground interiors, excluding a two-pixel
  boundary to tolerate antialiasing differences; pixels with a delta of at least
  20 are severe, and pages need review when they exceed 5% of compared interiors
  after at least 0.5% of the page has a stable comparison sample

HTML review examples can set `qualityPages` to cap how many pages the browser
probe renders. Keeping this small makes CI artifacts quick while still giving
us stable samples to improve against.

## Manifest Shape

Each fixture declares a target, output files, expected text, and measurable
expectations. Current gates cover:

- converter crashes or missing declared outputs
- text coverage after normalization
- broken local HTML asset references
- diagnostic counts
- required files
- required substrings in generated outputs
- simple HTML DOM selector counts through `expectations.domSelectors`
- exact Markdown structure counts through `expectations.markdownStructures`
  (`headings`, `paragraphs`, ordered and unordered list items, links, images,
  and table rows)
- visual check reports through an optional `outputs.visual` JSON file with a
  `checks` array

Markdown fixtures report a `markdown-structure` quality check with expected and
actual counts, a match ratio, and aggregate minimum/average match metrics.
Diagnostic reports may also include top-level `source` and `confidence`; the
harness retains those fields plus diagnostic code/source histograms.

Known divergences are listed separately and must include an owning issue and
reason. Ratchet baselines cap the number of accepted `failed` and `known`
fixtures, cap failure categories, and can set aggregate metric floors such as
`minimumTextCoverage`.

The checked-in smoke corpus includes synthetic HTML and tagged-first Markdown
outputs. Converter work should add real PDF fixtures and expected result folders
as stable vertical slices become available.
