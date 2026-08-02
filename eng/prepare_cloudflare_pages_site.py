#!/usr/bin/env python3
"""Adapt the assembled GitHub Pages tree for Cloudflare Pages."""

from __future__ import annotations

import argparse
import re
import shutil
import sys
from pathlib import Path

SCRIPT_DIRECTORY = Path(__file__).resolve().parent
if str(SCRIPT_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIRECTORY))

from harden_wasm_site import (  # noqa: E402
    cloudflare_headers,
    inline_script_hashes,
    production_headers,
)
from publish_wasm_pages_site import (  # noqa: E402
    normalize_application_path,
    normalize_base_path,
    rewrite_base_href,
)

APP_BASE_PATTERN = re.compile(
    r"(\bconst\s+appBase\s*=\s*)([\"'])(.*?)(\2)(\s*;)",
    re.DOTALL,
)
ROOT_LINK_PATTERN = re.compile(
    r"(<a\b[^>]*\bhref\s*=\s*)([\"'])(.*?)(\2)([^>]*>\s*Return to unpdf\s*</a>)",
    re.IGNORECASE | re.DOTALL,
)
MAX_PAGES_FILES = 20_000
MAX_PAGES_FILE_SIZE = 25 * 1024 * 1024


def rewrite_not_found_base(not_found_path: Path, base_path: str) -> None:
    document = not_found_path.read_text(encoding="utf-8-sig")
    matches = list(APP_BASE_PATTERN.finditer(document))
    if len(matches) != 1:
        raise ValueError(f"expected exactly one appBase assignment in {not_found_path}")
    rewritten = APP_BASE_PATTERN.sub(
        lambda match: f'{match.group(1)}"{base_path}"{match.group(5)}',
        document,
        count=1,
    )
    root_links = list(ROOT_LINK_PATTERN.finditer(rewritten))
    if len(root_links) != 1:
        raise ValueError(f"expected exactly one root link in {not_found_path}")
    rewritten = ROOT_LINK_PATTERN.sub(
        lambda match: f'{match.group(1)}"/"{match.group(5)}',
        rewritten,
        count=1,
    )
    not_found_path.write_text(rewritten, encoding="utf-8")


def remove_legacy_review_outputs(site: Path) -> int:
    examples = site / "examples"
    if not examples.is_dir():
        return 0

    removed = 0
    for example in examples.iterdir():
        if not example.is_dir() or not (example / "semantic-continuous" / "index.html").is_file():
            continue
        fixed_index = example / "index.html"
        if fixed_index.exists():
            fixed_index.unlink()
            removed += 1
        for obsolete_directory in (example / "assets", example / "semantic"):
            if obsolete_directory.exists():
                shutil.rmtree(obsolete_directory)
                removed += 1

    overview = examples / "index.html"
    if overview.is_file():
        document = overview.read_text(encoding="utf-8")
        rewritten = document.replace(">continuous semantic HTML</a>", ">html</a>")
        if rewritten != document:
            overview.write_text(rewritten, encoding="utf-8")
    return removed


def validate_pages_limits(
    site: Path,
    max_files: int = MAX_PAGES_FILES,
    max_file_size: int = MAX_PAGES_FILE_SIZE,
) -> tuple[int, int]:
    file_count = 0
    largest_file_size = 0
    for path in site.rglob("*"):
        if not path.is_file():
            continue
        file_count += 1
        size = path.stat().st_size
        largest_file_size = max(largest_file_size, size)
        if size > max_file_size:
            raise ValueError(
                f"Cloudflare Pages file exceeds {max_file_size} bytes: "
                f"{path.relative_to(site)} ({size} bytes)"
            )
    if file_count > max_files:
        raise ValueError(
            f"Cloudflare Pages site contains {file_count} files; limit is {max_files}"
        )
    return file_count, largest_file_size


def prepare_site(site: Path, application_path: str, base_path: str) -> tuple[int, int, int]:
    application_path = normalize_application_path(application_path)
    base_path = normalize_base_path(base_path)
    if base_path.strip("/") != application_path:
        raise ValueError("Cloudflare base path must match the application path")

    application = site / application_path
    index_path = application / "index.html"
    not_found_path = site / "404.html"
    nested_headers = application / "_headers"
    for required_path in (site / "index.html", index_path, not_found_path, nested_headers):
        if not required_path.is_file():
            raise FileNotFoundError(f"assembled Pages site is missing {required_path.relative_to(site)}")

    removed_legacy_outputs = remove_legacy_review_outputs(site)
    rewrite_base_href(index_path, base_path)
    rewrite_not_found_base(not_found_path, base_path)

    script_hashes = sorted(
        set(
            inline_script_hashes(index_path.read_text(encoding="utf-8"))
            + inline_script_hashes(not_found_path.read_text(encoding="utf-8"))
        )
    )
    headers = production_headers(script_hashes)
    (site / "_headers").write_text(
        cloudflare_headers(headers, base_path),
        encoding="utf-8",
    )

    nested_headers.unlink()
    (application / "staticwebapp.config.json").unlink(missing_ok=True)
    file_count, largest_file_size = validate_pages_limits(site)
    return file_count, largest_file_size, removed_legacy_outputs


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--site", required=True, type=Path)
    parser.add_argument("--application-path", required=True)
    parser.add_argument("--base-path", required=True)
    args = parser.parse_args()

    site = args.site.resolve()
    file_count, largest_file_size, removed_legacy_outputs = prepare_site(
        site,
        args.application_path,
        args.base_path,
    )
    print(
        f"Prepared {file_count} Cloudflare Pages files; largest is "
        f"{largest_file_size} bytes; removed {removed_legacy_outputs} legacy review outputs."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
