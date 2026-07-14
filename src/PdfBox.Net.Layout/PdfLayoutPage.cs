namespace PdfBox.Net.Layout;

/// <summary>
/// Layout data for one PDF page.
/// </summary>
public sealed class PdfLayoutPage
{
    public PdfLayoutPage(
        int pageNumber,
        PdfLayoutRectangle mediaBox,
        PdfLayoutRectangle cropBox,
        float width,
        float height,
        int rotation,
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyList<PdfTextRun> runs,
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfTextBlock> blocks,
        IReadOnlyList<PdfLayoutImage> images,
        IReadOnlyList<PdfLayoutPath> paths,
        IReadOnlyList<PdfLayoutVectorGroup> vectorGroups,
        IReadOnlyList<PdfLayoutLink> links,
        IReadOnlyList<PdfLayoutDiagnostic> diagnostics)
        : this(
            pageNumber,
            mediaBox,
            cropBox,
            width,
            height,
            rotation,
            glyphs,
            runs,
            lines,
            blocks,
            images,
            paths,
            [],
            vectorGroups,
            links,
            diagnostics)
    {
    }

    public PdfLayoutPage(
        int pageNumber,
        PdfLayoutRectangle mediaBox,
        PdfLayoutRectangle cropBox,
        float width,
        float height,
        int rotation,
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyList<PdfTextRun> runs,
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfTextBlock> blocks,
        IReadOnlyList<PdfLayoutImage> images,
        IReadOnlyList<PdfLayoutPath> paths,
        IReadOnlyList<PdfLayoutShading> shadings,
        IReadOnlyList<PdfLayoutVectorGroup> vectorGroups,
        IReadOnlyList<PdfLayoutLink> links,
        IReadOnlyList<PdfLayoutDiagnostic> diagnostics,
        IReadOnlyList<PdfLayoutPaintOperation>? paintOperations = null,
        IReadOnlyList<PdfLayoutFormControl>? formControls = null,
        IReadOnlyList<PdfTextHighlight>? textHighlights = null)
    {
        PageNumber = pageNumber;
        MediaBox = mediaBox;
        CropBox = cropBox;
        Width = width;
        Height = height;
        Rotation = rotation;
        Glyphs = glyphs.ToArray();
        Runs = runs.ToArray();
        Lines = lines.ToArray();
        Blocks = blocks.ToArray();
        Images = images.ToArray();
        Paths = paths.ToArray();
        Shadings = shadings.ToArray();
        VectorGroups = vectorGroups.ToArray();
        Links = links.ToArray();
        Diagnostics = diagnostics.ToArray();
        PaintOperations = paintOperations?.ToArray() ?? [];
        FormControls = formControls?.ToArray() ?? [];
        TextHighlights = textHighlights?.ToArray() ?? [];
    }

    /// <summary>
    /// Gets the one-based page number.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Gets the page media box from the PDF.
    /// </summary>
    public PdfLayoutRectangle MediaBox { get; }

    /// <summary>
    /// Gets the page crop box from the PDF.
    /// </summary>
    public PdfLayoutRectangle CropBox { get; }

    /// <summary>
    /// Gets the normalized visible page width.
    /// </summary>
    public float Width { get; }

    /// <summary>
    /// Gets the normalized visible page height.
    /// </summary>
    public float Height { get; }

    /// <summary>
    /// Gets the page rotation in degrees.
    /// </summary>
    public int Rotation { get; }

    /// <summary>
    /// Gets positioned text glyphs.
    /// </summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }

    /// <summary>
    /// Gets text runs.
    /// </summary>
    public IReadOnlyList<PdfTextRun> Runs { get; }

    /// <summary>
    /// Gets text lines.
    /// </summary>
    public IReadOnlyList<PdfTextLine> Lines { get; }

    /// <summary>
    /// Gets text blocks.
    /// </summary>
    public IReadOnlyList<PdfTextBlock> Blocks { get; }

    /// <summary>
    /// Gets image placements on this page.
    /// </summary>
    public IReadOnlyList<PdfLayoutImage> Images { get; }

    /// <summary>
    /// Gets vector path paint operations on this page.
    /// </summary>
    public IReadOnlyList<PdfLayoutPath> Paths { get; }

    /// <summary>
    /// Gets browser-representable axial and radial shading paint operations on the page.
    /// </summary>
    public IReadOnlyList<PdfLayoutShading> Shadings { get; }

    /// <summary>
    /// Gets transparency groups that retain the compositing hierarchy for vector paths.
    /// </summary>
    public IReadOnlyList<PdfLayoutVectorGroup> VectorGroups { get; }

    /// <summary>
    /// Gets link annotations on this page.
    /// </summary>
    public IReadOnlyList<PdfLayoutLink> Links { get; }

    /// <summary>
    /// Gets diagnostics emitted for this page.
    /// </summary>
    public IReadOnlyList<PdfLayoutDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets image and vector paint operations in PDF content-stream order.
    /// </summary>
    public IReadOnlyList<PdfLayoutPaintOperation> PaintOperations { get; }

    /// <summary>
    /// Gets supported AcroForm widgets represented as semantic controls.
    /// </summary>
    public IReadOnlyList<PdfLayoutFormControl> FormControls { get; }

    /// <summary>
    /// Gets source highlight rectangles confidently matched to text glyphs.
    /// </summary>
    public IReadOnlyList<PdfTextHighlight> TextHighlights { get; }

    /// <summary>
    /// Gets the page text in reading order.
    /// </summary>
    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Text));
}
