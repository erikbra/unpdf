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
| Image placements and export | Supported through passthrough or decoding | Planned in #50 | Planned in #48/#98 |
| CMYK/YCCK, ICC and unusual color spaces | Provider conversion where supported | Diagnostic/degraded | Planned |
| JPX/JPEG2000 and TIFF | Provider decode where supported | Diagnostic/degraded | Planned |
| Annotation appearances | Raster fallback where supported | Disabled | Planned |
| Image masks and soft masks | Stencil and soft-mask image assets | Diagnostic/degraded | Planned |
| Transparency groups and blend modes | Localized raster fallback for compact knockout, complex-blend, and soft-masked vector/text groups | Diagnostic/degraded | Planned |
| Complex clipping, shadings and overprint | Partial with fallback for some cases | Partial with diagnostics | Planned |
| Tagged structure and accessible semantics | Inferred/preserved where supported | Same | Planned |
| Encryption | Supported where PdfBox.Net can open the document | Same | Planned |
| Digital-signature validation | Not provided | Not provided | Not provided |

“Partial” means representative operations work but unsupported variants remain
and must produce diagnostics rather than silent content loss. The CLI is the
highest-fidelity current product; it is not full PDF conformance.
