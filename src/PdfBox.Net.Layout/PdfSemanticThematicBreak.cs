namespace PdfBox.Net.Layout;

/// <summary>
/// Source alignment retained for a semantic thematic break.
/// </summary>
public enum PdfSemanticThematicBreakAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Source paint metadata for a horizontal rule promoted into document flow.
/// </summary>
public sealed class PdfSemanticThematicBreak
{
    public PdfSemanticThematicBreak(
        int sourcePathIndex,
        float thickness,
        PdfLayoutColor color,
        PdfSemanticThematicBreakAlignment alignment)
    {
        SourcePathIndex = sourcePathIndex;
        Thickness = MathF.Max(0.01f, thickness);
        Color = color;
        Alignment = alignment;
    }

    /// <summary>
    /// Gets the page path consumed by this semantic element.
    /// </summary>
    public int SourcePathIndex { get; }

    /// <summary>
    /// Gets the source rule thickness in normalized page units.
    /// </summary>
    public float Thickness { get; }

    /// <summary>
    /// Gets the source paint color.
    /// </summary>
    public PdfLayoutColor Color { get; }

    /// <summary>
    /// Gets the rule alignment relative to its surrounding flow region.
    /// </summary>
    public PdfSemanticThematicBreakAlignment Alignment { get; }
}
