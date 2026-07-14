namespace PdfBox.Net.Layout;

/// <summary>
/// Identifies a collected page paint operation in PDF content-stream order.
/// </summary>
public readonly record struct PdfLayoutPaintOperation(PdfLayoutPaintOperationKind Kind, int Index);

/// <summary>
/// The kind of layout object referenced by a paint operation.
/// </summary>
public enum PdfLayoutPaintOperationKind
{
    Image,
    Path
}
