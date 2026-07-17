#!/usr/bin/env python3
"""Publish the adaptive browser WASM app into the shared GitHub Pages tree."""

from __future__ import annotations

import argparse
import re
import shutil
from pathlib import Path
from urllib.parse import urlsplit

BASE_HREF_PATTERN = re.compile(
    r"(<base\b[^>]*\bhref\s*=\s*)([\"'])(.*?)(\2)",
    re.IGNORECASE,
)
WASM_BASE_PATH_PLACEHOLDER = "__WASM_BASE_PATH__"


def normalize_base_path(value: str) -> str:
    parsed = urlsplit(value)
    if parsed.scheme or parsed.netloc or parsed.query or parsed.fragment:
        raise ValueError("WASM base path must be an absolute URL path")
    if not parsed.path.startswith("/") or not parsed.path.endswith("/"):
        raise ValueError("WASM base path must start and end with a slash")
    if any(part in {".", ".."} for part in parsed.path.split("/")):
        raise ValueError("WASM base path must not contain relative segments")
    return parsed.path


def rewrite_base_href(index_path: Path, base_path: str) -> None:
    document = index_path.read_text(encoding="utf-8-sig")
    matches = list(BASE_HREF_PATTERN.finditer(document))
    if len(matches) != 1:
        raise ValueError(f"expected exactly one base href in {index_path}")
    rewritten = BASE_HREF_PATTERN.sub(
        lambda match: f'{match.group(1)}"{base_path}"',
        document,
        count=1,
    )
    index_path.write_text(rewritten, encoding="utf-8")

    # The generated sidecars still contain the pre-deployment base URI. The
    # small HTML shell can be served uncompressed while framework assets retain
    # their generated Brotli and Gzip variants.
    for suffix in (".br", ".gz"):
        index_path.with_name(index_path.name + suffix).unlink(missing_ok=True)


def publish_site(
    application: Path,
    site: Path,
    landing_page: Path,
    not_found_page: Path,
    base_path: str,
) -> None:
    base_path = normalize_base_path(base_path)
    required_paths = (
        application / "index.html",
        application / "_framework",
        application / "samples" / "hello.pdf",
    )
    for required_path in required_paths:
        if not required_path.exists():
            raise FileNotFoundError(f"published WASM app is missing {required_path.relative_to(application)}")
    for template in (landing_page, not_found_page):
        if not template.is_file():
            raise FileNotFoundError(f"Pages template does not exist: {template}")

    destination = site / "wasm"
    shutil.rmtree(destination, ignore_errors=True)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(application, destination)
    rewrite_base_href(destination / "index.html", base_path)

    shutil.copy2(landing_page, site / "index.html")
    not_found = not_found_page.read_text(encoding="utf-8")
    if not_found.count(WASM_BASE_PATH_PLACEHOLDER) != 1:
        raise ValueError("Pages 404 template must contain exactly one WASM base-path placeholder")
    (site / "404.html").write_text(
        not_found.replace(WASM_BASE_PATH_PLACEHOLDER, base_path),
        encoding="utf-8",
    )
    (site / ".nojekyll").touch()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--application", required=True, type=Path)
    parser.add_argument("--site", required=True, type=Path)
    parser.add_argument("--landing-page", required=True, type=Path)
    parser.add_argument("--not-found-page", required=True, type=Path)
    parser.add_argument("--base-path", required=True)
    args = parser.parse_args()
    publish_site(
        args.application.resolve(),
        args.site.resolve(),
        args.landing_page.resolve(),
        args.not_found_page.resolve(),
        args.base_path,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
