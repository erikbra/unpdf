import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "harden_wasm_site.py"
SPEC = importlib.util.spec_from_file_location("harden_wasm_site", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class HardenWasmSiteTest(unittest.TestCase):
    def test_hardens_inline_import_map_and_emits_host_configs(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "index.html").write_text(
                """<!doctype html><html><head><meta charset=\"utf-8\"></head><body>
<script type=\"importmap\">{\"imports\":{\"app\":\"./app.js\"}}</script>
<script src=\"app.js\"></script></body></html>""",
                encoding="utf-8",
            )
            (root / "index.html.br").write_bytes(b"stale")
            (root / "index.html.gz").write_bytes(b"stale")

            MODULE.harden_site(root)
            MODULE.verify_site(root)

            document = (root / "index.html").read_text(encoding="utf-8")
            self.assertIn("id=\"unpdf-csp\"", document)
            self.assertIn("'wasm-unsafe-eval'", document)
            self.assertIn("'sha256-", document)
            self.assertIn("connect-src 'self'", document)
            script_policy = document.split("script-src", 1)[1].split("style-src", 1)[0]
            self.assertNotIn("'unsafe-inline'", script_policy)
            self.assertFalse((root / "index.html.br").exists())
            self.assertFalse((root / "index.html.gz").exists())

            cloudflare = (root / "_headers").read_text(encoding="utf-8")
            self.assertIn("Content-Security-Policy:", cloudflare)
            self.assertIn("frame-ancestors 'none'", cloudflare)
            self.assertIn("X-Content-Type-Options: nosniff", cloudflare)
            azure = json.loads((root / "staticwebapp.config.json").read_text(encoding="utf-8"))
            self.assertEqual("DENY", azure["globalHeaders"]["X-Frame-Options"])

    def test_verify_rejects_tampered_inline_script(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "index.html").write_text(
                "<html><head></head><body><script>first()</script></body></html>",
                encoding="utf-8",
            )
            MODULE.harden_site(root)
            index = root / "index.html"
            index.write_text(
                index.read_text(encoding="utf-8").replace("first()", "second()"),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "inline script hashes"):
                MODULE.verify_site(root)


if __name__ == "__main__":
    unittest.main()
