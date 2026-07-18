from __future__ import annotations

import importlib.util
import json
import shlex
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HARNESS_PATH = ROOT / "tools/conversion_quality/run_conversion_quality.py"
SPEC = importlib.util.spec_from_file_location("run_conversion_quality", HARNESS_PATH)
assert SPEC is not None
harness = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = harness
SPEC.loader.exec_module(harness)


def write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


class ConversionQualityHarnessTest(unittest.TestCase):
    def test_passing_html_fixture_writes_comparison_and_summary(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            baseline = root / "ratchet.json"

            write_json(manifest, self._manifest())
            write_json(baseline, self._zero_baseline())
            write_text(
                results / "html-basic/index.html",
                """<!doctype html>
                <html><head><link href="assets/page.css" rel="stylesheet"></head>
                <body><section data-page-number="1">
                <span style="position:absolute">Hello PDF conversion</span>
                <span>Second line</span>
                </section></body></html>""",
            )
            write_text(results / "html-basic/assets/page.css", ".page { position: relative; }\n")
            write_json(results / "html-basic/diagnostics.json", {"diagnostics": []})
            self._write_passing_visual(results / "html-basic/visual.json")

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--ratchet-baseline",
                    str(baseline),
                    "--fail-on-unexpected",
                    "--fail-on-regression",
                ]
            )

            self.assertEqual(0, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual({"passed": 1, "known": 0, "failed": 0}, comparison["summary"]["status"])
            self.assertEqual(1.0, comparison["fixtures"][0]["metrics"]["textCoverage"])
            checks = {check["category"]: check for check in comparison["fixtures"][0]["qualityChecks"]}
            self.assertEqual("passed", checks["dom"]["status"])
            self.assertEqual("passed", checks["text-coverage"]["status"])
            self.assertEqual("passed", checks["visual"]["status"])
            self.assertEqual(2, checks["visual"]["metrics"]["visualChecks"])
            self.assertEqual(1, comparison["summary"]["checkCategories"]["dom"]["passed"])
            self.assertEqual(1, comparison["summary"]["checkCategories"]["text-coverage"]["passed"])
            self.assertEqual(1, comparison["summary"]["checkCategories"]["visual"]["passed"])
            summary = (out_dir / "summary.md").read_text(encoding="utf-8")
            self.assertIn("html-basic", summary)
            self.assertIn("visual: passed", summary)

    def test_low_text_coverage_broken_assets_and_diagnostics_fail_ratchet(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            baseline = root / "ratchet.json"

            data = self._manifest()
            data["fixtures"][0]["expectedText"] = "Alpha Beta Gamma Delta"
            write_json(manifest, data)
            write_json(baseline, self._zero_baseline())
            write_text(
                results / "html-basic/index.html",
                """<!doctype html><html><head><link href="assets/missing.css" rel="stylesheet"></head>
                <body><span style="position:absolute">Alpha</span></body></html>""",
            )
            write_json(
                results / "html-basic/diagnostics.json",
                {"diagnostics": [{"severity": "warning", "message": "Synthetic diagnostic"}]},
            )
            write_json(
                results / "html-basic/visual.json",
                {
                    "checks": [
                        {
                            "name": "foreground-mask",
                            "passed": False,
                            "message": "Foreground mask drift exceeded threshold.",
                            "metrics": {"pdfMissRatio": 0.5},
                        }
                    ]
                },
            )

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--ratchet-baseline",
                    str(baseline),
                    "--fail-on-regression",
                ]
            )

            self.assertEqual(1, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual("failed", comparison["fixtures"][0]["status"])
            self.assertEqual(1, comparison["summary"]["categories"]["text-coverage"])
            self.assertEqual(1, comparison["summary"]["categories"]["broken-assets"])
            self.assertEqual(1, comparison["summary"]["categories"]["diagnostics"])
            self.assertEqual(2, comparison["summary"]["categories"]["dom"])
            self.assertEqual(2, comparison["summary"]["categories"]["visual"])
            checks = {check["category"]: check for check in comparison["fixtures"][0]["qualityChecks"]}
            self.assertEqual("failed", checks["dom"]["status"])
            self.assertEqual("failed", checks["text-coverage"]["status"])
            self.assertEqual("failed", checks["visual"]["status"])
            self.assertFalse(comparison["ratchet"]["passed"])

    def test_known_divergence_is_reported_separately_and_can_be_ratchet_accepted(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            baseline = root / "ratchet.json"
            known = root / "known.json"

            data = self._manifest()
            data["fixtures"][0]["expectedText"] = "Alpha Beta Gamma Delta"
            write_json(manifest, data)
            write_json(
                baseline,
                {
                    "schema": 1,
                    "maxStatus": {"failed": 0, "known": 1},
                    "maxCategories": {"text-coverage": 1},
                },
            )
            write_json(
                known,
                {
                    "schema": 1,
                    "divergences": [
                        {
                            "id": "known-low-coverage",
                            "fixture": "html-basic",
                            "category": "text-coverage",
                            "issue": "#631",
                            "reason": "Synthetic test verifies known-divergence classification.",
                        }
                    ],
                },
            )
            write_text(
                results / "html-basic/index.html",
                """<!doctype html><html><body>
                <section data-page-number="1">
                <span style="position:absolute">Alpha</span>
                <span>Supplemental DOM text</span>
                </section>
                </body></html>""",
            )
            write_json(results / "html-basic/diagnostics.json", {"diagnostics": []})
            self._write_passing_visual(results / "html-basic/visual.json")

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--known-divergences",
                    str(known),
                    "--ratchet-baseline",
                    str(baseline),
                    "--fail-on-unexpected",
                    "--fail-on-regression",
                ]
            )

            self.assertEqual(0, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual("known", comparison["fixtures"][0]["status"])
            self.assertEqual("known-low-coverage", comparison["fixtures"][0]["failures"][0]["known"]["id"])

    def test_converter_command_failure_is_crash_category(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            baseline = root / "ratchet.json"

            write_json(manifest, self._manifest())
            write_json(baseline, self._zero_baseline())

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--ratchet-baseline",
                    str(baseline),
                    "--converter-command",
                    f"{shlex.quote(sys.executable)} -c \"import sys; sys.exit(7)\"",
                    "--fail-on-regression",
                ]
            )

            self.assertEqual(1, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual("failed", comparison["fixtures"][0]["status"])
            self.assertEqual(1, comparison["summary"]["categories"]["crash"])
            self.assertEqual(7, comparison["fixtures"][0]["failures"][0]["exitCode"])

    def test_markdown_fixture_reports_structures_and_diagnostic_provenance(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            data = {
                "schema": 1,
                "fixtures": [
                    {
                        "id": "markdown-tagged",
                        "target": "markdown",
                        "outputs": {
                            "markdown": "markdown-tagged/document.md",
                            "diagnostics": "markdown-tagged/diagnostics.json",
                        },
                        "expectedText": "Title Intro link First Nested Diagram Name Value Alpha 42",
                        "expectations": {
                            "minTextCoverage": 1.0,
                            "maxDiagnostics": 1,
                            "markdownStructures": {
                                "headings": 1,
                                "paragraphs": 1,
                                "orderedListItems": 1,
                                "unorderedListItems": 1,
                                "links": 1,
                                "images": 1,
                                "tableRows": 2,
                            },
                        },
                    }
                ],
            }
            write_json(manifest, data)
            write_text(
                results / "markdown-tagged/document.md",
                """# Title

Intro [link](https://example.com).

1. First
    - Nested

![Diagram](assets/diagram.png)

| Name | Value |
| --- | --- |
| Alpha | 42 |

```text
# Ignored code heading
- Ignored code list
![Ignored](ignored.png)
```
""",
            )
            write_json(
                results / "markdown-tagged/diagnostics.json",
                {
                    "source": "SemanticStructure",
                    "confidence": "High",
                    "diagnostics": [
                        {
                            "code": "markdown-semantic-structure-used",
                            "source": "SemanticStructure",
                        }
                    ],
                },
            )

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--fail-on-unexpected",
                ]
            )

            self.assertEqual(0, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            fixture = comparison["fixtures"][0]
            structures = fixture["metrics"]["markdownStructures"]
            self.assertEqual(1.0, structures["matchRatio"])
            self.assertEqual(7, structures["matched"])
            self.assertEqual(
                {"markdown-semantic-structure-used": 1},
                fixture["metrics"]["diagnosticCodes"],
            )
            self.assertEqual({"SemanticStructure": 1}, fixture["metrics"]["diagnosticSources"])
            self.assertEqual("SemanticStructure", fixture["metrics"]["diagnosticSource"])
            self.assertEqual("High", fixture["metrics"]["diagnosticConfidence"])
            checks = {check["category"]: check for check in fixture["qualityChecks"]}
            self.assertEqual("passed", checks["markdown-structure"]["status"])
            self.assertEqual(1.0, comparison["summary"]["metrics"]["minimumMarkdownStructureMatch"])

    def test_markdown_structure_mismatch_is_a_quality_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            write_json(
                manifest,
                {
                    "schema": 1,
                    "fixtures": [
                        {
                            "id": "markdown-mismatch",
                            "target": "markdown",
                            "outputs": {"markdown": "document.md"},
                            "expectedText": "Only prose",
                            "expectations": {
                                "minTextCoverage": 1.0,
                                "markdownStructures": {"headings": 1, "paragraphs": 1},
                            },
                        }
                    ],
                },
            )
            write_text(results / "document.md", "Only prose\n")

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--fail-on-unexpected",
                ]
            )

            self.assertEqual(1, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            fixture = comparison["fixtures"][0]
            self.assertEqual("markdown-structure", fixture["failures"][0]["category"])
            self.assertEqual(0.5, fixture["metrics"]["markdownStructures"]["matchRatio"])
            checks = {check["category"]: check for check in fixture["qualityChecks"]}
            self.assertEqual("failed", checks["markdown-structure"]["status"])

    def test_markdown_heuristic_profiles_report_labeled_metrics_and_diagnostics(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            manifest = root / "manifest.json"
            results = root / "results"
            out_dir = root / "out"
            baseline = root / "ratchet.json"
            write_json(
                manifest,
                {
                    "schema": 1,
                    "fixtures": [
                        {
                            "id": "tagged",
                            "target": "markdown",
                            "markdownProfile": "tagged",
                            "outputs": {
                                "markdown": "tagged/document.md",
                                "diagnostics": "tagged/diagnostics.json",
                            },
                            "expectedText": "Title Intro",
                            "expectations": {
                                "minTextCoverage": 1.0,
                                "maxDiagnostics": 1,
                                "markdownHeuristics": {
                                    "readingOrder": ["Title", "Intro"],
                                    "headings": ["Title"],
                                    "requiredDiagnosticCodes": [
                                        "markdown-semantic-structure-used"
                                    ],
                                    "requiredConfidence": "High",
                                    "minMetrics": {
                                        "readingOrderAccuracy": 1.0,
                                        "headingF1": 1.0,
                                    },
                                },
                            },
                        },
                        {
                            "id": "simple-list-link",
                            "target": "markdown",
                            "markdownProfile": "simple-untagged",
                            "outputs": {
                                "markdown": "simple-list-link/document.md",
                                "diagnostics": "simple-list-link/diagnostics.json",
                            },
                            "expectedText": "First Second Docs",
                            "expectations": {
                                "minTextCoverage": 1.0,
                                "maxDiagnostics": 1,
                                "markdownHeuristics": {
                                    "readingOrder": "First Second Docs",
                                    "listItems": ["First", "Second"],
                                    "links": [
                                        {
                                            "text": "Docs",
                                            "target": "https://example.test",
                                        }
                                    ],
                                    "requiredDiagnosticCodes": [
                                        "markdown-layout-fallback-used"
                                    ],
                                    "requiredConfidence": "Low",
                                    "minMetrics": {
                                        "readingOrderAccuracy": 1.0,
                                        "listF1": 1.0,
                                        "linkAccuracy": 1.0,
                                    },
                                },
                            },
                        },
                        {
                            "id": "simple-table",
                            "target": "markdown",
                            "markdownProfile": "simple-untagged",
                            "outputs": {"markdown": "simple-table/document.md"},
                            "expectedText": "Name Value Alpha 42",
                            "expectations": {
                                "minTextCoverage": 1.0,
                                "markdownHeuristics": {
                                    "readingOrder": "Name Value Alpha 42",
                                    "tableCells": [
                                        ["Name", "Value"],
                                        ["Alpha", "42"],
                                    ],
                                    "minMetrics": {
                                        "readingOrderAccuracy": 1.0,
                                        "tableCellAccuracy": 1.0,
                                    },
                                },
                            },
                        },
                        {
                            "id": "ambiguous-columns",
                            "target": "markdown",
                            "markdownProfile": "ambiguous-untagged",
                            "outputs": {
                                "markdown": "ambiguous-columns/document.md",
                                "diagnostics": "ambiguous-columns/diagnostics.json",
                            },
                            "expectedText": "Left one Right one Left two Right two",
                            "expectations": {
                                "minTextCoverage": 1.0,
                                "maxDiagnostics": 2,
                                "markdownHeuristics": {
                                    "readingOrder": "Left one Right one Left two Right two",
                                    "requiredDiagnosticCodes": [
                                        "markdown-untagged-multicolumn-ambiguous"
                                    ],
                                    "requiredConfidence": "Low",
                                    "minMetrics": {"readingOrderAccuracy": 1.0},
                                },
                            },
                        },
                    ],
                },
            )
            write_text(results / "tagged/document.md", "# Title\n\nIntro\n")
            write_json(
                results / "tagged/diagnostics.json",
                {
                    "source": "SemanticStructure",
                    "confidence": "High",
                    "diagnostics": [
                        {
                            "code": "markdown-semantic-structure-used",
                            "source": "SemanticStructure",
                        }
                    ],
                },
            )
            write_text(
                results / "simple-list-link/document.md",
                "- First\n- Second\n\n[Docs](https://example.test)\n",
            )
            write_json(
                results / "simple-list-link/diagnostics.json",
                {
                    "source": "HeuristicFallback",
                    "confidence": "Low",
                    "diagnostics": [
                        {
                            "code": "markdown-layout-fallback-used",
                            "source": "HeuristicFallback",
                        }
                    ],
                },
            )
            write_text(
                results / "simple-table/document.md",
                "| Name | Value |\n| --- | --- |\n| Alpha | 42 |\n",
            )
            write_text(
                results / "ambiguous-columns/document.md",
                "Left one\n\nRight one\n\nLeft two\n\nRight two\n",
            )
            write_json(
                results / "ambiguous-columns/diagnostics.json",
                {
                    "source": "HeuristicFallback",
                    "confidence": "Low",
                    "diagnostics": [
                        {
                            "code": "markdown-untagged-multicolumn-ambiguous",
                            "source": "HeuristicFallback",
                        },
                        {
                            "code": "markdown-layout-fallback-used",
                            "source": "HeuristicFallback",
                        },
                    ],
                },
            )
            write_json(
                baseline,
                {
                    "schema": 1,
                    "maxStatus": {"failed": 0, "known": 0},
                    "maxCategories": {"markdown-heuristic": 0},
                    "minMetrics": {
                        "minimumMarkdownReadingOrderAccuracy": 1.0,
                        "minimumMarkdownHeadingF1": 1.0,
                        "minimumMarkdownListF1": 1.0,
                        "minimumMarkdownTableCellAccuracy": 1.0,
                        "minimumMarkdownLinkAccuracy": 1.0,
                    },
                    "minMarkdownProfiles": {
                        "tagged": {
                            "minimumMarkdownReadingOrderAccuracy": 1.0,
                            "minimumMarkdownHeadingF1": 1.0,
                        },
                        "simple-untagged": {
                            "minimumMarkdownReadingOrderAccuracy": 1.0,
                            "minimumMarkdownListF1": 1.0,
                            "minimumMarkdownTableCellAccuracy": 1.0,
                            "minimumMarkdownLinkAccuracy": 1.0,
                        },
                        "ambiguous-untagged": {
                            "minimumMarkdownReadingOrderAccuracy": 1.0,
                        },
                    },
                },
            )

            exit_code = harness.main(
                [
                    "--manifest",
                    str(manifest),
                    "--results-dir",
                    str(results),
                    "--out-dir",
                    str(out_dir),
                    "--ratchet-baseline",
                    str(baseline),
                    "--fail-on-unexpected",
                    "--fail-on-regression",
                ]
            )

            self.assertEqual(0, exit_code)
            comparison = json.loads((out_dir / "comparison.json").read_text(encoding="utf-8"))
            self.assertEqual(
                {"tagged", "simple-untagged", "ambiguous-untagged"},
                set(comparison["summary"]["markdownProfiles"]),
            )
            self.assertEqual(
                1.0,
                comparison["summary"]["metrics"]["minimumMarkdownReadingOrderAccuracy"],
            )
            simple = comparison["fixtures"][1]["metrics"]["markdownHeuristics"]
            self.assertEqual(1.0, simple["listF1"])
            self.assertEqual(1.0, simple["linkAccuracy"])
            table = comparison["fixtures"][2]["metrics"]["markdownHeuristics"]
            self.assertEqual(1.0, table["tableCellAccuracy"])
            checks = {
                check["category"]: check
                for check in comparison["fixtures"][3]["qualityChecks"]
            }
            self.assertEqual("passed", checks["markdown-heuristic"]["status"])
            summary_markdown = (out_dir / "summary.md").read_text(encoding="utf-8")
            self.assertIn("## Markdown Profiles", summary_markdown)
            self.assertIn("ambiguous-untagged", summary_markdown)

    def test_markdown_profile_ratchet_fails_when_reading_order_regresses(self) -> None:
        summary = {
            "status": {"passed": 1, "known": 0, "failed": 0},
            "categories": {},
            "metrics": {"minimumMarkdownReadingOrderAccuracy": 0.75},
            "markdownProfiles": {
                "simple-untagged": {
                    "metrics": {"minimumMarkdownReadingOrderAccuracy": 0.75}
                }
            },
        }
        ratchet = harness.check_ratchet(
            summary,
            {
                "minMetrics": {"minimumMarkdownReadingOrderAccuracy": 0.9},
                "minMarkdownProfiles": {
                    "simple-untagged": {
                        "minimumMarkdownReadingOrderAccuracy": 1.0
                    }
                },
            },
        )

        self.assertFalse(ratchet["passed"])
        self.assertEqual(2, len(ratchet["failures"]))

    @staticmethod
    def _manifest() -> dict:
        return {
            "schema": 1,
            "fixtures": [
                {
                    "id": "html-basic",
                    "target": "html",
                    "categories": ["html", "text"],
                    "outputs": {
                        "html": "html-basic/index.html",
                        "diagnostics": "html-basic/diagnostics.json",
                        "visual": "html-basic/visual.json",
                    },
                    "expectedText": "Hello PDF conversion Second line",
                    "expectations": {
                        "minTextCoverage": 1.0,
                        "maxBrokenLocalReferences": 0,
                        "maxDiagnostics": 0,
                        "minVisualChecks": 2,
                        "domSelectors": [
                            {"selector": "[data-page-number]", "count": 1},
                            {"selector": "span", "minCount": 2},
                        ],
                        "requiredSubstrings": [
                            {"output": "html", "text": "position:absolute"},
                        ],
                    },
                }
            ],
        }

    @staticmethod
    def _zero_baseline() -> dict:
        return {
            "schema": 1,
            "maxStatus": {"failed": 0, "known": 0},
            "maxCategories": {
                "broken-assets": 0,
                "crash": 0,
                "diagnostics": 0,
                "dom": 0,
                "markdown-structure": 0,
                "required-files": 0,
                "required-substrings": 0,
                "text-coverage": 0,
                "visual": 0,
            },
            "minMetrics": {"minimumTextCoverage": 1.0},
        }

    @staticmethod
    def _write_passing_visual(path: Path) -> None:
        write_json(
            path,
            {
                "checks": [
                    {
                        "name": "page-screenshot-dimensions",
                        "passed": True,
                        "metrics": {
                            "actualHeight": 1056,
                            "actualWidth": 816,
                            "expectedHeight": 1056,
                            "expectedWidth": 816,
                        },
                    },
                    {
                        "name": "foreground-mask",
                        "passed": True,
                        "metrics": {
                            "browserMissRatio": 0.01,
                            "foregroundDeltaRatio": 0.02,
                            "pdfMissRatio": 0.01,
                        },
                    },
                ]
            },
        )


if __name__ == "__main__":
    unittest.main()
