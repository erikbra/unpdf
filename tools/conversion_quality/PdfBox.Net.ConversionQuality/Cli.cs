namespace PdfBox.Net.ConversionQuality;

public static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine(
                "Usage: PdfBox.Net.ConversionQuality --manifest <path> --out-dir <path>\n" +
                "   or: PdfBox.Net.ConversionQuality --markdown-quality-out <path>");
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            Dictionary<string, string> options = ParseOptions(args);
            if (options.TryGetValue("--markdown-quality-out", out string? markdownOut) &&
                !string.IsNullOrWhiteSpace(markdownOut))
            {
                IReadOnlyList<MarkdownQualityFixtureResult> fixtures =
                    MarkdownQualityFixtureGenerator.Generate(markdownOut);
                Console.WriteLine(
                    $"Wrote {fixtures.Count} Markdown quality fixture(s) to {Path.GetFullPath(markdownOut)}");
                foreach (MarkdownQualityFixtureResult fixture in fixtures)
                {
                    Console.WriteLine($"- {fixture.Id}: {fixture.Directory}");
                }

                return 0;
            }

            string manifestPath = Required(options, "--manifest");
            string outDir = Required(options, "--out-dir");
            HtmlReviewArtifactResult result = HtmlReviewArtifactGenerator.Generate(manifestPath, outDir);

            Console.WriteLine($"Wrote {result.Examples.Count} HTML review artifact(s) to {result.OutputDirectory}");
            foreach (HtmlReviewExampleResult example in result.Examples)
            {
                Console.WriteLine($"- {example.Id}: {example.Directory}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{name}'.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{name}' needs a value.");
            }

            options[name] = args[++index];
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name)
    {
        return options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option '{name}'.");
    }
}
