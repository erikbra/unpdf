using System.Text;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel;

namespace PdfBox.Net.Layout.Tests;

public sealed class PdfSemanticExtractorTest
{
    [Fact]
    public void ReconstructText_PositionedWordGapsPreserveBoundariesWithoutSplittingLetters()
    {
        PdfTextGlyph[] glyphs = CreatePositionedGlyphs(
            ["Justified", "prose", "keeps", "boundaries."],
            characterGap: 0.25f,
            wordGap: 2.5f);

        string text = PdfSemanticExtractor.ReconstructText(glyphs);

        Assert.Equal("Justified prose keeps boundaries.", text);
        Assert.Equal(3, text.Count(static character => character == ' '));
    }

    [Fact]
    public void ReconstructText_ArabicVisualGlyphOrderBecomesLogicalOrder()
    {
        PdfTextGlyph[] glyphs = CreateVisualGlyphs("ةدحتملا ممألا ةموظنم");

        string text = PdfSemanticExtractor.ReconstructText(glyphs);

        Assert.Equal("منظومة الأمم المتحدة", text);
        Assert.Equal(PdfTextDirection.RightToLeft, PdfTextDirectionDetector.Detect(text));
    }

    [Fact]
    public void ReconstructText_MixedArabicLatinAndDigitsPreservesLtrRuns()
    {
        PdfTextGlyph[] glyphs = CreateVisualGlyphs("WHO 2024 ةمظنم");

        string text = PdfSemanticExtractor.ReconstructText(glyphs);

        Assert.Equal("منظمة WHO 2024", text);
        Assert.Equal(PdfTextDirection.RightToLeft, PdfTextDirectionDetector.Detect(text));
    }

    [Fact]
    public void ReconstructText_LeftToRightGlyphOrderIsUnchanged()
    {
        PdfTextGlyph[] glyphs = CreateVisualGlyphs("Section 12 (UN)");

        string text = PdfSemanticExtractor.ReconstructText(glyphs);

        Assert.Equal("Section 12 (UN)", text);
        Assert.Equal(PdfTextDirection.LeftToRight, PdfTextDirectionDetector.Detect(text));
    }

    [Fact]
    public void Extract_AlignedMonospacedBlock_PreservesSourceColumnsAndLineBreaks()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the ordinary body font and line rhythm.", 72f, 72f, 410f),
            CreateFixtureLine("A second prose line completes the surrounding paragraph.", 72f, 84f, 340f),
            CreateMonospacedFixtureLine("LOAD  R0, [R1]", 96f, 124f, fontName: "CMTT10"),
            CreateMonospacedFixtureLine("STORE R0, [R2]", 96f, 136f, fontName: "CMTT10"),
            CreateMonospacedFixtureLine("  ADD R0, #1", 96f, 148f, fontName: "CMTT10"),
            CreateFixtureLine("Ordinary prose resumes after the aligned listing.", 72f, 184f, 310f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement code = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.CodeBlock);

        Assert.Equal("LOAD  R0, [R1]\nSTORE R0, [R2]\n  ADD R0, #1", code.Text);
        Assert.Equal(
            ["LOAD  R0, [R1]", "STORE R0, [R2]", "  ADD R0, #1"],
            code.Lines.Select(static line => line.Text));
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Text.Contains("LOAD", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_IsolatedCodeLikeMonospacedTokenInsideProse_IsAnnotatedInline()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the ordinary body font and line rhythm.", 72f, 72f, 410f),
            CreateFixtureLine("A second prose line completes the surrounding paragraph.", 72f, 84f, 340f),
            CreateInlineCodeFixtureLine("Use the ", "gpio_set()", " helper to configure the pin.", 72f, 124f)
        ]);

        PdfSemanticElement paragraph = Assert.Single(
            Assert.Single(PdfSemanticExtractor.Extract(layout).Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.Paragraph &&
                element.Text.Contains("gpio_set()", StringComparison.Ordinal));
        PdfSemanticInlineCode inlineCode = Assert.Single(Assert.Single(paragraph.Lines).InlineCode);

        Assert.Equal("gpio_set()", inlineCode.Text);
        Assert.All(inlineCode.Runs, static run => Assert.Contains("Courier", run.FontName, StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_MonospacedHeadersFormValuesEmailsAndFormulas_AreNotCode()
    {
        PdfLayoutDocument headerLayout = CreateSemanticPassageFixture(
        [
            CreateMonospacedFixtureLine("[main]  build", 72f, 10f),
            CreateMonospacedFixtureLine("[main]  release", 72f, 22f),
            CreateFixtureLine("Opening body prose establishes the ordinary document content.", 72f, 72f, 380f),
            CreateFixtureLine("A second body line establishes the normal line rhythm.", 72f, 84f, 330f)
        ]);
        PdfSemanticPage headerPage = Assert.Single(PdfSemanticExtractor.Extract(headerLayout).Pages);
        Assert.Contains(headerPage.Elements, static element => element.Kind == PdfSemanticElementKind.Header);
        Assert.DoesNotContain(headerPage.Elements, static element => element.Kind == PdfSemanticElementKind.CodeBlock);

        PdfTextLine firstValue = CreateMonospacedFixtureLine("ACCOUNT  0042", 96f, 124f);
        PdfTextLine secondValue = CreateMonospacedFixtureLine("STATUS   OPEN", 96f, 148f);
        PdfLayoutDocument formLayout = CreateCodeFixtureDocument(
        [
            CreateFixtureLine("Opening body prose establishes an ordinary form page.", 72f, 72f, 340f),
            CreateFixtureLine("A second body line establishes the normal line rhythm.", 72f, 84f, 330f),
            firstValue,
            secondValue
        ],
        [
            CreateFormControl(0, "account", firstValue.Bounds),
            CreateFormControl(1, "status", secondValue.Bounds)
        ]);
        Assert.DoesNotContain(
            Assert.Single(PdfSemanticExtractor.Extract(formLayout).Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.CodeBlock);

        PdfLayoutDocument emailLayout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening body prose establishes the ordinary document content.", 72f, 72f, 380f),
            CreateFixtureLine("A second body line establishes the normal line rhythm.", 72f, 84f, 330f),
            CreateInlineCodeFixtureLine("Contact ", "ops@example.com", " for access.", 72f, 124f),
            CreateMonospacedFixtureLine("ada@example.com", 96f, 148f),
            CreateMonospacedFixtureLine("emmy@example.com", 96f, 160f)
        ]);
        PdfSemanticPage emailPage = Assert.Single(PdfSemanticExtractor.Extract(emailLayout).Pages);
        PdfSemanticElement emailParagraph = Assert.Single(
            emailPage.Elements,
            static element => element.Text.Contains("ops@example.com", StringComparison.Ordinal));
        Assert.All(emailParagraph.Lines, static line => Assert.Empty(line.InlineCode));
        Assert.DoesNotContain(emailPage.Elements, static element => element.Kind == PdfSemanticElementKind.CodeBlock);

        PdfLayoutDocument formulaLayout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening body prose establishes the ordinary scientific content.", 72f, 72f, 400f),
            CreateFixtureLine("A second body line establishes the normal line rhythm.", 72f, 84f, 330f),
            CreateMonospacedFixtureLine("x = y + z", 200f, 124f, fontName: "CMMI10"),
            CreateMonospacedFixtureLine("z = a + b", 200f, 136f, fontName: "CMMI10")
        ]);
        Assert.DoesNotContain(
            Assert.Single(PdfSemanticExtractor.Extract(formulaLayout).Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.CodeBlock);
    }

    [Fact]
    public void Extract_AdamPageTwo_PreservesRuledAlgorithmCaptionRowsAndIndentation()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "arxiv-adam-page-2.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement element = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Algorithm);
        PdfSemanticAlgorithm algorithm = Assert.IsType<PdfSemanticAlgorithm>(element.Algorithm);

        Assert.StartsWith("Algorithm 1: Adam", algorithm.Caption, StringComparison.Ordinal);
        Assert.Contains("⊙", algorithm.Caption, StringComparison.Ordinal);
        Assert.DoesNotContain('\f', algorithm.Caption);
        Assert.DoesNotContain('�', algorithm.Caption);
        Assert.Equal(5, algorithm.CaptionLines.Count);
        Assert.Equal(17, algorithm.Rows.Count);
        Assert.Equal(4, algorithm.Rows.Count(static row => row.Text.StartsWith("Require:", StringComparison.Ordinal)));
        Assert.StartsWith("while", algorithm.Rows[7].Text, StringComparison.Ordinal);
        Assert.StartsWith("end while", algorithm.Rows[^2].Text, StringComparison.Ordinal);
        Assert.StartsWith("return", algorithm.Rows[^1].Text, StringComparison.Ordinal);
        Assert.Equal(0f, algorithm.Rows[0].Indentation, 2);
        Assert.InRange(algorithm.Rows[4].Indentation, 9f, 11f);
        Assert.InRange(algorithm.Rows[8].Indentation, 18f, 21f);
        Assert.Contains("1st", algorithm.Rows[4].Text, StringComparison.Ordinal);
        Assert.Contains(algorithm.Rows[1].Line.Runs, static run => run.FontName.Contains("CMR7", StringComparison.Ordinal));
        Assert.InRange(algorithm.TopRule.Thickness, 0.7f, 0.9f);
        Assert.InRange(algorithm.CaptionRule.Thickness, 0.7f, 0.9f);
        Assert.InRange(algorithm.BottomRule.Thickness, 0.7f, 0.9f);
        Assert.Equal(3, new[]
        {
            algorithm.TopRule.SourcePathIndex,
            algorithm.CaptionRule.SourcePathIndex,
            algorithm.BottomRule.SourcePathIndex
        }.Distinct().Count());
        Assert.DoesNotContain(page.Elements, static candidate =>
            candidate.Kind == PdfSemanticElementKind.ThematicBreak);
        Assert.DoesNotContain(page.Elements, static candidate =>
            candidate.Kind == PdfSemanticElementKind.Paragraph &&
            candidate.Text.StartsWith("Require:", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_AdamPageTwo_SeparatesReplacementFormulaInVisualTokenOrder()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "arxiv-adam-page-2.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement prose = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.EndsWith("with the following lines:", StringComparison.Ordinal));
        Assert.Equal(2, prose.Lines.Count);
        Assert.DoesNotContain('√', prose.Text);
        Assert.DoesNotContain('←', prose.Text);

        PdfSemanticElement formula = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("αt = α", StringComparison.Ordinal));
        PdfSemanticLine formulaLine = Assert.Single(formula.Lines);
        Assert.Equal(
            "αt = α · √1 − β2t/(1 − β1t) and θt ← θt−1 − αt · mt/(√vt + ε̂).",
            formula.Text);
        Assert.Equal(
            formulaLine.Runs.OrderBy(static run => run.Bounds.X).ThenBy(static run => run.Bounds.Y),
            formulaLine.Runs);
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Trim().All(character => char.IsWhiteSpace(character) || character is '·' or '−' or '←'));
    }

    [Fact]
    public void Extract_AdamPageTwo_RestoresProseWordBoundariesAndDiscretionaryHyphenation()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "arxiv-adam-page-2.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        string prose = string.Join(" ", page.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Paragraph)
            .Select(static element => element.Text));

        Assert.Contains("careful choice of stepsizes", prose, StringComparison.Ordinal);
        Assert.Contains("parameter space at timestep t", prose, StringComparison.Ordinal);
        Assert.Contains("noisy objective function", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("ofstepsizes", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("attimestep", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("objec-tive", prose, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_JmlrLdaPageFour_SeparatesNumberedDisplayFormulasFromProse()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "jmlr-lda-page-4.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement equationOne = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Any(static line => line.Text.TrimEnd().EndsWith("(1)", StringComparison.Ordinal)));
        PdfSemanticElement equationTwo = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Any(static line => line.Text.TrimEnd().EndsWith("(2)", StringComparison.Ordinal)));

        Assert.InRange(equationOne.Bounds.Height, 35f, 55f);
        Assert.InRange(equationTwo.Bounds.Height, 20f, 35f);
        Assert.Contains("p(θ", equationOne.Text, StringComparison.Ordinal);
        Assert.Contains("p(θ,z,w|α,β)", equationTwo.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("where the parameter", equationOne.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Given the parameters", equationTwo.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("We refer to the latent", equationTwo.Text, StringComparison.Ordinal);

        PdfSemanticElement simplexProse = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("probability density on this", StringComparison.Ordinal));
        Assert.Contains("simplex:", simplexProse.Text, StringComparison.Ordinal);
        Assert.Contains(simplexProse.Lines, static line =>
            line.Text.Contains('≥') && line.Text.Contains('k'));
        Assert.Contains(simplexProse.Lines, static line =>
            line.Text.Replace(" ", "", StringComparison.Ordinal).Contains("i=1", StringComparison.Ordinal));
        Assert.True(simplexProse.Bounds.Bottom <= equationOne.Bounds.Y + 1f);

        PdfSemanticElement interveningProse = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("where the parameter", StringComparison.Ordinal));
        Assert.Contains("a set of N words w is given by:", interveningProse.Text, StringComparison.Ordinal);
        Assert.True(equationOne.Bounds.Bottom < interveningProse.Bounds.Y);
        Assert.True(interveningProse.Bounds.Bottom < equationTwo.Bounds.Y);

        PdfSemanticElement footnote = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("1. We refer to the latent", StringComparison.Ordinal));
        Assert.True(equationTwo.Bounds.Bottom < footnote.Bounds.Y);

        AssertTrailingEquationNumber(equationOne, "(1)");
        AssertTrailingEquationNumber(equationTwo, "(2)");

        PdfTextGlyph[] formulaGlyphs = equationOne.Lines.Concat(equationTwo.Lines)
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .ToArray();
        Assert.All(formulaGlyphs, glyph =>
            Assert.Equal(1, formulaGlyphs.Count(candidate => ReferenceEquals(candidate, glyph))));
        PdfTextGlyph[] numberedSourceGlyphs = Assert.Single(layout.Pages).Lines
            .Where(static line => line.Text.EndsWith("(1)", StringComparison.Ordinal) ||
                line.Text.EndsWith("(2)", StringComparison.Ordinal))
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .ToArray();
        Assert.All(numberedSourceGlyphs, glyph =>
            Assert.Equal(1, formulaGlyphs.Count(candidate => ReferenceEquals(candidate, glyph))));

        static void AssertTrailingEquationNumber(PdfSemanticElement formula, string number)
        {
            PdfSemanticLine line = Assert.Single(formula.Lines, line =>
                line.Text.TrimEnd().EndsWith(number, StringComparison.Ordinal));
            PdfTextGlyph[] glyphs = line.Runs
                .SelectMany(static run => run.Glyphs)
                .OrderBy(static glyph => glyph.Bounds.X)
                .ThenBy(static glyph => glyph.Bounds.Y)
                .ToArray();
            PdfTextGlyph[] numberGlyphs = glyphs[^number.Length..];
            Assert.Equal(number, string.Concat(numberGlyphs.Select(static glyph => glyph.Text)));
            Assert.True(
                numberGlyphs[0].Bounds.X - glyphs[..^number.Length].Max(static glyph => glyph.Bounds.Right) >=
                numberGlyphs.Max(static glyph => glyph.FontSize) * 1.4f);
        }
    }

    [Fact]
    public void Extract_JmlrLdaPageEleven_SeparatesUnnumberedDisplayFormulasFromProse()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "jmlr-lda-page-11.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement introduction = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("The key inferential problem", StringComparison.Ordinal));
        Assert.EndsWith("given a document:", introduction.Text, StringComparison.Ordinal);
        Assert.Equal(2, introduction.Lines.Count);
        Assert.DoesNotContain("p(θ", introduction.Text, StringComparison.Ordinal);

        PdfSemanticElement posterior = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Any(static line =>
                line.Text.StartsWith("p(θ,z", StringComparison.Ordinal) && line.Text.Contains('=')));
        Assert.Contains("p(θ,z,w", posterior.Text, StringComparison.Ordinal);
        Assert.Contains("p(w", posterior.Text, StringComparison.Ordinal);
        Assert.InRange(posterior.Bounds.Height, 18f, 30f);

        PdfSemanticElement interveningProse = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Unfortunately, this distribution", StringComparison.Ordinal));
        Assert.EndsWith("model parameters:", interveningProse.Text, StringComparison.Ordinal);
        Assert.Equal(2, interveningProse.Lines.Count);

        PdfSemanticElement marginal = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Any(static line =>
                line.Text.StartsWith("p(w|", StringComparison.Ordinal) && line.Text.Contains('=')));
        Assert.Contains(marginal.Lines, static line => line.Text.Contains('∫'));
        Assert.Contains(marginal.Lines, static line => line.Text.Contains("i−", StringComparison.Ordinal));
        Assert.InRange(marginal.Bounds.Height, 35f, 55f);
        Assert.DoesNotContain("coupling between", marginal.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a function which is intractable", marginal.Text, StringComparison.Ordinal);

        PdfSemanticElement followingProse = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("a function which is intractable", StringComparison.Ordinal));
        Assert.Contains("coupling between θ and β", followingProse.Text, StringComparison.Ordinal);

        Assert.True(introduction.Bounds.Bottom < posterior.Bounds.Y);
        Assert.True(posterior.Bounds.Bottom < interveningProse.Bounds.Y);
        Assert.True(interveningProse.Bounds.Y < marginal.Bounds.Y);
        Assert.True(marginal.Bounds.Bottom < followingProse.Bounds.Y);

        PdfTextGlyph[] formulaGlyphs = posterior.Lines.Concat(marginal.Lines)
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .ToArray();
        PdfTextGlyph[] semanticGlyphs = page.Elements
            .SelectMany(static element => element.Lines)
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .ToArray();
        Assert.All(formulaGlyphs, glyph =>
            Assert.Equal(1, semanticGlyphs.Count(candidate => ReferenceEquals(candidate, glyph))));
    }

    [Fact]
    public void Extract_JmlrLdaPageTwelve_SeparatesAdjacentNumberedEquationsFromTables()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "jmlr-lda-page-12.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        string[] equationNumbers = ["(4)", "(5)", "(6)", "(7)", "(8)"];
        PdfSemanticElement[] formulas = equationNumbers
            .Select(number => Assert.Single(page.Elements, element =>
                element.Kind == PdfSemanticElementKind.Paragraph &&
                element.Lines.Any(line => line.Text.TrimEnd().EndsWith(number, StringComparison.Ordinal))))
            .ToArray();

        Assert.Equal(formulas.Length, formulas.Distinct(ReferenceEqualityComparer.Instance).Count());
        for (int index = 1; index < formulas.Length; index++)
        {
            Assert.True(formulas[index - 1].Bounds.Y < formulas[index].Bounds.Y);
        }
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table &&
            (element.Text.Contains("(6)", StringComparison.Ordinal) ||
                element.Text.Contains("(7)", StringComparison.Ordinal)));
        Assert.Contains("φ", formulas[2].Text, StringComparison.Ordinal);
        Assert.Contains("γ", formulas[3].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("zero, we obtain", formulas[2].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("zero, we obtain", formulas[3].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_InlineLargeOperatorInRightColumn_RemainsInParagraphFlow()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("The estimator is evaluated on every observation in the sample.", 330f, 72f, 230f),
            CreateFixtureLine("Its definition remains part of this ordinary right-column paragraph.", 330f, 84f, 240f),
            CreateStyledFixtureLine(
                330f,
                96f,
                ("The estimator is computed as ", "Times-Roman"),
                ("∑", "CMEX10"),
                (" x over the sample.", "Times-Roman")),
            CreateFixtureLine("The surrounding discussion continues without a display break.", 330f, 108f, 220f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement paragraph = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph && element.Text.Contains('∑'));

        Assert.Equal(4, paragraph.Lines.Count);
        Assert.StartsWith("The estimator is evaluated", paragraph.Text, StringComparison.Ordinal);
        Assert.EndsWith("without a display break.", paragraph.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ShortInlineLargeOperatorInRightColumn_RemainsInParagraphFlow()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("A short instruction follows in the right column.", 330f, 72f, 230f),
            CreateStyledFixtureLine(
                330f,
                84f,
                ("Use the ", "Times-Roman"),
                ("∑", "CMEX10"),
                (" operator", "Times-Roman")),
            CreateFixtureLine("The discussion then continues normally.", 330f, 96f, 210f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement paragraph = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph && element.Text.Contains('∑'));

        Assert.Equal(3, paragraph.Lines.Count);
        Assert.Contains("Use the ∑ operator", paragraph.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_NumberedFormula_DoesNotAbsorbNearbyMixedProse()
    {
        PdfTextLine formula = CreateStyledFixtureLineWithTrailingNumber(
            220f,
            120f,
            500f,
            "(1)",
            ("x", "CMMI10"),
            ("=", "CMR10"),
            ("1", "CMR10"));
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the body font and line rhythm.", 72f, 72f, 390f),
            CreateFixtureLine("The next display is followed by a short explanatory clause.", 72f, 84f, 410f),
            formula,
            CreateStyledFixtureLine(
                220f,
                132f,
                ("if ", "Times-Roman"),
                ("x", "CMMI10"),
                (" = 1", "Times-Roman")),
            CreateFixtureLine("Ordinary prose resumes after the explanation.", 72f, 160f, 310f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement equation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph && element.Text.EndsWith("(1)", StringComparison.Ordinal));
        PdfSemanticElement explanation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("if x = 1", StringComparison.Ordinal));

        Assert.Single(equation.Lines);
        Assert.DoesNotContain("if", equation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(1)", explanation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_SubjectToProseNearNumberedFormula_RemainsSeparate()
    {
        PdfTextLine formula = CreateStyledFixtureLineWithTrailingNumber(
            220f,
            120f,
            500f,
            "(1)",
            ("x", "CMMI10"),
            ("=", "CMR10"),
            ("1", "CMR10"));
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the body font and line rhythm.", 72f, 72f, 390f),
            formula,
            CreateStyledFixtureLine(
                220f,
                132f,
                ("Subject to ", "Times-Roman"),
                ("x", "CMMI10"),
                (" = 1 being approved remains provisional", "Times-Roman")),
            CreateFixtureLine("Ordinary prose resumes after the explanation.", 72f, 160f, 310f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement equation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph && element.Text.EndsWith("(1)", StringComparison.Ordinal));
        PdfSemanticElement explanation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Subject to x = 1 being approved", StringComparison.Ordinal));

        Assert.Single(equation.Lines);
        Assert.DoesNotContain("approved", equation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(1)", explanation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_SubjectToFormulaWithLongFunctionName_JoinsNumberedProgram()
    {
        PdfTextLine formula = CreateStyledFixtureLineWithTrailingNumber(
            220f,
            120f,
            500f,
            "(1)",
            ("x", "CMMI10"),
            ("=", "CMR10"),
            ("1", "CMR10"));
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the body font and line rhythm.", 72f, 72f, 390f),
            formula,
            CreateStyledFixtureLine(
                220f,
                132f,
                ("subject to diag(", "Times-Roman"),
                ("X", "CMMI10"),
                (") = 1", "Times-Roman")),
            CreateFixtureLine("Ordinary prose resumes after the program.", 72f, 160f, 310f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement equation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("(1)", StringComparison.Ordinal) &&
            element.Text.Contains("subject to", StringComparison.Ordinal));

        Assert.Equal(2, equation.Lines.Count);
        Assert.Contains("subject to diag(X) = 1", equation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_MinimizedProseNearNumberedFormula_RemainsSeparate()
    {
        PdfTextLine formula = CreateStyledFixtureLineWithTrailingNumber(
            220f,
            120f,
            500f,
            "(1)",
            ("x", "CMMI10"),
            ("=", "CMR10"),
            ("1", "CMR10"));
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the body font and line rhythm.", 72f, 72f, 390f),
            formula,
            CreateStyledFixtureLine(
                220f,
                132f,
                ("Minimized result ", "Times-Roman"),
                ("x", "CMMI10"),
                (" = 1 remains provisional", "Times-Roman")),
            CreateFixtureLine("Ordinary prose resumes after the explanation.", 72f, 160f, 310f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement equation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph && element.Text.EndsWith("(1)", StringComparison.Ordinal));
        PdfSemanticElement explanation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Minimized result", StringComparison.Ordinal));

        Assert.Single(equation.Lines);
        Assert.DoesNotContain("Minimized", equation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(1)", explanation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_TableRowsEndingInParenthesizedNumbers_RemainTableRows()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("The following parameter summary contains two aligned columns.", 72f, 72f, 390f),
            CreateFormulaLikeTableRow("parameter", "value", "(2)", 120f),
            CreateFormulaLikeTableRow("threshold", "limit", "(3)", 134f),
            CreateFormulaLikeTableRow("offset", "constant", "(4)", 148f),
            CreateFixtureLine("Ordinary prose resumes below the table.", 72f, 190f, 300f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement table = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table);

        Assert.Equal(
            ["parameter", "= value", "(2)"],
            table.TableRows[0].Cells.Select(static cell => cell.Text));
        Assert.Equal(
            ["threshold", "= limit", "(3)"],
            table.TableRows[1].Cells.Select(static cell => cell.Text));
        Assert.Equal(
            ["offset", "= constant", "(4)"],
            table.TableRows[2].Cells.Select(static cell => cell.Text));
        Assert.Equal(["(2)", "(3)", "(4)"], table.TableRows.Select(static row => row.Cells[^1].Text));
    }

    [Fact]
    public void Extract_WideSparseBibliographyRows_RemainProse()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening body prose establishes the ordinary font.", 72f, 52f, 340f),
            CreateFixtureLine("References", 72f, 82f, 72f, 14f, "Times-Bold"),
            CreateCompositeFixtureLine(
                116f,
                ("Ryan Kiros,", 72f, 74f, "Times-Roman"),
                ("Yukun Zhu,", 158f, 70f, "Times-Roman"),
                ("Ruslan R Salakhutdinov,", 240f, 142f, "Times-Roman"),
                ("1499-1509.", 430f, 72f, "Times-Roman")),
            CreateCompositeFixtureLine(
                130f,
                ("and Sanja Fidler. 2015.", 72f, 132f, "Times-Roman"),
                ("Skip-thought vectors.", 216f, 116f, "Times-Roman"),
                ("Minjoon Seo,", 344f, 78f, "Times-Roman"),
                ("Aniruddha Kembhavi,", 434f, 118f, "Times-Roman")),
            CreateCompositeFixtureLine(
                144f,
                ("Quoc Le and Tomas Mikolov.", 72f, 148f, "Times-Roman"),
                ("Distributed representations.", 232f, 140f, "Times-Roman"),
                ("Richard Socher. 2013.", 384f, 116f, "Times-Roman"),
                ("Recursive models.", 512f, 92f, "Times-Roman"))
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.Table);
        string text = string.Join(" ", page.Elements.Select(static element => element.Text));
        Assert.Contains("Ryan Kiros", text, StringComparison.Ordinal);
        Assert.Contains("Minjoon Seo", text, StringComparison.Ordinal);
        Assert.Contains("Richard Socher", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_LineBreakHyphenation_UsesParagraphEdgesAndPreservesAuthoredCompounds()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("The noisy objec-", 72f, 72f, 400f),
            CreateFixtureLine("tive function is state-", 72f, 84f, 250f),
            CreateFixtureLine("of-the-art and remains readable.", 72f, 96f, 400f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement paragraph = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph);

        Assert.Equal(
            "The noisy objective function is state-of-the-art and remains readable.",
            paragraph.Text);
    }

    [Fact]
    public void Extract_DetachedMathFragments_FormSeparateDisplayFormulaInSourceOrder()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary prose establishes the body font and column width.", 72f, 72f, 400f),
            CreateFixtureLine("The replacement equations follow on the next visual row:", 72f, 84f, 400f),
            CreateCompositeFixtureLine(89.5f, ("√", 150f, 8f, "CMSY10")),
            CreateCompositeFixtureLine(
                96.5f,
                ("·", 148f, 4f, "CMSY10"),
                ("−", 176f, 8f, "CMSY10"),
                ("←", 260f, 10f, "CMSY10")),
            CreateCompositeFixtureLine(
                97f,
                ("a", 120f, 8f, "CMMI10"),
                ("=", 132f, 8f, "CMR10"),
                ("1", 160f, 8f, "CMR10"),
                ("b", 190f, 8f, "CMMI10"),
                ("and", 212f, 20f, "Times-Roman"),
                ("x", 245f, 8f, "CMMI10"),
                ("y", 275f, 8f, "CMMI10"),
                ("p", 300f, 8f, "CMMI10"),
                ("/", 315f, 8f, "CMMI10"),
                ("q", 330f, 8f, "CMMI10"),
                ("r", 345f, 8f, "CMMI10"))
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement[] paragraphs = page.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Paragraph)
            .ToArray();
        PdfSemanticElement prose = Assert.Single(paragraphs, static paragraph =>
            paragraph.Text.EndsWith("next visual row:", StringComparison.Ordinal));
        PdfSemanticElement formulaElement = Assert.Single(paragraphs, static paragraph =>
            paragraph.Text.Contains('←'));
        PdfSemanticLine formula = Assert.Single(formulaElement.Lines);
        Assert.Equal(
            ["a", "=", "·", "√", "1", "−", "b", "and", "x", "←", "y", "p", "/", "q", "r"],
            formula.Runs.Select(static run => run.Text));
        Assert.DoesNotContain('√', prose.Text);
        Assert.DoesNotContain('←', prose.Text);
    }

    [Fact]
    public void Extract_GenericRuledPseudocode_UsesStructuralEvidenceAndRequiresAllRules()
    {
        PdfTextLine[] lines =
        [
            CreateFixtureLine("Opening prose establishes the ordinary body font and line rhythm.", 72f, 72f, 410f),
            CreateFixtureLine("A second prose line completes the surrounding paragraph.", 72f, 84f, 340f),
            CreateFixtureLine("Algorithm 7: Generic bounded search.", 72f, 118f, 250f, fontName: "Times-Bold"),
            CreateFixtureLine("Require: input sequence", 72f, 148f, 150f),
            CreateFixtureLine("Require: stopping condition", 72f, 160f, 180f),
            CreateFixtureLine("while work remains do", 84f, 172f, 150f),
            CreateFixtureLine("candidate = next(input)", 96f, 184f, 170f),
            CreateFixtureLine("state = update(candidate)", 96f, 196f, 180f),
            CreateFixtureLine("end while", 84f, 208f, 80f),
            CreateFixtureLine("return state", 84f, 220f, 90f),
            CreateFixtureLine("Ordinary prose resumes after the pseudocode block.", 72f, 260f, 330f)
        ];
        PdfLayoutPath[] completeRules =
        [
            CreateRulePath(0, 72f, 110f, 472f, 110f),
            CreateRulePath(1, 72f, 140f, 472f, 140f),
            CreateRulePath(2, 72f, 236f, 472f, 236f)
        ];

        PdfSemanticElement algorithm = Assert.Single(
            Assert.Single(PdfSemanticExtractor.Extract(CreateSemanticPassageFixture(lines, completeRules)).Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.Algorithm);
        Assert.Equal("Algorithm 7: Generic bounded search.", algorithm.Algorithm!.Caption);
        Assert.Equal(7, algorithm.Algorithm.Rows.Count);

        PdfSemanticPage incomplete = Assert.Single(PdfSemanticExtractor.Extract(
            CreateSemanticPassageFixture(lines, completeRules.Take(2).ToArray())).Pages);
        Assert.DoesNotContain(incomplete.Elements, static element => element.Kind == PdfSemanticElementKind.Algorithm);
    }

    [Fact]
    public void Extract_ScientificFrontMatter_GroupsAffiliationsAndSeparatesDateFromAbstractHeading()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateScientificFrontMatterFixture(inlineAbstract: false, includeNarrowQuotation: false)).Pages);

        PdfSemanticElement frontMatter = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.FrontMatter);
        Assert.Equal(5, frontMatter.Lines.Count);
        Assert.Equal("Ada Lovelace and Emmy Noether", frontMatter.Lines[0].Text);
        Assert.StartsWith("† Department of Applied Mathematics", frontMatter.Lines[1].Text);
        Assert.StartsWith("‡ Center for Computational Science", frontMatter.Lines[2].Text);
        Assert.StartsWith("§ Institute for Scientific Computing", frontMatter.Lines[3].Text);
        Assert.Equal("September 2008", frontMatter.Lines[4].Text);

        PdfSemanticElement abstractHeading = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading && element.Text == "Abstract");
        Assert.True(abstractHeading.Bounds.Y > frontMatter.Bounds.Bottom);
        Assert.DoesNotContain("Abstract", frontMatter.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ScientificFrontMatter_SeparatesHomePageFromInlineAbstractAndKeepsNarrowQuotationOrdinary()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateScientificFrontMatterFixture(inlineAbstract: true, includeNarrowQuotation: true)).Pages);

        PdfSemanticElement frontMatter = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.FrontMatter);
        Assert.EndsWith("WWW home page: https://example.edu/research", frontMatter.Text, StringComparison.Ordinal);
        PdfSemanticElement abstractParagraph = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Abstract. This study", StringComparison.Ordinal));
        Assert.True(abstractParagraph.Bounds.Y > frontMatter.Bounds.Bottom);

        PdfSemanticElement quotation = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("A narrow quotation remains", StringComparison.Ordinal));
        Assert.Equal(2, quotation.Lines.Count);
        Assert.True(quotation.Bounds.Width < 220f);
    }

    [Fact]
    public void Extract_StronglyInsetMultiLinePassage_BecomesBlockQuoteWithoutInventedAttribution()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary body prose establishes the full measure used by nearby paragraphs.", 72f, 72f, 460f),
            CreateFixtureLine("A second body line confirms the normal left and right margins.", 72f, 85f, 430f),
            CreateFixtureLine("This deliberately inset passage carries a sustained observation about reliable", 108f, 132f, 380f),
            CreateFixtureLine("document exchange across several visual lines and remains independent from the", 108f, 145f, 380f),
            CreateFixtureLine("surrounding narrative even without explicit quotation punctuation in the source.", 108f, 158f, 365f),
            CreateFixtureLine("Ordinary body prose resumes at the established page margin after the passage.", 72f, 206f, 450f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement quotation = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.BlockQuote);

        Assert.Equal(3, quotation.Lines.Count);
        Assert.NotNull(quotation.Quotation);
        Assert.Null(quotation.Quotation!.Attribution);
        Assert.StartsWith("This deliberately inset passage", quotation.Quotation.Text);
    }

    [Fact]
    public void Extract_MultiLineQuotedPassage_PreservesSourceAttribution()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 72f, 440f),
            CreateFixtureLine("“The Ghent PDF Output Suite 5.0 was created to check whether a PDF output", 72f, 120f, 450f),
            CreateFixtureLine("workflow conforms to PDF/X-4 and can process production files reliably", 72f, 133f, 430f),
            CreateFixtureLine("without unexpected problems worldwide”, stated Ada Lovelace, workflow chair.", 72f, 146f, 455f),
            CreateFixtureLine("Ordinary body prose resumes after the attributed quotation.", 72f, 194f, 390f)
        ]);

        PdfSemanticElement quotation = Assert.Single(
            Assert.Single(PdfSemanticExtractor.Extract(layout).Pages).Elements,
            static element => element.Kind == PdfSemanticElementKind.BlockQuote);

        Assert.EndsWith("worldwide”,", quotation.Quotation!.Text, StringComparison.Ordinal);
        Assert.Equal("stated Ada Lovelace, workflow chair.", quotation.Quotation.Attribution);
    }

    [Fact]
    public void Extract_OrdinaryQuotedPhraseAndParagraphBeginningWithShortQuote_RemainParagraphs()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 72f, 440f),
            CreateFixtureLine("The report calls this “a useful phrase” while continuing an ordinary", 72f, 120f, 430f),
            CreateFixtureLine("paragraph whose quoted words are not a separate passage.", 72f, 133f, 360f),
            CreateFixtureLine("“Quoted words” can also begin a normal paragraph that continues with", 72f, 180f, 430f),
            CreateFixtureLine("the author’s own analysis over another visual line.", 72f, 193f, 330f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.BlockQuote);
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("The report calls this", StringComparison.Ordinal));
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("“Quoted words”", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_InsetBibliographyEntryWithUrl_RemainsNonQuotationContent()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary body prose establishes the full measure used by nearby paragraphs.", 72f, 72f, 460f),
            CreateFixtureLine("A second body line confirms the normal left and right margins.", 72f, 85f, 430f),
            CreateFixtureLine("Federal Information and Information Systems. U.S. Department of Commerce,", 108f, 132f, 380f),
            CreateFixtureLine("Federal Information Processing Standards Publication 200, Washington, DC.", 108f, 145f, 380f),
            CreateFixtureLine("https://doi.org/10.6028/NIST.FIPS.200", 108f, 158f, 310f),
            CreateFixtureLine("Ordinary body prose resumes at the established page margin after the reference.", 72f, 206f, 450f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.BlockQuote);
    }

    [Fact]
    public void Extract_ShadedLabelledCallout_BecomesAside()
    {
        PdfLayoutPath shading = CreateFilledRectanglePath(new PdfLayoutRectangle(96f, 116f, 400f, 88f));
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 72f, 440f),
            CreateFixtureLine("NOTE", 108f, 128f, 42f, 11f, "Times-Bold"),
            CreateFixtureLine("This independent callout records a tangential implementation detail", 108f, 151f, 360f),
            CreateFixtureLine("without interrupting the natural reading order of the main discussion.", 108f, 164f, 350f),
            CreateFixtureLine("Ordinary body prose resumes outside the shaded callout.", 72f, 230f, 360f)
        ],
        [shading]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement aside = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Aside);

        Assert.Equal("NOTE", aside.Aside!.Label);
        Assert.Single(aside.Aside.Content);
        Assert.Contains("tangential implementation detail", aside.Aside.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ShadedFootnote_IsNeverPromotedToAside()
    {
        PdfLayoutPath shading = CreateFilledRectanglePath(new PdfLayoutRectangle(96f, 570f, 400f, 82f));
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Ordinary body prose establishes the surrounding document rhythm.", 72f, 72f, 440f),
            CreateFixtureLine("NOTE", 108f, 588f, 42f, 11f, "Times-Bold"),
            CreateFixtureLine("*", 108f, 614f, 6f, 8f, "Symbol"),
            CreateFixtureLine("Copyright notice remains a footnote even inside a shaded region.", 122f, 614f, 330f, 8f)
        ],
        [shading]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.Contains(page.Elements, static element => element.Kind == PdfSemanticElementKind.Footnote);
        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.Aside);
        Assert.All(
            page.Elements.Where(static element => element.Kind == PdfSemanticElementKind.Footnote)
                .SelectMany(static element => element.Lines),
            static line => Assert.Empty(line.InlineSemantics));
    }

    [Fact]
    public void Extract_StandaloneHorizontalRuleBetweenFlowRegions_BecomesThematicBreak()
    {
        PdfLayoutColor color = new(0.2f, 0.4f, 0.6f, 1f, "DeviceRGB");
        PdfLayoutPath rule = CreateRulePath(7, 180f, 172f, 432f, 172f, 2.25f, color);
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes the ordinary page measure and rhythm.", 72f, 72f, 420f),
            CreateFixtureLine("A second line completes the introductory flow region.", 72f, 84f, 350f),
            CreateFixtureLine("The first discussion region occupies its own natural-flow block.", 72f, 118f, 410f),
            CreateFixtureLine("Its continuation closes before the visual transition.", 72f, 130f, 330f),
            CreateFixtureLine("A distinct discussion region begins after the visual transition.", 72f, 214f, 410f),
            CreateFixtureLine("The following line confirms ordinary content has resumed.", 72f, 226f, 360f)
        ],
        [rule]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement thematicBreak = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);

        Assert.Equal(7, thematicBreak.ThematicBreak!.SourcePathIndex);
        Assert.Equal(2.25f, thematicBreak.ThematicBreak.Thickness);
        Assert.Equal(color, thematicBreak.ThematicBreak.Color);
        Assert.Equal(PdfSemanticThematicBreakAlignment.Center, thematicBreak.ThematicBreak.Alignment);
        Assert.Equal(252f, thematicBreak.Bounds.Width);
        int breakIndex = Array.IndexOf(page.Elements.ToArray(), thematicBreak);
        Assert.True(breakIndex > 0 && breakIndex < page.Elements.Count - 1);
        Assert.True(page.Elements[breakIndex - 1].Bounds.Bottom < thematicBreak.Bounds.Y);
        Assert.True(thematicBreak.Bounds.Bottom < page.Elements[breakIndex + 1].Bounds.Y);
    }

    [Fact]
    public void Extract_HorizontalRuleBesideConcurrentColumnFlow_RemainsSourceVector()
    {
        List<PdfTextLine> lines = [];
        for (int index = 0; index < 12; index++)
        {
            float leftY = index < 6 ? 72f + index * 12f : 220f + (index - 6) * 12f;
            float rightY = 72f + index * 12f;
            lines.Add(CreateFixtureLine($"Left column flow line {index + 1}", 72f, leftY, 210f));
            lines.Add(CreateFixtureLine($"Right column flow line {index + 1}", 330f, rightY, 210f));
        }

        PdfLayoutPath sourceRule = CreateRulePath(11, 72f, 176f, 290f, 176f);
        PdfLayoutDocument layout = CreateSemanticPassageFixture(lines, [sourceRule]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.Contains(layout.Pages[0].Runs, run =>
            run.Bounds.X >= 330f &&
            run.Bounds.Y > 140f &&
            run.Bounds.Bottom < 220f);
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
        Assert.Contains(sourceRule, layout.Pages[0].Paths);
    }

    [Fact]
    public void Extract_DocumentTitleRule_RemainsOwnedByTitle()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("A Geometric Study of Reliable Documents", 150f, 90f, 312f, 22f, "Times-Bold"),
            CreateFixtureLine("Ada Lovelace and Emmy Noether", 198f, 154f, 216f),
            CreateFixtureLine("Ordinary body prose begins below the title composition.", 72f, 198f, 360f),
            CreateFixtureLine("A second body line establishes normal reading flow.", 72f, 210f, 340f)
        ],
        [CreateRulePath(0, 126f, 132f, 486f, 132f, 1.5f)]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Heading && element.IsDocumentTitle);
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
    }

    [Fact]
    public void Extract_FootnoteSeparator_RemainsOwnedByFootnotes()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes ordinary page content.", 72f, 72f, 330f),
            CreateFixtureLine("A second line establishes the normal line rhythm.", 72f, 84f, 320f),
            CreateFixtureLine("The final body region closes before the source notes.", 72f, 530f, 340f),
            CreateFixtureLine("*", 72f, 620f, 5f, 8f, "Symbol"),
            CreateFixtureLine("A source note remains below its short separator.", 86f, 620f, 290f, 8f)
        ],
        [CreateRulePath(0, 72f, 596f, 216f, 596f)]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.Contains(page.Elements, static element => element.Kind == PdfSemanticElementKind.Footnote);
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
    }

    [Fact]
    public void Extract_FormBoxRule_RemainsOwnedByFormControl()
    {
        PdfLayoutRectangle controlBounds = new(180f, 166f, 252f, 24f);
        PdfLayoutFormControl control = new(
            0,
            "field",
            "Field",
            PdfLayoutFormControlKind.Text,
            controlBounds);
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes ordinary page content.", 72f, 120f, 350f),
            CreateFixtureLine("The first flow region closes above the control.", 72f, 132f, 320f),
            CreateFixtureLine("A later flow region resumes below the control.", 72f, 224f, 330f),
            CreateFixtureLine("Its continuation confirms the ordinary reading order.", 72f, 236f, 350f)
        ],
        [CreateRulePath(0, controlBounds.X, controlBounds.Y, controlBounds.Right, controlBounds.Y)],
        formControls: [control]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
    }

    [Fact]
    public void Extract_FigureRule_RemainsOwnedByFigureRegion()
    {
        PdfLayoutImage image = new(
            0,
            "figure",
            PdfLayoutImageKind.XObject,
            new PdfLayoutRectangle(180f, 178f, 252f, 80f),
            new PdfLayoutTransform(252f, 0f, 0f, 80f, 180f, 178f),
            252,
            80,
            8,
            "DeviceRGB",
            false,
            null);
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes ordinary page content.", 72f, 120f, 350f),
            CreateFixtureLine("The first flow region closes above the figure.", 72f, 132f, 320f),
            CreateFixtureLine("A later flow region resumes below the figure.", 72f, 290f, 330f),
            CreateFixtureLine("Its continuation confirms the ordinary reading order.", 72f, 302f, 350f)
        ],
        [CreateRulePath(0, 180f, 166f, 432f, 166f)],
        images: [image]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
    }

    [Fact]
    public void Extract_PairedDecorativeRules_RemainVectorDecoration()
    {
        PdfLayoutDocument layout = CreateSemanticPassageFixture(
        [
            CreateFixtureLine("Opening prose establishes ordinary page content.", 72f, 120f, 350f),
            CreateFixtureLine("The first flow region closes above the decoration.", 72f, 132f, 320f),
            CreateFixtureLine("A later flow region resumes below the decoration.", 72f, 216f, 330f),
            CreateFixtureLine("Its continuation confirms the ordinary reading order.", 72f, 228f, 350f)
        ],
        [
            CreateRulePath(0, 180f, 164f, 432f, 164f),
            CreateRulePath(1, 180f, 172f, 432f, 172f)
        ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
    }

    [Fact]
    public void Extract_BodySizeBoldStandaloneLine_WithSectionGapBecomesHeadingButInlineLeadInDoesNot()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateSemanticBoundaryFixture(includeBullets: false)).Pages);

        PdfSemanticElement heading = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.Text == "Standalone Policy Label");
        Assert.Equal(2, heading.HeadingLevel);

        PdfSemanticElement inlineLeadIn = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Important:", StringComparison.Ordinal));
        Assert.Contains("remains ordinary prose", inlineLeadIn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.Text.StartsWith("Important:", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.Text.StartsWith("All comments", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_BulletList_PreservesWrappedLinesAndInlineFormatting()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateSemanticBoundaryFixture(includeBullets: true)).Pages);

        PdfSemanticElement element = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.List);
        PdfSemanticList list = Assert.IsType<PdfSemanticList>(element.SemanticList);
        Assert.Equal(PdfSemanticListKind.Unordered, list.Kind);
        Assert.Equal(PdfSemanticListMarkerKind.Bullet, list.MarkerKind);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.Items[0].Lines.Count);
        Assert.Equal(2, list.Items[1].Lines.Count);
        Assert.Contains("first wrapped item continues", list.Items[0].Text, StringComparison.Ordinal);
        Assert.Contains(list.Items[0].Lines.SelectMany(static line => line.Runs), run =>
            run.FontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) &&
            run.Text.Contains("Federal perspective", StringComparison.Ordinal));
        Assert.Contains(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text == "Ordinary prose resumes after the list.");
    }

    [Fact]
    public void Extract_BulletList_KeepsItemsWhenInferredSpaceAndBodyWidthShiftTheAnchor()
    {
        const float continuationX = 80.8f;
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateSplitBulletFixtureLine("In the first item the initial glyph is narrow.", 72f, 120f, 2.4f),
            CreateFixtureLine("Its continuation uses the shared hanging indent.", continuationX, 132f, 260f),
            CreateSplitBulletFixtureLine("The second item starts with a wider glyph and runs to the margin.", 72f, 148f, 5.4f),
            CreateFixtureLine("Its first continuation remains aligned with the source body.", continuationX, 160f, 360f),
            CreateFixtureLine("short final continuation.", continuationX, 172f, 112f),
            CreateSplitBulletFixtureLine("Similarly, the third marker follows a slightly larger item gap.", 72f, 188f, 4.8f),
            CreateFixtureLine("The third item reaches an inline expression.", continuationX, 200f, 240f),
            CreateStyledFixtureLine(
                continuationX,
                212f,
                ("Masking to ", "Times-Roman"),
                ("−∞", "CMSY10"),
                (" keeps the continuation in the item.", "Times-Roman")))).Pages);

        PdfSemanticList list = Assert.IsType<PdfSemanticList>(
            Assert.Single(page.Elements, static element => element.Kind == PdfSemanticElementKind.List).SemanticList);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal([2, 3, 3], list.Items.Select(static item => item.Lines.Count));
        Assert.Contains("short final continuation", list.Items[1].Text, StringComparison.Ordinal);
        Assert.Contains("−∞ keeps the continuation", list.Items[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_SymbolFontBulletRowsWithMixedBodyRuns_BecomeOneUnorderedList()
    {
        (string Name, string Country)[] members =
        [
            ("Alpha Studio", "Germany"),
            ("Beta Press", "China"),
            ("Color Experts", "Latin America"),
            ("Delta Imaging", "International"),
            ("Epsilon Systems", "Europe"),
            ("Future Academy", "India"),
            ("Government Printing Institute", "India"),
            ("University Press", "Malaysia"),
            ("Visual Arts Faculty", "Slovenia"),
            ("Workflow Center", "Belgium")
        ];
        PdfTextLine[] sourceLines = members
            .Select((member, index) => CreateSymbolBulletMemberFixtureLine(
                member.Name,
                member.Country,
                252f + index * 15.5f))
            .ToArray();

        PdfSemanticPage page = Assert.Single(
            PdfSemanticExtractor.Extract(CreateListFixture(sourceLines)).Pages);
        PdfSemanticElement element = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.List);
        PdfSemanticList list = Assert.IsType<PdfSemanticList>(element.SemanticList);

        Assert.Equal(PdfSemanticListKind.Unordered, list.Kind);
        Assert.Equal(PdfSemanticListMarkerKind.Bullet, list.MarkerKind);
        Assert.Equal(10, list.Items.Count);
        Assert.Equal(
            members.Select(static member => $"{member.Name} ({member.Country})"),
            list.Items.Select(static item => item.Text));
        Assert.All(list.Items, static item =>
        {
            Assert.Equal("•", item.Marker);
            Assert.Equal(2, item.MarkerLength);
            Assert.Equal(5, Assert.Single(item.Lines).Runs.Count);
            Assert.DoesNotContain("•", item.Text, StringComparison.Ordinal);
        });
        Assert.Equal(sourceLines.Length, element.Lines.Count);
        for (int index = 0; index < sourceLines.Length; index++)
        {
            Assert.Equal(sourceLines[index].Runs, element.Lines[index].Runs);
        }
    }

    [Fact]
    public void Extract_DecimalOrderedList_RecordsSourceStartAndNumberingGap()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateStyledFixtureLine(72f, 120f, ("3. ", "Times-Roman"), ("Third step", "Times-Roman")),
            CreateStyledFixtureLine(72f, 136f, ("4. ", "Times-Roman"), ("Fourth step", "Times-Roman")),
            CreateStyledFixtureLine(72f, 152f, ("6. ", "Times-Roman"), ("Sixth step", "Times-Roman")))).Pages);

        PdfSemanticList list = Assert.IsType<PdfSemanticList>(
            Assert.Single(page.Elements, static element => element.Kind == PdfSemanticElementKind.List).SemanticList);
        Assert.Equal(PdfSemanticListKind.Ordered, list.Kind);
        Assert.Equal(PdfSemanticListMarkerKind.Decimal, list.MarkerKind);
        Assert.Equal(3, list.Start);
        Assert.Null(list.Items[0].Value);
        Assert.Null(list.Items[1].Value);
        Assert.Equal(6, list.Items[2].Value);
        Assert.Equal(["Third step", "Fourth step", "Sixth step"], list.Items.Select(static item => item.Text));
    }

    [Fact]
    public void Extract_HyphenList_RequiresRepeatedAlignedHangingMarkers()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateStyledFixtureLine(72f, 120f, ("- ", "Times-Roman"), ("First item", "Times-Roman")),
            CreateStyledFixtureLine(72f, 136f, ("- ", "Times-Roman"), ("Second item", "Times-Roman")),
            CreateStyledFixtureLine(72f, 152f, ("- ", "Times-Roman"), ("Third item", "Times-Roman")))).Pages);

        PdfSemanticList list = Assert.IsType<PdfSemanticList>(
            Assert.Single(page.Elements, static element => element.Kind == PdfSemanticElementKind.List).SemanticList);
        Assert.Equal(PdfSemanticListKind.Unordered, list.Kind);
        Assert.Equal(PdfSemanticListMarkerKind.Hyphen, list.MarkerKind);
    }

    [Fact]
    public void Extract_NestedList_UsesStableIndentationLevels()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateStyledFixtureLine(72f, 120f, ("1. ", "Times-Roman"), ("First parent", "Times-Roman")),
            CreateStyledFixtureLine(96f, 136f, ("a. ", "Times-Roman"), ("First child", "Times-Roman")),
            CreateStyledFixtureLine(96f, 152f, ("b. ", "Times-Roman"), ("Second child", "Times-Roman")),
            CreateStyledFixtureLine(72f, 168f, ("2. ", "Times-Roman"), ("Second parent", "Times-Roman")))).Pages);

        PdfSemanticList root = Assert.IsType<PdfSemanticList>(
            Assert.Single(page.Elements, static element => element.Kind == PdfSemanticElementKind.List).SemanticList);
        Assert.Equal(2, root.Items.Count);
        PdfSemanticList nested = Assert.Single(root.Items[0].NestedLists);
        Assert.Equal(PdfSemanticListMarkerKind.LowerAlpha, nested.MarkerKind);
        Assert.Equal(["First child", "Second child"], nested.Items.Select(static item => item.Text));
        Assert.Empty(root.Items[1].NestedLists);
    }

    [Fact]
    public void Extract_RepeatedInlineTerms_CreateStructuredDefinitionListWithWrappedDefinitions()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateInlineDefinitionListFixture()).Pages);

        PdfSemanticElement element = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList);
        PdfSemanticDefinitionList list = Assert.IsType<PdfSemanticDefinitionList>(element.DefinitionList);
        Assert.Equal(new[] { "API", "CUI", "MFA", "SIEM" }, list.Entries
            .SelectMany(static entry => entry.Terms)
            .Select(static term => term.Text)
            .ToArray());
        Assert.Contains("continues on its wrapped source line", list.Entries[0].Definition.Text, StringComparison.Ordinal);
        Assert.Equal(2, list.Entries[0].Definition.Lines.Count);
        Assert.NotNull(list.TermColumnWidth);
        Assert.All(list.Entries, static entry => Assert.Single(entry.Terms));
    }

    [Fact]
    public void Extract_RepeatedTwoColumnPairs_PreserveTermColumnGeometry()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTwoColumnDefinitionListFixture()).Pages);

        PdfSemanticDefinitionList list = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;
        Assert.Equal(4, list.Entries.Count);
        Assert.Equal("Controlled Unclassified Information", list.Entries[1].Definition.Text);
        Assert.NotNull(list.TermColumnWidth);
        Assert.InRange(list.TermColumnWidth!.Value, 55f, 65f);
        Assert.InRange(list.ColumnGap, 70f, 90f);
    }

    [Fact]
    public void Extract_TwoColumnAliasWithContinuingDefinition_AllowsMultipleTermsForOneDefinition()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTwoColumnAliasDefinitionListFixture()).Pages);

        PdfSemanticDefinitionList list = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;
        Assert.Equal(4, list.Entries.Count);
        Assert.Equal(new[] { "API", "application interface" }, list.Entries[0].Terms
            .Select(static term => term.Text)
            .ToArray());
        Assert.Equal(
            "Application programming interfaces provide access to reusable software operations.",
            list.Entries[0].Definition.Text);
        Assert.All(list.Entries.Skip(1), static entry => Assert.Single(entry.Terms));
    }

    [Fact]
    public void Extract_NistStyleStackedGlossary_ExcludesIntroAndStopsAtNextAppendix()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateStackedGlossaryFixture()).Pages);

        PdfSemanticDefinitionList list = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;
        Assert.Equal(new[] { "agency", "assessment", "audit log", "common secure configuration", "sanitization" }, list.Entries
            .Select(static entry => Assert.Single(entry.Terms).Text)
            .ToArray());
        Assert.EndsWith("[55]", list.Entries[3].Definition.Text, StringComparison.Ordinal);
        Assert.Null(list.TermColumnWidth);
        Assert.DoesNotContain(list.Entries.SelectMany(static entry => entry.Terms), static term =>
            term.Text.Contains("unless otherwise noted", StringComparison.Ordinal));
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("unless otherwise noted.", StringComparison.Ordinal));
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.Text == "Appendix C. Tailoring Criteria");
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("continues onto the next page.", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CrossPageGlossary_MarksDefinitionContinuationAndKeepsPageArtifactSeparate()
    {
        PdfSemanticDocument semantic = PdfSemanticExtractor.Extract(CreateCrossPageDefinitionListFixture());
        PdfSemanticDefinitionList firstPageList = Assert.Single(semantic.Pages[0].Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;
        PdfSemanticDefinitionList secondPageList = Assert.Single(semantic.Pages[1].Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;

        Assert.True(firstPageList.ContinuesOnNextPage);
        Assert.True(secondPageList.ContinuesPreviousList);
        Assert.True(firstPageList.Entries[^1].ContinuesOnNextPage);
        PdfSemanticDefinitionListEntry continuation = secondPageList.Entries[0];
        Assert.True(continuation.ContinuesPreviousDefinition);
        Assert.Empty(continuation.Terms);
        Assert.Equal("operational requirements and implementation guidance.", continuation.Definition.Text);
        Assert.Contains(semantic.Pages[1].Elements, static element =>
            element.Kind == PdfSemanticElementKind.Header && element.Text == "May 2024");
    }

    [Fact]
    public void Extract_CrossPageGlossary_KeepsCompletedEntriesInOneLogicalList()
    {
        PdfSemanticDocument semantic = PdfSemanticExtractor.Extract(
            CreateCrossPageDefinitionListFixture(definitionContinues: false));
        PdfSemanticDefinitionList firstPageList = Assert.Single(semantic.Pages[0].Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;
        PdfSemanticDefinitionList secondPageList = Assert.Single(semantic.Pages[1].Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList).DefinitionList!;

        Assert.True(firstPageList.ContinuesOnNextPage);
        Assert.True(secondPageList.ContinuesPreviousList);
        Assert.False(firstPageList.Entries[^1].ContinuesOnNextPage);
        Assert.All(secondPageList.Entries, static entry => Assert.False(entry.ContinuesPreviousDefinition));
    }

    [Fact]
    public void Extract_NumericRowsAndOrdinaryBoldLeadIns_DoNotBecomeDefinitionLists()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateDefinitionListNegativeFixture()).Pages);

        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList);
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Important:", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_DefinitionLikeLabelsOnFormPage_DoNotBecomeDefinitionList()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            AddFormControl(CreateInlineDefinitionListFixture())).Pages);

        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.DefinitionList);
    }

    [Fact]
    public void Extract_TableOfContents_CreatesNestedNavigationAndResolvedDestinations()
    {
        PdfLayoutDocument layout = CreateDocumentIndexFixture(
            "Table of Contents",
            [
                CreateFixtureLine("1. Introduction ........................ 1", 72f, 104f, 468f, 11f),
                CreateFixtureLine("1.1. Scope .............................. 2", 96f, 120f, 444f, 11f),
                CreateFixtureLine("Appendix A. Controls .................. A-1", 72f, 136f, 468f, 11f),
                CreateFixtureLine("A.1. Exceptions ....................... A-2", 96f, 152f, 444f, 11f)
            ],
            [
                CreateDocumentIndexLink(0, 100f, 2),
                CreateDocumentIndexLink(1, 116f, 3),
                CreateDocumentIndexLink(2, 132f, 8),
                CreateDocumentIndexLink(3, 148f, 9)
            ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticElement element = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Navigation);
        PdfSemanticDocumentIndex documentIndex = Assert.IsType<PdfSemanticDocumentIndex>(element.DocumentIndex);
        Assert.Equal(PdfSemanticDocumentIndexKind.TableOfContents, documentIndex.Kind);
        Assert.Equal("Table of Contents", documentIndex.Heading);
        Assert.Equal(2, documentIndex.Items.Count);
        Assert.Equal("1. Introduction", documentIndex.Items[0].Label);
        Assert.Equal("1", documentIndex.Items[0].PageLabel);
        Assert.Equal(2, documentIndex.Items[0].Link?.DestinationPageNumber);
        PdfSemanticDocumentIndexItem scope = Assert.Single(documentIndex.Items[0].Children);
        Assert.Equal("1.1. Scope", scope.Label);
        Assert.Equal("2", scope.PageLabel);
        Assert.Equal(3, scope.Link?.DestinationPageNumber);
        Assert.Equal("Appendix A. Controls", documentIndex.Items[1].Label);
        PdfSemanticDocumentIndexItem appendixEntry = Assert.Single(documentIndex.Items[1].Children);
        Assert.Equal("A.1. Exceptions", appendixEntry.Label);
        Assert.Equal("A-2", appendixEntry.PageLabel);
        Assert.DoesNotContain("...", element.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.List);
    }

    [Theory]
    [InlineData("List of Figures", PdfSemanticDocumentIndexKind.ListOfFigures, "Figure")]
    [InlineData("List of Tables", PdfSemanticDocumentIndexKind.ListOfTables, "Table")]
    public void Extract_FigureAndTableIndexes_UseFlexibleGapAndSharedNavigationPattern(
        string heading,
        PdfSemanticDocumentIndexKind expectedKind,
        string entryKind)
    {
        PdfLayoutDocument layout = CreateDocumentIndexFixture(
            heading,
            [
                CreateDocumentIndexGapFixtureLine($"{entryKind} 1. System overview", "4", 72f, 104f),
                CreateDocumentIndexGapFixtureLine($"{entryKind} 2. Data flow", "9", 72f, 120f)
            ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);
        PdfSemanticDocumentIndex documentIndex = Assert.IsType<PdfSemanticDocumentIndex>(
            Assert.Single(page.Elements, static element =>
                element.Kind == PdfSemanticElementKind.Navigation).DocumentIndex);
        Assert.Equal(expectedKind, documentIndex.Kind);
        Assert.Equal(2, documentIndex.Items.Count);
        Assert.Equal($"{entryKind} 1. System overview", documentIndex.Items[0].Label);
        Assert.Equal(["4", "9"], documentIndex.Items.Select(static item => item.PageLabel));
    }

    [Fact]
    public void Extract_TableOfContentsHeading_DoesNotPromoteArbitraryLinkList()
    {
        PdfLayoutDocument layout = CreateDocumentIndexFixture(
            "Table of Contents",
            [
                CreateFixtureLine("Project documentation", 72f, 104f, 220f, 11f),
                CreateFixtureLine("API examples", 72f, 120f, 160f, 11f),
                CreateFixtureLine("Release notes", 72f, 136f, 150f, 11f)
            ],
            [
                new PdfLayoutLink(
                    0,
                    new PdfLayoutRectangle(68f, 100f, 228f, 14f),
                    PdfLayoutLinkKind.Uri,
                    "https://example.com/docs",
                    null,
                    null,
                    []),
                new PdfLayoutLink(
                    1,
                    new PdfLayoutRectangle(68f, 116f, 168f, 14f),
                    PdfLayoutLinkKind.Uri,
                    "https://example.com/examples",
                    null,
                    null,
                    [])
            ]);

        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(layout).Pages);

        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.Navigation);
    }

    [Fact]
    public void Extract_AlphabeticAndRomanLists_UsesMarkerSequenceEvidence()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateStyledFixtureLine(72f, 120f, ("(a) ", "Times-Roman"), ("Alpha one", "Times-Roman")),
            CreateStyledFixtureLine(72f, 136f, ("(b) ", "Times-Roman"), ("Alpha two", "Times-Roman")),
            CreateFixtureLine("Separating ordinary prose.", 72f, 168f, 180f),
            CreateStyledFixtureLine(72f, 200f, ("i. ", "Times-Roman"), ("Roman one", "Times-Roman")),
            CreateStyledFixtureLine(72f, 216f, ("ii. ", "Times-Roman"), ("Roman two", "Times-Roman")),
            CreateStyledFixtureLine(72f, 232f, ("iii. ", "Times-Roman"), ("Roman three", "Times-Roman")))).Pages);

        PdfSemanticList[] lists = page.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.List)
            .Select(static element => element.SemanticList!)
            .ToArray();
        Assert.Equal(2, lists.Length);
        Assert.Equal(PdfSemanticListMarkerKind.LowerAlpha, lists[0].MarkerKind);
        Assert.Equal(PdfSemanticListMarkerKind.LowerRoman, lists[1].MarkerKind);
    }

    [Fact]
    public void Extract_NumberedHeadingsEquationsDatesAndIsolatedNumbers_AreNotLists()
    {
        PdfSemanticDocument document = PdfSemanticExtractor.Extract(CreateListFixture(
            CreateFixtureLine("1. Introduction", 72f, 120f, 120f, 13f, "Times-Bold"),
            CreateFixtureLine("1.1. Purpose", 72f, 136f, 110f, 13f, "Times-Bold"),
            CreateFixtureLine("2. Methods", 72f, 152f, 100f, 13f, "Times-Bold"),
            CreateStyledFixtureLine(170f, 200f, ("(1) ", "CMR10"), ("E = mc2", "CMR10")),
            CreateStyledFixtureLine(170f, 216f, ("(2) ", "CMR10"), ("F = ma", "CMR10")),
            CreateFixtureLine("13. July 2026", 72f, 264f, 120f),
            CreateFixtureLine("14. August 2026", 72f, 280f, 130f),
            CreateFixtureLine("[1] A bracketed scientific citation remains prose.", 72f, 304f, 260f),
            CreateFixtureLine("1. This isolated numbered paragraph is not a list.", 72f, 328f, 280f)));
        PdfSemanticPage page = Assert.Single(document.Pages);

        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.List);
        PdfSemanticElement introduction = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading && element.Text.Contains("Introduction", StringComparison.Ordinal));
        PdfSemanticElement purpose = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading && element.Text.Contains("Purpose", StringComparison.Ordinal));
        Assert.Equal(1, document.SectionTree.FindSection(introduction)?.Level);
        Assert.Equal(2, document.SectionTree.FindSection(purpose)?.Level);

        Assert.All(
            page.Elements.SelectMany(static element => element.Lines)
                .Where(static line => line.Text.Contains('=')),
            static line => Assert.Empty(line.InlineSemantics));
    }

    [Fact]
    public void Extract_ConservativeInlineSemantics_RequireExplicitTextAndApprovedContext()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateFixtureLine("Published: March 14, 2024", 72f, 20f, 190f, 10f),
            CreateFixtureLine("Updated: 03/04/2024", 310f, 20f, 150f, 10f),
            CreateFixtureLine("Issued: 2024-03-14 09:30 UTC", 72f, 34f, 220f, 10f),
            CreateFixtureLine("World Health Organization (WHO) issued guidance.", 72f, 120f, 340f, 12f),
            CreateFixtureLine("NASA issued separate guidance without an expansion.", 72f, 136f, 330f, 12f),
            CreateFixtureLine("2024-04-05 - Public consultation opened.", 72f, 168f, 280f, 12f),
            CreateFixtureLine("Copyright 2026 Example. All rights reserved.", 72f, 744f, 270f, 8f),
            CreateFixtureLine("Ordinary smaller text is not ancillary.", 72f, 700f, 220f, 8f))).Pages);

        PdfSemanticLine[] lines = page.Elements
            .SelectMany(static element => element.Lines)
            .Distinct((IEqualityComparer<PdfSemanticLine>)ReferenceEqualityComparer.Instance)
            .ToArray();
        PdfSemanticLine published = Assert.Single(lines, static line => line.Text.StartsWith("Published:", StringComparison.Ordinal));
        PdfSemanticInline publishedTime = Assert.Single(published.InlineSemantics, static semantic =>
            semantic.Kind == PdfSemanticInlineKind.Time);
        Assert.Equal("2024-03-14", publishedTime.Value);

        PdfSemanticLine ambiguous = Assert.Single(lines, static line => line.Text.StartsWith("Updated:", StringComparison.Ordinal));
        Assert.DoesNotContain(ambiguous.InlineSemantics, static semantic => semantic.Kind == PdfSemanticInlineKind.Time);

        PdfSemanticLine issued = Assert.Single(lines, static line => line.Text.StartsWith("Issued:", StringComparison.Ordinal));
        PdfSemanticInline issuedTime = Assert.Single(issued.InlineSemantics, static semantic =>
            semantic.Kind == PdfSemanticInlineKind.Time);
        Assert.Equal("2024-03-14T09:30Z", issuedTime.Value);

        PdfSemanticLine expanded = Assert.Single(lines, static line => line.Text.StartsWith("World Health", StringComparison.Ordinal));
        PdfSemanticInline abbreviation = Assert.Single(expanded.InlineSemantics, static semantic =>
            semantic.Kind == PdfSemanticInlineKind.Abbreviation);
        Assert.Equal("WHO", expanded.Text.Substring(abbreviation.Start, abbreviation.Length));
        Assert.Equal("World Health Organization", abbreviation.Value);

        PdfSemanticLine unpaired = Assert.Single(lines, static line => line.Text.StartsWith("NASA", StringComparison.Ordinal));
        Assert.DoesNotContain(unpaired.InlineSemantics, static semantic =>
            semantic.Kind == PdfSemanticInlineKind.Abbreviation);

        PdfSemanticLine timeline = Assert.Single(lines, static line => line.Text.StartsWith("2024-04-05", StringComparison.Ordinal));
        PdfSemanticInline timelineTime = Assert.Single(timeline.InlineSemantics, static semantic =>
            semantic.Kind == PdfSemanticInlineKind.Time);
        Assert.Equal("2024-04-05", timelineTime.Value);

        PdfSemanticLine copyright = Assert.Single(lines, static line => line.Text.StartsWith("Copyright", StringComparison.Ordinal));
        Assert.Contains(copyright.InlineSemantics, static semantic => semantic.Kind == PdfSemanticInlineKind.Small);
        PdfSemanticLine ordinarySmall = Assert.Single(lines, static line => line.Text.StartsWith("Ordinary smaller", StringComparison.Ordinal));
        Assert.DoesNotContain(ordinarySmall.InlineSemantics, static semantic => semantic.Kind == PdfSemanticInlineKind.Small);
    }

    [Fact]
    public void Extract_BracketedBibliography_PreservesSourceNumbersDestinationsAndCrossPageItems()
    {
        PdfSemanticDocument document = PdfSemanticExtractor.Extract(CreateBracketedBibliographyFixture());

        PdfSemanticElement[] fragments = document.Pages
            .SelectMany(static page => page.Elements)
            .Where(static element => element.Kind == PdfSemanticElementKind.Bibliography)
            .ToArray();
        Assert.Equal(2, fragments.Length);
        PdfSemanticBibliography bibliography = Assert.IsType<PdfSemanticBibliographyFragment>(
            fragments[0].BibliographyFragment).Bibliography;
        Assert.All(fragments, element => Assert.Same(bibliography, element.BibliographyFragment?.Bibliography));
        Assert.Equal(PdfSemanticBibliographyMarkerKind.BracketedNumber, bibliography.MarkerKind);
        Assert.Equal(3, bibliography.Start);
        Assert.Equal([3, 5], bibliography.Items.Select(static item => item.SourceNumber));
        Assert.Equal("cite.Lovelace2020", bibliography.Items[0].Id);
        Assert.Equal("cite.Noether2022", bibliography.Items[1].Id);
        Assert.Contains("continued on the next source page", bibliography.Items[0].Text, StringComparison.Ordinal);

        PdfSemanticBibliographyItemFragment pageTwoItem = Assert.Single(fragments[0].BibliographyFragment!.Items);
        Assert.True(pageTwoItem.IsFirstPart);
        Assert.False(pageTwoItem.IsLastPart);
        PdfSemanticBibliographyItemFragment[] pageThreeItems = fragments[1].BibliographyFragment!.Items.ToArray();
        Assert.Equal(2, pageThreeItems.Length);
        Assert.False(pageThreeItems[0].IsFirstPart);
        Assert.True(pageThreeItems[0].IsLastPart);
        Assert.True(pageThreeItems[1].IsFirstPart);
        Assert.True(pageThreeItems[1].IsLastPart);
        Assert.DoesNotContain(document.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph && element.Text.StartsWith("[", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_AuthorYearBibliography_UsesAnOrderedSemanticModelWithoutSourceMarkers()
    {
        PdfSemanticDocument document = PdfSemanticExtractor.Extract(CreateAuthorYearBibliographyFixture());

        PdfSemanticBibliography bibliography = Assert.IsType<PdfSemanticBibliographyFragment>(
            Assert.Single(document.Elements, static element =>
                element.Kind == PdfSemanticElementKind.Bibliography).BibliographyFragment).Bibliography;
        Assert.Equal(PdfSemanticBibliographyMarkerKind.AuthorYear, bibliography.MarkerKind);
        Assert.Null(bibliography.Start);
        Assert.Equal(2, bibliography.Items.Count);
        Assert.All(bibliography.Items, static item =>
        {
            Assert.Null(item.SourceNumber);
            Assert.Equal(0, item.MarkerLength);
        });
        Assert.StartsWith("Lovelace, Ada (1843)", bibliography.Items[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("Noether, Emmy (1918)", bibliography.Items[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_NumberedBibliography_ReinterpretsEvidenceBackedDecimalList()
    {
        PdfSemanticDocument document = PdfSemanticExtractor.Extract(CreateNumberedBibliographyFixture());

        PdfSemanticBibliography bibliography = Assert.IsType<PdfSemanticBibliographyFragment>(
            Assert.Single(document.Elements, static element =>
                element.Kind == PdfSemanticElementKind.Bibliography).BibliographyFragment).Bibliography;
        Assert.Equal(PdfSemanticBibliographyMarkerKind.Number, bibliography.MarkerKind);
        Assert.Equal([1, 2], bibliography.Items.Select(static item => item.SourceNumber));
        Assert.Contains("Analytical Engine", bibliography.Items[0].Text, StringComparison.Ordinal);
        Assert.Contains("invariant variation", bibliography.Items[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(document.Elements, static element => element.Kind == PdfSemanticElementKind.List);
    }

    [Fact]
    public void Extract_NumberedBibliographyStartsWithinOneParagraph_UsesAlignedSourceLines()
    {
        using PDDocument source = Loader.LoadPDF(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-unet-page-8.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(source, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = true,
            IncludePaths = true
        });

        PdfSemanticDocument document = PdfSemanticExtractor.Extract(layout);

        PdfSemanticElement bibliographyElement = Assert.Single(document.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Bibliography);
        PdfSemanticBibliographyFragment fragment = Assert.IsType<PdfSemanticBibliographyFragment>(
            bibliographyElement.BibliographyFragment);
        PdfSemanticBibliography bibliography = fragment.Bibliography;
        Assert.Equal(PdfSemanticBibliographyMarkerKind.Number, bibliography.MarkerKind);
        Assert.Equal(
            Enumerable.Range(1, 14).Select(static number => (int?)number).ToArray(),
            bibliography.Items.Select(static item => item.SourceNumber).ToArray());
        Assert.Equal(14, fragment.Items.Count);
        Assert.All(fragment.Items, static item =>
        {
            Assert.True(item.IsFirstPart);
            Assert.True(item.IsLastPart);
        });

        PdfSemanticBibliographyItemFragment firstItem = fragment.Items[0];
        Assert.True(firstItem.Lines.Count >= 3);
        Assert.Contains("electron microscopy images", firstItem.Text, StringComparison.OrdinalIgnoreCase);
        Assert.All(firstItem.Lines.Skip(1), line =>
            Assert.True(line.Bounds.X > firstItem.Lines[0].Bounds.X + 4f));
        float[] markerStarts = fragment.Items
            .Select(static item => item.Lines[0].Bounds.X)
            .ToArray();
        Assert.InRange(markerStarts[..9].Max() - markerStarts[..9].Min(), 0f, 1f);
        Assert.InRange(markerStarts[9..].Max() - markerStarts[9..].Min(), 0f, 1f);
        Assert.InRange(markerStarts[..9].Average() - markerStarts[9..].Average(), 3f, 7f);

        PdfSemanticElement footnote = Assert.Single(document.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("U-net implementation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("U-net implementation", footnote.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bibliography.Items, static item =>
            item.Text.Contains("U-net implementation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_BibliographicNumberedProseOutsideReferenceSection_IsNotBibliography()
    {
        PdfLayoutPage page = CreateBibliographyFixturePage(
            1,
            [
                CreateFixtureLine("Related work", 72f, 72f, 100f, 14f, "Times-Bold"),
                CreateFixtureLine("1. Lovelace, A. Notes on the Analytical Engine. 1843.", 72f, 108f, 380f),
                CreateFixtureLine("A continuation line uses the same hanging indentation.", 88f, 122f, 330f),
                CreateFixtureLine("2. Noether, E. Invariant variation problems. 1918.", 72f, 136f, 380f),
                CreateFixtureLine("A second continuation remains ordinary numbered prose.", 88f, 150f, 330f)
            ]);

        PdfSemanticDocument document = PdfSemanticExtractor.Extract(new PdfLayoutDocument([page], []));

        Assert.DoesNotContain(document.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Bibliography);
    }

    [Fact]
    public void Extract_NumberedInstructions_AreNotClassifiedAsBibliographyEntries()
    {
        PdfSemanticDocument document = PdfSemanticExtractor.Extract(CreateNumberedReferenceInstructionsFixture());

        Assert.DoesNotContain(document.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Bibliography);
        Assert.Contains(document.Elements, static element => element.Kind == PdfSemanticElementKind.List);
    }

    [Fact]
    public void Extract_ProminentTopLineRemainsPageHeadingInsteadOfRunningHeader()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(CreateListFixture(
            CreateFixtureLine("Mission Objectives", 36f, 20f, 180f, 20f, "HelveticaNeueLTStd-Bd"),
            CreateFixtureLine("Opening body text establishes the ordinary font size.", 72f, 80f, 300f),
            CreateFixtureLine("A second body line establishes the normal text rhythm.", 72f, 94f, 300f),
            CreateFixtureLine("A third body line completes the representative page.", 72f, 108f, 300f))).Pages);

        PdfSemanticElement heading = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading && element.Text == "Mission Objectives");
        Assert.Equal(1, heading.HeadingLevel);
        Assert.False(heading.IsDocumentTitle);
        Assert.DoesNotContain(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Header && element.Text.Contains("Mission Objectives", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CompactRuledTableInPageColumn_SplitsSameBaselineOppositeProse()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateCompactRuledTableColumnFixture()).Pages);

        PdfSemanticElement table = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Table);
        Assert.Equal(4, table.TableRows.Count);
        Assert.All(table.TableRows, row => Assert.Equal(2, row.Cells.Count));
        Assert.Equal(
            [
                ["Field", "Value"],
                ["1. Account", "42"],
                ["2. Status", "Open1"],
                ["3. Region", "West"]
            ],
            table.TableRows.Select(row => row.Cells.Select(static cell => cell.Text).ToArray()).ToArray());
        Assert.InRange(table.Bounds.X, 35.9f, 36.1f);
        Assert.InRange(table.Bounds.Right, 295.9f, 296.1f);
        Assert.True(table.Bounds.Right < 306f, "The table must remain inside the left page column.");
        Assert.DoesNotContain("Opposite prose", table.Text, StringComparison.Ordinal);
        Assert.Contains(page.Elements, element =>
            element.Kind != PdfSemanticElementKind.Table &&
            element.Text.Contains("Opposite prose aligned with Field", StringComparison.Ordinal));

        Assert.All(table.TableRows[0].Cells, cell => Assert.True(cell.BorderTop));
        Assert.All(table.TableRows.Skip(1).SelectMany(static row => row.Cells), cell => Assert.False(cell.BorderTop));
        Assert.All(table.TableRows, row =>
        {
            Assert.True(row.Cells[0].BorderLeft);
            Assert.True(row.Cells[0].BorderRight);
            Assert.True(row.Cells[1].BorderLeft);
            Assert.True(row.Cells[1].BorderRight);
        });
        Assert.All(table.TableRows[0].Cells, cell => Assert.True(cell.BorderBottom));
        Assert.All(table.TableRows.Skip(1).Take(2).SelectMany(static row => row.Cells), cell => Assert.False(cell.BorderBottom));
        Assert.All(table.TableRows[^1].Cells, cell => Assert.True(cell.BorderBottom));
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak);
        Assert.All(table.Lines, static line => Assert.Empty(line.InlineSemantics));
    }

    [Fact]
    public void Extract_NumberedTableCaptionAboveTable_AttachesWithoutDuplicatingText()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTableCaptionFixture(TableCaptionFixturePlacement.Above)).Pages);

        PdfSemanticElement table = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table);
        Assert.True(table.TableCaption != null, TableCaptionDiagnostic(page));
        PdfSemanticTableCaption caption = table.TableCaption!;
        Assert.Equal("12", caption.Number);
        Assert.Equal("Table 12: Comparative outcomes across cohorts.", caption.Text);
        Assert.Equal(PdfSemanticTableCaptionPosition.Above, caption.Position);
        Assert.DoesNotContain("Table 12", table.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind != PdfSemanticElementKind.Table &&
            element.Text.StartsWith("Table 12", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NumberedTableCaptionBelowTable_AttachesWithSourcePosition()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTableCaptionFixture(TableCaptionFixturePlacement.Below)).Pages);

        PdfSemanticElement table = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table);
        PdfSemanticTableCaption caption = Assert.IsType<PdfSemanticTableCaption>(table.TableCaption);
        Assert.Equal("12", caption.Number);
        Assert.Equal(PdfSemanticTableCaptionPosition.Below, caption.Position);
        Assert.DoesNotContain(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Table 12", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_WrappedTableCaption_PreservesAllSourceLines()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTableCaptionFixture(TableCaptionFixturePlacement.Above, wrapped: true)).Pages);

        PdfSemanticElement table = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table);
        Assert.True(table.TableCaption != null, TableCaptionDiagnostic(page));
        PdfSemanticTableCaption caption = table.TableCaption!;
        Assert.Equal(2, caption.Lines.Count);
        Assert.Equal(
            "Table 12: Comparative outcomes across all cohorts and evaluation periods with linked baseline details.",
            caption.Text);
    }

    [Fact]
    public void Extract_NearbyTableReferenceAndInterruptedTitle_RemainOrdinaryProse()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTableCaptionFixture(TableCaptionFixturePlacement.Above, includeNegativeProse: true)).Pages);

        PdfSemanticElement table = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table);
        Assert.Null(table.TableCaption);
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("Table 12 shows the measurements", StringComparison.Ordinal));
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.Contains("Intervening discussion remains visible", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_NumberedTableTitleWithInterveningProse_IsNotAttached()
    {
        PdfSemanticPage page = Assert.Single(PdfSemanticExtractor.Extract(
            CreateTableCaptionFixture(
                TableCaptionFixturePlacement.Above,
                includeInterruptedTitle: true)).Pages);

        PdfSemanticElement table = Assert.Single(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Table);
        Assert.Null(table.TableCaption);
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Table 12: Candidate title", StringComparison.Ordinal));
        Assert.Contains(page.Elements, static element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Intervening prose blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ArxivFrontPage_GroupsTitleAuthorsAbstractFootnotesAndFooter()
    {
        PdfSemanticDocument semantic = ExtractArxivSemanticDocument();
        PdfSemanticPage page = semantic.Pages[0];

        PdfSemanticElement[] headers = page.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Header)
            .ToArray();
        Assert.Equal(2, headers.Length);
        PdfSemanticElement arxivHeader = Assert.Single(headers, header =>
            header.Text.Contains("arXiv:1706.03762v7", StringComparison.Ordinal));
        Assert.Contains(arxivHeader.Lines, line => MathF.Abs(line.Direction - 90f) < 0.01f);
        PdfSemanticElement permissionHeader = Assert.Single(headers, header =>
            header.Text.Contains("Provided proper attribution", StringComparison.Ordinal));
        Assert.Equal(3, permissionHeader.Lines.Count);
        Assert.Contains("reproduce the tables and figures in this paper solely for use in journalistic or", permissionHeader.Text, StringComparison.Ordinal);
        Assert.Contains("scholarly works.", permissionHeader.Text, StringComparison.Ordinal);
        Assert.All(permissionHeader.Lines, line => Assert.True(line.Color.Red > line.Color.Blue));

        PdfSemanticElement title = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.HeadingLevel == 1 &&
            element.Text.Contains("Attention", StringComparison.Ordinal));
        Assert.Equal("Attention Is All You Need", title.Text);

        PdfSemanticElement[] authorBlocks = page.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.AuthorBlock)
            .ToArray();
        Assert.Equal(8, authorBlocks.Length);
        Assert.Equal(7, authorBlocks.Count(static author => author.Lines.Count == 3));
        Assert.Contains(authorBlocks, author => author.Text.Contains("Ashish Vaswani", StringComparison.Ordinal) &&
            author.Text.Contains("Google Brain", StringComparison.Ordinal) &&
            author.Text.Contains("avaswani@google.com", StringComparison.Ordinal));
        Assert.Contains(authorBlocks, author => author.Text.Contains("Aidan N. Gomez", StringComparison.Ordinal) &&
            author.Text.Contains("University of Toronto", StringComparison.Ordinal) &&
            author.Text.Contains("aidan@cs.toronto.edu", StringComparison.Ordinal));
        Assert.Contains(authorBlocks, author => author.Text.Contains("Illia Polosukhin", StringComparison.Ordinal) &&
            author.Text.Contains("illia.polosukhin@gmail.com", StringComparison.Ordinal) &&
            author.Lines.Count == 2);
        Assert.All(authorBlocks.SelectMany(static author => author.Lines), static line => Assert.Empty(line.InlineCode));
        Assert.DoesNotContain(page.Elements, static element => element.Kind == PdfSemanticElementKind.CodeBlock);

        PdfSemanticElement abstractHeading = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            element.Text == "Abstract");
        PdfSemanticElement abstractParagraph = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Bounds.Y > abstractHeading.Bounds.Y &&
            element.Text.StartsWith("The dominant sequence transduction models", StringComparison.Ordinal));
        Assert.Contains("large and limited training data.", abstractParagraph.Text, StringComparison.Ordinal);

        PdfSemanticElement[] footnotes = page.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Footnote)
            .ToArray();
        Assert.Equal(3, footnotes.Length);
        Assert.Contains(footnotes, footnote => footnote.Text.Contains("Equal contribution", StringComparison.Ordinal));
        Assert.Contains(footnotes, footnote => footnote.Text.Contains("Work performed while at Google Brain", StringComparison.Ordinal));
        Assert.Contains(footnotes, footnote => footnote.Text.Contains("Work performed while at Google Research", StringComparison.Ordinal));

        PdfSemanticElement footer = Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Footer &&
            element.Text.Contains("31st Conference", StringComparison.Ordinal));
        Assert.Contains("Long Beach, CA, USA.", footer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ArxivSecondPage_GroupsSectionHeadingsAndParagraphs()
    {
        PdfSemanticDocument semantic = ExtractArxivSemanticDocument();
        PdfSemanticPage page = semantic.Pages[1];

        PdfSemanticElement introduction = Heading(page, "1 Introduction");
        PdfSemanticElement background = Heading(page, "2 Background");
        PdfSemanticElement architecture = Heading(page, "3 Model Architecture");

        PdfSemanticElement[] introductionParagraphs = ParagraphsBetween(page, introduction, background);
        Assert.Equal(4, introductionParagraphs.Length);
        Assert.StartsWith("Recurrent neural networks, long short-term memory", introductionParagraphs[0].Text);
        Assert.Contains("The fundamental constraint of sequential computation, however, remains.", introductionParagraphs[1].Text, StringComparison.Ordinal);
        Assert.StartsWith("Attention mechanisms have become an integral part", introductionParagraphs[2].Text);
        Assert.StartsWith("In this work we propose the Transformer", introductionParagraphs[3].Text);

        PdfSemanticElement[] backgroundParagraphs = ParagraphsBetween(page, background, architecture);
        Assert.Equal(4, backgroundParagraphs.Length);
        Assert.StartsWith("The goal of reducing sequential computation", backgroundParagraphs[0].Text);
        Assert.StartsWith("Self-attention, sometimes called intra-attention", backgroundParagraphs[1].Text);
        Assert.StartsWith("End-to-end memory networks", backgroundParagraphs[2].Text);
        Assert.StartsWith("To the best of our knowledge", backgroundParagraphs[3].Text);

        PdfSemanticElement[] architectureParagraphs = page.Elements
            .Where(element => element.Kind == PdfSemanticElementKind.Paragraph &&
                element.Bounds.Y > architecture.Bounds.Y)
            .ToArray();
        Assert.Single(architectureParagraphs);
        Assert.StartsWith("Most competitive neural sequence transduction models", architectureParagraphs[0].Text);
    }

    [Fact]
    public void Extract_ArxivTables_CreatesSemanticTableRowsAndCells()
    {
        PdfSemanticDocument semantic = ExtractArxivSemanticDocument();

        PdfSemanticElement complexityTable = Assert.Single(semantic.Pages[5].Elements, element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Text.Contains("Self-Attention", StringComparison.Ordinal) &&
            element.Text.Contains("Complexity per Layer", StringComparison.Ordinal));
        Assert.Equal(5, complexityTable.TableRows.Count);
        Assert.Equal("Layer Type", complexityTable.TableRows[0].Cells[0].Text);
        Assert.Equal("Sequential Operations", complexityTable.TableRows[0].Cells[2].Text);
        Assert.True(complexityTable.TableRows[0].IsHeader);
        Assert.Contains(complexityTable.TableRows, row =>
            !row.IsHeader &&
            row.Cells[0].Text == "Self-Attention" &&
            row.Cells[1].Text.Contains("O(n2", StringComparison.Ordinal));
        Assert.Contains(complexityTable.TableRows.SelectMany(static row => row.Cells), cell =>
            cell.BorderTop || cell.BorderBottom);
        Assert.DoesNotContain(semantic.Pages[5].Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("Layer Type", StringComparison.Ordinal));

        PdfSemanticElement bleuTable = Assert.Single(semantic.Pages[7].Elements, element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Text.Contains("Transformer (big)", StringComparison.Ordinal));
        Assert.True(bleuTable.TableRows.Count >= 10);
        Assert.True(bleuTable.TableRows[0].IsHeader);
        Assert.True(bleuTable.TableRows[1].IsHeader);
        Assert.Equal("Model", bleuTable.TableRows[0].Cells[0].Text);
        Assert.Equal(2, bleuTable.TableRows[0].Cells[0].RowSpan);
        Assert.Equal("BLEU", bleuTable.TableRows[0].Cells[1].Text);
        Assert.Equal(2, bleuTable.TableRows[0].Cells[1].ColumnSpan);
        Assert.True(bleuTable.TableRows[0].Cells[2].IsPlaceholder);
        Assert.Equal("Training Cost (FLOPs)", bleuTable.TableRows[0].Cells[3].Text);
        Assert.Equal(2, bleuTable.TableRows[0].Cells[3].ColumnSpan);
        Assert.True(bleuTable.TableRows[0].Cells[4].IsPlaceholder);
        Assert.True(bleuTable.TableRows[1].Cells[0].IsPlaceholder);
        Assert.Equal("EN-DE", bleuTable.TableRows[1].Cells[1].Text);
        Assert.Equal("EN-FR", bleuTable.TableRows[1].Cells[2].Text);
        Assert.Equal("EN-DE", bleuTable.TableRows[1].Cells[3].Text);
        Assert.Equal("EN-FR", bleuTable.TableRows[1].Cells[4].Text);
        Assert.All(bleuTable.TableRows[0].Cells.Where(static cell => !cell.IsPlaceholder), cell => Assert.True(cell.BorderTop));
        Assert.False(bleuTable.TableRows[0].Cells[0].BorderBottom);
        Assert.True(bleuTable.TableRows[0].Cells[1].BorderBottom);
        Assert.True(bleuTable.TableRows[0].Cells[3].BorderBottom);
        Assert.Contains(bleuTable.TableRows, row =>
            !row.IsHeader &&
            row.Cells[0].Text == "ByteNet [18]" &&
            row.Cells.Any(cell => cell.Text == "23.75"));
        Assert.Contains(bleuTable.TableRows, row =>
            !row.IsHeader &&
            row.Cells[0].Text == "Transformer (big)" &&
            row.Cells.Any(cell => cell.Text == "28.4"));
        PdfSemanticTableRow transformerBig = Assert.Single(bleuTable.TableRows, row =>
            !row.IsHeader &&
            row.Cells[0].Text == "Transformer (big)");
        Assert.All(transformerBig.Cells, cell => Assert.True(cell.BorderBottom));
        Assert.DoesNotContain(semantic.Pages[7].Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Text.StartsWith("BLEU Training Cost", StringComparison.Ordinal));

        PdfSemanticElement variationTable = Assert.Single(semantic.Pages[8].Elements, element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Text.Contains("Pdrop", StringComparison.Ordinal) &&
            element.Text.Contains("base", StringComparison.Ordinal) &&
            element.Text.Contains("big", StringComparison.Ordinal));
        Assert.Equal(13, variationTable.TableRows.Max(static row => row.Cells.Count));
        Assert.True(variationTable.TableRows[0].IsHeader);
        Assert.True(variationTable.TableRows[1].IsHeader);
        Assert.Equal("", variationTable.TableRows[0].Cells[0].Text);
        Assert.Equal("N", variationTable.TableRows[1].Cells[1].Text);
        Assert.Contains(variationTable.TableRows[0].Cells, cell => cell.Text == "train");
        Assert.Contains(variationTable.TableRows[1].Cells, cell => cell.Text == "steps");
        Assert.Contains(variationTable.TableRows[1].Cells, cell => cell.Text.Contains("×106", StringComparison.Ordinal));
        Assert.Contains(variationTable.TableRows, row =>
            !row.IsHeader &&
            row.Cells[0].Text == "big" &&
            row.Cells[12].Text == "213");
        Assert.Contains(variationTable.TableRows.SelectMany(static row => row.Cells), cell => cell.BorderRight);

        PdfSemanticTableRow groupA = Assert.Single(variationTable.TableRows, row => row.Cells[0].Text == "(A)");
        Assert.Equal(4, groupA.Cells[0].RowSpan);
        Assert.Equal("1", groupA.Cells[4].Text);
        Assert.Equal("512", groupA.Cells[5].Text);
        Assert.Equal("5.29", groupA.Cells[10].Text);
        Assert.Contains(variationTable.TableRows, row =>
            row.Cells[0].IsPlaceholder &&
            row.Cells[4].Text == "32" &&
            row.Cells[5].Text == "16" &&
            row.Cells[6].Text == "16");

        PdfSemanticTableRow groupB = Assert.Single(variationTable.TableRows, row => row.Cells[0].Text == "(B)");
        Assert.Equal(2, groupB.Cells[0].RowSpan);
        Assert.Equal("16", groupB.Cells[5].Text);
        Assert.Equal("58", groupB.Cells[12].Text);

        PdfSemanticTableRow groupC = Assert.Single(variationTable.TableRows, row => row.Cells[0].Text == "(C)");
        Assert.Equal(7, groupC.Cells[0].RowSpan);
        Assert.Equal("2", groupC.Cells[1].Text);
        Assert.Equal("6.11", groupC.Cells[10].Text);

        PdfSemanticTableRow groupD = Assert.Single(variationTable.TableRows, row => row.Cells[0].Text == "(D)");
        Assert.Equal(4, groupD.Cells[0].RowSpan);
        Assert.Equal("0.0", groupD.Cells[7].Text);
        Assert.Equal("5.77", groupD.Cells[10].Text);

        PdfSemanticTableRow groupE = Assert.Single(variationTable.TableRows, row => row.Cells[0].Text == "(E)");
        Assert.Equal("positional embedding instead of sinusoids", groupE.Cells[1].Text);
        Assert.Equal(9, groupE.Cells[1].ColumnSpan);
        Assert.All(groupE.Cells.Skip(2).Take(8), cell => Assert.True(cell.IsPlaceholder));
        Assert.DoesNotContain(variationTable.TableRows, row =>
            row.Cells[0].Text is "(A)" or "(B)" or "(D)" &&
            row.Cells.Skip(1).All(static cell => string.IsNullOrWhiteSpace(cell.Text)));

        PdfSemanticElement parserTable = Assert.Single(semantic.Pages[9].Elements, element =>
            element.Kind == PdfSemanticElementKind.Table &&
            element.Text.Contains("Parser", StringComparison.Ordinal) &&
            element.Text.Contains("WSJ 23 F1", StringComparison.Ordinal));
        Assert.Equal(3, parserTable.TableRows.Max(static row => row.Cells.Count));
        Assert.True(parserTable.TableRows[0].IsHeader);
        Assert.Equal("Parser", parserTable.TableRows[0].Cells[0].Text);
        Assert.Equal("Training", parserTable.TableRows[0].Cells[1].Text);
        Assert.Equal("WSJ 23 F1", parserTable.TableRows[0].Cells[2].Text);
        Assert.Contains(parserTable.TableRows, row =>
            !row.IsHeader &&
            row.Cells[0].Text.StartsWith("Vinyals & Kaiser", StringComparison.Ordinal) &&
            row.Cells[2].Text == "88.3");
        Assert.Contains(parserTable.TableRows.SelectMany(static row => row.Cells), cell => cell.BorderRight);

        PdfSemanticElement[] captionedTables = semantic.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Table && element.TableCaption != null)
            .Where(static element => element.TableCaption!.Number is "1" or "2" or "3" or "4")
            .ToArray();
        Assert.Equal(4, captionedTables.Length);
        Assert.All(captionedTables, static table =>
            Assert.DoesNotContain(table.TableCaption!.Text, table.Text, StringComparison.Ordinal));
        Assert.DoesNotContain(semantic.Elements, element =>
            element.Kind == PdfSemanticElementKind.Paragraph &&
            captionedTables.Any(table => string.Equals(
                element.Text,
                table.TableCaption!.Text,
                StringComparison.Ordinal)));
    }

    private static PdfSemanticElement Heading(PdfSemanticPage page, string text)
    {
        return Assert.Single(page.Elements, element =>
            element.Kind == PdfSemanticElementKind.Heading &&
            string.Equals(element.Text, text, StringComparison.Ordinal));
    }

    private static PdfTextGlyph[] CreatePositionedGlyphs(
        IReadOnlyList<string> words,
        float characterGap,
        float wordGap)
    {
        const float glyphWidth = 4f;
        const float fontSize = 10f;
        PdfLayoutColor color = new(0, 0, 0, 1, "DeviceGray");
        List<PdfTextGlyph> glyphs = [];
        float x = 72f;
        for (int wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            foreach (char character in words[wordIndex])
            {
                PdfLayoutRectangle bounds = new(x, 100f, glyphWidth, 7f);
                glyphs.Add(new PdfTextGlyph(character.ToString(), "Times-Roman", fontSize, 0f, bounds, color));
                x += glyphWidth + characterGap;
            }

            x += wordIndex + 1 < words.Count ? wordGap - characterGap : 0f;
        }

        return glyphs.ToArray();
    }

    private static PdfTextGlyph[] CreateVisualGlyphs(string visualText)
    {
        PdfLayoutColor color = new(0, 0, 0, 1, "DeviceGray");
        List<PdfTextGlyph> glyphs = [];
        float x = 72f;
        foreach (Rune rune in visualText.EnumerateRunes())
        {
            float width = Rune.IsWhiteSpace(rune) ? 3f : 6f;
            PdfLayoutRectangle bounds = new(x, 100f, width, 8f);
            glyphs.Add(new PdfTextGlyph(rune.ToString(), "NotoNaskhArabic", 10f, 0f, bounds, color));
            x += width;
        }

        return glyphs.ToArray();
    }

    private static PdfLayoutDocument CreateInlineDefinitionListFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Opening context establishes ordinary body text for semantic inference.", 72f, 72f, 410f),
            CreateFixtureLine("A second context line completes the introductory paragraph.", 72f, 84f, 360f),
            CreateStyledFixtureLine(72f, 120f, ("API", "Times-Bold"), (" Application programming interface", "Times-Roman")),
            CreateFixtureLine("continues on its wrapped source line.", 100f, 132f, 220f),
            CreateStyledFixtureLine(72f, 150f, ("CUI", "Times-Bold"), (" Controlled unclassified information", "Times-Roman")),
            CreateStyledFixtureLine(72f, 168f, ("MFA", "Times-Bold"), (" Multi-factor authentication", "Times-Roman")),
            CreateStyledFixtureLine(72f, 186f, ("SIEM", "Times-Bold"), (" Security information and event management", "Times-Roman")),
            CreateFixtureLine("Ordinary prose resumes after the repeated pairs.", 72f, 220f, 320f)
        ];
        return CreateDefinitionFixtureDocument([lines]);
    }

    private static PdfLayoutDocument CreateTwoColumnDefinitionListFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Acronym reference", 72f, 60f, 150f, 14f, "Times-Bold"),
            CreateFixtureLine("The following abbreviations are used throughout this document.", 72f, 84f, 390f),
            CreateColumnDefinitionLine("API", "Application Programming Interface", 120f),
            CreateColumnDefinitionLine("CUI", "Controlled Unclassified Information", 140f),
            CreateColumnDefinitionLine("MFA", "Multi-Factor Authentication", 160f),
            CreateColumnDefinitionLine("SIEM", "Security Information and Event Management", 180f)
        ];
        return CreateDefinitionFixtureDocument([lines]);
    }

    private static PdfLayoutDocument CreateTwoColumnAliasDefinitionListFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Acronym reference", 72f, 60f, 150f, 14f, "Times-Bold"),
            CreateFixtureLine("The following terms are used throughout this document.", 72f, 84f, 360f),
            CreateColumnDefinitionLine("API", "Application programming interfaces provide", 120f),
            CreateColumnDefinitionLine("application interface", "access to reusable software operations.", 140f),
            CreateColumnDefinitionLine("CUI", "Controlled Unclassified Information", 164f),
            CreateColumnDefinitionLine("MFA", "Multi-Factor Authentication", 184f),
            CreateColumnDefinitionLine("SIEM", "Security Information and Event Management", 204f)
        ];
        return CreateDefinitionFixtureDocument([lines]);
    }

    private static PdfLayoutDocument CreateStackedGlossaryFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Appendix B. Glossary", 72f, 60f, 170f, 14f, "Times-Bold"),
            CreateFixtureLine("Appendix B provides definitions for the terminology used in this publication. The definitions are", 72f, 90f, 460f, 12f),
            CreateFixtureLine("consistent with the definitions in the referenced standards", 72f, 102f, 390f, 12f),
            CreateFixtureLine("unless otherwise noted.", 72f, 114f, 150f, 12f),
            CreateFixtureLine("agency", 72f, 150f, 48f, 9f, "Times-Bold"),
            CreateFixtureLine("An executive department or other government organization.", 72f, 162f, 360f, 9f),
            CreateFixtureLine("assessment", 72f, 186f, 70f, 9f, "Times-Bold"),
            CreateFixtureLine("See security control assessment.", 72f, 198f, 220f, 9f),
            CreateFixtureLine("audit log", 72f, 222f, 60f, 9f, "Times-Bold"),
            CreateFixtureLine("A chronological record of system activities and accessed resources.", 72f, 234f, 410f, 9f),
            CreateFixtureLine("common secure configuration", 72f, 258f, 180f, 9f, "Times-Bold"),
            CreateFixtureLine("Recognized benchmarks that stipulate secure configuration settings", 72f, 270f, 390f, 9f),
            CreateFixtureLine("for specific information technology platforms and products.", 72f, 282f, 340f, 9f),
            CreateFixtureLine("[55]", 72f, 294f, 30f, 9f),
            CreateFixtureLine("sanitization", 72f, 318f, 70f, 9f, "Times-Bold"),
            CreateFixtureLine("Actions taken to render data on media unrecoverable.", 72f, 330f, 330f, 9f),
            CreateFixtureLine("Appendix C. Tailoring Criteria", 72f, 378f, 230f, 14f, "Times-Bold"),
            CreateFixtureLine("This appendix describes ordinary tailoring guidance that continues onto the next", 72f, 398f, 430f, 12f),
            CreateFixtureLine("page.", 72f, 410f, 40f, 12f)
        ];
        return CreateDefinitionFixtureDocument([lines]);
    }

    private static PdfLayoutDocument CreateCrossPageDefinitionListFixture(bool definitionContinues = true)
    {
        List<PdfTextLine> firstPage =
        [
            CreateFixtureLine("agency", 72f, 520f, 48f, 10f, "Times-Bold"),
            CreateFixtureLine("An executive department or government organization.", 72f, 532f, 330f),
            CreateFixtureLine("assessment", 72f, 556f, 70f, 10f, "Times-Bold"),
            CreateFixtureLine("See security control assessment.", 72f, 568f, 220f),
            CreateFixtureLine("audit log", 72f, 592f, 60f, 10f, "Times-Bold"),
            CreateFixtureLine("A chronological record of system activity.", 72f, 604f, 280f),
            CreateFixtureLine("common secure configuration", 72f, 628f, 180f, 10f, "Times-Bold"),
            CreateFixtureLine(
                definitionContinues ? "Recognized benchmarks for systems that meet" : "Recognized benchmarks for systems. [79]",
                72f,
                640f,
                300f)
        ];
        List<PdfTextLine> secondPage =
        [
            CreateFixtureLine("May 2024", 72f, 51f, 70f),
            .. (definitionContinues
                ? new[] { CreateFixtureLine("operational requirements and implementation guidance.", 72f, 75f, 340f) }
                : Array.Empty<PdfTextLine>()),
            CreateFixtureLine("confidentiality", 72f, 110f, 90f, 10f, "Times-Bold"),
            CreateFixtureLine("Preserving authorized restrictions on information access.", 72f, 122f, 360f),
            CreateFixtureLine("configuration management", 72f, 146f, 160f, 10f, "Times-Bold"),
            CreateFixtureLine("Activities that maintain the integrity of system configurations.", 72f, 158f, 390f),
            CreateFixtureLine("controlled area", 72f, 182f, 100f, 10f, "Times-Bold"),
            CreateFixtureLine("An area with sufficient physical and procedural protections.", 72f, 194f, 380f),
            CreateFixtureLine("external network", 72f, 218f, 100f, 10f, "Times-Bold"),
            CreateFixtureLine("A network not controlled by the organization.", 72f, 230f, 300f)
        ];
        return CreateDefinitionFixtureDocument([firstPage, secondPage]);
    }

    private static PdfLayoutDocument CreateDefinitionListNegativeFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Opening body text establishes ordinary semantic flow.", 72f, 60f, 330f),
            CreateStyledFixtureLine(72f, 100f, ("Important: ", "Times-Bold"), ("this is ordinary lead-in prose.", "Times-Roman")),
            CreateStyledFixtureLine(72f, 118f, ("Note: ", "Times-Bold"), ("this remains another ordinary paragraph.", "Times-Roman")),
            CreateStyledFixtureLine(72f, 136f, ("Warning: ", "Times-Bold"), ("this is not a glossary entry.", "Times-Roman")),
            CreateStyledFixtureLine(72f, 154f, ("Remember: ", "Times-Bold"), ("bold lead-ins do not define terms.", "Times-Roman")),
            CreateColumnDefinitionLine("100", "records processed", 210f),
            CreateColumnDefinitionLine("200", "records processed", 228f),
            CreateColumnDefinitionLine("300", "records processed", 246f),
            CreateColumnDefinitionLine("400", "records processed", 264f),
            CreateFixtureLine("Table 2. Security Control Tailoring Criteria", 72f, 320f, 260f, 10f, "Times-Bold"),
            CreateColumnDefinitionLine("NCO", "The control is not directly related to the requirement.", 350f),
            CreateColumnDefinitionLine("FED", "The control is primarily a government responsibility.", 368f),
            CreateColumnDefinitionLine("ORC", "The outcome is addressed by another requirement.", 386f),
            CreateColumnDefinitionLine("N/A", "The control is not applicable.", 404f),
            CreateColumnDefinitionLine("CUI", "The control directly protects the information.", 422f)
        ];
        return CreateDefinitionFixtureDocument([lines]);
    }

    private static PdfTextLine CreateColumnDefinitionLine(string term, string definition, float y)
    {
        PdfTextLine termLine = CreateFixtureLine(term, 72f, y, 60f, 10f, "Times-Bold");
        PdfTextLine definitionLine = CreateFixtureLine(definition, 210f, y, 300f);
        PdfTextRun[] runs = termLine.Runs.Concat(definitionLine.Runs).ToArray();
        return new PdfTextLine(
            term + " " + definition,
            new PdfLayoutRectangle(72f, y, 438f, 7.5f),
            runs);
    }

    private static PdfLayoutDocument CreateDefinitionFixtureDocument(
        IReadOnlyList<IReadOnlyList<PdfTextLine>> pageLines)
    {
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage[] pages = pageLines
            .Select((lines, index) =>
            {
                PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
                PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
                return new PdfLayoutPage(
                    index + 1,
                    pageBounds,
                    pageBounds,
                    pageBounds.Width,
                    pageBounds.Height,
                    0,
                    glyphs,
                    runs,
                    lines,
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    []);
            })
            .ToArray();
        return new PdfLayoutDocument(pages, []);
    }

    private static PdfLayoutDocument AddFormControl(PdfLayoutDocument layout)
    {
        PdfLayoutPage source = Assert.Single(layout.Pages);
        PdfLayoutFormControl control = new(
            0,
            "definition-like-field",
            "Definition-like field",
            PdfLayoutFormControlKind.Text,
            new PdfLayoutRectangle(70f, 110f, 180f, 18f));
        PdfLayoutPage page = new(
            source.PageNumber,
            source.MediaBox,
            source.CropBox,
            source.Width,
            source.Height,
            source.Rotation,
            source.Glyphs,
            source.Runs,
            source.Lines,
            source.Blocks,
            source.Images,
            source.Paths,
            source.Shadings,
            source.VectorGroups,
            source.Links,
            source.Diagnostics,
            source.PaintOperations,
            [control]);
        return new PdfLayoutDocument([page], layout.Diagnostics);
    }

    private static PdfLayoutDocument CreateScientificFrontMatterFixture(
        bool inlineAbstract,
        bool includeNarrowQuotation)
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Reusable Scientific Front Matter", 106f, 70f, 400f, 18f, "Times-Bold"),
            CreateFixtureLine("Ada Lovelace and Emmy Noether", 181f, 112f, 250f),
            CreateFixtureLine("†", 80f, 138f, 8f, 8f, "Symbol"),
            CreateFixtureLine("Department of Applied Mathematics, Example University", 92f, 138f, 428f),
            CreateFixtureLine("‡", 110f, 152f, 8f, 8f, "Symbol"),
            CreateFixtureLine("Center for Computational Science, Example City", 122f, 152f, 368f),
            CreateFixtureLine("§", 142f, 166f, 8f, 8f, "Symbol"),
            CreateFixtureLine("Institute for Scientific Computing", 154f, 166f, 316f),
            CreateFixtureLine("September 2008", 256f, 196f, 100f)
        ];

        if (inlineAbstract)
        {
            lines.Add(CreateFixtureLine("WWW home page: https://example.edu/research", 126f, 220f, 360f, 9f));
            lines.Add(CreateFixtureLine("Abstract. This study introduces a reusable semantic grouping strategy for papers.", 108f, 260f, 396f));
        }
        else
        {
            lines.Add(CreateFixtureLine("Abstract", 270f, 235f, 72f, 10f, "Times-Bold"));
            lines.Add(CreateFixtureLine("This study introduces a reusable semantic grouping strategy for papers.", 108f, 260f, 396f));
        }

        lines.Add(CreateFixtureLine("It preserves source rows while keeping the abstract body in normal document flow.", 108f, 273f, 396f));
        lines.Add(CreateFixtureLine("The strategy uses layout evidence instead of document-specific titles or names.", 108f, 286f, 396f));
        if (includeNarrowQuotation)
        {
            lines.Add(CreateFixtureLine("A narrow quotation remains", 206f, 330f, 200f));
            lines.Add(CreateFixtureLine("intentionally narrow.", 218f, 343f, 176f));
            lines.Add(CreateFixtureLine("1 Introduction", 72f, 380f, 110f, 13f, "Times-Bold"));
        }

        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateBracketedBibliographyFixture()
    {
        PdfTextLine citation = CreateStyledFixtureLine(
            72f,
            104f,
            ("Prior work ", "Times-Roman"),
            ("[3]", "Times-Roman"),
            (" establishes the baseline.", "Times-Roman"));
        PdfTextRun citationMarker = citation.Runs[1];
        PdfLayoutLink citationLink = new(
            0,
            citationMarker.Bounds,
            PdfLayoutLinkKind.Destination,
            null,
            "cite.Lovelace2020",
            null,
            []);
        PdfTextLine secondCitation = CreateStyledFixtureLine(
            72f,
            120f,
            ("A later comparison ", "Times-Roman"),
            ("[5]", "Times-Roman"),
            (" confirms it.", "Times-Roman"));
        PdfLayoutLink secondCitationLink = new(
            1,
            secondCitation.Runs[1].Bounds,
            PdfLayoutLinkKind.Destination,
            null,
            "cite.Noether2022",
            null,
            []);
        PdfLayoutPage firstPage = CreateBibliographyFixturePage(
            1,
            [
                CreateFixtureLine("Opening scientific prose establishes the body font.", 72f, 72f, 340f),
                CreateFixtureLine("A second line establishes ordinary paragraph rhythm.", 72f, 88f, 340f),
                citation,
                secondCitation
            ],
            [citationLink, secondCitationLink]);
        PdfLayoutPage secondPage = CreateBibliographyFixturePage(
            2,
            [
                CreateFixtureLine("References", 72f, 72f, 100f, 14f, "Times-Bold"),
                CreateStyledFixtureLine(
                    72f,
                    108f,
                    ("[3] Lovelace, A. (2020). ", "Times-Roman"),
                    ("Analytical Engines", "Times-Italic"),
                    (". https://doi.org/10.1000/first", "Times-Roman")),
                CreateFixtureLine("The discussion continues to the bottom of this source page", 88f, 122f, 360f)
            ]);
        PdfLayoutPage thirdPage = CreateBibliographyFixturePage(
            3,
            [
                CreateFixtureLine("and is continued on the next source page.", 88f, 72f, 300f),
                CreateFixtureLine("[5] Noether, E. (2022). Symmetry in modern physics.", 72f, 126f, 380f)
            ]);
        return new PdfLayoutDocument([firstPage, secondPage, thirdPage], []);
    }

    private static PdfLayoutDocument CreateAuthorYearBibliographyFixture()
    {
        PdfLayoutPage page = CreateBibliographyFixturePage(
            1,
            [
                CreateFixtureLine("Opening body prose establishes the ordinary font.", 72f, 52f, 340f),
                CreateFixtureLine("References", 72f, 84f, 100f, 14f, "Times-Bold"),
                CreateFixtureLine("Lovelace, Ada (1843). Notes on the Analytical Engine.", 72f, 120f, 380f),
                CreateFixtureLine("Noether, Emmy (1918). Invariant variation problems.", 72f, 156f, 380f)
            ]);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateNumberedReferenceInstructionsFixture()
    {
        PdfLayoutPage page = CreateBibliographyFixturePage(
            1,
            [
                CreateFixtureLine("Opening body prose establishes the ordinary font.", 72f, 52f, 340f),
                CreateFixtureLine("References", 72f, 84f, 100f, 14f, "Times-Bold"),
                CreateFixtureLine("1. Open the document.", 72f, 120f, 180f),
                CreateFixtureLine("2. Select Save.", 72f, 136f, 160f),
                CreateFixtureLine("3. Close the application.", 72f, 152f, 210f)
            ]);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateNumberedBibliographyFixture()
    {
        PdfLayoutPage page = CreateBibliographyFixturePage(
            1,
            [
                CreateFixtureLine("Opening body prose establishes the ordinary font.", 72f, 52f, 340f),
                CreateFixtureLine("References", 72f, 84f, 100f, 14f, "Times-Bold"),
                CreateFixtureLine("1. Lovelace, A. Notes on the Analytical Engine. 1843.", 72f, 120f, 380f),
                CreateFixtureLine("2. Noether, E. Invariant variation problems. 1918.", 72f, 136f, 380f)
            ]);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutPage CreateBibliographyFixturePage(
        int pageNumber,
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfLayoutLink>? links = null)
    {
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        return new PdfLayoutPage(
            pageNumber,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            links ?? [],
            []);
    }

    private static PdfLayoutDocument CreateSemanticBoundaryFixture(bool includeBullets)
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Opening body text establishes the ordinary font and line geometry.", 72f, 72f, 410f),
            CreateFixtureLine("A second body line completes the paragraph before the section boundary.", 72f, 84f, 410f),
            CreateFixtureLine("Standalone Policy Label", 72f, 108f, 126f, 10f, "Times-Bold"),
            CreateFixtureLine("The section body begins on the next ordinary line.", 72f, 120f, 330f),
            CreateStyledFixtureLine(
                72f,
                156f,
                ("Important: ", "Times-Bold"),
                ("this shared visual line remains ordinary prose.", "Times-Roman")),
            CreateFixtureLine("All comments remain subject to release.", 72f, 180f, 230f, 10f, "Times-Bold")
        ];

        if (includeBullets)
        {
            lines.Add(CreateFixtureLine("The following perspectives apply:", 72f, 216f, 240f));
            lines.Add(CreateStyledFixtureLine(
                72f,
                232f,
                ("• ", "Symbol"),
                ("Federal perspective", "Times-Italic"),
                (": the first wrapped item", "Times-Roman")));
            lines.Add(CreateFixtureLine("continues on an indented visual line.", 90f, 244f, 250f));
            lines.Add(CreateStyledFixtureLine(
                72f,
                260f,
                ("• ", "Symbol"),
                ("Nonfederal perspective", "Times-Italic"),
                (": the second wrapped item", "Times-Roman")));
            lines.Add(CreateFixtureLine("also preserves its continuation text.", 90f, 272f, 240f));
            lines.Add(CreateFixtureLine("Ordinary prose resumes after the list.", 72f, 302f, 240f));
        }

        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateListFixture(params PdfTextLine[] listLines)
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Opening prose establishes ordinary body text.", 72f, 72f, 260f),
            CreateFixtureLine("A second line establishes the normal vertical rhythm.", 72f, 84f, 280f),
            .. listLines
        ];
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateCompactRuledTableColumnFixture()
    {
        List<PdfTextLine> lines =
        [
            CreateCompositeFixtureLine(
                120f,
                ("Dense left-column prose establishes the page layout.", 36f, 250f, "Times-Roman"),
                ("Dense right-column prose establishes its independent flow.", 320f, 250f, "Times-Roman")),
            CreateCompositeFixtureLine(
                134f,
                ("The left column continues immediately before the table.", 36f, 250f, "Times-Roman"),
                ("The right column continues beside the source region.", 320f, 250f, "Times-Roman")),
            CreateCompositeFixtureLine(
                224f,
                ("Field", 44f, 90f, "Times-Bold"),
                ("Value", 174f, 90f, "Times-Bold"),
                ("Opposite prose aligned with Field remains ordinary text.", 320f, 250f, "Times-Roman")),
            CreateCompositeFixtureLine(
                252f,
                ("1. Account", 44f, 90f, "Times-Roman"),
                ("42", 174f, 90f, "Times-Roman"),
                ("Opposite prose aligned with Account also survives.", 320f, 250f, "Times-Roman")),
            CreateFixtureLine("1", 224f, 269f, 4f, 6f),
            CreateCompositeFixtureLine(
                272f,
                ("2. Status", 44f, 90f, "Times-Roman"),
                ("Open", 174f, 50f, "Times-Roman"),
                ("Opposite prose aligned with Status completes the paragraph.", 320f, 250f, "Times-Roman")),
            CreateCompositeFixtureLine(
                296f,
                ("3. Region", 44f, 90f, "Times-Roman"),
                ("West", 174f, 90f, "Times-Roman"),
                ("Opposite prose aligned with Region stays outside the table.", 320f, 250f, "Times-Roman")),
            CreateCompositeFixtureLine(
                328f,
                ("Left-column prose resumes below the compact table.", 36f, 250f, "Times-Roman"),
                ("Right-column prose continues below the aligned rows.", 320f, 250f, "Times-Roman"))
        ];

        float[] horizontalRules = [216f, 240f, 312f];
        float[] verticalRules = [36f, 166f, 296f];
        List<PdfLayoutPath> paths = [];
        foreach (float y in horizontalRules)
        {
            paths.Add(CreateRulePath(paths.Count, 36f, y, 296f, y));
        }

        foreach (float x in verticalRules)
        {
            paths.Add(CreateRulePath(paths.Count, x, 216f, x, 312f));
        }

        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            paths,
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutDocument CreateTableCaptionFixture(
        TableCaptionFixturePlacement placement,
        bool wrapped = false,
        bool includeNegativeProse = false,
        bool includeInterruptedTitle = false)
    {
        List<PdfTextLine> lines =
        [
            CreateFixtureLine("Opening prose establishes the ordinary body font.", 72f, 40f, 310f),
            CreateFixtureLine("A second line establishes the normal source rhythm.", 72f, 54f, 320f)
        ];

        if (placement == TableCaptionFixturePlacement.Above)
        {
            if (includeInterruptedTitle)
            {
                lines.Add(CreateFixtureLine(
                    "Table 12: Candidate title should remain visible.",
                    120f,
                    82f,
                    320f));
                lines.Add(CreateFixtureLine(
                    "Intervening prose blocks association with the data grid.",
                    72f,
                    108f,
                    360f));
            }
            else if (includeNegativeProse)
            {
                lines.Add(CreateFixtureLine(
                    "Table 12 shows the measurements used in the discussion.",
                    104f,
                    82f,
                    330f));
                lines.Add(CreateFixtureLine(
                    "Intervening discussion remains visible before the data grid.",
                    72f,
                    104f,
                    360f));
            }
            else if (wrapped)
            {
                lines.Add(CreateFixtureLine(
                    "Table 12: Comparative outcomes across all cohorts and",
                    112f,
                    82f,
                    340f));
                lines.Add(CreateFixtureLine(
                    "evaluation periods with linked baseline details.",
                    132f,
                    94f,
                    300f));
            }
            else
            {
                lines.Add(CreateFixtureLine(
                    "Table 12: Comparative outcomes across cohorts.",
                    132f,
                    92f,
                    300f));
            }
        }

        float tableTop = includeNegativeProse || includeInterruptedTitle ? 140f : 124f;
        lines.Add(CreateCompositeFixtureLine(
            tableTop,
            ("Cohort", 96f, 100f, "Times-Roman"),
            ("Baseline", 260f, 74f, "Times-Roman"),
            ("Outcome", 382f, 78f, "Times-Roman")));
        lines.Add(CreateCompositeFixtureLine(
            tableTop + 14f,
            ("Alpha", 96f, 100f, "Times-Roman"),
            ("10", 260f, 74f, "Times-Roman"),
            ("12", 382f, 78f, "Times-Roman")));
        lines.Add(CreateCompositeFixtureLine(
            tableTop + 28f,
            ("Beta", 96f, 100f, "Times-Roman"),
            ("14", 260f, 74f, "Times-Roman"),
            ("18", 382f, 78f, "Times-Roman")));
        lines.Add(CreateCompositeFixtureLine(
            tableTop + 42f,
            ("Gamma", 96f, 100f, "Times-Roman"),
            ("21", 260f, 74f, "Times-Roman"),
            ("25", 382f, 78f, "Times-Roman")));

        if (placement == TableCaptionFixturePlacement.Below)
        {
            lines.Add(CreateFixtureLine(
                "Table 12: Comparative outcomes across cohorts.",
                132f,
                tableTop + 68f,
                300f));
        }

        lines.Add(CreateFixtureLine(
            "Closing prose remains outside the detected table.",
            72f,
            tableTop + 96f,
            300f));
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static string TableCaptionDiagnostic(PdfSemanticPage page)
    {
        return string.Join(" | ", page.Elements.Select(static element =>
            $"{element.Kind}:{element.Bounds.X:0.#},{element.Bounds.Y:0.#}," +
            $"{element.Bounds.Width:0.#},{element.Bounds.Height:0.#}:" +
            element.Text[..Math.Min(80, element.Text.Length)]));
    }

    private enum TableCaptionFixturePlacement
    {
        Above,
        Below
    }

    private static PdfLayoutDocument CreateDocumentIndexFixture(
        string heading,
        IReadOnlyList<PdfTextLine> entries,
        IReadOnlyList<PdfLayoutLink>? links = null)
    {
        PdfTextLine[] lines =
        [
            CreateFixtureLine(heading, 72f, 72f, 220f, 14f, "Times-Bold"),
            .. entries
        ];
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            links ?? [],
            []);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfTextLine CreateCompositeFixtureLine(
        float y,
        params (string Text, float X, float Width, string FontName)[] segments)
    {
        PdfTextRun[] runs = segments
            .Select(segment => CreateFixtureLine(
                segment.Text,
                segment.X,
                y,
                segment.Width,
                fontName: segment.FontName).Runs.Single())
            .ToArray();
        return new PdfTextLine(
            string.Join(" ", segments.Select(static segment => segment.Text)),
            new PdfLayoutRectangle(
                runs.Min(static run => run.Bounds.X),
                runs.Min(static run => run.Bounds.Y),
                runs.Max(static run => run.Bounds.Right) - runs.Min(static run => run.Bounds.X),
                runs.Max(static run => run.Bounds.Bottom) - runs.Min(static run => run.Bounds.Y)),
            runs);
    }

    private static PdfLayoutPath CreateRulePath(
        int index,
        float startX,
        float startY,
        float endX,
        float endY,
        float strokeWidth = 0.5f,
        PdfLayoutColor? strokeColor = null)
    {
        PdfLayoutColor color = strokeColor ?? new PdfLayoutColor(0f, 0f, 0f, 1f, "DeviceGray");
        PdfLayoutStrokeStyle stroke = new(color, strokeWidth, 0, 0, 10f, [], 0f);
        PdfLayoutRectangle bounds = new(
            MathF.Min(startX, endX),
            MathF.Min(startY, endY),
            MathF.Abs(endX - startX),
            MathF.Abs(endY - startY));
        return new PdfLayoutPath(
            index,
            [
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, startX, startY, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, endX, endY, 0f, 0f, 0f, 0f)
            ],
            bounds,
            null,
            stroke,
            null);
    }

    private static PdfTextLine CreateDocumentIndexGapFixtureLine(
        string label,
        string pageLabel,
        float x,
        float y)
    {
        const float fontSize = 11f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfLayoutRectangle labelBounds = new(x, y, 180f, 8.25f);
        PdfTextGlyph labelGlyph = new(label, "Times-Roman", fontSize, 0f, labelBounds, color);
        PdfTextRun labelRun = new(label, "Times-Roman", fontSize, 0f, labelBounds, color, [labelGlyph]);
        PdfLayoutRectangle pageBounds = new(528f, y, 12f, 8.25f);
        PdfTextGlyph pageGlyph = new(pageLabel, "Times-Roman", fontSize, 0f, pageBounds, color);
        PdfTextRun pageRun = new(pageLabel, "Times-Roman", fontSize, 0f, pageBounds, color, [pageGlyph]);
        return new PdfTextLine(
            $"{label} {pageLabel}",
            new PdfLayoutRectangle(x, y, 540f - x, 8.25f),
            [labelRun, pageRun]);
    }

    private static PdfLayoutLink CreateDocumentIndexLink(int index, float y, int destinationPageNumber)
    {
        return new PdfLayoutLink(
            index,
            new PdfLayoutRectangle(68f, y, 476f, 14f),
            PdfLayoutLinkKind.Destination,
            null,
            $"page:{destinationPageNumber}",
            destinationPageNumber,
            []);
    }

    private static PdfTextLine CreateMonospacedFixtureLine(
        string text,
        float x,
        float y,
        float characterPitch = 6f,
        string fontName = "Courier")
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph[] glyphs = text
            .Select((character, index) => (character, index))
            .Where(static item => !char.IsWhiteSpace(item.character))
            .Select(item => new PdfTextGlyph(
                item.character.ToString(),
                fontName,
                fontSize,
                0f,
                new PdfLayoutRectangle(x + item.index * characterPitch, y, characterPitch, fontSize * 0.75f),
                color))
            .ToArray();
        PdfLayoutRectangle bounds = new(x, y, text.Length * characterPitch, fontSize * 0.75f);
        PdfTextRun run = new(text, fontName, fontSize, 0f, bounds, color, glyphs);
        return new PdfTextLine(text, bounds, [run]);
    }

    private static PdfTextLine CreateInlineCodeFixtureLine(
        string prefix,
        string code,
        string suffix,
        float x,
        float y)
    {
        PdfTextRun prefixRun = CreatePositionedFixtureRun(prefix, "Times-Roman", x, y, 5f);
        PdfTextRun codeRun = CreatePositionedFixtureRun(code, "Courier", prefixRun.Bounds.Right, y, 6f);
        PdfTextRun suffixRun = CreatePositionedFixtureRun(suffix, "Times-Roman", codeRun.Bounds.Right, y, 5f);
        PdfTextRun[] runs = [prefixRun, codeRun, suffixRun];
        return new PdfTextLine(
            prefix + code + suffix,
            new PdfLayoutRectangle(x, y, suffixRun.Bounds.Right - x, suffixRun.Bounds.Height),
            runs);
    }

    private static PdfTextRun CreatePositionedFixtureRun(
        string text,
        string fontName,
        float x,
        float y,
        float characterPitch)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph[] glyphs = text
            .Select((character, index) => new PdfTextGlyph(
                character.ToString(),
                fontName,
                fontSize,
                0f,
                new PdfLayoutRectangle(x + index * characterPitch, y, characterPitch, fontSize * 0.75f),
                color))
            .ToArray();
        PdfLayoutRectangle bounds = new(x, y, text.Length * characterPitch, fontSize * 0.75f);
        return new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, glyphs);
    }

    private static PdfLayoutFormControl CreateFormControl(
        int index,
        string name,
        PdfLayoutRectangle valueBounds)
    {
        return new PdfLayoutFormControl(
            index,
            name,
            name,
            PdfLayoutFormControlKind.Text,
            new PdfLayoutRectangle(
                valueBounds.X - 4f,
                valueBounds.Y - 3f,
                valueBounds.Width + 8f,
                valueBounds.Height + 6f));
    }

    private static PdfLayoutDocument CreateCodeFixtureDocument(
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfLayoutFormControl> formControls)
    {
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null,
            formControls);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfTextLine CreateStyledFixtureLine(
        float x,
        float y,
        params (string Text, string FontName)[] segments)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        List<PdfTextRun> runs = [];
        float segmentX = x;
        foreach ((string text, string fontName) in segments)
        {
            float width = MathF.Max(4f, text.Length * 5f);
            PdfLayoutRectangle bounds = new(segmentX, y, width, fontSize * 0.75f);
            PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
            runs.Add(new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, [glyph]));
            segmentX += width;
        }

        return new PdfTextLine(
            string.Concat(segments.Select(static segment => segment.Text)),
            new PdfLayoutRectangle(x, y, segmentX - x, fontSize * 0.75f),
            runs);
    }

    private static PdfTextLine CreateStyledFixtureLineWithTrailingNumber(
        float x,
        float y,
        float numberX,
        string number,
        params (string Text, string FontName)[] segments)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        List<PdfTextRun> runs = [];
        float segmentX = x;
        foreach ((string text, string fontName) in segments)
        {
            float width = MathF.Max(4f, text.Length * 5f);
            PdfLayoutRectangle bounds = new(segmentX, y, width, fontSize * 0.75f);
            PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
            runs.Add(new PdfTextRun(text, fontName, fontSize, 0f, bounds, color, [glyph]));
            segmentX += width;
        }

        PdfLayoutRectangle numberBounds = new(numberX, y, MathF.Max(4f, number.Length * 5f), fontSize * 0.75f);
        PdfTextGlyph numberGlyph = new(number, "Times-Roman", fontSize, 0f, numberBounds, color);
        runs.Add(new PdfTextRun(number, "Times-Roman", fontSize, 0f, numberBounds, color, [numberGlyph]));
        return new PdfTextLine(
            string.Concat(segments.Select(static segment => segment.Text)) + number,
            new PdfLayoutRectangle(x, y, numberBounds.Right - x, fontSize * 0.75f),
            runs);
    }

    private static PdfTextLine CreateFormulaLikeTableRow(
        string label,
        string value,
        string number,
        float y)
    {
        const float fontSize = 10f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        (string Text, float X, float Width)[] cells =
        [
            (label, 90f, 80f),
            (" = " + value, 240f, 90f),
            (number, 420f, 24f)
        ];
        PdfTextRun[] runs = cells.Select(cell =>
        {
            PdfLayoutRectangle bounds = new(cell.X, y, cell.Width, fontSize * 0.75f);
            PdfTextGlyph glyph = new(cell.Text, "CMR10", fontSize, 0f, bounds, color);
            return new PdfTextRun(cell.Text, "CMR10", fontSize, 0f, bounds, color, [glyph]);
        }).ToArray();
        return new PdfTextLine(
            string.Concat(cells.Select(static cell => cell.Text)),
            new PdfLayoutRectangle(90f, y, 354f, fontSize * 0.75f),
            runs);
    }

    private static PdfTextLine CreateSplitBulletFixtureLine(
        string body,
        float x,
        float y,
        float bodyGlyphWidth)
    {
        const float fontSize = 10f;
        const float markerWidth = 3.5f;
        const float bodyOffset = 8.5f;
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfLayoutRectangle markerBounds = new(x, y, markerWidth, 6f);
        PdfTextGlyph markerGlyph = new("•", "Times-Roman", 9f, 0f, markerBounds, color);
        PdfTextRun markerRun = new("•", "Times-Roman", 9f, 0f, markerBounds, color, [markerGlyph]);
        PdfLayoutRectangle bodyBounds = new(x + bodyOffset, y, body.Length * bodyGlyphWidth, 6f);
        PdfTextGlyph bodyGlyph = new(body, "Times-Roman", fontSize, 0f, bodyBounds, color);
        PdfTextRun bodyRun = new(body, "Times-Roman", fontSize, 0f, bodyBounds, color, [bodyGlyph]);
        return new PdfTextLine(
            "•" + body,
            new PdfLayoutRectangle(x, y, bodyBounds.Right - x, 6f),
            [markerRun, bodyRun]);
    }

    private static PdfTextLine CreateSymbolBulletMemberFixtureLine(
        string memberName,
        string country,
        float y)
    {
        const float fontSize = 10f;
        PdfLayoutColor black = new(0f, 0f, 0f, 1f, "DeviceRGB");
        PdfLayoutColor accent = new(0.51f, 0.01f, 0.29f, 1f, "DeviceRGB");
        List<PdfTextRun> runs = [];

        AddRun("•", "SymbolMT", 114.05f, y + 2.18f, 4.83f, 4.77f, black);
        AddRun(" ", "ArialMT", 118.8f, y + 2.03f, 2.92f, 4.92f, black);
        float memberWidth = memberName.Length * 5f;
        AddRun(memberName, "SourceSansPro-Regular", 132.05f, y + 0.34f, memberWidth, 6.62f, accent);
        string countryText = $" ({country})";
        float countryX = 132.05f + memberWidth;
        float countryWidth = countryText.Length * 4.5f;
        AddRun(countryText, "SourceSansPro-Regular", countryX, y + 0.34f, countryWidth, 6.62f, black);
        AddRun(" ", "SourceSansPro-Bold", countryX + countryWidth, y, 2.1f, 6.95f, black);

        float left = runs.Min(static run => run.Bounds.X);
        float top = runs.Min(static run => run.Bounds.Y);
        float right = runs.Max(static run => run.Bounds.Right);
        float bottom = runs.Max(static run => run.Bounds.Bottom);
        PdfLayoutRectangle bounds = new(left, top, right - left, bottom - top);
        return new PdfTextLine(string.Concat(runs.Select(static run => run.Text)), bounds, runs);

        void AddRun(
            string text,
            string fontName,
            float x,
            float runY,
            float width,
            float height,
            PdfLayoutColor color)
        {
            PdfLayoutRectangle runBounds = new(x, runY, width, height);
            PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, runBounds, color);
            runs.Add(new PdfTextRun(text, fontName, fontSize, 0f, runBounds, color, [glyph]));
        }
    }

    private static PdfTextLine CreateFixtureLine(
        string text,
        float x,
        float y,
        float width,
        float fontSize = 10f,
        string fontName = "Times-Roman")
    {
        PdfLayoutRectangle bounds = new(x, y, width, fontSize * 0.75f);
        PdfLayoutColor color = new(0f, 0f, 0f, 1f, "DeviceGray");
        PdfTextGlyph glyph = new(text, fontName, fontSize, 0f, bounds, color);
        PdfTextRun run = new(text, fontName, fontSize, 0f, bounds, color, [glyph]);
        return new PdfTextLine(text, bounds, [run]);
    }

    private static PdfLayoutDocument CreateSemanticPassageFixture(
        IReadOnlyList<PdfTextLine> lines,
        IReadOnlyList<PdfLayoutPath>? paths = null,
        IReadOnlyList<PdfLayoutImage>? images = null,
        IReadOnlyList<PdfLayoutFormControl>? formControls = null)
    {
        PdfTextRun[] runs = lines.SelectMany(static line => line.Runs).ToArray();
        PdfTextGlyph[] glyphs = runs.SelectMany(static run => run.Glyphs).ToArray();
        PdfLayoutRectangle pageBounds = new(0f, 0f, 612f, 792f);
        PdfLayoutPage page = new(
            1,
            pageBounds,
            pageBounds,
            pageBounds.Width,
            pageBounds.Height,
            0,
            glyphs,
            runs,
            lines,
            [],
            images ?? [],
            paths ?? [],
            [],
            [],
            [],
            [],
            null,
            formControls);
        return new PdfLayoutDocument([page], []);
    }

    private static PdfLayoutPath CreateFilledRectanglePath(PdfLayoutRectangle bounds)
    {
        PdfLayoutColor fill = new(0.92f, 0.94f, 0.96f, 1f, "DeviceRGB");
        return new PdfLayoutPath(0, [], bounds, fill, null, fillRule: 1);
    }

    private static PdfSemanticElement[] ParagraphsBetween(
        PdfSemanticPage page,
        PdfSemanticElement first,
        PdfSemanticElement second)
    {
        return page.Elements
            .Where(element => element.Kind == PdfSemanticElementKind.Paragraph &&
                element.Bounds.Y > first.Bounds.Y &&
                element.Bounds.Y < second.Bounds.Y)
            .ToArray();
    }

    private static PdfSemanticDocument ExtractArxivSemanticDocument()
    {
        using PDDocument document = Loader.LoadPDF(Path.Combine(AppContext.BaseDirectory, "Fixtures", "arxiv-sample.pdf"));
        PdfLayoutDocument layout = PdfLayoutExtractor.Extract(document, new PdfLayoutOptions
        {
            IncludeImages = false,
            IncludeLinks = false,
            IncludePaths = true
        });
        return PdfSemanticExtractor.Extract(layout);
    }
}
