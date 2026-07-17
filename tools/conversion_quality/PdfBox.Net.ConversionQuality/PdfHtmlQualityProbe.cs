using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.Rendering;

namespace PdfBox.Net.ConversionQuality;

public sealed class PdfHtmlQualityProbe
{
    private const string Passed = "passed";
    private const string NeedsReview = "needs-review";
    private const string Skipped = "skipped";
    private const float CssPixelsPerPoint = 96f / 72f;
    private const int ForegroundLuminanceThreshold = 245;
    private const int ForegroundDilationRadius = 3;
    private const int ForegroundDimensionTolerancePx = 2;
    private const double ForegroundDeltaReviewThreshold = 0.15;
    private const double PdfMissReviewThreshold = 0.10;
    private const double BrowserMissReviewThreshold = 0.10;
    private const int ColorInteriorErosionRadius = 2;
    private const double SevereColorDeltaThreshold = 20;
    private const double SevereColorDeltaRatioReviewThreshold = 0.05;
    private const double MinimumColorComparedPixelRatio = 0.005;
    private const double MaximumSemanticPageHeightRatio = 4;

    private static readonly string[] FixtureExpectationSignals =
    [
        "should look",
        "should match",
        "should be",
        "should not",
        "must be",
        "expected result",
        "not rendered correctly",
        "no x must be visible"
    ];

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<PdfHtmlQualityReport> AnalyzeAsync(
        PdfHtmlQualityProbeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SourcePdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HtmlDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);

        string sourcePdfPath = Path.GetFullPath(options.SourcePdfPath);
        string htmlDirectory = Path.GetFullPath(options.HtmlDirectory);
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        string htmlPath = Path.Combine(htmlDirectory, "index.html");
        int pageLimit = Math.Min(Math.Max(0, options.MaxPages), options.Layout.Pages.Count);

        RecreateDirectory(outputDirectory);

        List<PdfHtmlQualityCheck> checks = [];
        List<PdfHtmlQualityPageReport> pages = [];
        List<string> artifacts = [];
        TextReference textReference = await TextReference.CreateAsync(
            sourcePdfPath,
            options.Layout,
            pageLimit,
            cancellationToken);

        if (!File.Exists(htmlPath))
        {
            checks.Add(new PdfHtmlQualityCheck(
                "html-file",
                "setup",
                NeedsReview,
                null,
                $"Generated HTML was not found at {htmlPath}.",
                new Dictionary<string, double>()));
        }
        else
        {
            await AnalyzeBrowserPagesAsync(
                sourcePdfPath,
                htmlPath,
                options.Layout,
                textReference,
                pageLimit,
                outputDirectory,
                pages,
                checks,
                artifacts,
                cancellationToken);
        }

        IReadOnlyList<PdfHtmlQualityIssueCategory> issueCategories = BuildIssueCategories(pages, checks);
        IReadOnlyList<string> limitations = BuildCurrentLimitations(options.Notes, pages, checks);
        string status = CombinedStatus(checks);
        PdfHtmlQualityReport report = new(
            Schema: 1,
            Status: status,
            SourcePdf: RelativePath(outputDirectory, sourcePdfPath),
            Html: RelativePath(outputDirectory, htmlPath),
            Notes: options.Notes ?? "",
            TextReferenceSource: textReference.Source,
            PagesAnalyzed: pages.Count,
            IssueCategories: issueCategories,
            Limitations: limitations,
            Checks: checks,
            Pages: pages,
            Artifacts: artifacts);

        WriteText(
            Path.Combine(outputDirectory, "quality-report.json"),
            JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        WriteText(Path.Combine(outputDirectory, "quality-report.md"), RenderMarkdownReport(report));
        return report;
    }

    private static async Task AnalyzeBrowserPagesAsync(
        string sourcePdfPath,
        string htmlPath,
        PdfLayoutDocument layout,
        TextReference textReference,
        int pageLimit,
        string outputDirectory,
        List<PdfHtmlQualityPageReport> pages,
        List<PdfHtmlQualityCheck> checks,
        List<string> artifacts,
        CancellationToken cancellationToken)
    {
        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage browserPage = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = 1400,
                    Height = 1800
                }
            });
            await browserPage.GotoAsync(new Uri(htmlPath).AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            for (int pageIndex = 0; pageIndex < pageLimit; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AnalyzePageAsync(
                    sourcePdfPath,
                    layout.Pages[pageIndex],
                    textReference.PageTexts[pageIndex],
                    browserPage,
                    outputDirectory,
                    pages,
                    checks,
                    artifacts,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            checks.Add(new PdfHtmlQualityCheck(
                "browser-render",
                "setup",
                Skipped,
                null,
                $"Playwright browser analysis could not run: {ex.Message}",
                new Dictionary<string, double>()));
        }
    }

    private static async Task AnalyzePageAsync(
        string sourcePdfPath,
        PdfLayoutPage layoutPage,
        string referenceText,
        IPage browserPage,
        string outputDirectory,
        List<PdfHtmlQualityPageReport> pages,
        List<PdfHtmlQualityCheck> allChecks,
        List<string> allArtifacts,
        CancellationToken cancellationToken)
    {
        int pageNumber = layoutPage.PageNumber;
        List<PdfHtmlQualityCheck> pageChecks = [];
        List<string> pageArtifacts = [];
        BrowserPageCapture? capture = await CaptureBrowserPageAsync(browserPage, pageNumber);

        if (capture == null)
        {
            pageChecks.Add(new PdfHtmlQualityCheck(
                "page-rendered",
                "layout",
                NeedsReview,
                pageNumber,
                "No matching .pdf-page element was rendered.",
                new Dictionary<string, double>()));
            allChecks.AddRange(pageChecks);
            pages.Add(new PdfHtmlQualityPageReport(
                PageNumber: pageNumber,
                Status: CombinedStatus(pageChecks),
                SourceWordCount: 0,
                HtmlWordCount: 0,
                TextTokenCoverage: 0,
                LongSourceTokens: 0,
                LongHtmlTokens: 0,
                LayoutTextRuns: layoutPage.Runs.Count,
                HtmlTextRuns: 0,
                LayoutImages: layoutPage.Images.Count,
                HtmlImages: 0,
                LayoutVectorPaths: layoutPage.Paths.Count,
                HtmlVectorPaths: 0,
                TextImageOverlaps: 0,
                TextVectorOverlaps: 0,
                Visual: null,
                Checks: pageChecks,
                Artifacts: pageArtifacts));
            return;
        }

        BrowserPageSnapshot snapshot = capture.Snapshot;

        AddPageDimensionCheck(pageChecks, layoutPage, snapshot);
        AddTextRunCountCheck(pageChecks, layoutPage, snapshot, pageNumber);

        TextQualityMetrics textMetrics = TextQualityMetrics.Create(referenceText, snapshot.Text, snapshot.ProseText);
        pageChecks.Add(CreateWordBoundaryCheck(pageNumber, textMetrics));
        foreach (string expectation in ExtractFixtureExpectations(layoutPage))
        {
            pageChecks.Add(new PdfHtmlQualityCheck(
                "fixture-expectation",
                "fixture",
                Passed,
                pageNumber,
                expectation,
                new Dictionary<string, double>()));
        }

        BrowserBox[] overlapCandidateImages = snapshot.Images
            .Where(image => !IsPageBackground(image, snapshot))
            .ToArray();
        BrowserBox[] overlapCandidateVectorPaths = snapshot.VectorPaths
            .Where(path => path.Area >= 400 && !IsPageBackground(path, snapshot))
            .ToArray();
        int backgroundImageCount = snapshot.Images.Count - overlapCandidateImages.Length;
        int backgroundVectorPathCount = snapshot.VectorPaths.Count(path =>
            path.Area >= 400 && IsPageBackground(path, snapshot));
        int imageOverlaps = CountOverlaps(snapshot.TextRuns, overlapCandidateImages, minArea: 4, minTextFraction: 0.10);
        int vectorOverlaps = CountOverlaps(
            snapshot.TextRuns,
            overlapCandidateVectorPaths,
            minArea: 8,
            minTextFraction: 0.20);
        pageChecks.Add(new PdfHtmlQualityCheck(
            "text-image-overlap",
            "layout",
            imageOverlaps == 0 ? Passed : NeedsReview,
            pageNumber,
            imageOverlaps == 0
                ? "No text/image overlaps were found in the browser layout."
                : $"{imageOverlaps} text run(s) overlap rendered image boxes.",
            new Dictionary<string, double>
            {
                ["overlapCount"] = imageOverlaps,
                ["htmlImageCount"] = snapshot.Images.Count,
                ["layoutImageCount"] = layoutPage.Images.Count,
                ["backgroundImageCount"] = backgroundImageCount,
                ["overlapCandidateImageCount"] = overlapCandidateImages.Length
            }));
        pageChecks.Add(new PdfHtmlQualityCheck(
            "text-vector-overlap",
            "layout",
            vectorOverlaps == 0 ? Passed : NeedsReview,
            pageNumber,
            vectorOverlaps == 0
                ? "No text/large-vector overlaps were found in the browser layout."
                : $"{vectorOverlaps} text run(s) overlap large rendered vector boxes.",
            new Dictionary<string, double>
            {
                ["overlapCount"] = vectorOverlaps,
                ["htmlVectorPathCount"] = snapshot.VectorPaths.Count,
                ["layoutVectorPathCount"] = layoutPage.Paths.Count,
                ["backgroundVectorPathCount"] = backgroundVectorPathCount,
                ["overlapCandidateVectorPathCount"] = overlapCandidateVectorPaths.Length
            }));

        PdfHtmlVisualMetrics? visualMetrics = await AddVisualChecksAsync(
            sourcePdfPath,
            layoutPage,
            capture.Png,
            snapshot,
            outputDirectory,
            pageChecks,
            pageArtifacts,
            cancellationToken);

        allChecks.AddRange(pageChecks);
        allArtifacts.AddRange(pageArtifacts);
        AddPageReport();

        void AddPageReport()
        {
            pages.Add(new PdfHtmlQualityPageReport(
                PageNumber: pageNumber,
                Status: CombinedStatus(pageChecks),
                SourceWordCount: textMetrics.SourceWordCount,
                HtmlWordCount: textMetrics.HtmlWordCount,
                TextTokenCoverage: textMetrics.TokenCoverage,
                LongSourceTokens: textMetrics.LongSourceTokens,
                LongHtmlTokens: textMetrics.LongHtmlTokens,
                LayoutTextRuns: layoutPage.Runs.Count,
                HtmlTextRuns: snapshot.TextRuns.Count,
                LayoutImages: layoutPage.Images.Count,
                HtmlImages: snapshot.Images.Count,
                LayoutVectorPaths: layoutPage.Paths.Count,
                HtmlVectorPaths: snapshot.VectorPaths.Count,
                TextImageOverlaps: imageOverlaps,
                TextVectorOverlaps: vectorOverlaps,
                Visual: visualMetrics,
                Checks: pageChecks,
                Artifacts: pageArtifacts));
        }
    }

    private static async Task<BrowserPageCapture?> CaptureBrowserPageAsync(IPage browserPage, int pageNumber)
    {
        ILocator fixedPage = browserPage.Locator($".pdf-page[data-page-number='{pageNumber.ToString(CultureInfo.InvariantCulture)}']");
        if (await fixedPage.CountAsync() > 0)
        {
            string snapshotJson = await fixedPage.EvaluateAsync<string>(
                """
                root => {
                  const pageBox = root.getBoundingClientRect();
                  const nonProseSelector = "a,code,pre,math,.pdf-semantic-math,.pdf-semantic-formula";
                  const readableText = (element, proseOnly = false) => {
                    const copy = element.cloneNode(true);
                    copy.querySelectorAll(".pdf-text-run-svg").forEach(node => node.remove());
                    if (proseOnly) {
                      copy.querySelectorAll(nonProseSelector).forEach(node => node.remove());
                    }
                    return copy.textContent || "";
                  };
                  const readBox = element => {
                    const box = element.getBoundingClientRect();
                    return {
                      x: box.x - pageBox.x,
                      y: box.y - pageBox.y,
                      width: box.width,
                      height: box.height,
                      text: readableText(element)
                    };
                  };
                  return JSON.stringify({
                    width: pageBox.width,
                    height: pageBox.height,
                    text: readableText(root),
                    proseText: readableText(root, true),
                    textRuns: Array.from(root.querySelectorAll(".pdf-text-run")).map(readBox),
                    images: Array.from(root.querySelectorAll(".pdf-image")).map(readBox),
                    vectorPaths: Array.from(root.querySelectorAll("[data-path-index]")).map(readBox)
                  });
                }
                """) ?? "{}";
            BrowserPageSnapshot snapshot = JsonSerializer.Deserialize<BrowserPageSnapshot>(snapshotJson, JsonOptions)
                ?? new BrowserPageSnapshot();
            byte[] png = await fixedPage.ScreenshotAsync();
            return new BrowserPageCapture(snapshot, png);
        }

        string? continuousSnapshotJson = await browserPage.EvaluateAsync<string?>(
            """
            pageNumber => {
              const flow = document.querySelector(".pdf-semantic-document-flow");
              const marker = document.querySelector(`.pdf-semantic-page-break[data-page-number="${pageNumber}"]`);
              if (!flow || !marker) {
                return null;
              }

              const flowBox = flow.getBoundingClientRect();
              const markerBox = marker.getBoundingClientRect();
              const nodeFollows = (node, reference) =>
                Boolean(reference.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_FOLLOWING);
              const nodePrecedes = (node, reference) =>
                Boolean(reference.compareDocumentPosition(node) & Node.DOCUMENT_POSITION_PRECEDING);
              const nextBoundary = Array.from(document.querySelectorAll(
                ".pdf-semantic-page-break[data-page-number],.pdf-page[data-page-number]"))
                .find(element => element !== marker && nodeFollows(element, marker));
              const nextBox = nextBoundary ? nextBoundary.getBoundingClientRect() : null;
              const top = marker.classList.contains("pdf-semantic-page-start") ? flowBox.top : markerBox.bottom;
              const bottom = nextBox ? Math.min(nextBox.top, flowBox.bottom) : flowBox.bottom;
              const region = {
                x: flowBox.left,
                y: top,
                width: flowBox.width,
                height: Math.max(1, bottom - top)
              };
              region.right = region.x + region.width;
              region.bottom = region.y + region.height;

              const overlayId = `pdf-quality-page-region-${pageNumber}`;
              let overlay = document.getElementById(overlayId);
              if (!overlay) {
                overlay = document.createElement("div");
                overlay.id = overlayId;
                document.body.appendChild(overlay);
              }

              Object.assign(overlay.style, {
                position: "absolute",
                left: `${region.x + window.scrollX}px`,
                top: `${region.y + window.scrollY}px`,
                width: `${region.width}px`,
                height: `${region.height}px`,
                pointerEvents: "none",
                background: "transparent",
                zIndex: "2147483647"
              });

              const intersects = element => {
                const box = element.getBoundingClientRect();
                return box.width > 0 &&
                  box.height > 0 &&
                  box.right > region.x &&
                  box.left < region.right &&
                  box.bottom > region.y &&
                  box.top < region.bottom;
              };
              const nonProseSelector = "a,code,pre,math,.pdf-semantic-math,.pdf-semantic-formula";
              const readableText = (element, proseOnly = false) => {
                const copy = element.cloneNode(true);
                copy.querySelectorAll(".pdf-text-run-svg").forEach(node => node.remove());
                if (proseOnly) {
                  copy.querySelectorAll(nonProseSelector).forEach(node => node.remove());
                }
                return copy.textContent || "";
              };
              const readableRegionText = (element, proseOnly = false) => {
                const containsCurrentMarker = element.contains(marker);
                const containsNextBoundary = nextBoundary && element.contains(nextBoundary);
                if (!containsCurrentMarker && !containsNextBoundary) {
                  return readableText(element, proseOnly);
                }

                const text = [];
                const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                  const node = walker.currentNode;
                  const parent = node.parentElement;
                  if (parent && parent.closest(".pdf-text-run-svg")) {
                    continue;
                  }

                  if (proseOnly && parent && parent.closest(nonProseSelector)) {
                    continue;
                  }

                  if (containsCurrentMarker && !nodeFollows(node, marker)) {
                    continue;
                  }

                  if (containsNextBoundary && !nodePrecedes(node, nextBoundary)) {
                    continue;
                  }

                  text.push(node.nodeValue || "");
                }

                return text.join("");
              };
              const readBox = element => {
                const box = element.getBoundingClientRect();
                return {
                  x: box.x - region.x,
                  y: box.y - region.y,
                  width: box.width,
                  height: box.height,
                  text: readableRegionText(element)
                };
              };

              const textElements = Array.from(flow.querySelectorAll(
                ".pdf-text-run,.pdf-semantic-element,.pdf-semantic-figure,.pdf-semantic-line-grid-cell,.pdf-semantic-column-run"))
                .filter(element => !element.classList.contains("pdf-semantic-page-break") && intersects(element))
                .filter(element => readableRegionText(element).trim().length > 0);
              const imageElements = Array.from(flow.querySelectorAll(".pdf-image,.pdf-semantic-figure,img,svg image"))
                .filter(intersects);
              const vectorElements = Array.from(flow.querySelectorAll("[data-path-index]"))
                .filter(intersects);
              const usesSpatialTextGrid = Array.from(flow.querySelectorAll(
                ".pdf-semantic-line-grid,.pdf-semantic-columns"))
                .some(intersects);

              return JSON.stringify({
                width: region.width,
                height: region.height,
                text: textElements.map(readableRegionText).join("\n"),
                proseText: textElements.map(element => readableRegionText(element, true)).join("\n"),
                textRuns: textElements.map(readBox),
                images: imageElements.map(readBox),
                vectorPaths: vectorElements.map(readBox),
                isSemanticFlow: true,
                usesSpatialTextGrid
              });
            }
            """,
            pageNumber);
        if (string.IsNullOrWhiteSpace(continuousSnapshotJson))
        {
            return null;
        }

        BrowserPageSnapshot continuousSnapshot = JsonSerializer.Deserialize<BrowserPageSnapshot>(continuousSnapshotJson, JsonOptions)
            ?? new BrowserPageSnapshot();
        ILocator continuousRegion = browserPage.Locator($"#pdf-quality-page-region-{pageNumber.ToString(CultureInfo.InvariantCulture)}");
        byte[] continuousPng = await continuousRegion.ScreenshotAsync();
        return new BrowserPageCapture(continuousSnapshot, continuousPng);
    }

    private static void AddPageDimensionCheck(
        List<PdfHtmlQualityCheck> checks,
        PdfLayoutPage layoutPage,
        BrowserPageSnapshot snapshot)
    {
        bool sourceHasVisibleContent = layoutPage.Glyphs.Count > 0 || layoutPage.Images.Count > 0 || layoutPage.Paths.Count > 0;
        bool htmlHasVisibleContent = snapshot.TextRuns.Count > 0 || snapshot.Images.Count > 0 || snapshot.VectorPaths.Count > 0;
        if (!sourceHasVisibleContent && !htmlHasVisibleContent)
        {
            checks.Add(new PdfHtmlQualityCheck(
                "page-dimensions",
                "layout",
                Skipped,
                layoutPage.PageNumber,
                "The PDF and generated HTML page regions are both empty, so page geometry is not meaningful.",
                new Dictionary<string, double>()));
            return;
        }

        double expectedWidth = layoutPage.Width * CssPixelsPerPoint;
        double expectedHeight = layoutPage.Height * CssPixelsPerPoint;
        double widthDelta = Math.Abs(expectedWidth - snapshot.Width);
        double heightDelta = Math.Abs(expectedHeight - snapshot.Height);
        bool isReflowedSemanticPage = snapshot.IsSemanticFlow && !snapshot.UsesSpatialTextGrid;
        bool withinTolerance = widthDelta <= 1.0 && heightDelta <= 1.0;
        double heightRatio = expectedHeight > 0 ? snapshot.Height / expectedHeight : 0;
        bool plausibleSemanticHeight = heightRatio <= MaximumSemanticPageHeightRatio;
        string status = isReflowedSemanticPage
            ? (widthDelta <= 1.0 && plausibleSemanticHeight ? Passed : NeedsReview)
            : (withinTolerance ? Passed : NeedsReview);
        string message = isReflowedSemanticPage
            ? widthDelta > 1.0
                ? "Continuous semantic HTML width differs from the PDF page width."
                : !plausibleSemanticHeight
                    ? "Continuous semantic HTML region is implausibly tall; a following source-page boundary may be missing."
                    : "Continuous semantic HTML preserves page width; page height is intentionally reflowed between source-page boundaries."
            : withinTolerance
                ? "Browser page dimensions match the PDF page dimensions."
                : "Browser page dimensions differ from the PDF page dimensions.";
        checks.Add(new PdfHtmlQualityCheck(
            "page-dimensions",
            "layout",
            status,
            layoutPage.PageNumber,
            message,
            new Dictionary<string, double>
            {
                ["expectedWidthPx"] = expectedWidth,
                ["actualWidthPx"] = snapshot.Width,
                ["expectedHeightPx"] = expectedHeight,
                ["actualHeightPx"] = snapshot.Height,
                ["widthDeltaPx"] = widthDelta,
                ["heightDeltaPx"] = heightDelta,
                ["heightRatio"] = heightRatio,
                ["maximumSemanticHeightRatio"] = MaximumSemanticPageHeightRatio
            }));
    }

    private static void AddTextRunCountCheck(
        List<PdfHtmlQualityCheck> checks,
        PdfLayoutPage layoutPage,
        BrowserPageSnapshot snapshot,
        int pageNumber)
    {
        int expected = layoutPage.Runs.Count;
        int actual = snapshot.TextRuns.Count;
        bool semanticGrouping = snapshot.IsSemanticFlow && !snapshot.UsesSpatialTextGrid;
        checks.Add(new PdfHtmlQualityCheck(
            "text-run-count",
            "structure",
            semanticGrouping ? Skipped : (expected == actual ? Passed : NeedsReview),
            pageNumber,
            semanticGrouping
                ? "Continuous semantic HTML intentionally groups extracted text runs into paragraphs and other semantic elements."
                : expected == actual
                    ? "The browser DOM contains the same number of text runs as the extracted layout."
                    : $"The browser DOM contains {actual} text runs, but the extracted layout contains {expected}.",
            new Dictionary<string, double>
            {
                ["layoutTextRuns"] = expected,
                ["htmlTextRuns"] = actual
            }));
    }

    private static PdfHtmlQualityCheck CreateWordBoundaryCheck(int pageNumber, TextQualityMetrics metrics)
    {
        bool needsReview = false;
        if (metrics.SourceWordCount > 0)
        {
            double minimumCoverage = metrics.SourceWordCount < 10 ? 0.95 : 0.90;
            needsReview = metrics.TokenCoverage < minimumCoverage ||
                metrics.WordCountRatio < 0.85 ||
                metrics.WordCountRatio > 1.15 ||
                metrics.LongHtmlTokens > metrics.LongSourceTokens + 4 ||
                metrics.AdjacentSourceWordJoinCount > 0;
        }
        else if (metrics.HtmlWordCount > 0)
        {
            needsReview = true;
        }

        string message = needsReview
            ? "Rendered HTML text appears to have lost word boundaries or token coverage."
            : "Rendered HTML text preserves word boundaries at the current threshold.";
        return new PdfHtmlQualityCheck(
            "word-boundaries",
            "text",
            needsReview ? NeedsReview : Passed,
            pageNumber,
            message,
            new Dictionary<string, double>
            {
                ["sourceWordCount"] = metrics.SourceWordCount,
                ["htmlWordCount"] = metrics.HtmlWordCount,
                ["wordCountRatio"] = metrics.WordCountRatio,
                ["tokenCoverage"] = metrics.TokenCoverage,
                ["longSourceTokens"] = metrics.LongSourceTokens,
                ["longHtmlTokens"] = metrics.LongHtmlTokens,
                ["adjacentSourceWordJoinCount"] = metrics.AdjacentSourceWordJoinCount
            });
    }

    private static async Task<PdfHtmlVisualMetrics?> AddVisualChecksAsync(
        string sourcePdfPath,
        PdfLayoutPage layoutPage,
        byte[] browserPng,
        BrowserPageSnapshot snapshot,
        string outputDirectory,
        List<PdfHtmlQualityCheck> checks,
        List<string> artifacts,
        CancellationToken cancellationToken)
    {
        int pageNumber = layoutPage.PageNumber;
        string sourcePng = $"page-{pageNumber.ToString(CultureInfo.InvariantCulture)}-source.png";
        string htmlPng = $"page-{pageNumber.ToString(CultureInfo.InvariantCulture)}-html.png";
        string diffPng = $"page-{pageNumber.ToString(CultureInfo.InvariantCulture)}-diff.png";
        string colorHeatmapPng = $"page-{pageNumber.ToString(CultureInfo.InvariantCulture)}-color-heatmap.png";
        string visualReportHtml = $"page-{pageNumber.ToString(CultureInfo.InvariantCulture)}-visual-report.html";

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, htmlPng), browserPng, cancellationToken);
            artifacts.Add(htmlPng);

            using RenderedPdfPage pdfRender = await RenderPdfPageAsync(sourcePdfPath, pageNumber, cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, sourcePng), pdfRender.Png, cancellationToken);
            artifacts.Add(sourcePng);

            using BufferedImage browserImage = DecodePng(browserPng);
            double expectedWidth = layoutPage.Width * CssPixelsPerPoint;
            double expectedHeight = layoutPage.Height * CssPixelsPerPoint;
            bool sameSize = pdfRender.Image.Width == browserImage.Width && pdfRender.Image.Height == browserImage.Height;
            int widthDelta = Math.Abs(pdfRender.Image.Width - browserImage.Width);
            int heightDelta = Math.Abs(pdfRender.Image.Height - browserImage.Height);
            bool comparableSize = widthDelta <= ForegroundDimensionTolerancePx &&
                heightDelta <= ForegroundDimensionTolerancePx;
            bool pageSizeMatches = Math.Abs(expectedWidth - snapshot.Width) <= 1.0 &&
                Math.Abs(expectedHeight - snapshot.Height) <= 1.0 &&
                comparableSize;

            int comparisonWidth = Math.Min(pdfRender.Image.Width, browserImage.Width);
            int comparisonHeight = Math.Min(pdfRender.Image.Height, browserImage.Height);
            if (comparisonWidth <= 0 || comparisonHeight <= 0)
            {
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-foreground-mask",
                    "visual",
                    NeedsReview,
                    pageNumber,
                    "PDF render and browser page screenshot have no overlapping pixel region, so the foreground mask was not compared.",
                    new Dictionary<string, double>
                    {
                        ["pdfWidth"] = pdfRender.Image.Width,
                        ["pdfHeight"] = pdfRender.Image.Height,
                        ["htmlWidth"] = browserImage.Width,
                        ["htmlHeight"] = browserImage.Height,
                        ["widthDelta"] = widthDelta,
                        ["heightDelta"] = heightDelta
                    }));
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-color-difference",
                    "visual",
                    Skipped,
                    pageNumber,
                    "Color comparison did not run because the images have no overlapping pixel region.",
                    new Dictionary<string, double>()));
                WriteText(Path.Combine(outputDirectory, visualReportHtml), RenderVisualReport(pageNumber, [sourcePng, htmlPng], []));
                artifacts.Add(visualReportHtml);
                return new PdfHtmlVisualMetrics(
                    pdfRender.Image.Width,
                    pdfRender.Image.Height,
                    browserImage.Width,
                    browserImage.Height,
                    PdfRenderSource: pdfRender.Source,
                    PageSizeMatches: pageSizeMatches,
                    ForegroundDeltaRatio: null,
                    PdfMissRatio: null,
                    HtmlMissRatio: null,
                    MeanColorDelta: null,
                    SevereColorDeltaRatio: null,
                    ColorComparedPixelRatio: null);
            }

            ForegroundShapeStats? foregroundStats = ForegroundShapeStats.Create(
                pdfRender.Image,
                browserImage,
                comparisonWidth,
                comparisonHeight,
                ForegroundLuminanceThreshold,
                ForegroundDilationRadius);
            List<string> visualArtifacts = [sourcePng, htmlPng];
            if (foregroundStats == null)
            {
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-foreground-mask",
                    "visual",
                    Skipped,
                    pageNumber,
                    "Foreground mask comparison did not find any foreground pixels.",
                    new Dictionary<string, double>()));
            }
            else
            {
                using BufferedImage diff = ForegroundShapeStats.CreateDiffImage(
                    pdfRender.Image,
                    browserImage,
                    comparisonWidth,
                    comparisonHeight,
                    ForegroundLuminanceThreshold);
                await File.WriteAllBytesAsync(
                    Path.Combine(outputDirectory, diffPng),
                    RenderingBackend.Current.ImageCodec.Encode(diff, EncodedImageFormat.Png, 100),
                    cancellationToken);
                artifacts.Add(diffPng);
                visualArtifacts.Add(diffPng);

                bool foregroundNeedsReview = !comparableSize ||
                    foregroundStats.ForegroundDeltaRatio > ForegroundDeltaReviewThreshold ||
                    foregroundStats.PdfMissRatio > PdfMissReviewThreshold ||
                    foregroundStats.BrowserMissRatio > BrowserMissReviewThreshold;
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-foreground-mask",
                    "visual",
                    foregroundNeedsReview ? NeedsReview : Passed,
                    pageNumber,
                    !comparableSize
                        ? "PDF render and browser page screenshot have different pixel dimensions; foreground masks were compared across the overlapping region only."
                        : foregroundNeedsReview
                        ? "PDF and browser foreground masks differ beyond the current review thresholds."
                        : sameSize
                            ? "PDF and browser foreground masks match within the current review thresholds."
                            : "PDF and browser foreground masks match within the current review thresholds after allowing a small renderer/browser rounding delta.",
                    new Dictionary<string, double>
                    {
                        ["foregroundDeltaRatio"] = foregroundStats.ForegroundDeltaRatio,
                        ["pdfMissRatio"] = foregroundStats.PdfMissRatio,
                        ["htmlMissRatio"] = foregroundStats.BrowserMissRatio,
                        ["pdfWidth"] = pdfRender.Image.Width,
                        ["pdfHeight"] = pdfRender.Image.Height,
                        ["htmlWidth"] = browserImage.Width,
                        ["htmlHeight"] = browserImage.Height,
                        ["comparedWidth"] = comparisonWidth,
                        ["comparedHeight"] = comparisonHeight,
                        ["widthDelta"] = widthDelta,
                        ["heightDelta"] = heightDelta
                    }));
            }

            ColorDifferenceStats? colorStats = ColorDifferenceStats.Create(
                pdfRender.Image,
                browserImage,
                comparisonWidth,
                comparisonHeight,
                ForegroundLuminanceThreshold,
                ColorInteriorErosionRadius,
                SevereColorDeltaThreshold);
            if (colorStats == null)
            {
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-color-difference",
                    "visual",
                    Skipped,
                    pageNumber,
                    "Color comparison found no stable shared-foreground interior pixels after excluding antialiased edges.",
                    new Dictionary<string, double>()));
            }
            else
            {
                using BufferedImage heatmap = colorStats.CreateHeatmap();
                await File.WriteAllBytesAsync(
                    Path.Combine(outputDirectory, colorHeatmapPng),
                    RenderingBackend.Current.ImageCodec.Encode(heatmap, EncodedImageFormat.Png, 100),
                    cancellationToken);
                artifacts.Add(colorHeatmapPng);
                visualArtifacts.Add(colorHeatmapPng);

                bool hasMaterialColorSample = colorStats.ComparedPixelRatio >= MinimumColorComparedPixelRatio;
                bool colorNeedsReview = !comparableSize ||
                    hasMaterialColorSample &&
                    colorStats.SevereColorDeltaRatio > SevereColorDeltaRatioReviewThreshold;
                string colorStatus = colorNeedsReview
                    ? NeedsReview
                    : hasMaterialColorSample
                        ? Passed
                        : Skipped;
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-color-difference",
                    "visual",
                    colorStatus,
                    pageNumber,
                    !comparableSize
                        ? "PDF render and browser page screenshot have different pixel dimensions; color was compared across stable foreground interiors in the overlapping region only."
                        : !hasMaterialColorSample
                            ? "Color comparison found too few stable shared-foreground interior pixels for a material page-level assessment."
                        : colorNeedsReview
                            ? "PDF and browser colors differ severely across a material share of stable foreground interiors."
                            : "PDF and browser colors match within the current perceptual review thresholds after excluding antialiased edges.",
                    new Dictionary<string, double>
                    {
                        ["meanColorDelta"] = colorStats.MeanColorDelta,
                        ["severeColorDeltaRatio"] = colorStats.SevereColorDeltaRatio,
                        ["colorComparedPixelRatio"] = colorStats.ComparedPixelRatio,
                        ["colorComparedPixels"] = colorStats.ComparedPixels,
                        ["severeColorDeltaThreshold"] = SevereColorDeltaThreshold,
                        ["severeColorDeltaRatioReviewThreshold"] = SevereColorDeltaRatioReviewThreshold,
                        ["minimumColorComparedPixelRatio"] = MinimumColorComparedPixelRatio,
                        ["colorInteriorErosionRadius"] = ColorInteriorErosionRadius
                    }));
            }

            WriteText(
                Path.Combine(outputDirectory, visualReportHtml),
                RenderVisualReport(pageNumber, visualArtifacts, checks.Where(check => check.PageNumber == pageNumber)));
            artifacts.Add(visualReportHtml);
            return new PdfHtmlVisualMetrics(
                pdfRender.Image.Width,
                pdfRender.Image.Height,
                browserImage.Width,
                browserImage.Height,
                PdfRenderSource: pdfRender.Source,
                PageSizeMatches: pageSizeMatches,
                ForegroundDeltaRatio: foregroundStats?.ForegroundDeltaRatio,
                PdfMissRatio: foregroundStats?.PdfMissRatio,
                HtmlMissRatio: foregroundStats?.BrowserMissRatio,
                MeanColorDelta: colorStats?.MeanColorDelta,
                SevereColorDeltaRatio: colorStats?.SevereColorDeltaRatio,
                ColorComparedPixelRatio: colorStats?.ComparedPixelRatio);
        }
        catch (Exception ex)
        {
            if (!checks.Any(check => check.Id == "visual-foreground-mask" && check.PageNumber == pageNumber))
            {
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-foreground-mask",
                    "visual",
                    Skipped,
                    pageNumber,
                    $"Visual foreground comparison could not run: {ex.Message}",
                    new Dictionary<string, double>()));
            }

            if (!checks.Any(check => check.Id == "visual-color-difference" && check.PageNumber == pageNumber))
            {
                checks.Add(new PdfHtmlQualityCheck(
                    "visual-color-difference",
                    "visual",
                    Skipped,
                    pageNumber,
                    $"Visual color comparison could not run: {ex.Message}",
                    new Dictionary<string, double>()));
            }

            return null;
        }
    }

    private static async Task<RenderedPdfPage> RenderPdfPageAsync(
        string sourcePdfPath,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        string? pdftoppm = FindExecutable("pdftoppm");
        if (pdftoppm is not null)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "pdfbox-net-poppler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                string outputPrefix = Path.Combine(tempDirectory, "page");
                ProcessResult result = await RunProcessAsync(
                    pdftoppm,
                    [
                        "-f",
                        pageNumber.ToString(CultureInfo.InvariantCulture),
                        "-l",
                        pageNumber.ToString(CultureInfo.InvariantCulture),
                        "-singlefile",
                        "-r",
                        "96",
                        "-png",
                        sourcePdfPath,
                        outputPrefix
                    ],
                    cancellationToken);
                string pngPath = outputPrefix + ".png";
                if (result.ExitCode == 0 && File.Exists(pngPath))
                {
                    byte[] png = await File.ReadAllBytesAsync(pngPath, cancellationToken);
                    return new RenderedPdfPage(DecodePng(png), png, "poppler-pdftoppm");
                }
            }
            catch
            {
                // Fall back to the in-process renderer below. The report records which renderer was used.
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }

        using PDDocument document = Loader.LoadPDF(sourcePdfPath);
        BufferedImage image = new PDFRenderer(document).RenderImageWithDPI(pageNumber - 1, 96f, ImageType.RGB);
        byte[] encoded = RenderingBackend.Current.ImageCodec.Encode(image, EncodedImageFormat.Png, 100);
        return new RenderedPdfPage(image, encoded, "pdfbox-net-renderer");
    }

    private static BufferedImage DecodePng(byte[] png)
    {
        return RenderingBackend.Current.ImageCodec.Decode(png)
            ?? throw new InvalidOperationException("Unable to decode PNG image.");
    }

    private static int CountOverlaps(
        IReadOnlyList<BrowserBox> first,
        IReadOnlyList<BrowserBox> second,
        double minArea,
        double minTextFraction)
    {
        int count = 0;
        foreach (BrowserBox textBox in first)
        {
            if (textBox.Area <= 0)
            {
                continue;
            }

            foreach (BrowserBox objectBox in second)
            {
                double area = IntersectionArea(textBox, objectBox);
                if (area >= minArea && area / textBox.Area >= minTextFraction)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static bool IsPageBackground(BrowserBox box, BrowserPageSnapshot snapshot)
    {
        if (box.Area <= 0 || snapshot.Width <= 0 || snapshot.Height <= 0)
        {
            return false;
        }

        double horizontalTolerance = Math.Max(2, snapshot.Width * 0.02);
        double verticalTolerance = Math.Max(2, snapshot.Height * 0.02);
        bool touchesLeft = box.X <= horizontalTolerance;
        bool touchesTop = box.Y <= verticalTolerance;
        bool touchesRight = box.Right >= snapshot.Width - horizontalTolerance;
        bool touchesBottom = box.Bottom >= snapshot.Height - verticalTolerance;

        BrowserBox page = new()
        {
            Width = snapshot.Width,
            Height = snapshot.Height
        };
        double visibleArea = IntersectionArea(box, page);
        if (touchesLeft && touchesTop && touchesRight && touchesBottom &&
            visibleArea >= page.Area * 0.85)
        {
            return true;
        }

        double visibleWidth = Math.Max(0, Math.Min(box.Right, page.Right) - Math.Max(box.X, page.X));
        double visibleHeight = Math.Max(0, Math.Min(box.Bottom, page.Bottom) - Math.Max(box.Y, page.Y));
        bool fullWidthEdgeBand = touchesLeft && touchesRight &&
            (touchesTop || touchesBottom) &&
            visibleWidth >= page.Width * 0.90 &&
            visibleHeight >= page.Height * 0.15;
        bool fullHeightEdgeBand = touchesTop && touchesBottom &&
            (touchesLeft || touchesRight) &&
            visibleHeight >= page.Height * 0.90 &&
            visibleWidth >= page.Width * 0.15;
        return fullWidthEdgeBand || fullHeightEdgeBand;
    }

    private static double IntersectionArea(BrowserBox first, BrowserBox second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

    private static string CombinedStatus(IEnumerable<PdfHtmlQualityCheck> checks)
    {
        PdfHtmlQualityCheck[] materialized = checks.ToArray();
        if (materialized.Any(static check => check.Status == NeedsReview))
        {
            return NeedsReview;
        }

        return materialized.Length == 0 || materialized.All(static check => check.Status == Skipped)
            ? Skipped
            : Passed;
    }

    private static IReadOnlyList<PdfHtmlQualityIssueCategory> BuildIssueCategories(
        IReadOnlyList<PdfHtmlQualityPageReport> pages,
        IReadOnlyList<PdfHtmlQualityCheck> checks)
    {
        PdfHtmlQualityCheck[] setupChecks = checks.Where(static check => check.Category == "setup").ToArray();
        return
        [
            new PdfHtmlQualityIssueCategory(
                "text-boundaries",
                "Text reconstruction and word boundaries",
                CombinedStatus(checks.Where(static check => check.Id == "word-boundaries")),
                TextBoundaryEvidence(pages)),
            new PdfHtmlQualityIssueCategory(
                "layout-geometry",
                "Page geometry and text-run structure",
                CombinedStatus(checks.Where(static check => check.Id is "page-dimensions" or "text-run-count")),
                LayoutGeometryEvidence(pages, checks)),
            new PdfHtmlQualityIssueCategory(
                "object-wrapping",
                "Text wrapping around images and vector objects",
                CombinedStatus(checks.Where(static check => check.Id is "text-image-overlap" or "text-vector-overlap")),
                ObjectWrappingEvidence(pages)),
            new PdfHtmlQualityIssueCategory(
                "visual-foreground",
                "Visual foreground parity",
                CombinedStatus(checks.Where(static check => check.Id == "visual-foreground-mask")),
                VisualForegroundEvidence(pages)),
            new PdfHtmlQualityIssueCategory(
                "visual-color",
                "Visual color parity",
                CombinedStatus(checks.Where(static check => check.Id == "visual-color-difference")),
                VisualColorEvidence(pages)),
            new PdfHtmlQualityIssueCategory(
                "fixture-expectations",
                "Source fixture expectations (manual review)",
                FixtureExpectationStatus(checks),
                FixtureExpectationEvidence(checks)),
            new PdfHtmlQualityIssueCategory(
                "probe-setup",
                "Probe setup and tool availability",
                setupChecks.Length == 0 ? Passed : CombinedStatus(setupChecks),
                SetupEvidence(setupChecks))
        ];
    }

    private static string TextBoundaryEvidence(IReadOnlyList<PdfHtmlQualityPageReport> pages)
    {
        if (pages.Count == 0)
        {
            return "No pages were analyzed.";
        }

        PdfHtmlQualityPageReport worst = pages.MinBy(static page => page.TextTokenCoverage)!;
        int pagesNeedingReview = pages.Count(static page =>
            page.Checks.Any(static check => check.Id == "word-boundaries" && check.Status == NeedsReview));
        int suspiciousJoins = pages.Sum(static page => page.Checks
            .Where(static check => check.Id == "word-boundaries")
            .Sum(static check => (int)check.Metrics.GetValueOrDefault("adjacentSourceWordJoinCount")));
        return pagesNeedingReview > 0
            ? $"{pagesNeedingReview} page(s) need review; worst token coverage is {FormatRatio(worst.TextTokenCoverage)} on page {worst.PageNumber}, with {suspiciousJoins} high-confidence adjacent-word join(s)."
            : $"All analyzed pages passed; lowest token coverage is {FormatRatio(worst.TextTokenCoverage)} on page {worst.PageNumber}.";
    }

    private static string LayoutGeometryEvidence(
        IReadOnlyList<PdfHtmlQualityPageReport> pages,
        IReadOnlyList<PdfHtmlQualityCheck> checks)
    {
        int dimensionIssues = checks.Count(static check => check.Id == "page-dimensions" && check.Status == NeedsReview);
        int runCountIssues = checks.Count(static check => check.Id == "text-run-count" && check.Status == NeedsReview);
        if (pages.Count == 0)
        {
            return "No browser page geometry was captured.";
        }

        int maxRunDelta = pages.Max(static page => Math.Abs(page.LayoutTextRuns - page.HtmlTextRuns));
        int semanticGroupingChecks = checks.Count(static check => check.Id == "text-run-count" && check.Status == Skipped);
        return dimensionIssues > 0 || runCountIssues > 0
            ? $"{dimensionIssues} page dimension check(s) and {runCountIssues} text-run count check(s) need review; max run-count delta is {maxRunDelta}."
            : semanticGroupingChecks > 0
                ? $"Page dimensions passed where measured; {semanticGroupingChecks} continuous semantic-flow run-count check(s) were skipped because source runs are intentionally grouped."
                : $"Page dimensions and text-run counts passed; max run-count delta is {maxRunDelta}.";
    }

    private static string ObjectWrappingEvidence(IReadOnlyList<PdfHtmlQualityPageReport> pages)
    {
        int imageOverlaps = pages.Sum(static page => page.TextImageOverlaps);
        int vectorOverlaps = pages.Sum(static page => page.TextVectorOverlaps);
        return imageOverlaps + vectorOverlaps > 0
            ? $"{imageOverlaps} text/image overlap(s) and {vectorOverlaps} text/vector overlap(s) were found."
            : "No text overlaps with image boxes or large vector boxes were found on analyzed pages.";
    }

    private static string VisualForegroundEvidence(IReadOnlyList<PdfHtmlQualityPageReport> pages)
    {
        PdfHtmlVisualMetrics[] visuals = pages
            .Select(static page => page.Visual)
            .Where(static visual => visual is not null)
            .Cast<PdfHtmlVisualMetrics>()
            .ToArray();
        if (visuals.Length == 0)
        {
            return "No visual foreground mask metrics were captured.";
        }

        double maxDelta = visuals.Max(static visual => visual.ForegroundDeltaRatio ?? 0);
        double maxPdfMiss = visuals.Max(static visual => visual.PdfMissRatio ?? 0);
        double maxHtmlMiss = visuals.Max(static visual => visual.HtmlMissRatio ?? 0);
        return $"Max foreground delta is {FormatRatio(maxDelta)}; max source-only foreground ratio is {FormatRatio(maxPdfMiss)}; max HTML-only foreground ratio is {FormatRatio(maxHtmlMiss)}.";
    }

    private static string VisualColorEvidence(IReadOnlyList<PdfHtmlQualityPageReport> pages)
    {
        PdfHtmlVisualMetrics[] visuals = pages
            .Select(static page => page.Visual)
            .Where(static visual => visual?.SevereColorDeltaRatio is not null)
            .Cast<PdfHtmlVisualMetrics>()
            .ToArray();
        if (visuals.Length == 0)
        {
            return "No stable shared-foreground interior pixels were available for perceptual color comparison.";
        }

        double maxMeanDelta = visuals.Max(static visual => visual.MeanColorDelta ?? 0);
        double maxSevereRatio = visuals.Max(static visual => visual.SevereColorDeltaRatio ?? 0);
        double minComparedRatio = visuals.Min(static visual => visual.ColorComparedPixelRatio ?? 0);
        return $"Max mean CIE Lab color delta is {FormatRatio(maxMeanDelta)}; max severe color-delta ratio is {FormatRatio(maxSevereRatio)}; minimum compared page-pixel ratio is {FormatRatio(minComparedRatio)}.";
    }

    private static string FixtureExpectationStatus(IReadOnlyList<PdfHtmlQualityCheck> checks)
    {
        return checks.Any(static check => check.Id == "fixture-expectation")
            ? NeedsReview
            : Skipped;
    }

    private static string FixtureExpectationEvidence(IReadOnlyList<PdfHtmlQualityCheck> checks)
    {
        PdfHtmlQualityCheck[] expectations = checks
            .Where(static check => check.Id == "fixture-expectation")
            .ToArray();
        if (expectations.Length == 0)
        {
            return "The source PDF does not contain recognizable self-describing fixture expectations.";
        }

        int pageCount = expectations
            .Select(static check => check.PageNumber)
            .Distinct()
            .Count();
        return $"Captured {expectations.Length} source-authored acceptance statement(s) across {pageCount} page(s); compare them with the visual evidence before treating the fixture as passed.";
    }

    private static string SetupEvidence(IReadOnlyList<PdfHtmlQualityCheck> setupChecks)
    {
        if (setupChecks.Count == 0)
        {
            return "The browser probe and available renderers ran.";
        }

        return string.Join(" ", setupChecks.Select(static check => check.Message));
    }

    private static IReadOnlyList<string> BuildCurrentLimitations(
        string? notes,
        IReadOnlyList<PdfHtmlQualityPageReport> pages,
        IReadOnlyList<PdfHtmlQualityCheck> checks)
    {
        List<string> limitations = [];
        AddManifestNoteLimitations(notes, limitations);

        PdfHtmlQualityCheck[] needsReview = checks
            .Where(static check => check.Status == NeedsReview)
            .ToArray();
        if (needsReview.Any(static check => check.Id == "word-boundaries"))
        {
            AddUnique(
                limitations,
                "Text reconstruction did not preserve all word boundaries or reading order on the affected page(s).");
        }

        if (needsReview.Any(static check => check.Id is "page-dimensions" or "text-run-count"))
        {
            AddUnique(
                limitations,
                "Continuous semantic HTML is evaluated as reflowed content; it may not preserve the original PDF page height, per-glyph run count, or exact page-local geometry.");
        }

        if (needsReview.Any(static check => check.Id == "text-image-overlap"))
        {
            AddUnique(
                limitations,
                "Some image-backed content still behaves like fixed-position objects; text may collide with image regions.");
        }

        if (needsReview.Any(static check => check.Id == "text-vector-overlap"))
        {
            AddUnique(
                limitations,
                "Large vector overlays are not semantically attached to nearby content; review whether these are decorative backgrounds, table rules, clipping masks, or true text/object collisions.");
        }

        if (needsReview.Any(static check => check.Id == "visual-foreground-mask"))
        {
            bool dimensionMismatch = needsReview.Any(static check =>
                check.Id == "visual-foreground-mask" &&
                check.Message.Contains("different pixel dimensions", StringComparison.OrdinalIgnoreCase));
            if (dimensionMismatch)
            {
                AddUnique(
                    limitations,
                    "The visual probe compared at least one page using only the overlapping crop because renderer/browser pixel dimensions differed beyond the probe tolerance.");
            }
            else
            {
                AddUnique(
                    limitations,
                    "Visual foreground parity differs beyond the current thresholds; inspect the diff PNGs for unsupported transparency, print-color behavior, formula glyph positioning, image-mask handling, or text placement issues.");
            }
        }

        if (needsReview.Any(static check => check.Id == "visual-color-difference"))
        {
            AddUnique(
                limitations,
                "Perceptual color differences exceed the severe-corruption threshold on at least one page; inspect the color heatmap for incorrect fills, opacity, blending, or color-space conversion.");
        }

        if (pages.Any(static page => page.Visual?.HtmlMissRatio >= BrowserMissReviewThreshold))
        {
            AddUnique(
                limitations,
                "The HTML contains additional foreground pixels compared with the PDF render on at least one page, which often means duplicated glyphs, fallback text, or unmodeled object masks.");
        }

        if (limitations.Count == 0)
        {
            limitations.Add("No unsupported behavior was detected by the current probe thresholds.");
        }

        return limitations;
    }

    private static void AddManifestNoteLimitations(string? notes, List<string> limitations)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return;
        }

        string normalized = notes.ToLowerInvariant();
        if (normalized.Contains("color", StringComparison.Ordinal) ||
            normalized.Contains("transparency", StringComparison.Ordinal) ||
            normalized.Contains("production-output", StringComparison.Ordinal))
        {
            AddUnique(
                limitations,
                "This fixture intentionally exercises print-production features such as color management, overprint, shading, transparency, or masks; the HTML converter currently approximates these rather than preserving production fidelity.");
        }

        if (normalized.Contains("math", StringComparison.Ordinal) ||
            normalized.Contains("formula", StringComparison.Ordinal))
        {
            AddUnique(
                limitations,
                "Mathematical expressions are reconstructed from positioned PDF glyphs; complex fractions, roots, and grouped formula runs may require manual visual review.");
        }

        if (Regex.IsMatch(
            normalized,
            @"\b(acroform|interactive[- ]form|form[- ]heavy|form field|widget)\b",
            RegexOptions.CultureInvariant))
        {
            AddUnique(
                limitations,
                "Interactive form and widget appearances are exported visually; editable form semantics are not reconstructed in the generated HTML.");
        }
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    private static string FormatRatio(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string RenderMarkdownReport(PdfHtmlQualityReport report)
    {
        StringBuilder markdown = new();
        markdown.AppendLine("# HTML Quality Probe");
        markdown.AppendLine();
        markdown.AppendLine("These checks are diagnostic and non-gating. A `needs-review` status means the generated artifacts found a likely conversion-quality issue, not that artifact generation failed.");
        markdown.AppendLine();
        markdown.AppendLine($"- Status: `{report.Status}`");
        markdown.AppendLine($"- Source PDF: [{report.SourcePdf}]({report.SourcePdf})");
        markdown.AppendLine($"- Generated HTML: [{report.Html}]({report.Html})");
        if (!string.IsNullOrWhiteSpace(report.Notes))
        {
            markdown.AppendLine($"- Sample notes: {report.Notes}");
        }

        markdown.AppendLine($"- Text reference: `{report.TextReferenceSource}`");
        markdown.AppendLine($"- Pages analyzed: {report.PagesAnalyzed}");
        markdown.AppendLine();

        markdown.AppendLine("## Page Summary");
        markdown.AppendLine();
        markdown.AppendLine("| Page | Status | Source words | HTML words | Token coverage | Long HTML tokens | Foreground delta | Severe color delta | Text/object overlaps |");
        markdown.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (PdfHtmlQualityPageReport page in report.Pages)
        {
            string visualDelta = page.Visual?.ForegroundDeltaRatio is double ratio
                ? ratio.ToString("0.###", CultureInfo.InvariantCulture)
                : "";
            string colorDelta = page.Visual?.SevereColorDeltaRatio is double severeColorRatio
                ? severeColorRatio.ToString("0.###", CultureInfo.InvariantCulture)
                : "";
            markdown.Append("| ");
            markdown.Append(page.PageNumber.ToString(CultureInfo.InvariantCulture));
            markdown.Append(" | `");
            markdown.Append(page.Status);
            markdown.Append("` | ");
            markdown.Append(page.SourceWordCount.ToString(CultureInfo.InvariantCulture));
            markdown.Append(" | ");
            markdown.Append(page.HtmlWordCount.ToString(CultureInfo.InvariantCulture));
            markdown.Append(" | ");
            markdown.Append(page.TextTokenCoverage.ToString("0.###", CultureInfo.InvariantCulture));
            markdown.Append(" | ");
            markdown.Append(page.LongHtmlTokens.ToString(CultureInfo.InvariantCulture));
            markdown.Append(" | ");
            markdown.Append(visualDelta);
            markdown.Append(" | ");
            markdown.Append(colorDelta);
            markdown.Append(" | ");
            markdown.Append((page.TextImageOverlaps + page.TextVectorOverlaps).ToString(CultureInfo.InvariantCulture));
            markdown.AppendLine(" |");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Issue Categories");
        markdown.AppendLine();
        markdown.AppendLine("| Category | Status | Evidence |");
        markdown.AppendLine("| --- | --- | --- |");
        foreach (PdfHtmlQualityIssueCategory category in report.IssueCategories)
        {
            markdown.Append("| ");
            markdown.Append(WebUtility.HtmlEncode(category.Title));
            markdown.Append(" | `");
            markdown.Append(category.Status);
            markdown.Append("` | ");
            markdown.Append(WebUtility.HtmlEncode(category.Evidence));
            markdown.AppendLine(" |");
        }

        PdfHtmlQualityCheck[] fixtureExpectations = report.Checks
            .Where(static check => check.Id == "fixture-expectation")
            .ToArray();
        if (fixtureExpectations.Length > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("## Fixture Expectations");
            markdown.AppendLine();
            markdown.AppendLine("These source-authored statements describe the intended appearance and make the corresponding visual diffs easier to interpret.");
            markdown.AppendLine();
            foreach (IGrouping<int?, PdfHtmlQualityCheck> page in fixtureExpectations.GroupBy(static check => check.PageNumber))
            {
                string pageLabel = page.Key is int pageNumber
                    ? $"Page {pageNumber.ToString(CultureInfo.InvariantCulture)}"
                    : "Document";
                markdown.AppendLine($"- {pageLabel}:");
                foreach (PdfHtmlQualityCheck expectation in page)
                {
                    markdown.Append("  - ");
                    markdown.AppendLine(WebUtility.HtmlEncode(expectation.Message));
                }
            }
        }

        markdown.AppendLine();
        markdown.AppendLine("## Current Limitations");
        markdown.AppendLine();
        foreach (string limitation in report.Limitations)
        {
            markdown.Append("- ");
            markdown.AppendLine(limitation);
        }

        markdown.AppendLine();
        markdown.AppendLine("## Findings");
        markdown.AppendLine();
        foreach (PdfHtmlQualityCheck check in report.Checks.Where(static check => check.Status == NeedsReview))
        {
            markdown.Append("- ");
            if (check.PageNumber is int pageNumber)
            {
                markdown.Append("Page ");
                markdown.Append(pageNumber.ToString(CultureInfo.InvariantCulture));
                markdown.Append(", ");
            }

            markdown.Append('`');
            markdown.Append(check.Id);
            markdown.Append("`: ");
            markdown.AppendLine(check.Message);
        }

        if (!report.Checks.Any(static check => check.Status == NeedsReview))
        {
            markdown.AppendLine("- No checks need review at the current thresholds.");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Artifacts");
        markdown.AppendLine();
        foreach (string artifact in report.Artifacts)
        {
            markdown.Append("- [");
            markdown.Append(artifact);
            markdown.Append("](");
            markdown.Append(artifact);
            markdown.AppendLine(")");
        }

        return markdown.ToString();
    }

    private static string RenderVisualReport(
        int pageNumber,
        IReadOnlyList<string> visualArtifacts,
        IEnumerable<PdfHtmlQualityCheck> checks)
    {
        PdfHtmlQualityCheck[] pageChecks = checks.ToArray();
        PdfHtmlQualityCheck[] fixtureExpectations = pageChecks
            .Where(static check => check.Id == "fixture-expectation")
            .ToArray();
        StringBuilder html = new();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\" />");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.Append("  <title>Page ");
        html.Append(pageNumber.ToString(CultureInfo.InvariantCulture));
        html.AppendLine(" visual quality probe</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body{font-family:Arial,sans-serif;margin:24px;color:#111827;background:#f9fafb}");
        html.AppendLine("    .images{display:flex;gap:18px;align-items:flex-start;flex-wrap:wrap}");
        html.AppendLine("    figure{margin:0;padding:10px;background:white;border:1px solid #d1d5db}");
        html.AppendLine("    img{max-width:30vw;height:auto;border:1px solid #e5e7eb}");
        html.AppendLine("    code{white-space:pre-wrap}");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.Append("  <h1>Page ");
        html.Append(pageNumber.ToString(CultureInfo.InvariantCulture));
        html.AppendLine(" Visual Quality Probe</h1>");
        html.AppendLine("  <p>Foreground diff: dark pixels are shared foreground, red is source-PDF-only foreground, and blue is HTML-only foreground.</p>");
        html.AppendLine("  <p>Color heatmap: white pixels are background or excluded antialiased edges, gray is a close color match, and yellow through dark red indicates increasing perceptual color difference.</p>");
        html.AppendLine("  <div class=\"images\">");
        foreach (string artifact in visualArtifacts)
        {
            html.Append("    <figure><figcaption>");
            html.Append(WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(artifact)));
            html.Append("</figcaption><img src=\"");
            html.Append(WebUtility.HtmlEncode(artifact));
            html.Append("\" alt=\"");
            html.Append(WebUtility.HtmlEncode(artifact));
            html.AppendLine("\" /></figure>");
        }

        html.AppendLine("  </div>");
        if (fixtureExpectations.Length > 0)
        {
            html.AppendLine("  <h2>Fixture Expectations</h2>");
            html.AppendLine("  <p>These statements were extracted from the source PDF and describe the intended result.</p>");
            html.AppendLine("  <ul>");
            foreach (PdfHtmlQualityCheck expectation in fixtureExpectations)
            {
                html.Append("    <li>");
                html.Append(WebUtility.HtmlEncode(expectation.Message));
                html.AppendLine("</li>");
            }

            html.AppendLine("  </ul>");
        }

        html.AppendLine("  <h2>Checks</h2>");
        html.AppendLine("  <code>");
        foreach (PdfHtmlQualityCheck check in pageChecks.Where(static check => check.Id != "fixture-expectation"))
        {
            html.Append(WebUtility.HtmlEncode($"{check.Status} {check.Id}: {check.Message}"));
            html.Append('\n');
        }

        html.AppendLine("</code>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static string RelativePath(string baseDirectory, string path)
    {
        return Path.GetRelativePath(baseDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static IReadOnlyList<string> ExtractFixtureExpectations(PdfLayoutPage page)
    {
        string[] lines = page.Lines
            .Select(static line => NormalizeFixtureText(line.Text))
            .Where(static line => line.Length > 0)
            .ToArray();
        List<string> expectations = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string candidate = lines[index];
            if (NeedsFixtureContinuation(candidate) && index + 1 < lines.Length)
            {
                string next = lines[index + 1];
                if (candidate.Length + next.Length + 1 <= 280)
                {
                    candidate += " " + next;
                }
            }

            candidate = CollapseRepeatedFixtureText(candidate);
            if (FixtureExpectationSignals.Any(signal => candidate.Contains(signal, StringComparison.OrdinalIgnoreCase)))
            {
                AddUnique(expectations, candidate);
            }
        }

        return expectations.Take(12).ToArray();
    }

    private static string NormalizeFixtureText(string text)
    {
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static bool EndsFixtureStatement(string text)
    {
        return text.EndsWith(".", StringComparison.Ordinal) ||
            text.EndsWith('!') ||
            text.EndsWith('?');
    }

    private static bool NeedsFixtureContinuation(string text)
    {
        return !EndsFixtureStatement(text) &&
            (text.EndsWith("should look", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith("should match", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith("expected result:", StringComparison.OrdinalIgnoreCase));
    }

    private static string CollapseRepeatedFixtureText(string text)
    {
        string collapsed = text;
        for (int length = text.Length / 2; length >= 12; length--)
        {
            if (!text.AsSpan(0, length).SequenceEqual(text.AsSpan(length, length)))
            {
                continue;
            }

            collapsed = text[..length] + text[(length * 2)..];
            break;
        }

        return collapsed;
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

    private static string? FindExecutable(string name)
    {
        IEnumerable<string> pathCandidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] extraCandidates =
        [
            Path.Combine(home, ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "bin", name),
            Path.Combine("/opt/homebrew/bin", name),
            Path.Combine("/usr/local/bin", name),
            Path.Combine("/usr/bin", name)
        ];

        foreach (string candidate in pathCandidates.Concat(extraCandidates))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TextReference
    {
        private TextReference(string source, IReadOnlyList<string> pageTexts)
        {
            Source = source;
            PageTexts = pageTexts;
        }

        public string Source { get; }

        public IReadOnlyList<string> PageTexts { get; }

        public static async Task<TextReference> CreateAsync(
            string sourcePdfPath,
            PdfLayoutDocument layout,
            int pageLimit,
            CancellationToken cancellationToken)
        {
            string? pdftotext = FindExecutable("pdftotext");
            if (pdftotext is not null)
            {
                List<string> pageTexts = [];
                for (int pageNumber = 1; pageNumber <= pageLimit; pageNumber++)
                {
                    ProcessResult result = await RunProcessAsync(
                        pdftotext,
                        [
                            "-f",
                            pageNumber.ToString(CultureInfo.InvariantCulture),
                            "-l",
                            pageNumber.ToString(CultureInfo.InvariantCulture),
                            "-layout",
                            sourcePdfPath,
                            "-"
                        ],
                        cancellationToken);
                    if (result.ExitCode != 0)
                    {
                        pageTexts.Clear();
                        break;
                    }

                    pageTexts.Add(result.StandardOutput);
                }

                if (pageTexts.Count == pageLimit)
                {
                    return new TextReference("poppler-pdftotext-layout", pageTexts);
                }
            }

            return new TextReference(
                "pdfbox-net-run-fallback",
                layout.Pages.Take(pageLimit)
                    .Select(ReconstructLayoutPageText)
                    .ToArray());
        }

        private static string ReconstructLayoutPageText(PdfLayoutPage page)
        {
            return string.Join(
                Environment.NewLine,
                page.Lines.Select(static line => ReconstructLineText(line.Runs.SelectMany(static run => run.Glyphs))));
        }

        private static string ReconstructLineText(IEnumerable<PdfTextGlyph> glyphSource)
        {
            PdfTextGlyph[] glyphs = glyphSource
                .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
                .OrderBy(static glyph => glyph.Bounds.X)
                .ThenBy(static glyph => glyph.Bounds.Y)
                .ToArray();
            if (glyphs.Length == 0)
            {
                return "";
            }

            StringBuilder text = new();
            PdfTextGlyph? previous = null;
            foreach (PdfTextGlyph glyph in glyphs)
            {
                if (previous != null && ShouldInsertWordBoundary(previous, glyph))
                {
                    AppendSpaceIfNeeded(text);
                }

                if (string.IsNullOrWhiteSpace(glyph.Text))
                {
                    AppendSpaceIfNeeded(text);
                }
                else
                {
                    text.Append(glyph.Text);
                }

                previous = glyph;
            }

            return CollapseWhitespace(text.ToString());
        }

        private static bool ShouldInsertWordBoundary(PdfTextGlyph previous, PdfTextGlyph glyph)
        {
            if (glyph.Bounds.X <= previous.Bounds.X || glyph.Text.Length == 0 || previous.Text.Length == 0)
            {
                return false;
            }

            if (NoSpaceBefore(glyph.Text[0]) || NoSpaceAfter(previous.Text[^1]))
            {
                return false;
            }

            float gap = glyph.Bounds.X - previous.Bounds.Right;
            float threshold = MathF.Max(0.8f, MathF.Min(previous.FontSize, glyph.FontSize) * 0.16f);
            return gap > threshold;
        }

        private static void AppendSpaceIfNeeded(StringBuilder text)
        {
            if (text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
            }
        }

        private static string CollapseWhitespace(string text)
        {
            StringBuilder normalized = new(text.Length);
            bool pendingWhitespace = false;
            foreach (char character in text.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingWhitespace = normalized.Length > 0;
                    continue;
                }

                if (pendingWhitespace)
                {
                    normalized.Append(' ');
                    pendingWhitespace = false;
                }

                normalized.Append(character);
            }

            return normalized.ToString();
        }

        private static bool NoSpaceBefore(char character)
        {
            return character is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '\'' or '’';
        }

        private static bool NoSpaceAfter(char character)
        {
            return character is '(' or '[' or '{' or '“' or '"';
        }
    }

    private sealed class TextQualityMetrics
    {
        private TextQualityMetrics(
            int sourceWordCount,
            int htmlWordCount,
            double wordCountRatio,
            double tokenCoverage,
            int longSourceTokens,
            int longHtmlTokens,
            int adjacentSourceWordJoinCount)
        {
            SourceWordCount = sourceWordCount;
            HtmlWordCount = htmlWordCount;
            WordCountRatio = wordCountRatio;
            TokenCoverage = tokenCoverage;
            LongSourceTokens = longSourceTokens;
            LongHtmlTokens = longHtmlTokens;
            AdjacentSourceWordJoinCount = adjacentSourceWordJoinCount;
        }

        public int SourceWordCount { get; }

        public int HtmlWordCount { get; }

        public double WordCountRatio { get; }

        public double TokenCoverage { get; }

        public int LongSourceTokens { get; }

        public int LongHtmlTokens { get; }

        public int AdjacentSourceWordJoinCount { get; }

        public static TextQualityMetrics Create(string source, string html, string proseHtml)
        {
            string[] sourceWords = Words(source);
            string[] htmlWords = Words(html);
            Dictionary<string, int> sourceCounts = TokenCounts(sourceWords);
            Dictionary<string, int> htmlCounts = TokenCounts(htmlWords);
            int total = sourceCounts.Values.Sum();
            int matched = sourceCounts.Sum(pair => Math.Min(pair.Value, htmlCounts.GetValueOrDefault(pair.Key)));
            return new TextQualityMetrics(
                sourceWords.Length,
                htmlWords.Length,
                sourceWords.Length == 0 ? (htmlWords.Length == 0 ? 1 : 0) : htmlWords.Length / (double)sourceWords.Length,
                total == 0 ? (htmlWords.Length == 0 ? 1 : 0) : matched / (double)total,
                sourceWords.Count(static word => word.Length >= 32),
                htmlWords.Count(static word => word.Length >= 32),
                CountAdjacentSourceWordJoins(source, proseHtml));
        }

        private static int CountAdjacentSourceWordJoins(string source, string proseHtml)
        {
            IReadOnlyList<WordToken> sourceWords = WordTokens(source);
            IReadOnlyList<string> htmlWords = Words(proseHtml);
            HashSet<string> sourceVocabulary = new(
                sourceWords.Select(static word => word.Text),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<int>> joinedSourcePositions = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index + 1 < sourceWords.Count; index++)
            {
                WordToken first = sourceWords[index];
                WordToken second = sourceWords[index + 1];
                string separator = source[first.End..second.Start];
                string joined = first.Text + second.Text;
                if (first.Text.Length < 2 ||
                    second.Text.Length < 2 ||
                    Math.Min(first.Text.Length, second.Text.Length) > 3 ||
                    joined.Length < 6 ||
                    !first.Text.All(static character => char.IsLetter(character) && char.IsLower(character)) ||
                    !second.Text.All(static character => char.IsLetter(character) && char.IsLower(character)) ||
                    separator.Length == 0 ||
                    !separator.All(char.IsWhiteSpace) ||
                    sourceVocabulary.Contains(joined))
                {
                    continue;
                }

                if (!joinedSourcePositions.TryGetValue(joined, out List<int>? positions))
                {
                    positions = [];
                    joinedSourcePositions.Add(joined, positions);
                }

                positions.Add(index);
            }

            int count = 0;
            for (int htmlIndex = 0; htmlIndex < htmlWords.Count; htmlIndex++)
            {
                if (!joinedSourcePositions.TryGetValue(htmlWords[htmlIndex], out List<int>? positions))
                {
                    continue;
                }

                bool contextualMatch = positions.Any(sourceIndex =>
                    HasMatchingWordsBefore(sourceWords, sourceIndex, htmlWords, htmlIndex, 2) ||
                    HasMatchingWordsAfter(sourceWords, sourceIndex + 2, htmlWords, htmlIndex + 1, 2) ||
                    HasMatchingWordsBefore(sourceWords, sourceIndex, htmlWords, htmlIndex, 1) &&
                    HasMatchingWordsAfter(sourceWords, sourceIndex + 2, htmlWords, htmlIndex + 1, 1));
                if (contextualMatch)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasMatchingWordsBefore(
            IReadOnlyList<WordToken> sourceWords,
            int sourceIndex,
            IReadOnlyList<string> htmlWords,
            int htmlIndex,
            int count)
        {
            if (sourceIndex < count || htmlIndex < count)
            {
                return false;
            }

            return Enumerable.Range(1, count).All(offset => string.Equals(
                sourceWords[sourceIndex - offset].Text,
                htmlWords[htmlIndex - offset],
                StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasMatchingWordsAfter(
            IReadOnlyList<WordToken> sourceWords,
            int sourceIndex,
            IReadOnlyList<string> htmlWords,
            int htmlIndex,
            int count)
        {
            if (sourceIndex + count > sourceWords.Count || htmlIndex + count > htmlWords.Count)
            {
                return false;
            }

            return Enumerable.Range(0, count).All(offset => string.Equals(
                sourceWords[sourceIndex + offset].Text,
                htmlWords[htmlIndex + offset],
                StringComparison.OrdinalIgnoreCase));
        }

        private static WordToken[] WordTokens(string value)
        {
            return Regex.Matches(value, @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)?")
                .Select(static match => new WordToken(match.Value, match.Index, match.Index + match.Length))
                .ToArray();
        }

        private static string[] Words(string value)
        {
            return Regex.Matches(value, @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)?")
                .Select(static match => match.Value)
                .ToArray();
        }

        private static Dictionary<string, int> TokenCounts(IEnumerable<string> words)
        {
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            foreach (string word in words)
            {
                counts[word] = counts.GetValueOrDefault(word) + 1;
            }

            return counts;
        }

        private readonly record struct WordToken(string Text, int Start, int End);
    }

    private sealed class BrowserPageSnapshot
    {
        public double Width { get; set; }

        public double Height { get; set; }

        public string Text { get; set; } = "";

        public string ProseText { get; set; } = "";

        public List<BrowserBox> TextRuns { get; set; } = [];

        public List<BrowserBox> Images { get; set; } = [];

        public List<BrowserBox> VectorPaths { get; set; } = [];

        public bool IsSemanticFlow { get; set; }

        public bool UsesSpatialTextGrid { get; set; }
    }

    private sealed record BrowserPageCapture(BrowserPageSnapshot Snapshot, byte[] Png);

    private sealed class BrowserBox
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public string Text { get; set; } = "";

        public double Right => X + Width;

        public double Bottom => Y + Height;

        public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    }

    private static bool[] CreateForegroundMask(
        BufferedImage image,
        int width,
        int height,
        int luminanceThreshold)
    {
        bool[] mask = new bool[width * height];
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int argb = image.GetRgb(x, y);
                int alpha = (argb >> 24) & 0xFF;
                if (alpha == 0)
                {
                    index++;
                    continue;
                }

                int red = CompositeChannelOnWhite((argb >> 16) & 0xFF, alpha);
                int green = CompositeChannelOnWhite((argb >> 8) & 0xFF, alpha);
                int blue = CompositeChannelOnWhite(argb & 0xFF, alpha);
                int luminance = ((red * 299) + (green * 587) + (blue * 114)) / 1000;
                mask[index++] = luminance < luminanceThreshold;
            }
        }

        return mask;
    }

    private static int CompositeChannelOnWhite(int channel, int alpha)
    {
        return alpha >= 255 ? channel : ((channel * alpha) + (255 * (255 - alpha))) / 255;
    }

    private sealed class RenderedPdfPage : IDisposable
    {
        public RenderedPdfPage(BufferedImage image, byte[] png, string source)
        {
            Image = image;
            Png = png;
            Source = source;
        }

        public BufferedImage Image { get; }

        public byte[] Png { get; }

        public string Source { get; }

        public void Dispose()
        {
            Image.Dispose();
        }
    }

    private sealed class ForegroundShapeStats
    {
        private ForegroundShapeStats(double foregroundDeltaRatio, double pdfMissRatio, double browserMissRatio)
        {
            ForegroundDeltaRatio = foregroundDeltaRatio;
            PdfMissRatio = pdfMissRatio;
            BrowserMissRatio = browserMissRatio;
        }

        public double ForegroundDeltaRatio { get; }

        public double PdfMissRatio { get; }

        public double BrowserMissRatio { get; }

        public static ForegroundShapeStats? Create(
            BufferedImage pdfPage,
            BufferedImage browserPage,
            int width,
            int height,
            int luminanceThreshold,
            int dilationRadius)
        {
            bool[] pdfMask = CreateForegroundMask(pdfPage, width, height, luminanceThreshold);
            bool[] browserMask = CreateForegroundMask(browserPage, width, height, luminanceThreshold);
            int pdfForeground = pdfMask.Count(static foreground => foreground);
            int browserForeground = browserMask.Count(static foreground => foreground);
            int maxForeground = Math.Max(pdfForeground, browserForeground);
            if (maxForeground == 0)
            {
                return null;
            }

            bool[] dilatedPdfMask = DilateMask(pdfMask, width, height, dilationRadius);
            bool[] dilatedBrowserMask = DilateMask(browserMask, width, height, dilationRadius);
            int pdfMisses = CountMisses(pdfMask, dilatedBrowserMask);
            int browserMisses = CountMisses(browserMask, dilatedPdfMask);
            return new ForegroundShapeStats(
                Math.Abs(pdfForeground - browserForeground) / (double)maxForeground,
                pdfMisses / (double)maxForeground,
                browserMisses / (double)maxForeground);
        }

        public static BufferedImage CreateDiffImage(
            BufferedImage pdfPage,
            BufferedImage browserPage,
            int width,
            int height,
            int luminanceThreshold)
        {
            bool[] pdfMask = CreateForegroundMask(pdfPage, width, height, luminanceThreshold);
            bool[] browserMask = CreateForegroundMask(browserPage, width, height, luminanceThreshold);
            BufferedImage diff = new(width, height, BufferedImage.TYPE_INT_ARGB);
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool pdf = pdfMask[index];
                    bool browser = browserMask[index++];
                    int argb = (pdf, browser) switch
                    {
                        (true, true) => unchecked((int)0xFF333333),
                        (true, false) => unchecked((int)0xFFE11D48),
                        (false, true) => unchecked((int)0xFF2563EB),
                        _ => unchecked((int)0xFFFFFFFF)
                    };
                    diff.SetRgb(x, y, argb);
                }
            }

            return diff;
        }

        private static bool[] DilateMask(bool[] mask, int width, int height, int radius)
        {
            if (radius <= 0)
            {
                return (bool[])mask.Clone();
            }

            bool[] dilated = new bool[mask.Length];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (!mask[rowOffset + x])
                    {
                        continue;
                    }

                    int minX = Math.Max(0, x - radius);
                    int maxX = Math.Min(width - 1, x + radius);
                    int minY = Math.Max(0, y - radius);
                    int maxY = Math.Min(height - 1, y + radius);
                    for (int yy = minY; yy <= maxY; yy++)
                    {
                        int offset = yy * width;
                        for (int xx = minX; xx <= maxX; xx++)
                        {
                            dilated[offset + xx] = true;
                        }
                    }
                }
            }

            return dilated;
        }

        private static int CountMisses(bool[] source, bool[] target)
        {
            int misses = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] && !target[i])
                {
                    misses++;
                }
            }

            return misses;
        }
    }

    private sealed class ColorDifferenceStats
    {
        private static readonly double[] LinearRgb = Enumerable.Range(0, 256)
            .Select(static channel =>
            {
                double value = channel / 255d;
                return value <= 0.04045
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4);
            })
            .ToArray();

        private readonly float[] deltas;
        private readonly int width;
        private readonly int height;

        private ColorDifferenceStats(
            int width,
            int height,
            float[] deltas,
            int comparedPixels,
            double meanColorDelta,
            double severeColorDeltaRatio)
        {
            this.width = width;
            this.height = height;
            this.deltas = deltas;
            ComparedPixels = comparedPixels;
            ComparedPixelRatio = comparedPixels / (double)(width * height);
            MeanColorDelta = meanColorDelta;
            SevereColorDeltaRatio = severeColorDeltaRatio;
        }

        public int ComparedPixels { get; }

        public double ComparedPixelRatio { get; }

        public double MeanColorDelta { get; }

        public double SevereColorDeltaRatio { get; }

        public static ColorDifferenceStats? Create(
            BufferedImage pdfPage,
            BufferedImage browserPage,
            int width,
            int height,
            int luminanceThreshold,
            int erosionRadius,
            double severeDeltaThreshold)
        {
            bool[] pdfMask = CreateForegroundMask(pdfPage, width, height, luminanceThreshold);
            bool[] browserMask = CreateForegroundMask(browserPage, width, height, luminanceThreshold);
            bool[] pdfInterior = ErodeMask(pdfMask, width, height, erosionRadius);
            bool[] browserInterior = ErodeMask(browserMask, width, height, erosionRadius);
            float[] deltas = new float[width * height];
            Array.Fill(deltas, float.NaN);

            int comparedPixels = 0;
            int severePixels = 0;
            double totalDelta = 0;
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++, index++)
                {
                    if (!pdfInterior[index] || !browserInterior[index])
                    {
                        continue;
                    }

                    LabColor pdfColor = ToLab(pdfPage.GetRgb(x, y));
                    LabColor browserColor = ToLab(browserPage.GetRgb(x, y));
                    double delta = pdfColor.DistanceTo(browserColor);
                    deltas[index] = (float)delta;
                    totalDelta += delta;
                    comparedPixels++;
                    if (delta >= severeDeltaThreshold)
                    {
                        severePixels++;
                    }
                }
            }

            if (comparedPixels == 0)
            {
                return null;
            }

            return new ColorDifferenceStats(
                width,
                height,
                deltas,
                comparedPixels,
                totalDelta / comparedPixels,
                severePixels / (double)comparedPixels);
        }

        public BufferedImage CreateHeatmap()
        {
            BufferedImage heatmap = new(width, height, BufferedImage.TYPE_INT_ARGB);
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++, index++)
                {
                    float delta = deltas[index];
                    heatmap.SetRgb(
                        x,
                        y,
                        float.IsNaN(delta) ? unchecked((int)0xFFFFFFFF) : HeatmapColor(delta));
                }
            }

            return heatmap;
        }

        private static bool[] ErodeMask(bool[] mask, int width, int height, int radius)
        {
            if (radius <= 0)
            {
                return (bool[])mask.Clone();
            }

            bool[] eroded = new bool[mask.Length];
            for (int y = radius; y < height - radius; y++)
            {
                for (int x = radius; x < width - radius; x++)
                {
                    int index = (y * width) + x;
                    if (!mask[index])
                    {
                        continue;
                    }

                    bool interior = true;
                    for (int yy = y - radius; yy <= y + radius && interior; yy++)
                    {
                        int rowOffset = yy * width;
                        for (int xx = x - radius; xx <= x + radius; xx++)
                        {
                            if (!mask[rowOffset + xx])
                            {
                                interior = false;
                                break;
                            }
                        }
                    }

                    eroded[index] = interior;
                }
            }

            return eroded;
        }

        private static LabColor ToLab(int argb)
        {
            int alpha = (argb >> 24) & 0xFF;
            double red = LinearRgb[CompositeChannelOnWhite((argb >> 16) & 0xFF, alpha)];
            double green = LinearRgb[CompositeChannelOnWhite((argb >> 8) & 0xFF, alpha)];
            double blue = LinearRgb[CompositeChannelOnWhite(argb & 0xFF, alpha)];

            double x = ((red * 0.4124564) + (green * 0.3575761) + (blue * 0.1804375)) / 0.95047;
            double y = (red * 0.2126729) + (green * 0.7151522) + (blue * 0.0721750);
            double z = ((red * 0.0193339) + (green * 0.1191920) + (blue * 0.9503041)) / 1.08883;
            double fx = LabPivot(x);
            double fy = LabPivot(y);
            double fz = LabPivot(z);
            return new LabColor(
                (116 * fy) - 16,
                500 * (fx - fy),
                200 * (fy - fz));
        }

        private static double LabPivot(double value)
        {
            const double epsilon = 216d / 24389d;
            const double kappa = 24389d / 27d;
            return value > epsilon ? Math.Cbrt(value) : ((kappa * value) + 16) / 116;
        }

        private static int HeatmapColor(double delta)
        {
            if (delta < 5)
            {
                return unchecked((int)0xFFD1D5DB);
            }

            if (delta < 20)
            {
                return InterpolateColor(0xFEF08A, 0xF59E0B, (delta - 5) / 15);
            }

            return InterpolateColor(0xF97316, 0x991B1B, Math.Min(1, (delta - 20) / 40));
        }

        private static int InterpolateColor(int startRgb, int endRgb, double amount)
        {
            int red = InterpolateChannel((startRgb >> 16) & 0xFF, (endRgb >> 16) & 0xFF, amount);
            int green = InterpolateChannel((startRgb >> 8) & 0xFF, (endRgb >> 8) & 0xFF, amount);
            int blue = InterpolateChannel(startRgb & 0xFF, endRgb & 0xFF, amount);
            return unchecked((int)(0xFF000000 | (uint)(red << 16) | (uint)(green << 8) | (uint)blue));
        }

        private static int InterpolateChannel(int start, int end, double amount)
        {
            return (int)Math.Round(start + ((end - start) * amount), MidpointRounding.AwayFromZero);
        }

        private readonly record struct LabColor(double L, double A, double B)
        {
            public double DistanceTo(LabColor other)
            {
                double deltaL = L - other.L;
                double deltaA = A - other.A;
                double deltaB = B - other.B;
                return Math.Sqrt((deltaL * deltaL) + (deltaA * deltaA) + (deltaB * deltaB));
            }
        }
    }
}

public sealed record PdfHtmlQualityProbeOptions(
    string SourcePdfPath,
    string HtmlDirectory,
    PdfLayoutDocument Layout,
    string OutputDirectory,
    int MaxPages = 2,
    string? Notes = null);

public sealed record PdfHtmlQualityReport(
    int Schema,
    string Status,
    string SourcePdf,
    string Html,
    string Notes,
    string TextReferenceSource,
    int PagesAnalyzed,
    IReadOnlyList<PdfHtmlQualityIssueCategory> IssueCategories,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<PdfHtmlQualityCheck> Checks,
    IReadOnlyList<PdfHtmlQualityPageReport> Pages,
    IReadOnlyList<string> Artifacts);

public sealed record PdfHtmlQualityIssueCategory(
    string Id,
    string Title,
    string Status,
    string Evidence);

public sealed record PdfHtmlQualityPageReport(
    int PageNumber,
    string Status,
    int SourceWordCount,
    int HtmlWordCount,
    double TextTokenCoverage,
    int LongSourceTokens,
    int LongHtmlTokens,
    int LayoutTextRuns,
    int HtmlTextRuns,
    int LayoutImages,
    int HtmlImages,
    int LayoutVectorPaths,
    int HtmlVectorPaths,
    int TextImageOverlaps,
    int TextVectorOverlaps,
    PdfHtmlVisualMetrics? Visual,
    IReadOnlyList<PdfHtmlQualityCheck> Checks,
    IReadOnlyList<string> Artifacts);

public sealed record PdfHtmlQualityCheck(
    string Id,
    string Category,
    string Status,
    int? PageNumber,
    string Message,
    IReadOnlyDictionary<string, double> Metrics);

public sealed record PdfHtmlVisualMetrics(
    int PdfWidth,
    int PdfHeight,
    int HtmlWidth,
    int HtmlHeight,
    string PdfRenderSource,
    bool PageSizeMatches,
    double? ForegroundDeltaRatio,
    double? PdfMissRatio,
    double? HtmlMissRatio,
    double? MeanColorDelta,
    double? SevereColorDeltaRatio,
    double? ColorComparedPixelRatio);
