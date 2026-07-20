#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any, BinaryIO, Callable, Iterable
from urllib.parse import urlparse


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MANIFEST = ROOT / "tools/conversion_quality/remote-corpus-manifest.json"
DEFAULT_BASE_REVIEW_MANIFEST = ROOT / "tools/conversion_quality/html-review-manifest.json"
DEFAULT_CACHE_DIR = ROOT / "artifacts/cache/conversion-quality/remote-pdfs"
DEFAULT_REVIEW_MANIFEST = ROOT / "artifacts/cache/conversion-quality/remote-html-review-manifest.json"
DEFAULT_OUT_DIR = ROOT / "artifacts/html-examples"
GENERATOR_PROJECT = ROOT / "tools/conversion_quality/PdfBox.Net.ConversionQuality/PdfBox.Net.ConversionQuality.csproj"
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
ID_RE = re.compile(r"^[a-z0-9][a-z0-9-]*$")
MINIMUM_EXPECTATIONS = (
    "minTextRuns",
    "minImagePlacements",
    "minVectorPaths",
    "minLinks",
    "minFormControls",
)


@dataclass(frozen=True)
class RemoteDocument:
    id: str
    title: str
    source_page: str
    pdf_url: str
    sha256: str
    categories: tuple[str, ...]
    quality_pages: int
    notes: str
    expectations: dict[str, Any]


def load_manifest(path: Path) -> tuple[str, list[RemoteDocument]]:
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)

    if not isinstance(data, dict) or data.get("schema") != 1:
        raise ValueError(f"{path} must be a schema 1 JSON object")
    description = data.get("description")
    if not isinstance(description, str) or not description.strip():
        raise ValueError(f"{path} is missing description")
    entries = data.get("documents")
    if not isinstance(entries, list) or not entries:
        raise ValueError(f"{path} must contain a non-empty documents array")

    documents: list[RemoteDocument] = []
    ids: set[str] = set()
    for index, entry in enumerate(entries):
        if not isinstance(entry, dict):
            raise ValueError(f"{path} document {index + 1} must be an object")
        document = _parse_document(entry, path, index)
        if document.id in ids:
            raise ValueError(f"{path} contains duplicate document id {document.id!r}")
        ids.add(document.id)
        documents.append(document)

    return description, documents


def select_documents(
    documents: list[RemoteDocument],
    *,
    ids: Iterable[str] = (),
    categories: Iterable[str] = (),
) -> list[RemoteDocument]:
    requested_ids = _selection_values(ids, "--id")
    requested_categories = _selection_values(categories, "--category")
    if not requested_ids and not requested_categories:
        return list(documents)

    available_ids = {document.id for document in documents}
    available_categories = {category for document in documents for category in document.categories}
    unknown_ids = sorted(requested_ids - available_ids)
    if unknown_ids:
        raise ValueError(
            f"Unknown --id value(s): {', '.join(unknown_ids)}. "
            f"Available values: {', '.join(sorted(available_ids))}"
        )
    unknown_categories = sorted(requested_categories - available_categories)
    if unknown_categories:
        raise ValueError(
            f"Unknown --category value(s): {', '.join(unknown_categories)}. "
            f"Available values: {', '.join(sorted(available_categories))}"
        )

    selected = [
        document
        for document in documents
        if (not requested_ids or document.id in requested_ids)
        and (
            not requested_categories
            or any(category in requested_categories for category in document.categories)
        )
    ]
    if not selected:
        raise ValueError(
            "Remote corpus selection matched no documents; --id and --category filters must overlap"
        )
    return selected


def _selection_values(values: Iterable[str], option: str) -> set[str]:
    normalized: set[str] = set()
    for value in values:
        value = value.strip()
        if not value:
            raise ValueError(f"{option} selection values must not be empty")
        normalized.add(value)
    return normalized


def _parse_document(entry: dict[str, Any], path: Path, index: int) -> RemoteDocument:
    prefix = f"{path} document {index + 1}"

    def required_string(name: str) -> str:
        value = entry.get(name)
        if not isinstance(value, str) or not value.strip():
            raise ValueError(f"{prefix} is missing {name}")
        return value.strip()

    document_id = required_string("id")
    if not ID_RE.fullmatch(document_id):
        raise ValueError(f"{prefix} id must contain only lowercase letters, digits, and hyphens")

    source_page = required_string("sourcePage")
    pdf_url = required_string("pdfUrl")
    _validate_https_url(source_page, f"{prefix} sourcePage")
    _validate_https_url(pdf_url, f"{prefix} pdfUrl")

    sha256 = required_string("sha256").lower()
    if not SHA256_RE.fullmatch(sha256):
        raise ValueError(f"{prefix} sha256 must be 64 lowercase hexadecimal characters")

    categories = entry.get("categories")
    if not isinstance(categories, list) or not categories or not all(
        isinstance(value, str) and value.strip() for value in categories
    ):
        raise ValueError(f"{prefix} categories must be a non-empty string array")

    quality_pages = entry.get("qualityPages", 2)
    if not isinstance(quality_pages, int) or quality_pages < 1:
        raise ValueError(f"{prefix} qualityPages must be a positive integer")
    expectations = entry.get("expectations")
    if not isinstance(expectations, dict):
        raise ValueError(f"{prefix} expectations must be an object")
    _validate_expectations(expectations, prefix)

    return RemoteDocument(
        id=document_id,
        title=required_string("title"),
        source_page=source_page,
        pdf_url=pdf_url,
        sha256=sha256,
        categories=tuple(value.strip() for value in categories),
        quality_pages=quality_pages,
        notes=required_string("notes"),
        expectations=expectations,
    )


def _validate_expectations(expectations: dict[str, Any], prefix: str) -> None:
    page_count = expectations.get("pageCount")
    if not isinstance(page_count, int) or isinstance(page_count, bool) or page_count < 1:
        raise ValueError(f"{prefix} expectations.pageCount must be a positive integer")

    required_text = expectations.get("requiredText")
    if not isinstance(required_text, list) or not required_text or not all(
        isinstance(value, str) and value.strip() for value in required_text
    ):
        raise ValueError(f"{prefix} expectations.requiredText must be a non-empty string array")

    min_text_runs = expectations.get("minTextRuns")
    if not isinstance(min_text_runs, int) or isinstance(min_text_runs, bool) or min_text_runs < 1:
        raise ValueError(f"{prefix} expectations.minTextRuns must be a positive integer")

    for name in MINIMUM_EXPECTATIONS[1:]:
        value = expectations.get(name)
        if value is not None and (not isinstance(value, int) or isinstance(value, bool) or value < 0):
            raise ValueError(f"{prefix} expectations.{name} must be a non-negative integer")

    min_images_by_page = expectations.get("minImagePlacementsByPage")
    if min_images_by_page is not None:
        if not isinstance(min_images_by_page, dict) or not min_images_by_page:
            raise ValueError(
                f"{prefix} expectations.minImagePlacementsByPage must be a non-empty object"
            )
        for page_number, minimum in min_images_by_page.items():
            if not isinstance(page_number, str) or not page_number.isdigit() or int(page_number) < 1:
                raise ValueError(
                    f"{prefix} expectations.minImagePlacementsByPage keys must be positive page numbers"
                )
            if not isinstance(minimum, int) or isinstance(minimum, bool) or minimum < 0:
                raise ValueError(
                    f"{prefix} expectations.minImagePlacementsByPage values must be non-negative integers"
                )

    for expectation_name in (
        "semanticOrderedListItemCountsByPage",
        "semanticUnorderedListItemCountsByPage",
    ):
        semantic_lists_by_page = expectations.get(expectation_name)
        if semantic_lists_by_page is None:
            continue
        if not isinstance(semantic_lists_by_page, dict) or not semantic_lists_by_page:
            raise ValueError(
                f"{prefix} expectations.{expectation_name} must be a non-empty object"
            )
        for page_number, item_counts in semantic_lists_by_page.items():
            if (
                not isinstance(page_number, str)
                or not page_number.isdigit()
                or int(page_number) < 1
            ):
                raise ValueError(
                    f"{prefix} expectations.{expectation_name} keys must be positive page numbers"
                )
            if (
                not isinstance(item_counts, list)
                or not item_counts
                or any(
                    not isinstance(item_count, int)
                    or isinstance(item_count, bool)
                    or item_count < 1
                    for item_count in item_counts
                )
            ):
                raise ValueError(
                    f"{prefix} expectations.{expectation_name} values must be non-empty positive integer arrays"
                )

    semantic_mixed_regions_by_page = expectations.get("semanticMixedRegionCountsByPage")
    if semantic_mixed_regions_by_page is not None:
        if not isinstance(semantic_mixed_regions_by_page, dict) or not semantic_mixed_regions_by_page:
            raise ValueError(
                f"{prefix} expectations.semanticMixedRegionCountsByPage must be a non-empty object"
            )
        for page_number, count in semantic_mixed_regions_by_page.items():
            if not isinstance(page_number, str) or not page_number.isdigit() or int(page_number) < 1:
                raise ValueError(
                    f"{prefix} expectations.semanticMixedRegionCountsByPage keys must be positive page numbers"
                )
            if not isinstance(count, int) or isinstance(count, bool) or count < 1:
                raise ValueError(
                    f"{prefix} expectations.semanticMixedRegionCountsByPage values must be positive integers"
                )

    semantic_ruled_grid_columns_by_page = expectations.get(
        "semanticRuledGridColumnCountsByPage"
    )
    if semantic_ruled_grid_columns_by_page is not None:
        if (
            not isinstance(semantic_ruled_grid_columns_by_page, dict)
            or not semantic_ruled_grid_columns_by_page
        ):
            raise ValueError(
                f"{prefix} expectations.semanticRuledGridColumnCountsByPage must be a non-empty object"
            )
        for page_number, count in semantic_ruled_grid_columns_by_page.items():
            if not isinstance(page_number, str) or not page_number.isdigit() or int(page_number) < 1:
                raise ValueError(
                    f"{prefix} expectations.semanticRuledGridColumnCountsByPage keys must be positive page numbers"
                )
            if not isinstance(count, int) or isinstance(count, bool) or count < 2:
                raise ValueError(
                    f"{prefix} expectations.semanticRuledGridColumnCountsByPage values must be integers of at least two"
                )

    semantic_ruled_grid_borders_by_page = expectations.get(
        "semanticRuledGridSourceBorderCountsByPage"
    )
    if semantic_ruled_grid_borders_by_page is not None:
        if (
            not isinstance(semantic_ruled_grid_borders_by_page, dict)
            or not semantic_ruled_grid_borders_by_page
        ):
            raise ValueError(
                f"{prefix} expectations.semanticRuledGridSourceBorderCountsByPage must be a non-empty object"
            )
        for page_number, count in semantic_ruled_grid_borders_by_page.items():
            if not isinstance(page_number, str) or not page_number.isdigit() or int(page_number) < 1:
                raise ValueError(
                    f"{prefix} expectations.semanticRuledGridSourceBorderCountsByPage keys must be positive page numbers"
                )
            if not isinstance(count, int) or isinstance(count, bool) or count < 1:
                raise ValueError(
                    f"{prefix} expectations.semanticRuledGridSourceBorderCountsByPage values must be positive integers"
                )


def _validate_https_url(value: str, label: str) -> None:
    parsed = urlparse(value)
    if parsed.scheme.casefold() != "https" or not parsed.netloc or parsed.username or parsed.password:
        raise ValueError(f"{label} must be an HTTPS URL without credentials")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def fetch_document(
    document: RemoteDocument,
    cache_dir: Path,
    *,
    retries: int = 3,
    timeout_seconds: float = 30,
    open_url: Callable[..., BinaryIO] = urllib.request.urlopen,
    sleep: Callable[[float], None] = time.sleep,
) -> Path:
    if retries < 1:
        raise ValueError("retries must be at least 1")
    if timeout_seconds <= 0:
        raise ValueError("timeout_seconds must be positive")

    cache_dir.mkdir(parents=True, exist_ok=True)
    target = cache_dir / f"{document.id}.pdf"
    if target.exists() and sha256_file(target) == document.sha256:
        return target
    target.unlink(missing_ok=True)

    last_error: Exception | None = None
    for attempt in range(1, retries + 1):
        temporary = cache_dir / f".{document.id}.{os.getpid()}.{attempt}.tmp"
        try:
            request = urllib.request.Request(
                document.pdf_url,
                headers={"User-Agent": "pdfbox-net-conversion-quality/1.0"},
            )
            digest = hashlib.sha256()
            with open_url(request, timeout=timeout_seconds) as response, temporary.open("wb") as output:
                if hasattr(response, "geturl"):
                    _validate_https_url(response.geturl(), f"{document.id} final download URL")
                while chunk := response.read(1024 * 1024):
                    digest.update(chunk)
                    output.write(chunk)
                output.flush()
                os.fsync(output.fileno())

            actual_hash = digest.hexdigest()
            if actual_hash != document.sha256:
                raise ValueError(
                    f"SHA-256 mismatch for {document.id}: expected {document.sha256}, got {actual_hash}"
                )
            os.replace(temporary, target)
            return target
        except Exception as error:
            last_error = error
            temporary.unlink(missing_ok=True)
            if attempt < retries:
                sleep(min(2 ** (attempt - 1), 4))

    raise RuntimeError(f"Failed to download {document.id} after {retries} attempts: {last_error}") from last_error


def materialize_review_manifest(
    description: str,
    documents: list[RemoteDocument],
    pdf_paths: dict[str, Path],
    output_path: Path,
    base_manifest_path: Path | None = None,
) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_parent = output_path.parent.resolve()
    examples: list[dict[str, Any]] = []
    descriptions = [description]
    if base_manifest_path is not None:
        with base_manifest_path.open("r", encoding="utf-8") as handle:
            base_manifest = json.load(handle)
        if not isinstance(base_manifest, dict) or base_manifest.get("schema") != 1:
            raise ValueError(f"{base_manifest_path} must be a schema 1 JSON object")
        base_examples = base_manifest.get("examples")
        if not isinstance(base_examples, list):
            raise ValueError(f"{base_manifest_path} must contain an examples array")
        base_description = base_manifest.get("description")
        if isinstance(base_description, str) and base_description.strip():
            descriptions.insert(0, base_description.strip())
        for entry in base_examples:
            if not isinstance(entry, dict):
                raise ValueError(f"{base_manifest_path} examples must be objects")
            example = dict(entry)
            source_pdf = example.get("sourcePdf")
            if not isinstance(source_pdf, str) or not source_pdf.strip():
                raise ValueError(f"{base_manifest_path} example is missing sourcePdf")
            resolved_pdf = (base_manifest_path.parent / source_pdf).resolve()
            example["sourcePdf"] = Path(os.path.relpath(resolved_pdf, output_parent)).as_posix()
            examples.append(example)

    ids = {example.get("id") for example in examples}
    for document in documents:
        if document.id in ids:
            raise ValueError(f"Duplicate HTML review example id {document.id!r}")
        ids.add(document.id)
        source_pdf = os.path.relpath(pdf_paths[document.id].resolve(), output_parent)
        examples.append(
            {
                "id": document.id,
                "title": document.title,
                "sourcePdf": Path(source_pdf).as_posix(),
                "qualityPages": document.quality_pages,
                "notes": (
                    f"{document.notes} Categories: {', '.join(document.categories)}. "
                    f"Canonical source: {document.source_page}"
                ),
                "expectations": document.expectations,
            }
        )

    temporary = output_path.with_name(f".{output_path.name}.{os.getpid()}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(
                {
                    "schema": 1,
                    "description": (
                        " ".join(descriptions) +
                        " Generated from checked-in and pinned remote review manifests."
                    ),
                    "examples": examples,
                },
                handle,
                indent=2,
            )
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, output_path)
    finally:
        temporary.unlink(missing_ok=True)


def run_generator(review_manifest: Path, out_dir: Path, *, no_build: bool = True) -> None:
    command = [
        "dotnet",
        "run",
        "--project",
        str(GENERATOR_PROJECT),
        "--configuration",
        "Release",
    ]
    if no_build:
        command.append("--no-build")
    command.extend(["--", "--manifest", str(review_manifest), "--out-dir", str(out_dir)])
    subprocess.run(command, cwd=ROOT, check=True)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Download the pinned remote PDF corpus and generate HTML review artifacts."
    )
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--base-review-manifest", type=Path, default=DEFAULT_BASE_REVIEW_MANIFEST)
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE_DIR)
    parser.add_argument("--review-manifest-out", type=Path, default=DEFAULT_REVIEW_MANIFEST)
    parser.add_argument("--out-dir", type=Path, default=DEFAULT_OUT_DIR)
    parser.add_argument(
        "--id",
        dest="ids",
        action="append",
        default=[],
        metavar="DOCUMENT_ID",
        help="Select a document by manifest id. Repeat to select any of multiple ids.",
    )
    parser.add_argument(
        "--category",
        dest="categories",
        action="append",
        default=[],
        metavar="CATEGORY",
        help="Select documents in a manifest category. Repeat to select any of multiple categories.",
    )
    parser.add_argument("--retries", type=int, default=3)
    parser.add_argument("--timeout-seconds", type=float, default=30)
    parser.add_argument("--build", action="store_true", help="Allow dotnet run to build the generator project.")
    parser.add_argument("--skip-generator", action="store_true", help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        description, all_documents = load_manifest(args.manifest.resolve())
        documents = select_documents(all_documents, ids=args.ids, categories=args.categories)
        print(f"Selected {len(documents)} of {len(all_documents)} remote corpus documents.")
        pdf_paths: dict[str, Path] = {}
        for document in documents:
            print(f"Fetching {document.id}...", flush=True)
            pdf_paths[document.id] = fetch_document(
                document,
                args.cache_dir.resolve(),
                retries=args.retries,
                timeout_seconds=args.timeout_seconds,
            )
        materialize_review_manifest(
            description,
            documents,
            pdf_paths,
            args.review_manifest_out.resolve(),
            args.base_review_manifest.resolve() if args.base_review_manifest else None,
        )
        if not args.skip_generator:
            run_generator(args.review_manifest_out.resolve(), args.out_dir.resolve(), no_build=not args.build)
        print(f"Remote corpus review manifest: {args.review_manifest_out.resolve()}")
        if not args.skip_generator:
            print(f"Remote corpus HTML examples: {args.out_dir.resolve()}")
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"remote corpus failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
