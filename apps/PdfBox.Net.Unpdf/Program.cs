namespace PdfBox.Net.Unpdf;

public static class Program
{
    public static int Main(string[] args)
    {
        return UnpdfCommand.Run(args, Console.Out, Console.Error);
    }
}
