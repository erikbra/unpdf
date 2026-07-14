namespace PdfBox.Net.Layout;

/// <summary>
/// A collected vector path paint operation in normalized page coordinates.
/// </summary>
public sealed class PdfLayoutPath
{
    public PdfLayoutPath(
        int index,
        IReadOnlyList<PdfLayoutPathCommand> commands,
        PdfLayoutRectangle bounds,
        PdfLayoutColor? fillColor,
        PdfLayoutStrokeStyle? stroke,
        int? fillRule,
        bool usesShapeAlpha = false,
        IReadOnlyList<string>? colorantNames = null,
        PdfLayoutRectangle? clipBounds = null,
        bool usesSoftMask = false,
        IReadOnlyList<PdfLayoutClipPath>? clipPaths = null)
    {
        Index = index;
        Commands = commands.ToArray();
        Bounds = bounds;
        FillColor = fillColor;
        Stroke = stroke;
        FillRule = fillRule;
        UsesShapeAlpha = usesShapeAlpha;
        ColorantNames = colorantNames?.ToArray() ?? [];
        ClipBounds = clipBounds;
        UsesSoftMask = usesSoftMask;
        ClipPaths = clipPaths?.ToArray() ?? [];
    }

    /// <summary>
    /// Gets the zero-based path paint operation index on the page.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets path commands in normalized page coordinates.
    /// </summary>
    public IReadOnlyList<PdfLayoutPathCommand> Commands { get; }

    /// <summary>
    /// Gets the path command bounds.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the fill color when the path is filled.
    /// </summary>
    public PdfLayoutColor? FillColor { get; }

    /// <summary>
    /// Gets the stroke style when the path is stroked.
    /// </summary>
    public PdfLayoutStrokeStyle? Stroke { get; }

    /// <summary>
    /// Gets the PDF fill rule when the path is filled. 0 is even-odd, 1 is non-zero winding.
    /// </summary>
    public int? FillRule { get; }

    /// <summary>
    /// Gets whether the path uses PDF shape-alpha compositing.
    /// </summary>
    /// <remarks>
    /// SVG has no equivalent to the PDF alpha-source flag. Rendering such a path as a
    /// normal SVG opacity can create a solid visual artifact, so HTML exporters can use
    /// this flag to select a suitable fallback.
    /// </remarks>
    public bool UsesShapeAlpha { get; }

    /// <summary>
    /// Gets whether this path is painted through a PDF soft mask that cannot be flattened into an independent SVG path.
    /// </summary>
    public bool UsesSoftMask { get; }

    /// <summary>
    /// Gets explicit Separation or DeviceN colorants painted by this path.
    /// </summary>
    public IReadOnlyList<string> ColorantNames { get; }

    /// <summary>
    /// Gets the effective rectangular clip for this path when it differs from its containing group.
    /// </summary>
    public PdfLayoutRectangle? ClipBounds { get; }

    /// <summary>
    /// Gets the exact clipping paths introduced within this path's containing form.
    /// </summary>
    /// <remarks>
    /// Each entry intersects the preceding entry. Keeping the operations separate preserves
    /// PDF clipping semantics when the path is emitted as SVG.
    /// </remarks>
    public IReadOnlyList<PdfLayoutClipPath> ClipPaths { get; }

    /// <summary>
    /// Gets whether the path has a fill paint operation.
    /// </summary>
    public bool IsFilled => FillColor.HasValue;

    /// <summary>
    /// Gets whether the path has a stroke paint operation.
    /// </summary>
    public bool IsStroked => Stroke is not null;
}
