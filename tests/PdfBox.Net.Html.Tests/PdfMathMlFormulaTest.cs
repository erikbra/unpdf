using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Playwright;
using PdfBox.Net;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

namespace PdfBox.Net.Html.Tests;

public class PdfMathMlFormulaTest
{
    private static readonly PdfLayoutColor Black = new(0f, 0f, 0f, 1f, "DeviceGray");

    [Fact]
    public void TryCreate_EmitsIdentifierAndNumberTokens()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("x", 92f, 100f),
            Glyph("+", 104f, 100f, fontName: "CMR10"),
            Glyph("y", 116f, 100f),
            Glyph("=", 128f, 100f, fontName: "CMR10"),
            Glyph("2", 140f, 100f, fontName: "CMR10")
        ];

        string markup = Render(glyphs, [], out PdfMathMlFormula formula);
        XDocument dom = Parse(markup);
        XElement math = Assert.Single(dom.Root!.Elements("math"));

        Assert.Equal(["x", "y"], math.Descendants("mi").Select(static token => token.Value).ToArray());
        Assert.Equal(["+", "="], math.Descendants("mo").Select(static token => token.Value).ToArray());
        Assert.Equal("2", Assert.Single(math.Descendants("mn")).Value);
        Assert.Equal("x+y=2", formula.AccessibleText);
    }

    [Fact]
    public void TryCreate_EmitsAlignedBracketedMatrix()
    {
        PdfTextGlyph[] glyphs = MatrixGlyphs(secondRowSecondColumnX: 150f);

        string markup = Render(glyphs, [], out PdfMathMlFormula formula);
        XDocument dom = Parse(markup);
        XElement math = Assert.Single(dom.Root!.Elements("math"));
        XElement table = Assert.Single(math.Descendants("mtable"));
        XElement[] rows = table.Elements("mtr").ToArray();

        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(2, row.Elements("mtd").Count()));
        Assert.Equal(["a", "b"], rows[0].Elements("mtd").Select(static cell => cell.Value).ToArray());
        Assert.Equal(["c", "d"], rows[1].Elements("mtd").Select(static cell => cell.Value).ToArray());
        Assert.Equal("A=[a,b;c,d]", formula.AccessibleText);
        Assert.All(glyphs, glyph =>
            Assert.Contains(formula.ClaimedGlyphs, claimed => ReferenceEquals(claimed, glyph)));
    }

    [Fact]
    public async Task TryCreate_AlignedBracketedMatrixRendersCompactlyInBrowser()
    {
        PdfTextGlyph[] glyphs = MatrixGlyphs(secondRowSecondColumnX: 150f);
        Assert.True(PdfMathMlFormula.TryCreate(glyphs, [], out PdfMathMlFormula? candidate));
        PdfMathMlFormula formula = Assert.IsType<PdfMathMlFormula>(candidate);
        PdfHtmlDocument styles = PdfHtmlConverter.Convert(new PdfLayoutDocument([], []));
        StringBuilder html = new(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <link rel="stylesheet" href="assets/pdfbox-net-fixed.css" />
            </head>
            <body class="pdf-document pdf-document-continuous">
              <main id="matrix-fixture" style="box-sizing:border-box;padding:12pt;width:320pt">
                <p id="matrix-before" style="margin:0">Before matrix</p>
                <div id="matrix-formula" class="pdf-semantic-element pdf-semantic-formula pdf-semantic-formula-native" style="--pdf-semantic-formula-width:180pt;--pdf-semantic-formula-height:36pt;--pdf-semantic-math-font-size:10pt">
            """);
        formula.WriteTo(html, includeEquationNumber: false);
        html.Append(
            """
                </div>
                <p id="matrix-after" style="margin:0">After matrix</p>
              </main>
            </body>
            </html>
            """);
        PdfHtmlDocument browserDocument = new(html.ToString(), styles.CssPath, styles.Css);
        using TempDirectory tempDirectory = new();
        browserDocument.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 800,
                Height = 600
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);
        await page.EvaluateAsync("() => document.fonts.ready");

        MatrixBrowserMetrics metrics = await page.EvaluateAsync<MatrixBrowserMetrics>(
            """
            () => {
              const formula = document.querySelector('#matrix-formula');
              const math = formula.querySelector(':scope > .pdf-semantic-mathml');
              const table = math.querySelector('mtable');
              const rows = Array.from(table.querySelectorAll(':scope > mtr'));
              const formulaBox = formula.getBoundingClientRect();
              const mathBox = math.getBoundingClientRect();
              const tableBox = table.getBoundingClientRect();
              const beforeBox = document.querySelector('#matrix-before').getBoundingClientRect();
              const afterBox = document.querySelector('#matrix-after').getBoundingClientRect();
              const contained = (inner, outer) =>
                inner.left >= outer.left - 1 && inner.right <= outer.right + 1 &&
                inner.top >= outer.top - 1 && inner.bottom <= outer.bottom + 1;
              const rowBox = row => {
                const cells = Array.from(row.querySelectorAll(':scope > mtd'))
                  .map(cell => cell.getBoundingClientRect());
                return {
                  top: Math.min(...cells.map(cell => cell.top)),
                  bottom: Math.max(...cells.map(cell => cell.bottom))
                };
              };
              const rowBoxes = rows.map(rowBox);
              const tableStyle = getComputedStyle(table);
              return {
                tableDisplay: tableStyle.display,
                tableVisibility: tableStyle.visibility,
                tableOpacity: Number.parseFloat(tableStyle.opacity),
                formulaWidth: formulaBox.width,
                formulaHeight: formulaBox.height,
                tableWidth: tableBox.width,
                tableHeight: tableBox.height,
                tableVisible: tableBox.width > 1 && tableBox.height > 1,
                mathContained: contained(mathBox, formulaBox),
                tableContained: contained(tableBox, formulaBox) && contained(tableBox, mathBox),
                rowsNonOverlapping: rowBoxes.length === 2 && rowBoxes[0].bottom <= rowBoxes[1].top + 1,
                siblingsNonOverlapping:
                  beforeBox.bottom <= formulaBox.top + 1 && formulaBox.bottom <= afterBox.top + 1,
                rowCount: rows.length,
                cellCount: table.querySelectorAll('mtd').length
              };
            }
            """);

        Assert.NotEqual("none", metrics.TableDisplay);
        Assert.NotEqual("hidden", metrics.TableVisibility);
        Assert.True(metrics.TableOpacity > 0);
        Assert.True(metrics.TableVisible);
        Assert.True(metrics.MathContained);
        Assert.True(metrics.TableContained);
        Assert.True(metrics.RowsNonOverlapping);
        Assert.True(metrics.SiblingsNonOverlapping);
        Assert.Equal(2, metrics.RowCount);
        Assert.Equal(4, metrics.CellCount);
        Assert.InRange(metrics.FormulaWidth, 80, 260);
        Assert.InRange(metrics.FormulaHeight, 20, 96);
        Assert.InRange(metrics.TableWidth, 16, 180);
        Assert.InRange(metrics.TableHeight, 16, 72);
    }

    [Fact]
    public void TryCreate_RejectsMisalignedBracketedMatrix()
    {
        PdfTextGlyph[] glyphs = MatrixGlyphs(secondRowSecondColumnX: 156f);

        Assert.False(PdfMathMlFormula.TryCreate(glyphs, [], out PdfMathMlFormula? formula));
        Assert.Null(formula);
    }

    [Fact]
    public void WriteTo_KeepsLinkedEquationNumberAdjacentToMath()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("x", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10"),
            Glyph("(", 190f, 100f, fontName: "Times-Roman"),
            Glyph("7", 196f, 100f, fontName: "Times-Roman"),
            Glyph(")", 202f, 100f, fontName: "Times-Roman")
        ];
        Assert.True(PdfMathMlFormula.TryCreate(glyphs, [], out PdfMathMlFormula? candidate));
        PdfMathMlFormula formula = Assert.IsType<PdfMathMlFormula>(candidate);
        PdfLayoutRectangle linkBounds = Bounds(formula.EquationNumberGlyphs.Select(static glyph => glyph.PageBounds));
        PdfLayoutLink link = new(
            0,
            linkBounds,
            PdfLayoutLinkKind.Destination,
            null,
            "page:4",
            4,
            []);
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            [],
            [],
            [],
            [],
            [],
            [],
            [link],
            []);
        PdfLayoutLink matchedLink = Assert.IsType<PdfLayoutLink>(
            PdfHtmlConverter.FormulaLinkForGlyphs(page, formula.EquationNumberGlyphs));
        StringBuilder markup = new("<div>");

        formula.WriteTo(markup, includeEquationNumber: false);
        PdfHtmlConverter.WriteEquationNumber(markup, formula.EquationNumber!, matchedLink);
        markup.Append("</div>");

        XDocument dom = Parse(markup.ToString());
        Assert.Equal(["math", "a"], dom.Root!.Elements().Select(static element => element.Name.LocalName).ToArray());
        XElement equationLink = Assert.Single(dom.Root.Elements("a"));
        Assert.Equal("#page-4", equationLink.Attribute("href")?.Value);
        Assert.Equal("destination", equationLink.Attribute("data-link-kind")?.Value);
        Assert.Equal("Equation 7", equationLink.Attribute("aria-label")?.Value);
        Assert.Equal("(7)", equationLink.Value);
        Assert.DoesNotContain(dom.Root.Element("math")!.Descendants(), element => element.Name.LocalName == "a");
    }

    [Fact]
    public void TryCreate_EmitsFractionAndKeepsEquationNumberOutsideMath()
    {
        PdfTextGlyph unrelated = Glyph("z", 92f, 160f);
        PdfTextGlyph[] glyphs =
        [
            Glyph("y", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 126f, 93f, 7f, 5f, 6f, "CMR7"),
            Glyph("x", 126f, 110f, 7f, 5f, 6f),
            Glyph("(", 190f, 100f, fontName: "Times-Roman"),
            Glyph("1", 196f, 100f, fontName: "Times-Roman"),
            Glyph("2", 202f, 100f, fontName: "Times-Roman"),
            Glyph(")", 208f, 100f, fontName: "Times-Roman"),
            unrelated
        ];
        PdfLayoutPath rule = Rule(120f, 104f, 20f);

        string markup = Render(glyphs, [rule], out PdfMathMlFormula formula);
        XDocument dom = Parse(markup);

        XElement math = Assert.Single(dom.Root!.Elements("math"));
        XElement fraction = Assert.Single(math.Descendants("mfrac"));
        Assert.Equal("1", fraction.Elements().First().Value);
        Assert.Equal("x", fraction.Elements().Last().Value);
        Assert.Equal("y=(1)/(x)", formula.AccessibleText);
        Assert.Single(math.Descendants("mn"), number => number.Value == "1");
        XElement equationNumber = Assert.Single(dom.Root.Elements("span"));
        Assert.Equal("(12)", equationNumber.Value);
        Assert.Equal("Equation 12", equationNumber.Attribute("aria-label")?.Value);
        Assert.All(glyphs[..^1], glyph =>
            Assert.Contains(formula.ClaimedGlyphs, claimed => ReferenceEquals(claimed, glyph)));
        Assert.DoesNotContain(formula.ClaimedGlyphs, claimed => ReferenceEquals(claimed, unrelated));
    }

    [Fact]
    public void TryCreate_EmitsSquareRootAndSubSuperscripts()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("y", 92f, 100f),
            Glyph("i", 99f, 105f, 5f, 5f, 6f),
            Glyph("2", 99f, 91f, 5f, 5f, 6f, "CMR7"),
            Glyph("=", 112f, 100f, fontName: "CMR10"),
            Glyph("√", 128f, 96f, 10f, 12f, 10f, "CMEX10"),
            Glyph("x", 140f, 100f, fontName: "CMBX10")
        ];
        PdfLayoutPath rootRule = Rule(137f, 96f, 12f);

        string markup = Render(glyphs, [rootRule], out PdfMathMlFormula formula);
        XDocument dom = Parse(markup);
        XElement math = Assert.Single(dom.Root!.Elements("math"));

        XElement scripts = Assert.Single(math.Descendants("msubsup"));
        Assert.Equal(["y", "i", "2"], scripts.Elements().Select(static element => element.Value).ToArray());
        XElement root = Assert.Single(math.Descendants("msqrt"));
        Assert.Equal("x", root.Value);
        Assert.Equal("bold", Assert.Single(root.Elements("mi")).Attribute("mathvariant")?.Value);
        Assert.Contains("sqrt(x)", formula.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_EmitsLargeOperatorLimits()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("y", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("∑", 124f, 96f, 12f, 14f, 10f, "CMEX10"),
            Glyph("n", 128f, 88f, 5f, 5f, 6f),
            Glyph("i", 124f, 112f, 4f, 5f, 6f),
            Glyph("=", 128f, 112f, 4f, 5f, 6f, "CMR7"),
            Glyph("1", 132f, 112f, 4f, 5f, 6f, "CMR7"),
            Glyph("x", 145f, 100f)
        ];

        string markup = Render(glyphs, [], out _);
        XDocument dom = Parse(markup);
        XElement limits = Assert.Single(dom.Descendants("munderover"));

        Assert.Equal("∑", limits.Elements().First().Value);
        Assert.Equal("i=1", limits.Elements().ElementAt(1).Value);
        Assert.Equal("n", limits.Elements().Last().Value);
    }

    [Fact]
    public void TryCreate_EmitsRaisedFractionBeyondBaselineSpan()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("w", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("e", 120f, 100f, fontName: "CMR10"),
            Glyph("x", 128f, 100f, fontName: "CMR10"),
            Glyph("p", 136f, 100f, fontName: "CMR10"),
            Glyph("−", 146f, 100f, fontName: "CMSY10"),
            Glyph("(", 158f, 93f, fontName: "CMR10"),
            Glyph("d", 166f, 93f),
            Glyph("1", 174f, 98f, 5f, 5f, 6f, "CMR7"),
            Glyph(")", 180f, 93f, fontName: "CMR10"),
            Glyph("2", 188f, 88f, 5f, 5f, 6f, "CMR7"),
            Glyph("2", 174f, 110f, fontName: "CMR10"),
            Glyph("σ", 184f, 110f),
            Glyph("2", 192f, 108f, 5f, 5f, 6f, "CMR7")
        ];

        string markup = Render(glyphs, [Rule(156f, 104f, 48f)], out PdfMathMlFormula formula);
        XDocument dom = Parse(markup);
        XElement fraction = Assert.Single(dom.Descendants("mfrac"));

        Assert.Equal("(d1)2", fraction.Elements().First().Value);
        Assert.Equal("2σ2", fraction.Elements().Last().Value);
        Assert.Contains("exp−((d_(1))^(2))/(2σ^(2))", formula.AccessibleText, StringComparison.Ordinal);
        Assert.All(glyphs, glyph =>
            Assert.Contains(formula.ClaimedGlyphs, claimed => ReferenceEquals(claimed, glyph)));
    }

    [Fact]
    public void TryCreate_UnrelatedRulesAndRadicalDoNotChangeOperatorSelection()
    {
        PdfTextGlyph unrelatedRadical = Glyph("√", 300f, 250f, 10f, 12f, 10f, "CMEX10");
        PdfTextGlyph[] glyphs =
        [
            Glyph("y", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("∑", 124f, 88f, 12f, 14f, 10f, "CMEX10"),
            Glyph("n", 128f, 60f, 5f, 5f, 6f),
            Glyph("i", 124f, 112f, 4f, 5f, 6f),
            Glyph("=", 128f, 112f, 4f, 5f, 6f, "CMR7"),
            Glyph("1", 132f, 112f, 4f, 5f, 6f, "CMR7"),
            Glyph("x", 145f, 100f),
            unrelatedRadical
        ];

        string markup = Render(
            glyphs,
            [Rule(308f, 250f, 20f), Rule(400f, 400f, 30f)],
            out PdfMathMlFormula formula);
        XDocument dom = Parse(markup);

        Assert.Single(dom.Descendants("munderover"));
        Assert.Empty(dom.Descendants("mfrac"));
        Assert.Empty(dom.Descendants("msqrt"));
        Assert.DoesNotContain(formula.ClaimedGlyphs, glyph => ReferenceEquals(glyph, unrelatedRadical));
    }

    [Fact]
    public void TryCreate_RejectsCompetingFormulaBaselines()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("x", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10"),
            Glyph("y", 92f, 126f),
            Glyph("=", 104f, 126f, fontName: "CMR10"),
            Glyph("2", 116f, 126f, fontName: "CMR10")
        ];

        Assert.False(PdfMathMlFormula.TryCreate(glyphs, [], out PdfMathMlFormula? formula));
        Assert.Null(formula);
    }

    [Fact]
    public void TryCreate_RejectsProseLikeNeighboringBaseline()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("x", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10"),
            Glyph("u", 92f, 126f, fontName: "Times-Roman"),
            Glyph("s", 100f, 126f, fontName: "Times-Roman"),
            Glyph("i", 108f, 126f, fontName: "Times-Roman"),
            Glyph("n", 116f, 126f, fontName: "Times-Roman"),
            Glyph("g", 124f, 126f, fontName: "Times-Roman")
        ];

        Assert.False(PdfMathMlFormula.TryCreate(glyphs, [], out PdfMathMlFormula? formula));
        Assert.Null(formula);
    }

    [Fact]
    public void IsFullyClaimedFormulaElement_MatchesClonesButNotNearbyEquation()
    {
        PdfTextGlyph[] equationGlyphs =
        [
            Glyph("q", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10"),
            Glyph("(", 190f, 100f, fontName: "Times-Roman"),
            Glyph("2", 196f, 100f, fontName: "Times-Roman"),
            Glyph(")", 202f, 100f, fontName: "Times-Roman")
        ];
        PdfTextGlyph prose = Glyph(
            "Training is performed.",
            92f,
            126f,
            width: 100f,
            fontName: "Times-Roman");
        PdfTextGlyph[] clonedEquationGlyphs = equationGlyphs
            .Select(static glyph => glyph with { })
            .ToArray();
        PdfTextGlyph[] nearbyEquationGlyphs = equationGlyphs
            .Select(static glyph => OffsetGlyph(glyph, 0.25f, 0f))
            .ToArray();
        PdfSemanticLine formulaLine = SemanticLine(clonedEquationGlyphs);
        PdfSemanticLine proseLine = SemanticLine([prose]);
        PdfSemanticElement duplicate = Element(PdfSemanticElementKind.Paragraph, formulaLine);
        PdfSemanticElement nearby = Element(
            PdfSemanticElementKind.Paragraph,
            SemanticLine(nearbyEquationGlyphs));
        PdfSemanticElement mixed = Element(PdfSemanticElementKind.Paragraph, formulaLine, proseLine);
        PdfSemanticElement table = Element(PdfSemanticElementKind.Table, formulaLine);
        HashSet<PdfHtmlConverter.FormulaGlyphKey> claimed = equationGlyphs
            .Select(PdfHtmlConverter.FormulaGlyphIdentity)
            .ToHashSet();

        Assert.All(clonedEquationGlyphs, (glyph, index) =>
            Assert.False(ReferenceEquals(glyph, equationGlyphs[index])));
        Assert.True(PdfHtmlConverter.IsFullyClaimedFormulaElement(duplicate, claimed));
        Assert.False(PdfHtmlConverter.IsFullyClaimedFormulaElement(nearby, claimed));
        Assert.False(PdfHtmlConverter.IsFullyClaimedFormulaElement(mixed, claimed));
        Assert.Contains("Training is performed.", mixed.Text, StringComparison.Ordinal);
        Assert.False(PdfHtmlConverter.IsFullyClaimedFormulaElement(table, claimed));
    }

    [Fact]
    public void UnclaimedTableCellGlyphs_OmitsClonedClaimsAndKeepsOtherRows()
    {
        PdfTextGlyph[] equationGlyphs =
        [
            Glyph("q", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10"),
            Glyph("(", 190f, 100f, fontName: "Times-Roman"),
            Glyph("2", 196f, 100f, fontName: "Times-Roman"),
            Glyph(")", 202f, 100f, fontName: "Times-Roman")
        ];
        PdfTextGlyph[] clonedEquationGlyphs = equationGlyphs
            .Select(static glyph => glyph with { })
            .ToArray();
        PdfTextGlyph prose = Glyph(
            "Training is performed.",
            92f,
            126f,
            width: 100f,
            fontName: "Times-Roman");
        PdfTextGlyph[] nearbyEquationGlyphs = equationGlyphs
            .Select(static glyph => OffsetGlyph(glyph, 0f, 52f))
            .ToArray();
        PdfSemanticTableCell claimedCell = TableCell(clonedEquationGlyphs, borderTop: true);
        PdfSemanticTableCell proseCell = TableCell([prose]);
        PdfSemanticTableCell nearbyCell = TableCell(nearbyEquationGlyphs, borderBottom: true);
        PdfSemanticElement table = Table(claimedCell, proseCell, nearbyCell);
        HashSet<PdfHtmlConverter.FormulaGlyphKey> claimed = equationGlyphs
            .Select(PdfHtmlConverter.FormulaGlyphIdentity)
            .ToHashSet();

        PdfTextGlyph[] renderedGlyphs = table.TableRows
            .SelectMany(static row => row.Cells)
            .SelectMany(cell => PdfHtmlConverter.UnclaimedTableCellGlyphs(cell, claimed))
            .ToArray();

        Assert.All(clonedEquationGlyphs, glyph =>
            Assert.DoesNotContain(renderedGlyphs, rendered => ReferenceEquals(rendered, glyph)));
        Assert.Contains(renderedGlyphs, glyph => ReferenceEquals(glyph, prose));
        Assert.All(nearbyEquationGlyphs, glyph =>
            Assert.Contains(renderedGlyphs, rendered => ReferenceEquals(rendered, glyph)));
        Assert.True(claimedCell.BorderTop);
        Assert.True(nearbyCell.BorderBottom);
        string ariaLabel = PdfHtmlConverter.TableAriaLabel(table, claimed);
        Assert.Contains("Training is performed.", ariaLabel, StringComparison.Ordinal);
        Assert.Equal(1, ariaLabel.Split("(2)", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void UnclaimedTableCellGlyphs_UnrelatedClaimsLeaveTableUnchanged()
    {
        PdfSemanticElement table = Table(
            TableCell([
                Glyph("A", 92f, 100f, fontName: "Times-Roman"),
                Glyph("1", 104f, 100f, fontName: "Times-Roman")
            ], borderLeft: true),
            TableCell([
                Glyph("B", 92f, 126f, fontName: "Times-Roman"),
                Glyph("2", 104f, 126f, fontName: "Times-Roman")
            ], borderRight: true));
        HashSet<PdfHtmlConverter.FormulaGlyphKey> unrelatedClaims =
        [
            PdfHtmlConverter.FormulaGlyphIdentity(
                Glyph("z", 300f, 300f, fontName: "CMMI10"))
        ];
        PdfTextGlyph[] sourceGlyphs = table.TableRows
            .SelectMany(static row => row.Cells)
            .SelectMany(static cell => cell.Lines)
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .ToArray();
        PdfTextGlyph[] renderedGlyphs = table.TableRows
            .SelectMany(static row => row.Cells)
            .SelectMany(cell => PdfHtmlConverter.UnclaimedTableCellGlyphs(cell, unrelatedClaims))
            .ToArray();

        Assert.Equal(sourceGlyphs.Length, renderedGlyphs.Length);
        Assert.All(sourceGlyphs, (glyph, index) => Assert.Same(glyph, renderedGlyphs[index]));
        Assert.Equal(
            table.Text.Replace('\t', ' ').Replace(Environment.NewLine, " "),
            PdfHtmlConverter.TableAriaLabel(table, unrelatedClaims));
        Assert.True(table.TableRows[0].Cells[0].BorderLeft);
        Assert.True(table.TableRows[1].Cells[0].BorderRight);
    }

    [Fact]
    public void FormulaSourceRuns_ExcludesProseLineFromFallback()
    {
        PdfTextGlyph[] formulaGlyphs =
        [
            Glyph("q", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10")
        ];
        PdfTextGlyph prose = Glyph(
            "in closed form: using the notation below, we have",
            92f,
            126f,
            width: 220f,
            fontName: "Times-Roman");
        PdfSemanticElement mixed = Element(
            PdfSemanticElementKind.Paragraph,
            SemanticLine([prose]),
            SemanticLine(formulaGlyphs));

        IReadOnlyList<PdfTextRun> sourceRuns = PdfHtmlConverter.FormulaSourceRuns(mixed);
        PdfTextGlyph[] sourceGlyphs = sourceRuns.SelectMany(static run => run.Glyphs).ToArray();

        Assert.All(formulaGlyphs, glyph =>
            Assert.Contains(sourceGlyphs, source => ReferenceEquals(source, glyph)));
        Assert.DoesNotContain(sourceGlyphs, glyph => ReferenceEquals(glyph, prose));
        Assert.Contains("in closed form", mixed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormulaOperatorLimitRun_AcceptsSingleLimitAndRejectsShortProseWord()
    {
        PdfTextGlyph sum = Glyph("∑", 200f, 100f, 12f, 14f, 15f, "CMEX10");
        PdfTextGlyph limit = Glyph("k", 204f, 86f, 5f, 5f, 6f, "Times-Italic");
        PdfTextGlyph prose = Glyph("for", 199f, 116f, 15f, 5f, 6f, "Times-Roman");
        PdfTextGlyph article = Glyph("a", 204f, 86f, 5f, 5f, 6f, "Times-Roman");
        PdfTextLine sumLine = LayoutLine([sum]);
        PdfTextLine limitLine = LayoutLine([limit]);
        PdfTextLine proseLine = LayoutLine([prose]);
        PdfTextLine articleLine = LayoutLine([article]);
        PdfTextRun sumRun = Assert.Single(sumLine.Runs);
        PdfTextRun limitRun = Assert.Single(limitLine.Runs);
        PdfTextRun proseRun = Assert.Single(proseLine.Runs);
        PdfTextRun articleRun = Assert.Single(articleLine.Runs);
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [sum, limit, prose, article],
            [sumRun, limitRun, proseRun, articleRun],
            [sumLine, limitLine, proseLine, articleLine],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        PdfLayoutRectangle formulaBounds = new(190f, 92f, 36f, 30f);

        Assert.True(PdfHtmlConverter.IsFormulaOperatorLimitRun(page, formulaBounds, limitRun));
        Assert.False(PdfHtmlConverter.IsFormulaOperatorLimitRun(page, formulaBounds, proseRun));
        Assert.False(PdfHtmlConverter.IsFormulaOperatorLimitRun(page, formulaBounds, articleRun));
    }

    [Fact]
    public void TryCreate_IgnoresUnpaintedAndTinyLatexitPayloads()
    {
        PdfTextGlyph[] glyphs =
        [
            Glyph("x", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("1", 116f, 100f, fontName: "CMR10"),
            Glyph("<latexit sha1_base64=hidden>", 92f, 100f, 80f, 8f, 10f, "Courier") with { IsPainted = false },
            Glyph("<latexit>", 92f, 100f, 0.0001f, 0.0001f, 0.0001f, "Courier")
        ];

        string markup = Render(glyphs, [], out _);

        Assert.DoesNotContain("latexit", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<mi>x</mi><mo>=</mo><mn>1</mn>", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_DdpmPageTwo_PreservesNativeEquationAndAmbiguousFallback()
    {
        // Original page 2 of Denoising Diffusion Probabilistic Models: https://arxiv.org/abs/2006.11239
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-ddpm-page-2.pdf"));
        Assert.Equal(1, document.GetNumberOfPages());
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeLinks = false
        });
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement equationTwo = Assert.Single(
            dom.Descendants(),
            element => HasClass(element, "pdf-semantic-formula-native") &&
                element.Elements().Any(child =>
                    HasClass(child, "pdf-semantic-equation-number") && child.Value == "(2)"));
        XElement math = Assert.Single(equationTwo.Elements("math"));
        string annotation = Assert.Single(
            math.Descendants("annotation"),
            element => element.Attribute("encoding")?.Value == "text/plain").Value;

        Assert.Contains("q(x_(1:T)|x_(0)):=∏_(t=1)^(T)", annotation, StringComparison.Ordinal);
        Assert.Contains("q(x_(t)|x_(t−1))", annotation, StringComparison.Ordinal);
        Assert.Contains("sqrt(1−β_(t))x_(t−1),β_(t)I", annotation, StringComparison.Ordinal);
        Assert.Single(math.Descendants("munderover"));
        Assert.Single(math.Descendants("msqrt"));
        XElement equationNumber = Assert.Single(
            equationTwo.Elements(),
            element => HasClass(element, "pdf-semantic-equation-number"));
        Assert.Equal("(2)", equationNumber.Value);
        Assert.DoesNotContain("(2)", math.Value, StringComparison.Ordinal);

        XElement[] tables = dom.Descendants("table").ToArray();
        Assert.DoesNotContain(tables, table => table.Value.Contains("(2)", StringComparison.Ordinal));
        Assert.DoesNotContain(tables, table => table.Value.Contains("∏", StringComparison.Ordinal));
        Assert.Single(dom.Descendants(), element => element.Value == "(2)");

        XElement equationThree = Assert.Single(
            dom.Descendants(),
            element => element.Attribute("role")?.Value == "math" &&
                element.Value.Contains("(3)", StringComparison.Ordinal));
        Assert.True(HasClass(equationThree, "pdf-semantic-formula"));
        Assert.Empty(equationThree.Descendants("math"));
        Assert.Contains(
            equationThree.Descendants(),
            element => HasClass(element, "pdf-semantic-formula-run") &&
                element.Attribute("style")?.Value.Contains("left:", StringComparison.Ordinal) == true);

        XElement figureTwo = Assert.Single(
            dom.Descendants("figure"),
            element => element.Elements("figcaption").Any(caption =>
                caption.Value.StartsWith("Figure 2:", StringComparison.Ordinal)));
        Assert.Single(figureTwo.Elements("figcaption"));
        Assert.DoesNotContain("latexi", converted.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha1_base64", converted.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(figureTwo.Descendants("text"), element =>
            element.Attribute("style")?.Value.Contains("font-size:0", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(figureTwo.Descendants("text"), element =>
            element.Value.StartsWith("Figure 2:", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_JmlrLdaPageFour_EmitsTwoIsolatedNumberedFormulaBlocks()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("jmlr-lda-page-4.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });
        _ = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement[] formulas = dom.Descendants()
            .Where(static element => element.Name.LocalName == "div" && HasClass(element, "pdf-semantic-formula"))
            .ToArray();
        Assert.Equal(2, formulas.Length);
        XElement equationOne = Assert.Single(formulas, static formula =>
            formula.Attribute("data-equation-number")?.Value == "(1)");
        XElement equationTwo = Assert.Single(formulas, static formula =>
            formula.Attribute("data-equation-number")?.Value == "(2)");
        Assert.All(formulas, static formula =>
        {
            Assert.True(HasClass(formula, "pdf-semantic-formula-native"));
            Assert.True(HasClass(formula, "pdf-semantic-formula-numbered"));
            Assert.Single(formula.Elements("math"));
        });
        Assert.Contains("p(θ|α)", equationOne.Attribute("aria-label")?.Value, StringComparison.Ordinal);
        Assert.Contains("p(θ,z,w|α,β)", equationTwo.Attribute("aria-label")?.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("We refer to the latent", equationTwo.Value, StringComparison.Ordinal);

        XElement footnote = Assert.Single(dom.Descendants("p"), static paragraph =>
            paragraph.Value.StartsWith("1. We refer to the latent", StringComparison.Ordinal));
        Assert.DoesNotContain(footnote.AncestorsAndSelf(), static element =>
            HasClass(element, "pdf-semantic-formula"));
        Assert.DoesNotContain(dom.Descendants("p"), static paragraph =>
            paragraph.Value.Trim() == "()" ||
            paragraph.Value.TrimStart().StartsWith("∏", StringComparison.Ordinal));
        Assert.DoesNotContain(dom.Descendants(), static element =>
            HasClass(element, "pdf-semantic-formula-run"));
    }

    [Fact]
    public void Convert_JmlrLdaPageEleven_EmitsTwoIsolatedUnnumberedFormulaBlocks()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("jmlr-lda-page-11.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });
        _ = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement[] formulas = dom.Descendants()
            .Where(static element => element.Name.LocalName == "div" && HasClass(element, "pdf-semantic-formula"))
            .ToArray();
        Assert.Equal(2, formulas.Length);
        XElement posterior = Assert.Single(formulas, static formula =>
            formula.Attribute("aria-label")?.Value.Contains("p(θ,z|w,α,β)", StringComparison.Ordinal) == true);
        Assert.True(HasClass(posterior, "pdf-semantic-formula-native"));
        Assert.Single(posterior.Elements("math"));
        Assert.Contains("p(θ,z,w|α,β)", posterior.Attribute("aria-label")?.Value, StringComparison.Ordinal);

        XElement marginal = Assert.Single(formulas, static formula =>
            formula.Attribute("aria-label")?.Value.Contains("p(w|α,β)", StringComparison.Ordinal) == true &&
            formula.Attribute("aria-label")?.Value.Contains('∫') == true);
        Assert.False(HasClass(marginal, "pdf-semantic-formula-native"));
        XElement outlineLayer = Assert.Single(
            marginal.Elements("svg"),
            static element => HasClass(element, "pdf-semantic-formula-glyph-outline-layer"));
        Assert.Equal(
            ["open", "close", "open", "close"],
            outlineLayer.Elements("path")
                .Where(static path => HasClass(path, "pdf-semantic-formula-tall-delimiter"))
                .Select(static path => path.Attribute("data-delimiter")?.Value ?? "")
                .ToArray());
        XElement vectorLayer = Assert.Single(
            marginal.Elements("svg"),
            static element => HasClass(element, "pdf-semantic-formula-vector-layer") &&
                !HasClass(element, "pdf-semantic-formula-glyph-outline-layer"));
        Assert.Single(vectorLayer.Elements("path"), static path =>
            path.Attribute("data-path-index")?.Value == "18");
        XElement[] marginalRuns = marginal.Descendants()
            .Where(static element => HasClass(element, "pdf-semantic-formula-run"))
            .ToArray();
        Assert.Equal(4, marginalRuns.Count(static run => run.Value == "∏"));
        Assert.Single(marginalRuns, static run => run.Value == "∫");
        Assert.DoesNotContain("coupling between", marginal.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "couplingbetween",
            marginal.Value.Replace(" ", "", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("a function which is intractable", marginal.Value, StringComparison.Ordinal);

        XElement introduction = Assert.Single(dom.Descendants("p"), static paragraph =>
            paragraph.Value.StartsWith("The key inferential problem", StringComparison.Ordinal));
        Assert.EndsWith("given a document:", introduction.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("p(θ", introduction.Value, StringComparison.Ordinal);
        XElement followingProse = Assert.Single(dom.Descendants("p"), static paragraph =>
            paragraph.Value.StartsWith("a function which is intractable", StringComparison.Ordinal));
        Assert.Contains("coupling between θ and β", followingProse.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(followingProse.AncestorsAndSelf(), static element =>
            HasClass(element, "pdf-semantic-formula"));
        Assert.DoesNotContain(dom.Descendants("p"), static paragraph =>
            paragraph.Value.Trim() is "(" or ")" or "()" or ")(" or "∫");
    }

    [Fact]
    public void Convert_JmlrLdaPageTwelve_EmitsAdjacentEquationsOnceAsFormulaBlocks()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("jmlr-lda-page-12.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });
        _ = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement[] formulas = dom.Descendants()
            .Where(static element => element.Name.LocalName == "div" && HasClass(element, "pdf-semantic-formula"))
            .ToArray();
        foreach (string equationNumber in new[] { "(4)", "(5)", "(6)", "(7)", "(8)" })
        {
            Assert.Single(formulas, formula =>
                formula.Attribute("data-equation-number")?.Value == equationNumber ||
                formula.Attribute("aria-label")?.Value.Contains(equationNumber, StringComparison.Ordinal) == true);
        }

        XElement equationSix = Assert.Single(formulas, static formula =>
            formula.Attribute("aria-label")?.Value.Contains("(6)", StringComparison.Ordinal) == true);
        XElement[] equationSixRuns = equationSix.Descendants()
            .Where(static element => HasClass(element, "pdf-semantic-formula-run"))
            .ToArray();
        Assert.DoesNotContain(equationSixRuns, static run => run.Value == "N");
        Assert.Equal(2, equationSixRuns.Count(static run => run.Value == "n"));

        XElement equationSeven = Assert.Single(formulas, static formula =>
            formula.Attribute("aria-label")?.Value.Contains("(7)", StringComparison.Ordinal) == true);
        Assert.False(HasClass(equationSeven, "pdf-semantic-formula-native"));
        XElement[] equationSevenRuns = equationSeven.Descendants()
            .Where(static element => HasClass(element, "pdf-semantic-formula-run"))
            .ToArray();
        Assert.Single(equationSevenRuns, static run => run.Value == "N");
        Assert.Equal(2, equationSevenRuns.Count(static run => run.Value == "n"));

        XElement equationEight = Assert.Single(formulas, static formula =>
            formula.Attribute("aria-label")?.Value.Contains("(8)", StringComparison.Ordinal) == true);
        Assert.Equal(
            ["open", "close"],
            equationEight.Descendants("path")
                .Where(static path => HasClass(path, "pdf-semantic-formula-tall-delimiter"))
                .Select(static path => path.Attribute("data-delimiter")?.Value ?? "")
                .ToArray());

        Assert.DoesNotContain(dom.Descendants("table"), static table =>
            table.Value.Contains("(6)", StringComparison.Ordinal) ||
            table.Value.Contains("(7)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Convert_JmlrComplexEquationFallbacks_PreserveSourceGeometryInBrowser()
    {
        using TempDirectory tempDirectory = new();
        string pageElevenDirectory = Path.Combine(tempDirectory.Path, "page-11");
        string pageTwelveDirectory = Path.Combine(tempDirectory.Path, "page-12");
        ConvertFixture("jmlr-lda-page-11.pdf").WriteToDirectory(pageElevenDirectory);
        ConvertFixture("jmlr-lda-page-12.pdf").WriteToDirectory(pageTwelveDirectory);

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
                Height = 1200
            }
        });

        await page.GotoAsync(new Uri(Path.Combine(pageElevenDirectory, "index.html")).AbsoluteUri);
        await page.EvaluateAsync("() => document.fonts.ready");
        JmlrPageElevenBrowserMetrics pageEleven = await page.EvaluateAsync<JmlrPageElevenBrowserMetrics>(
            """
            () => {
              const formula = Array.from(document.querySelectorAll('.pdf-semantic-formula'))
                .find(element => element.getAttribute('aria-label')?.includes('∫'));
              const delimiters = Array.from(
                formula.querySelectorAll('.pdf-semantic-formula-tall-delimiter'));
              const fractionRule = formula.querySelector('[data-path-index="18"]');
              const formulaBox = formula.getBoundingClientRect();
              const ruleBox = fractionRule.getBoundingClientRect();
              return {
                delimiterCount: delimiters.length,
                minimumDelimiterHeight: Math.min(
                  ...delimiters.map(delimiter => delimiter.getBoundingClientRect().height)),
                fractionRuleWidth: ruleBox.width,
                fractionRuleInside:
                  ruleBox.left >= formulaBox.left - 1 &&
                  ruleBox.right <= formulaBox.right + 1 &&
                  ruleBox.top >= formulaBox.top - 1 &&
                  ruleBox.bottom <= formulaBox.bottom + 1,
                productCount: Array.from(
                  formula.querySelectorAll('.pdf-semantic-formula-run'))
                  .filter(run => run.textContent === '∏').length
              };
            }
            """);

        Assert.Equal(4, pageEleven.DelimiterCount);
        Assert.True(pageEleven.MinimumDelimiterHeight >= 36);
        Assert.True(pageEleven.FractionRuleWidth >= 45);
        Assert.True(pageEleven.FractionRuleInside);
        Assert.Equal(4, pageEleven.ProductCount);

        await page.GotoAsync(new Uri(Path.Combine(pageTwelveDirectory, "index.html")).AbsoluteUri);
        await page.EvaluateAsync("() => document.fonts.ready");
        JmlrPageTwelveBrowserMetrics pageTwelve = await page.EvaluateAsync<JmlrPageTwelveBrowserMetrics>(
            """
            () => {
              const formulas = Array.from(document.querySelectorAll('.pdf-semantic-formula'));
              const formula = number => formulas.find(element =>
                element.dataset.equationNumber === number ||
                element.getAttribute('aria-label')?.includes(number));
              const six = formula('(6)');
              const seven = formula('(7)');
              const eight = formula('(8)');
              const runs = element => Array.from(
                element.querySelectorAll('.pdf-semantic-formula-run'));
              const sevenRuns = runs(seven);
              const sumIndex = sevenRuns.findIndex(run => run.textContent === '∑');
              const sumBox = sevenRuns[sumIndex].getBoundingClientRect();
              const upperBox = sevenRuns
                .slice(sumIndex + 1)
                .find(run => run.textContent === 'N')
                .getBoundingClientRect();
              const lowerBox = sevenRuns
                .slice(sumIndex + 1)
                .find(run => run.textContent === 'n')
                .getBoundingClientRect();
              const center = box => ({
                x: box.left + box.width / 2,
                y: box.top + box.height / 2
              });
              const sumCenter = center(sumBox);
              const sixBox = six.getBoundingClientRect();
              const sevenBox = seven.getBoundingClientRect();
              const eightSum = runs(eight).find(run => run.textContent === '∑')
                .getBoundingClientRect();
              const eightDelimiters = Array.from(
                eight.querySelectorAll('.pdf-semantic-formula-tall-delimiter'));
              return {
                formulaCount: formulas.length,
                sixUpperNCount: runs(six).filter(run => run.textContent === 'N').length,
                sevenUpperNCount: sevenRuns.filter(run => run.textContent === 'N').length,
                sevenLowerNCount: sevenRuns.filter(run => run.textContent === 'n').length,
                upperAboveOperator: center(upperBox).y < sumCenter.y,
                lowerBelowOperator: center(lowerBox).y > sumCenter.y,
                upperCenterDelta: Math.abs(center(upperBox).x - sumCenter.x),
                lowerCenterDelta: Math.abs(center(lowerBox).x - sumCenter.x),
                adjacentEquationsSeparated: sixBox.bottom <= sevenBox.top + 1,
                equationEightDelimiterCount: eightDelimiters.length,
                equationEightMinimumDelimiterHeight: Math.min(
                  ...eightDelimiters.map(delimiter => delimiter.getBoundingClientRect().height)),
                equationEightSumHeight: eightSum.height
              };
            }
            """);

        Assert.Equal(5, pageTwelve.FormulaCount);
        Assert.Equal(0, pageTwelve.SixUpperNCount);
        Assert.Equal(1, pageTwelve.SevenUpperNCount);
        Assert.Equal(2, pageTwelve.SevenLowerNCount);
        Assert.True(pageTwelve.UpperAboveOperator);
        Assert.True(pageTwelve.LowerBelowOperator);
        Assert.InRange(pageTwelve.UpperCenterDelta, 0, 12);
        Assert.InRange(pageTwelve.LowerCenterDelta, 0, 12);
        Assert.True(pageTwelve.AdjacentEquationsSeparated);
        Assert.Equal(2, pageTwelve.EquationEightDelimiterCount);
        Assert.True(
            pageTwelve.EquationEightMinimumDelimiterHeight >=
            pageTwelve.EquationEightSumHeight * 1.5);

        static PdfHtmlDocument ConvertFixture(string fileName)
        {
            using PDDocument document = Loader.LoadPDF(FixturePath(fileName));
            PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
            {
                IncludeImages = false,
                IncludeLinks = false,
                IncludePaths = true
            });
            return PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
            {
                TextMode = PdfHtmlTextMode.Semantic,
                SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
            });
        }
    }

    private static string Render(
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyList<PdfLayoutPath> paths,
        out PdfMathMlFormula formula)
    {
        Assert.True(PdfMathMlFormula.TryCreate(glyphs, paths, out PdfMathMlFormula? candidate));
        formula = Assert.IsType<PdfMathMlFormula>(candidate);
        StringBuilder markup = new();
        markup.Append("<div>");
        formula.WriteTo(markup);
        markup.Append("</div>");
        return markup.ToString();
    }

    private static PdfTextGlyph Glyph(
        string text,
        float x,
        float y,
        float width = 8f,
        float height = 8f,
        float fontSize = 10f,
        string fontName = "CMMI10")
    {
        return new PdfTextGlyph(text, fontName, fontSize, 0f, new PdfLayoutRectangle(x, y, width, height), Black);
    }

    private static PdfTextGlyph[] MatrixGlyphs(float secondRowSecondColumnX)
    {
        return
        [
            Glyph("A", 92f, 100f),
            Glyph("=", 104f, 100f, fontName: "CMR10"),
            Glyph("[", 120f, 86f, 6f, 36f, fontName: "CMEX10"),
            Glyph("a", 132f, 91f),
            Glyph("b", 150f, 91f),
            Glyph("c", 132f, 109f),
            Glyph("d", secondRowSecondColumnX, 109f),
            Glyph("]", 166f, 86f, 6f, 36f, fontName: "CMEX10")
        ];
    }

    private static PdfTextGlyph OffsetGlyph(PdfTextGlyph glyph, float x, float y)
    {
        PdfLayoutRectangle bounds = glyph.Bounds;
        PdfLayoutRectangle pageBounds = glyph.PageBounds;
        return glyph with
        {
            Bounds = new PdfLayoutRectangle(bounds.X + x, bounds.Y + y, bounds.Width, bounds.Height),
            PageBounds = new PdfLayoutRectangle(
                pageBounds.X + x,
                pageBounds.Y + y,
                pageBounds.Width,
                pageBounds.Height)
        };
    }

    private static PdfLayoutPath Rule(float x, float y, float width)
    {
        return new PdfLayoutPath(0, [], new PdfLayoutRectangle(x, y, width, 0.4f), null, null, null);
    }

    private static PdfSemanticTableCell TableCell(
        IReadOnlyList<PdfTextGlyph> glyphs,
        bool borderTop = false,
        bool borderRight = false,
        bool borderBottom = false,
        bool borderLeft = false)
    {
        PdfSemanticLine line = SemanticLine(glyphs);
        return new PdfSemanticTableCell(
            line.Text,
            line.Bounds,
            [line],
            borderTop,
            borderRight,
            borderBottom,
            borderLeft);
    }

    private static PdfSemanticElement Table(params PdfSemanticTableCell[] cells)
    {
        PdfSemanticTableRow[] rows = cells
            .Select(static cell => new PdfSemanticTableRow([cell], isHeader: false))
            .ToArray();
        PdfSemanticLine[] lines = cells.SelectMany(static cell => cell.Lines).ToArray();
        return new PdfSemanticElement(
            PdfSemanticElementKind.Table,
            string.Join(Environment.NewLine, cells.Select(static cell => cell.Text)),
            Bounds(cells.Select(static cell => cell.Bounds)),
            lines,
            tableRows: rows);
    }

    private static PdfSemanticElement Element(
        PdfSemanticElementKind kind,
        params PdfSemanticLine[] lines)
    {
        return new PdfSemanticElement(
            kind,
            string.Join(Environment.NewLine, lines.Select(static line => line.Text)),
            Bounds(lines.Select(static line => line.Bounds)),
            lines);
    }

    private static PdfSemanticLine SemanticLine(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        PdfTextRun[] runs = glyphs.Select(glyph => new PdfTextRun(
            glyph.Text,
            glyph.FontName,
            glyph.FontSize,
            glyph.Direction,
            glyph.Bounds,
            glyph.Color,
            [glyph],
            glyph.PageBounds)).ToArray();
        return new PdfSemanticLine(
            string.Concat(glyphs.Select(static glyph => glyph.Text)),
            Bounds(glyphs.Select(static glyph => glyph.Bounds)),
            glyphs[0].FontName,
            glyphs[0].FontSize,
            glyphs[0].Direction,
            glyphs[0].Color,
            runs);
    }

    private static PdfTextLine LayoutLine(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        PdfTextRun[] runs = glyphs.Select(glyph => new PdfTextRun(
            glyph.Text,
            glyph.FontName,
            glyph.FontSize,
            glyph.Direction,
            glyph.Bounds,
            glyph.Color,
            [glyph],
            glyph.PageBounds)).ToArray();
        return new PdfTextLine(
            string.Concat(glyphs.Select(static glyph => glyph.Text)),
            Bounds(glyphs.Select(static glyph => glyph.Bounds)),
            runs);
    }

    private static PdfLayoutRectangle Bounds(IEnumerable<PdfLayoutRectangle> rectangles)
    {
        PdfLayoutRectangle[] values = rectangles.ToArray();
        float left = values.Min(static bounds => bounds.X);
        float top = values.Min(static bounds => bounds.Y);
        float right = values.Max(static bounds => bounds.Right);
        float bottom = values.Max(static bounds => bounds.Bottom);
        return new PdfLayoutRectangle(left, top, right - left, bottom - top);
    }

    private static XDocument Parse(string markup) => XDocument.Parse(markup);

    private static XDocument ParseHtml(string html)
    {
        string xml = Regex.Replace(html, "<!doctype html>\\s*", "", RegexOptions.IgnoreCase)
            .Replace("\0", "", StringComparison.Ordinal);
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

    private sealed class MatrixBrowserMetrics
    {
        public string TableDisplay { get; set; } = "";

        public string TableVisibility { get; set; } = "";

        public double TableOpacity { get; set; }

        public double FormulaWidth { get; set; }

        public double FormulaHeight { get; set; }

        public double TableWidth { get; set; }

        public double TableHeight { get; set; }

        public bool TableVisible { get; set; }

        public bool MathContained { get; set; }

        public bool TableContained { get; set; }

        public bool RowsNonOverlapping { get; set; }

        public bool SiblingsNonOverlapping { get; set; }

        public int RowCount { get; set; }

        public int CellCount { get; set; }
    }

    private sealed class JmlrPageElevenBrowserMetrics
    {
        public int DelimiterCount { get; set; }

        public double MinimumDelimiterHeight { get; set; }

        public double FractionRuleWidth { get; set; }

        public bool FractionRuleInside { get; set; }

        public int ProductCount { get; set; }

    }

    private sealed class JmlrPageTwelveBrowserMetrics
    {
        public int FormulaCount { get; set; }

        public int SixUpperNCount { get; set; }

        public int SevenUpperNCount { get; set; }

        public int SevenLowerNCount { get; set; }

        public bool UpperAboveOperator { get; set; }

        public bool LowerBelowOperator { get; set; }

        public double UpperCenterDelta { get; set; }

        public double LowerCenterDelta { get; set; }

        public bool AdjacentEquationsSeparated { get; set; }

        public int EquationEightDelimiterCount { get; set; }

        public double EquationEightMinimumDelimiterHeight { get; set; }

        public double EquationEightSumHeight { get; set; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pdfbox-net-mathml-matrix-" + Guid.NewGuid().ToString("N"));
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
