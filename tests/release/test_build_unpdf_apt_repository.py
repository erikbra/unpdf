import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "eng" / "build_unpdf_apt_repository.py"
SPEC = importlib.util.spec_from_file_location("build_unpdf_apt_repository", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class AptRepositoryBuilderTest(unittest.TestCase):
    def test_semver_prerelease_uses_debian_precedence(self):
        self.assertEqual("4.0.0~preview.1", MODULE.debian_version("4.0.0-preview.1"))
        self.assertEqual("4.0.0", MODULE.debian_version("4.0.0"))

    def test_control_metadata_maps_architecture_and_version(self):
        control = MODULE.control_text("4.0.0~preview.1", "arm64")
        self.assertIn("Package: unpdf", control)
        self.assertIn("Version: 4.0.0~preview.1", control)
        self.assertIn("Architecture: arm64", control)

    def test_gpg_command_keeps_passphrase_out_of_process_arguments(self):
        passphrase_file = Path("unpdf-passphrase")
        command = MODULE.gpg_command(passphrase_file)
        self.assertIn("--pinentry-mode", command)
        self.assertIn("--passphrase-file", command)
        self.assertIn(str(passphrase_file), command)
        self.assertNotIn("secret", command)
