namespace PdfBox.Net.Html;

/// <summary>
/// Generated fixed-layout HTML document and its CSS asset.
/// </summary>
public sealed class PdfHtmlDocument
{
    public PdfHtmlDocument(string html, string cssPath, string css)
        : this(html, cssPath, css, [])
    {
    }

    public PdfHtmlDocument(string html, string cssPath, string css, IReadOnlyList<PdfHtmlAsset> assets)
    {
        Html = html;
        CssPath = cssPath;
        Css = css;
        Assets = assets.ToArray();
    }

    /// <summary>
    /// Gets the generated HTML.
    /// </summary>
    public string Html { get; }

    /// <summary>
    /// Gets the relative CSS path referenced by the generated HTML.
    /// </summary>
    public string CssPath { get; }

    /// <summary>
    /// Gets the generated CSS.
    /// </summary>
    public string Css { get; }

    /// <summary>
    /// Gets binary assets referenced by the generated HTML.
    /// </summary>
    public IReadOnlyList<PdfHtmlAsset> Assets { get; }

    /// <summary>
    /// Writes the HTML and CSS files into a directory.
    /// </summary>
    /// <param name="directory">Output directory.</param>
    public void WriteToDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string htmlPath = Path.Combine(directory, "index.html");
        string cssPath = Path.Combine(directory, CssPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(htmlPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cssPath)!);
        File.WriteAllText(htmlPath, Html);
        File.WriteAllText(cssPath, Css);

        foreach (PdfHtmlAsset asset in Assets)
        {
            string assetPath = Path.Combine(directory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, asset.Data);
        }
    }
}
