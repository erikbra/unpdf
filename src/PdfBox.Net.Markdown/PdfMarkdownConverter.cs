using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfBox.Net.Layout;

namespace PdfBox.Net.Markdown;

/// <summary>
/// Converts a layout document to tagged-first, conservative Markdown.
/// </summary>
public static partial class PdfMarkdownConverter
{
    /// <summary>
    /// Converts an extracted layout document to Markdown.
    /// </summary>
    public static PdfMarkdownDocument Convert(PdfLayoutDocument layout, PdfMarkdownOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new PdfMarkdownOptions();

        PdfSemanticDocument semantic = PdfSemanticExtractor.Extract(
            layout,
            options.SemanticExtractionOptions);
        Dictionary<string, PdfLayoutImageAsset> imageAssets = layout.ImageAssets
            .GroupBy(static asset => asset.AssetId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        List<PdfMarkdownDiagnostic> diagnostics = [];
        List<PdfMarkdownPageResult> pageResults = [];
        HashSet<string> usedAssetIds = new(StringComparer.Ordinal);
        List<string> pageMarkdown = [];

        for (int index = 0; index < layout.Pages.Count; index++)
        {
            PdfLayoutPage page = layout.Pages[index];
            PdfSemanticPage semanticPage = semantic.Pages[index];
            int diagnosticStart = diagnostics.Count;
            PageRenderResult result = RenderPage(
                page,
                semanticPage,
                layout.TaggedStructure,
                imageAssets,
                usedAssetIds,
                options,
                diagnostics);
            if (!string.IsNullOrWhiteSpace(result.Markdown))
            {
                pageMarkdown.Add(result.Markdown.Trim());
            }

            PdfMarkdownOutputSource source = PageSource(result.UsedTagged, result.UsedFallback);
            bool hasWarnings = diagnostics
                .Skip(diagnosticStart)
                .Any(static diagnostic =>
                    diagnostic.Severity == PdfMarkdownDiagnosticSeverity.Warning);
            PdfMarkdownConfidence confidence = source switch
            {
                PdfMarkdownOutputSource.SemanticStructure when !hasWarnings => PdfMarkdownConfidence.High,
                PdfMarkdownOutputSource.SemanticStructure => PdfMarkdownConfidence.Medium,
                PdfMarkdownOutputSource.Mixed => PdfMarkdownConfidence.Medium,
                _ => PdfMarkdownConfidence.Low
            };
            pageResults.Add(new PdfMarkdownPageResult(page.PageNumber, source, confidence));
            if (options.IncludeInformationalDiagnostics)
            {
                diagnostics.Add(SourceDiagnostic(page.PageNumber, source));
            }
        }

        PdfLayoutImageAsset[] usedAssets = layout.ImageAssets
            .Where(asset => usedAssetIds.Contains(asset.AssetId))
            .ToArray();
        string markdown = string.Join(Environment.NewLine + Environment.NewLine, pageMarkdown);
        if (markdown.Length > 0)
        {
            markdown += Environment.NewLine;
        }

        return new PdfMarkdownDocument(markdown, usedAssets, diagnostics, pageResults);
    }

    private static PageRenderResult RenderPage(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfTaggedStructureDocument? taggedDocument,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        ISet<string> usedAssetIds,
        PdfMarkdownOptions options,
        List<PdfMarkdownDiagnostic> diagnostics)
    {
        List<MarkdownBlock> blocks = [];
        Dictionary<PdfTaggedStructureElement, int> structureOrder = StructureOrder(taggedDocument);
        bool usedTagged = false;
        bool usedFallback = false;
        foreach ((PdfSemanticElement element, int order) in semanticPage.Elements.Select(
            static (element, index) => (element, index)))
        {
            string? elementMarkdown = RenderElement(element, page, diagnostics);
            if (string.IsNullOrWhiteSpace(elementMarkdown))
            {
                continue;
            }

            bool tagged = element.TaggedStructure != null;
            usedTagged |= tagged;
            usedFallback |= !tagged;
            int sourceOrder = element.TaggedStructure != null &&
                structureOrder.TryGetValue(element.TaggedStructure, out int taggedOrder)
                    ? taggedOrder
                    : structureOrder.Count + order;
            blocks.Add(new MarkdownBlock(element.Bounds, sourceOrder, tagged, elementMarkdown));
        }

        if (semanticPage.Elements.Any(static element =>
            element.TaggedStructure == null &&
            element.Kind is PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer))
        {
            diagnostics.Add(new PdfMarkdownDiagnostic(
                "markdown-running-marginalia-removed",
                "Repeated untagged header or footer text was omitted from Markdown flow.",
                PdfMarkdownDiagnosticSeverity.Information,
                page.PageNumber,
                PdfMarkdownOutputSource.HeuristicFallback));
        }

        if (HasAmbiguousColumnFlow(page, semanticPage))
        {
            diagnostics.Add(new PdfMarkdownDiagnostic(
                "markdown-untagged-multicolumn-ambiguous",
                "Concurrent untagged column flow used geometric reading order and may require review.",
                PdfMarkdownDiagnosticSeverity.Warning,
                page.PageNumber,
                PdfMarkdownOutputSource.HeuristicFallback));
        }

        if (options.IncludeImages && taggedDocument != null)
        {
            foreach (PdfTaggedStructureElement figure in taggedDocument.Elements
                .Where(static element => element.Kind == PdfTaggedStructureKind.Figure))
            {
                foreach (PdfLayoutImage image in figure.DescendantContentReferences()
                    .Where(reference => reference.PageNumber == page.PageNumber)
                    .SelectMany(static reference => reference.Images)
                    .Distinct((IEqualityComparer<PdfLayoutImage>)ReferenceEqualityComparer.Instance))
                {
                    if (!imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
                    {
                        diagnostics.Add(new PdfMarkdownDiagnostic(
                            "markdown-image-asset-missing",
                            $"Tagged figure image '{image.AssetId}' has no exported asset.",
                            PdfMarkdownDiagnosticSeverity.Warning,
                            page.PageNumber,
                            PdfMarkdownOutputSource.SemanticStructure));
                        continue;
                    }

                    string alt = NormalizeText(
                        figure.AlternateDescription ??
                        image.AlternateDescription ??
                        figure.Title ??
                        "PDF figure");
                    blocks.Add(new MarkdownBlock(
                        image.Bounds,
                        structureOrder.GetValueOrDefault(figure, structureOrder.Count + blocks.Count),
                        true,
                        $"![{EscapeText(alt)}]({EscapeDestination(asset.RelativePath)})"));
                    usedAssetIds.Add(asset.AssetId);
                    usedTagged = true;
                }
            }
        }

        MarkdownBlock[] authoredBlocks = blocks
            .Where(static block => block.IsAuthored)
            .OrderBy(static block => block.SourceOrder)
            .ToArray();
        int authoredIndex = 0;
        IEnumerable<MarkdownBlock> orderedBlocks = blocks
            .OrderBy(static block => block.Bounds.Y)
            .ThenBy(static block => block.Bounds.X)
            .ThenBy(static block => block.SourceOrder)
            .Select(block => block.IsAuthored ? authoredBlocks[authoredIndex++] : block);
        string markdown = string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedBlocks.Select(static block => block.Markdown));
        return new PageRenderResult(markdown, usedTagged, usedFallback);
    }

    private static Dictionary<PdfTaggedStructureElement, int> StructureOrder(
        PdfTaggedStructureDocument? taggedDocument)
    {
        Dictionary<PdfTaggedStructureElement, int> order = new(
            (IEqualityComparer<PdfTaggedStructureElement>)ReferenceEqualityComparer.Instance);
        if (taggedDocument == null)
        {
            return order;
        }

        int index = 0;
        foreach (PdfTaggedStructureElement root in taggedDocument.Roots)
        {
            foreach (PdfTaggedStructureElement element in DescendantsAndSelf(root))
            {
                order.TryAdd(element, index++);
            }
        }

        return order;
    }

    private static string? RenderElement(
        PdfSemanticElement element,
        PdfLayoutPage page,
        List<PdfMarkdownDiagnostic> diagnostics)
    {
        return element.Kind switch
        {
            PdfSemanticElementKind.Heading => RenderHeading(element, page),
            PdfSemanticElementKind.Paragraph => RenderParagraph(element, page),
            PdfSemanticElementKind.List when element.SemanticList != null =>
                RenderList(element.SemanticList, element, page, 0),
            PdfSemanticElementKind.Table when element.TaggedStructure != null =>
                RenderTable(element, page, diagnostics),
            PdfSemanticElementKind.Table => RenderUntaggedTable(element, page, diagnostics),
            PdfSemanticElementKind.BlockQuote => RenderBlockQuote(element, page),
            PdfSemanticElementKind.CodeBlock => RenderCodeBlock(element.Text),
            PdfSemanticElementKind.AuthorBlock or
            PdfSemanticElementKind.FrontMatter or
            PdfSemanticElementKind.Navigation or
            PdfSemanticElementKind.Bibliography or
            PdfSemanticElementKind.DefinitionList or
            PdfSemanticElementKind.Aside or
            PdfSemanticElementKind.Algorithm or
            PdfSemanticElementKind.Footnote => RenderParagraph(element, page),
            _ => null
        };
    }

    private static string? RenderUntaggedTable(
        PdfSemanticElement element,
        PdfLayoutPage page,
        List<PdfMarkdownDiagnostic> diagnostics)
    {
        PdfSemanticTableRow[] rows = element.TableRows.ToArray();
        int columnCount = rows.Select(static row => row.Cells.Count).DefaultIfEmpty(0).Max();
        bool rectangular = IsRectangularTable(rows, columnCount);
        bool hasUsefulGrid = rectangular &&
            rows.Length >= 2 &&
            columnCount >= 2 &&
            rows.All(static row => row.Cells.Count(static cell =>
                !string.IsNullOrWhiteSpace(cell.Text)) >= 2);
        if (hasUsefulGrid)
        {
            diagnostics.Add(new PdfMarkdownDiagnostic(
                "markdown-untagged-table-inferred",
                "A rectangular untagged table was emitted from conservative layout inference.",
                PdfMarkdownDiagnosticSeverity.Information,
                page.PageNumber,
                PdfMarkdownOutputSource.HeuristicFallback));
            return RenderRectangularTable(
                element,
                page,
                rows,
                columnCount,
                inferFirstRowAsHeader: true);
        }

        diagnostics.Add(new PdfMarkdownDiagnostic(
            "markdown-untagged-table-degraded",
            "An inferred untagged table was emitted as plain text because its grid was ambiguous.",
            PdfMarkdownDiagnosticSeverity.Warning,
            page.PageNumber,
            PdfMarkdownOutputSource.HeuristicFallback));
        return RenderParagraph(element, page);
    }

    private static string? RenderHeading(PdfSemanticElement element, PdfLayoutPage page)
    {
        string text = RenderInlineText(element.Text, element.Lines, element.TaggedStructure, page);
        return text.Length == 0
            ? null
            : new string('#', Math.Clamp(element.HeadingLevel, 1, 6)) + " " + text;
    }

    private static string? RenderParagraph(PdfSemanticElement element, PdfLayoutPage page)
    {
        string text = RenderInlineText(element.Text, element.Lines, element.TaggedStructure, page);
        return text.Length == 0 ? null : text;
    }

    private static string? RenderBlockQuote(PdfSemanticElement element, PdfLayoutPage page)
    {
        string text = RenderInlineText(element.Text, element.Lines, element.TaggedStructure, page);
        return text.Length == 0 ? null : "> " + text;
    }

    private static string? RenderCodeBlock(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        string fence = text.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        return fence + Environment.NewLine + text + Environment.NewLine + fence;
    }

    private static string RenderList(
        PdfSemanticList list,
        PdfSemanticElement owner,
        PdfLayoutPage page,
        int depth)
    {
        StringBuilder markdown = new();
        for (int index = 0; index < list.Items.Count; index++)
        {
            PdfSemanticListItem item = list.Items[index];
            string marker = list.Kind == PdfSemanticListKind.Ordered
                ? (item.Value ?? index + (list.Start ?? 1)).ToString(CultureInfo.InvariantCulture) + "."
                : "-";
            string text = RenderInlineText(item.Text, item.Lines, owner.TaggedStructure, page);
            string indent = new(' ', depth * 4);
            markdown.Append(indent).Append(marker).Append(' ').Append(text);
            foreach (PdfSemanticList nested in item.NestedLists)
            {
                string nestedMarkdown = RenderList(nested, owner, page, depth + 1);
                if (nestedMarkdown.Length > 0)
                {
                    markdown.AppendLine().Append(nestedMarkdown);
                }
            }

            if (index < list.Items.Count - 1)
            {
                markdown.AppendLine();
            }
        }

        return markdown.ToString();
    }

    private static string? RenderTable(
        PdfSemanticElement element,
        PdfLayoutPage page,
        List<PdfMarkdownDiagnostic> diagnostics)
    {
        PdfSemanticTableRow[] rows = element.TableRows.ToArray();
        int columnCount = rows.Select(static row => row.Cells.Count).DefaultIfEmpty(0).Max();
        if (!IsRectangularTable(rows, columnCount))
        {
            diagnostics.Add(new PdfMarkdownDiagnostic(
                "markdown-table-not-rectangular",
                "A tagged table used spans or an irregular grid and was emitted as plain text.",
                PdfMarkdownDiagnosticSeverity.Warning,
                page.PageNumber,
                PdfMarkdownOutputSource.SemanticStructure));
            return RenderParagraph(element, page);
        }

        return RenderRectangularTable(
            element,
            page,
            rows,
            columnCount,
            inferFirstRowAsHeader: false);
    }

    private static bool IsRectangularTable(
        IReadOnlyList<PdfSemanticTableRow> rows,
        int columnCount)
    {
        return rows.Count > 0 &&
            columnCount > 0 &&
            rows.All(row => row.Cells.Count == columnCount) &&
            rows.SelectMany(static row => row.Cells)
                .All(static cell => cell.RowSpan == 1 && cell.ColumnSpan == 1 && !cell.IsPlaceholder);
    }

    private static string RenderRectangularTable(
        PdfSemanticElement element,
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticTableRow> rows,
        int columnCount,
        bool inferFirstRowAsHeader)
    {
        PdfSemanticTableRow? header = rows.FirstOrDefault(static row => row.IsHeader);
        if (header == null && inferFirstRowAsHeader)
        {
            header = rows[0];
        }

        IEnumerable<PdfSemanticTableRow> bodyRows = header == null
            ? rows
            : rows.Where(row => !ReferenceEquals(row, header));
        string[] headerCells = header?.Cells
            .Select(cell => RenderTableCell(cell, element, page))
            .ToArray() ?? Enumerable.Repeat("", columnCount).ToArray();
        StringBuilder markdown = new();
        WriteTableRow(markdown, headerCells);
        WriteTableRow(markdown, Enumerable.Repeat("---", columnCount));
        foreach (PdfSemanticTableRow row in bodyRows)
        {
            WriteTableRow(
                markdown,
                row.Cells.Select(cell => RenderTableCell(cell, element, page)));
        }

        return markdown.ToString().TrimEnd();
    }

    private static bool HasAmbiguousColumnFlow(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        PdfSemanticElement[] flow = semanticPage.Elements
            .Where(static element =>
                element.TaggedStructure == null &&
                element.Kind is PdfSemanticElementKind.Heading or PdfSemanticElementKind.Paragraph)
            .Where(element => element.Bounds.Width < page.Width * 0.55f)
            .ToArray();
        if (flow.Length < 4)
        {
            return false;
        }

        PdfSemanticElement[] left = flow
            .Where(element => element.Bounds.X + element.Bounds.Width / 2f < page.Width * 0.45f)
            .ToArray();
        PdfSemanticElement[] right = flow
            .Where(element => element.Bounds.X + element.Bounds.Width / 2f > page.Width * 0.55f)
            .ToArray();
        if (left.Length < 2 || right.Length < 2)
        {
            return false;
        }

        float leftTop = left.Min(static element => element.Bounds.Y);
        float leftBottom = left.Max(static element => element.Bounds.Bottom);
        float rightTop = right.Min(static element => element.Bounds.Y);
        float rightBottom = right.Max(static element => element.Bounds.Bottom);
        return MathF.Min(leftBottom, rightBottom) - MathF.Max(leftTop, rightTop) > 0f;
    }

    private static string RenderTableCell(
        PdfSemanticTableCell cell,
        PdfSemanticElement owner,
        PdfLayoutPage page)
    {
        return RenderInlineText(cell.Text, cell.Lines, owner.TaggedStructure, page)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static void WriteTableRow(StringBuilder markdown, IEnumerable<string> cells)
    {
        markdown.Append("| ")
            .Append(string.Join(" | ", cells))
            .AppendLine(" |");
    }

    private static string RenderInlineText(
        string sourceText,
        IReadOnlyList<PdfSemanticLine> lines,
        PdfTaggedStructureElement? taggedStructure,
        PdfLayoutPage page)
    {
        string text = NormalizeText(sourceText);
        if (text.Length == 0)
        {
            return "";
        }

        List<LinkSpan> spans = [];
        if (taggedStructure != null)
        {
            foreach (PdfTaggedStructureElement taggedLink in DescendantsAndSelf(taggedStructure)
                .Where(static element => element.Kind == PdfTaggedStructureKind.Link))
            {
                string linkedText = NormalizeText(TaggedText(taggedLink, page.PageNumber));
                PdfLayoutLink? link = LinkForRuns(
                    page,
                    taggedLink.DescendantContentReferences()
                        .Where(reference => reference.PageNumber == page.PageNumber)
                        .SelectMany(static reference => reference.TextRuns));
                AddLinkSpan(spans, text, linkedText, link);
            }
        }

        foreach (PdfLayoutLink link in page.Links.Where(HasTarget))
        {
            string linkedText = NormalizeText(string.Concat(lines
                .SelectMany(static line => line.Runs)
                .SelectMany(static run => run.Glyphs)
                .Where(glyph => LinkOverlapsGlyph(link, glyph.PageBounds))
                .Select(static glyph => glyph.Text)));
            AddLinkSpan(spans, text, linkedText, link);
        }

        StringBuilder markdown = new();
        int cursor = 0;
        foreach (LinkSpan span in spans
            .OrderBy(static span => span.Start)
            .ThenByDescending(static span => span.Length))
        {
            if (span.Start < cursor)
            {
                continue;
            }

            markdown.Append(EscapeText(text[cursor..span.Start]));
            markdown.Append('[')
                .Append(EscapeText(text.Substring(span.Start, span.Length)))
                .Append("](")
                .Append(EscapeDestination(span.Target))
                .Append(')');
            cursor = span.Start + span.Length;
        }

        markdown.Append(EscapeText(text[cursor..]));
        return markdown.ToString();
    }

    private static void AddLinkSpan(
        List<LinkSpan> spans,
        string text,
        string linkedText,
        PdfLayoutLink? link)
    {
        if (link == null || linkedText.Length == 0)
        {
            return;
        }

        int start = text.IndexOf(linkedText, StringComparison.Ordinal);
        if (start >= 0)
        {
            spans.Add(new LinkSpan(start, linkedText.Length, LinkTarget(link)));
        }
    }

    private static PdfLayoutLink? LinkForRuns(PdfLayoutPage page, IEnumerable<PdfTextRun> runs)
    {
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        return page.Links
            .Where(HasTarget)
            .Select(link => new
            {
                Link = link,
                Matches = glyphs.Count(glyph => LinkOverlapsGlyph(link, glyph.PageBounds))
            })
            .Where(static candidate => candidate.Matches > 0)
            .OrderByDescending(static candidate => candidate.Matches)
            .Select(static candidate => candidate.Link)
            .FirstOrDefault();
    }

    private static bool LinkOverlapsGlyph(PdfLayoutLink link, PdfLayoutRectangle glyph)
    {
        float glyphArea = glyph.Width * glyph.Height;
        return glyphArea > 0.01f && LinkBounds(link).Any(bounds =>
            IntersectionArea(bounds, glyph) >= glyphArea * 0.5f);
    }

    private static IEnumerable<PdfLayoutRectangle> LinkBounds(PdfLayoutLink link)
    {
        return link.QuadBounds.Count > 0 ? link.QuadBounds : [link.Bounds];
    }

    private static float IntersectionArea(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        float width = MathF.Max(0, MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X));
        float height = MathF.Max(0, MathF.Min(first.Bottom, second.Bottom) - MathF.Max(first.Y, second.Y));
        return width * height;
    }

    private static bool HasTarget(PdfLayoutLink link)
    {
        return !string.IsNullOrWhiteSpace(link.Uri) ||
            !string.IsNullOrWhiteSpace(link.Destination) ||
            link.DestinationPageNumber.HasValue;
    }

    private static string LinkTarget(PdfLayoutLink link)
    {
        if (!string.IsNullOrWhiteSpace(link.Uri))
        {
            return link.Uri;
        }

        if (link.DestinationPageNumber.HasValue)
        {
            return "#page-" + link.DestinationPageNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        return "#" + Uri.EscapeDataString(link.Destination ?? "");
    }

    private static string TaggedText(PdfTaggedStructureElement element, int pageNumber)
    {
        if (!string.IsNullOrEmpty(element.ActualText))
        {
            return element.ActualText;
        }

        return string.Concat(element.Kids.Select(kid => kid switch
        {
            PdfTaggedContentKid content when content.Content.PageNumber == pageNumber =>
                string.Concat(content.Content.TextRuns.Select(static run => run.Text)),
            PdfTaggedElementKid child => TaggedText(child.Element, pageNumber),
            _ => ""
        }));
    }

    private static IEnumerable<PdfTaggedStructureElement> DescendantsAndSelf(
        PdfTaggedStructureElement element)
    {
        yield return element;
        foreach (PdfTaggedStructureElement child in element.Children)
        {
            foreach (PdfTaggedStructureElement descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }

    private static string NormalizeText(string value)
    {
        return WhitespacePattern().Replace(value, " ").Trim();
    }

    private static string EscapeText(string value)
    {
        string escaped = MarkdownPunctuationPattern().Replace(value, static match => "\\" + match.Value);
        return LeadingListMarkerPattern().Replace(
            escaped,
            static match => match.Groups["number"].Success
                ? match.Groups["number"].Value + "\\" + match.Groups["punctuation"].Value
                : "\\" + match.Groups["bullet"].Value);
    }

    private static string EscapeDestination(string value)
    {
        return value
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
    }

    private static PdfMarkdownOutputSource PageSource(bool usedTagged, bool usedFallback)
    {
        if (usedTagged && usedFallback)
        {
            return PdfMarkdownOutputSource.Mixed;
        }

        if (usedTagged)
        {
            return PdfMarkdownOutputSource.SemanticStructure;
        }

        return usedFallback
            ? PdfMarkdownOutputSource.HeuristicFallback
            : PdfMarkdownOutputSource.Empty;
    }

    private static PdfMarkdownDiagnostic SourceDiagnostic(
        int pageNumber,
        PdfMarkdownOutputSource source)
    {
        return source switch
        {
            PdfMarkdownOutputSource.SemanticStructure => new PdfMarkdownDiagnostic(
                "markdown-semantic-structure-used",
                "Markdown was produced from authored tagged-PDF structure.",
                PdfMarkdownDiagnosticSeverity.Information,
                pageNumber,
                source),
            PdfMarkdownOutputSource.Mixed => new PdfMarkdownDiagnostic(
                "markdown-mixed-source-used",
                "Markdown combined authored tagged structure with conservative layout fallback.",
                PdfMarkdownDiagnosticSeverity.Information,
                pageNumber,
                source),
            PdfMarkdownOutputSource.HeuristicFallback => new PdfMarkdownDiagnostic(
                "markdown-layout-fallback-used",
                "No usable tagged structure was available; conservative layout inference was used.",
                PdfMarkdownDiagnosticSeverity.Information,
                pageNumber,
                source),
            _ => new PdfMarkdownDiagnostic(
                "markdown-page-empty",
                "No supported text or tagged figure content was found.",
                PdfMarkdownDiagnosticSeverity.Information,
                pageNumber,
                source)
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"[\\`*_#\[\]<>]")]
    private static partial Regex MarkdownPunctuationPattern();

    [GeneratedRegex(@"^(?:(?<bullet>[-+])|(?<number>\d+)(?<punctuation>[.)]))(?=\s)")]
    private static partial Regex LeadingListMarkerPattern();

    private sealed record MarkdownBlock(
        PdfLayoutRectangle Bounds,
        int SourceOrder,
        bool IsAuthored,
        string Markdown);

    private sealed record LinkSpan(int Start, int Length, string Target);

    private sealed record PageRenderResult(string Markdown, bool UsedTagged, bool UsedFallback);
}
