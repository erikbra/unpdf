namespace PdfBox.Net.Layout;

/// <summary>
/// A PDF clipping path retained in normalized page coordinates.
/// </summary>
public sealed class PdfLayoutClipPath
{
    public PdfLayoutClipPath(
        IReadOnlyList<PdfLayoutPathCommand> commands,
        PdfLayoutRectangle bounds,
        int windingRule)
    {
        Commands = commands.ToArray();
        Bounds = bounds;
        WindingRule = windingRule;
    }

    /// <summary>
    /// Gets the clipping path commands in normalized page coordinates.
    /// </summary>
    public IReadOnlyList<PdfLayoutPathCommand> Commands { get; }

    /// <summary>
    /// Gets the conservative bounds of the clipping path.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the PDF winding rule. 0 is even-odd and 1 is non-zero winding.
    /// </summary>
    public int WindingRule { get; }
}
