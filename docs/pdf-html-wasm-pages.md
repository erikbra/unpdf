# unpdf Browser Preview

The `unpdf` browser PDF-to-HTML converter is published at
[erikbra.github.io/unpdf/wasm/](https://erikbra.github.io/unpdf/wasm/).
It reads the selected PDF locally and does not upload the document or extracted
content to a server.

The repository root at
[erikbra.github.io/unpdf/](https://erikbra.github.io/unpdf/) links to
the browser converter and the existing signed `unpdf` APT repository.

## Deployment

The `Publish PDF-to-HTML WASM Preview` workflow publishes the release build on
relevant changes to `main` and can also be run manually. It checks out the
existing `gh-pages` branch, replaces only the `wasm` directory, updates the
root landing and fallback pages, and preserves the `apt` repository. WASM and
APT publication share one concurrency lock so neither workflow can overwrite a
concurrent update from the other.

The deployment helper rewrites the application base URI to
`/unpdf/wasm/`. The shared 404 page loads the application shell for paths
below that base, allowing the Blazor router to handle direct navigation.

## Static-host behavior

Release builds fingerprint framework assets. GitHub Pages controls cache
headers and transfer compression, so the deployment does not assume that the
generated Brotli or Gzip sidecars will be selected by the host. The patched
HTML shell is deliberately served without a generated compression sidecar;
fingerprinted framework assets retain theirs.

The workflow verifies the deployed root and application documents byte for
byte, confirms the sample PDF is public, and checks that GitHub Pages serves a
framework binary with the `application/wasm` media type. The ordinary CI build
continues to run the Playwright conversion test that asserts no network
requests occur after a local PDF is selected.

Disabling or removing this workflow does not affect package build or test CI.
The current preview is public and has no telemetry, upload endpoint, or server
conversion component.
