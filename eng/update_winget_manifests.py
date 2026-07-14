#!/usr/bin/env python3
"""Generate WinGet manifests from an unpdf release manifest."""

import argparse
import json
from pathlib import Path
from urllib.request import urlopen

PACKAGE_ID = "ErikBra.Unpdf"
MANIFEST_VERSION = "1.10.0"


def load_manifest(source: str) -> dict:
    if source.startswith(("https://", "http://")):
        with urlopen(source) as response:
            return json.load(response)
    return json.loads(Path(source).read_text(encoding="utf-8"))


def generate(manifest: dict) -> dict[str, str]:
    version = manifest["version"]
    windows = next((artifact for artifact in manifest["artifacts"] if artifact["rid"] == "win-x64"), None)
    if windows is None:
        raise ValueError("release manifest is missing win-x64")
    release_url = f"https://github.com/erikbra/unpdf/releases/tag/unpdf-v{version}"
    schema_root = "https://aka.ms/winget-manifest"
    common = f"PackageIdentifier: {PACKAGE_ID}\nPackageVersion: {version}\n"

    version_manifest = f"""# yaml-language-server: $schema={schema_root}.version.{MANIFEST_VERSION}.schema.json

{common}DefaultLocale: en-US
ManifestType: version
ManifestVersion: {MANIFEST_VERSION}
"""
    installer_manifest = f"""# yaml-language-server: $schema={schema_root}.installer.{MANIFEST_VERSION}.schema.json

{common}InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: unpdf.exe
    PortableCommandAlias: unpdf
Installers:
  - Architecture: x64
    InstallerUrl: {windows["url"]}
    InstallerSha256: {windows["sha256"].upper()}
ManifestType: installer
ManifestVersion: {MANIFEST_VERSION}
"""
    locale_manifest = f"""# yaml-language-server: $schema={schema_root}.defaultLocale.{MANIFEST_VERSION}.schema.json

{common}PackageLocale: en-US
Publisher: PdfBox.Net contributors
PublisherUrl: https://github.com/erikbra/unpdf
PublisherSupportUrl: https://github.com/erikbra/unpdf/issues
PackageName: unpdf
PackageUrl: https://github.com/erikbra/unpdf
License: Apache-2.0
LicenseUrl: https://github.com/erikbra/unpdf/blob/main/LICENSE
ShortDescription: Convert PDF documents to semantic HTML from the command line.
Description: unpdf is a self-contained single-file command-line converter for producing semantic or fixed-layout HTML from PDF documents.
Tags:
  - cli
  - html
  - pdf
  - pdf-converter
ReleaseNotesUrl: {release_url}
ManifestType: defaultLocale
ManifestVersion: {MANIFEST_VERSION}
"""
    return {
        f"{PACKAGE_ID}.yaml": version_manifest,
        f"{PACKAGE_ID}.installer.yaml": installer_manifest,
        f"{PACKAGE_ID}.locale.en-US.yaml": locale_manifest,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output
    output.mkdir(parents=True, exist_ok=True)
    for name, contents in generate(load_manifest(args.manifest)).items():
        (output / name).write_text(contents, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
