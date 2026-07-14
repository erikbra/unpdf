using System.Globalization;
using System.Text;

namespace PdfBox.Net.Layout;

/// <summary>
/// The dominant direction of strong Unicode characters in extracted text.
/// </summary>
public enum PdfTextDirection
{
    Neutral,
    LeftToRight,
    RightToLeft
}

/// <summary>
/// Detects the dominant Unicode text direction without depending on a platform text renderer.
/// </summary>
public static class PdfTextDirectionDetector
{
    /// <summary>
    /// Detects the dominant direction from strong script characters. Digits and punctuation do not
    /// select a direction, which keeps Latin acronyms and numbers stable inside RTL text.
    /// </summary>
    public static PdfTextDirection Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int rightToLeft = 0;
        int leftToRight = 0;
        PdfTextDirection firstStrong = PdfTextDirection.Neutral;
        foreach (Rune rune in text.EnumerateRunes())
        {
            PdfTextDirection direction = DirectionOf(rune);
            if (direction == PdfTextDirection.Neutral)
            {
                continue;
            }

            if (firstStrong == PdfTextDirection.Neutral)
            {
                firstStrong = direction;
            }

            if (direction == PdfTextDirection.RightToLeft)
            {
                rightToLeft++;
            }
            else
            {
                leftToRight++;
            }
        }

        if (rightToLeft == 0)
        {
            return leftToRight == 0 ? PdfTextDirection.Neutral : PdfTextDirection.LeftToRight;
        }

        if (leftToRight == 0)
        {
            return PdfTextDirection.RightToLeft;
        }

        return rightToLeft == leftToRight
            ? firstStrong
            : rightToLeft > leftToRight
                ? PdfTextDirection.RightToLeft
                : PdfTextDirection.LeftToRight;
    }

    internal static PdfTextDirection DirectionOf(string text)
    {
        int rightToLeft = 0;
        int leftToRight = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            PdfTextDirection direction = DirectionOf(rune);
            if (direction == PdfTextDirection.Neutral && Rune.IsDigit(rune))
            {
                direction = PdfTextDirection.LeftToRight;
            }

            if (direction == PdfTextDirection.RightToLeft)
            {
                rightToLeft++;
            }
            else if (direction == PdfTextDirection.LeftToRight)
            {
                leftToRight++;
            }
        }

        return rightToLeft == leftToRight
            ? PdfTextDirection.Neutral
            : rightToLeft > leftToRight
                ? PdfTextDirection.RightToLeft
                : PdfTextDirection.LeftToRight;
    }

    private static PdfTextDirection DirectionOf(Rune rune)
    {
        int value = rune.Value;
        if (IsRightToLeftCodePoint(value))
        {
            return PdfTextDirection.RightToLeft;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter
            ? PdfTextDirection.LeftToRight
            : PdfTextDirection.Neutral;
    }

    private static bool IsRightToLeftCodePoint(int value)
    {
        return value is >= 0x0590 and <= 0x08FF or
            >= 0xFB1D and <= 0xFDFF or
            >= 0xFE70 and <= 0xFEFF or
            >= 0x10800 and <= 0x10FFF or
            >= 0x1E800 and <= 0x1EEFF;
    }
}
