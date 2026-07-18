import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "validate_conversion_packages.py"
SPEC = importlib.util.spec_from_file_location("validate_conversion_packages", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def nuspec(package_id: str, version: str, dependencies: list[tuple[str, str]]) -> str:
    dependency_xml = "".join(
        f'<dependency id="{package}" version="{dependency_version}" />'
        for package, dependency_version in dependencies
    )
    return f"""<?xml version="1.0"?>
    <package>
      <metadata>
        <id>{package_id}</id>
        <version>{version}</version>
        <authors>unpdf contributors</authors>
        <license type="expression">Apache-2.0</license>
        <projectUrl>https://github.com/erikbra/unpdf</projectUrl>
        <readme>README.md</readme>
        <repository type="git" url="https://github.com/erikbra/unpdf" commit="abc123" />
        <dependencies><group targetFramework="net10.0">{dependency_xml}</group></dependencies>
      </metadata>
    </package>
    """


class ConversionPackageValidatorTest(unittest.TestCase):
    def test_nupkg_validation_accepts_expected_html_boundary(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "PdfBox.Net.Html.4.0.0-test.1.nupkg"
            with zipfile.ZipFile(archive, "w") as package:
                package.writestr(
                    "PdfBox.Net.Html.nuspec",
                    nuspec(
                        "PdfBox.Net.Html",
                        "4.0.0-test.1",
                        [("PdfBox.Net.Layout", "4.0.0-test.1")],
                    ),
                )
                package.writestr("README.md", "# README")
                package.writestr("lib/net10.0/PdfBox.Net.Html.dll", b"assembly")

            dependencies = MODULE.validate_nupkg(
                archive,
                MODULE.PACKAGES[1],
                "4.0.0-test.1",
            )

            self.assertEqual({"PdfBox.Net.Layout": "4.0.0-test.1"}, dependencies)

    def test_nupkg_validation_rejects_html_markdown_cross_dependency(self):
        with tempfile.TemporaryDirectory() as temporary:
            archive = Path(temporary) / "PdfBox.Net.Html.4.0.0-test.1.nupkg"
            with zipfile.ZipFile(archive, "w") as package:
                package.writestr(
                    "PdfBox.Net.Html.nuspec",
                    nuspec(
                        "PdfBox.Net.Html",
                        "4.0.0-test.1",
                        [
                            ("PdfBox.Net.Layout", "4.0.0-test.1"),
                            ("PdfBox.Net.Markdown", "4.0.0-test.1"),
                        ],
                    ),
                )
                package.writestr("README.md", "# README")
                package.writestr("lib/net10.0/PdfBox.Net.Html.dll", b"assembly")

            with self.assertRaisesRegex(ValueError, "direct dependencies"):
                MODULE.validate_nupkg(
                    archive,
                    MODULE.PACKAGES[1],
                    "4.0.0-test.1",
                )

    def test_consumer_assets_require_only_primary_direct_dependency(self):
        with tempfile.TemporaryDirectory() as temporary:
            assets_path = Path(temporary) / "project.assets.json"
            assets_path.write_text(
                json.dumps(
                    {
                        "project": {
                            "frameworks": {
                                "net10.0": {
                                    "dependencies": {
                                        "PdfBox.Net.Markdown": {
                                            "target": "Package",
                                            "version": "[4.0.0-test.1, )",
                                        }
                                    }
                                }
                            }
                        },
                        "libraries": {
                            "PdfBox.Net.Markdown/4.0.0-test.1": {},
                            "PdfBox.Net.Layout/4.0.0-test.1": {},
                            "PdfBox.Net.Core/4.0.0-preview.6": {},
                        },
                    }
                ),
                encoding="utf-8",
            )

            MODULE.validate_consumer_assets(
                assets_path,
                "PdfBox.Net.Markdown",
                "4.0.0-test.1",
            )

    def test_sample_projects_have_one_conversion_package_reference(self):
        for primary_package, project, _ in MODULE.CONSUMERS:
            with self.subTest(project=project):
                self.assertEqual(
                    [primary_package],
                    MODULE.sample_package_references(project),
                )


if __name__ == "__main__":
    unittest.main()
