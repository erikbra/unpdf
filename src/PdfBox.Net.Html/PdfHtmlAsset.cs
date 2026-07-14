namespace PdfBox.Net.Html;

/// <summary>
/// Binary asset emitted by a generated HTML document.
/// </summary>
public sealed class PdfHtmlAsset
{
    public PdfHtmlAsset(string relativePath, string contentType, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(data);

        RelativePath = relativePath.Replace('\\', '/');
        ContentType = contentType;
        Data = data.ToArray();
    }

    /// <summary>
    /// Gets the output path relative to the generated document root.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the asset MIME type.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the asset bytes.
    /// </summary>
    public byte[] Data { get; }
}
