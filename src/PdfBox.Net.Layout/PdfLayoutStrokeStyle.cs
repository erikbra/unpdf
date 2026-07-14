namespace PdfBox.Net.Layout;

/// <summary>
/// Stroke style resolved from a PDF graphics state.
/// </summary>
public sealed class PdfLayoutStrokeStyle
{
    public PdfLayoutStrokeStyle(
        PdfLayoutColor color,
        float width,
        int lineCap,
        int lineJoin,
        float miterLimit,
        IReadOnlyList<float> dashArray,
        float dashPhase)
    {
        Color = color;
        Width = width;
        LineCap = lineCap;
        LineJoin = lineJoin;
        MiterLimit = miterLimit;
        DashArray = dashArray.ToArray();
        DashPhase = dashPhase;
    }

    /// <summary>
    /// Gets the stroke color.
    /// </summary>
    public PdfLayoutColor Color { get; }

    /// <summary>
    /// Gets the stroke width in normalized page units.
    /// </summary>
    public float Width { get; }

    /// <summary>
    /// Gets the PDF line cap value.
    /// </summary>
    public int LineCap { get; }

    /// <summary>
    /// Gets the PDF line join value.
    /// </summary>
    public int LineJoin { get; }

    /// <summary>
    /// Gets the miter limit.
    /// </summary>
    public float MiterLimit { get; }

    /// <summary>
    /// Gets the dash pattern array.
    /// </summary>
    public IReadOnlyList<float> DashArray { get; }

    /// <summary>
    /// Gets the dash phase.
    /// </summary>
    public float DashPhase { get; }
}
