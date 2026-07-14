# unpdf

`unpdf` converts PDF documents to semantic or fixed-layout HTML on .NET. This
repository contains the `PdfBox.Net.Layout` and `PdfBox.Net.Html` libraries, the
cross-platform `unpdf` command-line application, and the browser WebAssembly
sample.

The conversion projects use the Apache PDFBox-derived .NET libraries from the
pinned [PdfBox.Net](https://github.com/erikbra/pdfbox-net) submodule. Clone with
`git clone --recurse-submodules` (or run `git submodule update --init`).

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
