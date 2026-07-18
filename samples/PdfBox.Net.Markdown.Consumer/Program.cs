using PdfBox.Net;
using PdfBox.Net.Layout;
using PdfBox.Net.Markdown;
using PdfBox.Net.PDModel;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PdfBox.Net.Markdown.Consumer <input.pdf> <output-directory>");
    return 2;
}

using PDDocument pdf = Loader.LoadPDF(args[0]);
PdfLayoutDocument layout = PdfLayoutExtractor.Extract(pdf);
PdfMarkdownDocument markdown = PdfMarkdownConverter.Convert(layout);
markdown.WriteToDirectory(args[1]);
return 0;
