using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Playwright;
using PdfBox.Net.COS;
using PdfBox.Net.FontBox.CFF;
using PdfBox.Net.FontBox.TTF;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.Common.Function;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Graphics;
using PdfBox.Net.PDModel.Graphics.Color;
using PdfBox.Net.PDModel.Graphics.Form;
using PdfBox.Net.PDModel.Graphics.Image;
using PdfBox.Net.PDModel.Graphics.State;
using PdfBox.Net.PDModel.Interactive.Action;
using PdfBox.Net.PDModel.Interactive.Annotation;
using PdfBox.Net.PDModel.Resources;
using PdfBox.Net.Rendering;
using PdfBox.Net.Util;

namespace PdfBox.Net.Html.Tests;

public class PdfHtmlConverterTest
{
    [Fact]
    public void Convert_DeviceCmykWhiteOverprintModeOne_DoesNotPaintFill()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDExtendedGraphicsState overprintModeZero = new();
        overprintModeZero.SetNonStrokingOverprintControl(true);
        overprintModeZero.SetOverprintMode(0);
        PDExtendedGraphicsState overprintModeOne = new();
        overprintModeOne.SetNonStrokingOverprintControl(true);
        overprintModeOne.SetOverprintMode(1);

        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(0f, 0f, 0f, 0f);
            content.AddRect(10, 10, 10, 10);
            content.Fill();
            content.SetGraphicsStateParameters(overprintModeZero);
            content.AddRect(30, 10, 10, 10);
            content.Fill();
            content.SetGraphicsStateParameters(overprintModeOne);
            content.AddRect(50, 10, 10, 10);
            content.Fill();
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        IReadOnlyList<PdfLayoutPath> paths = Assert.Single(layout.Pages).Paths;
        Assert.Equal(3, paths.Count);
        Assert.True(paths[0].IsFilled);
        Assert.True(paths[1].IsFilled);
        Assert.False(paths[2].IsFilled);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement[] vectorPaths = dom.Descendants()
            .Where(element => element.Name.LocalName == "path" && element.Attribute("data-path-index") is not null)
            .ToArray();
        Assert.Equal("#FFFFFF", vectorPaths[0].Attribute("fill")?.Value);
        Assert.Equal("#FFFFFF", vectorPaths[1].Attribute("fill")?.Value);
        Assert.Equal("none", vectorPaths[2].Attribute("fill")?.Value);
    }

    [Fact]
    public void Convert_ZeroTintSeparationOverprintModeOne_DoesNotPaintFill()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDSeparation spotWhite = new(
            "Spot White",
            PDDeviceCMYK.Instance,
            CreateType4Function("{ pop 0 0 0 0 }", 1, 4));
        PDExtendedGraphicsState overprintModeZero = new();
        overprintModeZero.SetNonStrokingOverprintControl(true);
        overprintModeZero.SetOverprintMode(0);
        PDExtendedGraphicsState overprintModeOne = new();
        overprintModeOne.SetNonStrokingOverprintControl(true);
        overprintModeOne.SetOverprintMode(1);

        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(new PDColor([0f], spotWhite));
            content.AddRect(10, 10, 10, 10);
            content.Fill();
            content.SetGraphicsStateParameters(overprintModeZero);
            content.AddRect(30, 10, 10, 10);
            content.Fill();
            content.SetGraphicsStateParameters(overprintModeOne);
            content.AddRect(50, 10, 10, 10);
            content.Fill();
        }

        PdfLayoutPath[] paths = Assert.Single(PdfLayoutExtractor.Extract(document).Pages).Paths.ToArray();
        Assert.Equal(3, paths.Length);
        Assert.True(paths[0].IsFilled);
        Assert.True(paths[1].IsFilled);
        Assert.False(paths[2].IsFilled);

        XElement[] vectorPaths = ParseHtml(PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document)).Html)
            .Descendants()
            .Where(element => element.Name.LocalName == "path" && element.Attribute("data-path-index") is not null)
            .ToArray();
        Assert.Equal("none", vectorPaths[2].Attribute("fill")?.Value);
    }

    [Fact]
    public void Convert_DeviceCmykOverprintModeOne_ComposesRepeatedOpaquePathComponents()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDExtendedGraphicsState overprintModeOne = new();
        overprintModeOne.SetNonStrokingOverprintControl(true);
        overprintModeOne.SetOverprintMode(1);
        PDExtendedGraphicsState normalPaint = new();
        normalPaint.SetNonStrokingOverprintControl(false);
        normalPaint.SetOverprintMode(0);

        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(0.9f, 0.1f, 0.1f, 0.5f);
            content.AddRect(10, 10, 30, 30);
            content.Fill();

            content.SetNonStrokingColor(0.9f, 0.1f, 0.9f, 0f);
            content.AddRect(15, 15, 10, 10);
            content.Fill();
            content.AddRect(15.002f, 15.002f, 10, 10);
            content.Fill();

            content.SetGraphicsStateParameters(overprintModeOne);
            content.SetNonStrokingColor(0f, 0f, 0.1f, 0.5f);
            content.AddRect(15, 15, 10, 10);
            content.Fill();
            content.AddRect(15.002f, 15.002f, 10, 10);
            content.Fill();

            content.SetGraphicsStateParameters(normalPaint);
            content.SetNonStrokingColor(0.9f, 0.1f, 0.9f, 0f);
            content.AddRect(70, 15, 10, 10);
            content.Fill();

            content.SetGraphicsStateParameters(overprintModeOne);
            content.SetNonStrokingColor(0f, 0f, 0.1f, 0.5f);
            content.AddRect(70, 15, 10, 10);
            content.Fill();

            content.AddRect(50, 15, 10, 10);
            content.Fill();
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        IReadOnlyList<PdfLayoutPath> paths = Assert.Single(layout.Pages).Paths;
        Assert.Equal(8, paths.Count);

        float[] expectedCompositeRgb = PDDeviceCMYK.Instance.ToRGB([0.9f, 0.1f, 0.1f, 0.5f]);
        const float browserColorTolerance = (1f / 255f) + 0.0001f;
        foreach (PdfLayoutPath path in paths.Skip(1).Take(4))
        {
            PdfLayoutColor composite = Assert.IsType<PdfLayoutColor>(path.FillColor);
            Assert.InRange(MathF.Abs(composite.Red - expectedCompositeRgb[0]), 0f, browserColorTolerance);
            Assert.InRange(MathF.Abs(composite.Green - expectedCompositeRgb[1]), 0f, browserColorTolerance);
            Assert.InRange(MathF.Abs(composite.Blue - expectedCompositeRgb[2]), 0f, browserColorTolerance);
            Assert.Equal(["Cyan", "Magenta", "Yellow", "Black"], path.ColorantNames);
        }

        float[] expectedUncomposedRgb = PDDeviceCMYK.Instance.ToRGB([0f, 0f, 0.1f, 0.5f]);
        float[] expectedSimpleBackdropRgb = PDDeviceCMYK.Instance.ToRGB([0.9f, 0.1f, 0.9f, 0f]);
        PdfLayoutColor simpleBackdrop = Assert.IsType<PdfLayoutColor>(paths[5].FillColor);
        Assert.InRange(MathF.Abs(simpleBackdrop.Red - expectedSimpleBackdropRgb[0]), 0f, browserColorTolerance);
        Assert.InRange(MathF.Abs(simpleBackdrop.Green - expectedSimpleBackdropRgb[1]), 0f, browserColorTolerance);
        Assert.InRange(MathF.Abs(simpleBackdrop.Blue - expectedSimpleBackdropRgb[2]), 0f, browserColorTolerance);

        PdfLayoutColor simpleOverprint = Assert.IsType<PdfLayoutColor>(paths[6].FillColor);
        Assert.InRange(MathF.Abs(simpleOverprint.Red - expectedUncomposedRgb[0]), 0f, browserColorTolerance);
        Assert.InRange(MathF.Abs(simpleOverprint.Green - expectedUncomposedRgb[1]), 0f, browserColorTolerance);
        Assert.InRange(MathF.Abs(simpleOverprint.Blue - expectedUncomposedRgb[2]), 0f, browserColorTolerance);

        PdfLayoutColor differentGeometry = Assert.IsType<PdfLayoutColor>(paths[7].FillColor);
        Assert.Equal(simpleOverprint, differentGeometry);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement[] vectorPaths = dom.Descendants()
            .Where(element => element.Name.LocalName == "path" && element.Attribute("data-path-index") is not null)
            .ToArray();
        foreach (XElement path in vectorPaths.Skip(1).Take(4))
        {
            Assert.Equal(vectorPaths[0].Attribute("fill")?.Value, path.Attribute("fill")?.Value);
        }
        Assert.NotEqual(vectorPaths[5].Attribute("fill")?.Value, vectorPaths[6].Attribute("fill")?.Value);
        Assert.Equal(vectorPaths[6].Attribute("fill")?.Value, vectorPaths[7].Attribute("fill")?.Value);
    }

    [Fact]
    public void Convert_DeviceNOverprint_ComposesDeclaredProcessComponentsWithMatchingCmykPath()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDDeviceN deviceN = new(
            ["Cyan", "None"],
            PDDeviceCMYK.Instance,
            CreateType4Function("{ pop pop 1 0 0 0 }", 2, 4));
        PDExtendedGraphicsState overprint = new();
        overprint.SetNonStrokingOverprintControl(true);
        overprint.SetOverprintMode(1);

        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(0f, 0f, 0f, 1f);
            content.AddRect(10, 10, 20, 20);
            content.Fill();
            content.SetGraphicsStateParameters(overprint);
            content.SetNonStrokingColor(new PDColor([1f, 0f], deviceN));
            content.AddRect(10, 10, 20, 20);
            content.Fill();
        }

        PdfLayoutPath[] paths = Assert.Single(PdfLayoutExtractor.Extract(document).Pages).Paths.ToArray();
        Assert.Equal(2, paths.Length);
        PdfLayoutColor backdrop = Assert.IsType<PdfLayoutColor>(paths[0].FillColor);
        PdfLayoutColor overprinted = Assert.IsType<PdfLayoutColor>(paths[1].FillColor);
        Assert.Equal(backdrop, overprinted);

        float[] expectedRgb = PDDeviceCMYK.Instance.ToRGB([1f, 0f, 0f, 1f]);
        const float tolerance = (1f / 255f) + 0.0001f;
        Assert.InRange(MathF.Abs(overprinted.Red - expectedRgb[0]), 0f, tolerance);
        Assert.InRange(MathF.Abs(overprinted.Green - expectedRgb[1]), 0f, tolerance);
        Assert.InRange(MathF.Abs(overprinted.Blue - expectedRgb[2]), 0f, tolerance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Convert_SeparationAndDeviceCmykOverprint_NoOpUsesContainingBackdrop(bool spotOnTop)
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDSeparation spot = new(
            "GWG Green",
            PDDeviceCMYK.Instance,
            CreateType4Function(
                spotOnTop ? "{ pop 0.005 0 0.01 0 }" : "{ pop 0.5 0 1 0 }",
                1,
                4));
        PDExtendedGraphicsState overprint = new();
        overprint.SetNonStrokingOverprintControl(true);
        overprint.SetOverprintMode(1);

        PDColor spotColor = new([1f], spot);
        PDColor processColor = new(
            spotOnTop ? [0.5f, 0f, 1f, 0f] : [0.005f, 0f, 0.01f, 0f],
            PDDeviceCMYK.Instance);
        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(spotOnTop ? processColor : spotColor);
            content.AddRect(10, 10, 20, 20);
            content.Fill();
            content.SetGraphicsStateParameters(overprint);
            content.SetNonStrokingColor(spotOnTop ? spotColor : processColor);
            content.AddRect(12, 12, 16, 16);
            content.Fill();
        }

        PdfLayoutPath[] paths = Assert.Single(PdfLayoutExtractor.Extract(document).Pages).Paths.ToArray();
        Assert.Equal(2, paths.Length);
        Assert.Equal(paths[0].FillColor, paths[1].FillColor);
        Assert.Contains("GWG Green", paths[spotOnTop ? 1 : 0].ColorantNames);

        XElement[] vectorPaths = ParseHtml(PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document)).Html)
            .Descendants()
            .Where(element => element.Name.LocalName == "path" && element.Attribute("data-path-index") is not null)
            .ToArray();
        Assert.Equal(vectorPaths[0].Attribute("fill")?.Value, vectorPaths[1].Attribute("fill")?.Value);
    }

    [Fact]
    public void Convert_DeviceNBackdropWithProcessAndSeparationNoOps_PreservesRenderedFill()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDDeviceN backdropSpace = new(
            ["Black", "GWG Green"],
            PDDeviceCMYK.Instance,
            CreateType4Function("{ pop pop 0.5 0 1 0.5 }", 2, 4));
        PDSeparation black = new(
            "Black",
            PDDeviceCMYK.Instance,
            CreateType4Function("{ 0 0 0 4 -1 roll }", 1, 4));
        PDColor backdropColor = new([0.5f, 1f], backdropSpace);
        PDExtendedGraphicsState overprintModeZero = new();
        overprintModeZero.SetNonStrokingOverprintControl(true);
        overprintModeZero.SetOverprintMode(0);

        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(backdropColor);
            content.AddRect(10, 10, 30, 30);
            content.Fill();

            content.SetGraphicsStateParameters(overprintModeZero);
            content.SetNonStrokingColor(0f, 0f, 0f, 0.5f);
            content.SetStrokingColor(backdropColor);
            content.AddRect(12, 12, 26, 26);
            content.FillAndStroke();

            content.SetNonStrokingColor(new PDColor([0.5f], black));
            content.SetStrokingColor(backdropColor);
            content.AddRect(12, 12, 26, 26);
            content.FillAndStroke();
        }

        PdfLayoutPath[] paths = Assert.Single(PdfLayoutExtractor.Extract(document).Pages).Paths.ToArray();
        Assert.Equal(3, paths.Length);
        Assert.All(paths.Skip(1), path => Assert.Equal(paths[0].FillColor, path.FillColor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Convert_SeparationAndDeviceCmykStencilOverprint_NoOpUsesContainingBackdrop(bool spotOnTop)
    {
        using PDDocument document = CreateSeparationStencilOverprintDocument(spotOnTop);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfLayoutPath backdrop = Assert.Single(page.Paths);
        PdfLayoutImage image = Assert.Single(page.Images);
        Assert.Equal(backdrop.FillColor, image.OverprintCompositeColor);

        XElement convertedImage = Assert.Single(ParseHtml(PdfHtmlConverter.Convert(layout).Html)
            .Descendants(), element => element.Name.LocalName == "img");
        Assert.StartsWith("data:image/svg+xml;base64,", convertedImage.Attribute("src")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_UniformIndexedDeviceNOverprint_ComposesMatchingCmykClipBackdrop()
    {
        using PDDocument document = CreateUniformIndexedDeviceNClipOverprintDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfLayoutPath backdrop = Assert.Single(page.Paths);
        PdfLayoutImage image = Assert.Single(page.Images);
        PdfLayoutColor composite = Assert.IsType<PdfLayoutColor>(image.OverprintCompositeColor);
        Assert.Equal(composite, backdrop.FillColor);

        XElement convertedImage = Assert.Single(ParseHtml(PdfHtmlConverter.Convert(layout).Html)
            .Descendants(), element => element.Name.LocalName == "img");
        Assert.StartsWith("data:image/svg+xml;base64,", convertedImage.Attribute("src")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_UniformIndexedDeviceNOverprint_KnocksOutDeclaredZeroProcessComponents()
    {
        using PDDocument document = CreateUniformIndexedDeviceNClipOverprintDocument(
            ["Cyan", "Yellow", "Black", "None"],
            [255, 0, 0, 0]);

        PdfLayoutPage page = Assert.Single(PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        }).Pages);
        PdfLayoutColor composite = Assert.IsType<PdfLayoutColor>(Assert.Single(page.Images).OverprintCompositeColor);

        float[] expectedRgb = PDDeviceCMYK.Instance.ToRGB([1f, 0f, 0f, 0f]);
        const float tolerance = (1f / 255f) + 0.0001f;
        Assert.InRange(MathF.Abs(composite.Red - expectedRgb[0]), 0f, tolerance);
        Assert.InRange(MathF.Abs(composite.Green - expectedRgb[1]), 0f, tolerance);
        Assert.InRange(MathF.Abs(composite.Blue - expectedRgb[2]), 0f, tolerance);
    }

    [Fact]
    public void Convert_OverprintingIndexedDeviceNImage_PreservesUnderlyingAbsentProcessColorant()
    {
        using PDDocument document = CreateIndexedDeviceNOverprintDocument(
            imageOverprint: true,
            imageColorants: ["Cyan", "Black", "SpotGreen", "SpotRed"]);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfLayoutPath spotPath = Assert.Single(page.Paths);
        PdfLayoutImage image = Assert.Single(page.Images);
        Assert.Equal(
            [
                new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Path, spotPath.Index),
                new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Image, image.Index)
            ],
            page.PaintOperations);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement[] orderedPaints = dom.Descendants()
            .Where(element =>
                element.Name.LocalName == "img" ||
                (element.Name.LocalName == "path" && element.Attribute("data-path-index")?.Value == "0"))
            .ToArray();

        Assert.Equal(["path", "img", "path"], orderedPaints.Select(element => element.Name.LocalName));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Convert_IndexedDeviceNImage_DoesNotReplayOrdinaryOrMatchingProcessPaint(
        bool imageOverprint,
        bool imageContainsProcessYellow)
    {
        string[] imageColorants = imageContainsProcessYellow
            ? ["Cyan", "Black", "SpotGreen", "Yellow"]
            : ["Cyan", "Black", "SpotGreen", "SpotRed"];
        using PDDocument document = CreateIndexedDeviceNOverprintDocument(imageOverprint, imageColorants);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);

        Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "path" && element.Attribute("data-path-index")?.Value == "0");
    }

    [Fact]
    public void Convert_ConsecutiveOverprintingDeviceNImages_PreserveBothUnderlyingProcessMarks()
    {
        using PDDocument document = CreateIndexedDeviceNOverprintDocument(
            imageOverprint: true,
            imageColorants: ["Cyan", "Black", "SpotGreen", "SpotRed"],
            placementCount: 2);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);

        Assert.Equal(2, dom.Descendants().Count(element =>
            element.Name.LocalName == "path" && element.Attribute("data-path-index")?.Value == "0"));
        Assert.Equal(2, dom.Descendants().Count(element =>
            element.Name.LocalName == "path" && element.Attribute("data-path-index")?.Value == "1"));
    }

    [Fact]
    public void Convert_FormScopedClips_AreAppliedToIndividualPaths()
    {
        using PDDocument document = CreateFormScopedClipDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfLayoutPage page = Assert.Single(layout.Pages);
        Assert.Equal(2, page.Paths.Count);
        Assert.NotNull(page.Paths[0].ClipBounds);
        Assert.NotNull(page.Paths[1].ClipBounds);
        Assert.NotEqual(page.Paths[0].ClipBounds, page.Paths[1].ClipBounds);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);
        XElement[] paths = dom.Descendants()
            .Where(element => element.Name.LocalName == "path" && element.Attribute("data-path-index") is not null)
            .ToArray();
        Assert.Equal(2, paths.Length);
        Assert.All(paths, path => Assert.StartsWith("url(#pdf-vector-page-1-", path.Attribute("clip-path")?.Value));
        Assert.Equal(2, dom.Descendants().Count(element =>
            element.Name.LocalName == "clipPath" && element.Attribute("id")?.Value.Contains("path-clip", StringComparison.Ordinal) == true));
        Assert.All(dom.Descendants().Where(element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value.Contains("path-clip", StringComparison.Ordinal) == true), clipPath =>
            Assert.Contains(clipPath.Descendants(), element => element.Name.LocalName == "path"));
    }

    [Fact]
    public void Convert_NestedFormLateClipEmitsExactVectorAndImageClips()
    {
        using PDDocument document = CreateNestedLateClipDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfLayoutPage page = Assert.Single(layout.Pages);
        Assert.Equal(2, page.Paths.Count);
        Assert.Equal(2, page.Paths[1].ClipPaths.Count);
        Assert.Equal(3, Assert.Single(page.Images).ClipPaths.Count);
        Assert.DoesNotContain(layout.Diagnostics, diagnostic => diagnostic.Code == "path-clipping-unsupported");

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);
        XElement clippedTile = Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "path" && element.Attribute("data-path-index")?.Value == "1");
        Assert.Contains("path-clip-1", clippedTile.Attribute("clip-path")?.Value, StringComparison.Ordinal);
        XElement vectorClip = Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value.EndsWith("path-clip-1", StringComparison.Ordinal) == true);
        XElement vectorClipPath = Assert.Single(vectorClip.Descendants(), element => element.Name.LocalName == "path");
        Assert.Equal("nonzero", vectorClipPath.Attribute("clip-rule")?.Value);
        Assert.Contains("L", vectorClipPath.Attribute("d")?.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(vectorClip.Descendants(), element => element.Name.LocalName == "rect");

        XElement image = Assert.Single(ElementsByClass(dom, "pdf-image"));
        Assert.Equal("img", image.Name.LocalName);
        Assert.Contains("clip-path:url(#pdf-image-page-1-clip-0)", image.Attribute("style")?.Value, StringComparison.Ordinal);
        XElement imageClip = Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value == "pdf-image-page-1-clip-0");
        Assert.Equal("objectBoundingBox", imageClip.Attribute("clipPathUnits")?.Value);
        Assert.Contains(imageClip.Descendants(), element =>
            element.Name.LocalName == "path" && element.Attribute("d")?.Value.Contains("L", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Convert_LineAndCubicRectangularImageClipChain_EmitsItsSingleIntersection()
    {
        static PdfLayoutClipPath Rectangle(float x, float y, float width, float height)
        {
            PdfLayoutRectangle bounds = new(x, y, width, height);
            return new PdfLayoutClipPath(
                [
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, bounds.X, bounds.Y, 0f, 0f, 0f, 0f),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.Right, bounds.Y, 0f, 0f, 0f, 0f),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.Right, bounds.Bottom, 0f, 0f, 0f, 0f),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.X, bounds.Bottom, 0f, 0f, 0f, 0f),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.X, bounds.Y, 0f, 0f, 0f, 0f),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f)
                ],
                bounds,
                1);
        }

        static PdfLayoutClipPath CubicRectangle(float x, float y, float width, float height)
        {
            PdfLayoutRectangle bounds = new(x, y, width, height);
            return new PdfLayoutClipPath(
                [
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, bounds.X, bounds.Y, 0f, 0f, 0f, 0f),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.CurveTo, bounds.X, bounds.Y, bounds.X, bounds.Bottom, bounds.X, bounds.Bottom),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.CurveTo, bounds.X, bounds.Bottom, bounds.Right, bounds.Bottom, bounds.Right, bounds.Bottom),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.CurveTo, bounds.Right, bounds.Bottom, bounds.Right, bounds.Y, bounds.Right, bounds.Y),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.CurveTo, bounds.Right, bounds.Y, bounds.X, bounds.Y, bounds.X, bounds.Y),
                    new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f)
                ],
                bounds,
                1);
        }

        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutImage image = new(
            0,
            "image",
            PdfLayoutImageKind.XObject,
            new PdfLayoutRectangle(100f, 100f, 100f, 100f),
            new PdfLayoutTransform(100f, 0f, 0f, 100f, 100f, 100f),
            1,
            1,
            8,
            "DeviceRGB",
            false,
            "Im0",
            clipPaths:
            [
                Rectangle(0f, 0f, 612f, 792f),
                Rectangle(80f, 80f, 140f, 140f),
                CubicRectangle(100f, 100f, 100f, 100f)
            ]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [image],
            [],
            [],
            [],
            []);
        PdfLayoutDocument layout = new(
            [page],
            [new PdfLayoutImageAsset("image", "assets/images/image.png", "image/png", [1, 2, 3])],
            []);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);

        XElement clip = Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value == "pdf-image-page-1-clip-0");
        Assert.DoesNotContain(dom.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value.StartsWith("pdf-image-page-1-clip-0-step-", StringComparison.Ordinal) == true);
        Assert.Equal("M 0 0 L 1 0 L 1 1 L 0 1 Z", Assert.Single(clip.Elements()).Attribute("d")?.Value);
    }

    [Fact]
    public void Convert_MixedImageClipChain_OmitsContainingRectangleAncestors()
    {
        static PdfLayoutClipPath Clip(PdfLayoutRectangle bounds, params PdfLayoutPathCommand[] commands) =>
            new(commands, bounds, 1);

        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutRectangle imageBounds = new(100f, 100f, 100f, 100f);
        PdfLayoutClipPath pageRectangle = Clip(
            pageBounds,
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, 0f, 0f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 612f, 0f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 612f, 792f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 0f, 792f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f));
        PdfLayoutClipPath triangle = Clip(
            imageBounds,
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, 100f, 100f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 200f, 100f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 150f, 200f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f));
        PdfLayoutImage image = new(
            0,
            "image",
            PdfLayoutImageKind.XObject,
            imageBounds,
            new PdfLayoutTransform(100f, 0f, 0f, 100f, 100f, 100f),
            1,
            1,
            8,
            "DeviceRGB",
            false,
            "Im0",
            clipPaths: [pageRectangle, triangle]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [image],
            [],
            [],
            [],
            []);
        PdfLayoutDocument layout = new(
            [page],
            [new PdfLayoutImageAsset("image", "assets/images/image.png", "image/png", [1, 2, 3])],
            []);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);

        XElement clip = Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value == "pdf-image-page-1-clip-0");
        Assert.DoesNotContain(dom.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value.StartsWith("pdf-image-page-1-clip-0-step-", StringComparison.Ordinal) == true);
        Assert.Equal("M 0 0 L 1 0 L 0.5 1 Z", Assert.Single(clip.Elements()).Attribute("d")?.Value);
    }

    [Theory]
    [InlineData(43, 43, 42, false)]
    [InlineData(45, 43, 42, true)]
    public void Convert_UniformClippedImage_OmitsClipOnlyWhenItMatchesContainingUnderpaint(
        byte imageRed,
        byte imageGreen,
        byte imageBlue,
        bool expectsClip)
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutRectangle imageBounds = new(100f, 100f, 100f, 100f);
        PdfLayoutColor backdrop = new(43f / 255f, 43f / 255f, 42f / 255f, 1f, "DeviceCMYK");
        PdfLayoutPathCommand[] rectangle =
        [
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, 100f, 100f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 200f, 100f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 200f, 200f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 100f, 200f, 0f, 0f, 0f, 0f),
            new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f)
        ];
        PdfLayoutClipPath containingClip = new(rectangle, imageBounds, 1);
        PdfLayoutPath underpaint = new(
            0,
            rectangle,
            imageBounds,
            backdrop,
            null,
            1,
            clipPaths: [containingClip]);
        PdfLayoutClipPath triangle = new(
            [
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, 100f, 100f, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 200f, 100f, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 150f, 200f, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f)
            ],
            imageBounds,
            1);
        PdfLayoutImage image = new(
            0,
            "image",
            PdfLayoutImageKind.XObject,
            imageBounds,
            new PdfLayoutTransform(100f, 0f, 0f, 100f, 100f, 100f),
            1,
            1,
            1,
            "DeviceGray",
            false,
            "Im0",
            clipPaths: [triangle]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [image],
            [underpaint],
            [],
            [],
            [],
            [],
            paintOperations:
            [
                new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Path, underpaint.Index),
                new PdfLayoutPaintOperation(PdfLayoutPaintOperationKind.Image, image.Index)
            ]);
        PdfLayoutColor imageColor = new(
            imageRed / 255f,
            imageGreen / 255f,
            imageBlue / 255f,
            1f,
            "DeviceCMYK");
        PdfLayoutDocument layout = new(
            [page],
            [new PdfLayoutImageAsset("image", "assets/images/image.png", "image/png", [1, 2, 3], imageColor)],
            []);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);
        XElement renderedImage = Assert.Single(ElementsByClass(dom, "pdf-image"));

        Assert.Equal(expectsClip, renderedImage.Attribute("style")?.Value.Contains("clip-path", StringComparison.Ordinal) == true);
        Assert.Equal(expectsClip, dom.Descendants().Any(element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value == "pdf-image-page-1-clip-0"));
    }

    [Fact]
    public void Convert_TransparencyGroup_EmitsInvokingBlendMode()
    {
        using PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDTransparencyGroup group = new(new PDStream(document));
        group.SetBBox(new PDRectangle(0, 0, 100, 100));
        group.SetGroup(new PDTransparencyGroupAttributes());
        using (Stream formContent = group.GetContentStream().CreateOutputStream())
        {
            formContent.Write(Encoding.ASCII.GetBytes("0 1 1 0 k\n0 0 100 100 re\nf\n"));
        }

        PDExtendedGraphicsState graphicsState = new();
        graphicsState.SetBlendMode(BlendMode.MULTIPLY);
        using (PDPageContentStream pageContent = new(document, page))
        {
            pageContent.SetGraphicsStateParameters(graphicsState);
            pageContent.DrawForm(group);
        }

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfLayoutVectorGroup extractedGroup = Assert.Single(Assert.Single(layout.Pages).VectorGroups);
        Assert.Equal(BlendMode.MULTIPLY, extractedGroup.BlendMode);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        Assert.Contains("style=\"mix-blend-mode:multiply\"", html.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    public void Extract_TransparencyGroupFallback_SelectsOnlyCompactKnockoutGroups(
        bool knockout,
        bool isolated,
        int expectedFallbacks)
    {
        using PDDocument document = CreateCompactTransparencyGroupDocument(knockout, isolated);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeTransparencyGroupFallbacks = true
        });

        PdfLayoutImage[] fallbacks = Assert.Single(layout.Pages).Images
            .Where(image => image.Kind == PdfLayoutImageKind.TransparencyGroupFallback)
            .ToArray();
        Assert.Equal(expectedFallbacks, fallbacks.Length);
        Assert.Equal(expectedFallbacks, layout.ImageAssets.Count(asset =>
            asset.AssetId.Contains("transparency-group", StringComparison.Ordinal)));
    }

    [Fact]
    public void Extract_TransparencyGroupFallback_MatchesFullPageRenderCrop()
    {
        const float scale = 3f;
        using PDDocument document = CreateCompactTransparencyGroupDocument(knockout: true, isolated: false);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeTransparencyGroupFallbacks = true
        });

        PdfLayoutImage fallback = Assert.Single(
            Assert.Single(layout.Pages).Images,
            image => image.Kind == PdfLayoutImageKind.TransparencyGroupFallback);
        using BufferedImage fullPage = new PDFRenderer(document).RenderImage(0, scale, ImageType.RGB);
        AssertFallbackMatchesFullPageCrop(layout, fallback, fullPage, scale);
    }

    [Fact]
    public void Extract_TransparencyGroupFallback_BatchedRegionsMatchFullPageRenderCrops()
    {
        const float scale = 3f;
        using PDDocument document = CreateMultipleCompactTransparencyGroupDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeTransparencyGroupFallbacks = true
        });

        PdfLayoutImage[] fallbacks = Assert.Single(layout.Pages).Images
            .Where(image => image.Kind == PdfLayoutImageKind.TransparencyGroupFallback)
            .OrderBy(static image => image.Bounds.X)
            .ToArray();
        Assert.Equal(2, fallbacks.Length);
        using BufferedImage fullPage = new PDFRenderer(document).RenderImage(0, scale, ImageType.RGB);
        foreach (PdfLayoutImage fallback in fallbacks)
        {
            AssertFallbackMatchesFullPageCrop(layout, fallback, fullPage, scale);
        }
    }

    [Fact]
    public void Extract_TransparencyGroupFallback_NonZeroCropBoxMatchesFullPageRenderCrop()
    {
        const float scale = 3f;
        using PDDocument document = CreateCompactTransparencyGroupDocument(knockout: true, isolated: false);
        document.GetPage(0).SetCropBox(new PDRectangle(50, 100, 300, 400));

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeTransparencyGroupFallbacks = true
        });

        PdfLayoutImage fallback = Assert.Single(
            Assert.Single(layout.Pages).Images,
            image => image.Kind == PdfLayoutImageKind.TransparencyGroupFallback);
        using BufferedImage fullPage = new PDFRenderer(document).RenderImage(0, scale, ImageType.RGB);
        AssertFallbackMatchesFullPageCrop(layout, fallback, fullPage, scale);
    }

    private static void AssertFallbackMatchesFullPageCrop(
        PdfLayoutDocument layout,
        PdfLayoutImage fallback,
        BufferedImage fullPage,
        float scale)
    {
        PdfLayoutImageAsset asset = Assert.Single(
            layout.ImageAssets,
            candidate => candidate.AssetId == fallback.AssetId);
        using BufferedImage actual = DecodePng(asset.Data);
        int left = Math.Clamp((int)MathF.Floor(fallback.Bounds.X * scale), 0, fullPage.Width - 1);
        int top = Math.Clamp((int)MathF.Floor(fallback.Bounds.Y * scale), 0, fullPage.Height - 1);
        int right = Math.Clamp((int)MathF.Ceiling(fallback.Bounds.Right * scale), left + 1, fullPage.Width);
        int bottom = Math.Clamp((int)MathF.Ceiling(fallback.Bounds.Bottom * scale), top + 1, fullPage.Height);

        Assert.Equal(right - left, actual.Width);
        Assert.Equal(bottom - top, actual.Height);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                Assert.Equal(fullPage.GetRgb(x, y), actual.GetRgb(x - left, y - top));
            }
        }
    }

    [Fact]
    public void Convert_LuminositySoftMaskedUnderlay_DerivesTextDropShadowGeometry()
    {
        using PDDocument document = CreateLuminositySoftMaskedTextShadowDocument();

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeTransparencyGroupFallbacks = true
        });

        PdfLayoutPage page = Assert.Single(layout.Pages);
        PdfTextRun title = Assert.Single(page.Runs, run => run.Text == "Shadowed title");
        Assert.True(title.Shadow is not null,
            $"title={title.PageBounds}; paths={string.Join(';', page.Paths.Select(path => path.Bounds.ToString()))}; groups={string.Join(';', page.VectorGroups.Select(group => group.Bounds.ToString()))}");
        PdfTextShadow shadow = Assert.IsType<PdfTextShadow>(title.Shadow);
        Assert.InRange(shadow.OffsetX, 1f, 8f);
        Assert.InRange(shadow.OffsetY, 0.5f, 8f);
        Assert.InRange(shadow.BlurRadius, 1f, 6f);
        Assert.Equal(new PdfLayoutColor(0f, 0f, 0f, 0.6f, "DeviceRGB"), shadow.Color);
        Assert.DoesNotContain(page.Images, image => image.Kind == PdfLayoutImageKind.TransparencyGroupFallback);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(converted.Html);
        XElement titleElement = Assert.Single(dom.Descendants(), element =>
            element.Name.LocalName == "span" && element.Value.Contains("Shadowed title", StringComparison.Ordinal));
        Assert.True(HasClass(titleElement, "pdf-text-shadow"));
        string style = Assert.IsType<XAttribute>(titleElement.Attribute("style")).Value;
        Assert.Contains("--pdf-text-shadow-x:", style, StringComparison.Ordinal);
        Assert.Contains("--pdf-text-shadow-y:", style, StringComparison.Ordinal);
        Assert.Contains("--pdf-text-shadow-blur:", style, StringComparison.Ordinal);
        Assert.Contains("--pdf-text-shadow-color:rgba(0,0,0,0.6)", style, StringComparison.Ordinal);
        Assert.Contains("filter: drop-shadow(", converted.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("data-source-name=\"transparency-group\"", converted.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-path-index=", converted.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_UsesFixedLayoutFallbackForSpatialPages()
    {
        using PDDocument columnsDocument = Loader.LoadPDF(FixturePath("4PP-Highlighting.pdf"));
        PdfLayoutDocument columnsLayout = PdfLayoutExtractor.Extract(columnsDocument);
        PdfHtmlDocument columnsHtml = PdfHtmlConverter.Convert(columnsLayout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument columnsDom = ParseHtml(columnsHtml.Html);
        XElement columnsGrid = Assert.Single(ElementsByClass(columnsDom, "pdf-semantic-line-grid"));
        XElement[] gridRows = columnsGrid.Descendants()
            .Where(element => HasClass(element, "pdf-semantic-line-grid-row"))
            .ToArray();
        Assert.Equal(84, gridRows.Length);
        Assert.All(gridRows, row => Assert.Equal(2, row.Descendants()
            .Count(element => HasClass(element, "pdf-semantic-line-grid-cell"))));
        XElement[] marks = columnsGrid.Descendants("mark").ToArray();
        Assert.Equal(16, marks.Length);
        Assert.All(marks, mark =>
        {
            Assert.True(HasClass(mark, "pdf-semantic-mark"));
            Dictionary<string, string> style = ParseStyle(mark.Attribute("style")?.Value ?? "");
            Assert.Equal("rgba(255,255,0,1)", style["--pdf-semantic-mark-background"]);
            Assert.DoesNotContain("position", style.Keys);
        });
        Assert.Equal(152, columnsGrid.Descendants()
            .Count(element => HasClass(element, "pdf-semantic-line-grid-cell") && !element.Descendants("mark").Any()));
        Assert.Empty(ElementsByClass(columnsDom, "pdf-semantic-layout-fallback-page"));
        Assert.Contains(".pdf-semantic-line-grid", columnsHtml.Css, StringComparison.Ordinal);
        Assert.Contains("mark.pdf-semantic-mark", columnsHtml.Css, StringComparison.Ordinal);

        using PDDocument staggeredColumnsDocument = CreateTextDocument("""
            BT
            /F1 10 Tf
            72 700 Td
            (left 01) Tj
            0 -14 Td (left 02) Tj
            0 -14 Td (left 03) Tj
            0 -14 Td (left 04) Tj
            0 -14 Td (left 05) Tj
            0 -14 Td (left 06) Tj
            0 -14 Td (left 07) Tj
            0 -14 Td (left 08) Tj
            0 -14 Td (left 09) Tj
            0 -14 Td (left 10) Tj
            0 -14 Td (left 11) Tj
            0 -14 Td (left 12) Tj
            ET
            BT
            /F1 10 Tf
            330 665 Td
            (right 01) Tj
            0 -14 Td (right 02) Tj
            0 -14 Td (right 03) Tj
            0 -14 Td (right 04) Tj
            0 -14 Td (right 05) Tj
            0 -14 Td (right 06) Tj
            0 -14 Td (right 07) Tj
            0 -14 Td (right 08) Tj
            0 -14 Td (right 09) Tj
            0 -14 Td (right 10) Tj
            0 -14 Td (right 11) Tj
            0 -14 Td (right 12) Tj
            ET
            """);
        PdfLayoutDocument staggeredColumnsLayout = PdfLayoutExtractor.Extract(staggeredColumnsDocument);
        PdfHtmlDocument staggeredColumnsHtml = PdfHtmlConverter.Convert(staggeredColumnsLayout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument staggeredColumnsDom = ParseHtml(staggeredColumnsHtml.Html);
        Assert.Equal(2, ElementsByClass(staggeredColumnsDom, "pdf-semantic-column").Count());
        Assert.Equal(24, ElementsByClass(staggeredColumnsDom, "pdf-semantic-column-run").Count());
        Assert.Empty(ElementsByClass(staggeredColumnsDom, "pdf-semantic-layout-fallback-page"));
        Assert.Contains(".pdf-semantic-columns", staggeredColumnsHtml.Css, StringComparison.Ordinal);

        using PDDocument formDocument = Loader.LoadPDF(FixturePath("Acroform-PDFBOX-2333.pdf"));
        PdfLayoutDocument formLayout = PdfLayoutExtractor.Extract(formDocument, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfHtmlDocument formHtml = PdfHtmlConverter.Convert(formLayout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument formDom = ParseHtml(formHtml.Html);
        XElement formFallback = Assert.Single(ElementsByClass(formDom, "pdf-semantic-layout-fallback-page"));
        Assert.Equal(formLayout.Pages[0].Images.Count,
            formFallback.Descendants().Count(element => HasClass(element, "pdf-image")));
        Assert.Equal(formLayout.Pages[0].FormControls.Count, formFallback.Descendants()
            .Count(element => HasClass(element, "pdf-form-control")));

        using PDDocument sparseDocument = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Title) Tj
            ET
            BT
            /F1 12 Tf
            285 650 Td
            (Placed expression) Tj
            ET
            """);
        PdfLayoutDocument sparseLayout = PdfLayoutExtractor.Extract(sparseDocument);
        PdfHtmlDocument sparseHtml = PdfHtmlConverter.Convert(sparseLayout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument sparseDom = ParseHtml(sparseHtml.Html);
        XElement sparseFallback = Assert.Single(ElementsByClass(sparseDom, "pdf-semantic-layout-fallback-page"));
        Assert.Equal(sparseLayout.Pages[0].Runs.Count, sparseFallback.Descendants()
            .Count(element => HasClass(element, "pdf-text-run")));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesSpanningHeaderBeforeDetectedColumns()
    {
        using PDDocument document = CreateTwoColumnDocument(includeRuledTable: false);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-semantic-line-grid"));
        XElement spanning = Assert.Single(ElementsByClass(dom, "pdf-semantic-column-spanning"));
        Assert.Contains("Two-Column Guidance", spanning.Value, StringComparison.Ordinal);
        Assert.Contains("Spanning source header", spanning.Value, StringComparison.Ordinal);
        XElement[] spanningRuns = spanning.DescendantsAndSelf()
            .Where(element => HasClass(element, "pdf-semantic-column-spanning-run"))
            .ToArray();
        Assert.NotEmpty(spanningRuns);
        Assert.All(spanningRuns, run =>
        {
            string fontSize = ParseStyle(run.Attribute("style")?.Value ?? "")["font-size"];
            Assert.Matches(@"^\d+(?:\.\d+)?pt$", fontSize);
            Assert.True(ParsePoints(fontSize) > 0);
        });
        XElement[] columns = ElementsByClass(dom, "pdf-semantic-column").ToArray();
        Assert.Equal(2, columns.Length);
        Assert.Contains("Left body 01", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Left body 12", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Right body 01", columns[1].Value, StringComparison.Ordinal);
        Assert.True(
            dom.Root!.Value.IndexOf("Spanning source header", StringComparison.Ordinal) <
            dom.Root.Value.IndexOf("Left body 01", StringComparison.Ordinal));

        Dictionary<string, string> style = ParseStyle(
            Assert.Single(ElementsByClass(dom, "pdf-semantic-columns")).Attribute("style")?.Value ?? "");
        Assert.True(ParsePoints(style["--pdf-semantic-column-gap"]) >= 10f);
        Assert.True(ParsePoints(style["--pdf-semantic-column-left-width"]) >= 170f);
        Assert.True(ParsePoints(style["--pdf-semantic-column-right-width"]) >= 170f);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_UsesMeasuredThreeColumnGridInColumnMajorOrder()
    {
        using PDDocument document = CreateThreeColumnDocument(includeRuledTable: false);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-semantic-line-grid"));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        XElement wrapper = Assert.Single(ElementsByClass(dom, "pdf-semantic-columns"));
        Assert.Equal("div", wrapper.Name.LocalName);
        Dictionary<string, string> style = ParseStyle(wrapper.Attribute("style")?.Value ?? "");
        Assert.Equal("3", style["--pdf-semantic-column-count"]);
        Assert.Contains("fr", style["--pdf-semantic-column-tracks"], StringComparison.Ordinal);

        float[] widths = Enumerable.Range(1, 3)
            .Select(index => ParsePoints(style[$"--pdf-semantic-column-width-{index}"]))
            .ToArray();
        float[] gutters = Enumerable.Range(1, 2)
            .Select(index => ParsePoints(style[$"--pdf-semantic-column-gutter-{index}"]))
            .ToArray();
        Assert.All(widths, width => Assert.True(width >= 90f));
        Assert.True(widths.Max() - widths.Min() >= 18f);
        Assert.All(gutters, gutter => Assert.True(gutter >= 18f));
        Assert.True(MathF.Abs(gutters[0] - gutters[1]) >= 4f);

        XElement spanning = Assert.Single(ElementsByClass(dom, "pdf-semantic-column-spanning"));
        Assert.Contains("Three-Column Field Notes", spanning.Value, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(dom.Root!.Value, Regex.Escape("Three-Column Field Notes")));

        XElement[] columns = ElementsByClass(dom, "pdf-semantic-column").ToArray();
        Assert.Equal(3, columns.Length);
        Assert.Contains("Alpha column 01", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Alpha column 14", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Middle column 01", columns[1].Value, StringComparison.Ordinal);
        Assert.Contains("Right column 14", columns[2].Value, StringComparison.Ordinal);
        Assert.True(dom.Root.Value.IndexOf("Alpha column 14", StringComparison.Ordinal) <
            dom.Root.Value.IndexOf("Middle column 01", StringComparison.Ordinal));
        Assert.True(dom.Root.Value.IndexOf("Middle column 14", StringComparison.Ordinal) <
            dom.Root.Value.IndexOf("Right column 01", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_RejectsRuledThreeBandTableAsColumns()
    {
        using PDDocument document = CreateThreeColumnDocument(includeRuledTable: true);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        Assert.Empty(ElementsByClass(dom, "pdf-semantic-columns"));
    }

    [Fact]
    public async Task Convert_SemanticContinuousFlow_RendersResponsiveMeasuredThreeColumnGeometry()
    {
        using PDDocument document = CreateThreeColumnDocument(includeRuledTable: false);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);
        Dictionary<string, string> style = ParseStyle(
            Assert.Single(ElementsByClass(dom, "pdf-semantic-columns")).Attribute("style")?.Value ?? "");
        float expectedWidthRatio = ParsePoints(style["--pdf-semantic-column-width-2"]) /
            ParsePoints(style["--pdf-semantic-column-width-1"]);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1000, Height = 1200 }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        ColumnGridRenderMetrics desktop = await ReadColumnGridRenderMetrics(page);
        await page.SetViewportSizeAsync(420, 1200);
        ColumnGridRenderMetrics narrow = await ReadColumnGridRenderMetrics(page);

        AssertColumnGridGeometry(desktop);
        AssertColumnGridGeometry(narrow);
        AssertWithin(0.03f, expectedWidthRatio, (float)(desktop.Widths[1] / desktop.Widths[0]));
        AssertWithin(0.03f, expectedWidthRatio, (float)(narrow.Widths[1] / narrow.Widths[0]));
        Assert.True(narrow.GridWidth < desktop.GridWidth);
        Assert.True(narrow.GridRight <= 420.5);
    }

    [Fact]
    public void Convert_SemanticTwoColumnRule_RemainsVectorAndIsNotPromoted()
    {
        PdfLayoutDocument layout = CreateSideBySideSemanticRuleLayoutFixture();
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        Assert.DoesNotContain(semanticPage.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);

        PdfHtmlDocument continuous = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument continuousDom = ParseHtml(continuous.Html);
        Assert.Equal(2, ElementsByClass(continuousDom, "pdf-semantic-column").Count());
        Assert.Empty(continuousDom.Descendants("hr"));

        PdfHtmlDocument fixedPages = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.FixedPages
        });
        XDocument fixedDom = ParseHtml(fixedPages.Html);
        Assert.Empty(fixedDom.Descendants("hr"));
        XElement sourceRule = Assert.Single(fixedDom.Descendants(), element =>
            element.Name.LocalName == "path" &&
            element.Attribute("data-path-index")?.Value == "11");
        Assert.Equal("none", sourceRule.Attribute("fill")?.Value);
        Assert.NotNull(sourceRule.Attribute("stroke"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesOrderedListInsideDetectedColumn()
    {
        using PDDocument document = CreateTwoColumnDocumentWithOrderedList();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticList semanticList = Assert.IsType<PdfSemanticList>(Assert.Single(
            semanticPage.Elements,
            static element => element.Kind == PdfSemanticElementKind.List).SemanticList);
        Assert.Equal(PdfSemanticListKind.Ordered, semanticList.Kind);
        Assert.Equal(3, semanticList.Items.Count);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement[] columns = ElementsByClass(dom, "pdf-semantic-column").ToArray();
        Assert.Equal(2, columns.Length);
        XElement list = Assert.Single(columns[0].Descendants("ol"));
        Assert.Equal(
            ["First certification step", "Second certification step", "Third certification step"],
            list.Elements("li").Select(static item => item.Value.Trim()).ToArray());
        Assert.Empty(columns[1].Descendants("ol"));
        Assert.Contains("Right body 12", columns[1].Value, StringComparison.Ordinal);
        Assert.Single(ElementsByClass(dom, "pdf-semantic-column-block"));
        Assert.Equal(24, columns
            .SelectMany(static column => column.Descendants())
            .Count(static element => HasClass(element, "pdf-semantic-column-run")));
        Assert.Single(Regex.Matches(html.Html, "First certification step").Cast<Match>());
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_DoesNotPromoteListWhoseSourceLinesCrossColumns()
    {
        using PDDocument document = CreateTwoColumnDocumentWithOrderedList(includeCrossColumnText: true);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        Assert.Contains(semanticPage.Elements, static element => element.Kind == PdfSemanticElementKind.List);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement[] columns = ElementsByClass(dom, "pdf-semantic-column").ToArray();
        Assert.Equal(2, columns.Length);
        Assert.Empty(columns.SelectMany(static column => column.Descendants("ol")));
        Assert.Contains("1. First certification instruction reaches near gutter", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Right list note 1", columns[1].Value, StringComparison.Ordinal);
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-column-block"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_DetectsColumnsAroundTableConfinedToOneColumn()
    {
        using PDDocument document = CreateTwoColumnDocument(includeRuledTable: true);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        Assert.Contains(semanticPage.Elements, element => element.Kind == PdfSemanticElementKind.Table);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        XElement[] columns = ElementsByClass(dom, "pdf-semantic-column").ToArray();
        Assert.Equal(2, columns.Length);
        XElement table = Assert.Single(columns[0].Descendants("table"));
        Assert.True(HasClass(table, "pdf-semantic-table"));
        Assert.Contains(table.Descendants(), element =>
            HasClass(element, "pdf-semantic-table-cell-border-top") ||
            HasClass(element, "pdf-semantic-table-cell-border-right") ||
            HasClass(element, "pdf-semantic-table-cell-border-bottom") ||
            HasClass(element, "pdf-semantic-table-cell-border-left"));
        Assert.Empty(columns[1].Descendants("table"));
        Assert.Contains("Table A1", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Table B3", columns[0].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Table A1", columns[1].Value, StringComparison.Ordinal);
        Assert.Contains("Right body 12", columns[1].Value, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(dom.Root!.Value, Regex.Escape("Table A1")));
        Assert.Single(Regex.Matches(dom.Root.Value, Regex.Escape("Table B3")));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_AllowsIndependentSideBySideRuledTables()
    {
        using PDDocument document = CreateTwoColumnDocument(RuledTableLayout.SideBySide);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement spanningTable = Assert.Single(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Bounds.X < layout.Pages[0].Width / 2f &&
            element.Bounds.Right > layout.Pages[0].Width / 2f);
        PdfLayoutPath headerRule = Assert.Single(layout.Pages[0].Paths, path =>
            path.IsStroked &&
            path.Bounds.X < layout.Pages[0].Width / 2f &&
            path.Bounds.Right > layout.Pages[0].Width / 2f);
        Assert.True(headerRule.Bounds.Bottom < spanningTable.Bounds.Y);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement[] columns = ElementsByClass(dom, "pdf-semantic-column").ToArray();
        Assert.Equal(2, columns.Length);
        XElement leftTable = Assert.Single(columns[0].Descendants("table"));
        XElement rightTable = Assert.Single(columns[1].Descendants("table"));
        Assert.True(HasClass(leftTable, "pdf-semantic-table"));
        Assert.True(HasClass(rightTable, "pdf-semantic-table"));
        Assert.Contains(leftTable.Descendants(), element =>
            HasClass(element, "pdf-semantic-table-cell-border-left") ||
            HasClass(element, "pdf-semantic-table-cell-border-right"));
        Assert.Contains(rightTable.Descendants(), element =>
            HasClass(element, "pdf-semantic-table-cell-border-left") ||
            HasClass(element, "pdf-semantic-table-cell-border-right"));
        Assert.Contains("Left A1", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Left C3", columns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Right A1", columns[1].Value, StringComparison.Ordinal);
        Assert.Contains("Right C3", columns[1].Value, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(dom.Root!.Value, Regex.Escape("Left A1")));
        Assert.Single(Regex.Matches(dom.Root.Value, Regex.Escape("Right C3")));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesSemanticTableWhenRulesCrossGutter()
    {
        using PDDocument document = CreateTwoColumnDocument(RuledTableLayout.FullWidth);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement spanningTable = Assert.Single(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Bounds.X < layout.Pages[0].Width / 2f &&
            element.Bounds.Right > layout.Pages[0].Width / 2f);
        Assert.True(layout.Pages[0].Paths.Count(path =>
            path.IsStroked &&
            path.Bounds.X < layout.Pages[0].Width / 2f &&
            path.Bounds.Right > layout.Pages[0].Width / 2f &&
            path.Bounds.Bottom >= spanningTable.Bounds.Y - 2f &&
            path.Bounds.Y <= spanningTable.Bounds.Bottom + 2f) >= 2);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        Assert.Empty(ElementsByClass(dom, "pdf-semantic-columns"));
        XElement table = Assert.Single(ElementsByClass(dom, "pdf-semantic-table"));
        Assert.Contains("Full A1", table.Value, StringComparison.Ordinal);
        Assert.Contains("Full C3", table.Value, StringComparison.Ordinal);
        Assert.Contains(table.Descendants(), element =>
            HasClass(element, "pdf-semantic-table-cell-border-top") ||
            HasClass(element, "pdf-semantic-table-cell-border-bottom"));
        Assert.Single(Regex.Matches(dom.Root!.Value, Regex.Escape("Full A1")));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_RotatedImagePageIsNotBlank()
    {
        using PDDocument document = CreateRotatedCroppedImageDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument html = ParseHtml(converted.Html);

        Assert.Single(Assert.Single(layout.Pages).Images);
        Assert.DoesNotContain(layout.Diagnostics, diagnostic => diagnostic.Code == "image-rotation-unsupported");
        Assert.Single(converted.Assets);
        Assert.Single(ElementsByClass(html, "pdf-image"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_HidesCoLocatedOcrTextOverFullPageScan()
    {
        using PDDocument document = CreateScanImageWithTextDocument(paintText: false, imageWidth: 612, imageHeight: 792);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfLayoutPage page = Assert.Single(layout.Pages);
        Assert.Single(page.Images);
        Assert.NotEmpty(page.Glyphs);
        Assert.All(page.Glyphs.Where(glyph => !string.IsNullOrWhiteSpace(glyph.Text)), glyph => Assert.False(glyph.IsPainted));

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement scanPage = Assert.Single(ElementsByClass(dom, "pdf-ocr-scan-page"));
        Assert.Single(scanPage.Descendants(), element => HasClass(element, "pdf-image"));
        XElement[] ocrRuns = scanPage.Descendants()
            .Where(element => HasClass(element, "pdf-ocr-text-run"))
            .ToArray();
        Assert.NotEmpty(ocrRuns);
        Assert.All(ocrRuns, run => Assert.False(string.IsNullOrWhiteSpace(run.Attribute("aria-label")?.Value)));
        Assert.Contains("Searchable OCR text", scanPage.Value, StringComparison.Ordinal);
        Assert.Contains(".pdf-ocr-text-run", converted.Css, StringComparison.Ordinal);
        Assert.Contains("opacity: 0", converted.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_DoesNotHideOcrTextWhenImageIsNotPageDominant()
    {
        using PDDocument document = CreateScanImageWithTextDocument(paintText: false, imageWidth: 500, imageHeight: 650);
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        }), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-ocr-scan-page"));
        Assert.Empty(ElementsByClass(dom, "pdf-ocr-text-run"));
        Assert.Contains("Searchable OCR text", converted.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_DoesNotHideBornDigitalTextAroundImages()
    {
        using PDDocument document = CreateSideBySideImageDocument(includeCaption: true, clipSecondImage: false);
        PdfHtmlDocument converted = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        }), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-ocr-scan-page"));
        Assert.Empty(ElementsByClass(dom, "pdf-ocr-text-run"));
        Assert.Contains("Body line 1 keeps semantic flow active", converted.Html, StringComparison.Ordinal);
        XElement figure = Assert.Single(ElementsByClass(dom, "pdf-semantic-figure"));
        Assert.Equal(2, figure.Descendants().Count(element => element.Name.LocalName == "image"));
    }

    [Fact]
    public void Convert_SemanticContinuousFixedFallback_PreservesPositionedWordBoundaries()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 10 Tf
            72 650 Td
            [(Justified) -250 (prose) -250 (keeps) -250 (boundaries.)] TJ
            ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        Assert.Equal("Justifiedprosekeepsboundaries.", Assert.Single(layout.Pages[0].Runs).Text);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });

        XDocument html = ParseHtml(converted.Html);
        Assert.Single(ElementsByClass(html, "pdf-semantic-layout-fallback-page"));
        Assert.Contains("Justified prose keeps boundaries.", converted.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Justifiedprose", converted.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticArabic_EmitsLogicalTextAndRtlDirectionForFlowAndFixedFallback()
    {
        PdfHtmlOptions options = new()
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        };

        XDocument flow = ParseHtml(PdfHtmlConverter.Convert(CreateArabicVisualOrderLayout(2f), options).Html);
        XElement flowElement = Assert.Single(ElementsByClass(flow, "pdf-semantic-element"), element =>
            element.Attribute("dir")?.Value == "rtl");
        Assert.Contains("منظومة الأمم المتحدة", flowElement.Value, StringComparison.Ordinal);
        Assert.True(HasClass(flowElement, "pdf-text-rtl"));

        XDocument fixedFallback = ParseHtml(PdfHtmlConverter.Convert(CreateArabicVisualOrderLayout(80f), options).Html);
        Assert.Single(ElementsByClass(fixedFallback, "pdf-semantic-layout-fallback-page"));
        XElement fixedRun = Assert.Single(ElementsByClass(fixedFallback, "pdf-text-run"));
        Assert.Equal("rtl", fixedRun.Attribute("dir")?.Value);
        Assert.True(HasClass(fixedRun, "pdf-text-rtl"));
        Assert.Contains("منظومة الأمم المتحدة", fixedRun.Value, StringComparison.Ordinal);
        XElement svgText = Assert.Single(fixedRun.Descendants(), element => element.Name.LocalName == "text");
        Assert.Equal("rtl", svgText.Attribute("direction")?.Value);
        Assert.Equal("start", svgText.Attribute("text-anchor")?.Value);
        Assert.Equal("134", svgText.Attribute("textLength")?.Value);
        Assert.Equal("0 0 134 12", svgText.Parent?.Attribute("viewBox")?.Value);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_UsesFixedLayoutForFullPageVectorBackdrops()
    {
        using PDDocument document = CreateTextDocument("""
            q
            0.95 0.82 0.25 rg
            0 0 612 792 re
            f
            Q
            BT
            /F1 10 Tf
            72 750 Td
            (Backdrop layout line 01) Tj
            0 -20 Td (Backdrop layout line 02) Tj
            0 -20 Td (Backdrop layout line 03) Tj
            0 -20 Td (Backdrop layout line 04) Tj
            0 -20 Td (Backdrop layout line 05) Tj
            0 -20 Td (Backdrop layout line 06) Tj
            0 -20 Td (Backdrop layout line 07) Tj
            0 -20 Td (Backdrop layout line 08) Tj
            0 -20 Td (Backdrop layout line 09) Tj
            0 -20 Td (Backdrop layout line 10) Tj
            0 -20 Td (Backdrop layout line 11) Tj
            0 -20 Td (Backdrop layout line 12) Tj
            0 -20 Td (Backdrop layout line 13) Tj
            0 -20 Td (Backdrop layout line 14) Tj
            0 -20 Td (Backdrop layout line 15) Tj
            0 -20 Td (Backdrop layout line 16) Tj
            0 -20 Td (Backdrop layout line 17) Tj
            0 -20 Td (Backdrop layout line 18) Tj
            0 -20 Td (Backdrop layout line 19) Tj
            0 -20 Td (Backdrop layout line 20) Tj
            ET
            """);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Single(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesAnnotationAndAutomaticTextLinks()
    {
        using PDDocument annotationDocument = CreateLinkedTextDocument(textY: 760);
        PdfHtmlDocument annotationHtml = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(annotationDocument), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument annotationDom = ParseHtml(annotationHtml.Html);

        XElement annotationLink = Assert.Single(ElementsByClass(annotationDom, "pdf-semantic-link"));
        Assert.Equal("https://example.com/pdfbox", annotationLink.Attribute("href")?.Value);
        Assert.Equal("uri", annotationLink.Attribute("data-link-kind")?.Value);
        Assert.Empty(ElementsByClass(annotationDom, "pdf-link-overlay"));

        using PDDocument automaticDocument = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 760 Td
            (Contact hello@example.com or https://example.com/pdfbox.) Tj
            ET
            """);
        PdfHtmlDocument automaticHtml = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(automaticDocument), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument automaticDom = ParseHtml(automaticHtml.Html);

        Assert.Contains(automaticDom.Descendants("a"), link =>
            link.Attribute("href")?.Value == "mailto:hello@example.com");
        Assert.Contains(automaticDom.Descendants("a"), link =>
            link.Attribute("href")?.Value == "https://example.com/pdfbox");
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsOneLinkedBibliographyAcrossPageBreaks()
    {
        PdfHtmlDocument html = PdfHtmlConverter.Convert(CreateBibliographyLayoutFixture(), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement bibliography = Assert.Single(ElementsByClass(dom, "pdf-semantic-bibliography"));
        Assert.Equal("References", bibliography.Attribute("aria-label")?.Value);
        Assert.Equal("bracketed-number", bibliography.Attribute("data-marker-kind")?.Value);
        Assert.Equal("3", bibliography.Attribute("start")?.Value);
        Assert.Contains("content: \"[\" counter(list-item) \"] \"", html.Css, StringComparison.Ordinal);
        XElement[] items = bibliography.Elements("li").ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("cite.Lovelace2020", items[0].Attribute("id")?.Value);
        Assert.Equal("cite.Noether2022", items[1].Attribute("id")?.Value);
        Assert.Equal("5", items[1].Attribute("value")?.Value);
        Assert.All(items, item =>
        {
            Assert.True(HasClass(item, "pdf-font-times-roman"));
            Assert.True(HasClass(item, "pdf-font-size-10"));
            Assert.True(HasClass(item, "pdf-color-000000-ff"));
        });
        Assert.Contains(".pdf-font-times-roman{font-family:'Times-Roman', serif}", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-font-size-10{font-size:10pt}", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-color-000000-ff{color:#000000}", html.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("[3]", items[0].Value, StringComparison.Ordinal);
        Assert.Contains("continued on the next source page", items[0].Value, StringComparison.Ordinal);
        Assert.Single(items[0].Descendants(), element =>
            HasClass(element, "pdf-semantic-page-break") &&
            element.Attribute("data-page-number")?.Value == "3");
        Assert.Contains(items[0].Descendants("a"), link =>
            link.Attribute("href")?.Value == "https://doi.org/10.1000/first");
        XElement citedTitle = Assert.Single(items[0].Descendants("cite"));
        Assert.Equal("Analytical Engines", citedTitle.Value);
        Assert.Contains(citedTitle.DescendantsAndSelf(), element => HasClass(element, "pdf-semantic-italic"));
        XElement person = Assert.Single(items[1].Descendants("em"), element => element.Value == "Noether, E.");
        Assert.True(HasClass(person, "pdf-semantic-italic"));
        Assert.Empty(items[1].Descendants("cite"));

        XElement inTextCitation = Assert.Single(ElementsByClass(dom, "pdf-semantic-link"), link =>
            link.Value.Contains("[3]", StringComparison.Ordinal));
        Assert.Equal("#cite.Lovelace2020", inTextCitation.Attribute("href")?.Value);
        Assert.NotNull(dom.Descendants().SingleOrDefault(element =>
            element.Attribute("id")?.Value == inTextCitation.Attribute("href")?.Value.TrimStart('#')));
        Assert.Single(dom.Descendants("ol"), element => HasClass(element, "pdf-semantic-bibliography"));
    }

    [Fact]
    public void Convert_SemanticTextMode_EmitsOnlyEvidenceBackedInlineSemantics()
    {
        PdfHtmlDocument html = PdfHtmlConverter.Convert(CreateInlineSemanticLayoutFixture(), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement published = Assert.Single(dom.Descendants("time"), element => element.Value == "March 14, 2024");
        Assert.Equal("2024-03-14", published.Attribute("datetime")?.Value);
        XElement timeline = Assert.Single(dom.Descendants("time"), element => element.Value == "2024-04-05");
        Assert.Equal("2024-04-05", timeline.Attribute("datetime")?.Value);
        Assert.DoesNotContain(dom.Descendants("time"), element => element.Value.Contains("03/04/2024", StringComparison.Ordinal));

        XElement abbreviation = Assert.Single(dom.Descendants("abbr"));
        Assert.Equal("WHO", abbreviation.Value);
        Assert.Equal("World Health Organization", abbreviation.Attribute("title")?.Value);
        Assert.DoesNotContain(dom.Descendants("abbr"), element => element.Value == "NASA");

        string[] smallText = dom.Descendants("small").Select(static element => element.Value).ToArray();
        Assert.Contains(smallText, static text => text.StartsWith("Published:", StringComparison.Ordinal));
        Assert.Contains(smallText, static text => text.StartsWith("Updated:", StringComparison.Ordinal));
        Assert.Contains(smallText, static text => text.StartsWith("Copyright", StringComparison.Ordinal));
        Assert.DoesNotContain(smallText, static text => text.Contains("Ordinary smaller", StringComparison.Ordinal));

        XElement captionTitle = Assert.Single(dom.Descendants("cite"), element =>
            element.Value == "The Design of Everyday Things");
        Assert.Contains(captionTitle.DescendantsAndSelf(), element => HasClass(element, "pdf-semantic-italic"));
        XElement emphasis = Assert.Single(dom.Descendants("em"), element => element.Value == "important emphasis");
        Assert.True(HasClass(emphasis, "pdf-semantic-italic"));
        Assert.DoesNotContain(dom.Descendants("cite"), element => element.Value == "important emphasis");

        string visibleText = dom.Root?.Value ?? string.Empty;
        Assert.Contains("World Health Organization (WHO) issued guidance.", visibleText, StringComparison.Ordinal);
        Assert.Contains("Updated: 03/04/2024", visibleText, StringComparison.Ordinal);
        Assert.Contains("Copyright 2026 Example. All rights reserved.", visibleText, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-small", html.Css, StringComparison.Ordinal);
        Assert.Contains("font-size: inherit", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_FormDateLabels_EmitInvariantTimeWithoutGuessingNumericDates()
    {
        PdfLayoutFormControl[] controls =
        [
            new PdfLayoutFormControl(
                0,
                "published",
                "Published",
                PdfLayoutFormControlKind.Text,
                new PdfLayoutRectangle(180f, 120f, 180f, 20f),
                sourceLabelText: "Published: 14 March 2024"),
            new PdfLayoutFormControl(
                1,
                "updated",
                "Updated",
                PdfLayoutFormControlKind.Text,
                new PdfLayoutRectangle(180f, 160f, 180f, 20f),
                sourceLabelText: "Updated: 03/04/2024")
        ];
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            pageNumber: 1,
            mediaBox: pageBounds,
            cropBox: pageBounds,
            width: pageBounds.Width,
            height: pageBounds.Height,
            rotation: 0,
            glyphs: [],
            runs: [],
            lines: [],
            blocks: [],
            images: [],
            paths: [],
            shadings: [],
            vectorGroups: [],
            links: [],
            diagnostics: [],
            formControls: controls);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        }).Html);

        XElement time = Assert.Single(dom.Descendants("time"));
        Assert.Equal("14 March 2024", time.Value);
        Assert.Equal("2024-03-14", time.Attribute("datetime")?.Value);
        XElement ambiguous = Assert.Single(dom.Descendants("label"), element =>
            element.Value == "Updated: 03/04/2024");
        Assert.Empty(ambiguous.Descendants("time"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_ArxivReferencesRemainOneLinkedBibliography()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement bibliography = Assert.Single(ElementsByClass(dom, "pdf-semantic-bibliography"));
        XElement[] items = bibliography.Elements("li").ToArray();
        Assert.Equal(40, items.Length);
        Assert.DoesNotContain(items, item => item.Value.TrimStart().StartsWith("[", StringComparison.Ordinal));
        Assert.All(items, item =>
        {
            string[] classes = item.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            Assert.Single(classes, className =>
                className.StartsWith("pdf-font-nimbusromno9l-", StringComparison.Ordinal));
            Assert.Single(classes, className =>
                className.StartsWith("pdf-font-size-", StringComparison.Ordinal));
            Assert.True(HasClass(item, "pdf-color-000000-ff"));
        });
        Assert.Contains(items, item => HasClass(item, "pdf-font-size-9"));
        Assert.Contains(items, item => HasClass(item, "pdf-font-size-10"));

        XElement[] pageBreaks = bibliography.Descendants()
            .Where(element => HasClass(element, "pdf-semantic-page-break"))
            .ToArray();
        Assert.Equal(["11", "12"], pageBreaks.Select(element => element.Attribute("data-page-number")?.Value));
        Assert.All(pageBreaks, pageBreak => Assert.Single(pageBreak.Ancestors("li")));
        XElement pageElevenItem = Assert.Single(pageBreaks[0].Ancestors("li"));
        Assert.True(HasClass(pageElevenItem, "pdf-font-nimbusromno9l-regu"));
        Assert.True(HasClass(pageElevenItem, "pdf-font-size-9"));
        Assert.True(HasClass(pageElevenItem, "pdf-color-000000-ff"));

        HashSet<string> ids = dom.Descendants()
            .Select(element => element.Attribute("id")?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        string[] citationTargets = dom.Descendants("a")
            .Select(link => link.Attribute("href")?.Value)
            .OfType<string>()
            .Where(href => href.StartsWith("#cite.", StringComparison.Ordinal))
            .Select(static href => Uri.UnescapeDataString(href[1..]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(citationTargets);
        Assert.All(citationTargets, target => Assert.Contains(target, ids));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
    }

    [Fact]
    public async Task Convert_SemanticContinuousFlow_ArxivReferencesPreserveSourceTypographyInBrowser()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludePaths = false
        });
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        string[] typography = await page.EvaluateAsync<string[]>(
            """
            () => {
              const firstItem = document.querySelector('.pdf-semantic-bibliography > li');
              const crossPageItem = document.querySelector('#page-11').closest('li');
              const firstStyle = getComputedStyle(firstItem);
              const firstMarkerStyle = getComputedStyle(firstItem, '::marker');
              const crossPageStyle = getComputedStyle(crossPageItem);
              const crossPageMarkerStyle = getComputedStyle(crossPageItem, '::marker');
              return [
                firstStyle.fontFamily,
                firstStyle.fontSize,
                firstStyle.color,
                firstMarkerStyle.fontFamily,
                firstMarkerStyle.fontSize,
                firstMarkerStyle.color,
                crossPageStyle.fontFamily,
                crossPageStyle.fontSize,
                crossPageStyle.color,
                crossPageMarkerStyle.fontFamily,
                crossPageMarkerStyle.fontSize,
                crossPageMarkerStyle.color
              ];
            }
            """);

        Assert.Contains("NimbusRomNo9L-Regu", typography[0], StringComparison.Ordinal);
        Assert.Equal("12px", typography[1]);
        Assert.Equal("rgb(0, 0, 0)", typography[2]);
        Assert.Contains("NimbusRomNo9L-Regu", typography[3], StringComparison.Ordinal);
        Assert.Equal("12px", typography[4]);
        Assert.Equal("rgb(0, 0, 0)", typography[5]);
        Assert.Contains("NimbusRomNo9L-Regu", typography[6], StringComparison.Ordinal);
        Assert.Equal("12px", typography[7]);
        Assert.Equal("rgb(0, 0, 0)", typography[8]);
        Assert.Contains("NimbusRomNo9L-Regu", typography[9], StringComparison.Ordinal);
        Assert.Equal("12px", typography[10]);
        Assert.Equal("rgb(0, 0, 0)", typography[11]);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsBulletLinesAsListItems()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (\225 First member) Tj
            0 -18 Td (\225 Second member) Tj
            0 -18 Td (\225 Third member) Tj
            0 -18 Td (\225 Fourth member) Tj
            0 -18 Td (\225 Fifth member) Tj
            0 -18 Td (\225 Sixth member) Tj
            0 -18 Td (\225 Seventh member) Tj
            0 -18 Td (\225 Eighth member) Tj
            0 -18 Td (\225 Ninth member) Tj
            ET
            """);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement list = Assert.Single(dom.Descendants("ul"));
        Assert.Equal(new[]
            {
                "First member", "Second member", "Third member", "Fourth member", "Fifth member",
                "Sixth member", "Seventh member", "Eighth member", "Ninth member"
            },
            list.Elements("li").Select(item => item.Value.Trim()).ToArray());
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsLabelledNestedDocumentNavigationWithCssLeaders()
    {
        PdfLayoutDocument layout = CreateDocumentIndexLayoutFixture(includeImages: false);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement navigation = Assert.Single(dom.Descendants("nav"), element =>
            HasClass(element, "pdf-semantic-document-index"));
        XElement heading = Assert.Single(navigation.Elements(), element =>
            element.Name.LocalName.StartsWith("h", StringComparison.Ordinal));
        Assert.Equal("Table of Contents", heading.Value);
        Assert.Equal(heading.Attribute("id")?.Value, navigation.Attribute("aria-labelledby")?.Value);

        XElement rootList = Assert.Single(navigation.Elements("ol"));
        XElement[] rootItems = rootList.Elements("li").ToArray();
        Assert.Equal(2, rootItems.Length);
        Assert.Single(rootItems[0].Elements("ol"));
        Assert.Single(rootItems[1].Elements("ol"));
        XElement[] pageNumbers = ElementsByClass(dom, "pdf-semantic-document-index-page-number").ToArray();
        Assert.Equal(["1", "2", "A-1", "A-2"], pageNumbers.Select(static element => element.Value));
        Assert.All(ElementsByClass(dom, "pdf-semantic-document-index-leader"), leader =>
        {
            Assert.Empty(leader.Value);
            Assert.Equal("true", leader.Attribute("aria-hidden")?.Value);
        });
        Assert.DoesNotContain("...", navigation.Value, StringComparison.Ordinal);

        XElement firstEntry = Assert.Single(ElementsByClass(dom, "pdf-semantic-document-index-entry"), entry =>
            entry.Value.Contains("1. Introduction", StringComparison.Ordinal));
        Assert.Equal("#page-2", firstEntry.Attribute("href")?.Value);
        Assert.Contains(dom.Descendants(), element => element.Attribute("id")?.Value == "page-2");
        Assert.All(ElementsByClass(dom, "pdf-semantic-document-index-entry"), entry =>
            Assert.Null(entry.Attribute("style")));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));

        Assert.Contains(
            "grid-template-columns: auto minmax(1em, 1fr) auto;",
            html.Css,
            StringComparison.Ordinal);
        Assert.Contains("border-bottom: 0.08em dotted currentColor;", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesImageBackedIndexRowsWithSemanticNavigationIsland()
    {
        PdfLayoutDocument layout = CreateDocumentIndexLayoutFixture(includeImages: true);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement fallbackPage = Assert.Single(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        Assert.Equal("1", fallbackPage.Attribute("data-page-number")?.Value);
        XElement navigation = Assert.Single(dom.Descendants("nav"), element =>
            HasClass(element, "pdf-semantic-document-index"));
        Assert.True(HasClass(navigation, "pdf-semantic-document-index-island"));
        Assert.Equal(
            layout.Pages[0].Images.Select(static image => image.AssetId),
            fallbackPage.Descendants("img").Select(image => image.Attribute("data-asset-id")?.Value));
        Assert.Equal(42, fallbackPage.Descendants("img").Count());
        Assert.Equal(
            25,
            fallbackPage.Descendants("a").Count(element => HasClass(element, "pdf-link-overlay")));
        Assert.Contains("clip-path: inset(50%);", html.Css, StringComparison.Ordinal);
        Assert.Contains("position: absolute;", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_UnrelatedIndexPageGraphicDoesNotTriggerFixedFallback()
    {
        PdfLayoutDocument layout = CreateDocumentIndexLayoutFixture(
            includeImages: false,
            includeUnrelatedImage: true);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Single(layout.Pages[0].Images);
        XElement navigation = Assert.Single(dom.Descendants("nav"), element =>
            HasClass(element, "pdf-semantic-document-index"));
        Assert.False(HasClass(navigation, "pdf-semantic-document-index-island"));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsWrappedBulletItemsAndPreservesInlineFormatting()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            1 0 0 1 72 748 Tm
            (Context line one keeps semantic flow active.) Tj
            1 0 0 1 72 736 Tm
            (Context line two establishes ordinary prose.) Tj
            1 0 0 1 72 724 Tm
            (Context line three completes the opening paragraph.) Tj
            1 0 0 1 72 700 Tm
            (The following perspectives apply:) Tj
            1 0 0 1 72 684 Tm
            (\225 ) Tj /F2 12 Tf (Federal perspective) Tj /F1 12 Tf (: first wrapped item) Tj
            1 0 0 1 90 672 Tm
            (continues on an indented visual line.) Tj
            1 0 0 1 72 656 Tm
            (\225 ) Tj /F2 12 Tf (Nonfederal perspective) Tj /F1 12 Tf (: second wrapped item) Tj
            1 0 0 1 90 644 Tm
            (also preserves its continuation text.) Tj
            1 0 0 1 72 620 Tm
            (Ordinary prose resumes after the list.) Tj
            ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement semanticListElement = Assert.Single(semanticPage.Elements, static element =>
            element.Kind == PdfSemanticElementKind.List);
        PdfSemanticList semanticList = Assert.IsType<PdfSemanticList>(semanticListElement.SemanticList);
        Assert.Equal(2, semanticList.Items.Count);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement list = Assert.Single(dom.Descendants("ul"));
        XElement[] items = list.Elements("li").ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains("first wrapped item continues on an indented visual line", items[0].Value, StringComparison.Ordinal);
        Assert.Contains("second wrapped item also preserves its continuation text", items[1].Value, StringComparison.Ordinal);
        Assert.Contains(items[0].Descendants(), element =>
            HasClass(element, "pdf-semantic-italic") &&
            element.Value == "Federal perspective");
        Assert.Contains(dom.Descendants("p"), paragraph =>
            paragraph.Value == "Ordinary prose resumes after the list.");
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsNativeDefinitionListWithSourceColumnWidth()
    {
        PdfLayoutDocument layout = CreateDefinitionListLayoutFixture(columns: true);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement list = Assert.Single(dom.Descendants("dl"));
        Assert.Equal(new[] { "API", "CUI", "MFA", "SIEM" },
            list.Elements("dt").Select(static term => term.Value.Trim()).ToArray());
        Assert.Equal(4, list.Elements("dd").Count());
        Assert.Contains("Controlled Unclassified Information", list.Elements("dd").ElementAt(1).Value, StringComparison.Ordinal);
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-columns"));
        Assert.Empty(dom.Descendants("table"));

        Dictionary<string, string> style = ParseStyle(list.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePoints(style["--pdf-semantic-term-width"]), 55f, 65f);
        Assert.InRange(ParsePoints(style["--pdf-semantic-definition-gap"]), 70f, 90f);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsNativeBlockQuoteWithSourceAttribution()
    {
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 50f, 440f),
            CreateScientificFixtureLine("“The Ghent PDF Output Suite 5.0 was created to check whether a PDF output", 72f, 120f, 450f),
            CreateScientificFixtureLine("workflow conforms to PDF/X-4 and can process production files reliably", 72f, 133f, 430f),
            CreateScientificFixtureLine("without unexpected problems worldwide”, stated Ada Lovelace, workflow chair.", 72f, 146f, 455f),
            CreateScientificFixtureLine("Ordinary body prose resumes after the attributed quotation.", 72f, 194f, 390f)
        ]);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement blockquote = Assert.Single(dom.Descendants("blockquote"));
        Assert.True(HasClass(blockquote, "pdf-semantic-blockquote"));
        XElement quoteText = Assert.Single(blockquote.Elements("p"));
        Assert.StartsWith("“The Ghent PDF Output Suite", quoteText.Value);
        Assert.EndsWith("worldwide”,", quoteText.Value, StringComparison.Ordinal);
        Assert.Equal(
            "stated Ada Lovelace, workflow chair.",
            Assert.Single(blockquote.Elements("footer")).Value);
        Assert.DoesNotMatch(
            @"\.pdf-semantic-(?:blockquote|aside)\s*\{[^}]*position\s*:\s*absolute",
            converted.Css);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_FixedFallbackRetainsSourceSizedBlockQuoteIsland()
    {
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 72f, 440f),
            CreateScientificFixtureLine("“The Ghent PDF Output Suite 5.0 was created to check whether a PDF output", 72f, 120f, 450f),
            CreateScientificFixtureLine("workflow conforms to PDF/X-4 and can process production files reliably", 72f, 133f, 430f),
            CreateScientificFixtureLine("without unexpected problems worldwide”, stated Ada Lovelace, workflow chair.", 72f, 146f, 455f),
            CreateScientificFixtureLine("Ordinary body prose resumes after the attributed quotation.", 72f, 194f, 390f)
        ]);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement fallbackPage = Assert.Single(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        Assert.True(HasClass(fallbackPage, "pdf-semantic-layout-fallback-page-with-islands"));
        XElement blockquote = Assert.Single(fallbackPage.Elements("blockquote"));
        Assert.True(HasClass(blockquote, "pdf-semantic-fallback-island"));
        Assert.True(
            HasClass(blockquote, "pdf-font-times-roman"),
            blockquote.Attribute("class")?.Value);
        Assert.True(HasClass(blockquote, "pdf-font-size-10"));
        Assert.EndsWith(
            "worldwide”,",
            Assert.Single(blockquote.Elements("p")).Value,
            StringComparison.Ordinal);
        Assert.Equal("stated Ada Lovelace, workflow chair.", Assert.Single(blockquote.Elements("footer")).Value);

        Dictionary<string, string> style = ParseStyle(blockquote.Attribute("style")?.Value ?? "");
        Assert.Equal(72f, ParsePoints(style["--pdf-semantic-island-left"]), 2);
        Assert.Equal(120f, ParsePoints(style["--pdf-semantic-island-top"]), 2);
        Assert.Equal(455f, ParsePoints(style["--pdf-semantic-island-width"]), 2);
        Assert.Equal(13f, ParsePoints(style["--pdf-semantic-island-line-height"]), 2);
        Assert.DoesNotContain("position", blockquote.Attribute("style")?.Value ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fallbackPage.Elements("span"), element =>
            HasClass(element, "pdf-text-run") &&
            element.Value.Contains("Ghent PDF Output", StringComparison.Ordinal));
        Assert.Contains(fallbackPage.Elements("span"), element =>
            HasClass(element, "pdf-text-run") &&
            (element.Attribute("style")?.Value.Contains("position:absolute", StringComparison.Ordinal) ?? false));
        Assert.DoesNotMatch(
            @"\.pdf-semantic-blockquote\.pdf-semantic-fallback-island\s*\{[^}]*position\s*:\s*absolute",
            converted.Css);
        Assert.Matches(
            @"\.pdf-semantic-blockquote\.pdf-semantic-fallback-island\s*\{[^}]*margin\s*:\s*var\(--pdf-semantic-island-top\)",
            converted.Css);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_BlockQuoteWithoutAttributionHasNoFooter()
    {
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Ordinary body prose establishes the full measure used by nearby paragraphs.", 72f, 50f, 460f),
            CreateScientificFixtureLine("A second body line confirms the normal left and right margins.", 72f, 85f, 430f),
            CreateScientificFixtureLine("This deliberately inset passage carries a sustained observation about reliable", 108f, 132f, 380f),
            CreateScientificFixtureLine("document exchange across several visual lines and remains independent from the", 108f, 145f, 380f),
            CreateScientificFixtureLine("surrounding narrative even without explicit quotation punctuation in the source.", 108f, 158f, 365f),
            CreateScientificFixtureLine("Ordinary body prose resumes at the established page margin after the passage.", 72f, 206f, 450f)
        ]);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement blockquote = Assert.Single(dom.Descendants("blockquote"));
        Assert.Empty(blockquote.Elements("footer"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_OrdinaryQuotedTextRemainsParagraphs()
    {
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 50f, 440f),
            CreateScientificFixtureLine("The report calls this “a useful phrase” while continuing an ordinary", 72f, 120f, 430f),
            CreateScientificFixtureLine("paragraph whose quoted words are not a separate passage.", 72f, 133f, 360f),
            CreateScientificFixtureLine("“Quoted words” can also begin a normal paragraph that continues with", 72f, 180f, 430f),
            CreateScientificFixtureLine("the author’s own analysis over another visual line.", 72f, 193f, 330f)
        ]);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        Assert.Empty(dom.Descendants("blockquote"));
        Assert.Contains(dom.Descendants("p"), static paragraph =>
            paragraph.Value.StartsWith("The report calls this", StringComparison.Ordinal));
        Assert.Contains(dom.Descendants("p"), static paragraph =>
            paragraph.Value.StartsWith("“Quoted words”", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsLabelledFlowAsideForShadedCallout()
    {
        PdfLayoutPath shading = CreateSemanticCalloutPath(new PdfLayoutRectangle(96f, 116f, 400f, 88f));
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 50f, 440f),
            CreateScientificFixtureLine("NOTE", 108f, 128f, 42f, 11f, "Times-Bold"),
            CreateScientificFixtureLine("This independent callout records a tangential implementation detail", 108f, 151f, 360f),
            CreateScientificFixtureLine("without interrupting the natural reading order of the main discussion.", 108f, 164f, 350f),
            CreateScientificFixtureLine("Ordinary body prose resumes outside the shaded callout.", 72f, 230f, 360f)
        ],
        [shading]);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement aside = Assert.Single(dom.Descendants("aside"));
        Assert.Equal("NOTE", aside.Attribute("aria-label")?.Value);
        Assert.Equal("NOTE", Assert.Single(aside.Elements("header")).Value);
        Assert.Contains("tangential implementation detail", Assert.Single(aside.Elements("p")).Value, StringComparison.Ordinal);
        Assert.DoesNotContain("position:absolute", aside.Attribute("style")?.Value ?? "", StringComparison.Ordinal);
        Assert.True(HasClass(aside, "pdf-semantic-aside-source-decorated"));
        Dictionary<string, string> style = ParseStyle(aside.Attribute("style")?.Value ?? "");
        Assert.Equal(-12f, ParsePoints(style["--pdf-semantic-aside-inset-left"]), 2);
        Assert.Equal(400f, ParsePoints(style["--pdf-semantic-aside-source-width"]), 2);
        Assert.Equal("rgba(235,240,245,1)", style["--pdf-semantic-aside-source-background"]);
        Assert.Equal("rgba(51,102,153,1)", style["--pdf-semantic-aside-source-border-color"]);
        Assert.Equal(1.5f, ParsePoints(style["--pdf-semantic-aside-source-border-width"]), 2);
        Assert.Equal(12f, ParsePoints(style["--pdf-semantic-aside-source-padding-top"]), 2);
        Assert.Equal(28f, ParsePoints(style["--pdf-semantic-aside-source-padding-right"]), 2);
        Assert.Equal(32.5f, ParsePoints(style["--pdf-semantic-aside-source-padding-bottom"]), 2);
        Assert.Equal(12f, ParsePoints(style["--pdf-semantic-aside-source-padding-left"]), 2);
        Assert.Matches(
            @"\.pdf-semantic-aside-source-decorated\s*\{[^}]*box-sizing\s*:\s*border-box",
            converted.Css);
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        Assert.Empty(ElementsByClass(dom, "pdf-vector-layer"));
        Assert.DoesNotMatch(
            @"\.pdf-semantic-aside\s*\{[^}]*position\s*:\s*absolute",
            converted.Css);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_KnownInsetAsidePreservesNeutralSourceGeometry()
    {
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 50f, 440f),
            CreateScientificFixtureLine("DISCUSSION", 126f, 108f, 72f, 10f, "Times-Bold"),
            CreateScientificFixtureLine("This labelled discussion remains tangential to the primary requirement and", 126f, 130f, 416f),
            CreateScientificFixtureLine("retains the narrower source measure without invented visual decoration.", 126f, 143f, 390f),
            CreateScientificFixtureLine("Ordinary body prose resumes at the established page margin.", 72f, 194f, 390f)
        ]);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement aside = Assert.Single(dom.Descendants("aside"));
        Assert.True(HasClass(aside, "pdf-semantic-aside-source-geometry"));
        Assert.False(HasClass(aside, "pdf-semantic-aside-source-decorated"));
        Dictionary<string, string> style = ParseStyle(aside.Attribute("style")?.Value ?? "");
        Assert.Equal(18f, ParsePoints(style["--pdf-semantic-aside-inset-left"]), 2);
        Assert.Equal(416f, ParsePoints(style["--pdf-semantic-aside-source-width"]), 2);
        Assert.DoesNotContain("source-background", aside.Attribute("style")?.Value ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("source-border", aside.Attribute("style")?.Value ?? "", StringComparison.Ordinal);
        Match asideRule = Assert.IsType<Match>(Regex.Match(
            converted.Css,
            @"\.pdf-semantic-aside\s*\{(?<body>[^}]*)\}"));
        Assert.True(asideRule.Success);
        string asideRuleBody = asideRule.Groups["body"].Value;
        Assert.DoesNotContain("border", asideRuleBody, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"padding\s*:\s*0\s*;", asideRuleBody);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsInlinePairsAsNativeDefinitionTerms()
    {
        PdfLayoutDocument layout = CreateDefinitionListLayoutFixture(columns: false);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement list = Assert.Single(dom.Descendants("dl"));
        Assert.Equal(4, list.Elements("dt").Count());
        Assert.Equal(4, list.Elements("dd").Count());
        Assert.Contains("Application programming interface", list.Elements("dd").First().Value, StringComparison.Ordinal);
        Assert.DoesNotContain(dom.Descendants("h2"), static heading => heading.Value == "API");
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsMultipleTermsOnlyForSharedSourceDefinition()
    {
        PdfLayoutDocument layout = CreateDefinitionListAliasLayoutFixture();

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement list = Assert.Single(dom.Descendants("dl"));
        Assert.Equal(5, list.Elements("dt").Count());
        Assert.Equal(4, list.Elements("dd").Count());
        Assert.Equal(new[] { "API", "application interface" }, list.Elements("dt").Take(2)
            .Select(static term => term.Value.Trim())
            .ToArray());
        XElement sharedDefinition = list.Elements("dd").First();
        Assert.Contains("Application programming interfaces provide access", sharedDefinition.Value, StringComparison.Ordinal);
        Assert.Equal("1 / span 2", ParseStyle(sharedDefinition.Attribute("style")?.Value ?? "")["grid-row"]);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_KeepsPageMarkerInsideContinuingDefinition()
    {
        PdfLayoutDocument layout = CreateCrossPageDefinitionListLayoutFixture();

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement list = Assert.Single(dom.Descendants("dl"));
        XElement term = Assert.Single(list.Elements("dt"), static term => term.Value == "common secure configuration");
        XElement definition = term.ElementsAfterSelf("dd").First();
        XElement pageBreak = Assert.Single(definition.Descendants(), static element =>
            HasClass(element, "pdf-semantic-page-break"));
        Assert.Equal("page-2", pageBreak.Attribute("id")?.Value);
        Assert.Contains("operational requirements and implementation guidance", definition.Value, StringComparison.Ordinal);
        Assert.True(
            definition.Value.IndexOf("Recognized benchmarks", StringComparison.Ordinal) <
            definition.Value.IndexOf("operational requirements", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_KeepsDefinitionListOpenBetweenCompletedPageEntries()
    {
        PdfLayoutDocument layout = CreateCrossPageDefinitionListLayoutFixture(definitionContinues: false);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        }).Html);

        XElement list = Assert.Single(dom.Descendants("dl"));
        Assert.Equal(8, list.Elements("dt").Count());
        XElement completedDefinition = list.Elements("dd").ElementAt(3);
        Assert.Contains("Recognized benchmarks for systems.", completedDefinition.Value, StringComparison.Ordinal);
        Assert.Single(completedDefinition.Descendants(), static element =>
            HasClass(element, "pdf-semantic-page-break"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_ArxivApplicationBulletsRemainOneCompleteList()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = false
        });
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement list = Assert.Single(dom.Descendants("ul"), list =>
            list.Elements("li").Any(item =>
                item.Value.Contains("encoder-decoder attention", StringComparison.Ordinal)));
        XElement[] items = list.Elements("li").ToArray();
        Assert.Equal(3, items.Length);
        Assert.Contains("[38, 2, 9]", items[0].Value, StringComparison.Ordinal);
        Assert.Contains("previous layer of the encoder", items[1].Value, StringComparison.Ordinal);
        Assert.Contains("illegal connections. See Figure 2.", items[2].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsOrderedAndNestedListsWithSourceNumbering()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            1 0 0 1 72 748 Tm (Opening body line establishes normal prose.) Tj
            1 0 0 1 72 736 Tm (A second line establishes vertical rhythm.) Tj
            1 0 0 1 72 700 Tm (3. First parent) Tj
            1 0 0 1 96 684 Tm (a. First child) Tj
            1 0 0 1 96 668 Tm (b. Second child) Tj
            1 0 0 1 72 652 Tm (4. Second parent) Tj
            1 0 0 1 72 636 Tm (6. Fourth source value) Tj
            ET
            """);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document), new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement root = dom.Descendants("ol").First(list => list.Parent?.Name.LocalName != "li");
        Assert.Equal("3", root.Attribute("start")?.Value);
        XElement[] rootItems = root.Elements("li").ToArray();
        Assert.Equal(3, rootItems.Length);
        Assert.Null(rootItems[0].Attribute("value"));
        Assert.Null(rootItems[1].Attribute("value"));
        Assert.Equal("6", rootItems[2].Attribute("value")?.Value);

        XElement nested = Assert.Single(rootItems[0].Elements("ol"));
        Assert.Equal("a", nested.Attribute("type")?.Value);
        Assert.Equal(["First child", "Second child"], nested.Elements("li").Select(static item => item.Value.Trim()));
    }

    [Fact]
    public void Convert_SemanticTextMode_ListItemsPreserveRichInlineContent()
    {
        PdfLayoutDocument layout = CreateRichListLayoutFixture();
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        Assert.True(
            semanticPage.Elements.Count(static element => element.Kind == PdfSemanticElementKind.List) == 1,
            string.Join(Environment.NewLine, semanticPage.Elements.Select(element =>
                $"{element.Kind} ({element.Lines.Count} lines): {element.Text}")));
        Assert.Contains(semanticPage.Elements, static element => element.Kind == PdfSemanticElementKind.Footnote);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        XDocument dom = ParseHtml(html.Html);

        XElement firstItem = Assert.Single(dom.Descendants("ul")).Elements("li").First();
        Assert.Contains(firstItem.Descendants(), element =>
            HasClass(element, "pdf-semantic-bold") && element.Value == "Bold");
        Assert.Contains(firstItem.Descendants(), element =>
            HasClass(element, "pdf-semantic-italic") && element.Value == "italic");
        Assert.Contains(firstItem.Descendants("em"), element => element.Value == "italic");
        Assert.Empty(firstItem.Descendants("cite"));
        Assert.Empty(firstItem.Descendants("small"));
        Assert.Contains(firstItem.Descendants("sub"), element => element.Value == "2");
        Assert.Contains(firstItem.Descendants("sup"), element => element.Value == "3");
        Assert.Contains(firstItem.Descendants("a"), element =>
            element.Attribute("href")?.Value == "https://example.com/list");
        Assert.True(
            firstItem.Descendants("a").Any(element =>
                HasClass(element, "pdf-semantic-footnote-ref") && element.Value == "*"),
            firstItem.ToString(SaveOptions.DisableFormatting));
        Assert.All(
            ElementsByClass(dom, "pdf-semantic-footnote"),
            static footnote => Assert.Empty(footnote.Descendants("small")));
    }

    [Fact]
    public async Task Convert_SemanticContinuousFlow_RendersDetectedGridWithSourceGeometry()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("4PP-Highlighting.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1000,
                Height = 1400
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        GridRenderMetrics metrics = await page.EvaluateAsync<GridRenderMetrics>(
            """
            () => {
              const grid = document.querySelector(".pdf-semantic-line-grid");
              const rows = Array.from(grid.querySelectorAll(".pdf-semantic-line-grid-row"));
              const firstRow = rows[0].getBoundingClientRect();
              const secondRow = rows[1].getBoundingClientRect();
              const firstCells = Array.from(rows[0].querySelectorAll(".pdf-semantic-line-grid-cell"));
              const firstCell = firstCells[0].getBoundingClientRect();
              const secondCell = firstCells[1].getBoundingClientRect();
              const gridBox = grid.getBoundingClientRect();
              const firstHighlight = grid.querySelector("mark.pdf-semantic-mark").getBoundingClientRect();
              return {
                rowCount: rows.length,
                highlightCount: grid.querySelectorAll("mark.pdf-semantic-mark").length,
                firstCellLeft: firstCell.left - gridBox.left,
                secondCellLeft: secondCell.left - gridBox.left,
                firstCellTop: firstCell.top - gridBox.top,
                firstRowStep: secondRow.top - firstRow.top,
                firstHighlightWidth: firstHighlight.width
              };
            }
            """);

        const float cssPixelsPerPoint = 96f / 72f;
        Assert.Equal(84, metrics.RowCount);
        Assert.Equal(16, metrics.HighlightCount);
        AssertWithin(1f, 36f * cssPixelsPerPoint, (float)metrics.FirstCellLeft);
        AssertWithin(1f, 306f * cssPixelsPerPoint, (float)metrics.SecondCellLeft);
        AssertWithin(1f, 44.579f * cssPixelsPerPoint, (float)metrics.FirstCellTop);
        AssertWithin(1f, (52.679f - 44.579f) * cssPixelsPerPoint, (float)metrics.FirstRowStep);
        AssertWithin(1f, layout.Pages[0].TextHighlights[0].Bounds.Width * cssPixelsPerPoint, (float)metrics.FirstHighlightWidth);
    }

    [Fact]
    public void Convert_SemanticTextUsesMarkForOnlyTheCoveredPartOfALine()
    {
        using PDDocument document = CreateTextDocument("""
            1 0.5 0 rg
            102 698.5 33.4 8 re
            f
            0 0 0 rg
            BT /F1 10 Tf 72 700 Td (left 01 marked tail) Tj ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        XDocument dom = ParseHtml(converted.Html);

        XElement mark = Assert.Single(dom.Descendants("mark"));
        Assert.Equal("marked", mark.Value);
        Assert.Contains("left 01 ", mark.Parent?.Value, StringComparison.Ordinal);
        Assert.Contains(" tail", mark.Parent?.Value, StringComparison.Ordinal);
        Dictionary<string, string> style = ParseStyle(mark.Attribute("style")?.Value ?? "");
        Assert.Equal("rgba(255,128,0,1)", style["--pdf-semantic-mark-background"]);
        AssertWithin(0.01f, 33.4f, ParsePoints(style["--pdf-semantic-mark-width"]));
    }

    [Fact]
    public void Convert_SemanticTableFillRemainsPresentational()
    {
        using PDDocument document = CreateFilledTableDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        Assert.Single(layout.Pages[0].TextHighlights);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(converted.Html);

        Assert.NotEmpty(dom.Descendants("table"));
        Assert.Empty(dom.Descendants("mark"));
    }


    [Fact]
    public void Convert_EmitsPageContainersMatchingLayoutDimensions()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Hello HTML) Tj
            ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);

        XElement page = Assert.Single(ElementsByClass(dom, "pdf-page"));
        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        Assert.Equal("1", page.Attribute("data-page-number")?.Value);
        Dictionary<string, string> style = ParseStyle(page.Attribute("style")?.Value ?? "");
        AssertClose(layoutPage.Width, ParsePoints(style["width"]));
        AssertClose(layoutPage.Height, ParsePoints(style["height"]));
    }

    [Fact]
    public void Convert_EmitsSelectablePositionedTextWithHighCoverage()
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

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement[] spans = ElementsByClass(dom, "pdf-text-run").ToArray();

        Assert.Equal(layout.Pages[0].Runs.Count, spans.Length);
        Assert.True(TextCoverage(layout.Text, dom.Root?.Value ?? "") >= 0.99);
        Assert.All(spans, span =>
        {
            Dictionary<string, string> style = ParseStyle(span.Attribute("style")?.Value ?? "");
            Assert.Equal("absolute", style["position"]);
            Assert.True(ParsePoints(style["left"]) >= 0);
            Assert.True(ParsePoints(style["top"]) >= 0);
            Assert.True(ParsePoints(style["font-size"]) > 0);
        });
    }

    [Fact]
    public void Convert_SemanticTextMode_EmitsGroupedArxivElements()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = false
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-text-run"));
        Assert.Contains(".pdf-font-", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-color-", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-justified", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-measured-width", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-align-center", html.Css, StringComparison.Ordinal);

        XElement verticalHeader = Assert.Single(dom.Descendants("header"), header =>
            header.Value.Contains("arXiv:1706.03762v7", StringComparison.Ordinal));
        Assert.Contains("pdf-semantic-positioned", verticalHeader.Attribute("class")?.Value);
        Assert.Contains("pdf-semantic-vertical", verticalHeader.Attribute("class")?.Value);
        Dictionary<string, string> verticalStyle = ParseStyle(verticalHeader.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePoints(verticalStyle["top"]), 550f, 590f);

        XElement permissionHeader = Assert.Single(dom.Descendants("header"), header =>
            header.Value.Contains("Provided proper attribution", StringComparison.Ordinal));
        Assert.DoesNotContain("pdf-semantic-positioned", permissionHeader.Attribute("class")?.Value ?? "");
        Assert.Contains("reproduce the tables and figures in this paper solely for use in journalistic or", permissionHeader.Value, StringComparison.Ordinal);
        Assert.Contains("scholarly works.", permissionHeader.Value, StringComparison.Ordinal);

        XElement title = Assert.Single(dom.Descendants("h1"), element =>
            element.Value.Contains("Attention Is All You Need", StringComparison.Ordinal));
        Assert.Null(title.Attribute("data-semantic-kind"));
        Assert.Contains("pdf-semantic-title", title.Attribute("class")?.Value);
        Assert.DoesNotContain("pdf-semantic-title-rule-top", title.Attribute("class")?.Value ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("pdf-semantic-title-rule-bottom", title.Attribute("class")?.Value ?? "", StringComparison.Ordinal);
        Assert.Null(title.Attribute("style"));

        XElement[] authors = ElementsByClass(dom, "pdf-semantic-author-block").ToArray();
        Assert.Equal(8, authors.Length);
        Assert.All(authors, author => Assert.Equal("address", author.Name.LocalName));
        XElement[] authorRows = ElementsByClass(dom, "pdf-semantic-author-row").ToArray();
        Assert.Equal(new[] { 4, 3, 1 }, authorRows
            .Select(row => row.Descendants().Count(element => HasClass(element, "pdf-semantic-author-block")))
            .ToArray());
        Assert.Contains(authors, author =>
            author.Value.Contains("Ashish Vaswani", StringComparison.Ordinal) &&
            author.Value.Contains("avaswani@google.com", StringComparison.Ordinal));
        Assert.Contains(authors, author =>
            author.Value.Contains("Illia Polosukhin", StringComparison.Ordinal) &&
            author.Value.Contains("illia.polosukhin@gmail.com", StringComparison.Ordinal));
        XElement ashish = Assert.Single(authors, author => author.Value.Contains("Ashish Vaswani", StringComparison.Ordinal));
        XElement[] ashishLines = ashish.Descendants().Where(element => HasClass(element, "pdf-semantic-line")).ToArray();
        Assert.True(ashishLines.Length >= 3);
        Assert.NotEqual(ashishLines[0].Attribute("class")?.Value, ashishLines[^1].Attribute("class")?.Value);
        XElement[] asteriskReferences = dom.Descendants("a").Where(link =>
            link.Attribute("href")?.Value == "#page-1-fn-asterisk" &&
            link.Value == "∗").ToArray();
        Assert.Equal(8, asteriskReferences.Length);
        Assert.All(asteriskReferences, reference => Assert.Equal("sup", reference.Parent?.Name.LocalName));

        XElement abstractHeading = Assert.Single(dom.Descendants("h2"), element => element.Value == "Abstract");
        Assert.Null(abstractHeading.Attribute("data-semantic-kind"));
        Assert.Contains("pdf-semantic-align-center", abstractHeading.Attribute("class")?.Value);
        XElement abstractParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("The dominant sequence transduction models", StringComparison.Ordinal) &&
            paragraph.Value.Contains("large and limited training data.", StringComparison.Ordinal));
        Assert.Contains("pdf-semantic-justified", abstractParagraph.Attribute("class")?.Value);
        Assert.Contains("pdf-semantic-measured-width", abstractParagraph.Attribute("class")?.Value);
        Dictionary<string, string> abstractStyle = ParseStyle(abstractParagraph.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePercent(abstractStyle["--pdf-semantic-width"]), 80f, 84f);
        Assert.Equal("center", abstractStyle["--pdf-semantic-align-self"]);

        XElement introductionHeading = Assert.Single(dom.Descendants("h1"), heading => heading.Value == "1 Introduction");
        Assert.DoesNotContain("pdf-semantic-align-center", introductionHeading.Attribute("class")?.Value ?? "");
        XElement pageNumberFooter = Assert.Single(ElementsByClass(dom, "pdf-semantic-footer"), footer => footer.Value == "2");
        Assert.Contains("pdf-semantic-align-center", pageNumberFooter.Attribute("class")?.Value);
        XElement pageEndParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("Most competitive neural sequence transduction models", StringComparison.Ordinal));
        Assert.DoesNotContain("pdf-semantic-measured-width", pageEndParagraph.Attribute("class")?.Value ?? "");
        XElement[] footnotes = ElementsByClass(dom, "pdf-semantic-footnote").ToArray();
        Assert.True(footnotes.Length >= 4);
        Assert.All(footnotes, footnote => Assert.Equal("li", footnote.Name.LocalName));
        Assert.Equal(footnotes.Length, footnotes.Select(footnote => footnote.Attribute("id")?.Value).Distinct().Count());
        XElement asteriskFootnote = Assert.Single(footnotes, footnote =>
            footnote.Attribute("id")?.Value == "page-1-fn-asterisk");
        XElement numericFootnote = Assert.Single(footnotes, footnote =>
            footnote.Attribute("id")?.Value == "page-4-fn-4");
        XElement pageEightFootnote = Assert.Single(footnotes, footnote =>
            footnote.Attribute("id")?.Value == "page-8-fn-5");
        Assert.Null(asteriskFootnote.Attribute("value"));
        Assert.Equal("4", numericFootnote.Attribute("value")?.Value);
        Assert.Equal(
            asteriskReferences.Select(reference => "#" + reference.Parent!.Attribute("id")!.Value),
            asteriskFootnote.Descendants("a")
                .Where(link => HasClass(link, "pdf-semantic-footnote-backref"))
                .Select(link => link.Attribute("href")?.Value));
        AssertVisuallyHiddenAdditionalBacklinks(asteriskFootnote, 7);
        AssertVisuallyHiddenAdditionalBacklinks(pageEightFootnote, 1);
        XElement visibleMarker = Assert.Single(asteriskFootnote.Descendants(), element =>
            HasClass(element, "pdf-semantic-footnote-marker"));
        Assert.Single(visibleMarker.Elements("a"));

        const string additionalBacklinksSelector = ".pdf-semantic-footnote-backrefs {";
        int additionalBacklinksStart = html.Css.IndexOf(additionalBacklinksSelector, StringComparison.Ordinal);
        Assert.True(additionalBacklinksStart >= 0);
        int additionalBacklinksEnd = html.Css.IndexOf('}', additionalBacklinksStart);
        Assert.True(additionalBacklinksEnd > additionalBacklinksStart);
        string additionalBacklinksRule = html.Css[additionalBacklinksStart..additionalBacklinksEnd];
        Assert.Contains("clip: rect(0 0 0 0);", additionalBacklinksRule, StringComparison.Ordinal);
        Assert.Contains("clip-path: inset(50%);", additionalBacklinksRule, StringComparison.Ordinal);
        Assert.Contains("position: absolute;", additionalBacklinksRule, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden;", additionalBacklinksRule, StringComparison.Ordinal);

        XElement[] footnoteSections = ElementsByClass(dom, "pdf-semantic-footnotes").ToArray();
        Assert.NotEmpty(footnoteSections);
        Assert.All(footnoteSections, section =>
        {
            Assert.Equal("section", section.Name.LocalName);
            Assert.Empty(section.Ancestors("aside"));
            Assert.Empty(section.Ancestors("p"));
            string labelId = Assert.IsType<XAttribute>(section.Attribute("aria-labelledby")).Value;
            XElement label = Assert.Single(section.Elements(), element => element.Attribute("id")?.Value == labelId);
            Assert.Equal("Footnotes", label.Value);
            XElement list = Assert.Single(section.Elements("ol"));
            Assert.Empty(list.Ancestors("p"));
            Assert.NotEmpty(list.Elements("li"));
            Assert.All(list.Elements("li"), item => Assert.True(HasClass(item, "pdf-semantic-footnote")));
        });
        Assert.Contains(ElementsByClass(dom, "pdf-semantic-footer"), footer =>
            footer.Value.Contains("31st Conference", StringComparison.Ordinal));
        Assert.Contains(".pdf-semantic-flow > footer.pdf-semantic-footer", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-footnotes::before", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_SemanticCode_UsesNativeWhitespaceAndBrowserSelectionText()
    {
        const string sourceCode = "LOAD  R0, [R1]\nSTORE R0, [R2]\n  ADD R0, #1";
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(CreateSemanticCodeFixtureLines());
        PdfSemanticElement semanticCode = Assert.Single(
            PdfSemanticExtractor.Extract(layout).Elements,
            static element => element.Kind == PdfSemanticElementKind.CodeBlock);
        Assert.Equal(sourceCode, semanticCode.Text);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement pre = Assert.Single(dom.Descendants("pre"), element =>
            HasClass(element, "pdf-semantic-code-block"));
        XElement blockCode = Assert.Single(pre.Elements("code"));
        Assert.Equal(sourceCode, blockCode.Value);
        Assert.Empty(pre.Descendants("span"));
        Assert.DoesNotContain(pre.DescendantsAndSelf(), element =>
            element.Attribute("style")?.Value.Contains("position:", StringComparison.Ordinal) == true);
        XElement inlineCode = Assert.Single(dom.Descendants("code"), element =>
            HasClass(element, "pdf-semantic-inline-code"));
        Assert.Equal("gpio_set()", inlineCode.Value);
        Assert.Contains(".pdf-semantic-code-block > code", html.Css, StringComparison.Ordinal);
        Assert.Contains("white-space: pre;", html.Css, StringComparison.Ordinal);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync();
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        string[] browserText = await browserPage.EvaluateAsync<string[]>(
            """
            () => {
              const pre = document.querySelector("pre.pdf-semantic-code-block");
              const code = pre.querySelector(":scope > code");
              const range = document.createRange();
              range.selectNodeContents(code);
              const selection = getSelection();
              selection.removeAllRanges();
              selection.addRange(range);
              const selectedText = selection.toString();
              selection.removeAllRanges();
              return [pre.textContent, code.textContent, selectedText];
            }
            """);

        Assert.Equal([sourceCode, sourceCode, sourceCode], browserText);
    }

    [Fact]
    public void Convert_AdamPageTwo_EmitsStructuredRuledAlgorithmWithRichRows()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-adam-page-2.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        Assert.DoesNotContain('\f', html.Html);
        XDocument dom = ParseHtml(html.Html);

        XElement figure = Assert.Single(ElementsByClass(dom, "pdf-semantic-algorithm"));
        Assert.Equal("figure", figure.Name.LocalName);
        XElement caption = Assert.Single(figure.Elements("figcaption"));
        Assert.StartsWith("Algorithm 1: Adam", caption.Value, StringComparison.Ordinal);
        Assert.Contains("⊙", caption.Value, StringComparison.Ordinal);
        Assert.DoesNotContain('�', caption.Value);
        XElement rows = Assert.Single(figure.Elements("div"), element =>
            HasClass(element, "pdf-semantic-algorithm-rows"));
        Assert.Equal("list", rows.Attribute("role")?.Value);
        XElement[] sourceRows = rows.Elements("div")
            .Where(element => HasClass(element, "pdf-semantic-algorithm-row"))
            .ToArray();
        Assert.Equal(17, sourceRows.Length);
        Assert.Equal(4, sourceRows.Count(row => row.Value.StartsWith("Require:", StringComparison.Ordinal)));
        Assert.All(sourceRows, row =>
        {
            Assert.Equal("listitem", row.Attribute("role")?.Value);
            Assert.Single(row.Elements("code"));
        });
        Assert.StartsWith("while", sourceRows[7].Value, StringComparison.Ordinal);
        Assert.StartsWith("end while", sourceRows[^2].Value, StringComparison.Ordinal);
        Assert.StartsWith("return", sourceRows[^1].Value, StringComparison.Ordinal);
        Assert.Contains('←', sourceRows[8].Value);
        Assert.True(sourceRows[8].Value.IndexOf('←') > sourceRows[8].Value.IndexOf('t'));
        Assert.Contains(sourceRows[9].Descendants("sub"), subscript => subscript.Value == "t");
        Assert.Contains(sourceRows[11].Descendants("sup"), superscript => superscript.Value == "2");
        Assert.Contains('√', sourceRows[14].Value);
        Assert.True(sourceRows[14].Value.IndexOf('√') < sourceRows[14].Value.LastIndexOf('v'));
        Assert.NotEmpty(figure.Descendants("sub"));
        Assert.NotEmpty(figure.Descendants("sup"));
        Assert.Empty(dom.Descendants("hr"));
        Assert.DoesNotContain(dom.Descendants("p"), paragraph =>
            paragraph.Value.StartsWith("Require:", StringComparison.Ordinal));

        XElement replacementProse = Assert.Single(dom.Descendants("p"), paragraph =>
            HasClass(paragraph, "pdf-semantic-paragraph") &&
            paragraph.Value.EndsWith("with the following lines:", StringComparison.Ordinal));
        Assert.DoesNotContain('√', replacementProse.Value);
        Assert.DoesNotContain('←', replacementProse.Value);
        XElement replacementFormula = Assert.Single(ElementsByClass(dom, "pdf-semantic-formula"));
        Assert.Equal("div", replacementFormula.Name.LocalName);
        Assert.Equal("math", replacementFormula.Attribute("role")?.Value);
        Assert.Equal(
            "αt = α · √1 − β2t/(1 − β1t) and θt ← θt−1 − αt · mt/(√vt + ε̂).",
            replacementFormula.Attribute("aria-label")?.Value);
        Assert.NotEmpty(replacementFormula.Descendants("span"));
        Assert.Contains(replacementFormula, replacementProse.ElementsAfterSelf());
        Assert.DoesNotContain(dom.Descendants("p"), paragraph =>
            paragraph.Value.Trim().All(character => char.IsWhiteSpace(character) || character is '·' or '−' or '←'));

        Dictionary<string, string> style = ParseStyle(figure.Attribute("style")?.Value ?? "");
        Assert.Contains("--pdf-semantic-algorithm-top-rule-width", style.Keys);
        Assert.Contains("--pdf-semantic-algorithm-caption-rule-width", style.Keys);
        Assert.Contains("--pdf-semantic-algorithm-bottom-rule-width", style.Keys);
        Assert.Contains(".pdf-semantic-algorithm-row > code", html.Css, StringComparison.Ordinal);
        Assert.Contains("white-space: pre-wrap", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_AdamPageTwo_BrowserTextRestoresProseBoundariesAndDehyphenation()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-adam-page-2.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);
        string prose = string.Join(" ", dom.Descendants("p").Select(static paragraph => paragraph.Value));

        Assert.Contains("careful choice of stepsizes", prose, StringComparison.Ordinal);
        Assert.Contains("parameter space at timestep t", prose, StringComparison.Ordinal);
        Assert.Contains("noisy objective function", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("ofstepsizes", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("attimestep", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("objec-tive", prose, StringComparison.Ordinal);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync();
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);
        IReadOnlyList<string> browserParagraphs = await browserPage
            .Locator("p.pdf-semantic-paragraph")
            .AllInnerTextsAsync();
        string browserProse = string.Join(" ", browserParagraphs);

        Assert.Contains("careful choice of stepsizes", browserProse, StringComparison.Ordinal);
        Assert.Contains("parameter space at timestep t", browserProse, StringComparison.Ordinal);
        Assert.Contains("noisy objective function", browserProse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_AdamPageTwo_AlgorithmGeometryIsStableAtDesktopAndNarrowWidths()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-adam-page-2.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync();
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        foreach (int width in new[] { 1000, 360 })
        {
            await browserPage.SetViewportSizeAsync(width, 900);
            double[] geometry = await browserPage.EvaluateAsync<double[]>(
                """
                () => {
                  const figure = document.querySelector("figure.pdf-semantic-algorithm");
                  const flow = document.querySelector(".pdf-semantic-flow");
                  const caption = figure.querySelector("figcaption");
                  const rows = Array.from(figure.querySelectorAll(".pdf-semantic-algorithm-row"));
                  const codes = rows.map(row => row.querySelector(":scope > code").getBoundingClientRect());
                  const figureBox = figure.getBoundingClientRect();
                  const flowBox = flow.getBoundingClientRect();
                  const style = getComputedStyle(figure);
                  const captionStyle = getComputedStyle(caption);
                  const ordered = codes.every((box, index) => index === 0 || box.top >= codes[index - 1].bottom - 0.5);
                  const formula = document.querySelector(".pdf-semantic-formula");
                  const formulaBox = formula.getBoundingClientRect();
                  const formulaRuns = Array.from(formula.querySelectorAll(".pdf-semantic-formula-run"));
                  const runBoxes = formulaRuns.map(run => run.getBoundingClientRect());
                  const prose = Array.from(document.querySelectorAll("p.pdf-semantic-paragraph"))
                    .find(paragraph => paragraph.textContent.endsWith("with the following lines:"));
                  const nextHeading = Array.from(document.querySelectorAll(".pdf-semantic-element"))
                    .find(element => element.textContent.includes("ADAM’S UPDATE RULE"));
                  const byText = text => formulaRuns
                    .filter(run => run.textContent === text)
                    .sort((first, second) => first.getBoundingClientRect().left - second.getBoundingClientRect().left);
                  const alphas = byText("α");
                  const dots = byText("·");
                  const radicals = byText("√");
                  const ones = byText("1");
                  const minuses = byText("−");
                  const betas = byText("β");
                  const thetas = byText("θ");
                  const arrows = byText("←");
                  const visualFormulaOrder =
                    alphas.length >= 2 && dots.length >= 1 && radicals.length >= 1 &&
                    ones.length >= 1 && minuses.length >= 1 && betas.length >= 1 &&
                    thetas.length >= 2 && arrows.length === 1 &&
                    alphas[1].getBoundingClientRect().left < dots[0].getBoundingClientRect().left &&
                    dots[0].getBoundingClientRect().left < radicals[0].getBoundingClientRect().left &&
                    radicals[0].getBoundingClientRect().left < ones[0].getBoundingClientRect().left &&
                    ones[0].getBoundingClientRect().left < minuses[0].getBoundingClientRect().left &&
                    minuses[0].getBoundingClientRect().left < betas[0].getBoundingClientRect().left &&
                    thetas[0].getBoundingClientRect().left < arrows[0].getBoundingClientRect().left &&
                    arrows[0].getBoundingClientRect().left < thetas[1].getBoundingClientRect().left;
                  return [
                    document.documentElement.scrollWidth,
                    innerWidth,
                    figureBox.left,
                    figureBox.right,
                    flowBox.left,
                    flowBox.right,
                    parseFloat(style.borderTopWidth),
                    parseFloat(captionStyle.borderBottomWidth),
                    parseFloat(style.borderBottomWidth),
                    ordered ? 1 : 0,
                    figure.scrollWidth <= figure.clientWidth + 1 ? 1 : 0,
                    codes[0].left,
                    codes[4].left,
                    codes[8].left,
                    formulaBox.left,
                    formulaBox.right,
                    formulaBox.top,
                    formulaBox.bottom,
                    prose.getBoundingClientRect().bottom,
                    nextHeading.getBoundingClientRect().top,
                    formulaRuns.length,
                    visualFormulaOrder ? 1 : 0,
                    Math.min(...runBoxes.map(box => box.left)),
                    Math.max(...runBoxes.map(box => box.right)),
                    formula.scrollWidth,
                    formula.clientWidth,
                    getComputedStyle(formula).overflowX === "auto" ? 1 : 0
                  ];
                }
                """);

            Assert.True(geometry[0] <= geometry[1] + 1, $"viewport {width} overflowed by {geometry[0] - geometry[1]:0.##}px");
            Assert.True(geometry[2] >= geometry[4] - 1);
            Assert.True(geometry[3] <= geometry[5] + 1);
            Assert.All(geometry[6..9], border => Assert.True(border > 0));
            Assert.Equal(1, geometry[9]);
            Assert.Equal(1, geometry[10]);
            Assert.True(geometry[12] >= geometry[11] + 5);
            Assert.True(geometry[13] >= geometry[12] + 5);
            Assert.True(geometry[14] >= geometry[4] - 1);
            Assert.True(geometry[15] <= geometry[5] + 1);
            Assert.True(geometry[16] >= geometry[18] - 1);
            Assert.True(geometry[17] <= geometry[19] + 1);
            Assert.True(geometry[20] > 0);
            Assert.Equal(1, geometry[21]);
            Assert.True(geometry[22] >= geometry[14] - 1);
            Assert.Equal(1, geometry[26]);
            if (width == 1000)
            {
                Assert.True(geometry[23] <= geometry[15] + 1);
                Assert.True(geometry[24] <= geometry[25] + 1);
            }
            else
            {
                Assert.True(geometry[24] > geometry[25]);
            }
        }
    }

    [Fact]
    public void Convert_SemanticCode_FullPageVectorBackdropStillUsesFixedLayoutFallback()
    {
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
            CreateSemanticCodeFixtureLines(),
            [CreateSemanticCalloutPath(new PdfLayoutRectangle(0f, 0f, 612f, 792f))]);
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        Assert.Contains(semanticPage.Elements, static element =>
            element.Kind == PdfSemanticElementKind.CodeBlock);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Single(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        Assert.Empty(dom.Descendants("pre"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_KeepsPageBreakFootnoteContinuationInOneListItem()
    {
        PdfLayoutDocument layout = CreateCrossPageFootnoteLayoutFixture();
        PdfSemanticDocument semantic = PdfSemanticExtractor.Extract(layout);
        PdfSemanticElement firstFragment = Assert.Single(semantic.Pages[0].Elements, static element =>
            element.Kind == PdfSemanticElementKind.Footnote);
        PdfSemanticElement continuation = Assert.Single(semantic.Pages[1].Elements, static element =>
            element.Kind == PdfSemanticElementKind.Footnote);

        Assert.Equal("*", firstFragment.Note!.Marker);
        Assert.True(firstFragment.Note.ContinuesOnNextPage);
        Assert.Equal("*", continuation.Note!.Marker);
        Assert.True(continuation.Note.ContinuesPreviousNote);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-semantic-layout-fallback-page"));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-line-grid"));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-columns"));
        XElement section = Assert.Single(ElementsByClass(dom, "pdf-semantic-footnotes"));
        Assert.Equal("section", section.Name.LocalName);
        Assert.Empty(section.Ancestors("aside"));
        Assert.Empty(section.Ancestors("p"));
        string labelId = Assert.IsType<XAttribute>(section.Attribute("aria-labelledby")).Value;
        Assert.Equal("Footnotes", Assert.Single(section.Elements(), element =>
            element.Attribute("id")?.Value == labelId).Value);
        XElement list = Assert.Single(section.Elements("ol"));
        Assert.Empty(list.Ancestors("p"));
        XElement note = Assert.Single(list.Elements("li"));
        Assert.Equal("page-1-fn-asterisk", note.Attribute("id")?.Value);
        Assert.Contains("A note starts near the page boundary and", note.Value, StringComparison.Ordinal);
        Assert.Contains("continues on the next page.", note.Value, StringComparison.Ordinal);
        Assert.Single(note.Descendants(), element =>
            HasClass(element, "pdf-semantic-note-continuation") &&
            element.Attribute("data-source-page")?.Value == "2");

        XElement[] references = dom.Descendants("a")
            .Where(link => HasClass(link, "pdf-semantic-footnote-ref"))
            .ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, reference => Assert.Equal("#page-1-fn-asterisk", reference.Attribute("href")?.Value));
        Assert.Equal(
            references.Select(reference => "#" + reference.Parent!.Attribute("id")!.Value),
            note.Descendants("a")
                .Where(link => HasClass(link, "pdf-semantic-footnote-backref"))
                .Select(link => link.Attribute("href")?.Value));
    }

    [Fact]
    public void Convert_ScientificFrontMatter_PreservesSourceWidthRowsAndSpacingWithoutWideningQuotation()
    {
        PdfHtmlDocument html = PdfHtmlConverter.Convert(
            CreateScientificFrontMatterLayoutFixture(),
            new PdfHtmlOptions { TextMode = PdfHtmlTextMode.Semantic });
        XDocument dom = ParseHtml(html.Html);

        XElement frontMatter = Assert.Single(ElementsByClass(dom, "pdf-semantic-front-matter"));
        Assert.Contains("pdf-semantic-align-center", frontMatter.Attribute("class")?.Value);
        Assert.Contains("† Department of Applied Mathematics", frontMatter.Value, StringComparison.Ordinal);
        Assert.Contains("‡ Center for Computational Science", frontMatter.Value, StringComparison.Ordinal);
        Assert.Contains("§ Institute for Scientific Computing", frontMatter.Value, StringComparison.Ordinal);
        Assert.EndsWith("WWW home page: https://example.edu/research", frontMatter.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Abstract", frontMatter.Value, StringComparison.Ordinal);

        Dictionary<string, string> frontMatterStyle = ParseStyle(frontMatter.Attribute("style")?.Value ?? "");
        Assert.Equal(440f, ParsePoints(frontMatterStyle["--pdf-semantic-front-matter-width"]));
        Assert.Equal(28f, ParsePoints(frontMatterStyle["--pdf-semantic-front-matter-gap-after"]));
        XElement[] sourceRows = frontMatter.Elements()
            .Where(element => HasClass(element, "pdf-semantic-line"))
            .ToArray();
        Assert.Equal(6, sourceRows.Length);
        Assert.Null(sourceRows[0].Attribute("style"));
        Assert.True(ParsePoints(ParseStyle(sourceRows[1].Attribute("style")?.Value ?? "")["margin-top"]) > 12f);
        Assert.True(ParsePoints(ParseStyle(sourceRows[^1].Attribute("style")?.Value ?? "")["margin-top"]) > 10f);

        XElement abstractParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), element =>
            element.Value.StartsWith("Abstract. This study", StringComparison.Ordinal));
        Assert.Contains(frontMatter, abstractParagraph.NodesBeforeSelf());

        XElement quotation = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), element =>
            element.Value.StartsWith("A narrow quotation remains", StringComparison.Ordinal));
        Assert.Contains("pdf-semantic-align-center", quotation.Attribute("class")?.Value);
        Assert.DoesNotContain("pdf-semantic-front-matter", quotation.Attribute("class")?.Value ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("pdf-semantic-measured-width", quotation.Attribute("class")?.Value ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("--pdf-semantic-width", quotation.Attribute("style")?.Value ?? "", StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-front-matter", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticTextMode_EmitsArxivVariationTableSpans()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        XDocument dom = ParseHtml(html.Html);

        XElement[] tableCaptions = dom.Descendants("caption")
            .Where(static caption => caption.Value.StartsWith("Table ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(4, tableCaptions.Length);
        Assert.Equal(
            ["Table 1:", "Table 2:", "Table 3:", "Table 4:"],
            tableCaptions.Select(static caption => caption.Value[..8]).ToArray());
        Assert.All(tableCaptions, caption =>
        {
            Assert.Equal("table", caption.Parent?.Name.LocalName);
            Assert.Same(caption, caption.Parent!.Elements().First());
            Assert.True(HasClass(caption, "pdf-semantic-table-caption-above"));
            Assert.True(HasClass(caption, "pdf-semantic-table-caption-align-left"));
            Assert.Null(caption.Parent.Attribute("aria-label"));
            Dictionary<string, string> style = ParseStyle(caption.Attribute("style")?.Value ?? "");
            Assert.Contains("--pdf-semantic-table-caption-width", style);
            Assert.Contains("--pdf-semantic-table-caption-offset", style);
            Assert.Contains("--pdf-semantic-table-caption-gap", style);
        });
        Assert.DoesNotContain(dom.Descendants("p"), static paragraph =>
            paragraph.Value.StartsWith("Table 1:", StringComparison.Ordinal) ||
            paragraph.Value.StartsWith("Table 2:", StringComparison.Ordinal) ||
            paragraph.Value.StartsWith("Table 3:", StringComparison.Ordinal) ||
            paragraph.Value.StartsWith("Table 4:", StringComparison.Ordinal));
        foreach (string label in new[] { "Table 1:", "Table 2:", "Table 3:", "Table 4:" })
        {
            Assert.Single(Regex.Matches(html.Html, Regex.Escape(label)).Cast<Match>());
        }

        XElement variationTable = Assert.Single(dom.Descendants("table"), table =>
            table.Value.Contains("Pdrop", StringComparison.Ordinal) &&
            table.Value.Contains("positional embedding instead of sinusoids", StringComparison.Ordinal));
        XElement groupA = Assert.Single(variationTable.Descendants("th"), cell => cell.Value.Trim() == "(A)");
        Assert.Equal("rowgroup", groupA.Attribute("scope")?.Value);
        Assert.Equal("4", groupA.Attribute("rowspan")?.Value);
        Assert.Contains("pdf-semantic-table-row-group-header", groupA.Attribute("class")?.Value);

        XElement groupB = Assert.Single(variationTable.Descendants("th"), cell => cell.Value.Trim() == "(B)");
        Assert.Equal("2", groupB.Attribute("rowspan")?.Value);
        XElement groupC = Assert.Single(variationTable.Descendants("th"), cell => cell.Value.Trim() == "(C)");
        Assert.Equal("7", groupC.Attribute("rowspan")?.Value);
        XElement groupD = Assert.Single(variationTable.Descendants("th"), cell => cell.Value.Trim() == "(D)");
        Assert.Equal("4", groupD.Attribute("rowspan")?.Value);

        XElement descriptorCell = Assert.Single(variationTable.Descendants("td"), cell =>
            cell.Value.Contains("positional embedding instead of sinusoids", StringComparison.Ordinal));
        Assert.Equal("9", descriptorCell.Attribute("colspan")?.Value);
        Assert.DoesNotContain(variationTable.Descendants("tr"), row =>
            row.Elements().Count() == 1 &&
            row.Value.Trim() is "(A)" or "(B)" or "(D)");
        Assert.Contains(".pdf-semantic-table td[colspan]", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table-row-group-header", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table-caption-below", html.Css, StringComparison.Ordinal);
        Assert.Contains("caption-side: bottom", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table-caption-align-left", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table-caption-align-center", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table-caption-align-right", html.Css, StringComparison.Ordinal);
        Assert.Contains("text-align: start", html.Css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_SemanticTableCaption_AppliesConfidentSourceAlignmentInBrowser()
    {
        PdfHtmlDocument html = PdfHtmlConverter.Convert(
            CreateSemanticTableCaptionAlignmentLayoutFixture(),
            new PdfHtmlOptions { TextMode = PdfHtmlTextMode.Semantic });
        XDocument dom = ParseHtml(html.Html);
        XElement[] captions = dom.Descendants("caption")
            .Where(static caption => caption.Value.StartsWith("Table ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, captions.Length);
        Assert.True(HasClass(captions[0], "pdf-semantic-table-caption-align-left"));
        Assert.True(HasClass(captions[1], "pdf-semantic-table-caption-align-center"));
        Assert.True(HasClass(captions[2], "pdf-semantic-table-caption-align-right"));

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync();
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        string[] computedAlignments = await browserPage.EvaluateAsync<string[]>(
            """
            () => Array.from(document.querySelectorAll("caption.pdf-semantic-table-caption"))
              .map(caption => {
                const label = caption.textContent.trim().match(/^Table \d+:/)[0];
                const content = caption.querySelector(".pdf-semantic-table-caption-content");
                const captionStyle = getComputedStyle(caption);
                const contentStyle = getComputedStyle(content);
                return `${label}|${captionStyle.textAlign}|${captionStyle.textAlignLast}|${contentStyle.textAlign}|${contentStyle.textAlignLast}`;
              })
            """);

        Assert.Equal(
            [
                "Table 7:|left|left|left|left",
                "Table 8:|center|center|center|center",
                "Table 9:|right|right|right|right"
            ],
            computedAlignments);
    }

    [Fact]
    public void Convert_SemanticTableCaption_PreservesRichLinkGeometryAndStableHeadingId()
    {
        PdfLayoutDocument layout = CreateSemanticTableCaptionLayoutFixture();
        PdfHtmlDocument firstHtml = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        PdfHtmlDocument secondHtml = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        XDocument first = ParseHtml(firstHtml.Html);
        XDocument second = ParseHtml(secondHtml.Html);

        XElement table = Assert.Single(first.Descendants("table"), static table =>
            table.Value.Contains("Alpha", StringComparison.Ordinal) &&
            table.Value.Contains("Outcome", StringComparison.Ordinal));
        XElement caption = Assert.Single(table.Elements("caption"));
        Assert.Same(caption, table.Elements().First());
        Assert.Equal("Table 7: Linked benchmark details.", caption.Value);
        Assert.True(HasClass(caption, "pdf-semantic-table-caption-above"));
        Assert.Null(table.Attribute("aria-label"));

        XElement link = Assert.Single(caption.Descendants("a"));
        Assert.Equal("https://example.test/benchmark", link.Attribute("href")?.Value);
        Assert.True(HasClass(link, "pdf-semantic-link"));
        Assert.Contains(link.Descendants("strong"), static strong =>
            strong.Value == "Linked benchmark");
        Dictionary<string, string> captionStyle = ParseStyle(caption.Attribute("style")?.Value ?? "");
        Assert.Contains("--pdf-semantic-table-caption-width", captionStyle);
        Assert.Contains("--pdf-semantic-table-caption-offset", captionStyle);
        Assert.Contains("--pdf-semantic-table-caption-gap", captionStyle);
        Assert.DoesNotContain(first.Descendants("p"), static paragraph =>
            paragraph.Value.Contains("Table 7:", StringComparison.Ordinal));

        XElement firstHeading = Assert.Single(first.Descendants(), static element =>
            element.Name.LocalName.StartsWith("h", StringComparison.Ordinal) &&
            element.Value == "2 Results");
        XElement secondHeading = Assert.Single(second.Descendants(), static element =>
            element.Name.LocalName.StartsWith("h", StringComparison.Ordinal) &&
            element.Value == "2 Results");
        Assert.False(string.IsNullOrWhiteSpace(firstHeading.Attribute("id")?.Value));
        Assert.Equal(firstHeading.Attribute("id")?.Value, secondHeading.Attribute("id")?.Value);
    }

    [Fact]
    public void Convert_SemanticTextMode_ReservesGraphicRegionsAndPromotesFlowRulesToCss()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeLinks = false
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });
        XDocument dom = ParseHtml(html.Html);

        XElement firstPage = Assert.Single(dom.Descendants("section"), section =>
            section.Attribute("id")?.Value == "page-1");
        Assert.DoesNotContain(firstPage.Descendants(), element => HasClass(element, "pdf-vector-layer"));
        Assert.Empty(firstPage.Descendants("hr"));
        XElement title = Assert.Single(firstPage.Descendants("h1"), heading => HasClass(heading, "pdf-semantic-title"));
        Assert.True(HasClass(title, "pdf-semantic-title-rule-top"));
        Assert.True(HasClass(title, "pdf-semantic-title-rule-bottom"));
        Dictionary<string, string> titleStyle = ParseStyle(title.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePoints(titleStyle["--pdf-title-rule-top-thickness"]), 3.9f, 4.1f);
        Assert.InRange(ParsePoints(titleStyle["--pdf-title-rule-bottom-thickness"]), 0.9f, 1.1f);
        Assert.Contains("border-top: var(--pdf-title-rule-top-thickness", html.Css, StringComparison.Ordinal);
        Assert.Contains("border-bottom: var(--pdf-title-rule-bottom-thickness", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-footnotes::before", html.Css, StringComparison.Ordinal);
        XElement footnoteSection = Assert.Single(firstPage.Descendants(), element => HasClass(element, "pdf-semantic-footnotes"));
        Dictionary<string, string> footnoteRuleStyle = ParseStyle(footnoteSection.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePoints(footnoteRuleStyle["--pdf-footnote-rule-width"]), 140f, 146f);
        Assert.InRange(ParsePoints(footnoteRuleStyle["--pdf-footnote-rule-thickness"]), 0.3f, 0.5f);
        Assert.Equal("#000000", footnoteRuleStyle["--pdf-footnote-rule-color"]);
        Assert.DoesNotContain(dom.Descendants("h1").Where(heading =>
                heading.Value.Contains("Input-Input Layer5", StringComparison.Ordinal)),
            heading => HasClass(heading, "pdf-semantic-title"));

        XElement[] figureSpaces = ElementsByClass(dom, "pdf-semantic-figure-space").ToArray();
        Assert.True(figureSpaces.Length >= 2);
        Assert.All(figureSpaces, figure =>
        {
            Dictionary<string, string> style = ParseStyle(figure.Attribute("style")?.Value ?? "");
            Assert.True(ParsePoints(style["height"]) >= 30f);
        });

        XElement figure1Caption = Assert.Single(ElementsByClass(dom, "pdf-semantic-caption"), paragraph =>
            paragraph.Value == "Figure 1: The Transformer - model architecture.");
        Assert.Contains("pdf-semantic-align-center", figure1Caption.Attribute("class")?.Value);
        XElement figure4Caption = Assert.Single(ElementsByClass(dom, "pdf-semantic-caption"), paragraph =>
            paragraph.Value.StartsWith("Figure 4: Two attention heads", StringComparison.Ordinal));
        Assert.DoesNotContain("pdf-semantic-align-center", figure4Caption.Attribute("class")?.Value ?? "");

        XElement fourthPage = Assert.Single(dom.Descendants("section"), section =>
            section.Attribute("id")?.Value == "page-4");
        XElement flow = Assert.Single(fourthPage.Elements("article"), article => HasClass(article, "pdf-semantic-flow"));
        XElement[] flowChildren = flow.Elements().ToArray();
        XElement attentionLabels = Assert.Single(flowChildren, element =>
            HasClass(element, "pdf-semantic-line-row") &&
            element.Value.Contains("Scaled Dot-Product Attention", StringComparison.Ordinal) &&
            element.Value.Contains("Multi-Head Attention", StringComparison.Ordinal));
        XElement[] labelLines = attentionLabels.Elements("span").ToArray();
        Assert.Equal(new[] { "Scaled Dot-Product Attention", "Multi-Head Attention" }, labelLines.Select(static line => line.Value).ToArray());
        Assert.Contains("pdf-semantic-align-center", attentionLabels.Attribute("class")?.Value);
        Dictionary<string, string> attentionLabelStyle = ParseStyle(attentionLabels.Attribute("style")?.Value ?? "");
        Assert.Equal("2", attentionLabelStyle["--pdf-semantic-line-count"]);
        int labelIndex = Array.IndexOf(flowChildren, attentionLabels);
        int figureSpaceIndex = Array.FindIndex(flowChildren, element => HasClass(element, "pdf-semantic-figure-space"));
        Assert.True(labelIndex >= 0 && figureSpaceIndex > labelIndex);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsNativeThematicBreakWithoutDuplicateVector()
    {
        PdfLayoutColor color = new(0.2f, 0.4f, 0.6f, 1f, "DeviceRGB");
        PdfLayoutDocument layout = CreateSemanticHtmlFixture(
        [
            CreateScientificFixtureLine("Leading prose keeps this focused fixture in semantic flow mode.", 72f, 40f, 390f),
            CreateScientificFixtureLine("Its continuation establishes the page's ordinary top margin.", 72f, 52f, 360f),
            CreateScientificFixtureLine("Opening prose establishes the ordinary page measure and rhythm.", 72f, 72f, 420f),
            CreateScientificFixtureLine("A second line completes the introductory flow region.", 72f, 84f, 350f),
            CreateScientificFixtureLine("The first discussion region occupies its own natural-flow block.", 72f, 118f, 410f),
            CreateScientificFixtureLine("Its continuation closes before the visual transition.", 72f, 130f, 330f),
            CreateScientificFixtureLine("A distinct discussion region begins after the visual transition.", 72f, 214f, 410f),
            CreateScientificFixtureLine("The following line confirms ordinary content has resumed.", 72f, 226f, 360f)
        ],
        [CreateSemanticRulePath(0, 180f, 172f, 432f, 172f, 2.25f, color)]);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);
        XElement thematicBreak = Assert.Single(dom.Descendants("hr"));
        Dictionary<string, string> style = ParseStyle(thematicBreak.Attribute("style")?.Value ?? "");

        Assert.True(HasClass(thematicBreak, "pdf-semantic-thematic-break"));
        Assert.Equal("0", thematicBreak.Attribute("data-source-path-index")?.Value);
        Assert.Equal(252f, ParsePoints(thematicBreak.Attribute("data-source-width")?.Value ?? ""));
        Assert.InRange(ParsePercent(style["--pdf-thematic-break-width"]), 63.63f, 63.64f);
        Assert.Equal(2.25f, ParsePoints(style["--pdf-thematic-break-thickness"]));
        Assert.Equal("rgba(51,102,153,1)", style["--pdf-thematic-break-color"]);
        Assert.Equal("center", style["--pdf-thematic-break-alignment"]);
        Assert.DoesNotContain("position", thematicBreak.Attribute("style")?.Value ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(dom.Descendants(), element =>
            element.Name.LocalName == "path" &&
            element.Attribute("data-path-index")?.Value == "0");

        XElement[] siblings = thematicBreak.Parent!.Elements().ToArray();
        int breakIndex = Array.IndexOf(siblings, thematicBreak);
        Assert.True(breakIndex > 0 && breakIndex < siblings.Length - 1);
        Assert.True(HasClass(siblings[breakIndex - 1], "pdf-semantic-paragraph"));
        Assert.True(HasClass(siblings[breakIndex + 1], "pdf-semantic-paragraph"));

        Match cssRule = Regex.Match(
            html.Css,
            @"\.pdf-semantic-thematic-break\s*\{(?<body>[^}]*)\}",
            RegexOptions.CultureInvariant);
        Assert.True(cssRule.Success);
        Assert.Contains("border-top: var(--pdf-thematic-break-thickness", cssRule.Groups["body"].Value);
        Assert.DoesNotContain("position", cssRule.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Convert_SemanticContinuousFlow_PreservesCoverTitleCompositionWithoutInventedRules()
    {
        using PDDocument document = CreateCoverTitleCompositionDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement titleRegion = Assert.Single(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.HeadingLevel == 1 &&
            element.Text.Contains("Designing Secure Systems", StringComparison.Ordinal));

        Assert.Equal(3, titleRegion.Lines.Count);
        Assert.Equal(
            ["Designing Secure Systems", "Across Public Networks", "For Modern Operations"],
            titleRegion.Lines.Select(static line => line.Text.Trim()).ToArray());

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement title = Assert.Single(dom.Descendants("h1"), heading => HasClass(heading, "pdf-semantic-title"));
        Assert.True(HasClass(title, "pdf-semantic-align-right"));
        Assert.False(HasClass(title, "pdf-semantic-title-rule-top"));
        Assert.False(HasClass(title, "pdf-semantic-title-rule-bottom"));
        Assert.Equal(3, title.Elements("span").Count(element => HasClass(element, "pdf-semantic-line")));
        Assert.DoesNotContain("--pdf-title-rule-", title.Attribute("style")?.Value ?? "", StringComparison.Ordinal);

        XElement coverRegion = Assert.Single(ElementsByClass(dom, "pdf-semantic-cover-region"));
        XElement decorationLayer = Assert.Single(coverRegion.Elements(), element =>
            HasClass(element, "pdf-semantic-cover-decoration-layer"));
        Assert.Equal(2, decorationLayer.Descendants().Count(static element => element.Name.LocalName == "img"));
        Assert.Single(decorationLayer.Descendants(), element =>
            element.Name.LocalName == "path" && element.Attribute("data-path-index") != null);
        Dictionary<string, string> coverStyle = ParseStyle(coverRegion.Attribute("style")?.Value ?? "");
        Assert.Equal(612f, ParsePoints(coverStyle["--pdf-semantic-cover-width"]));
        Assert.Equal(792f, ParsePoints(coverStyle["--pdf-semantic-cover-height"]));
        Assert.Equal(2, html.Assets.Count(asset => asset.RelativePath.Contains("page-1-image", StringComparison.Ordinal)));

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 816,
                Height = 1200
            }
        });
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);
        double[] coverTextRightEdges = await browserPage.EvaluateAsync<double[]>(
            """
            () => {
              const textRight = element => {
                const range = document.createRange();
                range.selectNodeContents(element);
                const right = range.getBoundingClientRect().right;
                range.detach();
                return right;
              };
              const coverText = Array.from(document.querySelectorAll("#page-1 + .pdf-semantic-cover-region .pdf-semantic-cover-region-element"))
                .filter(element => element.getBoundingClientRect().top < 800);
              return coverText.flatMap(element => {
                const lines = Array.from(element.querySelectorAll(":scope > .pdf-semantic-line"));
                return (lines.length === 0 ? [element] : lines).map(textRight);
              });
            }
            """);

        Assert.Equal(10, coverTextRightEdges.Length);
        Assert.All(coverTextRightEdges, right => Assert.InRange(right, 712d, 732d));

        double[] coverTopEdges = await browserPage.EvaluateAsync<double[]>(
            """
            () => {
              const cover = document.querySelector("#page-1 + .pdf-semantic-cover-region");
              const coverTop = cover.getBoundingClientRect().top;
              return [
                cover.querySelector(".pdf-image").getBoundingClientRect().top - coverTop,
                cover.querySelector("header").getBoundingClientRect().top - coverTop,
                cover.querySelector("h1").getBoundingClientRect().top - coverTop
              ];
            }
            """);
        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        PdfSemanticElement header = Assert.Single(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Header);
        Assert.InRange(coverTopEdges[0], layoutPage.Images.Min(static image => image.Bounds.Y) * 4d / 3d - 2d,
            layoutPage.Images.Min(static image => image.Bounds.Y) * 4d / 3d + 2d);
        Assert.InRange(coverTopEdges[1], header.Bounds.Y * 4d / 3d - 2d, header.Bounds.Y * 4d / 3d + 2d);
        Assert.InRange(coverTopEdges[2], titleRegion.Bounds.Y * 4d / 3d - 2d, titleRegion.Bounds.Y * 4d / 3d + 2d);
    }

    [Fact]
    public void Convert_SemanticTextMode_GroupsMixedSizeSameBaselineTitleRuns()
    {
        using PDDocument document = CreateMixedSizeSameBaselineTitleDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = false
        });
        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        PdfTextLine largeCapLine = Assert.Single(layoutPage.Lines, line =>
            line.Runs.Any(static run => run.FontSize == 22f));
        PdfTextLine[] sourceTitleLines = layoutPage.Lines
            .Where(line => MathF.Abs(line.Bounds.Bottom - largeCapLine.Bounds.Bottom) <= 1f)
            .ToArray();
        Assert.Equal(2, sourceTitleLines.Length);
        Assert.Equal([16f, 22f], sourceTitleLines
            .SelectMany(static line => line.Runs)
            .Select(static run => run.FontSize)
            .Distinct()
            .Order()
            .ToArray());

        PdfSemanticPage semanticPage = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement title = Assert.Single(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.Text == "ADAM: A METHOD FOR STOCHASTIC OPTIMIZATION");

        Assert.Equal(1, title.HeadingLevel);
        Assert.Single(title.Lines);
        Assert.Equal([16f, 22f], title.Lines[0].Runs.Select(static run => run.FontSize).Distinct().Order().ToArray());
        Assert.Contains(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading && element.Text == "STACKED HEADING ONE");
        Assert.Contains(semanticPage.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading && element.Text == "STACKED HEADING TWO");

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);
        XElement titleHeading = Assert.Single(dom.Descendants("h1"), heading =>
            HasClass(heading, "pdf-semantic-title") &&
            heading.Value == "ADAM: A METHOD FOR STOCHASTIC OPTIMIZATION");

        Assert.Equal("ADAM: A METHOD FOR STOCHASTIC OPTIMIZATION", titleHeading.Value);
        Assert.True(HasClass(titleHeading, "pdf-font-size-22"));
        Assert.Contains(titleHeading.Descendants(), element => HasClass(element, "pdf-font-size-16"));
        Assert.DoesNotContain("A DAM", titleHeading.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("S TOCHASTIC", titleHeading.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("O PTIMIZATION", titleHeading.Value, StringComparison.Ordinal);
        Assert.Single(dom.Descendants("h1"), heading => heading.Value == "STACKED HEADING ONE");
        Assert.Single(dom.Descendants("h1"), heading => heading.Value == "STACKED HEADING TWO");
    }

    [Fact]
    public void Convert_TransparencyGroups_EmitNestedSvgOpacity()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfLayoutPage attentionVisualizationPage = layout.Pages[12];
        Assert.Contains(attentionVisualizationPage.VectorGroups, group => group.Opacity < 0.1f);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });

        Assert.Contains("<g data-vector-group-index=\"", html.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"pdf-vector-group\"", html.Html, StringComparison.Ordinal);
        Assert.Contains("Attention Visualizations", html.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Input-Input Layer5", html.Html, StringComparison.Ordinal);
        float[] groupOpacities = Regex.Matches(html.Html, "<g data-vector-group-index=\"[^\"]+\" opacity=\"(?<opacity>[^\"]+)\"")
            .Select(match => float.Parse(match.Groups["opacity"].Value, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Contains(groupOpacities, opacity => opacity < 0.1f);
        XDocument dom = ParseHtml(html.Html);
        XElement attentionVisualization = Assert.Single(ElementsByClass(dom, "pdf-semantic-figure"), figure =>
            figure.Attribute("data-source-page")?.Value == "13");
        Assert.Contains(attentionVisualization.Descendants(), element =>
            element.Name.LocalName == "g" &&
            element.Attribute("data-vector-group-index") != null &&
            element.Attribute("opacity")?.Value == "0" &&
            element.Descendants().Any(path =>
                path.Name.LocalName == "path" &&
                path.Attribute("fill")?.Value == "#D3D3D3"));
        Assert.Contains(attentionVisualization.Descendants(), element =>
            element.Name.LocalName == "g" &&
            element.Attribute("data-vector-group-index") != null &&
            element.Attribute("opacity")?.Value == "0.533" &&
            element.Descendants().Any(path =>
                path.Name.LocalName == "path" &&
                path.Attribute("fill")?.Value == "#E377C2"));
        Assert.Contains(attentionVisualization.Descendants(), element =>
            element.Name.LocalName == "g" &&
            element.Attribute("data-vector-group-index") != null &&
            element.Attribute("clip-path")?.Value.StartsWith("url(#pdf-vector-figure-13-", StringComparison.Ordinal) == true);
        Assert.Contains(attentionVisualization.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Descendants().Any(path =>
                path.Name.LocalName == "path" &&
                path.Attribute("d")?.Value.Contains("M 108 100.787", StringComparison.Ordinal) == true &&
                path.Attribute("d")?.Value.Contains("L 504.005 302.06", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_EmitsSoftPageMarkersInsteadOfFixedPages()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = false
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(ElementsByClass(dom, "pdf-page"));
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-page"));
        Assert.Contains("pdf-document-continuous", dom.Descendants("body").Single().Attribute("class")?.Value);
        Assert.Contains(".pdf-semantic-page-break", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-continuous-flow .pdf-semantic-page-break::after", html.Css, StringComparison.Ordinal);
        Assert.Contains("--pdf-page-width: min(612pt, calc(100vw - 48pt))", html.Css, StringComparison.Ordinal);
        Assert.Contains("--pdf-page-corner-shadow: 22pt", html.Css, StringComparison.Ordinal);
        Assert.Contains("background: var(--pdf-page-surround)", html.Css, StringComparison.Ordinal);
        Assert.Contains("radial-gradient(ellipse at right top", html.Css, StringComparison.Ordinal);
        Assert.Contains("calc(100% - var(--pdf-page-shadow-mask) - var(--pdf-page-shadow-mask) - var(--pdf-page-corner-shadow) - var(--pdf-page-corner-shadow))", html.Css, StringComparison.Ordinal);
        Assert.Contains("text-align-last: center", html.Css, StringComparison.Ordinal);
        Assert.Contains("break-before: page", html.Css, StringComparison.Ordinal);

        XElement documentFlow = Assert.Single(ElementsByClass(dom, "pdf-semantic-document-flow"));
        XElement verticalHeader = Assert.Single(dom.Descendants("header"), header =>
            header.Value.Contains("arXiv:1706.03762v7", StringComparison.Ordinal));
        Assert.Contains("pdf-semantic-positioned", verticalHeader.Attribute("class")?.Value);
        Assert.Contains("pdf-semantic-vertical", verticalHeader.Attribute("class")?.Value);
        Dictionary<string, string> verticalStyle = ParseStyle(verticalHeader.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePoints(verticalStyle["left"]), 18f, 24f);
        Assert.InRange(ParsePoints(verticalStyle["top"]), 550f, 590f);

        XElement article = Assert.Single(documentFlow.Elements("article"), element =>
            HasClass(element, "pdf-semantic-flow") &&
            HasClass(element, "pdf-semantic-continuous-flow"));
        XElement[] authorBlocks = ElementsByClass(dom, "pdf-semantic-author-block").ToArray();
        Assert.Equal(8, authorBlocks.Length);
        Assert.DoesNotContain(authorBlocks, author =>
            author.Attribute("class")?.Value.Contains("pdf-font-", StringComparison.Ordinal) == true ||
            author.Attribute("class")?.Value.Contains("pdf-color-", StringComparison.Ordinal) == true);
        Assert.Contains(authorBlocks, author => author.Descendants("span").Any(span =>
            span.Attribute("class")?.Value.Contains("pdf-font-", StringComparison.Ordinal) == true));
        XElement[] pageBreaks = ElementsByClass(dom, "pdf-semantic-page-break").ToArray();
        Assert.Equal(layout.Pages.Count, pageBreaks.Length);
        Assert.Equal("page-1", pageBreaks[0].Attribute("id")?.Value);
        Assert.Contains("pdf-semantic-page-start", pageBreaks[0].Attribute("class")?.Value);
        Assert.Equal("1", pageBreaks[0].Attribute("data-page-number")?.Value);
        Assert.Equal("page-2", pageBreaks[1].Attribute("id")?.Value);
        Assert.Equal("2", pageBreaks[1].Attribute("data-page-number")?.Value);

        XElement[] articleContent = article.Descendants().ToArray();
        int pageTwoBreakIndex = Array.IndexOf(articleContent, pageBreaks[1]);
        int introductionIndex = Array.FindIndex(articleContent, element =>
            element.Name.LocalName == "h1" &&
            element.Value == "1 Introduction");
        Assert.True(pageTwoBreakIndex >= 0 && introductionIndex > pageTwoBreakIndex);

        XElement introductionSection = Assert.Single(article.Descendants("section"), element =>
            element.Attribute("id")?.Value == "section-1-introduction");
        XElement backgroundSection = Assert.Single(article.Descendants("section"), element =>
            element.Attribute("id")?.Value == "section-2-background");
        XElement architectureSection = Assert.Single(article.Descendants("section"), element =>
            element.Attribute("id")?.Value == "section-3-model-architecture");
        Assert.Same(article, introductionSection.Parent);
        Assert.Same(article, backgroundSection.Parent);
        Assert.Same(article, architectureSection.Parent);
        Assert.Equal("heading-1-introduction", introductionSection.Attribute("aria-labelledby")?.Value);
        Assert.Equal("heading-1-introduction", introductionSection.Element("h1")?.Attribute("id")?.Value);
        Assert.Contains(pageBreaks[1].Ancestors(), ancestor => ReferenceEquals(ancestor, introductionSection));
        XElement encoderSection = Assert.Single(architectureSection.Descendants("section"), element =>
            element.Attribute("id")?.Value == "section-3-1-encoder-and-decoder-stacks");
        Assert.Same(architectureSection, encoderSection.Parent);
        Assert.Equal("h2", encoderSection.Elements().First().Name.LocalName);

        XElement abstractHeading = Assert.Single(article.Descendants("h2"), element => element.Value == "Abstract");
        Assert.Contains("pdf-semantic-align-center", abstractHeading.Attribute("class")?.Value);
        Assert.Contains(ElementsByClass(dom, "pdf-semantic-footer"), footer =>
            footer.Value.Contains("31st Conference", StringComparison.Ordinal));
        Assert.Contains(ElementsByClass(dom, "pdf-semantic-footer"), footer => footer.Value == "2");
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesClippedSideBySideCompositeGeometry()
    {
        using PDDocument document = CreateSideBySideImageDocument(includeCaption: true, clipSecondImage: true);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement figure = Assert.Single(ElementsByClass(dom, "pdf-semantic-figure"));
        Assert.Equal("222pt", ParseStyle(figure.Attribute("style")?.Value ?? "")["--pdf-semantic-figure-width"]);
        XElement svg = Assert.Single(figure.Descendants(), element => HasClass(element, "pdf-semantic-figure-svg"));
        Assert.Equal("0 0 222 90", svg.Attribute("viewBox")?.Value);
        XElement content = Assert.Single(svg.Elements(), element => element.Name.LocalName == "g");
        Assert.Equal("translate(-72 -282)", content.Attribute("transform")?.Value);
        XElement[] images = content.Elements().Where(static element => element.Name.LocalName == "image").ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal("72", images[0].Attribute("x")?.Value);
        Assert.Equal("204", images[1].Attribute("x")?.Value);
        Assert.Equal("180", images[1].Attribute("width")?.Value);
        Assert.Equal("120", images[1].Attribute("height")?.Value);
        Assert.Equal("url(#pdf-semantic-image-page-1-clip-1)", images[1].Attribute("clip-path")?.Value);
        XElement clipPath = Assert.Single(svg.Descendants(), element =>
            element.Name.LocalName == "clipPath" &&
            element.Attribute("id")?.Value == "pdf-semantic-image-page-1-clip-1");
        Assert.Contains(clipPath.Descendants(), element =>
            element.Name.LocalName == "path" &&
            element.Attribute("d")?.Value.Contains("204", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_KeepsPageBreakInsideContinuingSection()
    {
        using PDDocument document = CreateContinuingSectionDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfSemanticDocument semantic = PdfSemanticExtractor.Extract(layout);
        PdfSemanticElement extractedHeading = Assert.Single(semantic.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading);
        Assert.Equal(1, extractedHeading.HeadingLevel);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement article = Assert.Single(ElementsByClass(dom, "pdf-semantic-continuous-flow"));
        XElement section = Assert.Single(ElementsByClass(dom, "pdf-semantic-section"));
        XElement heading = Assert.Single(section.Elements("h1"));
        XElement pageTwoBreak = Assert.Single(ElementsByClass(dom, "pdf-semantic-page-break"), element =>
            element.Attribute("data-page-number")?.Value == "2");
        Assert.Same(article, section.Parent);
        Assert.Equal("section-1-continuing-section", section.Attribute("id")?.Value);
        Assert.Equal("heading-1-continuing-section", heading.Attribute("id")?.Value);
        Assert.Contains(pageTwoBreak.Ancestors(), ancestor => ReferenceEquals(ancestor, section));
        Assert.Contains("Second-page content", section.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesCaptionedImageGridGeometry()
    {
        using PDDocument document = CreateGridImageDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement figure = Assert.Single(ElementsByClass(dom, "pdf-semantic-figure"));
        XElement svg = Assert.Single(figure.Descendants(), element => HasClass(element, "pdf-semantic-figure-svg"));
        Assert.Equal("0 0 168 128", svg.Attribute("viewBox")?.Value);
        XElement content = Assert.Single(svg.Elements(), element => element.Name.LocalName == "g");
        Assert.Equal("translate(-150 -252)", content.Attribute("transform")?.Value);
        XElement[] images = content.Elements().Where(static element => element.Name.LocalName == "image").ToArray();
        Assert.Equal(4, images.Length);
        Assert.Equal(["150", "238", "150", "238"], images.Select(image => image.Attribute("x")?.Value));
        Assert.Equal(["252", "252", "320", "320"], images.Select(image => image.Attribute("y")?.Value));
        Assert.All(images, image =>
        {
            Assert.Equal("80", image.Attribute("width")?.Value);
            Assert.Equal("60", image.Attribute("height")?.Value);
        });
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_DoesNotGroupNearbyUncaptionedBodyImages()
    {
        using PDDocument document = CreateSideBySideImageDocument(includeCaption: false, clipSecondImage: false);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement[] figures = ElementsByClass(dom, "pdf-semantic-figure").ToArray();
        Assert.Equal(2, figures.Length);
        Assert.All(figures, figure =>
            Assert.Single(figure.Descendants(), static element => element.Name.LocalName == "image"));
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_GroupsFullWidthBertArchitectureFigure()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("acl-bert-page-3.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfLayoutPage sourcePage = Assert.Single(layout.Pages);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement figure = Assert.Single(ElementsByClass(dom, "pdf-semantic-figure"), element =>
            element.Attribute("data-source-page")?.Value == "1");
        Assert.True(HasClass(figure, "pdf-semantic-column-spanning-figure"));
        Assert.Equal("444.283pt", ParseStyle(figure.Attribute("style")?.Value ?? "")["--pdf-semantic-figure-width"]);

        XElement svg = Assert.Single(figure.Elements(), element => HasClass(element, "pdf-semantic-figure-svg"));
        Assert.Equal("0 0 444.283 178.185", svg.Attribute("viewBox")?.Value);
        XElement content = Assert.Single(svg.Elements(), element => element.Name.LocalName == "g");
        Assert.Equal("translate(-76.859 -64.196)", content.Attribute("transform")?.Value);
        Assert.Equal(sourcePage.Images.Count, svg.Descendants("image").Count());
        Assert.Contains(svg.Descendants("path"), element => element.Attribute("data-path-index") != null);

        XElement preTraining = Assert.Single(svg.Descendants("text"), element => element.Value == "Pre-training");
        XElement fineTuning = Assert.Single(svg.Descendants("text"), element => element.Value == "Fine-Tuning");
        XElement startEndSpan = Assert.Single(svg.Descendants("text"), element => element.Value == "Start/End Span");
        Assert.Equal("139.209", preTraining.Attribute("x")?.Value);
        Assert.Equal("389.17", fineTuning.Attribute("x")?.Value);
        Assert.Equal("240.696", preTraining.Attribute("y")?.Value);
        Assert.Equal(preTraining.Attribute("y")?.Value, fineTuning.Attribute("y")?.Value);
        Assert.Equal("83.761", startEndSpan.Attribute("y")?.Value);
        Assert.Equal("9.226pt", ParseStyle(preTraining.Attribute("style")?.Value ?? "")["font-size"]);
        Assert.Equal("9.226pt", ParseStyle(fineTuning.Attribute("style")?.Value ?? "")["font-size"]);
        Assert.Equal("5.382pt", ParseStyle(startEndSpan.Attribute("style")?.Value ?? "")["font-size"]);

        XElement caption = Assert.Single(figure.Elements("figcaption"));
        Assert.Contains("Figure 1: Overall pre-training and", caption.Value, StringComparison.Ordinal);
        Assert.Contains("all parameters are ﬁne-tuned.", caption.Value, StringComparison.Ordinal);
        Assert.Contains("questions/answers).", caption.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-column-run"), run =>
            run.Value.Contains("Figure 1:", StringComparison.Ordinal) ||
            run.Value == "Pre-training" ||
            run.Value == "Fine-Tuning");

        XElement[] proseColumns = figure.Parent!
            .Elements()
            .Where(element => HasClass(element, "pdf-semantic-column"))
            .ToArray();
        Assert.Equal(2, proseColumns.Length);
        Assert.Contains("2.3 Transfer Learning from Supervised Data", proseColumns[0].Value, StringComparison.Ordinal);
        Assert.Contains("Model Architecture", proseColumns[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_SemanticContinuousFlow_PreservesFiguresAndCrossPageParagraphContinuations()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeLinks = false
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument dom = ParseHtml(html.Html);

        XElement[] figures = ElementsByClass(dom, "pdf-semantic-figure").ToArray();
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-thematic-break"));
        Assert.Empty(dom.Descendants("hr"));
        Assert.True(figures.Length >= 2);
        Assert.Contains(figures, figure => figure.Attribute("data-source-page")?.Value == "3");
        Assert.Contains(figures, figure => figure.Attribute("data-source-page")?.Value == "4");
        Assert.Contains(figures, figure => figure.Attribute("data-source-page")?.Value == "13");
        Assert.True(ElementsByClass(dom, "pdf-semantic-figure-svg").Count() >= 2);
        XElement[] svgImages = dom.Descendants().Where(static element => element.Name.LocalName == "image").ToArray();
        Assert.Contains(svgImages, image => image.Attribute("href")?.Value == "assets/images/page-4-image-0.png");
        Assert.Contains(svgImages, image => image.Attribute("href")?.Value == "assets/images/page-4-image-1.png");
        XElement attentionVisualization = Assert.Single(figures, figure => figure.Attribute("data-source-page")?.Value == "13");
        XElement[] attentionVisualizationLabels = attentionVisualization.Descendants()
            .Where(static element => element.Name.LocalName == "text" && HasClass(element, "pdf-semantic-figure-text"))
            .ToArray();
        Assert.Contains(attentionVisualizationLabels, label => label.Value == "making");
        Assert.Contains(attentionVisualizationLabels, label => label.Value == "registration");
        Assert.Contains(attentionVisualizationLabels, label =>
            label.Attribute("transform")?.Value.Contains("rotate", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("In A tt p en u", StringComparison.Ordinal));
        XElement figureThreeCaption = Assert.Single(ElementsByClass(dom, "pdf-semantic-caption"), caption =>
            caption.Value.StartsWith("Figure 3:", StringComparison.Ordinal));
        Assert.Equal("figcaption", figureThreeCaption.Name.LocalName);
        Assert.Contains("Best viewed in color.", figureThreeCaption.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("registration registration", figureThreeCaption.Value, StringComparison.Ordinal);
        XElement figureFiveCaption = Assert.Single(ElementsByClass(dom, "pdf-semantic-caption"), caption =>
            caption.Value.StartsWith("Figure 5:", StringComparison.Ordinal));
        Assert.Equal("figcaption", figureFiveCaption.Name.LocalName);
        Assert.Contains("figcaption.pdf-semantic-caption", html.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("Input-Input Layer5", dom.Root?.Value ?? "", StringComparison.Ordinal);
        Assert.Empty(ElementsByClass(dom, "pdf-semantic-figure-space"));
        Assert.Contains(".pdf-semantic-formula", html.Css, StringComparison.Ordinal);
        Assert.Contains(
            ".pdf-semantic-formula.pdf-semantic-formula-native",
            html.Css,
            StringComparison.Ordinal);
        Assert.Contains("max-width: calc(100% - 3em);", html.Css, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto;", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-formula-run", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-formula-vector-layer", html.Css, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf-semantic-inline-run", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-inline-fraction", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-math", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-italic", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-formula-radical", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-formula-attached-suffix", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-inline-summation", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-font-cmr10{", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-font-size-9{", html.Css, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf-font-cmr6{", html.Css, StringComparison.Ordinal);
        Assert.DoesNotContain(".pdf-font-size-7{", html.Css, StringComparison.Ordinal);

        XElement continuedParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-page-spanning"), paragraph =>
            paragraph.Value.StartsWith("An attention function can be described", StringComparison.Ordinal));
        Assert.Equal("p", continuedParagraph.Name.LocalName);
        Assert.Contains("The output is computed as a weighted sum", continuedParagraph.Value, StringComparison.Ordinal);
        Assert.Contains("of the values, where the weight assigned to each value", continuedParagraph.Value, StringComparison.Ordinal);
        Assert.Contains("corresponding key.", continuedParagraph.Value, StringComparison.Ordinal);
        XElement pageFourBreak = Assert.Single(continuedParagraph.Elements(), element =>
            HasClass(element, "pdf-semantic-inline-page-break") &&
            element.Attribute("data-page-number")?.Value == "4");
        XElement pageThreeFooter = Assert.Single(continuedParagraph.Elements(), element =>
            HasClass(element, "pdf-semantic-footer") &&
            element.Value == "3");
        Assert.Contains("pdf-semantic-inline-flow-element", pageThreeFooter.Attribute("class")?.Value);
        Assert.Contains("pdf-semantic-align-center", pageThreeFooter.Attribute("class")?.Value);
        XElement[] continuedParagraphChildren = continuedParagraph.Elements().ToArray();
        Assert.True(
            Array.IndexOf(continuedParagraphChildren, pageThreeFooter) <
            Array.IndexOf(continuedParagraphChildren, pageFourBreak));
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-footer"), footer =>
            footer.Value == "3" &&
            footer.Parent != continuedParagraph);
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            !HasClass(paragraph, "pdf-semantic-page-spanning") &&
            paragraph.Value.StartsWith("of the values, where the weight assigned", StringComparison.Ordinal));

        XElement pageFourFootnote = Assert.Single(ElementsByClass(dom, "pdf-semantic-footnote"), footnote =>
            footnote.Attribute("id")?.Value == "page-4-fn-4");
        Assert.Contains("To illustrate why the dot products get large", pageFourFootnote.Value, StringComparison.Ordinal);
        Assert.Contains("∑", pageFourFootnote.Value, StringComparison.Ordinal);
        Assert.Contains(pageFourFootnote.Descendants("sub"), subscript =>
            subscript.Value == "i" &&
            HasClass(subscript, "pdf-semantic-math"));
        Assert.Contains(pageFourFootnote.Descendants("sub"), subscript =>
            subscript.Value == "k" &&
            HasClass(subscript, "pdf-semantic-math"));
        XElement summation = Assert.Single(pageFourFootnote.Descendants(), element =>
            HasClass(element, "pdf-semantic-inline-summation"));
        Assert.Contains("∑", summation.Value, StringComparison.Ordinal);
        Assert.Contains("i=1", summation.Value, StringComparison.Ordinal);
        Assert.Contains(summation.Descendants("sub"), subscript => subscript.Value == "k");
        Assert.True(
            html.Html.IndexOf("id=\"page-4-fn-4\"", StringComparison.Ordinal) <
            html.Html.IndexOf("id=\"page-5\"", StringComparison.Ordinal));
        Assert.Contains(dom.Descendants("a"), link =>
            link.Attribute("href")?.Value == "#page-4-fn-4" &&
            link.Value == "4");
        XElement pageFourFooter = Assert.Single(ElementsByClass(dom, "pdf-semantic-footer"), footer =>
            footer.Value == "4");
        Assert.Empty(pageFourFooter.Descendants("a"));

        XElement multiHeadIntro = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("Instead of performing a single attention function", StringComparison.Ordinal));
        Assert.DoesNotContain("pdf-semantic-measured-width", multiHeadIntro.Attribute("class")?.Value ?? "");

        XElement formula = Assert.Single(ElementsByClass(dom, "pdf-semantic-formula"), element =>
            element.Attribute("aria-label")?.Value.Contains("MultiHead", StringComparison.Ordinal) == true);
        Assert.Equal("div", formula.Name.LocalName);
        Assert.Equal("math", formula.Attribute("role")?.Value);
        Dictionary<string, string> formulaStyle = ParseStyle(formula.Attribute("style")?.Value ?? "");
        Assert.True(ParsePoints(formulaStyle["--pdf-semantic-formula-width"]) > 150f);
        Assert.True(ParsePoints(formulaStyle["--pdf-semantic-formula-height"]) > 30f);
        XElement[] formulaRuns = formula.Elements().Where(static element =>
            HasClass(element, "pdf-semantic-formula-run")).ToArray();
        Assert.NotEmpty(formulaRuns);
        Assert.Empty(formula.Elements("math"));
        Assert.Contains("MultiHead", string.Concat(formulaRuns.Select(static run => run.Value)), StringComparison.Ordinal);
        Assert.DoesNotContain("Where", string.Concat(formulaRuns.Select(static run => run.Value)), StringComparison.Ordinal);
        Assert.Contains(formulaRuns, run =>
            run.Value == "1" && HasClass(run, "pdf-semantic-formula-attached-suffix"));
        Assert.Contains(formulaRuns, run =>
            run.Value == "i" && HasClass(run, "pdf-semantic-formula-attached-suffix"));
        Assert.DoesNotContain("In this work", formula.Value, StringComparison.Ordinal);
        Assert.Contains(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("Where the projections", StringComparison.Ordinal));

        XElement attentionFormula = Assert.Single(ElementsByClass(dom, "pdf-semantic-formula"), element =>
            element.Attribute("aria-label")?.Value.Contains("Attention(Q, K, V)", StringComparison.Ordinal) == true);
        Assert.Contains(attentionFormula.Descendants(), element =>
            HasClass(element, "pdf-semantic-formula-vector-layer"));
        Assert.Contains(attentionFormula.Descendants(), element =>
            HasClass(element, "pdf-semantic-formula-radical") &&
            element.Value == "√");
        Assert.Contains(attentionFormula.Descendants(), element =>
            element.Name.LocalName == "path");
        Assert.Contains("√", attentionFormula.Value, StringComparison.Ordinal);
        Assert.Contains("dk", attentionFormula.Value, StringComparison.Ordinal);

        XElement attentionCostParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("In this work we employ h = 8", StringComparison.Ordinal));
        Assert.DoesNotContain("pdf-semantic-formula", attentionCostParagraph.Attribute("class")?.Value ?? "");
        Assert.DoesNotContain("pdf-semantic-measured-width", attentionCostParagraph.Attribute("class")?.Value ?? "");
        Assert.Contains("single-head attention with full dimensionality.", attentionCostParagraph.Value, StringComparison.Ordinal);

        XElement sequenceParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("symbol representations", StringComparison.Ordinal) &&
            paragraph.Value.Contains("continuous representations", StringComparison.Ordinal));
        Assert.Contains(sequenceParagraph.Descendants("sub"), subscript =>
            subscript.Value == "1" &&
            HasClass(subscript, "pdf-semantic-math"));
        Assert.Contains(sequenceParagraph.Descendants("sub"), subscript =>
            subscript.Value == "n" &&
            HasClass(subscript, "pdf-semantic-math"));

        XElement selfAttentionComparisonParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("In this section we compare various aspects of self-attention", StringComparison.Ordinal));
        Assert.Contains("such as a hidden layer in a typical sequence transduction encoder", selfAttentionComparisonParagraph.Value, StringComparison.Ordinal);

        XElement encoderParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("The encoder is composed of a stack of N = 6 identical layers.", StringComparison.Ordinal));
        Assert.Contains(encoderParagraph.Descendants(), element =>
            element.Value == "N" &&
            HasClass(element, "pdf-semantic-italic") &&
            HasClass(element, "pdf-semantic-math"));
        Assert.Contains(encoderParagraph.Descendants("sub"), subscript =>
            subscript.Value == "model");

        XElement positionalEncodingParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("relative positions", StringComparison.Ordinal) &&
            paragraph.Value.Contains("linear function", StringComparison.Ordinal));
        Assert.Contains(positionalEncodingParagraph.Descendants("sub"), subscript =>
            subscript.Value == "pos+k");
        Assert.Contains(positionalEncodingParagraph.Descendants("sub"), subscript =>
            subscript.Value == "pos");
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("pos+k can be represented", StringComparison.Ordinal));
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value == "PE pos");

        XElement scaledDotProductParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("We call our particular attention", StringComparison.Ordinal));
        Assert.Contains("and values of dimension", scaledDotProductParagraph.Value, StringComparison.Ordinal);
        Assert.Contains("divide each by √dk", scaledDotProductParagraph.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("a nd values", scaledDotProductParagraph.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("√ nd", scaledDotProductParagraph.Value, StringComparison.Ordinal);
        Assert.Contains(scaledDotProductParagraph.Descendants("sub"), subscript =>
            subscript.Value == "k" &&
            HasClass(subscript, "pdf-semantic-math"));

        XElement scalingParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("we scale the dot products by", StringComparison.Ordinal));
        Assert.Contains("for large values of dk", scalingParagraph.Value, StringComparison.Ordinal);
        XElement[] inverseSquareRootFractions = ElementsByClass(dom, "pdf-semantic-inline-fraction").ToArray();
        Assert.True(inverseSquareRootFractions.Length >= 2);
        Assert.All(inverseSquareRootFractions.Take(2), fraction =>
        {
            Assert.Contains("pdf-semantic-math", fraction.Attribute("class")?.Value);
            Assert.Contains(fraction.Descendants(), element =>
                HasClass(element, "pdf-semantic-inline-fraction-numerator") &&
                element.Value == "1");
            Assert.Contains(fraction.Descendants(), element =>
                HasClass(element, "pdf-semantic-inline-fraction-denominator") &&
                element.Value.Contains("√d", StringComparison.Ordinal));
            Assert.Contains(fraction.Descendants("sub"), subscript =>
                subscript.Value == "k");
        });
        Assert.DoesNotContain("√1", html.Html, StringComparison.Ordinal);

        XElement learningRateFormula = Assert.Single(ElementsByClass(dom, "pdf-semantic-formula"), element =>
            element.Attribute("aria-label")?.Value.Contains("lrate", StringComparison.Ordinal) == true &&
            element.Attribute("aria-label")?.Value.Contains("warmup_steps", StringComparison.Ordinal) == true);
        Assert.Equal("math", learningRateFormula.Attribute("role")?.Value);
        Assert.Contains("(3)", learningRateFormula.Value, StringComparison.Ordinal);
        Assert.Contains("warmup", learningRateFormula.Value, StringComparison.Ordinal);
        Assert.Single(ElementsByClass(dom, "pdf-semantic-formula"), element =>
            element.Attribute("aria-label")?.Value.Contains("lrate", StringComparison.Ordinal) == true);

        XElement regularizationParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("For the base model, we use a rate of", StringComparison.Ordinal));
        Assert.Contains(regularizationParagraph.Descendants(), element =>
            element.Value == "P" &&
            HasClass(element, "pdf-semantic-math"));
        Assert.Contains(regularizationParagraph.Descendants("sub"), subscript =>
            subscript.Value == "drop");

        XElement[] semanticTables = ElementsByClass(dom, "pdf-semantic-table")
            .Where(static element => element.Name.LocalName == "table")
            .ToArray();
        Assert.True(semanticTables.Length >= 2);
        XElement complexityTable = Assert.Single(semanticTables, table =>
            table.Value.Contains("Complexity per Layer", StringComparison.Ordinal) &&
            table.Value.Contains("Self-Attention", StringComparison.Ordinal));
        Assert.Contains(complexityTable.Elements("thead").Descendants("th"), header =>
            header.Value.Contains("Sequential", StringComparison.Ordinal) &&
            header.Value.Contains("Operations", StringComparison.Ordinal));
        XElement selfAttentionComplexity = Assert.Single(complexityTable.Elements("tbody").Descendants("td"), cell =>
            cell.Value.Contains("O(n2", StringComparison.Ordinal));
        Assert.Contains(selfAttentionComplexity.Descendants("sup"), superscript =>
            superscript.Value == "2" &&
            HasClass(superscript, "pdf-semantic-math"));
        Assert.Contains(complexityTable.Descendants("sub"), subscript =>
            subscript.Value == "k" &&
            HasClass(subscript, "pdf-semantic-math"));
        Assert.Contains(complexityTable.Descendants(), cell =>
            HasClass(cell, "pdf-semantic-table-cell-border-bottom") ||
            HasClass(cell, "pdf-semantic-table-cell-border-top"));
        XElement selfAttentionLabel = Assert.Single(complexityTable.Elements("tbody").Descendants("td"), cell =>
            cell.Value == "Self-Attention");
        Assert.Contains("pdf-semantic-table-cell-align-left", selfAttentionLabel.Attribute("class")?.Value);
        Assert.Contains("pdf-semantic-table-cell-align-center", selfAttentionComplexity.Attribute("class")?.Value);
        Assert.Contains(".pdf-semantic-table .pdf-semantic-table-cell-border-top", html.Css, StringComparison.Ordinal);
        Assert.Contains("border-top: 0.45pt solid currentColor", html.Css, StringComparison.Ordinal);
        Assert.Contains(".pdf-semantic-table .pdf-semantic-table-cell-border-bottom", html.Css, StringComparison.Ordinal);
        Assert.DoesNotContain(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.StartsWith("Layer Type", StringComparison.Ordinal));

        XElement bleuTable = Assert.Single(semanticTables, table =>
            table.Value.Contains("Transformer (big)", StringComparison.Ordinal) &&
            table.Value.Contains("28.4", StringComparison.Ordinal));
        XElement[] bleuHeaderRows = bleuTable.Elements("thead").Elements("tr").ToArray();
        Assert.Equal(2, bleuHeaderRows.Length);
        Assert.Equal(new[] { "Model", "BLEU", "Training Cost (FLOPs)" },
            bleuHeaderRows[0].Elements("th").Select(static header => header.Value).ToArray());
        Assert.Equal(new[] { "EN-DE", "EN-FR", "EN-DE", "EN-FR" },
            bleuHeaderRows[1].Elements("th").Select(static header => header.Value).ToArray());
        XElement[] bleuGroupHeaders = bleuHeaderRows[0].Elements("th").ToArray();
        Assert.Equal("2", bleuGroupHeaders[0].Attribute("rowspan")?.Value);
        Assert.Equal("2", bleuGroupHeaders[1].Attribute("colspan")?.Value);
        Assert.Equal("2", bleuGroupHeaders[2].Attribute("colspan")?.Value);
        Assert.All(bleuGroupHeaders, header =>
            Assert.True(HasClass(header, "pdf-semantic-table-cell-border-top")));
        Assert.False(HasClass(bleuGroupHeaders[0], "pdf-semantic-table-cell-border-bottom"));
        Assert.True(HasClass(bleuGroupHeaders[1], "pdf-semantic-table-cell-border-bottom"));
        Assert.True(HasClass(bleuGroupHeaders[2], "pdf-semantic-table-cell-border-bottom"));
        Assert.Contains(bleuTable.Elements("tbody").Descendants("td"), cell =>
            cell.Value == "ByteNet [18]");
        Assert.Contains(bleuTable.Elements("tbody").Descendants("td"), cell =>
            cell.Value == "23.75");
        Assert.Contains(bleuTable.Elements("tbody").Descendants("td"), cell =>
            cell.Value == "Transformer (big)");
        Assert.Contains(bleuTable.Descendants(), cell =>
            HasClass(cell, "pdf-semantic-table-cell-border-bottom"));
        XElement convS2SEnsembleBleu = Assert.Single(bleuTable.Elements("tbody").Descendants("td"), cell =>
            cell.Value == "41.29");
        Assert.True(HasClass(convS2SEnsembleBleu, "pdf-semantic-bold") ||
            convS2SEnsembleBleu.Descendants().Any(value => HasClass(value, "pdf-semantic-bold")));
        XElement transformerBase = Assert.Single(bleuTable.Elements("tbody").Elements("tr"), row =>
            row.Elements("td").First().Value == "Transformer (base model)");
        XElement transformerBaseCost = transformerBase.Elements("td").Last();
        Assert.True(HasClass(transformerBaseCost, "pdf-semantic-bold") ||
            transformerBaseCost.Descendants().Any(value => HasClass(value, "pdf-semantic-bold")));
        XElement transformerBig = Assert.Single(bleuTable.Elements("tbody").Elements("tr"), row =>
            row.Elements("td").First().Value == "Transformer (big)");
        Assert.All(transformerBig.Elements("td"), cell =>
            Assert.True(HasClass(cell, "pdf-semantic-table-cell-border-bottom")));
        Assert.True(HasClass(transformerBig.Elements("td").ElementAt(1), "pdf-semantic-bold") ||
            transformerBig.Elements("td").ElementAt(1).Descendants().Any(value => HasClass(value, "pdf-semantic-bold")));
        Assert.True(HasClass(transformerBig.Elements("td").ElementAt(2), "pdf-semantic-bold") ||
            transformerBig.Elements("td").ElementAt(2).Descendants().Any(value => HasClass(value, "pdf-semantic-bold")));
        Assert.DoesNotContain("border-bottom: 0.35pt solid #d1d5db", html.Css, StringComparison.Ordinal);

        XElement residualDropoutParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("Residual Dropout", StringComparison.Ordinal) &&
            paragraph.Value.Contains("For the base model", StringComparison.Ordinal));
        Assert.Contains(residualDropoutParagraph.Descendants("strong"), strong =>
            strong.Value == "Residual" &&
            HasClass(strong, "pdf-semantic-bold"));
        Assert.Contains(residualDropoutParagraph.Descendants("strong"), strong =>
            strong.Value == "Dropout" &&
            HasClass(strong, "pdf-semantic-bold"));

        XElement labelSmoothingParagraph = Assert.Single(ElementsByClass(dom, "pdf-semantic-paragraph"), paragraph =>
            paragraph.Value.Contains("Label Smoothing", StringComparison.Ordinal) &&
            paragraph.Value.Contains("label smoothing of value", StringComparison.Ordinal));
        Assert.Contains(labelSmoothingParagraph.Descendants("strong"), strong =>
            strong.Value == "Label" &&
            HasClass(strong, "pdf-semantic-bold"));
        Assert.Contains(labelSmoothingParagraph.Descendants("strong"), strong =>
            strong.Value == "Smoothing" &&
            HasClass(strong, "pdf-semantic-bold"));

        XElement variationTable = Assert.Single(semanticTables, table =>
            table.Value.Contains("Pdrop", StringComparison.Ordinal) &&
            table.Value.Contains("base", StringComparison.Ordinal) &&
            table.Value.Contains("big", StringComparison.Ordinal));
        XElement[] variationHeaderRows = variationTable.Elements("thead").Elements("tr").ToArray();
        Assert.Equal(2, variationHeaderRows.Length);
        Assert.Contains(variationHeaderRows[0].Elements("th"), header => header.Value == "train");
        Assert.Contains(variationHeaderRows[1].Elements("th"), header => header.Value == "steps");
        Assert.Contains(variationHeaderRows[0].Elements("th"), header => header.Value == "params");
        XElement parameterScaleHeader = Assert.Single(variationHeaderRows[1].Elements("th"), header =>
            header.Value.Contains("×106", StringComparison.Ordinal));
        Assert.Contains("×106", parameterScaleHeader.Value, StringComparison.Ordinal);
        Assert.Contains(parameterScaleHeader.Descendants("sup"), superscript => superscript.Value == "6");
        Assert.Contains(variationTable.Elements("tbody").Elements("tr"), row =>
            row.Elements("td").First().Value == "big" &&
            row.Elements("td").Last().Value == "213");
        Assert.Contains(variationTable.Descendants(), cell =>
            HasClass(cell, "pdf-semantic-table-cell-border-right"));

        XElement parserTable = Assert.Single(semanticTables, table =>
            table.Value.Contains("Parser", StringComparison.Ordinal) &&
            table.Value.Contains("WSJ 23 F1", StringComparison.Ordinal));
        Assert.Contains("pdf-semantic-measured-width", parserTable.Attribute("class")?.Value);
        Assert.DoesNotContain("pdf-semantic-table-centered-cells", parserTable.Attribute("class")?.Value ?? "");
        Dictionary<string, string> parserTableStyle = ParseStyle(parserTable.Attribute("style")?.Value ?? "");
        Assert.InRange(ParsePercent(parserTableStyle["--pdf-semantic-width"]), 80f, 97f);
        Assert.Equal("center", parserTableStyle["--pdf-semantic-align-self"]);
        XElement parserHeaderRow = Assert.Single(parserTable.Elements("thead").Elements("tr"));
        Assert.Equal(new[] { "Parser", "Training", "WSJ 23 F1" },
            parserHeaderRow.Elements("th").Select(static header => header.Value).ToArray());
        Assert.Contains(parserTable.Elements("tbody").Elements("tr"), row =>
            row.Elements("td").First().Value.StartsWith("Vinyals & Kaiser", StringComparison.Ordinal) &&
            row.Elements("td").Last().Value == "88.3");
        Assert.Contains(parserTable.Descendants(), cell =>
            HasClass(cell, "pdf-semantic-table-cell-border-right"));
        Assert.Contains(parserTable.Descendants(), cell =>
            HasClass(cell, "pdf-semantic-table-cell-align-center"));
    }

    [Fact]
    public async Task Convert_SemanticContinuousFlow_RendersReadableFlowInBrowser()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = false
        });
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1000,
                Height = 1400
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        ContinuousFlowMetrics metrics = await page.EvaluateAsync<ContinuousFlowMetrics>(
            """
            () => {
              const documentFlow = document.querySelector(".pdf-semantic-document-flow");
              const flow = document.querySelector(".pdf-semantic-continuous-flow");
              const markers = Array.from(document.querySelectorAll(".pdf-semantic-page-break"));
              const introduction = Array.from(document.querySelectorAll("h1"))
                .find(element => element.textContent === "1 Introduction");
              const pageThreeFooter = Array.from(document.querySelectorAll(".pdf-semantic-page-spanning > .pdf-semantic-footer"))
                .find(element => element.textContent.trim() === "3");
              const pageSixFooter = Array.from(document.querySelectorAll(".pdf-semantic-page-spanning > .pdf-semantic-footer"))
                .find(element => element.textContent.trim() === "6");
              const pageFourMarker = document.querySelector("#page-4");
              const documentBox = documentFlow.getBoundingClientRect();
              const flowBox = flow.getBoundingClientRect();
              const flowCenter = flowBox.left + flowBox.width / 2;
              const textCenterOffset = element => {
                const range = document.createRange();
                range.selectNodeContents(element);
                const textBox = range.getBoundingClientRect();
                range.detach();
                return Math.abs((textBox.left + textBox.width / 2) - flowCenter);
              };
              const childRightOverflow = Math.max(0, ...Array.from(flow.children)
                .filter(child => !child.classList.contains("pdf-semantic-page-break"))
                .map(child => child.getBoundingClientRect().right - documentBox.right));

              return {
                fixedPageCount: document.querySelectorAll(".pdf-page").length,
                markerCount: markers.length,
                documentWidth: documentBox.width,
                flowWidth: flowBox.width,
                firstMarkerTop: markers[0].getBoundingClientRect().top,
                secondMarkerTop: markers[1].getBoundingClientRect().top,
                introductionTop: introduction.getBoundingClientRect().top,
                pageThreeFooterBottom: pageThreeFooter.getBoundingClientRect().bottom,
                pageFourMarkerTop: pageFourMarker.getBoundingClientRect().top,
                pageThreeFooterCenterOffset: textCenterOffset(pageThreeFooter),
                pageSixFooterCenterOffset: textCenterOffset(pageSixFooter),
                childRightOverflow
              };
            }
            """);

        Assert.Equal(0, metrics.FixedPageCount);
        Assert.Equal(layout.Pages.Count, metrics.MarkerCount);
        Assert.InRange(metrics.DocumentWidth, 780, 840);
        Assert.InRange(metrics.FlowWidth, 500, 540);
        Assert.True(metrics.SecondMarkerTop > metrics.FirstMarkerTop);
        Assert.True(metrics.IntroductionTop > metrics.SecondMarkerTop);
        Assert.True(
            metrics.PageThreeFooterBottom <= metrics.PageFourMarkerTop + 1.0,
            $"Page 3 footer renders below the page 4 marker by {metrics.PageThreeFooterBottom - metrics.PageFourMarkerTop:0.###} CSS pixels.");
        Assert.True(
            metrics.PageThreeFooterCenterOffset <= 1.0,
            $"Page 3 footer text is {metrics.PageThreeFooterCenterOffset:0.###} CSS pixels away from center.");
        Assert.True(
            metrics.PageSixFooterCenterOffset <= 1.0,
            $"Page 6 footer text is {metrics.PageSixFooterCenterOffset:0.###} CSS pixels away from center.");
        Assert.True(
            metrics.ChildRightOverflow <= 1.0,
            $"Continuous semantic flow extends {metrics.ChildRightOverflow:0.###} CSS pixels outside the document column.");
    }

    [Fact]
    public async Task Convert_SemanticTextMode_DoesNotClipArxivPageFlow()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true,
            IncludeLinks = false
        });
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic
        });

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1200,
                Height = 1400
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        string[] captionAlignments = await page.EvaluateAsync<string[]>(
            """
            () => Array.from(document.querySelectorAll("caption.pdf-semantic-table-caption"))
              .filter(caption => /^Table [1-4]:/.test(caption.textContent.trim()))
              .map(caption => {
                const label = caption.textContent.trim().match(/^Table \d+:/)[0];
                const sourcePage = caption.closest(".pdf-semantic-page").dataset.pageNumber;
                const content = caption.querySelector(".pdf-semantic-table-caption-content");
                return `${label}|page-${sourcePage}|${getComputedStyle(caption).textAlign}|${getComputedStyle(content).textAlign}`;
              })
            """);
        Assert.Equal(
            [
                "Table 1:|page-6|left|left",
                "Table 2:|page-8|left|left",
                "Table 3:|page-9|left|left",
                "Table 4:|page-10|left|left"
            ],
            captionAlignments);

        double[] overflows = await page.EvaluateAsync<double[]>(
            """
            () => Array.from(document.querySelectorAll(".pdf-semantic-page"))
              .slice(0, 7)
              .map(page => {
                const flow = page.querySelector(".pdf-semantic-flow");
                if (!flow) {
                  return 0;
                }

                const pageBottom = page.getBoundingClientRect().bottom;
                const children = Array.from(flow.children);
                return Math.max(0, ...children.map(child => child.getBoundingClientRect().bottom - pageBottom));
              })
            """);

        string[] overflowDetails = await page.EvaluateAsync<string[]>(
            """
            () => Array.from(document.querySelectorAll(".pdf-semantic-page"))
              .slice(0, 7)
              .map((page, index) => {
                const flow = page.querySelector(".pdf-semantic-flow");
                if (!flow) {
                  return `page ${index + 1}: no semantic flow`;
                }

                const pageBottom = page.getBoundingClientRect().bottom;
                const child = Array.from(flow.children)
                  .map(child => ({ child, box: child.getBoundingClientRect() }))
                  .sort((left, right) => right.box.bottom - left.box.bottom)[0];
                if (!child) {
                  return `page ${index + 1}: no flow children`;
                }

                const classes = child.child.getAttribute("class") || child.child.tagName.toLowerCase();
                const text = (child.child.textContent || "").trim().replace(/\s+/g, " ").slice(0, 120);
                return `page ${index + 1}: ${classes}; bottom=${child.box.bottom.toFixed(3)}; pageBottom=${pageBottom.toFixed(3)}; text=${text}`;
              })
            """);

        Assert.True(overflows.Length >= 7);
        Assert.Equal(overflows.Length, overflowDetails.Length);
        for (int index = 0; index < overflows.Length; index++)
        {
            Assert.True(
                overflows[index] <= 1.0,
                $"Semantic flow on page {index + 1} extends {overflows[index]:0.###} CSS pixels below the page. {overflowDetails[index]}");
        }
    }

    [Fact]
    public void Convert_ForegroundBoxMaskMatchesLayoutRuns()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Box mask) Tj
            0 -24 Td
            (Second) Tj
            ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        PdfLayoutRectangle[] expected = layout.Pages[0].Runs.Select(run => run.Bounds).ToArray();
        PdfLayoutRectangle[] actual = ElementsByClass(dom, "pdf-text-run")
            .Select(span => RectangleFromStyle(ParseStyle(span.Attribute("style")?.Value ?? "")))
            .ToArray();

        Assert.Equal(expected.Length, actual.Length);
        Assert.True(ForegroundIntersectionOverUnion(expected, actual) >= 0.995);
    }

    [Fact]
    public void Convert_4ppHighlightingFixtureUsesFittedTextForCompressedGlyphBoxes()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("4PP-Highlighting.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement[] fittedRuns = ElementsByClass(dom, "pdf-text-run-svg").ToArray();
        XElement[] textRuns = ElementsByClass(dom, "pdf-text-run").ToArray();

        Assert.NotEmpty(fittedRuns);
        Assert.Equal(layout.Pages[0].Runs.Count, textRuns.Length);
        Assert.True(TextCoverage(layout.Text, dom.Root?.Value ?? "") >= 0.99);
        foreach (XElement textRun in textRuns.Take(8))
        {
            Dictionary<string, string> style = ParseStyle(textRun.Attribute("style")?.Value ?? "");
            Assert.True(ParsePoints(style["font-size"]) < 6);
        }
    }

    [Fact]
    public void Convert_UsesMeasuredSvgTextForUnknownBrowserFontMetrics()
    {
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutRectangle textBounds = new(72f, 80f, 180f, 16f);
        PdfTextGlyph glyph = new("Custom display title", "SubsetDisplayFont", 20f, 0f, textBounds, black);
        PdfTextRun run = new("Custom display title", "SubsetDisplayFont", 20f, 0f, textBounds, black, [glyph]);
        PdfTextLine line = new(run.Text, textBounds, [run]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [glyph],
            [run],
            [line],
            [new PdfTextBlock(run.Text, textBounds, [line])],
            [],
            [],
            [],
            [],
            [],
            []);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));
        XDocument dom = ParseHtml(html.Html);

        XElement fittedRun = Assert.Single(ElementsByClass(dom, "pdf-text-run"));
        XElement fittedText = Assert.Single(fittedRun.Descendants(), element => HasClass(element, "pdf-text-run-svg"))
            .Descendants()
            .Single(element => element.Name.LocalName == "text");
        Assert.Equal("180", fittedText.Attribute("textLength")?.Value);
        Assert.Equal("spacingAndGlyphs", fittedText.Attribute("lengthAdjust")?.Value);
        Dictionary<string, string> style = ParseStyle(fittedRun.Attribute("style")?.Value ?? "");
        Assert.Equal(20f, ParsePoints(style["font-size"]));
    }

    [Fact]
    public void Convert_UsesPdfRunWidthForExportedBrowserFont()
    {
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutRectangle leadingBounds = new(12f, 80f, 60f, 12f);
        PdfLayoutRectangle textBounds = new(72f, 80f, 140f, 12f);
        PdfLayoutRectangle followingBounds = new(212f, 80f, 50f, 12f);
        PdfTextGlyph leadingGlyph = new("Leading ", "SubsetEmbedded-Regular", 12f, 0f, leadingBounds, black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextGlyph glyph = new("Embedded bold segment", "SubsetEmbedded-Bold", 12f, 0f, textBounds, black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextGlyph followingGlyph = new(" trailing", "SubsetEmbedded-Regular", 12f, 0f, followingBounds, black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextRun leadingRun = new(leadingGlyph.Text, leadingGlyph.FontName, leadingGlyph.FontSize, 0f, leadingBounds, black, [leadingGlyph]);
        PdfTextRun run = new(glyph.Text, glyph.FontName, glyph.FontSize, 0f, textBounds, black, [glyph]);
        PdfTextRun followingRun = new(followingGlyph.Text, followingGlyph.FontName, followingGlyph.FontSize, 0f, followingBounds, black, [followingGlyph]);
        PdfLayoutRectangle lineBounds = new(12f, 80f, 250f, 12f);
        PdfTextLine line = new(leadingRun.Text + run.Text + followingRun.Text, lineBounds, [leadingRun, run, followingRun]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [leadingGlyph, glyph, followingGlyph],
            [leadingRun, run, followingRun],
            [line],
            [new PdfTextBlock(run.Text, textBounds, [line])],
            [],
            [],
            [],
            [],
            [],
            []);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(new PdfLayoutDocument([page], [])).Html);

        XElement fittedRun = Assert.Single(
            ElementsByClass(dom, "pdf-text-run"),
            element => element.Attribute("data-font")?.Value == "SubsetEmbedded-Bold");
        XElement fittedText = Assert.Single(fittedRun.Descendants(), element => HasClass(element, "pdf-text-run-svg"))
            .Descendants()
            .Single(element => element.Name.LocalName == "text");
        Assert.Equal("140", fittedText.Attribute("textLength")?.Value);
        Assert.Equal("spacingAndGlyphs", fittedText.Attribute("lengthAdjust")?.Value);
        Assert.Contains("font-weight:700", fittedText.Attribute("style")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_FittedEmbeddedFontRunsUseFontSizedViewportWithPdfBaseline()
    {
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutRectangle normalBounds = new(72f, 80f, 150f, 7.56f);
        PdfLayoutRectangle fragmentBounds = new(222f, 81.56f, 180f, 6f);
        PdfTextGlyph normalGlyph = new(
            "The embedded browser font ",
            "AAAAAA+SourceSansPro-Regular",
            12f,
            0f,
            normalBounds,
            black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextGlyph fragmentGlyph = new(
            "Workgroup’s objective",
            "BBBBBB+SourceSansPro-Regular",
            12f,
            0f,
            fragmentBounds,
            black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextRun normalRun = new(
            normalGlyph.Text,
            normalGlyph.FontName,
            normalGlyph.FontSize,
            0f,
            normalBounds,
            black,
            [normalGlyph]);
        PdfTextRun fragmentRun = new(
            fragmentGlyph.Text,
            fragmentGlyph.FontName,
            fragmentGlyph.FontSize,
            0f,
            fragmentBounds,
            black,
            [fragmentGlyph]);
        PdfLayoutRectangle lineBounds = new(72f, 80f, 330f, 7.56f);
        PdfTextLine line = new(normalRun.Text + fragmentRun.Text, lineBounds, [normalRun, fragmentRun]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [normalGlyph, fragmentGlyph],
            [normalRun, fragmentRun],
            [line],
            [new PdfTextBlock(line.Text, lineBounds, [line])],
            [],
            [],
            [],
            [],
            [],
            []);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(new PdfLayoutDocument([page], [])).Html);

        XElement[] fittedRuns = ElementsByClass(dom, "pdf-text-run").ToArray();
        Assert.Equal(2, fittedRuns.Length);
        foreach (XElement fittedRun in fittedRuns)
        {
            Dictionary<string, string> runStyle = ParseStyle(fittedRun.Attribute("style")?.Value ?? "");
            XElement svg = Assert.Single(fittedRun.Descendants(), element => HasClass(element, "pdf-text-run-svg"));
            Dictionary<string, string> svgStyle = ParseStyle(svg.Attribute("style")?.Value ?? "");
            XElement text = Assert.Single(svg.Descendants(), element => element.Name.LocalName == "text");
            string[] viewBox = svg.Attribute("viewBox")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(12f, ParsePoints(runStyle["font-size"]));
            Assert.Equal(12f, float.Parse(viewBox[3], CultureInfo.InvariantCulture));
            Assert.Equal(12f, ParsePoints(svgStyle["height"]));
            Assert.Equal(ParsePoints(runStyle["width"]), float.Parse(text.Attribute("textLength")!.Value, CultureInfo.InvariantCulture));
            Assert.Contains("font-size:12px", text.Attribute("style")?.Value, StringComparison.Ordinal);

            float runTop = ParsePoints(runStyle["top"]);
            float runHeight = ParsePoints(runStyle["height"]);
            float svgTop = ParsePoints(svgStyle["top"]);
            float textBaseline = float.Parse(text.Attribute("y")!.Value, CultureInfo.InvariantCulture);
            Assert.Equal(runTop + runHeight, runTop + svgTop + textBaseline, 3);
        }

        XElement normalFittedRun = fittedRuns.Single(run => run.Value.Contains("The embedded browser font", StringComparison.Ordinal));
        XElement fragmentFittedRun = fittedRuns.Single(run => run.Value.Contains("Workgroup’s objective", StringComparison.Ordinal));
        Dictionary<string, string> fragmentStyle = ParseStyle(fragmentFittedRun.Attribute("style")?.Value ?? "");
        Assert.Equal(6f, ParsePoints(fragmentStyle["height"]));
        XElement normalSvg = Assert.Single(normalFittedRun.Descendants(), element => HasClass(element, "pdf-text-run-svg"));
        XElement fragmentSvg = Assert.Single(fragmentFittedRun.Descendants(), element => HasClass(element, "pdf-text-run-svg"));
        Assert.Equal(7.56f, float.Parse(
            Assert.Single(normalSvg.Descendants(), element => element.Name.LocalName == "text").Attribute("y")!.Value,
            CultureInfo.InvariantCulture));
        Assert.Equal(7.56f, float.Parse(
            Assert.Single(fragmentSvg.Descendants(), element => element.Name.LocalName == "text").Attribute("y")!.Value,
            CultureInfo.InvariantCulture));
        Assert.Equal(0f, ParsePoints(ParseStyle(normalSvg.Attribute("style")!.Value)["top"]));
        Assert.Equal(-1.56f, ParsePoints(ParseStyle(fragmentSvg.Attribute("style")!.Value)["top"]));
    }

    [Fact]
    public void Convert_UsesPdfRunWidthBesideCompressedPunctuation()
    {
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutRectangle textBounds = new(72f, 80f, 100f, 8f);
        PdfLayoutRectangle dashBounds = new(172f, 83f, 5f, 5f);
        PdfTextGlyph textGlyph = new("Text before dash", "SubsetEmbedded-Regular", 10f, 0f, textBounds, black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextGlyph dashGlyph = new("–", "SubsetEmbedded-Regular", 10f, 0f, dashBounds, black)
        {
            UsesBrowserFontAsset = true
        };
        PdfTextRun textRun = new(textGlyph.Text, textGlyph.FontName, textGlyph.FontSize, 0f, textBounds, black, [textGlyph]);
        PdfTextRun dashRun = new(dashGlyph.Text, dashGlyph.FontName, dashGlyph.FontSize, 0f, dashBounds, black, [dashGlyph]);
        PdfLayoutRectangle lineBounds = new(72f, 80f, 105f, 8f);
        PdfTextLine line = new(textRun.Text + dashRun.Text, lineBounds, [textRun, dashRun]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [textGlyph, dashGlyph],
            [textRun, dashRun],
            [line],
            [new PdfTextBlock(line.Text, lineBounds, [line])],
            [],
            [],
            [],
            [],
            [],
            []);

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(new PdfLayoutDocument([page], [])).Html);

        XElement[] fittedRuns = ElementsByClass(dom, "pdf-text-run-svg").ToArray();
        Assert.Equal(2, fittedRuns.Length);
        Assert.Contains(fittedRuns, svg => svg.Descendants().Any(text => text.Value == "Text before dash"));
        Assert.Contains(fittedRuns, svg => svg.Descendants().Any(text => text.Value == "–"));
    }

    [Fact]
    public void Convert_AxialShading_EmitsAnSvgGradientLayer()
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutShading shading = new(
            0,
            2,
            new PdfLayoutRectangle(72f, 300f, 240f, 80f),
            72f,
            340f,
            0f,
            312f,
            340f,
            0f,
            [
                new PdfLayoutGradientStop(0f, new PdfLayoutColor(1f, 0f, 0f, 1f, "DeviceRGB")),
                new PdfLayoutGradientStop(1f, new PdfLayoutColor(0f, 0f, 1f, 1f, "DeviceRGB"))
            ]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [],
            [],
            [shading],
            [],
            [],
            []);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));
        XDocument dom = ParseHtml(html.Html);

        XElement shadingLayer = Assert.Single(ElementsByClass(dom, "pdf-shading-layer"));
        XElement gradient = Assert.Single(shadingLayer.Descendants(), element => element.Name.LocalName == "linearGradient");
        Assert.Equal("72", gradient.Attribute("x1")?.Value);
        Assert.Equal("312", gradient.Attribute("x2")?.Value);
        Assert.Equal(2, gradient.Descendants().Count(element => element.Name.LocalName == "stop"));
        XElement rectangle = Assert.Single(ElementsByClass(dom, "pdf-shading"));
        Assert.Equal("80", rectangle.Attribute("height")?.Value);
    }

    [Fact]
    public void Convert_TensorPatchShading_EmitsAClippedSvgMesh()
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutShading shading = new(
            0,
            7,
            new PdfLayoutRectangle(72f, 300f, 80f, 80f),
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            [],
            [
                new PdfLayoutShadingTriangle(
                    72f,
                    300f,
                    152f,
                    300f,
                    72f,
                    380f,
                    new PdfLayoutColor(1f, 0f, 0f, 1f, "DeviceRGB"))
            ]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [],
            [],
            [shading],
            [],
            [],
            []);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));
        XDocument dom = ParseHtml(html.Html);

        XElement mesh = Assert.Single(ElementsByClass(dom, "pdf-tensor-shading"));
        Assert.Contains("-clip", mesh.Attribute("clip-path")?.Value);
        XElement triangle = Assert.Single(mesh.Elements(), element => element.Name.LocalName == "path");
        Assert.Equal("#FF0000", triangle.Attribute("fill")?.Value);
        Assert.DoesNotContain(
            mesh.Descendants(),
            element => element.Name.LocalName.EndsWith("Gradient", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_OpaqueShapeAlphaPath_EmitsSvgPath()
    {
        PdfLayoutPath path = CreateShapeAlphaPath(alpha: 1f);
        PdfLayoutPage page = CreatePathPage(path);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));

        Assert.Contains("data-path-index=\"0\"", html.Html, StringComparison.Ordinal);
        Assert.Contains("pdf-vector-layer", html.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_TranslucentShapeAlphaPath_DoesNotEmitAnIncorrectSvgOpacityArtifact()
    {
        PdfLayoutPath path = CreateShapeAlphaPath(alpha: 0.75f);
        PdfLayoutPage page = CreatePathPage(path);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));

        Assert.DoesNotContain("data-path-index=\"0\"", html.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("pdf-vector-layer", html.Html, StringComparison.Ordinal);
    }

    private static PdfLayoutPath CreateShapeAlphaPath(float alpha)
    {
        return new PdfLayoutPath(
            0,
            [
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, 72f, 80f, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 192f, 80f, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 192f, 104f, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f)
            ],
            new PdfLayoutRectangle(72f, 80f, 120f, 24f),
            new PdfLayoutColor(0f, 0f, 0f, alpha, "DeviceCMYK"),
            null,
            1,
            usesShapeAlpha: true);
    }

    private static PdfLayoutPage CreatePathPage(PdfLayoutPath path)
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        return new PdfLayoutPage(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [],
            [path],
            [],
            [],
            [],
            []);
    }

    [Fact]
    public void Convert_FixedTextPreservesFontPresentationAndCorrectsAdjacentTransformedRuns()
    {
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfTextRun bold = CreateRun("Bold text", "ABCDEF+SourceSansPro-Bold", 10f, new PdfLayoutRectangle(72f, 80f, 42f, 7f), black);
        PdfTextRun italic = CreateRun("Italic text", "ABCDEF+SourceSansPro-Italic", 10f, new PdfLayoutRectangle(72f, 100f, 44f, 7f), black);
        PdfTextRun transformed = CreateRun("Transformed text fragment", "ABCDEF+SourceSansPro-Regular", 6.563f, new PdfLayoutRectangle(72f, 121.17f, 122.168f, 5.25f), black);
        PdfTextRun adjacent = CreateRun(" continues on the same baseline", "ABCDEF+SourceSansPro-Regular", 10f, new PdfLayoutRectangle(194.33f, 119.805f, 180f, 6.615f), black);
        PdfTextRun[] runs = [bold, italic, transformed, adjacent];
        PdfTextLine[] lines = runs
            .Select(run => new PdfTextLine(run.Text, run.Bounds, [run]))
            .ToArray();
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            runs.SelectMany(run => run.Glyphs).ToArray(),
            runs,
            lines,
            [new PdfTextBlock(string.Join(" ", runs.Select(run => run.Text)), new PdfLayoutRectangle(72f, 80f, 302.33f, 46.42f), lines)],
            [],
            [],
            [],
            [],
            [],
            []);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));
        XDocument dom = ParseHtml(html.Html);

        XElement boldElement = Assert.Single(ElementsByClass(dom, "pdf-text-run"), element =>
            element.Attribute("data-font")?.Value.Contains("Bold", StringComparison.Ordinal) == true);
        XElement italicElement = Assert.Single(ElementsByClass(dom, "pdf-text-run"), element =>
            element.Attribute("data-font")?.Value.Contains("Italic", StringComparison.Ordinal) == true);
        Dictionary<string, string> boldStyle = ParseStyle(boldElement.Attribute("style")?.Value ?? "");
        Dictionary<string, string> italicStyle = ParseStyle(italicElement.Attribute("style")?.Value ?? "");
        Assert.Equal("700", boldStyle["font-weight"]);
        Assert.Equal("italic", italicStyle["font-style"]);

        XElement boldSvgText = Assert.Single(boldElement.Descendants(), element => element.Name.LocalName == "text");
        Dictionary<string, string> boldSvgStyle = ParseStyle(boldSvgText.Attribute("style")?.Value ?? "");
        Assert.Equal("700", boldSvgStyle["font-weight"]);

        XElement transformedElement = Assert.Single(ElementsByClass(dom, "pdf-text-run"), element =>
            element.Value.Contains("Transformed text fragment", StringComparison.Ordinal));
        Dictionary<string, string> transformedStyle = ParseStyle(transformedElement.Attribute("style")?.Value ?? "");
        Assert.Equal(10f, ParsePoints(transformedStyle["font-size"]));
    }

    [Fact]
    public void Convert_PublicAcroFormFixturePreservesWidgetAppearancesBehindControls()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("Acroform-PDFBOX-2333.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfLayoutImage[] appearanceImages = layout.Pages[0].Images
            .Where(image => image.Kind == PdfLayoutImageKind.AnnotationAppearance)
            .ToArray();
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement[] imageElements = ElementsByClass(dom, "pdf-image")
            .Where(element => (element.Attribute("data-asset-id")?.Value ?? string.Empty).Contains("-annotation-", StringComparison.Ordinal))
            .ToArray();

        Assert.True(appearanceImages.Length >= 10);
        Assert.NotEmpty(layout.Pages[0].FormControls);
        Assert.Equal(appearanceImages.Length, imageElements.Length);
        Assert.Equal(layout.Pages[0].FormControls.Count, ElementsByClass(dom, "pdf-form-control").Count());
        Assert.Equal(appearanceImages.Length, html.Assets.Count(asset => asset.ContentType == "image/png"));
        Assert.All(appearanceImages, image =>
        {
            Assert.True(image.Bounds.Width > 0);
            Assert.True(image.Bounds.Height > 0);
            Assert.True(image.IntrinsicWidth > 0);
            Assert.True(image.IntrinsicHeight > 0);
        });
    }

    [Fact]
    public void Convert_EmitsNativeFormGroupsAndStableLabelAssociations()
    {
        PdfLayoutRectangle pageBounds = new(0, 0, 612, 792);
        PdfLayoutFormControl[] controls =
        [
            new(0, "fullName", "Full name", PdfLayoutFormControlKind.Text, new(20, 40, 180, 24),
                sourceLabelText: "Full legal name"),
            new(1, "tax.c1_1[0]", "Individual", PdfLayoutFormControlKind.CheckBox, new(20, 80, 16, 16),
                options: [new("individual", "Individual")], sourceLabelText: "Individual",
                authoredHierarchyKey: "tax", groupKey: "tax.c1_1", groupKind: PdfLayoutFormGroupKind.CheckBox,
                groupLabelText: "Tax classification"),
            new(2, "tax.c1_2[0]", "Consent", PdfLayoutFormControlKind.CheckBox, new(20, 225, 16, 16),
                options: [new("yes", "Yes")], sourceLabelText: "Unrelated consent", authoredHierarchyKey: "tax"),
            new(3, "tax.c1_1[1]", "Corporation", PdfLayoutFormControlKind.CheckBox, new(20, 105, 16, 16),
                options: [new("corporation", "Corporation")], sourceLabelText: "Corporation",
                authoredHierarchyKey: "tax", groupKey: "tax.c1_1", groupKind: PdfLayoutFormGroupKind.CheckBox,
                groupLabelText: "Tax classification"),
            new(4, "contact", "Contact: email", PdfLayoutFormControlKind.RadioButton, new(20, 145, 16, 16),
                options: [new("email", "Email")], sourceLabelText: "Email", groupKey: "contact",
                groupKind: PdfLayoutFormGroupKind.RadioButton, groupLabelText: "Preferred contact"),
            new(5, "contact", "Contact: phone", PdfLayoutFormControlKind.RadioButton, new(80, 145, 16, 16),
                options: [new("phone", "Phone")], sourceLabelText: "Phone", groupKey: "contact",
                groupKind: PdfLayoutFormGroupKind.RadioButton, groupLabelText: "Preferred contact"),
            new(6, "country", "Country", PdfLayoutFormControlKind.ComboBox, new(20, 185, 120, 22),
                options: [new("no", "Norway"), new("us", "United States")], sourceLabelText: "Country")
        ];
        PdfLayoutPage page = new(
            1, pageBounds, pageBounds, pageBounds.Width, pageBounds.Height, 0,
            [], [], [], [], [], [], [], [], [], [], formControls: controls);
        PdfLayoutDocument layout = new([page], [], []);

        XDocument first = ParseHtml(PdfHtmlConverter.Convert(layout).Html);
        XDocument second = ParseHtml(PdfHtmlConverter.Convert(layout).Html);

        XElement form = Assert.Single(first.Descendants("form"));
        Assert.True(HasClass(form, "pdf-form-page"));
        XElement[] fieldsets = form.Descendants("fieldset").ToArray();
        Assert.Equal(2, fieldsets.Length);
        Assert.Equal(
            ["Tax classification", "Preferred contact"],
            fieldsets.Select(fieldset => Assert.Single(fieldset.Elements("legend")).Value));

        XElement[] emitted = ElementsByClass(first, "pdf-form-control").ToArray();
        XElement[] labels = form.Descendants("label").ToArray();
        Assert.Equal(controls.Length, labels.Length);
        Assert.Equal(controls.Length, labels.Select(label => label.Attribute("for")?.Value).Distinct().Count());
        Assert.All(labels, label =>
        {
            string targetId = Assert.IsType<XAttribute>(label.Attribute("for")).Value;
            Assert.Single(emitted, control => control.Attribute("id")?.Value == targetId);
        });
        Assert.Equal(
            emitted.Select(control => control.Attribute("id")?.Value),
            ElementsByClass(second, "pdf-form-control").Select(control => control.Attribute("id")?.Value));
        Assert.Equal("Full legal name", Assert.Single(labels, label => label.Attribute("for")?.Value == "pdf-field-1-0").Value);
        Assert.Equal("Country", Assert.Single(labels, label => label.Attribute("for")?.Value == "pdf-field-1-6").Value);

        XElement text = Assert.Single(emitted, control => control.Attribute("id")?.Value == "pdf-field-1-0");
        Assert.Equal("text", text.Attribute("type")?.Value);
        Assert.Null(text.Attribute("aria-label"));
        XElement taxFieldset = Assert.Single(fieldsets, fieldset => fieldset.Attribute("data-group-key")?.Value == "tax.c1_1");
        Assert.Equal(2, taxFieldset.Descendants("input").Count(input => input.Attribute("type")?.Value == "checkbox"));
        Assert.Equal(
            ["tax.c1_1[0]", "tax.c1_1[1]"],
            taxFieldset.Descendants("input").Select(input => input.Attribute("name")?.Value));
        XElement radioFieldset = Assert.Single(fieldsets, fieldset => fieldset.Attribute("data-group-key")?.Value == "contact");
        Assert.Equal(2, radioFieldset.Descendants("input").Count(input => input.Attribute("type")?.Value == "radio"));
        Assert.Equal("select", Assert.Single(emitted, control => control.Attribute("name")?.Value == "country").Name.LocalName);

        XElement contradiction = Assert.Single(emitted, control => control.Attribute("name")?.Value == "tax.c1_2[0]");
        Assert.Empty(contradiction.Ancestors("fieldset"));
        Assert.Equal("position:absolute;left:20pt;top:225pt;width:16pt;height:16pt", contradiction.Attribute("style")?.Value);
        Assert.True(
            taxFieldset.Descendants("input").First().IsBefore(contradiction),
            "The non-contiguous fieldset should be emitted at its first authored control.");
    }

    [Fact]
    public async Task Convert_EmitsAccessibleSemanticControlsAndPreservesAuthoredAppearances()
    {
        PdfLayoutRectangle pageBounds = new(0, 0, 612, 792);
        PdfLayoutRectangle textBounds = new(20, 40, 180, 24);
        PdfLayoutFormControl[] controls =
        [
            new(0, "fullName", "Full legal name", PdfLayoutFormControlKind.Text, textBounds,
                ["Erik & Ada"], ["Default"], isReadOnly: true, isRequired: true, maxLength: 40),
            new(1, "accepted", "Accepted", PdfLayoutFormControlKind.CheckBox, new(20, 80, 16, 16),
                ["Yes"], [], [new("Yes", "Yes")], isChecked: true),
            new(2, "contact", "Contact by email", PdfLayoutFormControlKind.RadioButton, new(20, 110, 16, 16),
                ["email"], ["email"], [new("email", "Email")], isChecked: true, isDefaultChecked: true),
            new(3, "country", "Country", PdfLayoutFormControlKind.ComboBox, new(20, 140, 120, 22),
                ["no"], ["us"], [new("us", "United States"), new("no", "Norway")]),
            new(4, "colors", "Colors", PdfLayoutFormControlKind.ListBox, new(20, 180, 120, 60),
                ["red", "blue"], [], [new("red", "Red"), new("blue", "Blue")], isMultiple: true),
            new(5, "approval", "Approval signature", PdfLayoutFormControlKind.Signature, new(20, 260, 180, 50),
                ["Ada Lovelace"]),
            new(6, "secret", "Secret", PdfLayoutFormControlKind.Text, new(20, 330, 180, 24),
                ["initial"], isPassword: true),
            new(7, "notes", "Notes", PdfLayoutFormControlKind.Text, new(20, 370, 180, 24),
                ["Visible value"])
        ];
        PdfLayoutImage textAppearance = new(
            0,
            "text-appearance",
            PdfLayoutImageKind.AnnotationAppearance,
            textBounds,
            new PdfLayoutTransform(1, 0, 0, 1, textBounds.X, textBounds.Y),
            180,
            24,
            8,
            "DeviceRGB",
            true,
            "Widget");
        PdfLayoutImage checkAppearance = new(
            1,
            "check-appearance",
            PdfLayoutImageKind.AnnotationAppearance,
            controls[1].Bounds,
            new PdfLayoutTransform(1, 0, 0, 1, controls[1].Bounds.X, controls[1].Bounds.Y),
            16,
            16,
            8,
            "DeviceRGB",
            true,
            "Widget");
        PdfLayoutImage selectAppearance = new(
            2,
            "select-appearance",
            PdfLayoutImageKind.AnnotationAppearance,
            controls[3].Bounds,
            new PdfLayoutTransform(1, 0, 0, 1, controls[3].Bounds.X, controls[3].Bounds.Y),
            120,
            22,
            8,
            "DeviceRGB",
            true,
            "Widget");
        PdfLayoutImage passwordAppearance = new(
            3,
            "text-appearance",
            PdfLayoutImageKind.AnnotationAppearance,
            controls[6].Bounds,
            new PdfLayoutTransform(1, 0, 0, 1, controls[6].Bounds.X, controls[6].Bounds.Y),
            180,
            24,
            8,
            "DeviceRGB",
            true,
            "Widget");
        PdfLayoutPage page = new(
            1, pageBounds, pageBounds, pageBounds.Width, pageBounds.Height, 0,
            [], [], [], [], [textAppearance, checkAppearance, selectAppearance, passwordAppearance], [], [], [], [], [],
            formControls: controls);
        PdfLayoutDocument layout = new(
            [page],
            [
                new PdfLayoutImageAsset("text-appearance", "assets/images/text.png", "image/png", [1, 2, 3]),
                new PdfLayoutImageAsset("check-appearance", "assets/images/check.png", "image/png", [1, 2, 3]),
                new PdfLayoutImageAsset("select-appearance", "assets/images/select.png", "image/png", [1, 2, 3])
            ],
            []);

        PdfHtmlDocument converted = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(converted.Html);
        Assert.Empty(dom.Descendants("mark"));

        XElement[] emitted = ElementsByClass(dom, "pdf-form-control").ToArray();
        Assert.Equal(controls.Length, emitted.Length);
        XElement text = Assert.Single(emitted, element => element.Attribute("name")?.Value == "fullName");
        Assert.Null(text.Attribute("aria-label"));
        Assert.Equal(
            "Full legal name",
            Assert.Single(dom.Descendants("label"), label => label.Attribute("for")?.Value == text.Attribute("id")?.Value).Value);
        Assert.Equal("Erik & Ada", text.Attribute("value")?.Value);
        Assert.Equal("Default", text.Attribute("data-default-value")?.Value);
        Assert.NotNull(text.Attribute("readonly"));
        Assert.NotNull(text.Attribute("required"));
        Assert.Equal("40", text.Attribute("maxlength")?.Value);
        Assert.Contains("left:20pt", text.Attribute("style")?.Value);
        Assert.True(HasClass(text, "pdf-form-control-positioned"));
        Assert.True(HasClass(text, "pdf-form-control-authored-appearance"));
        Assert.Equal("pdf-image-page-1-placement-0", text.Attribute("data-widget-appearance-id")?.Value);

        XElement checkBox = Assert.Single(emitted, element => element.Attribute("type")?.Value == "checkbox");
        Assert.NotNull(checkBox.Attribute("checked"));
        Assert.True(HasClass(checkBox, "pdf-form-control-authored-appearance"));
        Assert.False(HasClass(checkBox, "pdf-form-control-positioned"));
        Assert.Equal("pdf-image-page-1-placement-1", checkBox.Attribute("data-widget-appearance-id")?.Value);
        XElement radio = Assert.Single(emitted, element => element.Attribute("type")?.Value == "radio");
        Assert.Equal("contact", radio.Attribute("name")?.Value);
        Assert.Equal("true", radio.Attribute("data-default-checked")?.Value);
        Assert.False(HasClass(radio, "pdf-form-control-authored-appearance"));
        Assert.False(HasClass(radio, "pdf-form-control-positioned"));

        XElement[] selects = emitted.Where(element => element.Name.LocalName == "select").ToArray();
        Assert.Equal(2, selects.Length);
        Assert.Equal("Norway", Assert.Single(selects[0].Elements(), option => option.Attribute("selected") is not null).Value);
        Assert.True(HasClass(selects[0], "pdf-form-control-positioned"));
        Assert.True(HasClass(selects[0], "pdf-form-control-authored-appearance"));
        Assert.True(HasClass(selects[1], "pdf-form-control-positioned"));
        Assert.False(HasClass(selects[1], "pdf-form-control-authored-appearance"));
        Assert.NotNull(selects[1].Attribute("multiple"));

        XElement password = Assert.Single(emitted, element => element.Attribute("type")?.Value == "password");
        Assert.True(HasClass(password, "pdf-form-control-positioned"));
        Assert.True(HasClass(password, "pdf-form-control-authored-appearance"));

        XElement notes = Assert.Single(emitted, element => element.Attribute("name")?.Value == "notes");
        Assert.Equal("Visible value", notes.Attribute("value")?.Value);
        Assert.True(HasClass(notes, "pdf-form-control-positioned"));
        Assert.False(HasClass(notes, "pdf-form-control-authored-appearance"));
        Assert.Null(notes.Attribute("data-widget-appearance-id"));

        XElement signature = Assert.Single(emitted, element => element.Attribute("data-field-kind")?.Value == "signature");
        Assert.Equal("Ada Lovelace", signature.Attribute("value")?.Value);
        Assert.NotNull(signature.Attribute("readonly"));
        Assert.True(HasClass(signature, "pdf-form-control-positioned"));
        Assert.Equal(4, ElementsByClass(dom, "pdf-image").Count());
        Assert.Contains(".pdf-form-control-positioned {", converted.Css,
            StringComparison.Ordinal);
        Assert.Contains(".pdf-form-control-authored-appearance[type=\"checkbox\"]", converted.Css,
            StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('input', markEdited)", converted.Html, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('change', markEdited)", converted.Html, StringComparison.Ordinal);

        MethodInfo writeFormControls = Assert.IsAssignableFrom<MethodInfo>(typeof(PdfHtmlConverter).GetMethod(
            "WriteFormControls",
            BindingFlags.NonPublic | BindingFlags.Static));
        StringBuilder flowMarkup = new();
        writeFormControls.Invoke(null, [flowMarkup, page, 1f, false]);
        XDocument flowDom = ParseHtml("<html><body>" + flowMarkup + "</body></html>");
        Assert.Single(ElementsByClass(flowDom, "pdf-form-controls-flow"));
        Assert.All(ElementsByClass(flowDom, "pdf-form-control"), control =>
        {
            Assert.False(HasClass(control, "pdf-form-control-positioned"));
            Assert.False(HasClass(control, "pdf-form-control-authored-appearance"));
            Assert.Null(control.Attribute("data-widget-appearance-id"));
        });

        PdfHtmlDocument continuous = PdfHtmlConverter.Convert(layout, new PdfHtmlOptions
        {
            TextMode = PdfHtmlTextMode.Semantic,
            SemanticPageMode = PdfHtmlSemanticPageMode.ContinuousFlow
        });
        XDocument continuousDom = ParseHtml(continuous.Html);
        Assert.Equal(controls.Length, ElementsByClass(continuousDom, "pdf-form-control").Count());
        Assert.Single(ElementsByClass(continuousDom, "pdf-semantic-layout-fallback-page"));
        Assert.Empty(ElementsByClass(continuousDom, "pdf-form-controls-flow"));

        using TempDirectory tempDirectory = new();
        converted.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage browserPage = await browser.NewPageAsync();
        await browserPage.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        ILocator passwordControl = browserPage.Locator("[name='secret']");
        ILocator textImage = browserPage.Locator("#pdf-image-page-1-placement-0");
        ILocator passwordImage = browserPage.Locator("#pdf-image-page-1-placement-3");
        Assert.Equal("text-appearance", await textImage.GetAttributeAsync("data-asset-id"));
        Assert.Equal("text-appearance", await passwordImage.GetAttributeAsync("data-asset-id"));
        Assert.False(await passwordControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-edited')"));
        Assert.False(await passwordImage.EvaluateAsync<bool>("element => element.classList.contains('pdf-widget-appearance-hidden')"));
        await passwordControl.FocusAsync();
        Assert.True(await passwordControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-active')"));
        Assert.Equal("2px", await passwordControl.EvaluateAsync<string>("element => getComputedStyle(element).outlineWidth"));
        Assert.True(await passwordImage.EvaluateAsync<bool>("element => element.classList.contains('pdf-widget-appearance-hidden')"));
        Assert.False(await textImage.EvaluateAsync<bool>("element => element.classList.contains('pdf-widget-appearance-hidden')"));
        await passwordControl.FillAsync("changed");
        await passwordControl.EvaluateAsync("element => element.blur()");
        Assert.True(await passwordControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-edited')"));
        Assert.False(await passwordControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-active')"));
        Assert.Equal("none", await passwordControl.EvaluateAsync<string>("element => getComputedStyle(element).outlineStyle"));
        Assert.True(await passwordImage.EvaluateAsync<bool>("element => element.classList.contains('pdf-widget-appearance-hidden')"));
        Assert.Equal("changed", await passwordControl.InputValueAsync());

        ILocator checkControl = browserPage.Locator("[name='accepted']");
        ILocator checkImage = browserPage.Locator("#pdf-image-page-1-placement-1");
        Assert.Equal("0", await checkControl.EvaluateAsync<string>("element => getComputedStyle(element).opacity"));
        await checkControl.ClickAsync();
        Assert.True(await checkControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-edited')"));
        Assert.True(await checkImage.EvaluateAsync<bool>("element => element.classList.contains('pdf-widget-appearance-hidden')"));
        Assert.Equal("1", await checkControl.EvaluateAsync<string>("element => getComputedStyle(element).opacity"));

        ILocator notesControl = browserPage.Locator("[name='notes']");
        Assert.Equal("Visible value", await notesControl.InputValueAsync());
        Assert.Equal("none", await notesControl.EvaluateAsync<string>("element => getComputedStyle(element).borderStyle"));
        Assert.NotEqual("rgba(0, 0, 0, 0)", await notesControl.EvaluateAsync<string>("element => getComputedStyle(element).color"));
        await notesControl.FocusAsync();
        Assert.True(await notesControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-active')"));
        await notesControl.FillAsync("Edited value");
        await notesControl.EvaluateAsync("element => element.blur()");
        Assert.True(await notesControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-edited')"));
        Assert.Equal("Edited value", await notesControl.InputValueAsync());
        Assert.NotEqual("none", await notesControl.EvaluateAsync<string>("element => getComputedStyle(element).borderStyle"));

        ILocator listControl = browserPage.Locator("[name='colors']");
        Assert.Equal("none", await listControl.EvaluateAsync<string>("element => getComputedStyle(element).borderStyle"));
        Assert.Equal("rgba(0, 0, 0, 0)", await listControl.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
        Assert.False(await listControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-authored-appearance')"));
        Assert.Null(await listControl.GetAttributeAsync("data-widget-appearance-id"));
        await listControl.FocusAsync();
        Assert.True(await listControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-active')"));
        Assert.NotEqual("none", await listControl.EvaluateAsync<string>("element => getComputedStyle(element).borderStyle"));
        await listControl.EvaluateAsync("element => element.dispatchEvent(new Event('change', { bubbles: true }))");
        await listControl.EvaluateAsync("element => element.blur()");
        Assert.True(await listControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-edited')"));
        Assert.False(await listControl.EvaluateAsync<bool>("element => element.classList.contains('pdf-form-control-active')"));
        Assert.NotEqual("none", await listControl.EvaluateAsync<string>("element => getComputedStyle(element).borderStyle"));
    }

    [Fact]
    public async Task Convert_RenderedInHeadlessBrowserMatchesLayoutGeometry()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Browser geometry) Tj
            0 -24 Td
            (Second browser line) Tj
            ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1000,
                Height = 1200
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        string artifactDirectory = ArtifactDirectory(nameof(Convert_RenderedInHeadlessBrowserMatchesLayoutGeometry));
        BrowserRenderComparison comparison = await CompareRenderedGeometryAsync(document, layout, page);
        if (comparison.Mismatches.Count > 0)
        {
            await WriteBrowserMismatchArtifactsAsync(html, page, artifactDirectory, comparison);
        }

        Assert.True(
            comparison.Mismatches.Count == 0,
            string.Join(Environment.NewLine, comparison.Mismatches) + Environment.NewLine + $"Artifacts: {artifactDirectory}");
    }

    [Fact]
    public void Convert_EmitsLinkOverlayWithUriAndBounds()
    {
        using PDDocument document = CreateLinkedTextDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);

        XElement link = Assert.Single(ElementsByClass(dom, "pdf-link-overlay"));
        Assert.Equal("https://example.com/pdfbox", link.Attribute("href")?.Value);
        Assert.Equal("uri", link.Attribute("data-link-kind")?.Value);
        Assert.Equal("https://example.com/pdfbox", link.Attribute("data-uri")?.Value);
        Assert.Equal("https://example.com/pdfbox", link.Attribute("aria-label")?.Value);
        Dictionary<string, string> style = ParseStyle(link.Attribute("style")?.Value ?? "");
        Assert.Equal("absolute", style["position"]);
        AssertClose(72, ParsePoints(style["left"]));
        AssertClose(88, ParsePoints(style["top"]));
        AssertClose(120, ParsePoints(style["width"]));
        AssertClose(24, ParsePoints(style["height"]));
    }

    [Fact]
    public async Task Convert_RenderedLinkOverlayInHeadlessBrowserMatchesLayoutGeometry()
    {
        using PDDocument document = CreateLinkedTextDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1000,
                Height = 1200
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        const float cssPixelsPerPoint = 96f / 72f;
        const float tolerancePx = 1.0f;
        PdfLayoutLink layoutLink = Assert.Single(Assert.Single(layout.Pages).Links);
        LocatorBoundingBoxResult pageBox = await page.Locator(".pdf-page").BoundingBoxAsync()
            ?? throw new InvalidOperationException("Page did not render a bounding box.");
        LocatorBoundingBoxResult linkBox = await page.Locator(".pdf-link-overlay").BoundingBoxAsync()
            ?? throw new InvalidOperationException("Link overlay did not render a bounding box.");

        AssertWithin(tolerancePx, layoutLink.Bounds.X * cssPixelsPerPoint, (float)(linkBox.X - pageBox.X));
        AssertWithin(tolerancePx, layoutLink.Bounds.Y * cssPixelsPerPoint, (float)(linkBox.Y - pageBox.Y));
        AssertWithin(tolerancePx, layoutLink.Bounds.Width * cssPixelsPerPoint, (float)linkBox.Width);
        AssertWithin(tolerancePx, layoutLink.Bounds.Height * cssPixelsPerPoint, (float)linkBox.Height);
    }

    [Fact]
    public void Convert_EmitsImageElementWithExportedAssetAndBounds()
    {
        using PDDocument document = CreateImageDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);

        Assert.Empty(layout.Diagnostics);
        PdfHtmlAsset asset = Assert.Single(html.Assets);
        Assert.Equal("assets/images/page-1-image-0.png", asset.RelativePath);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal((2, 2), PngDimensions(asset.Data));
        XElement image = Assert.Single(ElementsByClass(dom, "pdf-image"));
        Assert.Equal(asset.RelativePath, image.Attribute("src")?.Value);
        Assert.Equal("page-1-image-0", image.Attribute("data-asset-id")?.Value);
        Assert.Equal("Im0", image.Attribute("data-source-name")?.Value);
        Dictionary<string, string> style = ParseStyle(image.Attribute("style")?.Value ?? "");
        Assert.Equal("absolute", style["position"]);
        AssertClose(72, ParsePoints(style["left"]));
        AssertClose(132, ParsePoints(style["top"]));
        AssertClose(120, ParsePoints(style["width"]));
        AssertClose(60, ParsePoints(style["height"]));
    }

    [Fact]
    public void Convert_ReusedImageAssetIsDeterministicAndNotDuplicated()
    {
        using PDDocument document = CreateRepeatedImageDocument();
        PdfLayoutOptions options = new()
        {
            IncludeImageAssets = true
        };

        PdfLayoutDocument firstLayout = PdfLayoutExtractor.Extract(document, options);
        PdfLayoutDocument secondLayout = PdfLayoutExtractor.Extract(document, options);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(firstLayout);
        XDocument dom = ParseHtml(html.Html);

        PdfLayoutImage[] placements = Assert.Single(firstLayout.Pages).Images.ToArray();
        Assert.Equal(2, placements.Length);
        Assert.Equal("page-1-image-0", placements[0].AssetId);
        Assert.All(placements, image => Assert.Equal(placements[0].AssetId, image.AssetId));
        Assert.NotEqual(placements[0].Bounds.X, placements[1].Bounds.X);
        PdfLayoutImageAsset asset = Assert.Single(firstLayout.ImageAssets);
        Assert.Equal(placements[0].AssetId, asset.AssetId);
        Assert.Equal("assets/images/page-1-image-0.png", asset.RelativePath);

        Assert.Equal(
            firstLayout.Pages.SelectMany(page => page.Images).Select(image => image.AssetId),
            secondLayout.Pages.SelectMany(page => page.Images).Select(image => image.AssetId));
        PdfLayoutImageAsset secondAsset = Assert.Single(secondLayout.ImageAssets);
        Assert.Equal(asset.AssetId, secondAsset.AssetId);
        Assert.Equal(asset.RelativePath, secondAsset.RelativePath);
        Assert.Equal(asset.Data, secondAsset.Data);

        PdfHtmlAsset htmlAsset = Assert.Single(html.Assets);
        Assert.Equal(asset.RelativePath, htmlAsset.RelativePath);
        XElement[] images = ElementsByClass(dom, "pdf-image").ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image =>
        {
            Assert.Equal(asset.AssetId, image.Attribute("data-asset-id")?.Value);
            Assert.Equal(asset.RelativePath, image.Attribute("src")?.Value);
        });

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        string imageDirectory = Path.Combine(tempDirectory.Path, "assets", "images");
        Assert.Equal(
            [Path.GetFileName(asset.RelativePath)],
            Directory.GetFiles(imageDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Convert_EmbeddedTrueTypeFont_ExportsAndLoadsFontFace()
    {
        byte[] sourceFont = File.ReadAllBytes(FixturePath("LiberationSans-Regular.ttf"));
        using PDDocument document = CreateEmbeddedTrueTypeDocument(sourceFont);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        PdfLayoutFontAsset font = Assert.Single(layout.FontAssets);
        Assert.Contains("EmbeddedLiberation", font.FontNames);
        Assert.Equal("font/ttf", font.ContentType);
        Assert.Equal("truetype", font.CssFormat);
        Assert.Equal(sourceFont, font.Data);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        PdfHtmlAsset exported = Assert.Single(html.Assets, asset => asset.RelativePath == font.RelativePath);
        Assert.Equal(sourceFont, exported.Data);
        Assert.Contains("@font-face{font-family:'EmbeddedLiberation'", html.Css, StringComparison.Ordinal);
        Assert.Contains("src:url('fonts/", html.Css, StringComparison.Ordinal);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        bool loaded = await page.EvaluateAsync<bool>(
            "() => document.fonts.ready.then(() => Array.from(document.fonts).some(font => font.family === 'EmbeddedLiberation' && font.status === 'loaded'))");
        Assert.True(loaded, "The generated @font-face must load the embedded font from the emitted asset.");
    }

    [Fact]
    public async Task OpenTypeCffWriter_WrapsRawCffAndLoadsFontFace()
    {
        byte[] rawCff = CreateMinimalRawType1Cff();
        CFFType1Font cffFont = Assert.IsType<CFFType1Font>(new CFFParser().Parse(rawCff).Single());
        Assert.True(
            OpenTypeCffWriter.TryCreate(
                cffFont,
                rawCff,
                "RawCff",
                new Dictionary<int, int> { ['A'] = 1 },
                400,
                italic: false,
                out byte[] fontData,
                out string? failureReason),
            failureReason);
        Assert.True(fontData.AsSpan().StartsWith("OTTO"u8));
        Assert.IsType<OpenTypeFont>(new OTFParser().Parse(fontData));

        using TempDirectory tempDirectory = new();
        File.WriteAllBytes(Path.Combine(tempDirectory.Path, "raw-cff.otf"), fontData);
        File.WriteAllText(
            Path.Combine(tempDirectory.Path, "index.html"),
            """
            <!doctype html>
            <style>
              @font-face { font-family: 'RawCffWebFont'; src: url('raw-cff.otf') format('opentype'); }
              body { font-family: 'RawCffWebFont'; }
            </style>
            <span>A</span>
            """);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        string fontState = await page.EvaluateAsync<string>(
            "() => document.fonts.ready.then(() => JSON.stringify(Array.from(document.fonts).filter(font => font.family === 'RawCffWebFont').map(font => ({ status: font.status, family: font.family }))))");
        IReadOnlyList<IConsoleMessage> consoleMessages = await page.ConsoleMessagesAsync();
        Assert.True(
            fontState.Contains("\"status\":\"loaded\"", StringComparison.Ordinal),
            $"Browser font state: {fontState}; console: {string.Join(" | ", consoleMessages.Select(message => message.Text))}");
    }

    [Fact]
    public void Convert_GlyphOutlineFallback_EmitsOriginalSvgPath()
    {
        PdfLayoutColor black = new(0, 0, 0, 1, "DeviceRGB");
        PdfTextGlyph glyph = new("A", "EmbeddedCff", 12, 0, new PdfLayoutRectangle(72, 72, 8, 12), black)
        {
            Outline =
            [
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, 72, 84, 0, 0, 0, 0),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 76, 72, 0, 0, 0, 0),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, 80, 84, 0, 0, 0, 0),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0, 0, 0, 0, 0, 0)
            ]
        };
        PdfTextRun run = new("A", "EmbeddedCff", 12, 0, glyph.Bounds, black, [glyph]);
        PdfTextLine line = new("A", glyph.Bounds, [run]);
        PdfTextBlock block = new("A", glyph.Bounds, [line]);
        PdfLayoutPage page = new(
            1,
            new PdfLayoutRectangle(0, 0, 612, 792),
            new PdfLayoutRectangle(0, 0, 612, 792),
            612,
            792,
            0,
            [glyph],
            [run],
            [line],
            [block],
            [],
            [],
            [],
            [],
            []);

        PdfHtmlDocument html = PdfHtmlConverter.Convert(new PdfLayoutDocument([page], []));

        Assert.Contains("pdf-text-run-outline", html.Html, StringComparison.Ordinal);
        Assert.Contains("d=\"M 0 12 L 4 0 L 8 12 Z\"", html.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("textLength=\"8\"", html.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_EmitsInlineImageElementWithExportedAsset()
    {
        using PDDocument document = CreateInlineImageDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);

        PdfLayoutImage layoutImage = Assert.Single(Assert.Single(layout.Pages).Images);
        Assert.Equal(PdfLayoutImageKind.InlineImage, layoutImage.Kind);
        Assert.Empty(layout.Diagnostics);
        PdfHtmlAsset asset = Assert.Single(html.Assets);
        Assert.Equal("assets/images/page-1-image-0.png", asset.RelativePath);
        Assert.Equal((2, 2), PngDimensions(asset.Data));
        XElement image = Assert.Single(ElementsByClass(dom, "pdf-image"));
        Assert.Equal(asset.RelativePath, image.Attribute("src")?.Value);
        Assert.Equal("page-1-image-0", image.Attribute("data-asset-id")?.Value);
        Assert.Null(image.Attribute("data-source-name"));
    }

    [Fact]
    public void Convert_PaintsContainingVectorBackdropsBeforeImages()
    {
        using PDDocument document = CreateImageWithVectorBackdropDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);
        XElement page = Assert.Single(ElementsByClass(dom, "pdf-page"));
        XElement image = Assert.Single(ElementsByClass(dom, "pdf-image"));
        XElement[] vectorLayers = page.Elements()
            .Where(element => HasClass(element, "pdf-vector-layer"))
            .ToArray();
        Assert.Equal(2, vectorLayers.Length);
        XElement backdropLayer = vectorLayers[0];
        XElement foregroundLayer = vectorLayers[1];

        Assert.Equal("paint-0", backdropLayer.Attribute("data-vector-layer")?.Value);
        Assert.Equal("paint-1", foregroundLayer.Attribute("data-vector-layer")?.Value);
        Assert.Equal("0", Assert.Single(backdropLayer.Descendants(),
                element => element.Name.LocalName == "path" && element.Attribute("data-path-index") != null)
            .Attribute("data-path-index")?.Value);
        Assert.Equal("1", Assert.Single(foregroundLayer.Descendants(),
                element => element.Name.LocalName == "path" && element.Attribute("data-path-index") != null)
            .Attribute("data-path-index")?.Value);

        XElement[] children = page.Elements().ToArray();
        Assert.True(Array.IndexOf(children, backdropLayer) < Array.IndexOf(children, image));
        Assert.True(Array.IndexOf(children, image) < Array.IndexOf(children, foregroundLayer));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Convert_PreservesImageAndVectorContentStreamOrder(bool imageFirst)
    {
        using PDDocument document = CreateImageAndVectorDocument(imageFirst);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });

        XDocument dom = ParseHtml(PdfHtmlConverter.Convert(layout).Html);
        XElement page = Assert.Single(ElementsByClass(dom, "pdf-page"));
        XElement image = Assert.Single(ElementsByClass(dom, "pdf-image"));
        XElement vector = Assert.Single(ElementsByClass(dom, "pdf-vector-layer"));
        XElement[] children = page.Elements().ToArray();

        Assert.Equal(
            imageFirst,
            Array.IndexOf(children, image) < Array.IndexOf(children, vector));
    }

    [Fact]
    public async Task Convert_RenderedImageInHeadlessBrowserMatchesLayoutGeometry()
    {
        using PDDocument document = CreateImageDocument();
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        });
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1000,
                Height = 1200
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        const float cssPixelsPerPoint = 96f / 72f;
        const float tolerancePx = 1.0f;
        PdfLayoutImage layoutImage = Assert.Single(Assert.Single(layout.Pages).Images);
        ILocator pageLocator = page.Locator(".pdf-page");
        ILocator imageLocator = page.Locator(".pdf-image");
        await imageLocator.WaitForAsync();
        LocatorBoundingBoxResult pageBox = await pageLocator.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Page did not render a bounding box.");
        LocatorBoundingBoxResult imageBox = await imageLocator.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Image did not render a bounding box.");

        AssertWithin(tolerancePx, layoutImage.Bounds.X * cssPixelsPerPoint, (float)(imageBox.X - pageBox.X));
        AssertWithin(tolerancePx, layoutImage.Bounds.Y * cssPixelsPerPoint, (float)(imageBox.Y - pageBox.Y));
        AssertWithin(tolerancePx, layoutImage.Bounds.Width * cssPixelsPerPoint, (float)imageBox.Width);
        AssertWithin(tolerancePx, layoutImage.Bounds.Height * cssPixelsPerPoint, (float)imageBox.Height);
    }

    [Fact]
    public void Convert_EmitsVectorSvgOverlayWithPathStyle()
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

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
        XDocument dom = ParseHtml(html.Html);

        XElement svg = Assert.Single(ElementsByClass(dom, "pdf-vector-layer"));
        Assert.Equal("1", svg.Attribute("data-path-count")?.Value);
        Assert.Equal("0 0 612 792", svg.Attribute("viewBox")?.Value);
        XElement path = Assert.Single(dom.Descendants("path"), element => element.Attribute("data-path-index") != null);
        Assert.Equal("0", path.Attribute("data-path-index")?.Value);
        Assert.Equal("M 72 192 L 192 192 L 192 132 L 72 132 Z", path.Attribute("d")?.Value);
        Assert.Equal("#1A9933", path.Attribute("fill")?.Value);
        Assert.Equal("1", path.Attribute("fill-opacity")?.Value);
        Assert.Equal("nonzero", path.Attribute("fill-rule")?.Value);
        Assert.Equal("#FF0000", path.Attribute("stroke")?.Value);
        Assert.Equal("2", path.Attribute("stroke-width")?.Value);
        Assert.Equal("butt", path.Attribute("stroke-linecap")?.Value);
        Assert.Equal("miter", path.Attribute("stroke-linejoin")?.Value);
    }

    [Fact]
    public async Task Convert_RenderedVectorPathInHeadlessBrowserMatchesLayoutGeometry()
    {
        using PDDocument document = CreateTextDocument("""
            0.1 0.6 0.2 rg
            72 600 120 60 re
            f
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 1000,
                Height = 1200
            }
        });
        await page.GotoAsync(new Uri(Path.Combine(tempDirectory.Path, "index.html")).AbsoluteUri);

        const float cssPixelsPerPoint = 96f / 72f;
        const float tolerancePx = 1.0f;
        PdfLayoutPath layoutPath = Assert.Single(Assert.Single(layout.Pages).Paths);
        ILocator pageLocator = page.Locator(".pdf-page");
        ILocator pathLocator = page.Locator("[data-path-index]");
        await pathLocator.WaitForAsync();
        LocatorBoundingBoxResult pageBox = await pageLocator.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Page did not render a bounding box.");
        LocatorBoundingBoxResult pathBox = await pathLocator.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Vector path did not render a bounding box.");

        List<string> mismatches = [];
        AddMismatchIfOutsideTolerance(mismatches, "vector path x", layoutPath.Bounds.X * cssPixelsPerPoint, (float)(pathBox.X - pageBox.X), tolerancePx);
        AddMismatchIfOutsideTolerance(mismatches, "vector path y", layoutPath.Bounds.Y * cssPixelsPerPoint, (float)(pathBox.Y - pageBox.Y), tolerancePx);
        AddMismatchIfOutsideTolerance(mismatches, "vector path width", layoutPath.Bounds.Width * cssPixelsPerPoint, (float)pathBox.Width, tolerancePx);
        AddMismatchIfOutsideTolerance(mismatches, "vector path height", layoutPath.Bounds.Height * cssPixelsPerPoint, (float)pathBox.Height, tolerancePx);

        if (mismatches.Count > 0)
        {
            string artifactDirectory = ArtifactDirectory(nameof(Convert_RenderedVectorPathInHeadlessBrowserMatchesLayoutGeometry));
            await WriteGeometryMismatchArtifactsAsync(html, page, artifactDirectory, mismatches);
        }

        Assert.True(
            mismatches.Count == 0,
            string.Join(Environment.NewLine, mismatches) + Environment.NewLine +
            $"Artifacts: {ArtifactDirectory(nameof(Convert_RenderedVectorPathInHeadlessBrowserMatchesLayoutGeometry))}");
    }

    [Fact]
    public void WriteToDirectory_EmitsStableFilesWithNoBrokenLocalReferences()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Assets) Tj
            ET
            """);
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document));

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        string indexPath = Path.Combine(tempDirectory.Path, "index.html");
        string cssPath = Path.Combine(tempDirectory.Path, "assets", "pdfbox-net-fixed.css");
        Assert.True(File.Exists(indexPath));
        Assert.True(File.Exists(cssPath));
        Assert.Empty(BrokenLocalReferences(indexPath));
    }

    [Fact]
    public void WriteToDirectory_LinkOverlayDoesNotCreateBrokenLocalReference()
    {
        using PDDocument document = CreateLinkedTextDocument();
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document));

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        Assert.Empty(BrokenLocalReferences(Path.Combine(tempDirectory.Path, "index.html")));
    }

    [Fact]
    public void WriteToDirectory_ImageAssetDoesNotCreateBrokenLocalReference()
    {
        using PDDocument document = CreateImageDocument();
        PdfHtmlDocument html = PdfHtmlConverter.Convert(PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImageAssets = true
        }));

        using TempDirectory tempDirectory = new();
        html.WriteToDirectory(tempDirectory.Path);

        string indexPath = Path.Combine(tempDirectory.Path, "index.html");
        Assert.Empty(BrokenLocalReferences(indexPath));
        PdfHtmlAsset asset = Assert.Single(html.Assets);
        string assetPath = Path.Combine(tempDirectory.Path, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(assetPath));
        Assert.Equal(asset.Data, File.ReadAllBytes(assetPath));
    }

    [Fact]
    public void Convert_OutputIsDeterministic()
    {
        using PDDocument document = CreateTextDocument("""
            BT
            /F1 12 Tf
            72 700 Td
            (Stable) Tj
            ET
            """);
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document);

        PdfHtmlDocument first = PdfHtmlConverter.Convert(layout);
        PdfHtmlDocument second = PdfHtmlConverter.Convert(layout);

        Assert.Equal(first.Html, second.Html);
        Assert.Equal(first.Css, second.Css);
        Assert.Equal(first.CssPath, second.CssPath);
    }

    private static async Task<BrowserRenderComparison> CompareRenderedGeometryAsync(
        PDDocument document,
        PdfLayoutDocument layout,
        IPage page)
    {
        const float cssPixelsPerPoint = 96f / 72f;
        const float geometryTolerancePx = 1.0f;

        List<string> mismatches = new();
        PdfLayoutPage layoutPage = Assert.Single(layout.Pages);
        ILocator pageLocator = page.Locator(".pdf-page");
        LocatorBoundingBoxResult? pageBox = await pageLocator.BoundingBoxAsync();
        if (pageBox == null)
        {
            return new BrowserRenderComparison(["No .pdf-page element was rendered."], [], []);
        }

        float expectedPageWidth = layoutPage.Width * cssPixelsPerPoint;
        float expectedPageHeight = layoutPage.Height * cssPixelsPerPoint;
        AddMismatchIfOutsideTolerance(mismatches, "page width", expectedPageWidth, (float)pageBox.Width, geometryTolerancePx);
        AddMismatchIfOutsideTolerance(mismatches, "page height", expectedPageHeight, (float)pageBox.Height, geometryTolerancePx);

        byte[] pageScreenshot = await pageLocator.ScreenshotAsync();
        (int screenshotWidth, int screenshotHeight) = PngDimensions(pageScreenshot);
        AddMismatchIfOutsideTolerance(mismatches, "page screenshot width", expectedPageWidth, screenshotWidth, 1.0f);
        AddMismatchIfOutsideTolerance(mismatches, "page screenshot height", expectedPageHeight, screenshotHeight, 1.0f);

        using BufferedImage browserPage = DecodePng(pageScreenshot);
        using BufferedImage pdfPage = new PDFRenderer(document).RenderImageWithDPI(0, 96f, ImageType.RGB);
        byte[] pdfPagePng = RenderingBackend.Current.ImageCodec.Encode(pdfPage, EncodedImageFormat.Png, 100);
        AddMismatchIfOutsideTolerance(mismatches, "PDF render width", pdfPage.Width, browserPage.Width, 0);
        AddMismatchIfOutsideTolerance(mismatches, "PDF render height", pdfPage.Height, browserPage.Height, 0);
        if (pdfPage.Width == browserPage.Width && pdfPage.Height == browserPage.Height)
        {
            AddForegroundMaskMismatches(mismatches, pdfPage, browserPage);
        }

        ILocator textRuns = page.Locator(".pdf-text-run");
        int renderedRunCount = await textRuns.CountAsync();
        if (renderedRunCount != layoutPage.Runs.Count)
        {
            mismatches.Add($"text run count expected {layoutPage.Runs.Count}, actual {renderedRunCount}");
        }

        for (int i = 0; i < Math.Min(renderedRunCount, layoutPage.Runs.Count); i++)
        {
            PdfTextRun run = layoutPage.Runs[i];
            LocatorBoundingBoxResult? runBox = await textRuns.Nth(i).BoundingBoxAsync();
            if (runBox == null)
            {
                mismatches.Add($"text run {i} did not render a bounding box");
                continue;
            }

            float relativeX = (float)(runBox.X - pageBox.X);
            float relativeY = (float)(runBox.Y - pageBox.Y);
            AddMismatchIfOutsideTolerance(mismatches, $"text run {i} left", run.Bounds.X * cssPixelsPerPoint, relativeX, geometryTolerancePx);
            AddMismatchIfOutsideTolerance(mismatches, $"text run {i} top", run.Bounds.Y * cssPixelsPerPoint, relativeY, geometryTolerancePx);
            AddMismatchIfOutsideTolerance(mismatches, $"text run {i} width", run.Bounds.Width * cssPixelsPerPoint, (float)runBox.Width, geometryTolerancePx);
            AddMismatchIfOutsideTolerance(mismatches, $"text run {i} height", run.Bounds.Height * cssPixelsPerPoint, (float)runBox.Height, geometryTolerancePx);
        }

        string renderedText = await pageLocator.InnerTextAsync();
        if (TextCoverage(layout.Text, renderedText) < 0.99f)
        {
            mismatches.Add($"rendered text coverage below 0.99: {TextCoverage(layout.Text, renderedText):0.###}");
        }

        return new BrowserRenderComparison(mismatches, pageScreenshot, pdfPagePng);
    }

    private static BufferedImage DecodePng(byte[] png)
    {
        return RenderingBackend.Current.ImageCodec.Decode(png)
            ?? throw new InvalidOperationException("Unable to decode browser page screenshot PNG.");
    }

    private static void AddForegroundMaskMismatches(
        List<string> mismatches,
        BufferedImage pdfPage,
        BufferedImage browserPage)
    {
        ForegroundShapeStats? stats = ForegroundShapeStats.Create(
            pdfPage,
            browserPage,
            luminanceThreshold: 245,
            dilationRadius: 3);
        if (stats == null)
        {
            mismatches.Add("foreground mask comparison did not find any foreground pixels");
            return;
        }

        if (stats.ForegroundDeltaRatio > 0.45f)
        {
            mismatches.Add($"foreground mask pixel-count delta expected <= 0.45, actual {stats.ForegroundDeltaRatio:0.###}");
        }

        if (stats.PdfMissRatio > 0.25f)
        {
            mismatches.Add($"foreground mask PDF miss ratio expected <= 0.25, actual {stats.PdfMissRatio:0.###}");
        }

        if (stats.BrowserMissRatio > 0.25f)
        {
            mismatches.Add($"foreground mask browser miss ratio expected <= 0.25, actual {stats.BrowserMissRatio:0.###}");
        }
    }

    private static void AddMismatchIfOutsideTolerance(
        List<string> mismatches,
        string name,
        float expected,
        float actual,
        float tolerance)
    {
        if (MathF.Abs(expected - actual) > tolerance)
        {
            mismatches.Add($"{name} expected {expected:0.###}, actual {actual:0.###}, tolerance {tolerance:0.###}");
        }
    }

    private static async Task WriteBrowserMismatchArtifactsAsync(
        PdfHtmlDocument html,
        IPage page,
        string artifactDirectory,
        BrowserRenderComparison comparison)
    {
        if (Directory.Exists(artifactDirectory))
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }

        Directory.CreateDirectory(artifactDirectory);
        html.WriteToDirectory(artifactDirectory);
        if (comparison.PdfPagePng.Length > 0)
        {
            await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "pdf-page.png"), comparison.PdfPagePng);
        }

        if (comparison.BrowserPagePng.Length > 0)
        {
            await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "browser-page.png"), comparison.BrowserPagePng);
        }

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = true,
            Path = Path.Combine(artifactDirectory, "viewport.png")
        });
        File.WriteAllLines(Path.Combine(artifactDirectory, "mismatches.txt"), comparison.Mismatches);
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "visual-report.html"), VisualReportHtml(comparison.Mismatches));
    }

    private static async Task WriteGeometryMismatchArtifactsAsync(
        PdfHtmlDocument html,
        IPage page,
        string artifactDirectory,
        IReadOnlyList<string> mismatches)
    {
        if (Directory.Exists(artifactDirectory))
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }

        Directory.CreateDirectory(artifactDirectory);
        html.WriteToDirectory(artifactDirectory);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = true,
            Path = Path.Combine(artifactDirectory, "viewport.png")
        });
        await File.WriteAllLinesAsync(Path.Combine(artifactDirectory, "mismatches.txt"), mismatches);
    }

    private static string VisualReportHtml(IReadOnlyList<string> mismatches)
    {
        StringBuilder report = new();
        report.AppendLine("<!doctype html>");
        report.AppendLine("<html lang=\"en\">");
        report.AppendLine("<head>");
        report.AppendLine("  <meta charset=\"utf-8\" />");
        report.AppendLine("  <title>HTML render mismatch</title>");
        report.AppendLine("  <style>");
        report.AppendLine("    body{font-family:Arial,sans-serif;margin:24px;color:#111827;background:#f9fafb}");
        report.AppendLine("    .images{display:flex;gap:24px;align-items:flex-start;flex-wrap:wrap}");
        report.AppendLine("    figure{margin:0;padding:12px;background:white;border:1px solid #d1d5db}");
        report.AppendLine("    img{max-width:48vw;height:auto;border:1px solid #e5e7eb}");
        report.AppendLine("    code{white-space:pre-wrap}");
        report.AppendLine("  </style>");
        report.AppendLine("</head>");
        report.AppendLine("<body>");
        report.AppendLine("  <h1>HTML render mismatch</h1>");
        report.AppendLine("  <div class=\"images\">");
        report.AppendLine("    <figure><figcaption>PDF render</figcaption><img src=\"pdf-page.png\" alt=\"PDF render\" /></figure>");
        report.AppendLine("    <figure><figcaption>Browser render</figcaption><img src=\"browser-page.png\" alt=\"Browser render\" /></figure>");
        report.AppendLine("  </div>");
        report.AppendLine("  <h2>Mismatches</h2>");
        report.AppendLine("  <code>");
        report.Append(WebUtility.HtmlEncode(string.Join(Environment.NewLine, mismatches)));
        report.AppendLine("</code>");
        report.AppendLine("</body>");
        report.AppendLine("</html>");
        return report.ToString();
    }

    private static string ArtifactDirectory(string testName)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../artifacts/html-render-tests",
            testName));
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static (int Width, int Height) PngDimensions(byte[] png)
    {
        Assert.True(png.Length >= 24);
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(0, 4)) == 0x89504E47u);
        return (
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    private static Task<ColumnGridRenderMetrics> ReadColumnGridRenderMetrics(IPage page)
    {
        return page.EvaluateAsync<ColumnGridRenderMetrics>(
            """
            () => {
              const grid = document.querySelector(".pdf-semantic-columns");
              const columns = Array.from(grid.querySelectorAll(":scope > .pdf-semantic-column"));
              const gridBox = grid.getBoundingClientRect();
              const boxes = columns.map(column => column.getBoundingClientRect());
              return {
                columnCount: columns.length,
                gridLeft: gridBox.left,
                gridRight: gridBox.right,
                gridWidth: gridBox.width,
                lefts: boxes.map(box => box.left),
                rights: boxes.map(box => box.right),
                widths: boxes.map(box => box.width)
              };
            }
            """);
    }

    private static void AssertColumnGridGeometry(ColumnGridRenderMetrics metrics)
    {
        Assert.Equal(3, metrics.ColumnCount);
        Assert.Equal(3, metrics.Lefts.Length);
        Assert.Equal(3, metrics.Rights.Length);
        Assert.Equal(3, metrics.Widths.Length);
        for (int index = 0; index < metrics.ColumnCount; index++)
        {
            Assert.True(metrics.Widths[index] > 0);
            Assert.True(metrics.Lefts[index] >= metrics.GridLeft - 0.5);
            Assert.True(metrics.Rights[index] <= metrics.GridRight + 0.5);
            if (index > 0)
            {
                Assert.True(metrics.Lefts[index] > metrics.Lefts[index - 1]);
                Assert.True(metrics.Rights[index - 1] <= metrics.Lefts[index] + 0.5);
            }
        }
    }

    private sealed class ContinuousFlowMetrics
    {
        public int FixedPageCount { get; set; }

        public int MarkerCount { get; set; }

        public double DocumentWidth { get; set; }

        public double FlowWidth { get; set; }

        public double FirstMarkerTop { get; set; }

        public double SecondMarkerTop { get; set; }

        public double IntroductionTop { get; set; }

        public double PageThreeFooterBottom { get; set; }

        public double PageFourMarkerTop { get; set; }

        public double PageThreeFooterCenterOffset { get; set; }

        public double PageSixFooterCenterOffset { get; set; }

        public double ChildRightOverflow { get; set; }
    }

    private sealed class GridRenderMetrics
    {
        public int RowCount { get; set; }

        public int HighlightCount { get; set; }

        public double FirstCellLeft { get; set; }

        public double SecondCellLeft { get; set; }

        public double FirstCellTop { get; set; }

        public double FirstRowStep { get; set; }

        public double FirstHighlightWidth { get; set; }
    }

    private sealed class ColumnGridRenderMetrics
    {
        public int ColumnCount { get; set; }

        public double GridLeft { get; set; }

        public double GridRight { get; set; }

        public double GridWidth { get; set; }

        public double[] Lefts { get; set; } = [];

        public double[] Rights { get; set; } = [];

        public double[] Widths { get; set; } = [];
    }

    private sealed class BrowserRenderComparison
    {
        public BrowserRenderComparison(List<string> mismatches, byte[] browserPagePng, byte[] pdfPagePng)
        {
            Mismatches = mismatches;
            BrowserPagePng = browserPagePng;
            PdfPagePng = pdfPagePng;
        }

        public List<string> Mismatches { get; }

        public byte[] BrowserPagePng { get; }

        public byte[] PdfPagePng { get; }
    }

    private sealed class ForegroundShapeStats
    {
        private ForegroundShapeStats(float foregroundDeltaRatio, float pdfMissRatio, float browserMissRatio)
        {
            ForegroundDeltaRatio = foregroundDeltaRatio;
            PdfMissRatio = pdfMissRatio;
            BrowserMissRatio = browserMissRatio;
        }

        public float ForegroundDeltaRatio { get; }

        public float PdfMissRatio { get; }

        public float BrowserMissRatio { get; }

        public static ForegroundShapeStats? Create(
            BufferedImage pdfPage,
            BufferedImage browserPage,
            int luminanceThreshold,
            int dilationRadius)
        {
            bool[] pdfMask = ForegroundMask(pdfPage, luminanceThreshold);
            bool[] browserMask = ForegroundMask(browserPage, luminanceThreshold);
            int pdfForeground = pdfMask.Count(static foreground => foreground);
            int browserForeground = browserMask.Count(static foreground => foreground);
            int maxForeground = Math.Max(pdfForeground, browserForeground);
            if (maxForeground == 0)
            {
                return null;
            }

            bool[] dilatedPdfMask = DilateMask(pdfMask, pdfPage.Width, pdfPage.Height, dilationRadius);
            bool[] dilatedBrowserMask = DilateMask(browserMask, browserPage.Width, browserPage.Height, dilationRadius);
            int pdfMisses = CountMisses(pdfMask, dilatedBrowserMask);
            int browserMisses = CountMisses(browserMask, dilatedPdfMask);
            return new ForegroundShapeStats(
                MathF.Abs(pdfForeground - browserForeground) / (float)maxForeground,
                pdfMisses / (float)maxForeground,
                browserMisses / (float)maxForeground);
        }

        private static bool[] ForegroundMask(BufferedImage image, int luminanceThreshold)
        {
            bool[] mask = new bool[image.Width * image.Height];
            int index = 0;
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    int argb = image.GetRgb(x, y);
                    int alpha = (argb >> 24) & 0xFF;
                    if (alpha == 0)
                    {
                        index++;
                        continue;
                    }

                    int red = CompositeOnWhite((argb >> 16) & 0xFF, alpha);
                    int green = CompositeOnWhite((argb >> 8) & 0xFF, alpha);
                    int blue = CompositeOnWhite(argb & 0xFF, alpha);
                    int luminance = ((red * 299) + (green * 587) + (blue * 114)) / 1000;
                    mask[index++] = luminance < luminanceThreshold;
                }
            }

            return mask;
        }

        private static int CompositeOnWhite(int channel, int alpha)
        {
            return alpha >= 255 ? channel : ((channel * alpha) + (255 * (255 - alpha))) / 255;
        }

        private static bool[] DilateMask(bool[] mask, int width, int height, int radius)
        {
            if (radius <= 0)
            {
                return (bool[])mask.Clone();
            }

            bool[] dilated = new bool[mask.Length];
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (!mask[rowOffset + x])
                    {
                        continue;
                    }

                    int minX = Math.Max(0, x - radius);
                    int maxX = Math.Min(width - 1, x + radius);
                    int minY = Math.Max(0, y - radius);
                    int maxY = Math.Min(height - 1, y + radius);
                    for (int yy = minY; yy <= maxY; yy++)
                    {
                        int offset = yy * width;
                        for (int xx = minX; xx <= maxX; xx++)
                        {
                            dilated[offset + xx] = true;
                        }
                    }
                }
            }

            return dilated;
        }

        private static int CountMisses(bool[] source, bool[] target)
        {
            int misses = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] && !target[i])
                {
                    misses++;
                }
            }

            return misses;
        }
    }

    private static XDocument ParseHtml(string html)
    {
        string xml = Regex.Replace(html, "<!doctype html>\\s*", "", RegexOptions.IgnoreCase);
        return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
    }

    private static IEnumerable<XElement> ElementsByClass(XDocument document, string className)
    {
        return document.Descendants()
            .Where(element => HasClass(element, className));
    }

    private static bool HasClass(XElement element, string className)
    {
        return (element.Attribute("class")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
            .Contains(className, StringComparer.Ordinal);
    }

    private static void AssertVisuallyHiddenAdditionalBacklinks(XElement footnote, int expectedCount)
    {
        XElement additionalBacklinks = Assert.Single(footnote.Descendants(), element =>
            HasClass(element, "pdf-semantic-footnote-backrefs"));
        XElement[] links = additionalBacklinks.Elements("a").ToArray();
        Assert.Equal(expectedCount, links.Length);
        Assert.All(links, link => Assert.True(HasClass(link, "pdf-semantic-footnote-backref")));
    }

    private static Dictionary<string, string> ParseStyle(string style)
    {
        return style.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0].Trim(),
                parts => parts[1].Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static PdfLayoutRectangle RectangleFromStyle(Dictionary<string, string> style)
    {
        return new PdfLayoutRectangle(
            ParsePoints(style["left"]),
            ParsePoints(style["top"]),
            ParsePoints(style["width"]),
            ParsePoints(style["height"]));
    }

    private static float ParsePoints(string value)
    {
        Assert.EndsWith("pt", value);
        return float.Parse(value[..^2], CultureInfo.InvariantCulture);
    }

    private static float ParsePercent(string value)
    {
        Assert.EndsWith("%", value);
        return float.Parse(value[..^1], CultureInfo.InvariantCulture);
    }

    private static void AssertClose(float expected, float actual)
    {
        Assert.InRange(actual, expected - 0.01f, expected + 0.01f);
    }

    private static void AssertWithin(float tolerance, float expected, float actual)
    {
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
    }

    private static float TextCoverage(string expected, string actual)
    {
        Dictionary<string, int> expectedCounts = TokenCounts(expected);
        Dictionary<string, int> actualCounts = TokenCounts(actual);
        int total = expectedCounts.Values.Sum();
        int matched = expectedCounts.Sum(pair => Math.Min(pair.Value, actualCounts.GetValueOrDefault(pair.Key)));
        return total == 0 ? 1 : matched / (float)total;
    }

    private static Dictionary<string, int> TokenCounts(string value)
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(value, "\\w+|[^\\w\\s]"))
        {
            counts[match.Value] = counts.GetValueOrDefault(match.Value) + 1;
        }

        return counts;
    }

    private static float ForegroundIntersectionOverUnion(
        IReadOnlyList<PdfLayoutRectangle> expected,
        IReadOnlyList<PdfLayoutRectangle> actual)
    {
        if (expected.Count == 0 && actual.Count == 0)
        {
            return 1;
        }

        float intersection = 0;
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            intersection += IntersectionArea(expected[i], actual[i]);
        }

        float expectedArea = expected.Sum(Area);
        float actualArea = actual.Sum(Area);
        float union = expectedArea + actualArea - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static float IntersectionArea(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        float left = MathF.Max(first.X, second.X);
        float top = MathF.Max(first.Y, second.Y);
        float right = MathF.Min(first.Right, second.Right);
        float bottom = MathF.Min(first.Bottom, second.Bottom);
        return MathF.Max(0, right - left) * MathF.Max(0, bottom - top);
    }

    private static float Area(PdfLayoutRectangle rectangle)
    {
        return MathF.Max(0, rectangle.Width) * MathF.Max(0, rectangle.Height);
    }

    private static IEnumerable<string> BrokenLocalReferences(string htmlPath)
    {
        string html = File.ReadAllText(htmlPath);
        foreach (Match match in Regex.Matches(html, "(?:href|src)=\"(?<reference>[^\"]+)\""))
        {
            string reference = match.Groups["reference"].Value;
            if (reference.Contains(":", StringComparison.Ordinal) || reference.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string resolved = Path.Combine(Path.GetDirectoryName(htmlPath)!, reference.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(resolved))
            {
                yield return reference;
            }
        }
    }

    private static PdfTextRun CreateRun(
        string text,
        string fontName,
        float fontSize,
        PdfLayoutRectangle bounds,
        PdfLayoutColor color)
    {
        PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
        return new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, [glyph]);
    }

    private static PdfLayoutDocument CreateScientificFrontMatterLayoutFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateScientificFixtureLine("Reusable Scientific Front Matter", 106f, 70f, 400f, 18f, "Times-Bold"),
            CreateScientificFixtureLine("Ada Lovelace and Emmy Noether", 181f, 112f, 250f),
            CreateScientificFixtureLine("†", 80f, 138f, 8f, 8f, "Symbol"),
            CreateScientificFixtureLine("Department of Applied Mathematics, Example University", 92f, 138f, 428f),
            CreateScientificFixtureLine("‡", 110f, 152f, 8f, 8f, "Symbol"),
            CreateScientificFixtureLine("Center for Computational Science, Example City", 122f, 152f, 368f),
            CreateScientificFixtureLine("§", 142f, 166f, 8f, 8f, "Symbol"),
            CreateScientificFixtureLine("Institute for Scientific Computing", 154f, 166f, 316f),
            CreateScientificFixtureLine("September 2008", 256f, 196f, 100f),
            CreateScientificFixtureLine("WWW home page: https://example.edu/research", 126f, 220f, 360f, 9f),
            CreateScientificFixtureLine("Abstract. This study introduces a reusable semantic grouping strategy for papers.", 108f, 260f, 396f),
            CreateScientificFixtureLine("It preserves source rows while keeping the abstract body in normal document flow.", 108f, 273f, 396f),
            CreateScientificFixtureLine("The strategy uses layout evidence instead of document-specific titles or names.", 108f, 286f, 396f),
            CreateScientificFixtureLine("A narrow quotation remains", 206f, 330f, 200f),
            CreateScientificFixtureLine("intentionally narrow.", 218f, 343f, 176f),
            CreateScientificFixtureLine("1 Introduction", 72f, 380f, 110f, 13f, "Times-Bold")
        ];
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateRichListLayoutFixture()
    {
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        List<PdfTextRun> richRuns = [];
        float x = 72f;
        void AddRun(string text, string fontName, float fontSize = 12f, float yOffset = 0f)
        {
            float width = MathF.Max(4f, text.Length * fontSize * 0.48f);
            PdfLayoutRectangle bounds = new(x, 120f + yOffset, width, fontSize * 0.75f);
            PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
            richRuns.Add(new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, [glyph]));
            x += width;
        }

        AddRun("• ", "Symbol");
        AddRun("Bold", "Times-Bold");
        AddRun(" and ", "Times-Roman");
        AddRun("italic", "Times-Italic");
        AddRun(" H", "Times-Roman");
        AddRun("2", "Times-Roman", 7f, 4f);
        AddRun("O x", "Times-Roman");
        AddRun("3", "Times-Roman", 7f, -4f);
        AddRun(" visit https://example.com/list note ", "Times-Roman");
        AddRun("*", "Times-Roman", 7f, -4f);
        float richTop = richRuns.Min(static run => run.Bounds.Y);
        float richBottom = richRuns.Max(static run => run.Bounds.Bottom);
        PdfTextLine richItem = new(
            string.Concat(richRuns.Select(static run => run.Text)),
            new PdfLayoutRectangle(72f, richTop, x - 72f, richBottom - richTop),
            richRuns);

        List<PdfTextLine> lines =
        [
            CreateScientificFixtureLine("Opening body line establishes normal prose.", 72f, 72f, 260f, 12f),
            CreateScientificFixtureLine("A second line establishes vertical rhythm.", 72f, 84f, 280f, 12f),
            CreateScientificFixtureLine("A third line keeps the page in continuous semantic flow.", 72f, 96f, 300f, 12f),
            richItem,
            CreateScientificFixtureLine("• Plain second item", 72f, 140f, 160f, 12f),
            CreateScientificFixtureLine("*", 72f, 620f, 6f, 8f, "Symbol"),
            CreateScientificFixtureLine("Rich list footnote body.", 84f, 636f, 150f, 8f)
        ];
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            lines.Select(line => new PdfTextBlock(line.Text, line.Bounds, [line])).ToArray(),
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateCrossPageFootnoteLayoutFixture()
    {
        List<PdfTextLine> firstPage =
        [
            CreateScientificFixtureLine("Opening prose cites note * and repeats *.", 72f, 72f, 280f, 12f),
            CreateScientificFixtureLine("A second body line establishes ordinary page rhythm.", 72f, 86f, 320f, 12f),
            CreateScientificFixtureLine("The discussion continues with enough source lines for normal flow.", 72f, 104f, 390f, 12f),
            CreateScientificFixtureLine("Each line remains part of the same uncomplicated prose column.", 72f, 118f, 370f, 12f),
            CreateScientificFixtureLine("Semantic extraction can therefore retain the native reading order.", 72f, 136f, 390f, 12f),
            CreateScientificFixtureLine("No tables, grids, or side-by-side regions occur on this page.", 72f, 150f, 360f, 12f),
            CreateScientificFixtureLine("The remaining prose fills an ordinary single-column document.", 72f, 168f, 370f, 12f),
            CreateScientificFixtureLine("Another complete sentence keeps the fixture representative.", 72f, 182f, 350f, 12f),
            CreateScientificFixtureLine("The final body sentence appears well before the bottom note.", 72f, 200f, 360f, 12f),
            CreateScientificFixtureLine("*", 72f, 620f, 6f, 8f, "Symbol"),
            CreateScientificFixtureLine("A note starts near the page boundary and", 84f, 620f, 230f, 8f)
        ];
        List<PdfTextLine> secondPage =
        [
            CreateScientificFixtureLine("continues on the next page.", 72f, 72f, 170f, 8f),
            CreateScientificFixtureLine("Ordinary body prose resumes after the continued note.", 72f, 132f, 330f, 12f),
            CreateScientificFixtureLine("The second page also follows a simple single-column reading order.", 72f, 146f, 390f, 12f),
            CreateScientificFixtureLine("Its source lines are intentionally ordinary prose content.", 72f, 164f, 330f, 12f),
            CreateScientificFixtureLine("There are no geometric structures requiring special handling.", 72f, 178f, 350f, 12f),
            CreateScientificFixtureLine("Additional sentences keep the page on the semantic flow path.", 72f, 196f, 360f, 12f),
            CreateScientificFixtureLine("The note continuation remains the first meaningful element.", 72f, 210f, 350f, 12f),
            CreateScientificFixtureLine("Later body content remains separate from that note item.", 72f, 228f, 330f, 12f),
            CreateScientificFixtureLine("The final sentence completes the focused regression fixture.", 72f, 242f, 350f, 12f)
        ];
        return CreateDefinitionLayoutDocument([firstPage, secondPage]);
    }

    private static PdfLayoutDocument CreateDocumentIndexLayoutFixture(
        bool includeImages,
        bool includeUnrelatedImage = false)
    {
        PdfTextLine[] lines =
        [
            CreateScientificFixtureLine("Table of Contents", 72f, 72f, 220f, 14f, "Times-Bold"),
            CreateScientificFixtureLine("1. Introduction ........................ 1", 72f, 104f, 468f, 11f),
            CreateScientificFixtureLine("1.1. Scope .............................. 2", 96f, 120f, 444f, 11f),
            CreateScientificFixtureLine("Appendix A. Controls .................. A-1", 72f, 456f, 468f, 11f),
            CreateScientificFixtureLine("A.1. Exceptions ....................... A-2", 96f, 472f, 444f, 11f)
        ];
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutImage[] images = includeImages
            ? Enumerable.Range(0, 42)
                .Select(index => new PdfLayoutImage(
                    index,
                    $"index-row-asset-{index}",
                    PdfLayoutImageKind.XObject,
                    new PdfLayoutRectangle(
                        index % 2 == 0 ? 80f : 112f,
                        180f + index / 2 * 12f,
                        index % 2 == 0 ? 20f : 400f,
                        8f),
                    new PdfLayoutTransform(
                        index % 2 == 0 ? 20f : 400f,
                        0f,
                        0f,
                        8f,
                        index % 2 == 0 ? 80f : 112f,
                        180f + index / 2 * 12f),
                    index % 2 == 0 ? 20 : 400,
                    8,
                    8,
                    "DeviceRGB",
                    false,
                    null))
                .ToArray()
            : includeUnrelatedImage
                ?
                [
                    new PdfLayoutImage(
                        0,
                        "index-page-logo",
                        PdfLayoutImageKind.XObject,
                        new PdfLayoutRectangle(500f, 24f, 64f, 24f),
                        new PdfLayoutTransform(64f, 0f, 0f, 24f, 500f, 24f),
                        64,
                        24,
                        8,
                        "DeviceRGB",
                        false,
                        null)
                ]
                : [];
        PdfLayoutLink[] links =
        [
            CreateDocumentIndexLayoutLink(0, 100f, 2),
            CreateDocumentIndexLayoutLink(1, 116f, 2),
            CreateDocumentIndexLayoutLink(2, 452f, 2),
            CreateDocumentIndexLayoutLink(3, 468f, 2),
            .. (includeImages
                ? Enumerable.Range(0, 21)
                    .Select(index => CreateDocumentIndexLayoutLink(4 + index, 178f + index * 12f, 2))
                : Enumerable.Empty<PdfLayoutLink>())
        ];
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage indexPage = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            images,
            [],
            [],
            links,
            []);
        PdfLayoutPage destinationPage = new(
            2,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        PdfLayoutImageAsset[] imageAssets = images
            .Select(image => new PdfLayoutImageAsset(
                image.AssetId,
                $"assets/images/{image.AssetId}.png",
                "image/png",
                [137, 80, 78, 71]))
            .ToArray();
        return new PdfLayoutDocument([indexPage, destinationPage], imageAssets, []);
    }

    private static PdfLayoutDocument CreateBibliographyLayoutFixture()
    {
        PdfTextLine citation = CreateBibliographyStyledLine(
            72f,
            72f,
            ("Prior work ", "Times-Roman"),
            ("[3]", "Times-Roman"),
            (" establishes the baseline.", "Times-Roman"));
        PdfTextLine secondCitation = CreateBibliographyStyledLine(
            72f,
            88f,
            ("A later comparison ", "Times-Roman"),
            ("[5]", "Times-Roman"),
            (" confirms it.", "Times-Roman"));
        PdfLayoutPage firstPage = CreateBibliographyLayoutPage(
            1,
            [
                CreateScientificFixtureLine("Opening scientific prose establishes the body font.", 72f, 40f, 340f),
                CreateScientificFixtureLine("A second line establishes ordinary paragraph rhythm.", 72f, 56f, 340f),
                citation,
                secondCitation
            ],
            [
                new PdfLayoutLink(
                    0,
                    citation.Runs[1].Bounds,
                    PdfLayoutLinkKind.Destination,
                    null,
                    "cite.Lovelace2020",
                    null,
                    []),
                new PdfLayoutLink(
                    1,
                    secondCitation.Runs[1].Bounds,
                    PdfLayoutLinkKind.Destination,
                    null,
                    "cite.Noether2022",
                    null,
                    [])
            ]);
        PdfLayoutPage secondPage = CreateBibliographyLayoutPage(
            2,
            [
                CreateScientificFixtureLine("References", 72f, 52f, 100f, 14f, "Times-Bold"),
                CreateBibliographyStyledLine(
                    72f,
                    86f,
                    ("[3] Lovelace, A. (2020). ", "Times-Roman"),
                    ("Analytical Engines", "Times-Italic"),
                    (". https://doi.org/10.1000/first", "Times-Roman")),
                CreateScientificFixtureLine("The discussion continues to the bottom of this source page", 88f, 100f, 360f)
            ]);
        PdfLayoutPage thirdPage = CreateBibliographyLayoutPage(
            3,
            [
                CreateScientificFixtureLine("and is continued on the next source page.", 88f, 52f, 300f),
                CreateBibliographyStyledLine(
                    72f,
                    106f,
                    ("[5] ", "Times-Roman"),
                    ("Noether, E.", "Times-Italic"),
                    (" (2022). Symmetry in modern physics.", "Times-Roman"))
            ]);
        return new PdfLayoutDocument([firstPage, secondPage, thirdPage], []);
    }

    private static PdfLayoutDocument CreateSemanticTableCaptionLayoutFixture()
    {
        PdfTextLine caption = CreateBibliographyStyledLine(
            126f,
            124f,
            ("Table 7: ", "Times-Roman"),
            ("Linked benchmark", "Times-Bold"),
            (" details.", "Times-Roman"));
        PdfTextLine[] lines =
        [
            CreateScientificFixtureLine("2 Results", 72f, 52f, 100f, 14f, "Times-Bold"),
            CreateScientificFixtureLine("Opening prose establishes the ordinary body font.", 72f, 80f, 320f),
            CreateScientificFixtureLine("A second line establishes normal source rhythm.", 72f, 94f, 310f),
            caption,
            CreateSemanticTableRowLine(152f,
                ("Cohort", 96f, 100f),
                ("Baseline", 260f, 74f),
                ("Outcome", 382f, 78f)),
            CreateSemanticTableRowLine(166f,
                ("Alpha", 96f, 100f),
                ("10", 260f, 74f),
                ("12", 382f, 78f)),
            CreateSemanticTableRowLine(180f,
                ("Beta", 96f, 100f),
                ("14", 260f, 74f),
                ("18", 382f, 78f)),
            CreateSemanticTableRowLine(194f,
                ("Gamma", 96f, 100f),
                ("21", 260f, 74f),
                ("25", 382f, 78f)),
            CreateScientificFixtureLine("Closing prose remains outside the detected table.", 72f, 240f, 300f)
        ];
        PdfLayoutPage page = CreateBibliographyLayoutPage(
            1,
            lines,
            [
                new PdfLayoutLink(
                    0,
                    caption.Runs[1].Bounds,
                    PdfLayoutLinkKind.Uri,
                    "https://example.test/benchmark",
                    null,
                    null,
                    [])
            ]);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateSemanticTableCaptionAlignmentLayoutFixture()
    {
        return new PdfLayoutDocument(
            [
                CreateSemanticTableCaptionAlignmentPage(1, 7, 96f, 96f),
                CreateSemanticTableCaptionAlignmentPage(2, 8, 128f, 188f),
                CreateSemanticTableCaptionAlignmentPage(3, 9, 160f, 280f)
            ],
            []);
    }

    private static PdfLayoutPage CreateSemanticTableCaptionAlignmentPage(
        int pageNumber,
        int tableNumber,
        float firstLineX,
        float secondLineX)
    {
        PdfTextLine[] lines =
        [
            CreateScientificFixtureLine($"{pageNumber} Results", 72f, 52f, 100f, 14f, "Times-Bold"),
            CreateScientificFixtureLine("Opening prose establishes the ordinary body font.", 72f, 80f, 320f),
            CreateScientificFixtureLine("A second line establishes normal source rhythm.", 72f, 94f, 310f),
            CreateScientificFixtureLine($"Table {tableNumber}: Wrapped caption alignment", firstLineX, 112f, 300f),
            CreateScientificFixtureLine("continues on a second source line.", secondLineX, 124f, 180f),
            CreateSemanticTableRowLine(152f,
                ("Cohort", 96f, 100f),
                ("Baseline", 260f, 74f),
                ("Outcome", 382f, 78f)),
            CreateSemanticTableRowLine(166f,
                ("Alpha", 96f, 100f),
                ("10", 260f, 74f),
                ("12", 382f, 78f)),
            CreateSemanticTableRowLine(180f,
                ("Beta", 96f, 100f),
                ("14", 260f, 74f),
                ("18", 382f, 78f)),
            CreateSemanticTableRowLine(194f,
                ("Gamma", 96f, 100f),
                ("21", 260f, 74f),
                ("25", 382f, 78f)),
            CreateScientificFixtureLine("Closing prose remains outside the detected table.", 72f, 240f, 300f)
        ];
        return CreateBibliographyLayoutPage(pageNumber, lines);
    }

    private static PdfTextLine CreateSemanticTableRowLine(
        float y,
        params (string Text, float X, float Width)[] cells)
    {
        PdfTextRun[] runs = cells
            .Select(cell => CreateScientificFixtureLine(cell.Text, cell.X, y, cell.Width).Runs.Single())
            .ToArray();
        float left = runs.Min(static run => run.Bounds.X);
        float top = runs.Min(static run => run.Bounds.Y);
        float right = runs.Max(static run => run.Bounds.Right);
        float bottom = runs.Max(static run => run.Bounds.Bottom);
        return new PdfTextLine(
            string.Join(' ', cells.Select(static cell => cell.Text)),
            new PdfLayoutRectangle(left, top, right - left, bottom - top),
            runs);
    }

    private static PdfLayoutDocument CreateInlineSemanticLayoutFixture()
    {
        PdfTextLine[] lines =
        [
            CreateScientificFixtureLine("Published: March 14, 2024", 72f, 20f, 190f, 10f),
            CreateScientificFixtureLine("Updated: 03/04/2024", 310f, 20f, 150f, 10f),
            CreateScientificFixtureLine("Opening body text establishes the ordinary font size.", 72f, 72f, 340f, 12f),
            CreateScientificFixtureLine("A second body line establishes normal document rhythm.", 72f, 88f, 340f, 12f),
            CreateScientificFixtureLine("World Health Organization (WHO) issued guidance.", 72f, 126f, 340f, 12f),
            CreateScientificFixtureLine("NASA issued separate guidance without an expansion.", 72f, 144f, 330f, 12f),
            CreateScientificFixtureLine("2024-04-05 - Public consultation opened.", 72f, 180f, 280f, 12f),
            CreateBibliographyStyledLine(
                72f,
                216f,
                ("Figure 1. Adapted from ", "Times-Roman"),
                ("The Design of Everyday Things", "Times-Italic"),
                (".", "Times-Roman")),
            CreateBibliographyStyledLine(
                72f,
                246f,
                ("This is ", "Times-Roman"),
                ("important emphasis", "Times-Italic"),
                (" in ordinary prose.", "Times-Roman")),
            CreateScientificFixtureLine("Ordinary smaller text is not ancillary.", 72f, 700f, 220f, 8f),
            CreateScientificFixtureLine("Copyright 2026 Example. All rights reserved.", 72f, 744f, 270f, 8f)
        ];
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            lines.Select(line => new PdfTextBlock(line.Text, line.Bounds, [line])).ToArray(),
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutPage CreateBibliographyLayoutPage(
        int pageNumber,
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfLayoutLink>? links = null)
    {
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        return new PdfLayoutPage(
            pageNumber,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            links ?? [],
            []);
    }

    private static PdfTextLine CreateBibliographyStyledLine(
        float x,
        float y,
        params (string Text, string FontName)[] segments)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        List<PdfTextRun> runs = [];
        float segmentX = x;
        foreach ((string text, string fontName) in segments)
        {
            float width = MathF.Max(4f, text.Length * 5f);
            PdfLayoutRectangle bounds = new(segmentX, y, width, fontSize * 0.75f);
            PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
            runs.Add(new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, [glyph]));
            segmentX += width;
        }

        return new PdfTextLine(
            string.Concat(segments.Select(static segment => segment.Text)),
            new PdfLayoutRectangle(x, y, segmentX - x, fontSize * 0.75f),
            runs);
    }

    private static PdfLayoutLink CreateDocumentIndexLayoutLink(int index, float y, int destinationPageNumber)
    {
        return new PdfLayoutLink(
            index,
            new PdfLayoutRectangle(68f, y, 476f, 14f),
            PdfLayoutLinkKind.Destination,
            null,
            $"page:{destinationPageNumber}",
            destinationPageNumber,
            []);
    }

    private static PdfTextLine CreateScientificFixtureLine(
        string text,
        float x,
        float y,
        float width,
        float fontSize = 10f,
        string fontName = "Times-Roman")
    {
        PdfLayoutRectangle bounds = new(x, y, width, fontSize * 0.75f);
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
        PdfTextRun run = new(text, fontName, fontSize, 0f, bounds, color, [glyph]);
        return new PdfTextLine(text, bounds, [run]);
    }

    private static PdfTextLine CreateMonospacedSemanticFixtureLine(
        string text,
        float x,
        float y,
        float characterPitch = 6f)
    {
        const float fontSize = 10f;
        const string fontName = "Courier";
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph[] glyphs = text
            .Select((character, index) => (character, index))
            .Where(static item => !char.IsWhiteSpace(item.character))
            .Select(item => new PdfTextGlyph(
                item.character.ToString(),
                fontName,
                fontSize,
                0f,
                new PdfLayoutRectangle(x + item.index * characterPitch, y, characterPitch, fontSize * 0.75f),
                color))
            .ToArray();
        PdfLayoutRectangle bounds = new(x, y, text.Length * characterPitch, fontSize * 0.75f);
        PdfTextRun run = new(text, fontName, fontSize, 0f, bounds, color, glyphs);
        return new PdfTextLine(text, bounds, [run]);
    }

    private static PdfTextLine[] CreateSemanticCodeFixtureLines()
    {
        return
        [
            CreateScientificFixtureLine("Opening prose establishes the ordinary body font and line rhythm.", 72f, 72f, 410f),
            CreateScientificFixtureLine("A second prose line completes the surrounding paragraph.", 72f, 84f, 340f),
            CreateMonospacedSemanticFixtureLine("LOAD  R0, [R1]", 96f, 124f),
            CreateMonospacedSemanticFixtureLine("STORE R0, [R2]", 96f, 136f),
            CreateMonospacedSemanticFixtureLine("  ADD R0, #1", 96f, 148f),
            CreateInlineCodeSemanticFixtureLine("Use the ", "gpio_set()", " helper to configure the pin.", 72f, 184f),
            CreateScientificFixtureLine("Ordinary prose continues after the listing in normal document flow.", 72f, 208f, 390f),
            CreateScientificFixtureLine("Another body line keeps the page outside sparse-content fallback.", 72f, 220f, 380f),
            CreateScientificFixtureLine("The final sentence closes the surrounding technical discussion.", 72f, 232f, 370f)
        ];
    }

    private static PdfTextLine CreateInlineCodeSemanticFixtureLine(
        string prefix,
        string code,
        string suffix,
        float x,
        float y)
    {
        PdfTextRun prefixRun = CreatePositionedSemanticFixtureRun(prefix, "Times-Roman", x, y, 5f);
        PdfTextRun codeRun = CreatePositionedSemanticFixtureRun(code, "Courier", prefixRun.Bounds.Right, y, 6f);
        PdfTextRun suffixRun = CreatePositionedSemanticFixtureRun(suffix, "Times-Roman", codeRun.Bounds.Right, y, 5f);
        PdfTextRun[] runs = [prefixRun, codeRun, suffixRun];
        return new PdfTextLine(
            prefix + code + suffix,
            new PdfLayoutRectangle(x, y, suffixRun.Bounds.Right - x, suffixRun.Bounds.Height),
            runs);
    }

    private static PdfTextRun CreatePositionedSemanticFixtureRun(
        string text,
        string fontName,
        float x,
        float y,
        float characterPitch)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph[] glyphs = text
            .Select((character, index) => new PdfTextGlyph(
                character.ToString(),
                fontName,
                fontSize,
                0f,
                new PdfLayoutRectangle(x + index * characterPitch, y, characterPitch, fontSize * 0.75f),
                color))
            .ToArray();
        PdfLayoutRectangle bounds = new(x, y, text.Length * characterPitch, fontSize * 0.75f);
        return new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, glyphs);
    }

    private static PdfLayoutDocument CreateDefinitionListLayoutFixture(bool columns)
    {
        List<PdfTextLine> lines =
        [
            CreateDefinitionFixtureLine("Opening context establishes ordinary body text for semantic inference.", 72f, 72f, 410f),
            CreateDefinitionFixtureLine("A second context line completes the introductory paragraph.", 72f, 84f, 360f),
            CreateDefinitionPairLine("API", "Application programming interface", 120f, columns),
            CreateDefinitionPairLine("CUI", "Controlled Unclassified Information", 140f, columns),
            CreateDefinitionPairLine("MFA", "Multi-factor authentication", 160f, columns),
            CreateDefinitionPairLine("SIEM", "Security information and event management", 180f, columns)
        ];
        return CreateDefinitionLayoutDocument([lines]);
    }

    private static PdfLayoutDocument CreateSemanticHtmlFixture(
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfLayoutPath>? paths = null)
    {
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            paths ?? [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateSideBySideSemanticRuleLayoutFixture()
    {
        List<PdfTextLine> lines = [];
        for (int index = 0; index < 12; index++)
        {
            float leftY = index < 6 ? 72f + index * 12f : 220f + (index - 6) * 12f;
            float rightY = 72f + index * 12f;
            lines.Add(CreateScientificFixtureLine($"Left column flow line {index + 1}", 72f, leftY, 210f));
            lines.Add(CreateScientificFixtureLine($"Right column flow line {index + 1}", 330f, rightY, 210f));
        }

        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        return CreateSemanticHtmlFixture(
            lines,
            [CreateSemanticRulePath(11, 72f, 176f, 290f, 176f, 0.5f, color)]);
    }

    private static PdfLayoutPath CreateSemanticCalloutPath(PdfLayoutRectangle bounds)
    {
        PdfLayoutColor fill = new(0.92f, 0.94f, 0.96f, 1f, "DeviceRGB");
        PdfLayoutStrokeStyle stroke = new(
            new PdfLayoutColor(0.2f, 0.4f, 0.6f, 1f, "DeviceRGB"),
            1.5f,
            0,
            0,
            10f,
            [],
            0f);
        return new PdfLayoutPath(0, [], bounds, fill, stroke, fillRule: 1);
    }

    private static PdfLayoutPath CreateSemanticRulePath(
        int index,
        float startX,
        float startY,
        float endX,
        float endY,
        float strokeWidth,
        PdfLayoutColor color)
    {
        PdfLayoutStrokeStyle stroke = new(color, strokeWidth, 0, 0, 10f, [], 0f);
        return new PdfLayoutPath(
            index,
            [
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, startX, startY, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, endX, endY, 0f, 0f, 0f, 0f)
            ],
            new PdfLayoutRectangle(
                MathF.Min(startX, endX),
                MathF.Min(startY, endY),
                MathF.Abs(endX - startX),
                MathF.Abs(endY - startY)),
            null,
            stroke,
            null);
    }

    private static PdfLayoutDocument CreateDefinitionListAliasLayoutFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateDefinitionFixtureLine("Opening context establishes ordinary body text for semantic inference.", 72f, 72f, 410f),
            CreateDefinitionFixtureLine("A second context line completes the introductory paragraph.", 72f, 84f, 360f),
            CreateDefinitionPairLine("API", "Application programming interfaces provide", 120f, columns: true),
            CreateDefinitionPairLine("application interface", "access to reusable software operations.", 140f, columns: true),
            CreateDefinitionPairLine("CUI", "Controlled Unclassified Information", 164f, columns: true),
            CreateDefinitionPairLine("MFA", "Multi-factor authentication", 184f, columns: true),
            CreateDefinitionPairLine("SIEM", "Security information and event management", 204f, columns: true)
        ];
        return CreateDefinitionLayoutDocument([lines]);
    }

    private static PdfLayoutDocument CreateCrossPageDefinitionListLayoutFixture(bool definitionContinues = true)
    {
        List<PdfTextLine> firstPage =
        [
            CreateDefinitionFixtureLine("agency", 72f, 520f, 48f, "Times-Bold"),
            CreateDefinitionFixtureLine("An executive department or government organization.", 72f, 532f, 330f),
            CreateDefinitionFixtureLine("assessment", 72f, 556f, 70f, "Times-Bold"),
            CreateDefinitionFixtureLine("See security control assessment.", 72f, 568f, 220f),
            CreateDefinitionFixtureLine("audit log", 72f, 592f, 60f, "Times-Bold"),
            CreateDefinitionFixtureLine("A chronological record of system activity.", 72f, 604f, 280f),
            CreateDefinitionFixtureLine("common secure configuration", 72f, 628f, 180f, "Times-Bold"),
            CreateDefinitionFixtureLine(
                definitionContinues ? "Recognized benchmarks for systems that meet" : "Recognized benchmarks for systems. [79]",
                72f,
                640f,
                300f)
        ];
        List<PdfTextLine> secondPage =
        [
            CreateDefinitionFixtureLine("May 2024", 72f, 51f, 70f),
            .. (definitionContinues
                ? new[] { CreateDefinitionFixtureLine("operational requirements and implementation guidance.", 72f, 75f, 340f) }
                : Array.Empty<PdfTextLine>()),
            CreateDefinitionFixtureLine("confidentiality", 72f, 110f, 90f, "Times-Bold"),
            CreateDefinitionFixtureLine("Preserving authorized restrictions on information access.", 72f, 122f, 360f),
            CreateDefinitionFixtureLine("configuration management", 72f, 146f, 160f, "Times-Bold"),
            CreateDefinitionFixtureLine("Activities that maintain the integrity of system configurations.", 72f, 158f, 390f),
            CreateDefinitionFixtureLine("controlled area", 72f, 182f, 100f, "Times-Bold"),
            CreateDefinitionFixtureLine("An area with sufficient physical and procedural protections.", 72f, 194f, 380f),
            CreateDefinitionFixtureLine("external network", 72f, 218f, 100f, "Times-Bold"),
            CreateDefinitionFixtureLine("A network not controlled by the organization.", 72f, 230f, 300f)
        ];
        return CreateDefinitionLayoutDocument([firstPage, secondPage]);
    }

    private static PdfTextLine CreateDefinitionPairLine(
        string term,
        string definition,
        float y,
        bool columns)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        float termWidth = columns ? 60f : MathF.Max(12f, term.Length * 6f);
        float definitionX = columns ? 210f : 72f + termWidth;
        PdfTextRun termRun = CreateRun(
            term,
            "Times-Bold",
            fontSize,
            new PdfLayoutRectangle(72f, y, termWidth, 7.5f),
            color);
        PdfTextRun definitionRun = CreateRun(
            (columns ? "" : " ") + definition,
            "Times-Roman",
            fontSize,
            new PdfLayoutRectangle(definitionX, y, 300f, 7.5f),
            color);
        PdfTextRun[] runs = [termRun, definitionRun];
        return new PdfTextLine(
            term + " " + definition,
            new PdfLayoutRectangle(72f, y, definitionX + 300f - 72f, 7.5f),
            runs);
    }

    private static PdfTextLine CreateDefinitionFixtureLine(
        string text,
        float x,
        float y,
        float width,
        string fontName = "Times-Roman")
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfLayoutRectangle bounds = new(x, y, width, 7.5f);
        PdfTextRun run = CreateRun(text, fontName, fontSize, bounds, color);
        return new PdfTextLine(text, bounds, [run]);
    }

    private static PdfLayoutDocument CreateDefinitionLayoutDocument(
        IReadOnlyList<IReadOnlyList<PdfTextLine>> pageLines)
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage[] pages = pageLines
            .Select((lines, index) =>
            {
                PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
                PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
                return new PdfLayoutPage(
                    index + 1,
                    pageBounds,
                    pageBounds,
                    pageBounds.Width,
                    pageBounds.Height,
                    0,
                    glyphs,
                    runs,
                    lines,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    []);
            })
            .ToArray();
        return new PdfLayoutDocument(pages, []);
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

    private static PDDocument CreateContinuingSectionDocument()
    {
        PDDocument document = new();
        string[] pageContent =
        [
            """
            BT /F1 14 Tf 72 735 Td (1 Continuing Section) Tj ET
            BT /F1 10 Tf 72 700 Td (First-page content ends here.) Tj ET
            BT /F1 10 Tf 72 684 Td (The section has enough body text for inference.) Tj ET
            BT /F1 10 Tf 72 668 Td (This final sentence also ends on page one.) Tj ET
            """,
            """
            BT /F1 10 Tf 72 735 Td (Second-page content remains in the section.) Tj ET
            BT /F1 10 Tf 72 719 Td (Another complete line prevents sparse fallback.) Tj ET
            """
        ];
        foreach (string content in pageContent)
        {
            PDPage page = new();
            document.AddPage(page);
            COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
            pageDictionary.SetItem(COSName.RESOURCES, CreateDefaultResourcesDictionary());
            pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream(content));
        }

        return document;
    }

    private static PDDocument CreateThreeColumnDocument(bool includeRuledTable)
    {
        StringBuilder content = new();
        content.AppendLine("BT /F1 16 Tf 150 750 Td (Three-Column Field Notes) Tj ET");
        content.AppendLine("BT /F1 10 Tf 195 730 Td (Measured source header) Tj ET");
        for (int index = 1; index <= 14; index++)
        {
            int y = 680 - (index - 1) * 14;
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 8 Tf 42 {0} Td (Alpha column {1:00} compact note) Tj ET\n",
                y,
                index);
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 8 Tf 194 {0} Td (Middle column {1:00} carries wider field notes) Tj ET\n",
                y - 4,
                index);
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 8 Tf 390 {0} Td (Right column {1:00} moderate copy) Tj ET\n",
                y - 8,
                index);
        }

        if (includeRuledTable)
        {
            content.AppendLine("0.5 w");
            for (int y = 692; y >= 482; y -= 14)
            {
                content.AppendFormat(CultureInfo.InvariantCulture, "36 {0} m 552 {0} l S\n", y);
            }

            foreach (int x in new[] { 36, 184, 380, 552 })
            {
                content.AppendFormat(CultureInfo.InvariantCulture, "{0} 482 m {0} 692 l S\n", x);
            }
        }

        return CreateTextDocument(content.ToString());
    }

    private static PDDocument CreateTwoColumnDocument(bool includeRuledTable)
    {
        return CreateTwoColumnDocument(includeRuledTable ? RuledTableLayout.Left : RuledTableLayout.None);
    }

    private static PDDocument CreateTwoColumnDocumentWithOrderedList(bool includeCrossColumnText = false)
    {
        StringBuilder content = new();
        content.AppendLine("BT /F1 16 Tf 190 760 Td (Two-Column Guidance) Tj ET");
        content.AppendLine("BT /F1 11 Tf 220 740 Td (Spanning source header) Tj ET");
        for (int index = 1; index <= 12; index++)
        {
            int y = 700 - (index - 1) * 16;
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 10 Tf 72 {0} Td (Left body {1:00} source line reaches near gutter) Tj ET\n",
                y,
                index);
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 10 Tf 330 {0} Td (Right body {1:00} source line reaches page edge) Tj ET\n",
                y,
                index);
        }

        if (includeCrossColumnText)
        {
            content.AppendLine("BT /F1 10 Tf 72 500 Td (1. First certification instruction reaches near gutter) Tj ET");
            content.AppendLine("BT /F1 10 Tf 330 500 Td (Right list note 1) Tj ET");
            content.AppendLine("BT /F1 10 Tf 72 484 Td (2. Second certification instruction reaches near gutter) Tj ET");
            content.AppendLine("BT /F1 10 Tf 330 484 Td (Right list note 2) Tj ET");
            content.AppendLine("BT /F1 10 Tf 72 468 Td (3. Third certification instruction reaches near gutter) Tj ET");
            content.AppendLine("BT /F1 10 Tf 330 468 Td (Right list note 3) Tj ET");
        }
        else
        {
            content.AppendLine("BT /F1 10 Tf 72 500 Td (1. First certification step) Tj ET");
            content.AppendLine("BT /F1 10 Tf 72 484 Td (2. Second certification step) Tj ET");
            content.AppendLine("BT /F1 10 Tf 72 468 Td (3. Third certification step) Tj ET");
        }

        return CreateTextDocument(content.ToString());
    }

    private static PDDocument CreateTwoColumnDocument(RuledTableLayout ruledTableLayout)
    {
        StringBuilder content = new();
        content.AppendLine("BT /F1 16 Tf 190 760 Td (Two-Column Guidance) Tj ET");
        content.AppendLine("BT /F1 11 Tf 220 740 Td (Spanning source header) Tj ET");
        for (int index = 1; index <= 12; index++)
        {
            int y = 700 - (index - 1) * 16;
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 10 Tf 72 {0} Td (Left body {1:00} source line reaches near gutter) Tj ET\n",
                y,
                index);
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 10 Tf 330 {0} Td (Right body {1:00} source line reaches page edge) Tj ET\n",
                y,
                index);
        }

        if (ruledTableLayout is RuledTableLayout.Left or RuledTableLayout.SideBySide)
        {
            AppendRuledTable(content, 72, 160, 248, 290, ruledTableLayout == RuledTableLayout.Left ? "Table" : "Left");
        }

        if (ruledTableLayout == RuledTableLayout.SideBySide)
        {
            content.AppendLine("72 720 m 548 720 l S");
            AppendRuledTable(content, 330, 418, 506, 548, "Right");
        }

        if (ruledTableLayout == RuledTableLayout.FullWidth)
        {
            AppendRuledTable(content, 72, 230, 390, 548, "Full");
        }

        return CreateTextDocument(content.ToString());
    }

    private static void AppendRuledTable(
        StringBuilder content,
        int left,
        int firstDivider,
        int secondDivider,
        int right,
        string label)
    {
        content.AppendLine("0.5 w");
        foreach (int y in new[] { 490, 472, 454, 436 })
        {
            content.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} m {2} {1} l S\n", left, y, right);
        }

        foreach (int x in new[] { left, firstDivider, secondDivider, right })
        {
            content.AppendFormat(CultureInfo.InvariantCulture, "{0} 436 m {0} 490 l S\n", x);
        }

        int[] textLefts = [left + 6, firstDivider + 10, secondDivider + 10];
        for (int row = 1; row <= 3; row++)
        {
            int y = 476 - (row - 1) * 18;
            for (int column = 0; column < textLefts.Length; column++)
            {
                content.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "BT /F1 8 Tf {0} {1} Td ({2} {3}{4}) Tj ET\n",
                    textLefts[column],
                    y,
                    label,
                    (char)('A' + column),
                    row);
            }
        }
    }

    private static PDDocument CreateFilledTableDocument()
    {
        StringBuilder content = new();
        content.AppendLine("BT /F1 16 Tf 190 760 Td (Filled Table Guidance) Tj ET");
        content.AppendLine("BT /F1 11 Tf 220 740 Td (Spanning source header) Tj ET");
        for (int index = 1; index <= 12; index++)
        {
            int y = 700 - (index - 1) * 16;
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 10 Tf 72 {0} Td (Left body {1:00} source line reaches near gutter) Tj ET\n",
                y,
                index);
            content.AppendFormat(
                CultureInfo.InvariantCulture,
                "BT /F1 10 Tf 330 {0} Td (Right body {1:00} source line reaches page edge) Tj ET\n",
                y,
                index);
        }

        content.AppendLine("1 1 0 rg 78 474.8 32 6.4 re f");
        content.AppendLine("0 g 0 G");
        AppendRuledTable(content, 72, 160, 248, 290, "Table");
        return CreateTextDocument(content.ToString());
    }

    private enum RuledTableLayout
    {
        None,
        Left,
        SideBySide,
        FullWidth
    }

    private static PDDocument CreateEmbeddedTrueTypeDocument(byte[] fontBytes)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        TrueTypeFont trueTypeFont = new TTFParser().Parse(fontBytes);
        COSDictionary descriptor = new();
        descriptor.SetItem(COSName.GetPDFName("FontFile2"), CreateBinaryStream(document, fontBytes));

        COSDictionary fontDictionary = new();
        fontDictionary.SetName(COSName.GetPDFName("Subtype"), "TrueType");
        fontDictionary.SetName(COSName.GetPDFName("BaseFont"), "EmbeddedLiberation");
        fontDictionary.SetItem(COSName.GetPDFName("FontDescriptor"), descriptor);

        PDResources resources = new();
        resources.Put(COSName.GetPDFName("F1"), new PDTrueTypeFont(fontDictionary, trueTypeFont));
        page.SetResources(resources);

        COSDictionary pageDictionary = (COSDictionary)page.GetCOSObject();
        pageDictionary.SetItem(COSName.CONTENTS, CreateContentStream("""
            BT
            /F1 12 Tf
            72 700 Td
            (Embedded font) Tj
            ET
            """));
        return document;
    }

    private static byte[] CreateMinimalRawType1Cff()
    {
        byte[] nameIndex = BuildCffIndex([Encoding.ASCII.GetBytes("RawCff")]);
        byte[] stringIndex = BuildCffIndex([]);
        byte[] globalSubrIndex = BuildCffIndex([]);
        byte[] privateDictionary = BuildCffDictionary(
            EncodeCffInteger(0),
            EncodeCffInteger(10),
            [6]);
        byte[] charStringsIndex = BuildCffIndex([[14], [14]]);
        byte[] charset = [0, 0, 34]; // format 0; glyph 1 is the standard CFF "A" SID.
        byte[] topDictionary = [];
        while (true)
        {
            byte[] topDictionaryIndex = BuildCffIndex([topDictionary]);
            int prefixLength = 4 + nameIndex.Length + topDictionaryIndex.Length + stringIndex.Length + globalSubrIndex.Length;
            int charStringsOffset = prefixLength;
            int charsetOffset = charStringsOffset + charStringsIndex.Length;
            int privateOffset = charsetOffset + charset.Length;
            byte[] nextTopDictionary = BuildCffDictionary(
                EncodeCffInteger(0), EncodeCffInteger(0), EncodeCffInteger(500), EncodeCffInteger(700), [5],
                EncodeCffInteger(charStringsOffset), [17],
                EncodeCffInteger(charsetOffset), [15],
                EncodeCffInteger(privateDictionary.Length), EncodeCffInteger(privateOffset), [18]);
            if (nextTopDictionary.SequenceEqual(topDictionary))
            {
                topDictionary = nextTopDictionary;
                break;
            }

            topDictionary = nextTopDictionary;
        }

        using MemoryStream output = new();
        output.Write([1, 0, 4, 1]);
        output.Write(nameIndex);
        output.Write(BuildCffIndex([topDictionary]));
        output.Write(stringIndex);
        output.Write(globalSubrIndex);
        output.Write(charStringsIndex);
        output.Write(charset);
        output.Write(privateDictionary);
        return output.ToArray();
    }

    private static byte[] BuildCffDictionary(params byte[][] parts)
    {
        using MemoryStream output = new();
        foreach (byte[] part in parts)
        {
            output.Write(part);
        }

        return output.ToArray();
    }

    private static byte[] BuildCffIndex(byte[][] objects)
    {
        using MemoryStream output = new();
        output.WriteByte((byte)(objects.Length >> 8));
        output.WriteByte((byte)objects.Length);
        if (objects.Length == 0)
        {
            return output.ToArray();
        }

        output.WriteByte(1);
        int offset = 1;
        output.WriteByte(1);
        foreach (byte[] item in objects)
        {
            offset += item.Length;
            output.WriteByte((byte)offset);
        }

        foreach (byte[] item in objects)
        {
            output.Write(item);
        }

        return output.ToArray();
    }

    private static byte[] EncodeCffInteger(int value)
    {
        if (value is >= -107 and <= 107)
        {
            return [(byte)(value + 139)];
        }

        if (value is >= 108 and <= 1131)
        {
            int adjusted = value - 108;
            return [(byte)(247 + adjusted / 256), (byte)(adjusted % 256)];
        }

        if (value is >= -1131 and <= -108)
        {
            int adjusted = -value - 108;
            return [(byte)(251 + adjusted / 256), (byte)(adjusted % 256)];
        }

        return [28, (byte)(value >> 8), (byte)value];
    }

    private static PDDocument CreateLinkedTextDocument(float textY = 700)
    {
        PDDocument document = CreateTextDocument($"""
            BT
            /F1 12 Tf
            72 {textY.ToString(CultureInfo.InvariantCulture)} Td
            (Linked text) Tj
            ET
            """);
        PDAnnotationLink link = new();
        link.SetRectangle(new PDRectangle(72, textY - 20, 120, 24));
        PDActionURI action = new();
        action.SetURI("https://example.com/pdfbox");
        link.SetAction(action);
        document.GetPage(0).SetAnnotations([link]);
        return document;
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

    private static PDDocument CreateScanImageWithTextDocument(bool paintText, float imageWidth, float imageHeight)
    {
        string renderingMode = paintText ? "0" : "3";
        PDDocument document = CreateTextDocument($"""
            BT
            /F1 12 Tf
            {renderingMode} Tr
            72 700 Td
            (Searchable OCR text remains available without painting over the scanned page.) Tj
            ET
            """);
        PDPage page = document.GetPage(0);
        const int pixelWidth = 306;
        const int pixelHeight = 396;
        byte[] rgb = new byte[pixelWidth * pixelHeight * 3];
        Array.Fill(rgb, (byte)232);
        PDImageXObject image = LosslessFactory.CreateFromRawData(document, rgb, pixelWidth, pixelHeight, 8, 3);
        using (PDPageContentStream content = new(document, page, PDPageContentStream.AppendMode.PREPEND, true))
        {
            content.DrawImage(image, 0, 0, imageWidth, imageHeight);
        }

        return document;
    }

    private static PDDocument CreateCoverTitleCompositionDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDType1Font titleFont = new(PDType1Font.FontName.HELVETICA_BOLD);
        PDType1Font bodyFont = new(PDType1Font.FontName.HELVETICA);
        PDImageXObject updateMark = LosslessFactory.CreateFromRawData(document, [96, 96, 96], 1, 1, 8, 3);
        PDImageXObject logo = LosslessFactory.CreateFromRawData(document, [36, 112, 168], 1, 1, 8, 3);
        using (PDPageContentStream content = new(document, page))
        {
            content.DrawImage(updateMark, 24, 714, 48, 48);
            WriteCompositeFixtureLine(content, titleFont, "Technical Publication 42", 334, 690, 18);
            WriteRightAlignedFixtureLine(content, titleFont, "Designing Secure Systems", 540, 650, 26);
            WriteRightAlignedFixtureLine(content, titleFont, "Across Public Networks", 540, 616, 26);
            WriteRightAlignedFixtureLine(content, titleFont, "For Modern Operations", 540, 582, 26);

            content.SetStrokingColor(0.25f);
            content.MoveTo(72, 562);
            content.LineTo(220, 562);
            content.Stroke();

            WriteRightAlignedFixtureLine(content, bodyFont, "Morgan Lee", 540, 520, 13);
            WriteRightAlignedFixtureLine(content, bodyFont, "Avery Chen", 540, 502, 13);
            WriteRightAlignedFixtureLine(content, bodyFont, "Prepared for public release and broad technical review", 540, 460, 11);
            WriteRightAlignedFixtureLine(content, bodyFont, "Reference edition for infrastructure planning teams", 540, 444, 11);
            WriteRightAlignedFixtureLine(content, bodyFont, "Supporting notes describe the intended operating context", 540, 428, 11);
            WriteRightAlignedFixtureLine(content, bodyFont, "Distribution remains available without access restrictions", 540, 412, 11);
            content.DrawImage(logo, 360, 30, 180, 36);
        }

        return document;
    }

    private static PDDocument CreateMixedSizeSameBaselineTitleDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDType1Font font = new(PDType1Font.FontName.HELVETICA);
        using (PDPageContentStream content = new(document, page))
        {
            float titleX = 70f;
            const float titleBaseline = 690f;
            titleX = WriteMixedSizeTitleWord(content, font, "ADAM:", titleX, titleBaseline);
            titleX += 7f;
            titleX = WriteMixedSizeTitleWord(content, font, "A", titleX, titleBaseline);
            titleX += 7f;
            titleX = WriteMixedSizeTitleWord(content, font, "METHOD", titleX, titleBaseline);
            titleX += 7f;
            titleX = WriteMixedSizeTitleWord(content, font, "FOR", titleX, titleBaseline);
            titleX += 7f;
            titleX = WriteMixedSizeTitleWord(content, font, "STOCHASTIC", titleX, titleBaseline);
            titleX += 7f;
            WriteMixedSizeTitleWord(content, font, "OPTIMIZATION", titleX, titleBaseline);

            WriteCompositeFixtureLine(content, font, "STACKED HEADING ONE", 190, 620, 16);
            WriteCompositeFixtureLine(content, font, "STACKED HEADING TWO", 190, 585, 16);
            for (int index = 0; index < 8; index++)
            {
                WriteCompositeFixtureLine(
                    content,
                    font,
                    $"Body line {index + 1} provides enough ordinary prose to establish the document body size.",
                    72,
                    520 - index * 14,
                    10);
            }
        }

        return document;
    }

    private static float WriteMixedSizeTitleWord(
        PDPageContentStream content,
        PDFont font,
        string word,
        float x,
        float y)
    {
        const float initialSize = 22f;
        const float smallCapsSize = 16f;
        WriteCompositeFixtureLine(content, font, word[..1], x, y, initialSize);
        x += font.GetStringWidth(word[..1]) / 1000f * initialSize;
        if (word.Length > 1)
        {
            WriteCompositeFixtureLine(content, font, word[1..], x, y, smallCapsSize);
            x += font.GetStringWidth(word[1..]) / 1000f * smallCapsSize;
        }

        return x;
    }

    private static void WriteRightAlignedFixtureLine(
        PDPageContentStream content,
        PDFont font,
        string text,
        float right,
        float y,
        float fontSize)
    {
        float width = font.GetStringWidth(text) / 1000f * fontSize;
        WriteCompositeFixtureLine(content, font, text, right - width, y, fontSize);
    }

    private static PDDocument CreateSideBySideImageDocument(bool includeCaption, bool clipSecondImage)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDImageXObject firstImage = LosslessFactory.CreateFromRawData(document, [192, 48, 48], 1, 1, 8, 3);
        PDImageXObject secondImage = LosslessFactory.CreateFromRawData(document, [48, 96, 192], 1, 1, 8, 3);
        PDType1Font font = new(PDType1Font.FontName.HELVETICA);
        using (PDPageContentStream content = new(document, page))
        {
            WriteCompositeFixtureBody(content, font);
            content.DrawImage(firstImage, 72, 420, 120, 90);
            if (clipSecondImage)
            {
                content.SaveGraphicsState();
                content.AddRect(204, 420, 90, 90);
                content.Clip();
                content.DrawImage(secondImage, 204, 390, 180, 120);
                content.RestoreGraphicsState();
            }
            else
            {
                content.DrawImage(secondImage, 204, 420, 120, 90);
            }

            if (includeCaption)
            {
                WriteCompositeFixtureLine(content, font, "Figure 1: Side-by-side composite.", 72, 398, 9);
            }
            else
            {
                WriteCompositeFixtureLine(content, font, "These nearby images belong to separate body examples.", 72, 398, 9);
            }
        }

        return document;
    }

    private static PDDocument CreateGridImageDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDImageXObject image = LosslessFactory.CreateFromRawData(document, [64, 160, 96], 1, 1, 8, 3);
        PDType1Font font = new(PDType1Font.FontName.HELVETICA);
        using (PDPageContentStream content = new(document, page))
        {
            WriteCompositeFixtureBody(content, font);
            content.DrawImage(image, 150, 480, 80, 60);
            content.DrawImage(image, 238, 480, 80, 60);
            content.DrawImage(image, 150, 412, 80, 60);
            content.DrawImage(image, 238, 412, 80, 60);
            WriteCompositeFixtureLine(content, font, "Figure 1: Four-panel grid.", 150, 390, 9);
        }

        return document;
    }

    private static void WriteCompositeFixtureBody(PDPageContentStream content, PDFont font)
    {
        for (int index = 0; index < 10; index++)
        {
            WriteCompositeFixtureLine(
                content,
                font,
                $"Body line {index + 1} keeps semantic flow active for this focused fixture.",
                72,
                750 - index * 14,
                10);
        }
    }

    private static void WriteCompositeFixtureLine(
        PDPageContentStream content,
        PDFont font,
        string text,
        float x,
        float y,
        float fontSize)
    {
        content.BeginText();
        content.SetFont(font, fontSize);
        content.NewLineAtOffset(x, y);
        content.ShowText(text);
        content.EndText();
    }

    private static PDDocument CreateRotatedCroppedImageDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        page.SetCropBox(new PDRectangle(10, 20, 200, 300));
        page.SetRotation(90);
        document.AddPage(page);
        PDImageXObject image = LosslessFactory.CreateFromRawData(document, [255, 255, 255], 1, 1, 8, 3);
        using (PDPageContentStream content = new(document, page))
        {
            content.DrawImage(image, 40, 70, 50, 30);
        }

        return document;
    }

    private static PDDocument CreateRepeatedImageDocument()
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
            content.DrawImage(image, 240, 600, 120, 60);
        }

        return document;
    }

    private static PDDocument CreateImageWithVectorBackdropDocument()
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
            content.SetNonStrokingColor(1f, 1f, 1f);
            content.AddRect(60, 590, 144, 80);
            content.Fill();
            content.DrawImage(image, 72, 600, 120, 60);
            content.SetNonStrokingColor(1f, 0f, 0f);
            content.AddRect(210, 600, 12, 12);
            content.Fill();
        }

        return document;
    }

    private static PDDocument CreateImageAndVectorDocument(bool imageFirst)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDImageXObject image = LosslessFactory.CreateFromRawData(document, [255, 255, 255], 1, 1, 8, 3);
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

    private static PDDocument CreateIndexedDeviceNOverprintDocument(
        bool imageOverprint,
        IReadOnlyList<string> imageColorants,
        int placementCount = 1)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDDeviceN deviceN = new(
            imageColorants.ToList(),
            PDDeviceRGB.Instance,
            CreateType4Function("{ pop }", imageColorants.Count, 3));
        byte[] lookup = new byte[imageColorants.Count];
        Array.Fill(lookup, (byte)255);
        PDIndexed indexed = PDIndexed.Create(deviceN, 0, lookup);

        PDStream imageStream = new(document);
        using (Stream output = imageStream.CreateOutputStream())
        {
            output.WriteByte(0);
        }

        COSStream imageDictionary = imageStream.GetCOSObject();
        imageDictionary.SetInt(COSName.WIDTH, 1);
        imageDictionary.SetInt(COSName.HEIGHT, 1);
        imageDictionary.SetInt(COSName.BITS_PER_COMPONENT, 8);
        imageDictionary.SetItem(COSName.COLORSPACE, indexed.GetCOSObject());
        PDImageXObject image = new(imageStream, null);

        PDExtendedGraphicsState graphicsState = new();
        graphicsState.SetNonStrokingOverprintControl(imageOverprint);
        using (PDPageContentStream content = new(document, page))
        {
            for (int index = 0; index < placementCount; index++)
            {
                content.SetNonStrokingColor(0f, 0f, 1f, 0f);
                content.AddRect(10 + index * 30, 10, 20, 20);
                content.Fill();
            }

            content.SetGraphicsStateParameters(graphicsState);
            for (int index = 0; index < placementCount; index++)
            {
                content.DrawImage(image, 10 + index * 30, 10, 20, 20);
            }
        }

        return document;
    }

    private static PDDocument CreateUniformIndexedDeviceNClipOverprintDocument(
        List<string>? colorants = null,
        byte[]? lookup = null)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        colorants ??= ["Cyan", "None", "None", "None"];
        lookup ??= [255, 0, 0, 0];
        PDDeviceN deviceN = new(
            colorants,
            PDDeviceCMYK.Instance,
            CreateType4Function("{ pop pop pop pop 1 0 0 0 }", 4, 4));
        PDIndexed indexed = PDIndexed.Create(deviceN, 0, lookup);
        PDStream imageStream = new(document);
        using (Stream output = imageStream.CreateOutputStream())
        {
            output.WriteByte(0);
        }

        COSStream imageDictionary = imageStream.GetCOSObject();
        imageDictionary.SetInt(COSName.WIDTH, 1);
        imageDictionary.SetInt(COSName.HEIGHT, 1);
        imageDictionary.SetInt(COSName.BITS_PER_COMPONENT, 8);
        imageDictionary.SetItem(COSName.COLORSPACE, indexed.GetCOSObject());
        PDImageXObject image = new(imageStream, null);

        PDExtendedGraphicsState overprint = new();
        overprint.SetNonStrokingOverprintControl(true);
        overprint.SetOverprintMode(1);
        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(0f, 0f, 0f, 1f);
            content.AddRect(10, 10, 20, 20);
            content.Fill();
            content.SaveGraphicsState();
            content.AddRect(10, 10, 20, 20);
            content.Clip();
            content.SetGraphicsStateParameters(overprint);
            content.DrawImage(image, 10, 10, 20, 20);
            content.RestoreGraphicsState();
        }

        return document;
    }

    private static PDDocument CreateSeparationStencilOverprintDocument(bool spotOnTop)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        PDSeparation spot = new(
            "GWG Green",
            PDDeviceCMYK.Instance,
            CreateType4Function(
                spotOnTop ? "{ pop 0.005 0 0.01 0 }" : "{ pop 0.5 0 1 0 }",
                1,
                4));
        PDColor spotColor = new([1f], spot);
        PDColor processColor = new(
            spotOnTop ? [0.5f, 0f, 1f, 0f] : [0.005f, 0f, 0.01f, 0f],
            PDDeviceCMYK.Instance);

        PDStream imageStream = new(document);
        using (Stream output = imageStream.CreateOutputStream())
        {
            output.WriteByte(0);
        }

        COSStream imageDictionary = imageStream.GetCOSObject();
        imageDictionary.SetInt(COSName.WIDTH, 1);
        imageDictionary.SetInt(COSName.HEIGHT, 1);
        imageDictionary.SetInt(COSName.BITS_PER_COMPONENT, 1);
        imageDictionary.SetBoolean(COSName.IMAGE_MASK, true);
        PDImageXObject stencil = new(imageStream, null);

        PDExtendedGraphicsState overprint = new();
        overprint.SetNonStrokingOverprintControl(true);
        overprint.SetOverprintMode(1);
        using (PDPageContentStream content = new(document, page))
        {
            content.SetNonStrokingColor(spotOnTop ? processColor : spotColor);
            content.AddRect(10, 10, 20, 20);
            content.Fill();
            content.SetGraphicsStateParameters(overprint);
            content.SetNonStrokingColor(spotOnTop ? spotColor : processColor);
            content.DrawImage(stencil, 12, 12, 16, 16);
        }

        return document;
    }

    private static PDDocument CreateFormScopedClipDocument()
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDFormXObject form = new(new PDStream(document));
        form.SetBBox(new PDRectangle(0, 0, 100, 100));
        using (PDFormContentStream content = new(form))
        {
            content.SaveGraphicsState();
            content.AddRect(0, 0, 20, 20);
            content.Clip();
            content.SetNonStrokingColor(1f, 0f, 0f);
            content.AddRect(0, 0, 20, 20);
            content.Fill();
            content.RestoreGraphicsState();
            content.SetNonStrokingColor(0f, 0f, 1f);
            content.AddRect(60, 60, 20, 20);
            content.Fill();
        }

        using PDPageContentStream pageContent = new(document, page);
        pageContent.DrawForm(form);
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

    private static PDFunctionType4 CreateType4Function(
        string functionText,
        int inputComponents,
        int outputComponents)
    {
        COSStream stream = new();
        stream.SetInt(COSName.FUNCTION_TYPE, 4);
        stream.SetItem(COSName.DOMAIN, COSArray.Of(Enumerable.Repeat(new[] { 0f, 1f }, inputComponents).SelectMany(x => x).ToArray()));
        stream.SetItem(COSName.RANGE, COSArray.Of(Enumerable.Repeat(new[] { 0f, 1f }, outputComponents).SelectMany(x => x).ToArray()));
        using (Stream output = stream.CreateOutputStream())
        {
            output.Write(Encoding.ASCII.GetBytes(functionText));
        }

        return new PDFunctionType4(stream);
    }

    private static PDDocument CreateCompactTransparencyGroupDocument(bool knockout, bool isolated)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDTransparencyGroup group = new(new PDStream(document));
        group.SetBBox(new PDRectangle(0, 0, 40, 40));
        PDTransparencyGroupAttributes attributes = new();
        attributes.GetCOSObject().SetBoolean(COSName.K, knockout);
        attributes.GetCOSObject().SetBoolean(COSName.GetPDFName("I"), isolated);
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

    private static PDDocument CreateMultipleCompactTransparencyGroupDocument()
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
        foreach (float x in new[] { 100f, 200f })
        {
            pageContent.SaveGraphicsState();
            pageContent.Transform(new Matrix(1, 0, 0, 1, x, 300));
            pageContent.DrawForm(group);
            pageContent.RestoreGraphicsState();
        }

        return document;
    }

    private static PDDocument CreateLuminositySoftMaskedTextShadowDocument()
    {
        const string title = "Shadowed title";
        const float fontSize = 20f;
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);
        PDType1Font font = new(PDType1Font.FontName.HELVETICA_BOLD);
        float titleWidth = font.GetStringWidth(title) / 1000f * fontSize;

        PDTransparencyGroup maskGroup = new(new PDStream(document));
        maskGroup.SetBBox(new PDRectangle(0, 0, titleWidth + 12f, 26f));
        maskGroup.SetGroup(new PDTransparencyGroupAttributes());
        using (Stream maskContent = maskGroup.GetContentStream().CreateOutputStream())
        {
            maskContent.Write(Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"1 g\n0 0 {titleWidth + 12f:0.###} 26 re\nf\n")));
        }

        COSDictionary softMaskDictionary = new();
        softMaskDictionary.SetItem(COSName.GetPDFName("S"), COSName.GetPDFName("Luminosity"));
        softMaskDictionary.SetItem(COSName.GetPDFName("G"), maskGroup);
        PDSoftMask softMask = new(softMaskDictionary);

        PDTransparencyGroup shadowGroup = new(new PDStream(document));
        shadowGroup.SetBBox(new PDRectangle(0, 0, titleWidth + 12f, 26f));
        shadowGroup.SetGroup(new PDTransparencyGroupAttributes());
        using (Stream shadowContent = shadowGroup.GetContentStream().CreateOutputStream())
        {
            shadowContent.Write(Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"0 0 0 rg\n0 0 {titleWidth + 12f:0.###} 26 re\nf\n")));
        }

        PDExtendedGraphicsState shadowState = new();
        shadowState.SetSoftMask(softMask);
        shadowState.SetNonStrokingAlphaConstant(0.6f);
        using PDPageContentStream content = new(document, page);
        content.SaveGraphicsState();
        content.SetGraphicsStateParameters(shadowState);
        content.Transform(new Matrix(1, 0, 0, 1, 96, 692));
        content.DrawForm(shadowGroup);
        content.RestoreGraphicsState();
        content.BeginText();
        content.SetFont(font, fontSize);
        content.NewLineAtOffset(100, 700);
        content.ShowText(title);
        content.EndText();
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

    private static COSStream CreateInlineImageContentStream()
    {
        COSStream stream = new();
        using Stream output = stream.CreateOutputStream();
        WriteLatin1(output, "q\n120 0 0 60 72 600 cm\nBI\n/W 2 /H 2 /BPC 8 /CS /RGB\nID\n");
        output.Write([
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255
        ]);
        WriteLatin1(output, "\nEI\nQ\n");
        return stream;
    }

    private static COSStream CreateContentStream(string contentStream)
    {
        COSStream stream = new();
        using Stream output = stream.CreateOutputStream();
        byte[] bytes = Encoding.Latin1.GetBytes(contentStream);
        output.Write(bytes, 0, bytes.Length);
        return stream;
    }

    private static PdfLayoutDocument CreateArabicVisualOrderLayout(float y)
    {
        const string visualText = "ةدحتملا ممألا ةموظنم";
        PdfLayoutRectangle pageBounds = new(0, 0, 300, 180);
        PdfLayoutColor black = new(0, 0, 0, 1, "DeviceRGB");
        List<PdfTextGlyph> glyphs = [];
        float x = 40f;
        foreach (Rune rune in visualText.EnumerateRunes())
        {
            float width = Rune.IsWhiteSpace(rune) ? 4f : 7f;
            glyphs.Add(new PdfTextGlyph(
                rune.ToString(),
                "NotoNaskhArabic",
                12f,
                0f,
                new PdfLayoutRectangle(x, y, width, 10f),
                black)
            {
                UsesBrowserFontAsset = true
            });
            x += width;
        }

        PdfLayoutRectangle bounds = new(40f, y, x - 40f, 10f);
        PdfTextRun run = new(visualText, "NotoNaskhArabic", 12f, 0f, bounds, black, glyphs);
        PdfTextLine line = new(visualText, bounds, [run]);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            [run],
            [line],
            [new PdfTextBlock(visualText, bounds, [line])],
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static COSStream CreateBinaryStream(PDDocument document, byte[] data)
    {
        COSStream stream = new PDStream(document).GetCOSObject();
        using Stream output = stream.CreateOutputStream();
        output.Write(data);
        return stream;
    }

    private static COSDictionary CreateDefaultResourcesDictionary()
    {
        COSDictionary fonts = new();
        fonts.SetItem(COSName.GetPDFName("F1"), CreateType1FontDictionary("Helvetica"));
        fonts.SetItem(COSName.GetPDFName("F2"), CreateType1FontDictionary("Helvetica-Oblique"));

        COSDictionary resources = new();
        resources.SetItem(COSName.GetPDFName("Font"), fonts);
        return resources;
    }

    private static COSDictionary CreateType1FontDictionary(string baseFont)
    {
        COSDictionary fontDictionary = new();
        fontDictionary.SetItem(COSName.TYPE, COSName.GetPDFName("Font"));
        fontDictionary.SetItem(COSName.GetPDFName("Subtype"), COSName.GetPDFName("Type1"));
        fontDictionary.SetItem(COSName.GetPDFName("BaseFont"), COSName.GetPDFName(baseFont));
        return fontDictionary;
    }

    private static void WriteLatin1(Stream stream, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfbox-net-html-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
