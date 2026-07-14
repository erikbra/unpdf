import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "update_winget_manifests.py"
SPEC = importlib.util.spec_from_file_location("update_winget_manifests", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class WinGetManifestGeneratorTest(unittest.TestCase):
    def test_generates_portable_zip_manifest_from_release_contract(self):
        manifest = {"version": "4.0.0-preview.1", "artifacts": [{
            "rid": "win-x64",
            "url": "https://example.test/unpdf.zip",
            "sha256": "ab" * 32,
        }]}
        generated = MODULE.generate(manifest)
        self.assertEqual(3, len(generated))
        installer = generated["ErikBra.Unpdf.installer.yaml"]
        self.assertIn("InstallerType: zip", installer)
        self.assertIn("NestedInstallerType: portable", installer)
        self.assertIn("PortableCommandAlias: unpdf", installer)
        self.assertIn("AB" * 32, installer)

    def test_requires_windows_artifact(self):
        with self.assertRaisesRegex(ValueError, "win-x64"):
            MODULE.generate({"version": "1.0.0", "artifacts": []})
