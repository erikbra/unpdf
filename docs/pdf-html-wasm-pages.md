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

## Privacy and telemetry policy

User-selected PDFs, file names, extracted text, generated HTML, diagnostics,
and preview contents remain in the browser. The application performs no request
during an uploaded-file conversion; Playwright records the browser request
stream after selection and fails if any request occurs. The built-in sample is
the only conversion input fetched from the host.

Telemetry is absent by default. Local exception messages are displayed only in
the current page. If operational telemetry is introduced later, it may contain
only content-independent application version, stable error code, duration,
coarse size bucket, and browser capability flags. It must never contain PDF
bytes, file names, URLs from the PDF, extracted text, generated HTML, preview
DOM, free-form exception/diagnostic messages, or persistent document/user IDs.

The browser CSP limits script connections to the application's own origin, and
the conversion test provides a second independent no-request assertion.

## Browser security policy

Every release publish runs `eng/harden_wasm_site.py`. It hashes the generated
inline import map, replaces the development CSP with a policy that permits the
Blazor runtime's narrow `'wasm-unsafe-eval'` capability but not general
`'unsafe-eval'` or inline script, restricts connections to `'self'`, denies
objects and form submission, and sets a no-referrer policy. CI serves the
hardened static output to Playwright so a CSP that blocks startup or conversion
cannot be deployed unnoticed.

The publish output also contains two equivalent production-host configurations:

- `_headers` for Cloudflare Pages;
- `staticwebapp.config.json` for Azure Static Web Apps.

They add the CSP as an HTTP response header with `frame-ancestors 'none'`, plus
`X-Content-Type-Options`, `X-Frame-Options`, referrer, permissions, and
cross-origin boundary headers. Fingerprinted framework files receive an
immutable one-year cache policy while `index.html` remains revalidatable.

GitHub Pages does not apply these provider configuration files or allow this
repository to set arbitrary response headers. The deployed preview therefore
uses the checked CSP meta policy and verifies HTTPS content types, no cookies,
and the exact staged document. A meta CSP cannot enforce `frame-ancestors`, so
Pages remains the demo/preview host; production deployment must use the emitted
Cloudflare/Azure response-header configuration or an equivalent CDN/server
configuration.

Primary guidance:

- [Microsoft Blazor CSP guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-10.0)
- [MDN `frame-ancestors`](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/frame-ancestors)
- [MDN CSP `wasm-unsafe-eval`](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/script-src#unsafe_webassembly_execution)

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

The browser-adaptive default was compared with the last browser-lite commit
(`7851857`) on 2026-07-17. Both builds used Release publishing with .NET
10.0.10 on macOS arm64 and the same Playwright Chromium uploaded-PDF test. The
timings are single local cold runs, so they show direction only; CI ratchets are
the regression authority.

| Measure | Browser lite | Browser adaptive | Change |
| --- | ---: | ---: | ---: |
| Framework payload, raw | 13,935,182 bytes | 18,466,791 bytes | +32.5% |
| Framework payload, Brotli | 4,010,557 bytes | 5,236,230 bytes | +30.6% |
| Cold document load | 37 ms | 24 ms | -13 ms |
| Deterministic text conversion | 151 ms / 217 ms wall | 151 ms / 229 ms wall | 0 ms / +12 ms |
| Image-heavy browser fixture | image omitted/degraded | image exported and displayed | fidelity improved |

The roughly 1.17 MiB compressed cost buys the browser Skia backend and its
browser-native assets. It does not materially change the measured text-only
conversion, while the browser image smoke test now verifies the fidelity gain
that browser lite could not provide. These numbers close the comparison in
[unpdf #48](https://github.com/erikbra/unpdf/issues/48); future dependency
changes remain governed by the checked-in payload and timing baselines.

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
a core random-access source backed by browser `Blob.slice()` calls. The UI
rejects files larger than 32 MiB before opening their stream, clears prior
preview output before a new conversion, and disposes its cancellation source at
completion or component teardown. Automated browser tests cover the oversize
path and cancellation of an in-flight input request, including re-enabling the
controls afterwards.

Cancellation is honored while reading and between conversion stages;
interrupting synchronous PDFBox extraction itself requires moving extraction to
a worker or adding cancellation points in the core parser. True bounded-memory
input, incremental page/output processing, and worker evaluation remain tracked
by [unpdf #99](https://github.com/erikbra/unpdf/issues/99).

## Performance and offline behavior

`eng/wasm-performance-baseline.json` ratchets gross cold-load and deterministic
conversion ceilings in Playwright. The payload ratchet separately records every
framework asset's raw and Brotli bytes. CI fails if startup, conversion, network
request count, or payload exceeds its accepted budget. The intentionally broad
timing ceilings catch major regressions across shared CI hardware; stable hosted
percentiles should tighten them later.

Offline/PWA caching is deliberately disabled for now. Framework assets already
benefit from browser and immutable-host caching, while a service worker would
add update/version complexity and risk retaining generated document resources.
No selected PDF or generated output is placed in Cache Storage, IndexedDB, or a
service-worker cache.
