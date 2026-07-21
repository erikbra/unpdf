namespace PdfBox.Net.Layout;

/// <summary>
/// Reports a completed page boundary during incremental layout extraction.
/// </summary>
public readonly record struct PdfLayoutExtractionProgress(int CompletedPages, int TotalPages);
