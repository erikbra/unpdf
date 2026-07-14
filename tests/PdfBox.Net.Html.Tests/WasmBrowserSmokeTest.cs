using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;

namespace PdfBox.Net.Html.Tests;

public sealed class WasmBrowserSmokeTest
{
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
            conversionRequests.Clear();

            string fixture = Path.Combine(
                repositoryRoot,
                "tests",
                "PdfBox.Net.Tests",
                "Fixtures",
                "classic-xref-fixture.pdf");
            await page.Locator("input[type=file]").SetInputFilesAsync(fixture);

            await page.GetByText("classic-xref-fixture.pdf", new PageGetByTextOptions { Exact = true }).WaitForAsync();
            IFrameLocator preview = page.FrameLocator("iframe[title='Converted HTML preview']");
            await preview.GetByText("Hello", new FrameLocatorGetByTextOptions { Exact = true }).WaitForAsync();

            Assert.Empty(conversionRequests);
            Assert.Equal("1", await page.Locator(".metrics dd").Nth(1).InnerTextAsync());
        }
        finally
        {
            StopServer(server);
        }
    }

    [Fact]
    public void BrowserLite_DependencyGraph_ExcludesRenderingPackages()
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

        Assert.DoesNotContain(libraries, name => name.StartsWith("SkiaSharp/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(libraries, name => name.StartsWith("Magick.NET", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(libraries, name => name.StartsWith("PdfBox.Net.Rendering/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(libraries, name => name.StartsWith("PdfBox.Net.SkiaSharp/", StringComparison.OrdinalIgnoreCase));
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
