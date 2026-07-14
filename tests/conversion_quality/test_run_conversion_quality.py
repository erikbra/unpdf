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
