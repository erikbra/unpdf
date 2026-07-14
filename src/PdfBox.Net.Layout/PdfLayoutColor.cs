namespace PdfBox.Net.Layout;

/// <summary>
/// Browser-safe color resolved from a PDF graphics state.
/// </summary>
public readonly record struct PdfLayoutColor(float Red, float Green, float Blue, float Alpha, string? ColorSpaceName);
