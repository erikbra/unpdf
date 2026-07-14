using PdfBox.Net.Util;

namespace PdfBox.Net.Layout;

/// <summary>
/// Six-value affine transform in PDF matrix operand order.
/// </summary>
public readonly record struct PdfLayoutTransform(float A, float B, float C, float D, float E, float F)
{
    internal static PdfLayoutTransform FromMatrix(Matrix matrix)
    {
        return new PdfLayoutTransform(
            matrix.GetScaleX(),
            matrix.GetShearY(),
            matrix.GetShearX(),
            matrix.GetScaleY(),
            matrix.GetTranslateX(),
            matrix.GetTranslateY());
    }
}
