using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfBox.Net.Html;
using PdfBox.Net.ImageMagick;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.Rendering;

namespace PdfBox.Net.ConversionQuality;

public static class HtmlReviewArtifactGenerator
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static HtmlReviewArtifactResult Generate(string manifestPath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string fullManifestPath = Path.GetFullPath(manifestPath);
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new ArgumentException("Manifest path must include a directory.", nameof(manifestPath));

        HtmlReviewManifest manifest = LoadManifest(fullManifestPath);
        if (manifest.Schema != 1)
        {
            throw new InvalidOperationException($"Unsupported HTML review manifest schema {manifest.Schema}.");
        }

        if (manifest.Examples.Count == 0)
        {
            throw new InvalidOperationException("HTML review manifest must contain at least one example.");
        }

        SkiaRenderingBackend.Register();
        PdfBoxNetImageMagick.Register();
        RecreateDirectory(fullOutputDirectory);

        List<HtmlReviewExampleResult> results = [];
        foreach (HtmlReviewManifestExample example in manifest.Examples)
        {
            results.Add(GenerateExample(example, manifestDirectory, fullOutputDirectory));
        }

        WriteText(Path.Combine(fullOutputDirectory, "index.html"), RenderIndex(manifest, results));
        WriteText(
            Path.Combine(fullOutputDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);

        return new HtmlReviewArtifactResult(fullOutputDirectory, results);
    }

    private static HtmlReviewManifest LoadManifest(string manifestPath)
    {
        using FileStream stream = File.OpenRead(manifestPath);
        return JsonSerializer.Deserialize<HtmlReviewManifest>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"{manifestPath} did not contain a valid manifest.");
    }

    private static HtmlReviewExampleResult GenerateExample(
        HtmlReviewManifestExample example,
        string manifestDirectory,
        string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(example.Id))
        {
            throw new InvalidOperationException("HTML review example is missing id.");
        }

        if (string.IsNullOrWhiteSpace(example.SourcePdf))
        {
            throw new InvalidOperationException($"HTML review example '{example.Id}' is missing sourcePdf.");
        }

        string sourcePdf = ResolvePath(example.SourcePdf, manifestDirectory);
        if (!File.Exists(sourcePdf))
        {
            throw new FileNotFoundException($"HTML review source PDF was not found: {sourcePdf}", sourcePdf);
        }

        string directoryName = SafeDirectoryName(example.Id);
        string exampleDirectory = Path.Combine(outputDirectory, directoryName);
        RecreateDirectory(exampleDirectory);

        PdfLayoutDocument layout;
        PdfHtmlDocument html;
        PdfHtmlDocument semanticHtml;
        PdfHtmlDocument continuousSemanticHtml;
        string capturedConversionWarnings;
        TextWriter originalError = Console.Error;
        using (StringWriter conversionWarnings = new())
        {
            try
            {
                Console.SetError(conversionWarnings);
                using PDDocument document = Loader.LoadPDF(sourcePdf);
                layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
                {
                    IncludeImageAssets = true,
                    IncludeFontAssets = true,
                    IncludeTransparencyGroupFallbacks = true
                });
                ValidateExpectations(example, layout);
                html = PdfHtmlConverter.Convert(layout);
                semanticHtml = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
                {
                    CssPath = "assets/pdfbox-net-semantic.css",
                    TextMode = PdfHtmlTextMode.Semantic
                });
                continuousSemanticHtml = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
                {
                    CssPath = "assets/pdfbox-net-semantic-continuous.css",
                    TextMode = PdfHtmlTextMode.Semantic,
                    SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
                });
                ValidateSemanticExpectations(example, continuousSemanticHtml);
            }
            finally
            {
                Console.SetError(originalError);
            }

            capturedConversionWarnings = conversionWarnings.ToString();
        }

        html.WriteToDirectory(exampleDirectory);
        semanticHtml.WriteToDirectory(Path.Combine(exampleDirectory, "semantic"));
        continuousSemanticHtml.WriteToDirectory(Path.Combine(exampleDirectory, "semantic-continuous"));
        string copiedSourcePdf = Path.Combine(exampleDirectory, "source.pdf");
        File.Copy(sourcePdf, copiedSourcePdf, overwrite: true);
        string continuousSemanticDirectory = Path.Combine(exampleDirectory, "semantic-continuous");
        PdfHtmlQualityReport qualityReport = new PdfHtmlQualityProbe()
            .AnalyzeAsync(new PdfHtmlQualityProbeOptions(
                copiedSourcePdf,
                continuousSemanticDirectory,
                layout,
                Path.Combine(exampleDirectory, "quality"),
                example.QualityPages ?? 2,
                example.Notes))
            .GetAwaiter()
            .GetResult();
        ValidateQualityExpectations(example, qualityReport);

        HtmlReviewExampleResult result = new(
            example.Id,
            example.Title ?? example.Id,
            example.Notes ?? "",
            exampleDirectory,
            PageCount: layout.Pages.Count,
            TextRuns: layout.Pages.Sum(page => page.Runs.Count),
            TextLines: layout.Pages.Sum(page => page.Lines.Count),
            ImagePlacements: layout.Pages.Sum(page => page.Images.Count),
            VectorPaths: layout.Pages.Sum(page => page.Paths.Count),
            ExportedAssets: html.Assets.Count,
            Links: layout.Pages.Sum(page => page.Links.Count),
            Diagnostics: layout.Diagnostics.Count + layout.Pages.Sum(page => page.Diagnostics.Count) +
                CountNonEmptyLines(capturedConversionWarnings),
            QualityStatus: qualityReport.Status,
            QualityChecksNeedingReview: qualityReport.Checks.Count(static check => check.Status == "needs-review"),
            QualityArtifacts: qualityReport.Artifacts);

        WriteText(Path.Combine(exampleDirectory, "summary.md"), RenderExampleSummary(result, layout));
        WriteText(Path.Combine(exampleDirectory, "diagnostics.txt"), RenderDiagnostics(layout, capturedConversionWarnings));
        WriteText(Path.Combine(exampleDirectory, "compare.html"), RenderComparePage(result));
        return result;
    }

    private static string ResolvePath(string value, string baseDirectory)
    {
        if (Path.IsPathRooted(value))
        {
            return Path.GetFullPath(value);
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, value));
    }

    internal static void ValidateExpectations(HtmlReviewManifestExample example, PdfLayoutDocument layout)
    {
        HtmlReviewExpectations? expectations = example.Expectations;
        if (expectations is null)
        {
            return;
        }

        int textRuns = layout.Pages.Sum(page => page.Runs.Count);
        int imagePlacements = layout.Pages.Sum(page => page.Images.Count);
        int vectorPaths = layout.Pages.Sum(page => page.Paths.Count);
        int links = layout.Pages.Sum(page => page.Links.Count);
        int formControls = layout.Pages.Sum(page => page.FormControls.Count);
        List<string> failures = [];

        AddExactFailure(failures, "pages", layout.Pages.Count, expectations.PageCount);
        AddMinimumFailure(failures, "text runs", textRuns, expectations.MinTextRuns);
        AddMinimumFailure(failures, "image placements", imagePlacements, expectations.MinImagePlacements);
        AddMinimumFailure(failures, "vector paths", vectorPaths, expectations.MinVectorPaths);
        AddMinimumFailure(failures, "links", links, expectations.MinLinks);
        AddMinimumFailure(failures, "form controls", formControls, expectations.MinFormControls);
        foreach ((int pageNumber, int minimum) in expectations.MinImagePlacementsByPage)
        {
            PdfLayoutPage? page = layout.Pages.FirstOrDefault(page => page.PageNumber == pageNumber);
            int actual = page?.Images.Count ?? 0;
            AddMinimumFailure(failures, $"image placements on page {pageNumber}", actual, minimum);
        }

        string extractedText = NormalizeAlphaNumeric(string.Concat(
            layout.Pages.SelectMany(page => page.Runs).Select(run => run.Text)));
        if (expectations.RequiredText is null)
        {
            throw new InvalidOperationException(
                $"HTML review example '{example.Id}' requiredText expectation must be an array.");
        }

        foreach (string requiredText in expectations.RequiredText)
        {
            if (string.IsNullOrWhiteSpace(requiredText))
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an empty requiredText expectation.");
            }

            string normalizedRequiredText = NormalizeAlphaNumeric(requiredText);
            if (normalizedRequiredText.Length == 0 ||
                !extractedText.Contains(normalizedRequiredText, StringComparison.Ordinal))
            {
                failures.Add($"required text '{requiredText}' was not extracted");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"HTML review expectations failed for '{example.Id}': {string.Join("; ", failures)}.");
        }
    }

    internal static void ValidateSemanticExpectations(
        HtmlReviewManifestExample example,
        PdfHtmlDocument continuousSemanticHtml)
    {
        Dictionary<int, List<int>> expectedOrderedByPage =
            example.Expectations?.SemanticOrderedListItemCountsByPage ?? [];
        Dictionary<int, List<int>> expectedUnorderedByPage =
            example.Expectations?.SemanticUnorderedListItemCountsByPage ?? [];
        Dictionary<int, int> expectedMixedRegionsByPage =
            example.Expectations?.SemanticMixedRegionCountsByPage ?? [];
        Dictionary<int, int> expectedColumnsByPage =
            example.Expectations?.SemanticColumnCountsByPage ?? [];
        Dictionary<int, int> expectedRuledGridColumnsByPage =
            example.Expectations?.SemanticRuledGridColumnCountsByPage ?? [];
        Dictionary<int, int> expectedRuledGridSourceBordersByPage =
            example.Expectations?.SemanticRuledGridSourceBorderCountsByPage ?? [];
        Dictionary<int, int> expectedHeadingCountsByPage =
            example.Expectations?.SemanticHeadingCountsByPage ?? [];
        Dictionary<int, int> expectedTableCountsByPage =
            example.Expectations?.SemanticTableCountsByPage ?? [];
        List<string> expectedHeadingOutline =
            example.Expectations?.SemanticHeadingOutline ?? [];
        List<int> expectedFixedLayoutPages =
            example.Expectations?.SemanticFixedLayoutPageNumbers ?? [];
        if (expectedOrderedByPage.Count == 0 &&
            expectedUnorderedByPage.Count == 0 &&
            expectedMixedRegionsByPage.Count == 0 &&
            expectedColumnsByPage.Count == 0 &&
            expectedRuledGridColumnsByPage.Count == 0 &&
            expectedRuledGridSourceBordersByPage.Count == 0 &&
            expectedHeadingCountsByPage.Count == 0 &&
            expectedTableCountsByPage.Count == 0 &&
            expectedHeadingOutline.Count == 0 &&
            expectedFixedLayoutPages.Count == 0)
        {
            return;
        }

        string xml = Regex.Replace(
            continuousSemanticHtml.Html,
            "<!doctype html>\\s*",
            "",
            RegexOptions.IgnoreCase);
        XDocument dom = XDocument.Parse(xml);
        List<string> failures = [];
        ValidateSemanticListExpectations(
            example,
            dom,
            expectedOrderedByPage,
            "ol",
            "ordered",
            failures);
        ValidateSemanticListExpectations(
            example,
            dom,
            expectedUnorderedByPage,
            "ul",
            "unordered",
            failures);
        ValidateSemanticMixedRegionExpectations(
            example,
            dom,
            expectedMixedRegionsByPage,
            failures);
        ValidateSemanticColumnExpectations(
            example,
            dom,
            expectedColumnsByPage,
            failures);
        ValidateSemanticRuledGridExpectations(
            example,
            dom,
            expectedRuledGridColumnsByPage,
            failures);
        ValidateSemanticRuledGridSourceBorderExpectations(
            example,
            dom,
            expectedRuledGridSourceBordersByPage,
            failures);
        ValidateSemanticElementCountExpectations(
            example,
            dom,
            expectedHeadingCountsByPage,
            "heading",
            static element => element.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6",
            failures);
        ValidateSemanticElementCountExpectations(
            example,
            dom,
            expectedTableCountsByPage,
            "table",
            static element => element.Name.LocalName == "table",
            failures);
        ValidateSemanticHeadingOutline(
            dom,
            expectedHeadingOutline,
            failures);
        ValidateSemanticFixedLayoutPageExpectations(
            example,
            dom,
            expectedFixedLayoutPages,
            failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"HTML review semantic expectations failed for '{example.Id}': {string.Join("; ", failures)}.");
        }
    }

    private static void ValidateSemanticHeadingOutline(
        XDocument dom,
        IReadOnlyList<string> expectedOutline,
        List<string> failures)
    {
        if (expectedOutline.Count == 0)
        {
            return;
        }

        XElement[] headings = dom
            .Descendants()
            .Where(static element => HasClass(element, "pdf-semantic-heading"))
            .Where(static element => element.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .ToArray();
        string[] actualOutline = headings
            .Select(static heading =>
                heading.Name.LocalName + "|" + NormalizeWhitespace(string.Join(
                    ' ',
                    heading
                        .DescendantNodes()
                        .OfType<XText>()
                        .Select(static text => text.Value))))
            .ToArray();
        if (!actualOutline.SequenceEqual(expectedOutline, StringComparer.Ordinal))
        {
            failures.Add(
                $"semantic heading outline was [{string.Join("; ", actualOutline)}], " +
                $"expected [{string.Join("; ", expectedOutline)}]");
        }

        string[] headingIds = headings
            .Select(static heading => heading.Attribute("id")?.Value ?? "")
            .ToArray();
        if (headingIds.Any(string.IsNullOrWhiteSpace) ||
            headingIds.Distinct(StringComparer.Ordinal).Count() != headingIds.Length)
        {
            failures.Add("semantic heading ids were missing or duplicated");
        }

        HashSet<string> headingIdSet = headingIds.ToHashSet(StringComparer.Ordinal);
        string[] danglingSectionLabels = dom
            .Descendants()
            .Where(static element => HasClass(element, "pdf-semantic-section"))
            .Select(static section => section.Attribute("aria-labelledby")?.Value ?? "")
            .Where(label => !headingIdSet.Contains(label))
            .ToArray();
        if (danglingSectionLabels.Length > 0)
        {
            failures.Add(
                $"semantic sections referenced missing headings [{string.Join(", ", danglingSectionLabels)}]");
        }
    }

    internal static void ValidateQualityExpectations(
        HtmlReviewManifestExample example,
        PdfHtmlQualityReport qualityReport)
    {
        Dictionary<int, double> maximumPdfMissRatioByPage =
            example.Expectations?.MaxPdfMissRatioByPage ?? [];
        Dictionary<int, double> maximumSevereColorDeltaRatioByPage =
            example.Expectations?.MaxSevereColorDeltaRatioByPage ?? [];
        Dictionary<int, double> maximumHtmlHeightRatioByPage =
            example.Expectations?.MaxHtmlHeightRatioByPage ?? [];
        if (maximumPdfMissRatioByPage.Count == 0 &&
            maximumSevereColorDeltaRatioByPage.Count == 0 &&
            maximumHtmlHeightRatioByPage.Count == 0)
        {
            return;
        }

        List<string> failures = [];
        ValidateMaximumVisualMetric(
            example,
            qualityReport,
            maximumPdfMissRatioByPage,
            "PDF foreground miss ratio",
            static visual => visual.PdfMissRatio,
            failures);
        ValidateMaximumVisualMetric(
            example,
            qualityReport,
            maximumSevereColorDeltaRatioByPage,
            "severe color delta ratio",
            static visual => visual.SevereColorDeltaRatio,
            failures);
        ValidateMaximumQualityCheckMetric(
            example,
            qualityReport,
            maximumHtmlHeightRatioByPage,
            "HTML height ratio",
            "page-dimensions",
            "heightRatio",
            failures);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"HTML review visual expectations failed for '{example.Id}': {string.Join("; ", failures)}.");
        }
    }

    private static void ValidateMaximumQualityCheckMetric(
        HtmlReviewManifestExample example,
        PdfHtmlQualityReport qualityReport,
        IReadOnlyDictionary<int, double> maximumByPage,
        string metricName,
        string checkId,
        string metricId,
        List<string> failures)
    {
        foreach ((int pageNumber, double maximum) in maximumByPage)
        {
            if (pageNumber < 1 || !double.IsFinite(maximum) || maximum is < 0 or > 10)
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid maximum {metricName} expectation for page {pageNumber}.");
            }

            PdfHtmlQualityCheck? check = qualityReport.Pages
                .FirstOrDefault(page => page.PageNumber == pageNumber)
                ?.Checks
                .FirstOrDefault(check => check.Id == checkId);
            double? actual = check?.Metrics.GetValueOrDefault(metricId);
            if (actual is null)
            {
                failures.Add($"{metricName} on page {pageNumber} was unavailable");
            }
            else if (actual.Value > maximum)
            {
                failures.Add(
                    $"{metricName} on page {pageNumber} was {actual.Value.ToString("0.####", CultureInfo.InvariantCulture)}, " +
                    $"expected at most {maximum.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private static void ValidateMaximumVisualMetric(
        HtmlReviewManifestExample example,
        PdfHtmlQualityReport qualityReport,
        IReadOnlyDictionary<int, double> maximumByPage,
        string metricName,
        Func<PdfHtmlVisualMetrics, double?> selector,
        List<string> failures)
    {
        foreach ((int pageNumber, double maximum) in maximumByPage)
        {
            if (pageNumber < 1 || !double.IsFinite(maximum) || maximum is < 0 or > 1)
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid maximum {metricName} expectation for page {pageNumber}.");
            }

            PdfHtmlVisualMetrics? visual = qualityReport.Pages
                .FirstOrDefault(page => page.PageNumber == pageNumber)
                ?.Visual;
            double? actual = visual is null ? null : selector(visual);
            if (actual is null)
            {
                failures.Add($"{metricName} on page {pageNumber} was unavailable");
            }
            else if (actual.Value > maximum)
            {
                failures.Add(
                    $"{metricName} on page {pageNumber} was {actual.Value.ToString("0.####", CultureInfo.InvariantCulture)}, " +
                    $"expected at most {maximum.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private static void ValidateSemanticColumnExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyDictionary<int, int> expectedByPage,
        List<string> failures)
    {
        foreach ((int pageNumber, int expectedCount) in expectedByPage)
        {
            if (pageNumber < 1 || expectedCount < 2)
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid semantic column expectation for page {pageNumber}.");
            }

            int[] actualCounts = dom.Descendants()
                .Where(element =>
                    HasClass(element, "pdf-semantic-columns") &&
                    element.Attribute("data-source-page")?.Value ==
                        pageNumber.ToString(CultureInfo.InvariantCulture))
                .Select(columns => columns.Elements()
                    .Count(static element => HasClass(element, "pdf-semantic-column")))
                .ToArray();
            if (!actualCounts.SequenceEqual([expectedCount]))
            {
                failures.Add(
                    $"semantic column counts on page {pageNumber} were " +
                    $"[{string.Join(", ", actualCounts)}], expected [{expectedCount}]");
            }
        }
    }

    private static bool HasClass(XElement element, string className)
    {
        return element.Attribute("class")?.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.Ordinal) ?? false;
    }

    private static void ValidateSemanticElementCountExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyDictionary<int, int> expectedByPage,
        string elementKind,
        Func<XElement, bool> predicate,
        List<string> failures)
    {
        if (expectedByPage.Count == 0)
        {
            return;
        }

        if (expectedByPage.Any(static expectation =>
                expectation.Key < 1 || expectation.Value < 0))
        {
            throw new InvalidOperationException(
                $"HTML review example '{example.Id}' has invalid semantic {elementKind} count expectations.");
        }

        Dictionary<int, int> actualByPage = [];
        int currentPage = 0;
        foreach (XElement element in dom.Descendants())
        {
            if ((HasClass(element, "pdf-semantic-page-break") ||
                    HasClass(element, "pdf-semantic-layout-fallback-page")) &&
                int.TryParse(
                    element.Attribute("data-page-number")?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int pageNumber))
            {
                currentPage = pageNumber;
            }

            if (currentPage > 0 && predicate(element))
            {
                actualByPage[currentPage] = actualByPage.GetValueOrDefault(currentPage) + 1;
            }
        }

        foreach ((int pageNumber, int expectedCount) in expectedByPage)
        {
            int actual = actualByPage.GetValueOrDefault(pageNumber);
            if (actual != expectedCount)
            {
                failures.Add(
                    $"semantic {elementKind} count on page {pageNumber} was {actual}, expected {expectedCount}");
            }
        }
    }

    private static void ValidateSemanticFixedLayoutPageExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyList<int> expectedPageNumbers,
        List<string> failures)
    {
        if (expectedPageNumbers.Count == 0)
        {
            return;
        }

        int[] expected = expectedPageNumbers.Order().ToArray();
        if (expected.Any(static pageNumber => pageNumber < 1) ||
            expected.Distinct().Count() != expected.Length)
        {
            throw new InvalidOperationException(
                $"HTML review example '{example.Id}' has invalid semantic fixed-layout page expectations.");
        }

        int[] actual = dom.Descendants()
            .Where(static element =>
                element.Attribute("class")?.Value.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries)
                    .Contains("pdf-semantic-layout-fallback-page", StringComparer.Ordinal) ?? false)
            .Select(static element => int.TryParse(
                element.Attribute("data-page-number")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int pageNumber)
                    ? pageNumber
                    : 0)
            .Order()
            .ToArray();
        if (!actual.SequenceEqual(expected))
        {
            failures.Add(
                $"semantic fixed-layout pages were [{string.Join(", ", actual)}], " +
                $"expected [{string.Join(", ", expected)}]");
        }
    }

    private static void ValidateSemanticRuledGridSourceBorderExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyDictionary<int, int> expectedByPage,
        List<string> failures)
    {
        foreach ((int pageNumber, int expectedCount) in expectedByPage)
        {
            if (pageNumber < 1 || expectedCount < 1)
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid semantic ruled-grid source-border expectation for page {pageNumber}.");
            }

            int[] actualCounts = dom.Descendants()
                .Where(element =>
                    element.Attribute("data-layout")?.Value == "ruled-grid" &&
                    element.Parent?.Attribute("data-source-page")?.Value ==
                        pageNumber.ToString(CultureInfo.InvariantCulture))
                .Select(grid => int.TryParse(
                    grid.Attribute("data-source-border-count")?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int count)
                        ? count
                        : 0)
                .ToArray();
            if (actualCounts.Length != 1 || actualCounts[0] != expectedCount)
            {
                failures.Add(
                    $"semantic ruled-grid source-border counts on page {pageNumber} were " +
                    $"[{string.Join(", ", actualCounts)}], expected [{expectedCount}]");
            }
        }
    }

    private static void ValidateSemanticRuledGridExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyDictionary<int, int> expectedByPage,
        List<string> failures)
    {
        foreach ((int pageNumber, int expectedColumnCount) in expectedByPage)
        {
            if (pageNumber < 1 || expectedColumnCount < 2)
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid semantic ruled-grid expectation for page {pageNumber}.");
            }

            XElement[] grids = dom.Descendants()
                .Where(element =>
                    element.Attribute("data-layout")?.Value == "ruled-grid" &&
                    element.Parent?.Attribute("data-source-page")?.Value ==
                        pageNumber.ToString(CultureInfo.InvariantCulture))
                .ToArray();
            int[] actualColumnCounts = grids
                .Select(grid => int.TryParse(
                    grid.Attribute("data-column-count")?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int count)
                        ? count
                        : 0)
                .ToArray();
            if (actualColumnCounts.Length != 1 ||
                actualColumnCounts[0] != expectedColumnCount)
            {
                failures.Add(
                    $"semantic ruled-grid column counts on page {pageNumber} were " +
                    $"[{string.Join(", ", actualColumnCounts)}], expected [{expectedColumnCount}]");
            }
        }
    }

    private static void ValidateSemanticMixedRegionExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyDictionary<int, int> expectedByPage,
        List<string> failures)
    {
        foreach ((int pageNumber, int expectedCount) in expectedByPage)
        {
            if (pageNumber < 1 || expectedCount < 1)
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid semantic mixed-region expectation for page {pageNumber}.");
            }

            int actual = dom.Descendants()
                .Where(element =>
                    element.Attribute("data-source-page")?.Value == pageNumber.ToString() &&
                    (element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Contains("pdf-semantic-mixed-regions", StringComparer.Ordinal) ?? false))
                .Count(element =>
                {
                    XElement[] columns = element.Elements()
                        .Where(child => child.Attribute("class")?.Value.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries)
                            .Contains("pdf-semantic-column", StringComparer.Ordinal) ?? false)
                        .ToArray();
                    return columns.Length == 2 &&
                        columns.SelectMany(static column => column.DescendantsAndSelf())
                            .Any(descendant => descendant.Attribute("class")?.Value.Split(
                                    ' ',
                                    StringSplitOptions.RemoveEmptyEntries)
                                .Contains("pdf-semantic-mixed-region-figure", StringComparer.Ordinal) ?? false);
                });
            if (actual != expectedCount)
            {
                failures.Add(
                    $"semantic mixed-region count on page {pageNumber} was {actual}, expected {expectedCount}");
            }
        }
    }

    private static void ValidateSemanticListExpectations(
        HtmlReviewManifestExample example,
        XDocument dom,
        IReadOnlyDictionary<int, List<int>> expectedByPage,
        string tagName,
        string listKind,
        List<string> failures)
    {
        foreach ((int pageNumber, List<int> expectedItemCounts) in expectedByPage)
        {
            if (pageNumber < 1 || expectedItemCounts.Count == 0 || expectedItemCounts.Any(static count => count < 1))
            {
                throw new InvalidOperationException(
                    $"HTML review example '{example.Id}' has an invalid semantic {listKind}-list expectation for page {pageNumber}.");
            }

            XElement? page = dom.Descendants()
                .SingleOrDefault(element =>
                    element.Name.LocalName == "section" &&
                    element.Attribute("data-page-number")?.Value == pageNumber.ToString());
            IEnumerable<XElement> lists = page != null
                ? page.Descendants()
                : dom.Descendants()
                    .Where(element =>
                        element.Attribute("data-source-page")?.Value ==
                            pageNumber.ToString(CultureInfo.InvariantCulture) &&
                        element.Attribute("class")?.Value.Split(
                                ' ',
                                StringSplitOptions.RemoveEmptyEntries)
                            .Contains("pdf-semantic-ruled-grid-frame", StringComparer.Ordinal) == true)
                    .SelectMany(static frame => frame.Descendants());
            int[] actualItemCounts = lists
                .Where(element => element.Name.LocalName == tagName)
                .Select(static list => list.Elements().Count(static item => item.Name.LocalName == "li"))
                .ToArray();
            if (!actualItemCounts.SequenceEqual(expectedItemCounts))
            {
                failures.Add(
                    $"semantic {listKind}-list item counts on page {pageNumber} were " +
                    $"[{string.Join(", ", actualItemCounts)}], expected [{string.Join(", ", expectedItemCounts)}]");
            }
        }
    }

    private static void AddExactFailure(List<string> failures, string metric, int actual, int? expected)
    {
        if (expected is < 0)
        {
            throw new InvalidOperationException($"Expected {metric} cannot be negative.");
        }

        if (expected.HasValue && actual != expected.Value)
        {
            failures.Add($"{metric} was {actual}, expected {expected.Value}");
        }
    }

    private static void AddMinimumFailure(List<string> failures, string metric, int actual, int? minimum)
    {
        if (minimum is < 0)
        {
            throw new InvalidOperationException($"Minimum {metric} cannot be negative.");
        }

        if (minimum.HasValue && actual < minimum.Value)
        {
            failures.Add($"{metric} was {actual}, expected at least {minimum.Value}");
        }
    }

    private static string NormalizeAlphaNumeric(string value)
    {
        StringBuilder normalized = new(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                normalized.Append(Rune.ToLowerInvariant(rune));
            }
        }

        return normalized.ToString();
    }

    private static string SafeDirectoryName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            bool allowed = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or
                (>= '0' and <= '9') or '-' or '_';
            builder.Append(allowed ? character : '-');
        }

        string safe = builder.ToString().Trim('-');
        if (safe.Length == 0)
        {
            throw new InvalidOperationException($"HTML review example id '{value}' cannot be used as a directory name.");
        }

        return safe;
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8NoBom);
    }

    private static string RenderIndex(HtmlReviewManifest manifest, IReadOnlyList<HtmlReviewExampleResult> results)
    {
        StringBuilder html = new();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\" />");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.AppendLine("  <title>PDF conversion HTML review artifacts</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body{font-family:Arial,sans-serif;margin:24px;color:#111827;background:#f9fafb}");
        html.AppendLine("    table{border-collapse:collapse;width:100%;background:#fff;border:1px solid #d1d5db}");
        html.AppendLine("    th,td{border-bottom:1px solid #e5e7eb;padding:8px 10px;text-align:left;vertical-align:top}");
        html.AppendLine("    th{background:#f3f4f6;font-size:13px}");
        html.AppendLine("    a{color:#0f5ea8}");
        html.AppendLine("    .note{max-width:960px}");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("  <h1>PDF conversion HTML review artifacts</h1>");
        if (!string.IsNullOrWhiteSpace(manifest.Description))
        {
            html.Append("  <p class=\"note\">");
            html.Append(WebUtility.HtmlEncode(manifest.Description));
            html.AppendLine("</p>");
        }

        html.AppendLine("  <table>");
        html.AppendLine("    <thead><tr><th>Example</th><th>Links</th><th>Pages</th><th>Text runs</th><th>Images</th><th>Paths</th><th>Diagnostics</th><th>Quality</th></tr></thead>");
        html.AppendLine("    <tbody>");
        foreach (HtmlReviewExampleResult result in results)
        {
            string directoryName = SafeDirectoryName(result.Id);
            html.Append("      <tr><td>");
            html.Append(WebUtility.HtmlEncode(result.Title));
            html.Append("</td><td>");
            html.Append($"<a href=\"{directoryName}/compare.html\">compare</a> ");
            html.Append($"<a href=\"{directoryName}/source.pdf\">source PDF</a> ");
            html.Append($"<a href=\"{directoryName}/semantic-continuous/index.html\">continuous semantic HTML</a> ");
            html.Append($"<a href=\"{directoryName}/summary.md\">summary</a>");
            AppendQualityArtifactLinks(html, directoryName, result.QualityArtifacts);
            html.Append("</td><td>");
            html.Append(result.PageCount.ToString());
            html.Append("</td><td>");
            html.Append(result.TextRuns.ToString());
            html.Append("</td><td>");
            html.Append(result.ImagePlacements.ToString());
            html.Append("</td><td>");
            html.Append(result.VectorPaths.ToString());
            html.Append("</td><td>");
            html.Append(result.Diagnostics.ToString());
            html.Append("</td><td>");
            html.Append(WebUtility.HtmlEncode(result.QualityStatus));
            if (result.QualityChecksNeedingReview > 0)
            {
                html.Append(" (");
                html.Append(result.QualityChecksNeedingReview.ToString());
                html.Append(")");
            }

            html.Append($" <a href=\"{directoryName}/quality/quality-report.md\">report</a>");
            html.AppendLine("</td></tr>");
        }

        html.AppendLine("    </tbody>");
        html.AppendLine("  </table>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static string RenderExampleSummary(HtmlReviewExampleResult result, PdfLayoutDocument layout)
    {
        string preview = NormalizeWhitespace(layout.Text);
        if (preview.Length > 700)
        {
            preview = preview[..700] + "...";
        }

        StringBuilder summary = new();
        summary.AppendLine($"# {result.Title}");
        summary.AppendLine();
        if (result.Notes.Length > 0)
        {
            summary.AppendLine(result.Notes);
            summary.AppendLine();
        }

        summary.AppendLine("- Source PDF: [source.pdf](source.pdf)");
        summary.AppendLine("- Continuous semantic HTML: [semantic-continuous/index.html](semantic-continuous/index.html)");
        summary.AppendLine("- Side-by-side comparison: [compare.html](compare.html)");
        summary.AppendLine("- Quality probe: [quality/quality-report.md](quality/quality-report.md)");
        summary.AppendLine($"- Pages: {result.PageCount}");
        summary.AppendLine($"- Text runs: {result.TextRuns}");
        summary.AppendLine($"- Text lines: {result.TextLines}");
        summary.AppendLine($"- Image placements: {result.ImagePlacements}");
        summary.AppendLine($"- Vector paths: {result.VectorPaths}");
        summary.AppendLine($"- Exported assets: {result.ExportedAssets}");
        summary.AppendLine($"- Links: {result.Links}");
        summary.AppendLine($"- Diagnostics: {result.Diagnostics}");
        summary.AppendLine($"- Quality status: {result.QualityStatus}");
        summary.AppendLine($"- Quality checks needing review: {result.QualityChecksNeedingReview}");
        if (QualityArtifactLinks(result.QualityArtifacts).Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("## Quality Artifacts");
            summary.AppendLine();
            foreach ((string label, string artifact) in QualityArtifactLinks(result.QualityArtifacts))
            {
                summary.AppendLine($"- [{label}](quality/{artifact})");
            }
        }

        summary.AppendLine();
        summary.AppendLine("## Text Preview");
        summary.AppendLine();
        summary.AppendLine(preview.Length == 0 ? "_No extracted text._" : preview);
        return summary.ToString();
    }

    private static string RenderDiagnostics(PdfLayoutDocument layout, string capturedConversionWarnings)
    {
        List<PdfLayoutDiagnostic> diagnostics =
        [
            .. layout.Diagnostics,
            .. layout.Pages.SelectMany(page => page.Diagnostics)
        ];

        if (diagnostics.Count == 0 && CountNonEmptyLines(capturedConversionWarnings) == 0)
        {
            return "No diagnostics." + Environment.NewLine;
        }

        StringBuilder text = new();
        if (CountNonEmptyLines(capturedConversionWarnings) > 0)
        {
            text.AppendLine("Conversion warnings:");
            text.AppendLine(capturedConversionWarnings.TrimEnd());
            text.AppendLine();
        }

        foreach (PdfLayoutDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.PageNumber is int pageNumber)
            {
                text.Append($"page {pageNumber}: ");
            }

            text.Append(diagnostic.Severity);
            text.Append(' ');
            text.Append(diagnostic.Code);
            text.Append(": ");
            text.AppendLine(diagnostic.Message);
        }

        return text.ToString();
    }

    private static int CountNonEmptyLines(string value)
    {
        return value
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Count(line => line.Trim().Length > 0);
    }

    private static string RenderComparePage(HtmlReviewExampleResult result)
    {
        StringBuilder html = new();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\" />");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.Append("  <title>");
        html.Append(WebUtility.HtmlEncode(result.Title));
        html.AppendLine(" comparison</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    *{box-sizing:border-box}");
        html.AppendLine("    body{margin:0;font-family:Arial,sans-serif;color:#111827;background:#f3f4f6}");
        html.AppendLine("    header{height:48px;display:flex;align-items:center;gap:16px;padding:0 16px;background:#fff;border-bottom:1px solid #d1d5db}");
        html.AppendLine("    h1{font-size:16px;margin:0;flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}");
        html.AppendLine("    a{color:#0f5ea8}");
        html.AppendLine("    main{--left-pane-width:50%;display:grid;grid-template-columns:minmax(240px,var(--left-pane-width)) 10px minmax(240px,1fr);height:calc(100vh - 48px)}");
        html.AppendLine("    section{min-width:0;border-right:1px solid #d1d5db;display:grid;grid-template-rows:32px 1fr}");
        html.AppendLine("    section:first-child{border-right:0}");
        html.AppendLine("    h2{font-size:13px;margin:0;padding:8px 12px;background:#e5e7eb}");
        html.AppendLine("    iframe{border:0;width:100%;height:100%;background:#fff}");
        html.AppendLine("    .splitter{background:#d1d5db;cursor:col-resize;touch-action:none;position:relative;outline:none}");
        html.AppendLine("    .splitter::after{content:'';position:absolute;inset:0 -4px}");
        html.AppendLine("    .splitter:hover,.splitter:focus,.splitter.is-dragging{background:#0f5ea8}");
        html.AppendLine("    @media (max-width:900px){main{display:block;height:auto}section{height:80vh;border-right:0;border-bottom:1px solid #d1d5db}.splitter{display:none}}");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.Append("  <header><h1>");
        html.Append(WebUtility.HtmlEncode(result.Title));
        html.AppendLine("</h1><a href=\"source.pdf\">source PDF</a><a href=\"semantic-continuous/index.html\">continuous semantic HTML</a><a href=\"quality/quality-report.md\">quality report</a><a href=\"summary.md\">summary</a></header>");
        html.AppendLine("  <main data-comparison>");
        html.AppendLine("    <section><h2>Source PDF</h2><iframe title=\"Source PDF\" src=\"source.pdf\"></iframe></section>");
        html.AppendLine("    <div class=\"splitter\" data-splitter role=\"separator\" aria-orientation=\"vertical\" aria-label=\"Resize comparison panes\" aria-valuemin=\"20\" aria-valuemax=\"80\" aria-valuenow=\"50\" tabindex=\"0\"></div>");
        html.AppendLine("    <section><h2>Continuous Semantic HTML</h2><iframe title=\"Continuous Semantic HTML\" src=\"semantic-continuous/index.html\"></iframe></section>");
        html.AppendLine("  </main>");
        AppendComparisonResizeScript(html);
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static void AppendComparisonResizeScript(StringBuilder html)
    {
        html.AppendLine("  <script>");
        html.AppendLine("    (() => {");
        html.AppendLine("      const comparison = document.querySelector('[data-comparison]');");
        html.AppendLine("      const splitter = document.querySelector('[data-splitter]');");
        html.AppendLine("      if (!comparison || !splitter) return;");
        html.AppendLine("      const minimumPaneWidth = 240;");
        html.AppendLine("      let dragging = false;");
        html.AppendLine("      const setWidth = clientX => {");
        html.AppendLine("        const bounds = comparison.getBoundingClientRect();");
        html.AppendLine("        const width = Math.min(Math.max(clientX - bounds.left, minimumPaneWidth), bounds.width - minimumPaneWidth);");
        html.AppendLine("        comparison.style.setProperty('--left-pane-width', `${Math.round(width)}px`);");
        html.AppendLine("        splitter.setAttribute('aria-valuenow', Math.round((width / bounds.width) * 100).toString());");
        html.AppendLine("      };");
        html.AppendLine("      splitter.addEventListener('pointerdown', event => {");
        html.AppendLine("        dragging = true;");
        html.AppendLine("        splitter.classList.add('is-dragging');");
        html.AppendLine("        splitter.setPointerCapture(event.pointerId);");
        html.AppendLine("        setWidth(event.clientX);");
        html.AppendLine("      });");
        html.AppendLine("      splitter.addEventListener('pointermove', event => { if (dragging) setWidth(event.clientX); });");
        html.AppendLine("      const stopDragging = () => { dragging = false; splitter.classList.remove('is-dragging'); };");
        html.AppendLine("      splitter.addEventListener('pointerup', stopDragging);");
        html.AppendLine("      splitter.addEventListener('pointercancel', stopDragging);");
        html.AppendLine("      splitter.addEventListener('keydown', event => {");
        html.AppendLine("        if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;");
        html.AppendLine("        event.preventDefault();");
        html.AppendLine("        const bounds = comparison.getBoundingClientRect();");
        html.AppendLine("        const current = Number.parseFloat(getComputedStyle(comparison).getPropertyValue('--left-pane-width')) || bounds.width / 2;");
        html.AppendLine("        setWidth(bounds.left + current + (event.key === 'ArrowLeft' ? -32 : 32));");
        html.AppendLine("      });");
        html.AppendLine("    })();");
        html.AppendLine("  </script>");
    }

    private static void AppendQualityArtifactLinks(
        StringBuilder html,
        string directoryName,
        IReadOnlyList<string> qualityArtifacts)
    {
        foreach ((string label, string artifact) in QualityArtifactLinks(qualityArtifacts))
        {
            html.Append(' ');
            html.Append("<a href=\"");
            html.Append(directoryName);
            html.Append("/quality/");
            html.Append(WebUtility.HtmlEncode(artifact));
            html.Append("\">");
            html.Append(WebUtility.HtmlEncode(label));
            html.Append("</a>");
        }
    }

    private static IReadOnlyList<(string Label, string Artifact)> QualityArtifactLinks(
        IReadOnlyList<string> qualityArtifacts)
    {
        List<(string Label, string Artifact)> links = [];
        foreach (string artifact in qualityArtifacts)
        {
            if (artifact.EndsWith("-color-heatmap.png", StringComparison.Ordinal))
            {
                links.Add((PageArtifactLabel("color heatmap", artifact), artifact));
            }
            else if (artifact.EndsWith("-diff.png", StringComparison.Ordinal))
            {
                links.Add((PageArtifactLabel("diff", artifact), artifact));
            }
            else if (artifact.EndsWith("-visual-report.html", StringComparison.Ordinal))
            {
                links.Add((PageArtifactLabel("visual report", artifact), artifact));
            }
        }

        return links;
    }

    private static string PageArtifactLabel(string prefix, string artifact)
    {
        Match match = Regex.Match(artifact, @"page-(\d+)-", RegexOptions.CultureInvariant);
        return match.Success ? $"{prefix} p{match.Groups[1].Value}" : prefix;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed record HtmlReviewArtifactResult(
    string OutputDirectory,
    IReadOnlyList<HtmlReviewExampleResult> Examples);

public sealed record HtmlReviewExampleResult(
    string Id,
    string Title,
    string Notes,
    string Directory,
    int PageCount,
    int TextRuns,
    int TextLines,
    int ImagePlacements,
    int VectorPaths,
    int ExportedAssets,
    int Links,
    int Diagnostics,
    string QualityStatus,
    int QualityChecksNeedingReview,
    IReadOnlyList<string> QualityArtifacts);

public sealed class HtmlReviewManifest
{
    public int Schema { get; set; }

    public string? Description { get; set; }

    public List<HtmlReviewManifestExample> Examples { get; set; } = [];
}

public sealed class HtmlReviewManifestExample
{
    public string Id { get; set; } = "";

    public string? Title { get; set; }

    public string SourcePdf { get; set; } = "";

    public string? Notes { get; set; }

    public int? QualityPages { get; set; }

    public HtmlReviewExpectations? Expectations { get; set; }
}

public sealed class HtmlReviewExpectations
{
    public int? PageCount { get; set; }

    public List<string> RequiredText { get; set; } = [];

    public int? MinTextRuns { get; set; }

    public int? MinImagePlacements { get; set; }

    public Dictionary<int, int> MinImagePlacementsByPage { get; set; } = [];

    public int? MinVectorPaths { get; set; }

    public int? MinLinks { get; set; }

    public int? MinFormControls { get; set; }

    public Dictionary<int, List<int>> SemanticOrderedListItemCountsByPage { get; set; } = [];

    public Dictionary<int, List<int>> SemanticUnorderedListItemCountsByPage { get; set; } = [];

    public Dictionary<int, int> SemanticMixedRegionCountsByPage { get; set; } = [];

    public Dictionary<int, int> SemanticColumnCountsByPage { get; set; } = [];

    public Dictionary<int, int> SemanticRuledGridColumnCountsByPage { get; set; } = [];

    public Dictionary<int, int> SemanticRuledGridSourceBorderCountsByPage { get; set; } = [];

    public Dictionary<int, int> SemanticHeadingCountsByPage { get; set; } = [];

    public Dictionary<int, int> SemanticTableCountsByPage { get; set; } = [];

    public List<string> SemanticHeadingOutline { get; set; } = [];

    public List<int> SemanticFixedLayoutPageNumbers { get; set; } = [];

    public Dictionary<int, double> MaxPdfMissRatioByPage { get; set; } = [];

    public Dictionary<int, double> MaxSevereColorDeltaRatioByPage { get; set; } = [];

    public Dictionary<int, double> MaxHtmlHeightRatioByPage { get; set; } = [];
}
