namespace PdfBox.Net.Layout;

/// <summary>
/// Positioned text item captured from the PDF content stream.
/// </summary>
public sealed record PdfTextGlyph(
    string Text,
    string FontName,
    float FontSize,
    float Direction,
    PdfLayoutRectangle Bounds,
    PdfLayoutColor Color)
{
    /// <summary>
    /// Gets the glyph bounds in normalized page coordinates before direction-adjusted reading order normalization.
    /// </summary>
    public PdfLayoutRectangle PageBounds { get; init; } = Bounds;

    /// <summary>
    /// Gets the optional PDF glyph outline in normalized page coordinates when the embedded font cannot be used by a browser.
    /// </summary>
    public IReadOnlyList<PdfLayoutPathCommand>? Outline { get; init; }

    /// <summary>
    /// Gets whether the glyph's exact PDF font has been exported as a browser-loadable asset.
    /// </summary>
    public bool UsesBrowserFontAsset { get; init; }

    /// <summary>
    /// Gets whether the PDF text rendering mode paints this glyph.
    /// </summary>
    public bool IsPainted { get; init; } = true;
}
