using PdfBox.Net.Layout;

namespace PdfBox.Net.Markdown;

/// <summary>
/// Controls tagged-first Markdown conversion.
/// </summary>
public sealed class PdfMarkdownOptions
{
    /// <summary>
    /// Gets or sets the semantic extraction options used by the conservative layout fallback.
    /// </summary>
    public PdfSemanticExtractionOptions SemanticExtractionOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets whether tagged figures with exported image assets are emitted.
    /// </summary>
    public bool IncludeImages { get; set; } = true;

    /// <summary>
    /// Gets or sets whether informational source-selection diagnostics are included.
    /// </summary>
    public bool IncludeInformationalDiagnostics { get; set; } = true;
}
