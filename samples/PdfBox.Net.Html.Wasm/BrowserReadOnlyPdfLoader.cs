using PdfBox.Net.PDModel;
using PdfBox.Net.PdfParser;

namespace PdfBox.Net.Html.Wasm;

internal static class BrowserReadOnlyPdfLoader
{
    public const int PeakFullInputBufferCount = 3;

    /// <summary>
    /// Loads a document without retaining an additional source copy for incremental-save support.
    /// The browser converter is read-only, so keeping those source bytes after parsing only
    /// increases peak and steady-state memory.
    /// </summary>
    public static PDDocument Load(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using MemoryStream stream = new(input, writable: false);
        ParsedPDFDocument parsed = new PDFParser(stream).Parse();
        return new PDDocument(
            parsed.Document,
            source: null,
            permission: parsed.AccessPermission);
    }
}
