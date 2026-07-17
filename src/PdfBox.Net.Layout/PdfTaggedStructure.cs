namespace PdfBox.Net.Layout;

/// <summary>
/// Coarse authored role projected from a tagged PDF structure element.
/// </summary>
public enum PdfTaggedStructureKind
{
    Other,
    Document,
    Section,
    Heading,
    Paragraph,
    List,
    ListItem,
    ListLabel,
    ListBody,
    Table,
    TableRow,
    TableHeaderCell,
    TableCell,
    Figure,
    Link
}

/// <summary>
/// Relevant standard structure attributes retained for downstream converters.
/// </summary>
public sealed class PdfTaggedStructureAttributes
{
    public PdfTaggedStructureAttributes(
        IReadOnlyList<string>? owners = null,
        string? listNumbering = null,
        int rowSpan = 1,
        int columnSpan = 1,
        string? tableScope = null,
        IReadOnlyList<string>? tableHeaders = null,
        string? tableSummary = null,
        string? placement = null,
        string? writingMode = null,
        string? textAlignment = null)
    {
        Owners = owners?.ToArray() ?? [];
        ListNumbering = listNumbering;
        RowSpan = Math.Max(1, rowSpan);
        ColumnSpan = Math.Max(1, columnSpan);
        TableScope = tableScope;
        TableHeaders = tableHeaders?.ToArray() ?? [];
        TableSummary = tableSummary;
        Placement = placement;
        WritingMode = writingMode;
        TextAlignment = textAlignment;
    }

    public IReadOnlyList<string> Owners { get; }

    public string? ListNumbering { get; }

    public int RowSpan { get; }

    public int ColumnSpan { get; }

    public string? TableScope { get; }

    public IReadOnlyList<string> TableHeaders { get; }

    public string? TableSummary { get; }

    public string? Placement { get; }

    public string? WritingMode { get; }

    public string? TextAlignment { get; }
}

/// <summary>
/// One page/MCID correlation from the authored structure tree to extracted layout content.
/// </summary>
public sealed class PdfTaggedContentReference
{
    public PdfTaggedContentReference(
        int pageNumber,
        int markedContentId,
        IReadOnlyList<PdfTextRun> textRuns,
        IReadOnlyList<PdfLayoutImage> images)
    {
        PageNumber = pageNumber;
        MarkedContentId = markedContentId;
        TextRuns = textRuns.ToArray();
        Images = images.ToArray();
    }

    public int PageNumber { get; }

    public int MarkedContentId { get; }

    public IReadOnlyList<PdfTextRun> TextRuns { get; }

    public IReadOnlyList<PdfLayoutImage> Images { get; }

    public bool IsResolved => TextRuns.Count > 0 || Images.Count > 0;
}

/// <summary>
/// One ordered child in a tagged structure element's authored K array.
/// </summary>
public abstract class PdfTaggedStructureKid
{
    private protected PdfTaggedStructureKid()
    {
    }
}

/// <summary>
/// An ordered marked-content child in a tagged structure element.
/// </summary>
public sealed class PdfTaggedContentKid : PdfTaggedStructureKid
{
    public PdfTaggedContentKid(PdfTaggedContentReference content)
    {
        Content = content;
    }

    public PdfTaggedContentReference Content { get; }
}

/// <summary>
/// An ordered nested-element child in a tagged structure element.
/// </summary>
public sealed class PdfTaggedElementKid : PdfTaggedStructureKid
{
    public PdfTaggedElementKid(PdfTaggedStructureElement element)
    {
        Element = element;
    }

    public PdfTaggedStructureElement Element { get; }
}

/// <summary>
/// An authored tagged-PDF structure element and its resolved layout content.
/// </summary>
public sealed class PdfTaggedStructureElement
{
    public PdfTaggedStructureElement(
        string structureType,
        string standardStructureType,
        PdfTaggedStructureKind kind,
        string? actualText,
        string? alternateDescription,
        string? language,
        string? title,
        PdfTaggedStructureAttributes attributes,
        IReadOnlyList<PdfTaggedStructureKid> kids)
    {
        StructureType = structureType;
        StandardStructureType = standardStructureType;
        Kind = kind;
        ActualText = actualText;
        AlternateDescription = alternateDescription;
        Language = language;
        Title = title;
        Attributes = attributes;
        Kids = kids.ToArray();
    }

    public string StructureType { get; }

    public string StandardStructureType { get; }

    public PdfTaggedStructureKind Kind { get; }

    public string? ActualText { get; }

    public string? AlternateDescription { get; }

    public string? Language { get; }

    public string? Title { get; }

    public PdfTaggedStructureAttributes Attributes { get; }

    public IReadOnlyList<PdfTaggedStructureKid> Kids { get; }

    public IReadOnlyList<PdfTaggedContentReference> ContentReferences => Kids
        .OfType<PdfTaggedContentKid>()
        .Select(static kid => kid.Content)
        .ToArray();

    public IReadOnlyList<PdfTaggedStructureElement> Children => Kids
        .OfType<PdfTaggedElementKid>()
        .Select(static kid => kid.Element)
        .ToArray();

    public IEnumerable<PdfTaggedContentReference> DescendantContentReferences()
    {
        foreach (PdfTaggedStructureKid kid in Kids)
        {
            if (kid is PdfTaggedContentKid content)
            {
                yield return content.Content;
            }
            else if (kid is PdfTaggedElementKid child)
            {
                foreach (PdfTaggedContentReference reference in child.Element.DescendantContentReferences())
                {
                    yield return reference;
                }
            }
        }
    }
}

/// <summary>
/// Authored tagged-PDF structure projected into the shared layout layer.
/// </summary>
public sealed class PdfTaggedStructureDocument
{
    public PdfTaggedStructureDocument(IReadOnlyList<PdfTaggedStructureElement> roots)
    {
        Roots = roots.ToArray();
    }

    public IReadOnlyList<PdfTaggedStructureElement> Roots { get; }

    public IReadOnlyList<PdfTaggedStructureElement> Elements => Flatten(Roots).ToArray();

    public bool HasResolvedContent => Elements
        .SelectMany(static element => element.ContentReferences)
        .Any(static reference => reference.IsResolved);

    private static IEnumerable<PdfTaggedStructureElement> Flatten(
        IEnumerable<PdfTaggedStructureElement> elements)
    {
        foreach (PdfTaggedStructureElement element in elements)
        {
            yield return element;
            foreach (PdfTaggedStructureElement child in Flatten(element.Children))
            {
                yield return child;
            }
        }
    }
}
