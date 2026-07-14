using PdfBox.Net.Layout;

namespace PdfBox.Net.Layout.Tests;

public sealed class PdfSemanticSectionTreeTest
{
    [Fact]
    public void Create_SameLevelHeadingsBecomeSiblingSectionsWithStableIds()
    {
        PdfSemanticElement introduction = Heading("1 Introduction", 1, 72f);
        PdfSemanticElement background = Heading("2 Background", 1, 144f);

        PdfSemanticDocument document = Document(introduction, Paragraph("Intro body", 92f), background);

        Assert.Equal(2, document.SectionTree.Sections.Count);
        PdfSemanticSection first = document.SectionTree.Sections[0];
        PdfSemanticSection second = document.SectionTree.Sections[1];
        Assert.Equal("section-1-introduction", first.Id);
        Assert.Equal("heading-1-introduction", first.Heading.Id);
        Assert.Equal("section-2-background", second.Id);
        Assert.Null(first.Parent);
        Assert.Null(second.Parent);
        Assert.Same(first, document.SectionTree.FindSection(introduction));
        Assert.Same(second.Heading, document.SectionTree.FindHeading(background));
    }

    [Fact]
    public void Create_LowerLevelHeadingsNestUnderNearestHigherLevel()
    {
        PdfSemanticDocument document = Document(
            Heading("3 Model Architecture", 1, 72f),
            Heading("3.1 Encoder", 2, 96f),
            Heading("3.1.1 Layers", 3, 120f),
            Heading("3.2 Attention", 2, 144f));

        PdfSemanticSection architecture = Assert.Single(document.SectionTree.Sections);
        Assert.Equal(["3.1 Encoder", "3.2 Attention"],
            architecture.Sections.Select(static section => section.Heading.Element.Text));
        PdfSemanticSection encoder = architecture.Sections[0];
        PdfSemanticSection layers = Assert.Single(encoder.Sections);
        Assert.Same(architecture, encoder.Parent);
        Assert.Same(encoder, layers.Parent);
    }

    [Fact]
    public void Create_SkippedHeadingLevelNestsWithoutSyntheticSection()
    {
        PdfSemanticDocument document = Document(
            Heading("1 Overview", 1, 72f),
            Heading("1.1.1 Deep Detail", 3, 96f),
            Heading("1.2 Follow-up", 2, 120f));

        PdfSemanticSection overview = Assert.Single(document.SectionTree.Sections);
        Assert.Equal(2, overview.Sections.Count);
        Assert.Equal(3, overview.Sections[0].Level);
        Assert.Equal("1.1.1 Deep Detail", overview.Sections[0].Heading.Element.Text);
        Assert.Empty(overview.Sections[0].Sections);
        Assert.Equal(2, overview.Sections[1].Level);
    }

    [Fact]
    public void Create_TerminalNumberingNormalizesVisualPeersIntoHierarchy()
    {
        PdfSemanticDocument document = Document(
            Heading("1. Introduction", 2, 72f),
            Heading("1.1. Purpose", 2, 96f),
            Heading("2. Fundamentals", 2, 120f));

        Assert.Equal(["1. Introduction", "2. Fundamentals"],
            document.SectionTree.Sections.Select(static section => section.Heading.Element.Text));
        PdfSemanticSection introduction = document.SectionTree.Sections[0];
        PdfSemanticSection purpose = Assert.Single(introduction.Sections);
        Assert.Equal(1, introduction.Level);
        Assert.Equal(2, purpose.Level);
    }

    [Fact]
    public void Create_TerminalNumberingUnderUnnumberedParentKeepsContextualOffset()
    {
        PdfSemanticDocument document = Document(
            Heading("Mission Objectives", 1, 72f),
            Heading("1. Demonstrate heat shield performance", 2, 96f),
            Heading("2. Demonstrate mission operations", 2, 120f));

        PdfSemanticSection mission = Assert.Single(document.SectionTree.Sections);
        Assert.Equal(2, mission.Sections.Count);
        Assert.All(mission.Sections, section =>
        {
            Assert.Equal(2, section.Level);
            Assert.Same(mission, section.Parent);
        });
    }

    [Fact]
    public void Create_DistantFrontMatterHeadingDoesNotOwnNumberedBodySections()
    {
        PdfSemanticElement author = Heading("Document Author", 1, 72f);
        PdfSemanticElement introduction = Heading("1. Introduction", 2, 72f);
        PdfSemanticDocument document = new(
        [
            new PdfSemanticPage(1, [author]),
            new PdfSemanticPage(10, [introduction])
        ]);

        PdfSemanticSection introductionSection = document.SectionTree.FindSection(introduction)!;
        Assert.Equal(1, introductionSection.Level);
        Assert.Null(introductionSection.Parent);
        Assert.Contains(introductionSection, document.SectionTree.Sections);
    }

    [Fact]
    public void Create_DeepControlSupplementsStayInsideOwningControl()
    {
        PdfSemanticDocument document = Document(
            Heading("3 Security Requirements", 1, 72f),
            Heading("3.1 Access Control", 2, 96f),
            Heading("03.01.01 Account Management", 3, 120f),
            Heading("DISCUSSION", 2, 144f),
            Heading("REFERENCES", 2, 168f),
            Heading("03.01.02 Access Enforcement", 3, 192f),
            Heading("References", 2, 216f),
            Heading("Appendix A. Acronyms", 2, 240f));

        PdfSemanticSection security = document.SectionTree.Sections[0];
        PdfSemanticSection accessControl = Assert.Single(security.Sections);
        PdfSemanticSection accountManagement = accessControl.Sections[0];
        Assert.Equal(["DISCUSSION", "REFERENCES"],
            accountManagement.Sections.Select(static section => section.Heading.Element.Text));
        Assert.All(accountManagement.Sections, section => Assert.Equal(4, section.Level));
        Assert.Equal("03.01.02 Access Enforcement", accessControl.Sections[1].Heading.Element.Text);
        Assert.Equal(["References", "Appendix A. Acronyms"],
            document.SectionTree.Sections.Skip(1).Select(static section => section.Heading.Element.Text));
        Assert.All(document.SectionTree.Sections.Skip(1), section => Assert.Equal(1, section.Level));
    }

    private static PdfSemanticDocument Document(params PdfSemanticElement[] elements)
    {
        return new PdfSemanticDocument([new PdfSemanticPage(1, elements)]);
    }

    private static PdfSemanticElement Heading(string text, int level, float y)
    {
        return Element(PdfSemanticElementKind.Heading, text, y, level);
    }

    private static PdfSemanticElement Paragraph(string text, float y)
    {
        return Element(PdfSemanticElementKind.Paragraph, text, y, 0);
    }

    private static PdfSemanticElement Element(PdfSemanticElementKind kind, string text, float y, int level)
    {
        PdfLayoutRectangle bounds = new(72f, y, 240f, 12f);
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph glyph = new(text, "Helvetica", 12f, 0f, bounds, color);
        PdfTextRun run = new(text, "Helvetica", 12f, 0f, bounds, color, [glyph]);
        PdfSemanticLine line = new(text, bounds, "Helvetica", 12f, 0f, color, [run]);
        return new PdfSemanticElement(kind, text, bounds, [line], headingLevel: level);
    }
}
