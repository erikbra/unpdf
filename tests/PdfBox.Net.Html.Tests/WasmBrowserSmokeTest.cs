using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Graphics.Image;

namespace PdfBox.Net.Html.Tests;

public sealed class WasmBrowserSmokeTest
{
    private const string PublishedRootEnvironmentVariable = "UNPDF_WASM_PUBLISHED_ROOT";

    private readonly ITestOutputHelper _output;

    public WasmBrowserSmokeTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserLite_UsesBrowserCompatibilityNormalization()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            Assert.Equal("unpdf - PDF to HTML", await page.TitleAsync());
            Assert.Equal(
                "fi",
                await page.EvaluateAsync<string>("() => window.unpdf.normalizeCompatibility('\\uFB01', 'NFKC')"));
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserLite_UploadedPdf_IsConvertedWithoutNetworkRequests()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            List<string> conversionRequests = [];
            page.Request += (_, request) =>
            {
                if (IsNetworkRequest(request.Url))
                {
                    conversionRequests.Add($"{request.Method} {request.Url}");
                }
            };

            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            WasmPerformanceBaseline performanceBaseline = ReadPerformanceBaseline(repositoryRoot);
            double startupMilliseconds = await page.EvaluateAsync<double>(
                "() => performance.getEntriesByType('navigation')[0].loadEventEnd");
            Assert.InRange(
                startupMilliseconds,
                0.1,
                performanceBaseline.ColdStartup.MaximumLoadMilliseconds);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Try the built-in sample",
                Exact = true
            }).WaitForAsync();
            await page.GetByText(
                "Conversion runs locally in this browser. The PDF is not uploaded.",
                new PageGetByTextOptions { Exact = true }).WaitForAsync();
            conversionRequests.Clear();

            string fixture = Path.Combine(
                repositoryRoot,
                "tests",
                "SharedFixtures",
                "classic-xref-fixture.pdf");
            Stopwatch wallClock = Stopwatch.StartNew();
            await page.Locator("input[type=file]").SetInputFilesAsync(fixture);

            await page.GetByText("classic-xref-fixture.pdf", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            IFrameLocator preview = page.FrameLocator("iframe[title='Converted HTML preview']");
            await preview.GetByText("Hello", new FrameLocatorGetByTextOptions { Exact = true }).WaitForAsync();
            wallClock.Stop();

            Assert.Empty(conversionRequests);
            Assert.True(
                conversionRequests.Count <= performanceBaseline.DeterministicConversion.MaximumNetworkRequests);
            Assert.Equal("1", await page.Locator(".metrics dd").Nth(1).InnerTextAsync());
            Assert.Equal(1, await preview.Locator("[data-page-number='1']").CountAsync());
            string? previewSource = await page.Locator("iframe[title='Converted HTML preview']").GetAttributeAsync("src");
            Assert.StartsWith("blob:", previewSource, StringComparison.Ordinal);
            Assert.Equal(
                1,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));
            int fullInputCopies = int.Parse(
                (await page.Locator("[data-metric='input-copies'] dd").InnerTextAsync())
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(
                fullInputCopies,
                1,
                performanceBaseline.DeterministicConversion.MaximumFullInputCopies);
            long managedHeapGrowth = long.Parse(
                await page.Locator("[data-metric='managed-heap'] dd").GetAttributeAsync("data-growth-bytes")
                    ?? "-1",
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(
                managedHeapGrowth,
                0,
                performanceBaseline.DeterministicConversion.MaximumManagedHeapGrowthBytes);
            long wasmMemoryGrowth = long.Parse(
                await page.Locator("[data-metric='wasm-memory'] dd").GetAttributeAsync("data-growth-bytes")
                    ?? "-1",
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(
                wasmMemoryGrowth,
                0,
                performanceBaseline.DeterministicConversion.MaximumWasmMemoryGrowthBytes);
            string applicationDuration = await page.Locator(".metrics dd").Nth(2).InnerTextAsync();
            long applicationMilliseconds = long.Parse(
                applicationDuration.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(
                applicationMilliseconds,
                0,
                performanceBaseline.DeterministicConversion.MaximumApplicationMilliseconds);
            Assert.InRange(
                wallClock.ElapsedMilliseconds,
                0,
                performanceBaseline.DeterministicConversion.MaximumWallMilliseconds);
            _output.WriteLine(
                $"Cold browser load: {startupMilliseconds:0} ms. " +
                $"Deterministic conversion: {applicationDuration} " +
                $"({wallClock.ElapsedMilliseconds} ms wall clock).");
            _output.WriteLine(
                "Privacy assertion passed: PDF bytes stay in-browser; " +
                "the PDF never leaves the browser during conversion.");
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserShell_EnforcesCompatibleContentSecurityPolicy()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            List<string> policyViolations = [];
            page.Console += (_, message) =>
            {
                if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase) ||
                    message.Text.Contains("Refused to", StringComparison.OrdinalIgnoreCase))
                {
                    policyViolations.Add(message.Text);
                }
            };

            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "PDF to HTML",
                Exact = true
            }).WaitForAsync();

            string policy = await page.Locator("meta#unpdf-csp").GetAttributeAsync("content") ?? string.Empty;
            Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
            Assert.Contains("connect-src 'self'", policy, StringComparison.Ordinal);
            Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
            Assert.Contains("'wasm-unsafe-eval'", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("'unsafe-eval'", policy, StringComparison.Ordinal);
            Assert.Equal(
                "no-referrer",
                await page.Locator("meta#unpdf-referrer").GetAttributeAsync("content"));
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PublishedRootEnvironmentVariable)))
            {
                string scriptPolicy = policy.Split("style-src", StringSplitOptions.TrimEntries)[0];
                Assert.DoesNotContain("'unsafe-inline'", scriptPolicy, StringComparison.Ordinal);
                Assert.Contains("'sha256-", scriptPolicy, StringComparison.Ordinal);
            }
            Assert.Empty(policyViolations);
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserAdaptive_OversizedPdf_IsRejectedBeforeReadingOrSendingContent()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            List<string> conversionRequests = [];
            page.Request += (_, request) =>
            {
                if (IsNetworkRequest(request.Url))
                {
                    conversionRequests.Add($"{request.Method} {request.Url}");
                }
            };
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            conversionRequests.Clear();

            await page.Locator("input[type=file]").EvaluateAsync(
                """
                input => {
                    const oneMiB = new Uint8Array(1024 * 1024);
                    const file = new File(Array(33).fill(oneMiB), "oversized.pdf", { type: "application/pdf" });
                    const transfer = new DataTransfer();
                    transfer.items.add(file);
                    input.files = transfer.files;
                    input.dispatchEvent(new Event("change", { bubbles: true }));
                }
                """);

            await page.GetByText(
                "The browser preview currently accepts PDFs up to 32.0 MB.",
                new PageGetByTextOptions { Exact = true }).WaitForAsync();
            Assert.Empty(conversionRequests);
            Assert.True(await page.Locator("input[type=file]").IsEnabledAsync());
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserAdaptive_CancelledInput_ReleasesTheConversionUi()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            await page.RouteAsync("**/samples/hello.pdf", async route =>
            {
                try
                {
                    await Task.Delay(5_000);
                    await route.ContinueAsync();
                }
                catch (PlaywrightException)
                {
                    // The cancellation aborts the browser request before the delayed route resumes.
                }
            });
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Try the built-in sample",
                Exact = true
            }).ClickAsync();
            ILocator cancel = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Cancel",
                Exact = true
            });
            await cancel.WaitForAsync();
            await cancel.ClickAsync();

            await page.GetByText("Conversion cancelled.", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            Assert.True(await page.Locator("input[type=file]").IsEnabledAsync());
            Assert.True(await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Try the built-in sample",
                Exact = true
            }).IsEnabledAsync());
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserAdaptive_BrowserSafeImagesAreExportedWithoutNetworkRequests()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        string fixture = CreateTinyLosslessImagePdf();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            List<string> conversionRequests = [];
            page.Request += (_, request) =>
            {
                if (IsNetworkRequest(request.Url))
                {
                    conversionRequests.Add($"{request.Method} {request.Url}");
                }
            };
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            conversionRequests.Clear();

            await page.Locator("input[type=file]").SetInputFilesAsync(fixture);

            await page.GetByText("wasm-skia-image.pdf", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            IFrameLocator preview = page.FrameLocator("iframe[title='Converted HTML preview']");
            await preview.Locator("img.pdf-image").First.WaitForAsync(new LocatorWaitForOptions
            {
                Timeout = 90_000
            });

            Assert.Empty(conversionRequests);
            Assert.True(await preview.Locator("img.pdf-image").CountAsync() > 0);
            Assert.Equal(0, await page.Locator(".status.error").CountAsync());
        }
        finally
        {
            StopServer(server);
            Directory.Delete(Path.GetDirectoryName(fixture)!, recursive: true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserAdaptive_PreviewBlobSessionsAreRevokedAndSrcdocFallbackRemainsFunctional()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            string fixture = Path.Combine(
                repositoryRoot,
                "tests",
                "SharedFixtures",
                "classic-xref-fixture.pdf");

            await page.Locator("input[type=file]").SetInputFilesAsync(fixture);
            await page.Locator("[data-metric='preview-store'] dd").WaitForAsync();
            Assert.Equal(
                "revocable Blob URLs",
                await page.Locator("[data-metric='preview-store'] dd").InnerTextAsync());
            Assert.Equal(
                1,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));

            ILocator sampleButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Try the built-in sample",
                Exact = true
            });
            await sampleButton.ClickAsync();
            await page.GetByText("hello.pdf", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            Assert.Equal(
                1,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));

            await page.EvaluateAsync("() => window.unpdf.preview.setBlobSupportOverride(false)");
            await sampleButton.ClickAsync();
            await page.GetByText("srcdoc fallback", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            Assert.Equal(
                0,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));
            string? inlinePreview = await page.Locator("iframe[title='Converted HTML preview']")
                .GetAttributeAsync("srcdoc");
            Assert.False(string.IsNullOrWhiteSpace(inlinePreview));
            await page.FrameLocator("iframe[title='Converted HTML preview']")
                .GetByText("Hello", new FrameLocatorGetByTextOptions { Exact = true })
                .WaitForAsync();
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserAdaptive_TruncatedInputReportsErrorAndReleasesPreviewResources()
    {
        string repositoryRoot = FindRepositoryRoot();
        int port = ReservePort();
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string truncatedFixture = Path.Combine(temporaryDirectory, "truncated.pdf");
        Directory.CreateDirectory(temporaryDirectory);
        byte[] validInput = File.ReadAllBytes(Path.Combine(
            repositoryRoot,
            "tests",
            "SharedFixtures",
            "classic-xref-fixture.pdf"));
        File.WriteAllBytes(truncatedFixture, validInput[..Math.Min(64, validInput.Length)]);
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            string validFixture = Path.Combine(
                repositoryRoot,
                "tests",
                "SharedFixtures",
                "classic-xref-fixture.pdf");
            await page.Locator("input[type=file]").SetInputFilesAsync(validFixture);
            await page.Locator("[data-metric='preview-store'] dd").WaitForAsync();
            Assert.Equal(
                1,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));

            await page.Locator("input[type=file]").SetInputFilesAsync(truncatedFixture);
            await page.Locator(".status.error").WaitForAsync();

            Assert.Contains(
                "Conversion failed:",
                await page.Locator(".status.error").InnerTextAsync(),
                StringComparison.Ordinal);
            Assert.Equal(
                0,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));
            Assert.True(await page.Locator("input[type=file]").IsEnabledAsync());
            Assert.Equal(0, await page.Locator("iframe[title='Converted HTML preview']").CountAsync());
        }
        finally
        {
            StopServer(server);
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task BrowserAdaptive_MultiPageFixtureReportsFirstPageAndKeepsInputCopiesBounded()
    {
        string repositoryRoot = FindRepositoryRoot();
        WasmPerformanceBaseline performanceBaseline = ReadPerformanceBaseline(repositoryRoot);
        int port = ReservePort();
        string fixture = CreateRepeatedPagePdf(pageCount: 24);
        using Process server = StartServer(repositoryRoot, port);

        try
        {
            Uri appUri = new($"http://127.0.0.1:{port}");
            await WaitForServerAsync(appUri, server);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            IPage page = await browser.NewPageAsync();
            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

            await page.Locator("input[type=file]").SetInputFilesAsync(fixture);
            await page.GetByText("wasm-many-pages.pdf", new PageGetByTextOptions { Exact = true }).WaitForAsync();

            Assert.Equal("24", await page.Locator("[data-metric='pages'] dd").InnerTextAsync());
            Assert.Equal(
                24,
                await page.FrameLocator("iframe[title='Converted HTML preview']")
                    .Locator("[data-page-number]")
                    .CountAsync());
            int fullInputCopies = int.Parse(
                (await page.Locator("[data-metric='input-copies'] dd").InnerTextAsync())
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(performanceBaseline.DeterministicConversion.MaximumFullInputCopies, fullInputCopies);
            long firstPageMilliseconds = MetricMilliseconds(
                await page.Locator("[data-metric='first-page'] dd").InnerTextAsync());
            long totalMilliseconds = MetricMilliseconds(
                await page.Locator("[data-metric='time'] dd").InnerTextAsync());
            Assert.InRange(firstPageMilliseconds, 1, totalMilliseconds);
            long managedHeapGrowth = long.Parse(
                await page.Locator("[data-metric='managed-heap'] dd").GetAttributeAsync("data-growth-bytes")
                    ?? "-1",
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(
                managedHeapGrowth,
                0,
                performanceBaseline.DeterministicConversion.MaximumManagedHeapGrowthBytes);
            Assert.Equal(
                1,
                await page.EvaluateAsync<int>("() => window.unpdf.preview.activeSessionCount()"));
        }
        finally
        {
            StopServer(server);
            File.Delete(fixture);
        }
    }

    [Fact]
    public void BrowserAdaptive_DependencyGraph_IncludesSkiaButExcludesImageMagick()
    {
        string repositoryRoot = FindRepositoryRoot();
        string assetsPath = Path.Combine(
            repositoryRoot,
            "samples",
            "PdfBox.Net.Html.Wasm",
            "obj",
            "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Restore the WASM sample before running this test: {assetsPath}");

        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        string[] libraries = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(libraries, name => name.StartsWith("SkiaSharp/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(libraries, name => name.StartsWith("SkiaSharp.NativeAssets.WebAssembly/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(libraries, name => name.StartsWith("Magick.NET", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(libraries, name => name.StartsWith("PdfBox.Net.SkiaSharp/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(libraries, name => name.StartsWith("PdfBox.Net.ImageMagick/", StringComparison.OrdinalIgnoreCase));
    }

    private static Process StartServer(string repositoryRoot, int port)
    {
        string? publishedRoot = Environment.GetEnvironmentVariable(PublishedRootEnvironmentVariable);
        ProcessStartInfo startInfo;
        if (!string.IsNullOrWhiteSpace(publishedRoot))
        {
            startInfo = new ProcessStartInfo("python3")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot
            };
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("http.server");
            startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--bind");
            startInfo.ArgumentList.Add("127.0.0.1");
            startInfo.ArgumentList.Add("--directory");
            startInfo.ArgumentList.Add(Path.GetFullPath(publishedRoot));
        }
        else
        {
            startInfo = new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --project samples/PdfBox.Net.Html.Wasm/PdfBox.Net.Html.Wasm.csproj --configuration Release --no-build --no-launch-profile --urls http://127.0.0.1:{port}",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot
            };
        }
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the WASM development server.");
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    private static string CreateTinyLosslessImagePdf()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "wasm-skia-image.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDImageXObject image = LosslessFactory.CreateFromRawData(
            document,
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255],
            2,
            2,
            8,
            3);
        using (PDPageContentStream content = new(document, page))
        {
            content.DrawImage(image, 72, 600, 120, 60);
        }
        document.Save(path);
        return path;
    }

    private static string CreateRepeatedPagePdf(int pageCount)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "wasm-many-pages.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using PDDocument document = new();
        PDImageXObject image = LosslessFactory.CreateFromRawData(
            document,
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255],
            2,
            2,
            8,
            3);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            PDPage page = new();
            document.AddPage(page);
            using PDPageContentStream content = new(document, page);
            content.DrawImage(image, 72, 600, 120, 60);
        }

        document.Save(path);
        return path;
    }

    private static long MetricMilliseconds(string value) =>
        long.Parse(
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task WaitForServerAsync(Uri uri, Process server)
    {
        using HttpClient client = new();
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException($"The WASM development server exited with code {server.ExitCode}.");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(uri);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"The WASM development server did not start at {uri}.");
    }

    private static int ReservePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsNetworkRequest(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void StopServer(Process server)
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            server.WaitForExit(5_000);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Unpdf.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the unpdf repository root.");
    }

    private static WasmPerformanceBaseline ReadPerformanceBaseline(string repositoryRoot)
    {
        string path = Path.Combine(repositoryRoot, "eng", "wasm-performance-baseline.json");
        return JsonSerializer.Deserialize<WasmPerformanceBaseline>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Could not parse {path}.");
    }

    private sealed record WasmPerformanceBaseline(
        ColdStartupBudget ColdStartup,
        DeterministicConversionBudget DeterministicConversion);

    private sealed record ColdStartupBudget(double MaximumLoadMilliseconds);

    private sealed record DeterministicConversionBudget(
        long MaximumApplicationMilliseconds,
        int MaximumFullInputCopies,
        long MaximumManagedHeapGrowthBytes,
        int MaximumNetworkRequests,
        long MaximumWallMilliseconds,
        long MaximumWasmMemoryGrowthBytes);
}
