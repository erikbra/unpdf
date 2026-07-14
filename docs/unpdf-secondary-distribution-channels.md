# unpdf secondary distribution channels

Homebrew, WinGet, APT, and the checksummed GitHub Release archives cover the
primary desktop and Linux installation paths. Secondary channels should add a
meaningfully different audience without creating a second build, signing, or
version contract.

## Comparison

| Channel | Audience and reach | Review or moderation | Integrity and signing | Update automation | Installed-size effect | Maintenance cost |
| --- | --- | --- | --- | --- | --- | --- |
| Scoop | Windows developers who prefer user-scoped, portable installs | None for a project-owned bucket; the central buckets require review | Manifest SHA-256 verifies the existing ZIP; stable binaries still require Authenticode | Generate the manifest from `release-manifest.json`; Scoop also supports `checkver` and `autoupdate` | NativeAOT ZIP plus negligible manifest/shim overhead | **Low**: one JSON manifest and a Windows smoke test |
| Chocolatey | Windows users and managed environments, with substantial overlap with WinGet | The community repository runs validation, verification, and human moderation | Package scripts verify the release SHA-256; stable binaries still require Authenticode | Generate a Nuspec and install script, then submit through an API-keyed maintainer account | NativeAOT ZIP plus package/script overhead | Medium-high: moderation and maintainer response add a second Windows publication process |
| Snap | Ubuntu and other systems with snapd | Snap Store account, name registration, automated review, and possible manual review | Store assertions sign and authenticate snaps | CI can build and upload per architecture to channels | A snap and its base/runtime mounts add more disk use than the native executable | Medium-high: confinement, store credentials, channels, and architecture builds |
| AUR | Arch Linux users | Community-maintained Git repository; users review and build packages locally | A `-bin` PKGBUILD can verify the release SHA-256 and provenance remains available separately | Update the PKGBUILD version, hashes, and `.SRCINFO` | NativeAOT archive plus package metadata | Medium: small package, but a separate account/community workflow for a narrow audience |
| OCI container | CI and server users already operating container runtimes | None for GHCR; package visibility must be configured | OCI digest, GitHub provenance, and registry access controls | GitHub Actions can publish a multi-architecture image with `GITHUB_TOKEN` | Base image and container metadata are much larger than the standalone executable | Medium: useful for services, but bind mounts and output ownership make the local CLI less ergonomic |
| Direct install script | Unix-like systems without a supported package manager | None | Script must verify the release manifest and SHA-256, but users must also trust the bootstrap download | Script can resolve a requested release and architecture | No payload overhead beyond the release archive | Medium-high support risk: no package-manager ownership, upgrades, rollback, or reliable uninstall |

The comparison uses the official [Scoop manifest specification](https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests),
[Chocolatey moderation process](https://docs.chocolatey.org/en-us/community-repository/moderation/),
[Snap publication guidance](https://snapcraft.io/docs/releasing-your-app/),
[Arch User Repository submission guidance](https://wiki.archlinux.org/title/AUR_submission_guidelines),
and [GitHub Container Registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).

## Decision

**Scoop is the next secondary channel**, tracked by
[#826](https://github.com/erikbra/pdfbox-net/issues/826). It consumes the
existing Windows ZIP without repackaging, verifies its existing SHA-256,
creates no extra runtime payload, and can be maintained as a project-owned
bucket. It also gives users a portable, non-admin alternative to WinGet.

Implementation waits for the stable Windows release contract: the executable
must be Authenticode-signed and the release manifest must identify that signing
state. At that point, the Scoop generator should consume only
`release-manifest.json`, install `unpdf.exe` through a shim, test a real
conversion, and verify update and uninstall behavior on Windows.

Chocolatey is deferred because it duplicates the Windows audience while adding
moderation and account maintenance. Snap and AUR are deferred until demand
justifies Linux-specific store work beyond APT and Homebrew. A container should
be reconsidered if unpdf gains a server or batch-processing workflow. A direct
install script is not selected because the release archives already provide a
clear fallback without encouraging an unaudited `curl | sh` path.

## Supported fallback

The direct GitHub Release archives remain a first-class installation path. Each
archive is self-contained, versioned, checksummed in `release-manifest.json`,
and accompanied by an SPDX SBOM and GitHub build-provenance attestation. Users
can verify and unpack an archive without any package manager; this fallback does
not require a separate installer script.
