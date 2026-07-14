using PdfBox.Net.Layout;

namespace PdfBox.Net.Html;

/// <summary>
/// Options for HTML conversion.
/// </summary>
public sealed class PdfHtmlOptions
{
    /// <summary>
    /// Gets or sets the generated document title.
    /// </summary>
    public string Title { get; init; } = "PDF document";

    /// <summary>
    /// Gets or sets the CSS asset path referenced by the generated HTML.
    /// </summary>
    public string CssPath { get; init; } = "assets/pdfbox-net-fixed.css";

    /// <summary>
    /// Gets or sets the scalar applied to emitted page and text coordinates.
    /// </summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>
    /// Gets or sets how text is emitted in the generated HTML.
    /// </summary>
    public PdfHtmlTextMode TextMode { get; init; } = PdfHtmlTextMode.FixedLayout;

    /// <summary>
    /// Gets or sets the semantic extraction options used when <see cref="TextMode"/> is <see cref="PdfHtmlTextMode.Semantic"/>.
    /// </summary>
    public PdfSemanticExtractionOptions SemanticExtractionOptions { get; init; } = new();

    /// <summary>
    /// Gets or sets how inferred semantic text is arranged across source PDF pages.
    /// </summary>
    public PdfHtmlSemanticPageMode SemanticPageMode { get; init; } = PdfHtmlSemanticPageMode.FixedPages;
}
