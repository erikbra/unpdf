namespace PdfBox.Net.Layout;

/// <summary>
/// Selects how layout extraction handles PDF images that cannot be emitted as browser-safe assets directly.
/// </summary>
public enum PdfImageExportPolicy
{
    /// <summary>
    /// Preserve the image placement, omit the unavailable asset, and add a stable diagnostic.
    /// </summary>
    Degraded,

    /// <summary>
    /// Fail extraction when any requested image asset cannot be exported.
    /// </summary>
    Strict,

    /// <summary>
    /// Require a registered rendering backend before image asset extraction begins.
    /// </summary>
    BackendRequired
}
