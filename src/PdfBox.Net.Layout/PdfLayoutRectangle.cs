using PdfBox.Net.PDModel.Common;

namespace PdfBox.Net.Layout;

/// <summary>
/// Rectangle in normalized page coordinates. The origin is the visible page's top-left corner.
/// </summary>
public readonly record struct PdfLayoutRectangle(float X, float Y, float Width, float Height)
{
    /// <summary>
    /// Gets the right edge.
    /// </summary>
    public float Right => X + Width;

    /// <summary>
    /// Gets the bottom edge.
    /// </summary>
    public float Bottom => Y + Height;

    internal static PdfLayoutRectangle FromPdfRectangle(PDRectangle rectangle)
    {
        return new PdfLayoutRectangle(
            rectangle.GetLowerLeftX(),
            rectangle.GetLowerLeftY(),
            rectangle.GetWidth(),
            rectangle.GetHeight());
    }

    internal static PdfLayoutRectangle Union(IEnumerable<PdfLayoutRectangle> rectangles)
    {
        using IEnumerator<PdfLayoutRectangle> enumerator = rectangles.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return new PdfLayoutRectangle(0, 0, 0, 0);
        }

        PdfLayoutRectangle first = enumerator.Current;
        float left = first.X;
        float top = first.Y;
        float right = first.Right;
        float bottom = first.Bottom;

        while (enumerator.MoveNext())
        {
            PdfLayoutRectangle rectangle = enumerator.Current;
            left = MathF.Min(left, rectangle.X);
            top = MathF.Min(top, rectangle.Y);
            right = MathF.Max(right, rectangle.Right);
            bottom = MathF.Max(bottom, rectangle.Bottom);
        }

        return new PdfLayoutRectangle(left, top, right - left, bottom - top);
    }
}
