#!/usr/bin/env python3
"""Harden and verify the static browser-WASM publish output."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
from pathlib import Path


SCRIPT_PATTERN = re.compile(
    r"<script\b(?P<attributes>[^>]*)>(?P<body>.*?)</script>",
    re.IGNORECASE | re.DOTALL,
)
CSP_META_PATTERN = re.compile(
    r"[ \t]*<meta\s+id=[\"']unpdf-csp[\"'][^>]*>\s*",
    re.IGNORECASE,
)
REFERRER_META_PATTERN = re.compile(
    r"[ \t]*<meta\s+id=[\"']unpdf-referrer[\"'][^>]*>\s*",
    re.IGNORECASE,
)
HEAD_PATTERN = re.compile(r"(<head\b[^>]*>)", re.IGNORECASE)


def inline_script_hashes(document: str) -> list[str]:
    hashes: list[str] = []
    for match in SCRIPT_PATTERN.finditer(document):
        attributes = match.group("attributes")
        if re.search(r"\bsrc\s*=", attributes, re.IGNORECASE):
            continue
        body = match.group("body")
        if not body:
            continue
        digest = hashlib.sha256(body.encode("utf-8")).digest()
        hashes.append(f"'sha256-{base64.b64encode(digest).decode('ascii')}'")
    return sorted(set(hashes))


def content_security_policy(script_hashes: list[str], *, response_header: bool) -> str:
    script_sources = ["'self'", "'wasm-unsafe-eval'", *script_hashes]
    directives = [
        "base-uri 'self'",
        "default-src 'self'",
        "object-src 'none'",
        f"script-src {' '.join(script_sources)}",
        "style-src 'self' 'unsafe-inline' blob:",
        "img-src 'self' data: blob:",
        "font-src 'self' data: blob:",
        "connect-src 'self'",
        "worker-src 'self' blob:",
        "frame-src 'self' blob:",
        "manifest-src 'self'",
        "media-src 'none'",
        "form-action 'none'",
    ]
    if response_header:
        directives.append("frame-ancestors 'none'")
    return "; ".join(directives) + ";"


def production_headers(script_hashes: list[str]) -> dict[str, str]:
    return {
        "Content-Security-Policy": content_security_policy(
            script_hashes, response_header=True
        ),
        "Cross-Origin-Opener-Policy": "same-origin",
        "Cross-Origin-Resource-Policy": "same-origin",
        "Permissions-Policy": (
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), "
            "magnetometer=(), microphone=(), payment=(), usb=()"
        ),
        "Referrer-Policy": "no-referrer",
        "X-Content-Type-Options": "nosniff",
        "X-Frame-Options": "DENY",
    }


def cloudflare_headers(headers: dict[str, str]) -> str:
    lines = ["/*"]
    lines.extend(f"  {name}: {value}" for name, value in headers.items())
    lines.extend(
        [
            "",
            "/index.html",
            "  Cache-Control: no-cache",
            "",
            "/_framework/*",
            "  Cache-Control: public, max-age=31536000, immutable",
            "",
        ]
    )
    return "\n".join(lines)


def azure_static_web_app_config(headers: dict[str, str]) -> dict[str, object]:
    return {
        "globalHeaders": headers,
        "navigationFallback": {
            "rewrite": "/index.html",
            "exclude": ["/_framework/*", "/css/*", "/js/*", "/samples/*"],
        },
        "routes": [
            {
                "route": "/index.html",
                "headers": {"Cache-Control": "no-cache"},
            },
            {
                "route": "/_framework/*",
                "headers": {
                    "Cache-Control": "public, max-age=31536000, immutable"
                },
            },
        ],
    }


def hardened_document(document: str) -> tuple[str, list[str]]:
    document = CSP_META_PATTERN.sub("", document)
    document = REFERRER_META_PATTERN.sub("", document)
    script_hashes = inline_script_hashes(document)
    policy = content_security_policy(script_hashes, response_header=False)
    security_meta = (
        f'\n    <meta id="unpdf-csp" http-equiv="Content-Security-Policy" '
        f'content="{policy}" />\n'
        '    <meta id="unpdf-referrer" name="referrer" content="no-referrer" />'
    )
    if len(HEAD_PATTERN.findall(document)) != 1:
        raise ValueError("expected exactly one head element in published index.html")
    return HEAD_PATTERN.sub(r"\1" + security_meta, document, count=1), script_hashes


def harden_site(root: Path) -> None:
    index_path = root / "index.html"
    if not index_path.is_file():
        raise FileNotFoundError(f"published browser app is missing {index_path}")
    document = index_path.read_text(encoding="utf-8-sig")
    hardened, script_hashes = hardened_document(document)
    index_path.write_text(hardened, encoding="utf-8")
    for suffix in (".br", ".gz"):
        index_path.with_name(index_path.name + suffix).unlink(missing_ok=True)

    headers = production_headers(script_hashes)
    (root / "_headers").write_text(cloudflare_headers(headers), encoding="utf-8")
    (root / "staticwebapp.config.json").write_text(
        json.dumps(azure_static_web_app_config(headers), indent=2, sort_keys=True)
        + "\n",
        encoding="utf-8",
    )


def verify_site(root: Path) -> None:
    index_path = root / "index.html"
    document = index_path.read_text(encoding="utf-8-sig")
    matches = list(CSP_META_PATTERN.finditer(document))
    if len(matches) != 1:
        raise ValueError("published index.html must contain exactly one unpdf CSP meta element")
    if len(REFERRER_META_PATTERN.findall(document)) != 1:
        raise ValueError("published index.html must contain exactly one no-referrer meta element")

    document_without_security = CSP_META_PATTERN.sub("", document)
    document_without_security = REFERRER_META_PATTERN.sub("", document_without_security)
    script_hashes = inline_script_hashes(document_without_security)
    expected_policy = content_security_policy(script_hashes, response_header=False)
    if f'content="{expected_policy}"' not in matches[0].group(0):
        raise ValueError("published index.html CSP does not match its inline script hashes")
    if "'unsafe-inline'" in expected_policy.split("style-src", 1)[0]:
        raise ValueError("published script policy must not allow unsafe-inline")
    if "connect-src 'self'" not in expected_policy:
        raise ValueError("published CSP must prevent cross-origin connections")
    if "'wasm-unsafe-eval'" not in expected_policy or "'unsafe-eval'" in expected_policy:
        raise ValueError("published CSP must allow only WebAssembly evaluation")

    headers = production_headers(script_hashes)
    if (root / "_headers").read_text(encoding="utf-8") != cloudflare_headers(headers):
        raise ValueError("Cloudflare _headers does not match the hardened browser shell")
    actual_azure = json.loads((root / "staticwebapp.config.json").read_text(encoding="utf-8"))
    if actual_azure != azure_static_web_app_config(headers):
        raise ValueError("Azure Static Web Apps headers do not match the hardened browser shell")
    for suffix in (".br", ".gz"):
        if index_path.with_name(index_path.name + suffix).exists():
            raise ValueError("modified index.html must not retain stale compressed sidecars")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    if not args.check:
        harden_site(root)
    verify_site(root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
