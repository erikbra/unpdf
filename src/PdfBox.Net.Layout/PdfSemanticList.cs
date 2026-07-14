namespace PdfBox.Net.Layout;

/// <summary>
/// The semantic kind of an inferred list.
/// </summary>
public enum PdfSemanticListKind
{
    Unordered,
    Ordered
}

/// <summary>
/// The source marker style used by an inferred list.
/// </summary>
public enum PdfSemanticListMarkerKind
{
    Bullet,
    Hyphen,
    Decimal,
    LowerAlpha,
    UpperAlpha,
    LowerRoman,
    UpperRoman
}

/// <summary>
/// A semantic list inferred from repeated source markers and layout geometry.
/// </summary>
public sealed class PdfSemanticList
{
    public PdfSemanticList(
        PdfSemanticListKind kind,
        PdfSemanticListMarkerKind markerKind,
        IReadOnlyList<PdfSemanticListItem> items,
        int? start = null,
        bool isReversed = false)
    {
        Kind = kind;
        MarkerKind = markerKind;
        Items = items.ToArray();
        Start = start;
        IsReversed = isReversed;
    }

    public PdfSemanticListKind Kind { get; }

    public PdfSemanticListMarkerKind MarkerKind { get; }

    public IReadOnlyList<PdfSemanticListItem> Items { get; }

    public int? Start { get; }

    public bool IsReversed { get; }
}

/// <summary>
/// An item in an inferred semantic list.
/// </summary>
public sealed class PdfSemanticListItem
{
    public PdfSemanticListItem(
        string text,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfSemanticLine> lines,
        string marker,
        int markerLength,
        int? value = null,
        IReadOnlyList<PdfSemanticList>? nestedLists = null)
    {
        Text = text;
        Bounds = bounds;
        Lines = lines.ToArray();
        Marker = marker;
        MarkerLength = markerLength;
        Value = value;
        NestedLists = nestedLists?.ToArray() ?? [];
    }

    public string Text { get; }

    public PdfLayoutRectangle Bounds { get; }

    public IReadOnlyList<PdfSemanticLine> Lines { get; }

    public string Marker { get; }

    public int MarkerLength { get; }

    public int? Value { get; }

    public IReadOnlyList<PdfSemanticList> NestedLists { get; }
}
