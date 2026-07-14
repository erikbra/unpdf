#!/usr/bin/env python3
"""Build unpdf .deb packages and a signed static APT repository."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
import shutil
import subprocess
import tarfile
import tempfile
from pathlib import Path
from urllib.request import urlopen

RID_TO_ARCH = {"linux-x64": "amd64", "linux-arm64": "arm64"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def debian_version(version: str) -> str:
    match = re.fullmatch(r"([0-9]+\.[0-9]+\.[0-9]+)(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?", version)
    if not match:
        raise ValueError(f"unsupported release version: {version}")
    return match.group(1) + (f"~{match.group(2)}" if match.group(2) else "")


def load_manifest(source: str) -> dict:
    if source.startswith(("https://", "http://")):
        with urlopen(source) as response:
            return json.load(response)
    return json.loads(Path(source).read_text(encoding="utf-8"))


def download(url: str, destination: Path) -> None:
    with urlopen(url) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output)


def control_text(version: str, architecture: str) -> str:
    return f"""Package: unpdf
Version: {version}
Section: utils
Priority: optional
Architecture: {architecture}
Maintainer: PdfBox.Net contributors <noreply@github.com>
Homepage: https://github.com/erikbra/unpdf
Description: Convert PDF documents to semantic HTML
 unpdf is a self-contained single-file command-line converter for producing
 semantic or fixed-layout HTML from PDF documents.
"""


def build_deb(root: Path, artifact: dict, version: str, architecture: str, output: Path) -> Path:
    with tempfile.TemporaryDirectory() as temporary:
        temporary_path = Path(temporary)
        archive = temporary_path / artifact["file"]
        download(artifact["url"], archive)
        if sha256(archive) != artifact["sha256"]:
            raise RuntimeError(f"checksum mismatch for {archive.name}")
        extracted = temporary_path / "extracted"
        extracted.mkdir()
        with tarfile.open(archive, "r:gz") as source:
            source.extractall(extracted, filter="data")

        package = temporary_path / "package"
        (package / "DEBIAN").mkdir(parents=True)
        (package / "usr/bin").mkdir(parents=True)
        (package / "usr/share/doc/unpdf").mkdir(parents=True)
        (package / "usr/share/man/man1").mkdir(parents=True)
        (package / "DEBIAN/control").write_text(control_text(version, architecture), encoding="utf-8")
        shutil.copy2(extracted / "unpdf", package / "usr/bin/unpdf")
        (package / "usr/bin/unpdf").chmod(0o755)
        shutil.copy2(extracted / "LICENSE.txt", package / "usr/share/doc/unpdf/LICENSE")
        shutil.copy2(extracted / "NOTICE.txt", package / "usr/share/doc/unpdf/NOTICE")
        copyright_text = (root / "packaging/debian/copyright").read_bytes()
        (package / "usr/share/doc/unpdf/copyright").write_bytes(copyright_text)
        man_page = (root / "packaging/debian/unpdf.1").read_bytes()
        with gzip.GzipFile(filename=str(package / "usr/share/man/man1/unpdf.1.gz"), mode="wb", mtime=0) as compressed:
            compressed.write(man_page)

        destination = output / f"unpdf_{version}_{architecture}.deb"
        subprocess.run(["dpkg-deb", "--root-owner-group", "--build", str(package), str(destination)], check=True)
        return destination


def gpg_command(passphrase_file: Path | None = None) -> list[str]:
    command = ["gpg", "--batch", "--yes"]
    if passphrase_file is not None:
        command.extend(["--pinentry-mode", "loopback", "--passphrase-file", str(passphrase_file)])
    return command


def build_repository(
    root: Path,
    manifest: dict,
    output: Path,
    suite: str,
    gpg_key: str,
    gpg_passphrase_file: Path | None = None,
) -> None:
    shutil.rmtree(output, ignore_errors=True)
    output.mkdir(parents=True)
    version = debian_version(manifest["version"])
    artifacts = {artifact["rid"]: artifact for artifact in manifest["artifacts"]}
    pool = output / "pool/main/u/unpdf"
    pool.mkdir(parents=True)
    for rid, architecture in RID_TO_ARCH.items():
        if rid not in artifacts:
            raise ValueError(f"release manifest is missing {rid}")
        build_deb(root, artifacts[rid], version, architecture, pool)

    for architecture in RID_TO_ARCH.values():
        binary = output / f"dists/{suite}/main/binary-{architecture}"
        binary.mkdir(parents=True)
        packages = subprocess.run(
            ["dpkg-scanpackages", "--arch", architecture, "pool", "/dev/null"],
            cwd=output, check=True, capture_output=True,
        ).stdout
        (binary / "Packages").write_bytes(packages)
        with gzip.GzipFile(filename=str(binary / "Packages.gz"), mode="wb", mtime=0) as compressed:
            compressed.write(packages)

    release_dir = output / f"dists/{suite}"
    release = subprocess.run([
        "apt-ftparchive",
        "-o", "APT::FTPArchive::Release::Origin=unpdf",
        "-o", "APT::FTPArchive::Release::Label=unpdf",
        "-o", f"APT::FTPArchive::Release::Suite={suite}",
        "-o", f"APT::FTPArchive::Release::Codename={suite}",
        "-o", "APT::FTPArchive::Release::Architectures=amd64 arm64",
        "-o", "APT::FTPArchive::Release::Components=main",
        "release", f"dists/{suite}",
    ], cwd=output, check=True, capture_output=True).stdout
    (release_dir / "Release").write_bytes(release)
    subprocess.run(gpg_command(gpg_passphrase_file) + [
        "--local-user", gpg_key, "--digest-algo", "SHA256",
        "--clearsign", "--output", str(release_dir / "InRelease"), str(release_dir / "Release"),
    ], check=True)
    subprocess.run(gpg_command(gpg_passphrase_file) + [
        "--local-user", gpg_key, "--digest-algo", "SHA256",
        "--armor", "--detach-sign", "--output", str(release_dir / "Release.gpg"), str(release_dir / "Release"),
    ], check=True)
    with (output / "unpdf-archive-keyring.gpg").open("wb") as keyring:
        subprocess.run(["gpg", "--batch", "--export", gpg_key], check=True, stdout=keyring)
    with (output / "unpdf-archive-key.asc").open("wb") as armored_key:
        subprocess.run(["gpg", "--batch", "--armor", "--export", gpg_key], check=True, stdout=armored_key)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--suite", default="preview")
    parser.add_argument("--gpg-key", required=True)
    parser.add_argument("--gpg-passphrase-file", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    passphrase_file = args.gpg_passphrase_file.resolve() if args.gpg_passphrase_file else None
    build_repository(
        root,
        load_manifest(args.manifest),
        args.output.resolve(),
        args.suite,
        args.gpg_key,
        passphrase_file,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
