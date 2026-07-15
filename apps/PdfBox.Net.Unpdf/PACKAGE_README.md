# unpdf.tool

`unpdf.tool` is a cross-platform .NET command-line tool that converts PDF files
to self-contained HTML output directories. The installed command is `unpdf`.

## Install

```console
dotnet tool install --global unpdf.tool
```

Update an existing installation with:

```console
dotnet tool update --global unpdf.tool
```

## Convert a PDF

```console
unpdf input.pdf --output output-directory
```

The output directory contains `index.html`, CSS, fonts, and extracted image
assets. It must be empty unless `--force` is supplied.

Useful commands:

```console
unpdf --help
unpdf --version
unpdf input.pdf --output output-directory --text-mode semantic
unpdf input.pdf --output output-directory --page-mode continuous --force
```

The tool includes its SkiaSharp and ImageMagick rendering dependencies and the
native assets for supported platforms. It requires a compatible .NET runtime.

PDF-to-HTML conversion is necessarily approximate. Unsupported or degraded
operations are reported as diagnostics, and semantic output may not reproduce
arbitrary PDF painting exactly.

Source, issues, and other installation options are available at
[github.com/erikbra/unpdf](https://github.com/erikbra/unpdf).
