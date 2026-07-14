using System.Reflection;
using PdfBox.Net.Html;
using PdfBox.Net.ImageMagick;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.Rendering;

namespace PdfBox.Net.Unpdf;

public static class UnpdfCommand
{
    private static readonly object RenderingRegistrationLock = new();
    private static bool _renderingBackendRegistered;

    private const string Usage = """
        Usage: unpdf <input.pdf> [options]

        Convert a PDF to HTML with image export and rendering fallbacks.

        Options:
          -o, --output <directory>     Output directory. Defaults to <input>-html.
              --force                  Write into a non-empty output directory.
              --text-mode <mode>       semantic (default) or fixed.
              --page-mode <mode>       continuous (default) or fixed.
              --title <title>          HTML document title. Defaults to the input file name.
          -q, --quiet                  Suppress success and warning messages.
          -h, --help                   Show help.
              --version                Show version.

        Capability notes:
          Text, links, vector paths, semantic forms, browser-safe embedded fonts,
          image export, and annotation/transparency raster fallbacks are enabled.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        ParseResult parsed = Parse(args);
        if (parsed.ShowHelp)
        {
            output.WriteLine(Usage);
            return (int)UnpdfExitCode.Success;
        }

        if (parsed.ShowVersion)
        {
            output.WriteLine(GetVersion());
            return (int)UnpdfExitCode.Success;
        }

        if (parsed.Error is not null)
        {
            error.WriteLine($"unpdf: {parsed.Error}");
            error.WriteLine("Run 'unpdf --help' for usage.");
            return (int)UnpdfExitCode.UsageError;
        }

        Options options = parsed.Options!;
        string inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath))
        {
            error.WriteLine($"unpdf: input file does not exist: {inputPath}");
            return (int)UnpdfExitCode.InputError;
        }

        string outputPath = Path.GetFullPath(options.OutputPath ?? GetDefaultOutputPath(inputPath));
        if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any() && !options.Force)
        {
            error.WriteLine($"unpdf: output directory is not empty: {outputPath}");
            error.WriteLine("Use --force to overwrite generated files while preserving unrelated files.");
            return (int)UnpdfExitCode.OutputError;
        }

        PdfLayoutDocument layout;
        PdfHtmlDocument html;
        try
        {
            EnsureRenderingBackendRegistered();
            using PDDocument document = Loader.LoadPDF(inputPath);
            layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
            {
                IncludeAnnotationAppearances = true,
                IncludeFontAssets = true,
                IncludeImageAssets = true,
                IncludeImages = true,
                IncludeTransparencyGroupFallbacks = true
            });
            html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
            {
                SemanticPageMode = options.PageMode,
                TextMode = options.TextMode,
                Title = options.Title ?? Path.GetFileNameWithoutExtension(inputPath)
            });
        }
        catch (IOException exception)
        {
            error.WriteLine($"unpdf: cannot read input: {exception.Message}");
            return (int)UnpdfExitCode.InputError;
        }
        catch (Exception exception)
        {
            error.WriteLine($"unpdf: conversion failed: {exception.Message}");
            return (int)UnpdfExitCode.ConversionError;
        }

        try
        {
            html.WriteToDirectory(outputPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine($"unpdf: cannot write output: {exception.Message}");
            return (int)UnpdfExitCode.OutputError;
        }
        catch (IOException exception)
        {
            error.WriteLine($"unpdf: cannot write output: {exception.Message}");
            return (int)UnpdfExitCode.OutputError;
        }

        if (!options.Quiet)
        {
            foreach (PdfLayoutDiagnostic diagnostic in layout.Diagnostics)
            {
                error.WriteLine($"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code}: {diagnostic.Message}");
            }

            output.WriteLine($"Converted {layout.Pages.Count} page(s) to {outputPath}");
        }

        return (int)UnpdfExitCode.Success;
    }

    private static ParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return ParseResult.Fail("an input PDF is required");
        }

        string? inputPath = null;
        string? outputPath = null;
        string? title = null;
        bool force = false;
        bool quiet = false;
        PdfHtmlTextMode textMode = PdfHtmlTextMode.Semantic;
        PdfHtmlSemanticPageMode pageMode = PdfHtmlSemanticPageMode.ContinuousFlow;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "-h":
                case "--help":
                    return ParseResult.Help();
                case "--version":
                    return ParseResult.Version();
                case "--force":
                    force = true;
                    break;
                case "-q":
                case "--quiet":
                    quiet = true;
                    break;
                case "-o":
                case "--output":
                    if (!TryReadValue(args, ref index, argument, out outputPath, out string? outputError))
                    {
                        return ParseResult.Fail(outputError!);
                    }
                    break;
                case "--title":
                    if (!TryReadValue(args, ref index, argument, out title, out string? titleError))
                    {
                        return ParseResult.Fail(titleError!);
                    }
                    break;
                case "--text-mode":
                    if (!TryReadValue(args, ref index, argument, out string? textModeValue, out string? textModeError))
                    {
                        return ParseResult.Fail(textModeError!);
                    }
                    if (!TryParseTextMode(textModeValue!, out textMode))
                    {
                        return ParseResult.Fail("--text-mode must be 'semantic' or 'fixed'");
                    }
                    break;
                case "--page-mode":
                    if (!TryReadValue(args, ref index, argument, out string? pageModeValue, out string? pageModeError))
                    {
                        return ParseResult.Fail(pageModeError!);
                    }
                    if (!TryParsePageMode(pageModeValue!, out pageMode))
                    {
                        return ParseResult.Fail("--page-mode must be 'continuous' or 'fixed'");
                    }
                    break;
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        return ParseResult.Fail($"unknown option: {argument}");
                    }
                    if (inputPath is not null)
                    {
                        return ParseResult.Fail($"unexpected argument: {argument}");
                    }
                    inputPath = argument;
                    break;
            }
        }

        return inputPath is null
            ? ParseResult.Fail("an input PDF is required")
            : ParseResult.Success(new Options(inputPath, outputPath, title, force, quiet, textMode, pageMode));
    }

    private static void EnsureRenderingBackendRegistered()
    {
        if (_renderingBackendRegistered)
        {
            return;
        }

        lock (RenderingRegistrationLock)
        {
            if (_renderingBackendRegistered)
            {
                return;
            }

            SkiaRenderingBackend.Register();
            PdfBoxNetImageMagick.Register();
            _renderingBackendRegistered = true;
        }
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        index++;
        if (index >= args.Count || args[index].StartsWith("-", StringComparison.Ordinal))
        {
            value = null;
            error = $"{option} requires a value";
            return false;
        }

        value = args[index];
        error = null;
        return true;
    }

    private static bool TryParseTextMode(string value, out PdfHtmlTextMode mode)
    {
        if (string.Equals(value, "semantic", StringComparison.OrdinalIgnoreCase))
        {
            mode = PdfHtmlTextMode.Semantic;
            return true;
        }
        if (string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            mode = PdfHtmlTextMode.FixedLayout;
            return true;
        }

        mode = default;
        return false;
    }

    private static bool TryParsePageMode(string value, out PdfHtmlSemanticPageMode mode)
    {
        if (string.Equals(value, "continuous", StringComparison.OrdinalIgnoreCase))
        {
            mode = PdfHtmlSemanticPageMode.ContinuousFlow;
            return true;
        }
        if (string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            mode = PdfHtmlSemanticPageMode.FixedPages;
            return true;
        }

        mode = default;
        return false;
    }

    private static string GetDefaultOutputPath(string inputPath)
    {
        string directory = Path.GetDirectoryName(inputPath)!;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(inputPath)}-html");
    }

    private static string GetVersion()
    {
        Assembly assembly = typeof(UnpdfCommand).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private sealed record Options(
        string InputPath,
        string? OutputPath,
        string? Title,
        bool Force,
        bool Quiet,
        PdfHtmlTextMode TextMode,
        PdfHtmlSemanticPageMode PageMode);

    private sealed record ParseResult(Options? Options, string? Error, bool ShowHelp, bool ShowVersion)
    {
        public static ParseResult Success(Options options) => new(options, null, false, false);
        public static ParseResult Fail(string error) => new(null, error, false, false);
        public static ParseResult Help() => new(null, null, true, false);
        public static ParseResult Version() => new(null, null, false, true);
    }
}
