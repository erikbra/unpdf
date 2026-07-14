namespace PdfBox.Net.Layout;

/// <summary>
/// Heuristic thresholds for semantic grouping over positioned PDF text.
/// </summary>
public sealed class PdfSemanticExtractionOptions
{
    /// <summary>
    /// Minimum glyph gap, in points, that may imply an omitted word boundary.
    /// </summary>
    public float MinimumWordGap { get; init; } = 0.8f;

    /// <summary>
    /// Font-size multiplier used with <see cref="MinimumWordGap"/> to infer missing word boundaries.
    /// </summary>
    public float WordGapFontSizeMultiplier { get; init; } = 0.16f;

    /// <summary>
    /// Additional font-size delta over body text that can make a line a heading candidate.
    /// </summary>
    public float HeadingFontSizeDelta { get; init; } = 0.75f;

    /// <summary>
    /// Multiplier over the dominant line step that starts a new paragraph.
    /// </summary>
    public float ParagraphGapMultiplier { get; init; } = 1.35f;

    /// <summary>
    /// Maximum horizontal gap, in points, for assigning name and affiliation lines to an email-anchored author cell.
    /// </summary>
    public float AuthorColumnTolerance { get; init; } = 42f;
}
