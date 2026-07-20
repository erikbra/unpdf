using PdfBox.Net.Layout;

namespace PdfBox.Net.Layout.Tests;

public sealed class PdfSemanticHeadingNormalizerTest
{
    [Fact]
    public void Normalize_MultiPageAcademicOutlineUsesOneTitleAndContiguousSectionLevels()
    {
        PdfSemanticElement title = Heading("A Document-Wide Outline", 2, 72f, isDocumentTitle: true);
        PdfSemanticElement introduction = Heading("1 Introduction", 1, 120f);
        PdfSemanticElement background = Heading("1.1 Background", 1, 72f);
        PdfSemanticElement detail = Heading("1.1.1 Detail", 1, 120f);
        PdfSemanticElement methods = Heading("2 Methods", 1, 72f);
        PdfSemanticElement acknowledgements = Heading("Acknowledgements", 2, 120f);
        PdfSemanticElement references = Heading("References", 2, 168f);
        PdfSemanticElement appendix = Heading("Appendix A. Supplement", 2, 216f);
        PdfSemanticElement appendixDetail = Heading("A.1 Data", 1, 72f);

        PdfSemanticPage[] normalized = PdfSemanticHeadingNormalizer.Normalize(
        [
            new PdfSemanticPage(1, [title, introduction]),
            new PdfSemanticPage(2, [background, detail]),
            new PdfSemanticPage(3, [methods, acknowledgements, references, appendix]),
            new PdfSemanticPage(4, [appendixDetail])
        ]);

        PdfSemanticElement[] headings = normalized
            .SelectMany(static page => page.Elements)
            .ToArray();
        Assert.Equal(
            [1, 2, 3, 4, 2, 2, 2, 2, 3],
            headings.Select(static heading => heading.HeadingLevel));
        Assert.Single(headings, static heading => heading.IsDocumentTitle);
        Assert.True(headings[0].IsDocumentTitle);
        Assert.All(headings.Skip(1), static heading => Assert.False(heading.IsDocumentTitle));

        PdfSemanticDocument document = new(normalized);
        Assert.Equal(
            [2, 3, 4, 2, 2, 2, 2, 3],
            document.SectionTree.Headings
                .Select(static heading => heading.Element)
                .Where(static heading => !heading.IsDocumentTitle)
                .Select(heading => document.SectionTree.FindSection(heading)!.Level));
    }

    [Fact]
    public void Normalize_PreservesCoherentAuthoredHeadingLevelsAndPresentationMetadata()
    {
        PdfTaggedStructureElement titleTag = TaggedHeading("Title");
        PdfTaggedStructureElement sectionTag = TaggedHeading("H2");
        PdfTaggedStructureElement subsectionTag = TaggedHeading("H3");
        PdfSemanticElement title = Heading(
            "Authored title",
            1,
            72f,
            isDocumentTitle: true,
            taggedStructure: titleTag);
        PdfSemanticElement section = Heading(
            "Authored section",
            2,
            120f,
            taggedStructure: sectionTag);
        PdfSemanticElement subsection = Heading(
            "Authored subsection",
            3,
            72f,
            taggedStructure: subsectionTag);

        PdfSemanticPage[] normalized = PdfSemanticHeadingNormalizer.Normalize(
        [
            new PdfSemanticPage(1, [title, section]),
            new PdfSemanticPage(2, [subsection])
        ]);

        PdfSemanticElement[] headings = normalized.SelectMany(static page => page.Elements).ToArray();
        Assert.Equal([1, 2, 3], headings.Select(static heading => heading.HeadingLevel));
        Assert.Same(title, headings[0]);
        Assert.Same(section, headings[1]);
        Assert.Same(subsection, headings[2]);
        Assert.Same(title.Lines[0], headings[0].Lines[0]);
        Assert.Same(sectionTag, headings[1].TaggedStructure);
    }

    [Fact]
    public void Normalize_PreservesAuthoredLevelWhenDocumentHasNoDetectedTitle()
    {
        PdfTaggedStructureElement sectionTag = TaggedHeading("H2");
        PdfSemanticElement section = Heading(
            "Authored section",
            2,
            72f,
            taggedStructure: sectionTag);

        PdfSemanticElement normalized = Assert.Single(
            Assert.Single(PdfSemanticHeadingNormalizer.Normalize(
            [
                new PdfSemanticPage(1, [section])
            ])).Elements);

        Assert.Same(section, normalized);
        Assert.Equal(2, normalized.HeadingLevel);
        Assert.Same(sectionTag, normalized.TaggedStructure);
    }

    [Fact]
    public void Normalize_DemotesLaterPageTitleResetsWithoutChangingTextLines()
    {
        PdfSemanticElement title = Heading("Primary title", 1, 72f, isDocumentTitle: true);
        PdfSemanticElement first = Heading("1 First section", 1, 120f);
        PdfSemanticElement pageReset = Heading("2 Second section", 1, 72f, isDocumentTitle: true);

        PdfSemanticPage[] normalized = PdfSemanticHeadingNormalizer.Normalize(
        [
            new PdfSemanticPage(1, [title, first]),
            new PdfSemanticPage(2, [pageReset])
        ]);

        PdfSemanticElement normalizedReset = normalized[1].Elements[0];
        Assert.Equal(2, normalizedReset.HeadingLevel);
        Assert.False(normalizedReset.IsDocumentTitle);
        Assert.Same(pageReset.Lines[0], normalizedReset.Lines[0]);
        Assert.Equal(pageReset.Lines[0].DominantFontSize, normalizedReset.Lines[0].DominantFontSize);
        Assert.Equal(pageReset.Lines[0].DominantFontName, normalizedReset.Lines[0].DominantFontName);
    }

    private static PdfSemanticElement Heading(
        string text,
        int level,
        float y,
        bool isDocumentTitle = false,
        PdfTaggedStructureElement? taggedStructure = null)
    {
        PdfLayoutRectangle bounds = new(72f, y, 300f, 14f);
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph glyph = new(text, "Times-Bold", 14f, 0f, bounds, color);
        PdfTextRun run = new(text, "Times-Bold", 14f, 0f, bounds, color, [glyph]);
        PdfSemanticLine line = new(text, bounds, "Times-Bold", 14f, 0f, color, [run]);
        return new PdfSemanticElement(
            PdfSemanticElementKind.Heading,
            text,
            bounds,
            [line],
            headingLevel: level,
            isDocumentTitle: isDocumentTitle,
            taggedStructure: taggedStructure);
    }

    private static PdfTaggedStructureElement TaggedHeading(string standardStructureType)
    {
        return new PdfTaggedStructureElement(
            standardStructureType,
            standardStructureType,
            PdfTaggedStructureKind.Heading,
            null,
            null,
            null,
            null,
            new PdfTaggedStructureAttributes(),
            []);
    }
}
