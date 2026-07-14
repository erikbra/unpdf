namespace PdfBox.Net.Layout;

/// <summary>
/// Exported image bytes referenced by layout image placements.
/// </summary>
public sealed class PdfLayoutImageAsset
{
    public PdfLayoutImageAsset(string assetId, string relativePath, string contentType, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(data);

        AssetId = assetId;
        RelativePath = relativePath.Replace('\\', '/');
        ContentType = contentType;
        Data = data.ToArray();
    }

    /// <summary>
    /// Gets the image asset identifier referenced by page image placements.
    /// </summary>
    public string AssetId { get; }

    /// <summary>
    /// Gets the output path relative to the generated document root.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the exported image MIME type.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the exported image bytes.
    /// </summary>
    public byte[] Data { get; }
}
