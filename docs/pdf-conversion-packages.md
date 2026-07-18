# Conversion NuGet packages

The conversion API is published as three packages with one-way dependencies:

```text
PdfBox.Net.Html ───────┐
                      v
                PdfBox.Net.Layout ──> PdfBox.Net.Core
                      ^
PdfBox.Net.Markdown ───┘
```

`PdfBox.Net.Html` and `PdfBox.Net.Markdown` are independent. Installing one
must not restore or bundle the other.

## Package responsibilities

| Package | Supported modes | Important limitations |
| --- | --- | --- |
| `PdfBox.Net.Layout` | Page geometry, text, reading order, paths, links, forms, images, tagged structure, diagnostics | Describes PDF content but does not choose an HTML or Markdown representation. Image conversion and raster fallbacks may require a separately registered rendering backend. |
| `PdfBox.Net.Html` | Fixed-layout pages and semantic/tagged or heuristic HTML | Conversion is approximate. Unsupported image codecs, transparency, annotations, and semantic inference use documented policies and diagnostics; OCR is not included. |
| `PdfBox.Net.Markdown` | Tagged-first headings, paragraphs, lists, links, figures, rectangular tables, and conservative untagged heading/list/simple-table fallback | Ambiguous tables degrade to text; complex multi-column reconstruction and OCR are excluded from the MVP. Low-confidence output and degradation are reported explicitly. |

The package READMEs contain the smallest extraction and conversion examples.
Checked consumers under `samples/PdfBox.Net.Html.Consumer` and
`samples/PdfBox.Net.Markdown.Consumer` prove that each top-level converter can
be restored, built, and run with only its own direct `PackageReference`.

## Validate packages locally

After a Release solution build, run the same package-boundary check used by CI:

```console
dotnet build Unpdf.slnx --configuration Release
python3 eng/validate_conversion_packages.py \
  --configuration Release \
  --version 4.0.0-local.1 \
  --no-build
```

The validator:

1. packs Layout, HTML, and Markdown independently;
2. checks deterministic IDs, versions, authors, SPDX license, repository
   commit, README, target framework, and direct dependencies;
3. rejects bundled or cross-referenced HTML/Markdown assemblies;
4. restores the HTML-only and Markdown-only consumers from the local package
   source;
5. inspects each resolved NuGet graph; and
6. runs a real conversion through each consumer.

The NuGet publication workflow runs this validator before it can upload
packages.

## Run conversion quality checks

Fast harness tests:

```console
python3 -m unittest discover -s tests/conversion_quality -p 'test_*.py'
```

Deterministic smoke fixtures and ratchets:

```console
python3 tools/conversion_quality/run_conversion_quality.py \
  --manifest tools/conversion_quality/smoke/manifest.json \
  --results-dir tools/conversion_quality/smoke/results \
  --out-dir artifacts/conversion-quality-smoke \
  --known-divergences tools/conversion_quality/smoke/known-divergences.json \
  --ratchet-baseline tools/conversion_quality/smoke/ratchet-baseline.json \
  --fail-on-unexpected \
  --fail-on-regression
```

Read `comparison.json` for machine-readable per-fixture metrics and `summary.md`
for the review view. `passed` means all declared expectations matched. `known`
means every failure is present in the reviewed divergence ledger. `failed`
means an unexpected quality failure remains. Text coverage and Markdown
structure-match ratios are higher-is-better; failure-category counts,
diagnostics beyond the declared allowance, and broken references are
lower-is-better. A ratchet failure means the current aggregate crossed a
checked-in baseline even when an individual fixture remains understandable.

The generated Markdown heuristic suite exercises actual tagged and untagged
PDFs through the converter:

```console
dotnet tools/conversion_quality/PdfBox.Net.ConversionQuality/bin/Release/net10.0/PdfBox.Net.ConversionQuality.dll \
  --markdown-quality-out artifacts/markdown-quality-results
python3 tools/conversion_quality/run_conversion_quality.py \
  --manifest tools/conversion_quality/markdown-quality/manifest.json \
  --results-dir artifacts/markdown-quality-results \
  --out-dir artifacts/markdown-quality-report \
  --ratchet-baseline tools/conversion_quality/markdown-quality/ratchet-baseline.json \
  --fail-on-unexpected \
  --fail-on-regression
```

Its summary separates tagged, simple untagged, and ambiguous untagged
fixtures. Reading-order accuracy, heading/list F1, table-cell accuracy, and
link accuracy are higher-is-better. Ambiguous multi-column and table fixtures
must retain explicit low-confidence diagnostic evidence even when their
current text order meets the baseline.
