using System.Text;
using Microsoft.JSInterop;
using PdfBox.Net.Html;

namespace PdfBox.Net.Html.Wasm;

internal sealed class BrowserPreviewHost(IJSRuntime javascript)
{
    public async ValueTask<BrowserPreviewHandle> CreateAsync(
        PdfHtmlDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        bool supportsBlobUrls = await javascript.InvokeAsync<bool>(
            "unpdf.preview.supportsBlobUrls",
            cancellationToken);
        long outputBytes = Encoding.UTF8.GetByteCount(document.Html) +
            Encoding.UTF8.GetByteCount(document.Css) +
            document.Assets.Sum(static asset => (long)asset.Data.LongLength);
        if (!supportsBlobUrls)
        {
            string inlineHtml = BrowserHtmlDocument.InlineAssets(document);
            return new BrowserPreviewHandle(
                null,
                null,
                inlineHtml,
                Encoding.UTF8.GetByteCount(inlineHtml),
                UsesBlobUrls: false);
        }

        string sessionId = await javascript.InvokeAsync<string>(
            "unpdf.preview.begin",
            cancellationToken);
        try
        {
            foreach (PdfHtmlAsset asset in document.Assets)
            {
                await javascript.InvokeVoidAsync(
                    "unpdf.preview.addAsset",
                    cancellationToken,
                    sessionId,
                    asset.RelativePath,
                    Path.GetFileName(asset.RelativePath),
                    asset.ContentType,
                    asset.Data);
            }

            string previewUrl = await javascript.InvokeAsync<string>(
                "unpdf.preview.complete",
                cancellationToken,
                sessionId,
                document.Html,
                document.CssPath,
                document.Css);
            return new BrowserPreviewHandle(
                sessionId,
                previewUrl,
                null,
                outputBytes,
                UsesBlobUrls: true);
        }
        catch
        {
            await ReleaseAsync(sessionId);
            throw;
        }
    }

    public async ValueTask ReleaseAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            await javascript.InvokeVoidAsync("unpdf.preview.release", sessionId);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public ValueTask<BrowserMemorySnapshot> MeasureMemoryAsync(CancellationToken cancellationToken) =>
        javascript.InvokeAsync<BrowserMemorySnapshot>(
            "unpdf.memory.snapshot",
            cancellationToken);
}

internal sealed record BrowserPreviewHandle(
    string? SessionId,
    string? Url,
    string? InlineHtml,
    long OutputBytes,
    bool UsesBlobUrls);

internal sealed class BrowserMemorySnapshot
{
    public long? JavaScriptHeapBytes { get; init; }

    public long? WasmMemoryBytes { get; init; }
}
