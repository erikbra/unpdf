namespace PdfBox.Net.Layout;

/// <summary>
/// The source marker convention inferred for bibliography entries.
/// </summary>
public enum PdfSemanticBibliographyMarkerKind
{
    BracketedNumber,
    Number,
    AuthorYear
}

/// <summary>
/// A bibliography inferred from a references heading and repeated citation entries.
/// </summary>
public sealed class PdfSemanticBibliography
{
    public PdfSemanticBibliography(
        string heading,
        PdfSemanticBibliographyMarkerKind markerKind,
        IReadOnlyList<PdfSemanticBibliographyItem> items)
    {
        Heading = heading;
        MarkerKind = markerKind;
        Items = items.ToArray();
    }

    public string Heading { get; }

    public PdfSemanticBibliographyMarkerKind MarkerKind { get; }

    public IReadOnlyList<PdfSemanticBibliographyItem> Items { get; }

    public int? Start => Items.Count > 0 && Items[0].SourceNumber is > 1
        ? Items[0].SourceNumber
        : null;
}

/// <summary>
/// One logical bibliography entry, potentially continued on later source pages.
/// </summary>
public sealed class PdfSemanticBibliographyItem
{
    public PdfSemanticBibliographyItem(
        string text,
        int ordinal,
        int? sourceNumber,
        string marker,
        int markerLength,
        string id)
    {
        Text = text;
        Ordinal = ordinal;
        SourceNumber = sourceNumber;
        Marker = marker;
        MarkerLength = markerLength;
        Id = id;
    }

    public string Text { get; }

    public int Ordinal { get; }

    public int? SourceNumber { get; }

    public string Marker { get; }

    public int MarkerLength { get; }

    /// <summary>
    /// Gets the HTML destination ID. When possible this is the named destination already used by in-text PDF links.
    /// </summary>
    public string Id { get; }
}

/// <summary>
/// The part of a logical bibliography item present on one source page.
/// </summary>
public sealed class PdfSemanticBibliographyItemFragment
{
    public PdfSemanticBibliographyItemFragment(
        int itemIndex,
        string text,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfSemanticLine> lines,
        bool isFirstPart,
        bool isLastPart)
    {
        ItemIndex = itemIndex;
        Text = text;
        Bounds = bounds;
        Lines = lines.ToArray();
        IsFirstPart = isFirstPart;
        IsLastPart = isLastPart;
    }

    public int ItemIndex { get; }

    public string Text { get; }

    public PdfLayoutRectangle Bounds { get; }

    public IReadOnlyList<PdfSemanticLine> Lines { get; }

    public bool IsFirstPart { get; }

    public bool IsLastPart { get; }
}

/// <summary>
/// The bibliography content present on one source page.
/// </summary>
public sealed class PdfSemanticBibliographyFragment
{
    public PdfSemanticBibliographyFragment(
        PdfSemanticBibliography bibliography,
        int pageNumber,
        IReadOnlyList<PdfSemanticBibliographyItemFragment> items,
        bool isFirstFragment,
        bool isLastFragment)
    {
        Bibliography = bibliography;
        PageNumber = pageNumber;
        Items = items.ToArray();
        IsFirstFragment = isFirstFragment;
        IsLastFragment = isLastFragment;
    }

    public PdfSemanticBibliography Bibliography { get; }

    public int PageNumber { get; }

    public IReadOnlyList<PdfSemanticBibliographyItemFragment> Items { get; }

    public bool IsFirstFragment { get; }

    public bool IsLastFragment { get; }
}
