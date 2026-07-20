from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools/conversion_quality/run_remote_corpus.py"
SPEC = importlib.util.spec_from_file_location("remote_corpus", MODULE_PATH)
assert SPEC and SPEC.loader
remote_corpus = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = remote_corpus
SPEC.loader.exec_module(remote_corpus)


class Response(io.BytesIO):
    def __init__(self, content: bytes, url: str = "https://example.test/sample.pdf"):
        super().__init__(content)
        self._url = url

    def geturl(self):
        return self._url

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()


class RemoteCorpusTest(unittest.TestCase):
    def test_checked_in_manifest_has_pinned_https_documents(self) -> None:
        description, documents = remote_corpus.load_manifest(remote_corpus.DEFAULT_MANIFEST)

        self.assertIn("public PDFs", description)
        self.assertEqual(
            [
                "jmlr-lda",
                "acl-bert",
                "arxiv-adam",
                "arxiv-unet",
                "arxiv-svt",
                "arxiv-ddpm",
                "irs-w9",
                "uscis-i9",
                "nist-sp800-171r3",
                "nps-mount-rainier",
                "nps-point-reyes-map",
                "arxiv-attention",
                "arxiv-scaling-hamiltonian",
                "fbi-fd258",
                "opm-of306",
                "nasa-artemis-i",
                "nasa-apollo11-scan",
                "raspberry-pi4-datasheet",
                "nist-floating-point-slides",
                "un-system-arabic",
                "eeas-europe-japanese",
                "eu-access-city-form",
            ],
            [item.id for item in documents],
        )
        self.assertTrue(all(item.source_page.startswith("https://") for item in documents))
        self.assertTrue(all(item.pdf_url.startswith("https://") for item in documents))
        self.assertTrue(all(len(item.sha256) == 64 for item in documents))
        mount_rainier = next(item for item in documents if item.id == "nps-mount-rainier")
        self.assertEqual({"2": 1}, mount_rainier.expectations["minImagePlacementsByPage"])
        nist = next(item for item in documents if item.id == "nist-sp800-171r3")
        self.assertEqual(15, nist.quality_pages)
        uscis = next(item for item in documents if item.id == "uscis-i9")
        self.assertEqual(4, uscis.quality_pages)
        self.assertEqual(
            {"2": 3},
            uscis.expectations["semanticRuledGridColumnCountsByPage"],
        )
        self.assertEqual(
            {"2": 26},
            uscis.expectations["semanticRuledGridSourceBorderCountsByPage"],
        )
        self.assertEqual(
            {"2": [6, 2, 2, 9, 3, 7, 3]},
            uscis.expectations["semanticOrderedListItemCountsByPage"],
        )
        self.assertEqual(
            {"2": [3]},
            uscis.expectations["semanticUnorderedListItemCountsByPage"],
        )
        bert = next(item for item in documents if item.id == "acl-bert")
        self.assertEqual(2, bert.quality_pages)
        unet = next(item for item in documents if item.id == "arxiv-unet")
        self.assertEqual(8, unet.quality_pages)
        w9 = next(item for item in documents if item.id == "irs-w9")
        self.assertEqual(6, w9.quality_pages)
        self.assertEqual(
            {"1": [4]},
            w9.expectations["semanticOrderedListItemCountsByPage"],
        )
        fbi = next(item for item in documents if item.id == "fbi-fd258")
        self.assertEqual(
            {"2": 1},
            fbi.expectations["semanticMixedRegionCountsByPage"],
        )
        self.assertEqual(
            {
                "jmlr-lda": (
                    "https://www.jmlr.org/papers/volume3/blei03a/blei03a.pdf",
                    "4667de63545b57d55d6c43e5af6f3429edfaac9472ed9eff68fdf43572735dd9",
                ),
                "acl-bert": (
                    "https://aclanthology.org/N19-1423.pdf",
                    "987545ffb087f1ece898142c403a516baeabeb70ce19089397fac6f7db12c3d4",
                ),
                "arxiv-adam": (
                    "https://arxiv.org/pdf/1412.6980",
                    "eab9c73ae2ceda884b94830bda99312254bac4806f6c9f045cbab90721ecda31",
                ),
                "arxiv-unet": (
                    "https://arxiv.org/pdf/1505.04597",
                    "a3172b2124f38e260dc2c7ed968d87c31bc94dbc19a42a7ab3dcbd7534319c44",
                ),
                "arxiv-svt": (
                    "https://arxiv.org/pdf/0810.3286",
                    "5f7f969ec4caf973e49a76524ea9baca3b61ec9ee6334db478968e06c3ac8a76",
                ),
                "arxiv-ddpm": (
                    "https://arxiv.org/pdf/2006.11239",
                    "aee5e07a802e8dfd2a386374c94fd61d1d056cb7e1e0fec4f28e8120ff5d8505",
                ),
                "irs-w9": (
                    "https://www.irs.gov/pub/irs-pdf/fw9.pdf",
                    "2d420cbb4123dcf1fb82595b2359cfbb5d81f00b9df9d359fcc7af361d093f53",
                ),
                "uscis-i9": (
                    "https://www.uscis.gov/sites/default/files/document/forms/i-9.pdf",
                    "780f348c34df694bb0b4dbbfaf9f22b99b9757b80d16a37ba89aadf069597281",
                ),
                "nist-sp800-171r3": (
                    "https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-171r3.pdf",
                    "3e4631df8b5d61f40a6e542b52779ef30ddbbfff31e09214fa94ad6e6f5e6d08",
                ),
                "nps-mount-rainier": (
                    "https://www.nps.gov/mora/planyourvisit/upload/Mount-Rainier-Brochure-final_Combo_508_v2023.pdf",
                    "c2e3006635137aef1fa67b0787280b1602c0f19535f6f2a21df6e9c286186e49",
                ),
                "nps-point-reyes-map": (
                    "https://www.nps.gov/pore/planyourvisit/upload/map_park.pdf",
                    "dcc4afa633ad924a69a35a9bdd19bd3b29782849b7993158acab720260168d2f",
                ),
                "arxiv-attention": (
                    "https://arxiv.org/pdf/1706.03762v7",
                    "bdfaa68d8984f0dc02beaca527b76f207d99b666d31d1da728ee0728182df697",
                ),
                "arxiv-scaling-hamiltonian": (
                    "https://arxiv.org/pdf/1910.14368v1",
                    "c55618121c95dfa301aea350b74eca7e902690396629041f486414a513ca079e",
                ),
                "fbi-fd258": (
                    "https://www.fbi.gov/file-repository/cjis/fd-258.pdf/@@download/file/FD-258fillable.pdf",
                    "04f108e1adadefef05c0ab68682bd7f490534920086762508c5163f0b90784b5",
                ),
                "opm-of306": (
                    "https://www.opm.gov/media/dxrbwvmb/"
                    "declaration-for-federal-employment-optional-form-august-2023.pdf",
                    "b690ec40015e42b0ed0424e5c0193616dc6491b70525240463d24b951e6ce46c",
                ),
                "nasa-artemis-i": (
                    "https://www.nasa.gov/wp-content/uploads/2026/01/artemis-i-press-kit.pdf",
                    "551f510e493f3ef9bc834afe740d659b1f9772f3dc39845aba0bd7a8433b40c7",
                ),
                "nasa-apollo11-scan": (
                    "https://www.nasa.gov/wp-content/uploads/static/apollo50th/pdf/A11_PressKit.pdf",
                    "fcb1ae7a88e5251559dde0b7d51ec71f06795cf78f64491aa9594fdf9ca89334",
                ),
                "raspberry-pi4-datasheet": (
                    "https://datasheets.raspberrypi.com/rpi4/raspberry-pi-4-datasheet.pdf",
                    "8febd042d004c7a2897a60fe6e1b2c007a941c3c4119f4fb791dc0afb669a860",
                ),
                "nist-floating-point-slides": (
                    "https://csrc.nist.gov/csrc/media/presentations/2026/mpts2026-2b2/"
                    "images-media/mpts2026-2b2-slides-float-point-ciadoux.pdf",
                    "492b156147376bd72dafd2ebb06c952d43c6682ce2e6553a0d2c33ab6904d71f",
                ),
                "un-system-arabic": (
                    "https://www.un.org/sites/un2.un.org/files/un_system_chart_arabic.pdf",
                    "a9f4dd6641db49258c14adf720fcf0424f9b51e2c7bec554ebcd53a7cff1466b",
                ),
                "eeas-europe-japanese": (
                    "https://www.eeas.europa.eu/sites/default/files/documents/2023/Lets%20explore%20Europe_JP_web.pdf",
                    "f9aa34f34d4c3cfa37dc01772296114c655988bad56eb9b54d4bd5c1f6a1e369",
                ),
                "eu-access-city-form": (
                    "https://access-city-award.ec.europa.eu/sites/default/files/downloads/"
                    "Annex%20I%20Application%20Form%20-%20ACA%202026%20-%20EN.pdf",
                    "59fc6f3f9ad902a3a98c4151f8719eedf0931f26111cf3c92d7683a5307fe4ca",
                ),
            },
            {item.id: (item.pdf_url, item.sha256) for item in documents},
        )

    def test_selection_defaults_to_all_documents_and_preserves_manifest_order(self) -> None:
        _, documents = remote_corpus.load_manifest(remote_corpus.DEFAULT_MANIFEST)

        self.assertEqual(documents, remote_corpus.select_documents(documents))
        selected = remote_corpus.select_documents(
            documents,
            ids=("uscis-i9", "arxiv-adam", "uscis-i9"),
        )

        self.assertEqual(["arxiv-adam", "uscis-i9"], [document.id for document in selected])

    def test_selection_supports_category_and_combines_id_and_category_filters(self) -> None:
        _, documents = remote_corpus.load_manifest(remote_corpus.DEFAULT_MANIFEST)

        forms = remote_corpus.select_documents(documents, categories=("forms",))
        selected_form = remote_corpus.select_documents(
            documents,
            ids=("arxiv-adam", "uscis-i9"),
            categories=("forms", "government-form"),
        )

        self.assertEqual(
            ["irs-w9", "uscis-i9", "fbi-fd258", "opm-of306"],
            [document.id for document in forms],
        )
        self.assertEqual(["uscis-i9"], [document.id for document in selected_form])

    def test_selection_rejects_unknown_empty_and_non_overlapping_filters(self) -> None:
        _, documents = remote_corpus.load_manifest(remote_corpus.DEFAULT_MANIFEST)

        with self.assertRaisesRegex(ValueError, r"Unknown --id value\(s\): missing"):
            remote_corpus.select_documents(documents, ids=("missing",))
        with self.assertRaisesRegex(ValueError, r"Unknown --category value\(s\): missing"):
            remote_corpus.select_documents(documents, categories=("missing",))
        with self.assertRaisesRegex(ValueError, "--id selection values must not be empty"):
            remote_corpus.select_documents(documents, ids=("",))
        with self.assertRaisesRegex(ValueError, "selection matched no documents"):
            remote_corpus.select_documents(
                documents,
                ids=("arxiv-adam",),
                categories=("forms",),
            )

    def test_parse_args_collects_repeatable_selectors(self) -> None:
        args = remote_corpus.parse_args(
            ["--id", "arxiv-adam", "--id", "uscis-i9", "--category", "forms"]
        )

        self.assertEqual(["arxiv-adam", "uscis-i9"], args.ids)
        self.assertEqual(["forms"], args.categories)

    def test_manifest_rejects_non_https_and_duplicate_ids(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            manifest = Path(temp_dir) / "manifest.json"
            entry = self._entry("sample")
            entry["pdfUrl"] = "http://example.test/sample.pdf"
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "HTTPS"):
                remote_corpus.load_manifest(manifest)

            entry["pdfUrl"] = "https://example.test/sample.pdf"
            self._write_manifest(manifest, [entry, dict(entry)])
            with self.assertRaisesRegex(ValueError, "duplicate"):
                remote_corpus.load_manifest(manifest)

    def test_manifest_rejects_invalid_hash_and_expectations(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            manifest = Path(temp_dir) / "manifest.json"
            entry = self._entry("sample")
            entry["sha256"] = "not-a-hash"
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "sha256"):
                remote_corpus.load_manifest(manifest)

            entry["sha256"] = "0" * 64
            entry["expectations"]["requiredText"] = []
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "requiredText"):
                remote_corpus.load_manifest(manifest)

            entry["expectations"]["requiredText"] = ["sample"]
            entry["expectations"]["minFormControls"] = -1
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "minFormControls"):
                remote_corpus.load_manifest(manifest)

            entry["expectations"]["minFormControls"] = 0
            entry["expectations"]["minImagePlacementsByPage"] = {"0": 1}
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "positive page numbers"):
                remote_corpus.load_manifest(manifest)

            entry["expectations"]["minImagePlacementsByPage"] = {"1": 0}
            entry["expectations"]["semanticMixedRegionCountsByPage"] = {"2": 0}
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "positive integers"):
                remote_corpus.load_manifest(manifest)

            entry["expectations"]["semanticMixedRegionCountsByPage"] = {"2": 1}
            entry["expectations"]["semanticRuledGridColumnCountsByPage"] = {"2": 1}
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "at least two"):
                remote_corpus.load_manifest(manifest)

            entry["expectations"]["semanticRuledGridColumnCountsByPage"] = {"2": 3}
            entry["expectations"]["semanticRuledGridSourceBorderCountsByPage"] = {"2": 0}
            self._write_manifest(manifest, [entry])
            with self.assertRaisesRegex(ValueError, "positive integers"):
                remote_corpus.load_manifest(manifest)

    def test_fetch_document_retries_verifies_hash_and_installs_atomically(self) -> None:
        content = b"pinned-pdf-content"
        expected_hash = hashlib.sha256(content).hexdigest()
        document = self._document(expected_hash)
        attempts = 0

        def open_url(request, *, timeout):
            nonlocal attempts
            attempts += 1
            self.assertEqual("https://example.test/sample.pdf", request.full_url)
            self.assertEqual(7, timeout)
            if attempts == 1:
                raise OSError("transient")
            return Response(content)

        with tempfile.TemporaryDirectory() as temp_dir:
            cache_dir = Path(temp_dir)
            target = remote_corpus.fetch_document(
                document,
                cache_dir,
                retries=2,
                timeout_seconds=7,
                open_url=open_url,
                sleep=lambda _: None,
            )

            self.assertEqual(content, target.read_bytes())
            self.assertEqual(2, attempts)
            self.assertEqual([], list(cache_dir.glob("*.tmp")))

            cached = remote_corpus.fetch_document(
                document,
                cache_dir,
                open_url=lambda *args, **kwargs: self.fail("verified cache should not be downloaded"),
            )
            self.assertEqual(target, cached)

    def test_fetch_document_rejects_hash_mismatch_without_installing_pdf(self) -> None:
        document = self._document("0" * 64)
        with tempfile.TemporaryDirectory() as temp_dir:
            cache_dir = Path(temp_dir)
            with self.assertRaisesRegex(RuntimeError, "SHA-256 mismatch"):
                remote_corpus.fetch_document(
                    document,
                    cache_dir,
                    retries=2,
                    open_url=lambda *args, **kwargs: Response(b"wrong"),
                    sleep=lambda _: None,
                )

            self.assertFalse((cache_dir / "sample.pdf").exists())
            self.assertEqual([], list(cache_dir.iterdir()))

    def test_fetch_document_rejects_redirect_to_non_https_url(self) -> None:
        content = b"pinned-pdf-content"
        document = self._document(hashlib.sha256(content).hexdigest())
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaisesRegex(RuntimeError, "HTTPS"):
                remote_corpus.fetch_document(
                    document,
                    Path(temp_dir),
                    retries=1,
                    open_url=lambda *args, **kwargs: Response(content, "http://example.test/sample.pdf"),
                )

    def test_materialize_review_manifest_uses_relative_cached_pdf_and_expectations(self) -> None:
        document = self._document("0" * 64)
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            pdf = root / "cache/sample.pdf"
            pdf.parent.mkdir()
            pdf.write_bytes(b"pdf")
            output = root / "generated/review-manifest.json"

            remote_corpus.materialize_review_manifest("Remote corpus.", [document], {document.id: pdf}, output)

            data = json.loads(output.read_text(encoding="utf-8"))
            example = data["examples"][0]
            self.assertEqual("../cache/sample.pdf", example["sourcePdf"])
            self.assertEqual(1, example["expectations"]["pageCount"])
            self.assertIn(document.source_page, example["notes"])

    def test_materialize_review_manifest_combines_local_and_remote_examples(self) -> None:
        document = self._document("0" * 64)
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            local_pdf = root / "local/local.pdf"
            local_pdf.parent.mkdir()
            local_pdf.write_bytes(b"local")
            base_manifest = root / "manifests/base.json"
            base_manifest.parent.mkdir()
            base_manifest.write_text(
                json.dumps(
                    {
                        "schema": 1,
                        "description": "Local examples.",
                        "examples": [
                            {
                                "id": "local-sample",
                                "title": "Local sample",
                                "sourcePdf": "../local/local.pdf",
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            remote_pdf = root / "cache/sample.pdf"
            remote_pdf.parent.mkdir()
            remote_pdf.write_bytes(b"remote")
            output = root / "generated/review-manifest.json"

            remote_corpus.materialize_review_manifest(
                "Remote corpus.",
                [document],
                {document.id: remote_pdf},
                output,
                base_manifest,
            )

            data = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(["local-sample", "sample"], [item["id"] for item in data["examples"]])
            self.assertEqual("../local/local.pdf", data["examples"][0]["sourcePdf"])
            self.assertEqual("../cache/sample.pdf", data["examples"][1]["sourcePdf"])
            self.assertIn("Local examples.", data["description"])
            self.assertIn("Remote corpus.", data["description"])

    @staticmethod
    def _entry(document_id: str) -> dict:
        return {
            "id": document_id,
            "title": "Sample",
            "sourcePage": "https://example.test/sample",
            "pdfUrl": "https://example.test/sample.pdf",
            "sha256": "0" * 64,
            "categories": ["text-heavy"],
            "qualityPages": 1,
            "notes": "Sample document.",
            "expectations": {"pageCount": 1, "requiredText": ["sample"], "minTextRuns": 1},
        }

    @classmethod
    def _document(cls, sha256: str):
        entry = cls._entry("sample")
        entry["sha256"] = sha256
        with tempfile.TemporaryDirectory() as temp_dir:
            manifest = Path(temp_dir) / "manifest.json"
            cls._write_manifest(manifest, [entry])
            return remote_corpus.load_manifest(manifest)[1][0]

    @staticmethod
    def _write_manifest(path: Path, documents: list[dict]) -> None:
        path.write_text(json.dumps({"schema": 1, "description": "Academic remote corpus.", "documents": documents}))


if __name__ == "__main__":
    unittest.main()
