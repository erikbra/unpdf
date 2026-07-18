using PdfBox.Net;
using PdfBox.Net.Html;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PdfBox.Net.Html.Consumer <input.pdf> <output-directory>");
    return 2;
}

using PDDocument pdf = Loader.LoadPDF(args[0]);
PdfLayoutDocument layout = PdfLayoutExtractor.Extract(pdf);
PdfHtmlDocument html = PdfHtmlConverter.Convert(layout);
html.WriteToDirectory(args[1]);
return 0;
