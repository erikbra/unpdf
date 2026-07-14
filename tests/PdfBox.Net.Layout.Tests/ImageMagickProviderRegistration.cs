using ModuleInitializer = System.Runtime.CompilerServices.ModuleInitializerAttribute;
using PdfBox.Net.ImageMagick;

namespace PdfBox.Net.Layout.Tests;

internal static class ImageMagickProviderRegistration
{
    [ModuleInitializer]
    internal static void RegisterImageMagickProvider()
    {
        PdfBoxNetImageMagick.Register();
    }
}
