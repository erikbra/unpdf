using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfBox.Net.Layout;

internal static class PdfSemanticInlineInference
{
    private static readonly Regex ParentheticalPattern = new(
        @"\((?<value>[^()\r\n]{2,100})\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WordPattern = new(
        @"[\p{L}\p{N}][\p{L}\p{M}\p{N}'\u2019-]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsoDateTimePattern = new(
        @"(?<![\d/])(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})(?:(?:T|\s)(?<hour>\d{2}):(?<minute>\d{2})(?::(?<second>\d{2}))?(?:\s?(?<zone>Z|UTC|[+-]\d{2}:\d{2}))?)?(?![\d/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MonthFirstDatePattern = new(
        @"(?<!\p{L})(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<day>\d{1,2}),\s*(?<year>\d{4})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DayFirstDatePattern = new(
        @"(?<![\d\p{L}])(?<day>\d{1,2})\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<year>\d{4})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TimePattern = new(
        @"(?<![\d:])(?<hour>\d{1,2}):(?<minute>\d{2})(?::(?<second>\d{2}))?(?:(?:\s*(?<period>AM|PM))(?:\s*(?<zone>Z|UTC|[+-]\d{2}:\d{2}))?|\s*(?<zone>Z|UTC|[+-]\d{2}:\d{2}))?(?![\d:])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MetadataLabelPattern = new(
        @"^\s*(?:date|published|publication date|updated|issued|effective|last revised|last updated|as of)\s*[:\-]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AncillaryTextPattern = new(
        @"(?:\u00a9|\bcopyright\b|\ball rights reserved\b|\bconfidential\b|\bdisclaimer\b|\blegal notice\b|\bterms and conditions\b|\bprivacy notice\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MetadataTextPattern = new(
        @"^\s*(?:document\s+|report\s+)?(?:version|revision|published|updated|issued|effective|last revised|doi|isbn|issn|prepared for|prepared by)\s*[:#-]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BibliographyTitlePrefixPattern = new(
        @"(?:\(\d{4}[a-z]?\)|\b\d{4}[a-z]?)\s*[.),:;]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CaptionPattern = new(
        @"^\s*(?:figure|fig\.?|table|plate|illustration|image)\s+[A-Z0-9]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WorkAttributionCuePattern = new(
        @"(?:\badapted\s+from\b|\breproduced\s+from\b|\bexcerpt(?:ed)?\s+from\b|\bsource\s*:\s*|\bfrom\s+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> InitialStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "for", "in", "of", "on", "or", "the", "to"
    };
    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    ];

    public static void Apply(PdfLayoutPage page, PdfSemanticPage semanticPage)
    {
        float bodyFontSize = EstimateBodyFontSize(semanticPage);
        HashSet<PdfSemanticLine> visited = new(ReferenceEqualityComparer.Instance);
        foreach (PdfSemanticElement element in semanticPage.Elements)
        {
            if (element.TableCaption is { } tableCaption)
            {
                PdfSemanticElement captionElement = new(
                    PdfSemanticElementKind.Paragraph,
                    tableCaption.Text,
                    tableCaption.Bounds,
                    tableCaption.Lines);
                foreach (PdfSemanticLine line in tableCaption.Lines)
                {
                    if (visited.Add(line))
                    {
                        line.SetInlineSemantics(InferLine(page, captionElement, line, bodyFontSize));
                    }
                }
            }

            bool excludedContainer = element.Kind is PdfSemanticElementKind.Table or
                PdfSemanticElementKind.Footnote or
                PdfSemanticElementKind.CodeBlock or
                PdfSemanticElementKind.Navigation;
            foreach (PdfSemanticLine line in element.Lines)
            {
                if (!visited.Add(line))
                {
                    continue;
                }

                line.SetInlineSemantics(excludedContainer
                    ? []
                    : InferLine(page, element, line, bodyFontSize));
            }
        }
    }

    public static IReadOnlyList<PdfSemanticInline> InferFormLabel(string? text)
    {
        if (string.IsNullOrEmpty(text) || !MetadataLabelPattern.IsMatch(text))
        {
            return [];
        }

        return FindDateTimes(text);
    }

    private static IReadOnlyList<PdfSemanticInline> InferLine(
        PdfLayoutPage page,
        PdfSemanticElement element,
        PdfSemanticLine line,
        float bodyFontSize)
    {
        if (line.Text.Length == 0)
        {
            return [];
        }

        List<PdfSemanticInline> semantics = [];
        if (!HasFormulaOrBaselineAttachment(line) &&
            IsAncillarySmallText(page, element, line, bodyFontSize))
        {
            semantics.Add(new PdfSemanticInline(PdfSemanticInlineKind.Small, 0, line.Text.Length));
        }

        if (IsDateTimeContext(element, line.Text))
        {
            semantics.AddRange(FindDateTimes(line.Text));
        }

        semantics.AddRange(FindExplicitAbbreviations(line.Text));
        semantics.AddRange(FindCitations(element, line));
        return RemoveConflictingRanges(semantics);
    }

    private static bool IsAncillarySmallText(
        PdfLayoutPage page,
        PdfSemanticElement element,
        PdfSemanticLine line,
        float bodyFontSize)
    {
        if (bodyFontSize <= 0f ||
            line.DominantFontSize <= 0f ||
            line.DominantFontSize > bodyFontSize * 0.84f)
        {
            return false;
        }

        bool peripheral = line.Bounds.Y <= page.Height * 0.20f ||
            line.Bounds.Bottom >= page.Height * 0.72f;
        if (!peripheral)
        {
            return false;
        }

        bool legalContext = AncillaryTextPattern.IsMatch(line.Text);
        bool metadataContext = MetadataTextPattern.IsMatch(line.Text) &&
            element.Kind is PdfSemanticElementKind.Header or
                PdfSemanticElementKind.FrontMatter or
                PdfSemanticElementKind.Footer or
                PdfSemanticElementKind.Paragraph;
        return legalContext || metadataContext;
    }

    private static bool IsDateTimeContext(PdfSemanticElement element, string text)
    {
        if (element.Kind is PdfSemanticElementKind.Header or PdfSemanticElementKind.FrontMatter)
        {
            return MetadataLabelPattern.IsMatch(text) || IsDateTimeOnlyOrHeaderLead(text);
        }

        if (element.Kind is not (PdfSemanticElementKind.Paragraph or PdfSemanticElementKind.List))
        {
            return false;
        }

        PdfSemanticInline? first = FindDateTimes(text).FirstOrDefault();
        if (first == null || text[..first.Start].Trim().Length != 0)
        {
            return false;
        }

        string suffix = text[(first.Start + first.Length)..].TrimStart();
        return suffix.StartsWith("-", StringComparison.Ordinal) ||
            suffix.StartsWith('\u2013') ||
            suffix.StartsWith('\u2014') ||
            suffix.StartsWith(':');
    }

    private static bool IsDateTimeOnlyOrHeaderLead(string text)
    {
        IReadOnlyList<PdfSemanticInline> values = FindDateTimes(text);
        if (values.Count == 0)
        {
            return false;
        }

        PdfSemanticInline first = values[0];
        string prefix = text[..first.Start].Trim();
        string suffix = text[(first.Start + first.Length)..].Trim();
        return prefix.Length == 0 &&
            (suffix.Length == 0 || suffix[0] is '|' or '\u00b7' or '-' or '\u2013' or '\u2014');
    }

    private static IReadOnlyList<PdfSemanticInline> FindDateTimes(string text)
    {
        List<PdfSemanticInline> values = [];
        foreach (Match match in IsoDateTimePattern.Matches(text))
        {
            if (!TryIsoDateTime(match, out string? value))
            {
                continue;
            }

            values.Add(new PdfSemanticInline(PdfSemanticInlineKind.Time, match.Index, match.Length, value));
        }

        AddWrittenDates(text, MonthFirstDatePattern, values);
        AddWrittenDates(text, DayFirstDatePattern, values);
        foreach (Match match in TimePattern.Matches(text))
        {
            if (values.Any(value => RangesOverlap(value.Start, value.Length, match.Index, match.Length)) ||
                !TryTime(match, out string? value))
            {
                continue;
            }

            values.Add(new PdfSemanticInline(PdfSemanticInlineKind.Time, match.Index, match.Length, value));
        }

        return values.OrderBy(static value => value.Start).ToArray();
    }

    private static void AddWrittenDates(
        string text,
        Regex pattern,
        ICollection<PdfSemanticInline> values)
    {
        foreach (Match match in pattern.Matches(text))
        {
            int month = Array.FindIndex(MonthNames, name =>
                string.Equals(name, match.Groups["month"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
            if (!int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
                !int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
                !IsValidDate(year, month, day))
            {
                continue;
            }

            string value = FormattableString.Invariant($"{year:0000}-{month:00}-{day:00}");
            values.Add(new PdfSemanticInline(PdfSemanticInlineKind.Time, match.Index, match.Length, value));
        }
    }

    private static bool TryIsoDateTime(Match match, out string? value)
    {
        value = null;
        int year = ParseInvariant(match, "year");
        int month = ParseInvariant(match, "month");
        int day = ParseInvariant(match, "day");
        if (!IsValidDate(year, month, day))
        {
            return false;
        }

        string date = FormattableString.Invariant($"{year:0000}-{month:00}-{day:00}");
        if (!match.Groups["hour"].Success)
        {
            value = date;
            return true;
        }

        int hour = ParseInvariant(match, "hour");
        int minute = ParseInvariant(match, "minute");
        int second = match.Groups["second"].Success ? ParseInvariant(match, "second") : -1;
        if (!IsValidTime(hour, minute, second))
        {
            return false;
        }

        if (!TryNormalizeZone(match.Groups["zone"].Value, out string? zone))
        {
            return false;
        }

        value = date + "T" + FormatTime(hour, minute, second) + zone;
        return true;
    }

    private static bool TryTime(Match match, out string? value)
    {
        value = null;
        int hour = ParseInvariant(match, "hour");
        int minute = ParseInvariant(match, "minute");
        int second = match.Groups["second"].Success ? ParseInvariant(match, "second") : -1;
        string period = match.Groups["period"].Value;
        if (period.Length > 0)
        {
            if (hour is < 1 or > 12)
            {
                return false;
            }

            hour %= 12;
            if (period.Equals("PM", StringComparison.OrdinalIgnoreCase))
            {
                hour += 12;
            }
        }
        else if (match.Groups["hour"].Length != 2)
        {
            return false;
        }

        if (!IsValidTime(hour, minute, second))
        {
            return false;
        }

        if (!TryNormalizeZone(match.Groups["zone"].Value, out string? zone))
        {
            return false;
        }

        value = FormatTime(hour, minute, second) + zone;
        return true;
    }

    private static IReadOnlyList<PdfSemanticInline> FindExplicitAbbreviations(string text)
    {
        List<PdfSemanticInline> abbreviations = [];
        foreach (Match parenthetical in ParentheticalPattern.Matches(text))
        {
            Group inner = parenthetical.Groups["value"];
            string innerText = inner.Value.Trim();
            int innerTrimOffset = inner.Value.IndexOf(innerText, StringComparison.Ordinal);
            if (IsAbbreviation(innerText) &&
                TryFindExpansionBefore(text, parenthetical.Index, innerText, out string? expansion))
            {
                abbreviations.Add(new PdfSemanticInline(
                    PdfSemanticInlineKind.Abbreviation,
                    inner.Index + innerTrimOffset,
                    innerText.Length,
                    expansion));
                continue;
            }

            if (!TryFindAbbreviationBefore(text, parenthetical.Index, out int abbreviationStart, out string? abbreviation) ||
                !IsExpansion(innerText, abbreviation!))
            {
                continue;
            }

            abbreviations.Add(new PdfSemanticInline(
                PdfSemanticInlineKind.Abbreviation,
                abbreviationStart,
                abbreviation!.Length,
                innerText));
        }

        return abbreviations;
    }

    private static bool TryFindExpansionBefore(
        string text,
        int parenthesisStart,
        string abbreviation,
        out string? expansion)
    {
        expansion = null;
        Match[] words = WordPattern.Matches(text[..parenthesisStart]).Cast<Match>().TakeLast(10).ToArray();
        for (int start = words.Length - 2; start >= 0; start--)
        {
            Match[] candidateWords = words[start..];
            string candidate = text[words[start].Index..(words[^1].Index + words[^1].Length)];
            if (IsExpansion(candidate, abbreviation))
            {
                expansion = candidate;
                return true;
            }

            if (candidateWords.Length >= 8)
            {
                break;
            }
        }

        return false;
    }

    private static bool TryFindAbbreviationBefore(
        string text,
        int parenthesisStart,
        out int start,
        out string? abbreviation)
    {
        start = -1;
        abbreviation = null;
        Match match = Regex.Match(
            text[..parenthesisStart],
            @"(?<abbr>(?:[A-Z0-9][A-Z0-9&-]{1,11}|(?:[A-Z]\.){2,8}))\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !IsAbbreviation(match.Groups["abbr"].Value))
        {
            return false;
        }

        start = match.Groups["abbr"].Index;
        abbreviation = match.Groups["abbr"].Value;
        return true;
    }

    private static bool IsAbbreviation(string text)
    {
        string compact = NormalizeAbbreviation(text);
        return compact.Length is >= 2 and <= 12 &&
            compact.Any(char.IsLetter) &&
            compact.All(static character => char.IsUpper(character) || char.IsDigit(character));
    }

    private static bool IsExpansion(string text, string abbreviation)
    {
        Match[] words = WordPattern.Matches(text).Cast<Match>().ToArray();
        if (words.Length < 2 || words.Length > 10)
        {
            return false;
        }

        StringBuilder initials = new();
        foreach (Match word in words)
        {
            if (!InitialStopWords.Contains(word.Value))
            {
                initials.Append(char.ToUpperInvariant(word.Value[0]));
            }
        }

        return string.Equals(initials.ToString(), NormalizeAbbreviation(abbreviation), StringComparison.Ordinal);
    }

    private static IEnumerable<PdfSemanticInline> FindCitations(
        PdfSemanticElement element,
        PdfSemanticLine line)
    {
        bool bibliography = element.Kind == PdfSemanticElementKind.Bibliography;
        bool caption = CaptionPattern.IsMatch(line.Text);
        bool attribution = line.Text.TrimStart().StartsWith("Source:", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("adapted from", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("excerpt from", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("reproduced from", StringComparison.OrdinalIgnoreCase);
        if (!bibliography && !caption && !attribution)
        {
            yield break;
        }

        int cursor = 0;
        foreach (PdfTextRun run in line.Runs.OrderBy(static run => run.Bounds.X))
        {
            if (!IsItalicFont(run.FontName) || string.IsNullOrWhiteSpace(run.Text))
            {
                continue;
            }

            string candidate = run.Text.Trim();
            int runStart = line.Text.IndexOf(run.Text, cursor, StringComparison.Ordinal);
            if (runStart < 0)
            {
                runStart = line.Text.IndexOf(candidate, cursor, StringComparison.Ordinal);
            }

            if (runStart < 0)
            {
                continue;
            }

            int candidateStart = runStart + run.Text.IndexOf(candidate, StringComparison.Ordinal);
            cursor = Math.Max(cursor, runStart + run.Text.Length);
            string prefix = line.Text[..candidateStart];
            bool supported = bibliography
                ? BibliographyTitlePrefixPattern.IsMatch(prefix)
                : WorkAttributionCuePattern.IsMatch(prefix) && LooksLikeNonPersonWorkTitle(candidate);
            if (!supported || !ContainsWorkTitleText(candidate))
            {
                continue;
            }

            yield return new PdfSemanticInline(
                PdfSemanticInlineKind.Citation,
                candidateStart,
                candidate.Length);
        }
    }

    private static bool ContainsWorkTitleText(string text)
    {
        int wordCount = WordPattern.Matches(text).Count;
        return wordCount is >= 1 and <= 24 && text.Any(char.IsLetter);
    }

    private static bool LooksLikeNonPersonWorkTitle(string text)
    {
        string trimmed = text.Trim('"', '\'', '\u201c', '\u201d', '\u2018', '\u2019');
        if (trimmed.Contains(':') || text[0] is '"' or '\u201c' or '\u2018')
        {
            return true;
        }

        string firstWord = WordPattern.Match(trimmed).Value;
        return firstWord.Equals("A", StringComparison.OrdinalIgnoreCase) ||
            firstWord.Equals("An", StringComparison.OrdinalIgnoreCase) ||
            firstWord.Equals("The", StringComparison.OrdinalIgnoreCase) ||
            firstWord.Equals("Of", StringComparison.OrdinalIgnoreCase) ||
            firstWord.Equals("On", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<PdfSemanticInline> RemoveConflictingRanges(
        IEnumerable<PdfSemanticInline> source)
    {
        List<PdfSemanticInline> accepted = [];
        foreach (PdfSemanticInline candidate in source
            .OrderBy(static value => value.Kind == PdfSemanticInlineKind.Small ? 1 : 0)
            .ThenBy(static value => value.Start)
            .ThenByDescending(static value => value.Length))
        {
            if (candidate.Kind != PdfSemanticInlineKind.Small &&
                accepted.Any(existing => existing.Kind != PdfSemanticInlineKind.Small &&
                    RangesOverlap(existing.Start, existing.Length, candidate.Start, candidate.Length)))
            {
                continue;
            }

            accepted.Add(candidate);
        }

        return accepted.OrderBy(static value => value.Start).ThenByDescending(static value => value.Length).ToArray();
    }

    private static bool HasFormulaOrBaselineAttachment(PdfSemanticLine line)
    {
        if (line.Runs.Any(static run => IsMathFont(run.FontName)) &&
            line.Text.Any(static character => character is '=' or '\u2211' or '\u221a' or '\u222b'))
        {
            return true;
        }

        float center = line.Bounds.Y + line.Bounds.Height / 2f;
        return line.Runs.Any(run =>
            run.FontSize < line.DominantFontSize * 0.82f &&
            MathF.Abs(run.Bounds.Y + run.Bounds.Height / 2f - center) > line.DominantFontSize * 0.18f);
    }

    private static float EstimateBodyFontSize(PdfSemanticPage page)
    {
        return page.Elements
            .Where(static element => element.Kind is PdfSemanticElementKind.Paragraph or
                PdfSemanticElementKind.List or
                PdfSemanticElementKind.BlockQuote or
                PdfSemanticElementKind.DefinitionList)
            .SelectMany(static element => element.Lines)
            .Where(static line => line.DominantFontSize > 0f)
            .GroupBy(static line => MathF.Round(line.DominantFontSize * 2f) / 2f)
            .Select(static group => new
            {
                Size = group.Key,
                Weight = group.Sum(static line => Math.Max(1, line.Text.Length))
            })
            .OrderByDescending(static value => value.Weight)
            .ThenByDescending(static value => value.Size)
            .Select(static value => value.Size)
            .FirstOrDefault();
    }

    private static bool IsItalicFont(string fontName)
    {
        int plus = fontName.IndexOf('+');
        string normalized = plus >= 0 ? fontName[(plus + 1)..] : fontName;
        return normalized.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("-It", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMathFont(string fontName)
    {
        string normalized = fontName.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized.Contains("MATH", StringComparison.Ordinal) ||
            normalized.StartsWith("CMEX", StringComparison.Ordinal) ||
            normalized.StartsWith("CMSY", StringComparison.Ordinal) ||
            normalized.StartsWith("CMMI", StringComparison.Ordinal) ||
            normalized.StartsWith("MSAM", StringComparison.Ordinal) ||
            normalized.StartsWith("MSBM", StringComparison.Ordinal);
    }

    private static string NormalizeAbbreviation(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static int ParseInvariant(Match match, string groupName)
    {
        return int.Parse(match.Groups[groupName].Value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static bool IsValidDate(int year, int month, int day)
    {
        return year is >= 1 and <= 9999 &&
            month is >= 1 and <= 12 &&
            day >= 1 &&
            day <= DateTime.DaysInMonth(year, month);
    }

    private static bool IsValidTime(int hour, int minute, int second)
    {
        return hour is >= 0 and <= 23 &&
            minute is >= 0 and <= 59 &&
            second is >= -1 and <= 59;
    }

    private static string FormatTime(int hour, int minute, int second)
    {
        return second >= 0
            ? FormattableString.Invariant($"{hour:00}:{minute:00}:{second:00}")
            : FormattableString.Invariant($"{hour:00}:{minute:00}");
    }

    private static bool TryNormalizeZone(string zone, out string? normalized)
    {
        normalized = "";
        if (zone.Length == 0)
        {
            return true;
        }

        if (zone.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
            zone.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Z";
            return true;
        }

        if (zone.Length != 6 ||
            zone[0] is not ('+' or '-') ||
            zone[3] != ':' ||
            !int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(zone.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
            hour > 23 ||
            minute > 59)
        {
            normalized = null;
            return false;
        }

        normalized = zone;
        return true;
    }

    private static bool RangesOverlap(int firstStart, int firstLength, int secondStart, int secondLength)
    {
        return firstStart < secondStart + secondLength && secondStart < firstStart + firstLength;
    }
}
