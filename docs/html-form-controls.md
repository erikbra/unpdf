# HTML Form Controls

`PdfBox.Net.Layout` extracts supported AcroForm terminal fields as one
`PdfLayoutFormControl` per visible widget. Text fields, check boxes, radio
buttons, combo boxes, list boxes, and signature fields retain normalized widget
geometry, stable fully qualified names, current and default values, choice
options, and read-only/required flags. `PdfBox.Net.Html` emits native HTML
controls and omits a matching rasterized widget appearance so field text is not
duplicated.

Positioned controls keep the authored widget rectangle as their interaction
area and clip browser paint to that rectangle. Their editing surface remains
transparent so an oversized PDF widget cannot erase an intersecting prompt or
vector rule. Focus indicators are drawn inside the widget bounds.

## XFA limitation

XFA packets are not parsed or reconstructed as HTML controls. Pure XFA forms
and XFA-only behavior such as dynamic layout, calculations, validation scripts,
and event actions remain visual fallbacks using the PDF page content and any
available annotation appearances. Hybrid documents still expose supported
AcroForm widgets semantically. Layout extraction emits the
`xfa-semantic-forms-unsupported` warning whenever an XFA entry is present so
callers can identify this fallback explicitly.
