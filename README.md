# unpdf

`unpdf` converts PDF documents to semantic or fixed-layout HTML on .NET. This
repository contains the `PdfBox.Net.Layout` and `PdfBox.Net.Html` libraries, the
cross-platform `unpdf` command-line application, and the browser WebAssembly
sample.

The conversion projects use the Apache PDFBox-derived .NET libraries published
as [PdfBox.Net packages on NuGet](https://www.nuget.org/profiles/erikbra).

## Build and test

```sh
dotnet restore Unpdf.slnx
dotnet build Unpdf.slnx --configuration Release --no-restore
dotnet test Unpdf.slnx --configuration Release --no-build
```

Run the command-line application with:

```sh
dotnet run --project apps/PdfBox.Net.Unpdf -- input.pdf --output output-directory
```

See [the CLI documentation](apps/PdfBox.Net.Unpdf/README.md) and
[release process](docs/unpdf-release-process.md) for distribution details.
