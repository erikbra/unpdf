using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfBox.Net.COS;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.DocumentInterchange.LogicalStructure;
using PdfBox.Net.PDModel.DocumentInterchange.MarkedContent;
using PdfBox.Net.PDModel.DocumentInterchange.TaggedPdf;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Graphics.Image;

namespace PdfBox.Net.Html.Tests;

public sealed class PdfHtmlTaggedStructureTest
{
    [Fact]
    public void Convert_SemanticMode_PrefersAuthoredTagsAndMetadata()
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
        fixture.WriteText(
            fixture.AddElement(document, StandardStructureTypes.P),
            "Authored paragraph",
            675);

        XDocument dom = Convert(fixture);

        XElement h1 = Assert.Single(dom.Descendants("h1"));
        Assert.Equal("Authored heading", h1.Value);
        Assert.Equal(StandardStructureTypes.H1, h1.Attribute("data-pdf-structure-type")?.Value);
        Assert.Equal("en-GB", h1.Attribute("lang")?.Value);
        Assert.Equal("Heading metadata", h1.Attribute("title")?.Value);
        Assert.DoesNotContain("Visually ordinary", dom.Root!.Value, StringComparison.Ordinal);
        XElement paragraph = Assert.Single(dom.Descendants("p"), static element =>
            element.Attribute("data-pdf-structure-type")?.Value == StandardStructureTypes.P);
        Assert.Equal("Authored paragraph", paragraph.Value.Trim());
    }

    [Fact]
    public void Convert_SemanticMode_EmitsNestedListsAndRectangularTables()
    {
        using TaggedFixture fixture = new();
        PDStructureElement document = fixture.AddElement(fixture.Root, StandardStructureTypes.Document);
        PDStructureElement list = fixture.AddElement(document, StandardStructureTypes.L);
        PDListAttributeObject decimalNumbering = new();
        decimalNumbering.SetListNumbering(PDListAttributeObject.ListNumberingDecimal);
        list.AddAttribute(decimalNumbering);

        AddListItem(fixture, list, "1.", "First item", 700);
        PDStructureElement secondItem = fixture.AddElement(list, StandardStructureTypes.LI);
        fixture.WriteText(fixture.AddElement(secondItem, StandardStructureTypes.Lbl), "2.", 675, 72);
        PDStructureElement secondBody = fixture.AddElement(secondItem, StandardStructureTypes.LBody);
        fixture.WriteText(secondBody, "Second item", 675, 96);
        PDStructureElement nestedList = fixture.AddElement(secondBody, StandardStructureTypes.L);
        PDListAttributeObject bulletNumbering = new();
        bulletNumbering.SetListNumbering(PDListAttributeObject.ListNumberingDisc);
        nestedList.AddAttribute(bulletNumbering);
        AddListItem(fixture, nestedList, "•", "Nested item", 650, 112);

        PDStructureElement table = fixture.AddElement(document, StandardStructureTypes.Table);
        AddTableRow(fixture, table, 590, isHeader: true, "Name", "Value");
        AddTableRow(fixture, table, 565, isHeader: false, "Alpha", "42");

        XDocument dom = Convert(fixture);

        XElement orderedList = Assert.Single(dom.Descendants("ol"));
        Assert.Equal(StandardStructureTypes.L, orderedList.Attribute("data-pdf-structure-type")?.Value);
        XElement unorderedList = Assert.Single(orderedList.Descendants("ul"));
        Assert.Equal("Nested item", Assert.Single(unorderedList.Elements("li")).Value.Trim());
        XElement renderedTable = Assert.Single(dom.Descendants("table"));
        Assert.Equal(StandardStructureTypes.Table, renderedTable.Attribute("data-pdf-structure-type")?.Value);
        Assert.Equal(["Name", "Value"], renderedTable.Descendants("thead").Descendants("th").Select(static cell => cell.Value.Trim()));
        Assert.Equal(["Alpha", "42"], renderedTable.Descendants("tbody").Descendants("td").Select(static cell => cell.Value.Trim()));
    }

    [Fact]
    public void Convert_Figure_UsesAuthoredAlternateDescription()
    {
        using TaggedFixture fixture = new();
        PDStructureElement figure = fixture.AddElement(fixture.Root, StandardStructureTypes.Figure);
        figure.SetAlternateDescription("Red test square");
        fixture.WriteImage(figure, [255, 0, 0], 72, 620, 40, 40);

        XDocument dom = Convert(fixture);

        XElement image = Assert.Single(dom.Descendants("img"));
        Assert.Equal("Red test square", image.Attribute("alt")?.Value);
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

    private static XDocument Convert(TaggedFixture fixture)
    {
        string html = PdfHtmlConverter.Convert(fixture.Extract(), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html;
        return XDocument.Parse(
            Regex.Replace(html, "<!doctype html>\\s*", "", RegexOptions.IgnoreCase),
            LoadOptions.PreserveWhitespace);
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
