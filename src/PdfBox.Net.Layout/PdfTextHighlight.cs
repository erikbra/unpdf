namespace PdfBox.Net.Layout;

/// <summary>
/// Text glyphs confidently matched to a source highlight rectangle.
/// </summary>
public sealed class PdfTextHighlight
{
    public PdfTextHighlight(
        int sourcePathIndex,
        PdfLayoutRectangle bounds,
        PdfLayoutColor color,
        IReadOnlyList<PdfTextGlyph> glyphs)
    {
        SourcePathIndex = sourcePathIndex;
        Bounds = bounds;
        Color = color;
        Glyphs = glyphs.ToArray();
    }

    /// <summary>
    /// Gets the index of the filled source path that supplied the highlight geometry.
    /// </summary>
    public int SourcePathIndex { get; }

    /// <summary>
    /// Gets the source highlight bounds in normalized page coordinates.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the resolved source highlight color.
    /// </summary>
    public PdfLayoutColor Color { get; }

    /// <summary>
    /// Gets the text glyphs covered by the source highlight.
    /// </summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }
}
