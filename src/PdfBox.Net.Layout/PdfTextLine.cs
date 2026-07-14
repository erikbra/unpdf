namespace PdfBox.Net.Layout;

/// <summary>
/// Reading-order text line assembled from text runs.
/// </summary>
public sealed class PdfTextLine
{
    public PdfTextLine(string text, PdfLayoutRectangle bounds, IReadOnlyList<PdfTextRun> runs)
    {
        Text = text;
        Bounds = bounds;
        Runs = runs.ToArray();
    }

    /// <summary>
    /// Gets the line text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the line bounds.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the runs on the line.
    /// </summary>
    public IReadOnlyList<PdfTextRun> Runs { get; }
}
