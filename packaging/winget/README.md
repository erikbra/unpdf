# WinGet distribution

Generate manifests from an immutable release contract:

```console
python eng/update_winget_manifests.py \
  --manifest https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/release-manifest.json \
  --output packaging/winget/manifests/ErikBra.Unpdf/4.0.0-preview.1
```

The package uses WinGet's ZIP plus nested portable installer model. WinGet
extracts `unpdf.exe`, creates the `unpdf` command alias, and owns removal of the
portable package and PATH link.

The Windows workflow runs `winget validate`, installs the local manifest,
performs a real conversion, and verifies uninstall cleanup.

## Community repository submission

For a release suitable for the public source:

1. Generate and validate the version directory.
2. Fork `microsoft/winget-pkgs` and place the three files under
   `manifests/e/ErikBra/Unpdf/<version>`.
3. Run `winget validate --manifest <directory>` and test install/uninstall in
   Windows Sandbox.
4. Open a reviewable pull request following the repository's first-time
   contributor checklist and CLA process.

Automation deliberately prepares manifests but does not push branches or open
pull requests in `microsoft/winget-pkgs`. Stable submission should wait for an
Authenticode-signed Windows release so users do not receive an unsigned binary.
