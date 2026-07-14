# unpdf release process

The `Build unpdf Release Artifacts` workflow builds NativeAOT archives on five
native GitHub-hosted runners: Linux x64 and ARM64, Windows x64, and macOS Intel
and ARM64. Each runner executes the resulting binary and converts a deterministic
PDF before uploading it.

Every archive contains:

- the platform-specific `unpdf` executable;
- `LICENSE.txt` and `NOTICE.txt`;
- `VERSION`;
- `SIGNING.md`, with an explicit signing state;
- `artifact-manifest.json`;
- an SPDX 2.3 software bill of materials.

The aggregate job verifies every SHA-256 checksum and writes
`release-manifest.json`. Its filenames and immutable GitHub Release URLs are the
contract consumed by Homebrew, WinGet, APT, and other package managers.

## Preview releases

The current workflow produces `unsigned-preview` artifacts. It may publish only
SemVer prereleases, and the GitHub Release title and included `SIGNING.md` both
state that the binaries are unsigned. GitHub build-provenance attestations and
SHA-256 verification provide supply-chain evidence, but are not substitutes for
platform code signing.

## Stable-release signing

Before stable releases are enabled:

1. Windows artifacts must be Authenticode-signed through a protected GitHub
   environment using a hardware-backed or managed signing identity. The binary
   must pass `Get-AuthenticodeSignature` before packaging.
2. Both macOS binaries must be signed with Developer ID, submitted to Apple's
   notarization service, stapled, and verified with `codesign`, `spctl`, and
   `xcrun stapler validate` before packaging.
3. Signing jobs must consume the unsigned build by digest, produce a new signed
   artifact, and update the per-RID signing state. Signing credentials must never
   be available to pull-request jobs.
4. The stable publish job must reject a manifest unless Windows is marked
   `windows-authenticode` and both macOS artifacts are marked
   `macos-developer-id-notarized`.

Linux packages are authenticated through GitHub provenance/checksums and, for
APT, the separately signed repository metadata.
