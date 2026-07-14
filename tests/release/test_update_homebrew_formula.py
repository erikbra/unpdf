import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "update_homebrew_formula.py"
SPEC = importlib.util.spec_from_file_location("update_homebrew_formula", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class HomebrewFormulaGeneratorTest(unittest.TestCase):
    def test_selects_all_homebrew_architectures_and_embeds_conversion_test(self):
        rids = ["osx-arm64", "osx-x64", "linux-arm64", "linux-x64", "win-x64"]
        manifest = {
            "version": "4.0.0-preview.1",
            "artifacts": [{
                "rid": rid,
                "url": f"https://example.test/unpdf-{rid}.tar.gz",
                "sha256": (rid.replace("-", "a") * 64)[:64],
            } for rid in rids],
        }
        formula = MODULE.generate_formula(manifest)
        for rid in rids[:-1]:
            self.assertIn(f"unpdf-{rid}.tar.gz", formula)
        self.assertNotIn("unpdf-win-x64", formula)
        self.assertIn('system bin/"unpdf", "fixture.pdf"', formula)
        self.assertIn('assert_match "Hello"', formula)

    def test_requires_every_homebrew_rid(self):
        with self.assertRaisesRegex(ValueError, "linux-arm64"):
            MODULE.generate_formula({"version": "1.0.0", "artifacts": []})
