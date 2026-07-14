using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Playwright;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

namespace PdfBox.Net.Html.Tests;

public class PdfHtmlSemanticIslandTest
{
    private static readonly string[] W9CertificationStatements =
    [
        "The number shown on this form is my correct taxpayer identification number (or I am waiting for a number to be issued to me); and",
        "I am not subject to backup withholding because (a) I am exempt from backup withholding, or (b) I have not been notified by the Internal Revenue Service (IRS) that I am subject to backup withholding as a result of a failure to report all interest or dividends, or (c) the IRS has notified me that I am no longer subject to backup withholding; and",
        "I am a U.S. citizen or other U.S. person (defined below); and",
        "The FATCA code(s) entered on this form (if any) indicating that I am exempt from FATCA reporting is correct."
    ];
    private static readonly (string Name, string Country)[] LinkedMembers =
    [
        ("Alpha Studio", "Germany"),
        ("Beta Press", "China"),
        ("Color Experts", "Latin America"),
        ("Delta Imaging", "International"),
        ("Epsilon Systems", "Europe"),
        ("Future Academy", "India"),
        ("Government Printing Institute", "India"),
        ("University Press", "Malaysia"),
        ("Visual Arts Faculty", "Slovenia"),
        ("Workflow Center", "Belgium")
    ];

    [Fact]
    public void Convert_FixedFormFallback_EmitsProvenListAtItsSourceRunSlot()
    {
        PdfLayoutDocument layout = CreateFixedFormListLayout();
        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement listElement = Assert.Single(
            semanticPage.Elements,
            static element => element.Kind == PdfSemanticElementKind.List);

        PdfHtmlConverter.FallbackSemanticIsland island = Assert.Single(
            PdfHtmlConverter.FallbackSemanticIslands(page, semanticPage));
        Assert.Same(listElement, island.Element);
        Assert.Equal(1, island.FirstRunIndex);
        Assert.Equal(4, island.LastRunIndex);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, SemanticContinuousOptions());
        XDocument dom = ParseHtml(converted.Html);
        XElement fallbackPage = Assert.Single(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        XElement list = Assert.Single(fallbackPage.Elements("ol"));
        Assert.True(HasClass(list, "pdf-semantic-fallback-island"));
        Assert.Equal(
            ["First complete statement.", "Second complete statement.", "Third complete statement.", "Fourth complete statement."],
            list.Elements("li").Select(static item => NormalizeWhitespace(item.Value)).ToArray());

        XElement[] children = fallbackPage.Elements().ToArray();
        int preceding = Array.FindIndex(children, static element => element.Value.Contains("Certification begins", StringComparison.Ordinal));
        int listIndex = Array.IndexOf(children, list);
        int following = Array.FindIndex(children, static element => element.Value.Contains("Certification ends", StringComparison.Ordinal));
        Assert.True(preceding < listIndex && listIndex < following);
        Assert.DoesNotContain(fallbackPage.Elements("span"), element =>
            HasClass(element, "pdf-text-run") && element.Value.Contains("complete statement", StringComparison.Ordinal));
        Assert.Single(fallbackPage.Elements("form"));
        Assert.Equal("width:612pt;height:792pt", fallbackPage.Attribute("style")?.Value);
    }

    [Fact]
    public void FallbackSemanticIslands_RejectsEquivalentButUnprovenSourceObjects()
    {
        PdfTextRun[] pageRuns =
        [
            Run("1. First item", 72f, 100f),
            Run("2. Second item", 72f, 115f)
        ];
        PdfLayoutPage page = Page(pageRuns);
        PdfTextRun[] clones = pageRuns.Select(CloneRun).ToArray();
        PdfSemanticElement list = ListElement(clones.Select(SemanticLine).ToArray());

        Assert.Empty(PdfHtmlConverter.FallbackSemanticIslands(page, new PdfSemanticPage(1, [list])));
    }

    [Fact]
    public void FallbackSemanticIslands_RejectsAmbiguousSemanticGlyphOwnership()
    {
        PdfTextRun[] runs =
        [
            Run("1. First item", 72f, 100f),
            Run("2. Second item", 72f, 115f)
        ];
        PdfLayoutPage page = Page(runs);
        PdfSemanticLine[] lines = runs.Select(SemanticLine).ToArray();
        PdfSemanticElement list = ListElement(lines);
        PdfSemanticElement competing = new(
            PdfSemanticElementKind.Paragraph,
            string.Join(" ", lines.Select(static line => line.Text)),
            Union(lines.Select(static line => line.Bounds)),
            lines);

        Assert.Empty(PdfHtmlConverter.FallbackSemanticIslands(
            page,
            new PdfSemanticPage(1, [list, competing])));
    }

    [Fact]
    public void FallbackSemanticIslands_RejectsOverlappingProvenRegions()
    {
        PdfTextRun[] firstRuns =
        [
            Run("1. First A", 72f, 100f),
            Run("2. Second A", 72f, 115f)
        ];
        PdfTextRun[] secondRuns =
        [
            Run("1. First B", 72f, 100f),
            Run("2. Second B", 72f, 115f)
        ];
        PdfLayoutPage page = Page([.. firstRuns, .. secondRuns]);
        PdfSemanticElement first = ListElement(firstRuns.Select(SemanticLine).ToArray());
        PdfSemanticElement second = ListElement(secondRuns.Select(SemanticLine).ToArray());

        Assert.Empty(PdfHtmlConverter.FallbackSemanticIslands(
            page,
            new PdfSemanticPage(1, [first, second])));
    }

    [Fact]
    public void Convert_W9PageOne_PreservesCertificationListInsideFixedFormPage()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("irs-w9.pdf"));
        Assert.Equal(6, document.GetNumberOfPages());
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeFontAssets = true
        });
        PdfLayoutPage sourcePage = layout.Pages[0];
        Assert.True(sourcePage.FormControls.Count >= 20);
        PdfSemanticPage semanticSourcePage = PdfSemanticExtractor.Extract(layout).Pages[0];
        PdfSemanticElement sourceList = Assert.Single(
            semanticSourcePage.Elements,
            static element => element.Kind == PdfSemanticElementKind.List);
        Assert.Equal(4, Assert.IsType<PdfSemanticList>(sourceList.SemanticList).Items.Count);
        PdfHtmlConverter.FallbackSemanticIsland sourceIsland = Assert.Single(
            PdfHtmlConverter.FallbackSemanticIslands(sourcePage, semanticSourcePage));
        Assert.Equal(6, sourceIsland.OwnedRuns.Count);

        PdfHtmlDocument fixedDocument = PdfHtmlConverter.Convert(layout);
        PdfHtmlDocument semanticDocument = PdfHtmlConverter.Convert(layout, SemanticContinuousOptions());
        XElement fixedPage = PageElement(ParseHtml(fixedDocument.Html), 1);
        XElement semanticPage = PageElement(ParseHtml(semanticDocument.Html), 1);

        Assert.True(HasClass(semanticPage, "pdf-semantic-layout-fallback-page"));
        XElement list = Assert.Single(semanticPage.Elements("ol"));
        Assert.True(HasClass(list, "pdf-semantic-fallback-island"));
        Assert.Equal(
            W9CertificationStatements.Select(NormalizeWhitespace),
            list.Elements("li").Select(static item => NormalizeWhitespace(item.Value)));
        Assert.Equal(4, list.Elements("li").Count());

        string[] distinctiveText =
        [
            "number shown on this form",
            "not subject to backup withholding",
            "U.S. citizen or other U.S. person",
            "FATCA code(s) entered on this form"
        ];
        XElement[] remainingFixedRuns = semanticPage.Elements("span")
            .Where(static element => HasClass(element, "pdf-text-run"))
            .ToArray();
        Assert.All(distinctiveText, text => Assert.DoesNotContain(
            remainingFixedRuns,
            run => run.Value.Contains(text, StringComparison.Ordinal)));
        XElement[] originalFixedRuns = fixedPage.Elements("span")
            .Where(static element => HasClass(element, "pdf-text-run"))
            .ToArray();
        Assert.Equal(sourcePage.Runs.Count, originalFixedRuns.Length);
        Assert.Equal(
            originalFixedRuns
                .Where((_, index) => index < sourceIsland.FirstRunIndex || index > sourceIsland.LastRunIndex)
                .Select(FixedRunPresentationSignature),
            remainingFixedRuns.Select(FixedRunPresentationSignature));

        XElement[] pageChildren = semanticPage.Elements().ToArray();
        int introduction = Array.FindIndex(pageChildren, static element =>
            element.Value.Contains("Under penalties of perjury", StringComparison.Ordinal));
        int listIndex = Array.IndexOf(pageChildren, list);
        int instructions = Array.FindIndex(pageChildren, static element =>
            element.Value.Contains("Certification instructions", StringComparison.Ordinal));
        Assert.True(introduction < listIndex && listIndex < instructions);

        Assert.Equal(fixedPage.Attribute("style")?.Value, semanticPage.Attribute("style")?.Value);
        Assert.Equal(
            SerializedDirectChildrenByClass(fixedPage, "pdf-vector-layer"),
            SerializedDirectChildrenByClass(semanticPage, "pdf-vector-layer"));
        Assert.Equal(
            SerializedDirectChildrenByClass(fixedPage, "pdf-image"),
            SerializedDirectChildrenByClass(semanticPage, "pdf-image"));
        Assert.Equal(
            SerializedDirectChildrenByClass(fixedPage, "pdf-link-overlay"),
            SerializedDirectChildrenByClass(semanticPage, "pdf-link-overlay"));
        Assert.Equal(
            Assert.Single(fixedPage.Elements("form")).ToString(SaveOptions.DisableFormatting),
            Assert.Single(semanticPage.Elements("form")).ToString(SaveOptions.DisableFormatting));
        Assert.Equal(
            sourcePage.FormControls.Count,
            semanticPage.Descendants().Count(static element =>
                element.Name.LocalName is "input" or "select" or "textarea"));
    }

    [Fact]
    public void Convert_LinkedSymbolBulletRows_EmitOneFallbackListWithSemanticLinks()
    {
        PdfLayoutDocument layout = CreateLinkedMemberListLayout();
        PdfLayoutPage sourcePage = Assert.Single(layout.Pages);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement sourceList = Assert.Single(semanticPage.Elements, static element =>
            element.Kind == PdfSemanticElementKind.List);

        PdfHtmlConverter.FallbackSemanticIsland island = Assert.Single(
            PdfHtmlConverter.FallbackSemanticIslands(sourcePage, semanticPage));
        Assert.Same(sourceList, island.Element);
        Assert.Equal(50, island.OwnedRuns.Count);
        Assert.Equal(10, island.OwnedLinks.Count);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, SemanticContinuousOptions());
        XDocument dom = ParseHtml(converted.Html);
        XElement fallbackPage = Assert.Single(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        XElement list = Assert.Single(fallbackPage.Elements("ul"));
        Assert.True(HasClass(list, "pdf-semantic-fallback-island"));
        Assert.True(HasClass(list, "pdf-color-000000-ff"));
        XElement[] items = list.Elements("li").ToArray();
        Assert.Equal(10, items.Length);

        for (int index = 0; index < items.Length; index++)
        {
            (string name, string country) = LinkedMembers[index];
            XElement link = Assert.Single(items[index].Elements("a"));
            Assert.Equal(name, link.Value);
            Assert.Equal($"mailto:member-{index + 1}@example.test", link.Attribute("href")?.Value);
            Assert.Contains(
                link.DescendantsAndSelf(),
                static element => HasClass(element, "pdf-color-82034a-ff"));
            Assert.Equal($"{name} ({country})", NormalizeWhitespace(items[index].Value));
            Assert.DoesNotContain("•", items[index].Value, StringComparison.Ordinal);
        }

        Assert.Empty(ElementsByClass(dom, "pdf-link-overlay"));
        XElement[] remainingFixedRuns = fallbackPage.Elements("span")
            .Where(static element => HasClass(element, "pdf-text-run"))
            .ToArray();
        Assert.DoesNotContain(remainingFixedRuns, static run => run.Value.Contains('•'));
        Assert.All(LinkedMembers, member => Assert.DoesNotContain(
            remainingFixedRuns,
            run => run.Value.Contains(member.Name, StringComparison.Ordinal) ||
                run.Value.Contains(member.Country, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Convert_LinkedSymbolBulletRows_PreserveSourceGeometryInBrowser()
    {
        PdfLayoutDocument layout = CreateLinkedMemberListLayout();
        PdfSemanticElement sourceList = Assert.Single(
            Assert.Single(PdfSemanticExtractor.Extract(layout).Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.List);
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, SemanticContinuousOptions());
        using TempDirectory tempDirectory = new();
        converted.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1000, Height = 1200 }
        });
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);
        await browserPage.EvaluateAsync("() => document.fonts.ready");

        const float cssPixelsPerPoint = 96f / 72f;
        const float tolerancePx = 1f;
        LocatorBoundingBoxResult pageBox = await browserPage.Locator(".pdf-page").BoundingBoxAsync()
            ?? throw new InvalidOperationException("Fallback page did not render a bounding box.");
        LocatorBoundingBoxResult listBox = await browserPage.Locator("ul.pdf-semantic-fallback-island").BoundingBoxAsync()
            ?? throw new InvalidOperationException("Semantic list island did not render a bounding box.");
        AssertWithin(tolerancePx, sourceList.Bounds.X * cssPixelsPerPoint, (float)(listBox.X - pageBox.X));
        AssertWithin(tolerancePx, sourceList.Bounds.Y * cssPixelsPerPoint, (float)(listBox.Y - pageBox.Y));

        ILocator items = browserPage.Locator("ul.pdf-semantic-fallback-island > li");
        Assert.Equal(10, await items.CountAsync());
        LocatorBoundingBoxResult firstItem = await items.First.BoundingBoxAsync()
            ?? throw new InvalidOperationException("First list item did not render a bounding box.");
        LocatorBoundingBoxResult? previousItem = null;
        for (int index = 0; index < sourceList.Lines.Count; index++)
        {
            LocatorBoundingBoxResult itemBox = await items.Nth(index).BoundingBoxAsync()
                ?? throw new InvalidOperationException($"List item {index + 1} did not render a bounding box.");
            float expectedOffset = (sourceList.Lines[index].Bounds.Y - sourceList.Lines[0].Bounds.Y) * cssPixelsPerPoint;
            AssertWithin(tolerancePx, expectedOffset, (float)(itemBox.Y - firstItem.Y));
            if (previousItem != null)
            {
                Assert.True(itemBox.Y >= previousItem.Y + previousItem.Height - tolerancePx);
            }

            previousItem = itemBox;
        }

        ILocator links = browserPage.Locator("ul.pdf-semantic-fallback-island > li > a");
        Assert.Equal(10, await links.CountAsync());
        for (int index = 0; index < await links.CountAsync(); index++)
        {
            LocatorBoundingBoxResult linkBox = await links.Nth(index).BoundingBoxAsync()
                ?? throw new InvalidOperationException($"Member link {index + 1} did not render a bounding box.");
            Assert.True(linkBox.Width > 0 && linkBox.Height > 0);
        }
    }

    [Fact]
    public void FallbackSemanticIslands_RejectsLinkSharedWithContentOutsideList()
    {
        PdfLayoutDocument layout = CreateLinkedMemberListLayout(firstLinkCoversFollowingContent: true);
        PdfLayoutPage sourcePage = Assert.Single(layout.Pages);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.Empty(PdfHtmlConverter.FallbackSemanticIslands(sourcePage, semanticPage));
    }

    private static PdfLayoutDocument CreateFixedFormListLayout()
    {
        PdfTextRun[] runs =
        [
            Run("Certification begins in source order.", 72f, 60f),
            Run("1. First complete statement.", 72f, 100f),
            Run("2. Second complete statement.", 72f, 115f),
            Run("3. Third complete statement.", 72f, 130f),
            Run("4. Fourth complete statement.", 72f, 145f),
            Run("Certification ends after the list.", 72f, 180f)
        ];
        PdfLayoutFormControl control = new(
            0,
            "approval",
            "Approval",
            PdfLayoutFormControlKind.Text,
            new PdfLayoutRectangle(72f, 230f, 180f, 20f));
        PdfLayoutPage page = Page(runs, [control]);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateLinkedMemberListLayout(
        bool firstLinkCoversFollowingContent = false)
    {
        List<PdfTextLine> lines =
        [
            Line(Run("The following members expressed interest:", 78f, 225f))
        ];
        List<PdfLayoutLink> links = [];
        for (int index = 0; index < LinkedMembers.Length; index++)
        {
            PdfTextLine line = LinkedMemberLine(
                LinkedMembers[index].Name,
                LinkedMembers[index].Country,
                252f + index * 15.5f);
            lines.Add(line);
            PdfLayoutRectangle nameBounds = line.Runs[2].Bounds;
            links.Add(new PdfLayoutLink(
                index,
                new PdfLayoutRectangle(
                    nameBounds.X - 2f,
                    nameBounds.Y - 4f,
                    nameBounds.Width + 2.25f,
                    firstLinkCoversFollowingContent && index == 0 ? 190f : 15.4f),
                PdfLayoutLinkKind.Uri,
                $"mailto:member-{index + 1}@example.test",
                null,
                null,
                []));
        }

        lines.Add(Line(Run("Following fixed content.", 78f, 430f)));
        PdfLayoutFormControl control = new(
            0,
            "approval",
            "Approval",
            PdfLayoutFormControlKind.Text,
            new PdfLayoutRectangle(72f, 700f, 180f, 20f));
        return new PdfLayoutDocument([Page(lines, links, [control])], []);
    }

    private static PdfLayoutPage Page(
        IReadOnlyList<PdfTextRun> runs,
        IReadOnlyList<PdfLayoutFormControl>? controls = null)
    {
        PdfTextLine[] lines = runs
            .Select(Line)
            .ToArray();
        return Page(lines, [], controls);
    }

    private static PdfLayoutPage Page(
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfLayoutLink> links,
        IReadOnlyList<PdfLayoutFormControl>? controls = null)
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        return new PdfLayoutPage(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            runs.SelectMany(static run => run.Glyphs).ToArray(),
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            links,
            [],
            formControls: controls);
    }

    private static PdfTextLine Line(PdfTextRun run)
    {
        return new PdfTextLine(run.Text, run.Bounds, [run]);
    }

    private static PdfTextLine LinkedMemberLine(string memberName, string country, float y)
    {
        const float fontSize = 10f;
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutColor accent = new(0.51f, 0.01f, 0.29f, 1f, "DeviceRGB");
        List<PdfTextRun> runs = [];

        AddRun("•", "SymbolMT", 114.05f, y + 2.18f, 4.83f, 4.77f, black);
        AddRun(" ", "ArialMT", 118.8f, y + 2.03f, 2.92f, 4.92f, black);
        float memberWidth = memberName.Length * 5f;
        AddRun(memberName, "SourceSansPro-Regular", 132.05f, y + 0.34f, memberWidth, 6.62f, accent);
        string countryText = $" ({country})";
        float countryX = 132.05f + memberWidth;
        float countryWidth = countryText.Length * 4.5f;
        AddRun(countryText, "SourceSansPro-Regular", countryX, y + 0.34f, countryWidth, 6.62f, black);
        AddRun(" ", "SourceSansPro-Bold", countryX + countryWidth, y, 2.1f, 6.95f, black);

        PdfLayoutRectangle bounds = Union(runs.Select(static run => run.Bounds));
        return new PdfTextLine(string.Concat(runs.Select(static run => run.Text)), bounds, runs);

        void AddRun(
            string text,
            string fontName,
            float x,
            float runY,
            float width,
            float height,
            PdfLayoutColor color)
        {
            PdfLayoutRectangle runBounds = new(x, runY, width, height);
            PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, runBounds, color);
            runs.Add(new PdfTextRun(text, fontName, fontSize, 0f, runBounds, color, [glyph]));
        }
    }

    private static PdfTextRun Run(string text, float x, float y)
    {
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfLayoutRectangle bounds = new(x, y, MathF.Max(24f, text.Length * 4.8f), 8f);
        PdfTextGlyph glyph = new(text, "Helvetica", 10f, 0f, bounds, color);
        return new PdfTextRun(text, "Helvetica", 10f, 0f, bounds, color, [glyph]);
    }

    private static PdfTextRun CloneRun(PdfTextRun source)
    {
        PdfTextGlyph[] glyphs = source.Glyphs
            .Select(static glyph => glyph with { })
            .ToArray();
        return new PdfTextRun(
            source.Text,
            source.FontName,
            source.FontSize,
            source.Direction,
            source.Bounds,
            source.Color,
            glyphs,
            source.PageBounds,
            source.Shadow);
    }

    private static PdfSemanticLine SemanticLine(PdfTextRun run)
    {
        return new PdfSemanticLine(
            run.Text,
            run.Bounds,
            run.FontName,
            run.FontSize,
            run.Direction,
            run.Color,
            [run]);
    }

    private static PdfSemanticElement ListElement(IReadOnlyList<PdfSemanticLine> lines)
    {
        PdfSemanticListItem[] items = lines
            .Select((line, index) => new PdfSemanticListItem(
                line.Text[(line.Text.IndexOf(' ') + 1)..],
                line.Bounds,
                [line],
                $"{index + 1}.",
                markerLength: 2,
                value: index + 1))
            .ToArray();
        PdfSemanticList list = new(
            PdfSemanticListKind.Ordered,
            PdfSemanticListMarkerKind.Decimal,
            items);
        return new PdfSemanticElement(
            PdfSemanticElementKind.List,
            string.Join(Environment.NewLine, lines.Select(static line => line.Text)),
            Union(lines.Select(static line => line.Bounds)),
            lines,
            semanticList: list);
    }

    private static PdfLayoutRectangle Union(IEnumerable<PdfLayoutRectangle> rectangles)
    {
        PdfLayoutRectangle[] values = rectangles.ToArray();
        float left = values.Min(static bounds => bounds.X);
        float top = values.Min(static bounds => bounds.Y);
        float right = values.Max(static bounds => bounds.Right);
        float bottom = values.Max(static bounds => bounds.Bottom);
        return new PdfLayoutRectangle(left, top, right - left, bottom - top);
    }

    private static PdfHtmlOptions SemanticContinuousOptions()
    {
        return new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        };
    }

    private static XElement PageElement(XDocument dom, int pageNumber)
    {
        return Assert.Single(dom.Descendants("section"), element =>
            element.Attribute("data-page-number")?.Value == pageNumber.ToString());
    }

    private static string[] SerializedDirectChildrenByClass(XElement page, string className)
    {
        return page.Elements()
            .Where(element => HasClass(element, className))
            .Select(static element => element.ToString(SaveOptions.DisableFormatting))
            .ToArray();
    }

    private static string FixedRunPresentationSignature(XElement run)
    {
        return string.Join(
            "|",
            run.Attribute("class")?.Value,
            run.Attribute("data-font")?.Value,
            run.Attribute("dir")?.Value,
            run.Attribute("style")?.Value,
            run.Elements("svg").SingleOrDefault()?.ToString(SaveOptions.DisableFormatting));
    }

    private static IEnumerable<XElement> ElementsByClass(XDocument dom, string className)
    {
        return dom.Descendants().Where(element => HasClass(element, className));
    }

    private static bool HasClass(XElement element, string className)
    {
        return (element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Contains(className, StringComparer.Ordinal);
    }

    private static XDocument ParseHtml(string html)
    {
        return XDocument.Parse(Regex.Replace(html, "<!doctype html>\\s*", "", RegexOptions.IgnoreCase));
    }

    private static string NormalizeWhitespace(string text)
    {
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static void AssertWithin(float tolerance, float expected, float actual)
    {
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pdfbox-net-semantic-island-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
