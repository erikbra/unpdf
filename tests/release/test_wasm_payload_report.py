import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).parents[2] / "eng" / "wasm_payload_report.py"
SPEC = importlib.util.spec_from_file_location("wasm_payload_report", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
wasm_payload_report = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(wasm_payload_report)


class WasmPayloadReportTests(unittest.TestCase):
    def test_collect_payload_reports_normalized_raw_and_brotli_assets(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            publish_directory = Path(temporary_directory)
            framework_directory = publish_directory / "_framework"
            framework_directory.mkdir()
            self.write_asset(
                framework_directory, "PdfBox.Net.abcdefghij.wasm", 100, 55
            )
            self.write_asset(
                framework_directory, "dotnet.native.1234567890.wasm", 500, 200
            )

            report = wasm_payload_report.collect_payload(publish_directory)

            self.assertEqual(
                {
                    "assetCount": 2,
                    "rawBytes": 600,
                    "brotliBytes": 255,
                },
                report["totals"],
            )
            self.assertEqual(
                ["PdfBox.Net.wasm", "dotnet.native.wasm"],
                [asset["asset"] for asset in report["assets"]],
            )
            self.assertEqual("managed-assembly", report["assets"][0]["kind"])
            self.assertEqual("native-runtime", report["assets"][1]["kind"])

    def test_collect_payload_requires_brotli_sidecar(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            framework_directory = Path(temporary_directory) / "_framework"
            framework_directory.mkdir()
            (framework_directory / "uncompressed.wasm").write_bytes(b"wasm")

            with self.assertRaisesRegex(ValueError, "no Brotli sidecar"):
                wasm_payload_report.collect_payload(Path(temporary_directory))

    def test_baseline_fails_growth_and_unexplained_assets(self):
        report = self.report(
            [
                self.asset("existing.wasm", 1200, 500),
                self.asset("new.wasm", 100, 50),
            ]
        )
        baseline = self.baseline(
            {"existing.wasm": {"rawBytes": 1000, "brotliBytes": 400}},
            raw_bytes=1000,
            brotli_bytes=400,
            percent=1,
            minimum_bytes=10,
        )

        comparison = wasm_payload_report.compare_with_baseline(report, baseline)

        self.assertEqual("failed", comparison["status"])
        self.assertTrue(
            any("New payload asset new.wasm" in item for item in comparison["violations"])
        )
        self.assertTrue(
            any("existing.wasm raw size" in item for item in comparison["violations"])
        )
        self.assertTrue(
            any("Total raw payload" in item for item in comparison["violations"])
        )

    def test_baseline_allows_small_noise_and_suggests_downward_ratchet(self):
        report = self.report([self.asset("existing.wasm", 995, 390)])
        baseline = self.baseline(
            {
                "existing.wasm": {"rawBytes": 1000, "brotliBytes": 400},
                "removed.wasm": {"rawBytes": 50, "brotliBytes": 25},
            },
            raw_bytes=1050,
            brotli_bytes=425,
            percent=1,
            minimum_bytes=10,
        )

        comparison = wasm_payload_report.compare_with_baseline(report, baseline)

        self.assertEqual("passed", comparison["status"])
        self.assertEqual(["removed.wasm"], comparison["removedAssets"])
        self.assertTrue(comparison["ratchetSuggested"])

    def test_machine_and_markdown_reports_are_writable(self):
        report = self.report([self.asset("PdfBox.Net.wasm", 2048, 1024)])
        report["budget"] = {
            "status": "passed",
            "violations": [],
            "removedAssets": [],
            "ratchetSuggested": False,
        }

        with tempfile.TemporaryDirectory() as temporary_directory:
            output = Path(temporary_directory) / "report.json"
            wasm_payload_report.write_json(output, report)
            decoded = json.loads(output.read_text(encoding="utf-8"))

        markdown = wasm_payload_report.markdown_report(report)
        self.assertEqual(2048, decoded["totals"]["rawBytes"])
        self.assertIn("2.00 KiB raw", markdown)
        self.assertIn("`PdfBox.Net.wasm`", markdown)
        self.assertIn("Ratcheted baseline: **PASSED**", markdown)

    @staticmethod
    def write_asset(directory: Path, name: str, raw_bytes: int, brotli_bytes: int):
        (directory / name).write_bytes(b"x" * raw_bytes)
        (directory / f"{name}.br").write_bytes(b"b" * brotli_bytes)
        (directory / f"{name}.gz").write_bytes(b"g")

    @staticmethod
    def asset(name: str, raw_bytes: int, brotli_bytes: int):
        return {
            "asset": name,
            "publishedPath": f"_framework/{name}",
            "kind": "managed-assembly",
            "rawBytes": raw_bytes,
            "brotliBytes": brotli_bytes,
        }

    @staticmethod
    def report(assets):
        return {
            "schemaVersion": 1,
            "scope": "test",
            "totals": {
                "assetCount": len(assets),
                "rawBytes": sum(asset["rawBytes"] for asset in assets),
                "brotliBytes": sum(asset["brotliBytes"] for asset in assets),
            },
            "assets": assets,
        }

    @staticmethod
    def baseline(
        assets, raw_bytes, brotli_bytes, percent=1, minimum_bytes=1024
    ):
        return {
            "schemaVersion": 1,
            "growthAllowance": {
                "percent": percent,
                "minimumBytes": minimum_bytes,
            },
            "totals": {
                "rawBytes": raw_bytes,
                "brotliBytes": brotli_bytes,
            },
            "assets": assets,
        }


if __name__ == "__main__":
    unittest.main()
