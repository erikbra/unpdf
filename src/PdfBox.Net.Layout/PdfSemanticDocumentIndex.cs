namespace PdfBox.Net.Layout;

/// <summary>
/// The semantic kind of a major document index.
/// </summary>
public enum PdfSemanticDocumentIndexKind
{
    TableOfContents,
    ListOfFigures,
    ListOfTables
}

/// <summary>
/// A major document index inferred from a heading and repeated page-reference rows.
/// </summary>
public sealed class PdfSemanticDocumentIndex
{
    public PdfSemanticDocumentIndex(
        PdfSemanticDocumentIndexKind kind,
        string heading,
        IReadOnlyList<PdfSemanticLine> headingLines,
        IReadOnlyList<PdfSemanticDocumentIndexItem> items)
    {
        Kind = kind;
        Heading = heading;
        HeadingLines = headingLines.ToArray();
        Items = items.ToArray();
    }

    public PdfSemanticDocumentIndexKind Kind { get; }

    public string Heading { get; }

    public IReadOnlyList<PdfSemanticLine> HeadingLines { get; }

    public IReadOnlyList<PdfSemanticDocumentIndexItem> Items { get; }
}

/// <summary>
/// One page-reference row in a major document index.
/// </summary>
public sealed class PdfSemanticDocumentIndexItem
{
    public PdfSemanticDocumentIndexItem(
        string label,
        string pageLabel,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfSemanticLine> lines,
        PdfLayoutLink? link = null,
        IReadOnlyList<PdfSemanticDocumentIndexItem>? children = null)
    {
        Label = label;
        PageLabel = pageLabel;
        Bounds = bounds;
        Lines = lines.ToArray();
        Link = link;
        Children = children?.ToArray() ?? [];
    }

    public string Label { get; }

    public string PageLabel { get; }

    public PdfLayoutRectangle Bounds { get; }

    public IReadOnlyList<PdfSemanticLine> Lines { get; }

    public PdfLayoutLink? Link { get; }

    public IReadOnlyList<PdfSemanticDocumentIndexItem> Children { get; }
}
