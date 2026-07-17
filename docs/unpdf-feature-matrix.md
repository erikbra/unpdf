# unpdf feature matrix

This matrix describes product behavior, not every valid combination allowed by
the PDF specification. A rendering fallback improves visual fidelity but does
not make semantic HTML structurally identical to the source PDF.

| Capability | CLI | Browser lite | Browser rendering |
|---|---|---|---|
| Text and semantic grouping | Supported | Supported | Planned |
| Links and vector paths | Supported with diagnostics for complex operations | Same | Planned |
| Semantic AcroForm controls | Supported plus appearance fallback; XFA is degraded | Supported; XFA is degraded | Planned |
| Browser-safe TrueType/OpenType fonts | Supported | Supported | Planned |
| Raw Type 1/CFF fonts | SVG outline or substitute fallback | Same | Planned |
| Image placements and export | Supported through passthrough or decoding | Browser-safe passthrough; diagnostic/degraded otherwise | Supported through browser backend |
| CMYK/YCCK, ICC and unusual color spaces | Provider conversion where supported | Diagnostic/degraded | Planned |
| JPX/JPEG2000 and TIFF | Provider decode where supported | Diagnostic/degraded | Planned |
| Annotation appearances | Raster fallback where supported | Backend-required diagnostic | Planned |
| Image masks and soft masks | Stencil and soft-mask image assets | Diagnostic/degraded | Planned |
| Transparency groups and blend modes | Localized raster fallback for compact knockout, complex-blend, and soft-masked vector/text groups | Diagnostic/degraded | Planned |
| Complex clipping, shadings and overprint | Partial with fallback for some cases | Partial with diagnostics | Planned |
| Tagged structure and accessible semantics | Inferred/preserved where supported | Same | Planned |
| Encryption | Supported where PdfBox.Net can open the document | Same | Planned |
| Digital-signature validation | Not provided | Not provided | Not provided |

“Partial” means representative operations work but unsupported variants remain
and must produce diagnostics rather than silent content loss. The CLI is the
highest-fidelity current product; it is not full PDF conformance.

Browser-lite image export never registers or silently invokes a rendering
backend. `PdfImageExportPolicy.Degraded` preserves placements and reports an
omitted asset, `Strict` fails on the first unsupported requested asset, and
`BackendRequired` fails before asset extraction when no provider is registered.
Ordinary RGB JPEG streams are preserved byte-for-byte without a backend.

Stable backend-free diagnostics include
`image-asset-cmyk-backend-required`, `image-asset-jpx-backend-required`,
`image-asset-tiff-backend-required`, `image-asset-icc-backend-required`,
`annotation-appearance-backend-missing`, and
`transparency-group-rasterization-backend-missing`.
