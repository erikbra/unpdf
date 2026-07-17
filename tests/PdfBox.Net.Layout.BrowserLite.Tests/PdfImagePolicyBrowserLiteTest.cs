using System.Text;
using PdfBox.Net.COS;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.Graphics.Form;
using PdfBox.Net.PDModel.Graphics.Image;
using PdfBox.Net.PDModel.Graphics.State;
using PdfBox.Net.PDModel.Interactive.Annotation;
using PdfBox.Net.Rendering;
using PdfBox.Net.Util;

namespace PdfBox.Net.Layout.BrowserLite.Tests;

public sealed class PdfImagePolicyBrowserLiteTest
{
    // One-pixel Adobe-transform CMYK JPEG used to exercise the provider-free DCT path.
    private static readonly byte[] CmykJpeg = Convert.FromBase64String(
        "/9j/7gAOQWRvYmUAZAAAAAAC/9sAQwADAgICAgIDAgICAwMDAwQGBAQEBAQIBgYFBgkICgoJCAkJCgwPDAoLDgsJCQ0RDQ4PEBAREAoMEhMSEBMPEBAQ/9sAQwEDAwMEAwQIBAQIEAsJCxAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ/8AAFAgAAQABBAERAAIRAQMRAQQRAP/EABUAAQEAAAAAAAAAAAAAAAAAAAgJ/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/EABUBAQEAAAAAAAAAAAAAAAAAAAcJ/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAA4EAQACEQMRBAAAPwBEHNKpVN//2Q==");

    [Fact]
    public void TestAssembly_HasNoRenderingBackendRegistration()
    {
        Assert.False(RenderingBackend.IsRegistered);
    }

    [Fact]
    public void TestAssembly_DependencyGraphExcludesRenderingProviders()
    {
        string dependencyManifest = Path.ChangeExtension(
            typeof(PdfImagePolicyBrowserLiteTest).Assembly.Location,
            ".deps.json");
        string dependencies = File.ReadAllText(dependencyManifest);

        Assert.DoesNotContain("PdfBox.Net.SkiaSharp", dependencies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PdfBox.Net.ImageMagick", dependencies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SkiaSharp", dependencies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Magick.NET", dependencies, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PdfImageExportPolicy.Degraded)]
    [InlineData(PdfImageExportPolicy.Strict)]
    public void BrowserSafeJpeg_PreservesOriginalBytesWithoutBackend(PdfImageExportPolicy policy)
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-2x1-rgb.jpg"));
        using PDDocument document = CreateJpegImageDocument(original);

        PdfLayoutDocument layout = ExtractImageAssets(document, policy);

        PdfLayoutImageAsset asset = Assert.Single(layout.ImageAssets);
        Assert.Equal("image/jpeg", asset.ContentType);
        Assert.EndsWith(".jpg", asset.RelativePath, StringComparison.Ordinal);
        Assert.Equal(original, asset.Data);
        Assert.Empty(layout.Diagnostics);
    }

    [Fact]
    public void BackendRequired_RejectsEvenBrowserSafePassthroughWithoutBackend()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-2x1-rgb.jpg"));
        using PDDocument document = CreateJpegImageDocument(original);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ExtractImageAssets(document, PdfImageExportPolicy.BackendRequired));

        Assert.Contains("requires a registered rendering backend", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CmykYcckJpeg_DegradedModeOmitsAssetWithPreciseDiagnostic()
    {
        using PDDocument document = CreateFilteredImageDocument(
            COSName.DCT_DECODE,
            "DeviceCMYK",
            1,
            1,
            CmykJpeg);

        PdfLayoutDocument layout = ExtractImageAssets(document, PdfImageExportPolicy.Degraded);

        Assert.Empty(layout.ImageAssets);
        Assert.Single(Assert.Single(layout.Pages).Images);
        PdfLayoutDiagnostic diagnostic = Assert.Single(layout.Diagnostics);
        Assert.Equal("image-asset-cmyk-backend-required", diagnostic.Code);
        Assert.Contains("CMYK/YCCK", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CmykYcckJpeg_StrictModeFailsInsteadOfDegrading()
    {
        using PDDocument document = CreateFilteredImageDocument(
            COSName.DCT_DECODE,
            "DeviceCMYK",
            1,
            1,
            CmykJpeg);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ExtractImageAssets(document, PdfImageExportPolicy.Strict));

        Assert.Contains("strict mode", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CMYK/YCCK", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jpx_DegradedModeOmitsAssetWithPreciseDiagnostic()
    {
        using PDDocument document = CreateFilteredImageDocument(
            COSName.JPX_DECODE,
            "DeviceRGB",
            1,
            1,
            [0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20]);

        PdfLayoutDocument layout = ExtractImageAssets(document, PdfImageExportPolicy.Degraded);

        Assert.Empty(layout.ImageAssets);
        Assert.Single(Assert.Single(layout.Pages).Images);
        PdfLayoutDiagnostic diagnostic = Assert.Single(layout.Diagnostics);
        Assert.Equal("image-asset-jpx-backend-required", diagnostic.Code);
        Assert.Contains("JPEG 2000", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TIFF decoder is unavailable", "image-asset-tiff-backend-required")]
    [InlineData("ICC transform requires a rendering backend", "image-asset-icc-backend-required")]
    public void UnsupportedFailureFamilyMapping_HasStableDiagnosticCode(
        string failureMessage,
        string expectedCode)
    {
        Assert.Equal(
            expectedCode,
            PdfImageExportDiagnosticClassifier.CodeForFailure(failureMessage));
    }

    [Fact]
    public void AnnotationAppearance_WithoutBackendHasPreciseDiagnostic()
    {
        using PDDocument document = CreateAnnotationAppearanceDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeAnnotationAppearances = true
        });

        Assert.Empty(layout.ImageAssets);
        PdfLayoutDiagnostic diagnostic = Assert.Single(layout.Diagnostics);
        Assert.Equal("annotation-appearance-backend-missing", diagnostic.Code);
    }

    [Theory]
    [InlineData(PdfImageExportPolicy.Strict, "strict mode")]
    [InlineData(PdfImageExportPolicy.BackendRequired, "requires a registered rendering backend")]
    public void AnnotationAppearance_FailurePolicyIsEnforced(
        PdfImageExportPolicy policy,
        string expectedMessage)
    {
        using PDDocument document = CreateAnnotationAppearanceDocument();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
            {
                IncludeImageAssets = true,
                IncludeAnnotationAppearances = true,
                ImageExportPolicy = policy
            }));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Annotation appearances", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransparencyFallback_WithoutBackendHasPreciseDiagnostic()
    {
        using PDDocument document = CreateCompactKnockoutTransparencyGroupDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeTransparencyGroupFallbacks = true
        });

        Assert.Empty(layout.ImageAssets);
        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            candidate => candidate.Code == "transparency-group-rasterization-backend-missing");
        Assert.Equal(PdfLayoutDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Theory]
    [InlineData(PdfImageExportPolicy.Strict, "strict mode")]
    [InlineData(PdfImageExportPolicy.BackendRequired, "requires a registered rendering backend")]
    public void TransparencyFallback_FailurePolicyIsEnforced(
        PdfImageExportPolicy policy,
        string expectedMessage)
    {
        using PDDocument document = CreateCompactKnockoutTransparencyGroupDocument();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
            {
                IncludeImageAssets = true,
                IncludeTransparencyGroupFallbacks = true,
                ImageExportPolicy = policy
            }));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transparency-group fallback", exception.Message, StringComparison.Ordinal);
    }

    private static PdfLayoutDocument ExtractImageAssets(PDDocument document, PdfImageExportPolicy policy) =>
        PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            ImageExportPolicy = policy
        });

    private static PDDocument CreateJpegImageDocument(byte[] jpeg)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        using MemoryStream input = new(jpeg);
        PDImageXObject image = JPEGFactory.CreateFromStream(document, input);
        using PDPageContentStream content = new(document, page);
        content.DrawImage(image, 72, 600, 120, 60);
        return document;
    }

    private static PDDocument CreateFilteredImageDocument(
        COSName filter,
        string colorSpace,
        int width,
        int height,
        byte[] encodedData)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDStream stream = new(document);
        COSStream dictionary = stream.GetCOSObject();
        dictionary.SetInt(COSName.WIDTH, width);
        dictionary.SetInt(COSName.HEIGHT, height);
        dictionary.SetInt(COSName.BITS_PER_COMPONENT, 8);
        dictionary.SetName(COSName.COLORSPACE, colorSpace);
        dictionary.SetItem(COSName.FILTER, filter);
        using (Stream output = dictionary.CreateRawOutputStream())
        {
            output.Write(encodedData);
        }

        PDImageXObject image = new(stream, null);
        using PDPageContentStream content = new(document, page);
        content.DrawImage(image, 72, 600, 120, 60);
        return document;
    }

    private static PDDocument CreateAnnotationAppearanceDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDAppearanceStream appearance = new(new COSStream());
        appearance.SetBBox(new PDRectangle(0, 0, 40, 40));
        using (Stream output = appearance.GetCOSObject()!.CreateOutputStream())
        {
            output.Write(Encoding.ASCII.GetBytes("1 0 0 rg\n0 0 40 40 re\nf\n"));
        }
        PDAppearanceDictionary appearanceDictionary = new();
        appearanceDictionary.SetNormalAppearance(appearance);

        PDAnnotationSquare annotation = new();
        annotation.SetRectangle(new PDRectangle(20, 20, 40, 40));
        annotation.SetAppearance(appearanceDictionary);
        page.SetAnnotations([annotation]);
        return document;
    }

    private static PDDocument CreateCompactKnockoutTransparencyGroupDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDTransparencyGroup group = new(new PDStream(document));
        group.SetBBox(new PDRectangle(0, 0, 40, 40));
        PDTransparencyGroupAttributes attributes = new();
        attributes.GetCOSObject().SetBoolean(COSName.K, true);
        group.SetGroup(attributes);
        using (Stream formContent = group.GetContentStream().CreateOutputStream())
        {
            formContent.Write(Encoding.ASCII.GetBytes("1 0 0 rg\n0 0 40 40 re\nf\n"));
        }

        using PDPageContentStream pageContent = new(document, page);
        pageContent.SaveGraphicsState();
        pageContent.Transform(new Matrix(1, 0, 0, 1, 100, 300));
        pageContent.DrawForm(group);
        pageContent.RestoreGraphicsState();
        return document;
    }
}
