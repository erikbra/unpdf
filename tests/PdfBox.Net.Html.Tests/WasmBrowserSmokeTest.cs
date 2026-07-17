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
            page.Request += (_, request) => conversionRequests.Add($"{request.Method} {request.Url}");

            await page.GotoAsync(appUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
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
            Assert.Equal("1", await page.Locator(".metrics dd").Nth(1).InnerTextAsync());
            Assert.Equal(1, await preview.Locator("[data-page-number='1']").CountAsync());
            string applicationDuration = await page.Locator(".metrics dd").Nth(2).InnerTextAsync();
            _output.WriteLine(
                $"Deterministic browser conversion duration: {applicationDuration} " +
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
            page.Request += (_, request) => conversionRequests.Add($"{request.Method} {request.Url}");
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
        ProcessStartInfo startInfo = new("dotnet")
        {
            Arguments = $"run --project samples/PdfBox.Net.Html.Wasm/PdfBox.Net.Html.Wasm.csproj --configuration Release --no-build --no-launch-profile --urls http://127.0.0.1:{port}",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
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
}
