using PdfBox.Net.COS;
using PdfBox.Net.Layout;
using PdfBox.Net.Markdown;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.DocumentInterchange.LogicalStructure;
using PdfBox.Net.PDModel.DocumentInterchange.MarkedContent;
using PdfBox.Net.PDModel.DocumentInterchange.TaggedPdf;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Interactive.Action;
using PdfBox.Net.PDModel.Interactive.Annotation;

namespace PdfBox.Net.ConversionQuality;

public sealed record MarkdownQualityFixtureResult(string Id, string Directory);

public static class MarkdownQualityFixtureGenerator
{
    public static IReadOnlyList<MarkdownQualityFixtureResult> Generate(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        List<MarkdownQualityFixtureResult> results = [];
        results.Add(GenerateTaggedReference(root));
        results.Add(GenerateUntagged(root, "simple-heading-paragraph", CreateHeadingParagraph));
        results.Add(GenerateUntagged(root, "simple-list-link", CreateListAndLink));
        results.Add(GenerateUntagged(root, "simple-table", CreateSimpleTable));
        results.Add(GenerateUntagged(root, "ambiguous-multicolumn", CreateMultiColumn));
        results.Add(GenerateUntagged(root, "ambiguous-table", CreateAmbiguousTable));
        results.Add(GenerateUntagged(root, "header-footer-noise", CreateHeaderFooterNoise));
        return results;
    }

    private static MarkdownQualityFixtureResult GenerateTaggedReference(string root)
    {
        const string id = "tagged-reference";
        string directory = PrepareDirectory(root, id);
        using TaggedFixture fixture = new();
        PDStructureElement document = fixture.AddElement(fixture.Root, StandardStructureTypes.Document);
        PDStructureElement heading = fixture.AddElement(document, StandardStructureTypes.H1);
        heading.SetActualText("Tagged reference");
        fixture.WriteText(heading, "Tagged reference", 700);
        fixture.WriteText(
            fixture.AddElement(document, StandardStructureTypes.P),
            "Authored paragraph",
            660);
        fixture.Complete();
        fixture.Document.Save(Path.Combine(directory, "source.pdf"));
        PdfMarkdownConverter.Convert(PdfLayoutExtractor.Extract(fixture.Document))
            .WriteToDirectory(directory);
        return new MarkdownQualityFixtureResult(id, directory);
    }

    private static MarkdownQualityFixtureResult GenerateUntagged(
        string root,
        string id,
        Action<PDDocument> create)
    {
        string directory = PrepareDirectory(root, id);
        using PDDocument document = new();
        create(document);
        document.Save(Path.Combine(directory, "source.pdf"));
        PdfMarkdownConverter.Convert(PdfLayoutExtractor.Extract(document))
            .WriteToDirectory(directory);
        return new MarkdownQualityFixtureResult(id, directory);
    }

    private static string PrepareDirectory(string root, string id)
    {
        string directory = Path.Combine(root, id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CreateHeadingParagraph(PDDocument document)
    {
        PDPage page = AddPage(document);
        using PDPageContentStream content = new(document, page);
        WriteText(content, "Untagged quality heading", 72, 700, size: 22, bold: true);
        WriteText(content, "First paragraph follows the heading.", 72, 650);
        WriteText(content, "Second paragraph remains in reading order.", 72, 610);
    }

    private static void CreateListAndLink(PDDocument document)
    {
        PDPage page = AddPage(document);
        using (PDPageContentStream content = new(document, page))
        {
            WriteText(content, "Opening prose establishes ordinary body text.", 72, 720);
            WriteText(content, "A second line establishes the normal vertical rhythm.", 72, 704);
            WriteText(content, "- First checklist item", 72, 650);
            WriteText(content, "- Second checklist item", 72, 626);
            WriteText(content, "- Third checklist item", 72, 602);
            WriteText(content, "Project documentation", 72, 550);
        }

        PDAnnotationLink annotation = new();
        annotation.SetRectangle(new PDRectangle(70, 546, 150, 18));
        PDActionURI action = new();
        action.SetURI("https://example.test/docs");
        annotation.SetAction(action);
        page.SetAnnotations([annotation]);
    }

    private static void CreateSimpleTable(PDDocument document)
    {
        PDPage page = AddPage(document);
        using PDPageContentStream content = new(document, page);
        WriteText(content, "Table quality introduction.", 72, 700);
        DrawGrid(content, [630f, 600f, 570f, 540f], [72f, 210f, 360f]);
        WriteText(content, "Name", 84, 610, bold: true);
        WriteText(content, "Value", 222, 610, bold: true);
        WriteText(content, "Alpha", 84, 580);
        WriteText(content, "42", 222, 580);
        WriteText(content, "Beta", 84, 550);
        WriteText(content, "84", 222, 550);
    }

    private static void CreateAmbiguousTable(PDDocument document)
    {
        PDPage page = AddPage(document);
        using PDPageContentStream content = new(document, page);
        WriteText(content, "Ambiguous table introduction.", 72, 700);
        DrawGrid(content, [630f, 600f, 570f, 540f], [72f, 210f, 360f]);
        WriteText(content, "Name", 84, 610, bold: true);
        WriteText(content, "Value", 222, 610, bold: true);
        WriteText(content, "Alpha spans an uncertain row", 84, 580);
        WriteText(content, "Beta", 84, 550);
        WriteText(content, "84", 222, 550);
    }

    private static void CreateMultiColumn(PDDocument document)
    {
        PDPage page = AddPage(document);
        using PDPageContentStream content = new(document, page);
        WriteText(content, "Left column first passage.", 72, 700);
        WriteText(content, "Right column first passage.", 330, 690);
        WriteText(content, "Left column second passage.", 72, 630);
        WriteText(content, "Right column second passage.", 330, 620);
    }

    private static void CreateHeaderFooterNoise(PDDocument document)
    {
        for (int pageNumber = 1; pageNumber <= 2; pageNumber++)
        {
            PDPage page = AddPage(document);
            using PDPageContentStream content = new(document, page);
            WriteText(content, "Quality Suite Running Header", 72, 770, size: 9);
            WriteText(
                content,
                $"Page {pageNumber} body first paragraph.",
                72,
                690);
            WriteText(
                content,
                $"Page {pageNumber} body second paragraph.",
                72,
                650);
            WriteText(content, $"Quality Suite Footer {pageNumber}", 72, 30, size: 9);
        }
    }

    private static PDPage AddPage(PDDocument document)
    {
        PDPage page = new();
        document.AddPage(page);
        return page;
    }

    private static void DrawGrid(
        PDPageContentStream content,
        IReadOnlyList<float> horizontal,
        IReadOnlyList<float> vertical)
    {
        foreach (float y in horizontal)
        {
            content.MoveTo(vertical[0], y);
            content.LineTo(vertical[^1], y);
            content.Stroke();
        }

        foreach (float x in vertical)
        {
            content.MoveTo(x, horizontal[^1]);
            content.LineTo(x, horizontal[0]);
            content.Stroke();
        }
    }

    private static void WriteText(
        PDPageContentStream content,
        string text,
        float x,
        float y,
        float size = 12,
        bool bold = false)
    {
        content.BeginText();
        content.SetFont(
            new PDType1Font(
                bold
                    ? PDType1Font.FontName.HELVETICA_BOLD
                    : PDType1Font.FontName.HELVETICA),
            size);
        content.NewLineAtOffset(x, y);
        content.ShowText(text);
        content.EndText();
    }

    private sealed class TaggedFixture : IDisposable
    {
        private readonly PDPageContentStream _content;
        private readonly List<PDStructureElement> _parentOwners = [];
        private bool _completed;

        public TaggedFixture()
        {
            Document = new PDDocument();
            Page = new PDPage();
            Document.AddPage(Page);
            Page.SetStructParents(0);
            Root = new PDStructureTreeRoot();
            Document.GetDocumentCatalog().SetStructureTreeRoot(Root);
            PDMarkInfo markInfo = new();
            markInfo.SetMarked(true);
            Document.GetDocumentCatalog().SetMarkInfo(markInfo);
            _content = new PDPageContentStream(Document, Page);
        }

        public PDDocument Document { get; }

        public PDPage Page { get; }

        public PDStructureTreeRoot Root { get; }

        public PDStructureElement AddElement(PDStructureNode parent, string type)
        {
            PDStructureElement element = new(type, parent);
            parent.AppendKid(element);
            return element;
        }

        public void WriteText(PDStructureElement owner, string text, float y)
        {
            int markedContentId = _parentOwners.Count;
            owner.SetPage(Page);
            owner.AppendKid(markedContentId);
            _parentOwners.Add(owner);
            _content.BeginMarkedContent(
                COSName.GetPDFName(owner.GetStructureType() ?? StandardStructureTypes.Span),
                MarkedContentProperties(markedContentId));
            MarkdownQualityFixtureGenerator.WriteText(_content, text, 72, y);
            _content.EndMarkedContent();
        }

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _content.Dispose();
            COSArray parents = [];
            foreach (PDStructureElement owner in _parentOwners)
            {
                parents.Add(owner.GetCOSObject());
            }

            COSArray numbers = [COSInteger.Get(0), parents];
            COSDictionary parentTreeDictionary = new();
            parentTreeDictionary.SetItem(COSName.GetPDFName("Nums"), numbers);
            Root.SetParentTree(new PDParentTreeNumberTreeNode(parentTreeDictionary));
            Root.SetParentTreeNextKey(1);
            _completed = true;
        }

        public void Dispose()
        {
            if (!_completed)
            {
                _content.Dispose();
            }

            Document.Dispose();
        }

        private static PDPropertyList MarkedContentProperties(int markedContentId)
        {
            COSDictionary dictionary = new();
            dictionary.SetInt(COSName.GetPDFName("MCID"), markedContentId);
            return PDPropertyList.Create(dictionary);
        }
    }
}
