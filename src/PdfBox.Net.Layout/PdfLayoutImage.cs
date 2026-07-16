namespace PdfBox.Net.Layout;

/// <summary>
/// Positioned image placement on a PDF page.
/// </summary>
public sealed class PdfLayoutImage
{
    public PdfLayoutImage(
        int index,
        string assetId,
        PdfLayoutImageKind kind,
        PdfLayoutRectangle bounds,
        PdfLayoutTransform transform,
        int intrinsicWidth,
        int intrinsicHeight,
        int bitsPerComponent,
        string? colorSpaceName,
        bool interpolate,
        string? sourceName,
        bool overprint = false,
        IReadOnlyList<string>? colorantNames = null,
        IReadOnlyList<PdfLayoutClipPath>? clipPaths = null,
        PdfLayoutColor? overprintCompositeColor = null,
        bool multiplyOverprint = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        Index = index;
        AssetId = assetId;
        Kind = kind;
        Bounds = bounds;
        Transform = transform;
        IntrinsicWidth = intrinsicWidth;
        IntrinsicHeight = intrinsicHeight;
        BitsPerComponent = bitsPerComponent;
        ColorSpaceName = colorSpaceName;
        Interpolate = interpolate;
        SourceName = sourceName;
        Overprint = overprint;
        ColorantNames = colorantNames?.ToArray() ?? [];
        ClipPaths = clipPaths?.ToArray() ?? [];
        OverprintCompositeColor = overprintCompositeColor;
        MultiplyOverprint = multiplyOverprint;
    }

    /// <summary>
    /// Gets the zero-based image placement index on the page.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets a deterministic image asset identifier for conversion outputs.
    /// </summary>
    public string AssetId { get; }

    /// <summary>
    /// Gets the PDF image source kind.
    /// </summary>
    public PdfLayoutImageKind Kind { get; }

    /// <summary>
    /// Gets the normalized image bounds on the visible page.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the current transformation matrix used to place the image.
    /// </summary>
    public PdfLayoutTransform Transform { get; }

    /// <summary>
    /// Gets the intrinsic image width in pixels or samples.
    /// </summary>
    public int IntrinsicWidth { get; }

    /// <summary>
    /// Gets the intrinsic image height in pixels or samples.
    /// </summary>
    public int IntrinsicHeight { get; }

    /// <summary>
    /// Gets the number of bits per color component.
    /// </summary>
    public int BitsPerComponent { get; }

    /// <summary>
    /// Gets the PDF color space name when it can be resolved.
    /// </summary>
    public string? ColorSpaceName { get; }

    /// <summary>
    /// Gets whether image interpolation is requested by the PDF.
    /// </summary>
    public bool Interpolate { get; }

    /// <summary>
    /// Gets the PDF resource name when the image was resolved from a named resource.
    /// </summary>
    public string? SourceName { get; }

    /// <summary>
    /// Gets whether the image is painted with PDF overprint enabled.
    /// </summary>
    public bool Overprint { get; }

    /// <summary>
    /// Gets explicit Separation or DeviceN colorants painted by the image.
    /// </summary>
    public IReadOnlyList<string> ColorantNames { get; }

    /// <summary>
    /// Gets the exact opaque process-color result for a uniform DeviceN image composed over a matching path.
    /// </summary>
    public PdfLayoutColor? OverprintCompositeColor { get; }

    /// <summary>
    /// Gets whether a black-only process image should multiply with its HTML backdrop to preserve PDF overprint.
    /// </summary>
    public bool MultiplyOverprint { get; }

    /// <summary>
    /// Gets the exact clipping paths applied to this image placement.
    /// </summary>
    public IReadOnlyList<PdfLayoutClipPath> ClipPaths { get; }
}
