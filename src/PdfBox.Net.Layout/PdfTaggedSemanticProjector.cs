using System.Globalization;

namespace PdfBox.Net.Layout;

internal static class PdfTaggedSemanticProjector
{
    public static PdfSemanticPage? ProjectPage(PdfLayoutPage page, PdfTaggedStructureDocument structure)
    {
        List<PdfSemanticElement> elements = [];
        foreach (PdfTaggedStructureElement root in structure.Roots)
        {
            ProjectBlocks(root, page, elements);
        }

        return elements.Count == 0 ? null : new PdfSemanticPage(page.PageNumber, elements);
    }

    private static void ProjectBlocks(
        PdfTaggedStructureElement element,
        PdfLayoutPage page,
        List<PdfSemanticElement> output)
    {
        switch (element.Kind)
        {
            case PdfTaggedStructureKind.Heading:
                if (CreateTextElement(element, page, PdfSemanticElementKind.Heading) is PdfSemanticElement heading)
                {
                    output.Add(heading);
                }
                return;
            case PdfTaggedStructureKind.Paragraph:
                if (CreateTextElement(element, page, PdfSemanticElementKind.Paragraph) is PdfSemanticElement paragraph)
                {
                    output.Add(paragraph);
                }
                return;
            case PdfTaggedStructureKind.List:
                if (CreateListElement(element, page) is PdfSemanticElement list)
                {
                    output.Add(list);
                }
                return;
            case PdfTaggedStructureKind.Table:
                if (CreateTableElement(element, page) is PdfSemanticElement table)
                {
                    output.Add(table);
                }
                return;
        }

        foreach (PdfTaggedElementKid child in element.Kids.OfType<PdfTaggedElementKid>())
        {
            ProjectBlocks(child.Element, page, output);
        }
    }

    private static PdfSemanticElement? CreateTextElement(
        PdfTaggedStructureElement source,
        PdfLayoutPage page,
        PdfSemanticElementKind kind)
    {
        PdfTextRun[] runs = ContentRuns(source, page.PageNumber).ToArray();
        PdfLayoutImage[] images = ContentImages(source, page.PageNumber).ToArray();
        string text = AuthoredText(source, page.PageNumber);
        if (string.IsNullOrWhiteSpace(text) && runs.Length == 0)
        {
            return null;
        }

        PdfSemanticLine[] lines = HasActualText(source) ? [] : LinesForRuns(page, runs);
        if (string.IsNullOrEmpty(text))
        {
            text = string.Join(Environment.NewLine, lines.Select(static line => line.Text));
        }

        int headingLevel = HeadingLevel(source.StandardStructureType);
        return new PdfSemanticElement(
            kind,
            text,
            ContentBounds(runs, images),
            lines,
            headingLevel: headingLevel,
            isDocumentTitle: string.Equals(source.StandardStructureType, "Title", StringComparison.Ordinal),
            taggedStructure: source);
    }

    private static PdfSemanticElement? CreateListElement(
        PdfTaggedStructureElement source,
        PdfLayoutPage page)
    {
        PdfSemanticList? list = CreateList(source, page);
        if (list == null || list.Items.Count == 0)
        {
            return null;
        }

        PdfSemanticLine[] lines = list.Items
            .SelectMany(static item => item.Lines)
            .ToArray();
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        string text = string.Join(Environment.NewLine, list.Items.Select(static item => item.Text));
        return new PdfSemanticElement(
            PdfSemanticElementKind.List,
            text,
            runs.Length == 0
                ? UnionRectangles(list.Items.Select(static item => item.Bounds))
                : PdfLayoutRectangle.Union(runs.Select(static run => run.PageBounds)),
            lines,
            semanticList: list,
            taggedStructure: source);
    }

    private static PdfSemanticList? CreateList(PdfTaggedStructureElement source, PdfLayoutPage page)
    {
        PdfTaggedStructureElement[] itemElements = source.Children
            .Where(static child => child.Kind == PdfTaggedStructureKind.ListItem)
            .ToArray();
        if (itemElements.Length == 0)
        {
            return null;
        }

        string? numbering = source.Attributes.ListNumbering;
        string[] authoredLabels = itemElements
            .Select(item => item.Children
                .FirstOrDefault(static child => child.Kind == PdfTaggedStructureKind.ListLabel))
            .Select(label => label == null ? "" : AuthoredText(label, page.PageNumber).Trim())
            .ToArray();
        bool hasNumericLabels = numbering == null &&
            authoredLabels.Length > 0 &&
            authoredLabels.All(static marker => ParseListValue(marker).HasValue);
        PdfSemanticListKind kind = IsOrderedListNumbering(numbering) || hasNumericLabels
            ? PdfSemanticListKind.Ordered
            : PdfSemanticListKind.Unordered;
        PdfSemanticListMarkerKind markerKind = hasNumericLabels
            ? PdfSemanticListMarkerKind.Decimal
            : MarkerKind(numbering);
        List<PdfSemanticListItem> items = [];
        for (int index = 0; index < itemElements.Length; index++)
        {
            PdfTaggedStructureElement item = itemElements[index];
            PdfTaggedStructureElement? label = item.Children
                .FirstOrDefault(static child => child.Kind == PdfTaggedStructureKind.ListLabel);
            PdfTaggedStructureElement? body = item.Children
                .FirstOrDefault(static child => child.Kind == PdfTaggedStructureKind.ListBody);
            PdfTaggedStructureElement bodySource = body ?? item;
            PdfTextRun[] bodyRuns = ContentRuns(
                bodySource,
                page.PageNumber,
                skipNestedLists: true).ToArray();
            string text = AuthoredText(bodySource, page.PageNumber, skipNestedLists: true);
            PdfSemanticLine[] lines = HasActualText(bodySource)
                ? []
                : LinesForRuns(page, bodyRuns);
            if (string.IsNullOrEmpty(text))
            {
                text = string.Join(Environment.NewLine, lines.Select(static line => line.Text));
            }

            string marker = authoredLabels[index];
            if (marker.Length == 0)
            {
                marker = kind == PdfSemanticListKind.Ordered
                    ? (index + 1).ToString(CultureInfo.InvariantCulture) + "."
                    : "•";
            }

            PdfSemanticList[] nested = item.Children
                .Concat(body?.Children ?? [])
                .Where(static child => child.Kind == PdfTaggedStructureKind.List)
                .Select(child => CreateList(child, page))
                .Where(static nestedList => nestedList != null)
                .Cast<PdfSemanticList>()
                .ToArray();
            PdfTextRun[] itemRuns = (label == null
                    ? bodyRuns
                    : ContentRuns(label, page.PageNumber).Concat(bodyRuns))
                .Distinct((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance)
                .ToArray();
            if (string.IsNullOrWhiteSpace(text) && itemRuns.Length == 0 && nested.Length == 0)
            {
                continue;
            }

            items.Add(new PdfSemanticListItem(
                text,
                itemRuns.Length == 0
                    ? UnionRectangles(nested.SelectMany(static nestedList => nestedList.Items).Select(static nestedItem => nestedItem.Bounds))
                    : PdfLayoutRectangle.Union(itemRuns.Select(static run => run.PageBounds)),
                lines,
                marker,
                // Tagged list bodies are projected without their sibling Lbl
                // content, so the renderer must not trim a marker prefix from
                // the first body line.
                markerLength: 0,
                value: kind == PdfSemanticListKind.Ordered ? ParseListValue(marker) : null,
                nestedLists: nested));
        }

        return new PdfSemanticList(kind, markerKind, items);
    }

    private static PdfSemanticElement? CreateTableElement(
        PdfTaggedStructureElement source,
        PdfLayoutPage page)
    {
        PdfTaggedStructureElement[] rowElements = Descendants(source)
            .Where(static child => child.Kind == PdfTaggedStructureKind.TableRow)
            .ToArray();
        List<PdfSemanticTableRow> rows = [];
        foreach (PdfTaggedStructureElement rowElement in rowElements)
        {
            PdfTaggedStructureElement[] cells = rowElement.Children
                .Where(static child => child.Kind is
                    PdfTaggedStructureKind.TableHeaderCell or PdfTaggedStructureKind.TableCell)
                .ToArray();
            List<PdfSemanticTableCell> projectedCells = [];
            foreach (PdfTaggedStructureElement cell in cells)
            {
                PdfTextRun[] runs = ContentRuns(cell, page.PageNumber).ToArray();
                PdfLayoutImage[] images = ContentImages(cell, page.PageNumber).ToArray();
                string text = AuthoredText(cell, page.PageNumber);
                PdfSemanticLine[] lines = HasActualText(cell) ? [] : LinesForRuns(page, runs);
                if (string.IsNullOrEmpty(text))
                {
                    text = string.Join(Environment.NewLine, lines.Select(static line => line.Text));
                }

                if (string.IsNullOrWhiteSpace(text) && runs.Length == 0 && images.Length == 0)
                {
                    continue;
                }

                projectedCells.Add(new PdfSemanticTableCell(
                    text,
                    ContentBounds(runs, images),
                    lines,
                    rowSpan: cell.Attributes.RowSpan,
                    columnSpan: cell.Attributes.ColumnSpan));
            }

            if (projectedCells.Count > 0)
            {
                rows.Add(new PdfSemanticTableRow(
                    projectedCells,
                    isHeader: cells.All(static cell =>
                        cell.Kind == PdfTaggedStructureKind.TableHeaderCell)));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        PdfSemanticLine[] tableLines = rows
            .SelectMany(static row => row.Cells)
            .SelectMany(static cell => cell.Lines)
            .ToArray();
        PdfLayoutRectangle bounds = UnionRectangles(rows
            .SelectMany(static row => row.Cells)
            .Select(static cell => cell.Bounds));
        return new PdfSemanticElement(
            PdfSemanticElementKind.Table,
            string.Join(Environment.NewLine, rows.Select(row =>
                string.Join(" | ", row.Cells.Select(static cell => cell.Text)))),
            bounds,
            tableLines,
            tableRows: rows,
            taggedStructure: source);
    }

    private static IEnumerable<PdfTaggedStructureElement> Descendants(PdfTaggedStructureElement parent)
    {
        foreach (PdfTaggedStructureElement child in parent.Children)
        {
            yield return child;
            if (child.Kind != PdfTaggedStructureKind.Table)
            {
                foreach (PdfTaggedStructureElement descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static IEnumerable<PdfTextRun> ContentRuns(
        PdfTaggedStructureElement element,
        int pageNumber,
        bool skipNestedLists = false)
    {
        foreach (PdfTaggedStructureKid kid in element.Kids)
        {
            if (kid is PdfTaggedContentKid content && content.Content.PageNumber == pageNumber)
            {
                foreach (PdfTextRun run in content.Content.TextRuns)
                {
                    yield return run;
                }
            }
            else if (kid is PdfTaggedElementKid child &&
                !(skipNestedLists && child.Element.Kind == PdfTaggedStructureKind.List))
            {
                foreach (PdfTextRun run in ContentRuns(child.Element, pageNumber, skipNestedLists))
                {
                    yield return run;
                }
            }
        }
    }

    private static IEnumerable<PdfLayoutImage> ContentImages(
        PdfTaggedStructureElement element,
        int pageNumber)
    {
        return element.DescendantContentReferences()
            .Where(reference => reference.PageNumber == pageNumber)
            .SelectMany(static reference => reference.Images)
            .Distinct((IEqualityComparer<PdfLayoutImage>)ReferenceEqualityComparer.Instance);
    }

    private static string AuthoredText(
        PdfTaggedStructureElement element,
        int pageNumber,
        bool skipNestedLists = false)
    {
        if (!string.IsNullOrEmpty(element.ActualText))
        {
            return element.ActualText;
        }

        return string.Concat(element.Kids.Select(kid => kid switch
        {
            PdfTaggedContentKid content when content.Content.PageNumber == pageNumber =>
                string.Concat(content.Content.TextRuns.Select(static run => run.Text)),
            PdfTaggedElementKid child when !(skipNestedLists && child.Element.Kind == PdfTaggedStructureKind.List) =>
                AuthoredText(child.Element, pageNumber, skipNestedLists),
            _ => ""
        }));
    }

    private static bool HasActualText(PdfTaggedStructureElement element)
    {
        return !string.IsNullOrEmpty(element.ActualText) || element.Children.Any(HasActualText);
    }

    private static PdfSemanticLine[] LinesForRuns(PdfLayoutPage page, IReadOnlyList<PdfTextRun> runs)
    {
        HashSet<PdfTextRun> selected = runs.ToHashSet(
            (IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance);
        List<PdfSemanticLine> lines = [];
        foreach (PdfTextLine sourceLine in page.Lines)
        {
            PdfTextRun[] lineRuns = sourceLine.Runs.Where(selected.Contains).ToArray();
            if (lineRuns.Length == 0)
            {
                continue;
            }

            PdfTextRun dominant = lineRuns
                .OrderByDescending(static run => run.Glyphs.Count)
                .ThenByDescending(static run => run.FontSize)
                .First();
            lines.Add(new PdfSemanticLine(
                string.Concat(lineRuns.Select(static run => run.Text)),
                PdfLayoutRectangle.Union(lineRuns.Select(static run => run.Bounds)),
                dominant.FontName,
                dominant.FontSize,
                dominant.Direction,
                dominant.Color,
                lineRuns));
        }

        return lines.ToArray();
    }

    private static PdfLayoutRectangle ContentBounds(
        IReadOnlyList<PdfTextRun> runs,
        IReadOnlyList<PdfLayoutImage> images)
    {
        return UnionRectangles(
            runs.Select(static run => run.PageBounds)
                .Concat(images.Select(static image => image.Bounds)));
    }

    private static PdfLayoutRectangle UnionRectangles(IEnumerable<PdfLayoutRectangle> rectangles)
    {
        PdfLayoutRectangle[] values = rectangles.ToArray();
        return values.Length == 0
            ? new PdfLayoutRectangle(0, 0, 0, 0)
            : PdfLayoutRectangle.Union(values);
    }

    private static int HeadingLevel(string structureType)
    {
        return structureType.Length == 2 &&
            structureType[0] == 'H' &&
            char.IsDigit(structureType[1])
                ? Math.Clamp(structureType[1] - '0', 1, 6)
                : 1;
    }

    private static bool IsOrderedListNumbering(string? numbering)
    {
        return numbering is "Decimal" or "UpperRoman" or "LowerRoman" or "UpperAlpha" or "LowerAlpha";
    }

    private static PdfSemanticListMarkerKind MarkerKind(string? numbering) => numbering switch
    {
        "UpperRoman" => PdfSemanticListMarkerKind.UpperRoman,
        "LowerRoman" => PdfSemanticListMarkerKind.LowerRoman,
        "UpperAlpha" => PdfSemanticListMarkerKind.UpperAlpha,
        "LowerAlpha" => PdfSemanticListMarkerKind.LowerAlpha,
        "Decimal" => PdfSemanticListMarkerKind.Decimal,
        _ => PdfSemanticListMarkerKind.Bullet
    };

    private static int? ParseListValue(string marker)
    {
        string digits = new(marker.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }
}
