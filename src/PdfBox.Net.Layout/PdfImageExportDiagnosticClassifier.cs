namespace PdfBox.Net.Layout;

internal static class PdfImageExportDiagnosticClassifier
{
    internal static string CodeForFailure(string message)
    {
        if (message.Contains("JPX", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("JPEG 2000", StringComparison.OrdinalIgnoreCase))
        {
            return "image-asset-jpx-backend-required";
        }
        if (message.Contains("TIFF", StringComparison.OrdinalIgnoreCase))
        {
            return "image-asset-tiff-backend-required";
        }
        if (message.Contains("CMYK", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("YCCK", StringComparison.OrdinalIgnoreCase))
        {
            return "image-asset-cmyk-backend-required";
        }
        if (message.Contains("ICC", StringComparison.OrdinalIgnoreCase))
        {
            return "image-asset-icc-backend-required";
        }
        if (ExceptionNeedsBackend(message))
        {
            return "image-asset-backend-required";
        }
        return "image-asset-export-failed";
    }

    private static bool ExceptionNeedsBackend(string message) =>
        message.Contains("backend", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("decoder", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("codec", StringComparison.OrdinalIgnoreCase);
}
