#!/usr/bin/env python3
"""Generate the unpdf Homebrew formula from a release manifest."""

from __future__ import annotations

import argparse
import json
import textwrap
from pathlib import Path
from urllib.request import urlopen

FIXTURE_BASE64 = (
    "JVBERi0xLjQKJeLjz9MKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoK"
    "PDwgL1R5cGUgL1BhZ2VzIC9Db3VudCAxIC9LaWRzIFszIDAgUl0gPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2Ug"
    "L1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCAzMDAgMzAwXSAvQ29udGVudHMgNCAwIFIgPj4KZW5kb2JqCjQgMCBvYmoK"
    "PDwgL0xlbmd0aCAzNyA+PgpzdHJlYW0KQlQKL0YxIDEyIFRmCjcyIDcyMCBUZAooSGVsbG8pIFRqCkVUCmVuZHN0cmVhbQpl"
    "bmRvYmoKNSAwIG9iago8PCAvVGl0bGUgKENsYXNzaWMgRml4dHVyZSkgL0F1dGhvciAocGRmYm94LW5ldCkgPj4KZW5kb2Jq"
    "CnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAw"
    "MDAwMDAxMjEgMDAwMDAgbiAKMDAwMDAwMDIwOCAwMDAwMCBuIAowMDAwMDAwMjk0IDAwMDAwIG4gCnRyYWlsZXIKPDwgL1Np"
    "emUgNiAvUm9vdCAxIDAgUiAvSW5mbyA1IDAgUiA+PgpzdGFydHhyZWYKMzYxCiUlRU9GCg=="
)


def load_manifest(source: str) -> dict:
    if source.startswith(("https://", "http://")):
        with urlopen(source) as response:
            return json.load(response)
    return json.loads(Path(source).read_text(encoding="utf-8"))


def generate_formula(manifest: dict) -> str:
    artifacts = {artifact["rid"]: artifact for artifact in manifest["artifacts"]}
    required = {"osx-arm64", "osx-x64", "linux-arm64", "linux-x64"}
    missing = required - artifacts.keys()
    if missing:
        raise ValueError(f"manifest is missing Homebrew RIDs: {sorted(missing)}")
    version = manifest["version"]
    fixture_lines = "\n".join(f'      "{line}",' for line in textwrap.wrap(FIXTURE_BASE64, 90))

    def resource(rid: str, indent: str = "      ") -> str:
        artifact = artifacts[rid]
        return f'{indent}url "{artifact["url"]}"\n{indent}sha256 "{artifact["sha256"]}"'

    return f'''# frozen_string_literal: true

# NativeAOT PDF-to-HTML command-line converter.
class Unpdf < Formula
  desc "Convert PDF documents to semantic HTML"
  homepage "https://github.com/erikbra/pdfbox-net"
  version "{version}"
  license "Apache-2.0"

  on_macos do
    on_arm do
{resource("osx-arm64")}
    end
    on_intel do
{resource("osx-x64")}
    end
  end

  on_linux do
    on_arm do
{resource("linux-arm64")}
    end
    on_intel do
{resource("linux-x64")}
    end
  end

  def install
    bin.install "unpdf"
    pkgshare.install "LICENSE.txt", "NOTICE.txt", "SIGNING.md", "VERSION", "artifact-manifest.json", "sbom.spdx.json"
  end

  test do
    require "base64"

    assert_match version.to_s, shell_output("#{{bin}}/unpdf --version")
    fixture = [
{fixture_lines}
    ].join
    (testpath/"fixture.pdf").binwrite(Base64.decode64(fixture))
    system bin/"unpdf", "fixture.pdf", "--output", "html", "--quiet"
    assert_match "Hello", (testpath/"html/index.html").read
  end
end
'''


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    formula = generate_formula(load_manifest(args.manifest))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(formula, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
