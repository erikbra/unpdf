# unpdf APT signing-key operations

## Current identity

- Fingerprint: `1C65 ED46 DC97 C055 EB67 7577 22F9 EC17 9833 6E6A`
- Algorithm: Ed25519 signing key
- Created: 2026-07-13
- Expires: 2028-07-12
- GitHub environment: `unpdf-apt-production`, restricted to `main` and
  `unpdf-v*` release tags

The armored public key, binary keyring, and ungrouped fingerprint are versioned
under `packaging/debian`. The Pages publication repeats all three under `/apt`.
Always compare the complete fingerprint from at least two project-controlled
locations before trusting a replacement key.

## Private backup

The encrypted secret-key backup is stored outside the repository at
`~/.config/unpdf/apt-signing-key-backup.asc` on the release owner's machine,
mode `0600`. Its passphrase is stored in the macOS Keychain under service
`unpdf-apt-signing-key`; it is not stored beside the backup. The same encrypted
key and passphrase are separate secrets in the protected GitHub environment.

Before relying on a backup, import it into a temporary `GNUPGHOME`, sign a test
file using loopback pinentry, and verify that signature with the committed
public key. Never print the passphrase or export an unencrypted private key.

## Planned rotation

Begin rotation at least 90 days before expiration:

1. Generate a new passphrase-protected Ed25519 signing key and make an offline
   encrypted backup.
2. Publish a transition keyring containing both old and new public keys while
   repository metadata is still signed by the old key.
3. Announce both complete fingerprints in the repository, release notes, and
   Pages instructions.
4. Replace the production environment secrets, sign both `stable` and
   `preview` with the new key, and run public amd64 and arm64 install tests.
5. Keep both public keys available through the overlap period. Remove the old
   key only after supported clients have had time to refresh the keyring.

Changing a fingerprint without the overlap and public-install checks is a
release-blocking failure.

## Emergency revocation

If the private key or passphrase may be compromised:

1. Disable the `Publish unpdf APT Repository` workflow and remove the production
   environment secrets.
2. Use the encrypted offline backup to generate and publish a revocation
   certificate for the compromised key.
3. Generate a replacement key, publish the new key and fingerprint through a
   separately reviewed main-branch change, and update the environment secrets.
4. Re-sign both repository suites with the replacement key and run the public
   install matrix before re-enabling release-triggered publication.
5. Publish a security notice identifying the affected fingerprint and dates.

Do not continue serving newly generated metadata under a key whose custody is
uncertain.
