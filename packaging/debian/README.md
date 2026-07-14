# Debian and APT distribution

The repository builder consumes the immutable unpdf release manifest, verifies
both Linux archives, builds `amd64` and `arm64` packages, generates architecture
indices, and signs `InRelease` and `Release.gpg`:

```console
python3 eng/build_unpdf_apt_repository.py \
  --manifest https://github.com/erikbra/unpdf/releases/download/unpdf-v4.0.0-preview.1/release-manifest.json \
  --output artifacts/apt-repository \
  --suite preview \
  --gpg-key <fingerprint> \
  --gpg-passphrase-file <path>
```

## Public repository

GitHub Pages hosts the repository at
`https://erikbra.github.io/unpdf/apt`. Install the preview channel with:

```console
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://erikbra.github.io/unpdf/apt/unpdf-archive-keyring.gpg \
  | sudo tee /etc/apt/keyrings/unpdf.gpg >/dev/null
curl -fsSL https://erikbra.github.io/unpdf/apt/unpdf-preview.sources \
  | sudo tee /etc/apt/sources.list.d/unpdf.sources >/dev/null
sudo apt update
sudo apt install unpdf
```

Verify the downloaded key before trusting it. Its fingerprint is:

```text
1C65 ED46 DC97 C055 EB67 7577 22F9 EC17 9833 6E6A
```

The publish workflow keeps `stable` and `preview` indices independent under
the `/apt` subtree so a later browser application can use the Pages root. The
private key is available only to the `unpdf-apt-production` environment and
that environment permits deployment only from `main` and `unpdf-v*` release
tags. Pull-request tests use an ephemeral key that is discarded with the
runner.

APT authenticates the signed repository metadata, which contains SHA-256 hashes
for every `.deb`. Individual Debian archives are not separately `dpkg-sig`
signed; this follows normal APT repository trust semantics.

See [KEY-ROTATION.md](KEY-ROTATION.md) for backup, expiration, rotation, and
revocation procedures.
