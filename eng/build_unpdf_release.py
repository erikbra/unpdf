#!/usr/bin/env python3
"""Build and package a reproducible unpdf self-contained single-file release artifact."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path

SUPPORTED_RIDS = {"linux-x64", "linux-arm64", "win-x64", "osx-x64", "osx-arm64"}
SEMVER = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$")
EPOCH = 315532800  # 1980-01-01, representable by zip and tar.


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_version(value: str) -> str:
    if not SEMVER.fullmatch(value):
        raise ValueError(f"version must be SemVer, got: {value}")
    return value


def archive_name(version: str, rid: str) -> str:
    suffix = ".zip" if rid.startswith("win-") else ".tar.gz"
    return f"unpdf-{version}-{rid}{suffix}"


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def spdx_id(value: str) -> str:
    return "SPDXRef-" + re.sub(r"[^A-Za-z0-9.-]", "-", value)


def create_sbom(stage: Path, version: str, rid: str, executable: Path, assets: Path) -> None:
    packages = [{
        "SPDXID": "SPDXRef-Package-unpdf",
        "name": "unpdf",
        "versionInfo": version,
        "downloadLocation": "NOASSERTION",
        "filesAnalyzed": True,
        "licenseConcluded": "Apache-2.0",
        "licenseDeclared": "Apache-2.0",
        "copyrightText": "NOASSERTION",
    }]
    relationships = [{
        "spdxElementId": "SPDXRef-Package-unpdf",
        "relationshipType": "CONTAINS",
        "relatedSpdxElement": "SPDXRef-File-unpdf",
    }]
    if assets.exists():
        data = json.loads(assets.read_text(encoding="utf-8"))
        for library, details in sorted(data.get("libraries", {}).items()):
            if details.get("type") != "package":
                continue
            name, package_version = library.rsplit("/", 1)
            identifier = spdx_id(f"NuGet-{name}-{package_version}")
            packages.append({
                "SPDXID": identifier,
                "name": name,
                "versionInfo": package_version,
                "downloadLocation": f"https://www.nuget.org/packages/{name}/{package_version}",
                "filesAnalyzed": False,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": "NOASSERTION",
                "copyrightText": "NOASSERTION",
                "externalRefs": [{
                    "referenceCategory": "PACKAGE-MANAGER",
                    "referenceType": "purl",
                    "referenceLocator": f"pkg:nuget/{name}@{package_version}",
                }],
            })
            relationships.append({
                "spdxElementId": "SPDXRef-Package-unpdf",
                "relationshipType": "DEPENDS_ON",
                "relatedSpdxElement": identifier,
            })

    executable_hash = sha256(executable)
    document = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"unpdf-{version}-{rid}",
        "documentNamespace": f"https://github.com/erikbra/unpdf/sbom/{version}/{rid}/{executable_hash[:16]}",
        "creationInfo": {
            "created": "1980-01-01T00:00:00Z",
            "creators": ["Tool: PdfBox.Net release builder"],
        },
        "documentDescribes": ["SPDXRef-Package-unpdf"],
        "packages": packages,
        "files": [{
            "SPDXID": "SPDXRef-File-unpdf",
            "fileName": f"./{executable.name}",
            "checksums": [{"algorithm": "SHA256", "checksumValue": executable_hash}],
            "licenseConcluded": "Apache-2.0",
            "licenseInfoInFiles": ["Apache-2.0"],
            "copyrightText": "NOASSERTION",
        }],
        "relationships": relationships,
    }
    write_json(stage / "sbom.spdx.json", document)


def add_tar_entry(archive: tarfile.TarFile, path: Path, name: str) -> None:
    info = archive.gettarinfo(str(path), arcname=name)
    info.uid = info.gid = 0
    info.uname = info.gname = "root"
    info.mtime = EPOCH
    with path.open("rb") as stream:
        archive.addfile(info, stream)


def create_archive(stage: Path, destination: Path, rid: str) -> None:
    files = sorted(path for path in stage.iterdir() if path.is_file())
    if rid.startswith("win-"):
        with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for path in files:
                info = zipfile.ZipInfo(path.name, (1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = (0o755 if path.suffix == ".exe" else 0o644) << 16
                archive.writestr(info, path.read_bytes(), compresslevel=9)
    else:
        with destination.open("wb") as raw:
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as compressed:
                with tarfile.open(fileobj=compressed, mode="w") as archive:
                    for path in files:
                        add_tar_entry(archive, path, path.name)


def verify_signing(executable: Path, rid: str, signing_status: str) -> None:
    if signing_status == "unsigned-preview":
        return
    if signing_status == "windows-authenticode":
        if not rid.startswith("win-") or os.name != "nt":
            raise RuntimeError("Authenticode status can only be asserted on a native Windows runner")
        subprocess.run([
            "powershell", "-NoProfile", "-Command",
            "$signature = Get-AuthenticodeSignature -LiteralPath $args[0]; "
            "if ($signature.Status -ne 'Valid') { throw \"Invalid Authenticode signature: $($signature.Status)\" }",
            str(executable),
        ], check=True)
        return
    if signing_status == "macos-developer-id-notarized":
        if not rid.startswith("osx-") or sys.platform != "darwin":
            raise RuntimeError("macOS signing status can only be asserted on a native macOS runner")
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(executable)], check=True)
        subprocess.run(["spctl", "--assess", "--type", "execute", str(executable)], check=True)
        subprocess.run(["xcrun", "stapler", "validate", str(executable)], check=True)
        return
    raise RuntimeError(f"unsupported signing status: {signing_status}")


def build_release(
    root: Path,
    version: str,
    rid: str,
    output: Path,
    signing_status: str,
    skip_smoke: bool = False,
) -> Path:
    project = root / "apps/PdfBox.Net.Unpdf/PdfBox.Net.Unpdf.csproj"
    publish = output / "publish"
    stage = output / "stage"
    shutil.rmtree(output, ignore_errors=True)
    publish.mkdir(parents=True)
    stage.mkdir()
    subprocess.run([
        "dotnet", "publish", str(project), "-c", "Release", "-r", rid,
        "-p:PublishProfile=SingleFile", f"-p:Version={version}", "-o", str(publish),
    ], check=True)

    executable_name = "unpdf.exe" if rid.startswith("win-") else "unpdf"
    executable = publish / executable_name
    if not executable.is_file():
        raise RuntimeError(f"publish did not produce {executable}")
    verify_signing(executable, rid, signing_status)
    shutil.copy2(executable, stage / executable_name)
    if not rid.startswith("win-"):
        (stage / executable_name).chmod(0o755)
    shutil.copy2(root / "LICENSE", stage / "LICENSE.txt")
    shutil.copy2(root / "NOTICE", stage / "NOTICE.txt")
    (stage / "VERSION").write_text(version + "\n", encoding="utf-8")
    (stage / "SIGNING.md").write_text(
        f"# Signing status\n\nStatus: **{signing_status}**\n\n"
        + ("This preview artifact is not code-signed or notarized. Verify its SHA-256 checksum and GitHub provenance before use.\n"
           if signing_status == "unsigned-preview" else
           "The release workflow asserted and verified this platform signature before packaging.\n"),
        encoding="utf-8",
    )
    create_sbom(stage, version, rid, stage / executable_name, project.parent / "obj/project.assets.json")
    manifest = {
        "schemaVersion": 1,
        "product": "unpdf",
        "version": version,
        "rid": rid,
        "executable": executable_name,
        "executableSha256": sha256(stage / executable_name),
        "signing": {"status": signing_status},
        "source": "https://github.com/erikbra/unpdf",
    }
    write_json(stage / "artifact-manifest.json", manifest)
    sidecar_manifest = output / f"unpdf-{version}-{rid}.manifest.json"
    write_json(sidecar_manifest, manifest)

    archive = output / archive_name(version, rid)
    create_archive(stage, archive, rid)
    checksum = sha256(archive)
    checksum_path = archive.with_suffix(archive.suffix + ".sha256")
    checksum_path.write_text(f"{checksum}  {archive.name}\n", encoding="ascii")
    if sha256(archive) != checksum:
        raise RuntimeError("archive checksum verification failed")

    if not skip_smoke:
        subprocess.run([str(executable), "--version"], check=True)
        fixture_output = output / "smoke-html"
        subprocess.run([
            str(executable), str(root / "tests/SharedFixtures/classic-xref-fixture.pdf"),
            "--output", str(fixture_output), "--quiet",
        ], check=True)
        if "Hello" not in (fixture_output / "index.html").read_text(encoding="utf-8"):
            raise RuntimeError("release executable smoke output did not contain expected text")
    return archive


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True, type=validate_version)
    parser.add_argument("--rid", required=True, choices=sorted(SUPPORTED_RIDS))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--signing-status", default="unsigned-preview",
                        choices=["unsigned-preview", "windows-authenticode", "macos-developer-id-notarized"])
    parser.add_argument("--skip-smoke", action="store_true",
                        help="package without executing the target binary (for cross-RID package tests)")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    archive = build_release(
        root, args.version, args.rid, args.output.resolve(), args.signing_status, args.skip_smoke
    )
    print(json.dumps({"archive": str(archive), "checksum": str(archive.with_suffix(archive.suffix + '.sha256'))}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
