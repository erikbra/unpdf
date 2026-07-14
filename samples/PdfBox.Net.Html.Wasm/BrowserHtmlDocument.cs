using System.Net;
using PdfBox.Net.Html;

namespace PdfBox.Net.Html.Wasm;

internal static class BrowserHtmlDocument
{
    public static string InlineAssets(PdfHtmlDocument document)
    {
        string html = document.Html;
        string css = document.Css;

        foreach (PdfHtmlAsset asset in document.Assets)
        {
            string dataUri = $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Data)}";
            html = html.Replace(asset.RelativePath, dataUri, StringComparison.Ordinal);
            css = css.Replace(asset.RelativePath, dataUri, StringComparison.Ordinal);
            css = css.Replace(Path.GetFileName(asset.RelativePath), dataUri, StringComparison.Ordinal);
        }

        string stylesheet = $"  <style>\n{css}\n  </style>";
        string link = $"  <link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(document.CssPath)}\" />";
        return html.Replace(link, stylesheet, StringComparison.Ordinal);
    }
}
