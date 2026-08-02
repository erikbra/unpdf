import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "publish_wasm_pages_site.py"
LANDING_PAGE = Path(__file__).resolve().parents[2] / "packaging" / "pages" / "index.html"
SPEC = importlib.util.spec_from_file_location("publish_wasm_pages_site", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


def create_application(root: Path) -> Path:
    application = root / "application"
    (application / "_framework").mkdir(parents=True)
    (application / "samples").mkdir()
    (application / "index.html").write_text(
        '<!doctype html><html><head><base href="/"></head><body>WASM</body></html>',
        encoding="utf-8",
    )
    (application / "index.html.br").write_bytes(b"stale compressed index")
    (application / "index.html.gz").write_bytes(b"stale compressed index")
    (application / "_framework/runtime.wasm").write_bytes(b"wasm")
    (application / "_framework/runtime.wasm.br").write_bytes(b"compressed wasm")
    (application / "samples/hello.pdf").write_bytes(b"%PDF-1.4")
    (application / "_headers").write_text("/*\n  X-Frame-Options: DENY\n", encoding="utf-8")
    (application / "staticwebapp.config.json").write_text("{}\n", encoding="utf-8")
    return application


def create_templates(root: Path) -> tuple[Path, Path]:
    landing = root / "landing.html"
    not_found = root / "404.html"
    landing.write_text("<!doctype html><title>PdfBox.Net</title>", encoding="utf-8")
    not_found.write_text(
        '<!doctype html><script>const appBase = "__WASM_BASE_PATH__";</script>',
        encoding="utf-8",
    )
    return landing, not_found


class WasmPagesPublisherTest(unittest.TestCase):
    def test_production_landing_page_links_to_unpdf_source(self):
        page = LANDING_PAGE.read_text(encoding="utf-8")

        self.assertIn('href="https://github.com/erikbra/unpdf">View source</a>', page)
        self.assertIn('href="https://github.com/erikbra/unpdf">unpdf on GitHub</a>', page)
        self.assertNotIn('href="https://github.com/erikbra/pdfbox-net"', page)

    def test_publishes_app_and_preserves_existing_pages_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            application = create_application(root)
            landing, not_found = create_templates(root)
            site = root / "site"
            (site / "apt").mkdir(parents=True)
            (site / "apt/InRelease").write_text("signed repository", encoding="ascii")
            (site / "wasm").mkdir()
            (site / "wasm/stale.txt").write_text("old deployment", encoding="ascii")

            MODULE.publish_site(
                application,
                site,
                landing,
                not_found,
                "/unpdf/now/",
                "now",
                ("wasm",),
            )

            self.assertEqual(
                "signed repository",
                (site / "apt/InRelease").read_text(encoding="ascii"),
            )
            self.assertIn(
                "../now/",
                (site / "wasm/index.html").read_text(encoding="utf-8"),
            )
            self.assertIn(
                '<base href="/unpdf/now/">',
                (site / "now/index.html").read_text(encoding="utf-8"),
            )
            self.assertFalse((site / "now/index.html.br").exists())
            self.assertFalse((site / "now/index.html.gz").exists())
            self.assertTrue((site / "now/_framework/runtime.wasm.br").is_file())
            self.assertTrue((site / "now/_headers").is_file())
            self.assertTrue((site / "now/staticwebapp.config.json").is_file())
            self.assertEqual(landing.read_text(encoding="utf-8"), (site / "index.html").read_text(encoding="utf-8"))
            self.assertIn(
                'const appBase = "/unpdf/now/";',
                (site / "404.html").read_text(encoding="utf-8"),
            )
            self.assertTrue((site / ".nojekyll").is_file())

    def test_rejects_non_absolute_base_path(self):
        with self.assertRaisesRegex(ValueError, "start and end"):
            MODULE.normalize_base_path("pdfbox-net/wasm")

    def test_requires_complete_published_application(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            application = create_application(root)
            landing, not_found = create_templates(root)
            (application / "samples/hello.pdf").unlink()

            with self.assertRaises(FileNotFoundError) as raised:
                MODULE.publish_site(
                    application,
                    root / "site",
                    landing,
                    not_found,
                    "/pdfbox-net/wasm/",
                )
            self.assertIn(str(Path("samples") / "hello.pdf"), str(raised.exception))

    def test_rejects_unsafe_application_path(self):
        for value in ("/now", "now/", "../now", "now\\nested"):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    MODULE.normalize_application_path(value)
