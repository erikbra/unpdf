using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Playwright;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

namespace PdfBox.Net.Html.Tests;

public class PdfHtmlUnetFormulaTest
{
    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsCompleteUnetFormulaMathMl()
    {
        PdfHtmlDocument converted = ConvertFixture();
        XDocument dom = ParseHtml(converted.Html);
        XElement equationOne = Formula(dom, "(1)");
        XElement equationTwo = Formula(dom, "(2)");

        XElement mathOne = AssertNativeNumberedFormula(equationOne, "(1)");
        XElement limits = Assert.Single(mathOne.Descendants("munderover"));
        Assert.Equal(
            ["∑", "x∈Ω", "k"],
            limits.Elements().Select(static element => element.Value).ToArray());
        XElement labelFunction = Assert.Single(
            mathOne.Descendants("msub"),
            script => script.Elements().First().Value == "p");
        Assert.Equal("ℓ(x)", labelFunction.Elements().Last().Value);
        Assert.Equal(
            "E=∑_(x∈Ω)^(k)w(x)log(p_(ℓ(x))(x))",
            AccessibleText(equationOne, mathOne));

        XElement mathTwo = AssertNativeNumberedFormula(equationTwo, "(2)");
        XElement fraction = Assert.Single(mathTwo.Descendants("mfrac"));
        XElement[] fractionParts = fraction.Elements().ToArray();
        Assert.Equal("(d1(x)+d2(x))2", fractionParts[0].Value);
        Assert.Equal("2σ2", fractionParts[1].Value);
        Assert.Equal(
            ["d1", "d2"],
            fraction.Descendants("msub")
                .Where(script => script.Elements().First().Value == "d")
                .Select(static script => script.Value)
                .ToArray());
        Assert.Contains(
            fraction.Descendants("msup"),
            script => script.Elements().First().Value == "σ" && script.Elements().Last().Value == "2");
        Assert.Equal(
            "w(x)=w_(c)(x)+w_(0)·exp−((d_(1)(x)+d_(2)(x))^(2))/(2σ^(2))",
            AccessibleText(equationTwo, mathTwo));

        Assert.DoesNotContain(dom.Descendants(), element => HasClass(element, "pdf-semantic-formula-run"));
    }

    [Fact]
    public async Task Convert_SemanticContinuousFlow_KeepsUnetFormulasCompactAndContained()
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
                Width = 1000,
                Height = 1800
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);
        await page.EvaluateAsync("() => document.fonts.ready");

        FormulaMetrics[] metrics = await GetFormulaMetrics(page);
        Assert.Equal(["(1)", "(2)"], metrics.Select(static item => item.Number).ToArray());
        foreach (FormulaMetrics formula in metrics)
        {
            Assert.Equal("grid", formula.Display);
            Assert.Equal("static", formula.NumberPosition);
            Assert.InRange(Math.Abs(formula.FormulaLeft - formula.ContentLeft), 0, 1);
            Assert.InRange(Math.Abs(formula.FormulaRight - formula.ContentRight), 0, 1);
            Assert.InRange(Math.Abs(formula.MathCenter - formula.ContentCenter), 0, 1);
            Assert.InRange(Math.Abs(formula.NumberRight - formula.FormulaRight), 0, 1);
            Assert.True(formula.MathContained, $"{formula.Number} MathML escaped its formula box.");
            Assert.True(formula.NumberContained, $"{formula.Number} label escaped its formula box.");
            Assert.True(formula.StructureContained, $"{formula.Number} structured MathML escaped its math box.");
            Assert.True(
                formula.FormulaHeight <= 64,
                $"{formula.Number} rendered at {formula.FormulaHeight:0.###}px instead of a compact display line.");
            Assert.True(
                formula.FormulaScrollWidth <= formula.FormulaClientWidth + 1,
                $"{formula.Number} overflowed horizontally.");
            Assert.True(
                !formula.PreviousBottom.HasValue || formula.PreviousBottom <= formula.FormulaTop + 1,
                $"{formula.Number} overlapped preceding prose.");
            Assert.True(
                !formula.NextTop.HasValue || formula.FormulaBottom <= formula.NextTop + 1,
                $"{formula.Number} overlapped following prose.");
        }

        FormulaMetrics equationOne = metrics[0];
        Assert.True(equationOne.HasLimits);
        Assert.InRange(equationOne.LimitCenterDelta, 0, 12);
        Assert.True(metrics[1].HasFraction);
    }

    private static PdfHtmlDocument ConvertFixture()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-unet-pages-4-5.pdf"));
        Assert.Equal(2, document.GetNumberOfPages());
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false
        });
        return PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
    }

    private static XElement Formula(XDocument dom, string number)
    {
        return Assert.Single(
            dom.Descendants(),
            element => HasClass(element, "pdf-semantic-formula") &&
                element.Attribute("data-equation-number")?.Value == number);
    }

    private static XElement AssertNativeNumberedFormula(XElement formula, string number)
    {
        Assert.True(HasClass(formula, "pdf-semantic-formula-native"));
        Assert.True(HasClass(formula, "pdf-semantic-formula-numbered"));
        Assert.Null(formula.Attribute("role"));
        XElement math = Assert.Single(formula.Elements("math"));
        XElement equationNumber = Assert.Single(
            formula.Elements(),
            element => HasClass(element, "pdf-semantic-equation-number"));
        Assert.Equal(number, equationNumber.Value);
        Assert.Equal("Equation " + number[1..^1], equationNumber.Attribute("aria-label")?.Value);
        Assert.DoesNotContain(
            math.Descendants().Where(static element => element.Name.LocalName != "annotation"),
            element => element.Value == number);
        return math;
    }

    private static string AccessibleText(XElement formula, XElement math)
    {
        XElement annotation = Assert.Single(
            math.Descendants("annotation"),
            element => element.Attribute("encoding")?.Value == "text/plain");
        string accessibleText = annotation.Value;
        Assert.Equal(accessibleText, math.Attribute("aria-label")?.Value);
        Assert.Equal(accessibleText, formula.Attribute("aria-label")?.Value);
        return accessibleText;
    }

    private static Task<FormulaMetrics[]> GetFormulaMetrics(IPage page)
    {
        return page.EvaluateAsync<FormulaMetrics[]>(
            """
            () => ["(1)", "(2)"].map(number => {
              const formula = document.querySelector(
                `.pdf-semantic-formula-numbered[data-equation-number="${number}"]`);
              const content = formula.closest('.pdf-semantic-continuous-flow');
              const math = formula.querySelector(':scope > .pdf-semantic-mathml');
              const label = formula.querySelector(':scope > .pdf-semantic-equation-number');
              const structure = math.querySelector(number === '(1)' ? 'munderover' : 'mfrac');
              const formulaBox = formula.getBoundingClientRect();
              const contentBox = content.getBoundingClientRect();
              const mathBox = math.getBoundingClientRect();
              const labelBox = label.getBoundingClientRect();
              const structureBox = structure.getBoundingClientRect();
              const limits = number === '(1)' ? Array.from(structure.children) : [];
              const nearestVisibleSibling = direction => {
                let sibling = direction < 0 ? formula.previousElementSibling : formula.nextElementSibling;
                while (sibling) {
                  const box = sibling.getBoundingClientRect();
                  if (!sibling.classList.contains('pdf-semantic-page-break') && box.height > 1) {
                    return box;
                  }
                  sibling = direction < 0 ? sibling.previousElementSibling : sibling.nextElementSibling;
                }
                return null;
              };
              const previous = nearestVisibleSibling(-1);
              const next = nearestVisibleSibling(1);
              const contained = (inner, outer) =>
                inner.left >= outer.left - 1 && inner.right <= outer.right + 1 &&
                inner.top >= outer.top - 1 && inner.bottom <= outer.bottom + 1;
              const operatorCenter = limits.length
                ? limits[0].getBoundingClientRect().left + limits[0].getBoundingClientRect().width / 2
                : 0;
              const limitCenterDelta = limits.length
                ? Math.max(...limits.slice(1).map(limit => {
                    const box = limit.getBoundingClientRect();
                    return Math.abs(box.left + box.width / 2 - operatorCenter);
                  }))
                : 0;
              return {
                number,
                display: getComputedStyle(formula).display,
                numberPosition: getComputedStyle(label).position,
                formulaLeft: formulaBox.left,
                formulaRight: formulaBox.right,
                formulaTop: formulaBox.top,
                formulaBottom: formulaBox.bottom,
                formulaHeight: formulaBox.height,
                contentLeft: contentBox.left,
                contentRight: contentBox.right,
                contentCenter: contentBox.left + contentBox.width / 2,
                mathCenter: mathBox.left + mathBox.width / 2,
                numberRight: labelBox.right,
                mathContained: contained(mathBox, formulaBox),
                numberContained: contained(labelBox, formulaBox),
                structureContained: contained(structureBox, mathBox),
                formulaClientWidth: formula.clientWidth,
                formulaScrollWidth: formula.scrollWidth,
                previousBottom: previous?.bottom ?? null,
                nextTop: next?.top ?? null,
                hasLimits: limits.length === 3,
                hasFraction: structure.localName === 'mfrac',
                limitCenterDelta
              };
            })
            """);
    }

    private static XDocument ParseHtml(string html)
    {
        string xml = Regex.Replace(html, "<!doctype html>\\s*", "", RegexOptions.IgnoreCase);
        xml = string.Concat(xml.Where(XmlConvert.IsXmlChar));
        return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static bool HasClass(XElement element, string className)
    {
        return (element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Contains(className, StringComparer.Ordinal);
    }

    private sealed class FormulaMetrics
    {
        public string Number { get; set; } = "";

        public string Display { get; set; } = "";

        public string NumberPosition { get; set; } = "";

        public double FormulaLeft { get; set; }

        public double FormulaRight { get; set; }

        public double FormulaTop { get; set; }

        public double FormulaBottom { get; set; }

        public double FormulaHeight { get; set; }

        public double ContentLeft { get; set; }

        public double ContentRight { get; set; }

        public double ContentCenter { get; set; }

        public double MathCenter { get; set; }

        public double NumberRight { get; set; }

        public bool MathContained { get; set; }

        public bool NumberContained { get; set; }

        public bool StructureContained { get; set; }

        public double FormulaClientWidth { get; set; }

        public double FormulaScrollWidth { get; set; }

        public double? PreviousBottom { get; set; }

        public double? NextTop { get; set; }

        public bool HasLimits { get; set; }

        public bool HasFraction { get; set; }

        public double LimitCenterDelta { get; set; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfbox-net-unet-{Guid.NewGuid():N}");
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
