using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PdfBox.Net.ContentStream;
using PdfBox.Net.COS;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.Common.Function;
using PdfBox.Net.PDModel.Graphics;
using PdfBox.Net.PDModel.Graphics.Color;
using PdfBox.Net.PDModel.Graphics.Form;
using PdfBox.Net.PDModel.Graphics.Image;
using PdfBox.Net.PDModel.Graphics.State;
using PdfBox.Net.PDModel.Graphics.Shading;
using PdfBox.Net.PDModel.Interactive.Action;
using PdfBox.Net.PDModel.Interactive.Annotation;
using PdfBox.Net.PDModel.Interactive.DigitalSignature;
using PdfBox.Net.PDModel.Interactive.Form;
using PdfBox.Net.PDModel.Interactive.DocumentNavigation.Destination;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Resources;
using PdfBox.Net.Rendering;
using PdfBox.Net.Text;
using PdfBox.Net.Util;
using PdfBox.Net.Util.Geometry;
using ContentOperator = PdfBox.Net.ContentStream.Operator.Operator;

namespace PdfBox.Net.Layout;

/// <summary>
/// Extracts a shared page layout model from PDF documents.
/// </summary>
public static class PdfLayoutExtractor
{
    private readonly record struct MarkedContentCapture(int? MarkedContentId, bool IsArtifact);

    private static MarkedContentCapture CreateMarkedContentCapture(
        MarkedContentCapture parent,
        string operatorName,
        IList<COSBase> operands,
        PDResources? resources)
    {
        COSName? tag = operands.FirstOrDefault() as COSName;
        bool isArtifact = parent.IsArtifact ||
            string.Equals(tag?.GetName(), "Artifact", StringComparison.Ordinal);
        COSDictionary? properties = null;
        if (string.Equals(operatorName, "BDC", StringComparison.Ordinal) && operands.Count > 1)
        {
            properties = operands[1] switch
            {
                COSDictionary dictionary => dictionary,
                COSName propertyName => resources?.GetProperties(propertyName)?.GetCOSObject(),
                _ => null
            };
        }

        int markedContentId = properties?.GetInt(COSName.GetPDFName("MCID"), -1) ?? -1;
        return new MarkedContentCapture(
            isArtifact ? null : markedContentId >= 0 ? markedContentId : parent.MarkedContentId,
            isArtifact);
    }

    /// <summary>
    /// Extracts page geometry and positioned content from a PDF document.
    /// </summary>
    /// <param name="document">The PDF document.</param>
    /// <param name="options">Extraction options.</param>
    /// <returns>The extracted layout document.</returns>
    public static PdfLayoutDocument Extract(PDDocument document, PdfLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        options ??= new PdfLayoutOptions();
        LayoutTextStripper stripper = new(options);
        using StringWriter output = new(CultureInfo.InvariantCulture);
        stripper.WriteText(document, output);
        return stripper.CreateDocument();
    }

    private sealed class LayoutTextStripper : PDFTextStripper
    {
        private readonly PdfLayoutOptions _options;
        private readonly List<PageBuilder> _pages = new();
        private readonly List<PdfLayoutDiagnostic> _diagnostics = new();
        private readonly Dictionary<TextPosition, PdfLayoutColor> _textColors = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<TextPosition, bool> _textPaintStates = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<TextPosition, int?> _textMarkedContentIds = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<MarkedContentCapture> _markedContent = new();
        private readonly ImageAssetCollector _imageAssets = new();
        private readonly EmbeddedFontAssetCollector _fontAssets;
        private PDColorManagementContext? _colorManagementContext;
        private PageBuilder? _currentPage;
        private PDDocument? _document;

        public LayoutTextStripper(PdfLayoutOptions options)
        {
            _options = options;
            _fontAssets = new EmbeddedFontAssetCollector(options.IncludeFontAssets, _diagnostics);
            SetSortByPosition(options.SortTextByPosition);
            SetSuppressDuplicateOverlappingText(options.SuppressDuplicateOverlappingText);
            SetShouldSeparateByBeads(options.SeparateByBeads);
        }

        protected override void StartDocument(PDDocument document)
        {
            _pages.Clear();
            _diagnostics.Clear();
            _textColors.Clear();
            _textPaintStates.Clear();
            _textMarkedContentIds.Clear();
            _markedContent.Clear();
            _imageAssets.Clear();
            _fontAssets.Clear();
            _colorManagementContext = PDColorManagementContext.Create(document);
            _currentPage = null;
            _document = document;

            PDAcroForm? acroForm = document.GetDocumentCatalog().GetAcroForm(null);
            if (acroForm?.GetXFA() != null)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "xfa-semantic-forms-unsupported",
                    "XFA packets are not interpreted as semantic controls; visual page and annotation appearances are retained as fallback."));
            }

            int pageNumber = 1;
            int pageIndex = 0;
            foreach (PDPage page in document.GetPages())
            {
                _pages.Add(new PageBuilder(
                    pageNumber,
                    pageIndex,
                    page,
                    document,
                    _options,
                    _imageAssets,
                    _colorManagementContext,
                    acroForm));
                pageNumber++;
                pageIndex++;
            }
        }

        protected override void StartPage(PDPage page)
        {
            _markedContent.Clear();
            int pageNumber = GetCurrentPageNo();
            _currentPage = pageNumber >= 1 && pageNumber <= _pages.Count
                ? _pages[pageNumber - 1]
                : null;
        }

        protected override void WritePage()
        {
            if (!_options.IncludeText || _currentPage == null)
            {
                return;
            }

            List<TextPosition> textPositions = GetCharactersByArticle()
                .SelectMany(article => article)
                .ToList();

            if (_options.SortTextByPosition)
            {
                textPositions.Sort(new TextPositionComparator());
            }

            _currentPage.SetTextPositions(
                textPositions,
                _options,
                _textColors,
                _textPaintStates,
                _textMarkedContentIds,
                _fontAssets);
        }

        protected override void ProcessOperator(ContentOperator op, IList<COSBase> operands)
        {
            bool isEnd = string.Equals(op.GetName(), "EMC", StringComparison.Ordinal);
            if (string.Equals(op.GetName(), "BMC", StringComparison.Ordinal) ||
                string.Equals(op.GetName(), "BDC", StringComparison.Ordinal))
            {
                _markedContent.Push(CreateMarkedContentCapture(
                    _markedContent.TryPeek(out MarkedContentCapture parent) ? parent : default,
                    op.GetName(),
                    operands,
                    GetResources()));
            }

            base.ProcessOperator(op, operands);

            if (isEnd && _markedContent.Count > 0)
            {
                _markedContent.Pop();
            }
        }

        protected override void ProcessTextPosition(TextPosition text)
        {
            RenderingMode renderingMode = GetGraphicsState().GetTextState().GetRenderingModeInstance();
            _textPaintStates[text] = renderingMode.IsFill() || renderingMode.IsStroke();
            _textColors[text] = ResolveGraphicsColor(
                GetGraphicsState().GetNonStrokingColor(),
                GetGraphicsState().GetNonStrokeAlphaConstant(),
                GetCurrentPageNo(),
                _diagnostics,
                "text",
                _colorManagementContext);
            _textMarkedContentIds[text] = _markedContent.TryPeek(out MarkedContentCapture capture) && !capture.IsArtifact
                ? capture.MarkedContentId
                : null;
            base.ProcessTextPosition(text);
        }

        protected override void EndPage(PDPage page)
        {
            _markedContent.Clear();
            _currentPage = null;
        }

        public PdfLayoutDocument CreateDocument()
        {
            PdfLayoutPage[] pages = _pages.Select(page => page.Build()).ToArray();
            PdfLayoutImageAsset[] imageAssets = _imageAssets.Assets.ToArray();
            PdfLayoutFontAsset[] fontAssets = _fontAssets.Assets.ToArray();
            (PdfTaggedStructureDocument? taggedStructure, IReadOnlyList<PdfLayoutDiagnostic> taggedDiagnostics) =
                _document == null
                    ? (null, [])
                    : PdfTaggedStructureExtractor.Extract(_document, pages);
            PdfLayoutDiagnostic[] diagnostics = _diagnostics
                .Concat(pages.SelectMany(page => page.Diagnostics))
                .Concat(taggedDiagnostics)
                .ToArray();
            return new PdfLayoutDocument(pages, imageAssets, fontAssets, diagnostics, taggedStructure);
        }
    }

    private sealed class ImageAssetCollector
    {
        private readonly Dictionary<string, PdfLayoutImageAsset> _assetsByHash = new(StringComparer.Ordinal);

        public IReadOnlyList<PdfLayoutImageAsset> Assets => _assetsByHash.Values
            .OrderBy(static asset => asset.AssetId, StringComparer.Ordinal)
            .ToArray();

        public void Clear() => _assetsByHash.Clear();

        public PdfLayoutImageAsset Add(
            string preferredAssetId,
            string fileExtension,
            string contentType,
            byte[] data,
            PdfLayoutColor? uniformColor = null)
        {
            string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (_assetsByHash.TryGetValue(hash, out PdfLayoutImageAsset? existing))
            {
                if (existing.UniformColor is null && uniformColor is not null)
                {
                    existing = new PdfLayoutImageAsset(
                        existing.AssetId,
                        existing.RelativePath,
                        existing.ContentType,
                        existing.Data,
                        uniformColor);
                    _assetsByHash[hash] = existing;
                }

                return existing;
            }

            PdfLayoutImageAsset asset = new(
                preferredAssetId,
                $"assets/images/{preferredAssetId}.{fileExtension}",
                contentType,
                data,
                uniformColor);
            _assetsByHash.Add(hash, asset);
            return asset;
        }
    }

    private sealed class EmbeddedFontAssetCollector
    {
        private static readonly COSName FontFileKey = COSName.GetPDFName("FontFile");
        private static readonly COSName FontFile2Key = COSName.GetPDFName("FontFile2");
        private static readonly COSName FontFile3Key = COSName.GetPDFName("FontFile3");
        private static readonly COSName SubtypeKey = COSName.GetPDFName("Subtype");

        private readonly bool _includeAssets;
        private readonly List<PdfLayoutDiagnostic> _diagnostics;
        private readonly Dictionary<string, FontAssetBuilder> _assetsByHash = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _assetHashByFontName = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reportedUnsupportedFonts = new(StringComparer.Ordinal);

        public EmbeddedFontAssetCollector(bool includeAssets, List<PdfLayoutDiagnostic> diagnostics)
        {
            _includeAssets = includeAssets;
            _diagnostics = diagnostics;
        }

        public IReadOnlyList<PdfLayoutFontAsset> Assets => _assetsByHash.Values
            .OrderBy(static asset => asset.AssetId, StringComparer.Ordinal)
            .Select(static asset => asset.Build())
            .ToArray();

        public void Clear()
        {
            _assetsByHash.Clear();
            _assetHashByFontName.Clear();
            _reportedUnsupportedFonts.Clear();
        }

        public bool Collect(PDFont font, int pageNumber)
        {
            if (!_includeAssets)
            {
                return false;
            }

            string fontName = font.GetName();
            if (font is PDType3Font)
            {
                if (_reportedUnsupportedFonts.Add(fontName))
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "embedded-font-web-unsupported",
                        $"Type 3 font '{fontName}' uses PDF character procedures and cannot be emitted as CSS @font-face; fallback text is used.",
                        pageNumber));
                }

                return false;
            }

            if (string.IsNullOrWhiteSpace(fontName))
            {
                return false;
            }

            if (_assetHashByFontName.ContainsKey(fontName))
            {
                return true;
            }

            if (!TryReadBrowserFontProgram(font, out FontProgram program, out string? unsupportedReason))
            {
                if (!string.IsNullOrWhiteSpace(unsupportedReason) && _reportedUnsupportedFonts.Add(fontName))
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "embedded-font-web-unsupported",
                        $"Embedded font '{fontName}' is not emitted as CSS: {unsupportedReason}",
                        pageNumber));
                }

                return false;
            }

            AddProgram(fontName, program);
            return true;
        }

        private void AddProgram(string fontName, FontProgram program)
        {
            string hash = Convert.ToHexString(SHA256.HashData(program.Data)).ToLowerInvariant();
            string assetId = "pdf-font-" + hash[..16];
            if (!_assetsByHash.TryGetValue(hash, out FontAssetBuilder? asset))
            {
                string extension = program.CssFormat == "opentype" ? "otf" : "ttf";
                asset = new FontAssetBuilder(
                    assetId,
                    $"assets/fonts/{assetId}.{extension}",
                    program.ContentType,
                    program.CssFormat,
                    program.Data,
                    program.CssFontStyle,
                    program.CssFontWeight);
                _assetsByHash.Add(hash, asset);
            }

            asset.AddFontName(fontName);
            _assetHashByFontName[fontName] = hash;
        }

        private static bool TryReadBrowserFontProgram(PDFont font, out FontProgram program, out string? unsupportedReason)
        {
            program = default;
            unsupportedReason = null;
            PDFontDescriptor? descriptor = font.GetFontDescriptor();
            COSDictionary? descriptorDictionary = descriptor?.GetCOSObject();
            if (descriptor is null || descriptorDictionary is null)
            {
                return false;
            }

            string cssFontStyle = CssFontStyle(font.GetName(), descriptor);
            int cssFontWeight = CssFontWeight(font.GetName(), descriptor);

            if (descriptorDictionary.GetDictionaryObject(FontFile2Key) is COSStream trueTypeStream)
            {
                byte[] data = ReadStream(trueTypeStream);
                if (TryGetSfntFormat(data, out string contentType, out string cssFormat))
                {
                    if (SfntWebFontNormalizer.TryNormalize(
                            data,
                            out byte[] normalizedData,
                            out string? failureReason))
                    {
                        program = new FontProgram(
                            normalizedData,
                            contentType,
                            cssFormat,
                            cssFontStyle,
                            cssFontWeight);
                        return true;
                    }

                    unsupportedReason = $"FontFile2 cannot be normalized for browser use: {failureReason}";
                    return false;
                }

                unsupportedReason = "FontFile2 does not contain a browser-readable sfnt program.";
                return false;
            }

            if (descriptorDictionary.GetDictionaryObject(FontFile3Key) is COSStream fontFile3)
            {
                string? subtype = fontFile3.GetNameAsString(SubtypeKey);
                if (string.Equals(subtype, "OpenType", StringComparison.Ordinal))
                {
                    byte[] data = ReadStream(fontFile3);
                    if (TryGetSfntFormat(data, out string contentType, out string cssFormat))
                    {
                        if (SfntWebFontNormalizer.TryNormalize(
                                data,
                                out byte[] normalizedData,
                                out string? failureReason))
                        {
                            program = new FontProgram(
                                normalizedData,
                                contentType,
                                cssFormat,
                                cssFontStyle,
                                cssFontWeight);
                            return true;
                        }

                        unsupportedReason = $"OpenType FontFile3 cannot be normalized for browser use: {failureReason}";
                        return false;
                    }

                    unsupportedReason = "OpenType FontFile3 does not contain a browser-readable sfnt program.";
                    return false;
                }

                unsupportedReason = $"FontFile3 subtype '{subtype ?? "unknown"}' is not a browser-readable sfnt program.";
                return false;
            }

            if (descriptorDictionary.GetDictionaryObject(FontFileKey) is COSStream)
            {
                unsupportedReason = "Type 1 FontFile programs are not supported by browser @font-face.";
            }

            return false;
        }

        private static string CssFontStyle(string fontName, PDFontDescriptor descriptor)
        {
            return descriptor.IsItalic() || MathF.Abs(descriptor.GetItalicAngle()) > 0.01f ||
                   fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
                   fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase)
                ? "italic"
                : "normal";
        }

        private static int CssFontWeight(string fontName, PDFontDescriptor descriptor)
        {
            float descriptorWeight = descriptor.GetFontWeight();
            if (descriptorWeight > 0)
            {
                return Math.Clamp((int)MathF.Round(descriptorWeight / 100f) * 100, 100, 900);
            }

            return descriptor.IsForceBold() || fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                   fontName.Contains("Black", StringComparison.OrdinalIgnoreCase)
                ? 700
                : 400;
        }

        private static byte[] ReadStream(COSStream stream)
        {
            using Stream input = stream.CreateInputStream();
            using MemoryStream output = new();
            input.CopyTo(output);
            return output.ToArray();
        }

        private static bool TryGetSfntFormat(byte[] data, out string contentType, out string cssFormat)
        {
            contentType = string.Empty;
            cssFormat = string.Empty;
            if (data.Length < 4)
            {
                return false;
            }

            ReadOnlySpan<byte> signature = data.AsSpan(0, 4);
            if (signature.SequenceEqual("OTTO"u8))
            {
                contentType = "font/otf";
                cssFormat = "opentype";
                return true;
            }

            if (signature.SequenceEqual("\0\x01\0\0"u8) || signature.SequenceEqual("true"u8))
            {
                contentType = "font/ttf";
                cssFormat = "truetype";
                return true;
            }

            return false;
        }

        private readonly record struct FontProgram(
            byte[] Data,
            string ContentType,
            string CssFormat,
            string CssFontStyle,
            int CssFontWeight);

        private sealed class FontAssetBuilder
        {
            private readonly HashSet<string> _fontNames = new(StringComparer.Ordinal);

            public FontAssetBuilder(
                string assetId,
                string relativePath,
                string contentType,
                string cssFormat,
                byte[] data,
                string cssFontStyle,
                int cssFontWeight)
            {
                AssetId = assetId;
                RelativePath = relativePath;
                ContentType = contentType;
                CssFormat = cssFormat;
                Data = data;
                CssFontStyle = cssFontStyle;
                CssFontWeight = cssFontWeight;
            }

            public string AssetId { get; }

            public string RelativePath { get; }

            public string ContentType { get; }

            public string CssFormat { get; }

            public byte[] Data { get; }

            public string CssFontStyle { get; }

            public int CssFontWeight { get; }

            public void AddFontName(string fontName) => _fontNames.Add(fontName);

            public PdfLayoutFontAsset Build() => new(
                AssetId,
                _fontNames.Order(StringComparer.Ordinal).ToArray(),
                RelativePath,
                ContentType,
                CssFormat,
                Data,
                CssFontStyle,
                CssFontWeight);
        }
    }

    private sealed record SoftMaskedTransparencyGroup(PdfLayoutRectangle Bounds, PdfLayoutColor Color);

    private sealed class PageBuilder
    {
        private sealed record TransparencyGroupRenderBatch(
            PdfLayoutRectangle Bounds,
            IReadOnlyList<int> FallbackIndexes);

        private readonly record struct ScaledImageRegion(int Left, int Top, int Right, int Bottom)
        {
            public int Width => Right - Left;

            public int Height => Bottom - Top;
        }

        private readonly int _pageNumber;
        private readonly int _pageIndex;
        private readonly PDDocument _document;
        private readonly PDColorManagementContext? _colorManagementContext;
        private readonly PdfLayoutRectangle _mediaBox;
        private readonly PdfLayoutRectangle _cropBox;
        private readonly float _width;
        private readonly float _height;
        private readonly int _rotation;
        private readonly PdfImageExportPolicy _imageExportPolicy;
        private readonly List<PdfTextGlyph> _glyphs = new();
        private readonly List<PdfTextRun> _runs = new();
        private readonly List<PdfTextLine> _lines = new();
        private readonly List<PdfTextBlock> _blocks = new();
        private readonly List<PdfLayoutImage> _images = new();
        private readonly ImageAssetCollector _imageAssets;
        private readonly List<PdfLayoutPath> _paths = new();
        private readonly List<PdfLayoutShading> _shadings = new();
        private readonly List<PdfLayoutVectorGroup> _vectorGroups = new();
        private readonly List<PdfLayoutLink> _links = new();
        private readonly List<PdfLayoutFormControl> _formControls = new();
        private readonly List<PdfTextHighlight> _textHighlights = new();
        private readonly List<PdfLayoutDiagnostic> _diagnostics = new();
        private readonly List<PdfLayoutPaintOperation> _paintOperations = new();
        private readonly List<SoftMaskedTransparencyGroup> _softMaskedTransparencyGroups = new();
        private readonly List<PdfLayoutRectangle> _transparencyGroupFallbackBounds = new();
        private readonly List<PdfLayoutRectangle> _softMaskedTransparencyGroupFallbackBounds = new();
        private readonly HashSet<PdfLayoutRectangle> _representedSoftMaskedTransparencyGroupBounds = [];
        private bool _transparencyGroupFallbacksCollected;

        private const float AnnotationAppearanceScale = 2f;
        private const float TransparencyGroupRasterScale = 3f;
        // Model one page-content replay as 10% of a full-page raster. Nearby regions are batched
        // only when the saved replay is likely to cost more than rendering the union's empty pixels.
        private const float TransparencyGroupRenderReplayCostRatio = 0.1f;

        public PageBuilder(
            int pageNumber,
            int pageIndex,
            PDPage page,
            PDDocument document,
            PdfLayoutOptions options,
            ImageAssetCollector imageAssets,
            PDColorManagementContext? colorManagementContext,
            PDAcroForm? acroForm)
        {
            _pageNumber = pageNumber;
            _pageIndex = pageIndex;
            _document = document;
            _imageAssets = imageAssets;
            _colorManagementContext = colorManagementContext;
            PDRectangle mediaBox = page.GetMediaBox();
            PDRectangle cropBox = page.GetCropBox();
            _mediaBox = PdfLayoutRectangle.FromPdfRectangle(mediaBox);
            _cropBox = PdfLayoutRectangle.FromPdfRectangle(cropBox);
            _rotation = page.GetRotation();
            _imageExportPolicy = options.ImageExportPolicy;
            bool rotated = _rotation == 90 || _rotation == 270;
            _width = rotated ? cropBox.GetHeight() : cropBox.GetWidth();
            _height = rotated ? cropBox.GetWidth() : cropBox.GetHeight();

            if (options.IncludeLinks)
            {
                CollectLinks(page);
            }

            if (options.IncludeFormControls && acroForm != null)
            {
                CollectFormControls(page, acroForm);
            }

            if (options.IncludeImages || options.IncludePaths)
            {
                CollectGraphics(page, options);
            }

            if (options.IncludeAnnotationAppearances && options.IncludeImageAssets)
            {
                CollectAnnotationAppearances(document, pageIndex, page);
            }
        }

        public void SetTextPositions(
            IReadOnlyList<TextPosition> textPositions,
            PdfLayoutOptions options,
            IReadOnlyDictionary<TextPosition, PdfLayoutColor> textColors,
            IReadOnlyDictionary<TextPosition, bool> textPaintStates,
            IReadOnlyDictionary<TextPosition, int?> textMarkedContentIds,
            EmbeddedFontAssetCollector fontAssets)
        {
            _glyphs.Clear();
            _runs.Clear();
            _lines.Clear();
            _blocks.Clear();
            _textHighlights.Clear();

            _glyphs.AddRange(textPositions.Select(position => CreateGlyph(
                position,
                textColors,
                textPaintStates,
                textMarkedContentIds,
                fontAssets)));
            _lines.AddRange(CreateLines(_glyphs, options));
            _runs.AddRange(_lines.SelectMany(line => line.Runs));
            _representedSoftMaskedTransparencyGroupBounds.Clear();
            _representedSoftMaskedTransparencyGroupBounds.UnionWith(
                ApplyTextShadows(_runs, _softMaskedTransparencyGroups));
            _textHighlights.AddRange(DetectTextHighlights(_paths, _glyphs, _formControls));

            if (_lines.Count > 0)
            {
                _blocks.Add(new PdfTextBlock(
                    string.Join(Environment.NewLine, _lines.Select(line => line.Text)),
                    PdfLayoutRectangle.Union(_lines.Select(line => line.Bounds)),
                    _lines));
            }

            InferFormLabels();
        }

        private static IReadOnlyList<PdfLayoutRectangle> ApplyTextShadows(
            IReadOnlyList<PdfTextRun> runs,
            IReadOnlyList<SoftMaskedTransparencyGroup> groups)
        {
            List<PdfLayoutRectangle> representedGroups = [];
            foreach (SoftMaskedTransparencyGroup group in groups)
            {
                PdfTextRun? run = runs
                    .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
                    .Where(run => CanRepresentAsTextShadow(group.Bounds, run.PageBounds))
                    .OrderByDescending(run => IntersectionArea(group.Bounds, run.PageBounds))
                    .ThenByDescending(static run => run.PageBounds.Width)
                    .FirstOrDefault();
                if (run is null || run.Shadow is not null)
                {
                    continue;
                }

                float leftExpansion = MathF.Max(0, run.PageBounds.X - group.Bounds.X);
                float rightExpansion = MathF.Max(0, group.Bounds.Right - run.PageBounds.Right);
                float topExpansion = MathF.Max(0, run.PageBounds.Y - group.Bounds.Y);
                float bottomExpansion = MathF.Max(0, group.Bounds.Bottom - run.PageBounds.Bottom);
                float offsetX = (rightExpansion - leftExpansion) / 2f;
                float offsetY = (bottomExpansion - topExpansion) / 2f;
                float blurRadius = MathF.Max(
                    0.5f,
                    MathF.Min(leftExpansion + rightExpansion, topExpansion + bottomExpansion) / 4f);
                run.Shadow = new PdfTextShadow(offsetX, offsetY, blurRadius, group.Color);
                representedGroups.Add(group.Bounds);
            }

            return representedGroups;
        }

        private static bool CanRepresentAsTextShadow(
            PdfLayoutRectangle group,
            PdfLayoutRectangle text)
        {
            if (text.Width < 24f || text.Height < 3f || group.Width < text.Width * 0.85f ||
                group.Width > text.Width * 1.35f || group.Height < text.Height || group.Height > text.Height * 3.5f)
            {
                return false;
            }

            float intersection = IntersectionArea(group, text);
            return intersection >= text.Width * text.Height * 0.8f;
        }

        private static float IntersectionArea(PdfLayoutRectangle first, PdfLayoutRectangle second)
        {
            float width = MathF.Max(0, MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X));
            float height = MathF.Max(0, MathF.Min(first.Bottom, second.Bottom) - MathF.Max(first.Y, second.Y));
            return width * height;
        }

        private static IEnumerable<PdfTextHighlight> DetectTextHighlights(
            IReadOnlyList<PdfLayoutPath> paths,
            IReadOnlyList<PdfTextGlyph> glyphs,
            IReadOnlyList<PdfLayoutFormControl> formControls)
        {
            PdfTextGlyph[] horizontalGlyphs = glyphs
                .Where(static glyph => glyph.IsPainted && MathF.Abs(glyph.Direction) < 0.01f)
                .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
                .ToArray();
            foreach (PdfLayoutPath path in paths)
            {
                if (!IsSourceHighlightRectangle(path) ||
                    path.FillColor is not PdfLayoutColor color ||
                    formControls.Any(control => IntersectionArea(path.Bounds, control.Bounds) > 0.01f))
                {
                    continue;
                }

                PdfTextGlyph[] coveredGlyphs = horizontalGlyphs
                    .Where(glyph => CoversGlyphCenter(path.Bounds, glyph.PageBounds))
                    .OrderBy(static glyph => glyph.PageBounds.X)
                    .ToArray();
                PdfTextGlyph[] visibleGlyphs = coveredGlyphs
                    .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
                    .ToArray();
                if (visibleGlyphs.Length == 0 || !FitsSingleTextLine(path.Bounds, coveredGlyphs, visibleGlyphs))
                {
                    continue;
                }

                yield return new PdfTextHighlight(path.Index, path.Bounds, color, coveredGlyphs);
            }
        }

        private static bool IsSourceHighlightRectangle(PdfLayoutPath path)
        {
            if (path.FillColor is not PdfLayoutColor color ||
                color.Alpha <= 0.01f ||
                path.Stroke != null ||
                path.UsesShapeAlpha ||
                path.UsesSoftMask ||
                path.Bounds.Width <= 0.5f ||
                path.Bounds.Height <= 0.5f ||
                path.Commands.Count != 5 ||
                path.Commands[0].Kind != PdfLayoutPathCommandKind.MoveTo ||
                path.Commands[1].Kind != PdfLayoutPathCommandKind.LineTo ||
                path.Commands[2].Kind != PdfLayoutPathCommandKind.LineTo ||
                path.Commands[3].Kind != PdfLayoutPathCommandKind.LineTo ||
                path.Commands[4].Kind != PdfLayoutPathCommandKind.ClosePath)
            {
                return false;
            }

            PdfLayoutPathCommand[] corners = path.Commands.Take(4).ToArray();
            for (int index = 0; index < corners.Length; index++)
            {
                PdfLayoutPathCommand current = corners[index];
                PdfLayoutPathCommand next = corners[(index + 1) % corners.Length];
                if (MathF.Abs(current.X1 - next.X1) > 0.01f &&
                    MathF.Abs(current.Y1 - next.Y1) > 0.01f)
                {
                    return false;
                }
            }

            return corners.Select(static command => command.X1).DistinctBy(RoundedCoordinate).Count() == 2 &&
                corners.Select(static command => command.Y1).DistinctBy(RoundedCoordinate).Count() == 2;
        }

        private static int RoundedCoordinate(float value) => (int)MathF.Round(value * 100f);

        private static bool CoversGlyphCenter(PdfLayoutRectangle highlight, PdfLayoutRectangle glyph)
        {
            float centerX = glyph.X + glyph.Width / 2f;
            float centerY = glyph.Y + glyph.Height / 2f;
            return centerX >= highlight.X - 0.25f &&
                centerX <= highlight.Right + 0.25f &&
                centerY >= highlight.Y - 0.5f &&
                centerY <= highlight.Bottom + 0.5f;
        }

        private static bool FitsSingleTextLine(
            PdfLayoutRectangle highlight,
            IReadOnlyList<PdfTextGlyph> coveredGlyphs,
            IReadOnlyList<PdfTextGlyph> visibleGlyphs)
        {
            PdfLayoutRectangle textBounds = PdfLayoutRectangle.Union(coveredGlyphs.Select(static glyph => glyph.PageBounds));
            float centerTop = visibleGlyphs.Min(static glyph => glyph.PageBounds.Y + glyph.PageBounds.Height / 2f);
            float centerBottom = visibleGlyphs.Max(static glyph => glyph.PageBounds.Y + glyph.PageBounds.Height / 2f);
            float averageHeight = visibleGlyphs.Average(static glyph => glyph.PageBounds.Height);
            if (centerBottom - centerTop > MathF.Max(1f, averageHeight * 0.35f) ||
                highlight.Height < textBounds.Height * 0.55f ||
                highlight.Height > textBounds.Height * 2.25f ||
                highlight.Width < textBounds.Width * 0.8f ||
                highlight.Width > textBounds.Width * 1.25f)
            {
                return false;
            }

            float horizontalTolerance = MathF.Max(
                1f,
                visibleGlyphs.Average(static glyph => glyph.PageBounds.Width) * 0.75f);
            if (MathF.Abs(highlight.X - textBounds.X) > horizontalTolerance ||
                MathF.Abs(highlight.Right - textBounds.Right) > horizontalTolerance)
            {
                return false;
            }

            float intersection = IntersectionArea(highlight, textBounds);
            return intersection >= highlight.Width * highlight.Height * 0.5f;
        }

        public PdfLayoutPage Build()
        {
            CollectPendingTransparencyGroupFallbacks();
            return new PdfLayoutPage(
                _pageNumber,
                _mediaBox,
                _cropBox,
                _width,
                _height,
                _rotation,
                _glyphs,
                _runs,
                _lines,
                _blocks,
                _images,
                _paths,
                _shadings,
                _vectorGroups,
                _links,
                _diagnostics,
                _paintOperations,
                _formControls,
                _textHighlights);
        }

        private void CollectFormControls(PDPage page, PDAcroForm acroForm)
        {
            HashSet<COSDictionary> pageAnnotations = new(ReferenceEqualityComparer.Instance);
            foreach (PDAnnotation annotation in page.GetAnnotations())
            {
                pageAnnotations.Add(annotation.GetCOSDictionary());
            }
            COSBase pageObject = page.GetCOSObject();
            int fieldIndex = 0;
            List<FormFieldEntry> fields = [];

            foreach (PDField field in acroForm.GetFieldTree())
            {
                if (!TryGetFormControlKind(field, out PdfLayoutFormControlKind kind))
                {
                    fieldIndex++;
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(field.GetFullyQualifiedName())
                    ? $"pdf-field-{(fieldIndex + 1).ToString(CultureInfo.InvariantCulture)}"
                    : field.GetFullyQualifiedName()!;
                fields.Add(new FormFieldEntry(field, fieldIndex, kind, name));
                fieldIndex++;
            }

            foreach (FormFieldEntry entry in fields)
            {
                PDField field = entry.Field;
                PdfLayoutFormControlKind kind = entry.Kind;
                string name = entry.Name;
                string accessibleName = field.GetCOSObject() is COSDictionary fieldDictionary
                    ? fieldDictionary.GetString(COSName.GetPDFName("TU"), name) ?? name
                    : name;
                IReadOnlyList<string> values = CurrentValues(field);
                IReadOnlyList<string> defaultValues = DefaultValues(field);
                IReadOnlyList<PdfLayoutFormOption> fieldOptions = FormOptions(field);
                List<PDAnnotationWidget> widgets = field.GetWidgets();
                string? authoredHierarchyKey = AuthoredHierarchyKey(name);
                string? groupKey = LogicalGroupKey(entry, fields, widgets.Count);
                PdfLayoutFormGroupKind? groupKind = groupKey == null
                    ? null
                    : kind == PdfLayoutFormControlKind.RadioButton
                        ? PdfLayoutFormGroupKind.RadioButton
                        : PdfLayoutFormGroupKind.CheckBox;

                for (int widgetIndex = 0; widgetIndex < widgets.Count; widgetIndex++)
                {
                    PDAnnotationWidget widget = widgets[widgetIndex];
                    COSDictionary widgetDictionary = widget.GetCOSDictionary();
                    bool belongsToPage = pageAnnotations.Contains(widgetDictionary) ||
                        ReferenceEquals(widget.GetPage()?.GetCOSObject(), pageObject);
                    PDRectangle? rectangle = widget.GetRectangle();
                    if (!belongsToPage || rectangle == null || rectangle.GetWidth() <= 0 || rectangle.GetHeight() <= 0 ||
                        widget.IsHidden() || widget.IsInvisible() || widget.IsNoView())
                    {
                        continue;
                    }

                    IReadOnlyList<PdfLayoutFormOption> options = fieldOptions;
                    bool isChecked = false;
                    bool isDefaultChecked = false;
                    if (kind is PdfLayoutFormControlKind.CheckBox or PdfLayoutFormControlKind.RadioButton)
                    {
                        string exportValue = WidgetExportValue(field, widget, widgetIndex);
                        options = [new PdfLayoutFormOption(exportValue, exportValue)];
                        isChecked = values.Contains(exportValue, StringComparer.Ordinal);
                        isDefaultChecked = defaultValues.Contains(exportValue, StringComparer.Ordinal);
                    }

                    string widgetAccessibleName = kind == PdfLayoutFormControlKind.RadioButton
                        ? $"{accessibleName}: {options[0].Label}"
                        : accessibleName;

                    PDTextField? textField = field as PDTextField;
                    PDChoice? choice = field as PDChoice;
                    _formControls.Add(new PdfLayoutFormControl(
                        _formControls.Count,
                        name,
                        widgetAccessibleName,
                        kind,
                        NormalizeWidgetRectangle(rectangle),
                        values,
                        defaultValues,
                        options,
                        HasFieldFlag(field, acroForm, 1),
                        HasFieldFlag(field, acroForm, 1 << 1),
                        isChecked,
                        isDefaultChecked,
                        textField?.IsMultiline() == true,
                        textField?.IsPassword() == true,
                        choice?.IsMultiSelect() == true,
                        textField?.GetMaxLen(),
                        authoredHierarchyKey: authoredHierarchyKey,
                        groupKey: groupKey,
                        groupKind: groupKind));
                }
            }
        }

        private void InferFormLabels()
        {
            if (_lines.Count == 0 || _formControls.Count == 0)
            {
                return;
            }

            Dictionary<string, string?> groupLabels = _formControls
                .Where(static control => control.GroupKey != null)
                .GroupBy(static control => control.GroupKey!, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    group => VisibleGroupPrompt(group),
                    StringComparer.Ordinal);

            for (int i = 0; i < _formControls.Count; i++)
            {
                PdfLayoutFormControl control = _formControls[i];
                string? sourceLabel = VisibleControlCaption(control);
                string? groupLabel = control.GroupKey == null ? null : groupLabels[control.GroupKey];
                _formControls[i] = control.WithInferredLabels(sourceLabel, groupLabel);
            }
        }

        private string? VisibleControlCaption(PdfLayoutFormControl control)
        {
            return control.Kind is PdfLayoutFormControlKind.CheckBox or PdfLayoutFormControlKind.RadioButton
                ? NearestToggleCaption(control.Bounds)
                : NearestFieldPrompt(control.Bounds);
        }

        private string? NearestToggleCaption(PdfLayoutRectangle bounds)
        {
            float centerY = bounds.Y + bounds.Height / 2f;
            return _runs
                .Where(IsUsableCaption)
                .Select(run => new
                {
                    Run = run,
                    HorizontalGap = run.Bounds.X - bounds.Right,
                    VerticalDistance = MathF.Abs(run.Bounds.Y + run.Bounds.Height / 2f - centerY)
                })
                .Where(candidate => candidate.HorizontalGap >= -2f && candidate.HorizontalGap <= 240f)
                .Where(candidate => candidate.VerticalDistance <= MathF.Max(10f, bounds.Height))
                .OrderBy(static candidate => candidate.HorizontalGap)
                .ThenBy(static candidate => candidate.VerticalDistance)
                .Select(static candidate => NormalizeCaption(candidate.Run.Text))
                .FirstOrDefault();
        }

        private string? NearestFieldPrompt(PdfLayoutRectangle bounds)
        {
            PdfTextRun? above = _runs
                .Where(IsUsableCaption)
                .Where(run => run.Bounds.Bottom <= bounds.Y + 2f)
                .Where(run => bounds.Y - run.Bounds.Bottom <= MathF.Max(36f, bounds.Height * 2f))
                .Where(run => HorizontalOverlap(run.Bounds, bounds) >= MathF.Min(run.Bounds.Width, bounds.Width) * 0.2f)
                .OrderBy(run => bounds.Y - run.Bounds.Bottom)
                .ThenBy(run => MathF.Abs(run.Bounds.X - bounds.X))
                .FirstOrDefault();
            if (above != null)
            {
                return NormalizeCaption(above.Text);
            }

            float centerY = bounds.Y + bounds.Height / 2f;
            return _runs
                .Where(IsUsableCaption)
                .Where(run => run.Bounds.Right <= bounds.X + 2f)
                .Select(run => new
                {
                    Run = run,
                    HorizontalGap = bounds.X - run.Bounds.Right,
                    VerticalDistance = MathF.Abs(run.Bounds.Y + run.Bounds.Height / 2f - centerY)
                })
                .Where(candidate => candidate.HorizontalGap <= 120f && candidate.VerticalDistance <= MathF.Max(10f, bounds.Height))
                .OrderBy(static candidate => candidate.HorizontalGap)
                .ThenBy(static candidate => candidate.VerticalDistance)
                .Select(static candidate => NormalizeCaption(candidate.Run.Text))
                .FirstOrDefault();
        }

        private string? VisibleGroupPrompt(IGrouping<string, PdfLayoutFormControl> group)
        {
            PdfLayoutRectangle bounds = PdfLayoutRectangle.Union(group.Select(static control => control.Bounds));
            PdfTextRun[] promptRuns = _runs
                .Where(IsUsableCaption)
                .Where(run => run.Bounds.Bottom <= bounds.Y - 2f)
                .Where(run => bounds.Y - run.Bounds.Bottom <= 30f)
                .Where(run => HorizontalOverlap(run.Bounds, bounds) > 0)
                .OrderBy(static run => MathF.Round(run.Bounds.Y))
                .ThenBy(static run => run.Bounds.X)
                .ToArray();
            if (promptRuns.Length > 0)
            {
                return NormalizeCaption(string.Join(" ", promptRuns.Select(static run => run.Text)));
            }

            return _runs
                .Where(IsUsableCaption)
                .Where(run => run.Bounds.Bottom <= bounds.Y - 2f)
                .Where(run => bounds.Y - run.Bounds.Bottom <= 30f)
                .Where(run => run.Bounds.Right >= bounds.X - 100f && run.Bounds.X <= bounds.X)
                .OrderBy(run => bounds.Y - run.Bounds.Bottom)
                .ThenBy(run => bounds.X - run.Bounds.Right)
                .Select(static run => NormalizeCaption(run.Text))
                .FirstOrDefault();
        }

        private static bool IsUsableCaption(PdfTextRun run) =>
            !string.IsNullOrWhiteSpace(run.Text) && NormalizeCaption(run.Text).Length is > 0 and <= 240;

        private static string NormalizeCaption(string text) =>
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static float HorizontalOverlap(PdfLayoutRectangle first, PdfLayoutRectangle second) =>
            MathF.Max(0, MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X));

        private static string? AuthoredHierarchyKey(string name)
        {
            int separator = name.LastIndexOf('.');
            return separator > 0 ? name[..separator] : null;
        }

        private static string? LogicalGroupKey(
            FormFieldEntry entry,
            IReadOnlyList<FormFieldEntry> fields,
            int widgetCount)
        {
            if (entry.Kind is not (PdfLayoutFormControlKind.CheckBox or PdfLayoutFormControlKind.RadioButton))
            {
                return null;
            }

            string indexedFamily = RemoveTerminalArrayIndex(entry.Name);
            if (!string.Equals(indexedFamily, entry.Name, StringComparison.Ordinal))
            {
                int familySize = fields.Count(field =>
                    field.Kind == entry.Kind &&
                    string.Equals(RemoveTerminalArrayIndex(field.Name), indexedFamily, StringComparison.Ordinal));
                if (familySize > 1)
                {
                    return indexedFamily;
                }
            }

            return widgetCount > 1 ? entry.Name : null;
        }

        private static string RemoveTerminalArrayIndex(string name)
        {
            if (!name.EndsWith(']'))
            {
                return name;
            }

            int bracket = name.LastIndexOf('[');
            return bracket > name.LastIndexOf('.') &&
                int.TryParse(name.AsSpan(bracket + 1, name.Length - bracket - 2), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    ? name[..bracket]
                    : name;
        }

        private sealed record FormFieldEntry(
            PDField Field,
            int Index,
            PdfLayoutFormControlKind Kind,
            string Name);

        private static bool TryGetFormControlKind(PDField field, out PdfLayoutFormControlKind kind)
        {
            kind = field switch
            {
                PDTextField => PdfLayoutFormControlKind.Text,
                PDCheckBox => PdfLayoutFormControlKind.CheckBox,
                PDRadioButton => PdfLayoutFormControlKind.RadioButton,
                PDComboBox => PdfLayoutFormControlKind.ComboBox,
                PDListBox => PdfLayoutFormControlKind.ListBox,
                PDSignatureField => PdfLayoutFormControlKind.Signature,
                _ => default
            };
            return field is PDTextField or PDCheckBox or PDRadioButton or PDComboBox or PDListBox or PDSignatureField;
        }

        private static bool HasFieldFlag(PDField field, PDAcroForm acroForm, int flag)
        {
            COSName fieldFlags = COSName.GetPDFName("FF");
            COSDictionary? dictionary = field.GetCOSObject() as COSDictionary;
            while (dictionary != null)
            {
                if (dictionary.ContainsKey(fieldFlags))
                {
                    return (dictionary.GetInt(fieldFlags, 0) & flag) != 0;
                }

                dictionary = dictionary.GetCOSDictionary(COSName.PARENT, COSName.P);
            }

            return acroForm.GetCOSObject() is COSDictionary acroFormDictionary &&
                (acroFormDictionary.GetInt(fieldFlags, 0) & flag) != 0;
        }

        private static IReadOnlyList<string> CurrentValues(PDField field)
        {
            return field switch
            {
                PDChoice choice => choice.GetValue(),
                PDSignatureField signature => SignatureValue(signature.GetSignature()),
                PDButton button => ButtonValue(button.GetValue()),
                _ => StringValue(field.GetValueAsString())
            };
        }

        private static IReadOnlyList<string> DefaultValues(PDField field)
        {
            return field switch
            {
                PDTextField text => StringValue(text.GetDefaultValue()),
                PDChoice choice => choice.GetDefaultValue(),
                PDSignatureField signature => SignatureValue(signature.GetDefaultValue()),
                PDButton button => ButtonValue(button.GetDefaultValue()),
                _ => []
            };
        }

        private static IReadOnlyList<string> StringValue(string? value) =>
            string.IsNullOrEmpty(value) ? [] : [value];

        private static IReadOnlyList<string> ButtonValue(string? value) => StringValue(value);

        private static IReadOnlyList<string> SignatureValue(PDSignature? signature)
        {
            if (signature == null)
            {
                return [];
            }

            return [string.IsNullOrWhiteSpace(signature.GetName()) ? "Signed" : signature.GetName()!];
        }

        private static IReadOnlyList<PdfLayoutFormOption> FormOptions(PDField field)
        {
            if (field is not PDChoice choice)
            {
                return [];
            }

            List<string> exportValues = choice.GetOptionsExportValues();
            List<string> displayValues = choice.GetOptionsDisplayValues();
            return exportValues
                .Select((value, index) => new PdfLayoutFormOption(
                    value,
                    index < displayValues.Count ? displayValues[index] : value))
                .ToArray();
        }

        private static string WidgetExportValue(PDField field, PDAnnotationWidget widget, int widgetIndex)
        {
            PDAppearanceEntry? normalAppearance = widget.GetAppearance()?.GetNormalAppearance();
            if (normalAppearance?.IsSubDictionary() == true)
            {
                string? state = normalAppearance.GetSubDictionary().Keys
                    .Select(static key => key.GetName())
                    .FirstOrDefault(static value => !string.Equals(value, "Off", StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(state))
                {
                    return state;
                }
            }

            if (field is PDButton button)
            {
                List<string> exportValues = button.GetExportValues();
                if (widgetIndex < exportValues.Count)
                {
                    return exportValues[widgetIndex];
                }

                string? onValue = button.GetOnValues()
                    .FirstOrDefault(static value => !string.Equals(value, "Off", StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(onValue))
                {
                    return onValue;
                }
            }

            return "Yes";
        }

        private PdfLayoutRectangle NormalizeWidgetRectangle(PDRectangle rectangle)
        {
            (float X, float Y)[] corners =
            [
                NormalizeWidgetPoint(rectangle.GetLowerLeftX(), rectangle.GetLowerLeftY()),
                NormalizeWidgetPoint(rectangle.GetLowerLeftX(), rectangle.GetUpperRightY()),
                NormalizeWidgetPoint(rectangle.GetUpperRightX(), rectangle.GetLowerLeftY()),
                NormalizeWidgetPoint(rectangle.GetUpperRightX(), rectangle.GetUpperRightY())
            ];
            float left = corners.Min(static point => point.X);
            float top = corners.Min(static point => point.Y);
            float right = corners.Max(static point => point.X);
            float bottom = corners.Max(static point => point.Y);
            return new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }

        private (float X, float Y) NormalizeWidgetPoint(float x, float y)
        {
            float cropRight = _cropBox.X + _cropBox.Width;
            float cropTop = _cropBox.Y + _cropBox.Height;
            return _rotation switch
            {
                90 => (y - _cropBox.Y, x - _cropBox.X),
                180 => (cropRight - x, y - _cropBox.Y),
                270 => (cropTop - y, cropRight - x),
                _ => (x - _cropBox.X, cropTop - y)
            };
        }

        private void CollectGraphics(PDPage page, PdfLayoutOptions options)
        {
            LayoutGraphicsCollector collector = new(
                page,
                _pageNumber,
                _cropBox,
                _rotation,
                options.IncludeImages,
                options.IncludeImageAssets,
                options.ImageExportPolicy,
                options.IncludePaths,
                _imageAssets,
                _colorManagementContext);
            try
            {
                collector.Run(page);
            }
            catch (IOException ex)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "image-collection-failed",
                    "Image placement collection failed: " + ex.Message,
                    _pageNumber));
            }

            _images.AddRange(collector.Images);
            _paths.AddRange(collector.Paths);
            _shadings.AddRange(collector.Shadings);
            _vectorGroups.AddRange(collector.VectorGroups);
            _paintOperations.AddRange(collector.PaintOperations);
            _diagnostics.AddRange(collector.Diagnostics);
            _softMaskedTransparencyGroups.AddRange(collector.SoftMaskedTransparencyGroups);

            if (options.IncludeImageAssets && options.IncludeTransparencyGroupFallbacks)
            {
                _transparencyGroupFallbackBounds.AddRange(
                    collector.KnockoutTransparencyGroupBounds.Select(ExpandKnockoutTransparencyGroupBounds));
                _transparencyGroupFallbackBounds.AddRange(
                    collector.IsolatedDeviceCmykBlendGroupBounds.Select(
                        ExpandIsolatedDeviceCmykBlendGroupBounds));
                _softMaskedTransparencyGroupFallbackBounds.AddRange(
                    collector.SoftMaskedTransparencyGroupFallbackBounds);
            }
        }

        private void CollectPendingTransparencyGroupFallbacks()
        {
            if (_transparencyGroupFallbacksCollected)
            {
                return;
            }

            _transparencyGroupFallbacksCollected = true;
            PdfLayoutRectangle[] softMaskedBounds = _softMaskedTransparencyGroupFallbackBounds
                .Where(bounds => !_representedSoftMaskedTransparencyGroupBounds.Contains(bounds))
                .Select(ExpandSoftMaskedTransparencyGroupBounds)
                .ToArray();
            CollectTransparencyGroupFallbacks(
                _transparencyGroupFallbackBounds.Concat(softMaskedBounds).ToArray(),
                renderFullPage: softMaskedBounds.Length > 0);
        }

        private void CollectTransparencyGroupFallbacks(
            IReadOnlyList<PdfLayoutRectangle> groupBounds,
            bool renderFullPage = false)
        {
            PdfLayoutRectangle[] fallbackBounds = MergeTransparencyGroupBounds(groupBounds)
                .Where(IsCompactTransparencyGroup)
                .ToArray();
            if (fallbackBounds.Length == 0)
            {
                return;
            }

            if (!RenderingBackend.IsRegistered)
            {
                HandleFallbackExportFailure(
                    "transparency-group-rasterization-backend-missing",
                    "Transparency-group fallback rendering requires a registered rendering backend.",
                    backendMissing: true);
                return;
            }

            if (_rotation != 0)
            {
                HandleFallbackExportFailure(
                    "transparency-group-rasterization-rotation-unsupported",
                    "Transparency-group raster fallbacks are not collected for rotated pages yet.",
                    backendMissing: false);
                return;
            }

            try
            {
                PDFRenderer renderer = new(_document);
                if (renderFullPage)
                {
                    using BufferedImage pageImage = renderer.RenderImage(
                        _pageIndex,
                        TransparencyGroupRasterScale,
                        ImageType.RGB);
                    PdfLayoutRectangle pageBounds = new(0, 0, _width, _height);
                    for (int fallbackIndex = 0; fallbackIndex < fallbackBounds.Length; fallbackIndex++)
                    {
                        PdfLayoutRectangle bounds = fallbackBounds[fallbackIndex];
                        using BufferedImage image = CropRenderedRegion(
                            pageImage,
                            pageBounds,
                            bounds,
                            TransparencyGroupRasterScale);
                        AddTransparencyGroupFallback(image, bounds, fallbackIndex);
                    }

                    return;
                }

                foreach (TransparencyGroupRenderBatch batch in CreateTransparencyGroupRenderBatches(fallbackBounds))
                {
                    using BufferedImage renderedRegion = RenderPageRegion(
                        renderer,
                        batch.Bounds,
                        TransparencyGroupRasterScale);
                    if (batch.FallbackIndexes.Count == 1)
                    {
                        int fallbackIndex = batch.FallbackIndexes[0];
                        AddTransparencyGroupFallback(renderedRegion, fallbackBounds[fallbackIndex], fallbackIndex);
                        continue;
                    }

                    foreach (int fallbackIndex in batch.FallbackIndexes)
                    {
                        PdfLayoutRectangle bounds = fallbackBounds[fallbackIndex];
                        using BufferedImage image = CropRenderedRegion(
                            renderedRegion,
                            batch.Bounds,
                            bounds,
                            TransparencyGroupRasterScale);
                        AddTransparencyGroupFallback(image, bounds, fallbackIndex);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                HandleFallbackExportFailure(
                    "transparency-group-rasterization-failed",
                    "Transparency-group fallback rendering failed: " + ex.Message,
                    backendMissing: false,
                    exception: ex);
            }
        }

        private PdfLayoutRectangle ExpandKnockoutTransparencyGroupBounds(PdfLayoutRectangle bounds)
        {
            const float padding = 18f;
            float left = MathF.Max(0, bounds.X - padding);
            float top = MathF.Max(0, bounds.Y - padding);
            float right = MathF.Min(_width, bounds.Right + padding);
            float bottom = MathF.Min(_height, bounds.Bottom + 3f);
            return new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }

        private PdfLayoutRectangle ExpandIsolatedDeviceCmykBlendGroupBounds(PdfLayoutRectangle bounds)
        {
            const float padding = 1f;
            float left = MathF.Max(0, bounds.X - padding);
            float top = MathF.Max(0, bounds.Y - padding);
            float right = MathF.Min(_width, bounds.Right + padding);
            float bottom = MathF.Min(_height, bounds.Bottom + padding);
            return new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }

        private PdfLayoutRectangle ExpandSoftMaskedTransparencyGroupBounds(PdfLayoutRectangle bounds)
        {
            const float padding = 1f;
            float left = MathF.Max(0, bounds.X - padding);
            float top = MathF.Max(0, bounds.Y - padding);
            float right = MathF.Min(_width, bounds.Right + padding);
            float bottom = MathF.Min(_height, bounds.Bottom + padding);
            return new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }

        private static IEnumerable<PdfLayoutRectangle> MergeTransparencyGroupBounds(
            IEnumerable<PdfLayoutRectangle> regions)
        {
            List<PdfLayoutRectangle> merged = [];
            foreach (PdfLayoutRectangle region in regions
                .OrderBy(static region => region.Y)
                .ThenBy(static region => region.X))
            {
                PdfLayoutRectangle combined = region;
                bool mergedRegion;
                do
                {
                    mergedRegion = false;
                    for (int index = merged.Count - 1; index >= 0; index--)
                    {
                        if (!RectanglesTouch(merged[index], combined, 2f))
                        {
                            continue;
                        }

                        combined = PdfLayoutRectangle.Union([merged[index], combined]);
                        merged.RemoveAt(index);
                        mergedRegion = true;
                    }
                }
                while (mergedRegion);

                merged.Add(combined);
            }

            return merged;
        }

        private static bool RectanglesTouch(PdfLayoutRectangle first, PdfLayoutRectangle second, float tolerance)
        {
            return first.Right >= second.X - tolerance &&
                second.Right >= first.X - tolerance &&
                first.Bottom >= second.Y - tolerance &&
                second.Bottom >= first.Y - tolerance;
        }

        private bool IsCompactTransparencyGroup(PdfLayoutRectangle bounds)
        {
            return bounds.Width >= 12f &&
                bounds.Height >= 12f &&
                bounds.Width * bounds.Height <= _width * _height * 0.65f;
        }

        private IReadOnlyList<TransparencyGroupRenderBatch> CreateTransparencyGroupRenderBatches(
            IReadOnlyList<PdfLayoutRectangle> fallbackBounds)
        {
            List<TransparencyGroupRenderBatch> batches = fallbackBounds
                .Select(static (bounds, index) => new TransparencyGroupRenderBatch(bounds, [index]))
                .ToList();
            float replayCost = _width * _height * TransparencyGroupRenderReplayCostRatio;
            while (batches.Count > 1)
            {
                int firstIndex = -1;
                int secondIndex = -1;
                float bestSavings = 0f;
                PdfLayoutRectangle bestUnion = default;
                for (int first = 0; first < batches.Count - 1; first++)
                {
                    for (int second = first + 1; second < batches.Count; second++)
                    {
                        PdfLayoutRectangle union = PdfLayoutRectangle.Union(
                            [batches[first].Bounds, batches[second].Bounds]);
                        float separateArea = RectangleArea(batches[first].Bounds) +
                            RectangleArea(batches[second].Bounds);
                        float savings = separateArea + replayCost - RectangleArea(union);
                        if (savings <= bestSavings)
                        {
                            continue;
                        }

                        firstIndex = first;
                        secondIndex = second;
                        bestSavings = savings;
                        bestUnion = union;
                    }
                }

                if (firstIndex < 0)
                {
                    break;
                }

                TransparencyGroupRenderBatch firstBatch = batches[firstIndex];
                TransparencyGroupRenderBatch secondBatch = batches[secondIndex];
                batches[firstIndex] = new TransparencyGroupRenderBatch(
                    bestUnion,
                    firstBatch.FallbackIndexes.Concat(secondBatch.FallbackIndexes).Order().ToArray());
                batches.RemoveAt(secondIndex);
            }

            return batches.OrderBy(static batch => batch.FallbackIndexes[0]).ToArray();
        }

        private static float RectangleArea(PdfLayoutRectangle bounds) => bounds.Width * bounds.Height;

        private BufferedImage RenderPageRegion(
            PDFRenderer renderer,
            PdfLayoutRectangle bounds,
            float scale)
        {
            ScaledImageRegion region = GetScaledImageRegion(bounds, scale);
            BufferedImage image = new(region.Width, region.Height, BufferedImage.TYPE_INT_RGB);
            try
            {
                using Graphics2D graphics = image.CreateGraphics();
                graphics.SetBackground(Color.White);
                graphics.ClearRect(0, 0, image.Width, image.Height);
                graphics.Translate(-region.Left, -region.Top);
                renderer.RenderPageToGraphics(
                    _pageIndex,
                    graphics,
                    scale,
                    scale,
                    RenderDestination.EXPORT);
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        private BufferedImage CropRenderedRegion(
            BufferedImage renderedRegion,
            PdfLayoutRectangle renderedBounds,
            PdfLayoutRectangle fallbackBounds,
            float scale)
        {
            ScaledImageRegion source = GetScaledImageRegion(renderedBounds, scale);
            ScaledImageRegion target = GetScaledImageRegion(fallbackBounds, scale);
            int left = Math.Clamp(target.Left - source.Left, 0, renderedRegion.Width - 1);
            int top = Math.Clamp(target.Top - source.Top, 0, renderedRegion.Height - 1);
            int right = Math.Clamp(target.Right - source.Left, left + 1, renderedRegion.Width);
            int bottom = Math.Clamp(target.Bottom - source.Top, top + 1, renderedRegion.Height);
            BufferedImage crop = new(right - left, bottom - top, BufferedImage.TYPE_INT_RGB);
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    crop.SetRgb(x - left, y - top, renderedRegion.GetRgb(x, y));
                }
            }

            return crop;
        }

        private ScaledImageRegion GetScaledImageRegion(PdfLayoutRectangle bounds, float scale)
        {
            int pageWidth = Math.Max((int)MathF.Floor(_width * scale), 1);
            int pageHeight = Math.Max((int)MathF.Floor(_height * scale), 1);
            int left = Math.Clamp((int)MathF.Floor(bounds.X * scale), 0, pageWidth - 1);
            int top = Math.Clamp((int)MathF.Floor(bounds.Y * scale), 0, pageHeight - 1);
            int right = Math.Clamp((int)MathF.Ceiling(bounds.Right * scale), left + 1, pageWidth);
            int bottom = Math.Clamp((int)MathF.Ceiling(bounds.Bottom * scale), top + 1, pageHeight);
            return new ScaledImageRegion(left, top, right, bottom);
        }

        private void AddTransparencyGroupFallback(
            BufferedImage image,
            PdfLayoutRectangle bounds,
            int fallbackIndex)
        {
            string preferredAssetId = $"page-{_pageNumber.ToString(CultureInfo.InvariantCulture)}-transparency-group-{fallbackIndex.ToString(CultureInfo.InvariantCulture)}";
            byte[] data = RenderingBackend.Current.ImageCodec.Encode(image, EncodedImageFormat.Png, 100);
            PdfLayoutImageAsset asset = _imageAssets.Add(preferredAssetId, "png", "image/png", data);
            int index = _images.Count;
            _images.Add(new PdfLayoutImage(
                index,
                asset.AssetId,
                PdfLayoutImageKind.TransparencyGroupFallback,
                bounds,
                new PdfLayoutTransform(1, 0, 0, 1, bounds.X, bounds.Y),
                image.Width,
                image.Height,
                8,
                "DeviceRGB",
                true,
                "transparency-group"));
            _paintOperations.Add(new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Image, index));
        }

        private void CollectAnnotationAppearances(PDDocument document, int pageIndex, PDPage page)
        {
            if (!RenderingBackend.IsRegistered)
            {
                if (page.GetAnnotations().Any(ShouldCollectAnnotationAppearance))
                {
                    HandleFallbackExportFailure(
                        "annotation-appearance-backend-missing",
                        "Annotation appearances require a registered rendering backend and were skipped.",
                        backendMissing: true);
                }

                return;
            }

            if (_rotation != 0)
            {
                if (page.GetAnnotations().Any(ShouldCollectAnnotationAppearance))
                {
                    HandleFallbackExportFailure(
                        "annotation-rotation-unsupported",
                        "Annotation appearance geometry is not collected for rotated pages yet.",
                        backendMissing: false);
                }

                return;
            }

            HashSet<string> seenAnnotationAppearances = new(StringComparer.Ordinal);
            PDAnnotation[] annotations = page.GetAnnotations()
                .Where(ShouldCollectAnnotationAppearance)
                .Where(annotation => seenAnnotationAppearances.Add(AnnotationAppearanceKey(annotation)))
                .ToArray();
            if (annotations.Length == 0)
            {
                return;
            }

            PDFRenderer renderer = new(document);
            try
            {
                renderer.SetAnnotationsFilter(_ => false);
                using BufferedImage withoutAnnotations = renderer.RenderImage(pageIndex, AnnotationAppearanceScale, ImageType.RGB);
                renderer.SetAnnotationsFilter(annotation => annotation is PDAnnotation pdAnnotation && ShouldCollectAnnotationAppearance(pdAnnotation));
                using BufferedImage withAnnotations = renderer.RenderImage(pageIndex, AnnotationAppearanceScale, ImageType.RGB);

                for (int annotationIndex = 0; annotationIndex < annotations.Length; annotationIndex++)
                {
                    CollectAnnotationAppearanceAsset(withAnnotations, withoutAnnotations, annotations[annotationIndex], annotationIndex);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                HandleFallbackExportFailure(
                    "annotation-appearance-export-failed",
                    "Annotation appearance export failed: " + ex.Message,
                    backendMissing: false,
                    exception: ex);
            }
        }

        private void HandleFallbackExportFailure(
            string diagnosticCode,
            string message,
            bool backendMissing,
            Exception? exception = null)
        {
            if (_imageExportPolicy == PdfImageExportPolicy.Strict)
            {
                throw new InvalidOperationException(
                    $"Raster fallback export failed in strict mode: {message}",
                    exception);
            }

            if (backendMissing && _imageExportPolicy == PdfImageExportPolicy.BackendRequired)
            {
                throw new InvalidOperationException(
                    $"Raster fallback export requires a registered rendering backend: {message}",
                    exception);
            }

            _diagnostics.Add(new PdfLayoutDiagnostic(
                PdfLayoutDiagnosticSeverity.Warning,
                diagnosticCode,
                message,
                _pageNumber));
        }

        private static bool ShouldCollectAnnotationAppearance(PDAnnotation annotation)
        {
            PDRectangle? rectangle = annotation.GetRectangle();
            return rectangle != null &&
                rectangle.GetWidth() > 0 &&
                rectangle.GetHeight() > 0 &&
                annotation is not PDAnnotationLink &&
                !annotation.IsHidden() &&
                !annotation.IsInvisible() &&
                !annotation.IsNoView();
        }

        private static string AnnotationAppearanceKey(PDAnnotation annotation)
        {
            PDRectangle rectangle = annotation.GetRectangle()
                ?? throw new InvalidOperationException("Annotation appearance key requires a rectangle.");
            return string.Join(
                "|",
                annotation.GetSubtype() ?? string.Empty,
                MathF.Round(rectangle.GetLowerLeftX(), 3).ToString(CultureInfo.InvariantCulture),
                MathF.Round(rectangle.GetLowerLeftY(), 3).ToString(CultureInfo.InvariantCulture),
                MathF.Round(rectangle.GetUpperRightX(), 3).ToString(CultureInfo.InvariantCulture),
                MathF.Round(rectangle.GetUpperRightY(), 3).ToString(CultureInfo.InvariantCulture));
        }

        private void CollectAnnotationAppearanceAsset(
            BufferedImage withAnnotations,
            BufferedImage withoutAnnotations,
            PDAnnotation annotation,
            int annotationIndex)
        {
            PDRectangle? rectangle = annotation.GetRectangle();
            if (rectangle == null)
            {
                return;
            }

            PdfLayoutRectangle bounds = NormalizePdfRectangle(rectangle);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            int x = Math.Clamp((int)MathF.Floor(bounds.X * AnnotationAppearanceScale), 0, withAnnotations.Width - 1);
            int y = Math.Clamp((int)MathF.Floor(bounds.Y * AnnotationAppearanceScale), 0, withAnnotations.Height - 1);
            int right = Math.Clamp((int)MathF.Ceiling(bounds.Right * AnnotationAppearanceScale), x + 1, withAnnotations.Width);
            int bottom = Math.Clamp((int)MathF.Ceiling(bounds.Bottom * AnnotationAppearanceScale), y + 1, withAnnotations.Height);
            int width = right - x;
            int height = bottom - y;
            using BufferedImage appearance = new(width, height, BufferedImage.TYPE_INT_ARGB);
            int changedPixels = 0;

            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    int withPixel = withAnnotations.GetRgb(x + px, y + py);
                    int withoutPixel = withoutAnnotations.GetRgb(x + px, y + py);
                    int alpha = DifferenceAlpha(withPixel, withoutPixel);
                    if (alpha == 0)
                    {
                        appearance.SetRgb(px, py, 0);
                        continue;
                    }

                    changedPixels++;
                    appearance.SetRgb(px, py, (alpha << 24) | (withPixel & 0x00FFFFFF));
                }
            }

            if (changedPixels < Math.Max(4, width * height / 1000))
            {
                return;
            }

            string preferredAssetId = $"page-{_pageNumber.ToString(CultureInfo.InvariantCulture)}-annotation-{annotationIndex.ToString(CultureInfo.InvariantCulture)}";
            byte[] data = RenderingBackend.Current.ImageCodec.Encode(appearance, EncodedImageFormat.Png, 100);
            PdfLayoutImageAsset asset = _imageAssets.Add(preferredAssetId, "png", "image/png", data);
            int index = _images.Count;
            _images.Add(new PdfLayoutImage(
                index,
                asset.AssetId,
                PdfLayoutImageKind.AnnotationAppearance,
                bounds,
                new PdfLayoutTransform(1, 0, 0, 1, bounds.X, bounds.Y),
                width,
                height,
                8,
                "DeviceRGB",
                true,
                annotation.GetSubtype()));
            _paintOperations.Add(new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Image, index));
        }

        private static int DifferenceAlpha(int first, int second)
        {
            int redDelta = Math.Abs(((first >> 16) & 0xFF) - ((second >> 16) & 0xFF));
            int greenDelta = Math.Abs(((first >> 8) & 0xFF) - ((second >> 8) & 0xFF));
            int blueDelta = Math.Abs((first & 0xFF) - (second & 0xFF));
            int delta = Math.Max(redDelta, Math.Max(greenDelta, blueDelta));
            return delta < 4 ? 0 : Math.Clamp(delta * 2, 32, 255);
        }

        private void CollectLinks(PDPage page)
        {
            IList<PDAnnotation> annotations = page.GetAnnotations();
            if (_rotation != 0)
            {
                if (annotations.OfType<PDAnnotationLink>().Any())
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "link-rotation-unsupported",
                        "Link annotation geometry is not collected for rotated pages yet.",
                        _pageNumber));
                }

                return;
            }

            int index = 0;
            foreach (PDAnnotationLink annotation in annotations.OfType<PDAnnotationLink>())
            {
                PDRectangle? rectangle = annotation.GetRectangle();
                if (rectangle == null)
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "link-missing-rectangle",
                        "Link annotation has no rectangle and was skipped.",
                        _pageNumber));
                    continue;
                }

                (PdfLayoutLinkKind kind, string? uri, string? destination, int? destinationPageNumber) = Target(annotation);
                _links.Add(new PdfLayoutLink(
                    index,
                    NormalizePdfRectangle(rectangle),
                    kind,
                    uri,
                    destination,
                    destinationPageNumber,
                    QuadBounds(annotation.GetQuadPoints())));
                index++;
            }
        }

        private PdfLayoutRectangle NormalizePdfRectangle(PDRectangle rectangle)
        {
            return NormalizePdfBox(
                rectangle.GetLowerLeftX(),
                rectangle.GetLowerLeftY(),
                rectangle.GetUpperRightX(),
                rectangle.GetUpperRightY());
        }

        private IReadOnlyList<PdfLayoutRectangle> QuadBounds(float[]? quadPoints)
        {
            if (quadPoints == null || quadPoints.Length < 8)
            {
                return [];
            }

            List<PdfLayoutRectangle> bounds = new();
            for (int i = 0; i + 7 < quadPoints.Length; i += 8)
            {
                float minX = MathF.Min(MathF.Min(quadPoints[i], quadPoints[i + 2]), MathF.Min(quadPoints[i + 4], quadPoints[i + 6]));
                float maxX = MathF.Max(MathF.Max(quadPoints[i], quadPoints[i + 2]), MathF.Max(quadPoints[i + 4], quadPoints[i + 6]));
                float minY = MathF.Min(MathF.Min(quadPoints[i + 1], quadPoints[i + 3]), MathF.Min(quadPoints[i + 5], quadPoints[i + 7]));
                float maxY = MathF.Max(MathF.Max(quadPoints[i + 1], quadPoints[i + 3]), MathF.Max(quadPoints[i + 5], quadPoints[i + 7]));
                bounds.Add(NormalizePdfBox(minX, minY, maxX, maxY));
            }

            return bounds;
        }

        private PdfLayoutRectangle NormalizePdfBox(float lowerLeftX, float lowerLeftY, float upperRightX, float upperRightY)
        {
            float cropTop = _cropBox.Y + _cropBox.Height;
            return new PdfLayoutRectangle(
                lowerLeftX - _cropBox.X,
                cropTop - upperRightY,
                MathF.Max(0, upperRightX - lowerLeftX),
                MathF.Max(0, upperRightY - lowerLeftY));
        }

        private static (PdfLayoutLinkKind Kind, string? Uri, string? Destination, int? DestinationPageNumber) Target(
            PDAnnotationLink annotation)
        {
            if (annotation.GetAction() is PDActionURI uriAction)
            {
                string? uri = uriAction.GetURI();
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    return (PdfLayoutLinkKind.Uri, uri, null, null);
                }
            }

            PDDestination? destination = annotation.GetAction() is PDActionGoTo goToAction
                ? goToAction.GetDestination()
                : annotation.GetDestination();
            return DestinationTarget(destination);
        }

        private static (PdfLayoutLinkKind Kind, string? Uri, string? Destination, int? DestinationPageNumber) DestinationTarget(
            PDDestination? destination)
        {
            if (destination == null)
            {
                return (PdfLayoutLinkKind.Unknown, null, null, null);
            }

            if (destination is PDNamedDestination namedDestination)
            {
                return (PdfLayoutLinkKind.Destination, null, namedDestination.GetNamedDestination(), null);
            }

            if (destination is PDPageDestination pageDestination)
            {
                int pageIndex = pageDestination.RetrievePageNumber();
                if (pageIndex >= 0)
                {
                    return (PdfLayoutLinkKind.Destination, null, $"page:{pageIndex + 1}", pageIndex + 1);
                }

                return (PdfLayoutLinkKind.Destination, null, "page", null);
            }

            return (PdfLayoutLinkKind.Destination, null, destination.GetType().Name, null);
        }

        private PdfTextGlyph CreateGlyph(
            TextPosition position,
            IReadOnlyDictionary<TextPosition, PdfLayoutColor> textColors,
            IReadOnlyDictionary<TextPosition, bool> textPaintStates,
            IReadOnlyDictionary<TextPosition, int?> textMarkedContentIds,
            EmbeddedFontAssetCollector fontAssets)
        {
            PDFont font = position.GetFont();
            bool hasBrowserFontAsset = fontAssets.Collect(font, _pageNumber);
            float height = MathF.Max(0, position.GetHeightDir());
            float width = MathF.Max(0, position.GetWidthDirAdj());
            float y = position.GetYDirAdj() - height;
            float direction = position.GetDir();
            bool vertical = MathF.Abs(direction - 90f) < 0.01f || MathF.Abs(direction - 270f) < 0.01f;
            PdfLayoutRectangle pageBounds = new(
                position.GetX(),
                position.GetY() - (vertical ? width : height),
                vertical ? height : width,
                vertical ? width : height);
            return new PdfTextGlyph(
                NormalizeGlyphText(position),
                font.GetName(),
                position.GetFontSizeInPtFloat(),
                direction,
                new PdfLayoutRectangle(
                    position.GetXDirAdj(),
                    y,
                    width,
                    height),
                textColors.GetValueOrDefault(position, new PdfLayoutColor(0, 0, 0, 1, null)))
            {
                PageBounds = pageBounds,
                Outline = hasBrowserFontAsset ? null : TryCreateGlyphOutline(position, font),
                UsesBrowserFontAsset = hasBrowserFontAsset,
                IsPainted = textPaintStates.GetValueOrDefault(position, true),
                MarkedContentId = textMarkedContentIds.GetValueOrDefault(position)
            };
        }

        private PdfLayoutPathCommand[]? TryCreateGlyphOutline(TextPosition position, PDFont font)
        {
            // Embedded PDF vector fonts that cannot be referenced by CSS @font-face still have
            // exact, cross-platform glyph geometry. Never collect normalized paths from
            // unembedded fonts because those paths depend on host font substitution.
            if (font is not PDVectorFont vectorFont ||
                !HasEmbeddedGlyphOutlineSource(font) ||
                position.GetCharacterCodes() is not [int code])
            {
                return null;
            }

            try
            {
                GeneralPath path = vectorFont.GetNormalizedPath(code);
                if (path.Segments.Count == 0)
                {
                    return [];
                }

                Matrix glyphMatrix = Matrix.Concatenate(position.GetTextMatrix(), font.GetFontMatrix());
                List<PdfLayoutPathCommand> commands = new(path.Segments.Count);
                (float X, float Y) current = default;
                foreach (GeneralPath.Segment segment in path.Segments)
                {
                    switch (segment.Type)
                    {
                        case GeneralPath.SegmentType.MoveTo:
                            current = NormalizeGlyphOutlinePoint(segment.X1, segment.Y1, glyphMatrix);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, current.X, current.Y, 0, 0, 0, 0));
                            break;
                        case GeneralPath.SegmentType.LineTo:
                            current = NormalizeGlyphOutlinePoint(segment.X1, segment.Y1, glyphMatrix);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, current.X, current.Y, 0, 0, 0, 0));
                            break;
                        case GeneralPath.SegmentType.QuadTo:
                            {
                                (float X, float Y) control = NormalizeGlyphOutlinePoint(segment.X1, segment.Y1, glyphMatrix);
                                (float X, float Y) end = NormalizeGlyphOutlinePoint(segment.X2, segment.Y2, glyphMatrix);
                                commands.Add(new PdfLayoutPathCommand(
                                    PdfLayoutPathCommandKind.CurveTo,
                                    current.X + ((control.X - current.X) * (2f / 3f)),
                                    current.Y + ((control.Y - current.Y) * (2f / 3f)),
                                    end.X + ((control.X - end.X) * (2f / 3f)),
                                    end.Y + ((control.Y - end.Y) * (2f / 3f)),
                                    end.X,
                                    end.Y));
                                current = end;
                                break;
                            }
                        case GeneralPath.SegmentType.CurveTo:
                            {
                                (float X, float Y) control1 = NormalizeGlyphOutlinePoint(segment.X1, segment.Y1, glyphMatrix);
                                (float X, float Y) control2 = NormalizeGlyphOutlinePoint(segment.X2, segment.Y2, glyphMatrix);
                                (float X, float Y) end = NormalizeGlyphOutlinePoint(segment.X3, segment.Y3, glyphMatrix);
                                commands.Add(new PdfLayoutPathCommand(
                                    PdfLayoutPathCommandKind.CurveTo,
                                    control1.X,
                                    control1.Y,
                                    control2.X,
                                    control2.Y,
                                    end.X,
                                    end.Y));
                                current = end;
                                break;
                            }
                        case GeneralPath.SegmentType.Close:
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0, 0, 0, 0, 0, 0));
                            break;
                    }
                }

                return commands.ToArray();
            }
            catch (IOException ex)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "glyph-outline-collection-failed",
                    "Embedded vector glyph outlines could not be collected: " + ex.Message,
                    _pageNumber));
                return null;
            }
        }

        private static bool HasEmbeddedGlyphOutlineSource(PDFont font)
        {
            if (font is PDType1CFont)
            {
                return true;
            }

            COSDictionary? descriptor = font.GetFontDescriptor()?.GetCOSObject();
            return descriptor?.GetDictionaryObject(COSName.GetPDFName("FontFile")) is COSStream ||
                descriptor?.GetDictionaryObject(COSName.GetPDFName("FontFile2")) is COSStream ||
                descriptor?.GetDictionaryObject(COSName.GetPDFName("FontFile3")) is COSStream;
        }

        private (float X, float Y) NormalizeGlyphOutlinePoint(float x, float y, Matrix glyphMatrix)
        {
            Vector point = glyphMatrix.Transform(x, y);
            float cropTop = _cropBox.Y + _cropBox.Height;
            return (point.GetX() - _cropBox.X, cropTop - point.GetY());
        }

        private static string NormalizeGlyphText(TextPosition position)
        {
            string text = position.GetUnicode();
            string fontName = position.GetFont().GetName();
            if (IsComputerModernSymbolFont(fontName) &&
                position.GetCharacterCodes() is [12] &&
                text.Length == 1 &&
                text[0] is '\f' or '\uFFFD')
            {
                return "⊙";
            }

            if (!fontName.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
                text.Length < 2 ||
                text.Length % 2 != 0)
            {
                return text;
            }

            int halfLength = text.Length / 2;
            return text.AsSpan(0, halfLength).SequenceEqual(text.AsSpan(halfLength))
                ? text[..halfLength]
                : text;
        }

        private static bool IsComputerModernSymbolFont(string fontName)
        {
            string baseName = fontName[(fontName.LastIndexOf('+') + 1)..];
            return baseName.StartsWith("CMSY", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("CMBSY", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<PdfTextLine> CreateLines(IReadOnlyList<PdfTextGlyph> glyphs, PdfLayoutOptions options)
        {
            List<TextLineBucket> buckets = [];
            foreach (PdfTextGlyph glyph in glyphs.Where(static glyph => IsHorizontalDirection(glyph.Direction)))
            {
                if (string.IsNullOrEmpty(glyph.Text))
                {
                    continue;
                }

                TextLineBucket? bucket = null;
                float closestTopDelta = float.MaxValue;
                foreach (TextLineBucket candidate in buckets)
                {
                    if (MathF.Abs(candidate.Direction - glyph.Direction) > 0.01f)
                    {
                        continue;
                    }

                    float topDelta = MathF.Abs(candidate.Top - glyph.Bounds.Y);
                    if (topDelta <= options.SameLineTolerance && topDelta < closestTopDelta)
                    {
                        bucket = candidate;
                        closestTopDelta = topDelta;
                    }
                }

                if (bucket == null)
                {
                    bucket = new TextLineBucket(glyph.Direction, glyph.Bounds.Y);
                    buckets.Add(bucket);
                }

                bucket.Add(glyph);
            }

            MergeInlineMathAttachmentBuckets(buckets);
            foreach (TextLineBucket bucket in buckets
                .OrderBy(static bucket => bucket.Top)
                .ThenBy(static bucket => bucket.Left))
            {
                yield return CreateLine(OrderLineGlyphs(bucket.Glyphs), options);
            }

            foreach (PdfTextLine line in CreateOrderedLines(
                glyphs.Where(static glyph => !IsHorizontalDirection(glyph.Direction)),
                options))
            {
                yield return line;
            }
        }

        private static void MergeInlineMathAttachmentBuckets(List<TextLineBucket> buckets)
        {
            foreach (TextLineBucket attachment in buckets
                .OrderBy(static bucket => bucket.MaximumFontSize)
                .ToArray())
            {
                if (!buckets.Contains(attachment))
                {
                    continue;
                }

                List<PdfTextGlyph> attachedGlyphs = [];
                foreach (PdfTextGlyph glyph in attachment.Glyphs.ToArray())
                {
                    TextLineBucket? target = buckets
                        .Where(bucket => !ReferenceEquals(bucket, attachment))
                        .Where(bucket => MathF.Abs(bucket.Direction - attachment.Direction) < 0.01f)
                        .Where(bucket => glyph.FontSize <= bucket.MaximumFontSize * 0.75f)
                        .Where(bucket => AttachmentGlyphIsNearBucket(glyph, bucket))
                        .Where(bucket => HasNearbyTeXMathGlyph(bucket, glyph))
                        .OrderBy(bucket => AttachmentGlyphDistance(glyph, bucket))
                        .FirstOrDefault();
                    if (target == null)
                    {
                        continue;
                    }

                    target.Add(glyph);
                    attachedGlyphs.Add(glyph);
                }

                attachment.RemoveRange(attachedGlyphs);
                if (attachment.Glyphs.Count == 0)
                {
                    buckets.Remove(attachment);
                }
            }
        }

        private static bool AttachmentGlyphIsNearBucket(PdfTextGlyph attachment, TextLineBucket target)
        {
            PdfLayoutRectangle targetBounds = target.Bounds;
            float verticalDistance = MathF.Abs(
                attachment.Bounds.Y + (attachment.Bounds.Height / 2f) -
                (targetBounds.Y + (targetBounds.Height / 2f)));
            float horizontalGap = HorizontalGap(attachment.Bounds, targetBounds);
            return verticalDistance <= MathF.Max(7f, target.MaximumFontSize * 0.9f) &&
                horizontalGap <= MathF.Max(8f, target.MaximumFontSize * 1.5f);
        }

        private static bool HasNearbyTeXMathGlyph(TextLineBucket target, PdfTextGlyph attachment)
        {
            foreach (PdfTextGlyph targetGlyph in target.Glyphs)
            {
                if (!IsTeXMathFont(targetGlyph.FontName) ||
                    HorizontalGap(attachment.Bounds, targetGlyph.Bounds) > 12f)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool IsTeXMathFont(string fontName)
        {
            string baseName = fontName[(fontName.LastIndexOf('+') + 1)..];
            return baseName.StartsWith("CM", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith("MSBM", StringComparison.OrdinalIgnoreCase);
        }

        private static float AttachmentGlyphDistance(PdfTextGlyph attachment, TextLineBucket target)
        {
            PdfLayoutRectangle targetBounds = target.Bounds;
            return HorizontalGap(attachment.Bounds, targetBounds) + MathF.Abs(
                attachment.Bounds.Y + (attachment.Bounds.Height / 2f) -
                (targetBounds.Y + (targetBounds.Height / 2f)));
        }

        private static float HorizontalGap(PdfLayoutRectangle first, PdfLayoutRectangle second)
        {
            return MathF.Max(0, MathF.Max(first.X - second.Right, second.X - first.Right));
        }

        private static IEnumerable<PdfTextLine> CreateOrderedLines(
            IEnumerable<PdfTextGlyph> glyphs,
            PdfLayoutOptions options)
        {
            List<PdfTextGlyph> currentLine = new();
            float currentTop = 0;
            foreach (PdfTextGlyph glyph in glyphs)
            {
                if (string.IsNullOrEmpty(glyph.Text))
                {
                    continue;
                }

                if (currentLine.Count == 0)
                {
                    currentLine.Add(glyph);
                    currentTop = glyph.Bounds.Y;
                    continue;
                }

                if (MathF.Abs(glyph.Bounds.Y - currentTop) <= options.SameLineTolerance)
                {
                    currentLine.Add(glyph);
                    currentTop = (currentTop * (currentLine.Count - 1) + glyph.Bounds.Y) / currentLine.Count;
                    continue;
                }

                yield return CreateLine(currentLine, options);
                currentLine.Clear();
                currentLine.Add(glyph);
                currentTop = glyph.Bounds.Y;
            }

            if (currentLine.Count > 0)
            {
                yield return CreateLine(currentLine, options);
            }
        }

        private static bool IsHorizontalDirection(float direction)
        {
            float normalized = direction % 360f;
            if (normalized < 0)
            {
                normalized += 360f;
            }

            return MathF.Abs(normalized) < 0.01f || MathF.Abs(normalized - 180f) < 0.01f;
        }

        private static IReadOnlyList<PdfTextGlyph> OrderLineGlyphs(IReadOnlyList<PdfTextGlyph> glyphs)
        {
            float direction = glyphs[0].Direction % 360f;
            if (direction < 0)
            {
                direction += 360f;
            }

            return MathF.Abs(direction - 180f) < 0.01f
                ? glyphs.OrderByDescending(static glyph => glyph.Bounds.X).ThenBy(static glyph => glyph.Bounds.Y).ToArray()
                : MathF.Abs(direction - 90f) < 0.01f || MathF.Abs(direction - 270f) < 0.01f
                    ? glyphs.OrderBy(static glyph => glyph.Bounds.Y).ThenBy(static glyph => glyph.Bounds.X).ToArray()
                    : glyphs.OrderBy(static glyph => glyph.Bounds.X).ThenBy(static glyph => glyph.Bounds.Y).ToArray();
        }

        private static PdfTextLine CreateLine(IReadOnlyList<PdfTextGlyph> glyphs, PdfLayoutOptions options)
        {
            List<PdfTextRun> runs = CreateRuns(glyphs, options).ToList();
            return new PdfTextLine(
                string.Concat(runs.Select(run => run.Text)),
                PdfLayoutRectangle.Union(runs.Select(run => run.Bounds)),
                runs);
        }

        private static IEnumerable<PdfTextRun> CreateRuns(IReadOnlyList<PdfTextGlyph> glyphs, PdfLayoutOptions options)
        {
            List<PdfTextGlyph> currentRun = new();
            PdfTextGlyph? previous = null;

            foreach (PdfTextGlyph glyph in glyphs)
            {
                if (previous != null && ShouldStartNewRun(previous, glyph, options))
                {
                    yield return CreateRun(currentRun);
                    currentRun.Clear();
                }

                currentRun.Add(glyph);
                previous = glyph;
            }

            if (currentRun.Count > 0)
            {
                yield return CreateRun(currentRun);
            }
        }

        private static bool ShouldStartNewRun(PdfTextGlyph previous, PdfTextGlyph glyph, PdfLayoutOptions options)
        {
            if (previous.MarkedContentId != glyph.MarkedContentId)
            {
                return true;
            }

            if (!string.Equals(previous.FontName, glyph.FontName, StringComparison.Ordinal))
            {
                return true;
            }

            if (MathF.Abs(previous.FontSize - glyph.FontSize) > 0.01f || MathF.Abs(previous.Direction - glyph.Direction) > 0.01f)
            {
                return true;
            }

            if (previous.IsPainted != glyph.IsPainted)
            {
                return true;
            }

            if (!SameColor(previous.Color, glyph.Color))
            {
                return true;
            }

            float gap = glyph.Bounds.X - previous.Bounds.Right;
            float threshold = MathF.Max(previous.Bounds.Height, glyph.Bounds.Height) * options.WordSpacingMultiplier;
            return gap > threshold;
        }

        private static bool SameColor(PdfLayoutColor first, PdfLayoutColor second)
        {
            return MathF.Abs(first.Red - second.Red) < 0.001f &&
                MathF.Abs(first.Green - second.Green) < 0.001f &&
                MathF.Abs(first.Blue - second.Blue) < 0.001f &&
                MathF.Abs(first.Alpha - second.Alpha) < 0.001f &&
                string.Equals(first.ColorSpaceName, second.ColorSpaceName, StringComparison.Ordinal);
        }

        private static PdfTextRun CreateRun(IReadOnlyList<PdfTextGlyph> glyphs)
        {
            StringBuilder text = new();
            foreach (PdfTextGlyph glyph in glyphs)
            {
                text.Append(glyph.Text);
            }

            PdfTextGlyph first = glyphs[0];
            return new PdfTextRun(
                text.ToString(),
                first.FontName,
                first.FontSize,
                first.Direction,
                PdfLayoutRectangle.Union(glyphs.Select(glyph => glyph.Bounds)),
                first.Color,
                glyphs,
                PdfLayoutRectangle.Union(glyphs.Select(glyph => glyph.PageBounds)));
        }

        private sealed class TextLineBucket
        {
            private float _topTotal;

            public TextLineBucket(float direction, float top)
            {
                Direction = direction;
                Top = top;
                Left = float.MaxValue;
            }

            public float Direction { get; }

            public float Top { get; private set; }

            public float Left { get; private set; }

            public float MaximumFontSize { get; private set; }

            public PdfLayoutRectangle Bounds => PdfLayoutRectangle.Union(Glyphs.Select(static glyph => glyph.Bounds));

            public List<PdfTextGlyph> Glyphs { get; } = [];

            public void Add(PdfTextGlyph glyph)
            {
                Glyphs.Add(glyph);
                _topTotal += glyph.Bounds.Y;
                Top = _topTotal / Glyphs.Count;
                Left = MathF.Min(Left, glyph.Bounds.X);
                MaximumFontSize = MathF.Max(MaximumFontSize, glyph.FontSize);
            }

            public void AddRange(IEnumerable<PdfTextGlyph> glyphs)
            {
                foreach (PdfTextGlyph glyph in glyphs)
                {
                    Add(glyph);
                }
            }

            public void RemoveRange(IEnumerable<PdfTextGlyph> glyphs)
            {
                foreach (PdfTextGlyph glyph in glyphs)
                {
                    Glyphs.Remove(glyph);
                }

                PdfTextGlyph[] remainingGlyphs = Glyphs.ToArray();
                Glyphs.Clear();
                _topTotal = 0;
                Top = 0;
                Left = float.MaxValue;
                MaximumFontSize = 0;
                foreach (PdfTextGlyph glyph in remainingGlyphs)
                {
                    Add(glyph);
                }
            }
        }
    }

    private static PdfLayoutColor ResolveGraphicsColor(
        PDColor color,
        float alpha,
        int pageNumber,
        List<PdfLayoutDiagnostic> diagnostics,
        string context,
        PDColorManagementContext? colorManagementContext)
    {
        try
        {
            PDColorSpace? sourceColorSpace = color.GetColorSpace();
            PDColorSpace? effectiveColorSpace = sourceColorSpace is null
                ? null
                : colorManagementContext?.ResolveDeviceColorSpace(sourceColorSpace) ?? sourceColorSpace;
            int rgb = effectiveColorSpace is null || ReferenceEquals(effectiveColorSpace, sourceColorSpace)
                ? color.ToRGB()
                : new PDColor(color.GetComponents(), effectiveColorSpace).ToRGB();
            return new PdfLayoutColor(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f,
                Math.Clamp(alpha, 0f, 1f),
                sourceColorSpace?.GetName());
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new PdfLayoutDiagnostic(
                PdfLayoutDiagnosticSeverity.Warning,
                "color-unresolved",
                $"{context} color could not be resolved: {ex.Message}",
                pageNumber));
            return new PdfLayoutColor(0, 0, 0, Math.Clamp(alpha, 0f, 1f), null);
        }
    }

    private sealed class LayoutGraphicsCollector : PDFGraphicsStreamEngine
    {
        private readonly int _pageNumber;
        private readonly PdfLayoutRectangle _cropBox;
        private readonly int _rotation;
        private readonly bool _includeImages;
        private readonly bool _includeImageAssets;
        private readonly PdfImageExportPolicy _imageExportPolicy;
        private readonly bool _includePaths;
        private readonly ImageAssetCollector _imageAssets;
        private readonly PDColorManagementContext? _colorManagementContext;
        private readonly List<PdfLayoutImage> _images = new();
        private readonly List<PdfLayoutPath> _paths = new();
        private readonly List<float[]?> _effectiveDeviceCmykFills = new();
        private readonly List<PdfLayoutShading> _shadings = new();
        private readonly List<PdfLayoutVectorGroup> _vectorGroups = new();
        private readonly List<PdfLayoutDiagnostic> _diagnostics = new();
        private readonly List<PdfLayoutPaintOperation> _paintOperations = new();
        private readonly Stack<MarkedContentCapture> _markedContent = new();
        private readonly Stack<List<PdfLayoutRectangle>> _vectorGroupContentBounds = new();
        private readonly Stack<VectorGroupBuilder> _activeVectorGroups = new();
        private readonly List<PdfLayoutRectangle> _knockoutTransparencyGroupBounds = new();
        private readonly List<PdfLayoutRectangle> _isolatedDeviceCmykBlendGroupBounds = new();
        private readonly List<PdfLayoutRectangle> _softMaskedTransparencyGroupFallbackBounds = new();
        private readonly List<SoftMaskedTransparencyGroup> _softMaskedTransparencyGroups = new();
        private bool _reportedShapeAlphaPath;
        private int _nextVectorGroupIndex;
        private readonly HashSet<int> _reportedUnsupportedShadingTypes = [];
        private bool _reportedRotatedPath;
        private bool _reportedUnsupportedTextClipping;

        public LayoutGraphicsCollector(
            PDPage page,
            int pageNumber,
            PdfLayoutRectangle cropBox,
            int rotation,
            bool includeImages,
            bool includeImageAssets,
            PdfImageExportPolicy imageExportPolicy,
            bool includePaths,
            ImageAssetCollector imageAssets,
            PDColorManagementContext? colorManagementContext)
            : base(page)
        {
            _pageNumber = pageNumber;
            _cropBox = cropBox;
            _rotation = rotation;
            _includeImages = includeImages;
            _includeImageAssets = includeImageAssets;
            _imageExportPolicy = imageExportPolicy;
            _includePaths = includePaths;
            _imageAssets = imageAssets;
            _colorManagementContext = colorManagementContext;
        }

        public IReadOnlyList<PdfLayoutImage> Images => _images;

        public IReadOnlyList<PdfLayoutPath> Paths => _paths;

        public IReadOnlyList<PdfLayoutShading> Shadings => _shadings;

        public IReadOnlyList<PdfLayoutVectorGroup> VectorGroups => _vectorGroups;

        public IReadOnlyList<PdfLayoutDiagnostic> Diagnostics => _diagnostics;

        public IReadOnlyList<PdfLayoutPaintOperation> PaintOperations => _paintOperations;

        public IReadOnlyList<PdfLayoutRectangle> KnockoutTransparencyGroupBounds => _knockoutTransparencyGroupBounds;

        public IReadOnlyList<PdfLayoutRectangle> IsolatedDeviceCmykBlendGroupBounds =>
            _isolatedDeviceCmykBlendGroupBounds;

        public IReadOnlyList<PdfLayoutRectangle> SoftMaskedTransparencyGroupFallbackBounds =>
            _softMaskedTransparencyGroupFallbackBounds;

        public IReadOnlyList<SoftMaskedTransparencyGroup> SoftMaskedTransparencyGroups => _softMaskedTransparencyGroups;

        protected override void ProcessOperator(ContentOperator op, IList<COSBase> operands)
        {
            bool isEnd = string.Equals(op.GetName(), "EMC", StringComparison.Ordinal);
            if (string.Equals(op.GetName(), "BMC", StringComparison.Ordinal) ||
                string.Equals(op.GetName(), "BDC", StringComparison.Ordinal))
            {
                _markedContent.Push(CreateMarkedContentCapture(
                    _markedContent.TryPeek(out MarkedContentCapture parent) ? parent : default,
                    op.GetName(),
                    operands,
                    GetResources()));
            }

            base.ProcessOperator(op, operands);

            if (isEnd && _markedContent.Count > 0)
            {
                _markedContent.Pop();
            }
        }

        public override void XObject(PDXObject xobject)
        {
            if (xobject is PDImageXObject image)
            {
                if (_includeImages)
                {
                    CollectXObjectImage(image, ResolveSourceName(xobject));
                }

                return;
            }

            if (xobject is PDFormXObject form)
            {
                PDGraphicsState graphicsState = GetGraphicsState();
                bool isTransparencyGroup = xobject is PDTransparencyGroup;
                bool hasSoftMask = isTransparencyGroup && graphicsState.GetSoftMask() is not null;
                bool hasLuminositySoftMask = isTransparencyGroup &&
                    string.Equals(
                        graphicsState.GetSoftMask()?.GetSubType()?.GetName(),
                        "Luminosity",
                        StringComparison.Ordinal);
                float fillOpacity = isTransparencyGroup
                    ? Math.Clamp(graphicsState.GetNonStrokeAlphaConstant(), 0f, 1f)
                    : 1f;
                float strokeOpacity = isTransparencyGroup
                    ? Math.Clamp(graphicsState.GetAlphaConstant(), 0f, 1f)
                    : 1f;
                if (isTransparencyGroup && MathF.Abs(fillOpacity - strokeOpacity) > 0.001f)
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "transparency-group-mixed-alpha",
                        "Transparency group uses distinct stroke and fill alpha values; SVG uses the fill alpha for group compositing.",
                        _pageNumber));
                }

                PDTransparencyGroupAttributes? attributes = form.GetGroup();
                string? blendColorSpaceName = null;
                try
                {
                    blendColorSpaceName = attributes?.GetColorSpace(form.GetResources())?.GetName();
                }
                catch (IOException ex)
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "transparency-group-color-space-unresolved",
                        $"Transparency group blending color space could not be resolved: {ex.Message}",
                        _pageNumber));
                }

                VectorGroupBuilder group = new(
                    _nextVectorGroupIndex++,
                    _activeVectorGroups.TryPeek(out VectorGroupBuilder? parent) ? parent.Index : null,
                    _paths.Count,
                    CurrentClipBounds(graphicsState),
                    fillOpacity,
                    graphicsState.GetBlendMode(),
                    attributes?.IsIsolated() ?? false,
                    attributes?.IsKnockout() ?? false,
                    CurrentClipPaths(graphicsState, skipCount: 1),
                    graphicsState.GetCurrentClippingPaths().Count);
                _activeVectorGroups.Push(group);
                _vectorGroupContentBounds.Push([]);
                try
                {
                    base.XObject(xobject);
                }
                finally
                {
                    List<PdfLayoutRectangle> contentBounds = _vectorGroupContentBounds.Pop();
                    VectorGroupBuilder completedGroup = _activeVectorGroups.Pop();
                    if (contentBounds.Count > 0)
                    {
                        PdfLayoutRectangle bounds = PdfLayoutRectangle.Union(contentBounds);
                        bool containsPaths = _paths.Count > completedGroup.FirstPathIndex;
                        if (containsPaths)
                        {
                            FlattenSimpleNonKnockoutDeviceCmykBlends(completedGroup, blendColorSpaceName);
                            FlattenNativeBlendResultIndicator(completedGroup, blendColorSpaceName);
                            _vectorGroups.Add(completedGroup.Build(_paths.Count - 1, bounds));
                        }

                        if (isTransparencyGroup && attributes?.IsKnockout() == true)
                        {
                            _knockoutTransparencyGroupBounds.Add(bounds);
                        }
                        else if (isTransparencyGroup &&
                            attributes?.IsIsolated() == true &&
                            string.Equals(blendColorSpaceName, "DeviceCMYK", StringComparison.Ordinal) &&
                            IsCompactIsolatedDeviceCmykBlendGroup(bounds) &&
                            _vectorGroups.Any(group =>
                                group.ParentIndex == completedGroup.Index &&
                                group.BlendMode != BlendMode.NORMAL))
                        {
                            _isolatedDeviceCmykBlendGroupBounds.Add(bounds);
                        }

                        if (hasSoftMask)
                        {
                            _softMaskedTransparencyGroupFallbackBounds.Add(bounds);
                        }

                        if (containsPaths &&
                            hasLuminositySoftMask &&
                            DominantGroupFillColor(completedGroup.FirstPathIndex, _paths.Count - 1) is PdfLayoutColor shadowColor)
                        {
                            _softMaskedTransparencyGroups.Add(new SoftMaskedTransparencyGroup(bounds, shadowColor));
                        }
                    }
                }

                return;
            }

            base.XObject(xobject);
        }

        private bool IsCompactIsolatedDeviceCmykBlendGroup(PdfLayoutRectangle bounds) =>
            bounds.Width * bounds.Height <= _cropBox.Width * _cropBox.Height * 0.025f;

        private void FlattenSimpleNonKnockoutDeviceCmykBlends(
            VectorGroupBuilder parent,
            string? blendColorSpaceName)
        {
            if (parent.IsKnockout ||
                !string.Equals(blendColorSpaceName, "DeviceCMYK", StringComparison.Ordinal))
            {
                return;
            }

            foreach (PdfLayoutVectorGroup child in _vectorGroups
                .Where(group => group.ParentIndex == parent.Index)
                .ToArray())
            {
                if (child.Opacity < 0.999f ||
                    child.IsKnockout ||
                    child.FirstPathIndex != child.LastPathIndex ||
                    child.BlendMode == BlendMode.NORMAL)
                {
                    continue;
                }

                int backdropIndex = child.FirstPathIndex - 1;
                if (backdropIndex < parent.FirstPathIndex)
                {
                    continue;
                }

                PdfLayoutPath backdrop = _paths[backdropIndex];
                PdfLayoutPath source = _paths[child.FirstPathIndex];
                int followingPathIndex = child.LastPathIndex + 1;
                if (followingPathIndex < _paths.Count &&
                    IsRectangularFill(source) &&
                    NearlySameRectangle(backdrop.Bounds, source.Bounds, 0.02f) &&
                    LooksLikeDiagonalCross(_paths[followingPathIndex], source.Bounds))
                {
                    continue;
                }

                float[]? backdropComponents = _effectiveDeviceCmykFills[backdropIndex];
                float[]? sourceComponents = _effectiveDeviceCmykFills[child.FirstPathIndex];
                if (!IsOpaqueFill(backdrop) ||
                    !IsOpaqueSimpleFill(source) ||
                    !IsRectangularFill(backdrop) ||
                    !ContainsBounds(backdrop.Bounds, source.Bounds, 0.02f) ||
                    backdropComponents is not { Length: >= 4 } ||
                    sourceComponents is not { Length: >= 4 })
                {
                    continue;
                }

                float[] composed = new float[4];
                BlendComposite.GetInstance(child.BlendMode, 1f).Compose(
                    sourceComponents.AsSpan(0, 4),
                    1f,
                    backdropComponents.AsSpan(0, 4),
                    1f,
                    composed,
                    subtractive: true);
                PdfLayoutColor resolved = ResolveColor(
                    new PDColor(composed, PDDeviceCMYK.Instance),
                    1f,
                    child.FirstPathIndex,
                    "native DeviceCMYK blend");
                ReplacePathFill(child.FirstPathIndex, resolved, composed);
                ReplaceVectorGroupBlendMode(child, BlendMode.NORMAL);
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Info,
                    "native-devicecmyk-blend-flattened",
                    "A simple non-knockout DeviceCMYK blend was precomposed in its declared blending color space.",
                    _pageNumber));
            }
        }

        private void FlattenNativeBlendResultIndicator(
            VectorGroupBuilder parent,
            string? blendColorSpaceName)
        {
            if (!parent.IsIsolated ||
                !string.Equals(blendColorSpaceName, "DeviceCMYK", StringComparison.Ordinal))
            {
                return;
            }

            foreach (PdfLayoutVectorGroup child in _vectorGroups
                .Where(group => group.ParentIndex == parent.Index)
                .ToArray())
            {
                if (child.Opacity < 0.999f ||
                    child.IsKnockout ||
                    child.FirstPathIndex != child.LastPathIndex ||
                    child.BlendMode is not (BlendMode.HARD_LIGHT or BlendMode.COLOR_BURN))
                {
                    continue;
                }

                int backdropIndex = child.FirstPathIndex - 1;
                int resultIndex = child.LastPathIndex + 1;
                if (backdropIndex < parent.FirstPathIndex || resultIndex >= _paths.Count)
                {
                    continue;
                }

                PdfLayoutPath backdrop = _paths[backdropIndex];
                PdfLayoutPath source = _paths[child.FirstPathIndex];
                PdfLayoutPath result = _paths[resultIndex];
                float[]? sourceComponents = _effectiveDeviceCmykFills[child.FirstPathIndex];
                float[]? resultComponents = _effectiveDeviceCmykFills[resultIndex];
                if (!IsOpaqueSimpleFill(backdrop) ||
                    !IsOpaqueSimpleFill(source) ||
                    !IsOpaqueSimpleFill(result) ||
                    !IsRectangularFill(backdrop) ||
                    !IsRectangularFill(source) ||
                    !NearlySameRectangle(backdrop.Bounds, source.Bounds, 0.02f) ||
                    !LooksLikeDiagonalCross(result, source.Bounds) ||
                    sourceComponents is not { Length: >= 4 } ||
                    resultComponents is not { Length: >= 4 } ||
                    !MatchesNativeBlendResult(
                        child.BlendMode,
                        backdrop,
                        sourceComponents,
                        resultComponents))
                {
                    continue;
                }

                ReplacePathFill(child.FirstPathIndex, result.FillColor!.Value, resultComponents);
                ReplaceVectorGroupBlendMode(child, BlendMode.NORMAL);
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Info,
                    "native-blend-result-flattened",
                    "A uniform CMYK transparency blend was flattened to its source-defined result color.",
                    _pageNumber));
            }
        }

        private void ReplaceVectorGroupBlendMode(PdfLayoutVectorGroup group, BlendMode blendMode)
        {
            int groupIndex = _vectorGroups.FindIndex(candidate => candidate.Index == group.Index);
            _vectorGroups[groupIndex] = new PdfLayoutVectorGroup(
                group.Index,
                group.ParentIndex,
                group.FirstPathIndex,
                group.LastPathIndex,
                group.Bounds,
                group.ClipBounds,
                group.Opacity,
                blendMode,
                group.IsIsolated,
                group.IsKnockout,
                group.ClipPaths);
        }

        private bool MatchesNativeBlendResult(
            BlendMode blendMode,
            PdfLayoutPath backdrop,
            IReadOnlyList<float> source,
            IReadOnlyList<float> expected)
        {
            float[]? backdropComponents = _effectiveDeviceCmykFills[backdrop.Index];
            if (backdropComponents is { Length: >= 4 })
            {
                Span<float> composed = stackalloc float[4];
                BlendComposite.GetInstance(blendMode, 1f).Compose(
                    source.Take(4).ToArray(),
                    1f,
                    backdropComponents.AsSpan(0, 4),
                    1f,
                    composed,
                    subtractive: true);
                for (int index = 0; index < 4; index++)
                {
                    if (MathF.Abs(composed[index] - expected[index]) > 0.015f)
                    {
                        return false;
                    }
                }

                return true;
            }

            // A calibrated source color is converted into the explicit CMYK group space by
            // the PDF compositor. The later cross records that intended composite color.
            return backdrop.FillColor?.ColorSpaceName is "Lab" or "ICCBased";
        }

        private static bool IsOpaqueFill(PdfLayoutPath path) =>
            path.FillColor is PdfLayoutColor { Alpha: >= 0.999f } &&
            !path.UsesShapeAlpha &&
            !path.UsesSoftMask;

        private static bool IsOpaqueSimpleFill(PdfLayoutPath path) =>
            IsOpaqueFill(path) && !path.IsStroked;

        private static bool IsRectangularFill(PdfLayoutPath path)
        {
            const float tolerance = 0.02f;
            if (path.Commands.Any(command => command.Kind == PdfLayoutPathCommandKind.CurveTo))
            {
                return false;
            }

            HashSet<int> corners = [];
            foreach (PdfLayoutPathCommand command in path.Commands)
            {
                if (command.Kind is not (PdfLayoutPathCommandKind.MoveTo or PdfLayoutPathCommandKind.LineTo))
                {
                    continue;
                }

                bool left = MathF.Abs(command.X1 - path.Bounds.X) <= tolerance;
                bool right = MathF.Abs(command.X1 - path.Bounds.Right) <= tolerance;
                bool top = MathF.Abs(command.Y1 - path.Bounds.Y) <= tolerance;
                bool bottom = MathF.Abs(command.Y1 - path.Bounds.Bottom) <= tolerance;
                if ((!left && !right) || (!top && !bottom))
                {
                    return false;
                }

                corners.Add((right ? 1 : 0) | (bottom ? 2 : 0));
            }

            return corners.Count == 4;
        }

        private static bool LooksLikeDiagonalCross(
            PdfLayoutPath path,
            PdfLayoutRectangle container)
        {
            if (path.Commands.Count < 12 ||
                path.Commands.Any(command => command.Kind == PdfLayoutPathCommandKind.CurveTo) ||
                path.Bounds.Width <= 0 ||
                path.Bounds.Height <= 0 ||
                path.Bounds.Width / path.Bounds.Height is < 0.9f or > 1.1f ||
                path.Bounds.Width / container.Width is < 0.55f or > 0.85f ||
                path.Bounds.Height / container.Height is < 0.55f or > 0.85f)
            {
                return false;
            }

            float pathCenterX = path.Bounds.X + (path.Bounds.Width / 2f);
            float pathCenterY = path.Bounds.Y + (path.Bounds.Height / 2f);
            float containerCenterX = container.X + (container.Width / 2f);
            float containerCenterY = container.Y + (container.Height / 2f);
            return MathF.Abs(pathCenterX - containerCenterX) <= container.Width * 0.02f &&
                MathF.Abs(pathCenterY - containerCenterY) <= container.Height * 0.02f;
        }

        private PdfLayoutColor? DominantGroupFillColor(int firstPathIndex, int lastPathIndex)
        {
            return _paths
                .Where(path => path.Index >= firstPathIndex && path.Index <= lastPathIndex && path.FillColor.HasValue)
                .OrderByDescending(static path => path.Bounds.Width * path.Bounds.Height)
                .Select(static path => path.FillColor)
                .FirstOrDefault();
        }

        public override void DrawImage(PDImage pdImage)
        {
            if (_includeImages)
            {
                CollectInlineImage(pdImage);
            }
        }

        public override void ShadingFill(COSName shadingName)
        {
            if (!_includePaths || _rotation != 0)
            {
                return;
            }

            PDShading? shading = GetResources()?.GetShading(shadingName);
            if (shading is not PDShadingType2 && shading is not PDShadingType7)
            {
                if (shading is not null && _reportedUnsupportedShadingTypes.Add(shading.GetShadingType()))
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "shading-type-unsupported",
                        $"PDF shading type {shading.GetShadingType().ToString(CultureInfo.InvariantCulture)} is not yet representable as browser SVG.",
                        _pageNumber));
                }

                return;
            }

            try
            {
                if (shading is PDShadingType7 tensor)
                {
                    CollectTensorShading(tensor);
                }
                else
                {
                    CollectShading((PDShadingType2)shading!);
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "shading-collection-failed",
                    "PDF shading could not be collected: " + ex.Message,
                    _pageNumber));
            }
        }

        protected override void ShowGlyph(Matrix textRenderingMatrix, PDFont font, int code, Vector displacement)
        {
            RenderingMode renderingMode = GetGraphicsState().GetTextState().GetRenderingModeInstance();
            if ((renderingMode.IsFill() || renderingMode.IsStroke()) &&
                _vectorGroupContentBounds.Count > 0 &&
                TryGetGlyphBounds(textRenderingMatrix, font, code, displacement) is PdfLayoutRectangle bounds)
            {
                foreach (List<PdfLayoutRectangle> groupContentBounds in _vectorGroupContentBounds)
                {
                    groupContentBounds.Add(bounds);
                }
            }

            if (!_reportedUnsupportedTextClipping &&
                renderingMode.IsClip())
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "path-clipping-unsupported",
                    "Text rendering introduced a glyph clipping path that cannot yet be represented in layout HTML/SVG.",
                    _pageNumber));
                _reportedUnsupportedTextClipping = true;
            }

            base.ShowGlyph(textRenderingMatrix, font, code, displacement);
        }

        private PdfLayoutRectangle? TryGetGlyphBounds(
            Matrix textRenderingMatrix,
            PDFont font,
            int code,
            Vector displacement)
        {
            if (_rotation != 0)
            {
                return null;
            }

            if (font is PDVectorFont vectorFont)
            {
                try
                {
                    GeneralPath path = vectorFont.GetNormalizedPath(code);
                    if (path.Segments.Count > 0)
                    {
                        Matrix glyphMatrix = Matrix.Concatenate(textRenderingMatrix, font.GetFontMatrix());
                        List<PdfLayoutRectangle> points = new(path.Segments.Count * 3);
                        foreach (GeneralPath.Segment segment in path.Segments)
                        {
                            switch (segment.Type)
                            {
                                case GeneralPath.SegmentType.MoveTo:
                                case GeneralPath.SegmentType.LineTo:
                                    points.Add(NormalizeGlyphPoint(segment.X1, segment.Y1, glyphMatrix));
                                    break;
                                case GeneralPath.SegmentType.QuadTo:
                                    points.Add(NormalizeGlyphPoint(segment.X1, segment.Y1, glyphMatrix));
                                    points.Add(NormalizeGlyphPoint(segment.X2, segment.Y2, glyphMatrix));
                                    break;
                                case GeneralPath.SegmentType.CurveTo:
                                    points.Add(NormalizeGlyphPoint(segment.X1, segment.Y1, glyphMatrix));
                                    points.Add(NormalizeGlyphPoint(segment.X2, segment.Y2, glyphMatrix));
                                    points.Add(NormalizeGlyphPoint(segment.X3, segment.Y3, glyphMatrix));
                                    break;
                            }
                        }

                        if (points.Count > 0)
                        {
                            return PdfLayoutRectangle.Union(points);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                }
            }

            return TryGetGlyphMetricBounds(textRenderingMatrix, font, displacement);
        }

        private PdfLayoutRectangle? TryGetGlyphMetricBounds(
            Matrix textRenderingMatrix,
            PDFont font,
            Vector displacement)
        {
            List<Vector> textSpacePoints = [];
            try
            {
                var fontBounds = font.GetBoundingBox();
                Matrix fontMatrix = font.GetFontMatrix();
                Vector[] candidates =
                [
                    fontMatrix.Transform(fontBounds.GetLowerLeftX(), fontBounds.GetLowerLeftY()),
                    fontMatrix.Transform(fontBounds.GetLowerLeftX(), fontBounds.GetUpperRightY()),
                    fontMatrix.Transform(fontBounds.GetUpperRightX(), fontBounds.GetLowerLeftY()),
                    fontMatrix.Transform(fontBounds.GetUpperRightX(), fontBounds.GetUpperRightY())
                ];
                float width = candidates.Max(static point => point.GetX()) -
                    candidates.Min(static point => point.GetX());
                float height = candidates.Max(static point => point.GetY()) -
                    candidates.Min(static point => point.GetY());
                if (candidates.All(static point =>
                        float.IsFinite(point.GetX()) && float.IsFinite(point.GetY())) &&
                    width is > 0.001f and <= 10f &&
                    height is > 0.001f and <= 10f)
                {
                    textSpacePoints.AddRange(candidates);
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
            }

            textSpacePoints.Add(new Vector(0, 0));
            textSpacePoints.Add(displacement);
            if (textSpacePoints.Count == 2)
            {
                if (font.IsVertical())
                {
                    textSpacePoints.Add(new Vector(-0.5f, -0.5f));
                    textSpacePoints.Add(new Vector(0.5f, 0.5f));
                }
                else
                {
                    float right = MathF.Max(0.5f, displacement.GetX());
                    textSpacePoints.Add(new Vector(0, -0.25f));
                    textSpacePoints.Add(new Vector(right, 0.9f));
                }
            }

            PdfLayoutRectangle bounds = PdfLayoutRectangle.Union(textSpacePoints.Select(point =>
            {
                (float x, float y) = NormalizePoint(point.GetX(), point.GetY(), textRenderingMatrix);
                return new PdfLayoutRectangle(x, y, 0, 0);
            }));
            return bounds.Width > 0.001f && bounds.Height > 0.001f ? bounds : null;
        }

        private PdfLayoutRectangle NormalizeGlyphPoint(float x, float y, Matrix glyphMatrix)
        {
            (float normalizedX, float normalizedY) = NormalizePoint(x, y, glyphMatrix);
            return new PdfLayoutRectangle(normalizedX, normalizedY, 0, 0);
        }

        protected override void OnStrokePath(IReadOnlyList<PathSegment> path, PDGraphicsState graphicsState)
        {
            if (_includePaths)
            {
                CollectPath(path, graphicsState, fillRule: null, includeFill: false, includeStroke: true);
            }
        }

        protected override void OnFillPath(int windingRule, IReadOnlyList<PathSegment> path, PDGraphicsState graphicsState)
        {
            if (_includePaths)
            {
                CollectPath(path, graphicsState, windingRule, includeFill: true, includeStroke: false);
            }
        }

        protected override void OnFillAndStrokePath(int windingRule, IReadOnlyList<PathSegment> path, PDGraphicsState graphicsState)
        {
            if (_includePaths)
            {
                CollectPath(path, graphicsState, windingRule, includeFill: true, includeStroke: true);
            }
        }

        private void CollectPath(
            IReadOnlyList<PathSegment> path,
            PDGraphicsState graphicsState,
            int? fillRule,
            bool includeFill,
            bool includeStroke)
        {
            if (path.Count == 0)
            {
                return;
            }

            if (_rotation != 0)
            {
                if (!_reportedRotatedPath)
                {
                    _diagnostics.Add(new PdfLayoutDiagnostic(
                        PdfLayoutDiagnosticSeverity.Warning,
                        "path-rotation-unsupported",
                        "Vector path geometry is not collected for rotated pages yet.",
                        _pageNumber));
                    _reportedRotatedPath = true;
                }

                return;
            }

            PdfLayoutPathCommand[] commands = NormalizePath(path, graphicsState.GetCurrentTransformationMatrix());
            if (commands.Length == 0)
            {
                return;
            }

            int index = _paths.Count;
            PdfLayoutRectangle bounds = Bounds(commands);
            bool usesShapeAlpha = graphicsState.GetAlphaSource();
            bool suppressFill = includeFill && IsOverprintNoOp(graphicsState);
            PdfLayoutRectangle? clipBounds = DistinctPathClipBounds(graphicsState);
            IReadOnlyList<PdfLayoutClipPath> clipPaths = AdditionalPathClipPaths(graphicsState);
            PDColor fillSource = graphicsState.GetNonStrokingColor();
            float fillAlpha = graphicsState.GetNonStrokeAlphaConstant();
            int? precomposedBackdropPathIndex = null;
            PdfLayoutColor? containingBackdropFill = null;
            bool composedDeviceN = false;
            bool composedSeparation = false;
            bool composedDeviceGray = false;
            float[]? effectiveDeviceCmykFill = includeFill && !suppressFill
                ? EffectiveDeviceCmykFill(
                    fillSource,
                    fillAlpha,
                    graphicsState,
                    commands,
                    bounds,
                    fillRule,
                    includeStroke,
                    clipBounds,
                    clipPaths,
                    out precomposedBackdropPathIndex,
                    out containingBackdropFill)
                : null;
            if (effectiveDeviceCmykFill is null && includeFill && !suppressFill)
            {
                effectiveDeviceCmykFill = EffectiveDeviceNOverprintFill(
                    fillSource,
                    fillAlpha,
                    graphicsState,
                    commands,
                    bounds,
                    fillRule,
                    includeStroke,
                    clipBounds,
                    clipPaths,
                    out precomposedBackdropPathIndex);
                composedDeviceN = effectiveDeviceCmykFill is not null;
            }
            if (effectiveDeviceCmykFill is null && includeFill && !suppressFill)
            {
                effectiveDeviceCmykFill = EffectiveSeparationOverprintFill(
                    fillSource,
                    fillAlpha,
                    graphicsState,
                    bounds,
                    out containingBackdropFill);
                composedSeparation = effectiveDeviceCmykFill is not null;
            }
            if (effectiveDeviceCmykFill is null && includeFill && !suppressFill)
            {
                effectiveDeviceCmykFill = EffectiveDeviceGrayOverprintFill(
                    fillSource,
                    fillAlpha,
                    graphicsState,
                    bounds,
                    out containingBackdropFill);
                composedDeviceGray = effectiveDeviceCmykFill is not null;
            }
            PdfLayoutColor? resolvedFill = includeFill && !suppressFill
                ? containingBackdropFill ?? ResolveColor(
                    effectiveDeviceCmykFill is null
                        ? fillSource
                        : new PDColor(effectiveDeviceCmykFill, PDDeviceCMYK.Instance),
                    fillAlpha,
                    index,
                    "fill")
                : null;
            PdfLayoutStrokeStyle? resolvedStroke = includeStroke ? StrokeStyle(graphicsState, index) : null;
            IReadOnlyList<string> colorantNames = effectiveDeviceCmykFill is not null &&
                !composedDeviceN &&
                !composedSeparation &&
                !composedDeviceGray &&
                !includeStroke
                ? DeviceCmykColorants(effectiveDeviceCmykFill)
                : ExplicitColorants(
                    includeFill ? fillSource : null,
                    includeStroke ? graphicsState.GetStrokingColor() : null);
            if (precomposedBackdropPathIndex is int backdropPathIndex &&
                resolvedFill is PdfLayoutColor precomposedFill &&
                effectiveDeviceCmykFill is not null)
            {
                ReplacePathFill(backdropPathIndex, precomposedFill, effectiveDeviceCmykFill);
            }

            NeutralizeCoveredOverprintPair(
                commands,
                bounds,
                fillRule,
                clipBounds,
                clipPaths,
                resolvedFill,
                resolvedStroke,
                graphicsState,
                colorantNames,
                effectiveDeviceCmykFill);

            _paths.Add(new PdfLayoutPath(
                index,
                commands,
                bounds,
                resolvedFill,
                resolvedStroke,
                fillRule,
                usesShapeAlpha,
                colorantNames,
                clipBounds,
                usesSoftMask: graphicsState.GetSoftMask() is not null,
                clipPaths: clipPaths));
            _effectiveDeviceCmykFills.Add(
                effectiveDeviceCmykFill ?? AlternateDeviceCmykComponents(fillSource));
            _paintOperations.Add(new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Path, index));
            bool requiresShapeAlphaFallback = usesShapeAlpha &&
                ((includeFill && graphicsState.GetNonStrokeAlphaConstant() < 0.999f) ||
                    (includeStroke && graphicsState.GetAlphaConstant() < 0.999f));
            if (requiresShapeAlphaFallback && !_reportedShapeAlphaPath)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "shape-alpha-vector-unsupported",
                    "A vector path uses PDF shape-alpha compositing. The HTML converter omits the unsupported vector path instead of rendering an incorrect solid opacity effect.",
                    _pageNumber));
                _reportedShapeAlphaPath = true;
            }
            foreach (List<PdfLayoutRectangle> groupContentBounds in _vectorGroupContentBounds)
            {
                groupContentBounds.Add(bounds);
            }
        }

        private void NeutralizeCoveredOverprintPair(
            IReadOnlyList<PdfLayoutPathCommand> commands,
            PdfLayoutRectangle bounds,
            int? fillRule,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths,
            PdfLayoutColor? fill,
            PdfLayoutStrokeStyle? stroke,
            PDGraphicsState graphicsState,
            IReadOnlyList<string> colorantNames,
            float[]? effectiveDeviceCmykFill)
        {
            if (!graphicsState.IsNonStrokingOverprint() ||
                fill is not PdfLayoutColor coverColor ||
                stroke is null ||
                coverColor.Alpha < 0.999f ||
                stroke.Color.Alpha < 0.999f ||
                !SameColor(coverColor, stroke.Color) ||
                _paths.Count == 0)
            {
                return;
            }

            int previousIndex = _paths.Count - 1;
            PdfLayoutPath previous = _paths[previousIndex];
            if (previous.FillColor is null ||
                previous.Stroke is not PdfLayoutStrokeStyle previousStroke ||
                previous.UsesShapeAlpha ||
                previous.UsesSoftMask ||
                previous.FillRule != fillRule ||
                previous.ClipBounds != clipBounds ||
                !SameClipPaths(previous.ClipPaths, clipPaths) ||
                !NearlySameRectangle(previous.Bounds, bounds, 0.1f) ||
                !SamePathGeometry(previous.Commands, commands, 0.1f) ||
                MathF.Abs(previousStroke.Width - stroke.Width) > 0.01f)
            {
                return;
            }

            // Some prepress PDFs emit an ordinary colored contour followed by an
            // opaque overprinting contour in the background color. Their coordinates
            // can differ by a few hundredths of a point. PDF rasterizers collapse the
            // pair, while independent SVG paths expose a colored antialiasing fringe.
            _paths[previousIndex] = new PdfLayoutPath(
                previous.Index,
                previous.Commands,
                previous.Bounds,
                coverColor,
                new PdfLayoutStrokeStyle(
                    coverColor,
                    previousStroke.Width,
                    previousStroke.LineCap,
                    previousStroke.LineJoin,
                    previousStroke.MiterLimit,
                    previousStroke.DashArray,
                    previousStroke.DashPhase),
                previous.FillRule,
                previous.UsesShapeAlpha,
                colorantNames,
                previous.ClipBounds,
                previous.UsesSoftMask,
                previous.ClipPaths);
            if (effectiveDeviceCmykFill is not null)
            {
                _effectiveDeviceCmykFills[previousIndex] = effectiveDeviceCmykFill.ToArray();
            }
        }

        private static bool SameColor(PdfLayoutColor first, PdfLayoutColor second)
        {
            const float tolerance = 0.5f / 255f;
            return MathF.Abs(first.Red - second.Red) <= tolerance &&
                MathF.Abs(first.Green - second.Green) <= tolerance &&
                MathF.Abs(first.Blue - second.Blue) <= tolerance &&
                MathF.Abs(first.Alpha - second.Alpha) <= tolerance;
        }

        private static bool IsOverprintNoOp(PDGraphicsState graphicsState)
        {
            if (!graphicsState.IsNonStrokingOverprint() || graphicsState.GetOverprintMode() != 1)
            {
                return false;
            }

            PDColor color = graphicsState.GetNonStrokingColor();
            return color.GetColorSpace() is PDDeviceCMYK or PDSeparation &&
                color.GetComponents().All(static component => component == 0f);
        }

        private float[]? EffectiveDeviceCmykFill(
            PDColor color,
            float alpha,
            PDGraphicsState graphicsState,
            IReadOnlyList<PdfLayoutPathCommand> commands,
            PdfLayoutRectangle bounds,
            int? fillRule,
            bool includeStroke,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths,
            out int? precomposedBackdropPathIndex,
            out PdfLayoutColor? containingBackdropFill)
        {
            precomposedBackdropPathIndex = null;
            containingBackdropFill = null;
            if (color.GetColorSpace() is not PDDeviceCMYK || color.GetComponents().Length < 4)
            {
                return null;
            }

            float[] components = color.GetComponents()
                .Take(4)
                .Select(static component => Math.Clamp(component, 0f, 1f))
                .ToArray();
            if (!graphicsState.IsNonStrokingOverprint() ||
                alpha < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null ||
                _paths.Count == 0)
            {
                return components;
            }

            if (graphicsState.GetOverprintMode() != 1)
            {
                DeviceCmykBackdrop? spotBackdrop = FindContainingSpotBackdrop(bounds);
                if (spotBackdrop is not null)
                {
                    float[] composed = AddProcessComponents(spotBackdrop.Value.Components, components);
                    if (SameComponents(composed, spotBackdrop.Value.Components))
                    {
                        containingBackdropFill = _paths[spotBackdrop.Value.PathIndex].FillColor;
                        return composed;
                    }
                }

                return components;
            }

            // Repeated near-identical contours form one visual shape in some OPM1 content.
            // A contained spot backdrop is also safe when this process paint is an exact no-op.
            DeviceCmykBackdrop? backdrop = FindDeviceCmykBackdrop(
                components,
                commands,
                bounds,
                fillRule,
                clipBounds,
                clipPaths);
            if (backdrop is null)
            {
                DeviceCmykBackdrop? containingBackdrop =
                    FindContainingSpotBackdrop(bounds) ??
                    FindContainingProcessBackdrop(bounds, requireSpotColorant: false);
                if (containingBackdrop is not null)
                {
                    float[] composed = AddProcessComponents(containingBackdrop.Value.Components, components);
                    if (SameComponents(composed, containingBackdrop.Value.Components))
                    {
                        containingBackdropFill = _paths[containingBackdrop.Value.PathIndex].FillColor;
                        return composed;
                    }
                }

                return components;
            }

            precomposedBackdropPathIndex = backdrop.Value.PathIndex;
            for (int component = 0; component < 4; component++)
            {
                if (components[component] == 0f)
                {
                    components[component] = backdrop.Value.Components[component];
                }
            }

            return components;
        }

        private float[]? EffectiveSeparationOverprintFill(
            PDColor color,
            float alpha,
            PDGraphicsState graphicsState,
            PdfLayoutRectangle bounds,
            out PdfLayoutColor? containingBackdropFill)
        {
            containingBackdropFill = null;
            if (color.GetColorSpace() is not PDSeparation ||
                AlternateDeviceCmykComponents(color) is not { } sourceComponents ||
                !graphicsState.IsNonStrokingOverprint() ||
                alpha < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null)
            {
                return null;
            }

            DeviceCmykBackdrop? backdrop =
                FindContainingSpotBackdrop(bounds) ??
                FindContainingProcessBackdrop(bounds, requireSpotColorant: false);
            if (backdrop is null)
            {
                return null;
            }

            float[] composed = AddProcessComponents(backdrop.Value.Components, sourceComponents);
            if (!SameComponents(composed, backdrop.Value.Components))
            {
                return null;
            }

            containingBackdropFill = _paths[backdrop.Value.PathIndex].FillColor;
            return composed;
        }

        private float[]? EffectiveDeviceGrayOverprintFill(
            PDColor color,
            float alpha,
            PDGraphicsState graphicsState,
            PdfLayoutRectangle bounds,
            out PdfLayoutColor? containingBackdropFill)
        {
            containingBackdropFill = null;
            if (color.GetColorSpace() is not PDDeviceGray ||
                color.GetComponents() is not { Length: > 0 } grayComponents ||
                !graphicsState.IsNonStrokingOverprint() ||
                alpha < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null)
            {
                return null;
            }

            float[] sourceComponents = [0f, 0f, 0f, 1f - Math.Clamp(grayComponents[0], 0f, 1f)];
            DeviceCmykBackdrop? backdrop =
                FindContainingSpotBackdrop(bounds) ??
                FindContainingProcessBackdrop(bounds, requireSpotColorant: false);
            if (backdrop is null)
            {
                return null;
            }

            float[] composed = AddProcessComponents(backdrop.Value.Components, sourceComponents);
            if (!SameComponents(composed, backdrop.Value.Components))
            {
                return null;
            }

            containingBackdropFill = _paths[backdrop.Value.PathIndex].FillColor;
            return composed;
        }

        private static float[]? AlternateDeviceCmykComponents(PDColor color)
        {
            PDColorSpace? colorSpace = color.GetColorSpace();
            PDFunction? function = colorSpace switch
            {
                PDSeparation separation when separation.GetAlternateColorSpace() is PDDeviceCMYK =>
                    SeparationTintTransform(separation),
                PDDeviceN deviceN when deviceN.GetAlternateColorSpace() is PDDeviceCMYK =>
                    deviceN.GetTintTransform(),
                _ => null
            };
            if (function is null)
            {
                return null;
            }

            try
            {
                float[] transformed = function.Eval(color.GetComponents());
                return transformed.Length >= 4
                    ? transformed.Take(4).Select(static component => Math.Clamp(component, 0f, 1f)).ToArray()
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static PDFunction? SeparationTintTransform(PDSeparation separation) =>
            separation.GetCOSObject() is COSArray array && array.GetObject(3) is COSBase functionObject
                ? PDFunction.Create(functionObject)
                : null;

        private DeviceCmykBackdrop? FindContainingSpotBackdrop(PdfLayoutRectangle bounds) =>
            FindContainingProcessBackdrop(bounds, requireSpotColorant: true);

        private DeviceCmykBackdrop? FindContainingProcessBackdrop(
            PdfLayoutRectangle bounds,
            bool requireSpotColorant)
        {
            const int searchLimit = 32;
            int firstCandidate = Math.Max(0, _paths.Count - searchLimit);
            for (int index = _paths.Count - 1; index >= firstCandidate; index--)
            {
                PdfLayoutPath path = _paths[index];
                if (!BoundsOverlap(path.Bounds, bounds))
                {
                    continue;
                }

                // Stroke-only geometry, such as a panel or cell border, cannot obscure a
                // filled backdrop. Keep looking behind it instead of treating it as an
                // ambiguous overlapping paint operation.
                if (!path.IsFilled || path.FillColor is null)
                {
                    continue;
                }

                float[]? pathComponents = _effectiveDeviceCmykFills[index];
                bool hasSpotColorant = path.ColorantNames.Any(static name => ProcessColorantIndex(name) < 0);
                if (pathComponents is { Length: >= 4 } &&
                    path.FillColor is PdfLayoutColor fill &&
                    fill.Alpha >= 0.999f &&
                    HasUniformFilledPaint(path, fill) &&
                    !path.UsesShapeAlpha &&
                    !path.UsesSoftMask &&
                    hasSpotColorant == requireSpotColorant &&
                    ContainsBounds(path.Bounds, bounds, 0.1f))
                {
                    return new DeviceCmykBackdrop(index, pathComponents);
                }

                return null;
            }

            return null;
        }

        private static bool HasUniformFilledPaint(PdfLayoutPath path, PdfLayoutColor fill)
        {
            if (path.Stroke is not PdfLayoutStrokeStyle stroke)
            {
                return true;
            }

            const float tolerance = 0.5f / 255f;
            return MathF.Abs(fill.Red - stroke.Color.Red) <= tolerance &&
                MathF.Abs(fill.Green - stroke.Color.Green) <= tolerance &&
                MathF.Abs(fill.Blue - stroke.Color.Blue) <= tolerance &&
                MathF.Abs(fill.Alpha - stroke.Color.Alpha) <= tolerance;
        }

        private static float[] AddProcessComponents(
            IReadOnlyList<float> backdrop,
            IReadOnlyList<float> source) =>
            Enumerable.Range(0, 4)
                .Select(index => MathF.Max(backdrop[index], source[index]))
                .ToArray();

        private float[]? EffectiveDeviceNOverprintFill(
            PDColor color,
            float alpha,
            PDGraphicsState graphicsState,
            IReadOnlyList<PdfLayoutPathCommand> commands,
            PdfLayoutRectangle bounds,
            int? fillRule,
            bool includeStroke,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths,
            out int? precomposedBackdropPathIndex)
        {
            precomposedBackdropPathIndex = null;
            if (color.GetColorSpace() is not PDDeviceN deviceN ||
                !graphicsState.IsNonStrokingOverprint() ||
                graphicsState.GetOverprintMode() != 1 ||
                alpha < 0.999f ||
                includeStroke ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null)
            {
                return null;
            }

            float[]? sourceComponents = DeviceNProcessComponents(deviceN, color.GetComponents());
            if (sourceComponents is null)
            {
                return null;
            }

            DeviceCmykBackdrop? backdrop = FindExactProcessBackdrop(
                commands,
                bounds,
                fillRule,
                clipBounds,
                clipPaths);
            if (backdrop is null)
            {
                return null;
            }

            float[] composed = ComposeProcessComponents(backdrop.Value.Components, sourceComponents);
            precomposedBackdropPathIndex = backdrop.Value.PathIndex;
            return composed;
        }

        private DeviceCmykBackdrop? FindExactProcessBackdrop(
            IReadOnlyList<PdfLayoutPathCommand> commands,
            PdfLayoutRectangle bounds,
            int? fillRule,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths)
        {
            const int searchLimit = 12;
            int firstCandidate = Math.Max(0, _paths.Count - searchLimit);
            for (int index = _paths.Count - 1; index >= firstCandidate; index--)
            {
                PdfLayoutPath path = _paths[index];
                if (!BoundsOverlap(path.Bounds, bounds))
                {
                    continue;
                }

                float[]? pathComponents = _effectiveDeviceCmykFills[index];
                if (pathComponents is { Length: >= 4 } &&
                    path.FillColor is PdfLayoutColor fill &&
                    fill.Alpha >= 0.999f &&
                    !path.IsStroked &&
                    !path.UsesShapeAlpha &&
                    !path.UsesSoftMask &&
                    path.FillRule == fillRule &&
                    path.ClipBounds == clipBounds &&
                    SameClipPaths(path.ClipPaths, clipPaths) &&
                    NearlySameRectangle(path.Bounds, bounds) &&
                    SamePathGeometry(path.Commands, commands, 0.0001f))
                {
                    return new DeviceCmykBackdrop(index, pathComponents);
                }

                // An intervening paint at the same location makes local composition ambiguous.
                return null;
            }

            return null;
        }

        private static float[]? DeviceNProcessComponents(PDDeviceN deviceN, IReadOnlyList<float> components)
        {
            IReadOnlyList<string> names = deviceN.GetColorantNames();
            if (components.Count < names.Count)
            {
                return null;
            }

            float[] process = [float.NaN, float.NaN, float.NaN, float.NaN];
            for (int index = 0; index < names.Count; index++)
            {
                string name = names[index];
                if (string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int processIndex = ProcessColorantIndex(name);
                if (processIndex < 0)
                {
                    return null;
                }

                process[processIndex] = Math.Clamp(components[index], 0f, 1f);
            }

            return process.Any(static component => !float.IsNaN(component)) ? process : null;
        }

        private static float[] ComposeProcessComponents(
            IReadOnlyList<float> backdrop,
            IReadOnlyList<float> source)
        {
            float[] composed = backdrop.Take(4).ToArray();
            for (int index = 0; index < composed.Length; index++)
            {
                if (!float.IsNaN(source[index]))
                {
                    composed[index] = source[index];
                }
            }

            return composed;
        }

        private static int ProcessColorantIndex(string name)
        {
            return name.ToUpperInvariant() switch
            {
                "CYAN" => 0,
                "MAGENTA" => 1,
                "YELLOW" => 2,
                "BLACK" => 3,
                _ => -1
            };
        }

        private DeviceCmykBackdrop? FindDeviceCmykBackdrop(
            IReadOnlyList<float> sourceComponents,
            IReadOnlyList<PdfLayoutPathCommand> commands,
            PdfLayoutRectangle bounds,
            int? fillRule,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths)
        {
            const int duplicatePathSearchLimit = 8;
            List<float[]> interveningComponents = [];
            int firstCandidate = Math.Max(0, _paths.Count - duplicatePathSearchLimit);
            for (int index = _paths.Count - 1; index >= firstCandidate; index--)
            {
                PdfLayoutPath path = _paths[index];
                float[]? pathComponents = _effectiveDeviceCmykFills[index];
                if (pathComponents is not { Length: >= 4 } ||
                    path.FillColor is not PdfLayoutColor fill ||
                    fill.Alpha < 0.999f ||
                    path.IsStroked ||
                    path.UsesShapeAlpha ||
                    path.UsesSoftMask ||
                    path.FillRule != fillRule ||
                    path.ClipBounds != clipBounds ||
                    !SameClipPaths(path.ClipPaths, clipPaths) ||
                    !NearlySameRectangle(path.Bounds, bounds) ||
                    !SamePathGeometry(path.Commands, commands, 0.01f))
                {
                    return null;
                }

                if (!SamePathGeometry(path.Commands, commands, 0.0001f))
                {
                    interveningComponents.Add(pathComponents);
                    continue;
                }

                float[] composed = pathComponents.Take(4).ToArray();
                for (int component = 0; component < 4; component++)
                {
                    if (sourceComponents[component] != 0f)
                    {
                        composed[component] = sourceComponents[component];
                    }
                }

                return interveningComponents.Count > 0 &&
                    interveningComponents.All(candidate =>
                        SameComponents(candidate, pathComponents) || SameComponents(candidate, composed))
                    ? new DeviceCmykBackdrop(index, pathComponents)
                    : null;
            }

            return null;
        }

        private void ReplacePathFill(int pathIndex, PdfLayoutColor fill, float[] effectiveComponents)
        {
            PdfLayoutPath path = _paths[pathIndex];
            _paths[pathIndex] = new PdfLayoutPath(
                path.Index,
                path.Commands,
                path.Bounds,
                fill,
                path.Stroke,
                path.FillRule,
                path.UsesShapeAlpha,
                DeviceCmykColorants(effectiveComponents),
                path.ClipBounds,
                path.UsesSoftMask,
                path.ClipPaths);
            _effectiveDeviceCmykFills[pathIndex] = effectiveComponents.ToArray();
        }

        private readonly record struct DeviceCmykBackdrop(int PathIndex, float[] Components);

        private static string[] DeviceCmykColorants(IReadOnlyList<float> components)
        {
            string[] names = ["Cyan", "Magenta", "Yellow", "Black"];
            return names
                .Where((_, index) => index < components.Count && components[index] > 0.0001f)
                .ToArray();
        }

        private static bool NearlySameRectangle(
            PdfLayoutRectangle first,
            PdfLayoutRectangle second,
            float tolerance = 0.01f)
        {
            return MathF.Abs(first.X - second.X) <= tolerance &&
                MathF.Abs(first.Y - second.Y) <= tolerance &&
                MathF.Abs(first.Width - second.Width) <= tolerance &&
                MathF.Abs(first.Height - second.Height) <= tolerance;
        }

        private static bool BoundsOverlap(PdfLayoutRectangle first, PdfLayoutRectangle second)
        {
            return first.X < second.Right &&
                first.Right > second.X &&
                first.Y < second.Bottom &&
                first.Bottom > second.Y;
        }

        private static bool ContainsBounds(
            PdfLayoutRectangle outer,
            PdfLayoutRectangle inner,
            float tolerance) =>
            outer.X - tolerance <= inner.X &&
            outer.Y - tolerance <= inner.Y &&
            outer.Right + tolerance >= inner.Right &&
            outer.Bottom + tolerance >= inner.Bottom;

        private static bool SameComponents(IReadOnlyList<float> first, IReadOnlyList<float> second)
        {
            const float tolerance = 0.0001f;
            return first.Count >= 4 && second.Count >= 4 &&
                Enumerable.Range(0, 4).All(index => MathF.Abs(first[index] - second[index]) <= tolerance);
        }

        private static bool SamePathGeometry(
            IReadOnlyList<PdfLayoutPathCommand> first,
            IReadOnlyList<PdfLayoutPathCommand> second,
            float tolerance)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            for (int index = 0; index < first.Count; index++)
            {
                PdfLayoutPathCommand left = first[index];
                PdfLayoutPathCommand right = second[index];
                if (left.Kind != right.Kind ||
                    MathF.Abs(left.X1 - right.X1) > tolerance ||
                    MathF.Abs(left.Y1 - right.Y1) > tolerance ||
                    MathF.Abs(left.X2 - right.X2) > tolerance ||
                    MathF.Abs(left.Y2 - right.Y2) > tolerance ||
                    MathF.Abs(left.X3 - right.X3) > tolerance ||
                    MathF.Abs(left.Y3 - right.Y3) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameClosedPolygonGeometry(
            IReadOnlyList<PdfLayoutPathCommand> first,
            IReadOnlyList<PdfLayoutPathCommand> second,
            float tolerance)
        {
            static List<(float X, float Y)> PolygonPoints(
                IReadOnlyList<PdfLayoutPathCommand> commands,
                float tolerance)
            {
                if (commands.Any(command => command.Kind is not (
                    PdfLayoutPathCommandKind.MoveTo or
                    PdfLayoutPathCommandKind.LineTo or
                    PdfLayoutPathCommandKind.ClosePath)))
                {
                    return [];
                }

                List<(float X, float Y)> points = commands
                    .Where(command => command.Kind is PdfLayoutPathCommandKind.MoveTo or PdfLayoutPathCommandKind.LineTo)
                    .Select(command => (command.X1, command.Y1))
                    .ToList();
                if (points.Count > 1 &&
                    MathF.Abs(points[0].X - points[^1].X) <= tolerance &&
                    MathF.Abs(points[0].Y - points[^1].Y) <= tolerance)
                {
                    points.RemoveAt(points.Count - 1);
                }

                return points;
            }

            static bool SamePoint((float X, float Y) first, (float X, float Y) second, float tolerance) =>
                MathF.Abs(first.X - second.X) <= tolerance &&
                MathF.Abs(first.Y - second.Y) <= tolerance;

            List<(float X, float Y)> firstPoints = PolygonPoints(first, tolerance);
            List<(float X, float Y)> secondPoints = PolygonPoints(second, tolerance);
            if (firstPoints.Count == 0 || firstPoints.Count != secondPoints.Count)
            {
                return false;
            }

            for (int offset = 0; offset < secondPoints.Count; offset++)
            {
                if (!SamePoint(firstPoints[0], secondPoints[offset], tolerance))
                {
                    continue;
                }

                bool forward = true;
                bool reverse = true;
                for (int index = 1; index < firstPoints.Count; index++)
                {
                    forward &= SamePoint(
                        firstPoints[index],
                        secondPoints[(offset + index) % secondPoints.Count],
                        tolerance);
                    reverse &= SamePoint(
                        firstPoints[index],
                        secondPoints[(offset - index + secondPoints.Count) % secondPoints.Count],
                        tolerance);
                }

                if (forward || reverse)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameClipPaths(
            IReadOnlyList<PdfLayoutClipPath> first,
            IReadOnlyList<PdfLayoutClipPath> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            for (int index = 0; index < first.Count; index++)
            {
                if (first[index].WindingRule != second[index].WindingRule ||
                    !SamePathGeometry(first[index].Commands, second[index].Commands, 0.01f))
                {
                    return false;
                }
            }

            return true;
        }

        private void CollectShading(PDShadingType2 shading)
        {
            float[]? coordinates = shading.GetCoords()?.ToFloatArray();
            int coordinateCount = shading is PDShadingType3 ? 6 : 4;
            if (coordinates is null || coordinates.Length < coordinateCount)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "shading-coordinates-missing",
                    "PDF shading has no usable coordinate array.",
                    _pageNumber));
                return;
            }

            PDGraphicsState graphicsState = GetGraphicsState();
            Matrix ctm = graphicsState.GetCurrentTransformationMatrix();
            (float startX, float startY) = NormalizePoint(coordinates[0], coordinates[1], ctm);
            float startRadius = shading is PDShadingType3
                ? TransformWidth(graphicsState, coordinates[2])
                : 0;
            int endOffset = shading is PDShadingType3 ? 3 : 2;
            (float endX, float endY) = NormalizePoint(coordinates[endOffset], coordinates[endOffset + 1], ctm);
            float endRadius = shading is PDShadingType3
                ? TransformWidth(graphicsState, coordinates[5])
                : 0;
            PdfLayoutRectangle bounds = CurrentClipBounds(graphicsState)
                ?? ShadingBounds(shading, ctm)
                ?? new PdfLayoutRectangle(0, 0, _cropBox.Width, _cropBox.Height);
            PdfLayoutGradientStop[] stops = CreateShadingStops(shading, graphicsState.GetNonStrokeAlphaConstant());
            if (stops.Length < 2 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            _shadings.Add(new PdfLayoutShading(
                _shadings.Count,
                shading.GetShadingType(),
                bounds,
                startX,
                startY,
                startRadius,
                endX,
                endY,
                endRadius,
                stops));
        }

        private void CollectTensorShading(PDShadingType7 shading)
        {
            const int targetTriangleBudget = 2048;

            if (shading.GetCOSObject() is not COSStream stream)
            {
                return;
            }

            PDRange? rangeX = shading.GetDecodeForParameter(0);
            PDRange? rangeY = shading.GetDecodeForParameter(1);
            int bitsPerFlag = shading.GetBitsPerFlag();
            int bitsPerCoordinate = shading.GetBitsPerCoordinate();
            int bitsPerComponent = shading.GetBitsPerComponent();
            int componentCount = shading.GetNumberOfColorComponents();
            if (rangeX is null || rangeY is null || componentCount <= 0 ||
                bitsPerFlag is <= 0 or > 32 || bitsPerCoordinate is <= 0 or > 32 ||
                bitsPerComponent is <= 0 or > 32)
            {
                throw new IOException("Invalid tensor patch mesh parameters.");
            }

            PDRange[] colorRanges = new PDRange[componentCount];
            for (int index = 0; index < componentCount; index++)
            {
                colorRanges[index] = shading.GetDecodeForParameter(index + 2)
                    ?? throw new IOException("Range missing in shading /Decode entry.");
            }

            PDGraphicsState graphicsState = GetGraphicsState();
            Matrix ctm = graphicsState.GetCurrentTransformationMatrix();
            float alpha = graphicsState.GetNonStrokeAlphaConstant();
            List<TensorPatchData> patches = [];
            TensorPatchData? previousPatch = null;
            using Stream input = stream.CreateInputStream();
            BitInput bitInput = new(input);
            while (bitInput.TryReadBits(bitsPerFlag, out uint rawFlag))
            {
                int flag = (int)(rawFlag & 3);
                TensorPatchData? patch = ReadTensorPatch(
                    bitInput,
                    flag,
                    previousPatch,
                    bitsPerCoordinate,
                    bitsPerComponent,
                    componentCount,
                    rangeX,
                    rangeY,
                    colorRanges);
                if (patch is null)
                {
                    break;
                }

                patches.Add(patch);
                previousPatch = patch;
            }

            if (patches.Count == 0)
            {
                return;
            }

            int subdivisions = Math.Clamp(
                (int)Math.Floor(Math.Sqrt(targetTriangleBudget / (2d * patches.Count))),
                1,
                12);
            List<PdfLayoutShadingTriangle> triangles = [];
            foreach (TensorPatchData patch in patches)
            {
                TessellateTensorPatch(patch, shading, ctm, alpha, subdivisions, triangles);
            }

            if (triangles.Count == 0)
            {
                return;
            }

            PdfLayoutRectangle bounds = CurrentClipBounds(graphicsState) ?? TriangleBounds(triangles);
            _shadings.Add(new PdfLayoutShading(
                _shadings.Count,
                shading.GetShadingType(),
                bounds,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                triangles));
        }

        private static TensorPatchData? ReadTensorPatch(
            BitInput input,
            int flag,
            TensorPatchData? previousPatch,
            int bitsPerCoordinate,
            int bitsPerComponent,
            int componentCount,
            PDRange rangeX,
            PDRange rangeY,
            IReadOnlyList<PDRange> colorRanges)
        {
            PointValue[] points = new PointValue[16];
            float[][] colors = Enumerable.Range(0, 4).Select(_ => new float[componentCount]).ToArray();
            int pointStart = 0;
            int colorStart = 0;
            if (flag != 0)
            {
                if (previousPatch is null)
                {
                    return null;
                }

                PointValue[] edge = previousPatch.GetImplicitEdge(flag);
                float[][] implicitColors = previousPatch.GetImplicitColors(flag);
                Array.Copy(edge, points, edge.Length);
                Array.Copy(implicitColors[0], colors[0], componentCount);
                Array.Copy(implicitColors[1], colors[1], componentCount);
                pointStart = 4;
                colorStart = 2;
            }

            ulong maxCoordinate = MaxSample(bitsPerCoordinate);
            ulong maxComponent = MaxSample(bitsPerComponent);
            for (int index = pointStart; index < points.Length; index++)
            {
                if (!input.TryReadBits(bitsPerCoordinate, out uint sourceX) ||
                    !input.TryReadBits(bitsPerCoordinate, out uint sourceY))
                {
                    return null;
                }

                points[index] = new PointValue(
                    Interpolate(sourceX, maxCoordinate, rangeX.GetMin(), rangeX.GetMax()),
                    Interpolate(sourceY, maxCoordinate, rangeY.GetMin(), rangeY.GetMax()));
            }

            for (int corner = colorStart; corner < colors.Length; corner++)
            {
                for (int component = 0; component < componentCount; component++)
                {
                    if (!input.TryReadBits(bitsPerComponent, out uint sourceColor))
                    {
                        return null;
                    }

                    colors[corner][component] = Interpolate(
                        sourceColor,
                        maxComponent,
                        colorRanges[component].GetMin(),
                        colorRanges[component].GetMax());
                }
            }

            return new TensorPatchData(points, colors);
        }

        private void TessellateTensorPatch(
            TensorPatchData patch,
            PDShadingType7 shading,
            Matrix ctm,
            float alpha,
            int subdivisions,
            ICollection<PdfLayoutShadingTriangle> triangles)
        {
            PointValue[][] points = new PointValue[subdivisions + 1][];
            for (int vIndex = 0; vIndex <= subdivisions; vIndex++)
            {
                points[vIndex] = new PointValue[subdivisions + 1];
                double v = vIndex / (double)subdivisions;
                for (int uIndex = 0; uIndex <= subdivisions; uIndex++)
                {
                    double u = uIndex / (double)subdivisions;
                    PointValue point = patch.Evaluate(u, v);
                    (float x, float y) = NormalizePoint(point.X, point.Y, ctm);
                    points[vIndex][uIndex] = new PointValue(x, y);
                }
            }

            for (int vIndex = 0; vIndex < subdivisions; vIndex++)
            {
                for (int uIndex = 0; uIndex < subdivisions; uIndex++)
                {
                    PointValue p00 = points[vIndex][uIndex];
                    PointValue p10 = points[vIndex][uIndex + 1];
                    PointValue p01 = points[vIndex + 1][uIndex];
                    PointValue p11 = points[vIndex + 1][uIndex + 1];
                    AddTensorTriangle(patch, shading, alpha, p00, p10, p01,
                        (uIndex + (1d / 3)) / subdivisions,
                        (vIndex + (1d / 3)) / subdivisions,
                        triangles);
                    AddTensorTriangle(patch, shading, alpha, p01, p10, p11,
                        (uIndex + (2d / 3)) / subdivisions,
                        (vIndex + (2d / 3)) / subdivisions,
                        triangles);
                }
            }
        }

        private void AddTensorTriangle(
            TensorPatchData patch,
            PDShadingType7 shading,
            float alpha,
            PointValue p1,
            PointValue p2,
            PointValue p3,
            double u,
            double v,
            ICollection<PdfLayoutShadingTriangle> triangles)
        {
            float[] parameters = patch.EvaluateColor(u, v);
            float[] components = shading.GetFunction() is null ? parameters : shading.EvalFunction(parameters);
            PdfLayoutColor color = ResolveGraphicsColor(
                new PDColor(components, shading.GetColorSpace()),
                alpha,
                _pageNumber,
                _diagnostics,
                "shading",
                _colorManagementContext);
            triangles.Add(new PdfLayoutShadingTriangle(
                p1.X, p1.Y,
                p2.X, p2.Y,
                p3.X, p3.Y,
                color));
        }

        private static PdfLayoutRectangle TriangleBounds(IReadOnlyList<PdfLayoutShadingTriangle> triangles)
        {
            float left = triangles.Min(triangle => MathF.Min(triangle.X1, MathF.Min(triangle.X2, triangle.X3)));
            float top = triangles.Min(triangle => MathF.Min(triangle.Y1, MathF.Min(triangle.Y2, triangle.Y3)));
            float right = triangles.Max(triangle => MathF.Max(triangle.X1, MathF.Max(triangle.X2, triangle.X3)));
            float bottom = triangles.Max(triangle => MathF.Max(triangle.Y1, MathF.Max(triangle.Y2, triangle.Y3)));
            return new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }

        private static ulong MaxSample(int bits) => bits == 32 ? uint.MaxValue : (1UL << bits) - 1;

        private static float Interpolate(uint source, ulong sourceMax, float destinationMin, float destinationMax)
        {
            return destinationMin + ((float)(source / (double)sourceMax) * (destinationMax - destinationMin));
        }

        private readonly record struct PointValue(float X, float Y);

        private sealed class TensorPatchData
        {
            private readonly PointValue[][] _controlPoints;

            public TensorPatchData(PointValue[] points, float[][] colors)
            {
                Colors = colors;
                _controlPoints = Enumerable.Range(0, 4).Select(_ => new PointValue[4]).ToArray();
                for (int index = 0; index < 4; index++)
                {
                    _controlPoints[0][index] = points[index];
                    _controlPoints[3][index] = points[9 - index];
                }

                _controlPoints[1][0] = points[11];
                _controlPoints[1][1] = points[12];
                _controlPoints[1][2] = points[13];
                _controlPoints[1][3] = points[4];
                _controlPoints[2][0] = points[10];
                _controlPoints[2][1] = points[15];
                _controlPoints[2][2] = points[14];
                _controlPoints[2][3] = points[5];
            }

            public float[][] Colors { get; }

            public PointValue Evaluate(double u, double v)
            {
                double[] bu = Bernstein(u);
                double[] bv = Bernstein(v);
                double x = 0;
                double y = 0;
                for (int row = 0; row < 4; row++)
                {
                    for (int column = 0; column < 4; column++)
                    {
                        double weight = bu[row] * bv[column];
                        x += _controlPoints[row][column].X * weight;
                        y += _controlPoints[row][column].Y * weight;
                    }
                }

                return new PointValue((float)x, (float)y);
            }

            public float[] EvaluateColor(double u, double v)
            {
                float[] result = new float[Colors[0].Length];
                for (int component = 0; component < result.Length; component++)
                {
                    result[component] = (float)(
                        ((1 - v) * (((1 - u) * Colors[0][component]) + (u * Colors[3][component]))) +
                        (v * (((1 - u) * Colors[1][component]) + (u * Colors[2][component]))));
                }

                return result;
            }

            public PointValue[] GetImplicitEdge(int flag)
            {
                return flag switch
                {
                    1 => Enumerable.Range(0, 4).Select(index => _controlPoints[index][3]).ToArray(),
                    2 => Enumerable.Range(0, 4).Select(index => _controlPoints[3][3 - index]).ToArray(),
                    3 => Enumerable.Range(0, 4).Select(index => _controlPoints[3 - index][0]).ToArray(),
                    _ => throw new ArgumentOutOfRangeException(nameof(flag))
                };
            }

            public float[][] GetImplicitColors(int flag)
            {
                return flag switch
                {
                    1 => [Colors[1], Colors[2]],
                    2 => [Colors[2], Colors[3]],
                    3 => [Colors[3], Colors[0]],
                    _ => throw new ArgumentOutOfRangeException(nameof(flag))
                };
            }

            private static double[] Bernstein(double value)
            {
                double inverse = 1 - value;
                return
                [
                    inverse * inverse * inverse,
                    3 * value * inverse * inverse,
                    3 * value * value * inverse,
                    value * value * value
                ];
            }
        }

        private sealed class BitInput(Stream input)
        {
            private int _bitsRemaining;
            private int _currentByte;

            public bool TryReadBits(int count, out uint value)
            {
                value = 0;
                for (int index = 0; index < count; index++)
                {
                    if (_bitsRemaining == 0)
                    {
                        _currentByte = input.ReadByte();
                        if (_currentByte < 0)
                        {
                            return false;
                        }

                        _bitsRemaining = 8;
                    }

                    value = (value << 1) | (uint)((_currentByte >> (_bitsRemaining - 1)) & 1);
                    _bitsRemaining--;
                }

                return true;
            }
        }

        private PdfLayoutRectangle? ShadingBounds(PDShading shading, Matrix ctm)
        {
            PDRectangle? boundingBox = shading.GetBBox();
            if (boundingBox is null)
            {
                return null;
            }

            (float lowerLeftX, float lowerLeftY) = NormalizePoint(
                boundingBox.GetLowerLeftX(),
                boundingBox.GetLowerLeftY(),
                ctm);
            (float upperRightX, float upperRightY) = NormalizePoint(
                boundingBox.GetUpperRightX(),
                boundingBox.GetUpperRightY(),
                ctm);
            float left = MathF.Min(lowerLeftX, upperRightX);
            float top = MathF.Min(lowerLeftY, upperRightY);
            return new PdfLayoutRectangle(
                left,
                top,
                MathF.Abs(upperRightX - lowerLeftX),
                MathF.Abs(upperRightY - lowerLeftY));
        }

        private PdfLayoutGradientStop[] CreateShadingStops(PDShading shading, float alpha)
        {
            const int stopCount = 9;
            float[] domain = shading is PDShadingType2 axial
                ? axial.GetDomain()?.ToFloatArray() ?? [0, 1]
                : [0, 1];
            float domainStart = domain.Length > 0 ? domain[0] : 0;
            float domainEnd = domain.Length > 1 ? domain[1] : 1;
            PdfLayoutGradientStop[] stops = new PdfLayoutGradientStop[stopCount];
            for (int index = 0; index < stopCount; index++)
            {
                float offset = index / (float)(stopCount - 1);
                float input = domainStart + ((domainEnd - domainStart) * offset);
                PdfLayoutColor color = ResolveGraphicsColor(
                    new PDColor(shading.EvalFunction(input), shading.GetColorSpace()),
                    alpha,
                    _pageNumber,
                    _diagnostics,
                    "shading",
                    _colorManagementContext);
                stops[index] = new PdfLayoutGradientStop(offset, color);
            }

            return stops;
        }

        private PdfLayoutStrokeStyle StrokeStyle(PDGraphicsState graphicsState, int index)
        {
            PDLineDashPattern dashPattern = graphicsState.GetLineDashPattern();
            return new PdfLayoutStrokeStyle(
                ResolveColor(graphicsState.GetStrokingColor(), graphicsState.GetAlphaConstant(), index, "stroke"),
                MathF.Max(0.25f, TransformWidth(graphicsState, graphicsState.GetLineWidth())),
                graphicsState.GetLineCap(),
                graphicsState.GetLineJoin(),
                graphicsState.GetMiterLimit(),
                dashPattern.GetDashArray().Select(dash => MathF.Max(0, TransformWidth(graphicsState, dash))).ToArray(),
                MathF.Max(0, TransformWidth(graphicsState, dashPattern.GetPhaseStart())));
        }

        private PdfLayoutRectangle? CurrentClipBounds(PDGraphicsState graphicsState)
        {
            PdfLayoutRectangle? result = null;
            foreach (PDRectangle bounds in graphicsState.GetCurrentClippingBounds())
            {
                PdfLayoutRectangle normalized = NormalizePdfBox(
                    bounds.GetLowerLeftX(),
                    bounds.GetLowerLeftY(),
                    bounds.GetUpperRightX(),
                    bounds.GetUpperRightY());
                result = result is PdfLayoutRectangle existing
                    ? Intersect(existing, normalized)
                    : normalized;
            }

            return result;
        }

        private static PdfLayoutRectangle Intersect(PdfLayoutRectangle first, PdfLayoutRectangle second)
        {
            float left = MathF.Max(first.X, second.X);
            float top = MathF.Max(first.Y, second.Y);
            float right = MathF.Min(first.Right, second.Right);
            float bottom = MathF.Min(first.Bottom, second.Bottom);
            return new PdfLayoutRectangle(
                left,
                top,
                MathF.Max(0, right - left),
                MathF.Max(0, bottom - top));
        }

        private PdfLayoutRectangle? DistinctPathClipBounds(PDGraphicsState graphicsState)
        {
            PdfLayoutRectangle? current = CurrentClipBounds(graphicsState);
            PdfLayoutRectangle baseline = _activeVectorGroups.TryPeek(out VectorGroupBuilder? group) &&
                group.ClipBounds is PdfLayoutRectangle groupClip
                    ? groupClip
                    : new PdfLayoutRectangle(0, 0, _cropBox.Width, _cropBox.Height);
            return current is PdfLayoutRectangle clip && !RectanglesApproximatelyEqual(clip, baseline)
                ? clip
                : null;
        }

        private IReadOnlyList<PdfLayoutClipPath> AdditionalPathClipPaths(PDGraphicsState graphicsState)
        {
            int skipCount = _activeVectorGroups.TryPeek(out VectorGroupBuilder? group)
                ? group.InvokingClipPathCount
                : 1;
            return CurrentClipPaths(graphicsState, skipCount);
        }

        private IReadOnlyList<PdfLayoutClipPath> CurrentClipPaths(PDGraphicsState graphicsState, int skipCount)
        {
            IReadOnlyList<PDGraphicsState.ClippingPath> clippingPaths = graphicsState.GetCurrentClippingPaths();
            if (clippingPaths.Count <= skipCount)
            {
                return [];
            }

            List<PdfLayoutClipPath> result = new(clippingPaths.Count - skipCount);
            for (int index = skipCount; index < clippingPaths.Count; index++)
            {
                PDGraphicsState.ClippingPath clippingPath = clippingPaths[index];
                PdfLayoutPathCommand[] commands = NormalizeClipPath(
                    clippingPath.GetCommands(),
                    clippingPath.CurrentTransformationMatrix);
                if (commands.Length > 0)
                {
                    result.Add(new PdfLayoutClipPath(commands, Bounds(commands), clippingPath.WindingRule));
                }
            }

            return result;
        }

        private static bool RectanglesApproximatelyEqual(
            PdfLayoutRectangle first,
            PdfLayoutRectangle second,
            float tolerance = 0.01f)
        {
            return MathF.Abs(first.X - second.X) <= tolerance &&
                MathF.Abs(first.Y - second.Y) <= tolerance &&
                MathF.Abs(first.Width - second.Width) <= tolerance &&
                MathF.Abs(first.Height - second.Height) <= tolerance;
        }

        private sealed class VectorGroupBuilder
        {
            public VectorGroupBuilder(
                int index,
                int? parentIndex,
                int firstPathIndex,
                PdfLayoutRectangle? clipBounds,
                float opacity,
                BlendMode blendMode,
                bool isIsolated,
                bool isKnockout,
                IReadOnlyList<PdfLayoutClipPath> clipPaths,
                int invokingClipPathCount)
            {
                Index = index;
                ParentIndex = parentIndex;
                FirstPathIndex = firstPathIndex;
                ClipBounds = clipBounds;
                Opacity = opacity;
                BlendMode = blendMode;
                IsIsolated = isIsolated;
                IsKnockout = isKnockout;
                ClipPaths = clipPaths;
                InvokingClipPathCount = invokingClipPathCount;
            }

            public int Index { get; }

            public int? ParentIndex { get; }

            public int FirstPathIndex { get; }

            public PdfLayoutRectangle? ClipBounds { get; }

            public float Opacity { get; }

            public BlendMode BlendMode { get; }

            public bool IsIsolated { get; }

            public bool IsKnockout { get; }

            public IReadOnlyList<PdfLayoutClipPath> ClipPaths { get; }

            public int InvokingClipPathCount { get; }

            public PdfLayoutVectorGroup Build(int lastPathIndex, PdfLayoutRectangle bounds)
            {
                return new PdfLayoutVectorGroup(
                    Index,
                    ParentIndex,
                    FirstPathIndex,
                    lastPathIndex,
                    bounds,
                    ClipBounds,
                    Opacity,
                    BlendMode,
                    IsIsolated,
                    IsKnockout,
                    ClipPaths);
            }
        }

        private PdfLayoutColor ResolveColor(PDColor color, float alpha, int index, string paintKind)
        {
            return ResolveGraphicsColor(
                color,
                alpha,
                _pageNumber,
                _diagnostics,
                $"path {index.ToString(CultureInfo.InvariantCulture)} {paintKind}",
                _colorManagementContext);
        }

        private PdfLayoutPathCommand[] NormalizePath(IReadOnlyList<PathSegment> path, Matrix ctm)
        {
            List<PdfLayoutPathCommand> commands = new(path.Count);
            foreach (PathSegment segment in path)
            {
                switch (segment.Type)
                {
                    case PathSegmentType.MoveTo:
                        {
                            (float x, float y) = NormalizePoint(segment.X1, segment.Y1, ctm);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, x, y, 0, 0, 0, 0));
                            break;
                        }
                    case PathSegmentType.LineTo:
                        {
                            (float x, float y) = NormalizePoint(segment.X1, segment.Y1, ctm);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, x, y, 0, 0, 0, 0));
                            break;
                        }
                    case PathSegmentType.CurveTo:
                        {
                            (float x1, float y1) = NormalizePoint(segment.X1, segment.Y1, ctm);
                            (float x2, float y2) = NormalizePoint(segment.X2, segment.Y2, ctm);
                            (float x3, float y3) = NormalizePoint(segment.X3, segment.Y3, ctm);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.CurveTo, x1, y1, x2, y2, x3, y3));
                            break;
                        }
                    case PathSegmentType.Close:
                        commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0, 0, 0, 0, 0, 0));
                        break;
                }
            }

            return commands.ToArray();
        }

        private PdfLayoutPathCommand[] NormalizeClipPath(
            IReadOnlyList<PDGraphicsState.ClippingPathCommand> path,
            Matrix ctm)
        {
            List<PdfLayoutPathCommand> commands = new(path.Count);
            foreach (PDGraphicsState.ClippingPathCommand command in path)
            {
                switch (command.Type)
                {
                    case PDGraphicsState.ClippingPathCommandType.MoveTo:
                        {
                            (float x, float y) = NormalizePoint(command.X1, command.Y1, ctm);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, x, y, 0, 0, 0, 0));
                            break;
                        }
                    case PDGraphicsState.ClippingPathCommandType.LineTo:
                        {
                            (float x, float y) = NormalizePoint(command.X1, command.Y1, ctm);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, x, y, 0, 0, 0, 0));
                            break;
                        }
                    case PDGraphicsState.ClippingPathCommandType.CurveTo:
                        {
                            (float x1, float y1) = NormalizePoint(command.X1, command.Y1, ctm);
                            (float x2, float y2) = NormalizePoint(command.X2, command.Y2, ctm);
                            (float x3, float y3) = NormalizePoint(command.X3, command.Y3, ctm);
                            commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.CurveTo, x1, y1, x2, y2, x3, y3));
                            break;
                        }
                    case PDGraphicsState.ClippingPathCommandType.Close:
                        commands.Add(new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0, 0, 0, 0, 0, 0));
                        break;
                }
            }

            return commands.ToArray();
        }

        private (float X, float Y) NormalizePoint(float x, float y, Matrix ctm)
        {
            Vector point = ctm.Transform(x, y);
            if (_rotation != 0)
            {
                return NormalizeRotatedImagePoint(point);
            }

            float cropTop = _cropBox.Y + _cropBox.Height;
            return (point.GetX() - _cropBox.X, cropTop - point.GetY());
        }

        private static PdfLayoutRectangle Bounds(IReadOnlyList<PdfLayoutPathCommand> commands)
        {
            List<PdfLayoutRectangle> points = new();
            foreach (PdfLayoutPathCommand command in commands)
            {
                switch (command.Kind)
                {
                    case PdfLayoutPathCommandKind.MoveTo:
                    case PdfLayoutPathCommandKind.LineTo:
                        points.Add(new PdfLayoutRectangle(command.X1, command.Y1, 0, 0));
                        break;
                    case PdfLayoutPathCommandKind.CurveTo:
                        points.Add(new PdfLayoutRectangle(command.X1, command.Y1, 0, 0));
                        points.Add(new PdfLayoutRectangle(command.X2, command.Y2, 0, 0));
                        points.Add(new PdfLayoutRectangle(command.X3, command.Y3, 0, 0));
                        break;
                }
            }

            return PdfLayoutRectangle.Union(points);
        }

        private static float TransformWidth(PDGraphicsState graphicsState, float width)
        {
            Matrix ctm = graphicsState.GetCurrentTransformationMatrix();
            float x = ctm.GetScaleX() + ctm.GetShearX();
            float y = ctm.GetScaleY() + ctm.GetShearY();
            return width * MathF.Sqrt(((x * x) + (y * y)) * 0.5f);
        }

        private void CollectXObjectImage(PDImageXObject image, string? sourceName)
        {
            Matrix ctm = GetGraphicsState().GetCurrentTransformationMatrix();
            (PdfLayoutRectangle bounds, PdfLayoutTransform transform) = NormalizeImageGeometry(ctm);
            int index = _images.Count;
            IReadOnlyList<PdfLayoutClipPath> clipPaths = CurrentClipPaths(GetGraphicsState(), skipCount: 1);
            string assetId = $"page-{_pageNumber.ToString(CultureInfo.InvariantCulture)}-image-{index.ToString(CultureInfo.InvariantCulture)}";
            if (_includeImageAssets)
            {
                assetId = ExportXObjectImageAsset(image, assetId, index, GetGraphicsState());
            }

            PdfLayoutColor? overprintCompositeColor = ResolveUniformIndexedDeviceNOverprint(
                image,
                GetGraphicsState(),
                clipPaths,
                index) ?? ResolveUniformSeparationImageBackdrop(
                    image,
                    GetGraphicsState(),
                    bounds,
                    CurrentClipBounds(GetGraphicsState())) ?? ResolveStencilOverprint(
                    image,
                    GetGraphicsState(),
                    bounds,
                    index);

            _images.Add(new PdfLayoutImage(
                index,
                assetId,
                PdfLayoutImageKind.XObject,
                bounds,
                transform,
                image.GetWidth(),
                image.GetHeight(),
                image.GetBitsPerComponent(),
                ColorSpaceName(image, index),
                image.GetInterpolate(),
                sourceName,
                GetGraphicsState().IsNonStrokingOverprint() || GetGraphicsState().IsOverprint(),
                ExplicitColorants(image.GetColorSpace()),
                clipPaths,
                overprintCompositeColor,
                IsBlackOnlyProcessImageOverprint(
                    image,
                    GetGraphicsState(),
                    bounds,
                    CurrentClipBounds(GetGraphicsState()),
                    clipPaths),
                CurrentMarkedContentId()));
            _paintOperations.Add(new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Image, index));
        }

        private PdfLayoutColor? ResolveUniformIndexedDeviceNOverprint(
            PDImageXObject image,
            PDGraphicsState graphicsState,
            IReadOnlyList<PdfLayoutClipPath> clipPaths,
            int imageIndex)
        {
            if (!graphicsState.IsNonStrokingOverprint() ||
                graphicsState.GetOverprintMode() != 1 ||
                graphicsState.GetNonStrokeAlphaConstant() < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null ||
                !TryGetUniformIndexedDeviceNProcessComponents(image, out float[] sourceComponents))
            {
                return null;
            }

            DeviceCmykBackdrop? backdrop = FindImageClipProcessBackdrop(clipPaths);
            if (backdrop is null)
            {
                return null;
            }

            float[] composed = ComposeProcessComponents(backdrop.Value.Components, sourceComponents);
            PdfLayoutColor resolved = ResolveColor(
                new PDColor(composed, PDDeviceCMYK.Instance),
                1f,
                imageIndex,
                "DeviceN image overprint");
            ReplacePathFill(backdrop.Value.PathIndex, resolved, composed);
            return resolved;
        }

        private PdfLayoutColor? ResolveStencilOverprint(
            PDImageXObject image,
            PDGraphicsState graphicsState,
            PdfLayoutRectangle bounds,
            int imageIndex)
        {
            COSDictionary? dictionary = image.GetCOSObject();
            if (dictionary is null ||
                !dictionary.GetBoolean(COSName.GetPDFName("ImageMask"), false) ||
                !graphicsState.IsNonStrokingOverprint() ||
                graphicsState.GetOverprintMode() != 1 ||
                graphicsState.GetNonStrokeAlphaConstant() < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null)
            {
                return null;
            }

            PDColor source = graphicsState.GetNonStrokingColor();
            float[]? sourceComponents;
            bool requireSpotBackdrop;
            if (source.GetColorSpace() is PDDeviceCMYK && source.GetComponents().Length >= 4)
            {
                sourceComponents = source.GetComponents().Take(4).ToArray();
                requireSpotBackdrop = true;
            }
            else
            {
                sourceComponents = source.GetColorSpace() is PDSeparation
                    ? AlternateDeviceCmykComponents(source)
                    : null;
                requireSpotBackdrop = false;
            }

            if (sourceComponents is null)
            {
                return null;
            }

            DeviceCmykBackdrop? backdrop = FindContainingProcessBackdrop(bounds, requireSpotBackdrop);
            if (backdrop is null)
            {
                return null;
            }

            float[] composed = AddProcessComponents(backdrop.Value.Components, sourceComponents);
            if (!SameComponents(composed, backdrop.Value.Components))
            {
                return null;
            }

            if (requireSpotBackdrop)
            {
                return _paths[backdrop.Value.PathIndex].FillColor;
            }

            return ResolveColor(
                new PDColor(composed, PDDeviceCMYK.Instance),
                1f,
                imageIndex,
                "stencil overprint");
        }

        private PdfLayoutColor? ResolveUniformSeparationImageBackdrop(
            PDImageXObject image,
            PDGraphicsState graphicsState,
            PdfLayoutRectangle bounds,
            PdfLayoutRectangle? clipBounds)
        {
            if (graphicsState.GetNonStrokeAlphaConstant() < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null ||
                image.GetBitsPerComponent() != 8 ||
                image.GetColorSpace() is not PDSeparation separation ||
                image.GetCOSObject()?.GetDictionaryObject(COSName.DECODE) is not null)
            {
                return null;
            }

            byte[] samples = image.GetImageData();
            int expectedSamples = image.GetWidth() * image.GetHeight();
            if (samples.Length != expectedSamples ||
                samples.Length == 0 ||
                samples.Any(sample => sample != samples[0]) ||
                AlternateDeviceCmykComponents(
                    new PDColor([samples[0] / 255f], separation)) is not { } sourceComponents)
            {
                return null;
            }

            PdfLayoutRectangle visibleBounds = clipBounds is PdfLayoutRectangle clip
                ? Intersect(bounds, clip)
                : bounds;
            if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
            {
                return null;
            }

            DeviceCmykBackdrop? backdrop = FindContainingSpotBackdrop(visibleBounds);
            if (backdrop is null ||
                !_paths[backdrop.Value.PathIndex].ColorantNames.Contains(
                    separation.GetColorantName(),
                    StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return SameComponents(sourceComponents, backdrop.Value.Components)
                ? _paths[backdrop.Value.PathIndex].FillColor
                : null;
        }

        private bool IsBlackOnlyProcessImageOverprint(
            PDImageXObject image,
            PDGraphicsState graphicsState,
            PdfLayoutRectangle bounds,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths)
        {
            if (!graphicsState.IsNonStrokingOverprint() ||
                graphicsState.GetOverprintMode() != 1 ||
                graphicsState.GetNonStrokeAlphaConstant() < 0.999f ||
                graphicsState.GetAlphaSource() ||
                graphicsState.GetSoftMask() is not null ||
                image.GetBitsPerComponent() != 8)
            {
                return false;
            }

            if (image.GetColorSpace() is PDDeviceGray)
            {
                return HasContainingSpotImageBackdrop(bounds, clipBounds, clipPaths);
            }

            if (image.GetColorSpace() is not PDIndexed indexed ||
                indexed.GetBaseColorSpace() is not PDDeviceCMYK ||
                !TryGetIndexedLookupBytes(indexed, out byte[] lookup))
            {
                return false;
            }

            byte[] samples = image.GetImageData();
            if (samples.Length != image.GetWidth() * image.GetHeight() || samples.Length == 0)
            {
                return false;
            }

            foreach (byte sample in samples.Distinct())
            {
                int offset = sample * 4;
                if (offset < 0 || offset + 4 > lookup.Length ||
                    lookup[offset] != 0 ||
                    lookup[offset + 1] != 0 ||
                    lookup[offset + 2] != 0)
                {
                    return false;
                }
            }

            return HasContainingSpotImageBackdrop(bounds, clipBounds, clipPaths);
        }

        private bool HasContainingSpotImageBackdrop(
            PdfLayoutRectangle bounds,
            PdfLayoutRectangle? clipBounds,
            IReadOnlyList<PdfLayoutClipPath> clipPaths)
        {
            if (clipPaths.Any(static clipPath => !IsRectangularClipPath(clipPath)))
            {
                return false;
            }

            PdfLayoutRectangle visibleBounds = clipBounds is PdfLayoutRectangle clip
                ? Intersect(bounds, clip)
                : bounds;
            return visibleBounds.Width > 0 &&
                visibleBounds.Height > 0 &&
                FindContainingSpotBackdrop(visibleBounds) is not null;
        }

        private static bool IsRectangularClipPath(PdfLayoutClipPath clipPath)
        {
            const float tolerance = 0.1f;
            HashSet<int> corners = [];
            foreach (PdfLayoutPathCommand command in clipPath.Commands)
            {
                if (command.Kind == PdfLayoutPathCommandKind.CurveTo)
                {
                    return false;
                }

                if (command.Kind is not (PdfLayoutPathCommandKind.MoveTo or PdfLayoutPathCommandKind.LineTo))
                {
                    continue;
                }

                bool left = MathF.Abs(command.X1 - clipPath.Bounds.X) <= tolerance;
                bool right = MathF.Abs(command.X1 - clipPath.Bounds.Right) <= tolerance;
                bool top = MathF.Abs(command.Y1 - clipPath.Bounds.Y) <= tolerance;
                bool bottom = MathF.Abs(command.Y1 - clipPath.Bounds.Bottom) <= tolerance;
                if ((!left && !right) || (!top && !bottom))
                {
                    return false;
                }

                corners.Add((right ? 1 : 0) | (bottom ? 2 : 0));
            }

            return corners.Count == 4;
        }

        private DeviceCmykBackdrop? FindImageClipProcessBackdrop(IReadOnlyList<PdfLayoutClipPath> clipPaths)
        {
            const int searchLimit = 12;
            foreach (PdfLayoutClipPath clipPath in clipPaths.Reverse())
            {
                int firstCandidate = Math.Max(0, _paths.Count - searchLimit);
                for (int index = _paths.Count - 1; index >= firstCandidate; index--)
                {
                    PdfLayoutPath path = _paths[index];
                    if (!BoundsOverlap(path.Bounds, clipPath.Bounds))
                    {
                        continue;
                    }

                    float[]? pathComponents = _effectiveDeviceCmykFills[index];
                    if (pathComponents is { Length: >= 4 } &&
                        path.FillColor is PdfLayoutColor fill &&
                        fill.Alpha >= 0.999f &&
                        !path.IsStroked &&
                        !path.UsesShapeAlpha &&
                        !path.UsesSoftMask &&
                        path.FillRule == clipPath.WindingRule &&
                        NearlySameRectangle(path.Bounds, clipPath.Bounds) &&
                        SameClosedPolygonGeometry(path.Commands, clipPath.Commands, 0.01f))
                    {
                        return new DeviceCmykBackdrop(index, pathComponents);
                    }

                    return null;
                }
            }

            return null;
        }

        private static bool TryGetUniformIndexedDeviceNProcessComponents(
            PDImageXObject image,
            out float[] processComponents)
        {
            processComponents = [];
            if (image.GetBitsPerComponent() != 8 ||
                image.GetColorSpace() is not PDIndexed indexed ||
                indexed.GetBaseColorSpace() is not PDDeviceN deviceN ||
                image.GetCOSObject()?.GetDictionaryObject(COSName.DECODE) is not null)
            {
                return false;
            }

            byte[] samples = image.GetImageData();
            int expectedSamples = image.GetWidth() * image.GetHeight();
            if (samples.Length != expectedSamples ||
                samples.Length == 0 ||
                samples.Any(sample => sample != samples[0]) ||
                !TryGetIndexedLookupBytes(indexed, out byte[]? lookup))
            {
                return false;
            }

            int componentCount = deviceN.GetColorantNames().Count;
            int offset = samples[0] * componentCount;
            if (componentCount == 0 || offset < 0 || offset + componentCount > lookup.Length)
            {
                return false;
            }

            float[] components = lookup
                .Skip(offset)
                .Take(componentCount)
                .Select(static value => value / 255f)
                .ToArray();
            float[]? mappedComponents = DeviceNProcessComponents(deviceN, components);
            if (mappedComponents is null)
            {
                return false;
            }

            processComponents = mappedComponents;
            return true;
        }

        private static bool TryGetIndexedLookupBytes(PDIndexed indexed, out byte[] data)
        {
            data = [];
            if (indexed.GetCOSObject() is not COSArray array || array.GetObject(3) is not COSBase lookup)
            {
                return false;
            }

            switch (lookup)
            {
                case COSString text:
                    data = text.GetBytes();
                    return true;
                case COSStream stream:
                    using (Stream input = stream.CreateInputStream())
                    using (MemoryStream output = new())
                    {
                        input.CopyTo(output);
                        data = output.ToArray();
                    }
                    return true;
                default:
                    return false;
            }
        }

        private void CollectInlineImage(PDImage image)
        {
            Matrix ctm = GetGraphicsState().GetCurrentTransformationMatrix();
            (PdfLayoutRectangle bounds, PdfLayoutTransform transform) = NormalizeImageGeometry(ctm);
            int index = _images.Count;
            string assetId = $"page-{_pageNumber.ToString(CultureInfo.InvariantCulture)}-image-{index.ToString(CultureInfo.InvariantCulture)}";
            if (_includeImageAssets)
            {
                assetId = ExportInlineImageAsset(image, assetId, index, GetGraphicsState());
            }

            _images.Add(new PdfLayoutImage(
                index,
                assetId,
                PdfLayoutImageKind.InlineImage,
                bounds,
                transform,
                image.GetWidth(),
                image.GetHeight(),
                image.GetBitsPerComponent(),
                ColorSpaceName(image, index),
                image.GetInterpolate(),
                null,
                GetGraphicsState().IsNonStrokingOverprint() || GetGraphicsState().IsOverprint(),
                ExplicitColorants(image.GetColorSpace()),
                CurrentClipPaths(GetGraphicsState(), skipCount: 1),
                markedContentId: CurrentMarkedContentId()));
            _paintOperations.Add(new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Image, index));
        }

        private int? CurrentMarkedContentId() =>
            _markedContent.TryPeek(out MarkedContentCapture capture) && !capture.IsArtifact
                ? capture.MarkedContentId
                : null;

        private (PdfLayoutRectangle Bounds, PdfLayoutTransform Transform) NormalizeImageGeometry(Matrix ctm)
        {
            Vector lowerLeft = ctm.TransformPoint(0, 0);
            Vector lowerRight = ctm.TransformPoint(1, 0);
            Vector upperRight = ctm.TransformPoint(1, 1);
            Vector upperLeft = ctm.TransformPoint(0, 1);
            if (_rotation == 0)
            {
                float minX = Min(lowerLeft.GetX(), lowerRight.GetX(), upperRight.GetX(), upperLeft.GetX());
                float maxX = Max(lowerLeft.GetX(), lowerRight.GetX(), upperRight.GetX(), upperLeft.GetX());
                float minY = Min(lowerLeft.GetY(), lowerRight.GetY(), upperRight.GetY(), upperLeft.GetY());
                float maxY = Max(lowerLeft.GetY(), lowerRight.GetY(), upperRight.GetY(), upperLeft.GetY());
                return (NormalizePdfBox(minX, minY, maxX, maxY), PdfLayoutTransform.FromMatrix(ctm));
            }

            (float X, float Y) normalizedLowerLeft = NormalizeRotatedImagePoint(lowerLeft);
            (float X, float Y) normalizedLowerRight = NormalizeRotatedImagePoint(lowerRight);
            (float X, float Y) normalizedUpperRight = NormalizeRotatedImagePoint(upperRight);
            (float X, float Y) normalizedUpperLeft = NormalizeRotatedImagePoint(upperLeft);
            float normalizedMinX = Min(normalizedLowerLeft.X, normalizedLowerRight.X, normalizedUpperRight.X, normalizedUpperLeft.X);
            float normalizedMaxX = Max(normalizedLowerLeft.X, normalizedLowerRight.X, normalizedUpperRight.X, normalizedUpperLeft.X);
            float normalizedMinY = Min(normalizedLowerLeft.Y, normalizedLowerRight.Y, normalizedUpperRight.Y, normalizedUpperLeft.Y);
            float normalizedMaxY = Max(normalizedLowerLeft.Y, normalizedLowerRight.Y, normalizedUpperRight.Y, normalizedUpperLeft.Y);
            return (
                new PdfLayoutRectangle(
                    normalizedMinX,
                    normalizedMinY,
                    MathF.Max(0, normalizedMaxX - normalizedMinX),
                    MathF.Max(0, normalizedMaxY - normalizedMinY)),
                NormalizeRotatedImageTransform(ctm));
        }

        private (float X, float Y) NormalizeRotatedImagePoint(Vector point)
        {
            float cropRight = _cropBox.X + _cropBox.Width;
            float cropTop = _cropBox.Y + _cropBox.Height;
            return _rotation switch
            {
                90 => (point.GetY() - _cropBox.Y, point.GetX() - _cropBox.X),
                180 => (cropRight - point.GetX(), point.GetY() - _cropBox.Y),
                270 => (cropTop - point.GetY(), cropRight - point.GetX()),
                _ => (point.GetX() - _cropBox.X, cropTop - point.GetY())
            };
        }

        private PdfLayoutTransform NormalizeRotatedImageTransform(Matrix ctm)
        {
            float a = ctm.GetScaleX();
            float b = ctm.GetShearY();
            float c = ctm.GetShearX();
            float d = ctm.GetScaleY();
            float e = ctm.GetTranslateX();
            float f = ctm.GetTranslateY();
            float cropRight = _cropBox.X + _cropBox.Width;
            float cropTop = _cropBox.Y + _cropBox.Height;
            return _rotation switch
            {
                90 => new PdfLayoutTransform(b, a, d, c, f - _cropBox.Y, e - _cropBox.X),
                180 => new PdfLayoutTransform(-a, b, -c, d, cropRight - e, f - _cropBox.Y),
                270 => new PdfLayoutTransform(-b, -a, -d, -c, cropTop - f, cropRight - e),
                _ => PdfLayoutTransform.FromMatrix(ctm)
            };
        }

        private string ExportXObjectImageAsset(
            PDImageXObject image,
            string assetId,
            int index,
            PDGraphicsState graphicsState)
        {
            EnsureImageExportBackendIfRequired(index);
            try
            {
                if (image.GetCOSObject()?.GetBoolean(COSName.GetPDFName("ImageMask"), false) == true)
                {
                    PdfLayoutColor color = ResolveColor(
                        graphicsState.GetNonStrokingColor(),
                        graphicsState.GetNonStrokeAlphaConstant(),
                        index,
                        "image mask");
                    byte[] stencilData = ExportStencilImagePng(
                        image.GetImageData(),
                        image.GetWidth(),
                        image.GetHeight(),
                        image.GetCOSObject()?.GetCOSArray(COSName.DECODE),
                        color);
                    return _imageAssets.Add(assetId, "png", "image/png", stencilData).AssetId;
                }

                if (TryExportOneBitGrayscalePng(image, out byte[]? pngData, out PdfLayoutColor? uniformColor))
                {
                    return _imageAssets.Add(assetId, "png", "image/png", pngData, uniformColor).AssetId;
                }

                PdfImageExportResult result = PdfImageExporter.ExportForBrowser(image, _colorManagementContext);
                return _imageAssets.Add(
                    assetId,
                    result.FileExtension,
                    result.ContentType,
                    result.Data).AssetId;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return HandleImageExportFailure(assetId, index, ex);
            }
        }

        private bool TryExportOneBitGrayscalePng(
            PDImageXObject image,
            out byte[] data,
            out PdfLayoutColor? uniformColor)
        {
            data = [];
            uniformColor = null;
            PDColorSpace colorSpace = image.GetColorSpace();
            if (image.GetBitsPerComponent() != 1 ||
                colorSpace is not PDDeviceGray ||
                image.GetSoftMask() != null)
            {
                return false;
            }

            COSDictionary? dictionary = image.GetCOSObject();
            if (dictionary == null ||
                dictionary.GetDictionaryObject(COSName.GetPDFName("Mask")) != null ||
                dictionary.GetBoolean(COSName.GetPDFName("ImageMask"), false) ||
                !TryGetOneBitDecodeInversion(dictionary.GetCOSArray(COSName.DECODE), out bool invert))
            {
                return false;
            }

            int width = image.GetWidth();
            int height = image.GetHeight();
            int rowBytes = (width + 7) / 8;
            byte[] samples = image.GetImageData();
            if (width <= 0 || height <= 0 || samples.Length != rowBytes * height)
            {
                return false;
            }

            PDColorSpace outputColorSpace = _colorManagementContext?.ResolveDeviceColorSpace(colorSpace) ?? colorSpace;
            if (TryGetUniformOneBitValue(samples, width, height, rowBytes, invert, out float uniformValue))
            {
                byte[] rgb = ToRgbBytes(outputColorSpace.ToRGB([uniformValue]));
                uniformColor = new PdfLayoutColor(
                    rgb[0] / 255f,
                    rgb[1] / 255f,
                    rgb[2] / 255f,
                    1f,
                    colorSpace.GetName());
            }

            data = ReferenceEquals(outputColorSpace, colorSpace)
                ? EncodeOneBitGrayscalePng(samples, width, height, rowBytes, invert)
                : EncodeOneBitRgbPng(samples, width, height, rowBytes, invert, outputColorSpace);
            return true;
        }

        private static bool TryGetUniformOneBitValue(
            byte[] samples,
            int width,
            int height,
            int rowBytes,
            bool invert,
            out float value)
        {
            int first = -1;
            for (int row = 0; row < height; row++)
            {
                int rowOffset = row * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    int sample = (samples[rowOffset + (x / 8)] >> (7 - (x % 8))) & 1;
                    sample = invert ? 1 - sample : sample;
                    if (first < 0)
                    {
                        first = sample;
                    }
                    else if (sample != first)
                    {
                        value = 0f;
                        return false;
                    }
                }
            }

            value = first == 1 ? 1f : 0f;
            return first >= 0;
        }

        private static bool TryGetOneBitDecodeInversion(COSArray? decode, out bool invert)
        {
            invert = false;
            if (decode == null)
            {
                return true;
            }

            if (decode.Count() != 2 ||
                decode.GetObject(0) is not COSNumber lowValue ||
                decode.GetObject(1) is not COSNumber highValue)
            {
                return false;
            }

            float low = lowValue.FloatValue();
            float high = highValue.FloatValue();
            if (MathF.Abs(low) <= 0.001f && MathF.Abs(high - 1f) <= 0.001f)
            {
                return true;
            }

            if (MathF.Abs(low - 1f) <= 0.001f && MathF.Abs(high) <= 0.001f)
            {
                invert = true;
                return true;
            }

            return false;
        }

        private static byte[] EncodeOneBitGrayscalePng(
            byte[] samples,
            int width,
            int height,
            int rowBytes,
            bool invert)
        {
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[]? invertedRow = invert ? new byte[rowBytes] : null;
                for (int row = 0; row < height; row++)
                {
                    zlib.WriteByte(0);
                    ReadOnlySpan<byte> source = samples.AsSpan(row * rowBytes, rowBytes);
                    if (invertedRow == null)
                    {
                        zlib.Write(source);
                        continue;
                    }

                    for (int index = 0; index < source.Length; index++)
                    {
                        invertedRow[index] = (byte)~source[index];
                    }
                    zlib.Write(invertedRow);
                }
            }

            using MemoryStream png = new();
            png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
            header[8] = 1;
            header[9] = 0;
            WritePngChunk(png, "IHDR"u8, header);
            WritePngChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
            WritePngChunk(png, "IEND"u8, []);
            return png.ToArray();
        }

        private static byte[] EncodeOneBitRgbPng(
            byte[] samples,
            int width,
            int height,
            int rowBytes,
            bool invert,
            PDColorSpace colorSpace)
        {
            byte[][] colors =
            [
                ToRgbBytes(colorSpace.ToRGB([0f])),
                ToRgbBytes(colorSpace.ToRGB([1f]))
            ];
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int row = 0; row < height; row++)
                {
                    zlib.WriteByte(0);
                    int rowOffset = row * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        int sample = (samples[rowOffset + (x / 8)] >> (7 - (x % 8))) & 1;
                        byte[] color = colors[invert ? 1 - sample : sample];
                        zlib.Write(color);
                    }
                }
            }

            using MemoryStream png = new();
            png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
            header[8] = 8;
            header[9] = 2;
            WritePngChunk(png, "IHDR"u8, header);
            WritePngChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
            WritePngChunk(png, "IEND"u8, []);
            return png.ToArray();
        }

        private static byte[] ToRgbBytes(float[] rgb) =>
        [
            (byte)MathF.Round(Math.Clamp(rgb[0], 0f, 1f) * byte.MaxValue),
            (byte)MathF.Round(Math.Clamp(rgb[1], 0f, 1f) * byte.MaxValue),
            (byte)MathF.Round(Math.Clamp(rgb[2], 0f, 1f) * byte.MaxValue)
        ];

        private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
        {
            Span<byte> value = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(value, payload.Length);
            output.Write(value);
            output.Write(type);
            output.Write(payload);

            uint crc = uint.MaxValue;
            crc = UpdatePngCrc(crc, type);
            crc = UpdatePngCrc(crc, payload) ^ uint.MaxValue;
            BinaryPrimitives.WriteUInt32BigEndian(value, crc);
            output.Write(value);
        }

        private static uint UpdatePngCrc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0
                        ? 0xedb88320u ^ (crc >> 1)
                        : crc >> 1;
                }
            }

            return crc;
        }

        private string ExportInlineImageAsset(
            PDImage image,
            string assetId,
            int index,
            PDGraphicsState graphicsState)
        {
            EnsureImageExportBackendIfRequired(index);
            try
            {
                if (image.IsStencil())
                {
                    PdfLayoutColor color = ResolveColor(
                        graphicsState.GetNonStrokingColor(),
                        graphicsState.GetNonStrokeAlphaConstant(),
                        index,
                        "inline image mask");
                    byte[] stencilData = ExportStencilImagePng(
                        image.GetData(),
                        image.GetWidth(),
                        image.GetHeight(),
                        image.GetDecode(),
                        color);
                    return _imageAssets.Add(assetId, "png", "image/png", stencilData).AssetId;
                }

                PdfImageExportResult result = PdfImageExporter.ExportPng(image, _colorManagementContext);
                return _imageAssets.Add(
                    assetId,
                    result.FileExtension,
                    result.ContentType,
                    result.Data).AssetId;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return HandleImageExportFailure(assetId, index, ex);
            }
        }

        private static byte[] ExportStencilImagePng(
            byte[] samples,
            int width,
            int height,
            COSArray? decode,
            PdfLayoutColor color)
        {
            if (width <= 0 || height <= 0)
            {
                throw new IOException("Image-mask width and height must be positive.");
            }

            int rowBytes = (width + 7) / 8;
            int expectedLength = checked(rowBytes * height);
            if (samples.Length < expectedLength)
            {
                throw new IOException("Image-mask stream ended before all one-bit samples were available.");
            }

            if (!TryGetOneBitDecodeInversion(decode, out bool invert))
            {
                throw new NotSupportedException("Image-mask Decode must be [0 1] or [1 0].");
            }

            byte red = (byte)MathF.Round(Math.Clamp(color.Red, 0f, 1f) * byte.MaxValue);
            byte green = (byte)MathF.Round(Math.Clamp(color.Green, 0f, 1f) * byte.MaxValue);
            byte blue = (byte)MathF.Round(Math.Clamp(color.Blue, 0f, 1f) * byte.MaxValue);
            byte opaqueAlpha = (byte)MathF.Round(Math.Clamp(color.Alpha, 0f, 1f) * byte.MaxValue);
            int transparentSample = invert ? 0 : 1;
            byte[] rgba = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
            int destination = 0;
            for (int row = 0; row < height; row++)
            {
                int rowOffset = row * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    int sample = (samples[rowOffset + (x / 8)] >> (7 - (x % 8))) & 1;
                    rgba[destination++] = red;
                    rgba[destination++] = green;
                    rgba[destination++] = blue;
                    rgba[destination++] = sample == transparentSample ? (byte)0 : opaqueAlpha;
                }
            }

            return RenderingBackend.Current.ImageCodec.EncodePng(
                new InterleavedPixelData(
                    rgba,
                    width,
                    height,
                    checked(width * 4),
                    InterleavedPixelFormat.Rgba32));
        }

        private void EnsureImageExportBackendIfRequired(int index)
        {
            if (_imageExportPolicy == PdfImageExportPolicy.BackendRequired && !RenderingBackend.IsRegistered)
            {
                throw new InvalidOperationException(
                    $"Image {index.ToString(CultureInfo.InvariantCulture)} export requires a registered rendering backend.");
            }
        }

        private string HandleImageExportFailure(string assetId, int index, Exception exception)
        {
            if (_imageExportPolicy == PdfImageExportPolicy.Strict)
            {
                throw new InvalidOperationException(
                    $"Image {index.ToString(CultureInfo.InvariantCulture)} asset export failed in strict mode: {exception.Message}",
                    exception);
            }

            string code = PdfImageExportDiagnosticClassifier.CodeForFailure(exception.Message);
            _diagnostics.Add(new PdfLayoutDiagnostic(
                PdfLayoutDiagnosticSeverity.Warning,
                code,
                $"Image {index.ToString(CultureInfo.InvariantCulture)} asset export degraded: {exception.Message}",
                _pageNumber));
            return assetId;
        }

        private static string[] ExplicitColorants(params PDColorSpace?[] colorSpaces)
        {
            return colorSpaces
                .Where(static colorSpace => colorSpace is not null)
                .SelectMany(static colorSpace => ColorantsFor(colorSpace!))
                .Where(static name =>
                    !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "All", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string[] ExplicitColorants(params PDColor?[] colors)
        {
            return colors
                .Where(static color => color is not null)
                .SelectMany(static color => ColorantsFor(color!))
                .Where(static name =>
                    !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "All", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IEnumerable<string> ColorantsFor(PDColor color)
        {
            PDColorSpace? colorSpace = color.GetColorSpace();
            if (colorSpace is not PDDeviceCMYK)
            {
                return colorSpace is null ? [] : ColorantsFor(colorSpace);
            }

            string[] processColorants = ["Cyan", "Magenta", "Yellow", "Black"];
            float[] components = color.GetComponents();
            return processColorants
                .Where((_, index) => index < components.Length && components[index] > 0.0001f)
                .ToArray();
        }

        private static IEnumerable<string> ColorantsFor(PDColorSpace colorSpace)
        {
            return colorSpace switch
            {
                PDIndexed indexed => ColorantsFor(indexed.GetBaseColorSpace()),
                PDSeparation separation => [separation.GetColorantName()],
                PDDeviceN deviceN => deviceN.GetColorantNames(),
                _ => Array.Empty<string>()
            };
        }

        private string? ResolveSourceName(PDXObject xobject)
        {
            PDResources? resources = GetResources();
            COSStream? stream = xobject.GetCOSObject();
            if (resources == null || stream == null)
            {
                return null;
            }

            foreach (COSName name in resources.GetXObjectNames())
            {
                PDXObject? candidate;
                try
                {
                    candidate = resources.GetXObject(name);
                }
                catch (IOException)
                {
                    continue;
                }

                if (ReferenceEquals(candidate?.GetCOSObject(), stream))
                {
                    return name.GetName();
                }
            }

            return null;
        }

        private string? ColorSpaceName(PDImageXObject image, int index)
        {
            try
            {
                return image.GetColorSpace().GetName();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "image-colorspace-unresolved",
                    $"Image {index.ToString(CultureInfo.InvariantCulture)} color space could not be resolved: {ex.Message}",
                    _pageNumber));
                return null;
            }
        }

        private string? ColorSpaceName(PDImage image, int index)
        {
            try
            {
                return image.GetColorSpace().GetName();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    "image-colorspace-unresolved",
                    $"Image {index.ToString(CultureInfo.InvariantCulture)} color space could not be resolved: {ex.Message}",
                    _pageNumber));
                return null;
            }
        }

        private PdfLayoutRectangle NormalizePdfBox(float lowerLeftX, float lowerLeftY, float upperRightX, float upperRightY)
        {
            float cropTop = _cropBox.Y + _cropBox.Height;
            return new PdfLayoutRectangle(
                lowerLeftX - _cropBox.X,
                cropTop - upperRightY,
                MathF.Max(0, upperRightX - lowerLeftX),
                MathF.Max(0, upperRightY - lowerLeftY));
        }

        private static float Min(float a, float b, float c, float d)
        {
            return MathF.Min(MathF.Min(a, b), MathF.Min(c, d));
        }

        private static float Max(float a, float b, float c, float d)
        {
            return MathF.Max(MathF.Max(a, b), MathF.Max(c, d));
        }
    }
}
