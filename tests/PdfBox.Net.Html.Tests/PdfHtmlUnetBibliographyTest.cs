using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Playwright;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

namespace PdfBox.Net.Html.Tests;

public sealed class PdfHtmlUnetBibliographyTest
{
    private const string CellTrackingHref =
        "http://www.codesolorzano.com/ celltrackingchallenge/Cell_Tracking_Challenge/Welcome.html";
    private const string SegmentationHref = "http://brainiac2.mit.edu/isbi_challenge/";

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsSeparateLinkedUnetReferences()
    {
        PdfHtmlDocument converted = ConvertFixture();
        XDocument dom = ParseHtml(converted.Html);

        XElement bibliography = Assert.Single(ElementsByClass(dom, "pdf-semantic-bibliography"));
        Assert.Equal("References", bibliography.Attribute("aria-label")?.Value);
        Assert.Equal("number", bibliography.Attribute("data-marker-kind")?.Value);
        XElement[] items = bibliography.Elements("li").ToArray();
        Assert.Equal(14, items.Length);
        Assert.Equal("cite.Caffe", items[5].Attribute("id")?.Value);
        Assert.Equal(
            Enumerable.Range(1, 14)
                .Where(static number => number != 6)
                .Select(static number => $"reference-{number}"),
            items
                .Where((_, index) => index != 5)
                .Select(static item => item.Attribute("id")?.Value));
        Assert.StartsWith("Ciresan", items[0].Value.Trim(), StringComparison.Ordinal);
        Assert.Contains("electron microscopy images", items[0].Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2852", items[0].Value, StringComparison.Ordinal);

        XElement[] bibliographyLinks = bibliography.Descendants("a").ToArray();
        Assert.NotEmpty(bibliographyLinks);
        Assert.All(bibliographyLinks, link =>
            Assert.DoesNotContain(link.Ancestors(), static ancestor => ancestor.Name.LocalName == "a"));
        Assert.DoesNotContain(bibliographyLinks, link => HasClass(link, "pdf-semantic-auto-link"));

        XElement[] itemThirteenLinks = items[12].Descendants("a").ToArray();
        XElement[] itemFourteenLinks = items[13].Descendants("a").ToArray();
        Assert.NotEmpty(itemThirteenLinks);
        Assert.NotEmpty(itemFourteenLinks);
        Assert.All(itemThirteenLinks, link => Assert.Equal(CellTrackingHref, link.Attribute("href")?.Value));
        Assert.All(itemFourteenLinks, link => Assert.Equal(SegmentationHref, link.Attribute("href")?.Value));
        Assert.All(itemThirteenLinks.Concat(itemFourteenLinks), link =>
            Assert.Equal("uri", link.Attribute("data-link-kind")?.Value));

        XElement footnote = Assert.Single(
            ElementsByClass(dom, "pdf-semantic-paragraph"),
            element => element.Value.Contains("U-net implementation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(footnote.Ancestors(), ancestor => ReferenceEquals(ancestor, bibliography));
        Assert.DoesNotContain("U-net implementation", bibliography.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    public async Task Convert_SemanticContinuousFlow_KeepsUnetReferencesReadableAndOrdered(int viewportWidth)
    {
        PdfHtmlDocument converted = ConvertFixture();
        using TempDirectory tempDirectory = new();
        converted.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = viewportWidth,
                Height = 1800
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);
        await page.EvaluateAsync("() => document.fonts.ready");

        BibliographyMetrics metrics = await GetBibliographyMetrics(page);

        Assert.Equal(14, metrics.ItemCount);
        Assert.True(metrics.ItemsAreOrdered);
        Assert.True(metrics.ItemsDoNotOverlap);
        Assert.True(
            metrics.ListIsContained,
            $"List [{metrics.ListLeft:0.###}, {metrics.ListRight:0.###}] / " +
            $"flow [{metrics.FlowLeft:0.###}, {metrics.FlowRight:0.###}], " +
            $"client {metrics.ListClientWidth:0.###}, scroll {metrics.ListScrollWidth:0.###}.");
        Assert.True(metrics.FootnoteFollowsList);
        Assert.True(metrics.FootnoteIsOutsideList);
        Assert.Equal(0, metrics.NestedLinkCount);
        Assert.Equal(2, metrics.LinkedTailItemCount);
        Assert.InRange(metrics.ItemLeftSpread, 0, 1);
        Assert.True(
            metrics.ItemHeights[0] >= metrics.LineHeight * 2.4,
            $"The first wrapped reference rendered at only {metrics.ItemHeights[0]:0.###}px.");
        Assert.True(
            metrics.ItemHeights[12] >= metrics.LineHeight * 1.8,
            $"The linked thirteenth reference did not retain a wrapped visual row.");
    }

    private static PdfHtmlDocument ConvertFixture()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-unet-page-8.pdf"));
        Assert.Equal(1, document.GetNumberOfPages());
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = true,
            IncludePaths = true
        });
        return PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
    }

    private static Task<BibliographyMetrics> GetBibliographyMetrics(IPage page)
    {
        return page.EvaluateAsync<BibliographyMetrics>(
            """
            () => {
              const list = document.querySelector('.pdf-semantic-bibliography');
              const items = Array.from(list.children);
              const boxes = items.map(item => item.getBoundingClientRect());
              const flowBox = list.closest('.pdf-semantic-flow').getBoundingClientRect();
              const listBox = list.getBoundingClientRect();
              const footnote = Array.from(document.querySelectorAll('.pdf-semantic-paragraph'))
                .find(element => element.textContent.includes('U-net implementation'));
              const footnoteBox = footnote.getBoundingClientRect();
              const lineHeight = Number.parseFloat(getComputedStyle(items[0]).lineHeight);
              return {
                itemCount: items.length,
                itemHeights: boxes.map(box => box.height),
                lineHeight,
                itemLeftSpread: Math.max(...boxes.map(box => box.left)) - Math.min(...boxes.map(box => box.left)),
                itemsAreOrdered: items.every((item, index) =>
                  item.id === (index === 5 ? 'cite.Caffe' : `reference-${index + 1}`)),
                itemsDoNotOverlap: boxes.slice(1).every((box, index) => boxes[index].bottom <= box.top + 0.5),
                listIsContained:
                  listBox.left >= flowBox.left - 1 &&
                  listBox.right <= flowBox.right + 1 &&
                  list.scrollWidth <= list.clientWidth + 1,
                listLeft: listBox.left,
                listRight: listBox.right,
                flowLeft: flowBox.left,
                flowRight: flowBox.right,
                listClientWidth: list.clientWidth,
                listScrollWidth: list.scrollWidth,
                footnoteFollowsList: listBox.bottom <= footnoteBox.top + 0.5,
                footnoteIsOutsideList: !list.contains(footnote),
                nestedLinkCount: list.querySelectorAll('a a').length,
                linkedTailItemCount: items.slice(12).filter(item => item.querySelector('a[data-link-kind="uri"]')).length
              };
            }
            """);
    }

    private static XDocument ParseHtml(string html)
    {
        string xml = Regex.Replace(html, "<!doctype html>\\s*", "", RegexOptions.IgnoreCase);
        xml = string.Concat(xml.Where(XmlConvert.IsXmlChar));
        return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
    }

    private static IEnumerable<XElement> ElementsByClass(XContainer container, string className)
    {
        return container.Descendants().Where(element => HasClass(element, className));
    }

    private static bool HasClass(XElement element, string className)
    {
        return (element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Contains(className, StringComparer.Ordinal);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private sealed class BibliographyMetrics
    {
        public int ItemCount { get; set; }

        public double[] ItemHeights { get; set; } = [];

        public double LineHeight { get; set; }

        public double ItemLeftSpread { get; set; }

        public bool ItemsAreOrdered { get; set; }

        public bool ItemsDoNotOverlap { get; set; }

        public bool ListIsContained { get; set; }

        public double ListLeft { get; set; }

        public double ListRight { get; set; }

        public double FlowLeft { get; set; }

        public double FlowRight { get; set; }

        public double ListClientWidth { get; set; }

        public double ListScrollWidth { get; set; }

        public bool FootnoteFollowsList { get; set; }

        public bool FootnoteIsOutsideList { get; set; }

        public int NestedLinkCount { get; set; }

        public int LinkedTailItemCount { get; set; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pdfbox-net-unet-bibliography-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for browser-held files on Windows.
            }
        }
    }
}
