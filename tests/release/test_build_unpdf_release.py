import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "build_unpdf_release.py"
SPEC = importlib.util.spec_from_file_location("build_unpdf_release", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class ReleaseBuilderTest(unittest.TestCase):
    def test_archive_names_are_stable(self):
        self.assertEqual("unpdf-4.0.0-preview.1-linux-x64.tar.gz", MODULE.archive_name("4.0.0-preview.1", "linux-x64"))
        self.assertEqual("unpdf-4.0.0-win-x64.zip", MODULE.archive_name("4.0.0", "win-x64"))

    def test_version_validation_rejects_non_semver(self):
        self.assertEqual("4.0.0-preview.1", MODULE.validate_version("4.0.0-preview.1"))
        with self.assertRaises(ValueError):
            MODULE.validate_version("latest")

    def test_sbom_contains_executable_and_nuget_dependency(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / "unpdf"
            executable.write_bytes(b"native executable")
            assets = root / "project.assets.json"
            assets.write_text(json.dumps({"libraries": {"Example.Package/1.2.3": {"type": "package"}}}))
            MODULE.create_sbom(root, "4.0.0-preview.1", "linux-x64", executable, assets)
            sbom = json.loads((root / "sbom.spdx.json").read_text())
            self.assertEqual("SPDX-2.3", sbom["spdxVersion"])
            self.assertEqual(["unpdf", "Example.Package"], [package["name"] for package in sbom["packages"]])
            self.assertEqual(MODULE.sha256(executable), sbom["files"][0]["checksums"][0]["checksumValue"])

    def test_signed_status_cannot_be_asserted_on_wrong_platform(self):
        if os.name == "nt":
            with self.assertRaisesRegex(RuntimeError, "native macOS runner"):
                MODULE.verify_signing(Path("unpdf"), "osx-arm64", "macos-developer-id-notarized")
        else:
            with self.assertRaisesRegex(RuntimeError, "native Windows runner"):
                MODULE.verify_signing(Path("unpdf.exe"), "win-x64", "windows-authenticode")


if __name__ == "__main__":
    unittest.main()
