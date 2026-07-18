#!/usr/bin/env python3
"""Pack and validate the standalone PDF conversion NuGet packages."""

from __future__ import annotations

import argparse
import json
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
NUGET_ORG = "https://api.nuget.org/v3/index.json"


@dataclass(frozen=True)
class PackageSpec:
    package_id: str
    project: Path
    direct_dependencies: tuple[str, ...]


PACKAGES = (
    PackageSpec(
        "PdfBox.Net.Layout",
        ROOT / "src/PdfBox.Net.Layout/PdfBox.Net.Layout.csproj",
        ("PdfBox.Net.Core",),
    ),
    PackageSpec(
        "PdfBox.Net.Html",
        ROOT / "src/PdfBox.Net.Html/PdfBox.Net.Html.csproj",
        ("PdfBox.Net.Layout",),
    ),
    PackageSpec(
        "PdfBox.Net.Markdown",
        ROOT / "src/PdfBox.Net.Markdown/PdfBox.Net.Markdown.csproj",
        ("PdfBox.Net.Layout",),
    ),
)

CONSUMERS = (
    (
        "PdfBox.Net.Html",
        ROOT / "samples/PdfBox.Net.Html.Consumer/PdfBox.Net.Html.Consumer.csproj",
        "index.html",
    ),
    (
        "PdfBox.Net.Markdown",
        ROOT / "samples/PdfBox.Net.Markdown.Consumer/PdfBox.Net.Markdown.Consumer.csproj",
        "document.md",
    ),
)


def run(command: list[str], *, cwd: Path = ROOT) -> None:
    print("+", " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True)


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def single_child(element: ET.Element, name: str) -> ET.Element:
    matches = [child for child in element.iter() if local_name(child.tag) == name]
    if len(matches) != 1:
        raise ValueError(f"Expected one {name} element, found {len(matches)}")
    return matches[0]


def child_text(element: ET.Element, name: str) -> str:
    value = single_child(element, name).text
    if value is None or not value.strip():
        raise ValueError(f"{name} must not be empty")
    return value.strip()


def nuspec_dependencies(metadata: ET.Element) -> dict[str, str]:
    dependencies: dict[str, str] = {}
    for element in metadata.iter():
        if local_name(element.tag) != "dependency":
            continue
        package_id = element.attrib.get("id")
        version = element.attrib.get("version")
        if not package_id or not version:
            raise ValueError("Every NuGet dependency must have an id and version")
        dependencies[package_id] = version
    return dependencies


def validate_nupkg(
    archive: Path,
    spec: PackageSpec,
    version: str,
) -> dict[str, str]:
    with zipfile.ZipFile(archive) as package:
        names = package.namelist()
        nuspec_names = [name for name in names if name.endswith(".nuspec")]
        if len(nuspec_names) != 1:
            raise ValueError(f"{archive} must contain exactly one nuspec")
        metadata = single_child(
            ET.fromstring(package.read(nuspec_names[0])),
            "metadata",
        )

        expected_metadata = {
            "id": spec.package_id,
            "version": version,
            "authors": "unpdf contributors",
            "license": "Apache-2.0",
            "projectUrl": "https://github.com/erikbra/unpdf",
            "readme": "README.md",
        }
        actual_metadata = {
            key: child_text(metadata, key)
            for key in expected_metadata
        }
        if actual_metadata != expected_metadata:
            raise ValueError(
                f"{archive} metadata mismatch: expected {expected_metadata}, "
                f"got {actual_metadata}"
            )

        license_element = single_child(metadata, "license")
        if license_element.attrib.get("type") != "expression":
            raise ValueError(f"{archive} must use an SPDX license expression")

        repository = single_child(metadata, "repository")
        if repository.attrib.get("type") != "git":
            raise ValueError(f"{archive} repository type must be git")
        if repository.attrib.get("url") != "https://github.com/erikbra/unpdf":
            raise ValueError(f"{archive} repository URL is incorrect")
        if not repository.attrib.get("commit"):
            raise ValueError(f"{archive} repository commit must be recorded")

        dependencies = nuspec_dependencies(metadata)
        if tuple(sorted(dependencies)) != tuple(sorted(spec.direct_dependencies)):
            raise ValueError(
                f"{archive} direct dependencies are {sorted(dependencies)}, "
                f"expected {sorted(spec.direct_dependencies)}"
            )
        if spec.package_id != "PdfBox.Net.Layout":
            layout_version = dependencies["PdfBox.Net.Layout"]
            if layout_version != version:
                raise ValueError(
                    f"{archive} must depend on PdfBox.Net.Layout {version}, "
                    f"got {layout_version}"
                )

        expected_library = f"lib/net10.0/{spec.package_id}.dll"
        required_files = {"README.md", expected_library}
        missing = required_files - set(names)
        if missing:
            raise ValueError(f"{archive} is missing {sorted(missing)}")

        bundled_conversion_libraries = {
            Path(name).stem
            for name in names
            if name.startswith("lib/") and name.endswith(".dll")
            and Path(name).stem.startswith("PdfBox.Net.")
        }
        if bundled_conversion_libraries != {spec.package_id}:
            raise ValueError(
                f"{archive} bundles unexpected conversion libraries: "
                f"{sorted(bundled_conversion_libraries)}"
            )

        return dependencies


def discover_package(artifacts_dir: Path, package_id: str, version: str) -> Path:
    matches: list[Path] = []
    for archive in artifacts_dir.glob("*.nupkg"):
        with zipfile.ZipFile(archive) as package:
            nuspec_names = [
                name for name in package.namelist()
                if name.endswith(".nuspec")
            ]
            if len(nuspec_names) != 1:
                continue
            metadata = single_child(
                ET.fromstring(package.read(nuspec_names[0])),
                "metadata",
            )
            if (
                child_text(metadata, "id") == package_id
                and child_text(metadata, "version") == version
            ):
                matches.append(archive)
    if len(matches) != 1:
        raise ValueError(
            f"Expected one {package_id} {version} package in {artifacts_dir}, "
            f"found {matches}"
        )
    return matches[0]


def sample_package_references(project: Path) -> list[str]:
    root = ET.parse(project).getroot()
    return [
        element.attrib["Include"]
        for element in root.iter()
        if local_name(element.tag) == "PackageReference"
    ]


def validate_consumer_assets(
    assets_path: Path,
    primary_package: str,
    version: str,
) -> None:
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    frameworks = assets["project"]["frameworks"]
    if len(frameworks) != 1:
        raise ValueError(f"{assets_path} must target exactly one framework")
    framework = next(iter(frameworks.values()))
    direct = sorted(framework.get("dependencies", {}))
    if direct != [primary_package]:
        raise ValueError(
            f"{assets_path} has direct dependencies {direct}, "
            f"expected only {primary_package}"
        )

    resolved = {
        identifier.rsplit("/", 1)[0]
        for identifier in assets.get("libraries", {})
    }
    required = {primary_package, "PdfBox.Net.Layout", "PdfBox.Net.Core"}
    missing = required - resolved
    if missing:
        raise ValueError(f"{assets_path} is missing transitive packages {sorted(missing)}")

    other_conversion_package = (
        "PdfBox.Net.Markdown"
        if primary_package == "PdfBox.Net.Html"
        else "PdfBox.Net.Html"
    )
    if other_conversion_package in resolved:
        raise ValueError(
            f"{assets_path} unexpectedly resolves {other_conversion_package}"
        )
    if f"{primary_package}/{version}" not in assets.get("libraries", {}):
        raise ValueError(
            f"{assets_path} did not resolve {primary_package} {version}"
        )
    if f"PdfBox.Net.Layout/{version}" not in assets.get("libraries", {}):
        raise ValueError(
            f"{assets_path} did not resolve PdfBox.Net.Layout {version}"
        )


def pack_packages(
    artifacts_dir: Path,
    version: str,
    configuration: str,
    no_build: bool,
) -> dict[str, Path]:
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    for spec in PACKAGES:
        for old_archive in artifacts_dir.glob(f"{spec.package_id}.*.nupkg"):
            old_archive.unlink()
        command = [
            "dotnet",
            "pack",
            str(spec.project),
            "--configuration",
            configuration,
            "--output",
            str(artifacts_dir),
            f"-p:Version={version}",
        ]
        if no_build:
            command.append("--no-build")
        run(command)

    archives: dict[str, Path] = {}
    for spec in PACKAGES:
        archive = discover_package(artifacts_dir, spec.package_id, version)
        validate_nupkg(archive, spec, version)
        archives[spec.package_id] = archive
        print(f"Validated {archive.relative_to(ROOT)}")
    return archives


def validate_consumers(
    artifacts_dir: Path,
    version: str,
    configuration: str,
) -> None:
    fixture = (
        ROOT
        / "tests/SharedFixtures/TextExtraction/Issue412/4PP-Highlighting.pdf"
    )
    with tempfile.TemporaryDirectory(prefix="unpdf-package-validation-") as temporary:
        temp_root = Path(temporary)
        packages_dir = temp_root / "packages"
        for primary_package, project, expected_output in CONSUMERS:
            references = sample_package_references(project)
            if references != [primary_package]:
                raise ValueError(
                    f"{project} must reference only {primary_package}, got {references}"
                )

            property_argument = f"-p:ConversionPackageVersion={version}"
            run([
                "dotnet",
                "restore",
                str(project),
                property_argument,
                "--packages",
                str(packages_dir),
                "--source",
                str(artifacts_dir),
                "--source",
                NUGET_ORG,
                "--force-evaluate",
            ])
            run([
                "dotnet",
                "build",
                str(project),
                "--configuration",
                configuration,
                "--no-restore",
                property_argument,
            ])

            assets_path = project.parent / "obj/project.assets.json"
            validate_consumer_assets(
                assets_path,
                primary_package,
                version,
            )

            output_dir = temp_root / primary_package
            run([
                "dotnet",
                "run",
                "--project",
                str(project),
                "--configuration",
                configuration,
                "--no-build",
                "--no-restore",
                property_argument,
                "--",
                str(fixture),
                str(output_dir),
            ])
            output_path = output_dir / expected_output
            if not output_path.is_file() or output_path.stat().st_size == 0:
                raise ValueError(
                    f"{primary_package} consumer did not create {output_path}"
                )
            print(f"Validated {primary_package} consumer output: {output_path}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Pack and validate PdfBox.Net conversion NuGet packages."
    )
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--version", default="4.0.0-local.1")
    parser.add_argument(
        "--artifacts-dir",
        type=Path,
        default=ROOT / "artifacts/conversion-packages",
    )
    parser.add_argument(
        "--no-build",
        action="store_true",
        help="Pass --no-build to dotnet pack after an existing solution build.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    artifacts_dir = args.artifacts_dir.resolve()
    pack_packages(
        artifacts_dir,
        args.version,
        args.configuration,
        args.no_build,
    )
    validate_consumers(
        artifacts_dir,
        args.version,
        args.configuration,
    )
    print("Conversion package validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
