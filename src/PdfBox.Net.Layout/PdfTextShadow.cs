namespace PdfBox.Net.Layout;

/// <summary>
/// A browser-representable drop shadow derived from PDF transparency graphics painted behind a text run.
/// </summary>
public sealed record PdfTextShadow(
    float OffsetX,
    float OffsetY,
    float BlurRadius,
    PdfLayoutColor Color);
