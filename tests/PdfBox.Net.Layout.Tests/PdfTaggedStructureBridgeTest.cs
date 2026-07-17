using PdfBox.Net.COS;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.DocumentInterchange.LogicalStructure;
using PdfBox.Net.PDModel.DocumentInterchange.MarkedContent;
using PdfBox.Net.PDModel.DocumentInterchange.TaggedPdf;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Graphics.Image;
using PdfBox.Net.PDModel.Interactive.Action;
using PdfBox.Net.PDModel.Interactive.Annotation;

namespace PdfBox.Net.Layout.Tests;

public sealed class PdfTaggedStructureBridgeTest
{
    [Fact]
    public void Extract_ProjectsAuthoredHeadingParagraphAndActualTextBeforeHeuristics()
    {
        using TaggedFixture fixture = new();
        fixture.Root.SetRoleMap(new Dictionary<string, string>
        {
            ["CustomHeading"] = StandardStructureTypes.H1
        });
        PDStructureElement document = fixture.AddElement(fixture.Root, StandardStructureTypes.Document);
        PDStructureElement heading = fixture.AddElement(document, "CustomHeading");
        heading.SetActualText("Authored heading");
        heading.SetLanguage("en-GB");
        heading.SetTitle("Heading metadata");
        fixture.WriteText(heading, "Visually ordinary", 700);
        PDStructureElement paragraph = fixture.AddElement(document, StandardStructureTypes.P);
        fixture.WriteText(paragraph, "Authored paragraph", 670);

        PdfLayoutDocument layout = fixture.Extract();

        PdfTaggedStructureDocument tagged = Assert.IsType<PdfTaggedStructureDocument>(layout.TaggedStructure);
        PdfTaggedStructureElement taggedHeading = Assert.Single(
            tagged.Elements,
            static element => element.Kind == PdfTaggedStructureKind.Heading);
        Assert.Equal("CustomHeading", taggedHeading.StructureType);
        Assert.Equal(StandardStructureTypes.H1, taggedHeading.StandardStructureType);
        Assert.Equal("Authored heading", taggedHeading.ActualText);
        Assert.Equal("en-GB", taggedHeading.Language);
        Assert.Equal("Heading metadata", taggedHeading.Title);
        PdfTaggedContentReference headingContent = Assert.Single(taggedHeading.ContentReferences);
        Assert.True(headingContent.IsResolved);
        Assert.Equal(0, headingContent.MarkedContentId);
        Assert.All(headingContent.TextRuns, static run => Assert.Equal(0, run.MarkedContentId));
        Assert.Contains("Visually ordinary", Assert.Single(layout.Pages).Text);

        PdfSemanticDocument semantic = PdfSemanticExtractor.Extract(layout);
        PdfSemanticElement semanticHeading = Assert.Single(
            Assert.Single(semantic.Pages).Elements,
            static element => element.TaggedStructure?.Kind == PdfTaggedStructureKind.Heading);
        Assert.Equal(PdfSemanticElementKind.Heading, semanticHeading.Kind);
        Assert.Equal(1, semanticHeading.HeadingLevel);
        Assert.Equal("Authored heading", semanticHeading.Text);
        Assert.Empty(semanticHeading.Lines);
        Assert.Contains(
            Assert.Single(semantic.Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.Paragraph &&
                element.Text == "Authored paragraph" &&
                element.TaggedStructure != null);
    }

    [Fact]
    public void Extract_PreservesNestedListAndRectangularTableRelationships()
    {
        using TaggedFixture fixture = new();
        PDStructureElement document = fixture.AddElement(fixture.Root, StandardStructureTypes.Document);
        PDStructureElement list = fixture.AddElement(document, StandardStructureTypes.L);
        PDListAttributeObject decimalNumbering = new();
        decimalNumbering.SetListNumbering(PDListAttributeObject.ListNumberingDecimal);
        list.AddAttribute(decimalNumbering);

        PDStructureElement firstItem = fixture.AddElement(list, StandardStructureTypes.LI);
        fixture.WriteText(fixture.AddElement(firstItem, StandardStructureTypes.Lbl), "1.", 700, 72);
        fixture.WriteText(fixture.AddElement(firstItem, StandardStructureTypes.LBody), "First item", 700, 96);
        PDStructureElement secondItem = fixture.AddElement(list, StandardStructureTypes.LI);
        fixture.WriteText(fixture.AddElement(secondItem, StandardStructureTypes.Lbl), "2.", 675, 72);
        PDStructureElement secondBody = fixture.AddElement(secondItem, StandardStructureTypes.LBody);
        fixture.WriteText(secondBody, "Second item", 675, 96);
        PDStructureElement nestedList = fixture.AddElement(secondBody, StandardStructureTypes.L);
        PDListAttributeObject bulletNumbering = new();
        bulletNumbering.SetListNumbering(PDListAttributeObject.ListNumberingDisc);
        nestedList.AddAttribute(bulletNumbering);
        PDStructureElement nestedItem = fixture.AddElement(nestedList, StandardStructureTypes.LI);
        fixture.WriteText(fixture.AddElement(nestedItem, StandardStructureTypes.Lbl), "•", 650, 112);
        fixture.WriteText(fixture.AddElement(nestedItem, StandardStructureTypes.LBody), "Nested item", 650, 130);

        PDStructureElement table = fixture.AddElement(document, StandardStructureTypes.Table);
        AddTableRow(fixture, table, 590, isHeader: true, "Name", "Value");
        AddTableRow(fixture, table, 565, isHeader: false, "Alpha", "42");

        PdfLayoutDocument layout = fixture.Extract();
        PdfTaggedStructureDocument tagged = Assert.IsType<PdfTaggedStructureDocument>(layout.TaggedStructure);
        PdfTaggedStructureElement taggedList = Assert.Single(
            tagged.Elements,
            static element => element.Kind == PdfTaggedStructureKind.List &&
                element.Attributes.ListNumbering == PDListAttributeObject.ListNumberingDecimal);
        Assert.Equal(2, taggedList.Children.Count(static child => child.Kind == PdfTaggedStructureKind.ListItem));
        Assert.Contains(
            tagged.Elements,
            static element => element.Kind == PdfTaggedStructureKind.List &&
                element.Attributes.ListNumbering == PDListAttributeObject.ListNumberingDisc);

        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement semanticList = Assert.Single(
            semanticPage.Elements,
            static element => element.TaggedStructure?.Attributes.ListNumbering ==
                PDListAttributeObject.ListNumberingDecimal);
        Assert.Equal(PdfSemanticListKind.Ordered, semanticList.SemanticList!.Kind);
        Assert.Equal(2, semanticList.SemanticList.Items.Count);
        PdfSemanticList nested = Assert.Single(semanticList.SemanticList.Items[1].NestedLists);
        Assert.Equal(PdfSemanticListKind.Unordered, nested.Kind);
        Assert.Equal("Nested item", Assert.Single(nested.Items).Text);

        PdfSemanticElement semanticTable = Assert.Single(
            semanticPage.Elements,
            static element => element.Kind == PdfSemanticElementKind.Table &&
                element.TaggedStructure != null);
        Assert.Equal(2, semanticTable.TableRows.Count);
        Assert.True(semanticTable.TableRows[0].IsHeader);
        Assert.False(semanticTable.TableRows[1].IsHeader);
        Assert.Equal(["Name", "Value"], semanticTable.TableRows[0].Cells.Select(static cell => cell.Text));
        Assert.Equal(["Alpha", "42"], semanticTable.TableRows[1].Cells.Select(static cell => cell.Text));
    }

    [Fact]
    public void Extract_AssociatesFigureAltTextWithItsMarkedImage()
    {
        using TaggedFixture fixture = new();
        PDStructureElement figure = fixture.AddElement(fixture.Root, StandardStructureTypes.Figure);
        figure.SetAlternateDescription("Red test square");
        fixture.WriteImage(figure, [255, 0, 0], 72, 620, 40, 40);

        PdfLayoutDocument layout = fixture.Extract();

        PdfTaggedStructureElement taggedFigure = Assert.Single(
            Assert.IsType<PdfTaggedStructureDocument>(layout.TaggedStructure).Elements,
            static element => element.Kind == PdfTaggedStructureKind.Figure);
        PdfTaggedContentReference reference = Assert.Single(taggedFigure.ContentReferences);
        PdfLayoutImage image = Assert.Single(reference.Images);
        Assert.Equal(reference.MarkedContentId, image.MarkedContentId);
        Assert.Equal("Red test square", image.AlternateDescription);
    }

    [Fact]
    public void Extract_PreservesInterleavedStructureKidOrder()
    {
        using TaggedFixture fixture = new();
        PDStructureElement paragraph = fixture.AddElement(fixture.Root, StandardStructureTypes.P);
        fixture.WriteText(paragraph, "Before ", 700, 72);
        PDStructureElement span = fixture.AddElement(paragraph, StandardStructureTypes.Span);
        fixture.WriteText(span, "middle", 700, 112);
        fixture.WriteText(paragraph, " after", 700, 148);

        PdfLayoutDocument layout = fixture.Extract();

        PdfTaggedStructureElement taggedParagraph = Assert.Single(
            Assert.IsType<PdfTaggedStructureDocument>(layout.TaggedStructure).Roots);
        Assert.Collection(
            taggedParagraph.Kids,
            static kid => Assert.IsType<PdfTaggedContentKid>(kid),
            static kid => Assert.IsType<PdfTaggedElementKid>(kid),
            static kid => Assert.IsType<PdfTaggedContentKid>(kid));
        PdfSemanticElement semanticParagraph = Assert.Single(
            Assert.Single(PdfSemanticExtractor.Extract(layout).Pages).Elements,
            static element => element.TaggedStructure?.Kind == PdfTaggedStructureKind.Paragraph);
        Assert.Equal("Before middle after", semanticParagraph.Text);
    }

    [Fact]
    public void Extract_PreservesTaggedLinkRoleAlongsideUriAnnotation()
    {
        using TaggedFixture fixture = new();
        PDStructureElement paragraph = fixture.AddElement(fixture.Root, StandardStructureTypes.P);
        PDStructureElement linkElement = fixture.AddElement(paragraph, StandardStructureTypes.Link);
        fixture.WriteText(linkElement, "Linked text", 700);
        PDAnnotationLink annotation = new();
        annotation.SetRectangle(new PDRectangle(72, 680, 120, 24));
        PDActionURI action = new();
        action.SetURI("https://example.com/tagged");
        annotation.SetAction(action);
        fixture.Page.SetAnnotations([annotation]);

        PdfLayoutDocument layout = fixture.Extract();

        PdfTaggedStructureElement taggedLink = Assert.Single(
            Assert.IsType<PdfTaggedStructureDocument>(layout.TaggedStructure).Elements,
            static element => element.Kind == PdfTaggedStructureKind.Link);
        Assert.True(Assert.Single(taggedLink.ContentReferences).IsResolved);
        PdfLayoutLink layoutLink = Assert.Single(Assert.Single(layout.Pages).Links);
        Assert.Equal(PdfLayoutLinkKind.Uri, layoutLink.Kind);
        Assert.Equal("https://example.com/tagged", layoutLink.Uri);
    }

    [Fact]
    public void Extract_ReportsStableDiagnosticsForUnresolvedAndUnsupportedStructure()
    {
        using TaggedFixture fixture = new();
        PDStructureElement unsupported = fixture.AddElement(fixture.Root, "VendorThing");
        unsupported.SetPage(fixture.Page);
        unsupported.AppendKid(42);

        PdfLayoutDocument layout = fixture.Extract();

        Assert.Contains(layout.Diagnostics, static diagnostic =>
            diagnostic.Code == "tagged-structure-type-unsupported" && diagnostic.PageNumber == 1);
        Assert.Contains(layout.Diagnostics, static diagnostic =>
            diagnostic.Code == "tagged-structure-parent-tree-missing" && diagnostic.PageNumber == 1);
        Assert.Contains(layout.Diagnostics, static diagnostic =>
            diagnostic.Code == "tagged-structure-mcid-unresolved" && diagnostic.PageNumber == 1);
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
        PDStructureElement firstCell = fixture.AddElement(row, cellType);
        PDStructureElement secondCell = fixture.AddElement(row, cellType);
        if (isHeader)
        {
            PDTableAttributeObject attributes = new();
            attributes.SetScope(PDTableAttributeObject.ScopeColumn);
            firstCell.AddAttribute(attributes);
        }

        fixture.WriteText(firstCell, first, y, 72);
        fixture.WriteText(secondCell, second, y, 180);
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

        public void WriteText(
            PDStructureElement owner,
            string text,
            float y,
            float x = 72)
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

        public PdfLayoutDocument Extract()
        {
            Complete();
            return PdfLayoutExtractor.Extract(Document);
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
