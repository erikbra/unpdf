namespace PdfBox.Net.Layout;

/// <summary>
/// Applies the document section hierarchy to the semantic heading elements emitted page by page.
/// </summary>
internal static class PdfSemanticHeadingNormalizer
{
    internal static PdfSemanticPage[] Normalize(IReadOnlyList<PdfSemanticPage> sourcePages)
    {
        PdfSemanticElement? documentTitle = sourcePages
            .SelectMany(static page => page.Elements)
            .FirstOrDefault(static element =>
                element.Kind == PdfSemanticElementKind.Heading && element.IsDocumentTitle);
        PdfSemanticSectionTree sourceTree = PdfSemanticSectionTree.Create(sourcePages);
        int rootLevel = documentTitle == null ? 1 : 2;

        return sourcePages
            .Select(page => new PdfSemanticPage(
                page.PageNumber,
                page.Elements.Select(element => NormalizeElement(
                    element,
                    sourceTree,
                    documentTitle,
                    rootLevel)).ToArray()))
            .ToArray();
    }

    private static PdfSemanticElement NormalizeElement(
        PdfSemanticElement source,
        PdfSemanticSectionTree sourceTree,
        PdfSemanticElement? documentTitle,
        int rootLevel)
    {
        if (source.Kind != PdfSemanticElementKind.Heading)
        {
            return source;
        }

        bool isDocumentTitle = ReferenceEquals(source, documentTitle);
        PdfSemanticSection? section = sourceTree.FindSection(source);
        int headingLevel;
        if (isDocumentTitle)
        {
            headingLevel = 1;
        }
        else if (source.TaggedStructure != null && !source.IsDocumentTitle)
        {
            headingLevel = Math.Clamp(source.HeadingLevel, 1, 6);
        }
        else if (section == null)
        {
            headingLevel = Math.Clamp(source.HeadingLevel, 1, 6);
        }
        else
        {
            headingLevel = Math.Clamp(rootLevel + SectionDepth(section), 1, 6);
        }

        if (headingLevel == source.HeadingLevel && isDocumentTitle == source.IsDocumentTitle)
        {
            return source;
        }

        return new PdfSemanticElement(
            source.Kind,
            source.Text,
            source.Bounds,
            source.Lines,
            headingLevel,
            source.TableRows,
            source.SemanticList,
            source.DocumentIndex,
            isDocumentTitle,
            source.BibliographyFragment,
            source.DefinitionList,
            source.Quotation,
            source.Aside,
            source.Note,
            source.ThematicBreak,
            source.Algorithm,
            source.TableCaption,
            source.TaggedStructure);
    }

    private static int SectionDepth(PdfSemanticSection section)
    {
        int depth = 0;
        for (PdfSemanticSection? parent = section.Parent; parent != null; parent = parent.Parent)
        {
            depth++;
        }

        return depth;
    }
}
