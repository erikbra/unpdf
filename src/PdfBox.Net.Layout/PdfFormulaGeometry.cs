namespace PdfBox.Net.Layout;

internal static class PdfFormulaGeometry
{
    internal static IReadOnlyDictionary<PdfTextGlyph, PdfLayoutRectangle> TallDelimiterBounds(
        IReadOnlyList<PdfTextGlyph> glyphs)
    {
        Dictionary<PdfTextGlyph, PdfLayoutRectangle> result =
            new((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        HashSet<PdfTextGlyph> pairedCloses =
            new((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        PdfTextGlyph[] closes = glyphs
            .Where(static glyph => IsComputerModernDelimiter(glyph, ")"))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ToArray();
        foreach (PdfTextGlyph open in glyphs
            .Where(static glyph => IsComputerModernDelimiter(glyph, "("))
            .OrderBy(static glyph => glyph.Bounds.X))
        {
            PdfTextGlyph? close = closes.FirstOrDefault(candidate =>
                !pairedCloses.Contains(candidate) &&
                candidate.Bounds.X > open.Bounds.Right &&
                MathF.Abs(candidate.Bounds.Y - open.Bounds.Y) <=
                    MathF.Max(1f, open.FontSize * 0.2f));
            if (close == null)
            {
                continue;
            }

            PdfTextGlyph[] interior = glyphs
                .Where(glyph => !ReferenceEquals(glyph, open) && !ReferenceEquals(glyph, close))
                .Where(glyph =>
                {
                    float center = glyph.Bounds.X + glyph.Bounds.Width / 2f;
                    return center >= open.Bounds.Right - 0.5f &&
                        center <= close.Bounds.X + 0.5f;
                })
                .ToArray();
            if (interior.Length == 0)
            {
                continue;
            }

            float top = MathF.Min(
                MathF.Min(open.Bounds.Y, close.Bounds.Y),
                interior.Min(static glyph => glyph.Bounds.Y));
            float bottom = MathF.Max(
                MathF.Max(open.Bounds.Bottom, close.Bounds.Bottom),
                interior.Max(static glyph => glyph.Bounds.Bottom));
            float height = bottom - top;
            if (height < MathF.Max(open.Bounds.Height, close.Bounds.Height) * 1.35f)
            {
                continue;
            }

            pairedCloses.Add(close);
            result.Add(open, new PdfLayoutRectangle(open.Bounds.X, top, open.Bounds.Width, height));
            result.Add(close, new PdfLayoutRectangle(close.Bounds.X, top, close.Bounds.Width, height));
        }

        return result;
    }

    private static bool IsComputerModernDelimiter(PdfTextGlyph glyph, string text)
    {
        return glyph.Text == text &&
            glyph.Outline is not { Count: > 0 } &&
            glyph.FontName.Contains("CMEX", StringComparison.OrdinalIgnoreCase);
    }
}
