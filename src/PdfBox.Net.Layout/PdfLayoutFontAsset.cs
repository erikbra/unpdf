namespace PdfBox.Net.Layout;

/// <summary>
/// A browser-safe embedded font program extracted from a PDF.
/// </summary>
public sealed class PdfLayoutFontAsset
{
    public PdfLayoutFontAsset(
        string assetId,
        IReadOnlyList<string> fontNames,
        string relativePath,
        string contentType,
        string cssFormat,
        byte[] data,
        string cssFontStyle = "normal",
        int cssFontWeight = 400)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentNullException.ThrowIfNull(fontNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cssFormat);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(cssFontStyle);

        AssetId = assetId;
        FontNames = fontNames.Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        RelativePath = relativePath.Replace('\\', '/');
        ContentType = contentType;
        CssFormat = cssFormat;
        Data = data.ToArray();
        CssFontStyle = cssFontStyle;
        CssFontWeight = Math.Clamp(cssFontWeight, 100, 900);
    }

    /// <summary>
    /// Gets the stable identifier for this deduplicated font program.
    /// </summary>
    public string AssetId { get; }

    /// <summary>
    /// Gets the PDF base-font names which use this program.
    /// </summary>
    public IReadOnlyList<string> FontNames { get; }

    /// <summary>
    /// Gets the browser-relative output path.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the MIME type of the font program.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the CSS <c>format()</c> value for the font program.
    /// </summary>
    public string CssFormat { get; }

    /// <summary>
    /// Gets the CSS font-style derived from the PDF font descriptor.
    /// </summary>
    public string CssFontStyle { get; }

    /// <summary>
    /// Gets the CSS/OpenType weight derived from the PDF font descriptor.
    /// </summary>
    public int CssFontWeight { get; }

    /// <summary>
    /// Gets the raw OpenType or TrueType program bytes.
    /// </summary>
    public byte[] Data { get; }
}
