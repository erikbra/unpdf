#!/usr/bin/env python3
"""Combine unpdf per-RID manifests into the package-manager release contract."""

import argparse
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    base_url = f"https://github.com/{args.repository}/releases/download/unpdf-v{args.version}"
    artifacts = []
    for checksum_path in sorted(args.directory.glob("unpdf-*.sha256")):
        checksum, filename = checksum_path.read_text(encoding="ascii").split()
        rid = filename.removeprefix(f"unpdf-{args.version}-").removesuffix(".tar.gz").removesuffix(".zip")
        sidecar = args.directory / f"unpdf-{args.version}-{rid}.manifest.json"
        if not sidecar.is_file():
            raise SystemExit(f"missing per-RID manifest: {sidecar.name}")
        artifact_manifest = json.loads(sidecar.read_text(encoding="utf-8"))
        artifacts.append({
            "rid": rid,
            "file": filename,
            "sha256": checksum,
            "url": f"{base_url}/{filename}",
            "signing": artifact_manifest["signing"],
        })
    expected = {"linux-x64", "linux-arm64", "win-x64", "osx-x64", "osx-arm64"}
    actual = {artifact["rid"] for artifact in artifacts}
    if actual != expected:
        raise SystemExit(f"release RID set mismatch: expected {sorted(expected)}, got {sorted(actual)}")
    args.output.write_text(json.dumps({
        "schemaVersion": 1,
        "product": "unpdf",
        "version": args.version,
        "tag": f"unpdf-v{args.version}",
        "artifacts": artifacts,
    }, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
