namespace PdfBox.Net.Layout;

/// <summary>
/// Identifies the PDF image source represented by a layout image.
/// </summary>
public enum PdfLayoutImageKind
{
    /// <summary>
    /// Image XObject invoked through the PDF Do operator.
    /// </summary>
    XObject,

    /// <summary>
    /// Inline image embedded directly in a content stream.
    /// </summary>
    InlineImage,

    /// <summary>
    /// Rasterized annotation appearance placed over the page content.
    /// </summary>
    AnnotationAppearance,

    /// <summary>
    /// Rasterized fallback for a compact PDF transparency group whose compositing cannot be represented by
    /// independent HTML SVG paths.
    /// </summary>
    TransparencyGroupFallback
}
