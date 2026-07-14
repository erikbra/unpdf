using System.Text;
using ImageMagick;
using PdfBox.Net.COS;
using PdfBox.Net.Layout;
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
using PdfBox.Net.PDModel.Resources;

namespace PdfBox.Net.Layout.Tests;

public class PdfLayoutExtractorTest
{
    [Fact]
    public void Extract_ModelsSupportedAcroFormFieldsAndWidgetGeometry()
    {
        using PDDocument document = CreateAcroFormDocument();

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        Assert.Equal(7, page.FormControls.Count);
        PdfLayoutFormControl text = Assert.Single(page.FormControls, control => control.Name == "fullName");
        Assert.Equal(PdfLayoutFormControlKind.Text, text.Kind);
        Assert.Equal("Full legal name", text.AccessibleName);
        Assert.Equal(["Erik"], text.Values);
        Assert.Equal(["Default name"], text.DefaultValues);
        Assert.True(text.IsReadOnly);
        Assert.True(text.IsRequired);
        Assert.True(text.IsMultiline);
        Assert.Equal(40, text.MaxLength);
        Assert.Equal(new PdfLayoutRectangle(20, 68, 180, 24), text.Bounds);

        PdfLayoutFormControl checkBox = Assert.Single(page.FormControls, control => control.Name == "accepted");
        Assert.Equal(PdfLayoutFormControlKind.CheckBox, checkBox.Kind);
        Assert.True(checkBox.IsChecked);
        Assert.False(checkBox.IsDefaultChecked);
        Assert.Equal(["Yes"], checkBox.Values);
        Assert.Equal(["Off"], checkBox.DefaultValues);
        Assert.Equal("Yes", Assert.Single(checkBox.Options).Value);

        PdfLayoutFormControl[] radios = page.FormControls.Where(control => control.Name == "contactMethod").ToArray();
        Assert.Equal(2, radios.Length);
        Assert.Equal(["email", "phone"], radios.Select(control => Assert.Single(control.Options).Value));
        Assert.False(radios[0].IsChecked);
        Assert.True(radios[1].IsChecked);
        Assert.True(radios[0].IsDefaultChecked);

        PdfLayoutFormControl combo = Assert.Single(page.FormControls, control => control.Name == "country");
        Assert.Equal(PdfLayoutFormControlKind.ComboBox, combo.Kind);
        Assert.Equal(["no"], combo.Values);
        Assert.Equal(["us"], combo.DefaultValues);
        Assert.Equal(["United States", "Norway"], combo.Options.Select(option => option.Label));

        PdfLayoutFormControl list = Assert.Single(page.FormControls, control => control.Name == "colors");
        Assert.Equal(PdfLayoutFormControlKind.ListBox, list.Kind);
        Assert.True(list.IsMultiple);
        Assert.Equal(["red", "blue"], list.Values);

        PdfLayoutFormControl signature = Assert.Single(page.FormControls, control => control.Name == "approval");
        Assert.Equal(PdfLayoutFormControlKind.Signature, signature.Kind);
        Assert.Equal(["Ada Lovelace"], signature.Values);
    }

    [Fact]
    public void Extract_UsesAuthoredHierarchyBeforeInferringFormLabels()
    {
        using PDDocument document = CreateSemanticAcroFormDocument();

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        PdfLayoutFormControl text = Assert.Single(page.FormControls, control => control.Name == "fullName");
        Assert.Equal("Full legal name", text.SourceLabelText);

        PdfLayoutFormControl choice = Assert.Single(page.FormControls, control => control.Name == "country");
        Assert.Equal("Country", choice.SourceLabelText);

        PdfLayoutFormControl[] taxFamily = page.FormControls
            .Where(control => control.Name.StartsWith("Boxes3a-b_ReadOrder[0].c1_1[", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, taxFamily.Length);
        Assert.All(taxFamily, control =>
        {
            Assert.Equal("Boxes3a-b_ReadOrder[0]", control.AuthoredHierarchyKey);
            Assert.Equal("Boxes3a-b_ReadOrder[0].c1_1", control.GroupKey);
            Assert.Equal(PdfLayoutFormGroupKind.CheckBox, control.GroupKind);
            Assert.Equal("Tax classification", control.GroupLabelText);
            Assert.NotNull(control.SourceLabelText);
        });
        Assert.Equal(["Individual", "Corporation"], taxFamily.Select(control => control.SourceLabelText));

        PdfLayoutFormControl contradiction = Assert.Single(
            page.FormControls,
            control => control.Name == "Boxes3a-b_ReadOrder[0].c1_2[0]");
        Assert.Equal("Boxes3a-b_ReadOrder[0]", contradiction.AuthoredHierarchyKey);
        Assert.Null(contradiction.GroupKey);
        Assert.Null(contradiction.GroupKind);
        Assert.Equal("Unrelated consent", contradiction.SourceLabelText);

        PdfLayoutFormControl[] radios = page.FormControls.Where(control => control.Name == "contactMethod").ToArray();
        Assert.Equal(2, radios.Length);
        Assert.All(radios, control =>
        {
            Assert.Equal("contactMethod", control.GroupKey);
            Assert.Equal(PdfLayoutFormGroupKind.RadioButton, control.GroupKind);
            Assert.Equal("Preferred contact", control.GroupLabelText);
        });
        Assert.Equal(["Email", "Phone"], radios.Select(control => control.SourceLabelText));
    }

    [Fact]
    public void Extract_XfaReportsSemanticFallbackDiagnostic()
    {
        using PDDocument document = new();
        document.AddPage(new PDPage());
        PDAcroForm acroForm = new(document);
        acroForm.SetXFA(new PDXFAResource(new COSStream()));
        document.GetDocumentCatalog().SetAcroForm(acroForm);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            item => item.Code == "xfa-semantic-forms-unsupported");
        Assert.Equal(PdfLayoutDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("visual", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_ComputerModernSymbolSlot12Control_NormalizesCircledDot()
    {
        using PDDocument document = CreateComputerModernSymbolControlDocument();

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);
        PdfTextGlyph glyph = Assert.Single(page.Glyphs);

        Assert.Equal("CMSY10", glyph.FontName);
        Assert.Equal("⊙", glyph.Text);
        Assert.Equal("⊙", Assert.Single(page.Runs).Text);
        Assert.Equal("⊙", Assert.Single(page.Lines).Text);
    }

    [Fact]
    public void Extract_RotatesAcroFormWidgetGeometryWithPage()
    {
        using PDDocument document = CreateAcroFormDocument();
        document.GetPage(0).SetRotation(90);

        PdfLayoutFormControl text = Assert.Single(
            Assert.Single(PdfLayoutExtractor.Extract(document).Pages).FormControls,
            control => control.Name == "fullName");

        Assert.Equal(new PdfLayoutRectangle(700, 20, 24, 180), text.Bounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_PreservesImageAndVectorContentStreamOrder(bool imageFirst)
    {
        using PDDocument document = CreateImageAndVectorDocument(imageFirst);

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        PdfLayoutPaintOperationKind[] expected = imageFirst
            ? [PdfLayoutPaintOperationKind.Image, PdfLayoutPaintOperationKind.Path]
            : [PdfLayoutPaintOperationKind.Path, PdfLayoutPaintOperationKind.Image];
        Assert.Equal(expected, page.PaintOperations.Select(operation => operation.Kind));
    }

    [Fact]
    public void Extract_DetectsPartialLineSourceHighlightFromTightRectangleGeometry()
    {
        using PDDocument document = CreateTextDocument("""
            1 0.5 0 rg
            102 698.5 33.4 8 re
            f
            0 0 0 rg
            BT /F1 10 Tf 72 700 Td (left 01 marked tail) Tj ET
            """);

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        PdfTextHighlight highlight = Assert.Single(page.TextHighlights);
        Assert.Equal("marked", string.Concat(highlight.Glyphs.Select(static glyph => glyph.Text)));
        AssertClose(102f, highlight.Bounds.X);
        AssertClose(85.5f, highlight.Bounds.Y);
        AssertClose(33.4f, highlight.Bounds.Width);
        AssertClose(8f, highlight.Bounds.Height);
        AssertClose(1f, highlight.Color.Red);
        AssertClose(128f / 255f, highlight.Color.Green);
        AssertClose(0f, highlight.Color.Blue);
        AssertClose(1f, highlight.Color.Alpha);
        Assert.Equal("DeviceRGB", highlight.Color.ColorSpaceName);
        Assert.Equal(0, highlight.SourcePathIndex);
    }

    [Theory]
    [InlineData("1 0 0 rg BT /F1 10 Tf 72 700 Td (ordinary colored text) Tj ET")]
    [InlineData("1 1 0 rg 68 692 140 20 re f 0 0 0 rg BT /F1 10 Tf 72 700 Td (vector background) Tj ET")]
    [InlineData("1 1 0 rg 72 698.5 42 8 re 0 0 0 RG B BT /F1 10 Tf 72 700 Td (table fill) Tj ET")]
    public void Extract_DoesNotClassifyColoredTextOrDecorativeFillsAsHighlights(string contentStream)
    {
        using PDDocument document = CreateTextDocument(contentStream);

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        Assert.Empty(page.TextHighlights);
    }

    [Fact]
    public void Extract_OutputIntentManagesDeviceCmykTextAndPathColors()
    {
        using PDDocument document = CreateTextDocument("""
            0 1 1 0 k
            BT
            /F1 12 Tf
            72 700 Td
            (Managed) Tj
            ET
            72 600 120 60 re
            f
            """);
        AddCmykOutputIntent(document);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfLayoutColor textColor = Assert.Single(page.Runs).Color;
        PdfLayoutColor pathColor = Assert.Single(page.Paths).FillColor!.Value;
        float[] naive = PDDeviceCMYK.Instance.ToRGB([0f, 1f, 1f, 0f]);
        AssertManagedColorDiffers(textColor, naive);
        AssertManagedColorDiffers(pathColor, naive);
        Assert.Equal(textColor, pathColor);
        Assert.Equal("DeviceCMYK", textColor.ColorSpaceName);
    }

    [Fact]
    public void Extract_OutputIntentManagesDeviceCmykShadingStops()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        AddCmykOutputIntent(document);
        PDShadingType2 shading = CreateCmykAxialShading();
        using (PDPageContentStream content = new(document, page))
        {
            content.ShadingFill(shading);
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutColor managed = Assert.Single(Assert.Single(layout.Pages).Shadings).Stops[0].Color;
        float[] naive = PDDeviceCMYK.Instance.ToRGB([0f, 1f, 1f, 0f]);
        AssertManagedColorDiffers(managed, naive);
        Assert.Equal("DeviceCMYK", managed.ColorSpaceName);
    }

    [Fact]
    public void Extract_MalformedOutputIntentPreservesDeviceCmykFallback()
    {
        using PDDocument document = CreateTextDocument("""
            0 1 1 0 k
            BT
            /F1 12 Tf
            72 700 Td
            (Fallback) Tj
            ET
            """);
        using MemoryStream malformedProfile = new([1, 2, 3, 4]);
        document.GetDocumentCatalog().AddOutputIntent(new PDOutputIntent(document, malformedProfile));

        PdfLayoutColor extracted = Assert.Single(Assert.Single(PdfLayoutExtractor.Extract(document).Pages).Runs).Color;

        Assert.Equal(new PdfLayoutColor(1f, 0f, 0f, 1f, "DeviceCMYK"), extracted);
    }

    [Fact]
    public void Extract_ShapeAlphaPath_IsMarkedForAnHtmlFallback()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDExtendedGraphicsState graphicsState = new();
        graphicsState.SetAlphaSourceFlag(true);
        graphicsState.SetNonStrokingAlphaConstant(0.75f);
        graphicsState.SetBlendMode(BlendMode.MULTIPLY);
        using (PDPageContentStream content = new(document, page))
        {
            content.SetGraphicsStateParameters(graphicsState);
            content.SetNonStrokingColor(0f, 0f, 0f);
            content.AddRect(72, 700, 120, 24);
            content.Fill();
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        Assert.True(Assert.Single(layoutPage.Paths).UsesShapeAlpha);
        Assert.Contains(layoutPage.Diagnostics, diagnostic => diagnostic.Code == "shape-alpha-vector-unsupported");
    }

    [Fact]
    public void Extract_OpaqueShapeAlphaPath_DoesNotReportAnHtmlFallback()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDExtendedGraphicsState graphicsState = new();
        graphicsState.SetAlphaSourceFlag(true);
        graphicsState.SetNonStrokingAlphaConstant(1f);
        using (PDPageContentStream content = new(document, page))
        {
            content.SetGraphicsStateParameters(graphicsState);
            content.SetNonStrokingColor(0f, 0f, 0f);
            content.AddRect(72, 700, 120, 24);
            content.Fill();
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        Assert.True(Assert.Single(layoutPage.Paths).UsesShapeAlpha);
        Assert.DoesNotContain(layoutPage.Diagnostics, diagnostic => diagnostic.Code == "shape-alpha-vector-unsupported");
    }

    [Fact]
    public void Extract_AxialShading_CapturesSvgGradientGeometryAndStops()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDShadingType2 shading = CreateAxialShading();
        using (PDPageContentStream content = new(document, page))
        {
            content.ShadingFill(shading);
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutShading extracted = Assert.Single(Assert.Single(layout.Pages).Shadings);
        Assert.Equal(PDShading.SHADING_TYPE2, extracted.ShadingType);
        Assert.Equal(100, extracted.StartX, 3);
        Assert.Equal(392, extracted.StartY, 3);
        Assert.Equal(400, extracted.EndX, 3);
        Assert.Equal(392, extracted.EndY, 3);
        Assert.Equal(9, extracted.Stops.Count);
        Assert.True(extracted.Stops[0].Color.Red > 0.99f);
        Assert.True(extracted.Stops[0].Color.Blue < 0.01f);
        Assert.True(extracted.Stops[^1].Color.Red < 0.01f);
        Assert.True(extracted.Stops[^1].Color.Blue > 0.99f);
    }

    [Fact]
    public void Extract_TensorPatchShading_CapturesAColoredTriangleMesh()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDShadingType7 shading = CreateTensorPatchShading(document, patchCount: 8);
        using (PDPageContentStream content = new(document, page))
        {
            content.AddRect(100, 500, 100, 100);
            content.Clip();
            content.ShadingFill(shading);
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        PdfLayoutShading extracted = Assert.Single(layoutPage.Shadings);
        Assert.Equal(PDShading.SHADING_TYPE7, extracted.ShadingType);
        Assert.Equal(1936, extracted.Triangles.Count);
        Assert.True(extracted.Triangles.Count <= 2048);
        Assert.Contains(extracted.Triangles, triangle => triangle.Color.Red > triangle.Color.Blue);
        Assert.Contains(extracted.Triangles, triangle => triangle.Color.Blue > triangle.Color.Red);
        Assert.DoesNotContain(layoutPage.Diagnostics, diagnostic => diagnostic.Code == "shading-type-unsupported");
    }

    [Fact]
    public void Extract_SinglePageText_CapturesGlyphRunsLinesAndBounds()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Hello) Tj
            ET
            """);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage page = Assert.Single(layout.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(612, page.Width);
        Assert.Equal(792, page.Height);
        Assert.Empty(layout.Diagnostics);
        Assert.Equal("Hello", page.Text);
        Assert.Equal(5, page.Glyphs.Count);
        PdfTextLine line = Assert.Single(page.Lines);
        PdfTextRun run = Assert.Single(line.Runs);
        Assert.Equal("Hello", line.Text);
        Assert.Equal("Hello", run.Text);
        Assert.Equal(5, run.Glyphs.Count);
        Assert.All(page.Glyphs, glyph => Assert.Equal("Helvetica", glyph.FontName));
        Assert.All(page.Glyphs, glyph => Assert.InRange(glyph.FontSize, 11.9f, 12.1f));
        Assert.InRange(run.Bounds.X, 71.9f, 72.1f);
        Assert.InRange(run.Bounds.Y, 78f, 95f);
        Assert.True(run.Bounds.Width > 20);
        Assert.True(run.Bounds.Height > 5);
    }

    [Fact]
    public void Extract_MultiLineText_PreservesReadingOrder()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (First line) Tj
            0 -24 Td
            (Second line) Tj
            ET
            """);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage page = Assert.Single(layout.Pages);
        Assert.Equal(["First line", "Second line"], page.Lines.Select(line => line.Text).ToArray());
        Assert.Equal($"First line{Environment.NewLine}Second line", page.Text);
        Assert.True(page.Lines[0].Bounds.Y < page.Lines[1].Bounds.Y);
    }

    [Fact]
    public void Extract_RotatedCroppedPage_NormalizesPageGeometryAndTextBounds()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 120 Td
            (Rotated) Tj
            ET
            """);
        PDPage page = document.GetPage(0);
        page.SetCropBox(new PDRectangle(10, 20, 200, 300));
        page.SetRotation(90);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        Assert.Equal(90, layoutPage.Rotation);
        Assert.Equal(300, layoutPage.Width);
        Assert.Equal(200, layoutPage.Height);
        Assert.Equal(new PdfLayoutRectangle(10, 20, 200, 300), layoutPage.CropBox);
        Assert.Equal("Rotated", layoutPage.Text);
        Assert.All(layoutPage.Glyphs, glyph =>
        {
            Assert.InRange(glyph.Bounds.X, 0, layoutPage.Width);
            Assert.InRange(glyph.Bounds.Y, -0.5f, layoutPage.Height + 0.5f);
        });
    }

    [Fact]
    public void Extract_ExistingRotationFixture_ExtractsTextWithoutDiagnostics()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rotation.pdf"));

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        Assert.Equal(2, layout.Pages.Count);
        Assert.Empty(layout.Diagnostics);
        Assert.Contains(layout.Pages, page => page.Glyphs.Count > 0);
        Assert.Contains(layout.Pages, page => page.Text.Length > 0);
    }

    [Fact]
    public void Extract_LinkAnnotation_CapturesUriBoundsAndQuadBounds()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Linked text) Tj
            ET
            """);
        PDAnnotationLink link = new();
        link.SetRectangle(new PDRectangle(72, 680, 120, 24));
        link.SetQuadPoints([72, 704, 192, 704, 72, 680, 192, 680]);
        PDActionURI action = new();
        action.SetURI("https://example.com/pdfbox");
        link.SetAction(action);
        document.GetPage(0).SetAnnotations([link]);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutLink layoutLink = Assert.Single(Assert.Single(layout.Pages).Links);
        Assert.Equal(0, layoutLink.Index);
        Assert.Equal(PdfLayoutLinkKind.Uri, layoutLink.Kind);
        Assert.Equal("https://example.com/pdfbox", layoutLink.Uri);
        Assert.Null(layoutLink.Destination);
        Assert.Null(layoutLink.DestinationPageNumber);
        AssertClose(72, layoutLink.Bounds.X);
        AssertClose(88, layoutLink.Bounds.Y);
        AssertClose(120, layoutLink.Bounds.Width);
        AssertClose(24, layoutLink.Bounds.Height);
        PdfLayoutRectangle quad = Assert.Single(layoutLink.QuadBounds);
        AssertClose(layoutLink.Bounds.X, quad.X);
        AssertClose(layoutLink.Bounds.Y, quad.Y);
        AssertClose(layoutLink.Bounds.Width, quad.Width);
        AssertClose(layoutLink.Bounds.Height, quad.Height);
    }

    [Fact]
    public void Extract_LinkCollectionCanBeDisabled()
    {
        using PDDocument document = CreateTextDocument("");
        PDAnnotationLink link = new();
        link.SetRectangle(new PDRectangle(72, 680, 120, 24));
        document.GetPage(0).SetAnnotations([link]);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeLinks = false
        });

        Assert.Empty(Assert.Single(layout.Pages).Links);
    }

    [Fact]
    public void Extract_RectanglePath_CapturesCommandsBoundsAndPaintStyle()
    {
        using PDDocument document = CreateTextDocument("""
            q
            2 w
            1 0 0 RG
            0.1 0.6 0.2 rg
            72 600 120 60 re
            B
            Q
            """);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPath path = Assert.Single(Assert.Single(layout.Pages).Paths);
        Assert.Empty(layout.Diagnostics);
        Assert.Equal(0, path.Index);
        Assert.True(path.IsFilled);
        Assert.True(path.IsStroked);
        Assert.Equal(1, path.FillRule);
        AssertClose(72, path.Bounds.X);
        AssertClose(132, path.Bounds.Y);
        AssertClose(120, path.Bounds.Width);
        AssertClose(60, path.Bounds.Height);
        Assert.Equal(
            [
                PdfLayoutPathCommandKind.MoveTo,
                PdfLayoutPathCommandKind.LineTo,
                PdfLayoutPathCommandKind.LineTo,
                PdfLayoutPathCommandKind.LineTo,
                PdfLayoutPathCommandKind.ClosePath
            ],
            path.Commands.Select(command => command.Kind).ToArray());
        AssertClose(72, path.Commands[0].X1);
        AssertClose(192, path.Commands[0].Y1);
        AssertClose(192, path.Commands[2].X1);
        AssertClose(132, path.Commands[2].Y1);
        Assert.NotNull(path.FillColor);
        AssertClose(0.1f, path.FillColor.Value.Red);
        AssertClose(0.6f, path.FillColor.Value.Green);
        AssertClose(0.2f, path.FillColor.Value.Blue);
        AssertClose(1, path.FillColor.Value.Alpha);
        Assert.NotNull(path.Stroke);
        AssertClose(2, path.Stroke.Width);
        AssertClose(1, path.Stroke.Color.Red);
        AssertClose(0, path.Stroke.Color.Green);
        AssertClose(0, path.Stroke.Color.Blue);
    }

    [Fact]
    public void Extract_PathCollectionCanBeDisabled()
    {
        using PDDocument document = CreateTextDocument("""
            72 600 120 60 re
            f
            """);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludePaths = false
        });

        Assert.Empty(Assert.Single(layout.Pages).Paths);
    }

    [Fact]
    public void Extract_PathClippingRetainsExactGeometryWithoutDiagnostic()
    {
        using PDDocument document = CreateTextDocument("""
            72 600 120 60 re
            W
            n
            90 610 50 20 re
            f
            """);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPath path = Assert.Single(Assert.Single(layout.Pages).Paths);
        PdfLayoutClipPath clipPath = Assert.Single(path.ClipPaths);
        Assert.Equal(5, clipPath.Commands.Count);
        Assert.Equal(1, clipPath.WindingRule);
        Assert.DoesNotContain(layout.Diagnostics, diagnostic => diagnostic.Code == "path-clipping-unsupported");
    }

    [Fact]
    public void Extract_TextClippingKeepsUnsupportedDiagnostic()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            7 Tr
            72 700 Td
            (Clip) Tj
            ET
            """);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            diagnostic => diagnostic.Code == "path-clipping-unsupported");
        Assert.Equal(PdfLayoutDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("glyph clipping path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, diagnostic.PageNumber);
    }

    [Fact]
    public void Extract_NestedFormLateClipRetainsVectorAndImageGeometry()
    {
        using PDDocument document = CreateNestedLateClipDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfLayoutPage page = Assert.Single(layout.Pages);

        Assert.Equal(2, page.VectorGroups.Count);
        Assert.Equal(2, page.Paths.Count);
        Assert.Single(page.Paths[0].ClipPaths);
        Assert.Equal(2, page.Paths[1].ClipPaths.Count);
        PdfLayoutClipPath lateClip = page.Paths[1].ClipPaths[1];
        Assert.Equal(4, lateClip.Commands.Count);
        Assert.Equal(PdfLayoutPathCommandKind.ClosePath, lateClip.Commands[^1].Kind);
        Assert.Equal(3, Assert.Single(page.Images).ClipPaths.Count);
        Assert.DoesNotContain(layout.Diagnostics, diagnostic => diagnostic.Code == "path-clipping-unsupported");
    }

    [Fact]
    public void Extract_XObjectImage_CapturesIntrinsicSizePlacementAndMetadata()
    {
        using PDDocument document = CreateImageDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutImage image = Assert.Single(Assert.Single(layout.Pages).Images);
        Assert.Empty(layout.Diagnostics);
        Assert.Empty(layout.ImageAssets);
        Assert.Equal(0, image.Index);
        Assert.Equal("page-1-image-0", image.AssetId);
        Assert.Equal(PdfLayoutImageKind.XObject, image.Kind);
        Assert.Equal("Im0", image.SourceName);
        Assert.Equal(2, image.IntrinsicWidth);
        Assert.Equal(2, image.IntrinsicHeight);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal("DeviceRGB", image.ColorSpaceName);
        Assert.False(image.Interpolate);
        AssertClose(72, image.Bounds.X);
        AssertClose(132, image.Bounds.Y);
        AssertClose(120, image.Bounds.Width);
        AssertClose(60, image.Bounds.Height);
        AssertClose(120, image.Transform.A);
        AssertClose(0, image.Transform.B);
        AssertClose(0, image.Transform.C);
        AssertClose(60, image.Transform.D);
        AssertClose(72, image.Transform.E);
        AssertClose(600, image.Transform.F);
    }

    [Theory]
    [InlineData(90, 50, 30, 30, 50)]
    [InlineData(180, 120, 50, 50, 30)]
    [InlineData(270, 220, 120, 30, 50)]
    public void Extract_RotatedCroppedPage_NormalizesXObjectImageGeometry(
        int rotation,
        float expectedX,
        float expectedY,
        float expectedWidth,
        float expectedHeight)
    {
        using PDDocument document = CreateRotatedCroppedImageDocument(rotation);

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        PdfLayoutImage image = Assert.Single(page.Images);
        Assert.DoesNotContain(page.Diagnostics, diagnostic => diagnostic.Code == "image-rotation-unsupported");
        AssertClose(expectedX, image.Bounds.X);
        AssertClose(expectedY, image.Bounds.Y);
        AssertClose(expectedWidth, image.Bounds.Width);
        AssertClose(expectedHeight, image.Bounds.Height);
        Assert.InRange(image.Bounds.X, 0, page.Width - image.Bounds.Width);
        Assert.InRange(image.Bounds.Y, 0, page.Height - image.Bounds.Height);

        PdfLayoutTransform expectedTransform = rotation switch
        {
            90 => new PdfLayoutTransform(0, 50, 30, 0, 50, 30),
            180 => new PdfLayoutTransform(-50, 0, 0, 30, 170, 50),
            270 => new PdfLayoutTransform(0, -50, -30, 0, 250, 170),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation))
        };
        Assert.Equal(expectedTransform, image.Transform);
    }

    [Fact]
    public void Extract_InlineImage_CapturesIntrinsicSizePlacementAndMetadata()
    {
        using PDDocument document = CreateInlineImageDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutImage image = Assert.Single(Assert.Single(layout.Pages).Images);
        Assert.Empty(layout.Diagnostics);
        Assert.Empty(layout.ImageAssets);
        Assert.Equal(0, image.Index);
        Assert.Equal("page-1-image-0", image.AssetId);
        Assert.Equal(PdfLayoutImageKind.InlineImage, image.Kind);
        Assert.Null(image.SourceName);
        Assert.Equal(2, image.IntrinsicWidth);
        Assert.Equal(2, image.IntrinsicHeight);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal("DeviceRGB", image.ColorSpaceName);
        Assert.False(image.Interpolate);
        AssertClose(72, image.Bounds.X);
        AssertClose(132, image.Bounds.Y);
        AssertClose(120, image.Bounds.Width);
        AssertClose(60, image.Bounds.Height);
        AssertClose(120, image.Transform.A);
        AssertClose(0, image.Transform.B);
        AssertClose(0, image.Transform.C);
        AssertClose(60, image.Transform.D);
        AssertClose(72, image.Transform.E);
        AssertClose(600, image.Transform.F);
    }

    [Fact]
    public void Extract_RotatedCroppedPage_NormalizesInlineImageGeometry()
    {
        using PDDocument document = CreateRotatedCroppedInlineImageDocument(rotation: 270);

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document).Pages);

        PdfLayoutImage image = Assert.Single(page.Images);
        Assert.Equal(PdfLayoutImageKind.InlineImage, image.Kind);
        Assert.DoesNotContain(page.Diagnostics, diagnostic => diagnostic.Code == "image-rotation-unsupported");
        Assert.Equal(new PdfLayoutRectangle(220, 120, 30, 50), image.Bounds);
        Assert.Equal(new PdfLayoutTransform(0, -50, -30, 0, 250, 170), image.Transform);
    }

    [Fact]
    public void Extract_ImageCollectionCanBeDisabled()
    {
        using PDDocument document = CreateImageDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false
        });

        Assert.Empty(Assert.Single(layout.Pages).Images);
    }

    [Fact]
    public void Extract_DeviceRgbJpegAsset_PreservesOriginalBrowserSafeStream()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-2x1-rgb.jpg"));
        using PDDocument document = CreateJpegImageDocument(original);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfLayoutImageAsset asset = Assert.Single(layout.ImageAssets);
        Assert.Equal("image/jpeg", asset.ContentType);
        Assert.EndsWith(".jpg", asset.RelativePath, StringComparison.Ordinal);
        Assert.Equal(original, asset.Data);
    }

    [Fact]
    public void Extract_TransparencyGroups_RetainsCompositingHierarchy()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage attentionVisualizationPage = layout.Pages[12];
        Assert.Contains("AttentionVisualizations", attentionVisualizationPage.Text, StringComparison.Ordinal);
        PdfLayoutVectorGroup[] groups = attentionVisualizationPage.VectorGroups.ToArray();
        Assert.NotEmpty(groups);
        Assert.Contains(groups, group => group.Opacity < 0.1f);
        Assert.Contains(groups, group => group.Opacity > 0.8f);
        Assert.Contains(groups, group =>
            group.ClipBounds is PdfLayoutRectangle clipBounds &&
            clipBounds.X is > 107f and < 109f &&
            clipBounds.Y is > 100f and < 102f &&
            clipBounds.Width is > 395f and < 397f &&
            clipBounds.Height is > 200f and < 202f);
        Assert.All(groups, group =>
        {
            Assert.True(group.HasPaths);
            Assert.InRange(group.FirstPathIndex, 0, attentionVisualizationPage.Paths.Count - 1);
            Assert.InRange(group.LastPathIndex, group.FirstPathIndex, attentionVisualizationPage.Paths.Count - 1);
        });
    }

    [Fact]
    public void Extract_IsDeterministicAcrossRepeatedRuns()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Alpha) Tj
            0 -24 Td
            (Beta) Tj
            ET
            """);

        PdfLayoutDocument first = PdfLayoutExtractor.Extract(document);
        PdfLayoutDocument second = PdfLayoutExtractor.Extract(document);

        Assert.Equal(Snapshot(first), Snapshot(second));
    }

    [Fact]
    public void Extract_UnnamedType3FontUsesFallbackTextAndReportsUnsupportedWebFont()
    {
        using PDDocument document = CreateUnnamedType3TextDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfTextGlyph glyph = Assert.Single(page.Glyphs, item => item.Text == "A");
        Assert.Equal("Type3", glyph.FontName);
        Assert.False(glyph.UsesBrowserFontAsset);
        Assert.Empty(layout.FontAssets);
        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            item => item.Code == "embedded-font-web-unsupported");
        Assert.Contains("Type 3", diagnostic.Message);
        Assert.Contains("fallback text", diagnostic.Message);
    }

    private static string Snapshot(PdfLayoutDocument document)
    {
        StringBuilder builder = new();
        foreach (PdfLayoutPage page in document.Pages)
        {
            builder.AppendLine($"{page.PageNumber}:{page.Width:0.###}:{page.Height:0.###}:{page.Rotation}:{page.Text}");
            foreach (PdfTextGlyph glyph in page.Glyphs)
            {
                builder.AppendLine(
                    $"{glyph.Text}:{glyph.FontName}:{glyph.FontSize:0.###}:{glyph.Direction:0.###}:{glyph.Bounds.X:0.###}:{glyph.Bounds.Y:0.###}:{glyph.Bounds.Width:0.###}:{glyph.Bounds.Height:0.###}");
            }
        }

        return builder.ToString();
    }

    private static void AssertClose(float expected, float actual)
    {
        Assert.InRange(actual, expected - 0.01f, expected + 0.01f);
    }

    private static void AssertManagedColorDiffers(PdfLayoutColor managed, float[] naive)
    {
        Assert.True(
            Math.Abs(managed.Red - naive[0]) > 0.02f ||
            Math.Abs(managed.Green - naive[1]) > 0.02f ||
            Math.Abs(managed.Blue - naive[2]) > 0.02f);
    }

    private static PDDocument CreateImageAndVectorDocument(bool imageFirst)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDImageXObject image = LosslessFactory.CreateFromRawData(
            document,
            [255, 255, 255],
            1,
            1,
            8,
            3);
        using PDPageContentStream content = new(document, page);
        if (imageFirst)
        {
            content.DrawImage(image, 72, 600, 120, 60);
        }

        content.AddRect(72, 600, 120, 60);
        content.Fill();
        if (!imageFirst)
        {
            content.DrawImage(image, 72, 600, 120, 60);
        }

        return document;
    }

    private static void AddCmykOutputIntent(PDDocument document)
    {
        using MemoryStream profile = new(ColorProfiles.CoatedFOGRA39.ToByteArray());
        document.GetDocumentCatalog().AddOutputIntent(new PDOutputIntent(document, profile));
    }

    private static PDShadingType2 CreateAxialShading()
    {
        COSDictionary functionDictionary = new();
        functionDictionary.SetInt(COSName.FUNCTION_TYPE, 2);
        functionDictionary.SetItem(COSName.DOMAIN, COSArray.Of(0f, 1f));
        functionDictionary.SetItem(COSName.C0, COSArray.Of(1f, 0f, 0f));
        functionDictionary.SetItem(COSName.C1, COSArray.Of(0f, 0f, 1f));
        functionDictionary.SetFloat(COSName.N, 1f);

        PDShadingType2 shading = new(new COSDictionary());
        shading.SetShadingType(PDShading.SHADING_TYPE2);
        shading.SetColorSpace(PDDeviceRGB.Instance);
        shading.SetCoords(COSArray.Of(100f, 400f, 400f, 400f));
        shading.SetFunction(new PDFunctionType2(functionDictionary));
        return shading;
    }

    private static PDShadingType2 CreateCmykAxialShading()
    {
        COSDictionary functionDictionary = new();
        functionDictionary.SetInt(COSName.FUNCTION_TYPE, 2);
        functionDictionary.SetItem(COSName.DOMAIN, COSArray.Of(0f, 1f));
        functionDictionary.SetItem(COSName.C0, COSArray.Of(0f, 1f, 1f, 0f));
        functionDictionary.SetItem(COSName.C1, COSArray.Of(1f, 0f, 0f, 0f));
        functionDictionary.SetFloat(COSName.N, 1f);

        PDShadingType2 shading = new(new COSDictionary());
        shading.SetShadingType(PDShading.SHADING_TYPE2);
        shading.SetColorSpace(PDDeviceCMYK.Instance);
        shading.SetCoords(COSArray.Of(100f, 400f, 400f, 400f));
        shading.SetFunction(new PDFunctionType2(functionDictionary));
        return shading;
    }

    private static PDShadingType7 CreateTensorPatchShading(PDDocument document, int patchCount)
    {
        COSStream stream = document.GetDocument().CreateCOSStream();
        PDShadingType7 shading = new(stream);
        shading.SetShadingType(PDShading.SHADING_TYPE7);
        shading.SetColorSpace(PDDeviceRGB.Instance);
        shading.SetBitsPerFlag(8);
        shading.SetBitsPerCoordinate(8);
        shading.SetBitsPerComponent(8);
        shading.SetDecodeValues(COSArray.Of(100f, 200f, 500f, 600f, 0f, 1f, 0f, 1f, 0f, 1f));

        byte[] coordinates =
        [
            0, 0,
            0, 85,
            0, 170,
            0, 255,
            85, 255,
            170, 255,
            255, 255,
            255, 170,
            255, 85,
            255, 0,
            170, 0,
            85, 0,
            85, 85,
            85, 170,
            170, 170,
            170, 85
        ];
        byte[] colors =
        [
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255
        ];
        using Stream output = stream.CreateOutputStream();
        for (int index = 0; index < patchCount; index++)
        {
            output.WriteByte(0);
            output.Write(coordinates);
            output.Write(colors);
        }
        return shading;
    }

    private static PDDocument CreateTextDocument(string contentStream)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.RESOURCES, CreateDefaultResourcesDictionary());
        pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream(contentStream));
        return document;
    }

    private static PDDocument CreateComputerModernSymbolControlDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        COSArray differences = new();
        differences.Add(COSInteger.Get(12));
        differences.Add(COSName.GetPDFName("odot"));
        COSDictionary encoding = new();
        encoding.SetName(COSName.TYPE, "Encoding");
        encoding.SetItem(COSName.DIFFERENCES, differences);

        COSDictionary font = new();
        font.SetItem(COSName.TYPE, COSName.GetPDFName("Font"));
        font.SetName(COSName.SUBTYPE, "Type1");
        font.SetName(COSName.GetPDFName("BaseFont"), "CMSY10");
        font.SetInt(COSName.GetPDFName("FirstChar"), 12);
        font.SetInt(COSName.GetPDFName("LastChar"), 12);
        font.SetItem(COSName.GetPDFName("Widths"), COSArray.Of(778f));
        font.SetItem(COSName.GetPDFName("Encoding"), encoding);
        font.SetItem(COSName.GetPDFName("ToUnicode"), CreateContentStream("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CMapType 2 def
            1 begincodespacerange
            <00> <FF>
            endcodespacerange
            1 beginbfchar
            <0C> <000C>
            endbfchar
            endcmap
            CMapName currentdict /CMap defineresource pop
            end
            end
            """));

        COSDictionary fonts = new();
        fonts.SetItem(COSName.GetPDFName("F1"), font);
        COSDictionary resources = new();
        resources.SetItem(COSName.GetPDFName("Font"), fonts);
        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.RESOURCES, resources);
        pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream("BT /F1 12 Tf 72 700 Td <0C> Tj ET"));
        return document;
    }

    private static PDDocument CreateUnnamedType3TextDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        COSDictionary type3Font = new();
        type3Font.SetItem(COSName.TYPE, COSName.GetPDFName("Font"));
        type3Font.SetName(COSName.SUBTYPE, "Type3");
        type3Font.SetItem(COSName.GetPDFName("FontMatrix"), COSArray.Of(0.001f, 0f, 0f, 0.001f, 0f, 0f));
        type3Font.SetItem(COSName.GetPDFName("FontBBox"), COSArray.Of(0f, 0f, 500f, 700f));
        type3Font.SetInt(COSName.GetPDFName("FirstChar"), 65);
        type3Font.SetInt(COSName.GetPDFName("LastChar"), 65);
        type3Font.SetItem(COSName.GetPDFName("Widths"), COSArray.Of(500f));
        type3Font.SetName(COSName.GetPDFName("Encoding"), "WinAnsiEncoding");
        COSDictionary charProcs = new();
        charProcs.SetItem(COSName.GetPDFName("A"), CreateContentStream("500 0 d0"));
        type3Font.SetItem(COSName.GetPDFName("CharProcs"), charProcs);

        COSDictionary fonts = new();
        fonts.SetItem(COSName.GetPDFName("FType3"), type3Font);
        COSDictionary resources = new();
        resources.SetItem(COSName.GetPDFName("Font"), fonts);

        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.RESOURCES, resources);
        pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream("BT /FType3 24 Tf 72 700 Td (A) Tj ET"));
        return document;
    }

    private static PDDocument CreateNestedLateClipDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDImageXObject tile = LosslessFactory.CreateFromRawData(
            document,
            [0, 255, 255],
            1,
            1,
            8,
            3);

        PDFormXObject inner = new(new PDStream(document));
        inner.SetBBox(new PDRectangle(0, 0, 100, 100));
        PDResources innerResources = new();
        COSName tileName = innerResources.Add(tile, "Im");
        inner.SetResources(innerResources);
        using (Stream content = inner.GetContentStream().CreateOutputStream())
        {
            content.Write(Encoding.ASCII.GetBytes(FormattableString.Invariant($"""
                0 0 1 rg
                10 45 80 10 re
                f
                10 10 m
                90 10 l
                10 90 l
                h
                W
                n
                0 1 1 rg
                10 10 80 80 re
                f
                q
                80 0 0 80 10 10 cm
                /{tileName.GetName()} Do
                Q
                """)));
        }

        PDFormXObject outer = new(new PDStream(document));
        outer.SetBBox(new PDRectangle(0, 0, 100, 100));
        using (PDFormContentStream content = new(outer))
        {
            content.DrawForm(inner);
        }

        using PDPageContentStream pageContent = new(document, page);
        pageContent.DrawForm(outer);
        return document;
    }

    private static PDDocument CreateAcroFormDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDAcroForm acroForm = new(document);
        document.GetDocumentCatalog().SetAcroForm(acroForm);

        PDTextField text = new(acroForm);
        text.SetPartialName("fullName");
        text.SetReadOnly(true);
        text.SetRequired(true);
        text.SetMultiline(true);
        text.SetMaxLen(40);
        COSDictionary textDictionary = (COSDictionary)text.GetCOSObject();
        textDictionary.SetString(COSName.GetPDFName("TU"), "Full legal name");
        textDictionary.SetString(COSName.V, "Erik");
        textDictionary.SetString(COSName.GetPDFName("DV"), "Default name");

        PDCheckBox checkBox = new(acroForm);
        checkBox.SetPartialName("accepted");
        ((COSDictionary)checkBox.GetCOSObject()).SetName(COSName.V, "Yes");
        ((COSDictionary)checkBox.GetCOSObject()).SetName(COSName.GetPDFName("DV"), "Off");

        PDRadioButton radio = new(acroForm);
        radio.SetPartialName("contactMethod");
        radio.SetExportValues(["email", "phone"]);
        ((COSDictionary)radio.GetCOSObject()).SetName(COSName.V, "phone");
        ((COSDictionary)radio.GetCOSObject()).SetName(COSName.GetPDFName("DV"), "email");

        PDComboBox combo = new(acroForm);
        combo.SetPartialName("country");
        combo.SetOptions(["us", "no"], ["United States", "Norway"]);
        ((COSDictionary)combo.GetCOSObject()).SetString(COSName.V, "no");
        ((COSDictionary)combo.GetCOSObject()).SetString(COSName.GetPDFName("DV"), "us");

        PDListBox list = new(acroForm);
        list.SetPartialName("colors");
        list.SetMultiSelect(true);
        list.SetOptions(["red", "green", "blue"]);
        ((COSDictionary)list.GetCOSObject()).SetItem(
            COSName.V,
            new COSArray { new COSString("red"), new COSString("blue") });

        PDSignatureField signature = new(acroForm);
        signature.SetPartialName("approval");
        PDSignature value = new();
        value.SetName("Ada Lovelace");
        ((COSDictionary)signature.GetCOSObject()).SetItem(COSName.V, value);

        List<PDAnnotationWidget> annotations = [];
        SetWidgets(text, [CreateFormWidget(page, new PDRectangle(20, 700, 180, 24))], annotations);
        SetWidgets(checkBox, [CreateFormWidget(page, new PDRectangle(20, 660, 16, 16))], annotations);
        SetWidgets(
            radio,
            [
                CreateFormWidget(page, new PDRectangle(20, 620, 16, 16)),
                CreateFormWidget(page, new PDRectangle(50, 620, 16, 16))
            ],
            annotations);
        SetWidgets(combo, [CreateFormWidget(page, new PDRectangle(20, 580, 120, 22))], annotations);
        SetWidgets(list, [CreateFormWidget(page, new PDRectangle(20, 500, 120, 60))], annotations);
        SetWidgets(signature, [CreateFormWidget(page, new PDRectangle(20, 430, 180, 50))], annotations);

        acroForm.SetFields([text, checkBox, radio, combo, list, signature]);
        page.SetAnnotations(annotations.Cast<PDAnnotation>().ToList());
        return document;
    }

    private static PDDocument CreateSemanticAcroFormDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDAcroForm acroForm = new(document);
        document.GetDocumentCatalog().SetAcroForm(acroForm);

        PDTextField text = new(acroForm);
        text.SetPartialName("fullName");
        PDComboBox choice = new(acroForm);
        choice.SetPartialName("country");
        choice.SetOptions(["no", "us"], ["Norway", "United States"]);

        PDNonTerminalField tax = new(acroForm);
        tax.SetPartialName("Boxes3a-b_ReadOrder[0]");
        PDCheckBox individual = new(acroForm);
        individual.SetPartialName("c1_1[0]");
        PDCheckBox corporation = new(acroForm);
        corporation.SetPartialName("c1_1[1]");
        PDCheckBox unrelated = new(acroForm);
        unrelated.SetPartialName("c1_2[0]");
        tax.SetChildren([individual, corporation, unrelated]);

        PDRadioButton radio = new(acroForm);
        radio.SetPartialName("contactMethod");
        radio.SetExportValues(["email", "phone"]);

        List<PDAnnotationWidget> annotations = [];
        SetWidgets(text, [CreateFormWidget(page, new PDRectangle(20, 700, 180, 24))], annotations);
        SetWidgets(choice, [CreateFormWidget(page, new PDRectangle(20, 650, 120, 22))], annotations);
        SetWidgets(individual, [CreateFormWidget(page, new PDRectangle(20, 570, 16, 16))], annotations);
        SetWidgets(corporation, [CreateFormWidget(page, new PDRectangle(20, 545, 16, 16))], annotations);
        SetWidgets(unrelated, [CreateFormWidget(page, new PDRectangle(20, 520, 16, 16))], annotations);
        SetWidgets(
            radio,
            [
                CreateFormWidget(page, new PDRectangle(200, 570, 16, 16)),
                CreateFormWidget(page, new PDRectangle(260, 570, 16, 16))
            ],
            annotations);

        acroForm.SetFields([text, choice, tax, radio]);
        page.SetAnnotations(annotations.Cast<PDAnnotation>().ToList());
        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.RESOURCES, CreateDefaultResourcesDictionary());
        pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream("""
            BT /F1 10 Tf 20 735 Td (Full legal name) Tj ET
            BT /F1 10 Tf 20 680 Td (Country) Tj ET
            BT /F1 10 Tf 20 610 Td (Tax classification) Tj ET
            BT /F1 10 Tf 42 574 Td (Individual) Tj ET
            BT /F1 10 Tf 42 549 Td (Corporation) Tj ET
            BT /F1 10 Tf 42 524 Td (Unrelated consent) Tj ET
            BT /F1 10 Tf 200 610 Td (Preferred contact) Tj ET
            BT /F1 10 Tf 222 574 Td (Email) Tj ET
            BT /F1 10 Tf 282 574 Td (Phone) Tj ET
            """));
        return document;
    }

    private static void SetWidgets(
        PDField field,
        IList<PDAnnotationWidget> widgets,
        List<PDAnnotationWidget> annotations)
    {
        field.SetWidgets(widgets);
        annotations.AddRange(widgets);
    }

    private static PDAnnotationWidget CreateFormWidget(PDPage page, PDRectangle rectangle)
    {
        PDAnnotationWidget widget = new();
        widget.SetRectangle(rectangle);
        widget.SetPage(page);
        return widget;
    }

    private static COSStream CreateContentStream(string contentStream)
    {
        COSStream stream = new();
        using Stream output = stream.CreateOutputStream();
        byte[] bytes = Encoding.Latin1.GetBytes(contentStream);
        output.Write(bytes, 0, bytes.Length);
        return stream;
    }

    private static PDDocument CreateImageDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        byte[] rgb =
        [
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255
        ];
        PDImageXObject image = LosslessFactory.CreateFromRawData(document, rgb, 2, 2, 8, 3);
        using (PDPageContentStream content = new(document, page))
        {
            content.DrawImage(image, 72, 600, 120, 60);
        }

        return document;
    }

    private static PDDocument CreateJpegImageDocument(byte[] jpeg)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        using MemoryStream input = new(jpeg);
        PDImageXObject image = JPEGFactory.CreateFromStream(document, input);
        using (PDPageContentStream content = new(document, page))
        {
            content.DrawImage(image, 72, 600, 120, 60);
        }

        return document;
    }

    private static PDDocument CreateRotatedCroppedImageDocument(int rotation)
    {
        PDDocument document = new();
        PDPage page = new();
        page.SetCropBox(new PDRectangle(10, 20, 200, 300));
        page.SetRotation(rotation);
        document.AddPage(page);
        PDImageXObject image = LosslessFactory.CreateFromRawData(document, [255, 255, 255], 1, 1, 8, 3);
        using (PDPageContentStream content = new(document, page))
        {
            content.DrawImage(image, 40, 70, 50, 30);
        }

        return document;
    }

    private static PDDocument CreateInlineImageDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.CONTENTS, CreateInlineImageContentStream());
        return document;
    }

    private static PDDocument CreateRotatedCroppedInlineImageDocument(int rotation)
    {
        PDDocument document = new();
        PDPage page = new();
        page.SetCropBox(new PDRectangle(10, 20, 200, 300));
        page.SetRotation(rotation);
        document.AddPage(page);

        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.CONTENTS, CreateInlineImageContentStream("50 0 0 30 40 70"));
        return document;
    }

    private static COSStream CreateInlineImageContentStream()
    {
        return CreateInlineImageContentStream("120 0 0 60 72 600");
    }

    private static COSStream CreateInlineImageContentStream(string transform)
    {
        COSStream stream = new();
        using Stream output = stream.CreateOutputStream();
        WriteLatin1(output, $"q\n{transform} cm\nBI\n/W 2 /H 2 /BPC 8 /CS /RGB\nID\n");
        output.Write([
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255
        ]);
        WriteLatin1(output, "\nEI\nQ\n");
        return stream;
    }

    private static COSDictionary CreateDefaultResourcesDictionary()
    {
        COSDictionary fontDictionary = new();
        fontDictionary.SetItem(COSName.TYPE, COSName.GetPDFName("Font"));
        fontDictionary.SetItem(COSName.GetPDFName("Subtype"), COSName.GetPDFName("Type1"));
        fontDictionary.SetItem(COSName.GetPDFName("BaseFont"), COSName.GetPDFName("Helvetica"));

        COSDictionary fonts = new();
        fonts.SetItem(COSName.GetPDFName("F1"), fontDictionary);

        COSDictionary resources = new();
        resources.SetItem(COSName.GetPDFName("Font"), fonts);
        return resources;
    }

    private static void WriteLatin1(Stream stream, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}
