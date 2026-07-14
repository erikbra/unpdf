import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "create_unpdf_release_manifest.py"


class CombinedManifestTest(unittest.TestCase):
    def test_combines_exact_supported_rid_set_and_signing_states(self):
        version = "4.0.0-preview.1"
        rids = ["linux-x64", "linux-arm64", "win-x64", "osx-x64", "osx-arm64"]
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for rid in rids:
                suffix = ".zip" if rid == "win-x64" else ".tar.gz"
                archive = f"unpdf-{version}-{rid}{suffix}"
                (directory / f"{archive}.sha256").write_text(f"{'a' * 64}  {archive}\n")
                (directory / f"unpdf-{version}-{rid}.manifest.json").write_text(json.dumps({
                    "signing": {"status": "unsigned-preview"}
                }))
            output = directory / "release-manifest.json"
            subprocess.run([
                sys.executable, str(SCRIPT), "--version", version,
                "--repository", "erikbra/pdfbox-net", "--directory", str(directory),
                "--output", str(output),
            ], check=True)
            manifest = json.loads(output.read_text())
            self.assertEqual(sorted(rids), [artifact["rid"] for artifact in manifest["artifacts"]])
            self.assertTrue(all(artifact["url"].startswith("https://github.com/erikbra/pdfbox-net/releases/download/")
                                for artifact in manifest["artifacts"]))
