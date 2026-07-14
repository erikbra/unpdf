#!/usr/bin/env python3
"""Merge one generated unpdf APT suite into a persistent Pages tree."""

from __future__ import annotations

import argparse
import html
import re
import shutil
from pathlib import Path

SUPPORTED_SUITES = {"preview", "stable"}


def normalize_fingerprint(value: str) -> str:
    fingerprint = re.sub(r"\s+", "", value).upper()
    if not re.fullmatch(r"[0-9A-F]{40}", fingerprint):
        raise ValueError("APT signing fingerprint must contain exactly 40 hexadecimal characters")
    return fingerprint


def sources_text(base_url: str, suite: str) -> str:
    return f"""Types: deb
URIs: {base_url.rstrip('/')}
Suites: {suite}
Components: main
Architectures: amd64 arm64
Signed-By: /etc/apt/keyrings/unpdf.gpg
"""


def landing_page(base_url: str, suites: list[str], fingerprint: str) -> str:
    suite_sections = []
    for suite in suites:
        escaped_suite = html.escape(suite)
        suite_sections.append(f"""
        <section>
          <h2>{escaped_suite.title()} channel</h2>
          <pre><code>curl -fsSL {html.escape(base_url)}/unpdf-archive-keyring.gpg | sudo tee /etc/apt/keyrings/unpdf.gpg &gt;/dev/null
curl -fsSL {html.escape(base_url)}/unpdf-{escaped_suite}.sources | sudo tee /etc/apt/sources.list.d/unpdf.sources &gt;/dev/null
sudo apt update
sudo apt install unpdf</code></pre>
        </section>""")
    grouped_fingerprint = " ".join(fingerprint[index:index + 4] for index in range(0, len(fingerprint), 4))
    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>unpdf APT repository</title>
  <style>
    body {{ max-width: 56rem; margin: 3rem auto; padding: 0 1rem; font: 1rem/1.5 system-ui, sans-serif; color: #172033; }}
    h1, h2 {{ line-height: 1.2; }}
    pre {{ overflow-x: auto; padding: 1rem; background: #f3f5f7; border: 1px solid #d8dde3; }}
    code {{ font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }}
  </style>
</head>
<body>
  <main>
    <h1>unpdf APT repository</h1>
    <p>Signed packages for Ubuntu and Debian on amd64 and arm64.</p>
    {''.join(suite_sections)}
    <h2>Signing key</h2>
    <p>Fingerprint: <code>{grouped_fingerprint}</code></p>
    <p><a href="unpdf-archive-key.asc">Armored public key</a> · <a href="https://github.com/erikbra/pdfbox-net">source</a></p>
  </main>
</body>
</html>
"""


def merge_repository(
    repository: Path,
    site: Path,
    suite: str,
    base_url: str,
    fingerprint: str,
) -> None:
    if suite not in SUPPORTED_SUITES:
        raise ValueError(f"unsupported APT suite: {suite}")
    fingerprint = normalize_fingerprint(fingerprint)
    source_suite = repository / "dists" / suite
    if not source_suite.is_dir():
        raise FileNotFoundError(f"generated repository is missing dists/{suite}")
    for filename in ("unpdf-archive-keyring.gpg", "unpdf-archive-key.asc"):
        if not (repository / filename).is_file():
            raise FileNotFoundError(f"generated repository is missing {filename}")

    apt_root = site / "apt"
    apt_root.mkdir(parents=True, exist_ok=True)
    shutil.copytree(repository / "pool", apt_root / "pool", dirs_exist_ok=True)
    destination_suite = apt_root / "dists" / suite
    shutil.rmtree(destination_suite, ignore_errors=True)
    destination_suite.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source_suite, destination_suite)
    shutil.copy2(repository / "unpdf-archive-keyring.gpg", apt_root / "unpdf-archive-keyring.gpg")
    shutil.copy2(repository / "unpdf-archive-key.asc", apt_root / "unpdf-archive-key.asc")
    (apt_root / "SIGNING-KEY-FINGERPRINT").write_text(f"{fingerprint}\n", encoding="ascii")
    (apt_root / f"unpdf-{suite}.sources").write_text(sources_text(base_url, suite), encoding="utf-8")
    suites = sorted(path.name for path in (apt_root / "dists").iterdir() if path.is_dir())
    (apt_root / "index.html").write_text(landing_page(base_url, suites, fingerprint), encoding="utf-8")
    (site / ".nojekyll").touch()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True, type=Path)
    parser.add_argument("--site", required=True, type=Path)
    parser.add_argument("--suite", required=True, choices=sorted(SUPPORTED_SUITES))
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--fingerprint", required=True)
    args = parser.parse_args()
    merge_repository(
        args.repository.resolve(),
        args.site.resolve(),
        args.suite,
        args.base_url,
        args.fingerprint,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
