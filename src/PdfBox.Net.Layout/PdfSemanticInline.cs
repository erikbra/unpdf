namespace PdfBox.Net.Layout;

/// <summary>
/// Identifies a conservatively inferred text-level semantic role.
/// </summary>
public enum PdfSemanticInlineKind
{
    Small,
    Time,
    Abbreviation,
    Citation
}

/// <summary>
/// A semantic annotation over an exact UTF-16 range of visible source-line text.
/// </summary>
public sealed class PdfSemanticInline
{
    public PdfSemanticInline(
        PdfSemanticInlineKind kind,
        int start,
        int length,
        string? value = null)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (kind is PdfSemanticInlineKind.Time or PdfSemanticInlineKind.Abbreviation &&
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Time and abbreviation semantics require an invariant value.", nameof(value));
        }

        Kind = kind;
        Start = start;
        Length = length;
        Value = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public PdfSemanticInlineKind Kind { get; }

    public int Start { get; }

    public int Length { get; }

    /// <summary>
    /// Gets the invariant datetime value or explicit abbreviation expansion, when required by the role.
    /// </summary>
    public string? Value { get; }
}
