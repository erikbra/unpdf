import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "publish_unpdf_apt_site.py"
SPEC = importlib.util.spec_from_file_location("publish_unpdf_apt_site", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)

FINGERPRINT = "0123456789ABCDEF0123456789ABCDEF01234567"


def create_repository(root: Path, suite: str, package_name: str) -> Path:
    repository = root / f"repository-{suite}"
    (repository / f"dists/{suite}").mkdir(parents=True)
    (repository / "pool/main/u/unpdf").mkdir(parents=True)
    (repository / f"dists/{suite}/InRelease").write_text(suite, encoding="ascii")
    (repository / f"pool/main/u/unpdf/{package_name}").write_text("package", encoding="ascii")
    (repository / "unpdf-archive-keyring.gpg").write_bytes(b"keyring")
    (repository / "unpdf-archive-key.asc").write_text("public key", encoding="ascii")
    return repository


class AptPagesPublisherTest(unittest.TestCase):
    def test_stable_and_preview_suites_coexist(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            site = root / "site"
            preview = create_repository(root, "preview", "unpdf_preview_amd64.deb")
            stable = create_repository(root, "stable", "unpdf_stable_amd64.deb")

            MODULE.merge_repository(preview, site, "preview", "https://example.test/apt", FINGERPRINT)
            MODULE.merge_repository(stable, site, "stable", "https://example.test/apt", FINGERPRINT)

            self.assertTrue((site / "apt/dists/preview/InRelease").is_file())
            self.assertTrue((site / "apt/dists/stable/InRelease").is_file())
            self.assertTrue((site / "apt/pool/main/u/unpdf/unpdf_preview_amd64.deb").is_file())
            self.assertTrue((site / "apt/pool/main/u/unpdf/unpdf_stable_amd64.deb").is_file())
            page = (site / "apt/index.html").read_text(encoding="utf-8")
            self.assertIn("Preview channel", page)
            self.assertIn("Stable channel", page)
            self.assertIn("0123 4567 89AB CDEF", page)

    def test_rejects_invalid_fingerprint(self):
        with self.assertRaisesRegex(ValueError, "40 hexadecimal"):
            MODULE.normalize_fingerprint("not-a-key")
