namespace PdfBox.Net.Layout;

/// <summary>
/// A browser-representable PDF shading paint operation.
/// </summary>
public sealed class PdfLayoutShading
{
    public PdfLayoutShading(
        int index,
        int shadingType,
        PdfLayoutRectangle bounds,
        float startX,
        float startY,
        float startRadius,
        float endX,
        float endY,
        float endRadius,
        IReadOnlyList<PdfLayoutGradientStop> stops,
        IReadOnlyList<PdfLayoutShadingTriangle>? triangles = null)
    {
        Index = index;
        ShadingType = shadingType;
        Bounds = bounds;
        StartX = startX;
        StartY = startY;
        StartRadius = startRadius;
        EndX = endX;
        EndY = endY;
        EndRadius = endRadius;
        Stops = stops.ToArray();
        Triangles = triangles?.ToArray() ?? [];
    }

    /// <summary>
    /// Gets the zero-based paint-operation index on the page.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the PDF shading type. Browser output currently supports axial (2), radial (3),
    /// and tensor-product patch mesh (7) shadings.
    /// </summary>
    public int ShadingType { get; }

    /// <summary>
    /// Gets the page-space region to paint.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the normalized start point in page-space coordinates.
    /// </summary>
    public float StartX { get; }

    /// <summary>
    /// Gets the normalized start point in page-space coordinates.
    /// </summary>
    public float StartY { get; }

    /// <summary>
    /// Gets the start circle radius for radial shadings.
    /// </summary>
    public float StartRadius { get; }

    /// <summary>
    /// Gets the normalized end point in page-space coordinates.
    /// </summary>
    public float EndX { get; }

    /// <summary>
    /// Gets the normalized end point in page-space coordinates.
    /// </summary>
    public float EndY { get; }

    /// <summary>
    /// Gets the end circle radius for radial shadings.
    /// </summary>
    public float EndRadius { get; }

    /// <summary>
    /// Gets sampled color stops from the PDF shading function.
    /// </summary>
    public IReadOnlyList<PdfLayoutGradientStop> Stops { get; }

    /// <summary>
    /// Gets the tessellated mesh used for tensor-product patch shadings.
    /// </summary>
    public IReadOnlyList<PdfLayoutShadingTriangle> Triangles { get; }
}

/// <summary>
/// A color sample in a browser-representable PDF shading.
/// </summary>
public readonly record struct PdfLayoutGradientStop(float Offset, PdfLayoutColor Color);

/// <summary>
/// A solid-color triangle approximating part of a PDF patch mesh.
/// </summary>
public readonly record struct PdfLayoutShadingTriangle(
    float X1,
    float Y1,
    float X2,
    float Y2,
    float X3,
    float Y3,
    PdfLayoutColor Color);
