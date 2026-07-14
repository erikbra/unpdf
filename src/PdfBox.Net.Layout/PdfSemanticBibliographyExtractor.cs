using System.Text.RegularExpressions;

namespace PdfBox.Net.Layout;

internal static class PdfSemanticBibliographyExtractor
{
    private static readonly Regex BracketedNumberPattern = new(
        @"^\s*(?<marker>\[(?<value>\d{1,4})\])\s*",
        RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new(
        @"^\s*(?<marker>(?<value>\d{1,4})[.)])\s+",
        RegexOptions.Compiled);
    private static readonly Regex CitationYearPattern = new(
        @"\b(?:18|19|20)\d{2}[a-z]?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AuthorYearStartPattern = new(
        @"^\s*[\p{L}][^\r\n]{0,180}?(?:\(\s*(?:18|19|20)\d{2}[a-z]?\s*\)|(?:18|19|20)\d{2}[a-z]?[.,])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LinkedNumberPattern = new(@"\d{1,4}", RegexOptions.Compiled);
    private static readonly Regex BibliographicLocatorPattern = new(
        @"(?:https?://|doi\s*:|\b10\.\d{4,9}/)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<PdfSemanticPage> Extract(
        PdfLayoutDocument layout,
        IReadOnlyList<PdfSemanticPage> pages)
    {
        Dictionary<int, string> destinationsByNumber = CitationDestinationsByNumber(layout);
        List<BibliographyDetection> detections = [];
        HashSet<PdfSemanticElement> consumed = [];

        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            PdfSemanticPage page = pages[pageIndex];
            for (int elementIndex = 0; elementIndex < page.Elements.Count; elementIndex++)
            {
                PdfSemanticElement heading = page.Elements[elementIndex];
                if (heading.Kind != PdfSemanticElementKind.Heading ||
                    consumed.Contains(heading) ||
                    !IsBibliographyHeading(heading.Text))
                {
                    continue;
                }

                ElementLocation[] scope = BibliographyScope(pages, pageIndex, elementIndex, heading.HeadingLevel)
                    .Where(location => !consumed.Contains(location.Element))
                    .ToArray();
                if (!TryDetectBibliography(heading, scope, destinationsByNumber, detections.Count, out BibliographyDetection detection))
                {
                    continue;
                }

                detections.Add(detection);
                foreach (ElementLocation location in detection.ConsumedElements)
                {
                    consumed.Add(location.Element);
                }
            }
        }

        return detections.Count == 0 ? pages : ApplyDetections(pages, detections);
    }

    private static IEnumerable<ElementLocation> BibliographyScope(
        IReadOnlyList<PdfSemanticPage> pages,
        int headingPageIndex,
        int headingElementIndex,
        int headingLevel)
    {
        for (int pageIndex = headingPageIndex; pageIndex < pages.Count; pageIndex++)
        {
            PdfSemanticPage page = pages[pageIndex];
            int start = pageIndex == headingPageIndex ? headingElementIndex + 1 : 0;
            for (int elementIndex = start; elementIndex < page.Elements.Count; elementIndex++)
            {
                PdfSemanticElement element = page.Elements[elementIndex];
                if (element.Kind == PdfSemanticElementKind.Heading &&
                    element.HeadingLevel > 0 &&
                    element.HeadingLevel <= headingLevel)
                {
                    yield break;
                }

                yield return new ElementLocation(pageIndex, elementIndex, page.PageNumber, element);
            }
        }
    }

    private static bool TryDetectBibliography(
        PdfSemanticElement heading,
        IReadOnlyList<ElementLocation> scope,
        IReadOnlyDictionary<int, string> destinationsByNumber,
        int bibliographyIndex,
        out BibliographyDetection detection)
    {
        detection = null!;
        if (TryBuildNumberedEntries(scope, BracketedNumberPattern, out List<RawBibliographyItem> bracketed) &&
            HasBibliographicEvidence(bracketed))
        {
            detection = CreateDetection(
                heading,
                PdfSemanticBibliographyMarkerKind.BracketedNumber,
                bracketed,
                destinationsByNumber,
                bibliographyIndex);
            return true;
        }

        if (TryBuildSemanticNumberedListEntries(scope, out List<RawBibliographyItem> semanticNumbered) &&
            HasBibliographicEvidence(semanticNumbered))
        {
            detection = CreateDetection(
                heading,
                PdfSemanticBibliographyMarkerKind.Number,
                semanticNumbered,
                destinationsByNumber,
                bibliographyIndex);
            return true;
        }

        if (TryBuildNumberedEntries(scope, NumberPattern, out List<RawBibliographyItem> numbered) &&
            HasBibliographicEvidence(numbered))
        {
            detection = CreateDetection(
                heading,
                PdfSemanticBibliographyMarkerKind.Number,
                numbered,
                destinationsByNumber,
                bibliographyIndex);
            return true;
        }

        if (TryBuildAuthorYearEntries(scope, out List<RawBibliographyItem> authorYear))
        {
            detection = CreateDetection(
                heading,
                PdfSemanticBibliographyMarkerKind.AuthorYear,
                authorYear,
                destinationsByNumber,
                bibliographyIndex);
            return true;
        }

        return false;
    }

    private static bool TryBuildSemanticNumberedListEntries(
        IReadOnlyList<ElementLocation> scope,
        out List<RawBibliographyItem> items)
    {
        items = [];
        RawBibliographyItem? current = null;
        foreach (ElementLocation location in scope)
        {
            PdfSemanticList? list = location.Element.SemanticList;
            if (location.Element.Kind == PdfSemanticElementKind.List &&
                list is
                {
                    Kind: PdfSemanticListKind.Ordered,
                    MarkerKind: PdfSemanticListMarkerKind.Decimal,
                    IsReversed: false
                })
            {
                int value = list.Start ?? 1;
                foreach (PdfSemanticListItem listItem in list.Items)
                {
                    value = listItem.Value ?? value;
                    if (current != null && value <= current.SourceNumber)
                    {
                        items.Clear();
                        return false;
                    }

                    PdfSemanticElement itemElement = new(
                        PdfSemanticElementKind.Paragraph,
                        listItem.Text,
                        listItem.Bounds,
                        listItem.Lines);
                    current = new RawBibliographyItem(
                        value,
                        listItem.Marker,
                        listItem.MarkerLength,
                        location with { ContentElement = itemElement });
                    items.Add(current);
                    value++;
                }

                continue;
            }

            if (current != null && location.Content.Kind == PdfSemanticElementKind.Paragraph)
            {
                current.Elements.Add(location);
            }
        }

        return items.Count >= 2;
    }

    private static bool TryBuildNumberedEntries(
        IReadOnlyList<ElementLocation> scope,
        Regex markerPattern,
        out List<RawBibliographyItem> items)
    {
        items = [];
        List<ElementLocation> markerStarts = [];
        RawBibliographyItem? current = null;
        foreach (ElementLocation location in NumberedEntryLocations(scope))
        {
            if (location.Content.Kind != PdfSemanticElementKind.Paragraph)
            {
                continue;
            }

            Match match = markerPattern.Match(location.Content.Text);
            if (match.Success)
            {
                int value = int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (current != null && value <= current.SourceNumber)
                {
                    items.Clear();
                    return false;
                }

                current = new RawBibliographyItem(
                    value,
                    match.Groups["marker"].Value,
                    match.Length,
                    location);
                items.Add(current);
                markerStarts.Add(location);
                continue;
            }

            if (current != null)
            {
                if (IsSeparatedTrailingParagraph(location, current, markerStarts))
                {
                    break;
                }

                current.Elements.Add(location);
            }
        }

        return items.Count >= 2 && HasConsistentEmbeddedMarkerGeometry(markerStarts);
    }

    private static IEnumerable<ElementLocation> NumberedEntryLocations(
        IReadOnlyList<ElementLocation> scope)
    {
        foreach (ElementLocation location in scope)
        {
            PdfSemanticElement content = location.Content;
            if (content.Kind != PdfSemanticElementKind.Paragraph || content.Lines.Count <= 1)
            {
                yield return location;
                continue;
            }

            foreach (PdfSemanticLine line in content.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                PdfSemanticElement lineElement = new(
                    PdfSemanticElementKind.Paragraph,
                    line.Text,
                    line.Bounds,
                    [line]);
                yield return location with { ContentElement = lineElement };
            }
        }
    }

    private static bool HasConsistentEmbeddedMarkerGeometry(
        IReadOnlyList<ElementLocation> markerStarts)
    {
        foreach (IGrouping<PdfSemanticElement, ElementLocation> group in markerStarts
            .GroupBy(static location => location.Element)
            .Where(static group => group.Count() > 1))
        {
            PdfSemanticLine[] lines = group
                .Select(static location => location.Content.Lines[0])
                .ToArray();
            float representativeFontSize = lines
                .Select(static line => line.DominantFontSize)
                .Order()
                .ElementAt(lines.Length / 2);
            // Multi-digit markers are commonly right-aligned, so their left
            // edge may move by roughly one glyph while the marker column stays aligned.
            float horizontalTolerance = MathF.Max(2f, representativeFontSize * 0.75f);
            if (lines.Max(static line => line.Bounds.X) - lines.Min(static line => line.Bounds.X) >
                horizontalTolerance)
            {
                return false;
            }

            for (int index = 1; index < lines.Length; index++)
            {
                if (lines[index].Bounds.Y <= lines[index - 1].Bounds.Y ||
                    MathF.Abs(lines[index].Direction - lines[0].Direction) > 1f)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsSeparatedTrailingParagraph(
        ElementLocation location,
        RawBibliographyItem current,
        IReadOnlyList<ElementLocation> markerStarts)
    {
        ElementLocation previous = current.Elements[^1];
        if (ReferenceEquals(previous.Element, location.Element) ||
            previous.PageNumber != location.PageNumber ||
            markerStarts.Count == 0)
        {
            return false;
        }

        PdfSemanticLine previousLine = previous.Content.Lines[^1];
        PdfSemanticLine currentLine = location.Content.Lines[0];
        float representativeFontSize = MathF.Max(previousLine.DominantFontSize, currentLine.DominantFontSize);
        float verticalGap = currentLine.Bounds.Y - previousLine.Bounds.Bottom;
        if (verticalGap <= representativeFontSize * 1.25f)
        {
            return false;
        }

        // A separated paragraph that returns to the marker gutter is not a
        // hanging continuation of the active entry.
        float markerColumn = markerStarts
            .Select(static marker => marker.Content.Lines[0].Bounds.X)
            .Order()
            .ElementAt(markerStarts.Count / 2);
        return MathF.Abs(currentLine.Bounds.X - markerColumn) <= representativeFontSize;
    }

    private static bool TryBuildAuthorYearEntries(
        IReadOnlyList<ElementLocation> scope,
        out List<RawBibliographyItem> items)
    {
        items = [];
        RawBibliographyItem? current = null;
        foreach (ElementLocation location in scope)
        {
            if (location.Content.Kind != PdfSemanticElementKind.Paragraph)
            {
                continue;
            }

            if (AuthorYearStartPattern.IsMatch(location.Content.Text))
            {
                current = new RawBibliographyItem(null, "", 0, location);
                items.Add(current);
                continue;
            }

            if (current != null)
            {
                current.Elements.Add(location);
            }
        }

        return items.Count >= 2;
    }

    private static bool HasBibliographicEvidence(IReadOnlyList<RawBibliographyItem> items)
    {
        string[] entries = items.Select(ItemText).ToArray();
        int datedEntries = entries.Count(CitationYearPattern.IsMatch);
        bool hasLocator = entries.Any(BibliographicLocatorPattern.IsMatch);
        bool sustainedCitationPunctuation = entries.All(entry =>
            entry.Length >= 32 &&
            entry.Contains('.', StringComparison.Ordinal) &&
            (entry.Contains(',', StringComparison.Ordinal) || entry.Contains(':', StringComparison.Ordinal)));
        return datedEntries >= Math.Max(2, (items.Count + 1) / 2) || hasLocator || sustainedCitationPunctuation;
    }

    private static BibliographyDetection CreateDetection(
        PdfSemanticElement heading,
        PdfSemanticBibliographyMarkerKind markerKind,
        IReadOnlyList<RawBibliographyItem> rawItems,
        IReadOnlyDictionary<int, string> destinationsByNumber,
        int bibliographyIndex)
    {
        List<PdfSemanticBibliographyItem> items = [];
        HashSet<string> usedIds = new(StringComparer.Ordinal);
        for (int itemIndex = 0; itemIndex < rawItems.Count; itemIndex++)
        {
            RawBibliographyItem rawItem = rawItems[itemIndex];
            string fallbackId = bibliographyIndex == 0
                ? $"reference-{itemIndex + 1}"
                : $"reference-{bibliographyIndex + 1}-{itemIndex + 1}";
            string id = rawItem.SourceNumber.HasValue &&
                destinationsByNumber.TryGetValue(rawItem.SourceNumber.Value, out string? destination) &&
                usedIds.Add(destination)
                    ? destination
                    : fallbackId;
            usedIds.Add(id);
            items.Add(new PdfSemanticBibliographyItem(
                ItemText(rawItem),
                itemIndex + 1,
                rawItem.SourceNumber,
                rawItem.Marker,
                rawItem.MarkerLength,
                id));
        }

        PdfSemanticBibliography bibliography = new(heading.Text.Trim(), markerKind, items);
        Dictionary<int, List<PdfSemanticBibliographyItemFragment>> fragmentsByPage = [];
        for (int itemIndex = 0; itemIndex < rawItems.Count; itemIndex++)
        {
            RawBibliographyItem rawItem = rawItems[itemIndex];
            IGrouping<int, ElementLocation>[] pageGroups = rawItem.Elements
                .GroupBy(static location => location.PageNumber)
                .ToArray();
            for (int groupIndex = 0; groupIndex < pageGroups.Length; groupIndex++)
            {
                IGrouping<int, ElementLocation> group = pageGroups[groupIndex];
                PdfSemanticLine[] lines = group
                    .SelectMany(static location => location.Content.Lines)
                    .ToArray();
                if (!fragmentsByPage.TryGetValue(group.Key, out List<PdfSemanticBibliographyItemFragment>? pageFragments))
                {
                    pageFragments = [];
                    fragmentsByPage[group.Key] = pageFragments;
                }

                pageFragments.Add(new PdfSemanticBibliographyItemFragment(
                    itemIndex,
                    string.Join(" ", group.Select(static location => location.Content.Text.Trim())),
                    PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
                    lines,
                    groupIndex == 0,
                    groupIndex == pageGroups.Length - 1));
            }
        }

        int[] fragmentPages = fragmentsByPage.Keys.Order().ToArray();
        PdfSemanticBibliographyFragment[] fragments = fragmentPages
            .Select((pageNumber, index) => new PdfSemanticBibliographyFragment(
                bibliography,
                pageNumber,
                fragmentsByPage[pageNumber],
                index == 0,
                index == fragmentPages.Length - 1))
            .ToArray();
        return new BibliographyDetection(
            rawItems.SelectMany(static item => item.Elements).ToArray(),
            fragments);
    }

    private static IReadOnlyList<PdfSemanticPage> ApplyDetections(
        IReadOnlyList<PdfSemanticPage> pages,
        IReadOnlyList<BibliographyDetection> detections)
    {
        Dictionary<PdfSemanticElement, PdfSemanticBibliographyFragment> replacementByElement = [];
        HashSet<PdfSemanticElement> consumed = [];
        foreach (BibliographyDetection detection in detections)
        {
            foreach (PdfSemanticBibliographyFragment fragment in detection.Fragments)
            {
                ElementLocation[] pageElements = detection.ConsumedElements
                    .Where(location => location.PageNumber == fragment.PageNumber)
                    .OrderBy(static location => location.ElementIndex)
                    .DistinctBy(static location => location.Element)
                    .ToArray();
                replacementByElement[pageElements[0].Element] = fragment;
                foreach (ElementLocation location in pageElements)
                {
                    consumed.Add(location.Element);
                }
            }
        }

        return pages.Select(page =>
        {
            List<PdfSemanticElement> elements = [];
            foreach (PdfSemanticElement element in page.Elements)
            {
                if (replacementByElement.TryGetValue(element, out PdfSemanticBibliographyFragment? fragment))
                {
                    PdfSemanticLine[] lines = fragment.Items.SelectMany(static item => item.Lines).ToArray();
                    elements.Add(new PdfSemanticElement(
                        PdfSemanticElementKind.Bibliography,
                        string.Join(Environment.NewLine, fragment.Items.Select(static item => item.Text)),
                        PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
                        lines,
                        bibliographyFragment: fragment));
                }

                if (!consumed.Contains(element))
                {
                    elements.Add(element);
                }
            }

            return new PdfSemanticPage(page.PageNumber, elements);
        }).ToArray();
    }

    private static Dictionary<int, string> CitationDestinationsByNumber(PdfLayoutDocument layout)
    {
        Dictionary<int, string> destinations = [];
        HashSet<int> ambiguousNumbers = [];
        foreach (PdfLayoutPage page in layout.Pages)
        {
            foreach (PdfLayoutLink link in page.Links.Where(static link =>
                link.Kind == PdfLayoutLinkKind.Destination &&
                !link.DestinationPageNumber.HasValue &&
                !string.IsNullOrWhiteSpace(link.Destination)))
            {
                string linkedText = LinkedText(page, link);
                MatchCollection numbers = LinkedNumberPattern.Matches(linkedText);
                if (numbers.Count != 1 ||
                    !int.TryParse(numbers[0].Value, out int number) ||
                    !LooksLikeBracketedCitation(page, link, number))
                {
                    continue;
                }

                if (destinations.TryGetValue(number, out string? existing) &&
                    !string.Equals(existing, link.Destination, StringComparison.Ordinal))
                {
                    ambiguousNumbers.Add(number);
                    destinations.Remove(number);
                }
                else if (!ambiguousNumbers.Contains(number))
                {
                    destinations[number] = link.Destination!;
                }
            }
        }

        return destinations;
    }

    private static string LinkedText(PdfLayoutPage page, PdfLayoutLink link)
    {
        PdfLayoutRectangle[] bounds = link.QuadBounds.Count > 0 ? link.QuadBounds.ToArray() : [link.Bounds];
        return string.Concat(page.Glyphs
            .Where(glyph => bounds.Any(linkBounds => Intersects(linkBounds, glyph.PageBounds, 0.5f)))
            .OrderBy(static glyph => glyph.PageBounds.Y)
            .ThenBy(static glyph => glyph.PageBounds.X)
            .Select(static glyph => glyph.Text));
    }

    private static bool LooksLikeBracketedCitation(PdfLayoutPage page, PdfLayoutLink link, int number)
    {
        string context = CitationContext(page, link);
        return Regex.IsMatch(context, @"\[[^\]]*\b" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"\b[^\]]*\]");
    }

    private static string CitationContext(PdfLayoutPage page, PdfLayoutLink link)
    {
        PdfLayoutRectangle[] linkBounds = link.QuadBounds.Count > 0
            ? link.QuadBounds.ToArray()
            : [link.Bounds];
        PdfTextLine? containingLine = page.Lines
            .Where(line => linkBounds.Any(bounds => Intersects(line.Bounds, bounds, 1f)))
            .OrderBy(line => MathF.Abs(
                line.Bounds.Y + line.Bounds.Height / 2f -
                (link.Bounds.Y + link.Bounds.Height / 2f)))
            .FirstOrDefault();
        if (containingLine != null)
        {
            return containingLine.Text;
        }

        PdfLayoutRectangle expanded = new(
            MathF.Max(0f, link.Bounds.X - 12f),
            MathF.Max(0f, link.Bounds.Y - 3f),
            link.Bounds.Width + 24f,
            link.Bounds.Height + 6f);
        return string.Concat(page.Glyphs
            .Where(glyph => Intersects(expanded, glyph.PageBounds, 0f))
            .OrderBy(static glyph => glyph.PageBounds.X)
            .Select(static glyph => glyph.Text));
    }

    private static bool Intersects(PdfLayoutRectangle first, PdfLayoutRectangle second, float tolerance)
    {
        return first.X <= second.Right + tolerance &&
            first.Right + tolerance >= second.X &&
            first.Y <= second.Bottom + tolerance &&
            first.Bottom + tolerance >= second.Y;
    }

    private static bool IsBibliographyHeading(string text)
    {
        string normalized = Regex.Replace(text.Trim().TrimEnd(':'), @"\s+", " ");
        return normalized.Equals("References", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Bibliography", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Works Cited", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Literature Cited", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Reference List", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Selected References", StringComparison.OrdinalIgnoreCase);
    }

    private static string ItemText(RawBibliographyItem item)
    {
        return string.Join(" ", item.Elements.Select(static location => location.Content.Text.Trim()));
    }

    private sealed class RawBibliographyItem
    {
        public RawBibliographyItem(int? sourceNumber, string marker, int markerLength, ElementLocation firstElement)
        {
            SourceNumber = sourceNumber;
            Marker = marker;
            MarkerLength = markerLength;
            Elements = [firstElement];
        }

        public int? SourceNumber { get; }

        public string Marker { get; }

        public int MarkerLength { get; }

        public List<ElementLocation> Elements { get; }
    }

    private sealed record BibliographyDetection(
        IReadOnlyList<ElementLocation> ConsumedElements,
        IReadOnlyList<PdfSemanticBibliographyFragment> Fragments);

    private sealed record ElementLocation(
        int PageIndex,
        int ElementIndex,
        int PageNumber,
        PdfSemanticElement Element,
        PdfSemanticElement? ContentElement = null)
    {
        public PdfSemanticElement Content => ContentElement ?? Element;
    }
}
