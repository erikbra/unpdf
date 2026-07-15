using System.Buffers.Binary;
using System.Text;
using Microsoft.Playwright;
using PdfBox.Net.COS;
using PdfBox.Net.FontBox.TTF;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.Common;
using PdfBox.Net.PDModel.Font;
using PdfBox.Net.PDModel.Resources;

namespace PdfBox.Net.Html.Tests;

public class EmbeddedWebFontNormalizationTest
{
    private const uint ChecksumMagic = 0xB1B0AFBA;
    private const int OffsetTableSize = 12;
    private const int TableRecordSize = 16;

    [Fact]
    public async Task Extract_ArxivSample_NormalizesAndLoadsEveryEmbeddedSfntFont()
    {
        using PDDocument document = Loader.LoadPDF(FixturePath("arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludePaths = false,
            IncludeFontAssets = true
        });

        Assert.Equal(10, layout.FontAssets.Count);
        Assert.All(layout.FontAssets, static font => AssertNormalizedSfnt(font.Data));

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

        string[] embeddedFontStates = await page.EvaluateAsync<string[]>(
            """
            async () => {
              const fonts = Array.from(document.fonts).filter(font => font.family.includes('Arial'));
              await Promise.allSettled(fonts.map(font => font.load()));
              return fonts.map(font => `${font.family}:${font.status}`).sort();
            }
            """);
        Assert.Equal(10, embeddedFontStates.Length);
        Assert.All(embeddedFontStates, static state => Assert.EndsWith(":loaded", state, StringComparison.Ordinal));
        Assert.DoesNotContain(
            await page.ConsoleMessagesAsync(),
            static message =>
                message.Text.Contains("OTS parsing error", StringComparison.Ordinal) ||
                message.Text.Contains("Failed to decode downloaded font", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ValidEmbeddedTrueTypeFont_PreservesBytes()
    {
        byte[] sourceFont = File.ReadAllBytes(FixturePath("LiberationSans-Regular.ttf"));
        using PDDocument document = CreateEmbeddedTrueTypeDocument(sourceFont);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        PdfLayoutFontAsset font = Assert.Single(layout.FontAssets);
        Assert.True(sourceFont.AsSpan().SequenceEqual(font.Data));
        Assert.DoesNotContain(layout.Diagnostics, static diagnostic => diagnostic.Code == "embedded-font-web-unsupported");
    }

    [Fact]
    public async Task Extract_UnpaddedEmbeddedTrueTypeFont_NormalizesAndLoadsFontFace()
    {
        byte[] sourceFont = File.ReadAllBytes(FixturePath("LiberationSans-Regular.ttf"));
        byte[] unpaddedFont = RemoveSfntTablePadding(sourceFont);
        Assert.Contains(ReadSfntTableOffsets(unpaddedFont), static offset => offset % sizeof(uint) != 0);
        using PDDocument document = CreateEmbeddedTrueTypeDocument(unpaddedFont);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        PdfLayoutFontAsset font = Assert.Single(layout.FontAssets);
        Assert.False(unpaddedFont.AsSpan().SequenceEqual(font.Data));
        AssertNormalizedSfnt(font.Data);
        Assert.DoesNotContain(layout.Diagnostics, static diagnostic => diagnostic.Code == "embedded-font-web-unsupported");

        PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
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
        IReadOnlyList<IConsoleMessage> consoleMessages = await page.ConsoleMessagesAsync();
        Assert.True(
            loaded,
            $"Normalized @font-face did not load; console: {string.Join(" | ", consoleMessages.Select(message => message.Text))}");
    }

    [Fact]
    public void Extract_OutOfBoundsEmbeddedTrueTypeFont_UsesFallbackAndReportsDiagnostic()
    {
        byte[] sourceFont = File.ReadAllBytes(FixturePath("LiberationSans-Regular.ttf"));
        byte[] malformedFont = (byte[])sourceFont.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(
            malformedFont.AsSpan(OffsetTableSize + 8, sizeof(uint)),
            checked((uint)malformedFont.Length + 1));
        using PDDocument document = CreateEmbeddedTrueTypeDocument(malformedFont, sourceFont);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        Assert.Empty(layout.FontAssets);
        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            static diagnostic => diagnostic.Code == "embedded-font-web-unsupported");
        Assert.Contains("extends outside the font data", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_TruncatedSfntDirectory_UsesFallbackWithoutThrowing()
    {
        byte[] sourceFont = File.ReadAllBytes(FixturePath("LiberationSans-Regular.ttf"));
        byte[] malformedFont = sourceFont[..(OffsetTableSize + TableRecordSize)];
        using PDDocument document = CreateEmbeddedTrueTypeDocument(malformedFont, sourceFont);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        Assert.Empty(layout.FontAssets);
        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            static diagnostic => diagnostic.Code == "embedded-font-web-unsupported");
        Assert.Contains("table directory is truncated", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_OverlappingSfntTables_UsesFallbackWithoutThrowing()
    {
        byte[] sourceFont = File.ReadAllBytes(FixturePath("LiberationSans-Regular.ttf"));
        byte[] malformedFont = (byte[])sourceFont.Clone();
        ReadOnlySpan<byte> secondRecord = malformedFont.AsSpan(OffsetTableSize + TableRecordSize, TableRecordSize);
        BinaryPrimitives.WriteUInt32BigEndian(
            malformedFont.AsSpan(OffsetTableSize + 8, sizeof(uint)),
            BinaryPrimitives.ReadUInt32BigEndian(secondRecord[8..]));
        using PDDocument document = CreateEmbeddedTrueTypeDocument(malformedFont, sourceFont);

        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeFontAssets = true
        });

        Assert.Empty(layout.FontAssets);
        PdfLayoutDiagnostic diagnostic = Assert.Single(
            layout.Diagnostics,
            static diagnostic => diagnostic.Code == "embedded-font-web-unsupported");
        Assert.Contains("overlap", diagnostic.Message, StringComparison.Ordinal);
    }

    private static void AssertNormalizedSfnt(byte[] fontData)
    {
        Assert.Equal(0, fontData.Length % sizeof(uint));
        Assert.All(ReadSfntTableOffsets(fontData), static offset => Assert.Equal(0, offset % sizeof(uint)));
        Assert.Equal(ChecksumMagic, CalculateChecksum(fontData));
    }

    private static PDDocument CreateEmbeddedTrueTypeDocument(byte[] fontBytes, byte[]? parserFontBytes = null)
    {
        PDDocument document = new();
        PDPage page = new();
        document.AddPage(page);

        TrueTypeFont trueTypeFont = new TTFParser().Parse(parserFontBytes ?? fontBytes);
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

    private static byte[] RemoveSfntTablePadding(byte[] fontBytes)
    {
        ushort tableCount = BinaryPrimitives.ReadUInt16BigEndian(fontBytes.AsSpan(4, sizeof(ushort)));
        int directoryLength = OffsetTableSize + tableCount * TableRecordSize;
        using MemoryStream output = new();
        output.Write(fontBytes, 0, directoryLength);
        int[] updatedOffsets = new int[tableCount];
        for (int index = 0; index < tableCount; index++)
        {
            ReadOnlySpan<byte> record = fontBytes.AsSpan(
                OffsetTableSize + index * TableRecordSize,
                TableRecordSize);
            int sourceOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]));
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
            updatedOffsets[index] = checked((int)output.Position);
            output.Write(fontBytes, sourceOffset, length);
        }

        byte[] result = output.ToArray();
        for (int index = 0; index < tableCount; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                result.AsSpan(OffsetTableSize + index * TableRecordSize + 8, sizeof(uint)),
                (uint)updatedOffsets[index]);
        }

        return result;
    }

    private static IReadOnlyList<int> ReadSfntTableOffsets(byte[] fontBytes)
    {
        ushort tableCount = BinaryPrimitives.ReadUInt16BigEndian(fontBytes.AsSpan(4, sizeof(ushort)));
        int[] offsets = new int[tableCount];
        for (int index = 0; index < tableCount; index++)
        {
            offsets[index] = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                fontBytes.AsSpan(OffsetTableSize + index * TableRecordSize + 8, sizeof(uint))));
        }

        return offsets;
    }

    private static uint CalculateChecksum(ReadOnlySpan<byte> data)
    {
        uint checksum = 0;
        for (int offset = 0; offset < data.Length; offset += sizeof(uint))
        {
            checksum = unchecked(checksum + BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
        }

        return checksum;
    }

    private static COSStream CreateContentStream(string contentStream)
    {
        COSStream stream = new();
        using Stream output = stream.CreateOutputStream();
        byte[] bytes = Encoding.Latin1.GetBytes(contentStream);
        output.Write(bytes);
        return stream;
    }

    private static COSStream CreateBinaryStream(PDDocument document, byte[] data)
    {
        COSStream stream = new PDStream(document).GetCOSObject();
        using Stream output = stream.CreateOutputStream();
        output.Write(data);
        return stream;
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "unpdf-font-normalization-" + Guid.NewGuid().ToString("N"));
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
