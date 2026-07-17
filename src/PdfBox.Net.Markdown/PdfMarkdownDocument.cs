using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfBox.Net.Layout;

namespace PdfBox.Net.Markdown;

/// <summary>
/// Identifies the source used to produce a Markdown result.
/// </summary>
public enum PdfMarkdownOutputSource
{
    Empty,
    SemanticStructure,
    HeuristicFallback,
    Mixed
}

/// <summary>
/// Coarse confidence category for generated Markdown.
/// </summary>
public enum PdfMarkdownConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Severity of a Markdown conversion diagnostic.
/// </summary>
public enum PdfMarkdownDiagnosticSeverity
{
    Information,
    Warning
}

/// <summary>
/// One deterministic Markdown conversion diagnostic.
/// </summary>
public sealed record PdfMarkdownDiagnostic(
    string Code,
    string Message,
    PdfMarkdownDiagnosticSeverity Severity,
    int? PageNumber,
    PdfMarkdownOutputSource Source);

/// <summary>
/// Source and confidence metadata for one converted page.
/// </summary>
public sealed record PdfMarkdownPageResult(
    int PageNumber,
    PdfMarkdownOutputSource Source,
    PdfMarkdownConfidence Confidence);

/// <summary>
/// Generated Markdown together with copied image assets and quality metadata.
/// </summary>
public sealed class PdfMarkdownDocument
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PdfMarkdownDocument(
        string markdown,
        IReadOnlyList<PdfLayoutImageAsset> assets,
        IReadOnlyList<PdfMarkdownDiagnostic> diagnostics,
        IReadOnlyList<PdfMarkdownPageResult> pages)
    {
        Markdown = markdown;
        Assets = assets.ToArray();
        Diagnostics = diagnostics.ToArray();
        Pages = pages.ToArray();
        Source = CombinedSource(Pages.Select(static page => page.Source));
        Confidence = Pages.Count == 0
            ? PdfMarkdownConfidence.Low
            : Pages.Min(static page => page.Confidence);
    }

    public string Markdown { get; }

    public IReadOnlyList<PdfLayoutImageAsset> Assets { get; }

    public IReadOnlyList<PdfMarkdownDiagnostic> Diagnostics { get; }

    public IReadOnlyList<PdfMarkdownPageResult> Pages { get; }

    public PdfMarkdownOutputSource Source { get; }

    public PdfMarkdownConfidence Confidence { get; }

    /// <summary>
    /// Writes <c>document.md</c>, <c>diagnostics.json</c>, and referenced image assets beneath one directory.
    /// </summary>
    public void WriteToDirectory(string directory, string fileName = "document.md")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        WriteContainedFile(root, fileName, Utf8NoBom.GetBytes(Markdown));
        byte[] diagnosticBytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(
            new
            {
                Source,
                Confidence,
                Diagnostics,
                Pages
            },
            JsonOptions) + Environment.NewLine);
        WriteContainedFile(root, "diagnostics.json", diagnosticBytes);
        foreach (PdfLayoutImageAsset asset in Assets)
        {
            WriteContainedFile(root, asset.RelativePath, asset.Data);
        }
    }

    private static void WriteContainedFile(string root, string relativePath, byte[] data)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) &&
            !string.Equals(path, root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Output path escapes the Markdown directory: {relativePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static PdfMarkdownOutputSource CombinedSource(IEnumerable<PdfMarkdownOutputSource> sources)
    {
        PdfMarkdownOutputSource[] materialized = sources
            .Where(static source => source != PdfMarkdownOutputSource.Empty)
            .Distinct()
            .ToArray();
        return materialized.Length switch
        {
            0 => PdfMarkdownOutputSource.Empty,
            1 => materialized[0],
            _ => PdfMarkdownOutputSource.Mixed
        };
    }
}
