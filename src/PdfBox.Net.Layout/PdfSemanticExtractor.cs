using System.Text;
using System.Text.RegularExpressions;

namespace PdfBox.Net.Layout;

/// <summary>
/// Infers coarse document structure from positioned text layout.
/// </summary>
public static class PdfSemanticExtractor
{
    private static readonly Regex NumberedHeadingPattern = new(@"^(?<number>\d{1,2}(?:\.\d+)*)\s+\p{L}", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"@", RegexOptions.Compiled);
    private static readonly Regex FootnoteMarkerPattern = new(@"^[*∗†‡]\s*$", RegexOptions.Compiled);
    private static readonly Regex SymbolFootnoteMarkerPattern = new(@"^[*∗†‡]\s*$", RegexOptions.Compiled);
    private static readonly Regex NumericFootnoteMarkerPattern = new(@"^\d{1,2}\s*$", RegexOptions.Compiled);
    private static readonly Regex TableCaptionPattern = new(
        @"^\s*Table\s*(?<number>\d{1,4}(?:\.\d+)*(?:[A-Za-z])?)(?:(?<separator>\s*[:.\-–—])\s*|(?=\s|$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TableReferenceContinuationPattern = new(
        @"^(?:also\s+)?(?:can|contains?|compares?|describes?|gives?|has|illustrates?|is|lists?|presents?|provides?|reports?|shows?|summarizes?|was|were)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DateListItemBodyPattern = new(
        @"^(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+(?:(?:\d{1,2})(?:,\s*)?)?\d{4}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RuledTableRowMarkerPattern = new(
        @"^(?:[\u0095\u2022\u25e6\u25aa\u2219]\s*|\d{1,3}[.)]\s+|[A-Za-z][.)]\s+)",
        RegexOptions.Compiled);
    private static readonly Regex DocumentIndexLeaderPattern = new(
        @"(?:\.{3,}|…+|·{3,})",
        RegexOptions.Compiled);
    private static readonly Regex DocumentIndexPageLabelPattern = new(
        @"^(?:(?:[A-Z]{1,3}-?)?\d+(?:[-–]\d+)?|[IVXLCDM]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DefinitionReferencePattern = new(
        @"^(?:\[\s*\d{1,3}(?:\s*(?:,|[-–])\s*(?:\d{1,3}|adapted))*\s*\]\s*)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuoteAttributionPattern = new(
        @"^(?:[-–—]\s*)?(?:according\s+to|added|explained|noted|recalled|reported|said|stated|wrote)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CodeSyntaxPattern = new(
        @"(?:[{}\[\];]|::|->|=>|:=|^\s*[$#>]\s|\b(?:case|const|do|else|for|foreach|if|let|return|sudo|var|while)\b|--[\w-]+|(?:^|\s)[./\\][\w-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InlineCodePattern = new(
        @"^(?:(?:--?[a-z][\w-]*)|(?:[A-Za-z_][\w-]*(?:\.[A-Za-z_][\w-]*|::[A-Za-z_][\w-]*|->(?:[A-Za-z_][\w-]*))+(?:\(\))?)|(?:[A-Za-z_][\w-]*(?:\([^\s()]*\)|\[[^\s\[\]]+\]))|(?:[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+)|(?:[a-z]+[A-Z][A-Za-z0-9]*))$",
        RegexOptions.Compiled);
    private static readonly Regex AlgorithmCaptionPattern = new(
        @"^Algorithm\b(?:\s+\d+)?\s*[:.]?\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FigureCaptionPattern = new(
        @"^\s*(?:Figure|Fig\.)\s*\d{1,4}(?:\.\d+)*(?:[A-Za-z])?\s*[:.\-–—]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AlgorithmKeywordPattern = new(
        @"\b(?:Require|while|do|end|return)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private const int MaximumDetectedTableColumnCount = 16;

    public static PdfSemanticDocument Extract(PdfLayoutDocument layout, PdfSemanticExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new PdfSemanticExtractionOptions();
        PdfSemanticPage[] pages = layout.Pages.Select(page => ExtractPage(page, options)).ToArray();
        pages = LinkDefinitionListContinuations(pages);
        pages = LinkFootnoteContinuations(layout.Pages, pages);
        pages = PdfSemanticBibliographyExtractor.Extract(layout, pages).ToArray();
        pages = pages
            .Select((page, index) => AddThematicBreaks(layout.Pages[index], page))
            .ToArray();
        for (int index = 0; index < pages.Length; index++)
        {
            PdfSemanticInlineInference.Apply(layout.Pages[index], pages[index]);
        }

        return new PdfSemanticDocument(pages);
    }

    private static PdfSemanticPage AddThematicBreaks(PdfLayoutPage page, PdfSemanticPage semanticPage)
    {
        PdfSemanticElement[] flowRegions = semanticPage.Elements
            .Where(IsThematicBreakFlowRegion)
            .OrderBy(static element => element.Bounds.Y)
            .ThenBy(static element => element.Bounds.X)
            .ToArray();
        if (flowRegions.Length < 2)
        {
            return semanticPage;
        }

        List<PdfSemanticElement> thematicBreaks = [];
        foreach (PdfLayoutPath path in page.Paths)
        {
            if (!TryCreateThematicRuleCandidate(page, path, out ThematicRuleCandidate candidate) ||
                IsOwnedThematicRuleCandidate(page, semanticPage, path, candidate) ||
                !TryGetThematicBreakNeighbors(
                    page,
                    semanticPage.Elements,
                    flowRegions,
                    candidate,
                    out PdfSemanticElement? previous,
                    out PdfSemanticElement? next))
            {
                continue;
            }

            thematicBreaks.Add(new PdfSemanticElement(
                PdfSemanticElementKind.ThematicBreak,
                "",
                candidate.Bounds,
                [],
                thematicBreak: new PdfSemanticThematicBreak(
                    path.Index,
                    candidate.Thickness,
                    candidate.Color,
                    ThematicBreakAlignment(page, candidate.Bounds, previous!, next!))));
        }

        if (thematicBreaks.Count == 0)
        {
            return semanticPage;
        }

        return new PdfSemanticPage(
            semanticPage.PageNumber,
            semanticPage.Elements
                .Concat(thematicBreaks)
                .OrderBy(static element => element.Bounds.Y)
                .ThenBy(static element => element.Bounds.X)
                .ToArray());
    }

    private static bool IsThematicBreakFlowRegion(PdfSemanticElement element)
    {
        return element.Kind is PdfSemanticElementKind.Heading or
            PdfSemanticElementKind.Paragraph or
            PdfSemanticElementKind.List or
            PdfSemanticElementKind.Table or
            PdfSemanticElementKind.AuthorBlock or
            PdfSemanticElementKind.FrontMatter or
            PdfSemanticElementKind.Navigation or
            PdfSemanticElementKind.Bibliography or
            PdfSemanticElementKind.DefinitionList or
            PdfSemanticElementKind.BlockQuote or
            PdfSemanticElementKind.Aside or
            PdfSemanticElementKind.CodeBlock or
            PdfSemanticElementKind.Algorithm;
    }

    private static bool TryCreateThematicRuleCandidate(
        PdfLayoutPage page,
        PdfLayoutPath path,
        out ThematicRuleCandidate candidate)
    {
        candidate = default;
        if (path.UsesShapeAlpha || path.UsesSoftMask ||
            path.Stroke?.DashArray.Any(static dash => dash > 0f) == true)
        {
            return false;
        }

        PdfLayoutRectangle sourceBounds;
        float thickness;
        PdfLayoutColor color;
        bool directStroke = path.IsStroked &&
            path.Commands.Count == 2 &&
            path.Commands[0].Kind == PdfLayoutPathCommandKind.MoveTo &&
            path.Commands[1].Kind == PdfLayoutPathCommandKind.LineTo;
        if (directStroke)
        {
            PdfLayoutPathCommand start = path.Commands[0];
            PdfLayoutPathCommand end = path.Commands[1];
            float width = MathF.Abs(end.X1 - start.X1);
            float rise = MathF.Abs(end.Y1 - start.Y1);
            thickness = MathF.Max(0.01f, path.Stroke!.Width);
            if (rise > MathF.Max(0.75f, thickness * 0.5f))
            {
                return false;
            }

            float centerY = (start.Y1 + end.Y1) / 2f;
            sourceBounds = new PdfLayoutRectangle(
                MathF.Min(start.X1, end.X1),
                centerY - thickness / 2f,
                width,
                thickness);
            color = path.Stroke.Color;
        }
        else if (path.IsFilled &&
            path.Bounds.Height > 0.01f &&
            path.Bounds.Height <= 4f &&
            path.Commands.All(static command => command.Kind != PdfLayoutPathCommandKind.CurveTo))
        {
            sourceBounds = path.Bounds;
            thickness = MathF.Max(path.Bounds.Height, path.Stroke?.Width ?? 0f);
            color = path.FillColor!.Value;
        }
        else
        {
            return false;
        }

        if (sourceBounds.Width < page.Width * 0.18f ||
            sourceBounds.Width > page.Width * 0.90f ||
            sourceBounds.Width < MathF.Max(24f, sourceBounds.Height * 20f) ||
            sourceBounds.Y < page.Height * 0.06f ||
            sourceBounds.Bottom > page.Height * 0.94f ||
            color.Alpha <= 0.01f)
        {
            return false;
        }

        candidate = new ThematicRuleCandidate(sourceBounds, thickness, color);
        return true;
    }

    private static bool IsOwnedThematicRuleCandidate(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutPath path,
        ThematicRuleCandidate candidate)
    {
        if (IsTitleRuleCandidate(page, semanticPage.Elements, candidate.Bounds) ||
            IsFootnoteRuleCandidate(page, semanticPage.Elements, candidate.Bounds) ||
            IsTableRuleCandidate(semanticPage.Elements, candidate.Bounds) ||
            IsAlgorithmRuleCandidate(semanticPage.Elements, path.Index) ||
            page.FormControls.Any(control => Intersects(
                ExpandRectangle(control.Bounds, 6f, 6f),
                candidate.Bounds)) ||
            page.Images.Any(image => Intersects(
                ExpandRectangle(image.Bounds, 18f, 18f),
                candidate.Bounds)) ||
            page.VectorGroups.Any(group =>
                group.HasPaths &&
                path.Index >= group.FirstPathIndex &&
                path.Index <= group.LastPathIndex))
        {
            return true;
        }

        return IsConnectedToOtherVector(page, path, candidate.Bounds);
    }

    private static bool IsTitleRuleCandidate(
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticElement> elements,
        PdfLayoutRectangle ruleBounds)
    {
        return elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Heading && element.IsDocumentTitle)
            .Any(title =>
            {
                bool spansTitle = ruleBounds.Width >= MathF.Max(title.Bounds.Width * 0.90f, page.Width * 0.40f) &&
                    ruleBounds.X <= title.Bounds.X + 8f &&
                    ruleBounds.Right >= title.Bounds.Right - 8f;
                if (!spansTitle)
                {
                    return false;
                }

                float gapAbove = title.Bounds.Y - ruleBounds.Bottom;
                float gapBelow = ruleBounds.Y - title.Bounds.Bottom;
                return gapAbove is >= -3f and <= 32f || gapBelow is >= -3f and <= 32f;
            });
    }

    private static bool IsFootnoteRuleCandidate(
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticElement> elements,
        PdfLayoutRectangle ruleBounds)
    {
        PdfSemanticElement[] footnotes = elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Footnote)
            .ToArray();
        if (footnotes.Length == 0)
        {
            return false;
        }

        float footnoteTop = footnotes.Min(static footnote => footnote.Bounds.Y);
        float footnoteLeft = footnotes.Min(static footnote => footnote.Bounds.X);
        return ruleBounds.Width >= page.Width * 0.10f &&
            ruleBounds.Width <= page.Width * 0.45f &&
            ruleBounds.X <= footnoteLeft + 16f &&
            ruleBounds.Y <= footnoteTop + 4f &&
            footnoteTop - ruleBounds.Y <= 28f;
    }

    private static bool IsTableRuleCandidate(
        IReadOnlyList<PdfSemanticElement> elements,
        PdfLayoutRectangle ruleBounds)
    {
        return elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Table)
            .Any(table => Intersects(ExpandRectangle(table.Bounds, 10f, 10f), ruleBounds));
    }

    private static bool IsAlgorithmRuleCandidate(
        IReadOnlyList<PdfSemanticElement> elements,
        int sourcePathIndex)
    {
        return elements
            .Where(static element => element.Algorithm != null)
            .SelectMany(static element => new[]
            {
                element.Algorithm!.TopRule.SourcePathIndex,
                element.Algorithm.CaptionRule.SourcePathIndex,
                element.Algorithm.BottomRule.SourcePathIndex
            })
            .Contains(sourcePathIndex);
    }

    private static bool IsConnectedToOtherVector(
        PdfLayoutPage page,
        PdfLayoutPath source,
        PdfLayoutRectangle ruleBounds)
    {
        PdfLayoutRectangle nearby = ExpandRectangle(ruleBounds, 8f, 12f);
        foreach (PdfLayoutPath path in page.Paths.Where(path => !ReferenceEquals(path, source)))
        {
            if (Intersects(nearby, ExpandPathBounds(path)))
            {
                return true;
            }

            foreach (PdfLayoutRectangle segment in PathRuleSegments(path))
            {
                bool joinsRule = segment.Height >= 6f &&
                    segment.Height >= MathF.Max(0.1f, segment.Width) * 8f &&
                    segment.X + segment.Width / 2f >= ruleBounds.X - 2f &&
                    segment.X + segment.Width / 2f <= ruleBounds.Right + 2f &&
                    segment.Y <= ruleBounds.Bottom + 2f &&
                    segment.Bottom >= ruleBounds.Y - 2f;
                if (joinsRule)
                {
                    return true;
                }
            }

            if (TryCreateThematicRuleCandidate(page, path, out ThematicRuleCandidate parallel) &&
                MathF.Abs(parallel.Bounds.X - ruleBounds.X) <= 4f &&
                MathF.Abs(parallel.Bounds.Right - ruleBounds.Right) <= 4f &&
                MathF.Abs(
                    parallel.Bounds.Y + parallel.Bounds.Height / 2f -
                    (ruleBounds.Y + ruleBounds.Height / 2f)) <= page.Height * 0.12f)
            {
                return true;
            }
        }

        return false;
    }

    private static PdfLayoutRectangle ExpandPathBounds(PdfLayoutPath path)
    {
        float strokeInset = MathF.Max(0f, (path.Stroke?.Width ?? 0f) / 2f);
        return ExpandRectangle(path.Bounds, strokeInset, strokeInset);
    }

    private static bool TryGetThematicBreakNeighbors(
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticElement> semanticElements,
        IReadOnlyList<PdfSemanticElement> flowRegions,
        ThematicRuleCandidate candidate,
        out PdfSemanticElement? previous,
        out PdfSemanticElement? next)
    {
        previous = flowRegions
            .Where(element => element.Bounds.Bottom <= candidate.Bounds.Y + 0.5f)
            .Where(element => SharesThematicFlowLane(candidate.Bounds, element.Bounds))
            .OrderByDescending(static element => element.Bounds.Bottom)
            .ThenBy(static element => element.Bounds.X)
            .FirstOrDefault();
        next = flowRegions
            .Where(element => element.Bounds.Y >= candidate.Bounds.Bottom - 0.5f)
            .Where(element => SharesThematicFlowLane(candidate.Bounds, element.Bounds))
            .OrderBy(static element => element.Bounds.Y)
            .ThenBy(static element => element.Bounds.X)
            .FirstOrDefault();
        if (previous == null || next == null || ReferenceEquals(previous, next))
        {
            return false;
        }

        float[] fontSizes = flowRegions
            .SelectMany(static element => element.Lines)
            .Select(static line => line.DominantFontSize)
            .Where(static size => size > 0f)
            .Order()
            .ToArray();
        float bodySize = fontSizes.Length == 0 ? 10f : fontSizes[fontSizes.Length / 2];
        float gapAbove = candidate.Bounds.Y - previous.Bounds.Bottom;
        float gapBelow = next.Bounds.Y - candidate.Bounds.Bottom;
        float minimumSideGap = MathF.Max(5f, bodySize * 0.60f);
        float minimumTransitionGap = MathF.Max(22f, bodySize * 2.4f);
        if (gapAbove < minimumSideGap ||
            gapBelow < minimumSideGap ||
            gapAbove + gapBelow < minimumTransitionGap)
        {
            return false;
        }

        if (HasSideBySideFlowOutsideRuleLane(page, candidate.Bounds, previous, next))
        {
            return false;
        }

        PdfLayoutRectangle transitionRegion = new(
            MathF.Min(previous.Bounds.X, next.Bounds.X),
            previous.Bounds.Bottom,
            MathF.Max(previous.Bounds.Right, next.Bounds.Right) - MathF.Min(previous.Bounds.X, next.Bounds.X),
            next.Bounds.Y - previous.Bounds.Bottom);
        if (HasInterveningSemanticElement(semanticElements, previous, next, candidate.Bounds) ||
            page.FormControls.Any(control => Intersects(control.Bounds, transitionRegion)))
        {
            return false;
        }

        return true;

        static bool HasInterveningSemanticElement(
            IReadOnlyList<PdfSemanticElement> elements,
            PdfSemanticElement before,
            PdfSemanticElement after,
            PdfLayoutRectangle rule)
        {
            return elements.Any(element =>
                !ReferenceEquals(element, before) &&
                !ReferenceEquals(element, after) &&
                element.Bounds.Y < after.Bounds.Y &&
                element.Bounds.Bottom > before.Bounds.Bottom &&
                SharesThematicFlowLane(rule, element.Bounds));
        }
    }

    private static bool HasSideBySideFlowOutsideRuleLane(
        PdfLayoutPage page,
        PdfLayoutRectangle ruleBounds,
        PdfSemanticElement previous,
        PdfSemanticElement next)
    {
        float transitionTop = previous.Bounds.Bottom;
        float transitionBottom = next.Bounds.Y;
        float laneGutter = MathF.Max(8f, page.Width * 0.015f);
        return page.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .Where(run =>
            {
                float centerY = run.Bounds.Y + run.Bounds.Height / 2f;
                return centerY > transitionTop && centerY < transitionBottom;
            })
            .Any(run =>
                run.Bounds.Right <= ruleBounds.X - laneGutter ||
                run.Bounds.X >= ruleBounds.Right + laneGutter);
    }

    private static bool SharesThematicFlowLane(
        PdfLayoutRectangle ruleBounds,
        PdfLayoutRectangle contentBounds)
    {
        float overlap = HorizontalOverlap(ruleBounds, contentBounds);
        return overlap >= MathF.Min(ruleBounds.Width, contentBounds.Width) * 0.35f ||
            ruleBounds.X + ruleBounds.Width / 2f >= contentBounds.X - 12f &&
            ruleBounds.X + ruleBounds.Width / 2f <= contentBounds.Right + 12f;
    }

    private static PdfSemanticThematicBreakAlignment ThematicBreakAlignment(
        PdfLayoutPage page,
        PdfLayoutRectangle ruleBounds,
        PdfSemanticElement previous,
        PdfSemanticElement next)
    {
        float flowLeft = MathF.Min(previous.Bounds.X, next.Bounds.X);
        float flowRight = MathF.Max(previous.Bounds.Right, next.Bounds.Right);
        float tolerance = MathF.Max(6f, (flowRight - flowLeft) * 0.04f);
        float ruleCenter = ruleBounds.X + ruleBounds.Width / 2f;
        float flowCenter = flowLeft + (flowRight - flowLeft) / 2f;
        if (MathF.Abs(ruleCenter - page.Width / 2f) <= page.Width * 0.04f ||
            MathF.Abs(ruleCenter - flowCenter) <= tolerance)
        {
            return PdfSemanticThematicBreakAlignment.Center;
        }

        if (MathF.Abs(ruleBounds.X - flowLeft) <= tolerance)
        {
            return PdfSemanticThematicBreakAlignment.Left;
        }

        if (MathF.Abs(ruleBounds.Right - flowRight) <= tolerance)
        {
            return PdfSemanticThematicBreakAlignment.Right;
        }

        return ruleCenter < flowCenter
            ? PdfSemanticThematicBreakAlignment.Left
            : PdfSemanticThematicBreakAlignment.Right;
    }

    private static PdfSemanticPage[] LinkFootnoteContinuations(
        IReadOnlyList<PdfLayoutPage> layoutPages,
        IReadOnlyList<PdfSemanticPage> sourcePages)
    {
        List<PdfSemanticElement>[] pages = sourcePages
            .Select(static page => page.Elements.ToList())
            .ToArray();
        for (int pageIndex = 1; pageIndex < pages.Length; pageIndex++)
        {
            List<PdfSemanticElement> previousElements = pages[pageIndex - 1];
            List<PdfSemanticElement> currentElements = pages[pageIndex];
            int previousNoteIndex = previousElements.FindLastIndex(static element =>
                element.Kind == PdfSemanticElementKind.Footnote && element.Note != null);
            if (previousNoteIndex < 0 ||
                previousElements.Skip(previousNoteIndex + 1).Any(static element =>
                    element.Kind is not (PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer)))
            {
                continue;
            }

            PdfSemanticElement previous = previousElements[previousNoteIndex];
            if (previous.Note!.ContinuesOnNextPage ||
                previous.Bounds.Bottom < layoutPages[pageIndex - 1].Height * 0.70f ||
                EndsSentence(previous.Text))
            {
                continue;
            }

            int continuationIndex = currentElements.FindIndex(static element =>
                element.Kind is not (PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer));
            if (continuationIndex < 0 ||
                currentElements.Take(continuationIndex).Any(static element =>
                    element.Kind is not (PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer)))
            {
                continue;
            }

            PdfSemanticElement continuation = currentElements[continuationIndex];
            string continuationText = continuation.Text.TrimStart();
            if (continuation.Kind != PdfSemanticElementKind.Paragraph ||
                continuationText.Length == 0 ||
                !char.IsLower(continuationText[0]) ||
                continuation.Bounds.Y > layoutPages[pageIndex].Height * 0.20f ||
                !HasCompatibleNoteFontSize(previous, continuation))
            {
                continue;
            }

            previousElements[previousNoteIndex] = WithNote(
                previous,
                new PdfSemanticNote(
                    previous.Note.Marker,
                    previous.Note.ContinuesPreviousNote,
                    continuesOnNextPage: true));
            currentElements[continuationIndex] = WithNote(
                continuation,
                new PdfSemanticNote(previous.Note.Marker, continuesPreviousNote: true),
                PdfSemanticElementKind.Footnote);
        }

        return sourcePages
            .Select((page, index) => new PdfSemanticPage(page.PageNumber, pages[index]))
            .ToArray();
    }

    private static bool HasCompatibleNoteFontSize(PdfSemanticElement first, PdfSemanticElement second)
    {
        float firstSize = first.Lines.Select(static line => line.DominantFontSize).DefaultIfEmpty(0f).Average();
        float secondSize = second.Lines.Select(static line => line.DominantFontSize).DefaultIfEmpty(0f).Average();
        return firstSize > 0f &&
            secondSize >= firstSize * 0.75f &&
            secondSize <= firstSize * 1.25f;
    }

    private static PdfSemanticElement WithNote(
        PdfSemanticElement source,
        PdfSemanticNote note,
        PdfSemanticElementKind? kind = null)
    {
        return new PdfSemanticElement(
            kind ?? source.Kind,
            source.Text,
            source.Bounds,
            source.Lines,
            source.HeadingLevel,
            source.TableRows,
            source.SemanticList,
            source.DocumentIndex,
            source.IsDocumentTitle,
            source.BibliographyFragment,
            source.DefinitionList,
            source.Quotation,
            source.Aside,
            note,
            source.ThematicBreak);
    }

    private static PdfSemanticPage[] LinkDefinitionListContinuations(IReadOnlyList<PdfSemanticPage> sourcePages)
    {
        List<PdfSemanticElement>[] pages = sourcePages
            .Select(static page => page.Elements.ToList())
            .ToArray();
        for (int pageIndex = 1; pageIndex < pages.Length; pageIndex++)
        {
            List<PdfSemanticElement> previousElements = pages[pageIndex - 1];
            List<PdfSemanticElement> currentElements = pages[pageIndex];
            int previousListIndex = previousElements.FindLastIndex(static element =>
                element.Kind == PdfSemanticElementKind.DefinitionList && element.DefinitionList != null);
            int currentListIndex = currentElements.FindIndex(static element =>
                element.Kind == PdfSemanticElementKind.DefinitionList && element.DefinitionList != null);
            if (previousListIndex < 0 || currentListIndex < 0)
            {
                continue;
            }

            PdfSemanticElement previousListElement = previousElements[previousListIndex];
            PdfSemanticElement currentListElement = currentElements[currentListIndex];
            PdfSemanticDefinitionListEntry? previousEntry = previousListElement.DefinitionList!.Entries.LastOrDefault();
            if (previousEntry == null ||
                previousListElement.DefinitionList.TermColumnWidth.HasValue !=
                currentListElement.DefinitionList!.TermColumnWidth.HasValue)
            {
                continue;
            }

            int continuationIndex = -1;
            int continuationLineIndex = -1;
            if (!EndsSentence(previousEntry.Definition.Text))
            {
                for (int index = 0; index < currentListIndex; index++)
                {
                    PdfSemanticElement candidate = currentElements[index];
                    if (candidate.Kind != PdfSemanticElementKind.Paragraph || candidate.Text.Length < 8)
                    {
                        continue;
                    }

                    int lineIndex = candidate.Lines
                        .Select(static line => line.Text.TrimStart())
                        .Select((text, index) => (text, index))
                        .Where(static item => item.text.Length > 0 && char.IsLower(item.text[0]))
                        .Select(static item => item.index)
                        .DefaultIfEmpty(-1)
                        .First();
                    if (lineIndex >= 0)
                    {
                        continuationIndex = index;
                        continuationLineIndex = lineIndex;
                        break;
                    }
                }
            }

            bool previousSuffixIsOnlyPageArtifacts = previousElements
                .Skip(previousListIndex + 1)
                .All(IsDefinitionListPageArtifact);
            bool currentPrefixIsOnlyPageArtifacts = currentElements
                .Take(currentListIndex)
                .Select((element, index) => (element, index))
                .All(item => item.index == continuationIndex || IsDefinitionListPageArtifact(item.element));
            if (!previousSuffixIsOnlyPageArtifacts || !currentPrefixIsOnlyPageArtifacts)
            {
                continue;
            }

            for (int index = previousListIndex + 1; index < previousElements.Count; index++)
            {
                if (previousElements[index].Kind == PdfSemanticElementKind.Paragraph)
                {
                    previousElements[index] = CreateSemanticLinesElement(
                        PdfSemanticElementKind.Footer,
                        previousElements[index].Lines);
                }
            }

            for (int index = 0; index < currentListIndex; index++)
            {
                if (index != continuationIndex && currentElements[index].Kind == PdfSemanticElementKind.Paragraph)
                {
                    currentElements[index] = CreateSemanticLinesElement(
                        PdfSemanticElementKind.Header,
                        currentElements[index].Lines);
                }
            }

            PdfSemanticDefinitionList previousList = new(
                previousListElement.DefinitionList.Entries,
                previousListElement.DefinitionList.TermColumnWidth,
                previousListElement.DefinitionList.ColumnGap,
                previousListElement.DefinitionList.ContinuesPreviousList,
                continuesOnNextPage: true);
            PdfSemanticDefinitionList currentList = new(
                currentListElement.DefinitionList.Entries,
                currentListElement.DefinitionList.TermColumnWidth,
                currentListElement.DefinitionList.ColumnGap,
                continuesPreviousList: true,
                currentListElement.DefinitionList.ContinuesOnNextPage);

            PdfSemanticLine[] continuationLines = [];
            if (continuationIndex >= 0 && continuationLineIndex >= 0)
            {
                PdfSemanticElement continuation = currentElements[continuationIndex];
                continuationLines = continuation.Lines.Skip(continuationLineIndex).ToArray();
                PdfSemanticDefinitionContent continuationContent = new(
                    JoinParagraphLines(continuationLines),
                    PdfLayoutRectangle.Union(continuationLines.Select(static line => line.Bounds)),
                    continuationLines);
                PdfSemanticDefinitionListEntry continuedPreviousEntry = new(
                    previousEntry.Terms,
                    previousEntry.Definition,
                    previousEntry.ContinuesPreviousDefinition,
                    continuesOnNextPage: true);
                previousList = new PdfSemanticDefinitionList(
                    previousList.Entries.SkipLast(1).Append(continuedPreviousEntry).ToArray(),
                    previousList.TermColumnWidth,
                    previousList.ColumnGap,
                    previousList.ContinuesPreviousList,
                    previousList.ContinuesOnNextPage);

                PdfSemanticDefinitionListEntry continuationEntry = new(
                    [],
                    continuationContent,
                    continuesPreviousDefinition: true);
                currentList = new PdfSemanticDefinitionList(
                    currentList.Entries.Prepend(continuationEntry).ToArray(),
                    currentList.TermColumnWidth,
                    currentList.ColumnGap,
                    currentList.ContinuesPreviousList,
                    currentList.ContinuesOnNextPage);
                if (continuationLineIndex == 0)
                {
                    currentElements.RemoveAt(continuationIndex);
                    currentListIndex--;
                }
                else
                {
                    currentElements[continuationIndex] = CreateSemanticLinesElement(
                        PdfSemanticElementKind.Header,
                        continuation.Lines.Take(continuationLineIndex).ToArray());
                }
            }

            previousElements[previousListIndex] = WithDefinitionList(previousListElement, previousList);
            currentElements[currentListIndex] = WithDefinitionList(
                currentListElement,
                currentList,
                continuationLines);
        }

        return sourcePages
            .Select((page, index) => new PdfSemanticPage(page.PageNumber, pages[index]))
            .ToArray();
    }

    private static bool IsDefinitionListPageArtifact(PdfSemanticElement element)
    {
        if (element.Kind is PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer)
        {
            return true;
        }

        string text = element.Text.Trim();
        return element.Kind == PdfSemanticElementKind.Paragraph &&
            text.Length <= 32 &&
            CountWords(text) <= 5;
    }

    private static PdfSemanticElement WithDefinitionList(
        PdfSemanticElement source,
        PdfSemanticDefinitionList definitionList,
        IEnumerable<PdfSemanticLine>? additionalLines = null)
    {
        PdfSemanticLine[] lines = source.Lines
            .Concat(additionalLines ?? [])
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        string text = string.Join(
            Environment.NewLine,
            definitionList.Entries.Select(static entry =>
                string.Join("; ", entry.Terms.Select(static term => term.Text)) + "\t" + entry.Definition.Text));
        return new PdfSemanticElement(
            PdfSemanticElementKind.DefinitionList,
            text,
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines,
            definitionList: definitionList);
    }

    private static PdfSemanticElement CreateSemanticLinesElement(
        PdfSemanticElementKind kind,
        IReadOnlyList<PdfSemanticLine> lines)
    {
        return new PdfSemanticElement(
            kind,
            kind == PdfSemanticElementKind.Paragraph
                ? JoinParagraphLines(lines)
                : string.Join(Environment.NewLine, lines.Select(static line => line.Text)),
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines);
    }

    private static PdfSemanticPage ExtractPage(PdfLayoutPage page, PdfSemanticExtractionOptions options)
    {
        RuledTableRegion[] ruledTableRegions = DetectRuledTableRegions(page);
        HorizontalTableLane[] horizontalTableLanes = DetectHorizontalTableLanes(page, ruledTableRegions);
        PdfLayoutRectangle[] horizontalTableBodies = horizontalTableLanes
            .Select(static lane => lane.RuleBounds)
            .ToArray();
        TableCandidateRegion[] tableCandidateRegions = ruledTableRegions
            .Select(region => new TableCandidateRegion(
                region.Bounds,
                FindHorizontalTableLane(region.Bounds, horizontalTableLanes)))
            .Concat(horizontalTableLanes.Select((lane, index) =>
                new TableCandidateRegion(lane.ExpandedBounds, index)))
            .ToArray();
        LineCandidate[] lines = CreateLineCandidates(
            page,
            tableCandidateRegions,
            options)
            .Where(static line => line.Text.Length > 0)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        if (lines.Length == 0)
        {
            return new PdfSemanticPage(page.PageNumber, []);
        }

        float bodyFontSize = EstimateBodyFontSize(lines);
        float lineStep = EstimateLineStep(lines, bodyFontSize);
        PdfLayoutRectangle[] horizontalRuleTableRegions = DetectHorizontalRuleTableRegions(
            page,
            lines,
            bodyFontSize,
            options);
        AlgorithmCandidate[] algorithms = DetectAlgorithms(page, lines, lineStep);
        HashSet<int> algorithmLineIndexes = algorithms
            .SelectMany(static algorithm => algorithm.SourceLines)
            .Select(static line => line.Index)
            .ToHashSet();
        CodeBlockCandidate[] codeBlocks = DetectCodeBlocks(page, lines, lineStep);
        HashSet<int> codeLineIndexes = codeBlocks
            .SelectMany(static block => block.Lines)
            .Select(static line => line.Line.Index)
            .ToHashSet();
        HashSet<int> consumed = [];
        List<PdfSemanticElement> elements = [];

        foreach (AlgorithmCandidate algorithm in algorithms)
        {
            if (algorithm.SourceLines.All(line => !consumed.Contains(line.Index)))
            {
                elements.Add(CreateAlgorithm(algorithm, consumed));
            }
        }

        LineCandidate[] headingLines = lines
            .Where(line => !IsInsideRuledTableRegion(line, ruledTableRegions))
            .Where(line => line.Source.Runs.Count < 3 ||
                !IsInsideTableLaneRegion(line, horizontalTableBodies))
            .Where(line => line.Source.Runs.Count < 3 ||
                !IsInsideHorizontalRuleTable(line.Bounds, horizontalRuleTableRegions))
            .Where(line => !algorithmLineIndexes.Contains(line.Index))
            .Where(line => !codeLineIndexes.Contains(line.Index))
            .Where(line => IsHeading(line, page, lines, bodyFontSize, lineStep, options))
            .ToArray();
        LineCandidate? titleCandidate = headingLines
            .Where(line => line.Bounds.Y < page.Height * 0.55f)
            .OrderByDescending(static line => line.FontSize)
            .ThenBy(static line => line.Bounds.Y)
            .FirstOrDefault();
        LineCandidate? documentTitle = titleCandidate != null && IsDocumentTitle(titleCandidate, page, bodyFontSize)
            ? titleCandidate
            : null;
        LineCandidate[] documentTitleLines = documentTitle == null
            ? []
            : GroupDocumentTitleLines(documentTitle, headingLines, page, bodyFontSize, lineStep);

        LineCandidate[] headerLines;
        if (documentTitle != null)
        {
            headerLines = lines
                .Where(line => line.Bounds.Bottom < documentTitle.Bounds.Y - lineStep * 0.5f)
                .ToArray();
        }
        else
        {
            headerLines = lines
                .Where(line => line.Bounds.Y < page.Height * 0.055f)
                .Where(line => !headingLines.Contains(line) || line.FontSize < bodyFontSize + 5f)
                .ToArray();
        }

        foreach (PdfSemanticElement header in GroupHeaders(headerLines, lineStep, consumed))
        {
            elements.Add(header);
        }

        foreach (LineCandidate line in lines.Where(line => IsFooter(line, page, bodyFontSize)))
        {
            if (consumed.Add(line.Index))
            {
                elements.Add(CreateElement(PdfSemanticElementKind.Footer, [line]));
            }
        }

        foreach (PdfSemanticElement table in ExtractRuledTables(
            ruledTableRegions,
            lines,
            consumed,
            options))
        {
            elements.Add(table);
        }

        PdfSemanticElement? frontMatter = ExtractScientificFrontMatter(
            page,
            lines,
            documentTitleLines,
            consumed,
            options);
        if (frontMatter != null)
        {
            elements.Add(frontMatter);
        }

        foreach (PdfSemanticElement author in ExtractAuthorBlocks(
            page,
            lines,
            documentTitleLines,
            headingLines,
            options,
            consumed))
        {
            elements.Add(author);
        }

        foreach (PdfSemanticElement footnote in ExtractFootnotes(page, lines, consumed))
        {
            elements.Add(footnote);
        }

        foreach (PdfSemanticElement documentIndex in ExtractDocumentIndexes(
            page,
            lines,
            bodyFontSize,
            lineStep,
            consumed))
        {
            elements.Add(documentIndex);
        }

        foreach (PdfSemanticElement definitionList in ExtractDefinitionLists(
            page,
            lines,
            bodyFontSize,
            lineStep,
            consumed,
            options))
        {
            elements.Add(definitionList);
        }

        foreach (CodeBlockCandidate codeBlock in codeBlocks)
        {
            if (codeBlock.Lines.All(line => !consumed.Contains(line.Line.Index)))
            {
                elements.Add(CreateCodeBlock(codeBlock, consumed));
            }
        }

        HashSet<int> documentTitleLineIndexes = documentTitleLines
            .Select(static line => line.Index)
            .ToHashSet();
        foreach (LineCandidate line in headingLines)
        {
            if (documentTitleLineIndexes.Contains(line.Index))
            {
                if (line.Index == documentTitleLines[0].Index &&
                    documentTitleLines.All(titleLine => !consumed.Contains(titleLine.Index)))
                {
                    foreach (LineCandidate titleLine in documentTitleLines)
                    {
                        consumed.Add(titleLine.Index);
                    }

                    elements.Add(CreateElement(
                        PdfSemanticElementKind.Heading,
                        MergeSameBaselineLines(documentTitleLines, options),
                        headingLevel: HeadingLevel(documentTitleLines[0], bodyFontSize),
                        isDocumentTitle: true));
                }

                continue;
            }

            if (consumed.Add(line.Index))
            {
                int level = HeadingLevel(line, bodyFontSize);
                elements.Add(CreateElement(PdfSemanticElementKind.Heading, [line], headingLevel: level));
            }
        }

        foreach (PdfSemanticElement caption in ExtractFigureCaptions(
            lines,
            lineStep,
            consumed,
            options))
        {
            elements.Add(caption);
        }

        foreach (PdfSemanticElement formula in ExtractNumberedDisplayFormulas(
            lines,
            bodyFontSize,
            consumed,
            options))
        {
            elements.Add(formula);
        }

        foreach (PdfSemanticElement table in ExtractTextTables(
            page,
            lines,
            bodyFontSize,
            lineStep,
            consumed,
            options,
            horizontalRuleTableRegions))
        {
            elements.Add(table);
        }

        foreach (PdfSemanticElement list in ExtractLists(lines, bodyFontSize, lineStep, consumed))
        {
            elements.Add(list);
        }

        foreach (PdfSemanticElement formula in ExtractUnnumberedDisplayFormulas(
            lines,
            bodyFontSize,
            consumed,
            options))
        {
            elements.Add(formula);
        }

        foreach (PdfSemanticElement paragraph in ExtractParagraphs(lines, bodyFontSize, lineStep, consumed, options))
        {
            elements.Add(paragraph);
        }

        PdfSemanticElement[] sortedElements = elements
            .OrderBy(static element => element.Bounds.Y)
            .ThenBy(static element => element.Bounds.X)
            .ToArray();
        PdfSemanticElement[] mergedElements = MergeAdjacentParagraphFragments(
            sortedElements,
            bodyFontSize,
            lineStep,
            page.Width,
            horizontalTableLanes.Length > 0);
        IReadOnlyList<PdfSemanticElement> detectedElements = DetectQuotationsAndAsides(page, mergedElements);
        return new PdfSemanticPage(
            page.PageNumber,
            AttachDetectedTableCaptions(
                detectedElements,
                bodyFontSize,
                lineStep,
                horizontalTableLanes));
    }

    private static IReadOnlyList<PdfSemanticElement> AttachDetectedTableCaptions(
        IReadOnlyList<PdfSemanticElement> elements,
        float bodyFontSize,
        float lineStep,
        IReadOnlyList<HorizontalTableLane> horizontalTableLanes)
    {
        PdfSemanticElement[] tables = elements
            .Where(static element =>
                element.Kind == PdfSemanticElementKind.Table &&
                element.TableRows.Count > 0 &&
                element.TableCaption == null)
            .ToArray();
        TableCaptionCandidate[] captions = DetectTableCaptionCandidates(
            elements,
            lineStep,
            horizontalTableLanes);
        if (tables.Length == 0 || captions.Length == 0)
        {
            return elements;
        }

        TableCaptionAssociation[] associations = tables
            .SelectMany(table =>
            {
                bool isHorizontalLaneTable = IsHorizontalTableLaneTable(table, horizontalTableLanes);
                return captions.Select(caption => TryCreateTableCaptionAssociation(
                    elements,
                    table,
                    caption,
                    bodyFontSize,
                    lineStep,
                    isHorizontalLaneTable));
            })
            .Where(static association => association.HasValue)
            .Select(static association => association!.Value)
            .OrderBy(static association => association.Score)
            .ThenBy(static association => association.Caption.Element.Bounds.Y)
            .ThenBy(static association => association.Caption.Element.Bounds.X)
            .ToArray();

        Dictionary<PdfSemanticElement, PdfSemanticElement> replacements =
            new(ReferenceEqualityComparer.Instance);
        HashSet<PdfSemanticElement> claimedCaptions = new(ReferenceEqualityComparer.Instance);
        foreach (TableCaptionAssociation association in associations)
        {
            if (replacements.ContainsKey(association.Table) ||
                association.Caption.SourceElements.Any(claimedCaptions.Contains))
            {
                continue;
            }

            foreach (PdfSemanticElement sourceElement in association.Caption.SourceElements)
            {
                claimedCaptions.Add(sourceElement);
            }

            PdfSemanticElement source = association.Caption.Element;
            PdfSemanticTableCaption caption = new(
                association.Caption.Number,
                source.Text,
                source.Bounds,
                source.Lines,
                association.Position);
            replacements.Add(association.Table, WithTableCaption(association.Table, caption));
        }

        if (replacements.Count == 0)
        {
            return elements;
        }

        return elements
            .Where(element => !claimedCaptions.Contains(element))
            .Select(element => replacements.GetValueOrDefault(element, element))
            .ToArray();
    }

    private static TableCaptionCandidate[] DetectTableCaptionCandidates(
        IReadOnlyList<PdfSemanticElement> elements,
        float lineStep,
        IReadOnlyList<HorizontalTableLane> horizontalTableLanes)
    {
        List<TableCaptionCandidate> captions = [];
        for (int index = 0; index < elements.Count; index++)
        {
            TableCaptionCandidate? detected = TryCreateTableCaptionCandidate(elements[index]);
            if (!detected.HasValue)
            {
                continue;
            }

            TableCaptionCandidate caption = detected.Value;
            bool isHorizontalLaneCaption = horizontalTableLanes.Any(lane =>
                IsInsideRectangleCenter(caption.Element.Bounds, lane.ExpandedBounds, 1f));
            if (!isHorizontalLaneCaption)
            {
                if (caption.Element.Lines.Count <= 8)
                {
                    captions.Add(caption);
                }

                continue;
            }

            PdfSemanticElement merged = caption.Element;
            List<PdfSemanticElement> sources = [.. caption.SourceElements];
            for (int candidateIndex = index + 1;
                candidateIndex < elements.Count &&
                    sources.Count < 8 &&
                    !EndsSentence(merged.Text);
                candidateIndex++)
            {
                PdfSemanticElement candidate = elements[candidateIndex];
                if (candidate.Kind != PdfSemanticElementKind.Paragraph ||
                    candidate.Bounds.Y < sources[^1].Bounds.Y - 1f ||
                    !SharesTableLane(candidate.Bounds, caption.Element.Bounds))
                {
                    continue;
                }

                float fontSize = MedianFontSize(merged.Lines);
                float gap = MathF.Max(0f, candidate.Bounds.Y - sources[^1].Bounds.Bottom);
                if (gap > MathF.Max(lineStep * 1.9f, fontSize * 2.2f))
                {
                    break;
                }

                merged = MergeParagraphElements(merged, candidate);
                sources.Add(candidate);
            }

            captions.Add(new TableCaptionCandidate(merged, caption.Number, sources));
        }

        return captions.ToArray();
    }

    private static bool IsHorizontalTableLaneTable(
        PdfSemanticElement table,
        IReadOnlyList<HorizontalTableLane> horizontalTableLanes)
    {
        return horizontalTableLanes.Any(lane =>
            HorizontalOverlap(table.Bounds, lane.RuleBounds) >=
                MathF.Min(table.Bounds.Width, lane.RuleBounds.Width) * 0.80f &&
            VerticalOverlap(table.Bounds, lane.RuleBounds) >=
                MathF.Min(table.Bounds.Height, lane.RuleBounds.Height) * 0.50f);
    }

    private static TableCaptionCandidate? TryCreateTableCaptionCandidate(PdfSemanticElement element)
    {
        if (element.Kind is not (PdfSemanticElementKind.Paragraph or PdfSemanticElementKind.Heading) ||
            element.Lines.Count == 0 ||
            element.Lines.Count > 12 ||
            element.Text.Length > 1200)
        {
            return null;
        }

        Match match = TableCaptionPattern.Match(element.Text);
        if (!match.Success)
        {
            return null;
        }

        string suffix = element.Text[match.Length..].TrimStart();
        bool hasSeparator = match.Groups["separator"].Success &&
            match.Groups["separator"].Value.Trim().Length > 0;
        if (!hasSeparator && suffix.Length > 0 &&
            (char.IsLower(suffix[0]) || TableReferenceContinuationPattern.IsMatch(suffix)))
        {
            return null;
        }

        return new TableCaptionCandidate(element, match.Groups["number"].Value, [element]);
    }

    private static TableCaptionAssociation? TryCreateTableCaptionAssociation(
        IReadOnlyList<PdfSemanticElement> elements,
        PdfSemanticElement table,
        TableCaptionCandidate caption,
        float bodyFontSize,
        float lineStep,
        bool isHorizontalLaneTable)
    {
        PdfSemanticTableCaptionPosition position;
        float gap;
        if (caption.Element.Bounds.Bottom <= table.Bounds.Y + 1f)
        {
            position = PdfSemanticTableCaptionPosition.Above;
            gap = MathF.Max(0f, table.Bounds.Y - caption.Element.Bounds.Bottom);
        }
        else if (caption.Element.Bounds.Y >= table.Bounds.Bottom - 1f)
        {
            position = PdfSemanticTableCaptionPosition.Below;
            gap = MathF.Max(0f, caption.Element.Bounds.Y - table.Bounds.Bottom);
        }
        else
        {
            return null;
        }

        float captionFontSize = MedianFontSize(caption.Element.Lines);
        float tableFontSize = MedianFontSize(table.Lines);
        float maximumGap = MathF.Max(72f, MathF.Max(lineStep * 4.5f, captionFontSize * 5f));
        if (captionFontSize <= 0f ||
            tableFontSize <= 0f ||
            gap > maximumGap ||
            captionFontSize < tableFontSize * 0.65f ||
            captionFontSize > MathF.Max(bodyFontSize * 1.35f, tableFontSize * 1.5f) ||
            !SharesTableLane(caption.Element.Bounds, table.Bounds) ||
            !HasContinuousCaptionLines(
                caption.Element.Lines,
                captionFontSize,
                lineStep,
                isHorizontalLaneTable) ||
            HasInterveningTableCaptionContent(elements, table, caption.Element, position))
        {
            return null;
        }

        float centerDistance = MathF.Abs(
            caption.Element.Bounds.X + caption.Element.Bounds.Width / 2f -
            (table.Bounds.X + table.Bounds.Width / 2f));
        float positionPenalty = isHorizontalLaneTable
            ? position == PdfSemanticTableCaptionPosition.Above ? 1f : 0f
            : position == PdfSemanticTableCaptionPosition.Below ? 2f : 0f;
        return new TableCaptionAssociation(
            table,
            caption,
            position,
            gap + centerDistance * 0.08f + positionPenalty);
    }

    private static bool HasContinuousCaptionLines(
        IReadOnlyList<PdfSemanticLine> lines,
        float captionFontSize,
        float lineStep,
        bool allowFontOutlier)
    {
        PdfSemanticLine[] ordered = lines
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        float minimumFontSize = ordered.Min(static line => line.DominantFontSize);
        float maximumFontSize = ordered.Max(static line => line.DominantFontSize);
        float fontTolerance = MathF.Max(1.5f, captionFontSize * 0.2f);
        if (!allowFontOutlier && maximumFontSize - minimumFontSize > fontTolerance)
        {
            return false;
        }

        if (allowFontOutlier)
        {
            int fontOutliers = ordered.Count(line =>
                MathF.Abs(line.DominantFontSize - captionFontSize) > fontTolerance);
            if (fontOutliers > Math.Max(1, ordered.Length / 5))
            {
                return false;
            }
        }

        float maximumLineGap = MathF.Max(lineStep * 1.75f, captionFontSize * 1.8f);
        return ordered
            .Pairwise(static (first, second) => MathF.Max(0f, second.Bounds.Y - first.Bounds.Bottom))
            .All(gap => gap <= maximumLineGap);
    }

    private static bool HasInterveningTableCaptionContent(
        IReadOnlyList<PdfSemanticElement> elements,
        PdfSemanticElement table,
        PdfSemanticElement caption,
        PdfSemanticTableCaptionPosition position)
    {
        float top = position == PdfSemanticTableCaptionPosition.Above
            ? caption.Bounds.Bottom
            : table.Bounds.Bottom;
        float bottom = position == PdfSemanticTableCaptionPosition.Above
            ? table.Bounds.Y
            : caption.Bounds.Y;
        return elements.Any(element =>
        {
            if (ReferenceEquals(element, table) || ReferenceEquals(element, caption))
            {
                return false;
            }

            float centerY = element.Bounds.Y + element.Bounds.Height / 2f;
            return centerY > top + 0.5f &&
                centerY < bottom - 0.5f &&
                SharesTableLane(element.Bounds, table.Bounds);
        });
    }

    private static bool SharesTableLane(PdfLayoutRectangle content, PdfLayoutRectangle table)
    {
        float overlap = MathF.Max(0f, HorizontalOverlap(content, table));
        float minimumWidth = MathF.Min(content.Width, table.Width);
        float centerDistance = MathF.Abs(
            content.X + content.Width / 2f -
            (table.X + table.Width / 2f));
        return minimumWidth > 0f &&
            (overlap >= minimumWidth * 0.35f ||
                centerDistance <= MathF.Max(24f, table.Width * 0.25f));
    }

    private static float MedianFontSize(IReadOnlyList<PdfSemanticLine> lines)
    {
        float[] sizes = lines
            .Select(static line => line.DominantFontSize)
            .Where(static size => size > 0f)
            .Order()
            .ToArray();
        return sizes.Length == 0 ? 0f : sizes[sizes.Length / 2];
    }

    private static PdfSemanticElement WithTableCaption(
        PdfSemanticElement table,
        PdfSemanticTableCaption caption)
    {
        return new PdfSemanticElement(
            table.Kind,
            table.Text,
            table.Bounds,
            table.Lines,
            tableRows: table.TableRows,
            tableCaption: caption);
    }

    private static IReadOnlyList<PdfSemanticElement> DetectQuotationsAndAsides(
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticElement> elements)
    {
        PdfSemanticElement[] quotations = elements
            .Select((element, index) => DetectQuotation(elements, index) ?? element)
            .ToArray();
        List<PdfSemanticElement> detected = [];
        int elementIndex = 0;
        while (elementIndex < quotations.Length)
        {
            PdfSemanticElement label = quotations[elementIndex];
            if (!TryCreateAside(page, quotations, elementIndex, out PdfSemanticElement? aside, out int consumedCount))
            {
                detected.Add(label);
                elementIndex++;
                continue;
            }

            detected.Add(aside!);
            elementIndex += consumedCount;
        }

        return detected;
    }

    private static PdfSemanticElement? DetectQuotation(
        IReadOnlyList<PdfSemanticElement> elements,
        int index)
    {
        PdfSemanticElement element = elements[index];
        if (element.Kind != PdfSemanticElementKind.Paragraph ||
            element.Lines.Count < 2 ||
            element.Text.Length < 80)
        {
            return null;
        }

        if (!TrySplitQuotedPassage(element, out string quoteText, out string? attribution) &&
            !IsStronglyInsetPassage(elements, index))
        {
            return null;
        }

        return new PdfSemanticElement(
            PdfSemanticElementKind.BlockQuote,
            element.Text,
            element.Bounds,
            element.Lines,
            quotation: new PdfSemanticQuotation(quoteText, attribution));
    }

    private static bool TrySplitQuotedPassage(
        PdfSemanticElement element,
        out string quoteText,
        out string? attribution)
    {
        quoteText = element.Text;
        attribution = null;
        string text = element.Text.Trim();
        if (text.Length < 80 || text[0] is not ('“' or '"'))
        {
            return false;
        }

        char closingQuote = text[0] == '“' ? '”' : '"';
        int closingIndex = text.LastIndexOf(closingQuote);
        int firstLineLength = element.Lines[0].Text.Trim().Length;
        if (closingIndex < Math.Max(64, firstLineLength))
        {
            return false;
        }

        string suffix = text[(closingIndex + 1)..].TrimStart();
        int delimiterLength = 0;
        while (delimiterLength < suffix.Length && suffix[delimiterLength] is ',' or ';' or ':')
        {
            delimiterLength++;
        }

        string delimiter = suffix[..delimiterLength];
        suffix = suffix[delimiterLength..].TrimStart();
        if (suffix.Length > 0 && !QuoteAttributionPattern.IsMatch(suffix))
        {
            return false;
        }

        quoteText = text[..(closingIndex + 1)] + delimiter;
        attribution = suffix.Length == 0 ? null : suffix;
        return true;
    }

    private static bool IsStronglyInsetPassage(
        IReadOnlyList<PdfSemanticElement> elements,
        int index)
    {
        PdfSemanticElement element = elements[index];
        if (element.Lines.Count < 3 ||
            element.Text.Length < 120 ||
            element.Text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            element.Text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            element.Text.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PdfSemanticElement? previous = elements.Take(index)
            .LastOrDefault(static candidate => candidate.Kind == PdfSemanticElementKind.Paragraph);
        PdfSemanticElement? next = elements.Skip(index + 1)
            .FirstOrDefault(static candidate => candidate.Kind == PdfSemanticElementKind.Paragraph);
        PdfSemanticElement[] neighbors = new PdfSemanticElement?[] { previous, next }
            .Where(static candidate => candidate != null)
            .Cast<PdfSemanticElement>()
            .ToArray();
        if (neighbors.Length == 0)
        {
            return false;
        }

        float ordinaryLeft = neighbors.Min(static candidate => candidate.Bounds.X);
        float ordinaryRight = neighbors.Max(static candidate => candidate.Bounds.Right);
        bool inset = element.Bounds.X >= ordinaryLeft + 18f &&
            element.Bounds.Right <= ordinaryRight - 18f;
        float anchorSpread = element.Lines.Max(static line => line.Bounds.X) -
            element.Lines.Min(static line => line.Bounds.X);
        return inset && anchorSpread <= 12f;
    }

    private static bool TryCreateAside(
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticElement> elements,
        int labelIndex,
        out PdfSemanticElement? aside,
        out int consumedCount)
    {
        aside = null;
        consumedCount = 0;
        PdfSemanticElement label = elements[labelIndex];
        if (label.Kind != PdfSemanticElementKind.Heading || label.IsDocumentTitle)
        {
            return false;
        }

        PdfLayoutRectangle? enclosingRegion = EnclosingCalloutRegion(page, label.Bounds);
        List<PdfSemanticElement> content = [];
        PdfSemanticElement previous = label;
        for (int index = labelIndex + 1;
            index < elements.Count && elements[index].Kind == PdfSemanticElementKind.Paragraph;
            index++)
        {
            PdfSemanticElement candidate = elements[index];
            if (!IsCalloutContentContinuation(previous, candidate, content.FirstOrDefault(), enclosingRegion))
            {
                break;
            }

            content.Add(candidate);
            previous = candidate;
        }

        if (content.Count == 0 || content.Any(static element => element.Kind == PdfSemanticElementKind.Footnote))
        {
            return false;
        }

        PdfLayoutRectangle contentBounds = PdfLayoutRectangle.Union(content.Select(static element => element.Bounds));
        PdfLayoutRectangle calloutBounds = PdfLayoutRectangle.Union([label.Bounds, contentBounds]);
        bool knownInsetLabel = IsKnownInsetCalloutLabel(label.Text) &&
            contentBounds.X >= page.Width * 0.15f &&
            contentBounds.Width <= page.Width * 0.78f;
        bool hasEnclosingRegion = HasEnclosingCalloutRegion(page, calloutBounds);
        bool isMediaSideBlock = IsMediaSideCallout(page, label, content, contentBounds, calloutBounds);
        if (!knownInsetLabel && !hasEnclosingRegion && !isMediaSideBlock)
        {
            return false;
        }

        PdfSemanticLine[] lines = label.Lines
            .Concat(content.SelectMany(static element => element.Lines))
            .ToArray();
        PdfSemanticAside semanticAside = new(label.Text.Trim(), label.Lines, content);
        aside = new PdfSemanticElement(
            PdfSemanticElementKind.Aside,
            string.Join(Environment.NewLine, label.Text.Trim(), string.Join(Environment.NewLine, content.Select(static item => item.Text))),
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines,
            aside: semanticAside);
        consumedCount = content.Count + 1;
        return true;
    }

    private static bool IsKnownInsetCalloutLabel(string text)
    {
        return text.Trim().ToUpperInvariant() is "DISCUSSION" or "NOTE" or "SIDE INFORMATION";
    }

    private static bool IsCalloutContentContinuation(
        PdfSemanticElement previous,
        PdfSemanticElement candidate,
        PdfSemanticElement? firstContent,
        PdfLayoutRectangle? enclosingRegion)
    {
        float previousFontSize = previous.Lines
            .Select(static line => line.DominantFontSize)
            .DefaultIfEmpty(10f)
            .Max();
        float candidateFontSize = candidate.Lines
            .Select(static line => line.DominantFontSize)
            .DefaultIfEmpty(previousFontSize)
            .Max();
        float verticalGap = candidate.Bounds.Y - previous.Bounds.Bottom;
        if (verticalGap < -2f || verticalGap > MathF.Max(30f, MathF.Max(previousFontSize, candidateFontSize) * 3f))
        {
            return false;
        }

        if (enclosingRegion is PdfLayoutRectangle region)
        {
            return ContainsRectangle(region, candidate.Bounds, 4f);
        }

        if (firstContent != null && MathF.Abs(candidate.Bounds.X - firstContent.Bounds.X) > 24f)
        {
            return false;
        }

        float overlap = MathF.Min(previous.Bounds.Right, candidate.Bounds.Right) -
            MathF.Max(previous.Bounds.X, candidate.Bounds.X);
        float minimumWidth = MathF.Min(previous.Bounds.Width, candidate.Bounds.Width);
        return MathF.Abs(previous.Bounds.X - candidate.Bounds.X) <= 24f ||
            overlap >= minimumWidth * 0.50f;
    }

    private static bool HasEnclosingCalloutRegion(PdfLayoutPage page, PdfLayoutRectangle content)
    {
        return EnclosingCalloutRegion(page, content).HasValue;
    }

    private static PdfLayoutRectangle? EnclosingCalloutRegion(
        PdfLayoutPage page,
        PdfLayoutRectangle content)
    {
        return page.Paths
            .Where(static path => path.IsFilled || path.IsStroked)
            .Select(static path => path.Bounds)
            .Concat(page.Shadings.Select(static shading => shading.Bounds))
            .Where(bounds => ContainsRectangle(bounds, content, 4f))
            .Where(bounds => bounds.Width < page.Width * 0.92f && bounds.Height < page.Height * 0.75f)
            .OrderBy(static bounds => bounds.Width * bounds.Height)
            .Cast<PdfLayoutRectangle?>()
            .FirstOrDefault();
    }

    private static bool IsMediaSideCallout(
        PdfLayoutPage page,
        PdfSemanticElement label,
        IReadOnlyList<PdfSemanticElement> contentElements,
        PdfLayoutRectangle content,
        PdfLayoutRectangle callout)
    {
        float labelFontSize = label.Lines
            .Select(static line => line.DominantFontSize)
            .DefaultIfEmpty(0f)
            .Max();
        float contentFontSize = contentElements
            .SelectMany(static element => element.Lines)
            .Select(static line => line.DominantFontSize)
            .DefaultIfEmpty(labelFontSize)
            .Max();
        if (label.Text.Length > 64 ||
            labelFontSize > contentFontSize + 4f ||
            content.Width > page.Width * 0.62f ||
            content.X < page.Width * 0.24f && content.Right > page.Width * 0.76f)
        {
            return false;
        }

        return page.Images.Any(image =>
        {
            PdfLayoutRectangle bounds = image.Bounds;
            float overlap = MathF.Min(bounds.Bottom, callout.Bottom) - MathF.Max(bounds.Y, callout.Y);
            return bounds.Width >= page.Width * 0.15f &&
                bounds.Height >= 36f &&
                overlap >= MathF.Min(bounds.Height, callout.Height) * 0.30f &&
                HorizontalGap(bounds, callout) <= 24f;
        });
    }

    private static bool ContainsRectangle(
        PdfLayoutRectangle outer,
        PdfLayoutRectangle inner,
        float tolerance)
    {
        return inner.X >= outer.X - tolerance &&
            inner.Y >= outer.Y - tolerance &&
            inner.Right <= outer.Right + tolerance &&
            inner.Bottom <= outer.Bottom + tolerance;
    }

    private static IEnumerable<PdfSemanticElement> GroupHeaders(
        IReadOnlyList<LineCandidate> lines,
        float lineStep,
        HashSet<int> consumed)
    {
        List<LineCandidate> current = [];
        foreach (LineCandidate line in lines.OrderBy(static line => line.Bounds.Y).ThenBy(static line => line.Bounds.X))
        {
            if (current.Count == 0)
            {
                current.Add(line);
                continue;
            }

            LineCandidate previous = current[^1];
            if (ShouldGroupHeader(previous, line, lineStep))
            {
                current.Add(line);
                continue;
            }

            yield return CreateHeader(current, consumed);
            current.Clear();
            current.Add(line);
        }

        if (current.Count > 0)
        {
            yield return CreateHeader(current, consumed);
        }
    }

    private static PdfSemanticElement[] MergeAdjacentParagraphFragments(
        IReadOnlyList<PdfSemanticElement> elements,
        float bodyFontSize,
        float lineStep,
        float pageWidth,
        bool preserveColumnLanes)
    {
        List<PdfSemanticElement> merged = [];
        foreach (PdfSemanticElement element in elements)
        {
            if (merged.Count > 0 &&
                ShouldMergeAdjacentParagraphFragments(
                    merged[^1],
                    element,
                    bodyFontSize,
                    lineStep,
                    pageWidth,
                    preserveColumnLanes))
            {
                merged[^1] = MergeParagraphElements(merged[^1], element);
                continue;
            }

            merged.Add(element);
        }

        return merged.ToArray();
    }

    private static bool ShouldMergeAdjacentParagraphFragments(
        PdfSemanticElement previous,
        PdfSemanticElement current,
        float bodyFontSize,
        float lineStep,
        float pageWidth,
        bool preserveColumnLanes)
    {
        if (previous.Kind != PdfSemanticElementKind.Paragraph ||
            current.Kind != PdfSemanticElementKind.Paragraph)
        {
            return false;
        }

        if (HasNumberedFormulaLine(previous) || HasNumberedFormulaLine(current))
        {
            return false;
        }

        float fragmentHorizontalGap = HorizontalGap(previous.Bounds, current.Bounds);
        if (fragmentHorizontalGap >= pageWidth * 0.05f &&
            (previous.Bounds.Width <= pageWidth * 0.20f ||
                current.Bounds.Width <= pageWidth * 0.20f))
        {
            return false;
        }

        if (preserveColumnLanes)
        {
            bool crossesPageGutter =
                previous.Bounds.Right <= pageWidth * 0.52f && current.Bounds.X >= pageWidth * 0.48f ||
                current.Bounds.Right <= pageWidth * 0.52f && previous.Bounds.X >= pageWidth * 0.48f;
            if (fragmentHorizontalGap >= pageWidth * 0.01f &&
                (crossesPageGutter ||
                    previous.Bounds.Width >= pageWidth * 0.20f &&
                    current.Bounds.Width >= pageWidth * 0.20f))
            {
                return false;
            }
        }

        float verticalGap = MathF.Max(0f, current.Bounds.Y - previous.Bounds.Bottom);
        if (IsFormulaFragmentElement(previous, bodyFontSize) &&
            IsDisplayFormulaElement(current, bodyFontSize) &&
            verticalGap <= lineStep * 2.5f)
        {
            return true;
        }

        if (IsDisplayFormulaElement(current, bodyFontSize))
        {
            return false;
        }

        if (IsDisplayFormulaElement(previous, bodyFontSize) &&
            StartsFormulaClause(current.Text) &&
            verticalGap <= lineStep * 7f &&
            current.Bounds.Height <= lineStep * 3.5f &&
            current.Text.Length <= 220)
        {
            return true;
        }

        if (IsDisplayFormulaElement(previous, bodyFontSize))
        {
            return false;
        }

        bool mathContinuation = IsInlineMathContinuation(previous, current);
        bool symbolicContinuation = StartsSymbolicParagraphContinuation(current.Text) &&
            current.Lines.Any(static line => line.Runs.Any(static run => IsMathFont(run.FontName)));
        float maximumContinuationGap = mathContinuation || symbolicContinuation ? lineStep * 4f : lineStep * 1.8f;
        return verticalGap <= maximumContinuationGap &&
            (StartsParagraphContinuation(current.Text) || mathContinuation || symbolicContinuation);
    }

    private static PdfSemanticElement MergeParagraphElements(
        PdfSemanticElement first,
        PdfSemanticElement second)
    {
        PdfSemanticLine[] lines = OrderLinesForReading(first.Lines.Concat(second.Lines));
        return new PdfSemanticElement(
            PdfSemanticElementKind.Paragraph,
            JoinParagraphLines(lines),
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines);
    }

    private static bool IsDisplayFormulaElement(PdfSemanticElement element, float bodyFontSize)
    {
        return element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Any(line => IsDisplayFormulaLine(line, bodyFontSize));
    }

    private static bool IsFormulaFragmentElement(PdfSemanticElement element, float bodyFontSize)
    {
        return element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Length <= 48 &&
            element.Lines.All(line => HasMathFont(line) || IsEquationNumberText(line.Text)) &&
            element.Lines.Any(line => IsFormulaContinuationLine(line, bodyFontSize));
    }

    private static bool IsDisplayFormulaLine(PdfSemanticLine line, float bodyFontSize)
    {
        if (!HasMathFont(line) || !HasFormulaOperator(line.Text))
        {
            return false;
        }

        if (HasFormulaFunction(line.Text))
        {
            return line.Text.IndexOf('=') >= 0 ||
                line.Bounds.Width >= 80f &&
                (StartsFormulaFunction(line.Text) || CountWords(line.Text) <= 4);
        }

        if (IsMathDominantFormulaLine(line.Text, line.Bounds, line.Runs))
        {
            return true;
        }

        bool centeredEnough = line.Bounds.X >= 150f && line.Bounds.Width >= 80f;
        int wordCount = CountWords(line.Text);
        return centeredEnough &&
            !IsProseDominantFormulaLine(line.Text, line.Runs) &&
            (wordCount <= 4 && line.DominantFontSize <= bodyFontSize + 1f ||
                wordCount <= 12 &&
                HasLargeFormulaOperator(line.Runs));
    }

    private static bool StartsFormulaClause(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith("where ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Where ", StringComparison.Ordinal);
    }

    private static bool StartsParagraphContinuation(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.Length > 0 &&
            (trimmed.StartsWith("PE", StringComparison.Ordinal) ||
                trimmed[0] is '/' or '=' or ',' or ')' or ']' or '(' or '∈' or '×' ||
                char.IsLower(trimmed[0]));
    }

    private static bool IsInlineMathContinuation(PdfSemanticElement previous, PdfSemanticElement current)
    {
        return !EndsSentence(previous.Text) &&
            current.Text.Length <= 160 &&
            current.Bounds.Width <= 120f &&
            current.Lines.Any(static line => line.Runs.Any(static run => IsMathFont(run.FontName)));
    }

    private static bool StartsSymbolicParagraphContinuation(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.Length > 0 &&
            trimmed[0] is '(' or '∈' or '×' or '/' or '=';
    }

    private static int CountWords(string text)
    {
        return WhitespacePattern
            .Split(text.Trim())
            .Count(static part => part.Length > 0);
    }

    private static PdfSemanticElement CreateHeader(IReadOnlyList<LineCandidate> lines, HashSet<int> consumed)
    {
        foreach (LineCandidate line in lines)
        {
            consumed.Add(line.Index);
        }

        return CreateElement(PdfSemanticElementKind.Header, lines);
    }

    private static bool ShouldGroupHeader(LineCandidate previous, LineCandidate current, float lineStep)
    {
        if (MathF.Abs(previous.Direction - current.Direction) > 0.01f)
        {
            return false;
        }

        if (!SameColor(previous.Color, current.Color))
        {
            return false;
        }

        if (!string.Equals(previous.FontName, current.FontName, StringComparison.Ordinal) ||
            MathF.Abs(previous.FontSize - current.FontSize) > 0.75f)
        {
            return false;
        }

        if (MathF.Abs(previous.Direction) > 0.01f)
        {
            return false;
        }

        return current.Bounds.Y - previous.Bounds.Y <= lineStep * 1.6f;
    }

    private static LineCandidate CreateLineCandidate(
        int index,
        PdfTextLine source,
        PdfSemanticExtractionOptions options,
        int? tableLaneIndex = null)
    {
        string text = ReconstructText(source.Runs.SelectMany(static run => run.Glyphs), options);
        (string fontName, float fontSize, float direction, PdfLayoutColor color) = DominantStyle(source.Runs);
        return new LineCandidate(
            index,
            source,
            new PdfSemanticLine(text, source.Bounds, fontName, fontSize, direction, color, source.Runs),
            fontName,
            fontSize,
            direction,
            color,
            tableLaneIndex);
    }

    private static IEnumerable<LineCandidate> CreateLineCandidates(
        PdfLayoutPage page,
        IReadOnlyList<TableCandidateRegion> tableCandidateRegions,
        PdfSemanticExtractionOptions options)
    {
        if (tableCandidateRegions.Count == 0)
        {
            return page.Lines
                .Select(line => CreateTextLine(line.Runs
                    .Where(static run => !IsMicroscopicUnpaintedPayloadRun(run))
                    .ToArray()))
                .Where(static line => line.Runs.Count > 0)
                .Select((line, index) => CreateLineCandidate(index, line, options));
        }

        List<LineCandidate> candidates = [];
        int candidateIndex = 0;
        foreach (PdfTextLine source in page.Lines)
        {
            List<PdfTextRun> remainingRuns = source.Runs
                .Where(static run => !IsMicroscopicUnpaintedPayloadRun(run))
                .ToList();
            foreach (TableCandidateRegion region in tableCandidateRegions)
            {
                PdfTextRun[] regionRuns = remainingRuns
                    .Where(run => IsInsideRectangleCenter(run.Bounds, region.Bounds, 1f))
                    .ToArray();
                if (regionRuns.Length == 0)
                {
                    continue;
                }

                candidates.Add(CreateLineCandidate(
                    candidateIndex++,
                    CreateTextLine(regionRuns),
                    options,
                    region.TableLaneIndex));
                remainingRuns.RemoveAll(regionRuns.Contains);
            }

            if (remainingRuns.Count > 0)
            {
                candidates.Add(CreateLineCandidate(
                    candidateIndex++,
                    CreateTextLine(remainingRuns),
                    options));
            }
        }

        return candidates;
    }

    private static bool IsMicroscopicUnpaintedPayloadRun(PdfTextRun run)
    {
        PdfTextGlyph[] glyphs = run.Glyphs
            .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
            .ToArray();
        PdfTextGlyph[] unpainted = glyphs
            .Where(static glyph => !glyph.IsPainted)
            .ToArray();
        return unpainted.Length >= 24 &&
            unpainted.Length >= glyphs.Length * 0.85f &&
            unpainted.All(static glyph =>
                glyph.FontSize <= 0.25f ||
                glyph.PageBounds.Width <= 0.01f && glyph.PageBounds.Height <= 0.01f) &&
            unpainted.Count(glyph =>
                NormalizeFontName(glyph.FontName).Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
                NormalizeFontName(glyph.FontName).Contains("Mono", StringComparison.OrdinalIgnoreCase)) >=
                unpainted.Length * 0.85f;
    }

    private static int? FindHorizontalTableLane(
        PdfLayoutRectangle bounds,
        IReadOnlyList<HorizontalTableLane> lanes)
    {
        for (int index = 0; index < lanes.Count; index++)
        {
            PdfLayoutRectangle ruleBounds = lanes[index].RuleBounds;
            if (HorizontalOverlap(bounds, ruleBounds) >=
                    MathF.Min(bounds.Width, ruleBounds.Width) * 0.80f &&
                VerticalOverlap(bounds, ruleBounds) >=
                    MathF.Min(bounds.Height, ruleBounds.Height) * 0.80f)
            {
                return index;
            }
        }

        return null;
    }

    private static PdfTextLine CreateTextLine(IReadOnlyList<PdfTextRun> runs)
    {
        PdfTextRun[] orderedRuns = runs
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .ToArray();
        return new PdfTextLine(
            string.Concat(orderedRuns.Select(static run => run.Text)),
            PdfLayoutRectangle.Union(orderedRuns.Select(static run => run.Bounds)),
            orderedRuns);
    }

    private static (string FontName, float FontSize, float Direction, PdfLayoutColor Color) DominantStyle(IReadOnlyList<PdfTextRun> runs)
    {
        return runs
            .GroupBy(static run => (
                NormalizeFontName(run.FontName),
                MathF.Round(run.FontSize * 2f) / 2f,
                MathF.Round(run.Direction),
                ColorKey(run.Color)))
            .Select(static group => new
            {
                group.Key,
                Weight = group.Sum(run => Math.Max(1, run.Text.Length))
            })
            .OrderByDescending(static item => item.Weight)
            .ThenByDescending(static item => item.Key.Item2)
            .Select(static item => (
                item.Key.Item1,
                item.Key.Item2,
                item.Key.Item3,
                item.Key.Item4.Color))
            .FirstOrDefault();
    }

    private static ColorKeyValue ColorKey(PdfLayoutColor color)
    {
        return new ColorKeyValue(
            MathF.Round(color.Red * 255f) / 255f,
            MathF.Round(color.Green * 255f) / 255f,
            MathF.Round(color.Blue * 255f) / 255f,
            MathF.Round(color.Alpha * 255f) / 255f,
            color.ColorSpaceName,
            color);
    }

    private static bool SameColor(PdfLayoutColor first, PdfLayoutColor second)
    {
        return MathF.Abs(first.Red - second.Red) < 0.001f &&
            MathF.Abs(first.Green - second.Green) < 0.001f &&
            MathF.Abs(first.Blue - second.Blue) < 0.001f &&
            MathF.Abs(first.Alpha - second.Alpha) < 0.001f &&
            string.Equals(first.ColorSpaceName, second.ColorSpaceName, StringComparison.Ordinal);
    }

    private static float EstimateBodyFontSize(IReadOnlyList<LineCandidate> lines)
    {
        return lines
            .Where(static line => line.Text.Length >= 20)
            .GroupBy(static line => MathF.Round(line.FontSize))
            .Select(static group => new
            {
                Size = group.Key,
                Weight = group.Sum(static line => line.Text.Length)
            })
            .OrderByDescending(static item => item.Weight)
            .ThenBy(static item => item.Size)
            .Select(static item => item.Size)
            .FirstOrDefault(10f);
    }

    private static float EstimateLineStep(IReadOnlyList<LineCandidate> lines, float bodyFontSize)
    {
        float[] gaps = lines
            .Where(line => MathF.Abs(line.FontSize - bodyFontSize) <= 1.5f)
            .OrderBy(static line => line.Bounds.Y)
            .Pairwise((first, second) => second.Bounds.Y - first.Bounds.Y)
            .Where(static gap => gap > 2f && gap < 24f)
            .Order()
            .ToArray();

        return gaps.Length == 0 ? MathF.Max(10f, bodyFontSize * 1.15f) : gaps[gaps.Length / 2];
    }

    private static AlgorithmCandidate[] DetectAlgorithms(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float lineStep)
    {
        AlgorithmRuleCandidate[] rules = AlgorithmHorizontalRules(page);
        if (rules.Length < 3)
        {
            return [];
        }

        List<AlgorithmCandidate> algorithms = [];
        HashSet<int> claimedLines = [];
        for (int topIndex = 0; topIndex + 2 < rules.Length; topIndex++)
        {
            AlgorithmCandidate? candidate = TryCreateAlgorithmCandidate(
                rules[topIndex],
                rules[topIndex + 1],
                rules[topIndex + 2],
                lines,
                lineStep);
            if (candidate == null || candidate.SourceLines.Any(line => claimedLines.Contains(line.Index)))
            {
                continue;
            }

            algorithms.Add(candidate);
            foreach (LineCandidate line in candidate.SourceLines)
            {
                claimedLines.Add(line.Index);
            }
        }

        return algorithms.ToArray();
    }

    private static AlgorithmRuleCandidate[] AlgorithmHorizontalRules(PdfLayoutPage page)
    {
        List<AlgorithmRuleCandidate> rules = [];
        foreach (PdfLayoutPath path in page.Paths)
        {
            if (!TryCreateThematicRuleCandidate(page, path, out ThematicRuleCandidate rule) ||
                rule.Bounds.Width < page.Width * 0.25f)
            {
                continue;
            }

            rules.Add(new AlgorithmRuleCandidate(path.Index, rule.Bounds, rule.Thickness, rule.Color));
        }

        return rules
            .OrderBy(static rule => RuleCenterY(rule.Bounds))
            .ThenBy(static rule => rule.Bounds.X)
            .ToArray();
    }

    private static AlgorithmCandidate? TryCreateAlgorithmCandidate(
        AlgorithmRuleCandidate topRule,
        AlgorithmRuleCandidate captionRule,
        AlgorithmRuleCandidate bottomRule,
        IReadOnlyList<LineCandidate> lines,
        float lineStep)
    {
        if (!AreAlgorithmRulesAligned(topRule, captionRule) ||
            !AreAlgorithmRulesAligned(captionRule, bottomRule))
        {
            return null;
        }

        float top = RuleCenterY(topRule.Bounds);
        float captionBottom = RuleCenterY(captionRule.Bounds);
        float bottom = RuleCenterY(bottomRule.Bounds);
        if (captionBottom - top < 8f || bottom - captionBottom < MathF.Max(30f, lineStep * 4f))
        {
            return null;
        }

        float left = MathF.Max(topRule.Bounds.X, MathF.Max(captionRule.Bounds.X, bottomRule.Bounds.X));
        float right = MathF.Min(topRule.Bounds.Right, MathF.Min(captionRule.Bounds.Right, bottomRule.Bounds.Right));
        LineCandidate[] captionLines = lines
            .Where(line => LineCenterY(line) > top + 0.5f && LineCenterY(line) < captionBottom - 0.5f)
            .Where(line => IsInsideHorizontalInterval(line.Bounds, left, right))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        if (captionLines.Length == 0 || !AlgorithmCaptionPattern.IsMatch(captionLines[0].Text.TrimStart()))
        {
            return null;
        }

        LineCandidate[] bodyLines = lines
            .Where(line => LineCenterY(line) > captionBottom + 0.5f && LineCenterY(line) < bottom - 0.5f)
            .Where(line => IsInsideHorizontalInterval(line.Bounds, left, right))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        LineCandidate[] rows = bodyLines
            .Where(static line => !IsAlgorithmRowDecoration(line))
            .ToArray();
        if (rows.Length < 6 ||
            !HasRepeatedAlgorithmRowSpacing(rows, lineStep) ||
            !HasAlgorithmIndentation(rows))
        {
            return null;
        }

        string bodyText = string.Join(' ', rows.Select(static row => row.Text));
        int keywordKinds = AlgorithmKeywordPattern.Matches(bodyText)
            .Select(static match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (keywordKinds < 4)
        {
            return null;
        }

        return new AlgorithmCandidate(
            topRule,
            captionRule,
            bottomRule,
            captionLines,
            rows,
            captionLines.Concat(bodyLines).ToArray());
    }

    private static bool AreAlgorithmRulesAligned(
        AlgorithmRuleCandidate first,
        AlgorithmRuleCandidate second)
    {
        float minimumWidth = MathF.Min(first.Bounds.Width, second.Bounds.Width);
        return minimumWidth > 0f && HorizontalOverlap(first.Bounds, second.Bounds) >= minimumWidth * 0.85f;
    }

    private static bool IsAlgorithmRowDecoration(LineCandidate line)
    {
        string compact = new(line.Text.Where(static character => !char.IsWhiteSpace(character)).ToArray());
        foreach (string suffix in new[] { "st", "nd", "rd", "th" })
        {
            if (compact.Length > suffix.Length && compact.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                compact = compact[..^suffix.Length];
                break;
            }
        }

        return compact.Length is > 0 and <= 10 && compact.All(static character => !char.IsLetterOrDigit(character));
    }

    private static bool HasRepeatedAlgorithmRowSpacing(IReadOnlyList<LineCandidate> rows, float lineStep)
    {
        float[] gaps = rows
            .Pairwise(static (first, second) => second.Bounds.Y - first.Bounds.Y)
            .Where(gap => gap >= MathF.Max(3f, lineStep * 0.35f) && gap <= MathF.Max(24f, lineStep * 2.25f))
            .Order()
            .ToArray();
        if (gaps.Length < 4)
        {
            return false;
        }

        float median = gaps[gaps.Length / 2];
        float tolerance = MathF.Max(2f, median * 0.28f);
        return gaps.Count(gap => MathF.Abs(gap - median) <= tolerance) >= Math.Max(4, (rows.Count - 1) / 2);
    }

    private static bool HasAlgorithmIndentation(IReadOnlyList<LineCandidate> rows)
    {
        float minimum = rows.Min(static row => row.Bounds.X);
        float maximum = rows.Max(static row => row.Bounds.X);
        float fontSize = rows.Select(static row => row.FontSize).Order().ElementAt(rows.Count / 2);
        return maximum - minimum >= MathF.Max(4f, fontSize * 0.6f);
    }

    private static float LineCenterY(LineCandidate line) => line.Bounds.Y + line.Bounds.Height / 2f;

    private static float RuleCenterY(PdfLayoutRectangle bounds) => bounds.Y + bounds.Height / 2f;

    private static PdfSemanticElement CreateAlgorithm(
        AlgorithmCandidate candidate,
        HashSet<int> consumed)
    {
        foreach (LineCandidate sourceLine in candidate.SourceLines)
        {
            consumed.Add(sourceLine.Index);
        }

        PdfSemanticLine[] captionLines = candidate.CaptionLines
            .Select(static line => line.SemanticLine)
            .ToArray();
        PdfSemanticAlgorithmRow[] rows = candidate.Rows
            .Select(row => new PdfSemanticAlgorithmRow(
                CreateAlgorithmRowLine(row, candidate.SourceLines),
                row.Bounds.X - candidate.CaptionRule.Bounds.X))
            .ToArray();
        string caption = JoinParagraphLines(captionLines);
        PdfSemanticAlgorithm algorithm = new(
            caption,
            captionLines,
            rows,
            ToSemanticAlgorithmRule(candidate.TopRule),
            ToSemanticAlgorithmRule(candidate.CaptionRule),
            ToSemanticAlgorithmRule(candidate.BottomRule));
        PdfSemanticLine[] semanticLines = captionLines
            .Concat(rows.Select(static row => row.Line))
            .ToArray();
        PdfLayoutRectangle bounds = PdfLayoutRectangle.Union(
            semanticLines.Select(static line => line.Bounds)
                .Concat([
                    candidate.TopRule.Bounds,
                    candidate.CaptionRule.Bounds,
                    candidate.BottomRule.Bounds
                ]));
        return new PdfSemanticElement(
            PdfSemanticElementKind.Algorithm,
            string.Join('\n', new[] { caption }.Concat(rows.Select(static row => row.Text))),
            bounds,
            semanticLines,
            algorithm: algorithm);
    }

    private static PdfSemanticLine CreateAlgorithmRowLine(
        LineCandidate row,
        IReadOnlyList<LineCandidate> sourceLines)
    {
        PdfTextRun[] runs = row.Source.Runs
            .Concat(sourceLines
                .Where(line => line.Index != row.Index &&
                    IsAlgorithmRowDecoration(line) &&
                    !string.Equals(line.Text.Trim(), "√", StringComparison.Ordinal) &&
                    IsInlineWithTextLine(row, line))
                .SelectMany(static line => line.Source.Runs))
            .ToArray();
        if (runs.Length == row.Source.Runs.Count)
        {
            return row.SemanticLine;
        }

        PdfSemanticLine source = row.SemanticLine;
        return new PdfSemanticLine(
            ReconstructText(runs.SelectMany(static run => run.Glyphs)),
            PdfLayoutRectangle.Union(runs.Select(static run => run.Bounds)),
            source.DominantFontName,
            source.DominantFontSize,
            source.Direction,
            source.Color,
            runs);
    }

    private static PdfSemanticAlgorithmRule ToSemanticAlgorithmRule(AlgorithmRuleCandidate rule)
    {
        return new PdfSemanticAlgorithmRule(rule.SourcePathIndex, rule.Bounds, rule.Thickness, rule.Color);
    }

    private static CodeBlockCandidate[] DetectCodeBlocks(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float lineStep)
    {
        List<CodeBlockCandidate> blocks = [];
        List<CodeLineEvidence> current = [];
        foreach (LineCandidate line in lines
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            CodeLineEvidence? evidence = CreateCodeLineEvidence(page, line);
            if (evidence == null)
            {
                AddCodeBlockCandidate(blocks, current);
                current.Clear();
                continue;
            }

            if (current.Count > 0 && !CanGroupCodeLines(current[^1], evidence, lineStep))
            {
                AddCodeBlockCandidate(blocks, current);
                current.Clear();
            }

            current.Add(evidence);
        }

        AddCodeBlockCandidate(blocks, current);
        return blocks.ToArray();
    }

    private static CodeLineEvidence? CreateCodeLineEvidence(PdfLayoutPage page, LineCandidate line)
    {
        if (MathF.Abs(line.Direction) > 0.01f ||
            line.Text.Length < 2 ||
            line.Source.Runs.Any(static run => IsCodeIncompatibleMathFontName(run.FontName)) ||
            IsInsideFormControl(page, line.Bounds))
        {
            return null;
        }

        PdfTextRun[] textRuns = line.Source.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .ToArray();
        int characterCount = textRuns.Sum(static run => run.Text.Count(static character => !char.IsWhiteSpace(character)));
        int monospacedCharacters = textRuns
            .Where(static run => IsMonospacedFontName(run.FontName))
            .Sum(static run => run.Text.Count(static character => !char.IsWhiteSpace(character)));
        if (characterCount == 0 || monospacedCharacters < characterCount * 0.9f)
        {
            return null;
        }

        PdfTextGlyph[] glyphs = textRuns
            .Where(static run => IsMonospacedFontName(run.FontName))
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .ToArray();
        if (!TryEstimateStableCharacterPitch(glyphs, out float characterPitch))
        {
            return null;
        }

        return new CodeLineEvidence(line, NormalizeFontName(line.FontName), characterPitch);
    }

    private static bool CanGroupCodeLines(
        CodeLineEvidence previous,
        CodeLineEvidence current,
        float lineStep)
    {
        float verticalStep = current.Line.Bounds.Y - previous.Line.Bounds.Y;
        return verticalStep > 1f &&
            verticalStep <= MathF.Max(lineStep * 1.8f, previous.Line.FontSize * 2f) &&
            string.Equals(previous.FontName, current.FontName, StringComparison.Ordinal) &&
            MathF.Abs(previous.Line.FontSize - current.Line.FontSize) <= 0.5f &&
            MathF.Abs(previous.CharacterPitch - current.CharacterPitch) <=
                MathF.Max(previous.CharacterPitch, current.CharacterPitch) * 0.12f;
    }

    private static void AddCodeBlockCandidate(
        ICollection<CodeBlockCandidate> blocks,
        IReadOnlyList<CodeLineEvidence> lines)
    {
        if (lines.Count < 2 || lines.Any(static line => line.Line.Text.Contains('@', StringComparison.Ordinal)))
        {
            return;
        }

        float[] verticalSteps = lines
            .Pairwise(static (first, second) => second.Line.Bounds.Y - first.Line.Bounds.Y)
            .Order()
            .ToArray();
        float lineStep = verticalSteps[verticalSteps.Length / 2];
        if (verticalSteps.Length > 1 &&
            verticalSteps.Any(step => MathF.Abs(step - lineStep) > MathF.Max(1.5f, lineStep * 0.20f)))
        {
            return;
        }

        float[] pitches = lines.Select(static line => line.CharacterPitch).Order().ToArray();
        float characterPitch = pitches[pitches.Length / 2];
        float blockX = lines
            .SelectMany(static line => line.Line.Source.Runs)
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .Select(static glyph => glyph.Bounds.X)
            .DefaultIfEmpty(lines.Min(static line => line.Line.Bounds.X))
            .Min();
        string[] textLines = lines
            .Select(line => ReconstructPreformattedLine(line.Line, blockX, characterPitch))
            .ToArray();
        if (textLines.Any(static text => text.Length == 0) || textLines.Sum(static text => text.Trim().Length) < 12)
        {
            return;
        }

        int syntaxLines = textLines.Count(static text => CodeSyntaxPattern.IsMatch(text.TrimStart()));
        int[] indentation = textLines.Select(static text => text.Length - text.TrimStart().Length).ToArray();
        bool hasIndentation = indentation.Distinct().Count() > 1 && indentation.Max() >= 2;
        bool hasAlignedColumns = HasRepeatedAlignedWhitespace(textLines);
        if (!hasAlignedColumns && syntaxLines < 2 && !(hasIndentation && syntaxLines >= 1))
        {
            return;
        }

        blocks.Add(new CodeBlockCandidate(
            lines.ToArray(),
            string.Join('\n', textLines),
            characterPitch,
            lineStep));
    }

    private static bool HasRepeatedAlignedWhitespace(IReadOnlyList<string> lines)
    {
        int[] gapColumns = lines
            .Select(static line =>
            {
                for (int index = 1; index + 1 < line.Length; index++)
                {
                    if (!char.IsWhiteSpace(line[index - 1]) &&
                        char.IsWhiteSpace(line[index]) &&
                        char.IsWhiteSpace(line[index + 1]))
                    {
                        return index;
                    }
                }

                return -1;
            })
            .Where(static column => column >= 0)
            .ToArray();
        return gapColumns.Length >= 2 &&
            gapColumns.Any(column => gapColumns.Count(candidate => Math.Abs(candidate - column) <= 1) >= 2);
    }

    private static string ReconstructPreformattedLine(
        LineCandidate line,
        float blockX,
        float characterPitch)
    {
        PdfTextGlyph[] glyphs = line.Source.Runs
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ThenBy(static glyph => glyph.Bounds.Y)
            .ToArray();
        StringBuilder text = new();
        int column = 0;
        foreach (PdfTextGlyph glyph in glyphs)
        {
            int targetColumn = Math.Max(0, (int)MathF.Round((glyph.Bounds.X - blockX) / characterPitch));
            if (targetColumn > column)
            {
                text.Append(' ', targetColumn - column);
                column = targetColumn;
            }

            string value = glyph.Text.Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal);
            if (value.Length == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                int widthColumns = Math.Max(value.Length, (int)MathF.Round(glyph.Bounds.Width / characterPitch));
                text.Append(' ', Math.Max(1, widthColumns));
                column += Math.Max(1, widthColumns);
            }
            else
            {
                text.Append(value);
                column += value.Length;
            }
        }

        return text.ToString().TrimEnd();
    }

    private static bool TryEstimateStableCharacterPitch(
        IEnumerable<PdfTextGlyph> glyphSource,
        out float characterPitch)
    {
        float[] samples = glyphSource
            .Where(static glyph => glyph.Bounds.Width > 0.1f)
            .Select(static glyph =>
            {
                int characters = glyph.Text.Count(static character => character is not ('\r' or '\n'));
                return characters == 0 ? 0f : glyph.Bounds.Width / characters;
            })
            .Where(static sample => sample > 0.1f)
            .Order()
            .ToArray();
        if (samples.Length < 2)
        {
            characterPitch = 0f;
            return false;
        }

        float medianPitch = samples[samples.Length / 2];
        int inliers = samples.Count(sample =>
            MathF.Abs(sample - medianPitch) <= MathF.Max(0.2f, medianPitch * 0.15f));
        characterPitch = medianPitch;
        return inliers >= Math.Max(2, (int)MathF.Ceiling(samples.Length * 0.8f));
    }

    private static bool IsInsideFormControl(PdfLayoutPage page, PdfLayoutRectangle bounds)
    {
        float centerX = bounds.X + bounds.Width / 2f;
        float centerY = bounds.Y + bounds.Height / 2f;
        return page.FormControls.Any(control =>
            centerX >= control.Bounds.X && centerX <= control.Bounds.Right &&
            centerY >= control.Bounds.Y && centerY <= control.Bounds.Bottom);
    }

    private static bool IsMonospacedFontName(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Mono", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("NimbusMon", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Menlo", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Monaco", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Typewriter", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("CMTT", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("LMTT", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("SFTT", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("SourceCode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("FiraCode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Inconsolata", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("OCRB", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("LetterGothic", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("LucidaConsole", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodeIncompatibleMathFontName(string fontName)
    {
        return IsMathFont(fontName) && !IsMonospacedFontName(fontName);
    }

    private static PdfSemanticElement CreateCodeBlock(
        CodeBlockCandidate codeBlock,
        HashSet<int> consumed)
    {
        PdfSemanticLine[] lines = codeBlock.Lines
            .Select((evidence, index) =>
            {
                consumed.Add(evidence.Line.Index);
                PdfSemanticLine source = evidence.Line.SemanticLine;
                string text = codeBlock.Text.Split('\n')[index];
                return new PdfSemanticLine(
                    text,
                    source.Bounds,
                    source.DominantFontName,
                    source.DominantFontSize,
                    source.Direction,
                    source.Color,
                    source.Runs);
            })
            .ToArray();
        return new PdfSemanticElement(
            PdfSemanticElementKind.CodeBlock,
            codeBlock.Text,
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines);
    }

    private static bool IsHeading(
        LineCandidate line,
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep,
        PdfSemanticExtractionOptions options)
    {
        if (line.Text.Length < 3)
        {
            return false;
        }

        if (line.Bounds.Y < page.Height * 0.055f)
        {
            return MathF.Abs(line.Direction) < 0.01f &&
                line.FontSize >= bodyFontSize + 5f &&
                line.Text.Length <= 80;
        }

        if (IsStandaloneAbstractHeading(line.Text))
        {
            return true;
        }

        if (NumberedHeadingPattern.IsMatch(line.Text))
        {
            return line.FontSize >= bodyFontSize + options.HeadingFontSizeDelta || line.IsBold;
        }

        if (IsStandaloneBodySizeHeading(line, page, lines, bodyFontSize, lineStep))
        {
            return true;
        }

        if (IsWrappedProseLine(line, lines, bodyFontSize, lineStep))
        {
            return false;
        }

        bool largerThanBody = line.FontSize >= bodyFontSize + options.HeadingFontSizeDelta;
        if (!largerThanBody)
        {
            return false;
        }

        bool centered = MathF.Abs(line.CenterX - page.Width / 2f) < page.Width * 0.18f;
        bool shortLine = line.Text.Length <= 80;
        bool hasHeadingFont = line.IsBold || line.FontSize >= bodyFontSize + 3f;
        return hasHeadingFont && (centered || shortLine || line.Bounds.Y < page.Height * 0.30f);
    }

    private static bool IsWrappedProseLine(
        LineCandidate line,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep)
    {
        if (IsUniformlyBold(line) ||
            line.FontSize > bodyFontSize + 3.25f ||
            CountWords(line.Text) < 3 && !EndsSentence(line.Text))
        {
            return false;
        }

        return lines
            .Where(candidate => candidate.Index != line.Index)
            .Where(candidate => MathF.Abs(candidate.FontSize - line.FontSize) <= 0.35f)
            .Where(candidate => !IsUniformlyBold(candidate))
            .Where(candidate =>
                MathF.Abs(candidate.Bounds.Y - line.Bounds.Bottom) <= lineStep * 1.35f ||
                MathF.Abs(line.Bounds.Y - candidate.Bounds.Bottom) <= lineStep * 1.35f)
            .Any(candidate =>
                CountWords(candidate.Text) + CountWords(line.Text) >= 10 &&
                (candidate.Text.Length >= 36 || line.Text.Length >= 36));
    }

    private static bool IsStandaloneBodySizeHeading(
        LineCandidate line,
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep)
    {
        string text = line.Text.Trim();
        if (MathF.Abs(line.Direction) > 0.01f ||
            line.FontSize < bodyFontSize - 0.75f ||
            text.Length > 80 ||
            CountWords(text) > 12 ||
            text.EndsWith('.') ||
            text.EndsWith('?') ||
            text.EndsWith('!') ||
            text.EndsWith(';') ||
            line.Bounds.Width > page.Width * 0.65f ||
            !IsUniformlyBold(line))
        {
            return false;
        }

        LineCandidate? previous = lines
            .Where(candidate => candidate.Index != line.Index &&
                candidate.Bounds.Bottom <= line.Bounds.Y + 0.5f)
            .OrderByDescending(static candidate => candidate.Bounds.Bottom)
            .FirstOrDefault();
        if (previous == null)
        {
            return line.Bounds.Y < page.Height * 0.18f;
        }

        float gapBefore = line.Bounds.Y - previous.Bounds.Bottom;
        return gapBefore >= MathF.Max(3f, lineStep * 0.65f);
    }

    private static bool IsUniformlyBold(LineCandidate line)
    {
        PdfTextRun[] substantiveRuns = line.Source.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .ToArray();
        return substantiveRuns.Length > 0 &&
            substantiveRuns.All(static run => IsBoldFontName(run.FontName));
    }

    private static int HeadingLevel(LineCandidate line, float bodyFontSize)
    {
        Match numberedHeading = NumberedHeadingPattern.Match(line.Text);
        if (numberedHeading.Success)
        {
            int depth = numberedHeading.Groups["number"].Value.Count(static character => character == '.') + 1;
            return Math.Clamp(depth, 1, 6);
        }

        if (line.FontSize >= bodyFontSize + 5f)
        {
            return 1;
        }

        return 2;
    }

    private static bool IsDocumentTitle(LineCandidate line, PdfLayoutPage page, float bodyFontSize)
    {
        bool muchLargerThanBody = line.FontSize >= bodyFontSize + 4f;
        bool centered = MathF.Abs(line.CenterX - page.Width / 2f) < page.Width * 0.18f;
        bool highOnPage = line.Bounds.Y < page.Height * 0.30f;
        return muchLargerThanBody && centered && highOnPage;
    }

    private static LineCandidate[] GroupDocumentTitleLines(
        LineCandidate documentTitle,
        IReadOnlyList<LineCandidate> headingLines,
        PdfLayoutPage page,
        float bodyFontSize,
        float lineStep)
    {
        LineCandidate[] ordered = headingLines
            .Where(line => line.Bounds.Y < page.Height * 0.55f)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        int anchorIndex = Array.IndexOf(ordered, documentTitle);
        if (anchorIndex < 0)
        {
            return [documentTitle];
        }

        int start = anchorIndex;
        while (start > 0 && ShouldGroupDocumentTitleLines(
            ordered[start - 1],
            ordered[start],
            bodyFontSize,
            lineStep))
        {
            start--;
        }

        int end = anchorIndex;
        while (end + 1 < ordered.Length && ShouldGroupDocumentTitleLines(
            ordered[end],
            ordered[end + 1],
            bodyFontSize,
            lineStep))
        {
            end++;
        }

        return ordered[start..(end + 1)];
    }

    private static bool ShouldGroupDocumentTitleLines(
        LineCandidate previous,
        LineCandidate current,
        float bodyFontSize,
        float lineStep)
    {
        if (MathF.Abs(previous.Direction) > 0.01f ||
            MathF.Abs(previous.Direction - current.Direction) > 0.01f ||
            !SameColor(previous.Color, current.Color) ||
            !string.Equals(previous.FontName, current.FontName, StringComparison.Ordinal))
        {
            return false;
        }

        if (SharesTitleBaseline(previous, current))
        {
            return true;
        }

        if (HeadingLevel(previous, bodyFontSize) != HeadingLevel(current, bodyFontSize) ||
            MathF.Abs(previous.FontSize - current.FontSize) > 0.75f)
        {
            return false;
        }

        float verticalGap = current.Bounds.Y - previous.Bounds.Bottom;
        float maximumGap = MathF.Max(lineStep * 1.8f, MathF.Max(previous.FontSize, current.FontSize) * 0.85f);
        if (verticalGap < -2f || verticalGap > maximumGap)
        {
            return false;
        }

        float edgeTolerance = MathF.Max(4f, MathF.Max(previous.FontSize, current.FontSize) * 0.45f);
        return MathF.Abs(previous.Bounds.X - current.Bounds.X) <= edgeTolerance ||
            MathF.Abs(previous.Bounds.Right - current.Bounds.Right) <= edgeTolerance ||
            MathF.Abs(previous.CenterX - current.CenterX) <= edgeTolerance;
    }

    private static bool SharesTitleBaseline(LineCandidate first, LineCandidate second)
    {
        float maximumFontSize = MathF.Max(first.FontSize, second.FontSize);
        float baselineTolerance = MathF.Max(1f, maximumFontSize * 0.08f);
        if (MathF.Abs(first.Bounds.Bottom - second.Bounds.Bottom) > baselineTolerance)
        {
            return false;
        }

        float horizontalGap = HorizontalGap(first.Bounds, second.Bounds);
        return horizontalGap <= maximumFontSize * 3f;
    }

    private static LineCandidate[] MergeSameBaselineLines(
        IReadOnlyList<LineCandidate> lines,
        PdfSemanticExtractionOptions options)
    {
        List<List<LineCandidate>> rows = [];
        foreach (LineCandidate line in lines.OrderBy(static line => line.Bounds.Y).ThenBy(static line => line.Bounds.X))
        {
            List<LineCandidate>? row = rows.FirstOrDefault(existing => SharesTitleBaseline(existing[0], line));
            if (row == null)
            {
                rows.Add([line]);
            }
            else
            {
                row.Add(line);
            }
        }

        return rows
            .OrderBy(static row => row.Min(static line => line.Bounds.Y))
            .ThenBy(static row => row.Min(static line => line.Bounds.X))
            .Select(row => row.Count == 1 ? row[0] : MergeSameBaselineLine(row, options))
            .ToArray();
    }

    private static LineCandidate MergeSameBaselineLine(
        IReadOnlyList<LineCandidate> lines,
        PdfSemanticExtractionOptions options)
    {
        PdfTextRun[] runs = lines
            .SelectMany(static line => line.Source.Runs)
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .ToArray();
        string text = ReconstructText(runs.SelectMany(static run => run.Glyphs), options);
        LineCandidate titleStyle = lines
            .OrderByDescending(static line => line.FontSize)
            .ThenBy(static line => line.Bounds.Y)
            .First();
        PdfLayoutRectangle bounds = PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds));
        PdfSemanticLine semanticLine = new(
            text,
            bounds,
            titleStyle.FontName,
            titleStyle.FontSize,
            titleStyle.Direction,
            titleStyle.Color,
            runs);
        PdfTextLine sourceLine = new(text, bounds, runs);
        return new LineCandidate(
            lines.Min(static line => line.Index),
            sourceLine,
            semanticLine,
            titleStyle.FontName,
            titleStyle.FontSize,
            titleStyle.Direction,
            titleStyle.Color);
    }

    private static PdfSemanticElement? ExtractScientificFrontMatter(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        IReadOnlyList<LineCandidate> titleLines,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        if (titleLines.Count == 0)
        {
            return null;
        }

        PdfLayoutRectangle titleBounds = PdfLayoutRectangle.Union(titleLines.Select(static line => line.Bounds));
        LineCandidate? abstractBoundary = lines
            .Where(line => line.Bounds.Y > titleBounds.Bottom)
            .Where(line => line.Bounds.Y < page.Height * 0.65f)
            .Where(line => IsStandaloneAbstractHeading(line.Text) || StartsWithAbstractLeadIn(line.Text))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .FirstOrDefault();
        if (abstractBoundary == null)
        {
            return null;
        }

        LineCandidate[] band = lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(line => MathF.Abs(line.Direction) < 0.01f)
            .Where(line => line.Bounds.Y > titleBounds.Bottom + 2f)
            .Where(line => line.Bounds.Bottom < abstractBoundary.Bounds.Y - 1f)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        if (band.Length < 2 || band.Count(ContainsEmailAddress) > 1)
        {
            return null;
        }

        LineCandidate[] sourceRows = MergeFrontMatterSourceRows(band, options);
        if (sourceRows.Length < 2)
        {
            return null;
        }

        int centeredRows = sourceRows.Count(line =>
            MathF.Abs(line.CenterX - page.Width / 2f) <= page.Width * 0.12f &&
            line.Bounds.Width <= page.Width * 0.92f);
        bool hasContactSignal = sourceRows.Any(static line =>
            line.Text.Contains('@', StringComparison.Ordinal) ||
            line.Text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("www.", StringComparison.OrdinalIgnoreCase));
        if (centeredRows < Math.Max(2, (int)MathF.Ceiling(sourceRows.Length * 0.75f)) ||
            (!hasContactSignal && sourceRows.Length < 4))
        {
            return null;
        }

        foreach (LineCandidate line in band)
        {
            consumed.Add(line.Index);
        }

        PdfSemanticLine[] semanticLines = sourceRows.Select(static line => line.SemanticLine).ToArray();
        return new PdfSemanticElement(
            PdfSemanticElementKind.FrontMatter,
            string.Join(Environment.NewLine, semanticLines.Select(static line => line.Text)),
            PdfLayoutRectangle.Union(semanticLines.Select(static line => line.Bounds)),
            semanticLines);
    }

    private static LineCandidate[] MergeFrontMatterSourceRows(
        IReadOnlyList<LineCandidate> lines,
        PdfSemanticExtractionOptions options)
    {
        List<List<LineCandidate>> rows = [];
        foreach (LineCandidate line in lines.OrderBy(static line => line.Bounds.Y).ThenBy(static line => line.Bounds.X))
        {
            List<LineCandidate>? row = rows.FirstOrDefault(existing =>
                BelongsToFrontMatterRow(existing, line));
            if (row == null)
            {
                rows.Add([line]);
            }
            else
            {
                row.Add(line);
            }
        }

        return rows
            .OrderBy(static row => row.Min(static line => line.Bounds.Y))
            .ThenBy(static row => row.Min(static line => line.Bounds.X))
            .Select(row => row.Count == 1 ? row[0] : MergeSameBaselineLine(row, options))
            .ToArray();
    }

    private static bool BelongsToFrontMatterRow(
        IReadOnlyList<LineCandidate> row,
        LineCandidate candidate)
    {
        PdfLayoutRectangle rowBounds = PdfLayoutRectangle.Union(row.Select(static line => line.Bounds));
        float overlap = MathF.Min(rowBounds.Bottom, candidate.Bounds.Bottom) -
            MathF.Max(rowBounds.Y, candidate.Bounds.Y);
        float centerDistance = MathF.Abs(
            rowBounds.Y + rowBounds.Height / 2f -
            (candidate.Bounds.Y + candidate.Bounds.Height / 2f));
        bool sameSourceRow = overlap >= MathF.Min(rowBounds.Height, candidate.Bounds.Height) * 0.3f ||
            centerDistance <= MathF.Max(rowBounds.Height, candidate.Bounds.Height) * 0.65f;
        if (!sameSourceRow)
        {
            return false;
        }

        float maximumFontSize = MathF.Max(row.Max(static line => line.FontSize), candidate.FontSize);
        return HorizontalGap(rowBounds, candidate.Bounds) <= maximumFontSize * 3f;
    }

    private static bool ContainsEmailAddress(LineCandidate line)
    {
        return line.Source.Runs.Any(run => EmailPattern.IsMatch(run.Text));
    }

    private static bool IsStandaloneAbstractHeading(string text)
    {
        return string.Equals(text.Trim().TrimEnd('.', ':'), "Abstract", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithAbstractLeadIn(string text)
    {
        string trimmed = text.TrimStart();
        if (!trimmed.StartsWith("Abstract", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Length == "Abstract".Length)
        {
            return false;
        }

        return trimmed["Abstract".Length] is '.' or ':' or '-' or '\u2014';
    }

    private static bool IsFooter(LineCandidate line, PdfLayoutPage page, float bodyFontSize)
    {
        if (IsSymbolFootnoteMarker(line.Text))
        {
            return false;
        }

        if (line.Bounds.Y > page.Height * 0.92f)
        {
            return true;
        }

        bool centered = MathF.Abs(line.CenterX - page.Width / 2f) < page.Width * 0.08f;
        return line.Bounds.Y > page.Height * 0.88f &&
            line.Text.Length <= 4 &&
            line.FontSize <= bodyFontSize &&
            centered;
    }

    private static IEnumerable<PdfSemanticElement> ExtractAuthorBlocks(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        IReadOnlyList<LineCandidate> titleLines,
        IReadOnlyList<LineCandidate> headingLines,
        PdfSemanticExtractionOptions options,
        HashSet<int> consumed)
    {
        if (titleLines.Count == 0)
        {
            yield break;
        }

        HashSet<int> titleLineIndexes = titleLines.Select(static line => line.Index).ToHashSet();
        PdfLayoutRectangle titleBounds = PdfLayoutRectangle.Union(titleLines.Select(static line => line.Bounds));
        LineCandidate? nextHeading = headingLines
            .Where(line => !titleLineIndexes.Contains(line.Index) && line.Bounds.Y > titleBounds.Bottom)
            .OrderBy(static line => line.Bounds.Y)
            .FirstOrDefault();
        if (nextHeading == null)
        {
            yield break;
        }

        LineCandidate[] band = lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(line => line.Bounds.Y > titleBounds.Bottom + 8f && line.Bounds.Bottom < nextHeading.Bounds.Y - 8f)
            .ToArray();
        if (!band.Any(line => line.Source.Runs.Any(run => EmailPattern.IsMatch(run.Text))))
        {
            yield break;
        }

        List<AuthorSegment> segments = [];
        foreach (LineCandidate line in band)
        {
            foreach (PdfTextRun run in line.Source.Runs)
            {
                string text = ReconstructText(run.Glyphs, options);
                if (text.Length == 0)
                {
                    continue;
                }

                segments.Add(new AuthorSegment(
                    line,
                    run,
                    text,
                    run.Bounds,
                    run.Bounds.X + run.Bounds.Width / 2f));
            }
        }

        List<AuthorCluster> clusters = segments
            .Where(static segment => EmailPattern.IsMatch(segment.Text))
            .OrderBy(static segment => segment.Bounds.Y)
            .ThenBy(static segment => segment.Bounds.X)
            .Select(static segment => new AuthorCluster(segment))
            .ToList();

        foreach (AuthorSegment segment in segments.Where(static segment => !EmailPattern.IsMatch(segment.Text)))
        {
            AuthorCluster? cluster = clusters
                .Where(cluster => IsSameAuthorBand(segment, cluster))
                .Select(cluster => new
                {
                    Cluster = cluster,
                    Gap = HorizontalGap(segment.Bounds, cluster.Anchor.Bounds)
                })
                .Where(item => item.Gap <= options.AuthorColumnTolerance)
                .OrderBy(static item => item.Gap)
                .ThenBy(item => MathF.Abs(item.Cluster.Anchor.CenterX - segment.CenterX))
                .Select(static item => item.Cluster)
                .FirstOrDefault();

            cluster?.Add(segment);
        }

        foreach (AuthorCluster cluster in clusters
            .OrderBy(static cluster => cluster.Bounds.Y)
            .ThenBy(static cluster => cluster.Bounds.X))
        {
            PdfSemanticElement? element = CreateAuthorElement(cluster);
            if (element == null)
            {
                continue;
            }

            foreach (AuthorSegment segment in cluster.Segments)
            {
                consumed.Add(segment.Line.Index);
            }

            yield return element;
        }
    }

    private static PdfSemanticElement? CreateAuthorElement(AuthorCluster cluster)
    {
        List<List<AuthorSegment>> rows = [];
        foreach (AuthorSegment segment in cluster.Segments.OrderBy(static segment => segment.Bounds.Y))
        {
            List<AuthorSegment>? row = rows.FirstOrDefault(row =>
                MathF.Abs(row[0].Bounds.Y - segment.Bounds.Y) <= 3f);
            if (row == null)
            {
                rows.Add([segment]);
            }
            else
            {
                row.Add(segment);
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        List<PdfSemanticLine> semanticLines = [];
        for (int index = 0; index < rows.Count; index++)
        {
            List<AuthorSegment> row = rows[index].OrderBy(static segment => segment.Bounds.X).ToList();
            string rowText = string.Join(" ", row.Select(static segment => segment.Text));
            if (index + 1 < rows.Count && row.All(static segment => IsFootnoteMarker(segment.Text)))
            {
                List<AuthorSegment> nextRow = rows[index + 1].OrderBy(static segment => segment.Bounds.X).ToList();
                string nextText = string.Join(" ", nextRow.Select(static segment => segment.Text));
                semanticLines.Add(CreateSyntheticLine(
                    nextText + " " + rowText,
                    row.Concat(nextRow).ToArray()));
                index++;
                continue;
            }

            semanticLines.Add(CreateSyntheticLine(rowText, row));
        }

        return new PdfSemanticElement(
            PdfSemanticElementKind.AuthorBlock,
            string.Join(Environment.NewLine, semanticLines.Select(static line => line.Text)),
            PdfLayoutRectangle.Union(semanticLines.Select(static line => line.Bounds)),
            semanticLines);
    }

    private static IEnumerable<PdfSemanticElement> ExtractFootnotes(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        HashSet<int> consumed)
    {
        float footnoteTop = page.Height * 0.70f;
        LineCandidate[] candidates = lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(line => line.Bounds.Y >= footnoteTop && line.Bounds.Y < page.Height * 0.92f)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();

        List<LineCandidate> current = [];
        foreach (LineCandidate line in candidates)
        {
            if (IsFootnoteMarkerLine(line, page))
            {
                if (current.Count > 0)
                {
                    yield return CreateFootnote(current, consumed);
                    current.Clear();
                }

                current.Add(line);
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            yield return CreateFootnote(current, consumed);
        }
    }

    private static PdfSemanticElement CreateFootnote(IReadOnlyList<LineCandidate> lines, HashSet<int> consumed)
    {
        foreach (LineCandidate line in lines)
        {
            consumed.Add(line.Index);
        }

        LineCandidate[] readingLines = OrderLinesForReading(lines);
        string text = JoinParagraphLines(readingLines.Select(static line => line.SemanticLine));
        string marker = FootnoteMarkerFromText(text);
        return new PdfSemanticElement(
            PdfSemanticElementKind.Footnote,
            text,
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            readingLines.Select(static line => line.SemanticLine).ToArray(),
            note: marker.Length > 0 ? new PdfSemanticNote(marker) : null);
    }

    private static string FootnoteMarkerFromText(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return "";
        }

        if (trimmed[0] is '*' or '∗' or '†' or '‡')
        {
            return trimmed[..1];
        }

        int length = 0;
        while (length < trimmed.Length && length < 2 && char.IsDigit(trimmed[length]))
        {
            length++;
        }

        return length > 0 && (length == trimmed.Length || char.IsWhiteSpace(trimmed[length]))
            ? trimmed[..length]
            : "";
    }

    private static IEnumerable<PdfSemanticElement> ExtractDefinitionLists(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        if (page.FormControls.Count > 0)
        {
            yield break;
        }

        DefinitionSourceRow[] rows = BuildDefinitionSourceRows(lines, consumed, options).ToArray();
        foreach (PdfSemanticElement definitionList in ExtractInlineDefinitionLists(
            page,
            rows,
            bodyFontSize,
            lineStep,
            consumed,
            options))
        {
            yield return definitionList;
        }

        foreach (PdfSemanticElement definitionList in ExtractStackedDefinitionLists(
            page,
            rows,
            bodyFontSize,
            lineStep,
            consumed))
        {
            yield return definitionList;
        }
    }

    private static IEnumerable<DefinitionSourceRow> BuildDefinitionSourceRows(
        IReadOnlyList<LineCandidate> lines,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        List<LineRow> rows = [];
        foreach (LineCandidate line in lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            LineRow? row = rows.FirstOrDefault(row => row.Contains(line));
            if (row == null)
            {
                rows.Add(new LineRow(line));
            }
            else
            {
                row.Add(line);
            }
        }

        foreach (LineRow row in rows.OrderBy(static row => row.Bounds.Y).ThenBy(static row => row.Bounds.X))
        {
            PdfTextRun[] runs = row.Lines
                .SelectMany(static line => line.Source.Runs)
                .Where(static run => MathF.Abs(run.Direction) < 0.01f)
                .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
                .OrderBy(static run => run.Bounds.X)
                .ThenBy(static run => run.Bounds.Y)
                .ToArray();
            if (runs.Length > 0)
            {
                yield return new DefinitionSourceRow(
                    row.Lines,
                    runs,
                    CreateDefinitionSemanticLine(runs, options));
            }
        }
    }

    private static PdfSemanticLine CreateDefinitionSemanticLine(
        IReadOnlyList<PdfTextRun> runs,
        PdfSemanticExtractionOptions options)
    {
        (string fontName, float fontSize, float direction, PdfLayoutColor color) = DominantStyle(runs);
        return new PdfSemanticLine(
            ReconstructText(runs.SelectMany(static run => run.Glyphs), options),
            PdfLayoutRectangle.Union(runs.Select(static run => run.Bounds)),
            fontName,
            fontSize,
            direction,
            color,
            runs);
    }

    private static IEnumerable<PdfSemanticElement> ExtractInlineDefinitionLists(
        PdfLayoutPage page,
        IReadOnlyList<DefinitionSourceRow> rows,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        int index = 0;
        while (index < rows.Count)
        {
            if (IsConsumed(rows[index], consumed) ||
                !TryCreateInlineDefinitionEntry(rows[index], bodyFontSize, options, out DefinitionSourceEntry? first))
            {
                index++;
                continue;
            }

            int start = index;
            List<DefinitionSourceEntry> entries = [first!];
            DefinitionSourceEntry current = first!;
            index++;
            while (index < rows.Count && !IsConsumed(rows[index], consumed))
            {
                DefinitionSourceRow row = rows[index];
                float gap = MathF.Max(0f, row.Bounds.Y - current.Bounds.Bottom);
                if (gap > MathF.Max(lineStep * 1.85f, bodyFontSize * 2.2f))
                {
                    break;
                }

                if (TryCreateInlineDefinitionEntry(row, bodyFontSize, options, out DefinitionSourceEntry? next) &&
                    AreCompatibleDefinitionEntries(first!, next!, page))
                {
                    if (ShouldAssociateAdditionalTerm(current, next!))
                    {
                        current.AddTermsAndDefinition(
                            next!.Terms,
                            next.DefinitionLines,
                            next.SourceLines);
                    }
                    else
                    {
                        entries.Add(next!);
                        current = next!;
                    }

                    index++;
                    continue;
                }

                if (IsInlineDefinitionContinuation(row, current, page))
                {
                    current.AddDefinitionLine(row.SemanticLine, row.Lines);
                    index++;
                    continue;
                }

                break;
            }

            if (entries.Count >= 3 &&
                !HasDefinitionTableRules(page, DefinitionListBounds(entries)))
            {
                yield return CreateDefinitionListElement(entries, consumed, preserveColumns: true);
                continue;
            }

            index = start + 1;
        }
    }

    private static bool TryCreateInlineDefinitionEntry(
        DefinitionSourceRow row,
        float bodyFontSize,
        PdfSemanticExtractionOptions options,
        out DefinitionSourceEntry? entry)
    {
        entry = null;
        PdfTextRun[] runs = row.Runs.ToArray();
        int normalRunIndex = 0;
        while (normalRunIndex < runs.Length && IsBoldFontName(runs[normalRunIndex].FontName))
        {
            normalRunIndex++;
        }

        if (normalRunIndex > 0 && normalRunIndex < runs.Length)
        {
            PdfTextRun[] termRuns = runs[..normalRunIndex];
            PdfTextRun[] definitionRuns = runs[normalRunIndex..];
            DefinitionSourceKind kind = HorizontalGap(termRuns[^1].Bounds, definitionRuns[0].Bounds) >=
                MathF.Max(8f, bodyFontSize * 0.9f)
                    ? DefinitionSourceKind.Columns
                    : DefinitionSourceKind.Inline;
            if (TryCreateInlineDefinitionEntry(
                row,
                termRuns,
                definitionRuns,
                kind,
                bodyFontSize,
                options,
                out entry))
            {
                return true;
            }
        }

        float splitGap = MathF.Max(8f, bodyFontSize * 0.9f);
        int largestGapIndex = -1;
        float largestGap = splitGap;
        for (int index = 1; index < runs.Length; index++)
        {
            float gap = HorizontalGap(runs[index - 1].Bounds, runs[index].Bounds);
            if (gap > largestGap)
            {
                largestGap = gap;
                largestGapIndex = index;
            }
        }

        if (largestGapIndex <= 0)
        {
            return false;
        }

        return TryCreateInlineDefinitionEntry(
            row,
            runs[..largestGapIndex],
            runs[largestGapIndex..],
            DefinitionSourceKind.Columns,
            bodyFontSize,
            options,
            out entry);
    }

    private static bool TryCreateInlineDefinitionEntry(
        DefinitionSourceRow row,
        IReadOnlyList<PdfTextRun> termRuns,
        IReadOnlyList<PdfTextRun> definitionRuns,
        DefinitionSourceKind kind,
        float bodyFontSize,
        PdfSemanticExtractionOptions options,
        out DefinitionSourceEntry? entry)
    {
        entry = null;
        PdfSemanticLine termLine = CreateDefinitionSemanticLine(termRuns, options);
        PdfSemanticLine definitionLine = CreateDefinitionSemanticLine(definitionRuns, options);
        bool sourceMarksTerm = termRuns.All(static run => IsBoldFontName(run.FontName)) || LooksLikeAcronym(termLine.Text);
        if (!sourceMarksTerm ||
            termRuns.Max(static run => run.FontSize) > bodyFontSize + 1.25f ||
            !LooksLikeDefinitionTerm(termLine.Text) ||
            !LooksLikeDefinitionText(definitionLine.Text))
        {
            return false;
        }

        PdfSemanticDefinitionTerm term = new(termLine.Text, termLine.Bounds, [termLine]);
        entry = new DefinitionSourceEntry(
            [term],
            [definitionLine],
            row.Lines,
            kind,
            termLine.Bounds.X,
            definitionLine.Bounds.X);
        return true;
    }

    private static bool AreCompatibleDefinitionEntries(
        DefinitionSourceEntry first,
        DefinitionSourceEntry next,
        PdfLayoutPage page)
    {
        if (first.Kind != next.Kind ||
            MathF.Abs(first.TermLeft - next.TermLeft) > MathF.Max(12f, page.Width * 0.035f))
        {
            return false;
        }

        return first.Kind != DefinitionSourceKind.Columns ||
            MathF.Abs(first.DefinitionLeft - next.DefinitionLeft) <= MathF.Max(18f, page.Width * 0.045f);
    }

    private static bool ShouldAssociateAdditionalTerm(
        DefinitionSourceEntry current,
        DefinitionSourceEntry next)
    {
        if (current.Kind != DefinitionSourceKind.Columns || current.DefinitionLines.Count == 0)
        {
            return false;
        }

        string previousDefinition = current.DefinitionLines[^1].Text.TrimEnd();
        string nextDefinition = next.DefinitionLines[0].Text.TrimStart();
        return previousDefinition.Length > 0 &&
            nextDefinition.Length > 0 &&
            !EndsSentence(previousDefinition) &&
            char.IsLower(nextDefinition[0]);
    }

    private static bool IsInlineDefinitionContinuation(
        DefinitionSourceRow row,
        DefinitionSourceEntry current,
        PdfLayoutPage page)
    {
        if (!LooksLikeDefinitionText(row.SemanticLine.Text) ||
            row.Runs.All(static run => IsBoldFontName(run.FontName)))
        {
            return false;
        }

        float tolerance = MathF.Max(16f, page.Width * 0.04f);
        if (current.Kind == DefinitionSourceKind.Columns)
        {
            return MathF.Abs(row.Bounds.X - current.DefinitionLeft) <= tolerance;
        }

        return row.Bounds.X >= current.TermLeft - tolerance &&
            row.Bounds.X <= current.DefinitionLeft + tolerance;
    }

    private static IEnumerable<PdfSemanticElement> ExtractStackedDefinitionLists(
        PdfLayoutPage page,
        IReadOnlyList<DefinitionSourceRow> rows,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed)
    {
        int index = 0;
        while (index < rows.Count)
        {
            if (IsConsumed(rows[index], consumed) || !IsStackedDefinitionTerm(rows[index], bodyFontSize))
            {
                index++;
                continue;
            }

            int start = index;
            List<DefinitionSourceEntry> entries = [];
            while (index < rows.Count && !IsConsumed(rows[index], consumed))
            {
                if (!IsStackedDefinitionTerm(rows[index], bodyFontSize))
                {
                    break;
                }

                List<DefinitionSourceRow> termRows = [rows[index]];
                index++;
                while (index < rows.Count &&
                    !IsConsumed(rows[index], consumed) &&
                    IsStackedDefinitionTerm(rows[index], bodyFontSize) &&
                    rows[index].Bounds.Y - termRows[^1].Bounds.Bottom <= lineStep * 1.25f)
                {
                    termRows.Add(rows[index]);
                    index++;
                }

                List<DefinitionSourceRow> definitionRows = [];
                DefinitionSourceRow previous = termRows[^1];
                while (index < rows.Count && !IsConsumed(rows[index], consumed))
                {
                    DefinitionSourceRow row = rows[index];
                    float gap = MathF.Max(0f, row.Bounds.Y - previous.Bounds.Bottom);
                    if (gap > MathF.Max(lineStep * 1.9f, bodyFontSize * 2.25f) ||
                        IsStackedDefinitionTerm(row, bodyFontSize))
                    {
                        break;
                    }

                    bool isReference = DefinitionReferencePattern.IsMatch(row.SemanticLine.Text.Trim());
                    if (!LooksLikeDefinitionText(row.SemanticLine.Text) && !isReference ||
                        row.Runs.All(static run => IsBoldFontName(run.FontName)) && !isReference ||
                        MathF.Abs(row.Bounds.X - termRows[0].Bounds.X) > MathF.Max(28f, page.Width * 0.075f))
                    {
                        break;
                    }

                    definitionRows.Add(row);
                    previous = row;
                    index++;
                }

                if (definitionRows.Count == 0)
                {
                    break;
                }

                PdfSemanticLine[] termLines = termRows.Select(static row => row.SemanticLine).ToArray();
                PdfSemanticDefinitionTerm term = new(
                    JoinParagraphLines(termLines),
                    PdfLayoutRectangle.Union(termLines.Select(static line => line.Bounds)),
                    termLines);
                DefinitionSourceEntry entry = new(
                    [term],
                    definitionRows.Select(static row => row.SemanticLine),
                    termRows.SelectMany(static row => row.Lines)
                        .Concat(definitionRows.SelectMany(static row => row.Lines)),
                    DefinitionSourceKind.Stacked,
                    term.Bounds.X,
                    definitionRows[0].Bounds.X);
                entries.Add(entry);

                if (index >= rows.Count ||
                    IsConsumed(rows[index], consumed) ||
                    !IsStackedDefinitionTerm(rows[index], bodyFontSize))
                {
                    break;
                }

                float entryGap = MathF.Max(0f, rows[index].Bounds.Y - entry.Bounds.Bottom);
                if (entryGap > MathF.Max(lineStep * 2.5f, bodyFontSize * 3f))
                {
                    break;
                }
            }

            if (entries.Count >= 4 &&
                IsLikelyStackedDefinitionGroup(entries) &&
                !HasDefinitionTableRules(page, DefinitionListBounds(entries)))
            {
                yield return CreateDefinitionListElement(entries, consumed, preserveColumns: false);
                continue;
            }

            index = start + 1;
        }
    }

    private static bool IsStackedDefinitionTerm(DefinitionSourceRow row, float bodyFontSize)
    {
        return row.Runs.Count > 0 &&
            row.Runs.All(static run => IsBoldFontName(run.FontName)) &&
            row.Runs.Max(static run => run.FontSize) <= bodyFontSize + 0.5f &&
            row.Runs.Min(static run => run.FontSize) >= bodyFontSize - 2.25f &&
            LooksLikeDefinitionTerm(row.SemanticLine.Text);
    }

    private static bool IsLikelyStackedDefinitionGroup(IReadOnlyList<DefinitionSourceEntry> entries)
    {
        int glossaryStyleTerms = entries.Count(entry =>
        {
            string text = entry.Terms[0].Text.TrimStart();
            return text.Length > 0 && (char.IsLower(text[0]) || LooksLikeAcronym(text));
        });
        int[] wordCounts = entries
            .Select(entry => WhitespacePattern.Split(entry.Terms[0].Text.Trim()).Count(static word => word.Length > 0))
            .OrderBy(static count => count)
            .ToArray();
        return glossaryStyleTerms >= Math.Max(1, entries.Count / 3) ||
            wordCounts[wordCounts.Length / 2] <= 3;
    }

    private static PdfLayoutRectangle DefinitionListBounds(IReadOnlyList<DefinitionSourceEntry> entries)
    {
        return PdfLayoutRectangle.Union(entries.Select(static entry => entry.Bounds));
    }

    private static bool HasDefinitionTableRules(PdfLayoutPage page, PdfLayoutRectangle bounds)
    {
        float tolerance = 3f;
        bool hasNearbyTableCaption = page.Lines
            .Where(line => line.Bounds.Bottom <= bounds.Y + tolerance)
            .Where(line => bounds.Y - line.Bounds.Bottom <= 90f)
            .Any(line => TableCaptionPattern.IsMatch(line.Text.TrimStart()));
        if (hasNearbyTableCaption)
        {
            return true;
        }

        PdfLayoutPath[] rules = page.Paths
            .Where(static path => path.IsStroked || path.IsFilled)
            .Where(path => path.Bounds.Bottom >= bounds.Y - tolerance && path.Bounds.Y <= bounds.Bottom + tolerance)
            .Where(path => path.Bounds.Right >= bounds.X - tolerance && path.Bounds.X <= bounds.Right + tolerance)
            .Where(static path =>
                path.Bounds.Width >= MathF.Max(8f, path.Bounds.Height * 4f) ||
                path.Bounds.Height >= MathF.Max(8f, path.Bounds.Width * 4f))
            .ToArray();
        int horizontalRules = rules.Count(static path => path.Bounds.Width >= path.Bounds.Height * 4f);
        int verticalRules = rules.Count(static path => path.Bounds.Height >= path.Bounds.Width * 4f);
        return rules.Length >= 3 && horizontalRules >= 2 && verticalRules >= 1;
    }

    private static bool LooksLikeDefinitionTerm(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 96 ||
            trimmed[0] is '\u0095' or '\u2022' ||
            NumberedHeadingPattern.IsMatch(trimmed) ||
            Regex.IsMatch(trimmed, @"^\d+(?:\.\d+)*\.?\s+") ||
            trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?') ||
            trimmed.EndsWith(':'))
        {
            return false;
        }

        int letters = trimmed.Count(char.IsLetter);
        int digits = trimmed.Count(char.IsDigit);
        int words = WhitespacePattern.Split(trimmed).Count(static word => word.Length > 0);
        return letters > 0 && digits <= Math.Max(letters, 3) && words <= 10;
    }

    private static bool LooksLikeDefinitionText(string text)
    {
        string trimmed = text.Trim();
        int letters = trimmed.Count(char.IsLetter);
        int digits = trimmed.Count(char.IsDigit);
        return trimmed.Length >= 8 &&
            letters >= 4 &&
            letters >= digits;
    }

    private static bool LooksLikeAcronym(string text)
    {
        string compact = new(text.Where(static character => char.IsLetterOrDigit(character)).ToArray());
        return compact.Length is >= 2 and <= 12 &&
            compact.Any(char.IsLetter) &&
            compact.Where(char.IsLetter).All(char.IsUpper);
    }

    private static bool IsConsumed(DefinitionSourceRow row, HashSet<int> consumed)
    {
        return row.Lines.All(line => consumed.Contains(line.Index));
    }

    private static PdfSemanticElement CreateDefinitionListElement(
        IReadOnlyList<DefinitionSourceEntry> sourceEntries,
        HashSet<int> consumed,
        bool preserveColumns)
    {
        foreach (LineCandidate line in sourceEntries.SelectMany(static entry => entry.SourceLines).Distinct())
        {
            consumed.Add(line.Index);
        }

        PdfSemanticDefinitionListEntry[] entries = sourceEntries
            .Select(static entry => entry.ToSemanticEntry())
            .ToArray();
        PdfSemanticLine[] lines = sourceEntries
            .SelectMany(static entry => entry.Terms.SelectMany(static term => term.Lines)
                .Concat(entry.DefinitionLines))
            .ToArray();
        float? termColumnWidth = null;
        float columnGap = 0f;
        if (preserveColumns)
        {
            float left = sourceEntries.Min(static entry => entry.Terms.Min(static term => term.Bounds.X));
            termColumnWidth = sourceEntries.Max(entry => entry.Terms.Max(term => term.Bounds.Right)) - left;
            float[] gaps = sourceEntries
                .Select(entry => entry.DefinitionLeft - entry.Terms.Max(static term => term.Bounds.Right))
                .Where(static gap => gap >= 0f)
                .OrderBy(static gap => gap)
                .ToArray();
            columnGap = gaps.Length == 0 ? 0f : gaps[gaps.Length / 2];
        }

        PdfSemanticDefinitionList definitionList = new(entries, termColumnWidth, columnGap);
        string text = string.Join(
            Environment.NewLine,
            entries.Select(static entry =>
                string.Join("; ", entry.Terms.Select(static term => term.Text)) + "\t" + entry.Definition.Text));
        return new PdfSemanticElement(
            PdfSemanticElementKind.DefinitionList,
            text,
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines,
            definitionList: definitionList);
    }

    private static IEnumerable<PdfSemanticElement> ExtractDocumentIndexes(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed)
    {
        foreach (LineCandidate heading in lines
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            if (consumed.Contains(heading.Index) ||
                !TryGetDocumentIndexKind(heading.Text, out PdfSemanticDocumentIndexKind kind))
            {
                continue;
            }

            List<RawDocumentIndexItem> rawItems = [];
            foreach (LineCandidate line in lines
                .Where(line => line.Bounds.Y >= heading.Bounds.Bottom)
                .OrderBy(static line => line.Bounds.Y)
                .ThenBy(static line => line.Bounds.X))
            {
                if (line.Index == heading.Index || consumed.Contains(line.Index))
                {
                    continue;
                }

                if (TryGetDocumentIndexKind(line.Text, out _))
                {
                    break;
                }

                if (TryParseDocumentIndexItem(page, line, out RawDocumentIndexItem item))
                {
                    rawItems.Add(item);
                    continue;
                }

                if (rawItems.Count > 0 || line.Bounds.Y - heading.Bounds.Bottom > lineStep * 3f)
                {
                    break;
                }
            }

            if (!TryCreateDocumentIndexElement(
                    page,
                    heading,
                    kind,
                    rawItems,
                    bodyFontSize,
                    out PdfSemanticElement element))
            {
                continue;
            }

            consumed.Add(heading.Index);
            foreach (RawDocumentIndexItem item in rawItems)
            {
                consumed.Add(item.Line.Index);
            }

            yield return element;
        }
    }

    private static bool TryGetDocumentIndexKind(
        string text,
        out PdfSemanticDocumentIndexKind kind)
    {
        string normalized = WhitespacePattern.Replace(text.Trim().TrimEnd(':'), " ");
        if (normalized.Equals("Contents", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Table of Contents", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfSemanticDocumentIndexKind.TableOfContents;
            return true;
        }

        if (normalized.Equals("List of Figures", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfSemanticDocumentIndexKind.ListOfFigures;
            return true;
        }

        if (normalized.Equals("List of Tables", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfSemanticDocumentIndexKind.ListOfTables;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryParseDocumentIndexItem(
        PdfLayoutPage page,
        LineCandidate line,
        out RawDocumentIndexItem item)
    {
        item = null!;
        if (MathF.Abs(line.Direction) > 0.01f || !line.Text.Any(char.IsLetter))
        {
            return false;
        }

        string label;
        string pageLabel;
        float pageRight;
        MatchCollection leaders = DocumentIndexLeaderPattern.Matches(line.Text);
        if (leaders.Count > 0)
        {
            Match leader = leaders[^1];
            label = NormalizeDocumentIndexLabel(line.Text[..leader.Index]);
            if (!TryNormalizeDocumentIndexPageLabel(
                    line.Text[(leader.Index + leader.Length)..],
                    out pageLabel))
            {
                return false;
            }

            pageRight = line.Bounds.Right;
        }
        else if (!TryParseFlexibleDocumentIndexItem(page, line, out label, out pageLabel, out pageRight))
        {
            return false;
        }

        if (label.Length < 2 || !label.Any(char.IsLetter))
        {
            return false;
        }

        item = new RawDocumentIndexItem(
            line,
            label,
            pageLabel,
            line.Bounds.X,
            pageRight,
            DocumentIndexLinkForLine(page, line));
        return true;
    }

    private static bool TryParseFlexibleDocumentIndexItem(
        PdfLayoutPage page,
        LineCandidate line,
        out string label,
        out string pageLabel,
        out float pageRight)
    {
        label = "";
        pageLabel = "";
        pageRight = 0f;
        PdfTextRun[] runs = line.Source.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .OrderBy(static run => run.Bounds.X)
            .ToArray();
        if (runs.Length < 2 ||
            !TryNormalizeDocumentIndexPageLabel(runs[^1].Text, out pageLabel))
        {
            return false;
        }

        float precedingRight = runs.Take(runs.Length - 1).Max(static run => run.Bounds.Right);
        float gap = runs[^1].Bounds.X - precedingRight;
        if (gap < MathF.Max(12f, line.FontSize * 1.25f) ||
            runs[^1].Bounds.Right < page.Width * 0.60f)
        {
            return false;
        }

        label = NormalizeDocumentIndexLabel(string.Join(
            " ",
            runs.Take(runs.Length - 1).Select(static run => run.Text.Trim())));
        MatchCollection trailingLeaders = DocumentIndexLeaderPattern.Matches(label);
        if (trailingLeaders.Count > 0)
        {
            Match trailingLeader = trailingLeaders[^1];
            if (trailingLeader.Index + trailingLeader.Length == label.Length)
            {
                label = NormalizeDocumentIndexLabel(label[..trailingLeader.Index]);
            }
        }

        pageRight = runs[^1].Bounds.Right;
        return true;
    }

    private static string NormalizeDocumentIndexLabel(string text)
    {
        return WhitespacePattern.Replace(text.Trim(), " ");
    }

    private static bool TryNormalizeDocumentIndexPageLabel(string text, out string pageLabel)
    {
        pageLabel = WhitespacePattern.Replace(text.Trim(), "");
        if (!DocumentIndexPageLabelPattern.IsMatch(pageLabel))
        {
            pageLabel = "";
            return false;
        }

        return true;
    }

    private static PdfLayoutLink? DocumentIndexLinkForLine(PdfLayoutPage page, LineCandidate line)
    {
        PdfLayoutRectangle expandedLine = ExpandRectangle(
            line.Bounds,
            2f,
            MathF.Max(2f, line.FontSize * 0.55f));
        return page.Links
            .Where(static link => !string.IsNullOrWhiteSpace(link.Uri) || link.DestinationPageNumber.HasValue)
            .Where(link => (link.QuadBounds.Count == 0 ? [link.Bounds] : link.QuadBounds)
                .Any(bounds => Intersects(bounds, expandedLine)))
            .OrderBy(link => (link.QuadBounds.Count == 0 ? [link.Bounds] : link.QuadBounds)
                .Min(bounds => MathF.Abs(
                    bounds.Y + bounds.Height / 2f - (line.Bounds.Y + line.Bounds.Height / 2f))))
            .ThenBy(link => (link.QuadBounds.Count == 0 ? [link.Bounds] : link.QuadBounds)
                .Min(static bounds => bounds.Width * bounds.Height))
            .FirstOrDefault();
    }

    private static bool TryCreateDocumentIndexElement(
        PdfLayoutPage page,
        LineCandidate heading,
        PdfSemanticDocumentIndexKind kind,
        IReadOnlyList<RawDocumentIndexItem> rawItems,
        float bodyFontSize,
        out PdfSemanticElement element)
    {
        element = null!;
        if (rawItems.Count < 2 || !AssignDocumentIndexIndentationLevels(rawItems))
        {
            return false;
        }

        float medianPageRight = rawItems
            .Select(static item => item.PageRight)
            .Order()
            .ElementAt(rawItems.Count / 2);
        float rightTolerance = MathF.Max(12f, bodyFontSize * 1.5f);
        int alignedPageNumbers = rawItems.Count(item =>
            MathF.Abs(item.PageRight - medianPageRight) <= rightTolerance);
        if (medianPageRight < page.Width * 0.60f ||
            alignedPageNumbers < (int)MathF.Ceiling(rawItems.Count * 0.75f) ||
            !TryBuildDocumentIndexItems(rawItems, out PdfSemanticDocumentIndexItem[] items))
        {
            return false;
        }

        PdfSemanticLine[] sourceLines =
        [
            heading.SemanticLine,
            .. rawItems.Select(static item => item.Line.SemanticLine)
        ];
        PdfSemanticDocumentIndex documentIndex = new(
            kind,
            NormalizeDocumentIndexLabel(heading.Text).TrimEnd(':'),
            [heading.SemanticLine],
            items);
        element = new PdfSemanticElement(
            PdfSemanticElementKind.Navigation,
            string.Join(
                Environment.NewLine,
                new[] { documentIndex.Heading }
                    .Concat(rawItems.Select(static item => $"{item.Label} {item.PageLabel}"))),
            PdfLayoutRectangle.Union(sourceLines.Select(static line => line.Bounds)),
            sourceLines,
            headingLevel: HeadingLevel(heading, bodyFontSize),
            documentIndex: documentIndex);
        return true;
    }

    private static bool AssignDocumentIndexIndentationLevels(IReadOnlyList<RawDocumentIndexItem> items)
    {
        List<float> anchors = [];
        foreach (RawDocumentIndexItem item in items)
        {
            float tolerance = MathF.Max(5f, item.Line.FontSize * 0.55f);
            int anchorIndex = anchors.FindIndex(anchor => MathF.Abs(anchor - item.AnchorX) <= tolerance);
            if (anchorIndex < 0)
            {
                anchors.Add(item.AnchorX);
            }
            else
            {
                anchors[anchorIndex] = (anchors[anchorIndex] + item.AnchorX) / 2f;
            }
        }

        anchors.Sort();
        if (anchors.Count > 6)
        {
            return false;
        }

        foreach (RawDocumentIndexItem item in items)
        {
            item.Level = anchors
                .Select((anchor, index) => new { Index = index, Distance = MathF.Abs(anchor - item.AnchorX) })
                .OrderBy(static match => match.Distance)
                .First()
                .Index;
        }

        return items[0].Level == 0 &&
            items.Skip(1).Select((item, index) => item.Level <= items[index].Level + 1).All(static valid => valid);
    }

    private static bool TryBuildDocumentIndexItems(
        IReadOnlyList<RawDocumentIndexItem> rawItems,
        out PdfSemanticDocumentIndexItem[] items)
    {
        List<RawDocumentIndexItem> roots = [];
        List<RawDocumentIndexItem> stack = [];
        foreach (RawDocumentIndexItem item in rawItems)
        {
            if (item.Level > stack.Count)
            {
                items = [];
                return false;
            }

            if (stack.Count > item.Level)
            {
                stack.RemoveRange(item.Level, stack.Count - item.Level);
            }

            if (item.Level == 0)
            {
                roots.Add(item);
            }
            else
            {
                stack[^1].Children.Add(item);
            }

            stack.Add(item);
        }

        items = roots.Select(CreateDocumentIndexItem).ToArray();
        return items.Length > 0;
    }

    private static PdfSemanticDocumentIndexItem CreateDocumentIndexItem(RawDocumentIndexItem item)
    {
        return new PdfSemanticDocumentIndexItem(
            item.Label,
            item.PageLabel,
            item.Line.Bounds,
            [item.Line.SemanticLine],
            item.Link,
            item.Children.Select(CreateDocumentIndexItem).ToArray());
    }

    private static IEnumerable<PdfSemanticElement> ExtractParagraphs(
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        List<LineCandidate> current = [];
        LineCandidate? previous = null;
        foreach (LineCandidate line in lines
            .OrderBy(static line => line.TableLaneIndex.HasValue ? 0 : 1)
            .ThenBy(static line => line.TableLaneIndex)
            .ThenBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            if (consumed.Contains(line.Index))
            {
                if (current.Count > 0)
                {
                    yield return CreateParagraph(current, consumed, options);
                    current.Clear();
                    previous = null;
                }

                continue;
            }

            if (!IsParagraphCandidate(line, bodyFontSize))
            {
                if (IsInlineArtifact(line, bodyFontSize))
                {
                    if (current.Count > 0 &&
                        (ShouldAttachFormulaArtifact(current, line, lineStep) ||
                            ShouldAttachInlineArtifact(current, line, lineStep) ||
                            ShouldAttachInlineMathContinuation(current, line, lineStep)))
                    {
                        current.Add(line);
                        previous = line;
                    }
                    else if (current.Count > 0)
                    {
                        // Detached tiny math fragments often belong to an upcoming display formula.
                        // Leave them out of the prose flow; formula rendering can recover them from runs.
                    }

                    continue;
                }

                if (current.Count > 0)
                {
                    yield return CreateParagraph(current, consumed, options);
                    current.Clear();
                    previous = null;
                }

                continue;
            }

            if (current.Count > 0 && TryParseListMarker(line, out _))
            {
                yield return CreateParagraph(current, consumed, options);
                current.Clear();
                previous = null;
            }

            bool currentFormulaBlock = current.Any(existing => IsDisplayFormulaLine(existing, bodyFontSize));
            bool lineFormulaBlock = IsDisplayFormulaLine(line, bodyFontSize) ||
                (currentFormulaBlock && IsDisplayFormulaContinuation(current, line, lineStep));
            if (current.Count > 0 && currentFormulaBlock != lineFormulaBlock)
            {
                List<LineCandidate> leadingFormulaAttachments = !currentFormulaBlock && lineFormulaBlock
                    ? DetachTrailingFormulaAttachments(current, line)
                    : [];
                if (current.Count > 0)
                {
                    yield return CreateParagraph(current, consumed, options);
                }

                current.Clear();
                current.AddRange(leadingFormulaAttachments);
                previous = null;
            }

            if (previous != null && ShouldStartParagraph(previous, line, lineStep, options))
            {
                yield return CreateParagraph(current, consumed, options);
                current.Clear();
            }

            current.Add(line);
            previous = line;
        }

        if (current.Count > 0)
        {
            yield return CreateParagraph(current, consumed, options);
        }
    }

    private static IEnumerable<PdfSemanticElement> ExtractFigureCaptions(
        IReadOnlyList<LineCandidate> lines,
        float lineStep,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        foreach (LineCandidate anchor in lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(line => FigureCaptionPattern.IsMatch(line.Text))
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            List<LineCandidate> captionLines = [anchor];
            LineCandidate previous = anchor;
            foreach (LineCandidate candidate in lines
                .Where(line => !consumed.Contains(line.Index) && line.Index != anchor.Index)
                .Where(line => line.Bounds.Y > previous.Bounds.Y)
                .OrderBy(static line => line.Bounds.Y)
                .ThenBy(static line => line.Bounds.X))
            {
                if (candidate.Bounds.Y - previous.Bounds.Y > lineStep * 1.55f)
                {
                    break;
                }

                if (MathF.Abs(candidate.Direction) > 0.01f ||
                    MathF.Abs(candidate.FontSize - anchor.FontSize) > 1.25f ||
                    MathF.Abs(candidate.Bounds.X - anchor.Bounds.X) > MathF.Max(18f, anchor.FontSize * 2.5f) ||
                    HorizontalGap(candidate.Bounds, anchor.Bounds) > MathF.Max(8f, anchor.FontSize))
                {
                    continue;
                }

                captionLines.Add(candidate);
                previous = candidate;
            }

            yield return CreateParagraph(captionLines, consumed, options);
        }
    }

    private static IEnumerable<PdfSemanticElement> ExtractNumberedDisplayFormulas(
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        LineCandidate[] numberedAnchors = lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(line => TryGetNumberedFormulaRow(line, out _))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        foreach (LineCandidate anchor in numberedAnchors)
        {
            if (consumed.Contains(anchor.Index) ||
                !TryGetNumberedFormulaRow(anchor, out PdfLayoutRectangle expressionBounds))
            {
                continue;
            }

            float verticalPadding = MathF.Max(18f, MathF.Max(bodyFontSize, anchor.FontSize) * 2.4f);
            LineCandidate[] formulaLines = lines
                .Where(line => !consumed.Contains(line.Index))
                .Where(line => ReferenceEquals(line, anchor) ||
                    IsFormulaRegionSourceLine(
                        line,
                        anchor,
                        expressionBounds,
                        verticalPadding,
                        numberedAnchors))
                .ToArray();
            yield return CreateParagraph(formulaLines, consumed, options);
        }
    }

    private static IEnumerable<PdfSemanticElement> ExtractUnnumberedDisplayFormulas(
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        foreach (LineCandidate anchor in lines
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            if (consumed.Contains(anchor.Index) ||
                !IsDisplayFormulaLine(anchor, bodyFontSize) ||
                IsFormulaLineEmbeddedInProse(anchor, lines))
            {
                continue;
            }

            float verticalPadding = MathF.Max(18f, MathF.Max(bodyFontSize, anchor.FontSize) * 2.4f);
            LineCandidate[] formulaLines = lines
                .Where(line => !consumed.Contains(line.Index))
                .Where(line => ReferenceEquals(line, anchor) ||
                    IsFormulaRegionSourceLine(line, anchor, anchor.Bounds, verticalPadding))
                .ToArray();
            yield return CreateParagraph(formulaLines, consumed, options);
        }
    }

    private static bool TryGetNumberedFormulaRow(
        LineCandidate line,
        out PdfLayoutRectangle expressionBounds)
    {
        expressionBounds = default;
        PdfTextGlyph[] glyphs = line.Source.Runs
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => glyph.IsPainted && !string.IsNullOrWhiteSpace(glyph.Text))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ThenBy(static glyph => glyph.Bounds.Y)
            .ToArray();
        for (int start = glyphs.Length - 1; start > 0; start--)
        {
            PdfTextGlyph[] numberGlyphs = glyphs[start..];
            string number = string.Concat(numberGlyphs.Select(static glyph => glyph.Text)).Trim();
            if (!IsEquationNumberText(number))
            {
                continue;
            }

            PdfTextGlyph[] expressionGlyphs = glyphs[..start];
            int equationNumberStart = line.Text.LastIndexOf("(", StringComparison.Ordinal);
            if (equationNumberStart <= 0)
            {
                continue;
            }

            string expression = line.Text[..equationNumberStart];
            float fontSize = numberGlyphs.Max(static glyph => glyph.FontSize);
            float numberBaseline = numberGlyphs.Average(static glyph => glyph.Bounds.Bottom);
            bool sharesExpressionBaseline = expressionGlyphs.Any(glyph =>
                MathF.Abs(glyph.Bounds.Bottom - numberBaseline) <= MathF.Max(1.2f, glyph.FontSize * 0.18f));
            bool hasFormulaFont = expressionGlyphs.Any(static glyph =>
            {
                string fontName = NormalizeFontName(glyph.FontName);
                return IsFormulaMathFont(fontName) ||
                    fontName.StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase) ||
                    fontName.Contains("ITAL", StringComparison.OrdinalIgnoreCase) ||
                    fontName.Contains("OBLIQUE", StringComparison.OrdinalIgnoreCase);
            });
            if (!sharesExpressionBaseline ||
                numberGlyphs[0].Bounds.X - expressionGlyphs.Max(static glyph => glyph.Bounds.Right) < fontSize * 1.4f ||
                !hasFormulaFont ||
                !HasFormulaOperator(expression))
            {
                continue;
            }

            expressionBounds = PdfLayoutRectangle.Union(expressionGlyphs.Select(static glyph => glyph.Bounds));
            return true;
        }

        return false;
    }

    private static bool IsFormulaRegionSourceLine(
        LineCandidate candidate,
        LineCandidate anchor,
        PdfLayoutRectangle regionBounds,
        float verticalPadding,
        IReadOnlyList<LineCandidate>? numberedAnchors = null)
    {
        if (!ReferenceEquals(candidate, anchor) &&
            (TryGetNumberedFormulaRow(candidate, out _) ||
                IsCloserToAnotherNumberedFormula(candidate, anchor, numberedAnchors)))
        {
            return false;
        }

        float verticalGap = MathF.Max(0f, MathF.Max(
            candidate.Bounds.Y - anchor.Bounds.Bottom,
            anchor.Bounds.Y - candidate.Bounds.Bottom));
        if (verticalGap > verticalPadding ||
            candidate.Text.Length > 220 ||
            CountWords(candidate.Text) > 8)
        {
            return false;
        }

        float horizontalPadding = MathF.Max(8f, anchor.FontSize * 1.25f);
        if (candidate.Bounds.Right < regionBounds.X - horizontalPadding ||
            candidate.Bounds.X > regionBounds.Right + horizontalPadding)
        {
            return false;
        }

        if (IsProseDominantFormulaRegionLine(candidate) &&
            !IsOptimizationFormulaClause(candidate))
        {
            return false;
        }

        if (HasFormulaOperator(candidate.Text))
        {
            return true;
        }

        return candidate.Text.Trim().Length <= 8 ||
            candidate.Source.Runs.Any(static run =>
                IsFormulaMathFont(run.FontName) ||
                NormalizeFontName(run.FontName).StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCloserToAnotherNumberedFormula(
        LineCandidate candidate,
        LineCandidate anchor,
        IReadOnlyList<LineCandidate>? numberedAnchors)
    {
        if (numberedAnchors == null || numberedAnchors.Count < 2)
        {
            return false;
        }

        float currentGap = VerticalGap(candidate.Bounds, anchor.Bounds);
        foreach (LineCandidate other in numberedAnchors)
        {
            if (ReferenceEquals(other, anchor))
            {
                continue;
            }

            float horizontalPadding = MathF.Max(8f, other.FontSize * 1.25f);
            float candidateCenter = candidate.CenterX;
            if (candidateCenter < other.Bounds.X - horizontalPadding ||
                candidateCenter > other.Bounds.Right + horizontalPadding)
            {
                continue;
            }

            if (VerticalGap(candidate.Bounds, other.Bounds) + 0.5f < currentGap)
            {
                return true;
            }
        }

        return false;
    }

    private static float VerticalGap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        return MathF.Max(0f, MathF.Max(
            first.Y - second.Bottom,
            second.Y - first.Bottom));
    }

    private static bool IsProseDominantFormulaRegionLine(LineCandidate candidate)
    {
        return IsProseDominantFormulaLine(candidate.Text, candidate.Source.Runs);
    }

    private static bool IsOptimizationFormulaClause(LineCandidate candidate)
    {
        bool hasFormulaFont = candidate.Source.Runs.Any(static run =>
            IsFormulaMathFont(run.FontName) ||
            NormalizeFontName(run.FontName).StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase));
        if (!hasFormulaFont ||
            !TryGetOptimizationExpression(candidate.Text, out string expression))
        {
            return false;
        }

        return !HasSentenceLikeOptimizationTail(expression);
    }

    private static bool TryGetOptimizationExpression(string text, out string expression)
    {
        string trimmed = text.TrimStart();
        if (TryConsumeLeadingWord(trimmed, "minimize", out expression) ||
            TryConsumeLeadingWord(trimmed, "maximize", out expression))
        {
            return true;
        }

        if (TryConsumeLeadingWord(trimmed, "subject", out string afterSubject) &&
            TryConsumeLeadingWord(afterSubject, "to", out expression))
        {
            return true;
        }

        expression = string.Empty;
        return false;
    }

    private static bool TryConsumeLeadingWord(
        string text,
        string expected,
        out string remainder)
    {
        if (!text.StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
            text.Length <= expected.Length ||
            !char.IsWhiteSpace(text[expected.Length]))
        {
            remainder = string.Empty;
            return false;
        }

        remainder = text[expected.Length..].TrimStart();
        return remainder.Length > 0;
    }

    private static bool HasSentenceLikeOptimizationTail(string expression)
    {
        int proseWords = 0;
        for (int index = 0; index < expression.Length;)
        {
            if (!char.IsLetter(expression[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < expression.Length && char.IsLetter(expression[index]))
            {
                index++;
            }

            int length = index - start;
            if (length <= 1)
            {
                continue;
            }

            char previous = start > 0 ? expression[start - 1] : '\0';
            char next = index < expression.Length ? expression[index] : '\0';
            if (IsFormulaWordBoundary(previous) || IsFormulaWordBoundary(next))
            {
                continue;
            }

            proseWords++;
            if (proseWords >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFormulaWordBoundary(char value)
    {
        return value is '=' or '<' or '>' or '≤' or '≥' or '∈' or '×' or '√' or
            '∑' or '∝' or '·' or '∗' or '*' or '/' or '^' or '_' or '(' or ')' or
            '[' or ']' or '{' or '}' or '|' or ',' or ';' or ':';
    }

    private static bool IsProseDominantFormulaLine(
        string text,
        IReadOnlyList<PdfTextRun> runs)
    {
        int totalLetters = runs.Sum(static run => run.Text.Count(char.IsLetter));
        int proseLetters = runs
            .Where(static run =>
            {
                string fontName = NormalizeFontName(run.FontName);
                return !IsFormulaMathFont(fontName) &&
                    !fontName.StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase) &&
                    !fontName.Contains("ITAL", StringComparison.OrdinalIgnoreCase) &&
                    !fontName.Contains("OBLIQUE", StringComparison.OrdinalIgnoreCase);
            })
            .Sum(static run => run.Text.Count(char.IsLetter));
        if (totalLetters == 0 || proseLetters * 3 < totalLetters * 2)
        {
            return false;
        }

        return proseLetters >= 8 || StartsWithProseWord(text);
    }

    private static bool StartsWithProseWord(string text)
    {
        string leadingWord = new(text
            .TrimStart()
            .TakeWhile(char.IsLetter)
            .ToArray());
        return leadingWord.Equals("a", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("an", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("the", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("if", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("for", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("use", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("using", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("where", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("when", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("then", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("thus", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("let", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("suppose", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("assume", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("because", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("since", StringComparison.OrdinalIgnoreCase) ||
            leadingWord.Equals("subject", StringComparison.OrdinalIgnoreCase);
    }

    private static int FormulaRegionWordCount(
        string text,
        IReadOnlyList<PdfTextRun> runs)
    {
        int lexicalRuns = runs.Count(static run =>
        {
            string fontName = NormalizeFontName(run.FontName);
            return run.Text.Count(char.IsLetter) >= 2 &&
                !IsFormulaMathFont(fontName) &&
                !fontName.StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase) &&
                !fontName.Contains("ITAL", StringComparison.OrdinalIgnoreCase) &&
                !fontName.Contains("OBLIQUE", StringComparison.OrdinalIgnoreCase);
        });
        return Math.Max(CountWords(text), lexicalRuns);
    }

    private static bool IsFormulaLineEmbeddedInProse(
        LineCandidate candidate,
        IReadOnlyList<LineCandidate> lines)
    {
        float candidateCenter = candidate.Bounds.X + candidate.Bounds.Width / 2f;
        return lines.Any(line =>
        {
            if (ReferenceEquals(line, candidate) ||
                FormulaRegionWordCount(line.Text, line.Source.Runs) < 8 ||
                line.Bounds.Width < MathF.Max(180f, candidate.Bounds.Width * 1.75f) ||
                candidateCenter < line.Bounds.X - 4f ||
                candidateCenter > line.Bounds.Right + 4f)
            {
                return false;
            }

            float verticalOverlap = MathF.Min(candidate.Bounds.Bottom, line.Bounds.Bottom) -
                MathF.Max(candidate.Bounds.Y, line.Bounds.Y);
            return verticalOverlap >= MathF.Min(candidate.Bounds.Height, line.Bounds.Height) * 0.30f;
        });
    }

    private static IEnumerable<PdfSemanticElement> ExtractLists(
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed)
    {
        LineCandidate[] readingLines = lines
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        int index = 0;
        while (index < readingLines.Length)
        {
            LineCandidate firstLine = readingLines[index];
            if (consumed.Contains(firstLine.Index) ||
                !IsParagraphCandidate(firstLine, bodyFontSize) ||
                !TryParseListMarker(firstLine, out ListMarkerCandidate firstMarker))
            {
                index++;
                continue;
            }

            List<RawListItem> rawItems = [];
            RawListItem? currentItem = null;
            LineCandidate? previousLine = null;
            int cursor = index;
            while (cursor < readingLines.Length)
            {
                LineCandidate line = readingLines[cursor];
                if (consumed.Contains(line.Index) || !IsParagraphCandidate(line, bodyFontSize))
                {
                    break;
                }

                float verticalStep = previousLine == null ? 0f : line.Bounds.Y - previousLine.Bounds.Y;
                if (previousLine != null && verticalStep > lineStep * 2.05f)
                {
                    break;
                }

                if (TryParseListMarker(line, out ListMarkerCandidate marker))
                {
                    if (line.Bounds.X < firstMarker.MarkerX - MathF.Max(8f, bodyFontSize) ||
                        line.Bounds.X > firstMarker.MarkerX + MathF.Max(120f, bodyFontSize * 12f) ||
                        MathF.Abs(line.FontSize - firstLine.FontSize) > 2f)
                    {
                        break;
                    }

                    currentItem = new RawListItem(marker, line);
                    rawItems.Add(currentItem);
                }
                else if (currentItem != null &&
                    IsListContinuationLine(currentItem, line, previousLine!, lineStep))
                {
                    currentItem.Lines.Add(line);
                }
                else
                {
                    break;
                }

                previousLine = line;
                cursor++;
            }

            if (!TryCreateSemanticList(rawItems, out PdfSemanticElement element))
            {
                index++;
                continue;
            }

            foreach (RawListItem item in rawItems)
            {
                foreach (LineCandidate line in item.Lines)
                {
                    consumed.Add(line.Index);
                }
            }

            yield return element;
            index = cursor;
        }
    }

    private static bool IsListContinuationLine(
        RawListItem item,
        LineCandidate line,
        LineCandidate previousLine,
        float lineStep)
    {
        float verticalStep = line.Bounds.Y - previousLine.Bounds.Y;
        float tolerance = MathF.Max(3f, item.Marker.Line.FontSize * 0.35f);
        return verticalStep > 0f &&
            verticalStep <= lineStep * 1.45f &&
            MathF.Abs(line.FontSize - item.Marker.Line.FontSize) <= 2f &&
            line.Bounds.X >= item.Marker.BodyX - tolerance &&
            line.Bounds.X > item.Marker.MarkerX + MathF.Max(4f, item.Marker.Line.FontSize * 0.35f) &&
            line.Bounds.X <= item.Marker.BodyX + MathF.Max(90f, item.Marker.Line.FontSize * 9f) &&
            !IsEquationNumberText(line.Text) &&
            !IsDisplayFormulaLine(line, item.Marker.Line.FontSize);
    }

    private static bool TryCreateSemanticList(
        IReadOnlyList<RawListItem> rawItems,
        out PdfSemanticElement element)
    {
        element = null!;
        if (rawItems.Count < 2 || !AssignListIndentationLevels(rawItems))
        {
            return false;
        }

        int index = 0;
        if (!TryBuildSemanticList(rawItems, ref index, 0, out PdfSemanticList list) || index != rawItems.Count)
        {
            return false;
        }

        PdfSemanticLine[] lines = rawItems
            .SelectMany(static item => item.Lines)
            .Select(static line => line.SemanticLine)
            .ToArray();
        element = new PdfSemanticElement(
            PdfSemanticElementKind.List,
            string.Join(Environment.NewLine, lines.Select(static line => line.Text)),
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            lines,
            semanticList: list);
        return true;
    }

    private static bool AssignListIndentationLevels(IReadOnlyList<RawListItem> items)
    {
        List<float> anchors = [];
        foreach (RawListItem item in items)
        {
            float tolerance = MathF.Max(7f, item.Marker.Line.FontSize * 0.7f);
            int anchorIndex = anchors.FindIndex(anchor => MathF.Abs(anchor - item.Marker.BodyX) <= tolerance);
            if (anchorIndex < 0)
            {
                anchors.Add(item.Marker.BodyX);
            }
            else
            {
                anchors[anchorIndex] = (anchors[anchorIndex] + item.Marker.BodyX) / 2f;
            }
        }

        anchors.Sort();
        if (anchors.Count > 8)
        {
            return false;
        }

        foreach (RawListItem item in items)
        {
            item.Level = anchors
                .Select((anchor, index) => new { Index = index, Distance = MathF.Abs(anchor - item.Marker.BodyX) })
                .OrderBy(static match => match.Distance)
                .First()
                .Index;
        }

        return items[0].Level == 0;
    }

    private static bool TryBuildSemanticList(
        IReadOnlyList<RawListItem> rawItems,
        ref int index,
        int level,
        out PdfSemanticList semanticList)
    {
        semanticList = null!;
        int rangeEnd = index;
        while (rangeEnd < rawItems.Count && rawItems[rangeEnd].Level >= level)
        {
            rangeEnd++;
        }

        RawListItem[] siblings = rawItems
            .Skip(index)
            .Take(rangeEnd - index)
            .Where(item => item.Level == level)
            .ToArray();
        if (!TryResolveListStyle(siblings, out ResolvedListStyle style))
        {
            return false;
        }

        List<PdfSemanticListItem> items = [];
        int siblingIndex = 0;
        while (index < rangeEnd)
        {
            RawListItem rawItem = rawItems[index];
            if (rawItem.Level != level)
            {
                return false;
            }

            index++;
            List<PdfSemanticList> nestedLists = [];
            while (index < rangeEnd && rawItems[index].Level > level)
            {
                int nestedLevel = rawItems[index].Level;
                if (nestedLevel != level + 1 ||
                    !TryBuildSemanticList(rawItems, ref index, nestedLevel, out PdfSemanticList nestedList))
                {
                    return false;
                }

                nestedLists.Add(nestedList);
            }

            int? value = null;
            if (style.Ordinals.Count > 0 && siblingIndex > 0)
            {
                int expected = style.Ordinals[siblingIndex - 1] + (style.IsReversed ? -1 : 1);
                if (style.Ordinals[siblingIndex] != expected)
                {
                    value = style.Ordinals[siblingIndex];
                }
            }

            PdfSemanticLine[] lines = rawItem.Lines.Select(static line => line.SemanticLine).ToArray();
            items.Add(new PdfSemanticListItem(
                ListItemText(rawItem),
                PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
                lines,
                rawItem.Marker.Marker,
                rawItem.Marker.MarkerLength,
                value,
                nestedLists));
            siblingIndex++;
        }

        int? start = null;
        if (style.Ordinals.Count > 0)
        {
            int defaultStart = style.IsReversed ? siblings.Length : 1;
            if (style.Ordinals[0] != defaultStart)
            {
                start = style.Ordinals[0];
            }
        }

        semanticList = new PdfSemanticList(style.Kind, style.MarkerKind, items, start, style.IsReversed);
        return true;
    }

    private static bool TryResolveListStyle(
        IReadOnlyList<RawListItem> siblings,
        out ResolvedListStyle style)
    {
        style = default;
        if (siblings.Count < 2)
        {
            return false;
        }

        ListMarkerCandidate[] markers = siblings.Select(static item => item.Marker).ToArray();
        if (markers.All(static marker => marker.Category == ListMarkerCategory.Bullet))
        {
            style = new ResolvedListStyle(
                PdfSemanticListKind.Unordered,
                PdfSemanticListMarkerKind.Bullet,
                [],
                false);
            return true;
        }

        if (markers.All(static marker => marker.Category == ListMarkerCategory.Hyphen))
        {
            bool hasHangingContinuation = siblings.Any(static item => item.Lines.Count > 1);
            float[] bodyOffsets = markers.Select(static marker => marker.BodyX - marker.MarkerX).ToArray();
            float offsetTolerance = MathF.Max(4f, markers[0].Line.FontSize * 0.4f);
            bool stableHangingIndent = bodyOffsets.All(offset =>
                    offset >= markers[0].Line.FontSize * 0.35f &&
                    offset <= markers[0].Line.FontSize * 3f) &&
                bodyOffsets.Max() - bodyOffsets.Min() <= offsetTolerance;
            if (!stableHangingIndent || siblings.Count < 3 && !hasHangingContinuation)
            {
                return false;
            }

            style = new ResolvedListStyle(
                PdfSemanticListKind.Unordered,
                PdfSemanticListMarkerKind.Hyphen,
                [],
                false);
            return true;
        }

        if (markers.Any(static marker => marker.Category is ListMarkerCategory.Bullet or ListMarkerCategory.Hyphen) ||
            markers.Select(static marker => marker.Shape).Distinct().Count() != 1)
        {
            return false;
        }

        if (markers.All(static marker => marker.DecimalValue.HasValue))
        {
            int[] values = markers.Select(static marker => marker.DecimalValue!.Value).ToArray();
            return TryCreateOrderedStyle(PdfSemanticListMarkerKind.Decimal, values, requireSequenceEvidence: false, out style);
        }

        bool sameCase = markers.All(marker => marker.IsUpperCase == markers[0].IsUpperCase);
        if (!sameCase)
        {
            return false;
        }

        bool romanCandidate = markers.All(static marker => marker.RomanValue.HasValue) &&
            markers.Any(static marker => marker.Token.Length > 1);
        if (romanCandidate)
        {
            int[] values = markers.Select(static marker => marker.RomanValue!.Value).ToArray();
            PdfSemanticListMarkerKind markerKind = markers[0].IsUpperCase
                ? PdfSemanticListMarkerKind.UpperRoman
                : PdfSemanticListMarkerKind.LowerRoman;
            return TryCreateOrderedStyle(markerKind, values, requireSequenceEvidence: true, out style);
        }

        if (markers.All(static marker => marker.AlphaValue.HasValue))
        {
            int[] values = markers.Select(static marker => marker.AlphaValue!.Value).ToArray();
            PdfSemanticListMarkerKind markerKind = markers[0].IsUpperCase
                ? PdfSemanticListMarkerKind.UpperAlpha
                : PdfSemanticListMarkerKind.LowerAlpha;
            return TryCreateOrderedStyle(markerKind, values, requireSequenceEvidence: false, out style);
        }

        return false;
    }

    private static bool TryCreateOrderedStyle(
        PdfSemanticListMarkerKind markerKind,
        IReadOnlyList<int> values,
        bool requireSequenceEvidence,
        out ResolvedListStyle style)
    {
        style = default;
        int[] differences = values.Pairwise(static (first, second) => second - first).ToArray();
        bool ascending = differences.All(static difference => difference > 0);
        bool descending = differences.All(static difference => difference < 0);
        if (!ascending && !descending ||
            requireSequenceEvidence && !differences.Any(static difference => Math.Abs(difference) == 1))
        {
            return false;
        }

        style = new ResolvedListStyle(
            PdfSemanticListKind.Ordered,
            markerKind,
            values.ToArray(),
            descending);
        return true;
    }

    private static string ListItemText(RawListItem item)
    {
        List<PdfSemanticLine> lines = item.Lines.Select(static line => line.SemanticLine).ToList();
        PdfSemanticLine first = lines[0];
        lines[0] = new PdfSemanticLine(
            first.Text[item.Marker.MarkerLength..].TrimStart(),
            first.Bounds,
            first.DominantFontName,
            first.DominantFontSize,
            first.Direction,
            first.Color,
            first.Runs);
        return JoinParagraphLines(lines);
    }

    private static bool TryParseListMarker(LineCandidate line, out ListMarkerCandidate marker)
    {
        marker = null!;
        if (MathF.Abs(line.Direction) > 0.01f || HasMathFont(line))
        {
            return false;
        }

        string text = line.Text;
        int start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start >= text.Length)
        {
            return false;
        }

        char first = text[start];
        if (IsBulletMarker(first) || first == '-')
        {
            int bodyStart = start + 1;
            if (bodyStart >= text.Length || !char.IsWhiteSpace(text[bodyStart]))
            {
                return false;
            }

            while (bodyStart < text.Length && char.IsWhiteSpace(text[bodyStart]))
            {
                bodyStart++;
            }

            if (bodyStart >= text.Length)
            {
                return false;
            }

            marker = new ListMarkerCandidate(
                line,
                first.ToString(),
                bodyStart,
                first == '-' ? ListMarkerCategory.Hyphen : ListMarkerCategory.Bullet,
                ListMarkerShape.Symbol,
                "",
                null,
                null,
                null,
                false,
                EstimateListBodyX(line, first.ToString(), bodyStart));
            return true;
        }

        int tokenStart = start;
        int tokenEnd;
        int markerEnd;
        ListMarkerShape shape;
        if (first == '(')
        {
            tokenStart++;
            tokenEnd = text.IndexOf(')', tokenStart);
            if (tokenEnd < 0)
            {
                return false;
            }

            markerEnd = tokenEnd + 1;
            shape = ListMarkerShape.Parenthesized;
        }
        else
        {
            tokenEnd = tokenStart;
            while (tokenEnd < text.Length && char.IsLetterOrDigit(text[tokenEnd]))
            {
                tokenEnd++;
            }

            if (tokenEnd == tokenStart || tokenEnd >= text.Length || text[tokenEnd] is not ('.' or ')'))
            {
                return false;
            }

            shape = text[tokenEnd] == '.' ? ListMarkerShape.Period : ListMarkerShape.ClosingParenthesis;
            markerEnd = tokenEnd + 1;
        }

        if (markerEnd >= text.Length || !char.IsWhiteSpace(text[markerEnd]))
        {
            return false;
        }

        int bodyIndex = markerEnd;
        while (bodyIndex < text.Length && char.IsWhiteSpace(text[bodyIndex]))
        {
            bodyIndex++;
        }

        if (bodyIndex >= text.Length)
        {
            return false;
        }

        if (DateListItemBodyPattern.IsMatch(text[bodyIndex..]))
        {
            return false;
        }

        string token = text[tokenStart..tokenEnd];
        int? decimalValue = null;
        int? alphaValue = null;
        int? romanValue = null;
        if (token.Length <= 3 && token.All(char.IsDigit) &&
            int.TryParse(token, out int parsedDecimal) && parsedDecimal > 0)
        {
            decimalValue = parsedDecimal;
        }
        else if (token.Length == 1 && token[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            alphaValue = char.ToUpperInvariant(token[0]) - 'A' + 1;
        }

        if (token.All(static character => "IVXLCDMivxlcdm".Contains(character, StringComparison.Ordinal)) &&
            TryParseRomanNumeral(token, out int parsedRoman))
        {
            romanValue = parsedRoman;
        }

        if (!decimalValue.HasValue && !alphaValue.HasValue && !romanValue.HasValue)
        {
            return false;
        }

        string markerText = text[start..markerEnd];
        marker = new ListMarkerCandidate(
            line,
            markerText,
            bodyIndex,
            ListMarkerCategory.Ordered,
            shape,
            token,
            decimalValue,
            alphaValue,
            romanValue,
            token.All(char.IsUpper),
            EstimateListBodyX(line, markerText, bodyIndex));
        return true;
    }

    private static float EstimateListBodyX(LineCandidate line, string marker, int bodyIndex)
    {
        PdfTextGlyph[] glyphs = line.Source.Runs
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => glyph.Text.Length > 0)
            .OrderBy(static glyph => glyph.Bounds.X)
            .ToArray();
        string compactMarker = new(marker.Where(static character => !char.IsWhiteSpace(character)).ToArray());
        int matchedMarkerCharacters = 0;
        foreach (PdfTextGlyph glyph in glyphs)
        {
            for (int characterIndex = 0; characterIndex < glyph.Text.Length; characterIndex++)
            {
                char character = glyph.Text[characterIndex];
                if (matchedMarkerCharacters < compactMarker.Length)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        continue;
                    }

                    if (character != compactMarker[matchedMarkerCharacters])
                    {
                        return EstimateListBodyXFromText(line, bodyIndex);
                    }

                    matchedMarkerCharacters++;
                    continue;
                }

                if (!char.IsWhiteSpace(character))
                {
                    float fraction = characterIndex / (float)glyph.Text.Length;
                    return glyph.Bounds.X + glyph.Bounds.Width * fraction;
                }
            }
        }

        return EstimateListBodyXFromText(line, bodyIndex);
    }

    private static float EstimateListBodyXFromText(LineCandidate line, int bodyIndex)
    {
        float textFraction = line.Text.Length == 0 ? 0f : bodyIndex / (float)line.Text.Length;
        return line.Bounds.X + MathF.Min(line.Bounds.Width * 0.3f, line.Bounds.Width * textFraction);
    }

    private static bool TryParseRomanNumeral(string token, out int value)
    {
        value = 0;
        string upper = token.ToUpperInvariant();
        Dictionary<char, int> values = new()
        {
            ['I'] = 1,
            ['V'] = 5,
            ['X'] = 10,
            ['L'] = 50,
            ['C'] = 100,
            ['D'] = 500,
            ['M'] = 1000
        };
        for (int index = 0; index < upper.Length; index++)
        {
            int current = values[upper[index]];
            int next = index + 1 < upper.Length ? values[upper[index + 1]] : 0;
            value += current < next ? -current : current;
        }

        return value > 0 && string.Equals(ToRomanNumeral(value), upper, StringComparison.Ordinal);
    }

    private static string ToRomanNumeral(int value)
    {
        (int Value, string Text)[] numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];
        StringBuilder result = new();
        foreach ((int numeralValue, string numeralText) in numerals)
        {
            while (value >= numeralValue)
            {
                result.Append(numeralText);
                value -= numeralValue;
            }
        }

        return result.ToString();
    }

    private static bool IsBulletMarker(char character)
    {
        return character is '\u0095' or '\u2022' or '\u2023' or '\u2043' or '\u25e6' or '\u25aa' or '\u2219';
    }

    private static RuledTableRegion[] DetectRuledTableRegions(PdfLayoutPage page)
    {
        TableRule[] rules = TableRules(page, new PdfLayoutRectangle(0, 0, page.Width, page.Height)).ToArray();
        PdfLayoutRectangle[] horizontalSpans = MergeHorizontalRuleSegments(
            rules.Where(static rule => rule.Orientation == TableRuleOrientation.Horizontal)
                .Select(static rule => rule.Bounds));
        List<List<PdfLayoutRectangle>> spanFamilies = [];
        float edgeTolerance = MathF.Max(2f, page.Width * 0.005f);
        foreach (PdfLayoutRectangle span in horizontalSpans
            .OrderBy(static span => span.X)
            .ThenBy(static span => span.Right)
            .ThenBy(static span => span.Y))
        {
            List<PdfLayoutRectangle>? family = spanFamilies.FirstOrDefault(existing =>
                MathF.Abs(existing.Average(static item => item.X) - span.X) <= edgeTolerance &&
                MathF.Abs(existing.Average(static item => item.Right) - span.Right) <= edgeTolerance);
            if (family == null)
            {
                spanFamilies.Add([span]);
            }
            else
            {
                family.Add(span);
            }
        }

        List<RuledTableRegion> regions = [];
        float maximumVerticalGap = page.Height * 0.30f;
        foreach (List<PdfLayoutRectangle> family in spanFamilies)
        {
            List<PdfLayoutRectangle> sequence = [];
            foreach (PdfLayoutRectangle span in family.OrderBy(static span => span.Y))
            {
                if (sequence.Count > 0 && span.Y - sequence[^1].Y > maximumVerticalGap)
                {
                    AddRuledTableRegion(page, rules, sequence, regions);
                    sequence.Clear();
                }

                sequence.Add(span);
            }

            AddRuledTableRegion(page, rules, sequence, regions);
        }

        return regions
            .Where(candidate => !regions.Any(other =>
                !ReferenceEquals(candidate, other) &&
                ContainsRectangle(other.Bounds, candidate.Bounds) &&
                other.Bounds.Width * other.Bounds.Height < candidate.Bounds.Width * candidate.Bounds.Height))
            .OrderBy(static region => region.Bounds.Y)
            .ThenBy(static region => region.Bounds.X)
            .ToArray();
    }

    private static HorizontalTableLane[] DetectHorizontalTableLanes(
        PdfLayoutPage page,
        IReadOnlyList<RuledTableRegion> ruledTableRegions)
    {
        TableRule[] rules = TableRules(page, new PdfLayoutRectangle(0, 0, page.Width, page.Height)).ToArray();
        List<(PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)> candidates = [];
        foreach (PdfLayoutRectangle[] sequence in HorizontalRuleSequences(page, rules))
        {
            if (sequence.Length < 3)
            {
                continue;
            }

            float left = sequence.Min(static span => span.X);
            float right = sequence.Max(static span => span.Right);
            float top = sequence.Min(static span => span.Y);
            float bottom = sequence.Max(static span => span.Bottom);
            float width = right - left;
            float height = bottom - top;
            float pageMidpoint = page.Width / 2f;
            bool isColumnLocal = right <= pageMidpoint || left >= pageMidpoint;
            if (width < page.Width * 0.18f ||
                width > page.Width * 0.45f ||
                !isColumnLocal ||
                height < 16f ||
                height > page.Height * 0.55f)
            {
                continue;
            }

            // Narrow-column captions can wrap to many short lines. Keep enough
            // vertical context to retain the full caption in its table lane;
            // neighboring tables still clip this expansion at their midpoint.
            float captionPadding = MathF.Min(108f, page.Height * 0.14f);
            PdfLayoutRectangle ruleBounds = new(left, top, width, height);
            if (!HasNearbyTableCaption(page, ruleBounds, captionPadding))
            {
                continue;
            }

            float horizontalPadding = MathF.Min(42f, page.Width * 0.07f);
            float laneTop = MathF.Max(0f, top - captionPadding);
            float laneLeft = MathF.Max(0f, left - horizontalPadding);
            float laneRight = MathF.Min(page.Width, right + horizontalPadding);
            float midpointTolerance = page.Width * 0.02f;
            if (right <= pageMidpoint + midpointTolerance)
            {
                laneRight = MathF.Min(laneRight, pageMidpoint);
            }
            else if (left >= pageMidpoint - midpointTolerance)
            {
                laneLeft = MathF.Max(laneLeft, pageMidpoint);
            }

            PdfLayoutRectangle lane = new(
                laneLeft,
                laneTop,
                laneRight - laneLeft,
                MathF.Min(page.Height, bottom + captionPadding) - laneTop);
            if (ruledTableRegions.Any(region =>
                HorizontalOverlap(region.Bounds, lane) >= MathF.Min(region.Bounds.Width, lane.Width) * 0.8f &&
                VerticalOverlap(region.Bounds, lane) >= MathF.Min(region.Bounds.Height, lane.Height) * 0.5f))
            {
                continue;
            }

            candidates.Add((ruleBounds, lane));
        }

        return candidates
            .Select(candidate => new HorizontalTableLane(
                candidate.RuleBounds,
                ClipTableLaneBetweenNeighbors(candidate, candidates)))
            .Distinct()
            .OrderBy(static lane => lane.ExpandedBounds.Y)
            .ThenBy(static lane => lane.ExpandedBounds.X)
            .ToArray();
    }

    private static bool HasNearbyTableCaption(
        PdfLayoutPage page,
        PdfLayoutRectangle tableBounds,
        float verticalPadding)
    {
        PdfLayoutRectangle searchBounds = new(
            tableBounds.X,
            MathF.Max(0f, tableBounds.Y - verticalPadding),
            tableBounds.Width,
            MathF.Min(page.Height, tableBounds.Bottom + verticalPadding) -
                MathF.Max(0f, tableBounds.Y - verticalPadding));
        return page.Lines
            .Where(line => line.Bounds.Bottom >= searchBounds.Y && line.Bounds.Y <= searchBounds.Bottom)
            .Where(line => HorizontalOverlap(line.Bounds, searchBounds) >=
                MathF.Min(line.Bounds.Width, searchBounds.Width) * 0.30f)
            .Any(line =>
                TableCaptionPattern.IsMatch(line.Text.TrimStart()) ||
                line.Runs.Any(run => TableCaptionPattern.IsMatch(run.Text.TrimStart())));
    }

    private static PdfLayoutRectangle ClipTableLaneBetweenNeighbors(
        (PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds) candidate,
        IReadOnlyList<(PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)> candidates)
    {
        (PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)[] sameColumn = candidates
            .Where(other => !other.Equals(candidate) &&
                HorizontalOverlap(other.RuleBounds, candidate.RuleBounds) >=
                    MathF.Min(other.RuleBounds.Width, candidate.RuleBounds.Width) * 0.8f)
            .ToArray();
        float top = candidate.ExpandedBounds.Y;
        float bottom = candidate.ExpandedBounds.Bottom;
        float left = candidate.ExpandedBounds.X;
        float right = candidate.ExpandedBounds.Right;
        (PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)? previous = sameColumn
            .Where(other => other.RuleBounds.Bottom <= candidate.RuleBounds.Y)
            .OrderByDescending(static other => other.RuleBounds.Bottom)
            .Cast<(PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)?>()
            .FirstOrDefault();
        if (previous.HasValue)
        {
            top = MathF.Max(top, (previous.Value.RuleBounds.Bottom + candidate.RuleBounds.Y) / 2f);
        }

        (PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)? next = sameColumn
            .Where(other => other.RuleBounds.Y >= candidate.RuleBounds.Bottom)
            .OrderBy(static other => other.RuleBounds.Y)
            .Cast<(PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)?>()
            .FirstOrDefault();
        if (next.HasValue)
        {
            bottom = MathF.Min(bottom, (candidate.RuleBounds.Bottom + next.Value.RuleBounds.Y) / 2f);
        }

        (PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)? leftNeighbor = candidates
            .Where(other => !other.Equals(candidate) &&
                other.RuleBounds.Right <= candidate.RuleBounds.X &&
                VerticalOverlap(other.RuleBounds, candidate.RuleBounds) >=
                    MathF.Min(other.RuleBounds.Height, candidate.RuleBounds.Height) * 0.25f)
            .OrderByDescending(static other => other.RuleBounds.Right)
            .Cast<(PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)?>()
            .FirstOrDefault();
        if (leftNeighbor.HasValue)
        {
            left = MathF.Max(left, (leftNeighbor.Value.RuleBounds.Right + candidate.RuleBounds.X) / 2f);
        }

        (PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)? rightNeighbor = candidates
            .Where(other => !other.Equals(candidate) &&
                other.RuleBounds.X >= candidate.RuleBounds.Right &&
                VerticalOverlap(other.RuleBounds, candidate.RuleBounds) >=
                    MathF.Min(other.RuleBounds.Height, candidate.RuleBounds.Height) * 0.25f)
            .OrderBy(static other => other.RuleBounds.X)
            .Cast<(PdfLayoutRectangle RuleBounds, PdfLayoutRectangle ExpandedBounds)?>()
            .FirstOrDefault();
        if (rightNeighbor.HasValue)
        {
            right = MathF.Min(right, (candidate.RuleBounds.Right + rightNeighbor.Value.RuleBounds.X) / 2f);
        }

        return new PdfLayoutRectangle(
            left,
            top,
            MathF.Max(0f, right - left),
            MathF.Max(0f, bottom - top));
    }

    private static PdfLayoutRectangle[][] HorizontalRuleSequences(
        PdfLayoutPage page,
        IReadOnlyList<TableRule> rules)
    {
        PdfLayoutRectangle[] horizontalSpans = MergeHorizontalRuleSegments(
            rules.Where(static rule => rule.Orientation == TableRuleOrientation.Horizontal)
                .Select(static rule => rule.Bounds));
        List<List<PdfLayoutRectangle>> spanFamilies = [];
        float edgeTolerance = MathF.Max(2f, page.Width * 0.005f);
        foreach (PdfLayoutRectangle span in horizontalSpans
            .OrderBy(static span => span.X)
            .ThenBy(static span => span.Right)
            .ThenBy(static span => span.Y))
        {
            List<PdfLayoutRectangle>? family = spanFamilies.FirstOrDefault(existing =>
                MathF.Abs(existing.Average(static item => item.X) - span.X) <= edgeTolerance &&
                MathF.Abs(existing.Average(static item => item.Right) - span.Right) <= edgeTolerance);
            if (family == null)
            {
                spanFamilies.Add([span]);
            }
            else
            {
                family.Add(span);
            }
        }

        List<PdfLayoutRectangle[]> sequences = [];
        float maximumVerticalGap = page.Height * 0.30f;
        foreach (List<PdfLayoutRectangle> family in spanFamilies)
        {
            List<PdfLayoutRectangle> sequence = [];
            foreach (PdfLayoutRectangle span in family.OrderBy(static span => span.Y))
            {
                if (sequence.Count > 0 && span.Y - sequence[^1].Y > maximumVerticalGap)
                {
                    sequences.Add(sequence.ToArray());
                    sequence.Clear();
                }

                sequence.Add(span);
            }

            if (sequence.Count > 0)
            {
                sequences.Add(sequence.ToArray());
            }
        }

        return sequences.ToArray();
    }

    private static PdfLayoutRectangle[] DetectHorizontalRuleTableRegions(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        PdfSemanticExtractionOptions options)
    {
        TableRule[] rules = TableRules(
            page,
            new PdfLayoutRectangle(0, 0, page.Width, page.Height)).ToArray();
        TableSourceRow[] sourceRows = BuildTableSourceRows(
            lines,
            bodyFontSize,
            [],
            options).ToArray();
        return HorizontalRuleSequences(page, rules)
            .Where(static sequence => sequence.Length >= 3)
            .Select(static sequence =>
            {
                float left = sequence.Min(static rule => rule.X);
                float right = sequence.Max(static rule => rule.Right);
                float top = sequence.Min(static rule => rule.Y);
                float bottom = sequence.Max(static rule => rule.Bottom);
                return new PdfLayoutRectangle(left, top, right - left, bottom - top);
            })
            .Where(region =>
                region.Width >= page.Width * 0.60f &&
                region.Width <= page.Width * 0.90f &&
                region.Height >= 16f &&
                region.Height <= page.Height * 0.55f)
            .Where(region => HasAlignedTableRows(region, sourceRows, page))
            .Distinct()
            .ToArray();
    }

    private static bool HasAlignedTableRows(
        PdfLayoutRectangle region,
        IReadOnlyList<TableSourceRow> sourceRows,
        PdfLayoutPage page)
    {
        TableSourceRow[] rows = sourceRows
            .Where(row => row.Cells.Count >= 3 && row.Cells.Count <= MaximumDetectedTableColumnCount)
            .Where(row => IsInsideHorizontalRuleTable(row.Bounds, [region]))
            .OrderBy(static row => row.Bounds.Y)
            .ThenBy(static row => row.Bounds.X)
            .ToArray();
        if (rows.Length < 3)
        {
            return false;
        }

        float[] anchors = TableColumnAnchors(rows);
        return rows.Count(row => IsCompatibleWithTableColumns(row, anchors, page)) >= 3;
    }

    private static PdfLayoutRectangle[] MergeHorizontalRuleSegments(IEnumerable<PdfLayoutRectangle> source)
    {
        List<List<PdfLayoutRectangle>> baselines = [];
        foreach (PdfLayoutRectangle segment in source.OrderBy(static item => item.Y).ThenBy(static item => item.X))
        {
            float centerY = segment.Y + segment.Height / 2f;
            List<PdfLayoutRectangle>? baseline = baselines.FirstOrDefault(existing =>
                MathF.Abs(existing.Average(static item => item.Y + item.Height / 2f) - centerY) <= 1f);
            if (baseline == null)
            {
                baselines.Add([segment]);
            }
            else
            {
                baseline.Add(segment);
            }
        }

        List<PdfLayoutRectangle> merged = [];
        foreach (List<PdfLayoutRectangle> baseline in baselines)
        {
            PdfLayoutRectangle[] ordered = baseline.OrderBy(static item => item.X).ToArray();
            float left = ordered[0].X;
            float right = ordered[0].Right;
            float y = ordered.Average(static item => item.Y + item.Height / 2f);
            foreach (PdfLayoutRectangle segment in ordered.Skip(1))
            {
                if (segment.X <= right + 2f)
                {
                    right = MathF.Max(right, segment.Right);
                    continue;
                }

                merged.Add(new PdfLayoutRectangle(left, y, right - left, 0));
                left = segment.X;
                right = segment.Right;
            }

            merged.Add(new PdfLayoutRectangle(left, y, right - left, 0));
        }

        return merged.ToArray();
    }

    private static void AddRuledTableRegion(
        PdfLayoutPage page,
        IReadOnlyList<TableRule> rules,
        IReadOnlyList<PdfLayoutRectangle> horizontalSpans,
        ICollection<RuledTableRegion> regions)
    {
        if (horizontalSpans.Count < 3)
        {
            return;
        }

        float left = horizontalSpans.Min(static span => span.X);
        float right = horizontalSpans.Max(static span => span.Right);
        float[] rowBoundaries = horizontalSpans
            .Select(static span => span.Y + span.Height / 2f)
            .Order()
            .Aggregate(new List<float>(), static (values, value) =>
            {
                if (values.Count == 0 || value - values[^1] > 1f)
                {
                    values.Add(value);
                }

                return values;
            })
            .ToArray();
        if (rowBoundaries.Length < 3)
        {
            return;
        }

        float width = right - left;
        float height = rowBoundaries[^1] - rowBoundaries[0];
        if (width < page.Width * 0.18f ||
            width > page.Width * 0.62f ||
            height < 16f ||
            height > page.Height * 0.55f)
        {
            return;
        }

        PdfLayoutRectangle bounds = new(left, rowBoundaries[0], width, height);
        TableRule[] verticalRules = rules
            .Where(static rule => rule.Orientation == TableRuleOrientation.Vertical)
            .Where(rule => rule.Bounds.X + rule.Bounds.Width / 2f >= left - 2f &&
                rule.Bounds.X + rule.Bounds.Width / 2f <= right + 2f &&
                VerticalOverlap(rule.Bounds, bounds) > 0f)
            .ToArray();
        List<List<PdfLayoutRectangle>> verticalFamilies = [];
        foreach (PdfLayoutRectangle vertical in verticalRules
            .Select(static rule => rule.Bounds)
            .OrderBy(static rule => rule.X))
        {
            float centerX = vertical.X + vertical.Width / 2f;
            List<PdfLayoutRectangle>? family = verticalFamilies.FirstOrDefault(existing =>
                MathF.Abs(existing.Average(static item => item.X + item.Width / 2f) - centerX) <= 1f);
            if (family == null)
            {
                verticalFamilies.Add([vertical]);
            }
            else
            {
                family.Add(vertical);
            }
        }

        float edgeInset = MathF.Max(3f, width * 0.03f);
        float[] internalDividers = verticalFamilies
            .Where(family => CoveredVerticalLength(family, bounds) >= height * 0.55f)
            .Select(static family => family.Average(static rule => rule.X + rule.Width / 2f))
            .Where(x => x > left + edgeInset && x < right - edgeInset)
            .Order()
            .ToArray();
        // The text-row detector already handles tables with three or more columns well.
        // This path is for compact two-column ruled regions that cannot seed that detector.
        if (internalDividers.Length != 1)
        {
            return;
        }

        float[] columnBoundaries = [left, .. internalDividers, right];
        PdfTextRun[] regionRuns = page.Lines
            .SelectMany(static line => line.Runs)
            .Where(run => IsInsideRectangleCenter(run.Bounds, bounds, 1f))
            .ToArray();
        if (regionRuns.Length < 4 ||
            Enumerable.Range(0, columnBoundaries.Length - 1).Any(columnIndex =>
                !regionRuns.Any(run => IsInsideHorizontalInterval(
                    run.Bounds,
                    columnBoundaries[columnIndex],
                    columnBoundaries[columnIndex + 1]))))
        {
            return;
        }

        TableRule[] regionRules = rules
            .Where(rule => Intersects(ExpandRectangle(bounds, 2f, 2f), rule.Bounds))
            .ToArray();
        regions.Add(new RuledTableRegion(bounds, rowBoundaries, columnBoundaries, regionRules));
    }

    private static float CoveredVerticalLength(
        IReadOnlyList<PdfLayoutRectangle> segments,
        PdfLayoutRectangle bounds)
    {
        (float Top, float Bottom)[] intervals = segments
            .Select(segment => (
                Top: MathF.Max(bounds.Y, segment.Y),
                Bottom: MathF.Min(bounds.Bottom, segment.Bottom)))
            .Where(static interval => interval.Bottom > interval.Top)
            .OrderBy(static interval => interval.Top)
            .ToArray();
        if (intervals.Length == 0)
        {
            return 0f;
        }

        float covered = 0f;
        float top = intervals[0].Top;
        float bottom = intervals[0].Bottom;
        foreach ((float nextTop, float nextBottom) in intervals.Skip(1))
        {
            if (nextTop <= bottom + 1f)
            {
                bottom = MathF.Max(bottom, nextBottom);
                continue;
            }

            covered += bottom - top;
            top = nextTop;
            bottom = nextBottom;
        }

        return covered + bottom - top;
    }

    private static bool IsInsideRuledTableRegion(
        LineCandidate line,
        IReadOnlyList<RuledTableRegion> regions)
    {
        return regions.Any(region => IsInsideRuledTableRegion(line.Bounds, region));
    }

    private static bool IsInsideRuledTableRegion(PdfLayoutRectangle bounds, RuledTableRegion region)
    {
        return IsInsideRectangleCenter(bounds, region.Bounds, 1f);
    }

    private static bool IsInsideTableLaneRegion(
        LineCandidate line,
        IReadOnlyList<PdfLayoutRectangle> tableLaneRegions)
    {
        return tableLaneRegions.Any(region => IsInsideRectangleCenter(line.Bounds, region, 1f));
    }

    private static bool IsInsideRectangleCenter(
        PdfLayoutRectangle value,
        PdfLayoutRectangle container,
        float tolerance)
    {
        float centerX = value.X + value.Width / 2f;
        float centerY = value.Y + value.Height / 2f;
        return centerX >= container.X - tolerance &&
            centerX <= container.Right + tolerance &&
            centerY >= container.Y - tolerance &&
            centerY <= container.Bottom + tolerance;
    }

    private static bool IsInsideHorizontalInterval(PdfLayoutRectangle bounds, float left, float right)
    {
        float centerX = bounds.X + bounds.Width / 2f;
        return centerX >= left - 1f && centerX <= right + 1f;
    }

    private static bool ContainsRectangle(PdfLayoutRectangle container, PdfLayoutRectangle value)
    {
        const float tolerance = 1f;
        return value.X >= container.X - tolerance &&
            value.Y >= container.Y - tolerance &&
            value.Right <= container.Right + tolerance &&
            value.Bottom <= container.Bottom + tolerance;
    }

    private static IEnumerable<PdfSemanticElement> ExtractRuledTables(
        IReadOnlyList<RuledTableRegion> regions,
        IReadOnlyList<LineCandidate> lines,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        foreach (RuledTableRegion region in regions)
        {
            LineCandidate[] regionLines = lines
                .Where(line => !consumed.Contains(line.Index))
                .Where(line => IsInsideRuledTableRegion(line.Bounds, region))
                .OrderBy(static line => line.Bounds.Y)
                .ThenBy(static line => line.Bounds.X)
                .ToArray();
            PdfSemanticElement? table = CreateRuledTableElement(region, regionLines, options);
            if (table == null)
            {
                continue;
            }

            foreach (LineCandidate line in regionLines)
            {
                consumed.Add(line.Index);
            }

            yield return table;
        }
    }

    private static PdfSemanticElement? CreateRuledTableElement(
        RuledTableRegion region,
        IReadOnlyList<LineCandidate> lines,
        PdfSemanticExtractionOptions options)
    {
        List<PdfSemanticTableRow> rows = [];
        for (int bandIndex = 0; bandIndex + 1 < region.RowBoundaries.Count; bandIndex++)
        {
            float top = region.RowBoundaries[bandIndex];
            float bottom = region.RowBoundaries[bandIndex + 1];
            LineCandidate[] bandLines = lines
                .Where(line => line.Bounds.Y + line.Bounds.Height / 2f >= top - 1f &&
                    line.Bounds.Y + line.Bounds.Height / 2f <= bottom + 1f)
                .OrderBy(static line => line.Bounds.Y)
                .ThenBy(static line => line.Bounds.X)
                .ToArray();
            if (bandLines.Length == 0)
            {
                continue;
            }

            List<List<LineCandidate>> logicalRows = SplitRuledTableBandRows(bandLines, region, options);
            for (int logicalRowIndex = 0; logicalRowIndex < logicalRows.Count; logicalRowIndex++)
            {
                PdfSemanticTableCell[] cells = Enumerable
                    .Range(0, region.ColumnBoundaries.Count - 1)
                    .Select(columnIndex => CreateRuledTableCell(
                        logicalRows[logicalRowIndex],
                        region,
                        columnIndex,
                        top,
                        bottom,
                        borderTop: bandIndex == 0 && logicalRowIndex == 0,
                        borderBottom: logicalRowIndex == logicalRows.Count - 1,
                        options))
                    .ToArray();
                if (cells.All(static cell => string.IsNullOrWhiteSpace(cell.Text)))
                {
                    continue;
                }

                bool isHeader = rows.Count == 0 && IsRuledTableHeaderRow(cells);
                rows.Add(new PdfSemanticTableRow(cells, isHeader));
            }
        }

        if (rows.Count < 2 ||
            Enumerable.Range(0, region.ColumnBoundaries.Count - 1).Any(columnIndex =>
                rows.All(row => string.IsNullOrWhiteSpace(row.Cells[columnIndex].Text))))
        {
            return null;
        }

        PdfSemanticLine[] semanticLines = rows
            .SelectMany(static row => row.Cells)
            .SelectMany(static cell => cell.Lines)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        string text = string.Join(
            Environment.NewLine,
            rows.Select(static row => string.Join('\t', row.Cells.Select(static cell => cell.Text))));
        return new PdfSemanticElement(
            PdfSemanticElementKind.Table,
            text,
            region.Bounds,
            semanticLines,
            tableRows: rows);
    }

    private static List<List<LineCandidate>> SplitRuledTableBandRows(
        IReadOnlyList<LineCandidate> lines,
        RuledTableRegion region,
        PdfSemanticExtractionOptions options)
    {
        List<List<LineCandidate>> rows = [];
        foreach (LineCandidate line in lines)
        {
            PdfTextRun[] firstColumnRuns = line.Source.Runs
                .Where(run => IsInsideHorizontalInterval(
                    run.Bounds,
                    region.ColumnBoundaries[0],
                    region.ColumnBoundaries[1]))
                .ToArray();
            string firstColumnText = ReconstructText(
                firstColumnRuns.SelectMany(static run => run.Glyphs),
                options).TrimStart();
            if (rows.Count == 0)
            {
                rows.Add([line]);
                continue;
            }

            if (!RuledTableRowMarkerPattern.IsMatch(firstColumnText) || rows[^1].Count == 0)
            {
                rows[^1].Add(line);
                continue;
            }

            List<LineCandidate> raisedAnnotations = [];
            while (rows[^1].Count > 0 && IsRaisedAnnotationForNextRuledTableRow(
                rows[^1][^1],
                line,
                region,
                options))
            {
                raisedAnnotations.Insert(0, rows[^1][^1]);
                rows[^1].RemoveAt(rows[^1].Count - 1);
            }

            if (raisedAnnotations.Count == 0)
            {
                rows.Add([line]);
                continue;
            }

            PdfTextRun[] mergedRuns = line.Source.Runs
                .Concat(raisedAnnotations.SelectMany(static annotation => annotation.Source.Runs))
                .ToArray();
            rows.Add([CreateLineCandidate(line.Index, CreateTextLine(mergedRuns), options)]);
        }

        return rows;
    }

    private static bool IsRaisedAnnotationForNextRuledTableRow(
        LineCandidate candidate,
        LineCandidate rowStart,
        RuledTableRegion region,
        PdfSemanticExtractionOptions options)
    {
        bool hasFirstColumnContent = candidate.Source.Runs.Any(run => IsInsideHorizontalInterval(
            run.Bounds,
            region.ColumnBoundaries[0],
            region.ColumnBoundaries[1]));
        if (hasFirstColumnContent || candidate.FontSize >= rowStart.FontSize * 0.85f)
        {
            return false;
        }

        string text = ReconstructText(
            candidate.Source.Runs.SelectMany(static run => run.Glyphs),
            options).Trim();
        if (!NumericFootnoteMarkerPattern.IsMatch(text) && !SymbolFootnoteMarkerPattern.IsMatch(text))
        {
            return false;
        }

        float candidateCenter = candidate.Bounds.Y + candidate.Bounds.Height / 2f;
        float rowCenter = rowStart.Bounds.Y + rowStart.Bounds.Height / 2f;
        return MathF.Abs(candidateCenter - rowCenter) <= MathF.Max(4f, rowStart.FontSize * 0.65f);
    }

    private static PdfSemanticTableCell CreateRuledTableCell(
        IReadOnlyList<LineCandidate> sourceLines,
        RuledTableRegion region,
        int columnIndex,
        float rowTop,
        float rowBottom,
        bool borderTop,
        bool borderBottom,
        PdfSemanticExtractionOptions options)
    {
        float left = region.ColumnBoundaries[columnIndex];
        float right = region.ColumnBoundaries[columnIndex + 1];
        PdfSemanticLine[] lines = sourceLines
            .Select(sourceLine =>
            {
                PdfTextRun[] runs = sourceLine.Source.Runs
                    .Where(run => IsInsideHorizontalInterval(run.Bounds, left, right))
                    .OrderBy(static run => run.Bounds.X)
                    .ThenBy(static run => run.Bounds.Y)
                    .ToArray();
                if (runs.Length == 0)
                {
                    return null;
                }

                string text = ReconstructText(runs.SelectMany(static run => run.Glyphs), options);
                return CreateSyntheticTableLine(text, runs);
            })
            .Where(static line => line != null)
            .Select(static line => line!)
            .ToArray();
        PdfLayoutRectangle bounds = lines.Length == 0
            ? new PdfLayoutRectangle((left + right) / 2f, rowTop, 0, rowBottom - rowTop)
            : PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds));
        return new PdfSemanticTableCell(
            JoinParagraphLines(lines),
            bounds,
            lines,
            borderTop: borderTop && HasHorizontalRule(region, rowTop, left, right),
            borderRight: HasVerticalRule(region, right, rowTop, rowBottom),
            borderBottom: borderBottom && HasHorizontalRule(region, rowBottom, left, right),
            borderLeft: HasVerticalRule(region, left, rowTop, rowBottom));
    }

    private static bool HasHorizontalRule(
        RuledTableRegion region,
        float y,
        float left,
        float right)
    {
        return region.Rules.Any(rule =>
            rule.Orientation == TableRuleOrientation.Horizontal &&
            MathF.Abs(rule.Bounds.Y + rule.Bounds.Height / 2f - y) <= 1f &&
            HorizontalOverlap(rule.Bounds, new PdfLayoutRectangle(left, y, right - left, 0)) >=
                (right - left) * 0.45f);
    }

    private static bool HasVerticalRule(
        RuledTableRegion region,
        float x,
        float top,
        float bottom)
    {
        PdfLayoutRectangle rowBounds = new(x, top, 0, bottom - top);
        float covered = region.Rules
            .Where(rule => rule.Orientation == TableRuleOrientation.Vertical &&
                MathF.Abs(rule.Bounds.X + rule.Bounds.Width / 2f - x) <= 1f)
            .Sum(rule => VerticalOverlap(rule.Bounds, rowBounds));
        return covered >= (bottom - top) * 0.45f;
    }

    private static bool IsRuledTableHeaderRow(IReadOnlyList<PdfSemanticTableCell> cells)
    {
        PdfSemanticLine[] lines = cells
            .Where(static cell => !string.IsNullOrWhiteSpace(cell.Text))
            .SelectMany(static cell => cell.Lines)
            .ToArray();
        return lines.Length > 0 &&
            lines.Count(line => IsBoldFontName(line.DominantFontName)) >= Math.Max(1, lines.Length / 2);
    }

    private static IEnumerable<PdfSemanticElement> ExtractTextTables(
        PdfLayoutPage page,
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        float lineStep,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options,
        IReadOnlyList<PdfLayoutRectangle> horizontalRuleTableRegions)
    {
        TableSourceRow[] rows = BuildTableSourceRows(lines, bodyFontSize, consumed, options).ToArray();
        int index = 0;
        while (index < rows.Length)
        {
            if (IsConsumed(rows[index], consumed))
            {
                index++;
                continue;
            }

            if (!IsTableLikeRow(rows[index], page, horizontalRuleTableRegions))
            {
                index++;
                continue;
            }

            if (rows[index].TableLaneIndex.HasValue &&
                TableCaptionPattern.IsMatch(rows[index].Text.TrimStart()))
            {
                index++;
                continue;
            }

            int start = index;
            int tableLikeRowCount = 1;
            List<TableSourceRow> group = [rows[index]];
            PdfLayoutRectangle? horizontalRuleTableRegion = FindHorizontalRuleTableRegion(
                rows[index].Bounds,
                horizontalRuleTableRegions);
            index++;
            while (index < rows.Length)
            {
                TableSourceRow row = rows[index];
                if (IsConsumed(row, consumed))
                {
                    break;
                }

                if (horizontalRuleTableRegion.HasValue &&
                    !IsInsideHorizontalRuleTable(row.Bounds, [horizontalRuleTableRegion.Value]))
                {
                    break;
                }

                if (row.TableLaneIndex != group[0].TableLaneIndex)
                {
                    break;
                }

                float gap = MathF.Max(0f, row.Bounds.Y - group[^1].Bounds.Bottom);
                if (gap > MathF.Max(lineStep * 1.7f, bodyFontSize * 2.1f) &&
                    !IsLooseTableContinuation(group, row, page, gap, lineStep, bodyFontSize))
                {
                    break;
                }

                if (TableCaptionPattern.IsMatch(row.Text.TrimStart()))
                {
                    break;
                }

                if (IsTableLikeRow(row, page, horizontalRuleTableRegions))
                {
                    group.Add(row);
                    tableLikeRowCount++;
                    index++;
                    continue;
                }

                if (IsTableContinuationRow(group, row, page, horizontalRuleTableRegions))
                {
                    group.Add(row);
                    index++;
                    continue;
                }

                break;
            }

            PrependTableLeadRows(rows, start, group, page, lineStep, bodyFontSize, consumed);
            int firstGroupIndex = Array.IndexOf(rows, group[0]);
            bool hasCaptionLead = firstGroupIndex > 0 &&
                IsTableCaptionLeadRow(rows, firstGroupIndex - 1, group[0], lineStep, bodyFontSize);
            if (!IsValidTableGroup(group, tableLikeRowCount, page, hasCaptionLead))
            {
                index = start + 1;
                continue;
            }

            yield return CreateTableElement(page, group, consumed);
        }
    }

    private static void PrependTableLeadRows(
        IReadOnlyList<TableSourceRow> rows,
        int startIndex,
        List<TableSourceRow> group,
        PdfLayoutPage page,
        float lineStep,
        float bodyFontSize,
        HashSet<int> consumed)
    {
        float[] anchors = TableColumnAnchors(group);
        for (int index = startIndex - 1; index >= 0; index--)
        {
            TableSourceRow row = rows[index];
            if (IsConsumed(row, consumed))
            {
                break;
            }


            if (row.TableLaneIndex != group[0].TableLaneIndex)
            {
                break;
            }

            float gap = MathF.Max(0f, group[0].Bounds.Y - row.Bounds.Bottom);
            if (gap > MathF.Max(lineStep * 1.7f, bodyFontSize * 2.1f))
            {
                break;
            }

            if (IsTableCaptionLeadRow(rows, index, group[0], lineStep, bodyFontSize) ||
                LooksLikeProse(row.Text) ||
                row.Cells.Count > anchors.Length + 1 ||
                !IsCompatibleWithTableColumns(row, anchors, page))
            {
                break;
            }

            group.Insert(0, row);
        }
    }

    private static bool IsTableCaptionLeadRow(
        IReadOnlyList<TableSourceRow> rows,
        int index,
        TableSourceRow firstTableRow,
        float lineStep,
        float bodyFontSize)
    {
        float maximumLineGap = MathF.Max(lineStep * 1.75f, bodyFontSize * 1.8f);
        PdfLayoutRectangle nextBounds = firstTableRow.Bounds;
        for (int candidateIndex = index; candidateIndex >= 0 && index - candidateIndex < 8; candidateIndex--)
        {
            TableSourceRow candidate = rows[candidateIndex];
            if (candidate.TableLaneIndex != firstTableRow.TableLaneIndex)
            {
                return false;
            }

            float gap = MathF.Max(0f, nextBounds.Y - candidate.Bounds.Bottom);
            if (gap > maximumLineGap || candidate.Cells.Count > 1)
            {
                return false;
            }

            if (TableCaptionPattern.IsMatch(candidate.Text.TrimStart()))
            {
                return true;
            }

            nextBounds = candidate.Bounds;
        }

        return false;
    }

    private static bool IsConsumed(TableSourceRow row, HashSet<int> consumed)
    {
        return row.Lines.All(line => consumed.Contains(line.Index));
    }

    private static IEnumerable<TableSourceRow> BuildTableSourceRows(
        IReadOnlyList<LineCandidate> lines,
        float bodyFontSize,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        List<LineRow> rows = [];
        foreach (LineCandidate line in lines
            .Where(line => !consumed.Contains(line.Index))
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            LineRow? row = rows.FirstOrDefault(row => row.Contains(line));
            if (row == null)
            {
                rows.Add(new LineRow(line));
            }
            else
            {
                row.Add(line);
            }
        }

        foreach (LineRow row in rows
            .OrderBy(static row => row.Lines[0].TableLaneIndex.HasValue ? 0 : 1)
            .ThenBy(static row => row.Lines[0].TableLaneIndex)
            .ThenBy(static row => row.Bounds.Y)
            .ThenBy(static row => row.Bounds.X))
        {
            TableSourceRow? sourceRow = CreateTableSourceRow(row, bodyFontSize, options);
            if (sourceRow != null)
            {
                yield return sourceRow;
            }
        }
    }

    private static TableSourceRow? CreateTableSourceRow(
        LineRow row,
        float bodyFontSize,
        PdfSemanticExtractionOptions options)
    {
        PdfTextRun[] runs = row.Lines
            .SelectMany(static line => line.Source.Runs)
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .ToArray();
        if (runs.Length == 0)
        {
            return null;
        }

        float splitGap = MathF.Max(5.5f, bodyFontSize * 0.65f);
        List<List<PdfTextRun>> clusters = [];
        List<PdfTextRun> current = [];
        PdfTextRun? previous = null;
        foreach (PdfTextRun run in runs)
        {
            if (previous != null && HorizontalGap(previous.Bounds, run.Bounds) >= splitGap)
            {
                clusters.Add(current);
                current = [];
            }

            current.Add(run);
            previous = run;
        }

        if (current.Count > 0)
        {
            clusters.Add(current);
        }

        TableSourceCell[] cells = clusters
            .Select(cluster => CreateTableSourceCell(cluster, options))
            .Where(static cell => cell.Text.Length > 0)
            .OrderBy(static cell => cell.Bounds.X)
            .ToArray();
        return cells.Length == 0 ? null : new TableSourceRow(row.Lines, cells);
    }

    private static TableSourceCell CreateTableSourceCell(
        IReadOnlyList<PdfTextRun> runs,
        PdfSemanticExtractionOptions options)
    {
        string text = ReconstructText(runs.SelectMany(static run => run.Glyphs), options);
        return new TableSourceCell(
            text,
            PdfLayoutRectangle.Union(runs.Select(static run => run.Bounds)),
            runs);
    }

    private static bool IsTableLikeRow(
        TableSourceRow row,
        PdfLayoutPage page,
        IReadOnlyList<PdfLayoutRectangle> horizontalRuleTableRegions)
    {
        if (row.Cells.Count < 3 || row.Cells.Count > MaximumDetectedTableColumnCount)
        {
            return false;
        }

        bool isInsideHorizontalRuleTable = IsInsideHorizontalRuleTable(
            row.Bounds,
            horizontalRuleTableRegions);
        float minimumRowWidth = row.TableLaneIndex.HasValue
            ? page.Width * 0.18f
            : page.Width * 0.34f;
        if (row.Bounds.Width < minimumRowWidth && !isInsideHorizontalRuleTable)
        {
            return false;
        }

        if (row.Cells.Count <= 3 && LooksLikeProse(row.Text) && !isInsideHorizontalRuleTable)
        {
            return false;
        }

        int compactCellCount = row.Cells.Count(static cell => cell.Text.Length <= 48);
        return compactCellCount >= row.Cells.Count - 1;
    }

    private static bool IsInsideHorizontalRuleTable(
        PdfLayoutRectangle rowBounds,
        IReadOnlyList<PdfLayoutRectangle> regions)
    {
        float centerY = rowBounds.Y + rowBounds.Height / 2f;
        return regions.Any(region =>
            centerY >= region.Y - 1f &&
            centerY <= region.Bottom + 1f &&
            rowBounds.X >= region.X - 2f &&
            rowBounds.Right <= region.Right + 2f);
    }

    private static PdfLayoutRectangle? FindHorizontalRuleTableRegion(
        PdfLayoutRectangle rowBounds,
        IReadOnlyList<PdfLayoutRectangle> regions)
    {
        foreach (PdfLayoutRectangle region in regions)
        {
            if (IsInsideHorizontalRuleTable(rowBounds, [region]))
            {
                return region;
            }
        }

        return null;
    }

    private static bool IsLooseTableContinuation(
        IReadOnlyList<TableSourceRow> existingRows,
        TableSourceRow row,
        PdfLayoutPage page,
        float gap,
        float lineStep,
        float bodyFontSize)
    {
        if (gap > MathF.Max(lineStep * 12f, bodyFontSize * 12f) ||
            row.Cells.Count == 0 ||
            row.Cells.Count > MaximumTableColumnCount(existingRows) + 1 ||
            LooksLikeProse(row.Text))
        {
            return false;
        }

        float[] anchors = TableColumnAnchors(existingRows);
        if (anchors.Length >= 3 && IsCompatibleWithTableColumns(row, anchors, page))
        {
            return true;
        }

        return IsTableRowWithinExistingBounds(existingRows, row, page);
    }

    private static bool IsTableContinuationRow(
        IReadOnlyList<TableSourceRow> existingRows,
        TableSourceRow row,
        PdfLayoutPage page,
        IReadOnlyList<PdfLayoutRectangle> horizontalRuleTableRegions)
    {
        if (row.Cells.Count == 0 || row.Cells.Count > MaximumTableColumnCount(existingRows) + 1)
        {
            return false;
        }

        if (IsTableLikeRow(row, page, horizontalRuleTableRegions))
        {
            return true;
        }

        if (IsTableSectionLabel(existingRows, row, page))
        {
            return true;
        }

        if (LooksLikeProse(row.Text))
        {
            return false;
        }

        float[] anchors = TableColumnAnchors(existingRows);
        if (anchors.Length < 3)
        {
            return false;
        }

        if (IsCompatibleWithTableColumns(row, anchors, page))
        {
            return true;
        }

        return IsTableRowWithinExistingBounds(existingRows, row, page);
    }

    private static bool IsTableSectionLabel(
        IReadOnlyList<TableSourceRow> existingRows,
        TableSourceRow row,
        PdfLayoutPage page)
    {
        if (!row.TableLaneIndex.HasValue ||
            row.Cells.Count != 1 ||
            row.Text.Length > 80 ||
            EndsSentence(row.Text) ||
            LooksLikeProse(row.Text) ||
            MaximumTableColumnCount(existingRows) < 3)
        {
            return false;
        }

        PdfLayoutRectangle existingBounds = PdfLayoutRectangle.Union(
            existingRows.Select(static existing => existing.Bounds));
        float overlap = HorizontalOverlap(existingBounds, row.Bounds);
        return overlap >= MathF.Min(existingBounds.Width, row.Bounds.Width) * 0.65f &&
            row.Bounds.X >= existingBounds.X - page.Width * 0.04f &&
            row.Bounds.Right <= existingBounds.Right + page.Width * 0.04f;
    }

    private static bool IsTableRowWithinExistingBounds(
        IReadOnlyList<TableSourceRow> existingRows,
        TableSourceRow row,
        PdfLayoutPage page)
    {
        PdfLayoutRectangle existingBounds = PdfLayoutRectangle.Union(existingRows.Select(static existing => existing.Bounds));
        float overlap = HorizontalOverlap(existingBounds, row.Bounds);
        if (row.Cells.Count >= 3 &&
            overlap >= MathF.Min(existingBounds.Width, row.Bounds.Width) * 0.65f)
        {
            return true;
        }

        int numericCells = row.Cells.Count(static cell => LooksLikeNumericTableValue(cell.Text));
        return row.Cells.Count >= 3 &&
            numericCells >= row.Cells.Count - 1 &&
            row.Bounds.X >= existingBounds.X - page.Width * 0.04f &&
            row.Bounds.Right <= existingBounds.Right + page.Width * 0.04f;
    }

    private static bool IsValidTableGroup(
        IReadOnlyList<TableSourceRow> rows,
        int tableLikeRowCount,
        PdfLayoutPage page,
        bool hasCaptionLead)
    {
        int maximumColumnCount = MaximumTableColumnCount(rows);
        if (rows.Count < 3 || tableLikeRowCount < 2 || maximumColumnCount < 3)
        {
            return false;
        }

        if (!hasCaptionLead && LooksLikeBibliographyTableGroup(rows))
        {
            PdfLayoutRectangle textBounds = PdfLayoutRectangle.Union(rows.Select(static row => row.Bounds));
            if (!TableRules(page, textBounds).Any())
            {
                return false;
            }
        }

        float[] anchors = TableColumnAnchors(rows);
        int compatibleTableRows = rows
            .Where(row => row.Cells.Count >= 3)
            .Count(row => IsCompatibleWithTableColumns(row, anchors, page));
        return compatibleTableRows >= Math.Max(2, tableLikeRowCount - 1);
    }

    private static bool LooksLikeBibliographyTableGroup(IReadOnlyList<TableSourceRow> rows)
    {
        if (rows.Count < 3 || MaximumTableColumnCount(rows) < 4)
        {
            return false;
        }

        TableSourceCell[] commaCells = rows
            .SelectMany(static row => row.Cells)
            .Where(static cell => cell.Text.Contains(',', StringComparison.Ordinal))
            .ToArray();
        int letterWords = commaCells.Sum(static cell => WhitespacePattern
            .Split(cell.Text.Trim())
            .Count(static word => word.Count(char.IsLetter) >= 2));
        return commaCells.Length >= 4 && letterWords >= 8;
    }

    private static bool IsCompatibleWithTableColumns(
        TableSourceRow row,
        IReadOnlyList<float> columnCenters,
        PdfLayoutPage page)
    {
        if (columnCenters.Count < 3)
        {
            return false;
        }

        float tolerance = MathF.Max(32f, page.Width * 0.075f);
        int matches = row.Cells.Count(cell =>
            columnCenters.Min(center => MathF.Abs(center - cell.CenterX)) <= tolerance);
        int requiredMatches = Math.Min(
            row.Cells.Count,
            Math.Max(2, (int)MathF.Ceiling(columnCenters.Count * 0.60f)));
        return matches >= requiredMatches;
    }

    private static PdfSemanticElement CreateTableElement(
        PdfLayoutPage page,
        IReadOnlyList<TableSourceRow> sourceRows,
        HashSet<int> consumed)
    {
        foreach (TableSourceRow row in sourceRows)
        {
            foreach (LineCandidate line in row.Lines)
            {
                consumed.Add(line.Index);
            }
        }

        float[] columnCenters = TableColumnAnchors(sourceRows);
        List<PdfSemanticTableRow> tableRows = [];
        bool hasSeenDataRow = false;
        foreach (TableSourceRow sourceRow in sourceRows)
        {
            bool isHeaderRow = !hasSeenDataRow && LooksLikeTableHeaderRow(sourceRow);
            bool isDataRow = !isHeaderRow && LooksLikeTableDataRow(sourceRow);
            PdfSemanticTableRow semanticRow = CreateTableRow(sourceRow, columnCenters, isHeader: !hasSeenDataRow && !isDataRow);
            if (!hasSeenDataRow &&
                !isDataRow &&
                tableRows.Count > 0 &&
                ShouldMergeHeaderContinuation(sourceRow, semanticRow))
            {
                tableRows[^1] = MergeHeaderContinuation(tableRows[^1], semanticRow);
            }
            else
            {
                tableRows.Add(semanticRow);
            }

            if (isDataRow)
            {
                hasSeenDataRow = true;
            }
        }

        PdfSemanticLine[] lines = sourceRows
            .SelectMany(static row => row.Lines)
            .Select(static line => line.SemanticLine)
            .ToArray();
        PdfLayoutRectangle textBounds = PdfLayoutRectangle.Union(sourceRows.Select(static row => row.Bounds));
        PdfLayoutRectangle bounds = TableVisualBounds(page, textBounds);
        PdfSemanticTableRow[] structuredRows = ApplyTableHeaderSpans(
            ApplyTableStructure(ApplyTableRules(page, textBounds, tableRows))).ToArray();
        string text = string.Join(
            Environment.NewLine,
            structuredRows.Select(static row => string.Join("\t", row.Cells
                .Where(static cell => !cell.IsPlaceholder)
                .Select(static cell => cell.Text))));
        return new PdfSemanticElement(
            PdfSemanticElementKind.Table,
            text,
            bounds,
            lines,
            tableRows: structuredRows);
    }

    private static PdfSemanticTableRow CreateTableRow(
        TableSourceRow sourceRow,
        IReadOnlyList<float> columnCenters,
        bool isHeader)
    {
        List<TableSourceCell>[] assignedCells = Enumerable
            .Range(0, columnCenters.Count)
            .Select(static _ => new List<TableSourceCell>())
            .ToArray();

        if (sourceRow.Cells.Count == columnCenters.Count)
        {
            for (int index = 0; index < sourceRow.Cells.Count; index++)
            {
                assignedCells[index].Add(sourceRow.Cells[index]);
            }
        }
        else
        {
            HashSet<int> usedColumns = [];
            foreach (TableSourceCell cell in sourceRow.Cells)
            {
                int columnIndex = NearestAvailableColumn(cell.CenterX, columnCenters, usedColumns);
                assignedCells[columnIndex].Add(cell);
                usedColumns.Add(columnIndex);
            }
        }

        PdfSemanticTableCell[] cells = assignedCells
            .Select((cells, index) => CreateSemanticTableCell(cells, sourceRow, columnCenters[index]))
            .ToArray();
        return new PdfSemanticTableRow(cells, isHeader);
    }

    private static int NearestAvailableColumn(
        float center,
        IReadOnlyList<float> columnCenters,
        HashSet<int> usedColumns)
    {
        int nearest = Enumerable
            .Range(0, columnCenters.Count)
            .Where(index => !usedColumns.Contains(index))
            .OrderBy(index => MathF.Abs(columnCenters[index] - center))
            .DefaultIfEmpty(0)
            .First();
        return nearest;
    }

    private static PdfSemanticTableCell CreateSemanticTableCell(
        IReadOnlyList<TableSourceCell> cells,
        TableSourceRow row,
        float columnCenter)
    {
        if (cells.Count == 0)
        {
            return new PdfSemanticTableCell(
                "",
                new PdfLayoutRectangle(columnCenter, row.Bounds.Y, 0, row.Bounds.Height),
                []);
        }

        PdfTextRun[] runs = cells
            .SelectMany(static cell => cell.Runs)
            .OrderBy(static run => run.Bounds.Y)
            .ThenBy(static run => run.Bounds.X)
            .ToArray();
        PdfSemanticLine line = CreateSyntheticTableLine(
            string.Join(" ", cells.Select(static cell => cell.Text)),
            runs);
        return new PdfSemanticTableCell(line.Text, line.Bounds, [line]);
    }

    private static PdfSemanticLine CreateSyntheticTableLine(string text, IReadOnlyList<PdfTextRun> runs)
    {
        (string fontName, float fontSize, float direction, PdfLayoutColor color) = DominantStyle(runs);
        return new PdfSemanticLine(
            NormalizeText(text),
            PdfLayoutRectangle.Union(runs.Select(static run => run.Bounds)),
            fontName,
            fontSize,
            direction,
            color,
            runs);
    }

    private static PdfSemanticTableRow MergeHeaderContinuation(
        PdfSemanticTableRow header,
        PdfSemanticTableRow continuation)
    {
        PdfSemanticTableCell[] cells = header.Cells
            .Select((cell, index) =>
            {
                PdfSemanticTableCell? next = continuation.Cells.ElementAtOrDefault(index);
                if (next == null || string.IsNullOrWhiteSpace(next.Text))
                {
                    return cell;
                }

                if (string.IsNullOrWhiteSpace(cell.Text))
                {
                    return next;
                }

                return new PdfSemanticTableCell(
                    cell.Text + " " + next.Text,
                    PdfLayoutRectangle.Union([cell.Bounds, next.Bounds]),
                    cell.Lines.Concat(next.Lines).ToArray(),
                    cell.BorderTop || next.BorderTop,
                    cell.BorderRight || next.BorderRight,
                    cell.BorderBottom || next.BorderBottom,
                    cell.BorderLeft || next.BorderLeft,
                    Math.Max(cell.RowSpan, next.RowSpan),
                    Math.Max(cell.ColumnSpan, next.ColumnSpan),
                    cell.IsPlaceholder && next.IsPlaceholder);
            })
            .ToArray();
        return new PdfSemanticTableRow(cells, isHeader: true);
    }

    private static bool ShouldMergeHeaderContinuation(
        TableSourceRow sourceRow,
        PdfSemanticTableRow continuation)
    {
        int nonEmptyCells = continuation.Cells.Count(static cell => !string.IsNullOrWhiteSpace(cell.Text));
        return sourceRow.Cells.Count <= 1 && nonEmptyCells == 1;
    }

    private static bool LooksLikeTableDataRow(TableSourceRow row)
    {
        return row.Cells.Any(static cell => cell.Text.Any(char.IsDigit)) &&
            !row.Text.Contains("FLOPs", StringComparison.OrdinalIgnoreCase) &&
            !row.Text.Contains("BLEU", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeTableHeaderRow(TableSourceRow row)
    {
        string[] cells = row.Cells.Select(static cell => cell.Text.Trim()).ToArray();
        if (cells.Any(static text =>
                string.Equals(text, "BLEU", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "PPL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "(dev)", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "steps", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "params", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Parser", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Training", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Training Cost", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("WSJ 23", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("FLOPs", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        int shortCells = row.Cells.Count(static cell => cell.Text.Trim().Length <= 18);
        int numericDataCells = row.Cells.Count(static cell => LooksLikeNumericTableValue(cell.Text));
        return row.Cells.Count >= 3 &&
            shortCells >= row.Cells.Count - 1 &&
            numericDataCells <= Math.Max(1, row.Cells.Count / 4);
    }

    private static bool LooksLikeNumericTableValue(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > 0 &&
            trimmed.Any(static character => char.IsDigit(character)) &&
            trimmed.All(static character =>
                char.IsDigit(character) ||
                character is '.' or ',' or '-' or '+' or '·' or '×' or '/' or '(' or ')' ||
                char.IsWhiteSpace(character) ||
                char.IsLetter(character));
    }

    private static IReadOnlyList<PdfSemanticTableRow> ApplyTableRules(
        PdfLayoutPage page,
        PdfLayoutRectangle tableBounds,
        IReadOnlyList<PdfSemanticTableRow> rows)
    {
        TableRule[] rules = TableRules(page, tableBounds).ToArray();
        if (rules.Length == 0 || rows.Count == 0)
        {
            return rows;
        }

        MutableCellBorders[][] borders = rows
            .Select(row => row.Cells.Select(static _ => new MutableCellBorders()).ToArray())
            .ToArray();
        PdfLayoutRectangle[] rowBounds = rows
            .Select(row => PdfLayoutRectangle.Union(row.Cells.Select(static cell => cell.Bounds)))
            .ToArray();
        float[] rowCenters = rowBounds.Select(static bounds => bounds.Y + bounds.Height / 2f).ToArray();
        float[] columnCenters = TableColumnCenters(rows);

        foreach (TableRule rule in rules)
        {
            if (rule.Orientation == TableRuleOrientation.Horizontal)
            {
                ApplyHorizontalTableRule(rule.Bounds, rows, rowCenters, borders);
            }
            else
            {
                ApplyVerticalTableRule(rule.Bounds, rows, rowBounds, columnCenters, borders);
            }
        }

        return rows
            .Select((row, rowIndex) => new PdfSemanticTableRow(
                row.Cells
                    .Select((cell, cellIndex) =>
                    {
                        MutableCellBorders cellBorders = borders[rowIndex][cellIndex];
                        return new PdfSemanticTableCell(
                            cell.Text,
                            cell.Bounds,
                            cell.Lines,
                            cell.BorderTop || cellBorders.Top,
                            cell.BorderRight || cellBorders.Right,
                            cell.BorderBottom || cellBorders.Bottom,
                            cell.BorderLeft || cellBorders.Left,
                            cell.RowSpan,
                            cell.ColumnSpan,
                            cell.IsPlaceholder);
                    })
                    .ToArray(),
                row.IsHeader))
            .ToArray();
    }

    private static IReadOnlyList<PdfSemanticTableRow> ApplyTableStructure(IReadOnlyList<PdfSemanticTableRow> rows)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        return ApplyDescriptorColumnSpans(ApplyRowGroupSpans(rows));
    }

    private static IReadOnlyList<PdfSemanticTableRow> ApplyTableHeaderSpans(IReadOnlyList<PdfSemanticTableRow> rows)
    {
        int headerRowCount = rows.TakeWhile(static row => row.IsHeader).Count();
        if (headerRowCount < 2)
        {
            return rows;
        }

        List<PdfSemanticTableRow> structuredRows = rows.ToList();
        for (int headerRowIndex = 0; headerRowIndex < headerRowCount - 1; headerRowIndex++)
        {
            PdfSemanticTableRow upperHeader = structuredRows[headerRowIndex];
            PdfSemanticTableRow lowerHeader = structuredRows[headerRowIndex + 1];
            PdfSemanticTableCell[] upperCells = upperHeader.Cells.ToArray();
            PdfSemanticTableCell[] lowerCells = lowerHeader.Cells.ToArray();
            if (upperCells.Length == 0 || lowerCells.Length == 0)
            {
                continue;
            }

            (int ColumnIndex, PdfSemanticTableCell Cell)[] parentCells = upperHeader.Cells
                .Select((cell, columnIndex) => (columnIndex, cell))
                .Where(static entry => !entry.cell.IsPlaceholder && !string.IsNullOrWhiteSpace(entry.cell.Text))
                .OrderBy(static entry => entry.cell.Bounds.X + entry.cell.Bounds.Width / 2f)
                .ToArray();
            (int ColumnIndex, PdfSemanticTableCell Cell)[] childCells = lowerHeader.Cells
                .Select((cell, columnIndex) => (columnIndex, cell))
                .Where(static entry => !entry.cell.IsPlaceholder && !string.IsNullOrWhiteSpace(entry.cell.Text))
                .ToArray();
            if (parentCells.Length == 0)
            {
                continue;
            }

            foreach ((int parentColumnIndex, PdfSemanticTableCell parentCell) in parentCells)
            {
                (int ColumnIndex, PdfSemanticTableCell Cell)[] children = childCells
                    .Where(child => NearestHeaderParent(parentCells, child.Cell) == parentColumnIndex)
                    .OrderBy(static child => child.ColumnIndex)
                    .ToArray();
                if (children.Length >= 2)
                {
                    int targetColumnIndex = children[0].ColumnIndex;
                    upperCells[targetColumnIndex] = new PdfSemanticTableCell(
                        parentCell.Text,
                        parentCell.Bounds,
                        parentCell.Lines,
                        parentCell.BorderTop,
                        parentCell.BorderRight,
                        parentCell.BorderBottom,
                        parentCell.BorderLeft,
                        columnSpan: children.Length);

                    foreach ((int childColumnIndex, _) in children.Skip(1))
                    {
                        upperCells[childColumnIndex] = CreatePlaceholderCell(upperCells[childColumnIndex]);
                    }

                    if (parentColumnIndex != targetColumnIndex)
                    {
                        upperCells[parentColumnIndex] = CreatePlaceholderCell(upperCells[parentColumnIndex]);
                    }

                    continue;
                }

                if (children.Length != 0 || parentColumnIndex >= lowerCells.Length)
                {
                    continue;
                }

                PdfSemanticTableCell lowerCell = lowerCells[parentColumnIndex];
                if (lowerCell.IsPlaceholder || !string.IsNullOrWhiteSpace(lowerCell.Text))
                {
                    continue;
                }

                upperCells[parentColumnIndex] = new PdfSemanticTableCell(
                    parentCell.Text,
                    parentCell.Bounds,
                    parentCell.Lines,
                    parentCell.BorderTop,
                    parentCell.BorderRight,
                    parentCell.BorderBottom,
                    parentCell.BorderLeft,
                    rowSpan: 2);
                lowerCells[parentColumnIndex] = CreatePlaceholderCell(lowerCell);
            }

            structuredRows[headerRowIndex] = new PdfSemanticTableRow(upperCells, isHeader: true);
            structuredRows[headerRowIndex + 1] = new PdfSemanticTableRow(lowerCells, isHeader: true);
        }

        return structuredRows;
    }

    private static int NearestHeaderParent(
        IReadOnlyList<(int ColumnIndex, PdfSemanticTableCell Cell)> parentCells,
        PdfSemanticTableCell childCell)
    {
        float childCenter = childCell.Bounds.X + childCell.Bounds.Width / 2f;
        return parentCells
            .OrderBy(parent => MathF.Abs((parent.Cell.Bounds.X + parent.Cell.Bounds.Width / 2f) - childCenter))
            .ThenBy(static parent => parent.ColumnIndex)
            .First()
            .ColumnIndex;
    }

    private static IReadOnlyList<PdfSemanticTableRow> ApplyRowGroupSpans(IReadOnlyList<PdfSemanticTableRow> rows)
    {
        List<PdfSemanticTableRow> structuredRows = rows.ToList();
        int headerRowCount = structuredRows.TakeWhile(static row => row.IsHeader).Count();
        for (int rowIndex = headerRowCount; rowIndex < structuredRows.Count; rowIndex++)
        {
            if (structuredRows[rowIndex].Cells.Count == 0)
            {
                continue;
            }

            string groupLabel = structuredRows[rowIndex].Cells[0].Text.Trim();
            if (!LooksLikeTableGroupLabel(groupLabel))
            {
                continue;
            }

            int groupStart = PreviousTableGroupBoundary(structuredRows, rowIndex, headerRowCount) + 1;
            int groupEnd = NextTableGroupBoundary(structuredRows, rowIndex);
            if (groupEnd < groupStart)
            {
                continue;
            }

            bool labelOnlyRow = IsTableGroupLabelOnlyRow(structuredRows[rowIndex]);
            int[] dataRowIndexes = Enumerable
                .Range(groupStart, groupEnd - groupStart + 1)
                .Where(index => !labelOnlyRow || index != rowIndex)
                .Where(index => !structuredRows[index].IsHeader)
                .Where(index => TableRowHasDataBeyondFirstColumn(structuredRows[index]))
                .ToArray();
            if (dataRowIndexes.Length <= 1)
            {
                continue;
            }

            int targetRowIndex = dataRowIndexes[0];
            PdfSemanticTableCell labelCell = structuredRows[rowIndex].Cells[0];
            PdfSemanticTableCell targetCell = structuredRows[targetRowIndex].Cells[0];
            PdfSemanticTableCell[] firstColumnCells = dataRowIndexes
                .Where(index => structuredRows[index].Cells.Count > 0)
                .Select(index => structuredRows[index].Cells[0])
                .Append(labelCell)
                .ToArray();
            PdfSemanticTableCell rowGroupCell = new(
                groupLabel,
                PdfLayoutRectangle.Union(firstColumnCells.Select(static cell => cell.Bounds)),
                labelCell.Lines.Count > 0 ? labelCell.Lines : targetCell.Lines,
                firstColumnCells.Any(static cell => cell.BorderTop),
                firstColumnCells.Any(static cell => cell.BorderRight),
                firstColumnCells.Any(static cell => cell.BorderBottom),
                firstColumnCells.Any(static cell => cell.BorderLeft),
                rowSpan: dataRowIndexes.Length);
            structuredRows[targetRowIndex] = ReplaceTableCell(structuredRows[targetRowIndex], 0, rowGroupCell);

            foreach (int coveredRowIndex in dataRowIndexes.Skip(1))
            {
                structuredRows[coveredRowIndex] = ReplaceTableCell(
                    structuredRows[coveredRowIndex],
                    0,
                    CreatePlaceholderCell(structuredRows[coveredRowIndex].Cells[0]));
            }

            if (labelOnlyRow)
            {
                structuredRows.RemoveAt(rowIndex);
                rowIndex--;
            }
            else if (rowIndex != targetRowIndex)
            {
                structuredRows[rowIndex] = ReplaceTableCell(
                    structuredRows[rowIndex],
                    0,
                    CreatePlaceholderCell(structuredRows[rowIndex].Cells[0]));
            }
        }

        return structuredRows;
    }

    private static IReadOnlyList<PdfSemanticTableRow> ApplyDescriptorColumnSpans(IReadOnlyList<PdfSemanticTableRow> rows)
    {
        int headerRowCount = rows.TakeWhile(static row => row.IsHeader).Count();
        int metricColumnIndex = FirstMetricColumnIndex(rows.Take(headerRowCount).ToArray());
        if (metricColumnIndex <= 2)
        {
            return rows;
        }

        List<PdfSemanticTableRow> structuredRows = rows.ToList();
        for (int rowIndex = headerRowCount; rowIndex < structuredRows.Count; rowIndex++)
        {
            PdfSemanticTableRow row = structuredRows[rowIndex];
            if (row.Cells.Count <= metricColumnIndex ||
                !LooksLikeTableGroupLabel(row.Cells[0].Text.Trim()))
            {
                continue;
            }

            int descriptorColumnIndex = Enumerable
                .Range(1, metricColumnIndex - 1)
                .FirstOrDefault(index => LooksLikeWideDescriptorCell(row.Cells[index].Text));
            if (descriptorColumnIndex == 0)
            {
                continue;
            }

            PdfSemanticTableCell[] cells = row.Cells.ToArray();
            PdfSemanticTableCell descriptorCell = cells[descriptorColumnIndex];
            PdfSemanticTableCell[] spannedCells = cells
                .Skip(1)
                .Take(metricColumnIndex - 1)
                .Append(descriptorCell)
                .ToArray();
            cells[1] = new PdfSemanticTableCell(
                descriptorCell.Text,
                PdfLayoutRectangle.Union(spannedCells.Select(static cell => cell.Bounds)),
                descriptorCell.Lines,
                spannedCells.Any(static cell => cell.BorderTop),
                spannedCells.Any(static cell => cell.BorderRight),
                spannedCells.Any(static cell => cell.BorderBottom),
                spannedCells.Any(static cell => cell.BorderLeft),
                columnSpan: metricColumnIndex - 1);
            for (int columnIndex = 2; columnIndex < metricColumnIndex; columnIndex++)
            {
                cells[columnIndex] = CreatePlaceholderCell(cells[columnIndex]);
            }

            structuredRows[rowIndex] = new PdfSemanticTableRow(cells, row.IsHeader);
        }

        return structuredRows;
    }

    private static int PreviousTableGroupBoundary(
        IReadOnlyList<PdfSemanticTableRow> rows,
        int rowIndex,
        int headerRowCount)
    {
        for (int index = rowIndex - 1; index >= headerRowCount; index--)
        {
            if (HasBottomBorder(rows[index]))
            {
                return index;
            }
        }

        return headerRowCount - 1;
    }

    private static int NextTableGroupBoundary(IReadOnlyList<PdfSemanticTableRow> rows, int rowIndex)
    {
        for (int index = rowIndex; index < rows.Count; index++)
        {
            if (HasBottomBorder(rows[index]))
            {
                return index;
            }
        }

        return rows.Count - 1;
    }

    private static int FirstMetricColumnIndex(IReadOnlyList<PdfSemanticTableRow> headerRows)
    {
        int columnCount = headerRows.Count == 0 ? 0 : headerRows.Max(static row => row.Cells.Count);
        for (int columnIndex = 1; columnIndex < columnCount; columnIndex++)
        {
            string headerText = string.Join(" ", headerRows
                .Where(row => columnIndex < row.Cells.Count)
                .Select(row => row.Cells[columnIndex].Text));
            if (headerText.Contains("PPL", StringComparison.OrdinalIgnoreCase) ||
                headerText.Contains("BLEU", StringComparison.OrdinalIgnoreCase) ||
                headerText.Contains("WSJ 23", StringComparison.OrdinalIgnoreCase))
            {
                return columnIndex;
            }
        }

        return -1;
    }

    private static bool LooksLikeTableGroupLabel(string text)
    {
        return text.Length == 3 &&
            text[0] == '(' &&
            text[2] == ')' &&
            char.IsUpper(text[1]);
    }

    private static bool IsTableGroupLabelOnlyRow(PdfSemanticTableRow row)
    {
        return row.Cells.Count > 0 &&
            LooksLikeTableGroupLabel(row.Cells[0].Text.Trim()) &&
            row.Cells.Skip(1).All(static cell => string.IsNullOrWhiteSpace(cell.Text));
    }

    private static bool TableRowHasDataBeyondFirstColumn(PdfSemanticTableRow row)
    {
        return row.Cells.Skip(1).Any(static cell => !cell.IsPlaceholder && !string.IsNullOrWhiteSpace(cell.Text));
    }

    private static bool LooksLikeWideDescriptorCell(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length >= 12 &&
            trimmed.Contains(' ', StringComparison.Ordinal) &&
            trimmed.Any(char.IsLetter);
    }

    private static bool HasBottomBorder(PdfSemanticTableRow row)
    {
        return row.Cells.Any(static cell => cell.BorderBottom);
    }

    private static PdfSemanticTableRow ReplaceTableCell(
        PdfSemanticTableRow row,
        int cellIndex,
        PdfSemanticTableCell replacement)
    {
        PdfSemanticTableCell[] cells = row.Cells.ToArray();
        cells[cellIndex] = replacement;
        return new PdfSemanticTableRow(cells, row.IsHeader);
    }

    private static PdfSemanticTableCell CreatePlaceholderCell(PdfSemanticTableCell cell)
    {
        return new PdfSemanticTableCell(
            "",
            cell.Bounds,
            [],
            cell.BorderTop,
            cell.BorderRight,
            cell.BorderBottom,
            cell.BorderLeft,
            isPlaceholder: true);
    }

    private static PdfLayoutRectangle TableVisualBounds(PdfLayoutPage page, PdfLayoutRectangle textBounds)
    {
        PdfLayoutRectangle[] rules = TableRules(page, textBounds)
            .Select(static rule => rule.Bounds)
            .ToArray();
        return rules.Length == 0
            ? textBounds
            : PdfLayoutRectangle.Union(rules.Append(textBounds));
    }

    private static IEnumerable<TableRule> TableRules(PdfLayoutPage page, PdfLayoutRectangle tableBounds)
    {
        PdfLayoutRectangle expanded = ExpandRectangle(tableBounds, 8f, 8f);
        foreach (PdfLayoutPath path in page.Paths)
        {
            foreach (PdfLayoutRectangle bounds in PathRuleSegments(path))
            {
                if (!Intersects(expanded, bounds))
                {
                    continue;
                }

                if (bounds.Width >= 12f && bounds.Width >= MathF.Max(0.1f, bounds.Height) * 8f)
                {
                    yield return new TableRule(TableRuleOrientation.Horizontal, bounds);
                }
                else if (bounds.Height >= 6f && bounds.Height >= MathF.Max(0.1f, bounds.Width) * 8f)
                {
                    yield return new TableRule(TableRuleOrientation.Vertical, bounds);
                }
            }
        }
    }

    private static IEnumerable<PdfLayoutRectangle> PathRuleSegments(PdfLayoutPath path)
    {
        PdfLayoutPathCommand? previous = null;
        foreach (PdfLayoutPathCommand command in path.Commands)
        {
            if (command.Kind == PdfLayoutPathCommandKind.MoveTo)
            {
                previous = command;
                continue;
            }

            if (command.Kind != PdfLayoutPathCommandKind.LineTo || previous == null)
            {
                continue;
            }

            PdfLayoutPathCommand start = previous.Value;
            float x = MathF.Min(start.X1, command.X1);
            float y = MathF.Min(start.Y1, command.Y1);
            float width = MathF.Abs(command.X1 - start.X1);
            float height = MathF.Abs(command.Y1 - start.Y1);
            yield return new PdfLayoutRectangle(x, y, width, height);
            previous = command;
        }
    }

    private static void ApplyHorizontalTableRule(
        PdfLayoutRectangle rule,
        IReadOnlyList<PdfSemanticTableRow> rows,
        IReadOnlyList<float> rowCenters,
        MutableCellBorders[][] borders)
    {
        if (rowCenters.Count == 0)
        {
            return;
        }

        float y = rule.Y + rule.Height / 2f;
        if (y <= rowCenters[0])
        {
            MarkHorizontalCells(rows[0], borders[0], rule, top: true);
            return;
        }

        if (y >= rowCenters[^1])
        {
            MarkHorizontalCells(rows[^1], borders[^1], rule, top: false);
            return;
        }

        int previousRowIndex = 0;
        for (int index = 0; index + 1 < rowCenters.Count; index++)
        {
            if (y >= rowCenters[index] && y <= rowCenters[index + 1])
            {
                previousRowIndex = index;
                break;
            }
        }

        MarkHorizontalCells(rows[previousRowIndex], borders[previousRowIndex], rule, top: false);
    }

    private static void MarkHorizontalCells(
        PdfSemanticTableRow row,
        IReadOnlyList<MutableCellBorders> borders,
        PdfLayoutRectangle rule,
        bool top)
    {
        for (int index = 0; index < row.Cells.Count; index++)
        {
            PdfSemanticTableCell cell = row.Cells[index];
            if (!HorizontallyTouchesRule(cell.Bounds, rule))
            {
                continue;
            }

            if (top)
            {
                borders[index].Top = true;
            }
            else
            {
                borders[index].Bottom = true;
            }
        }
    }

    private static bool HorizontallyTouchesRule(PdfLayoutRectangle cellBounds, PdfLayoutRectangle rule)
    {
        if (HorizontalOverlap(cellBounds, rule) > 0f)
        {
            return true;
        }

        if (cellBounds.Width > 0.5f)
        {
            return false;
        }

        float centerX = cellBounds.X + cellBounds.Width / 2f;
        return centerX >= rule.X - 0.5f && centerX <= rule.Right + 0.5f;
    }

    private static void ApplyVerticalTableRule(
        PdfLayoutRectangle rule,
        IReadOnlyList<PdfSemanticTableRow> rows,
        IReadOnlyList<PdfLayoutRectangle> rowBounds,
        IReadOnlyList<float> columnCenters,
        MutableCellBorders[][] borders)
    {
        if (columnCenters.Count == 0)
        {
            return;
        }

        float x = rule.X + rule.Width / 2f;
        int leftColumn = 0;
        for (int index = 0; index + 1 < columnCenters.Count; index++)
        {
            if (x >= columnCenters[index] && x <= columnCenters[index + 1])
            {
                leftColumn = index;
                break;
            }
        }

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (VerticalOverlap(rowBounds[rowIndex], rule) <= 0f ||
                leftColumn >= rows[rowIndex].Cells.Count)
            {
                continue;
            }

            borders[rowIndex][leftColumn].Right = true;
            if (leftColumn + 1 < rows[rowIndex].Cells.Count)
            {
                borders[rowIndex][leftColumn + 1].Left = true;
            }
        }
    }

    private static float[] TableColumnCenters(IReadOnlyList<PdfSemanticTableRow> rows)
    {
        int columnCount = rows.Max(static row => row.Cells.Count);
        return Enumerable
            .Range(0, columnCount)
            .Select(index => rows
                .Where(row => index < row.Cells.Count)
                .Select(row => row.Cells[index].Bounds.X + row.Cells[index].Bounds.Width / 2f)
                .DefaultIfEmpty()
                .Average())
            .ToArray();
    }

    private static bool Intersects(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        return first.X <= second.Right &&
            first.Right >= second.X &&
            first.Y <= second.Bottom &&
            first.Bottom >= second.Y;
    }

    private static float HorizontalOverlap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        return MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X);
    }

    private static float VerticalOverlap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        return MathF.Min(first.Bottom, second.Bottom) - MathF.Max(first.Y, second.Y);
    }

    private static PdfLayoutRectangle ExpandRectangle(PdfLayoutRectangle bounds, float horizontal, float vertical)
    {
        return new PdfLayoutRectangle(
            bounds.X - horizontal,
            bounds.Y - vertical,
            bounds.Width + horizontal + horizontal,
            bounds.Height + vertical + vertical);
    }

    private static bool LooksLikeProse(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length < 80)
        {
            return false;
        }

        return EndsSentence(trimmed) ||
            trimmed.Count(static character => character == ' ') >= 9;
    }

    private static int MaximumTableColumnCount(IReadOnlyList<TableSourceRow> rows)
    {
        return rows.Count == 0 ? 0 : rows.Max(static row => row.Cells.Count);
    }

    private static float[] TableColumnAnchors(IReadOnlyList<TableSourceRow> rows)
    {
        TableSourceRow? widest = rows
            .Where(static row => row.Cells.Count >= 3)
            .OrderByDescending(static row => row.Cells.Count)
            .ThenByDescending(static row => row.Bounds.Width)
            .FirstOrDefault();
        return widest == null
            ? []
            : widest.Cells.Select(static cell => cell.CenterX).Order().ToArray();
    }

    private static bool IsParagraphCandidate(LineCandidate line, float bodyFontSize)
    {
        if (line.Text.Length <= 1 || IsFootnoteMarker(line.Text))
        {
            return false;
        }

        return line.FontSize >= bodyFontSize - 2f || line.Text.Length >= 24;
    }

    private static bool IsInlineArtifact(LineCandidate line, float bodyFontSize)
    {
        return line.Text.Length <= 18 &&
            !IsFootnoteMarker(line.Text) &&
            (line.FontSize < bodyFontSize - 2f || HasMathFont(line) || line.Text.Length == 1);
    }

    private static bool ShouldAttachFormulaArtifact(
        IReadOnlyList<LineCandidate> current,
        LineCandidate artifact,
        float lineStep)
    {
        if (!current.Any(line => IsDisplayFormulaLine(line, line.FontSize)))
        {
            return false;
        }

        if (!HasMathFont(artifact))
        {
            return false;
        }

        PdfLayoutRectangle currentBounds = PdfLayoutRectangle.Union(current.Select(static line => line.Bounds));
        float verticalGap = MathF.Max(artifact.Bounds.Y - currentBounds.Bottom, currentBounds.Y - artifact.Bounds.Bottom);
        if (verticalGap > lineStep * 0.75f)
        {
            return false;
        }

        return HorizontalGap(currentBounds, artifact.Bounds) <= 8f ||
            (artifact.Bounds.X >= currentBounds.X - 4f && artifact.Bounds.X <= currentBounds.Right + 4f);
    }

    private static bool ShouldAttachInlineArtifact(
        IReadOnlyList<LineCandidate> current,
        LineCandidate artifact,
        float lineStep)
    {
        if (current.Any(line => IsDisplayFormulaLine(line, line.FontSize)))
        {
            return false;
        }

        PdfLayoutRectangle currentBounds = PdfLayoutRectangle.Union(current.Select(static line => line.Bounds));
        if (artifact.Bounds.Y > currentBounds.Bottom + lineStep * 1.6f ||
            artifact.Bounds.Bottom < currentBounds.Y - lineStep * 0.5f)
        {
            return false;
        }

        if (artifact.Bounds.Right < currentBounds.X - 8f ||
            artifact.Bounds.X > currentBounds.Right + 8f)
        {
            return false;
        }

        return current.Any(line => IsInlineWithTextLine(line, artifact));
    }

    private static bool ShouldAttachInlineMathContinuation(
        IReadOnlyList<LineCandidate> current,
        LineCandidate artifact,
        float lineStep)
    {
        if (!HasMathFont(artifact) ||
            artifact.Bounds.Width > 120f ||
            current.Count == 0)
        {
            return false;
        }

        string text = artifact.Text.TrimStart();
        if (text.Length == 0 || !char.IsLetter(text[0]))
        {
            return false;
        }

        LineCandidate previous = current[^1];
        float verticalGap = artifact.Bounds.Y - previous.Bounds.Bottom;
        return verticalGap >= -lineStep * 0.25f &&
            verticalGap <= lineStep * 1.8f &&
            MathF.Abs(artifact.Bounds.X - previous.Bounds.X) <= 16f &&
            !EndsSentence(previous.Text);
    }

    private static bool IsInlineWithTextLine(LineCandidate textLine, LineCandidate artifact)
    {
        if (artifact.Bounds.Right < textLine.Bounds.X - 8f ||
            artifact.Bounds.X > textLine.Bounds.Right + 8f)
        {
            return false;
        }

        float overlap = MathF.Min(textLine.Bounds.Bottom, artifact.Bounds.Bottom) -
            MathF.Max(textLine.Bounds.Y, artifact.Bounds.Y);
        if (overlap >= MathF.Min(textLine.Bounds.Height, artifact.Bounds.Height) * 0.15f)
        {
            return true;
        }

        float centerDistance = MathF.Abs(
            textLine.Bounds.Y + (textLine.Bounds.Height / 2f) -
            (artifact.Bounds.Y + (artifact.Bounds.Height / 2f)));
        return centerDistance <= MathF.Max(3f, textLine.Bounds.Height * 0.55f);
    }

    private static bool IsDisplayFormulaLine(LineCandidate line, float bodyFontSize)
    {
        if (!HasMathFont(line) || !HasFormulaOperator(line.Text))
        {
            return false;
        }

        if (HasFormulaFunction(line.Text))
        {
            return line.Text.IndexOf('=') >= 0 ||
                StartsFormulaFunction(line.Text) ||
                line.Bounds.Width >= 80f &&
                CountWords(line.Text) <= 4;
        }

        if (IsMathDominantFormulaLine(line.Text, line.Bounds, line.Source.Runs))
        {
            return true;
        }

        bool centeredEnough = line.Bounds.X >= 150f && line.Bounds.Width >= 80f;
        int wordCount = CountWords(line.Text);
        return centeredEnough &&
            !IsProseDominantFormulaLine(line.Text, line.Source.Runs) &&
            (wordCount <= 4 && line.FontSize <= bodyFontSize + 1f ||
                wordCount <= 12 &&
                HasLargeFormulaOperator(line.Source.Runs));
    }

    private static bool IsMathDominantFormulaLine(
        string text,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfTextRun> runs)
    {
        if (bounds.Width < 80f || text.Length > 220)
        {
            return false;
        }

        if (!IsFormulaMathFont(DominantStyle(runs).FontName))
        {
            return false;
        }

        int totalCharacters = runs.Sum(static run =>
            run.Text.Count(static character => !char.IsWhiteSpace(character)));
        int mathCharacters = runs
            .Where(static run => IsFormulaMathFont(run.FontName))
            .Sum(static run => run.Text.Count(static character => !char.IsWhiteSpace(character)));
        return mathCharacters >= 8 &&
            totalCharacters > 0 &&
            mathCharacters / (float)totalCharacters >= 0.58f;
    }

    private static List<LineCandidate> DetachTrailingFormulaAttachments(
        List<LineCandidate> current,
        LineCandidate displayLine)
    {
        List<LineCandidate> attachments = [];
        while (current.Count > 0 && IsFormulaLineAttachment(current[^1], displayLine))
        {
            attachments.Add(current[^1]);
            current.RemoveAt(current.Count - 1);
        }

        attachments.Reverse();
        return attachments;
    }

    private static bool IsFormulaLineAttachment(LineCandidate candidate, LineCandidate displayLine)
    {
        if (candidate.Text.Trim().Length is 0 or > 18 ||
            !candidate.Source.Runs
                .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
                .All(static run => IsMathFont(run.FontName)))
        {
            return false;
        }

        float verticalGap = MathF.Max(0f, MathF.Max(
            candidate.Bounds.Y - displayLine.Bounds.Bottom,
            displayLine.Bounds.Y - candidate.Bounds.Bottom));
        float horizontalTolerance = MathF.Max(8f, displayLine.FontSize);
        return verticalGap <= MathF.Max(2.5f, displayLine.FontSize * 0.35f) &&
            candidate.Bounds.Right >= displayLine.Bounds.X - horizontalTolerance &&
            candidate.Bounds.X <= displayLine.Bounds.Right + horizontalTolerance;
    }

    private static bool IsDisplayFormulaContinuation(
        IReadOnlyList<LineCandidate> current,
        LineCandidate line,
        float lineStep)
    {
        string text = line.Text.TrimStart();
        bool formulaClause =
            text.StartsWith("where ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Where ", StringComparison.Ordinal) ||
            text.StartsWith(",", StringComparison.Ordinal);
        bool formulaClauseContinuation =
            text.StartsWith("and ", StringComparison.OrdinalIgnoreCase) &&
            (HasFormulaOperator(text) || HasMathFont(line) || line.Bounds.Width <= 140f);
        if (!HasMathFont(line) && !formulaClause && !formulaClauseContinuation)
        {
            return false;
        }

        PdfLayoutRectangle currentBounds = PdfLayoutRectangle.Union(current.Select(static item => item.Bounds));
        float verticalGap = MathF.Max(0f, line.Bounds.Y - currentBounds.Bottom);
        float maximumFormulaGap = formulaClause ? lineStep * 7f : lineStep * 5f;
        if (verticalGap > maximumFormulaGap)
        {
            return false;
        }

        if (formulaClause || formulaClauseContinuation)
        {
            return true;
        }

        return IsFormulaContinuationLine(line);
    }

    private static bool IsFormulaContinuationLine(LineCandidate line)
    {
        return IsFormulaContinuationLine(line.Text, line.Bounds, line.Source.Runs);
    }

    private static bool IsFormulaContinuationLine(PdfSemanticLine line, float bodyFontSize)
    {
        return line.DominantFontSize <= bodyFontSize + 1f &&
            IsFormulaContinuationLine(line.Text, line.Bounds, line.Runs);
    }

    private static bool IsFormulaContinuationLine(
        string text,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfTextRun> runs)
    {
        bool compact = bounds.Width <= 120f || bounds.X >= 150f || text.Length <= 32;
        bool hasMathFont = runs.Any(static run => IsMathFont(run.FontName));
        return compact && hasMathFont;
    }

    private static bool HasLargeFormulaOperator(IReadOnlyList<PdfTextRun> runs)
    {
        return runs.Any(static run =>
            run.Text.IndexOfAny(['∑', '∏', '∫']) >= 0 ||
            NormalizeFontName(run.FontName).StartsWith("CMEX", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEquationNumberText(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length >= 3 &&
            trimmed[0] == '(' &&
            trimmed[^1] == ')' &&
            trimmed[1..^1].All(static character => char.IsDigit(character));
    }

    private static bool HasNumberedFormulaLine(PdfSemanticElement element)
    {
        return element.Lines.Any(static line =>
        {
            int open = line.Text.LastIndexOf("(", StringComparison.Ordinal);
            return open > 0 &&
                IsEquationNumberText(line.Text[open..]) &&
                HasFormulaOperator(line.Text[..open]);
        });
    }

    private static bool HasFormulaOperator(string text)
    {
        return text.IndexOfAny(['=', '∈', '×', '√', '∑', '∝', '·']) >= 0 ||
            HasFormulaFunction(text);
    }

    private static bool HasFormulaFunction(string text)
    {
        return
            text.Contains("Attention(", StringComparison.Ordinal) ||
            text.Contains("MultiHead(", StringComparison.Ordinal) ||
            text.Contains("Concat(", StringComparison.Ordinal) ||
            text.Contains("FFN(", StringComparison.Ordinal) ||
            text.Contains("PE", StringComparison.Ordinal);
    }

    private static bool StartsFormulaFunction(string text)
    {
        string trimmed = text.TrimStart();
        return
            trimmed.StartsWith("Attention(", StringComparison.Ordinal) ||
            trimmed.StartsWith("MultiHead(", StringComparison.Ordinal) ||
            trimmed.StartsWith("Concat(", StringComparison.Ordinal) ||
            trimmed.StartsWith("FFN(", StringComparison.Ordinal) ||
            trimmed.StartsWith("PE", StringComparison.Ordinal);
    }

    private static bool HasMathFont(LineCandidate line)
    {
        return line.Source.Runs.Any(static run => IsMathFont(run.FontName));
    }

    private static bool HasMathFont(PdfSemanticLine line)
    {
        return line.Runs.Any(static run => IsMathFont(run.FontName));
    }

    private static bool IsMathFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.StartsWith("CM", StringComparison.Ordinal) ||
            normalized.Contains("MSBM", StringComparison.Ordinal);
    }

    private static bool IsFormulaMathFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.StartsWith("CMMI", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMSY", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMEX", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMBSY", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("MSAM", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("MSBM", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("AMSA", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("AMSB", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldStartParagraph(
        LineCandidate previous,
        LineCandidate current,
        float lineStep,
        PdfSemanticExtractionOptions options)
    {
        if (previous.TableLaneIndex != current.TableLaneIndex &&
            (previous.TableLaneIndex.HasValue || current.TableLaneIndex.HasValue))
        {
            return true;
        }

        bool previousDirected = MathF.Abs(previous.Direction) > 0.01f;
        bool currentDirected = MathF.Abs(current.Direction) > 0.01f;
        if (previousDirected != currentDirected)
        {
            return true;
        }

        float gap = current.Bounds.Y - previous.Bounds.Y;
        if (IsFormulaContinuationLine(previous) &&
            IsFormulaContinuationLine(current) &&
            gap <= lineStep * 5f)
        {
            return false;
        }

        if ((HasMathFont(previous) || HasMathFont(current)) &&
            gap <= lineStep * 1.6f &&
            !StartsUppercase(current.Text))
        {
            return false;
        }

        if (gap > lineStep * options.ParagraphGapMultiplier)
        {
            return true;
        }

        if (gap > lineStep * 1.15f && EndsSentence(previous.Text) && StartsUppercase(current.Text))
        {
            return true;
        }

        return gap > lineStep * 0.85f && MathF.Abs(current.Bounds.X - previous.Bounds.X) > 22f;
    }

    private static bool IsSameAuthorBand(AuthorSegment segment, AuthorCluster cluster)
    {
        float yDelta = segment.Bounds.Y - cluster.Anchor.Bounds.Y;
        return yDelta >= -36f && yDelta <= 5f;
    }

    private static float HorizontalGap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        if (first.Right < second.X)
        {
            return second.X - first.Right;
        }

        if (second.Right < first.X)
        {
            return first.X - second.Right;
        }

        return 0f;
    }

    private static PdfSemanticElement CreateParagraph(
        IReadOnlyList<LineCandidate> lines,
        HashSet<int> consumed,
        PdfSemanticExtractionOptions options)
    {
        foreach (LineCandidate line in lines)
        {
            consumed.Add(line.Index);
        }

        LineCandidate[] readingLines = OrderLinesForReading(lines);
        PdfSemanticLine[] semanticLines = DetectInlineCode(
            MergeDisplayFormulaSourceLines(readingLines, options));
        return new PdfSemanticElement(
            PdfSemanticElementKind.Paragraph,
            JoinParagraphLines(semanticLines),
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            semanticLines);
    }

    private static PdfSemanticLine[] MergeDisplayFormulaSourceLines(
        IReadOnlyList<LineCandidate> lines,
        PdfSemanticExtractionOptions options)
    {
        LineCandidate[] anchors = lines
            .Where(line => HasFormulaOperator(line.Text) &&
                IsMathDominantFormulaLine(line.Text, line.Bounds, line.Source.Runs))
            .ToArray();
        if (anchors.Length != 1)
        {
            return lines.Select(static line => line.SemanticLine).ToArray();
        }

        LineCandidate anchor = anchors[0];
        LineCandidate[] attachments = lines
            .Where(line => !ReferenceEquals(line, anchor) && IsFormulaLineAttachment(line, anchor))
            .ToArray();
        if (attachments.Length == 0)
        {
            return lines.Select(static line => line.SemanticLine).ToArray();
        }

        HashSet<LineCandidate> mergedLines = attachments.Append(anchor).ToHashSet();
        PdfTextRun[] runs = mergedLines
            .SelectMany(static line => line.Source.Runs)
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .ToArray();
        (string fontName, float fontSize, float direction, PdfLayoutColor color) = DominantStyle(runs);
        PdfSemanticLine merged = new(
            ReconstructText(runs.SelectMany(static run => run.Glyphs), options),
            PdfLayoutRectangle.Union(mergedLines.Select(static line => line.Bounds)),
            fontName,
            fontSize,
            direction,
            color,
            runs);
        return OrderLinesForReading(lines
            .Where(line => !mergedLines.Contains(line))
            .Select(static line => line.SemanticLine)
            .Append(merged));
    }

    private static PdfSemanticLine[] DetectInlineCode(IReadOnlyList<PdfSemanticLine> lines)
    {
        int proseCharacters = lines
            .SelectMany(static line => line.Runs)
            .Where(static run => !IsMonospacedFontName(run.FontName) && !IsMathFont(run.FontName))
            .Sum(static run => run.Text.Count(static character => !char.IsWhiteSpace(character)));
        if (proseCharacters < 8)
        {
            return lines.ToArray();
        }

        return lines.Select(DetectInlineCode).ToArray();
    }

    private static PdfSemanticLine DetectInlineCode(PdfSemanticLine line)
    {
        PdfTextRun[] runs = line.Runs
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .ToArray();
        int proseCharacters = runs
            .Where(static run => !IsMonospacedFontName(run.FontName) && !IsMathFont(run.FontName))
            .Sum(static run => run.Text.Count(static character => !char.IsWhiteSpace(character)));
        if (proseCharacters < 8)
        {
            return line;
        }

        List<PdfSemanticInlineCode> inlineCode = [];
        for (int index = 0; index < runs.Length; index++)
        {
            PdfTextRun run = runs[index];
            if (!IsMonospacedFontName(run.FontName) ||
                IsCodeIncompatibleMathFontName(run.FontName) ||
                !TryEstimateStableCharacterPitch(run.Glyphs, out _))
            {
                continue;
            }

            List<PdfTextRun> codeRuns = [run];
            while (index + 1 < runs.Length &&
                IsMonospacedFontName(runs[index + 1].FontName) &&
                !IsCodeIncompatibleMathFontName(runs[index + 1].FontName) &&
                string.Equals(
                    NormalizeFontName(run.FontName),
                    NormalizeFontName(runs[index + 1].FontName),
                    StringComparison.Ordinal) &&
                HorizontalGap(codeRuns[^1].Bounds, runs[index + 1].Bounds) <= MathF.Max(1f, run.FontSize * 0.25f))
            {
                codeRuns.Add(runs[++index]);
            }

            string text = ReconstructText(codeRuns.SelectMany(static codeRun => codeRun.Glyphs));
            bool hasProseBefore = runs.Take(index - codeRuns.Count + 1)
                .Any(static candidate => !IsMonospacedFontName(candidate.FontName) && !string.IsNullOrWhiteSpace(candidate.Text));
            bool hasProseAfter = runs.Skip(index + 1)
                .Any(static candidate => !IsMonospacedFontName(candidate.FontName) && !string.IsNullOrWhiteSpace(candidate.Text));
            if ((hasProseBefore || hasProseAfter) && LooksLikeInlineCode(text))
            {
                inlineCode.Add(new PdfSemanticInlineCode(
                    text,
                    PdfLayoutRectangle.Union(codeRuns.Select(static codeRun => codeRun.Bounds)),
                    codeRuns));
            }
        }

        return inlineCode.Count == 0
            ? line
            : new PdfSemanticLine(
                line.Text,
                line.Bounds,
                line.DominantFontName,
                line.DominantFontSize,
                line.Direction,
                line.Color,
                line.Runs,
                inlineCode);
    }

    private static bool LooksLikeInlineCode(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length is >= 2 and <= 64 &&
            !trimmed.Contains('@', StringComparison.Ordinal) &&
            !trimmed.Any(char.IsWhiteSpace) &&
            InlineCodePattern.IsMatch(trimmed);
    }

    private static LineCandidate[] OrderLinesForReading(IReadOnlyList<LineCandidate> lines)
    {
        List<LineRow> rows = [];
        foreach (LineCandidate line in lines.OrderBy(static line => line.Bounds.Y).ThenBy(static line => line.Bounds.X))
        {
            LineRow? row = rows.FirstOrDefault(row => row.Contains(line));
            if (row == null)
            {
                rows.Add(new LineRow(line));
            }
            else
            {
                row.Add(line);
            }
        }

        return rows
            .OrderBy(static row => row.Bounds.Y)
            .ThenBy(static row => row.Bounds.X)
            .SelectMany(static row => row.Lines
                .OrderBy(static line => line.Bounds.X)
                .ThenBy(static line => line.Bounds.Y))
            .ToArray();
    }

    private static PdfSemanticLine[] OrderLinesForReading(IEnumerable<PdfSemanticLine> lines)
    {
        List<SemanticLineRow> rows = [];
        foreach (PdfSemanticLine line in lines.OrderBy(static line => line.Bounds.Y).ThenBy(static line => line.Bounds.X))
        {
            SemanticLineRow? row = rows.FirstOrDefault(row => row.Contains(line));
            if (row == null)
            {
                rows.Add(new SemanticLineRow(line));
            }
            else
            {
                row.Add(line);
            }
        }

        return rows
            .OrderBy(static row => row.Bounds.Y)
            .ThenBy(static row => row.Bounds.X)
            .SelectMany(static row => row.Lines
                .OrderBy(static line => line.Bounds.X)
                .ThenBy(static line => line.Bounds.Y))
            .ToArray();
    }

    private static PdfSemanticElement CreateElement(
        PdfSemanticElementKind kind,
        IReadOnlyList<LineCandidate> lines,
        int headingLevel = 0,
        bool isDocumentTitle = false)
    {
        PdfSemanticLine[] semanticLines = lines.Select(static line => line.SemanticLine).ToArray();
        string text = kind == PdfSemanticElementKind.Paragraph || kind == PdfSemanticElementKind.Footnote
            ? JoinParagraphLines(semanticLines)
            : string.Join(Environment.NewLine, semanticLines.Select(static line => line.Text));
        return new PdfSemanticElement(
            kind,
            text,
            PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
            semanticLines,
            headingLevel,
            isDocumentTitle: isDocumentTitle);
    }

    private static PdfSemanticLine CreateSyntheticLine(string text, IReadOnlyList<AuthorSegment> segments)
    {
        (string fontName, float fontSize, float direction, PdfLayoutColor color) = segments
            .GroupBy(static segment => (
                NormalizeFontName(segment.Run.FontName),
                MathF.Round(segment.Run.FontSize * 2f) / 2f,
                MathF.Round(segment.Run.Direction),
                ColorKey(segment.Run.Color)))
            .Select(static group => new
            {
                group.Key,
                Weight = group.Sum(static segment => Math.Max(1, segment.Text.Length))
            })
            .OrderByDescending(static item => item.Weight)
            .ThenByDescending(static item => item.Key.Item2)
            .Select(static item => (item.Key.Item1, item.Key.Item2, item.Key.Item3, item.Key.Item4.Color))
            .First();

        return new PdfSemanticLine(
            NormalizeText(text),
            PdfLayoutRectangle.Union(segments.Select(static segment => segment.Bounds)),
            fontName,
            fontSize,
            direction,
            color,
            segments.Select(static segment => segment.Run).ToArray());
    }

    private static string JoinParagraphLines(IEnumerable<PdfSemanticLine> lines)
    {
        PdfSemanticLine[] sourceLines = lines
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
        if (sourceLines.Length == 0)
        {
            return "";
        }

        PdfLayoutRectangle paragraphBounds = PdfLayoutRectangle.Union(
            sourceLines.Select(static line => line.Bounds));
        StringBuilder text = new();
        PdfSemanticLine? previousLine = null;
        foreach (PdfSemanticLine line in sourceLines)
        {
            string value = line.Text.Trim();
            if (text.Length == 0)
            {
                text.Append(value);
                previousLine = line;
                continue;
            }

            bool continuesHyphenatedWord = text[^1] == '-' &&
                value.Length > 0 &&
                char.IsLower(value[0]);
            if (previousLine != null &&
                continuesHyphenatedWord &&
                ShouldRemoveDiscretionaryLineBreakHyphen(previousLine, line, paragraphBounds, text, value))
            {
                text.Length--;
                text.Append(value);
            }
            else if (continuesHyphenatedWord)
            {
                text.Append(value);
            }
            else
            {
                text.Append(' ');
                text.Append(value);
            }

            previousLine = line;
        }

        return NormalizeText(text.ToString());
    }

    private static bool ShouldRemoveDiscretionaryLineBreakHyphen(
        PdfSemanticLine previous,
        PdfSemanticLine current,
        PdfLayoutRectangle paragraphBounds,
        StringBuilder accumulatedText,
        string currentText)
    {
        if (accumulatedText.Length == 0 ||
            accumulatedText[^1] != '-' ||
            currentText.Length == 0 ||
            !char.IsLower(currentText[0]))
        {
            return false;
        }

        float fontSize = MathF.Max(1f, MathF.Min(previous.DominantFontSize, current.DominantFontSize));
        float edgeTolerance = MathF.Max(fontSize * 1.75f, paragraphBounds.Width * 0.08f);
        bool endsAtTextEdge = paragraphBounds.Right - previous.Bounds.Right <= edgeTolerance;
        bool resumesAtTextEdge = current.Bounds.X - paragraphBounds.X <= edgeTolerance;
        if (!endsAtTextEdge || !resumesAtTextEdge)
        {
            return false;
        }

        int leftStart = accumulatedText.Length - 1;
        while (leftStart > 0 && char.IsLetter(accumulatedText[leftStart - 1]))
        {
            leftStart--;
        }

        int rightLength = 0;
        while (rightLength < currentText.Length && char.IsLetter(currentText[rightLength]))
        {
            rightLength++;
        }

        return accumulatedText.Length - 1 - leftStart >= 2 && rightLength >= 2;
    }

    /// <summary>
    /// Reconstructs readable text from positioned glyphs, including word boundaries represented only by PDF spacing.
    /// </summary>
    /// <param name="glyphSource">The glyphs to reconstruct in visual reading order.</param>
    /// <param name="options">Optional thresholds used to infer omitted word boundaries.</param>
    /// <returns>Normalized text with inferred word boundaries.</returns>
    public static string ReconstructText(
        IEnumerable<PdfTextGlyph> glyphSource,
        PdfSemanticExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(glyphSource);
        options ??= new PdfSemanticExtractionOptions();
        PdfTextGlyph[] visualGlyphs = glyphSource
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ThenBy(static glyph => glyph.Bounds.Y)
            .ToArray();
        if (visualGlyphs.Length == 0)
        {
            return "";
        }

        IReadOnlyList<PdfTextGlyph> glyphs = OrderGlyphsForLogicalText(visualGlyphs);

        StringBuilder text = new();
        PdfTextGlyph? previous = null;
        for (int index = 0; index < glyphs.Count; index++)
        {
            PdfTextGlyph glyph = glyphs[index];
            if (IsIntrusiveRadicalGlyph(glyphs, index))
            {
                continue;
            }

            if (previous != null && IsWordBoundaryBetween(previous, glyph, options))
            {
                AppendSpaceIfNeeded(text);
            }

            if (string.IsNullOrWhiteSpace(glyph.Text))
            {
                AppendSpaceIfNeeded(text);
            }
            else
            {
                text.Append(glyph.Text);
            }

            previous = glyph;
        }

        return NormalizeText(text.ToString());
    }

    private static bool IsIntrusiveRadicalGlyph(IReadOnlyList<PdfTextGlyph> glyphs, int index)
    {
        PdfTextGlyph glyph = glyphs[index];
        if (glyph.Text != "√" || !IsMathFont(glyph.FontName))
        {
            return false;
        }

        PdfTextGlyph? previous = NearestTextGlyph(glyphs, index, -1);
        PdfTextGlyph? next = NearestTextGlyph(glyphs, index, 1);
        if (previous == null ||
            next == null ||
            IsMathFont(previous.FontName) ||
            IsMathFont(next.FontName) ||
            next.Bounds.X >= glyph.Bounds.Right)
        {
            return false;
        }

        float directGap = next.Bounds.X - previous.Bounds.Right;
        float fontSize = MathF.Max(1f, MathF.Min(previous.FontSize, next.FontSize));
        return directGap >= -fontSize * 0.1f && directGap <= fontSize * 0.5f;
    }

    private static PdfTextGlyph? NearestTextGlyph(
        IReadOnlyList<PdfTextGlyph> glyphs,
        int index,
        int step)
    {
        for (int candidate = index + step; candidate >= 0 && candidate < glyphs.Count; candidate += step)
        {
            if (!string.IsNullOrWhiteSpace(glyphs[candidate].Text))
            {
                return glyphs[candidate];
            }
        }

        return null;
    }

    /// <summary>
    /// Converts horizontal glyphs collected in page-left-to-right visual order into Unicode logical order.
    /// RTL directional runs are reversed while Latin and numeric runs retain their internal order.
    /// </summary>
    public static IReadOnlyList<PdfTextGlyph> OrderGlyphsForLogicalText(IEnumerable<PdfTextGlyph> glyphSource)
    {
        ArgumentNullException.ThrowIfNull(glyphSource);
        PdfTextGlyph[] visualGlyphs = glyphSource
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ThenBy(static glyph => glyph.Bounds.Y)
            .ToArray();
        if (visualGlyphs.Length < 2 ||
            PdfTextDirectionDetector.Detect(string.Concat(visualGlyphs.Select(static glyph => glyph.Text))) != PdfTextDirection.RightToLeft)
        {
            return visualGlyphs;
        }

        PdfTextDirection[] directions = ResolveGlyphDirections(visualGlyphs);
        List<IReadOnlyList<PdfTextGlyph>> directionalRuns = [];
        int runStart = 0;
        for (int index = 1; index <= visualGlyphs.Length; index++)
        {
            if (index < visualGlyphs.Length && directions[index] == directions[runStart])
            {
                continue;
            }

            PdfTextGlyph[] run = visualGlyphs[runStart..index];
            directionalRuns.Add(directions[runStart] == PdfTextDirection.RightToLeft
                ? run.Reverse().ToArray()
                : run);
            runStart = index;
        }

        directionalRuns.Reverse();
        return directionalRuns.SelectMany(static run => run).ToArray();
    }

    private static PdfTextDirection[] ResolveGlyphDirections(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        PdfTextDirection[] directions = glyphs
            .Select(static glyph => PdfTextDirectionDetector.DirectionOf(glyph.Text))
            .ToArray();
        for (int index = 0; index < directions.Length; index++)
        {
            if (directions[index] != PdfTextDirection.Neutral)
            {
                continue;
            }

            PdfTextDirection before = NearestStrongDirection(directions, index, -1);
            PdfTextDirection after = NearestStrongDirection(directions, index, 1);
            directions[index] = before == after && before != PdfTextDirection.Neutral
                ? before
                : after != PdfTextDirection.Neutral
                    ? after
                    : before != PdfTextDirection.Neutral
                        ? before
                        : PdfTextDirection.RightToLeft;
        }

        return directions;
    }

    private static PdfTextDirection NearestStrongDirection(
        IReadOnlyList<PdfTextDirection> directions,
        int start,
        int step)
    {
        for (int index = start + step; index >= 0 && index < directions.Count; index += step)
        {
            if (directions[index] != PdfTextDirection.Neutral)
            {
                return directions[index];
            }
        }

        return PdfTextDirection.Neutral;
    }

    /// <summary>
    /// Determines whether adjacent positioned glyphs have a word boundary, including boundaries encoded as spacing.
    /// </summary>
    public static bool IsWordBoundaryBetween(
        PdfTextGlyph previous,
        PdfTextGlyph glyph,
        PdfSemanticExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(glyph);
        options ??= new PdfSemanticExtractionOptions();

        if (string.IsNullOrWhiteSpace(previous.Text) || string.IsNullOrWhiteSpace(glyph.Text))
        {
            return true;
        }

        bool rightToLeftPair = PdfTextDirectionDetector.DirectionOf(previous.Text) == PdfTextDirection.RightToLeft &&
            PdfTextDirectionDetector.DirectionOf(glyph.Text) == PdfTextDirection.RightToLeft;
        if (glyph.Bounds.X <= previous.Bounds.X && !rightToLeftPair)
        {
            return false;
        }

        string previousText = previous.Text;
        string currentText = glyph.Text;
        if (previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        if (NoSpaceBefore(currentText[0]) || NoSpaceAfter(previousText[^1]))
        {
            return false;
        }

        float gap = rightToLeftPair
            ? previous.Bounds.X - glyph.Bounds.Right
            : glyph.Bounds.X - previous.Bounds.Right;
        float threshold = MathF.Max(
            options.MinimumWordGap,
            MathF.Min(previous.FontSize, glyph.FontSize) * options.WordGapFontSizeMultiplier);
        return gap > threshold;
    }

    private static void AppendSpaceIfNeeded(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != ' ')
        {
            text.Append(' ');
        }
    }

    private static string NormalizeText(string text)
    {
        string normalized = WhitespacePattern.Replace(text.Trim(), " ");
        normalized = Regex.Replace(normalized, @"\s+([,.;:!?\]\)})])", "$1");
        normalized = Regex.Replace(normalized, @"([\[\(({])\s+", "$1");
        normalized = Regex.Replace(normalized, @"\s+([’'])", "$1");
        normalized = Regex.Replace(normalized, @"([“""])\s+", "$1");
        normalized = Regex.Replace(normalized, @"\s+([”""])", "$1");
        return normalized;
    }

    private static bool NoSpaceBefore(char character)
    {
        return character is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '\'' or '’';
    }

    private static bool NoSpaceAfter(char character)
    {
        return character is '(' or '[' or '{' or '\'' or '‘';
    }

    private static bool IsFootnoteMarker(string text)
    {
        return FootnoteMarkerPattern.IsMatch(text.Trim());
    }

    private static bool IsFootnoteMarkerLine(LineCandidate line, PdfLayoutPage page)
    {
        if (IsSymbolFootnoteMarker(line.Text))
        {
            return true;
        }

        return IsNumericFootnoteMarker(line.Text) && line.Bounds.X <= page.Width * 0.25f;
    }

    private static bool IsSymbolFootnoteMarker(string text)
    {
        return SymbolFootnoteMarkerPattern.IsMatch(text.Trim());
    }

    private static bool IsNumericFootnoteMarker(string text)
    {
        return NumericFootnoteMarkerPattern.IsMatch(text.Trim());
    }

    private static bool EndsSentence(string text)
    {
        return text.TrimEnd().LastOrDefault() is '.' or '?' or '!';
    }

    private static bool StartsUppercase(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.Length > 0 && char.IsUpper(trimmed[0]);
    }

    private static bool IsBoldFontName(string fontName)
    {
        return fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Medi", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("CMBX", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFontName(string fontName)
    {
        int subsetSeparator = fontName.IndexOf('+', StringComparison.Ordinal);
        return subsetSeparator >= 0 && subsetSeparator + 1 < fontName.Length
            ? fontName[(subsetSeparator + 1)..]
            : fontName;
    }

    private sealed class LineRow
    {
        private readonly List<LineCandidate> _lines;

        public LineRow(LineCandidate line)
        {
            _lines = [line];
            Bounds = line.Bounds;
        }

        public IReadOnlyList<LineCandidate> Lines => _lines;

        public PdfLayoutRectangle Bounds { get; private set; }

        public bool Contains(LineCandidate line)
        {
            if (_lines[0].TableLaneIndex != line.TableLaneIndex)
            {
                return false;
            }

            float overlap = MathF.Min(Bounds.Bottom, line.Bounds.Bottom) - MathF.Max(Bounds.Y, line.Bounds.Y);
            if (overlap >= MathF.Min(Bounds.Height, line.Bounds.Height) * 0.35f)
            {
                return true;
            }

            float centerDistance = MathF.Abs(
                Bounds.Y + (Bounds.Height / 2f) - (line.Bounds.Y + (line.Bounds.Height / 2f)));
            return centerDistance <= MathF.Max(2.5f, MathF.Max(Bounds.Height, line.Bounds.Height) * 0.55f);
        }

        public void Add(LineCandidate line)
        {
            _lines.Add(line);
            Bounds = PdfLayoutRectangle.Union([Bounds, line.Bounds]);
        }
    }

    private sealed class SemanticLineRow
    {
        private readonly List<PdfSemanticLine> _lines;

        public SemanticLineRow(PdfSemanticLine line)
        {
            _lines = [line];
            Bounds = line.Bounds;
        }

        public IReadOnlyList<PdfSemanticLine> Lines => _lines;

        public PdfLayoutRectangle Bounds { get; private set; }

        public bool Contains(PdfSemanticLine line)
        {
            float overlap = MathF.Min(Bounds.Bottom, line.Bounds.Bottom) - MathF.Max(Bounds.Y, line.Bounds.Y);
            if (overlap >= MathF.Min(Bounds.Height, line.Bounds.Height) * 0.35f)
            {
                return true;
            }

            float centerDistance = MathF.Abs(
                Bounds.Y + (Bounds.Height / 2f) - (line.Bounds.Y + (line.Bounds.Height / 2f)));
            return centerDistance <= MathF.Max(2.5f, MathF.Max(Bounds.Height, line.Bounds.Height) * 0.55f);
        }

        public void Add(PdfSemanticLine line)
        {
            _lines.Add(line);
            Bounds = PdfLayoutRectangle.Union([Bounds, line.Bounds]);
        }
    }

    private sealed class LineCandidate
    {
        public LineCandidate(
            int index,
            PdfTextLine source,
            PdfSemanticLine semanticLine,
            string fontName,
            float fontSize,
            float direction,
            PdfLayoutColor color,
            int? tableLaneIndex = null)
        {
            Index = index;
            Source = source;
            SemanticLine = semanticLine;
            FontName = fontName;
            FontSize = fontSize;
            Direction = direction;
            Color = color;
            TableLaneIndex = tableLaneIndex;
        }

        public int Index { get; }

        public PdfTextLine Source { get; }

        public PdfSemanticLine SemanticLine { get; }

        public string FontName { get; }

        public float FontSize { get; }

        public float Direction { get; }

        public PdfLayoutColor Color { get; }

        public int? TableLaneIndex { get; }

        public string Text => SemanticLine.Text;

        public PdfLayoutRectangle Bounds => SemanticLine.Bounds;

        public float CenterX => Bounds.X + Bounds.Width / 2f;

        public bool IsBold =>
            IsBoldFontName(FontName);
    }

    private sealed record CodeLineEvidence(
        LineCandidate Line,
        string FontName,
        float CharacterPitch);

    private sealed record CodeBlockCandidate(
        IReadOnlyList<CodeLineEvidence> Lines,
        string Text,
        float CharacterPitch,
        float LineStep);

    private sealed record AlgorithmCandidate(
        AlgorithmRuleCandidate TopRule,
        AlgorithmRuleCandidate CaptionRule,
        AlgorithmRuleCandidate BottomRule,
        IReadOnlyList<LineCandidate> CaptionLines,
        IReadOnlyList<LineCandidate> Rows,
        IReadOnlyList<LineCandidate> SourceLines);

    private readonly record struct AlgorithmRuleCandidate(
        int SourcePathIndex,
        PdfLayoutRectangle Bounds,
        float Thickness,
        PdfLayoutColor Color);

    private sealed class RawDocumentIndexItem
    {
        public RawDocumentIndexItem(
            LineCandidate line,
            string label,
            string pageLabel,
            float anchorX,
            float pageRight,
            PdfLayoutLink? link)
        {
            Line = line;
            Label = label;
            PageLabel = pageLabel;
            AnchorX = anchorX;
            PageRight = pageRight;
            Link = link;
        }

        public LineCandidate Line { get; }

        public string Label { get; }

        public string PageLabel { get; }

        public float AnchorX { get; }

        public float PageRight { get; }

        public PdfLayoutLink? Link { get; }

        public int Level { get; set; }

        public List<RawDocumentIndexItem> Children { get; } = [];
    }

    private sealed class RawListItem
    {
        public RawListItem(ListMarkerCandidate marker, LineCandidate line)
        {
            Marker = marker;
            Lines = [line];
        }

        public ListMarkerCandidate Marker { get; }

        public List<LineCandidate> Lines { get; }

        public int Level { get; set; }
    }

    private sealed class ListMarkerCandidate
    {
        public ListMarkerCandidate(
            LineCandidate line,
            string marker,
            int markerLength,
            ListMarkerCategory category,
            ListMarkerShape shape,
            string token,
            int? decimalValue,
            int? alphaValue,
            int? romanValue,
            bool isUpperCase,
            float bodyX)
        {
            Line = line;
            Marker = marker;
            MarkerLength = markerLength;
            Category = category;
            Shape = shape;
            Token = token;
            DecimalValue = decimalValue;
            AlphaValue = alphaValue;
            RomanValue = romanValue;
            IsUpperCase = isUpperCase;
            BodyX = bodyX;
        }

        public LineCandidate Line { get; }

        public string Marker { get; }

        public int MarkerLength { get; }

        public ListMarkerCategory Category { get; }

        public ListMarkerShape Shape { get; }

        public string Token { get; }

        public int? DecimalValue { get; }

        public int? AlphaValue { get; }

        public int? RomanValue { get; }

        public bool IsUpperCase { get; }

        public float MarkerX => Line.Bounds.X;

        public float BodyX { get; }
    }

    private readonly record struct ResolvedListStyle(
        PdfSemanticListKind Kind,
        PdfSemanticListMarkerKind MarkerKind,
        IReadOnlyList<int> Ordinals,
        bool IsReversed);

    private enum ListMarkerCategory
    {
        Bullet,
        Hyphen,
        Ordered
    }

    private enum ListMarkerShape
    {
        Symbol,
        Period,
        ClosingParenthesis,
        Parenthesized
    }

    private sealed class AuthorSegment
    {
        public AuthorSegment(LineCandidate line, PdfTextRun run, string text, PdfLayoutRectangle bounds, float centerX)
        {
            Line = line;
            Run = run;
            Text = text;
            Bounds = bounds;
            CenterX = centerX;
        }

        public LineCandidate Line { get; }

        public PdfTextRun Run { get; }

        public string Text { get; }

        public PdfLayoutRectangle Bounds { get; }

        public float CenterX { get; }
    }

    private sealed class AuthorCluster
    {
        private readonly List<AuthorSegment> _segments;

        public AuthorCluster(AuthorSegment anchor)
        {
            Anchor = anchor;
            _segments = [anchor];
        }

        public AuthorSegment Anchor { get; }

        public IReadOnlyList<AuthorSegment> Segments => _segments;

        public PdfLayoutRectangle Bounds => PdfLayoutRectangle.Union(_segments.Select(static segment => segment.Bounds));

        public void Add(AuthorSegment segment)
        {
            _segments.Add(segment);
        }
    }

    private sealed class DefinitionSourceRow
    {
        public DefinitionSourceRow(
            IReadOnlyList<LineCandidate> lines,
            IReadOnlyList<PdfTextRun> runs,
            PdfSemanticLine semanticLine)
        {
            Lines = lines.ToArray();
            Runs = runs.ToArray();
            SemanticLine = semanticLine;
        }

        public IReadOnlyList<LineCandidate> Lines { get; }

        public IReadOnlyList<PdfTextRun> Runs { get; }

        public PdfSemanticLine SemanticLine { get; }

        public PdfLayoutRectangle Bounds => SemanticLine.Bounds;
    }

    private sealed class DefinitionSourceEntry
    {
        private readonly List<PdfSemanticDefinitionTerm> _terms;
        private readonly List<PdfSemanticLine> _definitionLines;
        private readonly List<LineCandidate> _sourceLines;

        public DefinitionSourceEntry(
            IReadOnlyList<PdfSemanticDefinitionTerm> terms,
            IEnumerable<PdfSemanticLine> definitionLines,
            IEnumerable<LineCandidate> sourceLines,
            DefinitionSourceKind kind,
            float termLeft,
            float definitionLeft)
        {
            _terms = terms.ToList();
            _definitionLines = definitionLines.ToList();
            _sourceLines = sourceLines.Distinct().ToList();
            Kind = kind;
            TermLeft = termLeft;
            DefinitionLeft = definitionLeft;
        }

        public IReadOnlyList<PdfSemanticDefinitionTerm> Terms => _terms;

        public IReadOnlyList<PdfSemanticLine> DefinitionLines => _definitionLines;

        public IReadOnlyList<LineCandidate> SourceLines => _sourceLines;

        public DefinitionSourceKind Kind { get; }

        public float TermLeft { get; }

        public float DefinitionLeft { get; }

        public PdfLayoutRectangle Bounds => PdfLayoutRectangle.Union(
            Terms.Select(static term => term.Bounds).Concat(_definitionLines.Select(static line => line.Bounds)));

        public void AddDefinitionLine(PdfSemanticLine line, IEnumerable<LineCandidate> sourceLines)
        {
            _definitionLines.Add(line);
            foreach (LineCandidate sourceLine in sourceLines)
            {
                if (!_sourceLines.Contains(sourceLine))
                {
                    _sourceLines.Add(sourceLine);
                }
            }
        }

        public void AddTermsAndDefinition(
            IEnumerable<PdfSemanticDefinitionTerm> terms,
            IEnumerable<PdfSemanticLine> definitionLines,
            IEnumerable<LineCandidate> sourceLines)
        {
            _terms.AddRange(terms);
            _definitionLines.AddRange(definitionLines);
            foreach (LineCandidate sourceLine in sourceLines)
            {
                if (!_sourceLines.Contains(sourceLine))
                {
                    _sourceLines.Add(sourceLine);
                }
            }
        }

        public PdfSemanticDefinitionListEntry ToSemanticEntry()
        {
            PdfSemanticLine[] lines = OrderLinesForReading(_definitionLines);
            return new PdfSemanticDefinitionListEntry(
                Terms,
                new PdfSemanticDefinitionContent(
                    JoinParagraphLines(lines),
                    PdfLayoutRectangle.Union(lines.Select(static line => line.Bounds)),
                    lines));
        }
    }

    private enum DefinitionSourceKind
    {
        Inline,
        Columns,
        Stacked
    }

    private sealed class RuledTableRegion
    {
        public RuledTableRegion(
            PdfLayoutRectangle bounds,
            IReadOnlyList<float> rowBoundaries,
            IReadOnlyList<float> columnBoundaries,
            IReadOnlyList<TableRule> rules)
        {
            Bounds = bounds;
            RowBoundaries = rowBoundaries.ToArray();
            ColumnBoundaries = columnBoundaries.ToArray();
            Rules = rules.ToArray();
        }

        public PdfLayoutRectangle Bounds { get; }

        public IReadOnlyList<float> RowBoundaries { get; }

        public IReadOnlyList<float> ColumnBoundaries { get; }

        public IReadOnlyList<TableRule> Rules { get; }
    }

    private readonly record struct HorizontalTableLane(
        PdfLayoutRectangle RuleBounds,
        PdfLayoutRectangle ExpandedBounds);

    private readonly record struct TableCandidateRegion(
        PdfLayoutRectangle Bounds,
        int? TableLaneIndex);

    private sealed class TableSourceRow
    {
        public TableSourceRow(IReadOnlyList<LineCandidate> lines, IReadOnlyList<TableSourceCell> cells)
        {
            Lines = lines.ToArray();
            Cells = cells.ToArray();
            Bounds = PdfLayoutRectangle.Union(Lines.Select(static line => line.Bounds));
            Text = string.Join(" ", Cells.Select(static cell => cell.Text));
            TableLaneIndex = Lines.Select(static line => line.TableLaneIndex).Distinct().SingleOrDefault();
        }

        public IReadOnlyList<LineCandidate> Lines { get; }

        public IReadOnlyList<TableSourceCell> Cells { get; }

        public PdfLayoutRectangle Bounds { get; }

        public string Text { get; }

        public int? TableLaneIndex { get; }
    }

    private sealed class TableSourceCell
    {
        public TableSourceCell(string text, PdfLayoutRectangle bounds, IReadOnlyList<PdfTextRun> runs)
        {
            Text = text;
            Bounds = bounds;
            Runs = runs.ToArray();
        }

        public string Text { get; }

        public PdfLayoutRectangle Bounds { get; }

        public IReadOnlyList<PdfTextRun> Runs { get; }

        public float CenterX => Bounds.X + Bounds.Width / 2f;
    }

    private sealed class MutableCellBorders
    {
        public bool Top { get; set; }

        public bool Right { get; set; }

        public bool Bottom { get; set; }

        public bool Left { get; set; }
    }

    private readonly record struct ThematicRuleCandidate(
        PdfLayoutRectangle Bounds,
        float Thickness,
        PdfLayoutColor Color);

    private readonly record struct TableCaptionCandidate(
        PdfSemanticElement Element,
        string Number,
        IReadOnlyList<PdfSemanticElement> SourceElements);

    private readonly record struct TableCaptionAssociation(
        PdfSemanticElement Table,
        TableCaptionCandidate Caption,
        PdfSemanticTableCaptionPosition Position,
        float Score);

    private readonly record struct TableRule(TableRuleOrientation Orientation, PdfLayoutRectangle Bounds);

    private enum TableRuleOrientation
    {
        Horizontal,
        Vertical
    }

    private readonly record struct ColorKeyValue(
        float Red,
        float Green,
        float Blue,
        float Alpha,
        string? ColorSpaceName,
        PdfLayoutColor Color);
}

internal static class PdfSemanticEnumerableExtensions
{
    public static IEnumerable<TResult> Pairwise<TSource, TResult>(
        this IEnumerable<TSource> source,
        Func<TSource, TSource, TResult> selector)
    {
        using IEnumerator<TSource> enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            yield break;
        }

        TSource previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            TSource current = enumerator.Current;
            yield return selector(previous, current);
            previous = current;
        }
    }
}
