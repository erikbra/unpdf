using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using PdfBox.Net.COS;
using PdfBox.Net.ConversionQuality;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

namespace PdfBox.Net.Html.Tests;

public sealed class HtmlReviewArtifactGeneratorTest
{
    [Fact]
    public void Generate_CopiesSourcePdfBesideGeneratedHtmlAndComparisonPage()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source-input.pdf");
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
        }

        string manifestPath = Path.Combine(tempDirectory.Path, "manifest.json");
        string outputDirectory = Path.Combine(tempDirectory.Path, "html-review");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    examples = new[]
                    {
                        new
                        {
                            id = "review-artifact-sample",
                            title = "Review artifact sample",
                            sourcePdf,
                            notes = "Synthetic test manifest.",
                            expectations = new
                            {
                                pageCount = 1,
                                minTextRuns = 0,
                                minImagePlacements = 0,
                                minVectorPaths = 0,
                                minLinks = 0
                            }
                        }
                    }
                },
                new JsonSerializerOptions { WriteIndented = true }));

        HtmlReviewArtifactResult result = HtmlReviewArtifactGenerator.Generate(manifestPath, outputDirectory);

        HtmlReviewExampleResult example = Assert.Single(result.Examples);
        Assert.Equal("review-artifact-sample", example.Id);
        Assert.Equal(1, example.PageCount);
        string exampleDirectory = Path.Combine(outputDirectory, "review-artifact-sample");
        string copiedSource = Path.Combine(exampleDirectory, "source.pdf");
        string convertedHtml = Path.Combine(exampleDirectory, "index.html");
        string css = Path.Combine(exampleDirectory, "assets", "pdfbox-net-fixed.css");
        string semanticHtml = Path.Combine(exampleDirectory, "semantic", "index.html");
        string semanticCss = Path.Combine(exampleDirectory, "semantic", "assets", "pdfbox-net-semantic.css");
        string continuousSemanticHtml = Path.Combine(exampleDirectory, "semantic-continuous", "index.html");
        string continuousSemanticCss = Path.Combine(exampleDirectory, "semantic-continuous", "assets", "pdfbox-net-semantic-continuous.css");
        string compare = Path.Combine(exampleDirectory, "compare.html");
        string qualityReportJson = Path.Combine(exampleDirectory, "quality", "quality-report.json");
        string qualityReportMarkdown = Path.Combine(exampleDirectory, "quality", "quality-report.md");

        Assert.True(File.Exists(Path.Combine(outputDirectory, "index.html")));
        Assert.True(File.Exists(copiedSource));
        Assert.True(File.Exists(convertedHtml));
        Assert.True(File.Exists(css));
        Assert.True(File.Exists(semanticHtml));
        Assert.True(File.Exists(semanticCss));
        Assert.True(File.Exists(continuousSemanticHtml));
        Assert.True(File.Exists(continuousSemanticCss));
        Assert.True(File.Exists(compare));
        Assert.True(File.Exists(qualityReportJson));
        Assert.True(File.Exists(qualityReportMarkdown));
        Assert.Equal(File.ReadAllBytes(sourcePdf), File.ReadAllBytes(copiedSource));
        string comparisonHtml = File.ReadAllText(compare);
        Assert.Contains("source.pdf", comparisonHtml);
        Assert.Contains("semantic-continuous/index.html", comparisonHtml);
        Assert.Contains("quality/quality-report.md", comparisonHtml);
        Assert.DoesNotContain("Fixed-layout HTML", comparisonHtml);
        Assert.DoesNotContain("<h2>Semantic HTML</h2>", comparisonHtml);
        Assert.DoesNotContain("semantic/index.html", comparisonHtml);
        Assert.Contains("data-splitter", comparisonHtml);
        Assert.Contains("Resize comparison panes", comparisonHtml);
        Assert.Contains("pointerdown", comparisonHtml);
        Assert.DoesNotContain("Skia raster fallback", comparisonHtml);
        Assert.False(Directory.Exists(Path.Combine(exampleDirectory, "semantic-continuous-skia-raster")));
        Assert.False(File.Exists(Path.Combine(exampleDirectory, "figure-rendering-compare.html")));
        string artifactIndex = File.ReadAllText(Path.Combine(outputDirectory, "index.html"));
        Assert.Contains("review-artifact-sample/compare.html", artifactIndex);
        Assert.DoesNotContain("review-artifact-sample/semantic/index.html", artifactIndex);
        Assert.DoesNotContain("figure-rendering-compare.html", artifactIndex);
        Assert.Contains("review-artifact-sample/semantic-continuous/index.html", artifactIndex);
        Assert.Contains("review-artifact-sample/quality/quality-report.md", artifactIndex);
        string summary = File.ReadAllText(Path.Combine(exampleDirectory, "summary.md"));
        Assert.Contains("semantic-continuous/index.html", summary);
        Assert.DoesNotContain("figure-rendering-compare.html", summary);
        Assert.DoesNotContain("semantic/index.html", summary);

        using JsonDocument quality = JsonDocument.Parse(File.ReadAllText(qualityReportJson));
        Assert.Equal(1, quality.RootElement.GetProperty("Schema").GetInt32());
        Assert.Equal("../semantic-continuous/index.html", quality.RootElement.GetProperty("Html").GetString());
        Assert.Equal("Synthetic test manifest.", quality.RootElement.GetProperty("Notes").GetString());
        Assert.True(quality.RootElement.GetProperty("IssueCategories").GetArrayLength() > 0);
        Assert.True(quality.RootElement.GetProperty("Limitations").GetArrayLength() > 0);
        Assert.True(example.QualityStatus.Length > 0);
        string qualityMarkdown = File.ReadAllText(qualityReportMarkdown);
        Assert.Contains("- Sample notes: Synthetic test manifest.", qualityMarkdown);
        Assert.Contains("## Current Limitations", qualityMarkdown);
        string[] qualityArtifacts = quality.RootElement.GetProperty("Artifacts")
            .EnumerateArray()
            .Select(static artifact => artifact.GetString() ?? "")
            .ToArray();
        if (qualityArtifacts.Contains("page-1-diff.png", StringComparer.Ordinal))
        {
            Assert.Contains("review-artifact-sample/quality/page-1-diff.png", artifactIndex);
            Assert.Contains("quality/page-1-diff.png", File.ReadAllText(Path.Combine(exampleDirectory, "summary.md")));
        }
    }

    [Fact]
    public void ValidateExpectations_AcceptsNormalizedRequiredTextAndConservativeFloors()
    {
        PdfLayoutDocument layout = CreateSyntheticLayout("Review artifact sample");
        HtmlReviewManifestExample example = new()
        {
            Id = "normalized-expectations",
            Expectations = new HtmlReviewExpectations
            {
                PageCount = 1,
                RequiredText = ["review-artifact", "sample"],
                MinTextRuns = 1,
                MinImagePlacements = 0,
                MinVectorPaths = 0,
                MinLinks = 0
            }
        };

        HtmlReviewArtifactGenerator.ValidateExpectations(example, layout);
    }

    [Fact]
    public void ValidateExpectations_RejectsMissingImagesOnRequiredPage()
    {
        PdfLayoutDocument layout = CreateSyntheticLayout("Page one", "Page two");
        HtmlReviewManifestExample example = new()
        {
            Id = "page-image-expectations",
            Expectations = new HtmlReviewExpectations
            {
                PageCount = 2,
                RequiredText = ["page"],
                MinTextRuns = 1,
                MinImagePlacementsByPage = new Dictionary<int, int> { [2] = 1 }
            }
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => HtmlReviewArtifactGenerator.ValidateExpectations(example, layout));

        Assert.Contains("image placements on page 2 was 0, expected at least 1", exception.Message);
    }

    [Fact]
    public void ValidateSemanticExpectations_RequiresExactOrderedAndUnorderedListShapesByPage()
    {
        HtmlReviewManifestExample example = new()
        {
            Id = "semantic-list-expectations",
            Expectations = new HtmlReviewExpectations
            {
                SemanticOrderedListItemCountsByPage = new Dictionary<int, List<int>>
                {
                    [1] = [4]
                },
                SemanticUnorderedListItemCountsByPage = new Dictionary<int, List<int>>
                {
                    [3] = [10]
                }
            }
        };
        PdfHtmlDocument accepted = new(
            "<html><body><section data-page-number=\"1\"><ol><li>A</li><li>B</li><li>C</li><li>D</li></ol></section>" +
            "<section data-page-number=\"3\"><ul>" + string.Concat(Enumerable.Repeat("<li>Member</li>", 10)) +
            "</ul></section></body></html>",
            "styles.css",
            "");
        PdfHtmlDocument rejected = new(
            "<html><body><section data-page-number=\"1\"><ol><li>A</li><li>B</li><li>C</li></ol></section>" +
            "<section data-page-number=\"3\"><ul>" + string.Concat(Enumerable.Repeat("<li>Member</li>", 9)) +
            "</ul></section></body></html>",
            "styles.css",
            "");

        HtmlReviewArtifactGenerator.ValidateSemanticExpectations(example, accepted);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            HtmlReviewArtifactGenerator.ValidateSemanticExpectations(example, rejected));
        Assert.Contains("item counts on page 1 were [3], expected [4]", exception.Message);
        Assert.Contains("unordered-list item counts on page 3 were [9], expected [10]", exception.Message);
    }

    [Fact]
    public void ValidateSemanticExpectations_RequiresStableMixedRegionStructureByPage()
    {
        HtmlReviewManifestExample example = new()
        {
            Id = "mixed-regions",
            Expectations = new HtmlReviewExpectations
            {
                SemanticMixedRegionCountsByPage = new Dictionary<int, int>
                {
                    [2] = 1
                }
            }
        };
        PdfHtmlDocument accepted = new(
            """
            <html><body>
              <div class="pdf-semantic-columns pdf-semantic-mixed-regions" data-source-page="2">
                <div class="pdf-semantic-column">
                  <figure class="pdf-semantic-figure pdf-semantic-mixed-region-figure"></figure>
                </div>
                <div class="pdf-semantic-column"><p>Instructions</p></div>
              </div>
            </body></html>
            """,
            "styles.css",
            "");
        PdfHtmlDocument rejected = new(
            """
            <html><body>
              <div class="pdf-semantic-columns" data-source-page="2">
                <div class="pdf-semantic-column"></div>
                <div class="pdf-semantic-column"><p>Instructions</p></div>
              </div>
            </body></html>
            """,
            "styles.css",
            "");

        HtmlReviewArtifactGenerator.ValidateSemanticExpectations(example, accepted);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            HtmlReviewArtifactGenerator.ValidateSemanticExpectations(example, rejected));
        Assert.Contains("mixed-region count on page 2 was 0, expected 1", exception.Message);
    }

    [Fact]
    public void Generate_FailsWhenStableLayoutExpectationsAreNotMet()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source-input.pdf");
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
        }

        string manifestPath = Path.Combine(tempDirectory.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    examples = new[]
                    {
                        new
                        {
                            id = "failing-expectations",
                            sourcePdf,
                            expectations = new
                            {
                                pageCount = 2,
                                requiredText = new[] { "missing-token" },
                                minTextRuns = 100,
                                minImagePlacements = 1,
                                minVectorPaths = 1,
                                minLinks = 1
                            }
                        }
                    }
                }));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            HtmlReviewArtifactGenerator.Generate(manifestPath, Path.Combine(tempDirectory.Path, "html-review")));

        Assert.Contains("pages was 1, expected 2", exception.Message);
        Assert.Contains("text runs was", exception.Message);
        Assert.Contains("required text 'missing-token'", exception.Message);
        Assert.Contains("image placements was 0", exception.Message);
        Assert.Contains("vector paths was 0", exception.Message);
        Assert.Contains("links was 0", exception.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsWordBoundaryLossWithoutFailingProbe()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8" /></head>
            <body style="margin:0;background:#f3f4f6">
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;background:white">
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt;font-family:Arial,sans-serif">Reviewartifactsample</span>
              </section>
            </body>
            </html>
            """);

        string outputDirectory = Path.Combine(tempDirectory.Path, "quality");
        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            outputDirectory,
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal("needs-review", report.Status);
        Assert.Contains(report.Checks, check => check.Id == "word-boundaries" && check.Status == "needs-review");
        Assert.Contains(report.Limitations, limitation => limitation.Contains("word boundaries", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "quality-report.json")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "quality-report.md")));
    }

    [Fact]
    public async Task AnalyzeAsync_ExcludesPageBackgroundsFromOverlapChecks()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body style="margin:0">
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;overflow:hidden">
                <div class="pdf-image" style="position:absolute;left:0;top:0;width:612pt;height:700pt"></div>
                <svg viewBox="0 0 612 792" style="position:absolute;inset:0;width:612pt;height:792pt">
                  <path data-path-index="0" d="M0 0H612V792H0Z" />
                </svg>
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt">Review artifact sample</span>
              </section>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck imageCheck = Assert.Single(
            report.Checks,
            check => check.Id == "text-image-overlap" && check.PageNumber == 1);
        Assert.Equal("passed", imageCheck.Status);
        Assert.Equal(0d, imageCheck.Metrics["overlapCount"]);
        Assert.Equal(1d, imageCheck.Metrics["backgroundImageCount"]);
        Assert.Equal(0d, imageCheck.Metrics["overlapCandidateImageCount"]);

        PdfHtmlQualityCheck vectorCheck = Assert.Single(
            report.Checks,
            check => check.Id == "text-vector-overlap" && check.PageNumber == 1);
        Assert.Equal("passed", vectorCheck.Status);
        Assert.Equal(0d, vectorCheck.Metrics["overlapCount"]);
        Assert.Equal(1d, vectorCheck.Metrics["backgroundVectorPathCount"]);
        Assert.Equal(0d, vectorCheck.Metrics["overlapCandidateVectorPathCount"]);
    }

    [Fact]
    public async Task AnalyzeAsync_StillReportsOrdinaryContentObjectOverlaps()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body style="margin:0">
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;overflow:hidden">
                <div class="pdf-image" style="position:absolute;left:68pt;top:76pt;width:180pt;height:30pt"></div>
                <svg viewBox="0 0 180 30" style="position:absolute;left:68pt;top:76pt;width:180pt;height:30pt">
                  <path data-path-index="0" d="M0 0H180V30H0Z" />
                </svg>
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt">Review artifact sample</span>
              </section>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck imageCheck = Assert.Single(
            report.Checks,
            check => check.Id == "text-image-overlap" && check.PageNumber == 1);
        Assert.Equal("needs-review", imageCheck.Status);
        Assert.Equal(1d, imageCheck.Metrics["overlapCount"]);
        Assert.Equal(0d, imageCheck.Metrics["backgroundImageCount"]);
        Assert.Equal(1d, imageCheck.Metrics["overlapCandidateImageCount"]);

        PdfHtmlQualityCheck vectorCheck = Assert.Single(
            report.Checks,
            check => check.Id == "text-vector-overlap" && check.PageNumber == 1);
        Assert.Equal("needs-review", vectorCheck.Status);
        Assert.Equal(1d, vectorCheck.Metrics["overlapCount"]);
        Assert.Equal(0d, vectorCheck.Metrics["backgroundVectorPathCount"]);
        Assert.Equal(1d, vectorCheck.Metrics["overlapCandidateVectorPathCount"]);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsContextualJoiningOfAdjacentSourceWords()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        string padding = string.Join(" ", Enumerable.Repeat(
            "Reliable adaptive optimization preserves readable prose across complex scientific documents and practical reports.",
            12));
        string sourceText = padding + " A careful choice of stepsizes at timestep t remains important for robust training.";
        string htmlText = padding + " A careful choice ofstepsizes attimestep t remains important for robust training.";
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument(sourceText))
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            $$"""
            <!doctype html>
            <html lang="en">
            <body>
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;background:white">
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt">{{htmlText}}</span>
              </section>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck boundaryCheck = Assert.Single(report.Checks, static check =>
            check.Id == "word-boundaries" && check.PageNumber == 1);
        Assert.Equal("needs-review", boundaryCheck.Status);
        Assert.True(boundaryCheck.Metrics["tokenCoverage"] > 0.95d);
        Assert.Equal(2d, boundaryCheck.Metrics["adjacentSourceWordJoinCount"]);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotTreatMathLinksCodePunctuationOrAttestedCompoundsAsWordJoins()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        string padding = string.Join(" ", Enumerable.Repeat(
            "Reliable adaptive optimization preserves readable prose across complex scientific documents and practical reports.",
            12));
        string sourceText = padding + " alpha beta gamma delta example.com user_id data base database end result stable output";
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument(sourceText))
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            $$"""
            <!doctype html>
            <html lang="en">
            <body>
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;background:white">
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt">
                  {{padding}}
                  <span class="pdf-semantic-math">alphabeta</span>
                  <a href="https://example.test">gammadelta</a>
                  examplecom
                  <code>userid</code>
                  database database end result stable output
                </span>
              </section>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck boundaryCheck = Assert.Single(report.Checks, static check =>
            check.Id == "word-boundaries" && check.PageNumber == 1);
        Assert.Equal(0d, boundaryCheck.Metrics["adjacentSourceWordJoinCount"]);
    }

    [Fact]
    public async Task AnalyzeAsync_ExtractsSelfDescribingFixtureExpectations()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument("The patch should look like the reference image."))
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body>
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;background:white">
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt">The patch should look like the reference image.</span>
              </section>
            </body>
            </html>
            """);

        string outputDirectory = Path.Combine(tempDirectory.Path, "quality");
        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            outputDirectory,
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck expectation = Assert.Single(report.Checks, check => check.Id == "fixture-expectation");
        Assert.Equal("passed", expectation.Status);
        Assert.Contains("should look like the reference image", expectation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.IssueCategories, category => category.Id == "fixture-expectations" && category.Status == "needs-review");
        string qualityMarkdown = File.ReadAllText(Path.Combine(outputDirectory, "quality-report.md"));
        Assert.Contains("## Fixture Expectations", qualityMarkdown);
        Assert.Contains("should look like the reference image", qualityMarkdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsChangedFillColorsWithoutRegressingForegroundDiagnostics()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateBlueRectangleDocument(pageCount: 3, tinyLastPage: true))
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8" /></head>
            <body style="margin:0;background:#f3f4f6">
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;background:white;overflow:hidden">
                <div style="position:absolute;left:72.2pt;top:72.2pt;width:144pt;height:72pt;background:#ff0000"></div>
              </section>
              <section class="pdf-page" data-page-number="2" style="position:relative;width:612pt;height:792pt;background:white;overflow:hidden">
                <div style="position:absolute;left:72.2pt;top:72.2pt;width:144pt;height:72pt;background:#0000ff"></div>
              </section>
              <section class="pdf-page" data-page-number="3" style="position:relative;width:612pt;height:792pt;background:white;overflow:hidden">
                <div style="position:absolute;left:72.2pt;top:72.2pt;width:5pt;height:5pt;background:#ff0000"></div>
              </section>
            </body>
            </html>
            """);

        string outputDirectory = Path.Combine(tempDirectory.Path, "quality");
        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            outputDirectory,
            MaxPages: 3),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck changedFillForeground = Assert.Single(
            report.Pages[0].Checks,
            check => check.Id == "visual-foreground-mask");
        PdfHtmlQualityCheck changedFillColor = Assert.Single(
            report.Pages[0].Checks,
            check => check.Id == "visual-color-difference");
        PdfHtmlQualityCheck matchingFillColor = Assert.Single(
            report.Pages[1].Checks,
            check => check.Id == "visual-color-difference");
        PdfHtmlQualityCheck tinyFillColor = Assert.Single(
            report.Pages[2].Checks,
            check => check.Id == "visual-color-difference");

        Assert.True(
            changedFillForeground.Status == "passed",
            $"{changedFillForeground.Message} {JsonSerializer.Serialize(changedFillForeground.Metrics)}");
        Assert.InRange(changedFillForeground.Metrics["foregroundDeltaRatio"], 0, 0.02);
        Assert.Equal("needs-review", changedFillColor.Status);
        Assert.True(changedFillColor.Metrics["meanColorDelta"] > 20);
        Assert.True(changedFillColor.Metrics["severeColorDeltaRatio"] > 0.95);
        Assert.Equal("passed", matchingFillColor.Status);
        Assert.True(matchingFillColor.Metrics["severeColorDeltaRatio"] < 0.01);
        Assert.Equal("skipped", tinyFillColor.Status);
        Assert.True(tinyFillColor.Metrics["colorComparedPixelRatio"] < 0.005);
        Assert.Contains(report.IssueCategories, category =>
            category.Id == "visual-foreground" && category.Status == "passed");
        Assert.Contains(report.IssueCategories, category =>
            category.Id == "visual-color" && category.Status == "needs-review");
        Assert.Contains("page-1-diff.png", report.Artifacts);
        Assert.Contains("page-1-color-heatmap.png", report.Artifacts);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "page-1-color-heatmap.png")));
        Assert.Contains(
            "yellow through dark red",
            File.ReadAllText(Path.Combine(outputDirectory, "page-1-visual-report.html")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotDoubleCountMeasuredSvgText()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument("Measured display title"))
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body>
              <section class="pdf-page" data-page-number="1" style="position:relative;width:612pt;height:792pt;background:white">
                <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt;font-size:12pt">
                  <span class="pdf-text-run-copy" aria-hidden="true">Measured display title</span>
                  <svg class="pdf-text-run-svg" aria-hidden="true"><text>Measured display title</text></svg>
                </span>
              </section>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityPageReport page = Assert.Single(report.Pages);
        Assert.Equal(3, page.HtmlWordCount);
        Assert.Equal(1d, page.TextTokenCoverage);
    }

    [Fact]
    public async Task AnalyzeAsync_RecognizesSemanticGridCellsAsTextRuns()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument("Grid review text"))
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body>
              <main class="pdf-semantic-document-flow">
                <div class="pdf-semantic-page-break pdf-semantic-page-start" data-page-number="1"></div>
                <section class="pdf-semantic-line-grid">
                  <span class="pdf-semantic-line-grid-cell">Grid review text</span>
                </section>
              </main>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityPageReport page = Assert.Single(report.Pages);
        Assert.Equal(layout.Pages[0].Runs.Count, page.HtmlTextRuns);
        Assert.Equal(3, page.HtmlWordCount);
        Assert.Equal(1d, page.TextTokenCoverage);
        Assert.Contains(report.Checks, check => check.Id == "text-run-count" && check.Status == "passed");
        Assert.Contains(report.Checks, check => check.Id == "word-boundaries" && check.Status == "passed");
    }

    [Fact]
    public async Task AnalyzeAsync_SplitsContinuousTextAtInlineSourcePageMarkers()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        using (PDDocument document = new())
        {
            document.AddPage(new PDPage());
            document.AddPage(new PDPage());
            document.Save(sourcePdf);
        }

        PdfLayoutDocument layout = CreateSyntheticLayout("Page one", "Page two");
        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <style>
              .pdf-semantic-inline-page-break { display:block; height:12px; }
              .after-page-break { display:block; }
            </style>
            <body>
              <main class="pdf-semantic-document-flow" style="width:816px;padding-bottom:24px">
                <div class="pdf-semantic-page-break pdf-semantic-page-start" data-page-number="1"></div>
                <p class="pdf-semantic-element" style="padding-bottom:24px">Page one<span class="pdf-semantic-page-break pdf-semantic-inline-page-break" data-page-number="2"></span><span class="after-page-break">Page two</span></p>
              </main>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 2),
            TestContext.Current.CancellationToken);

        Assert.All(report.Pages, page =>
        {
            Assert.Equal(2, page.SourceWordCount);
            Assert.Equal(2, page.HtmlWordCount);
            Assert.Equal(1d, page.TextTokenCoverage);
        });
        Assert.All(
            report.Checks.Where(check => check.Id == "text-run-count"),
            check => Assert.Equal("skipped", check.Status));
    }

    [Fact]
    public async Task AnalyzeAsync_BoundsContinuousPageAtFollowingFixedPage()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        using (PDDocument document = new())
        {
            document.AddPage(new PDPage());
            document.AddPage(new PDPage());
            document.Save(sourcePdf);
        }

        PdfLayoutDocument layout = CreateSyntheticLayout("Page one", "Page two");
        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body style="margin:0">
              <main class="pdf-semantic-document-flow" style="width:816px">
                <div class="pdf-semantic-page-break pdf-semantic-page-start" data-page-number="1"></div>
                <p class="pdf-semantic-element" style="height:100px;margin:0">Page one</p>
                <section class="pdf-page" data-page-number="2" style="position:relative;width:612pt;height:792pt;background:white">
                  <span class="pdf-text-run" style="position:absolute;left:72pt;top:80pt">Page two</span>
                  <div class="pdf-image" style="position:absolute;left:72pt;top:120pt;width:40pt;height:40pt;background:#000"></div>
                  <svg style="position:absolute;left:72pt;top:180pt;width:40pt;height:40pt"><path data-path-index="1" d="M0 0h40v40z" /></svg>
                </section>
              </main>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 2),
            TestContext.Current.CancellationToken);

        Assert.Collection(
            report.Pages,
            pageOne =>
            {
                Assert.Equal(2, pageOne.HtmlWordCount);
                Assert.Equal(1d, pageOne.TextTokenCoverage);
                Assert.Equal(0, pageOne.HtmlImages);
                Assert.Equal(0, pageOne.HtmlVectorPaths);
                Assert.InRange(pageOne.Visual?.HtmlHeight ?? 0, 99, 101);
            },
            pageTwo =>
            {
                Assert.Equal(2, pageTwo.HtmlWordCount);
                Assert.Equal(1, pageTwo.HtmlImages);
                Assert.Equal(1, pageTwo.HtmlVectorPaths);
            });
    }

    [Fact]
    public async Task AnalyzeAsync_FlagsImplausiblyTallContinuousPageWithoutFollowingBoundary()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body style="margin:0">
              <main class="pdf-semantic-document-flow" style="width:816px;height:5000px">
                <div class="pdf-semantic-page-break pdf-semantic-page-start" data-page-number="1"></div>
                <p class="pdf-semantic-element">Review artifact sample</p>
              </main>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        PdfHtmlQualityCheck dimensions = Assert.Single(
            report.Checks,
            check => check.Id == "page-dimensions");
        Assert.Equal("needs-review", dimensions.Status);
        Assert.Contains("implausibly tall", dimensions.Message, StringComparison.Ordinal);
        Assert.True(dimensions.Metrics["heightRatio"] > dimensions.Metrics["maximumSemanticHeightRatio"]);
    }

    [Fact]
    public async Task AnalyzeAsync_SkipsGeometryForEmptyContinuousPage()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source.pdf");
        PdfLayoutDocument layout;
        using (PDDocument document = new())
        {
            document.AddPage(new PDPage());
            document.Save(sourcePdf);
            layout = PdfLayoutExtractor.Extract(document);
        }

        string htmlDirectory = Path.Combine(tempDirectory.Path, "html");
        Directory.CreateDirectory(htmlDirectory);
        File.WriteAllText(
            Path.Combine(htmlDirectory, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <body>
              <main class="pdf-semantic-document-flow" style="width:816px">
                <div class="pdf-semantic-page-break pdf-semantic-page-start" data-page-number="1"></div>
              </main>
            </body>
            </html>
            """);

        PdfHtmlQualityReport report = await new PdfHtmlQualityProbe().AnalyzeAsync(new PdfHtmlQualityProbeOptions(
            sourcePdf,
            htmlDirectory,
            layout,
            Path.Combine(tempDirectory.Path, "quality"),
            MaxPages: 1),
            TestContext.Current.CancellationToken);

        Assert.Contains(report.Checks, check => check.Id == "page-dimensions" && check.Status == "skipped");
        Assert.DoesNotContain(report.Checks, check => check.Id == "page-dimensions" && check.Status == "needs-review");
    }

    [Fact]
    public async Task Generate_ComparisonPaneSplitterResizesInBrowser()
    {
        using TempDirectory tempDirectory = new();
        string sourcePdf = Path.Combine(tempDirectory.Path, "source-input.pdf");
        using (PDDocument document = CreateTextDocument())
        {
            document.Save(sourcePdf);
        }

        string manifestPath = Path.Combine(tempDirectory.Path, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schema = 1,
                    examples = new[]
                    {
                        new
                        {
                            id = "resizable-comparison",
                            sourcePdf
                        }
                    }
                }));

        string outputDirectory = Path.Combine(tempDirectory.Path, "html-examples");
        HtmlReviewArtifactGenerator.Generate(manifestPath, outputDirectory);
        string comparePath = Path.Combine(outputDirectory, "resizable-comparison", "compare.html");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1400,
                Height = 900
            }
        });
        await page.GotoAsync(new Uri(comparePath).AbsoluteUri);

        ILocator sourcePane = page.Locator("[data-comparison] > section").First;
        LocatorBoundingBoxResult before = await sourcePane.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Source comparison pane did not have a bounding box.");
        LocatorBoundingBoxResult splitter = await page.Locator("[data-splitter]").BoundingBoxAsync()
            ?? throw new InvalidOperationException("Comparison splitter did not have a bounding box.");

        await page.Mouse.MoveAsync(splitter.X + (splitter.Width / 2), splitter.Y + (splitter.Height / 2));
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(splitter.X + 220, splitter.Y + (splitter.Height / 2));
        await page.Mouse.UpAsync();

        LocatorBoundingBoxResult after = await sourcePane.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Resized source comparison pane did not have a bounding box.");
        Assert.True(after.Width > before.Width + 180, $"Expected source pane width to grow from {before.Width} to {after.Width}.");
        Assert.True(int.Parse(await page.Locator("[data-splitter]").GetAttributeAsync("aria-valuenow") ?? "0") > 50);
    }

    private static PDDocument CreateTextDocument(string text = "Review artifact sample")
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.RESOURCES, CreateDefaultResourcesDictionary());
        pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream($"""
            BT
            /F1 12 Tf
            72 700 Td
            ({text}) Tj
            ET
            """));
        return document;
    }

    private static PDDocument CreateBlueRectangleDocument(int pageCount, bool tinyLastPage = false)
    {
        PDDocument document = new();
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            PDPage page = new();
            document.AddPage(page);
            using PDPageContentStream content = new(document, page);
            content.SetNonStrokingColor(0f, 0f, 1f);
            bool tiny = tinyLastPage && pageIndex == pageCount - 1;
            content.AddRect(72.2f, tiny ? 714.8f : 647.8f, tiny ? 5 : 144, tiny ? 5 : 72);
            content.Fill();
        }

        return document;
    }

    private static PdfLayoutDocument CreateSyntheticLayout(params string[] pageTexts)
    {
        PdfLayoutRectangle pageBounds = new(0, 0, 612, 792);
        PdfLayoutColor black = new(0, 0, 0, 1, "DeviceRGB");
        List<PdfLayoutPage> pages = [];
        for (int index = 0; index < pageTexts.Length; index++)
        {
            string[] words = pageTexts[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            List<PdfTextGlyph> glyphs = [];
            float x = 72;
            foreach (string word in words)
            {
                float width = Math.Max(4, word.Length * 6);
                glyphs.Add(new PdfTextGlyph(word, "Helvetica", 12, 0, new PdfLayoutRectangle(x, 72, width, 12), black));
                x += width + 3;
            }

            PdfTextRun run = new(
                string.Concat(words),
                "Helvetica",
                12,
                0,
                new PdfLayoutRectangle(72, 72, x - 75, 12),
                black,
                glyphs);
            PdfTextLine line = new(string.Concat(words), run.Bounds, [run]);
            pages.Add(new PdfLayoutPage(
                index + 1,
                pageBounds,
                pageBounds,
                612,
                792,
                0,
                glyphs,
                [run],
                [line],
                [],
                [],
                [],
                [],
                [],
                [],
                []));
        }

        return new PdfLayoutDocument(pages, []);
    }

    private static COSStream CreateContentStream(string contentStream)
    {
        COSStream stream = new();
        using Stream output = stream.CreateOutputStream();
        byte[] bytes = Encoding.Latin1.GetBytes(contentStream);
        output.Write(bytes, 0, bytes.Length);
        return stream;
    }

    private static COSDictionary CreateDefaultResourcesDictionary()
    {
        COSDictionary fontDictionary = new();
        fontDictionary.SetItem(COSName.TYPE, COSName.GetPDFName("Font"));
        fontDictionary.SetItem(COSName.GetPDFName("Subtype"), COSName.GetPDFName("Type1"));
        fontDictionary.SetItem(COSName.GetPDFName("BaseFont"), COSName.GetPDFName("Helvetica"));

        COSDictionary fonts = new();
        fonts.SetItem(COSName.GetPDFName("F1"), fontDictionary);

        COSDictionary resources = new();
        resources.SetItem(COSName.GetPDFName("Font"), fonts);
        return resources;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfbox-net-html-review-" + Guid.NewGuid().ToString("N"));
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
