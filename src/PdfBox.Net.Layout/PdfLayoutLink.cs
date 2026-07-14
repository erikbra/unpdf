namespace PdfBox.Net.Layout;

/// <summary>
/// Link annotation geometry and target metadata in normalized page coordinates.
/// </summary>
public sealed class PdfLayoutLink
{
    public PdfLayoutLink(
        int index,
        PdfLayoutRectangle bounds,
        PdfLayoutLinkKind kind,
        string? uri,
        string? destination,
        int? destinationPageNumber,
        IReadOnlyList<PdfLayoutRectangle> quadBounds)
    {
        Index = index;
        Bounds = bounds;
        Kind = kind;
        Uri = uri;
        Destination = destination;
        DestinationPageNumber = destinationPageNumber;
        QuadBounds = quadBounds.ToArray();
    }

    /// <summary>
    /// Gets the zero-based link index on the page.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the annotation bounds.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the target kind.
    /// </summary>
    public PdfLayoutLinkKind Kind { get; }

    /// <summary>
    /// Gets the URI target when <see cref="Kind"/> is <see cref="PdfLayoutLinkKind.Uri"/>.
    /// </summary>
    public string? Uri { get; }

    /// <summary>
    /// Gets a deterministic destination description for non-URI links.
    /// </summary>
    public string? Destination { get; }

    /// <summary>
    /// Gets the one-based destination page number when it can be resolved.
    /// </summary>
    public int? DestinationPageNumber { get; }

    /// <summary>
    /// Gets optional quad bounds when the annotation supplies quad points.
    /// </summary>
    public IReadOnlyList<PdfLayoutRectangle> QuadBounds { get; }
}
