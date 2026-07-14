namespace PdfBox.Net.Html;

/// <summary>
/// Controls how inferred semantic text is arranged across source PDF pages.
/// </summary>
public enum PdfHtmlSemanticPageMode
{
    /// <summary>
    /// Emit one fixed-size HTML section for each source PDF page.
    /// </summary>
    FixedPages,

    /// <summary>
    /// Emit one continuous semantic flow with soft markers at source PDF page boundaries.
    /// </summary>
    ContinuousFlow
}
