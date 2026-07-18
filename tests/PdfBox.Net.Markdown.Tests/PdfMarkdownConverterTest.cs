using PdfBox.Net.COS;
using PdfBox.Net.Layout;
using PdfBox.Net.Markdown;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.DocumentInterchange.LogicalStructure;
using PdfBox.Net.PDModel.DocumentInterchange.MarkedContent;
using PdfBox.Net.PDModel.DocumentInterchange.TaggedPdf;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Graphics.Image;
using PdfBox.Net.PDModel.Interactive.Action;
using PdfBox.Net.PDModel.Interactive.Annotation;

namespace PdfBox.Net.Markdown.Tests;

public sealed class PdfMarkdownConverterTest
{
    [Fact]
    public void Convert_TaggedStructure_ProducesExactHeadingParagraphListAndTableMarkdown()
    {
        using TaggedFixture fixture = new();
        PDStructureElement document = fixture.AddElement(fixture.Root, StandardStructureTypes.Document);
        PDStructureElement heading = fixture.AddElement(document, StandardStructureTypes.H1);
        heading.SetActualText("Authored heading");
        fixture.WriteText(heading, "Visually ordinary", 700);
        fixture.WriteText(
            fixture.AddElement(document, StandardStructureTypes.P),
            "Authored paragraph",
            675);

        PDStructureElement list = fixture.AddElement(document, StandardStructureTypes.L);
        PDListAttributeObject decimalNumbering = new();
        decimalNumbering.SetListNumbering(PDListAttributeObject.ListNumberingDecimal);
        list.AddAttribute(decimalNumbering);
        AddListItem(fixture, list, "1.", "First item", 640);
        PDStructureElement secondItem = fixture.AddElement(list, StandardStructureTypes.LI);
        fixture.WriteText(fixture.AddElement(secondItem, StandardStructureTypes.Lbl), "2.", 615, 72);
        PDStructureElement secondBody = fixture.AddElement(secondItem, StandardStructureTypes.LBody);
        fixture.WriteText(secondBody, "Second item", 615, 96);
        PDStructureElement nestedList = fixture.AddElement(secondBody, StandardStructureTypes.L);
        PDListAttributeObject bulletNumbering = new();
        bulletNumbering.SetListNumbering(PDListAttributeObject.ListNumberingDisc);
        nestedList.AddAttribute(bulletNumbering);
        AddListItem(fixture, nestedList, "•", "Nested item", 590, 112);

        PDStructureElement table = fixture.AddElement(document, StandardStructureTypes.Table);
        AddTableRow(fixture, table, 540, isHeader: true, "Name", "Value");
        AddTableRow(fixture, table, 515, isHeader: false, "Alpha", "42");

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(fixture.Extract());

        const string expected = """
            # Authored heading

            Authored paragraph

            1. First item
            2. Second item
                - Nested item

            | Name | Value |
            | --- | --- |
            | Alpha | 42 |

            """;
        Assert.Equal(NormalizeNewlines(expected), NormalizeNewlines(result.Markdown));
        Assert.Equal(PdfMarkdownOutputSource.SemanticStructure, result.Source);
        Assert.Equal(PdfMarkdownConfidence.High, result.Confidence);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "markdown-semantic-structure-used" &&
            diagnostic.Source == PdfMarkdownOutputSource.SemanticStructure);
    }

    [Fact]
    public void Convert_TaggedLinkAndFigure_EmitsTargetAltTextAndAsset()
    {
        using TaggedFixture fixture = new();
        PDStructureElement paragraph = fixture.AddElement(fixture.Root, StandardStructureTypes.P);
        PDStructureElement link = fixture.AddElement(paragraph, StandardStructureTypes.Link);
        fixture.WriteText(link, "Linked text", 700);
        fixture.AddUriLink("https://example.com/tagged", 72, 690, 120, 24);
        PDStructureElement figure = fixture.AddElement(fixture.Root, StandardStructureTypes.Figure);
        figure.SetAlternateDescription("Red test square");
        fixture.WriteImage(figure, [255, 0, 0], 72, 620, 40, 40);

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(fixture.Extract());

        Assert.StartsWith("[Linked text](https://example.com/tagged)", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("![Red test square](", result.Markdown, StringComparison.Ordinal);
        PdfLayoutImageAsset asset = Assert.Single(result.Assets);
        Assert.Contains(
            $"![Red test square]({asset.RelativePath})",
            result.Markdown,
            StringComparison.Ordinal);

        string directory = Path.Combine(Path.GetTempPath(), "pdfbox-net-markdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            result.WriteToDirectory(directory);
            Assert.Equal(result.Markdown, File.ReadAllText(Path.Combine(directory, "document.md")));
            Assert.True(File.Exists(Path.Combine(directory, asset.RelativePath)));
            string diagnostics = File.ReadAllText(Path.Combine(directory, "diagnostics.json"));
            Assert.Contains("\"source\": \"SemanticStructure\"", diagnostics, StringComparison.Ordinal);
            Assert.Contains("\"code\": \"markdown-semantic-structure-used\"", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Convert_TaggedBlocks_PreserveAuthoredOrderBeforeVisualGeometry()
    {
        using TaggedFixture fixture = new();
        PDStructureElement document = fixture.AddElement(fixture.Root, StandardStructureTypes.Document);
        fixture.WriteText(
            fixture.AddElement(document, StandardStructureTypes.P),
            "Authored first",
            500);
        fixture.WriteText(
            fixture.AddElement(document, StandardStructureTypes.P),
            "Authored second",
            700);

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(fixture.Extract());

        Assert.True(
            result.Markdown.IndexOf("Authored first", StringComparison.Ordinal) <
            result.Markdown.IndexOf("Authored second", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_UntaggedText_UsesConservativeReadingOrderFallback()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        using (PDPageContentStream content = new(document, page))
        {
            content.BeginText();
            content.SetFont(new PDType1Font(PDType1Font.FontName.HELVETICA_BOLD), 22);
            content.NewLineAtOffset(72, 700);
            content.ShowText("Fallback heading");
            content.EndText();
            content.BeginText();
            content.SetFont(new PDType1Font(PDType1Font.FontName.HELVETICA), 12);
            content.NewLineAtOffset(72, 650);
            content.ShowText("First paragraph in reading order.");
            content.EndText();
            content.BeginText();
            content.SetFont(new PDType1Font(PDType1Font.FontName.HELVETICA), 12);
            content.NewLineAtOffset(72, 625);
            content.ShowText("Second paragraph follows.");
            content.EndText();
        }

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(PdfLayoutExtractor.Extract(document));

        Assert.StartsWith("# Fallback heading", result.Markdown, StringComparison.Ordinal);
        Assert.True(
            result.Markdown.IndexOf("First paragraph", StringComparison.Ordinal) <
            result.Markdown.IndexOf("Second paragraph", StringComparison.Ordinal));
        Assert.Equal(
            "Fallback heading First paragraph in reading order. Second paragraph follows.",
            string.Join(
                " ",
                result.Markdown
                    .Replace("#", "", StringComparison.Ordinal)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(PdfMarkdownOutputSource.HeuristicFallback, result.Source);
        Assert.Equal(PdfMarkdownConfidence.Low, result.Confidence);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "markdown-layout-fallback-used");
    }

    [Fact]
    public void Convert_RectangularUntaggedTable_EmitsMeasuredMarkdownGrid()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        using (PDPageContentStream content = new(document, page))
        {
            WriteUntaggedText(
                content,
                "A short introduction establishes ordinary page text.",
                72,
                700);
            foreach (float y in new[] { 630f, 600f, 570f, 540f })
            {
                content.MoveTo(72, y);
                content.LineTo(360, y);
                content.Stroke();
            }

            foreach (float x in new[] { 72f, 210f, 360f })
            {
                content.MoveTo(x, 540);
                content.LineTo(x, 630);
                content.Stroke();
            }

            WriteUntaggedText(content, "Name", 84, 610, bold: true);
            WriteUntaggedText(content, "Value", 222, 610, bold: true);
            WriteUntaggedText(content, "Alpha", 84, 580);
            WriteUntaggedText(content, "42", 222, 580);
            WriteUntaggedText(content, "Beta", 84, 550);
            WriteUntaggedText(content, "84", 222, 550);
        }

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(PdfLayoutExtractor.Extract(document));

        Assert.Contains("| Name | Value |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Alpha | 42 |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Beta | 84 |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "markdown-untagged-table-inferred" &&
            diagnostic.Source == PdfMarkdownOutputSource.HeuristicFallback);
    }

    [Fact]
    public void Convert_UntaggedConcurrentColumns_ReportsLowConfidenceReadingOrder()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        using (PDPageContentStream content = new(document, page))
        {
            WriteUntaggedText(content, "Left column first passage.", 72, 700);
            WriteUntaggedText(content, "Right column first passage.", 330, 690);
            WriteUntaggedText(content, "Left column second passage.", 72, 630);
            WriteUntaggedText(content, "Right column second passage.", 330, 620);
        }

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(PdfLayoutExtractor.Extract(document));

        Assert.Equal(PdfMarkdownConfidence.Low, result.Confidence);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "markdown-untagged-multicolumn-ambiguous" &&
            diagnostic.Severity == PdfMarkdownDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Convert_IrregularTaggedTable_ReportsDeterministicFallbackDiagnostic()
    {
        using TaggedFixture fixture = new();
        PDStructureElement table = fixture.AddElement(fixture.Root, StandardStructureTypes.Table);
        PDStructureElement row = fixture.AddElement(table, StandardStructureTypes.TR);
        PDStructureElement cell = fixture.AddElement(row, StandardStructureTypes.TH);
        PDTableAttributeObject attributes = new();
        attributes.SetColSpan(2);
        cell.AddAttribute(attributes);
        fixture.WriteText(cell, "Wide heading", 700);

        PdfMarkdownDocument result = PdfMarkdownConverter.Convert(fixture.Extract());

        Assert.Contains("Wide heading", result.Markdown, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "markdown-table-not-rectangular" &&
            diagnostic.Severity == PdfMarkdownDiagnosticSeverity.Warning &&
            diagnostic.PageNumber == 1);
        Assert.Equal(PdfMarkdownConfidence.Medium, result.Confidence);
    }

    private static PDStructureElement AddListItem(
        TaggedFixture fixture,
        PDStructureElement list,
        string label,
        string body,
        float y,
        float labelX = 72)
    {
        PDStructureElement item = fixture.AddElement(list, StandardStructureTypes.LI);
        fixture.WriteText(fixture.AddElement(item, StandardStructureTypes.Lbl), label, y, labelX);
        fixture.WriteText(fixture.AddElement(item, StandardStructureTypes.LBody), body, y, labelX + 24);
        return item;
    }

    private static void AddTableRow(
        TaggedFixture fixture,
        PDStructureElement table,
        float y,
        bool isHeader,
        string first,
        string second)
    {
        PDStructureElement row = fixture.AddElement(table, StandardStructureTypes.TR);
        string cellType = isHeader ? StandardStructureTypes.TH : StandardStructureTypes.TD;
        fixture.WriteText(fixture.AddElement(row, cellType), first, y, 72);
        fixture.WriteText(fixture.AddElement(row, cellType), second, y, 180);
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void WriteUntaggedText(
        PDPageContentStream content,
        string text,
        float x,
        float y,
        bool bold = false)
    {
        content.BeginText();
        content.SetFont(
            new PDType1Font(
                bold
                    ? PDType1Font.FontName.HELVETICA_BOLD
                    : PDType1Font.FontName.HELVETICA),
            12);
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

        public void WriteText(PDStructureElement owner, string text, float y, float x = 72)
        {
            int markedContentId = AddMarkedContent(owner);
            _content.BeginMarkedContent(
                COSName.GetPDFName(owner.GetStructureType() ?? StandardStructureTypes.Span),
                MarkedContentProperties(markedContentId));
            _content.BeginText();
            _content.SetFont(new PDType1Font(PDType1Font.FontName.HELVETICA), 12);
            _content.NewLineAtOffset(x, y);
            _content.ShowText(text);
            _content.EndText();
            _content.EndMarkedContent();
        }

        public void WriteImage(
            PDStructureElement owner,
            byte[] rgb,
            float x,
            float y,
            float width,
            float height)
        {
            int markedContentId = AddMarkedContent(owner);
            PDImageXObject image = LosslessFactory.CreateFromRawData(Document, rgb, 1, 1, 8, 3);
            _content.BeginMarkedContent(
                COSName.GetPDFName(owner.GetStructureType() ?? StandardStructureTypes.Figure),
                MarkedContentProperties(markedContentId));
            _content.DrawImage(image, x, y, width, height);
            _content.EndMarkedContent();
        }

        public void AddUriLink(string uri, float x, float y, float width, float height)
        {
            PDAnnotationLink annotation = new();
            annotation.SetRectangle(new PDRectangle(x, y, width, height));
            PDActionURI action = new();
            action.SetURI(uri);
            annotation.SetAction(action);
            Page.SetAnnotations([annotation]);
        }

        public PdfLayoutDocument Extract()
        {
            Complete();
            return PdfLayoutExtractor.Extract(Document, new PdfLayoutOptions
            {
                IncludeImageAssets = true
            });
        }

        public void Dispose()
        {
            if (!_completed)
            {
                _content.Dispose();
            }

            Document.Dispose();
        }

        private int AddMarkedContent(PDStructureElement owner)
        {
            int markedContentId = _parentOwners.Count;
            owner.SetPage(Page);
            owner.AppendKid(markedContentId);
            _parentOwners.Add(owner);
            return markedContentId;
        }

        private void Complete()
        {
            if (_completed)
            {
                return;
            }

            _content.Dispose();
            COSArray parents = new();
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

        private static PDPropertyList MarkedContentProperties(int markedContentId)
        {
            COSDictionary dictionary = new();
            dictionary.SetInt(COSName.GetPDFName("MCID"), markedContentId);
            return PDPropertyList.Create(dictionary);
        }
    }
}
