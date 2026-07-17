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
publishes the `browser-wasm` application independently, then runs the
Playwright conversion test against a deterministic one-page fixture. The test
asserts expected text and page structure, records conversion duration, and
states in its output that the PDF never leaves the browser after selection.

Both CI and the Pages publication produce `wasm-payload-report` artifacts with
machine-readable JSON and a Markdown summary of every framework asset's raw and
Brotli size. The checked-in `eng/wasm-payload-baseline.json` is a ratchet:
unexpected new assets or growth beyond its small build-noise allowance fail
the workflow. When an intentional dependency or application change affects the
payload, publish locally and review the report before updating the baseline:

```sh
dotnet publish samples/PdfBox.Net.Html.Wasm/PdfBox.Net.Html.Wasm.csproj \
  --configuration Release --output artifacts/wasm-publish
python3 eng/wasm_payload_report.py \
  --publish-directory artifacts/wasm-publish/wwwroot \
  --baseline eng/wasm-payload-baseline.json \
  --update-baseline
```

Baseline growth should be committed with its explanation. Durable reductions
should ratchet the baseline down so the saved bytes cannot silently return.

Disabling or removing this workflow does not affect package build or test CI.
The current preview is public and has no telemetry, upload endpoint, or server
conversion component.

## Rendering backends and loading

The browser build registers `PdfBox.Net.SkiaSharp` and links
`SkiaSharp.NativeAssets.WebAssembly`. This requires the .NET `wasm-tools`
workload at publish time. Skia handles page rendering and browser-safe image
encoding without loading a desktop native library.

Image extraction uses the degraded policy by default: directly usable image
streams are preserved, Skia converts images it supports, and an unavailable
codec produces a stable diagnostic instead of aborting the whole document.
Callers that require every requested image can select the strict policy.

The Skia static archive is linked into the main application WebAssembly module,
so Blazor managed-assembly lazy loading cannot defer its native bytes. A truly
optional Skia download would require a separately compiled WebAssembly side
module or separate browser deployments.

ImageMagick is intentionally not part of initial startup. The
`@imagemagick/magick-wasm` package exposes a JavaScript module and a separate
`magick.wasm` payload, which makes dynamic import practical for uncommon codecs
that direct passthrough and Skia cannot handle. It is not binary-compatible
with the .NET ImageMagick rendering backend; browser support needs an async
JavaScript interop adapter before extraction retries the failed image.

## Browser memory model

The file picker reads into one exactly sized byte array rather than copying via
`MemoryStream` and `ToArray`. PDFBox still requires random access to the PDF,
and browser file streams are not a general seekable backing store, so the input
currently remains resident for conversion. True bounded-memory input requires
a core random-access source backed by browser `Blob.slice()` calls. Cancellation
is honored while reading and between conversion stages; interrupting synchronous
PDFBox extraction itself requires moving extraction to a worker or adding
cancellation points in the core parser.
