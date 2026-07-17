namespace PdfBox.Net.Layout;

/// <summary>
/// Selects how layout extraction handles requested image assets and raster fallbacks
/// that cannot be emitted directly.
/// </summary>
public enum PdfImageExportPolicy
{
    /// <summary>
    /// Preserve directly representable content, omit the unavailable asset or fallback,
    /// and add a stable diagnostic.
    /// </summary>
    Degraded,

    /// <summary>
    /// Fail extraction when any requested image asset or raster fallback cannot be exported.
    /// </summary>
    Strict,

    /// <summary>
    /// Require a registered rendering backend before requested image asset or raster-fallback
    /// extraction begins.
    /// </summary>
    BackendRequired
}
