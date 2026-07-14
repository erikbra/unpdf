namespace PdfBox.Net.Layout;

/// <summary>
/// Kind of link target represented by a layout link annotation.
/// </summary>
public enum PdfLayoutLinkKind
{
    /// <summary>
    /// The link target could not be classified.
    /// </summary>
    Unknown,

    /// <summary>
    /// The link targets an external URI.
    /// </summary>
    Uri,

    /// <summary>
    /// The link targets a PDF destination.
    /// </summary>
    Destination
}
