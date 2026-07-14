namespace PdfBox.Net.Layout;

/// <summary>
/// One vector path command in normalized page coordinates.
/// </summary>
public readonly record struct PdfLayoutPathCommand(
    PdfLayoutPathCommandKind Kind,
    float X1,
    float Y1,
    float X2,
    float Y2,
    float X3,
    float Y3);
