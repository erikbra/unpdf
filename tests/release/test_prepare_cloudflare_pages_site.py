import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "prepare_cloudflare_pages_site.py"
SPEC = importlib.util.spec_from_file_location("prepare_cloudflare_pages_site", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


def create_site(root: Path) -> Path:
    site = root / "site"
    application = site / "now"
    (application / "_framework").mkdir(parents=True)
    (application / "index.html").write_text(
        """<!doctype html><html><head><base href="/unpdf/now/"></head>
<body><script>startApp()</script></body></html>""",
        encoding="utf-8",
    )
    (application / "_framework/runtime.wasm").write_bytes(b"wasm")
    (application / "_headers").write_text("/*\n  X-Frame-Options: DENY\n", encoding="utf-8")
    (application / "staticwebapp.config.json").write_text("{}\n", encoding="utf-8")
    (site / "index.html").write_text("<!doctype html><title>unpdf</title>", encoding="utf-8")
    (site / "404.html").write_text(
        """<!doctype html><a href="/unpdf/">Return to unpdf</a><script>
const appBase = "/unpdf/now/";
fallback(appBase);
</script>""",
        encoding="utf-8",
    )

    example = site / "examples" / "sample"
    (example / "assets").mkdir(parents=True)
    (example / "semantic").mkdir()
    (example / "semantic-continuous").mkdir()
    (example / "index.html").write_text("fixed", encoding="utf-8")
    (example / "assets/fixed.css").write_text("fixed", encoding="utf-8")
    (example / "semantic/index.html").write_text("paged", encoding="utf-8")
    (example / "semantic-continuous/index.html").write_text("continuous", encoding="utf-8")
    (site / "examples/index.html").write_text(
        '<a href="sample/semantic-continuous/index.html">continuous semantic HTML</a>',
        encoding="utf-8",
    )
    return site


class PrepareCloudflarePagesSiteTest(unittest.TestCase):
    def test_prepares_host_paths_headers_and_removes_legacy_outputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            site = create_site(Path(temporary))

            file_count, largest_file_size, removed = MODULE.prepare_site(site, "now", "/now/")

            self.assertGreater(file_count, 0)
            self.assertGreater(largest_file_size, 0)
            self.assertEqual(3, removed)
            self.assertIn(
                '<base href="/now/">',
                (site / "now/index.html").read_text(encoding="utf-8"),
            )
            self.assertIn(
                'const appBase = "/now/";',
                (site / "404.html").read_text(encoding="utf-8"),
            )
            self.assertIn(
                '<a href="/">Return to unpdf</a>',
                (site / "404.html").read_text(encoding="utf-8"),
            )
            headers = (site / "_headers").read_text(encoding="utf-8")
            self.assertIn("/now/*\n", headers)
            self.assertIn("/now/index.html\n", headers)
            self.assertIn("/now/_framework/*\n", headers)
            self.assertGreaterEqual(headers.count("'sha256-"), 2)
            self.assertFalse((site / "now/_headers").exists())
            self.assertFalse((site / "now/staticwebapp.config.json").exists())
            self.assertFalse((site / "examples/sample/index.html").exists())
            self.assertFalse((site / "examples/sample/assets").exists())
            self.assertFalse((site / "examples/sample/semantic").exists())
            self.assertTrue((site / "examples/sample/semantic-continuous/index.html").is_file())
            self.assertIn(
                ">html</a>",
                (site / "examples/index.html").read_text(encoding="utf-8"),
            )

    def test_rejects_mismatched_application_and_base_paths(self):
        with tempfile.TemporaryDirectory() as temporary:
            site = create_site(Path(temporary))
            with self.assertRaisesRegex(ValueError, "must match"):
                MODULE.prepare_site(site, "now", "/wasm/")

    def test_rejects_files_over_the_configured_limit(self):
        with tempfile.TemporaryDirectory() as temporary:
            site = Path(temporary)
            (site / "large.bin").write_bytes(b"1234")
            with self.assertRaisesRegex(ValueError, "large.bin"):
                MODULE.validate_pages_limits(site, max_file_size=3)


if __name__ == "__main__":
    unittest.main()
