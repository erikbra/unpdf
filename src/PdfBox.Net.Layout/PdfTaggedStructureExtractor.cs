using PdfBox.Net.COS;
using PdfBox.Net.PDModel;
using PdfBox.Net.PDModel.DocumentInterchange.LogicalStructure;
using PdfBox.Net.PDModel.DocumentInterchange.TaggedPdf;

namespace PdfBox.Net.Layout;

internal static class PdfTaggedStructureExtractor
{
    public static (PdfTaggedStructureDocument? Document, IReadOnlyList<PdfLayoutDiagnostic> Diagnostics) Extract(
        PDDocument document,
        IReadOnlyList<PdfLayoutPage> layoutPages)
    {
        PDStructureTreeRoot? structureRoot = document.GetDocumentCatalog().GetStructureTreeRoot();
        if (structureRoot == null)
        {
            return (null, []);
        }

        ExtractionContext context = new(document, layoutPages, structureRoot);
        List<PdfTaggedStructureElement> roots = [];
        foreach (object kid in structureRoot.GetKids())
        {
            if (kid is PDStructureElement element &&
                context.ExtractElement(element, inheritedPage: null) is PdfTaggedStructureElement projected)
            {
                roots.Add(projected);
            }
        }

        PdfTaggedStructureDocument tagged = new(roots);
        foreach (PdfTaggedStructureElement figure in tagged.Elements
            .Where(static element => element.Kind == PdfTaggedStructureKind.Figure &&
                !string.IsNullOrWhiteSpace(element.AlternateDescription)))
        {
            foreach (PdfLayoutImage image in figure.DescendantContentReferences()
                .SelectMany(static reference => reference.Images))
            {
                image.AlternateDescription ??= figure.AlternateDescription;
            }
        }

        return (tagged, context.Diagnostics);
    }

    private sealed class ExtractionContext
    {
        private readonly Dictionary<COSDictionary, int> _pageNumbers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, PdfLayoutPage> _layoutPages;
        private readonly Dictionary<int, COSArray> _parentArraysByPage = [];
        private readonly HashSet<COSDictionary> _activeElements = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _reportedDiagnostics = new(StringComparer.Ordinal);
        private readonly List<PdfLayoutDiagnostic> _diagnostics = [];

        public ExtractionContext(
            PDDocument document,
            IReadOnlyList<PdfLayoutPage> layoutPages,
            PDStructureTreeRoot structureRoot)
        {
            _layoutPages = layoutPages.ToDictionary(static page => page.PageNumber);
            int pageNumber = 1;
            foreach (PDPage page in document.GetPages())
            {
                _pageNumbers[(COSDictionary)page.GetCOSObject()] = pageNumber;
                int structParents = page.GetStructParents();
                if (structParents >= 0 &&
                    structureRoot.GetParentTree() is PDParentTreeNumberTreeNode parentTree &&
                    FindNumberTreeValue(
                        parentTree.GetCOSObject(),
                        structParents,
                        new HashSet<COSDictionary>(ReferenceEqualityComparer.Instance)) is COSArray parentArray)
                {
                    _parentArraysByPage[pageNumber] = parentArray;
                }

                pageNumber++;
            }
        }

        public IReadOnlyList<PdfLayoutDiagnostic> Diagnostics => _diagnostics;

        public PdfTaggedStructureElement? ExtractElement(PDStructureElement element, PDPage? inheritedPage)
        {
            COSDictionary dictionary = element.GetCOSObject();
            if (!_activeElements.Add(dictionary))
            {
                Report(
                    "tagged-structure-cycle",
                    "Tagged PDF structure contains a cycle; the repeated element was ignored.",
                    PageNumber(element.GetPage() ?? inheritedPage));
                return null;
            }

            try
            {
                PDPage? elementPage = element.GetPage() ?? inheritedPage;
                string structureType = element.GetStructureType() ?? "Unknown";
                string standardType = element.GetStandardStructureType() ?? structureType;
                PdfTaggedStructureKind kind = StructureKind(standardType);
                if (kind == PdfTaggedStructureKind.Other &&
                    !StandardStructureTypes.Types.Contains(standardType))
                {
                    Report(
                        "tagged-structure-type-unsupported",
                        $"Tagged structure type '{structureType}' (resolved as '{standardType}') is not supported.",
                        PageNumber(elementPage));
                }

                List<PdfTaggedStructureKid> kids = [];
                foreach (object kid in element.GetKids())
                {
                    switch (kid)
                    {
                        case int markedContentId:
                            kids.Add(new PdfTaggedContentKid(ResolveContent(element, elementPage, markedContentId)));
                            break;
                        case PDMarkedContentReference markedReference:
                            kids.Add(new PdfTaggedContentKid(ResolveContent(
                                element,
                                markedReference.GetPage() ?? elementPage,
                                markedReference.GetMCID())));
                            break;
                        case PDStructureElement child:
                            if (ExtractElement(child, elementPage) is PdfTaggedStructureElement projectedChild)
                            {
                                kids.Add(new PdfTaggedElementKid(projectedChild));
                            }
                            break;
                    }
                }

                return new PdfTaggedStructureElement(
                    structureType,
                    standardType,
                    kind,
                    element.GetActualText(),
                    element.GetAlternateDescription(),
                    element.GetLanguage(),
                    element.GetTitle(),
                    ExtractAttributes(element),
                    kids);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
            {
                Report(
                    "tagged-structure-element-unresolved",
                    $"Tagged structure element could not be resolved: {exception.Message}",
                    PageNumber(element.GetPage() ?? inheritedPage));
                return null;
            }
            finally
            {
                _activeElements.Remove(dictionary);
            }
        }

        private PdfTaggedContentReference ResolveContent(
            PDStructureElement owner,
            PDPage? page,
            int markedContentId)
        {
            int? pageNumber = PageNumber(page);
            if (pageNumber is not int resolvedPageNumber || !_layoutPages.TryGetValue(resolvedPageNumber, out PdfLayoutPage? layoutPage))
            {
                Report(
                    "tagged-structure-page-unresolved",
                    $"MCID {markedContentId} for structure type '{owner.GetStructureType() ?? "Unknown"}' has no resolvable page.",
                    null,
                    markedContentId);
                return new PdfTaggedContentReference(0, markedContentId, [], []);
            }

            ValidateParentTree(owner, resolvedPageNumber, markedContentId);
            PdfTextRun[] runs = layoutPage.Runs
                .Where(run => run.MarkedContentId == markedContentId)
                .ToArray();
            PdfLayoutImage[] images = layoutPage.Images
                .Where(image => image.MarkedContentId == markedContentId)
                .ToArray();
            if (runs.Length == 0 && images.Length == 0)
            {
                Report(
                    "tagged-structure-mcid-unresolved",
                    $"MCID {markedContentId} for structure type '{owner.GetStructureType() ?? "Unknown"}' did not resolve to extracted page content.",
                    resolvedPageNumber,
                    markedContentId);
            }

            return new PdfTaggedContentReference(resolvedPageNumber, markedContentId, runs, images);
        }

        private void ValidateParentTree(PDStructureElement owner, int pageNumber, int markedContentId)
        {
            if (!_parentArraysByPage.TryGetValue(pageNumber, out COSArray? entries) ||
                markedContentId < 0 ||
                markedContentId >= entries.Size())
            {
                Report(
                    "tagged-structure-parent-tree-missing",
                    $"Page {pageNumber} has no parent-tree entry for MCID {markedContentId}.",
                    pageNumber,
                    markedContentId);
                return;
            }

            COSDictionary ownerDictionary = owner.GetCOSObject();
            COSDictionary? parentOwner = entries.GetObject(markedContentId) switch
            {
                COSDictionary dictionary => dictionary,
                COSObject indirect when indirect.GetObject() is COSDictionary dictionary => dictionary,
                _ => null
            };
            if (parentOwner == null ||
                !(ReferenceEquals(parentOwner, ownerDictionary) || parentOwner.Equals(ownerDictionary)))
            {
                Report(
                    "tagged-structure-parent-tree-mismatch",
                    $"Page {pageNumber} parent tree does not associate MCID {markedContentId} with structure type '{owner.GetStructureType() ?? "Unknown"}'.",
                    pageNumber,
                    markedContentId);
            }
        }

        private static COSBase? FindNumberTreeValue(
            COSDictionary node,
            int key,
            HashSet<COSDictionary> visited)
        {
            if (!visited.Add(node))
            {
                return null;
            }

            COSArray? numbers = node.GetCOSArray(COSName.GetPDFName("Nums"));
            if (numbers != null)
            {
                for (int index = 0; index + 1 < numbers.Size(); index += 2)
                {
                    if (numbers.GetObject(index) is COSInteger number && number.IntValue() == key)
                    {
                        return numbers.GetObject(index + 1);
                    }
                }
            }

            COSArray? kids = node.GetCOSArray(COSName.KIDS);
            if (kids == null)
            {
                return null;
            }

            for (int index = 0; index < kids.Size(); index++)
            {
                COSDictionary? child = kids.GetObject(index) switch
                {
                    COSDictionary dictionary => dictionary,
                    COSObject indirect when indirect.GetObject() is COSDictionary dictionary => dictionary,
                    _ => null
                };
                if (child != null && FindNumberTreeValue(child, key, visited) is COSBase value)
                {
                    return value;
                }
            }

            return null;
        }

        private int? PageNumber(PDPage? page)
        {
            if (page == null)
            {
                return null;
            }

            COSDictionary dictionary = (COSDictionary)page.GetCOSObject();
            if (_pageNumbers.TryGetValue(dictionary, out int pageNumber))
            {
                return pageNumber;
            }

            foreach ((COSDictionary candidate, int candidatePageNumber) in _pageNumbers)
            {
                if (candidate.Equals(dictionary))
                {
                    return candidatePageNumber;
                }
            }

            return null;
        }

        private void Report(
            string code,
            string message,
            int? pageNumber,
            int? markedContentId = null)
        {
            string key = string.Join('|', code, pageNumber, markedContentId, message);
            if (_reportedDiagnostics.Add(key))
            {
                _diagnostics.Add(new PdfLayoutDiagnostic(
                    PdfLayoutDiagnosticSeverity.Warning,
                    code,
                    message,
                    pageNumber));
            }
        }
    }

    private static PdfTaggedStructureAttributes ExtractAttributes(PDStructureElement element)
    {
        List<string> owners = [];
        string? listNumbering = null;
        int rowSpan = 1;
        int columnSpan = 1;
        string? tableScope = null;
        IReadOnlyList<string> tableHeaders = [];
        string? tableSummary = null;
        string? placement = null;
        string? writingMode = null;
        string? textAlignment = null;

        Revisions<PDAttributeObject> revisions = element.GetAttributes();
        for (int index = 0; index < revisions.Size(); index++)
        {
            PDAttributeObject attribute = revisions.GetObject(index);
            if (attribute.GetOwner() is string owner)
            {
                owners.Add(owner);
            }

            switch (attribute)
            {
                case PDListAttributeObject list:
                    listNumbering ??= list.GetListNumbering();
                    break;
                case PDTableAttributeObject table:
                    rowSpan = table.GetRowSpan();
                    columnSpan = table.GetColSpan();
                    tableScope ??= table.GetScope();
                    tableHeaders = table.GetHeaders().ToArray();
                    tableSummary ??= table.GetSummary();
                    break;
                case PDLayoutAttributeObject layout:
                    placement ??= layout.GetPlacement();
                    writingMode ??= layout.GetWritingMode();
                    textAlignment ??= layout.GetTextAlign();
                    break;
            }
        }

        return new PdfTaggedStructureAttributes(
            owners,
            listNumbering,
            rowSpan,
            columnSpan,
            tableScope,
            tableHeaders,
            tableSummary,
            placement,
            writingMode,
            textAlignment);
    }

    private static PdfTaggedStructureKind StructureKind(string type) => type switch
    {
        "Document" or "Part" or "Art" => PdfTaggedStructureKind.Document,
        "Sect" or "Div" => PdfTaggedStructureKind.Section,
        "H" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "Title" =>
            PdfTaggedStructureKind.Heading,
        "P" => PdfTaggedStructureKind.Paragraph,
        "L" => PdfTaggedStructureKind.List,
        "LI" => PdfTaggedStructureKind.ListItem,
        "Lbl" => PdfTaggedStructureKind.ListLabel,
        "LBody" => PdfTaggedStructureKind.ListBody,
        "Table" => PdfTaggedStructureKind.Table,
        "TR" => PdfTaggedStructureKind.TableRow,
        "TH" => PdfTaggedStructureKind.TableHeaderCell,
        "TD" => PdfTaggedStructureKind.TableCell,
        "Figure" => PdfTaggedStructureKind.Figure,
        "Link" => PdfTaggedStructureKind.Link,
        _ => PdfTaggedStructureKind.Other
    };
}
