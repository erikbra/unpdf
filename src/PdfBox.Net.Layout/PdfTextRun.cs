namespace PdfBox.Net.Layout;

/// <summary>
/// Consecutive glyphs on a line with compatible text state.
/// </summary>
public sealed class PdfTextRun
{
    public PdfTextRun(
        string text,
        string fontName,
        float fontSize,
        float direction,
        PdfLayoutRectangle bounds,
        PdfLayoutColor color,
        IReadOnlyList<PdfTextGlyph> glyphs,
        PdfLayoutRectangle? pageBounds = null,
        PdfTextShadow? shadow = null)
    {
        Text = text;
        FontName = fontName;
        FontSize = fontSize;
        Direction = direction;
        Bounds = bounds;
        Color = color;
        Glyphs = glyphs.ToArray();
        PageBounds = pageBounds ?? bounds;
        Shadow = shadow;
    }

    /// <summary>
    /// Gets the run text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the PDF font name used by the run.
    /// </summary>
    public string FontName { get; }

    /// <summary>
    /// Gets the rendered font size.
    /// </summary>
    public float FontSize { get; }

    /// <summary>
    /// Gets the text direction in degrees.
    /// </summary>
    public float Direction { get; }

    /// <summary>
    /// Gets the run bounds.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the run bounds in normalized page coordinates before direction-adjusted reading order normalization.
    /// </summary>
    public PdfLayoutRectangle PageBounds { get; }

    /// <summary>
    /// Gets the resolved fill color used by the run.
    /// </summary>
    public PdfLayoutColor Color { get; }

    /// <summary>
    /// Gets the glyphs that make up the run.
    /// </summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }

    /// <summary>
    /// Gets a drop shadow derived from a soft-masked transparency group painted behind this run, when available.
    /// </summary>
    public PdfTextShadow? Shadow { get; internal set; }

    /// <summary>
    /// Gets whether every glyph in the run uses an exported browser font asset.
    /// </summary>
    public bool UsesBrowserFontAsset => Glyphs.Count > 0 && Glyphs.All(static glyph => glyph.UsesBrowserFontAsset);

    /// <summary>
    /// Gets the page marked-content identifier shared by this run, when present.
    /// </summary>
    public int? MarkedContentId => Glyphs.Count > 0 &&
        Glyphs.All(glyph => glyph.MarkedContentId == Glyphs[0].MarkedContentId)
            ? Glyphs[0].MarkedContentId
            : null;
}
