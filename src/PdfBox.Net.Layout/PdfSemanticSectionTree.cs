using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfBox.Net.Util;

namespace PdfBox.Net.Layout;

/// <summary>
/// A deterministic document outline built from inferred headings.
/// </summary>
public sealed class PdfSemanticSectionTree
{
    private static readonly Regex TerminalNumberedHeadingPattern = new(
        @"^(?<number>\d{1,2}(?:\.\d+)*)\.\s+\p{L}",
        RegexOptions.Compiled);
    private static readonly Regex LeadingNumberedHeadingPattern = new(
        @"^\d{1,2}(?:\.\d+)*(?:\.)?\s+\p{L}",
        RegexOptions.Compiled);
    private static readonly Regex AppendixHeadingPattern = new(
        @"^Appendix\s+[A-Z]\.",
        RegexOptions.Compiled);
    private readonly Dictionary<PdfSemanticElement, PdfSemanticHeading> _headingsByElement;
    private readonly Dictionary<PdfSemanticElement, PdfSemanticSection> _sectionsByHeading;

    private PdfSemanticSectionTree(
        IReadOnlyList<PdfSemanticHeading> headings,
        IReadOnlyList<PdfSemanticSection> sections)
    {
        Headings = headings.ToArray();
        Sections = sections.ToArray();
        _headingsByElement = Headings.ToDictionary(static heading => heading.Element);
        _sectionsByHeading = Flatten(Sections).ToDictionary(static section => section.Heading.Element);
    }

    /// <summary>
    /// Gets all detected headings in document order, including the document title.
    /// </summary>
    public IReadOnlyList<PdfSemanticHeading> Headings { get; }

    /// <summary>
    /// Gets the top-level sections in document order.
    /// </summary>
    public IReadOnlyList<PdfSemanticSection> Sections { get; }

    /// <summary>
    /// Finds stable heading metadata for a semantic heading element.
    /// </summary>
    public PdfSemanticHeading? FindHeading(PdfSemanticElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _headingsByElement.GetValueOrDefault(element);
    }

    /// <summary>
    /// Finds the section opened by a semantic heading element, if the heading is not a document title.
    /// </summary>
    public PdfSemanticSection? FindSection(PdfSemanticElement heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        return _sectionsByHeading.GetValueOrDefault(heading);
    }

    internal static PdfSemanticSectionTree Create(IReadOnlyList<PdfSemanticPage> pages)
    {
        List<PdfSemanticHeading> headings = [];
        List<PdfSemanticSection> roots = [];
        List<PdfSemanticSection> stack = [];
        Dictionary<string, int> slugOccurrences = new(StringComparer.Ordinal);
        PdfSemanticElement? canonicalDocumentTitle = pages.FirstOrDefault()?.Elements
            .FirstOrDefault(static element =>
                element.Kind == PdfSemanticElementKind.Heading && element.IsDocumentTitle);

        foreach (PdfSemanticPage page in pages)
        {
            foreach (PdfSemanticElement element in page.Elements)
            {
                if (element.Kind != PdfSemanticElementKind.Heading)
                {
                    continue;
                }

                string slug = StableSlug(element.Text);
                int occurrence = slugOccurrences.GetValueOrDefault(slug) + 1;
                slugOccurrences[slug] = occurrence;
                string token = occurrence == 1
                    ? slug
                    : slug + "-" + occurrence.ToString(CultureInfo.InvariantCulture);
                PdfSemanticHeading heading = new(element, page.PageNumber, "heading-" + token);
                headings.Add(heading);

                if (IsCanonicalDocumentTitle(element, canonicalDocumentTitle) || IsFigureCaption(element.Text))
                {
                    continue;
                }

                int level = SectionLevel(element, page.PageNumber, stack);
                while (stack.Count > 0 && stack[^1].Level >= level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                PdfSemanticSection? parent = stack.Count == 0 ? null : stack[^1];
                PdfSemanticSection section = new(heading, level, "section-" + token, parent);
                if (parent == null)
                {
                    roots.Add(section);
                }
                else
                {
                    parent.AddChild(section);
                }

                stack.Add(section);
            }
        }

        return new PdfSemanticSectionTree(headings, roots);
    }

    private static bool IsCanonicalDocumentTitle(
        PdfSemanticElement element,
        PdfSemanticElement? canonicalDocumentTitle)
    {
        return element.IsDocumentTitle &&
            canonicalDocumentTitle != null &&
            string.Equals(element.Text, canonicalDocumentTitle.Text, StringComparison.Ordinal);
    }

    private static int SectionLevel(
        PdfSemanticElement heading,
        int pageNumber,
        IReadOnlyList<PdfSemanticSection> stack)
    {
        int visualLevel = Math.Clamp(heading.HeadingLevel, 1, 6);
        string text = heading.Text.Trim();
        if (text == "References" || AppendixHeadingPattern.IsMatch(text))
        {
            return 1;
        }

        if (text is "DISCUSSION" or "REFERENCES")
        {
            PdfSemanticSection? owningControl = stack.LastOrDefault(section =>
                section.Level >= 3 &&
                LeadingNumberedHeadingPattern.IsMatch(section.Heading.Element.Text.TrimStart()));
            if (owningControl != null)
            {
                return Math.Min(6, owningControl.Level + 1);
            }
        }

        Match numbered = TerminalNumberedHeadingPattern.Match(text);
        if (!numbered.Success)
        {
            return visualLevel;
        }

        int numberedDepth = numbered.Groups["number"].Value.Count(static character => character == '.') + 1;
        PdfSemanticSection? previousNumberedSection = stack.LastOrDefault(section =>
            HasTerminalSectionNumber(section.Heading.Element.Text));
        PdfSemanticSection? visualParent = previousNumberedSection?.Parent is { } numberedParent &&
            !HasTerminalSectionNumber(numberedParent.Heading.Element.Text)
                ? numberedParent
                : stack.LastOrDefault(section =>
                    section.Level < visualLevel &&
                    pageNumber - section.Heading.PageNumber <= 2 &&
                    !HasTerminalSectionNumber(section.Heading.Element.Text));
        return Math.Clamp((visualParent?.Level ?? 0) + numberedDepth, 1, 6);
    }

    private static bool HasTerminalSectionNumber(string text)
    {
        return TerminalNumberedHeadingPattern.IsMatch(text.TrimStart());
    }

    private static bool IsFigureCaption(string text)
    {
        string trimmed = text.TrimStart();
        if (!trimmed.StartsWith("Figure ", StringComparison.Ordinal))
        {
            return false;
        }

        int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        return colon is >= 8 and <= 18 &&
            trimmed[7..colon].All(static character => char.IsDigit(character));
    }

    private static IEnumerable<PdfSemanticSection> Flatten(IEnumerable<PdfSemanticSection> sections)
    {
        foreach (PdfSemanticSection section in sections)
        {
            yield return section;
            foreach (PdfSemanticSection child in Flatten(section.Sections))
            {
                yield return child;
            }
        }
    }

    private static string StableSlug(string text)
    {
        StringBuilder slug = new();
        bool pendingSeparator = false;
        foreach (char character in PdfStringNormalization.Normalize(text, NormalizationForm.FormKD))
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = slug.Length > 0;
            }
        }

        return slug.Length == 0 ? "untitled" : slug.ToString();
    }
}

/// <summary>
/// Stable metadata for an inferred document heading.
/// </summary>
public sealed class PdfSemanticHeading
{
    internal PdfSemanticHeading(PdfSemanticElement element, int pageNumber, string id)
    {
        Element = element;
        PageNumber = pageNumber;
        Id = id;
    }

    public PdfSemanticElement Element { get; }

    public int PageNumber { get; }

    public string Id { get; }
}

/// <summary>
/// A heading-owned semantic section with lower-level child sections.
/// </summary>
public sealed class PdfSemanticSection
{
    private readonly List<PdfSemanticSection> _sections = [];

    internal PdfSemanticSection(
        PdfSemanticHeading heading,
        int level,
        string id,
        PdfSemanticSection? parent)
    {
        Heading = heading;
        Level = level;
        Id = id;
        Parent = parent;
    }

    public PdfSemanticHeading Heading { get; }

    public int Level { get; }

    public string Id { get; }

    public PdfSemanticSection? Parent { get; }

    public IReadOnlyList<PdfSemanticSection> Sections => _sections;

    internal void AddChild(PdfSemanticSection section)
    {
        _sections.Add(section);
    }
}
