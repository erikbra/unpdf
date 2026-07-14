namespace PdfBox.Net.Layout;

/// <summary>
/// Layout data extracted from a PDF document.
/// </summary>
public sealed class PdfLayoutDocument
{
    public PdfLayoutDocument(IReadOnlyList<PdfLayoutPage> pages, IReadOnlyList<PdfLayoutDiagnostic> diagnostics)
        : this(pages, [], [], diagnostics)
    {
    }

    public PdfLayoutDocument(
        IReadOnlyList<PdfLayoutPage> pages,
        IReadOnlyList<PdfLayoutImageAsset> imageAssets,
        IReadOnlyList<PdfLayoutDiagnostic> diagnostics)
        : this(pages, imageAssets, [], diagnostics)
    {
    }

    public PdfLayoutDocument(
        IReadOnlyList<PdfLayoutPage> pages,
        IReadOnlyList<PdfLayoutImageAsset> imageAssets,
        IReadOnlyList<PdfLayoutFontAsset> fontAssets,
        IReadOnlyList<PdfLayoutDiagnostic> diagnostics)
    {
        Pages = pages.ToArray();
        ImageAssets = imageAssets.ToArray();
        FontAssets = fontAssets.ToArray();
        Diagnostics = diagnostics.ToArray();
    }

    /// <summary>
    /// Gets the extracted pages.
    /// </summary>
    public IReadOnlyList<PdfLayoutPage> Pages { get; }

    /// <summary>
    /// Gets exported image assets referenced by page image placements.
    /// </summary>
    public IReadOnlyList<PdfLayoutImageAsset> ImageAssets { get; }

    /// <summary>
    /// Gets browser-safe embedded font programs referenced by the text layout.
    /// </summary>
    public IReadOnlyList<PdfLayoutFontAsset> FontAssets { get; }

    /// <summary>
    /// Gets document-level diagnostics.
    /// </summary>
    public IReadOnlyList<PdfLayoutDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets extracted text in page order.
    /// </summary>
    public string Text => string.Join(Environment.NewLine, Pages.Select(page => page.Text).Where(text => text.Length > 0));
}
