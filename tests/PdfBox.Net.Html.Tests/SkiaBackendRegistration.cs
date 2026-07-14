using System.Runtime.CompilerServices;
using PdfBox.Net.Rendering;
using PdfBox.Net.ImageMagick;

#pragma warning disable CA2255
internal static class SkiaBackendRegistration
{
    [ModuleInitializer]
    internal static void RegisterSkiaBackend()
    {
        SkiaRenderingBackend.Register();
        PdfBoxNetImageMagick.Register();
    }
}
#pragma warning restore CA2255
