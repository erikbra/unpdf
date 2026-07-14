namespace PdfBox.Net.Layout;

/// <summary>
/// Paragraph-like text block assembled from lines.
/// </summary>
public sealed class PdfTextBlock
{
    public PdfTextBlock(string text, PdfLayoutRectangle bounds, IReadOnlyList<PdfTextLine> lines)
    {
        Text = text;
        Bounds = bounds;
        Lines = lines.ToArray();
    }

    /// <summary>
    /// Gets the block text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the block bounds.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the lines in the block.
    /// </summary>
    public IReadOnlyList<PdfTextLine> Lines { get; }
}
