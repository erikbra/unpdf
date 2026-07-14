namespace PdfBox.Net.Layout;

/// <summary>
/// Severity of a layout extraction diagnostic.
/// </summary>
public enum PdfLayoutDiagnosticSeverity
{
    /// <summary>
    /// Informational diagnostic.
    /// </summary>
    Info,

    /// <summary>
    /// Non-fatal warning.
    /// </summary>
    Warning,

    /// <summary>
    /// Extraction error.
    /// </summary>
    Error
}

/// <summary>
/// Diagnostic emitted while extracting a layout model.
/// </summary>
public sealed record PdfLayoutDiagnostic(
    PdfLayoutDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? PageNumber = null);
