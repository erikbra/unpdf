namespace PdfBox.Net.Html;

/// <summary>
/// Controls how text is emitted in generated HTML.
/// </summary>
public enum PdfHtmlTextMode
{
    /// <summary>
    /// Emit each positioned PDF text run as an absolutely positioned span.
    /// </summary>
    FixedLayout,

    /// <summary>
    /// Emit inferred semantic text elements such as headings, paragraphs, author blocks, footnotes, and footers.
    /// </summary>
    Semantic
}
