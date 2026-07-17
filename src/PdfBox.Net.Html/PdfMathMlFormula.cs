using System.Net;
using System.Text;
using PdfBox.Net.Layout;

namespace PdfBox.Net.Html;

internal sealed class PdfMathMlFormula
{
    private readonly MathNode _root;

    private PdfMathMlFormula(
        MathNode root,
        string? equationNumber,
        float fontSize,
        IReadOnlyList<PdfTextGlyph> equationNumberGlyphs,
        IReadOnlyList<PdfTextGlyph> claimedGlyphs)
    {
        _root = root;
        EquationNumber = equationNumber;
        FontSize = fontSize;
        EquationNumberGlyphs = equationNumberGlyphs;
        ClaimedGlyphs = claimedGlyphs;
        AccessibleText = root.Text;
    }

    public string AccessibleText { get; }

    public string? EquationNumber { get; }

    public IReadOnlyList<PdfTextGlyph> EquationNumberGlyphs { get; }

    public float FontSize { get; }

    public IReadOnlyList<PdfTextGlyph> ClaimedGlyphs { get; }

    public static bool IsEligibleGlyph(PdfTextGlyph glyph)
    {
        return glyph.IsPainted &&
            glyph.FontSize >= 1f &&
            glyph.Bounds.Width >= 0.05f &&
            glyph.Bounds.Height >= 0.05f &&
            !string.IsNullOrWhiteSpace(glyph.Text) &&
            !glyph.Text.Any(static character => char.IsControl(character));
    }

    public static bool TryCreate(
        IReadOnlyList<PdfTextGlyph> glyphSource,
        IReadOnlyList<PdfLayoutPath> paths,
        out PdfMathMlFormula? formula)
    {
        return TryCreate(glyphSource, paths, null, out formula);
    }

    public static bool TryCreate(
        IReadOnlyList<PdfTextGlyph> glyphSource,
        IReadOnlyList<PdfLayoutPath> paths,
        float? baselineBottomHint,
        out PdfMathMlFormula? formula)
    {
        formula = null;
        PdfTextGlyph[] glyphs = glyphSource
            .Where(IsEligibleGlyph)
            .ToArray();
        if (glyphs.Length is < 3 or > 180)
        {
            return false;
        }

        PdfTextGlyph? unsupported = glyphs.FirstOrDefault(static glyph => !IsSupportedText(glyph.Text));
        if (unsupported != null)
        {
            return false;
        }

        float fontSize = DominantFontSize(glyphs);
        if (HasProseLikeBaseline(glyphs, fontSize))
        {
            return false;
        }

        if (HasMultiLineOptimizationProgram(glyphs, fontSize))
        {
            return false;
        }

        float? effectiveBaselineHint = baselineBottomHint.HasValue &&
            HasStackedStructure(glyphs)
            ? baselineBottomHint
            : null;
        if (!TrySelectBaseline(glyphs, fontSize, effectiveBaselineHint, out BaselineSelection? baseline))
        {
            return false;
        }

        if (DominantFontSize(baseline!.Glyphs) > fontSize * 1.2f)
        {
            return false;
        }

        HashSet<PdfTextGlyph> relevant = SelectRelevantGlyphs(glyphs, paths, baseline!, fontSize);
        if (!TryTakeEquationNumber(
                baseline!.Glyphs,
                relevant,
                fontSize,
                out string? equationNumber,
                out IReadOnlyList<PdfTextGlyph> equationNumberGlyphs))
        {
            return false;
        }

        MathParser parser = new(relevant, paths, baseline.Bottom, fontSize);
        if (!parser.TryParse(out MathNode? root) || root == null)
        {
            return false;
        }

        if (!HasBalancedDelimiters(root.Text))
        {
            return false;
        }

        formula = new PdfMathMlFormula(
            root,
            equationNumber,
            fontSize,
            equationNumberGlyphs,
            relevant.Concat(equationNumberGlyphs).ToArray());
        return true;
    }

    public void WriteTo(StringBuilder html, bool includeEquationNumber = true)
    {
        html.Append("<math class=\"pdf-semantic-mathml\" display=\"block\" aria-label=\"")
            .Append(EncodeAttribute(AccessibleText))
            .Append("\"><semantics>");
        _root.WriteTo(html);
        html.Append("<annotation encoding=\"text/plain\">")
            .Append(Encode(AccessibleText))
            .Append("</annotation></semantics></math>");
        if (includeEquationNumber && EquationNumber != null)
        {
            html.Append("<span class=\"pdf-semantic-equation-number\" aria-label=\"Equation ")
                .Append(EncodeAttribute(EquationNumber[1..^1]))
                .Append("\">")
                .Append(Encode(EquationNumber))
                .Append("</span>");
        }
    }

    private static float DominantFontSize(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        return glyphs
            .GroupBy(static glyph => MathF.Round(glyph.FontSize * 2f) / 2f)
            .OrderByDescending(static group => group.Count())
            .ThenByDescending(static group => group.Key)
            .First()
            .Key;
    }

    private static bool TrySelectBaseline(
        IReadOnlyList<PdfTextGlyph> glyphs,
        float fontSize,
        float? baselineBottomHint,
        out BaselineSelection? selection)
    {
        float baselineTolerance = MathF.Max(1.2f, fontSize * 0.18f);
        PdfTextGlyph[] candidates = glyphs
            .Where(glyph => glyph.FontSize >= fontSize * 0.82f)
            .OrderBy(static glyph => glyph.Bounds.Bottom)
            .ThenBy(static glyph => glyph.Bounds.X)
            .ToArray();
        List<List<PdfTextGlyph>> groups = [];
        foreach (PdfTextGlyph glyph in candidates)
        {
            List<PdfTextGlyph>? group = groups.LastOrDefault();
            if (group == null || MathF.Abs(group.Average(static item => item.Bounds.Bottom) - glyph.Bounds.Bottom) > baselineTolerance)
            {
                groups.Add([glyph]);
            }
            else
            {
                group.Add(glyph);
            }
        }

        BaselineSelection[] ranked = groups
            .Select(group => new BaselineSelection(
                group.OrderBy(static glyph => glyph.Bounds.X).ToArray(),
                group.Average(static glyph => glyph.Bounds.Bottom),
                BaselineScore(group)))
            .Where(static group => group.Score >= 7)
            .OrderByDescending(static group => group.Score)
            .ThenByDescending(static group => group.Glyphs.Count)
            .ToArray();
        if (baselineBottomHint.HasValue)
        {
            ranked = ranked
                .Where(group => MathF.Abs(group.Bottom - baselineBottomHint.Value) <= baselineTolerance)
                .ToArray();
        }

        if (ranked.Length == 0 || ranked.Length > 1 && ranked[1].Score >= ranked[0].Score - 1)
        {
            selection = null;
            return false;
        }

        selection = ranked[0];
        return true;
    }

    private static int BaselineScore(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        string text = string.Concat(glyphs.OrderBy(static glyph => glyph.Bounds.X).Select(static glyph => glyph.Text));
        int mathGlyphs = glyphs.Count(static glyph => IsMathFont(glyph.FontName) || IsOperatorText(glyph.Text));
        int score = mathGlyphs * 5 / Math.Max(1, glyphs.Count);
        if (text.IndexOfAny(['=', '≡', '≈', '≤', '≥']) >= 0)
        {
            score += 5;
        }

        if (text.IndexOfAny(['√', '∑', '∏', '∫']) >= 0)
        {
            score += 3;
        }

        if (text.Contains(":=", StringComparison.Ordinal) || text.Contains("≔", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (text.Any(char.IsWhiteSpace) || glyphs.Any(static glyph => IsProseToken(glyph.Text)))
        {
            score -= 8;
        }

        return score;
    }

    private static HashSet<PdfTextGlyph> SelectRelevantGlyphs(
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyList<PdfLayoutPath> paths,
        BaselineSelection baseline,
        float fontSize)
    {
        float left = baseline.Glyphs.Min(static glyph => glyph.Bounds.X) - fontSize;
        float right = baseline.Glyphs.Max(static glyph => glyph.Bounds.Right) + fontSize;
        float top = baseline.Bottom - fontSize * 2.2f;
        float bottom = baseline.Bottom + fontSize * 1.8f;
        HashSet<PdfTextGlyph> relevant = new(ReferenceEqualityComparer.Instance);
        foreach (PdfTextGlyph glyph in baseline.Glyphs)
        {
            relevant.Add(glyph);
        }

        foreach (PdfTextGlyph glyph in glyphs)
        {
            if (glyph.Bounds.Right < left || glyph.Bounds.X > right || glyph.Bounds.Bottom < top || glyph.Bounds.Y > bottom)
            {
                continue;
            }

            if (glyph.FontSize < fontSize * 0.82f ||
                glyph.Text is "√" or "∑" or "∏" or "∫")
            {
                relevant.Add(glyph);
            }
        }

        AddLargeOperatorLimits(glyphs, relevant, baseline.Bottom, fontSize);
        AddConnectedFractions(glyphs, paths, relevant, baseline.Bottom, fontSize);
        AddBracketedMatrixGlyphs(glyphs, relevant, fontSize);
        return relevant;
    }

    private static bool HasStackedStructure(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        return glyphs.Any(static glyph => IsLargeOperatorText(glyph.Text)) ||
            HasBracketedMatrix(glyphs, fontSize: DominantFontSize(glyphs));
    }

    private static void AddBracketedMatrixGlyphs(
        IReadOnlyList<PdfTextGlyph> glyphs,
        ISet<PdfTextGlyph> relevant,
        float fontSize)
    {
        foreach (PdfTextGlyph open in glyphs.Where(static glyph => glyph.Text == "["))
        {
            foreach (PdfTextGlyph close in glyphs
                .Where(glyph => glyph.Text == "]" && glyph.Bounds.X > open.Bounds.X)
                .OrderBy(static glyph => glyph.Bounds.X))
            {
                if (!TryGetBracketedContent(glyphs, open, close, fontSize, out PdfTextGlyph[] content) ||
                    open.Bounds.X > relevant.Max(static glyph => glyph.Bounds.Right) + fontSize * 2.5f)
                {
                    continue;
                }

                relevant.Add(open);
                relevant.Add(close);
                foreach (PdfTextGlyph glyph in content)
                {
                    relevant.Add(glyph);
                }

                break;
            }
        }
    }

    private static bool HasBracketedMatrix(IReadOnlyList<PdfTextGlyph> glyphs, float fontSize)
    {
        return glyphs
            .Where(static glyph => glyph.Text == "[")
            .Any(open => glyphs
                .Where(glyph => glyph.Text == "]" && glyph.Bounds.X > open.Bounds.X)
                .Any(close => TryGetMatrixRows(glyphs, open, close, fontSize, out _)));
    }

    private static bool TryGetMatrixRows(
        IReadOnlyList<PdfTextGlyph> glyphs,
        PdfTextGlyph open,
        PdfTextGlyph close,
        float fontSize,
        out PdfTextGlyph[][] rows)
    {
        rows = [];
        if (!TryGetBracketedContent(glyphs, open, close, fontSize, out PdfTextGlyph[] interior))
        {
            return false;
        }

        if (interior.Any(glyph => glyph.FontSize < fontSize * 0.82f || !IsMatrixCellText(glyph.Text)))
        {
            return false;
        }

        float tolerance = MathF.Max(1.2f, fontSize * 0.18f);
        List<List<PdfTextGlyph>> rowGroups = [];
        foreach (PdfTextGlyph glyph in interior)
        {
            List<PdfTextGlyph>? row = rowGroups.LastOrDefault();
            if (row == null || MathF.Abs(row.Average(static item => item.Bounds.Bottom) - glyph.Bounds.Bottom) > tolerance)
            {
                rowGroups.Add([glyph]);
            }
            else
            {
                row.Add(glyph);
            }
        }

        if (rowGroups.Count is < 2 or > 6 ||
            rowGroups.Any(static row => row.Count is < 2 or > 6) ||
            rowGroups.Any(row => row.Count != rowGroups[0].Count))
        {
            return false;
        }

        rows = rowGroups
            .Select(static row => row.OrderBy(static glyph => glyph.Bounds.X).ToArray())
            .ToArray();
        float[] rowCenters = rows
            .Select(static row => row.Average(static glyph => glyph.Bounds.Y + glyph.Bounds.Height / 2f))
            .ToArray();
        for (int rowIndex = 1; rowIndex < rowCenters.Length; rowIndex++)
        {
            float spacing = rowCenters[rowIndex] - rowCenters[rowIndex - 1];
            if (spacing < fontSize * 0.65f || spacing > fontSize * 2.5f)
            {
                rows = [];
                return false;
            }
        }

        int columnCount = rows[0].Length;
        float[] columnCenters = new float[columnCount];
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            float[] centers = rows
                .Select(row => row[columnIndex].Bounds.X + row[columnIndex].Bounds.Width / 2f)
                .ToArray();
            if (centers.Max() - centers.Min() > MathF.Max(1.5f, fontSize * 0.25f))
            {
                rows = [];
                return false;
            }

            columnCenters[columnIndex] = centers.Average();
        }

        for (int columnIndex = 1; columnIndex < columnCenters.Length; columnIndex++)
        {
            float spacing = columnCenters[columnIndex] - columnCenters[columnIndex - 1];
            if (spacing < fontSize * 0.75f || spacing > fontSize * 5f)
            {
                rows = [];
                return false;
            }
        }

        float contentHeight = interior.Max(static glyph => glyph.Bounds.Bottom) -
            interior.Min(static glyph => glyph.Bounds.Y);
        if (MathF.Min(open.Bounds.Height, close.Bounds.Height) < contentHeight + fontSize * 0.2f)
        {
            rows = [];
            return false;
        }

        return true;
    }

    private static bool TryGetBracketedContent(
        IReadOnlyList<PdfTextGlyph> glyphs,
        PdfTextGlyph open,
        PdfTextGlyph close,
        float fontSize,
        out PdfTextGlyph[] content)
    {
        content = [];
        float tolerance = MathF.Max(1.2f, fontSize * 0.18f);
        float openCenterY = open.Bounds.Y + open.Bounds.Height / 2f;
        float closeCenterY = close.Bounds.Y + close.Bounds.Height / 2f;
        if (close.Bounds.X - open.Bounds.Right < fontSize * 1.5f ||
            close.Bounds.X - open.Bounds.Right > fontSize * 12f ||
            open.Bounds.Height < fontSize * 1.25f ||
            close.Bounds.Height < fontSize * 1.25f ||
            MathF.Abs(openCenterY - closeCenterY) > fontSize * 0.45f ||
            MathF.Abs(open.Bounds.Height - close.Bounds.Height) > fontSize * 0.55f)
        {
            return false;
        }

        content = glyphs
            .Where(glyph => !ReferenceEquals(glyph, open) && !ReferenceEquals(glyph, close))
            .Where(glyph => glyph.Bounds.X + glyph.Bounds.Width / 2f > open.Bounds.Right - tolerance &&
                glyph.Bounds.X + glyph.Bounds.Width / 2f < close.Bounds.X + tolerance)
            .Where(glyph => glyph.Bounds.Bottom >= MathF.Min(open.Bounds.Y, close.Bounds.Y) - tolerance &&
                glyph.Bounds.Y <= MathF.Max(open.Bounds.Bottom, close.Bounds.Bottom) + tolerance)
            .OrderBy(static glyph => glyph.Bounds.Bottom)
            .ThenBy(static glyph => glyph.Bounds.X)
            .ToArray();
        return content.Length is >= 4 and <= 36 &&
            content.Min(static glyph => glyph.Bounds.Y) >= MathF.Min(open.Bounds.Y, close.Bounds.Y) - tolerance &&
            content.Max(static glyph => glyph.Bounds.Bottom) <= MathF.Max(open.Bounds.Bottom, close.Bounds.Bottom) + tolerance;
    }

    private static bool IsMatrixCellText(string text)
    {
        return text.All(char.IsDigit) ||
            text.EnumerateRunes().All(static rune => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is
                System.Globalization.UnicodeCategory.NonSpacingMark or
                System.Globalization.UnicodeCategory.SpacingCombiningMark);
    }

    private static void AddLargeOperatorLimits(
        IReadOnlyList<PdfTextGlyph> glyphs,
        ISet<PdfTextGlyph> relevant,
        float baselineBottom,
        float fontSize)
    {
        PdfTextGlyph[] operators = relevant
            .Where(static glyph => IsLargeOperatorText(glyph.Text))
            .ToArray();
        foreach (PdfTextGlyph mathOperator in operators)
        {
            float left = mathOperator.Bounds.X - fontSize * 0.75f;
            float right = mathOperator.Bounds.Right + fontSize * 0.75f;
            float top = baselineBottom - fontSize * 6f;
            float bottom = baselineBottom + fontSize * 2f;
            foreach (PdfTextGlyph glyph in glyphs)
            {
                float center = glyph.Bounds.X + glyph.Bounds.Width / 2f;
                if (glyph.FontSize < fontSize * 0.82f &&
                    center >= left &&
                    center <= right &&
                    glyph.Bounds.Bottom >= top &&
                    glyph.Bounds.Y <= bottom)
                {
                    relevant.Add(glyph);
                }
            }
        }
    }

    private static void AddConnectedFractions(
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyList<PdfLayoutPath> paths,
        ISet<PdfTextGlyph> relevant,
        float baselineBottom,
        float fontSize)
    {
        float ruleTolerance = MathF.Max(0.8f, fontSize * 0.1f);
        bool added;
        do
        {
            added = false;
            float relevantLeft = relevant.Min(static glyph => glyph.Bounds.X);
            float relevantRight = relevant.Max(static glyph => glyph.Bounds.Right);
            foreach (PdfLayoutPath path in paths
                .Where(path => path.Bounds.Width >= MathF.Max(3f, fontSize * 0.35f))
                .Where(path => path.Bounds.Height <= MathF.Max(1.5f, fontSize * 0.18f))
                .Where(path => !IsRootRule(glyphs, path, fontSize))
                .OrderBy(static path => path.Bounds.Width))
            {
                if (path.Bounds.Right < relevantLeft - fontSize ||
                    path.Bounds.X > relevantRight + fontSize ||
                    MathF.Abs(path.Bounds.Y - baselineBottom) > fontSize * 1.8f)
                {
                    continue;
                }

                PdfTextGlyph[] numerator = glyphs
                    .Where(glyph => IsHorizontallyInside(glyph.Bounds, path.Bounds, ruleTolerance))
                    .Where(glyph => glyph.Bounds.Bottom <= path.Bounds.Y + ruleTolerance &&
                        path.Bounds.Y - glyph.Bounds.Bottom <= fontSize * 1.8f)
                    .ToArray();
                PdfTextGlyph[] denominator = glyphs
                    .Where(glyph => IsHorizontallyInside(glyph.Bounds, path.Bounds, ruleTolerance))
                    .Where(glyph => glyph.Bounds.Y >= path.Bounds.Bottom - ruleTolerance &&
                        glyph.Bounds.Y - path.Bounds.Bottom <= fontSize * 1.8f)
                    .ToArray();
                if (numerator.Length == 0 || denominator.Length == 0)
                {
                    continue;
                }

                foreach (PdfTextGlyph glyph in numerator.Concat(denominator))
                {
                    added |= relevant.Add(glyph);
                }
            }
        }
        while (added);
    }

    private static bool IsRootRule(
        IReadOnlyList<PdfTextGlyph> glyphs,
        PdfLayoutPath path,
        float fontSize)
    {
        return glyphs.Any(glyph => glyph.Text == "√" &&
            path.Bounds.X >= glyph.Bounds.X + glyph.Bounds.Width * 0.45f &&
            path.Bounds.X - glyph.Bounds.Right <= MathF.Max(2f, fontSize * 0.25f) &&
            path.Bounds.Y >= glyph.Bounds.Y - fontSize * 0.4f &&
            path.Bounds.Y <= glyph.Bounds.Bottom);
    }

    private static bool IsHorizontallyInside(
        PdfLayoutRectangle glyph,
        PdfLayoutRectangle container,
        float tolerance)
    {
        float center = glyph.X + glyph.Width / 2f;
        return center >= container.X - tolerance && center <= container.Right + tolerance;
    }

    private static bool TryTakeEquationNumber(
        IReadOnlyList<PdfTextGlyph> baselineGlyphs,
        ISet<PdfTextGlyph> relevant,
        float fontSize,
        out string? equationNumber,
        out IReadOnlyList<PdfTextGlyph> equationNumberGlyphs)
    {
        equationNumber = null;
        equationNumberGlyphs = [];
        PdfTextGlyph[] ordered = baselineGlyphs.OrderBy(static glyph => glyph.Bounds.X).ToArray();
        if (ordered.Length == 0)
        {
            return false;
        }

        int start;
        if (IsEquationNumber(ordered[^1].Text))
        {
            start = ordered.Length - 1;
            equationNumber = ordered[^1].Text;
        }
        else
        {
            int close = ordered.Length - 1;
            if (ordered[close].Text != ")")
            {
                return true;
            }

            int open = close - 1;
            while (open >= 0 && ordered[open].Text.All(char.IsDigit))
            {
                open--;
            }

            if (open < 0 || ordered[open].Text != "(" || open == close - 1)
            {
                return true;
            }

            string digits = string.Concat(ordered[(open + 1)..close].Select(static glyph => glyph.Text));
            start = open;
            equationNumber = "(" + digits + ")";
        }

        if (start == 0 || ordered[start].Bounds.X - ordered[start - 1].Bounds.Right < fontSize * 1.4f)
        {
            equationNumber = null;
            return true;
        }

        for (int index = start; index < ordered.Length; index++)
        {
            relevant.Remove(ordered[index]);
        }

        equationNumberGlyphs = ordered[start..];

        return true;
    }

    private static bool HasProseLikeBaseline(IReadOnlyList<PdfTextGlyph> glyphs, float fontSize)
    {
        if (glyphs.Any(static glyph => IsProseToken(glyph.Text)))
        {
            return true;
        }

        float baselineTolerance = MathF.Max(1.2f, fontSize * 0.18f);
        PdfTextGlyph[] candidates = glyphs
            .Where(glyph => glyph.FontSize >= fontSize * 0.82f)
            .OrderBy(static glyph => glyph.Bounds.Bottom)
            .ThenBy(static glyph => glyph.Bounds.X)
            .ToArray();
        List<List<PdfTextGlyph>> groups = [];
        foreach (PdfTextGlyph glyph in candidates)
        {
            List<PdfTextGlyph>? group = groups.LastOrDefault();
            if (group == null || MathF.Abs(group.Average(static item => item.Bounds.Bottom) - glyph.Bounds.Bottom) > baselineTolerance)
            {
                groups.Add([glyph]);
            }
            else
            {
                group.Add(glyph);
            }
        }

        foreach (List<PdfTextGlyph> group in groups)
        {
            int consecutiveLetters = 0;
            PdfTextGlyph? previous = null;
            foreach (PdfTextGlyph glyph in group.OrderBy(static item => item.Bounds.X))
            {
                bool isProseLetter = !IsMathFont(glyph.FontName) &&
                    glyph.Text.EnumerateRunes().All(static rune => Rune.IsLetter(rune));
                float gap = previous == null ? 0f : glyph.Bounds.X - previous.Bounds.Right;
                if (!isProseLetter || previous != null && gap > fontSize * 0.55f)
                {
                    consecutiveLetters = 0;
                }

                if (isProseLetter)
                {
                    consecutiveLetters += glyph.Text.EnumerateRunes().Count();
                    if (consecutiveLetters >= 5)
                    {
                        return true;
                    }
                }

                previous = isProseLetter ? glyph : null;
            }
        }

        return false;
    }

    private static bool HasMultiLineOptimizationProgram(
        IReadOnlyList<PdfTextGlyph> glyphs,
        float fontSize)
    {
        float baselineTolerance = MathF.Max(1.2f, fontSize * 0.18f);
        List<List<PdfTextGlyph>> groups = [];
        foreach (PdfTextGlyph glyph in glyphs
            .Where(glyph => glyph.FontSize >= fontSize * 0.82f)
            .OrderBy(static glyph => glyph.Bounds.Bottom)
            .ThenBy(static glyph => glyph.Bounds.X))
        {
            List<PdfTextGlyph>? group = groups.LastOrDefault();
            if (group == null ||
                MathF.Abs(group.Average(static item => item.Bounds.Bottom) - glyph.Bounds.Bottom) > baselineTolerance)
            {
                groups.Add([glyph]);
            }
            else
            {
                group.Add(glyph);
            }
        }

        string[] lines = groups
            .Select(group => string.Concat(group
                .OrderBy(static glyph => glyph.Bounds.X)
                .Select(static glyph => glyph.Text)))
            .Select(static text => string.Concat(text.Where(char.IsLetter)).ToLowerInvariant())
            .Where(static text => text.Length > 0)
            .ToArray();
        bool hasObjective = lines.Any(static line =>
            line.StartsWith("minimize", StringComparison.Ordinal) ||
            line.StartsWith("maximize", StringComparison.Ordinal) ||
            line.Contains("rank", StringComparison.Ordinal));
        bool hasConstraint = lines.Any(static line =>
            line.StartsWith("subjectto", StringComparison.Ordinal) ||
            line.StartsWith("suchthat", StringComparison.Ordinal));
        return hasObjective && hasConstraint;
    }

    private static bool IsEquationNumber(string text)
    {
        return text.Length >= 3 && text[0] == '(' && text[^1] == ')' && text[1..^1].All(char.IsDigit);
    }

    private static bool IsSupportedText(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.PrivateUse ||
                Rune.IsControl(rune))
            {
                return false;
            }
        }

        return text.Length <= 4 || KnownFunction(text);
    }

    private static bool IsProseToken(string text)
    {
        return text.Length > 1 && text.All(char.IsLetter) && !KnownFunction(text);
    }

    private static bool KnownFunction(string text)
    {
        return text is "log" or "exp" or "sin" or "cos" or "min" or "max" or "KL";
    }

    private static bool IsMathFont(string fontName)
    {
        int separator = fontName.IndexOf('+');
        string normalized = separator >= 0 ? fontName[(separator + 1)..] : fontName;
        return normalized.StartsWith("CM", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("MSBM", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Symbol", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOperatorText(string text)
    {
        return text.All(static character => IsOperatorCharacter(character));
    }

    private static bool IsLargeOperatorText(string text)
    {
        return text is "∑" or "∏" or "∫";
    }

    private static bool IsOperatorCharacter(char character)
    {
        return character is '=' or '+' or '-' or '−' or '±' or '×' or '·' or '/' or '|' or '∣' or
            '<' or '>' or '≤' or '≥' or '≈' or '≡' or '≔' or ':' or ';' or ',' or '.' or
            '(' or ')' or '[' or ']' or '{' or '}' or '∑' or '∏' or '∫' or '√';
    }

    private static bool IsMathOperatorText(string text)
    {
        return text.All(static character =>
            IsOperatorCharacter(character) ||
            character is '∈' or '∉' or '∋' or '⊂' or '⊆' or '⊃' or '⊇');
    }

    private static bool HasBalancedDelimiters(string text)
    {
        Stack<char> delimiters = new();
        foreach (char character in text)
        {
            if (character is '(' or '[' or '{')
            {
                delimiters.Push(character);
            }
            else if (character is ')' or ']' or '}')
            {
                if (delimiters.Count == 0 || !Matches(delimiters.Pop(), character))
                {
                    return false;
                }
            }
        }

        return delimiters.Count == 0;
    }

    private static bool Matches(char open, char close)
    {
        return (open, close) is ('(', ')') or ('[', ']') or ('{', '}');
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string EncodeAttribute(string text) => WebUtility.HtmlEncode(text);

    private sealed record BaselineSelection(IReadOnlyList<PdfTextGlyph> Glyphs, float Bottom, int Score);

    private sealed class MathParser
    {
        private readonly HashSet<PdfTextGlyph> _glyphs;
        private readonly IReadOnlyList<PdfLayoutPath> _paths;
        private readonly float _baselineBottom;
        private readonly float _fontSize;
        private readonly HashSet<PdfTextGlyph> _owned = new(ReferenceEqualityComparer.Instance);

        public MathParser(
            HashSet<PdfTextGlyph> glyphs,
            IReadOnlyList<PdfLayoutPath> paths,
            float baselineBottom,
            float fontSize)
        {
            _glyphs = glyphs;
            _paths = paths;
            _baselineBottom = baselineBottom;
            _fontSize = fontSize;
        }

        public bool TryParse(out MathNode? root)
        {
            root = null;
            List<PositionedNode> composites = [];
            if (!TryCreateMatrices(composites) ||
                !TryCreateFractions(composites) ||
                !TryCreateRoots(composites))
            {
                return false;
            }

            PdfTextGlyph[] baselineGlyphs = _glyphs
                .Where(glyph => !_owned.Contains(glyph) && IsOnBaseline(glyph))
                .OrderBy(static glyph => glyph.Bounds.X)
                .ToArray();
            foreach (PdfTextGlyph glyph in baselineGlyphs)
            {
                if (!TryCreateToken(glyph, out MathNode? token))
                {
                    return false;
                }

                composites.Add(new PositionedNode(token!, glyph.Bounds));
                _owned.Add(glyph);
            }

            composites.Sort(static (left, right) => left.Bounds.X.CompareTo(right.Bounds.X));
            if (composites.Count == 0 || !TryAttachScripts(composites))
            {
                return false;
            }

            if (_glyphs.Any(glyph => !_owned.Contains(glyph)))
            {
                return false;
            }

            root = new RowNode(composites.Select(static item => item.Node).ToArray());
            return true;
        }

        private bool TryCreateMatrices(ICollection<PositionedNode> nodes)
        {
            foreach (PdfTextGlyph open in _glyphs
                .Where(static glyph => glyph.Text == "[")
                .OrderBy(static glyph => glyph.Bounds.X))
            {
                if (_owned.Contains(open))
                {
                    continue;
                }

                PdfTextGlyph? close = _glyphs
                    .Where(glyph => !_owned.Contains(glyph) && glyph.Text == "]" && glyph.Bounds.X > open.Bounds.X)
                    .OrderBy(static glyph => glyph.Bounds.X)
                    .FirstOrDefault(candidate => TryGetMatrixRows(_glyphs.ToArray(), open, candidate, _fontSize, out _));
                if (close == null || !TryGetMatrixRows(_glyphs.ToArray(), open, close, _fontSize, out PdfTextGlyph[][] rows))
                {
                    continue;
                }

                List<IReadOnlyList<MathNode>> matrixRows = [];
                foreach (PdfTextGlyph[] row in rows)
                {
                    List<MathNode> cells = [];
                    foreach (PdfTextGlyph glyph in row)
                    {
                        if (!TryCreateToken(glyph, out MathNode? cell))
                        {
                            return false;
                        }

                        cells.Add(cell!);
                    }

                    matrixRows.Add(cells);
                }

                if (!TryCreateToken(open, out MathNode? openToken) ||
                    !TryCreateToken(close, out MathNode? closeToken))
                {
                    return false;
                }

                PdfTextGlyph[] ownedGlyphs = rows
                    .SelectMany(static row => row)
                    .Prepend(open)
                    .Append(close)
                    .ToArray();
                foreach (PdfTextGlyph glyph in ownedGlyphs)
                {
                    _owned.Add(glyph);
                }

                MathNode matrix = new RowNode([openToken!, new MatrixNode(matrixRows), closeToken!]);
                nodes.Add(new PositionedNode(matrix, Union(ownedGlyphs.Select(static glyph => glyph.Bounds))));
            }

            return true;
        }

        private bool TryCreateFractions(ICollection<PositionedNode> nodes)
        {
            foreach (PdfLayoutPath path in HorizontalRules().OrderBy(static path => path.Bounds.Width))
            {
                if (IsRootRule(path))
                {
                    continue;
                }

                float tolerance = MathF.Max(0.8f, _fontSize * 0.1f);
                PdfTextGlyph[] numerator = _glyphs
                    .Where(glyph => !_owned.Contains(glyph) && IsHorizontallyInside(glyph.Bounds, path.Bounds, tolerance))
                    .Where(glyph => glyph.Bounds.Bottom <= path.Bounds.Y + tolerance &&
                        path.Bounds.Y - glyph.Bounds.Bottom <= _fontSize * 1.8f)
                    .OrderBy(static glyph => glyph.Bounds.X)
                    .ToArray();
                PdfTextGlyph[] denominator = _glyphs
                    .Where(glyph => !_owned.Contains(glyph) && IsHorizontallyInside(glyph.Bounds, path.Bounds, tolerance))
                    .Where(glyph => glyph.Bounds.Y >= path.Bounds.Bottom - tolerance &&
                        glyph.Bounds.Y - path.Bounds.Bottom <= _fontSize * 1.8f)
                    .OrderBy(static glyph => glyph.Bounds.X)
                    .ToArray();
                if (numerator.Length == 0 || denominator.Length == 0)
                {
                    continue;
                }

                if (!TryCreateLinearRow(numerator, out MathNode? numeratorNode) ||
                    !TryCreateLinearRow(denominator, out MathNode? denominatorNode))
                {
                    return false;
                }

                foreach (PdfTextGlyph glyph in numerator.Concat(denominator))
                {
                    _owned.Add(glyph);
                }

                PdfLayoutRectangle bounds = Union(numerator.Concat(denominator).Select(static glyph => glyph.Bounds).Append(path.Bounds));
                nodes.Add(new PositionedNode(new FractionNode(numeratorNode!, denominatorNode!), bounds));
            }

            return true;
        }

        private bool TryCreateRoots(ICollection<PositionedNode> nodes)
        {
            foreach (PdfTextGlyph radical in _glyphs.Where(static glyph => glyph.Text == "√").OrderBy(static glyph => glyph.Bounds.X))
            {
                if (_owned.Contains(radical))
                {
                    continue;
                }

                PdfLayoutPath? rule = HorizontalRules()
                    .Where(path => path.Bounds.X >= radical.Bounds.X + radical.Bounds.Width * 0.45f)
                    .Where(path => path.Bounds.X - radical.Bounds.Right <= MathF.Max(2f, _fontSize * 0.25f))
                    .Where(path => path.Bounds.Y >= radical.Bounds.Y - _fontSize * 0.4f &&
                        path.Bounds.Y <= radical.Bounds.Bottom)
                    .OrderBy(static path => path.Bounds.X)
                    .FirstOrDefault();
                if (rule == null)
                {
                    return false;
                }

                float tolerance = MathF.Max(0.8f, _fontSize * 0.1f);
                PdfTextGlyph[] radicand = _glyphs
                    .Where(glyph => !_owned.Contains(glyph) && !ReferenceEquals(glyph, radical))
                    .Where(glyph => IsHorizontallyInside(glyph.Bounds, rule.Bounds, tolerance))
                    .Where(glyph => glyph.Bounds.Y >= rule.Bounds.Y - tolerance &&
                        glyph.Bounds.Y <= _baselineBottom + _fontSize * 0.6f)
                    .OrderBy(static glyph => glyph.Bounds.X)
                    .ToArray();
                if (radicand.Length == 0 || !TryCreateLinearRow(radicand, out MathNode? radicandNode))
                {
                    return false;
                }

                _owned.Add(radical);
                foreach (PdfTextGlyph glyph in radicand)
                {
                    _owned.Add(glyph);
                }

                PdfLayoutRectangle bounds = Union(radicand.Select(static glyph => glyph.Bounds)
                    .Append(radical.Bounds)
                    .Append(rule.Bounds));
                nodes.Add(new PositionedNode(new RootNode(radicandNode!), bounds));
            }

            return true;
        }

        private bool TryAttachScripts(IList<PositionedNode> nodes)
        {
            int[] attachmentOrder = Enumerable.Range(0, nodes.Count)
                .OrderByDescending(index => nodes[index].Node is TokenNode token && token.IsLargeOperator)
                .ThenBy(static index => index)
                .ToArray();
            foreach (int index in attachmentOrder)
            {
                PositionedNode positioned = nodes[index];
                float nextX = index + 1 < nodes.Count ? nodes[index + 1].Bounds.X : positioned.Bounds.Right + _fontSize * 2f;
                bool limits = positioned.Node is TokenNode token && token.IsLargeOperator;
                PdfTextGlyph[] candidates = _glyphs
                    .Where(glyph => !_owned.Contains(glyph) && glyph.FontSize < _fontSize * 0.82f)
                    .Where(glyph => limits
                        ? glyph.Bounds.X + glyph.Bounds.Width / 2f >= positioned.Bounds.X - _fontSize * 0.75f &&
                            glyph.Bounds.X + glyph.Bounds.Width / 2f <= positioned.Bounds.Right + _fontSize * 0.75f
                        : glyph.Bounds.X >= positioned.Bounds.Right - _fontSize * 0.25f && glyph.Bounds.X < nextX)
                    .OrderBy(static glyph => glyph.Bounds.X)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    continue;
                }

                float baseCenter = positioned.Bounds.Y + positioned.Bounds.Height / 2f;
                PdfTextGlyph[] superscript = candidates
                    .Where(glyph => limits
                        ? glyph.Bounds.Bottom < _baselineBottom - _fontSize * 0.45f
                        : glyph.Bounds.Y + glyph.Bounds.Height / 2f < baseCenter - _fontSize * 0.12f)
                    .ToArray();
                PdfTextGlyph[] subscript = candidates
                    .Where(glyph => limits
                        ? glyph.Bounds.Bottom >= _baselineBottom - _fontSize * 0.45f
                        : glyph.Bounds.Y + glyph.Bounds.Height / 2f > baseCenter + _fontSize * 0.12f)
                    .ToArray();
                if (superscript.Length + subscript.Length != candidates.Length ||
                    !TryCreateOptionalRow(subscript, out MathNode? subscriptNode) ||
                    !TryCreateOptionalRow(superscript, out MathNode? superscriptNode))
                {
                    return false;
                }

                foreach (PdfTextGlyph glyph in candidates)
                {
                    _owned.Add(glyph);
                }

                MathNode scripted = limits
                    ? new LimitsNode(positioned.Node, subscriptNode, superscriptNode)
                    : new ScriptNode(positioned.Node, subscriptNode, superscriptNode);
                nodes[index] = positioned with { Node = scripted };
            }

            return true;
        }

        private bool TryCreateOptionalRow(PdfTextGlyph[] glyphs, out MathNode? node)
        {
            if (glyphs.Length == 0)
            {
                node = null;
                return true;
            }

            return TryCreateLinearRow(glyphs, out node);
        }

        private static bool TryCreateLinearRow(IReadOnlyList<PdfTextGlyph> glyphs, out MathNode? node)
        {
            node = null;
            if (glyphs.Count == 0)
            {
                return false;
            }

            float fontSize = DominantFontSize(glyphs);
            PdfTextGlyph[] baselineGlyphs = glyphs
                .Where(glyph => glyph.FontSize >= fontSize * 0.82f)
                .OrderBy(static glyph => glyph.Bounds.X)
                .ToArray();
            HashSet<PdfTextGlyph> owned = new(ReferenceEqualityComparer.Instance);
            List<MathNode> nodes = [];
            for (int index = 0; index < baselineGlyphs.Length; index++)
            {
                PdfTextGlyph glyph = baselineGlyphs[index];
                if (!TryCreateToken(glyph, out MathNode? token))
                {
                    return false;
                }

                owned.Add(glyph);
                float nextX = index + 1 < baselineGlyphs.Length
                    ? baselineGlyphs[index + 1].Bounds.X
                    : glyph.Bounds.Right + fontSize * 2f;
                PdfTextGlyph[] scriptGlyphs = glyphs
                    .Where(candidate => !owned.Contains(candidate) && candidate.FontSize < fontSize * 0.82f)
                    .Where(candidate => candidate.Bounds.X >= glyph.Bounds.Right - fontSize * 0.25f &&
                        candidate.Bounds.X < nextX)
                    .OrderBy(static candidate => candidate.Bounds.X)
                    .ToArray();
                if (scriptGlyphs.Length == 0)
                {
                    nodes.Add(token!);
                    continue;
                }

                float baseCenter = glyph.Bounds.Y + glyph.Bounds.Height / 2f;
                PdfTextGlyph[] superscript = scriptGlyphs
                    .Where(candidate => candidate.Bounds.Y + candidate.Bounds.Height / 2f < baseCenter - fontSize * 0.12f)
                    .ToArray();
                PdfTextGlyph[] subscript = scriptGlyphs
                    .Where(candidate => candidate.Bounds.Y + candidate.Bounds.Height / 2f > baseCenter + fontSize * 0.12f)
                    .ToArray();
                if (superscript.Length + subscript.Length != scriptGlyphs.Length ||
                    !TryCreateFlatRow(subscript, out MathNode? subscriptNode) ||
                    !TryCreateFlatRow(superscript, out MathNode? superscriptNode))
                {
                    return false;
                }

                foreach (PdfTextGlyph scriptGlyph in scriptGlyphs)
                {
                    owned.Add(scriptGlyph);
                }

                nodes.Add(new ScriptNode(token!, subscriptNode, superscriptNode));
            }

            if (glyphs.Any(glyph => !owned.Contains(glyph)))
            {
                return false;
            }

            node = nodes.Count == 1 ? nodes[0] : new RowNode(nodes);
            return nodes.Count > 0;
        }

        private static bool TryCreateFlatRow(IReadOnlyList<PdfTextGlyph> glyphs, out MathNode? node)
        {
            node = null;
            if (glyphs.Count == 0)
            {
                return true;
            }

            List<MathNode> nodes = [];
            foreach (PdfTextGlyph glyph in glyphs.OrderBy(static glyph => glyph.Bounds.X))
            {
                if (!TryCreateToken(glyph, out MathNode? token))
                {
                    return false;
                }

                nodes.Add(token!);
            }

            node = nodes.Count == 1 ? nodes[0] : new RowNode(nodes);
            return true;
        }

        private static bool TryCreateToken(PdfTextGlyph glyph, out MathNode? node)
        {
            string text = glyph.Text == "`" && IsComputerModernMathItalic(glyph.FontName)
                ? "ℓ"
                : glyph.Text;
            if (text.All(char.IsDigit))
            {
                node = new TokenNode("mn", text, false, null);
                return true;
            }

            if (KnownFunction(text))
            {
                node = new TokenNode("mi", text, false, "normal");
                return true;
            }

            if (text.EnumerateRunes().All(static rune => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is
                    System.Globalization.UnicodeCategory.NonSpacingMark or
                    System.Globalization.UnicodeCategory.SpacingCombiningMark))
            {
                string? variant = glyph.FontName.Contains("BX", StringComparison.OrdinalIgnoreCase) ||
                    glyph.FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                    ? "bold"
                    : null;
                node = new TokenNode("mi", text, false, variant);
                return true;
            }

            if (IsMathOperatorText(text))
            {
                node = new TokenNode("mo", text, text is "∑" or "∏" or "∫", null);
                return true;
            }

            node = null;
            return false;
        }

        private static bool IsComputerModernMathItalic(string fontName)
        {
            int separator = fontName.IndexOf('+');
            string normalized = separator >= 0 ? fontName[(separator + 1)..] : fontName;
            return normalized.StartsWith("CMMI", StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<PdfLayoutPath> HorizontalRules()
        {
            return _paths.Where(path =>
                path.Bounds.Width >= MathF.Max(3f, _fontSize * 0.35f) &&
                path.Bounds.Height <= MathF.Max(1.5f, _fontSize * 0.18f));
        }

        private bool IsRootRule(PdfLayoutPath path)
        {
            return _glyphs.Any(glyph => glyph.Text == "√" &&
                path.Bounds.X >= glyph.Bounds.X + glyph.Bounds.Width * 0.45f &&
                path.Bounds.X - glyph.Bounds.Right <= MathF.Max(2f, _fontSize * 0.25f) &&
                path.Bounds.Y >= glyph.Bounds.Y - _fontSize * 0.4f &&
                path.Bounds.Y <= glyph.Bounds.Bottom);
        }

        private bool IsOnBaseline(PdfTextGlyph glyph)
        {
            if (glyph.FontSize < _fontSize * 0.82f)
            {
                return false;
            }

            if (glyph.Text is "∑" or "∏" or "∫")
            {
                return glyph.Bounds.Y <= _baselineBottom &&
                    _baselineBottom - glyph.Bounds.Bottom <= _fontSize * 2f;
            }

            return MathF.Abs(glyph.Bounds.Bottom - _baselineBottom) <= MathF.Max(1.2f, _fontSize * 0.18f);
        }

        private static bool IsHorizontallyInside(
            PdfLayoutRectangle glyph,
            PdfLayoutRectangle container,
            float tolerance)
        {
            float center = glyph.X + glyph.Width / 2f;
            return center >= container.X - tolerance && center <= container.Right + tolerance;
        }

        private static PdfLayoutRectangle Union(IEnumerable<PdfLayoutRectangle> rectangles)
        {
            PdfLayoutRectangle[] values = rectangles.ToArray();
            float left = values.Min(static rectangle => rectangle.X);
            float top = values.Min(static rectangle => rectangle.Y);
            float right = values.Max(static rectangle => rectangle.Right);
            float bottom = values.Max(static rectangle => rectangle.Bottom);
            return new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }
    }

    private abstract record MathNode
    {
        public abstract string Text { get; }

        public abstract void WriteTo(StringBuilder html);
    }

    private sealed record TokenNode(string Tag, string Value, bool IsLargeOperator, string? MathVariant) : MathNode
    {
        public override string Text => Value;

        public override void WriteTo(StringBuilder html)
        {
            html.Append('<').Append(Tag);
            if (MathVariant != null)
            {
                html.Append(" mathvariant=\"").Append(MathVariant).Append('"');
            }

            html.Append('>').Append(Encode(Value)).Append("</").Append(Tag).Append('>');
        }
    }

    private sealed record RowNode(IReadOnlyList<MathNode> Children) : MathNode
    {
        public override string Text => string.Concat(Children.Select(static child => child.Text));

        public override void WriteTo(StringBuilder html)
        {
            html.Append("<mrow>");
            foreach (MathNode child in Children)
            {
                child.WriteTo(html);
            }

            html.Append("</mrow>");
        }
    }

    private sealed record FractionNode(MathNode Numerator, MathNode Denominator) : MathNode
    {
        public override string Text => "(" + Numerator.Text + ")/(" + Denominator.Text + ")";

        public override void WriteTo(StringBuilder html)
        {
            html.Append("<mfrac>");
            Numerator.WriteTo(html);
            Denominator.WriteTo(html);
            html.Append("</mfrac>");
        }
    }

    private sealed record RootNode(MathNode Radicand) : MathNode
    {
        public override string Text => "sqrt(" + Radicand.Text + ")";

        public override void WriteTo(StringBuilder html)
        {
            html.Append("<msqrt>");
            Radicand.WriteTo(html);
            html.Append("</msqrt>");
        }
    }

    private sealed record MatrixNode(IReadOnlyList<IReadOnlyList<MathNode>> Rows) : MathNode
    {
        public override string Text => string.Join(
            ";",
            Rows.Select(static row => string.Join(",", row.Select(static cell => cell.Text))));

        public override void WriteTo(StringBuilder html)
        {
            html.Append("<mtable>");
            foreach (IReadOnlyList<MathNode> row in Rows)
            {
                html.Append("<mtr>");
                foreach (MathNode cell in row)
                {
                    html.Append("<mtd>");
                    cell.WriteTo(html);
                    html.Append("</mtd>");
                }

                html.Append("</mtr>");
            }

            html.Append("</mtable>");
        }
    }

    private sealed record ScriptNode(MathNode Base, MathNode? Subscript, MathNode? Superscript) : MathNode
    {
        public override string Text => Base.Text +
            (Subscript == null ? "" : "_(" + Subscript.Text + ")") +
            (Superscript == null ? "" : "^(" + Superscript.Text + ")");

        public override void WriteTo(StringBuilder html)
        {
            string tag = Subscript != null && Superscript != null ? "msubsup" : Subscript != null ? "msub" : "msup";
            html.Append('<').Append(tag).Append('>');
            Base.WriteTo(html);
            Subscript?.WriteTo(html);
            Superscript?.WriteTo(html);
            html.Append("</").Append(tag).Append('>');
        }
    }

    private sealed record LimitsNode(MathNode Base, MathNode? Under, MathNode? Over) : MathNode
    {
        public override string Text => Base.Text +
            (Under == null ? "" : "_(" + Under.Text + ")") +
            (Over == null ? "" : "^(" + Over.Text + ")");

        public override void WriteTo(StringBuilder html)
        {
            string tag = Under != null && Over != null ? "munderover" : Under != null ? "munder" : "mover";
            html.Append('<').Append(tag).Append('>');
            Base.WriteTo(html);
            Under?.WriteTo(html);
            Over?.WriteTo(html);
            html.Append("</").Append(tag).Append('>');
        }
    }

    private sealed record PositionedNode(MathNode Node, PdfLayoutRectangle Bounds);
}
