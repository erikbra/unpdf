using System.Runtime.CompilerServices;
using PdfBox.Net.ImageMagick;
using PdfBox.Net.Rendering;

namespace PdfBox.Net.Markdown.Tests;

internal static class RenderingBackendRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        SkiaRenderingBackend.Register();
        PdfBoxNetImageMagick.Register();
    }
}
