# Homebrew distribution

`Formula/unpdf.rb` is generated from the immutable unpdf release manifest:

```console
python3 eng/update_homebrew_formula.py \
  --manifest https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/release-manifest.json \
  --output packaging/homebrew/Formula/unpdf.rb
```

The project-owned tap is `erikbra/homebrew-unpdf`. Users install with:

```console
brew install erikbra/unpdf/unpdf
```

The formula supports Apple Silicon and Intel macOS, plus x64 and ARM64
Linuxbrew. `brew test unpdf` performs a real PDF-to-HTML conversion.

The remote tap install and `brew test` are verified on Apple Silicon macOS.
Linux x64 and ARM64 executables are built and executed on native runners by the
release workflow; a complete Linuxbrew installation job is deferred until the
tap adds its own Linux Homebrew runner.

The tap's daily/manual `Update unpdf formula` workflow consumes
`release-manifest.json`, regenerates the formula, and commits only when release
URLs or checksums change.

Submission to `homebrew-core` should wait until `unpdf` has stable, signed and
notarized releases, a sustained release history, and sufficient user adoption.
