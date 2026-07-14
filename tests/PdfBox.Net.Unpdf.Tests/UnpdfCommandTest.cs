using System.Text.Json;
using PdfBox.Net.Unpdf;

namespace PdfBox.Net.Unpdf.Tests;

public sealed class UnpdfCommandTest
{
    [Fact]
    public void Help_DescribesRenderingCapabilities()
    {
        CommandResult result = Run("--help");

        Assert.Equal(UnpdfExitCode.Success, result.ExitCode);
        Assert.Contains("Usage: unpdf", result.Output);
        Assert.Contains("image export, and annotation/transparency raster fallbacks are enabled", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("--unknown", "unknown option")]
    [InlineData("--output", "requires a value")]
    [InlineData("--text-mode", "requires a value")]
    [InlineData("--text-mode", "other", "must be 'semantic' or 'fixed'")]
    [InlineData("--profile", "rendering", "unknown option")]
    public void InvalidArguments_ReturnUsageError(params string[] argumentsAndExpectedMessage)
    {
        string expectedMessage = argumentsAndExpectedMessage[^1];
        string[] arguments = argumentsAndExpectedMessage[..^1];

        CommandResult result = Run(arguments);

        Assert.Equal(UnpdfExitCode.UsageError, result.ExitCode);
        Assert.Contains(expectedMessage, result.Error);
    }

    [Fact]
    public void MissingInput_ReturnsInputError()
    {
        CommandResult result = Run("missing.pdf");

        Assert.Equal(UnpdfExitCode.InputError, result.ExitCode);
        Assert.Contains("input file does not exist", result.Error);
    }

    [Fact]
    public void Version_IsAvailableWithoutLoadingADocument()
    {
        CommandResult result = Run("--version");

        Assert.Equal(UnpdfExitCode.Success, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public void InvalidPdf_ReturnsInputError()
    {
        using TemporaryDirectory temporary = new();
        string invalidPdf = Path.Combine(temporary.Path, "invalid.pdf");
        File.WriteAllText(invalidPdf, "not a PDF");

        CommandResult result = Run(invalidPdf);

        Assert.Equal(UnpdfExitCode.InputError, result.ExitCode);
        Assert.Contains("cannot read input", result.Error);
    }

    [Fact]
    public void Convert_WritesContinuousSemanticHtmlAndCss()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = Path.Combine(temporary.Path, "html");

        CommandResult result = Run(FixturePath, "--output", outputDirectory, "--title", "CLI fixture");

        Assert.Equal(UnpdfExitCode.Success, result.ExitCode);
        Assert.Contains("Converted 1 page(s)", result.Output);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "index.html")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "assets", "pdfbox-net-fixed.css")));
        string html = File.ReadAllText(Path.Combine(outputDirectory, "index.html"));
        Assert.Contains("<title>CLI fixture</title>", html);
        Assert.Contains("Hello", html);
        Assert.Contains("pdf-semantic-continuous", html);
    }

    [Fact]
    public void Convert_UnnamedType3FontUsesFallbackWithoutAborting()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = Path.Combine(temporary.Path, "html");

        CommandResult result = Run(Type3FixturePath, "--output", outputDirectory);

        Assert.Equal(UnpdfExitCode.Success, result.ExitCode);
        Assert.Contains("Converted 1 page(s)", result.Output);
        Assert.DoesNotContain("conversion failed", result.Error);
        Assert.Contains("embedded-font-web-unsupported", result.Error);
        Assert.Contains("Type 3 font 'Type3'", result.Error);
        string html = File.ReadAllText(Path.Combine(outputDirectory, "index.html"));
        Assert.Contains("Test Markdown that is converted to PDF", html);
    }

    [Fact]
    public void ExistingNonEmptyOutput_RequiresForce()
    {
        using TemporaryDirectory temporary = new();
        string outputDirectory = Path.Combine(temporary.Path, "html");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "keep.txt"), "keep");

        CommandResult rejected = Run(FixturePath, "--output", outputDirectory);
        CommandResult forced = Run(FixturePath, "--output", outputDirectory, "--force", "--quiet");

        Assert.Equal(UnpdfExitCode.OutputError, rejected.ExitCode);
        Assert.Contains("output directory is not empty", rejected.Error);
        Assert.Equal(UnpdfExitCode.Success, forced.ExitCode);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "index.html")));
        Assert.Empty(forced.Output);
        Assert.Empty(forced.Error);
    }

    [Fact]
    public void DependencyGraph_IncludesRenderingPackages()
    {
        string assetsPath = Path.Combine(RepositoryRoot, "apps", "PdfBox.Net.Unpdf", "obj", "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Restore the CLI before running this test: {assetsPath}");

        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        string[] libraries = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(libraries, name => name.StartsWith("SkiaSharp/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(libraries, name => name.StartsWith("Magick.NET", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(libraries, name => name.StartsWith("PdfBox.Net.SkiaSharp/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(libraries, name => name.StartsWith("PdfBox.Net.ImageMagick/", StringComparison.OrdinalIgnoreCase));
    }

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "classic-xref-fixture.pdf");
    private static string Type3FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "unnamed-type3-font-fixture.pdf");
    private static string RepositoryRoot => FindRepositoryRoot();

    private static CommandResult Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = UnpdfCommand.Run(args, output, error);
        return new CommandResult((UnpdfExitCode)exitCode, output.ToString(), error.ToString());
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

    private sealed record CommandResult(UnpdfExitCode ExitCode, string Output, string Error);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unpdf-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
