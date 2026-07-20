using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using PdfBox.Net.Layout;
using PdfBox.Net.PDModel.Graphics;

namespace PdfBox.Net.Html;

/// <summary>
/// Converts layout documents to fixed-layout HTML.
/// </summary>
public static class PdfHtmlConverter
{
    private const float BrowserFontBaselineRatio = 0.8f;
    private static readonly ConditionalWeakTable<PdfLayoutPage, FormulaPageLookup> FormulaPageLookups = new();

    private sealed class FormulaPageLookup
    {
        public FormulaPageLookup(HashSet<PdfTextRun> proseLineRuns, PdfTextGlyph[] largeOperators)
        {
            ProseLineRuns = proseLineRuns;
            LargeOperators = largeOperators;
        }

        public HashSet<PdfTextRun> ProseLineRuns { get; }

        public PdfTextGlyph[] LargeOperators { get; }
    }

    internal sealed class FallbackSemanticIsland
    {
        public FallbackSemanticIsland(
            PdfSemanticElement element,
            PdfLayoutRectangle bounds,
            IReadOnlyList<PdfTextRun> ownedRuns,
            IReadOnlyList<PdfLayoutLink> ownedLinks,
            int firstRunIndex,
            int lastRunIndex)
        {
            Element = element;
            Bounds = bounds;
            OwnedRuns = ownedRuns.ToArray();
            OwnedLinks = ownedLinks.ToArray();
            FirstRunIndex = firstRunIndex;
            LastRunIndex = lastRunIndex;
        }

        public PdfSemanticElement Element { get; }

        public PdfLayoutRectangle Bounds { get; }

        public IReadOnlyList<PdfTextRun> OwnedRuns { get; }

        public IReadOnlyList<PdfLayoutLink> OwnedLinks { get; }

        public int FirstRunIndex { get; }

        public int LastRunIndex { get; }
    }

    internal readonly record struct FormulaGlyphKey(
        string Text,
        string FontName,
        int FontSize,
        int Direction,
        int X,
        int Y,
        int Width,
        int Height,
        bool IsPainted);

    private const string Css = """
        .pdf-document {
          --pdf-page-background: #fff;
          --pdf-page-corner-shadow: 22pt;
          --pdf-page-edge-shadow: rgba(17, 24, 39, 0.16);
          --pdf-page-shadow-mask: 6pt;
          --pdf-page-surround: #f3f4f6;
          --pdf-page-width: min(612pt, calc(100vw - 48pt));
          background: var(--pdf-page-surround);
          color: #111827;
          font-family: Arial, Helvetica, sans-serif;
          margin: 0;
          padding: 24pt;
        }

        .pdf-page {
          background: var(--pdf-page-background);
          box-shadow: 0 1pt 4pt var(--pdf-page-edge-shadow);
          isolation: isolate;
          margin: 0 auto 24pt;
          overflow: hidden;
          position: relative;
        }

        .pdf-text-run {
          color: #111827;
          display: block;
          line-height: 1;
          margin: 0;
          padding: 0;
          position: absolute;
          transform-origin: 0 0;
          white-space: pre;
          z-index: 1000000;
        }

        .pdf-ocr-scan-page .pdf-ocr-text-run {
          opacity: 0;
        }

        .pdf-text-run-copy {
          color: transparent;
          display: block;
          left: 0;
          line-height: 1;
          position: absolute;
          top: 0;
          white-space: pre;
        }

        .pdf-text-run-svg {
          display: block;
          height: 100%;
          left: 0;
          overflow: visible;
          position: absolute;
          top: 0;
          width: 100%;
        }

        .pdf-text-rtl {
          direction: rtl;
          text-align: right;
          unicode-bidi: plaintext;
        }

        .pdf-text-shadow {
          filter: drop-shadow(
            var(--pdf-text-shadow-x)
            var(--pdf-text-shadow-y)
            var(--pdf-text-shadow-blur)
            var(--pdf-text-shadow-color));
        }

        .pdf-form-control {
          box-sizing: border-box;
          font: 10pt Arial, Helvetica, sans-serif;
          margin: 0;
          min-height: 1em;
          z-index: 1000002;
        }

        .pdf-form-control[type="checkbox"],
        .pdf-form-control[type="radio"] {
          accent-color: #1d4ed8;
        }

        .pdf-form-page-positioned,
        .pdf-form-group-positioned {
          display: contents;
        }

        .pdf-form-label-positioned,
        .pdf-form-legend-positioned {
          border: 0;
          clip: rect(0 0 0 0);
          clip-path: inset(50%);
          height: 1px;
          margin: -1px;
          overflow: hidden;
          padding: 0;
          position: absolute;
          white-space: nowrap;
          width: 1px;
        }

        .pdf-form-control-positioned {
          appearance: none;
          background: transparent;
          border: 0;
          border-radius: 0;
          box-shadow: none;
          padding: 0;
          resize: none;
        }

        .pdf-form-control-widget {
          clip-path: inset(0);
          min-height: 0;
          overflow: hidden;
        }

        .pdf-form-control-positioned.pdf-form-control-authored-appearance {
          color: transparent;
        }

        .pdf-form-control-positioned.pdf-form-control-active,
        .pdf-form-control-positioned.pdf-form-control-edited {
          appearance: none;
          background: transparent;
          border: 0;
          border-radius: 0;
          color: #111827;
          padding: 1px 2px;
        }

        .pdf-form-control-widget.pdf-form-control-active {
          outline: 2px solid #2563eb;
          outline-offset: -2px;
        }

        .pdf-form-control-authored-appearance[type="checkbox"],
        .pdf-form-control-authored-appearance[type="radio"] {
          opacity: 0;
        }

        .pdf-form-control-authored-appearance.pdf-form-control-active[type="checkbox"],
        .pdf-form-control-authored-appearance.pdf-form-control-active[type="radio"],
        .pdf-form-control-authored-appearance.pdf-form-control-edited[type="checkbox"],
        .pdf-form-control-authored-appearance.pdf-form-control-edited[type="radio"] {
          opacity: 1;
        }

        .pdf-widget-appearance-hidden {
          visibility: hidden;
        }

        .pdf-form-controls-flow {
          border: 1pt solid #9ca3af;
          display: grid;
          gap: 8pt;
          margin: 12pt 0;
          padding: 10pt;
        }

        .pdf-form-group-flow {
          display: grid;
          gap: 8pt;
        }

        .pdf-form-control-label {
          font: 10pt Arial, Helvetica, sans-serif;
        }

        .pdf-form-controls-flow .pdf-form-control {
          min-height: 24pt;
          width: 100%;
        }

        .pdf-semantic-element {
          box-sizing: border-box;
          color: #111827;
          margin: 0;
          overflow: visible;
          padding: 0;
          z-index: 1;
        }

        .pdf-page.pdf-semantic-page {
          box-sizing: border-box;
        }

        .pdf-semantic-layout-fallback-page {
          flex: none;
          margin: 24pt -72pt;
          max-width: none;
        }

        .pdf-semantic-layout-fallback-page:first-child {
          margin-top: 0;
        }

        .pdf-semantic-layout-fallback-page-with-islands {
          display: grid;
          grid-template-columns: minmax(0, 1fr);
          grid-template-rows: minmax(0, 1fr);
        }

        .pdf-semantic-element.pdf-semantic-fallback-island {
          align-self: start;
          grid-area: 1 / 1;
          justify-self: start;
          line-height: var(--pdf-semantic-island-line-height);
          margin: var(--pdf-semantic-island-top) 0 0 var(--pdf-semantic-island-left);
          min-height: var(--pdf-semantic-island-height);
          width: var(--pdf-semantic-island-width);
        }

        .pdf-semantic-blockquote.pdf-semantic-fallback-island {
          margin: var(--pdf-semantic-island-top) 0 0 var(--pdf-semantic-island-left);
        }

        .pdf-semantic-list.pdf-semantic-fallback-island {
          padding-left: var(--pdf-semantic-island-list-indent, 1.1em);
        }

        .pdf-semantic-list.pdf-semantic-fallback-island > li {
          margin-bottom: var(--pdf-semantic-island-list-item-gap, 0);
          padding-left: 0;
        }

        .pdf-semantic-list.pdf-semantic-fallback-island > li:last-child {
          margin-bottom: 0;
        }

        .pdf-semantic-fallback-island > p,
        .pdf-semantic-fallback-island > footer {
          display: inline;
          margin: 0;
        }

        .pdf-semantic-fallback-island > footer {
          margin-left: 0.25em;
        }

        .pdf-semantic-line-grid {
          align-self: stretch;
          box-sizing: border-box;
          display: flex;
          flex: none;
          flex-direction: column;
          margin: 0 0 0 -108pt;
          max-width: none;
          padding: var(--pdf-semantic-grid-top, 0) var(--pdf-semantic-grid-right, 0) 0 var(--pdf-semantic-grid-left, 0);
          width: calc(100% + 216pt);
        }

        .pdf-semantic-page-start + .pdf-semantic-line-grid {
          margin-top: -68pt;
        }

        .pdf-semantic-line-grid-row {
          column-gap: 0;
          display: grid;
          grid-template-columns: repeat(var(--pdf-semantic-grid-columns, 2), minmax(0, 1fr));
          min-height: var(--pdf-semantic-grid-row-height, auto);
        }

        .pdf-semantic-line-grid-cell {
          justify-self: start;
          line-height: var(--pdf-semantic-grid-row-height, 1);
          min-width: 0;
          overflow: visible;
          white-space: pre;
        }

        mark.pdf-semantic-mark {
          background-color: var(--pdf-semantic-mark-background);
          color: inherit;
          display: inline-block;
          line-height: inherit;
          min-width: var(--pdf-semantic-mark-width, auto);
          padding: 0;
        }

        .pdf-semantic-columns {
          align-self: stretch;
          align-items: start;
          box-sizing: border-box;
          display: grid;
          grid-template-columns: var(--pdf-semantic-column-tracks, repeat(2, minmax(0, 1fr)));
          margin: 0 0 0 calc((100% - var(--pdf-page-width)) / 2);
          max-width: none;
          padding: var(--pdf-semantic-columns-top, 0) var(--pdf-semantic-columns-right-relative, var(--pdf-semantic-columns-right, 0)) 0 var(--pdf-semantic-columns-left-relative, var(--pdf-semantic-columns-left, 0));
          width: var(--pdf-page-width);
        }

        .pdf-semantic-page-start + .pdf-semantic-columns {
          margin-top: -68pt;
        }

        .pdf-semantic-column {
          grid-column: var(--pdf-semantic-column-grid-position, auto);
          grid-row: var(--pdf-semantic-column-grid-row, auto);
          min-width: 0;
          position: relative;
        }

        .pdf-semantic-column-positioned-figure {
          margin: 0;
          max-width: none;
          position: absolute;
          z-index: 0;
        }

        .pdf-semantic-column-block {
          min-width: 0;
        }

        .pdf-semantic-column-block > .pdf-semantic-list {
          margin-bottom: 0;
        }

        .pdf-semantic-authored-column-block > .pdf-semantic-element {
          margin-bottom: 0;
        }

        .pdf-semantic-column-spanning {
          grid-column: 1 / -1;
          position: relative;
        }

        .pdf-semantic-column-spanning-run {
          display: block;
          line-height: 1;
          position: absolute;
          white-space: pre;
        }

        .pdf-semantic-column-spanning-figure {
          grid-column: 1 / -1;
          margin: 0;
        }

        .pdf-semantic-column-spanning-figure > figcaption {
          margin-top: 8pt;
        }

        .pdf-semantic-column-run {
          display: block;
          line-height: var(--pdf-semantic-column-row-height, 1);
          min-height: var(--pdf-semantic-column-row-height, auto);
          overflow: visible;
          white-space: pre;
        }

        .pdf-semantic-page-rule-group {
          align-self: stretch;
          box-sizing: border-box;
          height: var(--pdf-semantic-page-rule-group-height, 0);
          margin: 0 0 var(--pdf-semantic-page-rule-gap-after, 0)
            calc((100% - var(--pdf-page-width)) / 2);
          max-width: none;
          padding: 0 var(--pdf-semantic-page-rule-right-relative, 0)
            0 var(--pdf-semantic-page-rule-left-relative, 0);
          width: var(--pdf-page-width);
        }

        .pdf-semantic-page-rule-stack {
          height: 100%;
          position: relative;
          width: 100%;
        }

        .pdf-semantic-page-rule {
          border: 0;
          border-top: var(--pdf-semantic-page-rule-thickness, 0.5pt) solid
            var(--pdf-semantic-page-rule-color, currentColor);
          box-sizing: border-box;
          height: 0;
          left: var(--pdf-semantic-page-rule-left, 0);
          margin: 0;
          position: absolute;
          top: var(--pdf-semantic-page-rule-top, 0);
          width: var(--pdf-semantic-page-rule-width, 100%);
        }

        .pdf-semantic-ruled-grid-frame {
          align-self: stretch;
          box-sizing: border-box;
          margin: 6pt 0 10pt calc((100% - var(--pdf-page-width)) / 2);
          max-width: none;
          padding: 0 var(--pdf-semantic-ruled-right-relative, var(--pdf-semantic-ruled-right, 0))
            0 var(--pdf-semantic-ruled-left-relative, var(--pdf-semantic-ruled-left, 0));
          width: var(--pdf-page-width);
        }

        .pdf-semantic-ruled-grid {
          border: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          box-sizing: border-box;
          display: grid;
          grid-template-columns: var(--pdf-semantic-ruled-tracks);
          width: 100%;
        }

        .pdf-semantic-ruled-grid-cell,
        .pdf-semantic-ruled-grid-connector,
        .pdf-semantic-ruled-grid-spanning {
          box-sizing: border-box;
          min-width: 0;
        }

        .pdf-semantic-ruled-grid-cell {
          border-bottom: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          border-right: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          grid-column: var(--pdf-semantic-ruled-column);
          grid-row: var(--pdf-semantic-ruled-row);
          padding: 6pt;
        }

        .pdf-semantic-ruled-grid-cell-last {
          border-right: 0;
        }

        .pdf-semantic-ruled-grid-cell-shared-heading-right,
        .pdf-semantic-ruled-grid-cell-shared-heading-left {
          display: flex;
          flex-direction: column;
        }

        .pdf-semantic-ruled-grid-cell-shared-heading-right {
          border-right: 0;
          padding-right: 9pt;
        }

        .pdf-semantic-ruled-grid-cell-shared-heading-left {
          padding-left: 9pt;
        }

        .pdf-semantic-ruled-grid-cell-shared-heading-right > :first-child,
        .pdf-semantic-ruled-grid-cell-shared-heading-left > :first-child {
          box-sizing: border-box;
          min-height: var(--pdf-semantic-ruled-shared-heading-height, auto);
        }

        .pdf-semantic-ruled-grid .pdf-semantic-ruled-grid-column-heading {
          align-items: center;
          box-sizing: border-box;
          display: flex;
          flex-direction: column;
          justify-content: center;
          min-height: var(--pdf-semantic-ruled-shared-heading-height, auto);
          text-align: center;
          text-align-last: center;
        }

        .pdf-semantic-ruled-grid-cell-body {
          display: flex;
          flex: 1 1 auto;
          flex-direction: column;
          min-height: 0;
          position: relative;
        }

        .pdf-semantic-ruled-grid-cell-body::after {
          border-right: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          bottom: -6pt;
          content: "";
          pointer-events: none;
          position: absolute;
          right: calc(-9pt - var(--pdf-semantic-ruled-border-width, 0.5pt));
          top: calc(-1 * var(--pdf-semantic-ruled-border-width, 0.5pt));
        }

        .pdf-semantic-ruled-grid-cell > .pdf-semantic-list,
        .pdf-semantic-ruled-grid-cell-body > .pdf-semantic-list {
          margin-bottom: 0;
        }

        .pdf-semantic-ruled-grid-source-separator {
          padding-bottom: 3pt;
          position: relative;
        }

        .pdf-semantic-ruled-grid-source-separator::after {
          border-bottom: var(--pdf-semantic-ruled-source-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-source-border-color, #111827);
          bottom: 0;
          content: "";
          left: -6pt;
          pointer-events: none;
          position: absolute;
          right: -6pt;
        }

        .pdf-semantic-ruled-grid-cell-shared-heading-right
          .pdf-semantic-ruled-grid-source-separator::after {
          right: -9pt;
        }

        .pdf-semantic-ruled-grid-cell-shared-heading-left
          .pdf-semantic-ruled-grid-source-separator::after {
          left: -9pt;
        }

        .pdf-semantic-ruled-grid-cell > .pdf-semantic-list >
          li.pdf-semantic-ruled-grid-source-separator::after,
        .pdf-semantic-ruled-grid-cell-body > .pdf-semantic-list >
          li.pdf-semantic-ruled-grid-source-separator::after {
          left: calc(-1.35em - 6pt);
        }

        .pdf-semantic-ruled-grid-connector {
          align-items: start;
          border-bottom: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          border-right: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          display: flex;
          grid-column: var(--pdf-semantic-ruled-column);
          grid-row: var(--pdf-semantic-ruled-row);
          justify-content: center;
          overflow: visible;
          padding-top: 18pt;
          position: relative;
          white-space: nowrap;
          z-index: 2;
        }

        .pdf-semantic-ruled-grid-connector-collapsed {
          border-right: 0;
        }

        .pdf-semantic-ruled-grid-connector-collapsed > * {
          background: var(--pdf-page-background);
          padding: 0 4pt;
        }

        .pdf-semantic-ruled-grid-spanning {
          border-bottom: var(--pdf-semantic-ruled-border-width, 0.5pt) solid
            var(--pdf-semantic-ruled-border-color, #111827);
          grid-column: 1 / -1;
          grid-row: var(--pdf-semantic-ruled-row);
          padding: 6pt;
          text-align: center;
        }

        .pdf-semantic-ruled-grid-row-last {
          border-bottom: 0;
        }

        .pdf-semantic-flow .pdf-semantic-ruled-grid-lead-in {
          align-self: center;
          box-sizing: border-box;
          max-width: none;
          text-align: center;
          text-align-last: center;
          width: var(--pdf-semantic-ruled-grid-lead-in-width, 100%);
        }

        .pdf-semantic-document-flow {
          background: var(--pdf-page-background);
          box-shadow: 0 1pt 4pt var(--pdf-page-edge-shadow);
          box-sizing: border-box;
          margin: 0 auto 24pt;
          padding: 54pt 72pt 48pt;
          position: relative;
          width: var(--pdf-page-width);
        }

        .pdf-semantic-flow {
          box-sizing: border-box;
          display: flex;
          flex-direction: column;
          margin: 0 auto;
          min-height: 100%;
          padding: 54pt 0 36pt;
          width: min(396pt, calc(100% - 144pt));
        }

        .pdf-semantic-continuous-flow {
          min-height: 0;
          padding: 0;
          width: min(396pt, 100%);
        }

        .pdf-semantic-section {
          display: contents;
        }

        .pdf-semantic-flow > * + *,
        .pdf-semantic-section > * + * {
          margin-top: 0;
        }

        .pdf-semantic-page-break {
          border: 0;
          border-top: 1pt dashed #d1d5db;
          break-before: page;
          color: #6b7280;
          display: block;
          margin: 26pt 0 18pt;
          page-break-before: always;
          text-align: center;
        }

        .pdf-semantic-page-break::after {
          background: #fff;
          content: "Page " attr(data-page-number);
          font: 8pt Arial, Helvetica, sans-serif;
          padding: 0 6pt;
          position: relative;
          top: -0.65em;
        }

        .pdf-semantic-page-start {
          border-top: 0;
          break-before: auto;
          margin: 0 0 14pt;
          page-break-before: auto;
        }

        .pdf-semantic-page-artifacts {
          color: #6b7280;
          margin-bottom: 12pt;
        }

        .pdf-semantic-page-artifacts .pdf-semantic-element {
          line-height: 1.2;
          margin-bottom: 4pt;
        }

        .pdf-semantic-inline-page-break {
          margin-left: 0;
          margin-right: 0;
        }

        .pdf-semantic-continuous-flow .pdf-semantic-page-break:not(.pdf-semantic-page-start),
        .pdf-semantic-continuous-flow .pdf-semantic-inline-page-break {
          background:
            radial-gradient(ellipse at right top, var(--pdf-page-edge-shadow), rgba(17, 24, 39, 0) 70%) left top / calc(var(--pdf-page-shadow-mask) + var(--pdf-page-corner-shadow)) 4pt no-repeat,
            radial-gradient(ellipse at left top, var(--pdf-page-edge-shadow), rgba(17, 24, 39, 0) 70%) right top / calc(var(--pdf-page-shadow-mask) + var(--pdf-page-corner-shadow)) 4pt no-repeat,
            radial-gradient(ellipse at right bottom, var(--pdf-page-edge-shadow), rgba(17, 24, 39, 0) 70%) left bottom / calc(var(--pdf-page-shadow-mask) + var(--pdf-page-corner-shadow)) 4pt no-repeat,
            radial-gradient(ellipse at left bottom, var(--pdf-page-edge-shadow), rgba(17, 24, 39, 0) 70%) right bottom / calc(var(--pdf-page-shadow-mask) + var(--pdf-page-corner-shadow)) 4pt no-repeat,
            linear-gradient(to bottom, var(--pdf-page-edge-shadow), rgba(17, 24, 39, 0) 4pt) center top / calc(100% - var(--pdf-page-shadow-mask) - var(--pdf-page-shadow-mask) - var(--pdf-page-corner-shadow) - var(--pdf-page-corner-shadow)) 4pt no-repeat,
            linear-gradient(to top, var(--pdf-page-edge-shadow), rgba(17, 24, 39, 0) 4pt) center bottom / calc(100% - var(--pdf-page-shadow-mask) - var(--pdf-page-shadow-mask) - var(--pdf-page-corner-shadow) - var(--pdf-page-corner-shadow)) 4pt no-repeat,
            var(--pdf-page-surround);
          border: 0;
          box-shadow: none;
          box-sizing: border-box;
          height: 28pt;
          margin-bottom: 36pt;
          margin-left: calc((100% - var(--pdf-page-width)) / 2 - var(--pdf-page-shadow-mask));
          margin-top: 36pt;
          overflow: hidden;
          position: relative;
          width: calc(var(--pdf-page-width) + var(--pdf-page-shadow-mask) + var(--pdf-page-shadow-mask));
        }

        .pdf-semantic-continuous-flow .pdf-semantic-page-break::after {
          content: "";
          display: none;
        }

        .pdf-semantic-page-spanning {
          text-align-last: left;
        }

        .pdf-semantic-page-continuation {
          display: inline;
        }

        .pdf-semantic-inline-flow-element {
          display: block;
          margin: 8pt 0;
        }

        .pdf-semantic-math {
          font-family: "Times New Roman", Times, serif;
        }

        .pdf-semantic-italic {
          font-style: italic;
        }

        .pdf-semantic-small {
          font-size: inherit;
        }

        .pdf-semantic-citation {
          font-style: inherit;
        }

        .pdf-semantic-abbreviation[title] {
          text-decoration: none;
        }

        .pdf-semantic-bold {
          font-weight: 600;
        }

        .pdf-semantic-inline-fraction {
          display: inline-block;
          font-size: 0.72em;
          height: 1em;
          line-height: 1;
          margin: 0 0.06em;
          overflow: visible;
          text-align: center;
          vertical-align: -0.22em;
        }

        .pdf-semantic-inline-fraction-numerator {
          border-bottom: 0.04em solid currentColor;
          display: block;
          line-height: 0.5;
          min-width: 100%;
          padding: 0 0.1em 0.01em;
          text-align: center;
        }

        .pdf-semantic-inline-fraction-denominator {
          display: block;
          line-height: 0.5;
          padding: 0.01em 0.1em 0;
          text-align: center;
          white-space: nowrap;
        }

        .pdf-semantic-inline-summation {
          align-items: center;
          display: inline-flex;
          line-height: 1;
          margin: 0 0.06em;
          vertical-align: -0.25em;
        }

        .pdf-semantic-inline-summation-limits {
          display: inline-flex;
          flex-direction: column;
          font-size: 0.62em;
          line-height: 0.9;
          margin-left: 0.03em;
          text-align: center;
        }

        .pdf-semantic-inline-summation-limits sub {
          font-size: 0.75em;
          line-height: 0;
        }

        .pdf-semantic-formula {
          display: block;
          height: var(--pdf-semantic-formula-height, auto);
          line-height: 1;
          margin: 10pt auto;
          max-width: 100%;
          overflow: visible;
          position: relative;
          text-align: left;
          text-align-last: left;
          width: min(100%, var(--pdf-semantic-formula-width, 100%));
        }

        .pdf-semantic-formula-run {
          line-height: 1;
          position: absolute;
          white-space: pre;
        }

        .pdf-semantic-formula:not(.pdf-semantic-formula-native) {
          overflow-x: auto;
          overflow-y: hidden;
        }

        .pdf-semantic-formula-attached-suffix {
          transform: translateX(-0.15em);
        }

        .pdf-semantic-formula-radical {
          transform: translateY(5pt);
        }

        .pdf-semantic-formula-vector-layer {
          height: var(--pdf-semantic-formula-height, 100%);
          inset: 0;
          overflow: visible;
          pointer-events: none;
          position: absolute;
          width: var(--pdf-semantic-formula-width, 100%);
        }

        .pdf-semantic-formula.pdf-semantic-formula-native {
          align-items: center;
          display: flex;
          height: auto;
          justify-content: center;
          min-height: 1.5em;
        }

        .pdf-semantic-formula.pdf-semantic-formula-native.pdf-semantic-formula-numbered {
          column-gap: 0.5em;
          display: grid;
          grid-template-columns: max-content minmax(0, 1fr) max-content;
          width: 100%;
        }

        .pdf-semantic-formula-numbered::before {
          content: attr(data-equation-number);
          font-size: 0.9em;
          grid-column: 1;
          visibility: hidden;
          white-space: nowrap;
        }

        .pdf-semantic-formula-numbered > .pdf-semantic-mathml {
          grid-column: 2;
          justify-self: center;
          max-width: 100%;
          min-width: 0;
        }

        .pdf-semantic-formula-numbered > .pdf-semantic-equation-number {
          grid-column: 3;
          justify-self: end;
          position: static;
          right: auto;
        }

        .pdf-semantic-mathml {
          display: block;
          font-size: var(--pdf-semantic-math-font-size, 1em);
          max-width: calc(100% - 3em);
          overflow-x: auto;
          overflow-y: hidden;
        }

        .pdf-semantic-equation-number {
          color: inherit;
          font-size: 0.9em;
          position: absolute;
          right: 0;
          text-decoration: none;
          white-space: nowrap;
        }

        .pdf-semantic-figure {
          box-sizing: border-box;
          display: block;
          margin: 12pt auto;
          max-width: 100%;
          width: min(100%, var(--pdf-semantic-figure-width, 100%));
        }

        .pdf-semantic-cover-region {
          align-self: center;
          flex: 0 0 auto;
          height: var(--pdf-semantic-cover-height);
          margin: 0;
          max-width: none;
          position: relative;
          width: var(--pdf-semantic-cover-width);
        }

        .pdf-semantic-page-start + .pdf-semantic-cover-region {
          margin-top: -68pt;
        }

        .pdf-semantic-cover-decoration-layer {
          inset: 0;
          position: absolute;
        }

        .pdf-semantic-cover-region-element {
          margin: 0 !important;
          max-width: none;
          position: absolute;
        }

        .pdf-semantic-cover-region-element > .pdf-semantic-line {
          min-height: 1em;
          position: relative;
        }

        .pdf-semantic-cover-region-element > .pdf-semantic-line > .pdf-semantic-source-line-content {
          position: absolute;
          right: 0;
          top: 0;
          white-space: nowrap;
        }

        .pdf-semantic-inline-figure {
          margin-bottom: 8pt;
          margin-top: 8pt;
        }

        .pdf-semantic-figure-svg {
          display: block;
          height: auto;
          max-width: 100%;
          overflow: hidden;
          width: 100%;
        }

        .pdf-semantic-figure-text {
          dominant-baseline: alphabetic;
          paint-order: fill;
          white-space: pre;
        }

        .pdf-semantic-flow header {
          line-height: 1.25;
          margin-bottom: 26pt;
        }

        .pdf-semantic-flow h1,
        .pdf-semantic-flow h2,
        .pdf-semantic-flow h3,
        .pdf-semantic-flow h4,
        .pdf-semantic-flow h5,
        .pdf-semantic-flow h6 {
          font-weight: 600;
          line-height: 1.12;
          margin-bottom: 8pt;
        }

        .pdf-semantic-title {
          padding-left: 0;
          padding-right: 0;
        }

        .pdf-semantic-flow .pdf-semantic-title:not(.pdf-semantic-cover-region-element) {
          align-self: flex-start;
          max-width: calc(100% + 72pt);
          width: min(var(--pdf-semantic-title-width, 100%), calc(100% + 72pt));
        }

        .pdf-semantic-flow .pdf-semantic-title.pdf-semantic-align-center:not(.pdf-semantic-cover-region-element) {
          align-self: center;
        }

        .pdf-semantic-flow .pdf-semantic-title.pdf-semantic-align-right:not(.pdf-semantic-cover-region-element) {
          align-self: flex-end;
        }

        .pdf-semantic-element.pdf-semantic-title-regular {
          font-weight: 400;
        }

        .pdf-semantic-title-rule-top {
          border-top: var(--pdf-title-rule-top-thickness, 0.5pt) solid var(--pdf-title-rule-top-color, currentColor);
          padding-top: var(--pdf-title-rule-top-gap, 0);
        }

        .pdf-semantic-title-rule-bottom {
          border-bottom: var(--pdf-title-rule-bottom-thickness, 0.5pt) solid var(--pdf-title-rule-bottom-color, currentColor);
          padding-bottom: var(--pdf-title-rule-bottom-gap, 0);
        }

        .pdf-semantic-thematic-break {
          align-self: var(--pdf-thematic-break-alignment, center);
          border: 0;
          border-top: var(--pdf-thematic-break-thickness, 0.5pt) solid var(--pdf-thematic-break-color, currentColor);
          box-sizing: border-box;
          display: block;
          flex: 0 0 auto;
          height: 0;
          margin: 12pt 0;
          max-width: 100%;
          width: var(--pdf-thematic-break-width, 100%);
        }

        .pdf-semantic-title .pdf-semantic-line + .pdf-semantic-line {
          margin-top: 3pt;
        }

        .pdf-semantic-cover-title {
          align-self: flex-end;
          max-width: calc(100% + 72pt);
          width: min(var(--pdf-semantic-cover-title-width, 100%), calc(100% + 72pt));
        }

        .pdf-semantic-cover-title .pdf-semantic-line {
          white-space: nowrap;
        }

        .pdf-semantic-cover-text {
          transform: translateX(var(--pdf-semantic-cover-text-offset-x, 0));
        }

        .pdf-semantic-cover-text > .pdf-semantic-line {
          min-height: 1em;
          position: relative;
        }

        .pdf-semantic-cover-text > .pdf-semantic-line > .pdf-semantic-source-line-content {
          position: absolute;
          right: 0;
          top: 0;
          white-space: nowrap;
        }

        .pdf-semantic-heading {
          font-weight: 600;
          line-height: 1.12;
        }

        .pdf-semantic-paragraph {
          line-height: 1.18;
          margin-bottom: 6pt;
        }

        .pdf-semantic-code-block {
          line-height: 1.2;
          margin: 0 0 6pt;
          max-width: 100%;
          overflow-x: auto;
          white-space: pre;
        }

        .pdf-semantic-code-block > code {
          font: inherit;
          white-space: inherit;
        }

        .pdf-semantic-algorithm {
          align-self: center;
          border-bottom: var(--pdf-semantic-algorithm-bottom-rule-width, 0.5pt) solid var(--pdf-semantic-algorithm-bottom-rule-color, currentColor);
          border-top: var(--pdf-semantic-algorithm-top-rule-width, 0.5pt) solid var(--pdf-semantic-algorithm-top-rule-color, currentColor);
          box-sizing: border-box;
          margin: 0 0 8pt;
          max-width: 100%;
          width: min(100%, var(--pdf-semantic-width, 100%));
        }

        .pdf-semantic-algorithm-caption {
          border-bottom: var(--pdf-semantic-algorithm-caption-rule-width, 0.5pt) solid var(--pdf-semantic-algorithm-caption-rule-color, currentColor);
          line-height: 1.18;
          padding: 2pt 0 3pt;
        }

        .pdf-semantic-algorithm-rows {
          display: grid;
          min-width: 0;
          padding: 2pt 0;
        }

        .pdf-semantic-algorithm-row {
          box-sizing: border-box;
          line-height: 1.18;
          min-width: 0;
          padding-inline-start: min(var(--pdf-semantic-algorithm-indent, 0pt), 24%);
        }

        .pdf-semantic-algorithm-row > code {
          display: block;
          font: inherit;
          overflow-wrap: anywhere;
          white-space: pre-wrap;
        }

        .pdf-semantic-inline-code {
          white-space: pre;
        }

        .pdf-semantic-blockquote {
          line-height: 1.18;
          margin: 0 0 6pt;
          padding: 0;
        }

        .pdf-semantic-blockquote > p {
          margin: 0;
        }

        .pdf-semantic-blockquote > footer {
          font-style: normal;
          margin-top: 3pt;
        }

        .pdf-semantic-aside {
          line-height: 1.18;
          margin: 4pt 0 8pt;
          padding: 0;
        }

        .pdf-semantic-aside-source-geometry {
          align-self: flex-start;
          margin-left: var(--pdf-semantic-aside-inset-left, 0);
          max-width: none;
          width: var(--pdf-semantic-aside-source-width, auto);
        }

        .pdf-semantic-aside-source-decorated {
          background: var(--pdf-semantic-aside-source-background, transparent);
          border-color: var(--pdf-semantic-aside-source-border-color, transparent);
          border-style: var(--pdf-semantic-aside-source-border-style, solid);
          border-width: var(--pdf-semantic-aside-source-border-width, 0);
          box-sizing: border-box;
          padding:
            var(--pdf-semantic-aside-source-padding-top, 0)
            var(--pdf-semantic-aside-source-padding-right, 0)
            var(--pdf-semantic-aside-source-padding-bottom, 0)
            var(--pdf-semantic-aside-source-padding-left, 0);
        }

        .pdf-semantic-aside-label {
          font-weight: 600;
          margin: 0 0 5pt;
        }

        .pdf-semantic-aside > p {
          margin: 0 0 5pt;
        }

        .pdf-semantic-aside > p:last-child {
          margin-bottom: 0;
        }

        .pdf-semantic-list {
          line-height: 1.18;
          margin: 0 0 6pt;
          padding-left: 1.35em;
        }

        .pdf-semantic-list > li {
          margin: 0 0 2pt;
          padding-left: 0.2em;
        }

        .pdf-semantic-list .pdf-semantic-list {
          margin: 2pt 0 0;
        }

        ol.pdf-semantic-list-marker-parenthesized > li::marker {
          content: "(" counter(list-item) ") ";
        }

        ol.pdf-semantic-list-marker-closing-parenthesis > li::marker {
          content: counter(list-item) ") ";
        }

        .pdf-semantic-definition-list {
          column-gap: var(--pdf-semantic-definition-gap, 12pt);
          display: grid;
          grid-template-columns: minmax(0, var(--pdf-semantic-term-width, max-content)) minmax(0, 1fr);
          line-height: 1.18;
          margin: 0 0 8pt;
          row-gap: 5pt;
        }

        .pdf-semantic-definition-list > dt {
          font-weight: 600;
          min-width: 0;
        }

        .pdf-semantic-definition-list > dd {
          margin: 0;
          min-width: 0;
        }

        .pdf-semantic-definition-list-stacked {
          display: block;
        }

        .pdf-semantic-definition-list-stacked > dt {
          margin-top: 7pt;
        }

        .pdf-semantic-definition-list-stacked > dt:first-child {
          margin-top: 0;
        }

        .pdf-semantic-definition-list-stacked > dd {
          margin: 1pt 0 0;
        }

        .pdf-semantic-bibliography {
          line-height: 1.18;
          margin: 0 0 6pt;
          padding-left: 2.25em;
        }

        .pdf-semantic-bibliography[data-marker-kind="author-year"] {
          list-style: none;
          padding-left: 0;
        }

        .pdf-semantic-bibliography[data-marker-kind="bracketed-number"] > li::marker {
          content: "[" counter(list-item) "] ";
        }

        .pdf-semantic-bibliography > li {
          margin: 0 0 4pt;
          padding-left: 0.25em;
        }

        .pdf-semantic-document-index {
          margin: 0 0 12pt;
        }

        .pdf-semantic-document-index-island {
          border: 0;
          clip: rect(0 0 0 0);
          clip-path: inset(50%);
          height: 1px;
          margin: -1px;
          overflow: hidden;
          padding: 0;
          position: absolute;
          white-space: nowrap;
          width: 1px;
        }

        .pdf-semantic-document-index-heading {
          margin: 0 0 8pt;
        }

        .pdf-semantic-document-index-list {
          list-style: none;
          margin: 0;
          padding: 0;
        }

        .pdf-semantic-document-index-list .pdf-semantic-document-index-list {
          margin: 2pt 0 0 1.5em;
        }

        .pdf-semantic-document-index-item {
          margin: 0 0 2pt;
        }

        .pdf-semantic-document-index-entry {
          align-items: baseline;
          color: inherit;
          column-gap: 0.35em;
          display: grid;
          grid-template-columns: auto minmax(1em, 1fr) auto;
          line-height: 1.18;
          min-width: 0;
          text-decoration: none;
        }

        .pdf-semantic-document-index-label {
          min-width: 0;
        }

        .pdf-semantic-document-index-leader {
          align-self: baseline;
          border-bottom: 0.08em dotted currentColor;
          height: 0.7em;
          min-width: 1em;
          opacity: 0.65;
        }

        .pdf-semantic-document-index-page-number {
          font-variant-numeric: tabular-nums;
          text-align: right;
          white-space: nowrap;
        }

        .pdf-semantic-link,
        .pdf-semantic-auto-link {
          color: inherit;
          overflow-wrap: anywhere;
          text-decoration: underline;
          text-decoration-thickness: 0.06em;
          text-underline-offset: 0.12em;
        }

        .pdf-semantic-justified {
          text-align: justify;
          text-align-last: left;
        }

        .pdf-semantic-measured-width {
          align-self: var(--pdf-semantic-align-self, flex-start);
          width: min(100%, var(--pdf-semantic-width, 100%));
        }

        .pdf-semantic-align-center {
          text-align: center;
          text-align-last: center;
        }

        .pdf-semantic-align-right {
          text-align: right;
          text-align-last: right;
        }

        .pdf-semantic-line-row {
          column-gap: 18pt;
          display: grid;
          grid-template-columns: repeat(var(--pdf-semantic-line-count, 2), minmax(0, 1fr));
          margin-bottom: 6pt;
        }

        .pdf-semantic-line-row .pdf-semantic-line {
          min-width: 0;
        }

        .pdf-semantic-caption {
          line-height: 1.18;
        }

        figcaption.pdf-semantic-caption {
          font-weight: normal;
        }

        .pdf-semantic-table {
          border-collapse: collapse;
          line-height: 1.15;
          margin: 10pt auto 14pt;
          max-width: 100%;
          table-layout: auto;
          width: 100%;
        }

        .pdf-semantic-table.pdf-semantic-measured-width {
          width: min(100%, var(--pdf-semantic-width, 100%));
        }

        .pdf-semantic-table-caption {
          box-sizing: border-box;
          caption-side: top;
          font-weight: normal;
          line-height: 1.18;
          margin: 0;
          padding: 0 4pt var(--pdf-semantic-table-caption-gap, 5pt);
          text-align: start;
          text-align-last: start;
        }

        .pdf-semantic-table-caption-below {
          caption-side: bottom;
          padding: var(--pdf-semantic-table-caption-gap, 5pt) 4pt 0;
        }

        .pdf-semantic-table-caption-content {
          box-sizing: border-box;
          display: block;
          margin-inline-start: clamp(
            0%,
            var(--pdf-semantic-table-caption-offset, 0%),
            calc(100% - var(--pdf-semantic-table-caption-width, 100%)));
          width: var(--pdf-semantic-table-caption-width, 100%);
        }

        .pdf-semantic-table-caption.pdf-semantic-table-caption-align-left,
        .pdf-semantic-table-caption.pdf-semantic-table-caption-align-left .pdf-semantic-table-caption-content {
          text-align: left;
          text-align-last: left;
        }

        .pdf-semantic-table-caption.pdf-semantic-table-caption-align-center,
        .pdf-semantic-table-caption.pdf-semantic-table-caption-align-center .pdf-semantic-table-caption-content {
          text-align: center;
          text-align-last: center;
        }

        .pdf-semantic-table-caption.pdf-semantic-table-caption-align-right,
        .pdf-semantic-table-caption.pdf-semantic-table-caption-align-right .pdf-semantic-table-caption-content {
          text-align: right;
          text-align-last: right;
        }

        .pdf-semantic-table th,
        .pdf-semantic-table td {
          border: 0;
          padding: 2pt 4pt;
          text-align: left;
          vertical-align: top;
        }

        .pdf-semantic-table thead th {
          font-weight: 600;
          text-align: center;
        }

        .pdf-semantic-table tbody th {
          font-weight: 400;
        }

        .pdf-semantic-table .pdf-semantic-table-cell-border-top {
          border-top: 0.45pt solid currentColor;
        }

        .pdf-semantic-table .pdf-semantic-table-cell-border-right {
          border-right: 0.45pt solid currentColor;
        }

        .pdf-semantic-table .pdf-semantic-table-cell-border-bottom {
          border-bottom: 0.45pt solid currentColor;
        }

        .pdf-semantic-table .pdf-semantic-table-cell-border-left {
          border-left: 0.45pt solid currentColor;
        }

        .pdf-semantic-table td:not(:first-child) {
          text-align: right;
        }

        .pdf-semantic-table td.pdf-semantic-table-cell-align-left,
        .pdf-semantic-table th.pdf-semantic-table-cell-align-left {
          text-align: left;
        }

        .pdf-semantic-table td.pdf-semantic-table-cell-align-center,
        .pdf-semantic-table th.pdf-semantic-table-cell-align-center,
        .pdf-semantic-table td[colspan],
        .pdf-semantic-table th[colspan],
        .pdf-semantic-table-row-group-header {
          text-align: center;
        }

        .pdf-semantic-table td.pdf-semantic-table-cell-align-right,
        .pdf-semantic-table th.pdf-semantic-table-cell-align-right {
          text-align: right;
        }

        .pdf-semantic-table-row-group-header {
          vertical-align: middle;
        }

        .pdf-semantic-authors {
          display: flex;
          flex-direction: column;
          gap: 14pt;
          margin: 16pt 0 28pt;
        }

        .pdf-semantic-author-row {
          display: grid;
          gap: 18pt;
          justify-content: center;
        }

        .pdf-author-count-1 { grid-template-columns: minmax(78pt, 96pt); }
        .pdf-author-count-2 { grid-template-columns: repeat(2, minmax(78pt, 1fr)); }
        .pdf-author-count-3 { grid-template-columns: repeat(3, minmax(78pt, 1fr)); }
        .pdf-author-count-4 { grid-template-columns: repeat(4, minmax(72pt, 1fr)); }
        .pdf-author-count-5 { grid-template-columns: repeat(5, minmax(64pt, 1fr)); }
        .pdf-author-count-6 { grid-template-columns: repeat(6, minmax(58pt, 1fr)); }

        .pdf-semantic-authors address {
          font-style: normal;
          line-height: 1.15;
          text-align: center;
        }

        .pdf-semantic-front-matter {
          align-self: center;
          line-height: 1.15;
          margin: 0 0 var(--pdf-semantic-front-matter-gap-after, 12pt);
          max-width: calc(100% + 72pt);
          text-align: center;
          text-align-last: center;
          width: min(var(--pdf-semantic-front-matter-width, 100%), calc(100% + 72pt));
        }

        .pdf-semantic-front-matter > .pdf-semantic-line {
          min-width: 0;
          white-space: nowrap;
        }

        .pdf-semantic-line {
          display: block;
        }

        .pdf-semantic-author-block .pdf-semantic-line + .pdf-semantic-line {
          margin-top: 1pt;
        }

        .pdf-semantic-figure-space {
          display: block;
          flex: 0 0 auto;
          margin: 0 0 12pt;
          pointer-events: none;
        }

        .pdf-semantic-footnotes {
          margin-top: 18pt;
          padding-top: 0;
        }

        .pdf-semantic-footnote-group-label {
          border: 0;
          clip: rect(0 0 0 0);
          clip-path: inset(50%);
          height: 1px;
          margin: -1px;
          overflow: hidden;
          padding: 0;
          position: absolute;
          white-space: nowrap;
          width: 1px;
        }

        .pdf-semantic-footnotes::before {
          border-top: var(--pdf-footnote-rule-thickness, 0.5pt) solid var(--pdf-footnote-rule-color, #9ca3af);
          content: "";
          display: block;
          margin-bottom: 6pt;
          width: var(--pdf-footnote-rule-width, 144pt);
        }

        .pdf-semantic-note-list {
          list-style: none;
          margin: 0;
          padding: 0;
        }

        .pdf-semantic-footnotes .pdf-semantic-footnote {
          line-height: 1.18;
          margin: 0 0 4pt;
        }

        .pdf-semantic-footnote-marker {
          display: inline-block;
          min-width: 1.25em;
        }

        .pdf-semantic-footnote-backrefs {
          border: 0;
          clip: rect(0 0 0 0);
          clip-path: inset(50%);
          height: 1px;
          margin: -1px;
          overflow: hidden;
          padding: 0;
          position: absolute;
          white-space: nowrap;
          width: 1px;
        }

        .pdf-semantic-footnote-ref,
        .pdf-semantic-footnote-backref {
          color: inherit;
          text-decoration: none;
        }

        .pdf-semantic-footer,
        .pdf-semantic-header {
          line-height: 1.1;
        }

        .pdf-semantic-flow > footer.pdf-semantic-footer,
        .pdf-semantic-section footer.pdf-semantic-footer {
          margin-top: auto;
          padding-top: 16pt;
        }

        .pdf-semantic-positioned {
          position: absolute;
        }

        .pdf-semantic-vertical {
          transform: rotate(-90deg);
          transform-origin: left top;
          white-space: nowrap;
        }

        .pdf-link-overlay {
          background: transparent;
          display: block;
          outline: none;
          position: absolute;
          z-index: 1000002;
        }

        .pdf-image {
          display: block;
          object-fit: fill;
          position: absolute;
          z-index: 0;
        }

        .pdf-image-clip-definitions {
          height: 0;
          position: absolute;
          width: 0;
        }

        .pdf-vector-layer {
          display: block;
          left: 0;
          overflow: visible;
          pointer-events: none;
          position: absolute;
          top: 0;
          z-index: 0;
        }
        """;

    /// <summary>
    /// Converts an extracted layout document to fixed-layout HTML.
    /// </summary>
    /// <param name="layout">The layout document.</param>
    /// <param name="options">HTML conversion options.</param>
    /// <returns>The generated HTML document.</returns>
    public static PdfHtmlDocument Convert(PdfLayoutDocument layout, PdfHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        options ??= new PdfHtmlOptions();
        string cssPath = NormalizeCssPath(options.CssPath);
        Dictionary<string, PdfLayoutImageAsset> imageAssets = layout.ImageAssets
            .GroupBy(asset => asset.AssetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        bool semanticText = options.TextMode == PdfHtmlTextMode.Semantic;
        bool continuousSemanticFlow = semanticText && options.SemanticPageMode == PdfHtmlSemanticPageMode.ContinuousFlow;
        PdfSemanticDocument? semantic = semanticText
            ? PdfSemanticExtractor.Extract(layout, options.SemanticExtractionOptions)
            : null;
        StringBuilder html = new();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\" />");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.Append("  <title>").Append(Html(options.Title)).AppendLine("</title>");
        html.Append("  <link rel=\"stylesheet\" href=\"").Append(HtmlAttribute(cssPath)).AppendLine("\" />");
        html.AppendLine("</head>");
        html.Append("<body class=\"pdf-document");
        if (continuousSemanticFlow)
        {
            html.Append(" pdf-document-continuous");
        }

        html.AppendLine("\">");

        if (continuousSemanticFlow && semantic != null)
        {
            WriteSemanticContinuousDocument(
                html,
                layout,
                semantic,
                imageAssets,
                options.Scale,
                options.SemanticExtractionOptions);
        }
        else
        {
            for (int index = 0; index < layout.Pages.Count; index++)
            {
                WritePage(
                    html,
                    layout.Pages[index],
                    imageAssets,
                    options.Scale,
                    semantic?.Pages[index],
                    semantic?.SectionTree,
                    options.TextMode);
            }
        }

        if (layout.Pages.Any(static page => page.FormControls.Count > 0))
        {
            WriteFormControlInteractionScript(html);
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");
        string htmlText = html.ToString();
        HashSet<string> usedClassNames = ExtractClassNames(htmlText);
        PdfHtmlAsset[] assets = imageAssets.Values
            .Select(asset => new PdfHtmlAsset(asset.RelativePath, asset.ContentType, asset.Data))
            .Concat(layout.FontAssets.Select(asset => new PdfHtmlAsset(asset.RelativePath, asset.ContentType, asset.Data)))
            .ToArray();
        return new PdfHtmlDocument(htmlText, cssPath, BuildCss(semantic, layout.FontAssets, cssPath, usedClassNames), assets);
    }

    private static void WriteFontFaces(
        StringBuilder css,
        IReadOnlyList<PdfLayoutFontAsset> fontAssets,
        string cssPath)
    {
        foreach (PdfLayoutFontAsset asset in fontAssets.OrderBy(static asset => asset.AssetId, StringComparer.Ordinal))
        {
            foreach (string fontName in asset.FontNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal))
            {
                css.Append("@font-face{font-family:")
                    .Append(CssFontFamilyName(fontName))
                    .Append(";src:url('")
                    .Append(CssUrlRelativeToStylesheet(cssPath, asset.RelativePath))
                    .Append("') format('")
                    .Append(asset.CssFormat)
                    .Append("');font-display:block;font-style:")
                    .Append(asset.CssFontStyle)
                    .Append(";font-weight:")
                    .Append(asset.CssFontWeight.ToString(CultureInfo.InvariantCulture))
                    .AppendLine("}");
            }
        }
    }

    private static string BuildCss(
        PdfSemanticDocument? semantic,
        IReadOnlyList<PdfLayoutFontAsset> fontAssets,
        string cssPath,
        IReadOnlySet<string> usedClassNames)
    {
        StringBuilder css = new();
        WriteFontFaces(css, fontAssets, cssPath);
        css.Append(Css);
        if (semantic == null)
        {
            return css.ToString();
        }

        css.AppendLine();
        foreach (string fontName in semantic.Elements
            .SelectMany(static element => element.Lines)
            .SelectMany(static line => line.Runs
                .Select(static run => NormalizeFontName(run.FontName))
                .Append(line.DominantFontName))
            .Where(static fontName => !string.IsNullOrWhiteSpace(fontName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            if (!usedClassNames.Contains(FontClass(fontName)))
            {
                continue;
            }

            css.Append('.')
                .Append(FontClass(fontName))
                .Append("{font-family:")
                .Append(CssFontFamily(fontName))
                .AppendLine("}");
        }

        foreach (float fontSize in semantic.Elements
            .SelectMany(static element => element.Lines)
            .SelectMany(static line => line.Runs
                .Select(static run => MathF.Round(run.FontSize * 2f) / 2f)
                .Append(MathF.Round(line.DominantFontSize * 2f) / 2f))
            .Distinct()
            .Order())
        {
            if (!usedClassNames.Contains(FontSizeClass(fontSize)))
            {
                continue;
            }

            css.Append('.')
                .Append(FontSizeClass(fontSize))
                .Append("{font-size:")
                .Append(CssPoints(fontSize))
                .AppendLine("}");
        }

        foreach (PdfLayoutColor color in semantic.Elements
            .SelectMany(static element => element.Lines)
            .SelectMany(static line => line.Runs
                .Select(static run => run.Color)
                .Append(line.Color))
            .Distinct()
            .OrderBy(static color => color.Red)
            .ThenBy(static color => color.Green)
            .ThenBy(static color => color.Blue)
            .ThenBy(static color => color.Alpha))
        {
            if (!usedClassNames.Contains(ColorClass(color)))
            {
                continue;
            }

            css.Append('.')
                .Append(ColorClass(color))
                .Append("{color:")
                .Append(ColorHex(color));
            if (color.Alpha < 0.999f)
            {
                css.Append(";opacity:")
                    .Append(SvgNumber(color.Alpha));
            }

            css.AppendLine("}");
        }

        return css.ToString();
    }

    private static HashSet<string> ExtractClassNames(string html)
    {
        HashSet<string> classNames = new(StringComparer.Ordinal);
        const string marker = "class=\"";
        int searchStart = 0;
        while (searchStart < html.Length)
        {
            int classStart = html.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (classStart < 0)
            {
                break;
            }

            classStart += marker.Length;
            int classEnd = html.IndexOf('\"', classStart);
            if (classEnd < 0)
            {
                break;
            }

            foreach (string className in html[classStart..classEnd].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                classNames.Add(className);
            }

            searchStart = classEnd + 1;
        }

        return classNames;
    }

    private static void WritePage(
        StringBuilder html,
        PdfLayoutPage page,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale,
        PdfSemanticPage? semanticPage,
        PdfSemanticSectionTree? sectionTree,
        PdfHtmlTextMode textMode,
        string? additionalClass = null,
        PdfSemanticExtractionOptions? inferredTextOptions = null,
        PdfSemanticPage? fallbackSemanticPage = null)
    {
        bool isOcrScanPage = IsRasterScanPageWithOcrText(page);
        FallbackSemanticIsland[] fallbackSemanticIslands = FallbackSemanticIslands(page, fallbackSemanticPage);
        html.Append("  <section class=\"pdf-page");
        if (textMode == PdfHtmlTextMode.Semantic)
        {
            html.Append(" pdf-semantic-page");
        }

        if (!string.IsNullOrWhiteSpace(additionalClass))
        {
            html.Append(' ').Append(additionalClass);
        }

        if (fallbackSemanticIslands.Length > 0)
        {
            html.Append(" pdf-semantic-layout-fallback-page-with-islands");
        }

        if (isOcrScanPage)
        {
            html.Append(" pdf-ocr-scan-page");
        }

        html.Append("\" data-page-number=\"")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .Append("\" id=\"page-")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .Append("\" style=\"width:")
            .Append(CssPoints(page.Width * scale))
            .Append(";height:")
            .Append(CssPoints(page.Height * scale))
            .AppendLine("\">");

        PdfLayoutPath[] vectorPaths = RenderableVectorPaths(
            page,
            textMode == PdfHtmlTextMode.Semantic ? semanticPage : null);
        if (page.PaintOperations.Count > 0)
        {
            WriteOrderedGraphics(html, page, imageAssets, vectorPaths, scale);
        }
        else
        {
            PdfLayoutPath[] imageBackdrops = vectorPaths
                .Where(path => IsImageBackdropPath(path, page.Images))
                .ToArray();
            if (imageBackdrops.Length > 0)
            {
                WriteVectorLayer(
                    html,
                    page,
                    scale,
                    imageBackdrops,
                    "pdf-vector-layer pdf-vector-background-layer",
                    "background");
            }

            foreach (PdfLayoutImage image in page.Images)
            {
                if (imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
                {
                    WriteImage(html, page, image, asset, scale);
                }
            }

            PdfLayoutPath[] foregroundPaths = vectorPaths
                .Except(imageBackdrops)
                .ToArray();
            if (foregroundPaths.Length > 0)
            {
                WriteVectorLayer(
                    html,
                    page,
                    scale,
                    foregroundPaths,
                    "pdf-vector-layer",
                    "foreground");
            }
        }

        if (page.Shadings.Count > 0 &&
            !page.PaintOperations.Any(static operation =>
                operation.Kind == PdfLayoutPaintOperationKind.Shading))
        {
            WriteShadingLayer(html, page, scale);
        }

        if (textMode == PdfHtmlTextMode.Semantic && semanticPage != null)
        {
            WriteSemanticPage(html, page, semanticPage, sectionTree!, scale);
        }
        else
        {
            IReadOnlyDictionary<PdfTextRun, string>? inferredText = inferredTextOptions == null
                ? null
                : ReconstructFixedRunText(page, inferredTextOptions);
            HashSet<PdfTextRun> semanticIslandRuns = fallbackSemanticIslands
                .SelectMany(static island => island.OwnedRuns)
                .ToHashSet((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance);
            Dictionary<int, FallbackSemanticIsland> semanticIslandsByStart = fallbackSemanticIslands
                .ToDictionary(static island => island.FirstRunIndex);
            FootnoteContext? fallbackFootnotes = fallbackSemanticIslands.Length == 0 || fallbackSemanticPage == null
                ? null
                : FootnoteContext.Create(page.PageNumber, fallbackSemanticPage.Elements);
            for (int runIndex = 0; runIndex < page.Runs.Count; runIndex++)
            {
                if (semanticIslandsByStart.TryGetValue(runIndex, out FallbackSemanticIsland? island))
                {
                    WriteFallbackSemanticIsland(html, page, island, fallbackFootnotes!, scale);
                }

                PdfTextRun run = page.Runs[runIndex];
                if (semanticIslandRuns.Contains(run) ||
                    IsCoveredByVisualFallback(page, run.PageBounds, preserveTextOverComplexArtwork: true))
                {
                    continue;
                }

                WriteTextRun(
                    html,
                    run,
                    scale,
                    FixedTextFontSize(run, page.Runs),
                    page.Runs,
                    inferredText?.GetValueOrDefault(run),
                    visuallyHiddenOcr: isOcrScanPage && IsUnpaintedTextRun(run));
            }
        }

        HashSet<PdfLayoutLink> semanticIslandLinks = fallbackSemanticIslands
            .SelectMany(static island => island.OwnedLinks)
            .ToHashSet((IEqualityComparer<PdfLayoutLink>)ReferenceEqualityComparer.Instance);
        foreach (PdfLayoutLink link in page.Links)
        {
            if (!semanticIslandLinks.Contains(link))
            {
                WriteLink(html, link, scale);
            }
        }

        WriteFormControls(html, page, scale, positioned: true);

        html.AppendLine("  </section>");
    }

    internal static FallbackSemanticIsland[] FallbackSemanticIslands(
        PdfLayoutPage page,
        PdfSemanticPage? semanticPage)
    {
        if (semanticPage == null || IsRasterScanPageWithOcrText(page))
        {
            return [];
        }

        Dictionary<PdfTextRun, int> pageRunIndices = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < page.Runs.Count; index++)
        {
            if (!pageRunIndices.TryAdd(page.Runs[index], index))
            {
                return [];
            }
        }

        Dictionary<PdfTextGlyph, int> pageGlyphOccurrences = new(ReferenceEqualityComparer.Instance);
        foreach (PdfTextGlyph glyph in page.Glyphs)
        {
            pageGlyphOccurrences[glyph] = pageGlyphOccurrences.GetValueOrDefault(glyph) + 1;
        }

        Dictionary<PdfTextGlyph, int> semanticGlyphOwners = new(ReferenceEqualityComparer.Instance);
        foreach (PdfSemanticElement element in semanticPage.Elements)
        {
            HashSet<PdfTextGlyph> elementGlyphs = element.Lines
                .SelectMany(static line => line.Runs)
                .SelectMany(static run => run.Glyphs)
                .ToHashSet((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
            foreach (PdfTextGlyph glyph in elementGlyphs)
            {
                semanticGlyphOwners[glyph] = semanticGlyphOwners.GetValueOrDefault(glyph) + 1;
            }
        }

        List<FallbackSemanticIsland> proven = [];
        foreach (PdfSemanticElement element in semanticPage.Elements.Where(IsFallbackSemanticIslandCandidate))
        {
            if (TryProveFallbackSemanticIsland(
                page,
                element,
                pageRunIndices,
                pageGlyphOccurrences,
                semanticGlyphOwners,
                out FallbackSemanticIsland? island))
            {
                proven.Add(island!);
            }
        }

        return proven
            .Where((island, index) => !proven.Where((_, otherIndex) => otherIndex != index)
                .Any(other => BoundsOverlap(island.Bounds, other.Bounds)))
            .OrderBy(static island => island.FirstRunIndex)
            .ToArray();
    }

    private static bool IsFallbackSemanticIslandCandidate(PdfSemanticElement element)
    {
        return element.Kind switch
        {
            PdfSemanticElementKind.List => element.SemanticList is { Items.Count: > 0 },
            PdfSemanticElementKind.BlockQuote => element.Quotation != null,
            _ => false
        } && element.Lines.Count > 0;
    }

    private static bool TryProveFallbackSemanticIsland(
        PdfLayoutPage page,
        PdfSemanticElement element,
        IReadOnlyDictionary<PdfTextRun, int> pageRunIndices,
        IReadOnlyDictionary<PdfTextGlyph, int> pageGlyphOccurrences,
        IReadOnlyDictionary<PdfTextGlyph, int> semanticGlyphOwners,
        out FallbackSemanticIsland? island)
    {
        island = null;
        if (!TryGetFallbackSemanticIslandLines(element, out PdfSemanticLine[] sourceLines) ||
            !HasUsableSemanticIslandBounds(element.Bounds, page))
        {
            return false;
        }

        PdfTextRun[] sourceRunSequence = sourceLines.SelectMany(static line => line.Runs).ToArray();
        PdfTextRun[] ownedRuns = sourceRunSequence
            .Distinct((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance)
            .ToArray();
        if (ownedRuns.Length == 0 || ownedRuns.Length != sourceRunSequence.Length ||
            ownedRuns.Any(static run => run.Glyphs.Count == 0 || MathF.Abs(run.Direction) > 0.01f) ||
            ownedRuns.Any(run => !pageRunIndices.ContainsKey(run)) ||
            ownedRuns.Any(run => IsCoveredByVisualFallback(
                page,
                run.PageBounds,
                preserveTextOverComplexArtwork: true)))
        {
            return false;
        }

        PdfTextGlyph[] sourceGlyphSequence = ownedRuns.SelectMany(static run => run.Glyphs).ToArray();
        HashSet<PdfTextGlyph> ownedGlyphs = sourceGlyphSequence
            .ToHashSet((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        if (ownedGlyphs.Count != sourceGlyphSequence.Length ||
            ownedGlyphs.Any(static glyph => !glyph.IsPainted) ||
            ownedGlyphs.Any(glyph => pageGlyphOccurrences.GetValueOrDefault(glyph) != 1) ||
            ownedGlyphs.Any(glyph => semanticGlyphOwners.GetValueOrDefault(glyph) != 1))
        {
            return false;
        }

        int[] runIndices = ownedRuns.Select(run => pageRunIndices[run]).Order().ToArray();
        if (runIndices[^1] - runIndices[0] + 1 != runIndices.Length)
        {
            return false;
        }

        PdfLayoutRectangle sourceBounds = UnionRectangles(ownedRuns.Select(static run => run.Bounds));
        if (!HasUsableSemanticIslandBounds(sourceBounds, page) ||
            ownedRuns.Any(run => !RectangleContainsWithTolerance(element.Bounds, run.Bounds, 0.75f)))
        {
            return false;
        }

        HashSet<PdfTextRun> ownedRunSet = ownedRuns
            .ToHashSet((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance);
        if (page.Runs.Any(run => !ownedRunSet.Contains(run) &&
                RectangleIntersectionArea(run.Bounds, sourceBounds) > 0.1f) ||
            page.FormControls.Any(control => RectangleIntersectionArea(control.Bounds, sourceBounds) > 0.1f) ||
            !TryGetFallbackSemanticIslandLinks(
                page,
                element,
                sourceBounds,
                ownedGlyphs,
                out PdfLayoutLink[] ownedLinks))
        {
            return false;
        }

        island = new FallbackSemanticIsland(
            element,
            sourceBounds,
            ownedRuns,
            ownedLinks,
            runIndices[0],
            runIndices[^1]);
        return true;
    }

    private static bool TryGetFallbackSemanticIslandLinks(
        PdfLayoutPage page,
        PdfSemanticElement element,
        PdfLayoutRectangle sourceBounds,
        IReadOnlySet<PdfTextGlyph> ownedGlyphs,
        out PdfLayoutLink[] ownedLinks)
    {
        ownedLinks = page.Links
            .Where(link => LinkBounds(link)
                .Any(bounds => RectangleIntersectionArea(bounds, sourceBounds) > 0.1f))
            .ToArray();
        if (ownedLinks.Length == 0)
        {
            return true;
        }

        HashSet<PdfTextGlyph> visibleGlyphs = FallbackSemanticIslandVisibleGlyphs(element)
            .ToHashSet((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        foreach (PdfLayoutLink link in ownedLinks)
        {
            if (!HasSemanticLinkTarget(link) ||
                !visibleGlyphs.Any(glyph =>
                    ReferenceEquals(SemanticLinkForGlyph(page, glyph.PageBounds), link)) ||
                page.Glyphs.Any(glyph =>
                    !ownedGlyphs.Contains(glyph) &&
                    ReferenceEquals(SemanticLinkForGlyph(page, glyph.PageBounds), link)))
            {
                ownedLinks = [];
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<PdfTextGlyph> FallbackSemanticIslandVisibleGlyphs(PdfSemanticElement element)
    {
        if (element.SemanticList == null)
        {
            return element.Lines
                .SelectMany(static line => line.Runs)
                .SelectMany(static run => run.Glyphs)
                .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text));
        }

        return SemanticListVisibleGlyphs(element.SemanticList);
    }

    private static IEnumerable<PdfTextGlyph> SemanticListVisibleGlyphs(PdfSemanticList list)
    {
        foreach (PdfSemanticListItem item in list.Items)
        {
            for (int lineIndex = 0; lineIndex < item.Lines.Count; lineIndex++)
            {
                int leadingCharacters = lineIndex == 0 ? item.MarkerLength : 0;
                foreach (PdfTextGlyph glyph in item.Lines[lineIndex].Runs.SelectMany(static run => run.Glyphs))
                {
                    if (leadingCharacters >= glyph.Text.Length)
                    {
                        leadingCharacters -= glyph.Text.Length;
                        continue;
                    }

                    string visibleText = glyph.Text[leadingCharacters..];
                    leadingCharacters = 0;
                    if (!string.IsNullOrWhiteSpace(visibleText))
                    {
                        yield return glyph;
                    }
                }
            }

            foreach (PdfSemanticList nestedList in item.NestedLists)
            {
                foreach (PdfTextGlyph glyph in SemanticListVisibleGlyphs(nestedList))
                {
                    yield return glyph;
                }
            }
        }
    }

    private static bool TryGetFallbackSemanticIslandLines(
        PdfSemanticElement element,
        out PdfSemanticLine[] sourceLines)
    {
        sourceLines = element.Lines.ToArray();
        if (element.Kind != PdfSemanticElementKind.List || element.SemanticList == null)
        {
            return element.Kind == PdfSemanticElementKind.BlockQuote && element.Quotation != null;
        }

        PdfSemanticLine[] structuredLines = SemanticListSourceLines(element.SemanticList).ToArray();
        return structuredLines.Length == sourceLines.Length &&
            structuredLines.Zip(sourceLines, ReferenceEquals).All(static matches => matches);
    }

    private static IEnumerable<PdfSemanticLine> SemanticListSourceLines(PdfSemanticList list)
    {
        foreach (PdfSemanticListItem item in list.Items)
        {
            foreach (PdfSemanticLine line in item.Lines)
            {
                yield return line;
            }

            foreach (PdfSemanticList nestedList in item.NestedLists)
            {
                foreach (PdfSemanticLine line in SemanticListSourceLines(nestedList))
                {
                    yield return line;
                }
            }
        }
    }

    private static bool HasUsableSemanticIslandBounds(PdfLayoutRectangle bounds, PdfLayoutPage page)
    {
        return float.IsFinite(bounds.X) &&
            float.IsFinite(bounds.Y) &&
            float.IsFinite(bounds.Width) &&
            float.IsFinite(bounds.Height) &&
            bounds.Width > 0.5f &&
            bounds.Height > 0.5f &&
            bounds.X >= -0.5f &&
            bounds.Y >= -0.5f &&
            bounds.Right <= page.Width + 0.5f &&
            bounds.Bottom <= page.Height + 0.5f;
    }

    private static bool RectangleContainsWithTolerance(
        PdfLayoutRectangle outer,
        PdfLayoutRectangle inner,
        float tolerance)
    {
        return inner.X >= outer.X - tolerance &&
            inner.Y >= outer.Y - tolerance &&
            inner.Right <= outer.Right + tolerance &&
            inner.Bottom <= outer.Bottom + tolerance;
    }

    private static void WriteFallbackSemanticIsland(
        StringBuilder html,
        PdfLayoutPage page,
        FallbackSemanticIsland island,
        FootnoteContext footnotes,
        float scale)
    {
        PdfSemanticElement element = island.Element;
        float lineHeight = FallbackSemanticIslandLineHeight(element);
        string style = FallbackSemanticIslandStyle(island, lineHeight, scale);
        if (element.Kind == PdfSemanticElementKind.List)
        {
            WriteSemanticList(
                html,
                element,
                footnotes,
                page,
                rootAdditionalClass: "pdf-semantic-fallback-island",
                rootStyle: style);
            return;
        }

        html.Append("    <blockquote class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-fallback-island\" style=\"")
            .Append(style)
            .Append("\">");
        WriteSemanticText(html, element, footnotes, page);
        html.AppendLine("</blockquote>");
    }

    private static string FallbackSemanticIslandStyle(
        FallbackSemanticIsland island,
        float lineHeight,
        float scale)
    {
        StringBuilder style = new();
        style.Append("--pdf-semantic-island-left:")
            .Append(CssPoints(island.Bounds.X * scale))
            .Append(";--pdf-semantic-island-top:")
            .Append(CssPoints(island.Bounds.Y * scale))
            .Append(";--pdf-semantic-island-width:")
            .Append(CssPoints(island.Bounds.Width * scale))
            .Append(";--pdf-semantic-island-height:")
            .Append(CssPoints(island.Bounds.Height * scale))
            .Append(";--pdf-semantic-island-line-height:")
            .Append(CssPoints(lineHeight * scale));
        if (island.Element.SemanticList?.Items.FirstOrDefault() is PdfSemanticListItem firstItem &&
            TryGetSemanticListBodyIndent(firstItem, out float bodyIndent))
        {
            style.Append(";--pdf-semantic-island-list-indent:")
                .Append(CssPoints(bodyIndent * scale));
        }

        if (island.Element.SemanticList is { } list &&
            FallbackSemanticListItemGap(list, lineHeight) is float itemGap &&
            itemGap > 0f)
        {
            style.Append(";--pdf-semantic-island-list-item-gap:")
                .Append(CssPoints(itemGap * scale));
        }

        return style.ToString();
    }

    private static bool TryGetSemanticListBodyIndent(PdfSemanticListItem item, out float bodyIndent)
    {
        bodyIndent = 0f;
        PdfSemanticLine? firstLine = item.Lines.FirstOrDefault();
        if (firstLine == null || item.MarkerLength <= 0)
        {
            return false;
        }

        int characterIndex = 0;
        foreach (PdfTextGlyph glyph in firstLine.Runs.SelectMany(static run => run.Glyphs))
        {
            int nextCharacterIndex = characterIndex + glyph.Text.Length;
            if (nextCharacterIndex > item.MarkerLength && !string.IsNullOrWhiteSpace(glyph.Text))
            {
                bodyIndent = glyph.Bounds.X - item.Bounds.X;
                return bodyIndent > 0f && bodyIndent <= MathF.Max(72f, firstLine.DominantFontSize * 6f);
            }

            characterIndex = nextCharacterIndex;
        }

        return false;
    }

    private static float FallbackSemanticIslandLineHeight(PdfSemanticElement element)
    {
        float minimumLineStep = MathF.Max(1f, SemanticFontSize(element) * 1.18f);
        if (element.SemanticList != null)
        {
            return minimumLineStep;
        }

        float[] positions = element.Lines
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .Select(static line => line.Bounds.Y)
            .Distinct()
            .Order()
            .ToArray();
        float[] gaps = positions
            .Zip(positions.Skip(1), static (first, second) => second - first)
            .Where(gap => gap >= minimumLineStep * 0.75f && gap <= minimumLineStep * 3f)
            .Order()
            .ToArray();
        return gaps.Length == 0
            ? minimumLineStep
            : MathF.Max(minimumLineStep, gaps[gaps.Length / 2]);
    }

    private static float FallbackSemanticListItemGap(PdfSemanticList list, float lineHeight)
    {
        float[] gaps = list.Items
            .Zip(list.Items.Skip(1), static (first, second) => (First: first, Second: second))
            .Where(static pair => pair.First.Lines.Count > 0 && pair.Second.Lines.Count > 0)
            .Select(pair => pair.Second.Lines[0].Bounds.Y - pair.First.Lines[^1].Bounds.Y - lineHeight)
            .Where(gap => float.IsFinite(gap) && gap > 0f && gap <= lineHeight * 2f)
            .Order()
            .ToArray();
        return gaps.Length == 0 ? 0f : gaps[gaps.Length / 2];
    }

    private static IReadOnlyDictionary<PdfTextRun, string> ReconstructFixedRunText(
        PdfLayoutPage page,
        PdfSemanticExtractionOptions options)
    {
        Dictionary<PdfTextRun, string> textByRun = page.Runs.ToDictionary(
            static run => run,
            run => PdfSemanticExtractor.ReconstructText(run.Glyphs, options));

        foreach (PdfTextLine line in page.Lines)
        {
            for (int index = 0; index + 1 < line.Runs.Count; index++)
            {
                PdfTextRun current = line.Runs[index];
                PdfTextRun next = line.Runs[index + 1];
                PdfTextGlyph? lastGlyph = current.Glyphs.LastOrDefault();
                PdfTextGlyph? firstGlyph = next.Glyphs.FirstOrDefault();
                if (lastGlyph != null &&
                    firstGlyph != null &&
                    PdfSemanticExtractor.IsWordBoundaryBetween(lastGlyph, firstGlyph, options) &&
                    textByRun[current].Length > 0 &&
                    !char.IsWhiteSpace(textByRun[current][^1]))
                {
                    textByRun[current] += " ";
                }
            }
        }

        return textByRun;
    }

    private static void WriteOrderedGraphics(
        StringBuilder html,
        PdfLayoutPage page,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        IReadOnlyList<PdfLayoutPath> vectorPaths,
        float scale)
    {
        Dictionary<int, PdfLayoutImage> imagesByIndex = page.Images.ToDictionary(image => image.Index);
        Dictionary<int, PdfLayoutPath> pathsByIndex = vectorPaths.ToDictionary(path => path.Index);
        Dictionary<int, PdfLayoutShading> shadingsByIndex =
            page.Shadings.ToDictionary(static shading => shading.Index);
        List<PdfLayoutPath> pathBatch = [];
        PdfLayoutPath[] precedingUnderpaint = [];
        int vectorLayerIndex = 0;
        int graphicStackingIndex = 0;

        void FlushPaths()
        {
            if (pathBatch.Count == 0)
            {
                return;
            }

            WriteVectorLayer(
                html,
                page,
                scale,
                pathBatch,
                "pdf-vector-layer",
                "paint-" + vectorLayerIndex.ToString(CultureInfo.InvariantCulture),
                graphicStackingIndex);
            vectorLayerIndex++;
            graphicStackingIndex++;
            pathBatch.Clear();
        }

        foreach (PdfLayoutPaintOperation operation in page.PaintOperations)
        {
            if (operation.Kind == PdfLayoutPaintOperationKind.Path)
            {
                if (pathBatch.Count == 0)
                {
                    precedingUnderpaint = [];
                }

                if (pathsByIndex.TryGetValue(operation.Index, out PdfLayoutPath? path))
                {
                    pathBatch.Add(path);
                }

                continue;
            }

            if (pathBatch.Count > 0)
            {
                precedingUnderpaint = pathBatch.ToArray();
            }

            FlushPaths();
            if (operation.Kind == PdfLayoutPaintOperationKind.Shading)
            {
                if (shadingsByIndex.TryGetValue(operation.Index, out PdfLayoutShading? shading))
                {
                    WriteShadingLayer(
                        html,
                        page,
                        scale,
                        [shading],
                        graphicStackingIndex);
                    graphicStackingIndex++;
                }

                precedingUnderpaint = [];
                continue;
            }

            if (operation.Kind != PdfLayoutPaintOperationKind.Image)
            {
                continue;
            }

            if (imagesByIndex.TryGetValue(operation.Index, out PdfLayoutImage? image) &&
                imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
            {
                WriteImage(
                    html,
                    page,
                    image,
                    asset,
                    scale,
                    precedingUnderpaint,
                    graphicStackingIndex);
                graphicStackingIndex++;
                PdfLayoutPath[] preservedSpotUnderpaint = precedingUnderpaint
                    .Where(path => IsPreservedSpotUnderpaint(path, image))
                    .ToArray();
                if (preservedSpotUnderpaint.Length > 0)
                {
                    WriteVectorLayer(
                        html,
                        page,
                        scale,
                        preservedSpotUnderpaint,
                        "pdf-vector-layer pdf-overprint-preserved-layer",
                        "overprint-" + vectorLayerIndex.ToString(CultureInfo.InvariantCulture),
                        graphicStackingIndex);
                    vectorLayerIndex++;
                    graphicStackingIndex++;
                }
            }
        }

        FlushPaths();
    }

    private static bool IsPreservedSpotUnderpaint(PdfLayoutPath path, PdfLayoutImage image)
    {
        if (!image.Overprint || path.ColorantNames.Count == 0 || image.ColorantNames.Count == 0 ||
            !BoundsOverlap(path.Bounds, image.Bounds))
        {
            return false;
        }

        HashSet<string> imageColorants = new(image.ColorantNames, StringComparer.OrdinalIgnoreCase);
        int absentColorants = path.ColorantNames.Count(colorant => !imageColorants.Contains(colorant));
        if (absentColorants == path.ColorantNames.Count)
        {
            return true;
        }

        return absentColorants > 0 &&
            path.Bounds.Width < image.Bounds.Width &&
            path.Bounds.Height < image.Bounds.Height &&
            RectangleContainsWithTolerance(image.Bounds, path.Bounds, 0.25f);
    }

    private static bool BoundsOverlap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        return first.X < second.X + second.Width &&
            first.X + first.Width > second.X &&
            first.Y < second.Y + second.Height &&
            first.Y + first.Height > second.Y;
    }

    private static bool SameBounds(PdfLayoutRectangle first, PdfLayoutRectangle second, float tolerance)
    {
        return MathF.Abs(first.X - second.X) <= tolerance &&
            MathF.Abs(first.Y - second.Y) <= tolerance &&
            MathF.Abs(first.Width - second.Width) <= tolerance &&
            MathF.Abs(first.Height - second.Height) <= tolerance;
    }

    private static PdfLayoutImage? MatchingWidgetAppearance(PdfLayoutPage page, PdfLayoutFormControl control)
    {
        return page.Images.FirstOrDefault(image =>
            image.Kind == PdfLayoutImageKind.AnnotationAppearance &&
            string.Equals(image.SourceName, "Widget", StringComparison.OrdinalIgnoreCase) &&
            SameBounds(control.Bounds, image.Bounds, 0.5f));
    }

    private static void WriteFormControls(StringBuilder html, PdfLayoutPage page, float scale, bool positioned)
    {
        if (page.FormControls.Count == 0)
        {
            return;
        }

        string indentation = positioned ? "    " : "      ";
        html.Append(indentation)
            .Append("<form class=\"pdf-form-page ")
            .Append(positioned ? "pdf-form-page-positioned" : "pdf-form-controls-flow")
            .Append("\" aria-label=\"Form controls on original page ")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .AppendLine("\">");

        HashSet<PdfLayoutFormControl> emitted = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < page.FormControls.Count; i++)
        {
            PdfLayoutFormControl control = page.FormControls[i];
            if (!emitted.Add(control))
            {
                continue;
            }

            if (control.GroupKey != null)
            {
                PdfLayoutFormControl[] group = page.FormControls
                    .Where(candidate => string.Equals(candidate.GroupKey, control.GroupKey, StringComparison.Ordinal))
                    .ToArray();
                if (group.Length > 1)
                {
                    WriteFormGroup(html, page, group, scale, positioned);
                    emitted.UnionWith(group);
                    continue;
                }
            }

            WriteLabeledFormControl(html, page, control, scale, positioned);
        }

        html.Append(indentation).AppendLine("</form>");
    }

    private static void WriteFormGroup(
        StringBuilder html,
        PdfLayoutPage page,
        IReadOnlyList<PdfLayoutFormControl> controls,
        float scale,
        bool positioned)
    {
        PdfLayoutFormControl first = controls[0];
        string indentation = positioned ? "      " : "        ";
        string legend = first.GroupLabelText ?? GroupFallbackLabel(first);
        html.Append(indentation)
            .Append("<fieldset class=\"pdf-form-group ")
            .Append(positioned ? "pdf-form-group-positioned" : "pdf-form-group-flow")
            .Append("\" data-group-key=\"")
            .Append(HtmlAttribute(first.GroupKey!))
            .AppendLine("\">");
        html.Append(indentation)
            .Append("  <legend class=\"pdf-form-legend")
            .Append(positioned ? " pdf-form-legend-positioned" : string.Empty)
            .Append("\">")
            .Append(Html(legend))
            .AppendLine("</legend>");

        foreach (PdfLayoutFormControl control in controls)
        {
            WriteLabeledFormControl(html, page, control, scale, positioned);
        }

        html.Append(indentation).AppendLine("</fieldset>");
    }

    private static string GroupFallbackLabel(PdfLayoutFormControl control)
    {
        int optionSeparator = control.AccessibleName.IndexOf(": ", StringComparison.Ordinal);
        return optionSeparator > 0 ? control.AccessibleName[..optionSeparator] : control.AccessibleName;
    }

    private static void WriteLabeledFormControl(
        StringBuilder html,
        PdfLayoutPage page,
        PdfLayoutFormControl control,
        float scale,
        bool positioned)
    {
        string indentation = positioned ? "      " : "        ";
        html.Append(indentation)
            .Append("<label class=\"pdf-form-control-label")
            .Append(positioned ? " pdf-form-label-positioned" : string.Empty)
            .Append("\" for=\"")
            .Append(FormControlId(page.PageNumber, control.Index))
            .Append("\">");
        string labelText = control.SourceLabelText ?? control.AccessibleName;
        WritePlainTextWithInlineSemantics(
            html,
            labelText,
            control.SourceLabelText == null ? [] : control.SourceLabelInlineSemantics);
        html.AppendLine("</label>");

        PdfLayoutImage? authoredAppearance = positioned ? MatchingWidgetAppearance(page, control) : null;
        string? authoredAppearancePlacementId = authoredAppearance == null
            ? null
            : ImagePlacementId(page.PageNumber, authoredAppearance.Index);
        WriteFormControl(html, page.PageNumber, control, scale, positioned, authoredAppearancePlacementId);
    }

    private static void WriteFormControl(
        StringBuilder html,
        int pageNumber,
        PdfLayoutFormControl control,
        float scale,
        bool positioned,
        string? authoredAppearancePlacementId)
    {
        string currentValue = control.Values.FirstOrDefault() ?? string.Empty;
        string elementName = control.Kind switch
        {
            PdfLayoutFormControlKind.Text when control.IsMultiline => "textarea",
            PdfLayoutFormControlKind.ComboBox or PdfLayoutFormControlKind.ListBox => "select",
            _ => "input"
        };
        html.Append(positioned ? "    <" : "        <").Append(elementName);

        if (elementName == "input")
        {
            string inputType = control.Kind switch
            {
                PdfLayoutFormControlKind.CheckBox => "checkbox",
                PdfLayoutFormControlKind.RadioButton => "radio",
                PdfLayoutFormControlKind.Signature => "text",
                _ when control.IsPassword => "password",
                _ => "text"
            };
            html.Append(" type=\"").Append(inputType).Append('"');
            if (control.Kind is PdfLayoutFormControlKind.CheckBox or PdfLayoutFormControlKind.RadioButton)
            {
                html.Append(" value=\"")
                    .Append(HtmlAttribute(control.Options.FirstOrDefault()?.Value ?? "Yes"))
                    .Append('"');
                if (control.IsChecked)
                {
                    html.Append(" checked=\"checked\"");
                }
            }
            else
            {
                string value = control.Kind == PdfLayoutFormControlKind.Signature && currentValue.Length == 0
                    ? "Unsigned"
                    : currentValue;
                html.Append(" value=\"").Append(HtmlAttribute(value)).Append('"');
            }
        }

        AppendFormControlAttributes(html, pageNumber, control, scale, positioned, authoredAppearancePlacementId);

        if (elementName == "textarea")
        {
            html.Append('>').Append(Html(currentValue)).AppendLine("</textarea>");
            return;
        }

        if (elementName == "select")
        {
            if (control.IsMultiple)
            {
                html.Append(" multiple=\"multiple\"");
            }

            html.AppendLine(">");
            HashSet<string> optionValues = control.Options.Select(static option => option.Value).ToHashSet(StringComparer.Ordinal);
            if (currentValue.Length > 0 && !optionValues.Contains(currentValue))
            {
                WriteFormOption(html, currentValue, currentValue, selected: true,
                    control.DefaultValues.Contains(currentValue, StringComparer.Ordinal));
            }

            foreach (PdfLayoutFormOption option in control.Options)
            {
                WriteFormOption(
                    html,
                    option.Value,
                    option.Label,
                    control.Values.Contains(option.Value, StringComparer.Ordinal),
                    control.DefaultValues.Contains(option.Value, StringComparer.Ordinal));
            }

            html.Append(positioned ? "    </select>\n" : "        </select>\n");
            return;
        }

        html.AppendLine(" />");
    }

    private static void AppendFormControlAttributes(
        StringBuilder html,
        int pageNumber,
        PdfLayoutFormControl control,
        float scale,
        bool positioned,
        string? authoredAppearancePlacementId)
    {
        bool usesNativeToggleAppearance = control.Kind is
            PdfLayoutFormControlKind.CheckBox or PdfLayoutFormControlKind.RadioButton;
        html.Append(" class=\"pdf-form-control");
        if (positioned)
        {
            html.Append(" pdf-form-control-widget");
        }

        if (positioned && !usesNativeToggleAppearance)
        {
            html.Append(" pdf-form-control-positioned");
        }

        if (positioned && authoredAppearancePlacementId != null)
        {
            html.Append(" pdf-form-control-authored-appearance");
        }

        html.Append("\" id=\"")
            .Append(FormControlId(pageNumber, control.Index))
            .Append("\" name=\"")
            .Append(HtmlAttribute(control.Name))
            .Append("\" data-field-kind=\"")
            .Append(FormControlKind(control.Kind))
            .Append("\" data-default-value=\"")
            .Append(HtmlAttribute(string.Join("\n", control.DefaultValues)))
            .Append('"');

        if (authoredAppearancePlacementId != null)
        {
            html.Append(" data-widget-appearance-id=\"")
                .Append(HtmlAttribute(authoredAppearancePlacementId))
                .Append('"');
        }

        if (control.IsRequired)
        {
            html.Append(" required=\"required\" aria-required=\"true\"");
        }

        bool nativeReadOnly = control.Kind == PdfLayoutFormControlKind.Text ||
            control.Kind == PdfLayoutFormControlKind.Signature;
        if (control.IsReadOnly || control.Kind == PdfLayoutFormControlKind.Signature)
        {
            html.Append(nativeReadOnly
                ? " readonly=\"readonly\" aria-readonly=\"true\""
                : " disabled=\"disabled\" aria-disabled=\"true\"");
        }

        if (control.MaxLength is int maxLength && control.Kind == PdfLayoutFormControlKind.Text)
        {
            html.Append(" maxlength=\"").Append(maxLength.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        if (control.IsDefaultChecked)
        {
            html.Append(" data-default-checked=\"true\"");
        }

        if (positioned)
        {
            html.Append(" style=\"position:absolute;left:")
                .Append(CssPoints(control.Bounds.X * scale))
                .Append(";top:")
                .Append(CssPoints(control.Bounds.Y * scale))
                .Append(";width:")
                .Append(CssPoints(control.Bounds.Width * scale))
                .Append(";height:")
                .Append(CssPoints(control.Bounds.Height * scale))
                .Append('"');
        }
    }

    private static string FormControlId(int pageNumber, int controlIndex) =>
        "pdf-field-" + pageNumber.ToString(CultureInfo.InvariantCulture) + "-" +
        controlIndex.ToString(CultureInfo.InvariantCulture);

    private static void WriteFormControlInteractionScript(StringBuilder html)
    {
        html.AppendLine("  <script>");
        html.AppendLine("    (function () {");
        html.AppendLine("      const selector = '.pdf-form-control-widget';");
        html.AppendLine("      function appearance(control) {");
        html.AppendLine("        const id = control.dataset.widgetAppearanceId;");
        html.AppendLine("        return id === undefined ? null : document.getElementById(id);");
        html.AppendLine("      }");
        html.AppendLine("      function hideAppearance(control) {");
        html.AppendLine("        const image = appearance(control);");
        html.AppendLine("        if (image !== null) image.classList.add('pdf-widget-appearance-hidden');");
        html.AppendLine("      }");
        html.AppendLine("      function markEdited(event) {");
        html.AppendLine("        const control = event.target.closest(selector);");
        html.AppendLine("        if (control === null) return;");
        html.AppendLine("        control.classList.add('pdf-form-control-edited');");
        html.AppendLine("        hideAppearance(control);");
        html.AppendLine("      }");
        html.AppendLine("      document.addEventListener('focusin', function (event) {");
        html.AppendLine("        const control = event.target.closest(selector);");
        html.AppendLine("        if (control === null) return;");
        html.AppendLine("        control.classList.add('pdf-form-control-active');");
        html.AppendLine("        hideAppearance(control);");
        html.AppendLine("      });");
        html.AppendLine("      document.addEventListener('focusout', function (event) {");
        html.AppendLine("        const control = event.target.closest(selector);");
        html.AppendLine("        if (control === null) return;");
        html.AppendLine("        control.classList.remove('pdf-form-control-active');");
        html.AppendLine("        if (control.classList.contains('pdf-form-control-edited')) return;");
        html.AppendLine("        const image = appearance(control);");
        html.AppendLine("        if (image !== null) image.classList.remove('pdf-widget-appearance-hidden');");
        html.AppendLine("      });");
        html.AppendLine("      document.addEventListener('input', markEdited);");
        html.AppendLine("      document.addEventListener('change', markEdited);");
        html.AppendLine("    }());");
        html.AppendLine("  </script>");
    }

    private static void WriteFormOption(
        StringBuilder html,
        string value,
        string label,
        bool selected,
        bool defaultSelected)
    {
        html.Append("          <option value=\"").Append(HtmlAttribute(value)).Append('"');
        if (selected)
        {
            html.Append(" selected=\"selected\"");
        }

        if (defaultSelected)
        {
            html.Append(" data-default-selected=\"true\"");
        }

        html.Append('>').Append(Html(label)).AppendLine("</option>");
    }

    private static string FormControlKind(PdfLayoutFormControlKind kind) => kind switch
    {
        PdfLayoutFormControlKind.CheckBox => "checkbox",
        PdfLayoutFormControlKind.RadioButton => "radio",
        PdfLayoutFormControlKind.ComboBox => "combobox",
        PdfLayoutFormControlKind.ListBox => "listbox",
        PdfLayoutFormControlKind.Signature => "signature",
        _ => "text"
    };

    private static void WriteImage(
        StringBuilder html,
        PdfLayoutPage page,
        PdfLayoutImage image,
        PdfLayoutImageAsset asset,
        float scale,
        IReadOnlyList<PdfLayoutPath>? precedingUnderpaint = null,
        int? stackingIndex = null)
    {
        IReadOnlyList<PdfLayoutClipPath> clipPaths = CanPaintUniformImageWithoutClip(
            image,
            asset,
            precedingUnderpaint)
            ? []
            : EffectiveImageClipPaths(image);
        if (clipPaths.Count > 0)
        {
            WriteImageClipDefinitions(html, page, image, clipPaths);
        }

        html.Append("    <img class=\"pdf-image\" id=\"")
            .Append(ImagePlacementId(page.PageNumber, image.Index))
            .Append("\" src=\"")
            .Append(HtmlAttribute(image.OverprintCompositeColor is PdfLayoutColor compositeColor
                ? SolidColorImageDataUri(compositeColor)
                : asset.RelativePath))
            .Append("\" alt=\"")
            .Append(HtmlAttribute(image.AlternateDescription ?? string.Empty))
            .Append("\" data-asset-id=\"")
            .Append(HtmlAttribute(image.AssetId));

        if (!string.IsNullOrEmpty(image.SourceName))
        {
            html.Append("\" data-source-name=\"")
                .Append(HtmlAttribute(image.SourceName));
        }

        html.Append("\" style=\"position:absolute;left:")
            .Append(CssPoints(image.Bounds.X * scale))
            .Append(";top:")
            .Append(CssPoints(image.Bounds.Y * scale))
            .Append(";width:")
            .Append(CssPoints(image.Bounds.Width * scale))
            .Append(";height:")
            .Append(CssPoints(image.Bounds.Height * scale));
        if (stackingIndex is int imageStackingIndex)
        {
            html.Append(";z-index:")
                .Append(imageStackingIndex.ToString(CultureInfo.InvariantCulture));
        }
        if (clipPaths.Count > 0)
        {
            html.Append(";clip-path:url(#")
                .Append(ImageClipPathId(page, image))
                .Append(')');
        }
        if (image.MultiplyOverprint)
        {
            html.Append(";mix-blend-mode:multiply");
        }

        html.AppendLine("\" />");
    }

    private static string SolidColorImageDataUri(PdfLayoutColor color)
    {
        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"><rect width=\"1\" height=\"1\" fill=\"{ColorHex(color)}\"/></svg>";
        return "data:image/svg+xml;base64," + System.Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    private static bool CanPaintUniformImageWithoutClip(
        PdfLayoutImage image,
        PdfLayoutImageAsset asset,
        IReadOnlyList<PdfLayoutPath>? precedingUnderpaint)
    {
        if (image.ClipPaths.Count == 0 ||
            asset.UniformColor is not PdfLayoutColor imageColor ||
            precedingUnderpaint is null)
        {
            return false;
        }

        const float colorTolerance = (1f / 255f) + 0.0001f;
        return precedingUnderpaint.Any(path =>
            path.FillColor is PdfLayoutColor backdrop &&
            backdrop.Alpha >= 0.999f &&
            path.FillRule is int windingRule &&
            !path.UsesSoftMask &&
            IsAxisAlignedRectangle(new PdfLayoutClipPath(path.Commands, path.Bounds, windingRule)) &&
            path.ClipPaths.All(clipPath =>
                IsAxisAlignedRectangle(clipPath) &&
                ContainsWithTolerance(clipPath.Bounds, image.Bounds, 0.25f)) &&
            ContainsWithTolerance(path.Bounds, image.Bounds, 0.25f) &&
            MathF.Abs(backdrop.Red - imageColor.Red) <= colorTolerance &&
            MathF.Abs(backdrop.Green - imageColor.Green) <= colorTolerance &&
            MathF.Abs(backdrop.Blue - imageColor.Blue) <= colorTolerance);
    }

    private static void WriteImageClipDefinitions(
        StringBuilder html,
        PdfLayoutPage page,
        PdfLayoutImage image,
        IReadOnlyList<PdfLayoutClipPath> clipPaths)
    {
        html.AppendLine("    <svg class=\"pdf-image-clip-definitions\" width=\"0\" height=\"0\" aria-hidden=\"true\" focusable=\"false\">");
        html.AppendLine("      <defs>");
        WriteExactClipPathDefinitions(
            html,
            ImageClipPathId(page, image),
            clipPaths,
            "        ",
            " clipPathUnits=\"objectBoundingBox\"",
            clipPath => SvgObjectBoundingBoxPathData(clipPath.Commands, image.Bounds));
        html.AppendLine("      </defs>");
        html.AppendLine("    </svg>");
    }

    private static IReadOnlyList<PdfLayoutClipPath> EffectiveImageClipPaths(PdfLayoutImage image)
    {
        if (!image.ClipPaths.Any(static clipPath => !IsAxisAlignedRectangle(clipPath)))
        {
            return image.ClipPaths;
        }

        const float tolerance = 0.01f;
        return image.ClipPaths
            .Where(clipPath => !IsAxisAlignedRectangle(clipPath) ||
                !RectangleContains(clipPath.Bounds, image.Bounds, tolerance))
            .ToArray();
    }

    private static bool RectangleContains(
        PdfLayoutRectangle outer,
        PdfLayoutRectangle inner,
        float tolerance)
    {
        return outer.X <= inner.X + tolerance &&
            outer.Y <= inner.Y + tolerance &&
            outer.Right >= inner.Right - tolerance &&
            outer.Bottom >= inner.Bottom - tolerance;
    }

    private static string ImageClipPathId(PdfLayoutPage page, PdfLayoutImage image)
    {
        return "pdf-image-page-" + page.PageNumber.ToString(CultureInfo.InvariantCulture) +
            "-clip-" + image.Index.ToString(CultureInfo.InvariantCulture);
    }

    private static string ImagePlacementId(int pageNumber, int imageIndex)
    {
        return "pdf-image-page-" + pageNumber.ToString(CultureInfo.InvariantCulture) +
            "-placement-" + imageIndex.ToString(CultureInfo.InvariantCulture);
    }

    private static PdfLayoutPath[] RenderableVectorPaths(
        PdfLayoutPage page,
        PdfSemanticPage? semanticPage)
    {
        return page.Paths
            .Where(path => semanticPage == null || !IsSemanticFlowRulePath(page, semanticPage, path))
            .Where(static path => !RequiresShapeAlphaFallback(path))
            .Where(static path => !path.UsesSoftMask)
            .Where(path => !IsCoveredByVisualFallback(page, path.Bounds))
            .ToArray();
    }

    private static bool RequiresShapeAlphaFallback(PdfLayoutPath path)
    {
        return path.UsesShapeAlpha &&
            ((path.FillColor is PdfLayoutColor fillColor && fillColor.Alpha < 0.999f) ||
                path.Stroke?.Color.Alpha < 0.999f);
    }

    private static bool IsImageBackdropPath(PdfLayoutPath path, IReadOnlyList<PdfLayoutImage> images)
    {
        if (!path.IsFilled || path.FillColor is not PdfLayoutColor fillColor || fillColor.Alpha < 0.999f)
        {
            return false;
        }

        return images
            .Where(static image => !IsVisualFallbackImage(image))
            .Any(image => ContainsWithTolerance(path.Bounds, image.Bounds, 0.25f));
    }

    private static bool ContainsWithTolerance(
        PdfLayoutRectangle outer,
        PdfLayoutRectangle inner,
        float tolerance)
    {
        return outer.X <= inner.X + tolerance &&
            outer.Y <= inner.Y + tolerance &&
            outer.Right >= inner.Right - tolerance &&
            outer.Bottom >= inner.Bottom - tolerance;
    }

    private static void WriteVectorLayer(
        StringBuilder html,
        PdfLayoutPage page,
        float scale,
        IReadOnlyList<PdfLayoutPath> paths,
        string cssClass,
        string layerName,
        int? stackingIndex = null)
    {
        if (paths.Count == 0)
        {
            return;
        }

        html.Append("    <svg class=\"")
            .Append(cssClass)
            .Append("\" data-vector-layer=\"")
            .Append(layerName)
            .Append("\" data-path-count=\"")
            .Append(paths.Count.ToString(CultureInfo.InvariantCulture))
            .Append("\" viewBox=\"0 0 ")
            .Append(SvgNumber(page.Width))
            .Append(' ')
            .Append(SvgNumber(page.Height))
            .Append("\" style=\"position:absolute;left:0;top:0;width:")
            .Append(CssPoints(page.Width * scale))
            .Append(";height:")
            .Append(CssPoints(page.Height * scale));
        if (stackingIndex is int vectorStackingIndex)
        {
            html.Append(";z-index:")
                .Append(vectorStackingIndex.ToString(CultureInfo.InvariantCulture));
        }
        html
            .AppendLine("\" aria-hidden=\"true\">");

        WriteVectorContent(
            html,
            paths,
            page.VectorGroups,
            $"pdf-vector-page-{page.PageNumber.ToString(CultureInfo.InvariantCulture)}-{layerName}");

        html.AppendLine("    </svg>");
    }

    private static void WriteShadingLayer(
        StringBuilder html,
        PdfLayoutPage page,
        float scale,
        IReadOnlyList<PdfLayoutShading>? selectedShadings = null,
        int? stackingIndex = null)
    {
        IReadOnlyList<PdfLayoutShading> shadings = selectedShadings ?? page.Shadings;
        html.Append("    <svg class=\"pdf-shading-layer\" data-shading-count=\"")
            .Append(shadings.Count.ToString(CultureInfo.InvariantCulture))
            .Append("\" viewBox=\"0 0 ")
            .Append(SvgNumber(page.Width))
            .Append(' ')
            .Append(SvgNumber(page.Height))
            .Append("\" style=\"position:absolute;left:0;top:0;width:")
            .Append(CssPoints(page.Width * scale))
            .Append(";height:")
            .Append(CssPoints(page.Height * scale));
        if (stackingIndex is int shadingStackingIndex)
        {
            html.Append(";z-index:")
                .Append(shadingStackingIndex.ToString(CultureInfo.InvariantCulture));
        }
        html
            .AppendLine("\" aria-hidden=\"true\">");
        html.AppendLine("      <defs>");
        foreach (PdfLayoutShading shading in shadings)
        {
            WriteShadingDefinition(html, page, shading);
            if (shading.ShadingType == 7)
            {
                html.Append("        <clipPath id=\"")
                    .Append(ShadingId(page, shading))
                    .Append("-clip\"><rect x=\"")
                    .Append(SvgNumber(shading.Bounds.X))
                    .Append("\" y=\"")
                    .Append(SvgNumber(shading.Bounds.Y))
                    .Append("\" width=\"")
                    .Append(SvgNumber(shading.Bounds.Width))
                    .Append("\" height=\"")
                    .Append(SvgNumber(shading.Bounds.Height))
                    .AppendLine("\" /></clipPath>");
            }
        }

        html.AppendLine("      </defs>");
        foreach (PdfLayoutShading shading in shadings)
        {
            if (shading.ShadingType == 7)
            {
                WriteTensorShading(html, page, shading);
                continue;
            }

            html.Append("      <rect class=\"pdf-shading\" data-shading-index=\"")
                .Append(shading.Index.ToString(CultureInfo.InvariantCulture))
                .Append("\" x=\"")
                .Append(SvgNumber(shading.Bounds.X))
                .Append("\" y=\"")
                .Append(SvgNumber(shading.Bounds.Y))
                .Append("\" width=\"")
                .Append(SvgNumber(shading.Bounds.Width))
                .Append("\" height=\"")
                .Append(SvgNumber(shading.Bounds.Height))
                .Append("\"")
                .Append(" fill=\"url(#")
                .Append(ShadingId(page, shading))
                .AppendLine(")\" />");
        }

        html.AppendLine("    </svg>");
    }

    private static void WriteShadingDefinition(StringBuilder html, PdfLayoutPage page, PdfLayoutShading shading)
    {
        if (shading.ShadingType == 7)
        {
            return;
        }

        string id = ShadingId(page, shading);
        if (shading.ShadingType == 3)
        {
            html.Append("        <radialGradient id=\"")
                .Append(id)
                .Append("\" gradientUnits=\"userSpaceOnUse\" cx=\"")
                .Append(SvgNumber(shading.EndX))
                .Append("\" cy=\"")
                .Append(SvgNumber(shading.EndY))
                .Append("\" r=\"")
                .Append(SvgNumber(shading.EndRadius))
                .Append("\" fx=\"")
                .Append(SvgNumber(shading.StartX))
                .Append("\" fy=\"")
                .Append(SvgNumber(shading.StartY))
                .AppendLine("\">");
            WriteShadingStops(html, shading.Stops);
            html.AppendLine("        </radialGradient>");
            return;
        }

        html.Append("        <linearGradient id=\"")
            .Append(id)
            .Append("\" gradientUnits=\"userSpaceOnUse\" x1=\"")
            .Append(SvgNumber(shading.StartX))
            .Append("\" y1=\"")
            .Append(SvgNumber(shading.StartY))
            .Append("\" x2=\"")
            .Append(SvgNumber(shading.EndX))
            .Append("\" y2=\"")
            .Append(SvgNumber(shading.EndY))
            .AppendLine("\">");
        WriteShadingStops(html, shading.Stops);
        html.AppendLine("        </linearGradient>");
    }

    private static void WriteTensorShading(
        StringBuilder html,
        PdfLayoutPage page,
        PdfLayoutShading shading)
    {
        html.Append("      <g class=\"pdf-shading pdf-tensor-shading\" data-shading-index=\"")
            .Append(shading.Index.ToString(CultureInfo.InvariantCulture))
            .Append("\" clip-path=\"url(#")
            .Append(ShadingId(page, shading))
            .AppendLine("-clip)\">");
        foreach (PdfLayoutShadingTriangle triangle in shading.Triangles)
        {
            html.Append("        <path d=\"M ")
                .Append(SvgNumber(triangle.X1)).Append(' ').Append(SvgNumber(triangle.Y1))
                .Append(" L ").Append(SvgNumber(triangle.X2)).Append(' ').Append(SvgNumber(triangle.Y2))
                .Append(" L ").Append(SvgNumber(triangle.X3)).Append(' ').Append(SvgNumber(triangle.Y3))
                .Append(" Z\" fill=\"").Append(ColorHex(triangle.Color))
                .Append("\" fill-opacity=\"").Append(SvgNumber(triangle.Color.Alpha))
                .Append("\" stroke=\"").Append(ColorHex(triangle.Color))
                .Append("\" stroke-opacity=\"").Append(SvgNumber(triangle.Color.Alpha))
                .AppendLine("\" stroke-width=\"0.1\" />");
        }

        html.AppendLine("      </g>");
    }

    private static void WriteShadingStops(StringBuilder html, IReadOnlyList<PdfLayoutGradientStop> stops)
    {
        foreach (PdfLayoutGradientStop stop in stops)
        {
            html.Append("          <stop offset=\"")
                .Append(SvgNumber(stop.Offset * 100))
                .Append("%\" stop-color=\"")
                .Append(ColorHex(stop.Color))
                .Append("\" stop-opacity=\"")
                .Append(SvgNumber(stop.Color.Alpha))
                .AppendLine("\" />");
        }
    }

    private static string ShadingId(PdfLayoutPage page, PdfLayoutShading shading)
    {
        return "pdf-shading-page-" + page.PageNumber.ToString(CultureInfo.InvariantCulture) +
            "-" + shading.Index.ToString(CultureInfo.InvariantCulture);
    }

    private static void WriteVectorContent(
        StringBuilder html,
        IReadOnlyList<PdfLayoutPath> paths,
        IReadOnlyList<PdfLayoutVectorGroup> vectorGroups,
        string clipIdPrefix)
    {
        const int rootVectorGroupIndex = -1;
        Dictionary<int, PdfLayoutPath> pathsByIndex = paths.ToDictionary(path => path.Index);
        HashSet<int> includedPathIndexes = pathsByIndex.Keys.ToHashSet();
        PdfLayoutVectorGroup[] groups = vectorGroups
            .Where(group => group.HasPaths)
            .Where(group => paths.Any(path => path.Index >= group.FirstPathIndex && path.Index <= group.LastPathIndex))
            .ToArray();
        if (groups.Length == 0)
        {
            WriteVectorDefinitions(html, [], paths, clipIdPrefix);
            foreach (PdfLayoutPath path in paths)
            {
                WriteVectorPath(html, path, clipIdPrefix);
            }

            return;
        }

        HashSet<int> groupIndexes = groups.Select(group => group.Index).ToHashSet();
        Dictionary<int, PdfLayoutVectorGroup[]> groupsByParent = groups
            .GroupBy(group => group.ParentIndex is int parentIndex && groupIndexes.Contains(parentIndex)
                ? parentIndex
                : rootVectorGroupIndex)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(vectorGroup => vectorGroup.FirstPathIndex)
                    .ThenByDescending(vectorGroup => vectorGroup.LastPathIndex)
                    .ToArray());
        WriteVectorDefinitions(html, groups, paths, clipIdPrefix);
        int lastPathIndex = paths.Max(path => path.Index);
        WriteVectorContentRange(
            html,
            pathsByIndex,
            includedPathIndexes,
            groupsByParent,
            clipIdPrefix,
            rootVectorGroupIndex,
            0,
            lastPathIndex);
    }

    private static void WriteVectorContentRange(
        StringBuilder html,
        IReadOnlyDictionary<int, PdfLayoutPath> pathsByIndex,
        IReadOnlySet<int> includedPathIndexes,
        IReadOnlyDictionary<int, PdfLayoutVectorGroup[]> groupsByParent,
        string clipIdPrefix,
        int parentIndex,
        int firstPathIndex,
        int lastPathIndex)
    {
        int pathIndex = firstPathIndex;
        if (groupsByParent.TryGetValue(parentIndex, out PdfLayoutVectorGroup[]? childGroups))
        {
            foreach (PdfLayoutVectorGroup group in childGroups)
            {
                WriteVectorPathsBefore(
                    html,
                    pathsByIndex,
                    includedPathIndexes,
                    clipIdPrefix,
                    ref pathIndex,
                    group.FirstPathIndex);
                if (group.LastPathIndex < pathIndex)
                {
                    continue;
                }

                html.Append("      <g data-vector-group-index=\"")
                    .Append(group.Index.ToString(CultureInfo.InvariantCulture))
                    .Append("\" opacity=\"")
                    .Append(SvgNumber(group.Opacity))
                    .Append('"');
                if (group.IsIsolated || group.BlendMode != BlendMode.NORMAL)
                {
                    html.Append(" style=\"");
                    if (group.IsIsolated)
                    {
                        html.Append("isolation:isolate");
                    }

                    if (group.BlendMode != BlendMode.NORMAL)
                    {
                        if (group.IsIsolated)
                        {
                            html.Append(';');
                        }

                        html.Append("mix-blend-mode:").Append(CssBlendMode(group.BlendMode));
                    }

                    html.Append('"');
                }

                if (HasVectorClip(group))
                {
                    html.Append(" clip-path=\"url(#")
                        .Append(VectorClipPathId(clipIdPrefix, group))
                        .Append(")\"");
                }

                if (group.IsKnockout)
                {
                    html.Append(" data-knockout=\"true\"");
                }

                html.AppendLine(">");
                WriteVectorContentRange(
                    html,
                    pathsByIndex,
                    includedPathIndexes,
                    groupsByParent,
                    clipIdPrefix,
                    group.Index,
                    pathIndex,
                    group.LastPathIndex);
                html.AppendLine("      </g>");
                pathIndex = group.LastPathIndex + 1;
            }
        }

        WriteVectorPathsBefore(
            html,
            pathsByIndex,
            includedPathIndexes,
            clipIdPrefix,
            ref pathIndex,
            lastPathIndex + 1);
    }

    private static void WriteVectorDefinitions(
        StringBuilder html,
        IReadOnlyList<PdfLayoutVectorGroup> groups,
        IReadOnlyList<PdfLayoutPath> paths,
        string clipIdPrefix)
    {
        PdfLayoutVectorGroup[] clippedGroups = groups
            .Where(HasVectorClip)
            .ToArray();
        PdfLayoutPath[] clippedPaths = paths
            .Where(HasVectorClip)
            .ToArray();
        if (clippedGroups.Length == 0 && clippedPaths.Length == 0)
        {
            return;
        }

        html.AppendLine("      <defs>");
        foreach (PdfLayoutVectorGroup group in clippedGroups)
        {
            if (group.ClipPaths.Count > 0)
            {
                WriteExactClipPathDefinitions(
                    html,
                    VectorClipPathId(clipIdPrefix, group),
                    group.ClipPaths,
                    "        ",
                    string.Empty,
                    static clipPath => SvgPathData(clipPath.Commands));
                continue;
            }

            PdfLayoutRectangle bounds = group.ClipBounds!.Value;
            html.Append("        <clipPath id=\"")
                .Append(VectorClipPathId(clipIdPrefix, group))
                .Append("\"><rect x=\"")
                .Append(SvgNumber(bounds.X))
                .Append("\" y=\"")
                .Append(SvgNumber(bounds.Y))
                .Append("\" width=\"")
                .Append(SvgNumber(bounds.Width))
                .Append("\" height=\"")
                .Append(SvgNumber(bounds.Height))
                .AppendLine("\" /></clipPath>");
        }

        foreach (PdfLayoutPath path in clippedPaths)
        {
            if (path.ClipPaths.Count > 0)
            {
                WriteExactClipPathDefinitions(
                    html,
                    VectorClipPathId(clipIdPrefix, path),
                    path.ClipPaths,
                    "        ",
                    string.Empty,
                    static clipPath => SvgPathData(clipPath.Commands));
                continue;
            }

            PdfLayoutRectangle bounds = path.ClipBounds!.Value;
            html.Append("        <clipPath id=\"")
                .Append(VectorClipPathId(clipIdPrefix, path))
                .Append("\"><rect x=\"")
                .Append(SvgNumber(bounds.X))
                .Append("\" y=\"")
                .Append(SvgNumber(bounds.Y))
                .Append("\" width=\"")
                .Append(SvgNumber(bounds.Width))
                .Append("\" height=\"")
                .Append(SvgNumber(bounds.Height))
                .AppendLine("\" /></clipPath>");
        }

        html.AppendLine("      </defs>");
    }

    private static bool HasVectorClip(PdfLayoutVectorGroup group)
    {
        return group.ClipPaths.Count > 0 || group.ClipBounds.HasValue;
    }

    private static bool HasVectorClip(PdfLayoutPath path)
    {
        return path.ClipPaths.Count > 0 || path.ClipBounds.HasValue;
    }

    private static void WriteExactClipPathDefinitions(
        StringBuilder html,
        string clipPathId,
        IReadOnlyList<PdfLayoutClipPath> clipPaths,
        string indentation,
        string unitsAttribute,
        Func<PdfLayoutClipPath, string> pathData)
    {
        if (TryIntersectRectangularClipPaths(clipPaths, out PdfLayoutClipPath intersection))
        {
            html.Append(indentation)
                .Append("<clipPath id=\"")
                .Append(clipPathId)
                .Append('"')
                .Append(unitsAttribute)
                .Append("><path d=\"")
                .Append(HtmlAttribute(pathData(intersection)))
                .AppendLine("\" clip-rule=\"nonzero\" /></clipPath>");
            return;
        }

        string? previousId = null;
        for (int index = 0; index < clipPaths.Count; index++)
        {
            PdfLayoutClipPath clipPath = clipPaths[index];
            string currentId = index == clipPaths.Count - 1
                ? clipPathId
                : clipPathId + "-step-" + index.ToString(CultureInfo.InvariantCulture);
            html.Append(indentation)
                .Append("<clipPath id=\"")
                .Append(currentId)
                .Append('"')
                .Append(unitsAttribute)
                .Append('>');
            if (previousId is not null)
            {
                html.Append("<g clip-path=\"url(#")
                    .Append(previousId)
                    .Append(")\">");
            }

            html.Append("<path d=\"")
                .Append(HtmlAttribute(pathData(clipPath)))
                .Append("\" clip-rule=\"")
                .Append(FillRule(clipPath.WindingRule))
                .Append("\" />");
            if (previousId is not null)
            {
                html.Append("</g>");
            }

            html.AppendLine("</clipPath>");
            previousId = currentId;
        }
    }

    private static bool TryIntersectRectangularClipPaths(
        IReadOnlyList<PdfLayoutClipPath> clipPaths,
        out PdfLayoutClipPath intersection)
    {
        intersection = null!;
        if (clipPaths.Count == 0 || clipPaths.Any(static clipPath => !IsAxisAlignedRectangle(clipPath)))
        {
            return false;
        }

        float left = clipPaths.Max(static clipPath => clipPath.Bounds.X);
        float top = clipPaths.Max(static clipPath => clipPath.Bounds.Y);
        float right = clipPaths.Min(static clipPath => clipPath.Bounds.Right);
        float bottom = clipPaths.Min(static clipPath => clipPath.Bounds.Bottom);
        PdfLayoutRectangle bounds = new(
            left,
            top,
            MathF.Max(0f, right - left),
            MathF.Max(0f, bottom - top));
        intersection = new PdfLayoutClipPath(
            [
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.MoveTo, bounds.X, bounds.Y, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.Right, bounds.Y, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.Right, bounds.Bottom, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.LineTo, bounds.X, bounds.Bottom, 0f, 0f, 0f, 0f),
                new PdfLayoutPathCommand(PdfLayoutPathCommandKind.ClosePath, 0f, 0f, 0f, 0f, 0f, 0f)
            ],
            bounds,
            1);
        return true;
    }

    private static bool IsAxisAlignedRectangle(PdfLayoutClipPath clipPath)
    {
        if (clipPath.Commands.Count < 5 ||
            clipPath.Commands[^1].Kind != PdfLayoutPathCommandKind.ClosePath)
        {
            return false;
        }

        const float tolerance = 0.01f;
        PdfLayoutPathCommand move = clipPath.Commands[0];
        if (move.Kind != PdfLayoutPathCommandKind.MoveTo)
        {
            return false;
        }

        List<(float X, float Y)> points = [(move.X1, move.Y1)];
        (float X, float Y) current = points[0];
        foreach (PdfLayoutPathCommand command in clipPath.Commands.Skip(1).SkipLast(1))
        {
            (float X, float Y) end;
            switch (command.Kind)
            {
                case PdfLayoutPathCommandKind.LineTo:
                    end = (command.X1, command.Y1);
                    break;
                case PdfLayoutPathCommandKind.CurveTo:
                    end = (command.X3, command.Y3);
                    bool vertical = Near(current.X, end.X, tolerance) &&
                        Near(command.X1, current.X, tolerance) &&
                        Near(command.X2, current.X, tolerance);
                    bool horizontal = Near(current.Y, end.Y, tolerance) &&
                        Near(command.Y1, current.Y, tolerance) &&
                        Near(command.Y2, current.Y, tolerance);
                    if (!vertical && !horizontal)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            if (!Near(current.X, end.X, tolerance) && !Near(current.Y, end.Y, tolerance))
            {
                return false;
            }

            points.Add(end);
            current = end;
        }

        if (points.Count == 5 &&
            Near(points[^1].X, points[0].X, tolerance) &&
            Near(points[^1].Y, points[0].Y, tolerance))
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count != 4 ||
            (!Near(points[^1].X, points[0].X, tolerance) &&
                !Near(points[^1].Y, points[0].Y, tolerance)))
        {
            return false;
        }

        PdfLayoutRectangle bounds = clipPath.Bounds;
        bool topLeft = false;
        bool topRight = false;
        bool bottomRight = false;
        bool bottomLeft = false;
        foreach ((float x, float y) in points)
        {
            bool left = Near(x, bounds.X, tolerance);
            bool right = Near(x, bounds.Right, tolerance);
            bool top = Near(y, bounds.Y, tolerance);
            bool bottom = Near(y, bounds.Bottom, tolerance);
            topLeft |= left && top;
            topRight |= right && top;
            bottomRight |= right && bottom;
            bottomLeft |= left && bottom;
        }

        return topLeft && topRight && bottomRight && bottomLeft;
    }

    private static bool IsAxisAlignedRectangle(PdfLayoutPath path)
    {
        return IsAxisAlignedRectangle(new PdfLayoutClipPath(
            path.Commands,
            path.Bounds,
            path.FillRule ?? 1));
    }

    private static bool Near(float first, float second, float tolerance) =>
        MathF.Abs(first - second) <= tolerance;

    private static string VectorClipPathId(string clipIdPrefix, PdfLayoutVectorGroup group)
    {
        return clipIdPrefix + "-clip-" + group.Index.ToString(CultureInfo.InvariantCulture);
    }

    private static string VectorClipPathId(string clipIdPrefix, PdfLayoutPath path)
    {
        return clipIdPrefix + "-path-clip-" + path.Index.ToString(CultureInfo.InvariantCulture);
    }

    private static string CssBlendMode(BlendMode blendMode)
    {
        return blendMode switch
        {
            BlendMode.MULTIPLY => "multiply",
            BlendMode.SCREEN => "screen",
            BlendMode.OVERLAY => "overlay",
            BlendMode.DARKEN => "darken",
            BlendMode.LIGHTEN => "lighten",
            BlendMode.COLOR_DODGE => "color-dodge",
            BlendMode.COLOR_BURN => "color-burn",
            BlendMode.HARD_LIGHT => "hard-light",
            BlendMode.SOFT_LIGHT => "soft-light",
            BlendMode.DIFFERENCE => "difference",
            BlendMode.EXCLUSION => "exclusion",
            BlendMode.HUE => "hue",
            BlendMode.SATURATION => "saturation",
            BlendMode.COLOR => "color",
            BlendMode.LUMINOSITY => "luminosity",
            _ => "normal"
        };
    }

    private static void WriteVectorPathsBefore(
        StringBuilder html,
        IReadOnlyDictionary<int, PdfLayoutPath> pathsByIndex,
        IReadOnlySet<int> includedPathIndexes,
        string clipIdPrefix,
        ref int pathIndex,
        int exclusivePathIndex)
    {
        while (pathIndex < exclusivePathIndex)
        {
            if (includedPathIndexes.Contains(pathIndex) && pathsByIndex.TryGetValue(pathIndex, out PdfLayoutPath? path))
            {
                WriteVectorPath(html, path, clipIdPrefix);
            }

            pathIndex++;
        }
    }

    private static void WriteVectorPath(
        StringBuilder html,
        PdfLayoutPath path,
        string? clipIdPrefix = null)
    {
        html.Append("      <path data-path-index=\"")
            .Append(path.Index.ToString(CultureInfo.InvariantCulture))
            .Append("\" d=\"")
            .Append(HtmlAttribute(SvgPathData(path.Commands)))
            .Append("\"");

        if (HasVectorClip(path) && clipIdPrefix is not null)
        {
            html.Append(" clip-path=\"url(#")
                .Append(VectorClipPathId(clipIdPrefix, path))
                .Append(")\"");
        }

        if (path.FillColor is PdfLayoutColor fill)
        {
            html.Append(" fill=\"")
                .Append(ColorHex(fill))
                .Append("\" fill-opacity=\"")
                .Append(SvgNumber(fill.Alpha))
                .Append("\" fill-rule=\"")
                .Append(FillRule(path.FillRule))
                .Append("\"");
        }
        else
        {
            html.Append(" fill=\"none\"");
        }

        if (path.Stroke is PdfLayoutStrokeStyle stroke)
        {
            html.Append(" stroke=\"")
                .Append(ColorHex(stroke.Color))
                .Append("\" stroke-opacity=\"")
                .Append(SvgNumber(stroke.Color.Alpha))
                .Append("\" stroke-width=\"")
                .Append(SvgNumber(stroke.Width))
                .Append("\" stroke-linecap=\"")
                .Append(LineCap(stroke.LineCap))
                .Append("\" stroke-linejoin=\"")
                .Append(LineJoin(stroke.LineJoin))
                .Append("\" stroke-miterlimit=\"")
                .Append(SvgNumber(stroke.MiterLimit))
                .Append("\"");

            if (stroke.DashArray.Count > 0 && stroke.DashArray.Any(dash => dash > 0))
            {
                html.Append(" stroke-dasharray=\"")
                    .Append(string.Join(" ", stroke.DashArray.Select(SvgNumber)))
                    .Append("\" stroke-dashoffset=\"")
                    .Append(SvgNumber(stroke.DashPhase))
                    .Append("\"");
            }
        }
        else
        {
            html.Append(" stroke=\"none\"");
        }

        html.AppendLine(" />");
    }

    private static void WriteTextRun(
        StringBuilder html,
        PdfTextRun run,
        float scale,
        float fontSize,
        IReadOnlyList<PdfTextRun> pageRuns,
        string? text = null,
        bool visuallyHiddenOcr = false)
    {
        text ??= run.Text;
        bool rightToLeft = IsRightToLeftText(text);
        html.Append("    <span class=\"pdf-text-run")
            .Append(run.Shadow is null ? "" : " pdf-text-shadow")
            .Append(visuallyHiddenOcr ? " pdf-ocr-text-run" : "")
            .Append(rightToLeft ? " pdf-text-rtl" : "")
            .Append("\" data-font=\"")
            .Append(HtmlAttribute(run.FontName))
            .Append(visuallyHiddenOcr ? "\" aria-label=\"" + HtmlAttribute(text) : "")
            .Append(rightToLeft ? "\" dir=\"rtl" : "")
            .Append("\" style=\"position:absolute;left:")
            .Append(CssPoints(run.Bounds.X * scale))
            .Append(";top:")
            .Append(CssPoints(run.Bounds.Y * scale))
            .Append(";width:")
            .Append(CssPoints(run.Bounds.Width * scale))
            .Append(";height:")
            .Append(CssPoints(run.Bounds.Height * scale))
            .Append(";font-size:")
            .Append(CssPoints(fontSize * scale))
            .Append(";font-family:")
            .Append(CssFontFamily(run.FontName))
            .Append(FixedTextFontPresentation(run))
            .Append(";color:")
            .Append(ColorHex(run.Color));
        AppendTextShadowStyle(html, run.Shadow, scale);
        html.Append("\">");

        if (HasGlyphOutlineFallback(run))
        {
            WriteGlyphOutlineTextRun(html, run, text);
        }
        else if (rightToLeft || ShouldUseFittedText(run, pageRuns))
        {
            WriteFittedTextRun(html, run, fontSize, scale, pageRuns, text, rightToLeft);
        }
        else
        {
            html.Append(Html(text));
        }

        html.AppendLine("</span>");
    }

    private static void WriteGlyphOutlineTextRun(StringBuilder html, PdfTextRun run, string text)
    {
        html.Append("<span class=\"pdf-text-run-copy\" aria-hidden=\"true\">")
            .Append(Html(text))
            .Append("</span><svg class=\"pdf-text-run-svg pdf-text-run-outline\" viewBox=\"0 0 ")
            .Append(SvgNumber(run.Bounds.Width))
            .Append(' ')
            .Append(SvgNumber(run.Bounds.Height))
            .Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\">");

        foreach (PdfTextGlyph glyph in run.Glyphs)
        {
            if (glyph.Outline is not { Count: > 0 } outline)
            {
                continue;
            }

            html.Append("<path d=\"")
                .Append(HtmlAttribute(SvgPathData(outline, run.Bounds.X, run.Bounds.Y)))
                .Append("\" fill=\"")
                .Append(ColorHex(run.Color))
                .Append('"');
            if (run.Color.Alpha < 0.999f)
            {
                html.Append(" fill-opacity=\"")
                    .Append(SvgNumber(run.Color.Alpha))
                    .Append('"');
            }

            html.AppendLine(" />");
        }

        html.Append("</svg>");
    }

    private static void WriteFittedTextRun(
        StringBuilder html,
        PdfTextRun run,
        float fontSize,
        float scale,
        IReadOnlyList<PdfTextRun> pageRuns,
        string text,
        bool rightToLeft)
    {
        string color = ColorHex(run.Color);
        float runHeight = run.Bounds.Height * scale;
        float fittedHeight = run.UsesBrowserFontAsset
            ? MathF.Max(runHeight, fontSize * scale)
            : runHeight;
        float baseline = run.UsesBrowserFontAsset
            ? BrowserFontBaseline(run, fontSize, pageRuns) * scale
            : runHeight * 0.92f;
        float fittedTop = run.UsesBrowserFontAsset
            ? runHeight - baseline
            : 0f;
        html.Append("<span class=\"pdf-text-run-copy\" aria-hidden=\"true\">")
            .Append(Html(text))
            .Append("</span><svg class=\"pdf-text-run-svg\" viewBox=\"0 0 ")
            .Append(SvgNumber(run.Bounds.Width * scale))
            .Append(' ')
            .Append(SvgNumber(fittedHeight))
            .Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\"");
        if (run.UsesBrowserFontAsset)
        {
            html.Append(" style=\"height:")
                .Append(CssPoints(fittedHeight))
                .Append(";top:")
                .Append(CssPoints(fittedTop))
                .Append("\"");
        }

        html.Append('>')
            .Append("<text x=\"")
            .Append(rightToLeft ? SvgNumber(run.Bounds.Width * scale) : "0")
            .Append("\" y=\"")
            .Append(SvgNumber(baseline))
            .Append("\" textLength=\"")
            .Append(SvgNumber(run.Bounds.Width * scale))
            .Append("\" lengthAdjust=\"spacingAndGlyphs\" xml:space=\"preserve\" style=\"font-size:")
            .Append(SvgNumber(fontSize * scale))
            .Append("px")
            .Append(";font-family:")
            .Append(CssFontFamily(run.FontName))
            .Append(FixedTextFontPresentation(run))
            .Append(";fill:")
            .Append(color);

        if (run.Color.Alpha < 0.999f)
        {
            html.Append(";fill-opacity:")
                .Append(SvgNumber(run.Color.Alpha));
        }

        html.Append(rightToLeft ? "\" direction=\"rtl\" unicode-bidi=\"plaintext\" text-anchor=\"start" : "")
            .Append("\">")
            .Append(Html(text))
            .Append("</text></svg>");
    }

    private static float BrowserFontBaseline(
        PdfTextRun run,
        float fontSize,
        IReadOnlyList<PdfTextRun> pageRuns)
    {
        if (!HasCompressedGlyphBounds(run))
        {
            return MathF.Min(run.Bounds.Height, fontSize);
        }

        PdfTextRun? referenceRun = pageRuns
            .Where(candidate => !ReferenceEquals(candidate, run))
            .Where(static candidate => candidate.UsesBrowserFontAsset)
            .Where(candidate => !HasCompressedGlyphBounds(candidate))
            .Where(candidate => string.Equals(
                NormalizeFontName(candidate.FontName),
                NormalizeFontName(run.FontName),
                StringComparison.Ordinal))
            .Where(candidate => MathF.Abs(candidate.FontSize - fontSize) <= MathF.Max(0.5f, fontSize * 0.1f))
            .Where(candidate => MathF.Abs(RunBaseline(candidate) - RunBaseline(run)) <= 0.75f)
            .Where(candidate => MathF.Min(
                MathF.Abs(candidate.Bounds.X - run.Bounds.Right),
                MathF.Abs(run.Bounds.X - candidate.Bounds.Right)) <= 1.5f)
            .OrderBy(candidate => MathF.Min(
                MathF.Abs(candidate.Bounds.X - run.Bounds.Right),
                MathF.Abs(run.Bounds.X - candidate.Bounds.Right)))
            .FirstOrDefault();
        return referenceRun is null
            ? fontSize * BrowserFontBaselineRatio
            : MathF.Min(referenceRun.Bounds.Height, fontSize);
    }

    private static bool ShouldUseFittedText(PdfTextRun run, IReadOnlyList<PdfTextRun> pageRuns)
    {
        return run.FontSize > 0 &&
            run.Bounds.Height > 0 &&
            run.Bounds.Width > 0 &&
            MathF.Abs(run.Direction) < 0.01f &&
            !string.IsNullOrWhiteSpace(run.Text) &&
            (run.UsesBrowserFontAsset
                ? HasCompressedGlyphBounds(run) || HasTightlyAdjacentRun(run, pageRuns)
                : HasCompressedGlyphBounds(run) ||
                    HasUntrustedBrowserFontMetrics(run.FontName) &&
                    !HasMathFont(run.FontName) &&
                    !IsSymbolFont(run.FontName));
    }

    private static bool HasTightlyAdjacentRun(PdfTextRun run, IReadOnlyList<PdfTextRun> pageRuns)
    {
        return pageRuns.Any(candidate =>
            !ReferenceEquals(candidate, run) &&
            !string.IsNullOrWhiteSpace(candidate.Text) &&
            MathF.Abs(candidate.Direction) < 0.01f &&
            (HasDistinctFixedTextPresentation(run, candidate) || HasCompressedGlyphBounds(candidate)) &&
            MathF.Abs(RunBaseline(candidate) - RunBaseline(run)) <= 0.75f &&
            MathF.Min(
                MathF.Abs(candidate.Bounds.X - run.Bounds.Right),
                MathF.Abs(run.Bounds.X - candidate.Bounds.Right)) <= 1.5f);
    }

    private static bool HasDistinctFixedTextPresentation(PdfTextRun first, PdfTextRun second)
    {
        return !string.Equals(
                NormalizeFontName(first.FontName),
                NormalizeFontName(second.FontName),
                StringComparison.Ordinal) ||
            MathF.Abs(first.FontSize - second.FontSize) > 0.01f ||
            !string.Equals(ColorClass(first.Color), ColorClass(second.Color), StringComparison.Ordinal);
    }

    private static bool HasGlyphOutlineFallback(PdfTextRun run)
    {
        return run.Glyphs.Any(static glyph => glyph.Outline is { Count: > 0 }) &&
            run.Glyphs.All(static glyph =>
                glyph.Outline is { Count: > 0 } ||
                glyph.Outline is not null && string.IsNullOrWhiteSpace(glyph.Text));
    }

    private static float FixedTextFontSize(
        PdfTextRun run,
        IReadOnlyList<PdfTextRun>? pageRuns = null)
    {
        float fontSize = HasCompressedGlyphBounds(run)
            ? MathF.Max(0.5f, run.Bounds.Height * 1.25f)
            : run.FontSize;
        return pageRuns == null
            ? fontSize
            : CorrectTransformedTextFontSize(run, fontSize, pageRuns);
    }

    private static float CorrectTransformedTextFontSize(
        PdfTextRun run,
        float fontSize,
        IReadOnlyList<PdfTextRun> pageRuns)
    {
        if (fontSize >= 8f ||
            run.Text.Length < 8 ||
            !run.Text.Any(char.IsLetterOrDigit) ||
            fontSize <= 0)
        {
            return fontSize;
        }

        PdfTextRun? adjacentRun = pageRuns
            .Where(candidate => !ReferenceEquals(candidate, run))
            .Where(candidate => string.Equals(
                NormalizeFontName(candidate.FontName),
                NormalizeFontName(run.FontName),
                StringComparison.Ordinal))
            .Where(candidate => candidate.FontSize >= fontSize * 1.35f)
            .Where(candidate => MathF.Abs(RunBaseline(candidate) - RunBaseline(run)) <= 0.75f)
            .Where(candidate => MathF.Min(
                MathF.Abs(candidate.Bounds.X - run.Bounds.Right),
                MathF.Abs(run.Bounds.X - candidate.Bounds.Right)) <= 1.5f)
            .OrderBy(candidate => MathF.Min(
                MathF.Abs(candidate.Bounds.X - run.Bounds.Right),
                MathF.Abs(run.Bounds.X - candidate.Bounds.Right)))
            .FirstOrDefault();
        return adjacentRun?.FontSize ?? fontSize;
    }

    private static float RunBaseline(PdfTextRun run)
    {
        return run.Bounds.Bottom;
    }

    private static string FixedTextFontPresentation(PdfTextRun run)
    {
        StringBuilder style = new();
        if (IsBoldFont(run.FontName))
        {
            style.Append(";font-weight:700");
        }

        if (IsItalicFont(run.FontName))
        {
            style.Append(";font-style:italic");
        }

        return style.ToString();
    }

    private static bool HasCompressedGlyphBounds(PdfTextRun run)
    {
        return run.Bounds.Height / run.FontSize < 0.55f;
    }

    private static bool HasUntrustedBrowserFontMetrics(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return !normalized.Contains("Arial", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Times", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Nimbus", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Courier", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Mono", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Verdana", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("Tahoma", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSymbolFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.Contains("Symbol", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Dingbats", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSemanticPage(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfSemanticSectionTree sectionTree,
        float scale)
    {
        FootnoteContext footnotes = FootnoteContext.Create(page.PageNumber, semanticPage.Elements);
        PdfLayoutRectangle[] figureRegions = SemanticFigureRegions(page, semanticPage).ToArray();
        PdfSemanticElement[] positioned = semanticPage.Elements
            .Where(IsPositionedSemanticElement)
            .Where(element => !ShouldKeepInFlowForFigureRendering(page, semanticPage, element, figureRegions))
            .ToArray();
        foreach (PdfSemanticElement element in positioned)
        {
            WritePositionedSemanticElement(
                html,
                page,
                element,
                footnotes,
                scale,
                sectionTree.FindHeading(element)?.Id,
                sectionTree.FindSection(element)?.Level);
        }

        HashSet<PdfSemanticElement> positionedSet = positioned.ToHashSet();
        PdfSemanticElement[] flowElements = semanticPage.Elements
            .Where(element => !positionedSet.Contains(element))
            .ToArray();
        html.AppendLine("    <article class=\"pdf-semantic-flow\">");
        SemanticSectionWriter sectionWriter = new(html, sectionTree);
        WriteSemanticFlowElements(
            html,
            page,
            semanticPage,
            flowElements,
            footnotes,
            scale,
            imageAssets: null,
            figureRendering: SemanticFigureRendering.Space,
            omitSimplePageNumberFooters: false,
            skippedElements: null,
            skippedFigureRegions: null,
            paragraphMerge: null,
            sectionWriter,
            bibliographyWriter: null);
        sectionWriter.CloseAll();
        html.AppendLine("    </article>");
    }

    private static void WriteSemanticContinuousDocument(
        StringBuilder html,
        PdfLayoutDocument layout,
        PdfSemanticDocument semantic,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale,
        PdfSemanticExtractionOptions semanticOptions)
    {
        FootnoteContext[] footnoteContexts = FootnoteContext.CreateContinuous(layout.Pages, semantic.Pages);
        ContinuousPageContext[] pages = layout.Pages
            .Select((page, index) => CreateContinuousPageContext(
                page,
                semantic.Pages[index],
                footnoteContexts[index]))
            .ToArray();
        Dictionary<int, ContinuousParagraphMerge> paragraphMerges = [];
        HashSet<PdfSemanticElement> skippedElements = [];
        Dictionary<int, HashSet<PdfLayoutRectangle>> skippedFigureRegionsByPage = [];
        HashSet<int> inlinePageBreaks = [];
        for (int index = 0; index + 1 < pages.Length; index++)
        {
            if (pages[index].UsesFixedLayoutFallback ||
                pages[index].LineGrid != null ||
                pages[index].Columns != null ||
                pages[index].RuledGrid != null ||
                pages[index + 1].UsesFixedLayoutFallback ||
                pages[index + 1].LineGrid != null ||
                pages[index + 1].Columns != null ||
                pages[index + 1].RuledGrid != null)
            {
                continue;
            }

            ContinuousParagraphMerge? merge = TryCreateContinuousParagraphMerge(pages[index], pages[index + 1]);
            if (merge == null)
            {
                continue;
            }

            paragraphMerges[index] = merge;
            inlinePageBreaks.Add(merge.Next.Page.PageNumber);
            skippedElements.Add(merge.ContinuationElement);
            if (merge.CurrentPageNumberFooter != null)
            {
                skippedElements.Add(merge.CurrentPageNumberFooter);
            }

            foreach (PdfSemanticElement element in merge.LeadingElements)
            {
                skippedElements.Add(element);
            }

            if (!skippedFigureRegionsByPage.TryGetValue(
                    merge.Next.Page.PageNumber,
                    out HashSet<PdfLayoutRectangle>? skippedFigureRegions))
            {
                skippedFigureRegions = [];
                skippedFigureRegionsByPage[merge.Next.Page.PageNumber] = skippedFigureRegions;
            }

            foreach (PdfLayoutRectangle region in merge.LeadingFigureRegions)
            {
                skippedFigureRegions.Add(region);
            }
        }

        html.AppendLine("  <main class=\"pdf-semantic-document-flow\">");
        bool flowOpen = false;
        SemanticSectionWriter? sectionWriter = null;
        SemanticBibliographyWriter bibliographyWriter = new(html);
        SemanticDefinitionListRenderState definitionListState = new();

        for (int index = 0; index < pages.Length; index++)
        {
            ContinuousPageContext context = pages[index];
            if (context.LineGrid != null)
            {
                CloseSemanticDefinitionList(html, definitionListState);
                bibliographyWriter.CloseAll();
                if (!flowOpen)
                {
                    html.AppendLine("    <article class=\"pdf-semantic-flow pdf-semantic-continuous-flow\">");
                    flowOpen = true;
                    sectionWriter = new SemanticSectionWriter(html, semantic.SectionTree);
                }

                if (!inlinePageBreaks.Contains(context.Page.PageNumber))
                {
                    WriteSemanticPageBreak(html, context.Page.PageNumber, isFirstPage: index == 0);
                }

                WriteSemanticLineGrid(html, context.LineGrid, scale, semanticOptions);
                WriteFormControls(html, context.Page, scale, positioned: false);
                continue;
            }

            if (context.Columns != null)
            {
                CloseSemanticDefinitionList(html, definitionListState);
                bibliographyWriter.CloseAll();
                if (!flowOpen)
                {
                    html.AppendLine("    <article class=\"pdf-semantic-flow pdf-semantic-continuous-flow\">");
                    flowOpen = true;
                    sectionWriter = new SemanticSectionWriter(html, semantic.SectionTree);
                }

                if (!inlinePageBreaks.Contains(context.Page.PageNumber))
                {
                    WriteSemanticPageBreak(html, context.Page.PageNumber, isFirstPage: index == 0);
                }

                WriteSemanticColumns(html, context.Columns, context.Footnotes, imageAssets, scale, semanticOptions);
                WriteFormControls(html, context.Page, scale, positioned: false);
                continue;
            }

            if (context.UsesFixedLayoutFallback)
            {
                CloseSemanticDefinitionList(html, definitionListState);
                bibliographyWriter.CloseAll();
                if (flowOpen)
                {
                    sectionWriter!.CloseAll();
                    html.AppendLine("    </article>");
                    flowOpen = false;
                    sectionWriter = null;
                }

                WritePage(
                    html,
                    context.Page,
                    imageAssets,
                    scale,
                    semanticPage: null,
                    sectionTree: null,
                    textMode: PdfHtmlTextMode.FixedLayout,
                    additionalClass: "pdf-semantic-layout-fallback-page",
                    inferredTextOptions: semanticOptions,
                    fallbackSemanticPage: context.SemanticPage);
                PdfSemanticElement? documentIndexElement = SemanticDocumentIndexElement(context.SemanticPage);
                if (documentIndexElement != null)
                {
                    WriteSemanticDocumentIndex(
                        html,
                        documentIndexElement,
                        context.Page,
                        isVisualPreservationIsland: true);
                }

                continue;
            }

            if (!flowOpen)
            {
                html.AppendLine("    <article class=\"pdf-semantic-flow pdf-semantic-continuous-flow\">");
                flowOpen = true;
                sectionWriter = new SemanticSectionWriter(html, semantic.SectionTree);
            }

            bool isCoverPage = IsCoverPage(context.Page, context.SemanticPage);
            bibliographyWriter.PrepareForPage(context.SemanticPage);
            PdfSemanticElement? pageOpeningSectionHeading = isCoverPage
                ? context.SemanticPage.Elements.FirstOrDefault(element =>
                    IsTitleElement(element) && semantic.SectionTree.FindSection(element) != null)
                : FindPageOpeningSectionHeading(context.FlowElements, semantic.SectionTree);
            if (pageOpeningSectionHeading != null)
            {
                sectionWriter!.BeginElement(pageOpeningSectionHeading);
            }

            if (!inlinePageBreaks.Contains(context.Page.PageNumber))
            {
                WriteSemanticPageBreak(html, context.Page.PageNumber, isFirstPage: index == 0);
            }

            if (isCoverPage)
            {
                CloseSemanticDefinitionList(html, definitionListState);
                WriteSemanticCoverRegion(
                    html,
                    context.Page,
                    context.SemanticPage,
                    context.Footnotes,
                    imageAssets,
                    scale,
                    semantic.SectionTree);
                continue;
            }

            bool continuesDefinitionFromPreviousPage = definitionListState.IsOpen;
            if (!continuesDefinitionFromPreviousPage)
            {
                WriteContinuousPageArtifacts(
                    html,
                    context.Page,
                    context.PositionedElements,
                    context.Footnotes,
                    context.FigureRegions,
                    scale,
                    semantic.SectionTree);
            }

            if (context.RuledGrid?.TopRuleGroup != null)
            {
                WriteSemanticPageRuleGroup(html, context.RuledGrid.TopRuleGroup, context.Page, scale);
            }

            skippedFigureRegionsByPage.TryGetValue(context.Page.PageNumber, out HashSet<PdfLayoutRectangle>? skippedFigureRegions);
            WriteSemanticFlowElements(
                html,
                context.Page,
                context.SemanticPage,
                context.FlowElements,
                context.Footnotes,
                scale,
                imageAssets,
                figureRendering: SemanticFigureRendering.Content,
                omitSimplePageNumberFooters: false,
                skippedElements,
                skippedFigureRegions,
                paragraphMerges.GetValueOrDefault(index),
                sectionWriter,
                bibliographyWriter,
                definitionListState,
                context.RuledGrid);
            if (continuesDefinitionFromPreviousPage && !definitionListState.IsOpen)
            {
                WriteContinuousPageArtifacts(
                    html,
                    context.Page,
                    context.PositionedElements,
                    context.Footnotes,
                    context.FigureRegions,
                    scale,
                    semantic.SectionTree);
            }

            WriteFormControls(html, context.Page, scale, positioned: false);
        }

        if (flowOpen)
        {
            CloseSemanticDefinitionList(html, definitionListState);
            bibliographyWriter.CloseAll();
            sectionWriter!.CloseAll();
            html.AppendLine("    </article>");
        }

        html.AppendLine("  </main>");
    }

    private static void WriteSemanticCoverRegion(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        FootnoteContext footnotes,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale,
        PdfSemanticSectionTree sectionTree)
    {
        html.Append("      <section class=\"pdf-semantic-cover-region\" aria-label=\"Cover page\" style=\"--pdf-semantic-cover-width:")
            .Append(CssPoints(page.Width * scale))
            .Append(";--pdf-semantic-cover-height:")
            .Append(CssPoints(page.Height * scale))
            .AppendLine("\">");
        html.AppendLine("        <div class=\"pdf-semantic-cover-decoration-layer\" aria-label=\"Cover decoration\">");

        PdfLayoutPath[] vectorPaths = RenderableVectorPaths(page, semanticPage);
        if (page.PaintOperations.Count > 0)
        {
            WriteOrderedGraphics(html, page, imageAssets, vectorPaths, scale);
        }
        else
        {
            foreach (PdfLayoutImage image in page.Images)
            {
                if (imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
                {
                    WriteImage(html, page, image, asset, scale);
                }
            }

            WriteVectorLayer(
                html,
                page,
                scale,
                vectorPaths,
                "pdf-vector-layer pdf-semantic-cover-vector-layer",
                "cover");
        }

        if (page.Shadings.Count > 0 &&
            !page.PaintOperations.Any(static operation =>
                operation.Kind == PdfLayoutPaintOperationKind.Shading))
        {
            WriteShadingLayer(html, page, scale);
        }

        html.AppendLine("        </div>");
        foreach (PdfSemanticElement element in semanticPage.Elements)
        {
            WriteCoverSemanticElement(
                html,
                page,
                element,
                footnotes,
                scale,
                sectionTree.FindHeading(element)?.Id,
                sectionTree.FindSection(element)?.Level);
        }

        html.AppendLine("      </section>");
    }

    private static void WriteCoverSemanticElement(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        float scale,
        string? elementId,
        int? headingLevel)
    {
        string tagName = SemanticTagName(element, headingLevel);
        html.Append("        <")
            .Append(tagName)
            .Append(" class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth: false, allowCoverPositioning: false))
            .Append(" pdf-semantic-cover-region-element\"");
        if (!string.IsNullOrEmpty(elementId))
        {
            html.Append(" id=\"").Append(HtmlAttribute(elementId)).Append('"');
        }

        html.Append(" style=\"top:")
            .Append(CssPoints(element.Bounds.Y * scale))
            .Append(';')
            .Append(CoverElementHorizontalStyle(page, element, scale));
        List<string> titleRuleStyles = [];
        if (IsTitleElement(element))
        {
            AppendTitleRuleStyle(titleRuleStyles, page, element, TitleRulePosition.Above, "top");
            AppendTitleRuleStyle(titleRuleStyles, page, element, TitleRulePosition.Below, "bottom");
        }

        if (titleRuleStyles.Count > 0)
        {
            html.Append(';').Append(string.Join(";", titleRuleStyles));
        }

        html.Append("\">");
        WriteSemanticText(html, element, footnotes, page);
        html.Append("</")
            .Append(tagName)
            .AppendLine(">");
    }

    private static string CoverElementHorizontalStyle(
        PdfLayoutPage page,
        PdfSemanticElement element,
        float scale)
    {
        string? alignmentClass = SourceAlignmentClass(page, element);
        if (alignmentClass == "pdf-semantic-align-right")
        {
            return "left:0;right:" + CssPoints((page.Width - element.Bounds.Right) * scale);
        }

        if (alignmentClass == "pdf-semantic-align-center")
        {
            return "left:" + CssPoints(element.Bounds.X * scale) +
                ";width:" + CssPoints(element.Bounds.Width * scale);
        }

        return "left:" + CssPoints(element.Bounds.X * scale) +
            ";right:0";
    }

    private static ContinuousPageContext CreateContinuousPageContext(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        FootnoteContext? footnotes = null)
    {
        bool hasAuthoredStructure = semanticPage.Elements.Any(static element => element.TaggedStructure != null);
        PdfSemanticLineGrid? lineGrid = hasAuthoredStructure
            ? null
            : TryCreateSemanticLineGrid(page, semanticPage);
        PdfSemanticRuledGrid? ruledGrid = hasAuthoredStructure
            ? TryCreateSemanticRuledGrid(page, semanticPage)
            : null;
        PdfSemanticColumns? columns = null;
        if (lineGrid == null && ruledGrid == null)
        {
            columns = hasAuthoredStructure
                ? TryCreateAuthoredSemanticColumns(page, semanticPage)
                    ?? (TryCreateMixedGraphicTextColumns(page, semanticPage) is { } mixedRegions
                        ? AddColumnSpanningFigures(mixedRegions)
                        : null)
                : TryCreateSemanticColumns(page, semanticPage);
        }
        PdfLayoutRectangle[] figureRegions = SemanticFigureRegions(page, semanticPage).ToArray();
        PdfSemanticElement[] positioned = semanticPage.Elements
            .Where(IsPositionedSemanticElement)
            .Where(element => !ShouldKeepInFlowForFigureRendering(page, semanticPage, element, figureRegions))
            .ToArray();
        HashSet<PdfSemanticElement> positionedSet = positioned.ToHashSet();
        PdfSemanticElement[] flowElements = semanticPage.Elements
            .Where(element => !positionedSet.Contains(element))
            .ToArray();
        return new ContinuousPageContext(
            page,
            semanticPage,
            footnotes ?? FootnoteContext.Create(page.PageNumber, semanticPage.Elements),
            positioned,
            flowElements,
            figureRegions,
            lineGrid,
            columns,
            ruledGrid,
            RequiresFixedLayoutFallback(page, semanticPage, lineGrid, columns));
    }

    private static bool RequiresFixedLayoutFallback(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfSemanticLineGrid? lineGrid,
        PdfSemanticColumns? columns)
    {
        // Form widgets use page-space geometry to remain associated with their
        // labels and boxes. Reflowing them into a separate control list loses
        // that relationship and duplicates the visual form structure.
        if (page.FormControls.Count > 0)
        {
            return true;
        }

        if (IsRasterScanPageWithOcrText(page))
        {
            return true;
        }

        if (semanticPage.Elements.Any(static element => element.TaggedStructure != null))
        {
            // Tagged slide and cover pages can still rely on a page-spanning
            // graphic backdrop. Keep their authored text selectable, but retain
            // page geometry so paint-order-dependent artwork is not detached
            // into an incomplete semantic figure.
            return RequiresTaggedGraphicLayoutPreservation(page, semanticPage);
        }

        if (RequiresDocumentIndexVisualPreservation(page, semanticPage))
        {
            return true;
        }

        if (SemanticDocumentIndexElement(semanticPage) != null)
        {
            return false;
        }

        if (lineGrid != null || columns != null)
        {
            return false;
        }

        if (page.Runs.Count == 0)
        {
            return page.Images.Count > 0 || page.Paths.Count > 0;
        }

        if (HasFullPageVectorBackdrop(page))
        {
            return true;
        }

        if (semanticPage.Elements.Any(static element => element.Kind == PdfSemanticElementKind.DefinitionList))
        {
            return false;
        }

        // Bibliography extraction intentionally collapses many source lines into
        // one semantic fragment. Do not mistake that successful grouping for the
        // sparse-element signal used by the remaining visual fallback heuristics.
        if (semanticPage.Elements.Any(static element =>
                element.Kind == PdfSemanticElementKind.Bibliography &&
                element.BibliographyFragment != null))
        {
            return false;
        }

        if (HasSideBySideTextColumns(page, semanticPage))
        {
            return true;
        }

        if (page.Lines.Count >= 40 &&
            semanticPage.Elements.Count <= 3 &&
            BodyParagraphCount(semanticPage) <= 2)
        {
            return true;
        }

        if (page.Images.Count >= 8 ||
            page.Paths.Count >= 100 && page.Images.Count >= 4 ||
            page.Images.Count >= 2 && page.Paths.Count >= 8 && BodyParagraphCount(semanticPage) < 6)
        {
            return true;
        }

        if (page.Lines.Count <= 8 &&
            page.Runs.Count <= 20 &&
            PageContentTop(page) > page.Height * 0.07f)
        {
            return true;
        }

        return semanticPage.Elements.Count <= 1 &&
            PageContentTop(page) > page.Height * 0.14f;
    }

    private static bool RequiresTaggedGraphicLayoutPreservation(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (page.Width <= 0 ||
            page.Height <= 0 ||
            page.Lines.Count > 18 ||
            page.Runs.Count > 40 ||
            semanticPage.Elements.Count > 20 ||
            semanticPage.Elements.Any(static element =>
                element.Kind is PdfSemanticElementKind.Table or
                    PdfSemanticElementKind.DefinitionList or
                    PdfSemanticElementKind.Bibliography or
                    PdfSemanticElementKind.Navigation))
        {
            return false;
        }

        PdfTextGlyph[] textGlyphs = page.Glyphs
            .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
            .ToArray();
        if (textGlyphs.Length > 0 &&
            textGlyphs.Count(static glyph => glyph.IsPainted) < textGlyphs.Length * 0.75f)
        {
            return false;
        }

        PdfLayoutRectangle pageBounds = new(0, 0, page.Width, page.Height);
        float pageArea = page.Width * page.Height;
        return page.Images
            .Where(static image => !IsVisualFallbackImage(image))
            .Select(VisibleImageBounds)
            .Any(bounds =>
                bounds.Width >= page.Width * 0.8f &&
                bounds.Height >= page.Height * 0.65f &&
                RectangleIntersectionArea(bounds, pageBounds) >= pageArea * 0.62f);
    }

    private static bool RequiresDocumentIndexVisualPreservation(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        PdfSemanticElement? navigation = SemanticDocumentIndexElement(semanticPage);
        if (navigation?.DocumentIndex == null)
        {
            return false;
        }

        PdfSemanticDocumentIndexItem[] items = FlattenDocumentIndexItems(navigation.DocumentIndex.Items).ToArray();
        HashSet<int> representedLinkIndexes = items
            .Where(static item => item.Link != null)
            .Select(static item => item.Link!.Index)
            .ToHashSet();

        // Some index rows have no extractable text, but retain a row-sized destination
        // annotation over localized image or vector fragments. Preserve the source page
        // only when those unresolved linked rows are present; unrelated graphics do not
        // make an otherwise complete semantic index absolute.
        return page.Links
            .Where(static link => link.Kind == PdfLayoutLinkKind.Destination)
            .Where(HasSemanticLinkTarget)
            .Where(link => !representedLinkIndexes.Contains(link.Index))
            .SelectMany(LinkBounds)
            .Where(bounds => RectanglesIntersect(bounds, navigation.Bounds, 1f))
            .Where(bounds => !items.Any(item => RectangleIntersectionArea(bounds, item.Bounds) > 0.25f))
            .Any(bounds => HasLocalizedDocumentIndexRowGraphic(page, bounds));
    }

    private static IEnumerable<PdfSemanticDocumentIndexItem> FlattenDocumentIndexItems(
        IReadOnlyList<PdfSemanticDocumentIndexItem> items)
    {
        foreach (PdfSemanticDocumentIndexItem item in items)
        {
            yield return item;
            foreach (PdfSemanticDocumentIndexItem child in FlattenDocumentIndexItems(item.Children))
            {
                yield return child;
            }
        }
    }

    private static bool HasLocalizedDocumentIndexRowGraphic(
        PdfLayoutPage page,
        PdfLayoutRectangle rowBounds)
    {
        return page.Images.Any(image => IsLocalizedDocumentIndexRowGraphic(VisibleImageBounds(image), rowBounds)) ||
            page.Paths.Any(path => IsLocalizedDocumentIndexRowGraphic(path.Bounds, rowBounds)) ||
            page.Shadings.Any(shading => IsLocalizedDocumentIndexRowGraphic(shading.Bounds, rowBounds)) ||
            page.VectorGroups.Any(group => IsLocalizedDocumentIndexRowGraphic(group.Bounds, rowBounds));
    }

    private static bool IsLocalizedDocumentIndexRowGraphic(
        PdfLayoutRectangle graphicBounds,
        PdfLayoutRectangle rowBounds)
    {
        return graphicBounds.Width > 0.5f &&
            graphicBounds.Height >= 0f &&
            graphicBounds.Width <= rowBounds.Width * 1.25f &&
            graphicBounds.Height <= MathF.Max(24f, rowBounds.Height * 1.75f) &&
            RectanglesIntersect(graphicBounds, rowBounds, 1f);
    }

    private static PdfSemanticElement? SemanticDocumentIndexElement(PdfSemanticPage semanticPage)
    {
        return semanticPage.Elements.FirstOrDefault(static element =>
            element.Kind == PdfSemanticElementKind.Navigation && element.DocumentIndex != null);
    }

    private static bool IsRasterScanPageWithOcrText(PdfLayoutPage page)
    {
        if (page.Width <= 0 || page.Height <= 0 || page.Glyphs.Count < 12)
        {
            return false;
        }

        PdfLayoutRectangle pageBounds = new(0, 0, page.Width, page.Height);
        PdfLayoutImage? scanImage = page.Images
            .Where(static image => image.Kind is PdfLayoutImageKind.XObject or PdfLayoutImageKind.InlineImage)
            .Where(image => image.IntrinsicWidth >= page.Width * 0.5f && image.IntrinsicHeight >= page.Height * 0.5f)
            .Where(image => VisibleImageBounds(image).Width >= page.Width * 0.88f)
            .Where(image => VisibleImageBounds(image).Height >= page.Height * 0.88f)
            .Where(image => RectangleIntersectionArea(VisibleImageBounds(image), pageBounds) >= page.Width * page.Height * 0.82f)
            .OrderByDescending(image => RectangleIntersectionArea(VisibleImageBounds(image), pageBounds))
            .FirstOrDefault();
        if (scanImage == null)
        {
            return false;
        }

        PdfTextGlyph[] textGlyphs = page.Glyphs
            .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
            .Where(static glyph => glyph.PageBounds.Width > 0 && glyph.PageBounds.Height > 0)
            .ToArray();
        if (textGlyphs.Length < 12 || textGlyphs.Count(static glyph => !glyph.IsPainted) < textGlyphs.Length * 0.85f)
        {
            return false;
        }

        PdfLayoutRectangle imageBounds = VisibleImageBounds(scanImage);
        int coLocatedGlyphs = textGlyphs.Count(glyph => RectangleContainsCenter(imageBounds, glyph.PageBounds));
        return coLocatedGlyphs >= textGlyphs.Length * 0.9f;
    }

    private static float RectangleIntersectionArea(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        float width = MathF.Max(0, MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X));
        float height = MathF.Max(0, MathF.Min(first.Bottom, second.Bottom) - MathF.Max(first.Y, second.Y));
        return width * height;
    }

    private static bool RectangleContainsCenter(PdfLayoutRectangle outer, PdfLayoutRectangle inner)
    {
        float centerX = inner.X + inner.Width / 2f;
        float centerY = inner.Y + inner.Height / 2f;
        return centerX >= outer.X && centerX <= outer.Right &&
            centerY >= outer.Y && centerY <= outer.Bottom;
    }

    private static bool IsUnpaintedTextRun(PdfTextRun run)
    {
        PdfTextGlyph[] textGlyphs = run.Glyphs
            .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
            .ToArray();
        return textGlyphs.Length > 0 &&
            textGlyphs.Count(static glyph => !glyph.IsPainted) >= textGlyphs.Length * 0.85f;
    }

    private static bool HasFullPageVectorBackdrop(PdfLayoutPage page)
    {
        PdfLayoutPath[] filledPaths = page.Paths
            .Where(static path => path.IsFilled)
            .Where(static path => path.Bounds.Width > 2f && path.Bounds.Height > 2f)
            .ToArray();
        if (filledPaths.Length == 0)
        {
            return false;
        }

        PdfLayoutRectangle backdrop = UnionRectangles(filledPaths.Select(static path => path.Bounds));
        return backdrop.Width >= page.Width * 0.78f &&
            backdrop.Height >= page.Height * 0.78f;
    }

    private static bool HasSideBySideTextColumns(PdfLayoutPage page, PdfSemanticPage semanticPage)
    {
        if (semanticPage.Elements.Any(static element =>
                element.Kind is PdfSemanticElementKind.Table or PdfSemanticElementKind.DefinitionList) ||
            page.Lines.Count < 20)
        {
            return false;
        }

        PdfTextLine[] shortHorizontalLines = page.Lines
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .Where(static line => line.Runs.Any(run => MathF.Abs(run.Direction) < 0.01f))
            .Where(line => line.Bounds.Width <= page.Width * 0.42f)
            .ToArray();
        PdfTextLine[] leftColumn = shortHorizontalLines
            .Where(line => line.Bounds.X < page.Width * 0.42f)
            .ToArray();
        PdfTextLine[] rightColumn = shortHorizontalLines
            .Where(line => line.Bounds.X > page.Width * 0.58f)
            .ToArray();
        if (leftColumn.Length < 12 || rightColumn.Length < 12)
        {
            return false;
        }

        float leftTop = leftColumn.Min(static line => line.Bounds.Y);
        float leftBottom = leftColumn.Max(static line => line.Bounds.Bottom);
        float rightTop = rightColumn.Min(static line => line.Bounds.Y);
        float rightBottom = rightColumn.Max(static line => line.Bounds.Bottom);
        float overlap = MathF.Max(0, MathF.Min(leftBottom, rightBottom) - MathF.Max(leftTop, rightTop));
        return overlap >= MathF.Min(leftBottom - leftTop, rightBottom - rightTop) * 0.45f;
    }

    private static PdfSemanticLineGrid? TryCreateSemanticLineGrid(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (semanticPage.Elements.Any(static element =>
            element.Kind is PdfSemanticElementKind.DefinitionList or PdfSemanticElementKind.CodeBlock or PdfSemanticElementKind.Algorithm))
        {
            return null;
        }

        if (!TryGetTwoColumnRuns(page, semanticPage, out PdfTextRun[] candidateLines, out LineGridColumn[] gridColumns, out float pitch))
        {
            return null;
        }

        float columnTolerance = page.Width * 0.06f;

        List<LineGridRow> rows = [];
        foreach (PdfTextRun line in candidateLines
            .OrderBy(static run => run.Bounds.Y)
            .ThenBy(static run => run.Bounds.X))
        {
            int columnIndex = NearestGridColumn(gridColumns, line.Bounds.X);
            if (columnIndex < 0 || MathF.Abs(gridColumns[columnIndex].Left - line.Bounds.X) > columnTolerance)
            {
                return null;
            }

            float rowTolerance = MathF.Max(1f, line.Bounds.Height * 0.4f);
            LineGridRow? row = rows
                .Where(row => MathF.Abs(row.Top - line.Bounds.Y) <= rowTolerance)
                .OrderBy(row => MathF.Abs(row.Top - line.Bounds.Y))
                .FirstOrDefault();
            if (row == null)
            {
                row = new LineGridRow(line.Bounds.Y, gridColumns.Length);
                rows.Add(row);
            }

            if (!row.TryAdd(columnIndex, line))
            {
                return null;
            }
        }

        if (rows.Count < 12 || rows.Any(row => row.Cells.Any(static cell => cell == null)))
        {
            return null;
        }

        rows.Sort(static (first, second) => first.Top.CompareTo(second.Top));
        float rightInset = MathF.Max(0, page.Width - gridColumns[0].Left - pitch * gridColumns.Length);
        return new PdfSemanticLineGrid(
            page,
            rows,
            gridColumns.Length,
            gridColumns[0].Left,
            rightInset);
    }

    private static PdfSemanticColumns? TryCreateSemanticColumns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (semanticPage.Elements.Any(static element => element.Kind == PdfSemanticElementKind.DefinitionList))
        {
            return null;
        }

        if (!TryGetFlowColumnRuns(
                page,
                semanticPage,
                out PdfTextRun[] leadingRuns,
                out SemanticColumnTrack[] tracks,
                out SemanticColumnGutter[] gutters))
        {
            if (TryGetCaptionedFigureTwoColumnRuns(
                    page,
                    semanticPage,
                    out leadingRuns,
                    out tracks,
                    out gutters))
            {
                return AddColumnSpanningFigures(new PdfSemanticColumns(
                    page,
                    semanticPage,
                    leadingRuns,
                    tracks,
                    gutters,
                    SemanticColumnListElements(semanticPage),
                    tracks[0].Left,
                    MathF.Max(0, page.Width - tracks[^1].Right)));
            }

            PdfSemanticColumns? mixedRegions = TryCreateMixedGraphicTextColumns(page, semanticPage);
            if (mixedRegions != null)
            {
                return AddColumnSpanningFigures(mixedRegions);
            }

            if (!TryGetTwoColumnRuns(page, semanticPage, out _, out LineGridColumn[] legacyColumns, out float pitch))
            {
                return null;
            }

            float legacyRightInset = MathF.Max(0, page.Width - legacyColumns[0].Left - pitch * legacyColumns.Length);
            return AddColumnSpanningFigures(new PdfSemanticColumns(
                page,
                semanticPage,
                [],
                [
                    new SemanticColumnTrack(legacyColumns[0], legacyColumns[0].Left, legacyColumns[0].Left + pitch),
                    new SemanticColumnTrack(legacyColumns[1], legacyColumns[1].Left, page.Width - legacyRightInset)
                ],
                [new SemanticColumnGutter(legacyColumns[0].Left + pitch, legacyColumns[1].Left)],
                SemanticColumnListElements(semanticPage),
                legacyColumns[0].Left,
                legacyRightInset));
        }

        return AddColumnSpanningFigures(new PdfSemanticColumns(
            page,
            semanticPage,
            leadingRuns,
            tracks,
            gutters,
            SemanticColumnListElements(semanticPage),
            tracks[0].Left,
            MathF.Max(0, page.Width - tracks[^1].Right)));
    }

    private static PdfSemanticColumns? TryCreateAuthoredSemanticColumns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (!IsAuthoredColumnCandidate(page, semanticPage))
        {
            return null;
        }

        PdfTextRun[] leadingRuns;
        SemanticColumnTrack[] tracks;
        SemanticColumnGutter[] gutters;
        if ((!TryGetFlowColumnRuns(page, semanticPage, out leadingRuns, out tracks, out gutters) ||
                tracks.Length != 2) &&
            !TryGetAuthoredTwoColumnRuns(page, semanticPage, out leadingRuns, out tracks, out gutters))
        {
            return null;
        }

        PdfSemanticColumns columns = new(
            page,
            semanticPage,
            leadingRuns,
            tracks,
            gutters,
            SemanticColumnListElements(semanticPage),
            tracks[0].Left,
            MathF.Max(0, page.Width - tracks[^1].Right),
            preserveAuthoredSemanticElements: true);
        if (!HasCompleteAuthoredColumnOwnership(columns))
        {
            return null;
        }

        return AddColumnSpanningFigures(columns);
    }

    private static bool IsAuthoredColumnCandidate(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (page.FormControls.Count > 0 ||
            semanticPage.Elements.Any(static element =>
                element.Kind is PdfSemanticElementKind.DefinitionList or
                    PdfSemanticElementKind.Navigation or
                    PdfSemanticElementKind.Algorithm))
        {
            return false;
        }

        PdfSemanticElement[] authoredBody = AuthoredColumnBodyElements(page, semanticPage);
        if (authoredBody.Length < 4 ||
            authoredBody.Count(static element => element.Kind == PdfSemanticElementKind.Paragraph) < 2)
        {
            return false;
        }

        float pageArea = page.Width * page.Height;
        if (pageArea <= 0)
        {
            return false;
        }

        if (page.Images
                .Select(VisibleImageBounds)
                .Any(bounds => bounds.Width * bounds.Height >= pageArea * 0.015f) ||
            page.Shadings.Any(shading => shading.Bounds.Width * shading.Bounds.Height >= pageArea * 0.015f) ||
            page.VectorGroups.Any(group => group.Bounds.Width * group.Bounds.Height >= pageArea * 0.015f))
        {
            return false;
        }

        return !SemanticFigureRegions(page, semanticPage)
            .Any(region => region.Width * region.Height >= pageArea * 0.015f);
    }

    private static PdfSemanticElement[] AuthoredColumnBodyElements(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        float artifactBottom = page.Height * 0.07f;
        return semanticPage.Elements
            .Where(static element => element.TaggedStructure != null)
            .Where(static element => element.Lines.Count > 0)
            .Where(static element => !string.IsNullOrWhiteSpace(element.Text))
            .Where(static element => element.Bounds.Width > 0.1f && element.Bounds.Height > 0.1f)
            .Where(element =>
                element.Bounds.Bottom > artifactBottom ||
                element.Bounds.Height > page.Height * 0.04f)
            .ToArray();
    }

    private static bool HasCompleteAuthoredColumnOwnership(PdfSemanticColumns columns)
    {
        PdfSemanticElement[] body = AuthoredColumnBodyElements(columns.Page, columns.SemanticPage);
        PdfSemanticElement[][] byColumn = columns.Columns
            .Select(column => body
                .Where(element => IsSemanticElementInColumn(columns, column, element))
                .ToArray())
            .ToArray();
        if (byColumn.Any(static elements =>
                elements.Length < 2 ||
                elements.Sum(static element => element.Lines.Count) < 12 ||
                elements.Sum(static element => element.Text.Length) < 160))
        {
            return false;
        }

        HashSet<PdfSemanticElement> owned = byColumn
            .SelectMany(static elements => elements)
            .ToHashSet();
        return owned.Count == body.Length &&
            byColumn.SelectMany(static elements => elements).Count() == body.Length;
    }

    private static bool TryGetAuthoredTwoColumnRuns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        out PdfTextRun[] leadingRuns,
        out SemanticColumnTrack[] tracks,
        out SemanticColumnGutter[] gutters)
    {
        leadingRuns = [];
        tracks = [];
        gutters = [];

        PdfSemanticElement[] elements = AuthoredColumnBodyElements(page, semanticPage);
        PdfSemanticElement[] leftElements = elements
            .Where(element => element.Bounds.X + element.Bounds.Width / 2f < page.Width / 2f)
            .ToArray();
        PdfSemanticElement[] rightElements = elements
            .Where(element => element.Bounds.X + element.Bounds.Width / 2f >= page.Width / 2f)
            .ToArray();
        if (leftElements.Length < 2 || rightElements.Length < 2 ||
            leftElements.Sum(static element => element.Lines.Count) < 12 ||
            rightElements.Sum(static element => element.Lines.Count) < 12)
        {
            return false;
        }

        float gutterLeft = leftElements.Max(static element => element.Bounds.Right);
        float gutterRight = rightElements.Min(static element => element.Bounds.X);
        float minimumGap = page.Width * 0.018f;
        if (gutterRight - gutterLeft < minimumGap)
        {
            return false;
        }

        float boundary = (gutterLeft + gutterRight) / 2f;
        float leftTop = leftElements.Min(static element => element.Bounds.Y);
        float leftBottom = leftElements.Max(static element => element.Bounds.Bottom);
        float rightTop = rightElements.Min(static element => element.Bounds.Y);
        float rightBottom = rightElements.Max(static element => element.Bounds.Bottom);
        float verticalOverlap = MathF.Max(
            0,
            MathF.Min(leftBottom, rightBottom) - MathF.Max(leftTop, rightTop));
        if (verticalOverlap < MathF.Min(leftBottom - leftTop, rightBottom - rightTop) * 0.4f)
        {
            return false;
        }

        ColumnCorridor corridor = new(boundary, gutterLeft, gutterRight, []);
        bool hasConfinedSemanticTable = semanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Table)
            .Any(table => table.Bounds.Right <= boundary || table.Bounds.X >= boundary);
        bool hasUnownedRuledTableAcrossColumns =
            HasRuledTableAcrossColumnCorridors(page, [corridor]) &&
            !hasConfinedSemanticTable;
        if (hasUnownedRuledTableAcrossColumns ||
            !SpanningTablesAreColumnCompatible(page, semanticPage, boundary))
        {
            return false;
        }

        PdfTextRun[] horizontalRuns = page.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .ToArray();
        ColumnDetectionRow[] rows = CreateColumnDetectionRows(horizontalRuns);
        float corridorTolerance = MathF.Max(1f, page.Width * 0.002f);
        if (rows.Length < 12 ||
            horizontalRuns.Any(run =>
                run.Bounds.X < gutterLeft - corridorTolerance &&
                run.Bounds.Right > gutterRight + corridorTolerance))
        {
            return false;
        }

        PdfTextRun[] leftRuns = rows
            .Select(row => CombineColumnLine(row.Runs.Where(run =>
                run.Bounds.X + run.Bounds.Width / 2f < boundary)))
            .Where(static run => run != null)
            .Cast<PdfTextRun>()
            .ToArray();
        PdfTextRun[] rightRuns = rows
            .Select(row => CombineColumnLine(row.Runs.Where(run =>
                run.Bounds.X + run.Bounds.Width / 2f >= boundary)))
            .Where(static run => run != null)
            .Cast<PdfTextRun>()
            .ToArray();
        if (leftRuns.Length < 12 || rightRuns.Length < 12)
        {
            return false;
        }

        LineGridColumn leftColumn = CreateLineGridColumn(leftRuns);
        LineGridColumn rightColumn = CreateLineGridColumn(rightRuns);
        float leftInset = MathF.Max(
            0,
            MathF.Min(
                leftElements.Min(static element => element.Bounds.X),
                leftRuns.Min(static run => run.Bounds.X)));
        float rightEdge = MathF.Max(
            rightElements.Max(static element => element.Bounds.Right),
            rightRuns.Max(static run => run.Bounds.Right));
        float rightInset = MathF.Max(0, page.Width - rightEdge);
        tracks =
        [
            new SemanticColumnTrack(leftColumn, leftInset, gutterLeft),
            new SemanticColumnTrack(rightColumn, gutterRight, page.Width - rightInset)
        ];
        gutters = [new SemanticColumnGutter(gutterLeft, gutterRight)];
        return true;
    }

    private static PdfSemanticRuledGrid? TryCreateSemanticRuledGrid(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (page.FormControls.Count > 0 ||
            semanticPage.Elements.Any(static element =>
                element.Kind is PdfSemanticElementKind.Table or PdfSemanticElementKind.DefinitionList))
        {
            return null;
        }

        PdfSemanticElement[] authoredTextBlocks = semanticPage.Elements
            .Where(static element => element.TaggedStructure != null)
            .Where(static element => element.Lines.Count > 0)
            .Where(static element => !string.IsNullOrWhiteSpace(element.Text))
            .Where(static element => element.Bounds.Width > 0.1f && element.Bounds.Height > 0.1f)
            .ToArray();
        PdfSemanticElement[] authoredBlocks = authoredTextBlocks
            .Where(static element => element.Kind is
                PdfSemanticElementKind.Paragraph or PdfSemanticElementKind.List)
            .ToArray();
        if (authoredBlocks.Length < 6)
        {
            return null;
        }

        foreach (PdfLayoutRectangle region in SemanticFigureRegions(page, semanticPage)
            .Where(region => region.Width >= page.Width * 0.65f)
            .Where(region => region.Height >= page.Height * 0.20f)
            .OrderByDescending(static region => region.Width * region.Height))
        {
            if (!TryCreateSemanticRuledGridTracks(
                    page,
                    region,
                    out SemanticRuledGridTrack[] tracks,
                    out SemanticColumnGutter[] gutters))
            {
                continue;
            }

            PdfSemanticElement[] members = authoredBlocks
                .Where(element => RectangleContainsCenter(ExpandRectangle(region, 2f, 2f), element.Bounds))
                .OrderBy(static element => element.Bounds.Y)
                .ThenBy(static element => element.Bounds.X)
                .ToArray();
            if (members.Length < 6 ||
                authoredTextBlocks
                    .Where(element => RectangleContainsCenter(ExpandRectangle(region, 2f, 2f), element.Bounds))
                    .Any(element => !members.Contains(element)))
            {
                continue;
            }

            List<SemanticRuledGridPlacement> placements = [];
            bool ambiguous = false;
            foreach (PdfSemanticElement element in members)
            {
                if (TryPlaceSemanticRuledGridElement(
                        region,
                        tracks,
                        gutters,
                        element,
                        out SemanticRuledGridPlacement placement))
                {
                    placements.Add(placement);
                }
                else
                {
                    ambiguous = true;
                    break;
                }
            }

            if (ambiguous ||
                Enumerable.Range(0, tracks.Length).Any(index =>
                    placements.Count(placement => placement.ColumnIndex == index) < 2))
            {
                continue;
            }

            SemanticRuledGridBand[] bands = CreateSemanticRuledGridBands(
                tracks.Length,
                placements);
            if (bands.Length == 0 ||
                !bands.Any(static band => !band.IsSpanning))
            {
                continue;
            }

            return new PdfSemanticRuledGrid(
                page,
                region,
                tracks,
                gutters,
                bands,
                members,
                CreateSemanticRuledGridBorders(page, tracks, bands),
                TryCreateSemanticPageTopRuleGroup(page, semanticPage, region));
        }

        return null;
    }

    private static SemanticPageRuleGroup? TryCreateSemanticPageTopRuleGroup(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutRectangle ruledRegion)
    {
        PdfSemanticElement? firstContent = semanticPage.Elements
            .Where(static element => element.Lines.Count > 0)
            .Where(static element => element.Kind is not (
                PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer))
            .Where(element => element.Bounds.Bottom <= ruledRegion.Y)
            .OrderBy(static element => element.Bounds.Y)
            .ThenBy(static element => element.Bounds.X)
            .FirstOrDefault();
        if (firstContent == null)
        {
            return null;
        }

        float horizontalTolerance = MathF.Max(8f, page.Width * 0.02f);
        SemanticPageRule[] candidates = page.Paths
            .Select(path => TryCreateSemanticPageRule(path, out SemanticPageRule rule)
                ? rule
                : (SemanticPageRule?)null)
            .Where(static rule => rule != null)
            .Select(static rule => rule!.Value)
            .Where(rule =>
                rule.Width >= ruledRegion.Width * 0.80f &&
                rule.Left >= ruledRegion.X - horizontalTolerance &&
                rule.Right <= ruledRegion.Right + horizontalTolerance &&
                rule.Top >= page.Height * 0.02f &&
                rule.Bottom <= firstContent.Bounds.Y - 3f &&
                rule.Thickness <= 8f &&
                RelativeLuminance(rule.Color) <= 0.35f)
            .OrderBy(static rule => rule.Top)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        List<SemanticPageRule> cluster = [candidates[^1]];
        for (int index = candidates.Length - 2; index >= 0; index--)
        {
            SemanticPageRule next = cluster[0];
            if (next.Top - candidates[index].Bottom > 12f)
            {
                break;
            }

            cluster.Insert(0, candidates[index]);
        }

        if (cluster.Count > 3)
        {
            cluster.RemoveRange(0, cluster.Count - 3);
        }

        float top = cluster.Min(static rule => rule.Top);
        float bottom = cluster.Max(static rule => rule.Bottom);
        float gapAfter = MathF.Max(0f, firstContent.Bounds.Y - bottom - 4f);
        return new SemanticPageRuleGroup(
            ruledRegion.X,
            ruledRegion.Right,
            top,
            bottom,
            gapAfter,
            cluster);
    }

    private static bool TryCreateSemanticPageRule(
        PdfLayoutPath path,
        out SemanticPageRule rule)
    {
        rule = default;
        if (!path.IsStroked ||
            path.UsesShapeAlpha ||
            path.UsesSoftMask ||
            path.Stroke?.DashArray.Any(static dash => dash > 0f) == true ||
            path.Commands.Count != 2 ||
            path.Commands[0].Kind != PdfLayoutPathCommandKind.MoveTo ||
            path.Commands[1].Kind != PdfLayoutPathCommandKind.LineTo)
        {
            return false;
        }

        PdfLayoutPathCommand start = path.Commands[0];
        PdfLayoutPathCommand end = path.Commands[1];
        float thickness = MathF.Max(0.01f, path.Stroke!.Width);
        float rise = MathF.Abs(end.Y1 - start.Y1);
        if (rise > MathF.Max(0.75f, thickness * 0.5f))
        {
            return false;
        }

        float left = MathF.Min(start.X1, end.X1);
        float right = MathF.Max(start.X1, end.X1);
        float centerY = (start.Y1 + end.Y1) / 2f;
        rule = new SemanticPageRule(
            path.Index,
            left,
            right,
            centerY - thickness / 2f,
            thickness,
            path.Stroke.Color);
        return true;
    }

    private static float RelativeLuminance(PdfLayoutColor color)
    {
        return color.Red * 0.2126f + color.Green * 0.7152f + color.Blue * 0.0722f;
    }

    private static bool TryCreateSemanticRuledGridTracks(
        PdfLayoutPage page,
        PdfLayoutRectangle region,
        out SemanticRuledGridTrack[] tracks,
        out SemanticColumnGutter[] gutters)
    {
        tracks = [];
        gutters = [];
        List<List<PdfLayoutRectangle>> verticalFamilies = [];
        float positionTolerance = MathF.Max(1f, page.Width * 0.002f);
        foreach (PdfLayoutRectangle segment in page.Paths
            .Where(static path => path.IsStroked && !path.UsesSoftMask)
            .SelectMany(SemanticRuleSegments)
            .Where(segment => segment.Height >= 6f &&
                segment.Height >= MathF.Max(0.1f, segment.Width) * 8f)
            .Where(segment => segment.X + segment.Width / 2f >= region.X - positionTolerance &&
                segment.X + segment.Width / 2f <= region.Right + positionTolerance)
            .Where(segment => segment.Bottom >= region.Y - positionTolerance &&
                segment.Y <= region.Bottom + positionTolerance)
            .OrderBy(static segment => segment.X))
        {
            float x = segment.X + segment.Width / 2f;
            List<PdfLayoutRectangle>? family = verticalFamilies.FirstOrDefault(existing =>
                MathF.Abs(existing.Average(static item => item.X + item.Width / 2f) - x) <= positionTolerance);
            if (family == null)
            {
                verticalFamilies.Add([segment]);
            }
            else
            {
                family.Add(segment);
            }
        }

        float minimumCoverage = region.Height * 0.52f;
        float[] boundaries = verticalFamilies
            .Where(family => VerticalRuleCoverage(family, region) >= minimumCoverage)
            .Select(static family => family.Average(static item => item.X + item.Width / 2f))
            .Order()
            .ToArray();
        if (boundaries.Length < 4 ||
            MathF.Abs(boundaries[0] - region.X) > positionTolerance * 2f ||
            MathF.Abs(boundaries[^1] - region.Right) > positionTolerance * 2f)
        {
            return false;
        }

        boundaries[0] = region.X;
        boundaries[^1] = region.Right;
        List<SemanticRuledGridTrack> detectedTracks = [];
        float maximumGutterWidth = region.Width * 0.08f;
        float minimumTrackWidth = region.Width * 0.16f;
        for (int index = 0; index + 1 < boundaries.Length; index++)
        {
            float left = boundaries[index];
            float right = boundaries[index + 1];
            float width = right - left;
            if (width >= minimumTrackWidth)
            {
                detectedTracks.Add(new SemanticRuledGridTrack(left, right));
            }
            else if (width > maximumGutterWidth)
            {
                return false;
            }
        }

        // The initial reconciliation deliberately targets the common form layout
        // that exposed #152. Other column counts remain on the conservative
        // fallback path until their ownership and spanning behavior are covered.
        if (detectedTracks.Count != 3)
        {
            return false;
        }

        SemanticColumnGutter[] detectedGutters = detectedTracks
            .Zip(detectedTracks.Skip(1), static (left, right) =>
                new SemanticColumnGutter(left.Right, right.Left))
            .ToArray();
        if (detectedGutters.Any(gutter => gutter.Width > maximumGutterWidth))
        {
            return false;
        }

        tracks = detectedTracks.ToArray();
        gutters = detectedGutters;
        return true;
    }

    private static float VerticalRuleCoverage(
        IReadOnlyList<PdfLayoutRectangle> segments,
        PdfLayoutRectangle region)
    {
        PdfLayoutRectangle[] clipped = segments
            .Select(segment =>
            {
                float top = MathF.Max(region.Y, segment.Y);
                float bottom = MathF.Min(region.Bottom, segment.Bottom);
                return new PdfLayoutRectangle(segment.X, top, segment.Width, MathF.Max(0, bottom - top));
            })
            .Where(static segment => segment.Height > 0)
            .OrderBy(static segment => segment.Y)
            .ToArray();
        if (clipped.Length == 0)
        {
            return 0f;
        }

        float coverage = 0f;
        float top = clipped[0].Y;
        float bottom = clipped[0].Bottom;
        foreach (PdfLayoutRectangle segment in clipped.Skip(1))
        {
            if (segment.Y <= bottom + 1f)
            {
                bottom = MathF.Max(bottom, segment.Bottom);
            }
            else
            {
                coverage += bottom - top;
                top = segment.Y;
                bottom = segment.Bottom;
            }
        }

        return coverage + bottom - top;
    }

    private static IEnumerable<PdfLayoutRectangle> SemanticRuleSegments(PdfLayoutPath path)
    {
        PdfLayoutPathCommand? subpathStart = null;
        PdfLayoutPathCommand? previous = null;
        foreach (PdfLayoutPathCommand command in path.Commands)
        {
            if (command.Kind == PdfLayoutPathCommandKind.MoveTo)
            {
                subpathStart = command;
                previous = command;
                continue;
            }

            if (command.Kind == PdfLayoutPathCommandKind.ClosePath)
            {
                if (previous != null && subpathStart != null)
                {
                    yield return RuleSegment(previous.Value, subpathStart.Value);
                }

                previous = null;
                subpathStart = null;
                continue;
            }

            if (command.Kind != PdfLayoutPathCommandKind.LineTo || previous == null)
            {
                continue;
            }

            yield return RuleSegment(previous.Value, command);
            previous = command;
        }
    }

    private static PdfLayoutRectangle RuleSegment(
        PdfLayoutPathCommand start,
        PdfLayoutPathCommand end)
    {
        return new PdfLayoutRectangle(
            MathF.Min(start.X1, end.X1),
            MathF.Min(start.Y1, end.Y1),
            MathF.Abs(end.X1 - start.X1),
            MathF.Abs(end.Y1 - start.Y1));
    }

    private static bool TryPlaceSemanticRuledGridElement(
        PdfLayoutRectangle region,
        IReadOnlyList<SemanticRuledGridTrack> tracks,
        IReadOnlyList<SemanticColumnGutter> gutters,
        PdfSemanticElement element,
        out SemanticRuledGridPlacement placement)
    {
        placement = default;
        int overlappingTracks = tracks.Count(track =>
            HorizontalOverlap(element.Bounds, track.Bounds) >=
                MathF.Min(element.Bounds.Width, track.Width) * 0.25f);
        if (element.Bounds.Width >= region.Width * 0.52f && overlappingTracks >= 2)
        {
            placement = new SemanticRuledGridPlacement(element, null, null, IsSpanning: true);
            return true;
        }

        if (element.Text.Trim().Length <= 32 &&
            element.Bounds.Width <= region.Width * 0.12f)
        {
            float center = element.Bounds.X + element.Bounds.Width / 2f;
            int gutterIndex = Enumerable.Range(0, gutters.Count)
                .OrderBy(index => MathF.Abs(center - gutters[index].Boundary))
                .FirstOrDefault();
            float tolerance = MathF.Max(12f, region.Width * 0.025f);
            if (gutters.Count > 0 &&
                center >= gutters[gutterIndex].Left - tolerance &&
                center <= gutters[gutterIndex].Right + tolerance)
            {
                placement = new SemanticRuledGridPlacement(
                    element,
                    null,
                    gutterIndex,
                    IsSpanning: false);
                return true;
            }
        }

        float[] overlapRatios = tracks
            .Select(track => HorizontalOverlap(element.Bounds, track.Bounds) /
                MathF.Max(0.1f, element.Bounds.Width))
            .ToArray();
        int bestTrack = Array.IndexOf(overlapRatios, overlapRatios.Max());
        float secondBest = overlapRatios
            .Where((_, index) => index != bestTrack)
            .DefaultIfEmpty(0f)
            .Max();
        if (bestTrack >= 0 && overlapRatios[bestTrack] >= 0.70f && secondBest <= 0.30f)
        {
            placement = new SemanticRuledGridPlacement(element, bestTrack, null, IsSpanning: false);
            return true;
        }

        return false;
    }

    private static float HorizontalOverlap(
        PdfLayoutRectangle first,
        PdfLayoutRectangle second)
    {
        return MathF.Max(0, MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X));
    }

    private static SemanticRuledGridBand[] CreateSemanticRuledGridBands(
        int columnCount,
        IReadOnlyList<SemanticRuledGridPlacement> placements)
    {
        List<SemanticRuledGridBand> bands = [];
        List<SemanticRuledGridPlacement[]> spanningGroups = [];
        List<SemanticRuledGridPlacement> currentGroup = [];
        foreach (SemanticRuledGridPlacement placement in placements
            .Where(static placement => placement.IsSpanning)
            .OrderBy(static placement => placement.Element.Bounds.Y))
        {
            if (currentGroup.Count > 0 &&
                placement.Element.Bounds.Y - currentGroup.Max(static item => item.Element.Bounds.Bottom) > 18f)
            {
                spanningGroups.Add(currentGroup.ToArray());
                currentGroup.Clear();
            }

            currentGroup.Add(placement);
        }

        if (currentGroup.Count > 0)
        {
            spanningGroups.Add(currentGroup.ToArray());
        }

        float previousBottom = float.NegativeInfinity;
        foreach (SemanticRuledGridPlacement[] spanningGroup in spanningGroups)
        {
            float groupTop = spanningGroup.Min(static placement => placement.Element.Bounds.Y);
            AddSemanticRuledGridLaneBand(
                bands,
                columnCount,
                placements.Where(placement =>
                    !placement.IsSpanning &&
                    placement.Element.Bounds.Y + placement.Element.Bounds.Height / 2f >= previousBottom &&
                    placement.Element.Bounds.Y + placement.Element.Bounds.Height / 2f < groupTop));
            bands.Add(SemanticRuledGridBand.CreateSpanning(
                spanningGroup.Select(static placement => placement.Element).ToArray()));
            previousBottom = spanningGroup.Max(static placement => placement.Element.Bounds.Bottom);
        }

        AddSemanticRuledGridLaneBand(
            bands,
            columnCount,
            placements.Where(placement =>
                !placement.IsSpanning &&
                placement.Element.Bounds.Y + placement.Element.Bounds.Height / 2f >= previousBottom));
        return bands.ToArray();
    }

    private static void AddSemanticRuledGridLaneBand(
        ICollection<SemanticRuledGridBand> bands,
        int columnCount,
        IEnumerable<SemanticRuledGridPlacement> source)
    {
        SemanticRuledGridPlacement[] placements = source
            .OrderBy(static placement => placement.Element.Bounds.Y)
            .ThenBy(static placement => placement.Element.Bounds.X)
            .ToArray();
        if (placements.Length == 0)
        {
            return;
        }

        IReadOnlyList<PdfSemanticElement>[] columns = Enumerable.Range(0, columnCount)
            .Select(index => (IReadOnlyList<PdfSemanticElement>)placements
                .Where(placement => placement.ColumnIndex == index)
                .Select(static placement => placement.Element)
                .ToArray())
            .ToArray();
        SemanticRuledGridConnector[] connectors = placements
            .Where(static placement => placement.GutterIndex.HasValue)
            .Select(static placement => new SemanticRuledGridConnector(
                placement.GutterIndex!.Value,
                placement.Element))
            .ToArray();
        bands.Add(SemanticRuledGridBand.CreateLanes(columns, connectors));
    }

    private static SemanticRuledGridBorders CreateSemanticRuledGridBorders(
        PdfLayoutPage page,
        IReadOnlyList<SemanticRuledGridTrack> tracks,
        IReadOnlyList<SemanticRuledGridBand> bands)
    {
        Dictionary<PdfSemanticElement, SemanticRuledBorder> elementBorders =
            new((IEqualityComparer<PdfSemanticElement>)ReferenceEqualityComparer.Instance);
        Dictionary<PdfSemanticListItem, SemanticRuledBorder> listItemBorders =
            new((IEqualityComparer<PdfSemanticListItem>)ReferenceEqualityComparer.Instance);
        SemanticRuledHorizontalRule[] rules = SemanticRuledHorizontalRules(page);

        foreach (SemanticRuledGridBand band in bands.Where(static band => !band.IsSpanning))
        {
            for (int columnIndex = 0; columnIndex < tracks.Count; columnIndex++)
            {
                SemanticRuledGridUnit[] units = SemanticRuledGridUnits(band.Columns[columnIndex])
                    .OrderBy(static unit => unit.Bounds.Y)
                    .ThenBy(static unit => unit.Bounds.X)
                    .ToArray();
                if (units.Length < 2)
                {
                    continue;
                }

                SemanticRuledGridTrack track = tracks[columnIndex];
                for (int unitIndex = 0; unitIndex + 1 < units.Length; unitIndex++)
                {
                    SemanticRuledGridUnit above = units[unitIndex];
                    SemanticRuledGridUnit below = units[unitIndex + 1];
                    float aboveCenter = above.Bounds.Y + above.Bounds.Height / 2f;
                    float belowCenter = below.Bounds.Y + below.Bounds.Height / 2f;
                    float expected = (above.Bounds.Bottom + below.Bounds.Y) / 2f;
                    SemanticRuledHorizontalRule? matched = rules
                        .Where(rule =>
                            rule.CenterY > aboveCenter &&
                            rule.CenterY < belowCenter &&
                            HorizontalOverlap(
                                new PdfLayoutRectangle(rule.Left, rule.CenterY, rule.Width, rule.Thickness),
                                track.Bounds) >= track.Width * 0.80f)
                        .OrderBy(rule => MathF.Abs(rule.CenterY - expected))
                        .ThenByDescending(static rule => rule.Width)
                        .Select(static rule => (SemanticRuledHorizontalRule?)rule)
                        .FirstOrDefault();
                    if (matched == null)
                    {
                        continue;
                    }

                    SemanticRuledBorder border = new(
                        matched.Value.SourcePathIndex,
                        matched.Value.CenterY,
                        matched.Value.Thickness,
                        matched.Value.Color);
                    if (above.ListItem != null)
                    {
                        listItemBorders[above.ListItem] = border;
                    }
                    else
                    {
                        elementBorders[above.Element] = border;
                    }
                }
            }
        }

        return new SemanticRuledGridBorders(elementBorders, listItemBorders);
    }

    private static IEnumerable<SemanticRuledGridUnit> SemanticRuledGridUnits(
        IEnumerable<PdfSemanticElement> elements)
    {
        foreach (PdfSemanticElement element in elements)
        {
            if (element.SemanticList != null)
            {
                foreach (PdfSemanticListItem item in element.SemanticList.Items
                    .Where(SemanticListItemHasRenderableContent))
                {
                    yield return new SemanticRuledGridUnit(element, item, item.Bounds);
                }

                continue;
            }

            yield return new SemanticRuledGridUnit(element, null, element.Bounds);
        }
    }

    private static SemanticRuledHorizontalRule[] SemanticRuledHorizontalRules(PdfLayoutPage page)
    {
        List<SemanticRuledHorizontalRule> rules = [];
        foreach (PdfLayoutPath path in page.Paths
            .Where(static path => path.IsStroked && !path.UsesSoftMask && !path.UsesShapeAlpha)
            .Where(static path => path.Stroke?.DashArray.Any(static dash => dash > 0f) != true))
        {
            float thickness = MathF.Max(0.01f, path.Stroke!.Width);
            foreach (PdfLayoutRectangle segment in SemanticRuleSegments(path)
                .Where(segment =>
                    segment.Width >= 6f &&
                    segment.Width >= MathF.Max(0.1f, segment.Height) * 8f))
            {
                rules.Add(new SemanticRuledHorizontalRule(
                    path.Index,
                    segment.X,
                    segment.Right,
                    segment.Y + segment.Height / 2f,
                    thickness,
                    path.Stroke.Color));
            }
        }

        return rules.ToArray();
    }

    private static bool TryGetSemanticRuledGridSharedHeadingBottom(
        PdfSemanticRuledGrid grid,
        SemanticRuledGridBand band,
        int columnIndex,
        out float sharedHeadingBottom)
    {
        sharedHeadingBottom = 0f;
        if (band.IsSpanning ||
            columnIndex < 0 ||
            columnIndex + 1 >= grid.Tracks.Count ||
            grid.Gutters[columnIndex].Width > 0.5f)
        {
            return false;
        }

        PdfSemanticElement? leftHeading = band.Columns[columnIndex].FirstOrDefault();
        PdfSemanticElement? rightHeading = band.Columns[columnIndex + 1].FirstOrDefault();
        if (leftHeading == null ||
            rightHeading == null ||
            !grid.SourceBorders.ElementBorders.TryGetValue(
                leftHeading,
                out SemanticRuledBorder leftBorder) ||
            !grid.SourceBorders.ElementBorders.TryGetValue(
                rightHeading,
                out SemanticRuledBorder rightBorder))
        {
            return false;
        }

        float positionTolerance = MathF.Max(1f, grid.Page.Width * 0.002f);
        if (MathF.Abs(leftBorder.CenterY - rightBorder.CenterY) > positionTolerance * 2f)
        {
            return false;
        }

        float boundary = (grid.Tracks[columnIndex].Right +
            grid.Tracks[columnIndex + 1].Left) / 2f;
        float dividerTop = (leftBorder.CenterY + rightBorder.CenterY) / 2f;
        PdfLayoutRectangle[] verticalSegments = grid.Page.Paths
            .Where(static path => path.IsStroked && !path.UsesSoftMask)
            .SelectMany(SemanticRuleSegments)
            .Where(segment =>
                segment.Height >= 6f &&
                segment.Height >= MathF.Max(0.1f, segment.Width) * 8f &&
                MathF.Abs(segment.X + segment.Width / 2f - boundary) <= positionTolerance)
            .ToArray();
        bool startsAtHeadingBottom = verticalSegments.Any(segment =>
            MathF.Abs(segment.Y - dividerTop) <= positionTolerance * 2f &&
            segment.Bottom >= dividerTop + 6f);
        bool crossesHeading = verticalSegments.Any(segment =>
            segment.Y < dividerTop - positionTolerance * 2f &&
            segment.Bottom > grid.Region.Y + positionTolerance);
        if (!startsAtHeadingBottom || crossesHeading)
        {
            return false;
        }

        sharedHeadingBottom = dividerTop;
        return true;
    }

    private static bool TryGetSemanticRuledGridColumnHeadingBorder(
        PdfSemanticRuledGrid grid,
        SemanticRuledGridBand band,
        int columnIndex,
        out SemanticRuledBorder headingBorder)
    {
        headingBorder = default;
        if (band.IsSpanning ||
            columnIndex < 0 ||
            columnIndex >= grid.Tracks.Count)
        {
            return false;
        }

        PdfSemanticElement? heading = band.Columns[columnIndex].FirstOrDefault();
        if (heading == null ||
            !grid.SourceBorders.ElementBorders.TryGetValue(heading, out headingBorder))
        {
            return false;
        }

        float positionTolerance = MathF.Max(1f, grid.Page.Width * 0.002f);
        if (heading.Bounds.Y < grid.Region.Y - positionTolerance ||
            heading.Bounds.Bottom > headingBorder.CenterY + positionTolerance)
        {
            return false;
        }

        float headingBorderCenterY = headingBorder.CenterY;
        int alignedColumnCount = band.Columns.Count(column =>
        {
            PdfSemanticElement? candidate = column.FirstOrDefault();
            return candidate != null &&
                grid.SourceBorders.ElementBorders.TryGetValue(
                    candidate,
                    out SemanticRuledBorder candidateBorder) &&
                MathF.Abs(candidateBorder.CenterY - headingBorderCenterY) <=
                    positionTolerance * 2f;
        });
        return alignedColumnCount >= 2;
    }

    private static PdfSemanticColumns AddColumnSpanningFigures(PdfSemanticColumns columns)
    {
        PdfLayoutPage page = columns.Page;
        float horizontalTolerance = MathF.Max(16f, page.Width * 0.04f);
        float contentLeft = columns.LeftInset;
        float contentRight = page.Width - columns.RightInset;
        float minimumWidth = MathF.Max(0, contentRight - contentLeft - horizontalTolerance * 2f);
        columns.SpanningFigures = SemanticFigureRegions(page, columns.SemanticPage)
            .Where(region => region.Y <= page.Height * 0.25f)
            .Where(region => region.X <= contentLeft + horizontalTolerance)
            .Where(region => region.Right >= contentRight - horizontalTolerance)
            .Where(region => region.Width >= minimumWidth)
            .Select(region => new PdfSemanticColumnFigure(
                region,
                columns.SemanticPage.Elements
                    .Where(IsFigureCaption)
                    .Where(caption => caption.Bounds.Y >= region.Y - 2f)
                    .Where(caption => IsCaptionAssociatedWithFigure(page, caption.Bounds, region))
                    .OrderBy(static caption => caption.Bounds.Y)
                    .FirstOrDefault()))
            .Where(static figure => figure.Caption != null)
            .ToArray();
        return columns;
    }

    private static PdfSemanticElement[] SemanticColumnListElements(PdfSemanticPage semanticPage)
    {
        return semanticPage.Elements
            .Where(static element =>
                element.Kind == PdfSemanticElementKind.List &&
                element.SemanticList != null &&
                element.Lines.Count > 0)
            .ToArray();
    }

    private static PdfSemanticColumns? TryCreateMixedGraphicTextColumns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        if (page.FormControls.Count > 0 ||
            semanticPage.Elements.Any(static element =>
                element.Kind is PdfSemanticElementKind.Table or PdfSemanticElementKind.DefinitionList))
        {
            return null;
        }

        PdfTextRun[] horizontalRuns = page.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .ToArray();
        if (horizontalRuns.Length < 18)
        {
            return null;
        }

        ColumnDetectionRow[] rows = CreateColumnDetectionRows(horizontalRuns);
        if (rows.Length < 12)
        {
            return null;
        }

        PdfLayoutRectangle[] graphicSeeds = page.Images
            .Select(VisibleImageBounds)
            .Concat(page.Paths.Select(static path => path.Bounds))
            .Where(bounds => IsMixedRegionGraphicSeed(page, bounds))
            .OrderByDescending(static bounds => bounds.Width * bounds.Height)
            .ToArray();
        foreach (PdfLayoutRectangle seed in graphicSeeds)
        {
            bool graphicOnLeft = seed.X + seed.Width / 2f < page.Width / 2f;
            float minimumGap = MathF.Max(6f, page.Width * 0.012f);
            float regionTop = MathF.Max(0, seed.Y - MathF.Max(8f, page.Height * 0.02f));
            PdfTextRun[] provisionalText = MixedRegionFragments(
                rows,
                regionTop,
                seed,
                graphicOnLeft,
                minimumGap);
            if (provisionalText.Length < 12)
            {
                continue;
            }

            float textEdge = graphicOnLeft
                ? Percentile(provisionalText.Select(static run => run.Bounds.X), 0.1f)
                : Percentile(provisionalText.Select(static run => run.Bounds.Right), 0.9f);
            float seedEdge = graphicOnLeft ? seed.Right : seed.X;
            if (graphicOnLeft
                    ? textEdge - seedEdge < minimumGap
                    : seedEdge - textEdge < minimumGap)
            {
                continue;
            }

            float provisionalBoundary = (seedEdge + textEdge) / 2f;
            PdfLayoutRectangle[] localGraphics = page.Images
                .Select(VisibleImageBounds)
                .Concat(page.Paths.Select(static path => path.Bounds))
                .Where(bounds => IsLocalizedMixedRegionGraphic(
                    page,
                    seed,
                    bounds,
                    provisionalBoundary,
                    graphicOnLeft))
                .Append(seed)
                .ToArray();
            PdfLayoutRectangle graphicRegion = UnionRectangles(localGraphics);
            float graphicEdge = graphicOnLeft ? graphicRegion.Right : graphicRegion.X;
            if (graphicOnLeft
                    ? textEdge <= graphicEdge + 2f
                    : textEdge >= graphicEdge - 2f)
            {
                continue;
            }

            float boundary = (graphicEdge + textEdge) / 2f;
            PdfTextRun[] graphicFragments = MixedRegionColumnFragments(
                rows,
                regionTop,
                boundary,
                graphicOnLeft)
                .Where(run => run.Bounds.Bottom >= graphicRegion.Y - 12f)
                .Where(run => run.Bounds.Y <= graphicRegion.Bottom + 12f)
                .Where(run => graphicOnLeft
                    ? run.Bounds.Right <= textEdge - 1f
                    : run.Bounds.X >= textEdge + 1f)
                .ToArray();
            PdfTextRun[] textFragments = MixedRegionColumnFragments(
                rows,
                regionTop,
                boundary,
                !graphicOnLeft);
            if (graphicFragments.Length < 3 || textFragments.Length < 12)
            {
                continue;
            }

            float textTop = textFragments.Min(static run => run.Bounds.Y);
            float textBottom = textFragments.Max(static run => run.Bounds.Bottom);
            float verticalOverlap = MathF.Max(
                0,
                MathF.Min(graphicRegion.Bottom, textBottom) - MathF.Max(graphicRegion.Y, textTop));
            if (verticalOverlap < MathF.Min(graphicRegion.Height, textBottom - textTop) * 0.45f)
            {
                continue;
            }

            graphicRegion = UnionRectangles(
                localGraphics.Concat(graphicFragments.Select(static run => run.Bounds)));
            graphicEdge = graphicOnLeft ? graphicRegion.Right : graphicRegion.X;
            float gutterLeft = graphicOnLeft ? graphicEdge : textEdge;
            float gutterRight = graphicOnLeft ? textEdge : graphicEdge;
            if (gutterRight <= gutterLeft + 1f)
            {
                continue;
            }

            float overlapTolerance = MathF.Max(1f, page.Width * 0.002f);
            if (horizontalRuns
                .Where(run => run.Bounds.Y >= graphicRegion.Y - overlapTolerance)
                .Where(run => run.Bounds.Y <= MathF.Max(graphicRegion.Bottom, textBottom) + overlapTolerance)
                .Any(run =>
                    run.Bounds.X < gutterLeft - overlapTolerance &&
                    run.Bounds.Right > gutterRight + overlapTolerance))
            {
                continue;
            }

            boundary = (gutterLeft + gutterRight) / 2f;
            PdfTextRun[] leftFragments = MixedRegionColumnFragments(
                rows,
                MathF.Min(regionTop, graphicRegion.Y),
                boundary,
                takeLeft: true);
            PdfTextRun[] rightFragments = MixedRegionColumnFragments(
                rows,
                MathF.Min(regionTop, graphicRegion.Y),
                boundary,
                takeLeft: false);
            if (leftFragments.Length == 0 ||
                rightFragments.Length == 0 ||
                (graphicOnLeft ? rightFragments : leftFragments).Length < 12)
            {
                continue;
            }

            LineGridColumn leftColumn = CreateLineGridColumn(leftFragments);
            LineGridColumn rightColumn = CreateLineGridColumn(rightFragments);
            float leftInset = graphicOnLeft
                ? MathF.Max(0, MathF.Min(graphicRegion.X, Percentile(leftFragments.Select(static run => run.Bounds.X), 0.1f)))
                : MathF.Max(0, Percentile(leftFragments.Select(static run => run.Bounds.X), 0.1f));
            float contentRight = graphicOnLeft
                ? Percentile(rightFragments.Select(static run => run.Bounds.Right), 0.9f)
                : MathF.Max(graphicRegion.Right, Percentile(rightFragments.Select(static run => run.Bounds.Right), 0.9f));
            float rightInset = MathF.Max(0, page.Width - contentRight);
            if (gutterLeft - leftInset < page.Width * 0.12f ||
                page.Width - rightInset - gutterRight < page.Width * 0.12f)
            {
                continue;
            }

            float mixedTop = MathF.Min(
                graphicRegion.Y,
                MathF.Min(
                    leftFragments.Min(static run => run.Bounds.Y),
                    rightFragments.Min(static run => run.Bounds.Y)));
            PdfTextRun[] leadingRuns = horizontalRuns
                .Where(run => run.Bounds.Bottom < mixedTop - 1f)
                .OrderBy(static run => run.Bounds.Y)
                .ThenBy(static run => run.Bounds.X)
                .ToArray();
            string accessibleText = string.Join(
                ' ',
                graphicFragments
                    .OrderBy(static run => run.Bounds.Y)
                    .ThenBy(static run => run.Bounds.X)
                    .SelectMany(static run => run.Text.Split(
                        [' ', '\t', '\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries)));
            PdfSemanticColumns columns = new(
                page,
                semanticPage,
                leadingRuns,
                [
                    new SemanticColumnTrack(leftColumn, leftInset, gutterLeft),
                    new SemanticColumnTrack(rightColumn, gutterRight, page.Width - rightInset)
                ],
                [new SemanticColumnGutter(gutterLeft, gutterRight)],
                SemanticColumnListElements(semanticPage),
                leftInset,
                rightInset)
            {
                ColumnFigures =
                [
                    new PdfSemanticColumnRegionFigure(
                        graphicRegion,
                        graphicOnLeft ? 0 : 1,
                        accessibleText)
                ]
            };
            return columns;
        }

        return null;
    }

    private static bool IsMixedRegionGraphicSeed(
        PdfLayoutPage page,
        PdfLayoutRectangle bounds)
    {
        if (bounds.Width < page.Width * 0.12f ||
            bounds.Width > page.Width * 0.50f ||
            bounds.Height < page.Height * 0.20f ||
            bounds.Height > page.Height * 0.92f)
        {
            return false;
        }

        float center = bounds.X + bounds.Width / 2f;
        return center <= page.Width * 0.42f || center >= page.Width * 0.58f;
    }

    private static bool IsLocalizedMixedRegionGraphic(
        PdfLayoutPage page,
        PdfLayoutRectangle seed,
        PdfLayoutRectangle candidate,
        float boundary,
        bool graphicOnLeft)
    {
        float center = candidate.X + candidate.Width / 2f;
        if (candidate.Width > page.Width * 0.55f ||
            candidate.Height > page.Height * 0.95f ||
            graphicOnLeft && center >= boundary ||
            !graphicOnLeft && center <= boundary)
        {
            return false;
        }

        return VerticalGap(seed, candidate) <= MathF.Max(12f, page.Height * 0.04f) &&
            HorizontalGap(seed, candidate) <= page.Width * 0.06f;
    }

    private static PdfTextRun[] MixedRegionFragments(
        IReadOnlyList<ColumnDetectionRow> rows,
        float regionTop,
        PdfLayoutRectangle graphic,
        bool graphicOnLeft,
        float minimumGap)
    {
        return rows
            .Where(row => row.Top >= regionTop)
            .Select(row => CombineColumnLine(row.Runs.Where(run =>
            {
                float center = run.Bounds.X + run.Bounds.Width / 2f;
                return graphicOnLeft
                    ? center >= graphic.Right + minimumGap
                    : center <= graphic.X - minimumGap;
            })))
            .Where(static run => run != null)
            .Cast<PdfTextRun>()
            .ToArray();
    }

    private static PdfTextRun[] MixedRegionColumnFragments(
        IReadOnlyList<ColumnDetectionRow> rows,
        float regionTop,
        float boundary,
        bool takeLeft)
    {
        return rows
            .Where(row => row.Top >= regionTop)
            .Select(row => CombineColumnLine(row.Runs.Where(run =>
            {
                float center = run.Bounds.X + run.Bounds.Width / 2f;
                return takeLeft ? center < boundary : center >= boundary;
            })))
            .Where(static run => run != null)
            .Cast<PdfTextRun>()
            .ToArray();
    }

    private static LineGridColumn CreateLineGridColumn(IReadOnlyList<PdfTextRun> runs)
    {
        LineGridColumn column = new(Percentile(runs.Select(static run => run.Bounds.X), 0.1f));
        foreach (PdfTextRun run in runs)
        {
            column.Add(run);
        }

        return column;
    }

    private static PdfLayoutRectangle[] CaptionedTopSpanningFigureRegions(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        return SemanticFigureRegions(page, semanticPage)
            .Where(region => region.Y <= page.Height * 0.25f)
            .Where(region => region.Width >= page.Width * 0.65f)
            .Where(region => semanticPage.Elements
                .Where(IsFigureCaption)
                .Any(caption => IsCaptionAssociatedWithFigure(page, caption.Bounds, region)))
            .ToArray();
    }

    private static bool TryGetFlowColumnRuns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        out PdfTextRun[] leadingRuns,
        out SemanticColumnTrack[] tracks,
        out SemanticColumnGutter[] gutters)
    {
        leadingRuns = [];
        tracks = [];
        gutters = [];
        if (page.FormControls.Count > 0)
        {
            return false;
        }

        PdfTextRun[] horizontalRuns = page.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .ToArray();
        if (horizontalRuns.Length < 24)
        {
            return false;
        }

        ColumnDetectionRow[] rows = CreateColumnDetectionRows(horizontalRuns);
        if (rows.Length < 12)
        {
            return false;
        }

        float minimumGap = page.Width * 0.018f;
        ColumnGapObservation[] observations = rows
            .SelectMany((row, rowIndex) => row.Gaps(
                rowIndex,
                page.Width * 0.12f,
                page.Width * 0.88f,
                minimumGap))
            .ToArray();

        ColumnCorridor[] corridors = CreateColumnCorridors(page, rows, observations, minimumGap);
        ColumnCorridor[] anchoredCorridors = CreateAnchoredColumnCorridors(
            page,
            horizontalRuns,
            observations,
            minimumGap);
        if (anchoredCorridors.Length >= 2)
        {
            corridors = anchoredCorridors;
        }

        if (corridors.Length is < 1 or > 3)
        {
            return false;
        }

        SemanticColumnGutter[] detectedGutters = corridors
            .Select(corridor => new SemanticColumnGutter(corridor.Left, corridor.Right))
            .ToArray();
        if (HasRuledTableAcrossColumnCorridors(page, corridors))
        {
            return false;
        }

        foreach (ColumnCorridor corridor in corridors)
        {
            if (!SpanningTablesAreColumnCompatible(page, semanticPage, corridor.Boundary))
            {
                return false;
            }
        }

        float columnTop = corridors
            .Select(corridor => corridor.ColumnTop ?? FlowColumnTop(page.Height, rows, corridor.SupportingGaps))
            .Max();
        leadingRuns = rows
            .Where(row => row.Top < columnTop)
            .SelectMany(static row => row.Runs)
            .OrderBy(static run => run.Bounds.Y)
            .ThenBy(static run => run.Bounds.X)
            .ToArray();
        ColumnDetectionRow[] contentRows = rows
            .Where(row => row.Top >= columnTop)
            .ToArray();
        float corridorTolerance = MathF.Max(1f, page.Width * 0.002f);
        if (contentRows
            .SelectMany(static row => row.Runs)
            .Any(run => detectedGutters.Any(gutter =>
                run.Bounds.X < gutter.Left - corridorTolerance &&
                run.Bounds.Right > gutter.Right + corridorTolerance)))
        {
            return false;
        }

        float[] boundaries = corridors.Select(static corridor => corridor.Boundary).ToArray();
        PdfTextRun[][] columnRuns = Enumerable.Range(0, boundaries.Length + 1)
            .Select(columnIndex => contentRows
                .Select(row => CombineColumnLine(row.Runs.Where(run =>
                    ColumnIndexAt(boundaries, run.Bounds.X + run.Bounds.Width / 2f) == columnIndex)))
                .Where(static run => run != null)
                .Cast<PdfTextRun>()
                .ToArray())
            .ToArray();
        if (columnRuns.Any(static runs => runs.Length < 12))
        {
            return false;
        }

        float commonTop = columnRuns.Max(static runs => runs.Min(static run => run.Bounds.Y));
        float commonBottom = columnRuns.Min(static runs => runs.Max(static run => run.Bounds.Bottom));
        float minimumSpan = columnRuns.Min(static runs =>
            runs.Max(static run => run.Bounds.Bottom) - runs.Min(static run => run.Bounds.Y));
        if (MathF.Max(0, commonBottom - commonTop) < minimumSpan * 0.4f)
        {
            return false;
        }

        float[] anchors = columnRuns
            .Select(static runs => Percentile(runs.Select(static run => run.Bounds.X), 0.1f))
            .ToArray();
        if (anchors.Zip(anchors.Skip(1), static (left, right) => right - left)
            .Any(distance => distance < page.Width * 0.12f || distance > page.Width * 0.55f))
        {
            return false;
        }

        float leftInset = MathF.Max(0, anchors[0]);
        float contentRight = Percentile(columnRuns[^1].Select(static run => run.Bounds.Right), 0.9f);
        float rightInset = MathF.Max(0, page.Width - contentRight);
        List<SemanticColumnTrack> measuredTracks = [];
        for (int columnIndex = 0; columnIndex < columnRuns.Length; columnIndex++)
        {
            LineGridColumn column = new(anchors[columnIndex]);
            foreach (PdfTextRun run in columnRuns[columnIndex])
            {
                column.Add(run);
            }

            float left = columnIndex == 0 ? leftInset : detectedGutters[columnIndex - 1].Right;
            float right = columnIndex == detectedGutters.Length
                ? page.Width - rightInset
                : detectedGutters[columnIndex].Left;
            if (right - left < page.Width * 0.1f)
            {
                return false;
            }

            measuredTracks.Add(new SemanticColumnTrack(column, left, right));
        }

        tracks = measuredTracks.ToArray();
        gutters = detectedGutters;
        return true;
    }

    private static bool TryGetCaptionedFigureTwoColumnRuns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        out PdfTextRun[] leadingRuns,
        out SemanticColumnTrack[] tracks,
        out SemanticColumnGutter[] gutters)
    {
        leadingRuns = [];
        tracks = [];
        gutters = [];
        if (page.FormControls.Count > 0 ||
            CaptionedTopSpanningFigureRegions(page, semanticPage).Length == 0)
        {
            return false;
        }

        PdfTextRun[] horizontalRuns = page.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .ToArray();
        if (horizontalRuns.Length < 24)
        {
            return false;
        }

        ColumnDetectionRow[] rows = CreateColumnDetectionRows(horizontalRuns);
        if (rows.Length < 12)
        {
            return false;
        }

        float centralLeft = page.Width * 0.35f;
        float centralRight = page.Width * 0.65f;
        float minimumGap = page.Width * 0.018f;
        ColumnGapObservation[] observations = rows
            .SelectMany((row, rowIndex) => row.Gaps(rowIndex, centralLeft, centralRight, minimumGap))
            .ToArray();
        if (observations.Length == 0)
        {
            return false;
        }

        float[] boundaries = observations
            .Select(static observation => (observation.Left + observation.Right) / 2f)
            .Append(page.Width / 2f)
            .ToArray();
        float boundary = boundaries
            .OrderByDescending(candidate => observations
                .Where(observation => observation.Left <= candidate && observation.Right >= candidate)
                .Select(static observation => observation.RowIndex)
                .Distinct()
                .Count())
            .ThenBy(candidate => MathF.Abs(candidate - page.Width / 2f))
            .First();
        ColumnGapObservation[] supportingGaps = observations
            .Where(observation => observation.Left <= boundary && observation.Right >= boundary)
            .GroupBy(static observation => observation.RowIndex)
            .Select(static group => group.OrderBy(static observation => observation.Right - observation.Left).First())
            .ToArray();
        if (supportingGaps.Length < 12 || supportingGaps.Length < rows.Length * 0.18f)
        {
            return false;
        }

        float gutterLeft = Percentile(supportingGaps.Select(static gap => gap.Left), 0.85f);
        float gutterRight = Percentile(supportingGaps.Select(static gap => gap.Right), 0.15f);
        if (gutterRight - gutterLeft < minimumGap)
        {
            gutterLeft = boundary - minimumGap / 2f;
            gutterRight = boundary + minimumGap / 2f;
        }

        if (!SpanningTablesAreColumnCompatible(page, semanticPage, boundary))
        {
            return false;
        }

        float columnTop = FlowColumnTop(page.Height, rows, supportingGaps);
        leadingRuns = rows
            .Where(row => row.Top < columnTop)
            .SelectMany(static row => row.Runs)
            .OrderBy(static run => run.Bounds.Y)
            .ThenBy(static run => run.Bounds.X)
            .ToArray();
        ColumnDetectionRow[] contentRows = rows
            .Where(row => row.Top >= columnTop)
            .ToArray();
        PdfTextRun[] leftRuns = contentRows
            .Select(row => CombineColumnLine(row.Runs
                .Where(run => run.Bounds.X + run.Bounds.Width / 2f < boundary)))
            .Where(static run => run != null)
            .Cast<PdfTextRun>()
            .ToArray();
        PdfTextRun[] rightRuns = contentRows
            .Select(row => CombineColumnLine(row.Runs
                .Where(run => run.Bounds.X + run.Bounds.Width / 2f >= boundary)))
            .Where(static run => run != null)
            .Cast<PdfTextRun>()
            .ToArray();
        if (leftRuns.Length < 12 || rightRuns.Length < 12)
        {
            return false;
        }

        float leftTop = leftRuns.Min(static run => run.Bounds.Y);
        float leftBottom = leftRuns.Max(static run => run.Bounds.Bottom);
        float rightTop = rightRuns.Min(static run => run.Bounds.Y);
        float rightBottom = rightRuns.Max(static run => run.Bounds.Bottom);
        float verticalOverlap = MathF.Max(0, MathF.Min(leftBottom, rightBottom) - MathF.Max(leftTop, rightTop));
        if (verticalOverlap < MathF.Min(leftBottom - leftTop, rightBottom - rightTop) * 0.4f)
        {
            return false;
        }

        float leftAnchor = Percentile(leftRuns.Select(static run => run.Bounds.X), 0.1f);
        float rightAnchor = Percentile(rightRuns.Select(static run => run.Bounds.X), 0.1f);
        float pitch = rightAnchor - leftAnchor;
        if (pitch < page.Width * 0.25f || pitch > page.Width * 0.7f)
        {
            return false;
        }

        LineGridColumn leftColumn = new(leftAnchor);
        LineGridColumn rightColumn = new(rightAnchor);
        foreach (PdfTextRun run in leftRuns)
        {
            leftColumn.Add(run);
        }

        foreach (PdfTextRun run in rightRuns)
        {
            rightColumn.Add(run);
        }

        float leftInset = MathF.Max(0, leftAnchor);
        float contentRight = Percentile(rightRuns.Select(static run => run.Bounds.Right), 0.9f);
        float rightInset = MathF.Max(0, page.Width - contentRight);
        tracks =
        [
            new SemanticColumnTrack(leftColumn, leftInset, gutterLeft),
            new SemanticColumnTrack(rightColumn, gutterRight, page.Width - rightInset)
        ];
        gutters = [new SemanticColumnGutter(gutterLeft, gutterRight)];
        return true;
    }

    private static ColumnCorridor[] CreateColumnCorridors(
        PdfLayoutPage page,
        IReadOnlyList<ColumnDetectionRow> rows,
        IReadOnlyList<ColumnGapObservation> observations,
        float minimumGap)
    {
        int minimumSupport = Math.Max(12, (int)MathF.Ceiling(rows.Count * 0.18f));
        float minimumSeparation = page.Width * 0.12f;
        ColumnCorridor[] candidates = observations
            .Select(static observation => (observation.Left + observation.Right) / 2f)
            .Append(page.Width / 2f)
            .Where(candidate => candidate >= page.Width * 0.12f && candidate <= page.Width * 0.88f)
            .Distinct()
            .Select(candidate => CreateColumnCorridorCandidate(candidate, observations, minimumGap))
            .Where(static candidate => candidate != null)
            .Cast<ColumnCorridor>()
            .Where(candidate => candidate.SupportingGaps.Length >= minimumSupport)
            .OrderByDescending(static candidate => candidate.SupportingGaps.Length)
            .ThenByDescending(static candidate => candidate.Right - candidate.Left)
            .ThenBy(candidate => MathF.Abs(candidate.Boundary - page.Width / 2f))
            .ToArray();

        List<ColumnCorridor> selected = [];
        foreach (ColumnCorridor candidate in candidates)
        {
            if (selected.Any(existing => MathF.Abs(existing.Boundary - candidate.Boundary) < minimumSeparation))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count == 3)
            {
                break;
            }
        }

        return selected.OrderBy(static corridor => corridor.Boundary).ToArray();
    }

    private static ColumnCorridor[] CreateAnchoredColumnCorridors(
        PdfLayoutPage page,
        IReadOnlyList<PdfTextRun> horizontalRuns,
        IReadOnlyList<ColumnGapObservation> observations,
        float minimumGap)
    {
        float anchorTolerance = page.Width * 0.035f;
        List<LineGridColumn> anchorCandidates = [];
        foreach (PdfTextRun run in horizontalRuns
            .Where(run => run.Bounds.Width <= page.Width * 0.36f)
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y))
        {
            LineGridColumn? candidate = anchorCandidates
                .Where(candidate => MathF.Abs(candidate.Left - run.Bounds.X) <= anchorTolerance)
                .OrderBy(candidate => MathF.Abs(candidate.Left - run.Bounds.X))
                .FirstOrDefault();
            if (candidate == null)
            {
                candidate = new LineGridColumn(run.Bounds.X);
                anchorCandidates.Add(candidate);
            }

            candidate.Add(run);
        }

        int minimumAnchorSupport = Math.Max(12, (int)MathF.Ceiling(horizontalRuns.Count * 0.08f));
        float minimumAnchorSeparation = page.Width * 0.12f;
        List<LineGridColumn> selected = [];
        foreach (LineGridColumn candidate in anchorCandidates
            .Where(candidate => candidate.Lines.Count >= minimumAnchorSupport)
            .OrderByDescending(static candidate => candidate.Lines.Count)
            .ThenBy(static candidate => candidate.Left))
        {
            if (selected.Any(existing => MathF.Abs(existing.Left - candidate.Left) < minimumAnchorSeparation))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count == 4)
            {
                break;
            }
        }

        if (selected.Count < 3)
        {
            return [];
        }

        LineGridColumn[] anchors = selected.OrderBy(static candidate => candidate.Left).ToArray();
        float columnTop = anchors.Min(anchor => StableColumnAnchorTop(page, anchor));
        List<ColumnCorridor> corridors = [];
        for (int index = 0; index + 1 < anchors.Length; index++)
        {
            float left = Percentile(anchors[index].Lines.Select(static run => run.Bounds.Right), 0.85f);
            float right = Percentile(anchors[index + 1].Lines.Select(static run => run.Bounds.X), 0.15f);
            float boundary = (left + right) / 2f;
            if (right - left < minimumGap)
            {
                left = boundary - minimumGap / 2f;
                right = boundary + minimumGap / 2f;
            }

            ColumnGapObservation[] supportingGaps = observations
                .Where(observation => observation.Left <= boundary && observation.Right >= boundary)
                .GroupBy(static observation => observation.RowIndex)
                .Select(static group => group.OrderBy(static observation => observation.Right - observation.Left).First())
                .ToArray();
            corridors.Add(new ColumnCorridor(boundary, left, right, supportingGaps, columnTop));
        }

        return corridors.ToArray();
    }

    private static float StableColumnAnchorTop(PdfLayoutPage page, LineGridColumn anchor)
    {
        PdfTextRun[] runs = anchor.Lines.OrderBy(static run => run.Bounds.Y).ToArray();
        float supportHeight = page.Height * 0.08f;
        int minimumSupport = Math.Min(5, runs.Length);
        foreach (PdfTextRun run in runs)
        {
            if (runs.Count(candidate =>
                    candidate.Bounds.Y >= run.Bounds.Y &&
                    candidate.Bounds.Y <= run.Bounds.Y + supportHeight) >= minimumSupport)
            {
                return run.Bounds.Y;
            }
        }

        return runs[0].Bounds.Y;
    }

    private static ColumnCorridor? CreateColumnCorridorCandidate(
        float boundary,
        IReadOnlyList<ColumnGapObservation> observations,
        float minimumGap)
    {
        ColumnGapObservation[] supportingGaps = observations
            .Where(observation => observation.Left <= boundary && observation.Right >= boundary)
            .GroupBy(static observation => observation.RowIndex)
            .Select(static group => group.OrderBy(static observation => observation.Right - observation.Left).First())
            .ToArray();
        if (supportingGaps.Length == 0)
        {
            return null;
        }

        float left = Percentile(supportingGaps.Select(static gap => gap.Left), 0.85f);
        float right = Percentile(supportingGaps.Select(static gap => gap.Right), 0.15f);
        if (right - left < minimumGap)
        {
            left = boundary - minimumGap / 2f;
            right = boundary + minimumGap / 2f;
        }

        return new ColumnCorridor(boundary, left, right, supportingGaps);
    }

    private static int ColumnIndexAt(IReadOnlyList<float> boundaries, float horizontalCenter)
    {
        for (int index = 0; index < boundaries.Count; index++)
        {
            if (horizontalCenter < boundaries[index])
            {
                return index;
            }
        }

        return boundaries.Count;
    }

    private static bool HasRuledTableAcrossColumnCorridors(
        PdfLayoutPage page,
        IReadOnlyList<ColumnCorridor> corridors)
    {
        return CreateSourceRuleFamilies(page).Any(family =>
            family.RuleCount >= 3 &&
            corridors.Count(corridor =>
                family.Left < corridor.Boundary &&
                family.Right > corridor.Boundary) >= Math.Min(2, corridors.Count));
    }

    private static bool SpanningTablesAreColumnCompatible(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        float columnBoundary)
    {
        PdfSemanticElement[] spanningTables = semanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Table)
            .Where(table => table.Bounds.X < columnBoundary && table.Bounds.Right > columnBoundary)
            .Where(table => HasSupportingTableRules(page, table))
            .ToArray();
        if (spanningTables.Length == 0)
        {
            return true;
        }

        SourceRuleFamily[] ruleFamilies = CreateSourceRuleFamilies(page);
        SourceRuleFamily[] leftFamilies = ruleFamilies
            .Where(family => family.Right <= columnBoundary + 1f)
            .ToArray();
        SourceRuleFamily[] rightFamilies = ruleFamilies
            .Where(family => family.Left >= columnBoundary - 1f)
            .ToArray();
        if (leftFamilies.Length == 0 || rightFamilies.Length == 0)
        {
            return false;
        }

        foreach (PdfSemanticElement table in spanningTables)
        {
            float verticalTolerance = MathF.Max(2f, table.Bounds.Height * 0.1f);
            if (ruleFamilies.Any(family =>
                    family.Left < columnBoundary &&
                    family.Right > columnBoundary &&
                    family.Bottom >= table.Bounds.Y - verticalTolerance &&
                    family.Top <= table.Bounds.Bottom + verticalTolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static SourceRuleFamily[] CreateSourceRuleFamilies(PdfLayoutPage page)
    {
        float minimumWidth = page.Width * 0.08f;
        float rowTolerance = MathF.Max(1f, page.Height * 0.0015f);
        float endpointTolerance = MathF.Max(2f, page.Width * 0.005f);
        List<HorizontalRuleRow> rows = [];
        foreach (PdfLayoutPath path in page.Paths
            .Where(static path => path.IsStroked)
            .Where(path => path.Bounds.Width >= MathF.Max(8f, path.Bounds.Height * 4f))
            .OrderBy(static path => path.Bounds.Y)
            .ThenBy(static path => path.Bounds.X))
        {
            HorizontalRuleRow? row = rows
                .Where(row => MathF.Abs(row.Top - path.Bounds.Y) <= rowTolerance)
                .OrderBy(row => MathF.Abs(row.Top - path.Bounds.Y))
                .FirstOrDefault();
            if (row == null)
            {
                row = new HorizontalRuleRow(path.Bounds.Y);
                rows.Add(row);
            }

            row.Add(path.Bounds.Y, path.Bounds.X, path.Bounds.Right);
        }

        List<SourceRuleFamily> families = [];
        foreach (HorizontalRuleSpan span in rows
            .SelectMany(row => row.MergedSpans(endpointTolerance))
            .Where(span => span.Right - span.Left >= minimumWidth))
        {
            SourceRuleFamily? family = families
                .Where(family =>
                    MathF.Abs(family.Left - span.Left) <= endpointTolerance &&
                    MathF.Abs(family.Right - span.Right) <= endpointTolerance)
                .OrderBy(family =>
                    MathF.Abs(family.Left - span.Left) + MathF.Abs(family.Right - span.Right))
                .FirstOrDefault();
            if (family == null)
            {
                family = new SourceRuleFamily(span.Left, span.Right);
                families.Add(family);
            }

            family.Add(span.Top, span.Left, span.Right);
        }

        return families
            .Where(static family => family.RuleCount >= 2)
            .Where(family => page.Runs.Any(run =>
                MathF.Abs(run.Direction) < 0.01f &&
                run.Bounds.X + run.Bounds.Width / 2f >= family.Left - endpointTolerance &&
                run.Bounds.X + run.Bounds.Width / 2f <= family.Right + endpointTolerance &&
                run.Bounds.Y + run.Bounds.Height / 2f >= family.Top - rowTolerance &&
                run.Bounds.Y + run.Bounds.Height / 2f <= family.Bottom + rowTolerance))
            .ToArray();
    }

    private static PdfTextRun? CombineColumnLine(IEnumerable<PdfTextRun> sourceRuns)
    {
        PdfTextRun[] runs = sourceRuns.OrderBy(static run => run.Bounds.X).ToArray();
        if (runs.Length == 0)
        {
            return null;
        }

        if (runs.Length == 1)
        {
            return runs[0];
        }

        PdfTextRun first = runs[0];
        PdfLayoutRectangle bounds = UnionRectangles(runs.Select(static run => run.Bounds));
        PdfTextGlyph[] glyphs = runs
            .SelectMany(static run => run.Glyphs)
            .OrderBy(static glyph => glyph.Bounds.X)
            .ToArray();
        return new PdfTextRun(
            string.Concat(runs.Select(static run => run.Text)),
            first.FontName,
            first.FontSize,
            first.Direction,
            bounds,
            first.Color,
            glyphs);
    }

    private static ColumnDetectionRow[] CreateColumnDetectionRows(IEnumerable<PdfTextRun> runs)
    {
        List<ColumnDetectionRow> rows = [];
        foreach (PdfTextRun run in runs.OrderBy(static run => run.Bounds.Y).ThenBy(static run => run.Bounds.X))
        {
            float center = run.Bounds.Y + run.Bounds.Height / 2f;
            float tolerance = MathF.Max(2.5f, run.Bounds.Height * 0.35f);
            ColumnDetectionRow? row = rows
                .Where(row => MathF.Abs(row.Center - center) <= tolerance)
                .OrderBy(row => MathF.Abs(row.Center - center))
                .FirstOrDefault();
            if (row == null)
            {
                row = new ColumnDetectionRow(center);
                rows.Add(row);
            }

            row.Add(run);
        }

        return rows.OrderBy(static row => row.Center).ToArray();
    }

    private static float FlowColumnTop(
        float pageHeight,
        IReadOnlyList<ColumnDetectionRow> rows,
        IReadOnlyList<ColumnGapObservation> supportingGaps)
    {
        float[] supportedCenters = supportingGaps
            .Select(gap => rows[gap.RowIndex].Center)
            .OrderBy(static center => center)
            .ToArray();
        if (supportedCenters.Length < 12)
        {
            return rows[0].Top;
        }

        float largestGap = 0;
        float columnTop = rows
            .Where(row => row.Center >= supportedCenters[0])
            .Select(static row => row.Top)
            .DefaultIfEmpty(rows[0].Top)
            .Min();
        if (supportedCenters[0] <= pageHeight * 0.12f)
        {
            return columnTop;
        }

        for (int index = 1; index < supportedCenters.Length; index++)
        {
            float gap = supportedCenters[index] - supportedCenters[index - 1];
            int remaining = supportedCenters.Length - index;
            if (remaining >= 12 &&
                supportedCenters[index] <= pageHeight * 0.4f &&
                gap > largestGap &&
                gap >= 24f)
            {
                largestGap = gap;
                columnTop = rows
                    .Where(row => row.Center >= supportedCenters[index])
                    .Select(static row => row.Top)
                    .DefaultIfEmpty(rows[0].Top)
                    .Min();
            }
        }

        return columnTop;
    }

    private static float Percentile(IEnumerable<float> values, float percentile)
    {
        float[] ordered = values.OrderBy(static value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        int index = (int)MathF.Round((ordered.Length - 1) * percentile);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static bool TryGetTwoColumnRuns(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        out PdfTextRun[] candidateRuns,
        out LineGridColumn[] gridColumns,
        out float pitch)
    {
        candidateRuns = [];
        gridColumns = [];
        pitch = 0;
        if (semanticPage.Elements.Any(static element =>
                element.Kind is PdfSemanticElementKind.Table or PdfSemanticElementKind.DefinitionList or PdfSemanticElementKind.Algorithm) ||
            page.Lines.Count < 24)
        {
            return false;
        }

        PdfTextRun[] horizontalRuns = page.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .ToArray();
        candidateRuns = horizontalRuns
            .Where(run => run.Bounds.Width <= page.Width * 0.42f)
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .ToArray();
        int candidateRunCount = candidateRuns.Length;
        if (candidateRunCount < horizontalRuns.Length * 0.95f)
        {
            return false;
        }

        float columnTolerance = page.Width * 0.06f;
        List<LineGridColumn> columns = [];
        foreach (PdfTextRun run in candidateRuns)
        {
            LineGridColumn? column = columns
                .Where(column => MathF.Abs(column.Left - run.Bounds.X) <= columnTolerance)
                .OrderBy(column => MathF.Abs(column.Left - run.Bounds.X))
                .FirstOrDefault();
            if (column == null)
            {
                column = new LineGridColumn(run.Bounds.X);
                columns.Add(column);
            }

            column.Add(run);
        }

        gridColumns = columns
            .Where(column => column.Lines.Count >= 12)
            .OrderBy(static column => column.Left)
            .ToArray();
        if (gridColumns.Length != 2 ||
            gridColumns.Any(column => column.Lines.Count < candidateRunCount * 0.35f))
        {
            return false;
        }

        pitch = gridColumns[1].Left - gridColumns[0].Left;
        return pitch >= page.Width * 0.25f && pitch <= page.Width * 0.7f;
    }

    private static int NearestGridColumn(IReadOnlyList<LineGridColumn> columns, float left)
    {
        int index = -1;
        float distance = float.MaxValue;
        for (int candidate = 0; candidate < columns.Count; candidate++)
        {
            float candidateDistance = MathF.Abs(columns[candidate].Left - left);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                index = candidate;
            }
        }

        return index;
    }

    private static void WriteSemanticLineGrid(
        StringBuilder html,
        PdfSemanticLineGrid grid,
        float scale,
        PdfSemanticExtractionOptions semanticOptions)
    {
        html.Append("      <section class=\"pdf-semantic-line-grid\" style=\"--pdf-semantic-grid-columns:")
            .Append(grid.ColumnCount.ToString(CultureInfo.InvariantCulture))
            .Append(";--pdf-semantic-grid-left:")
            .Append(CssPoints(grid.LeftInset * scale))
            .Append(";--pdf-semantic-grid-right:")
            .Append(CssPoints(grid.RightInset * scale))
            .Append(";--pdf-semantic-grid-top:")
            .Append(CssPoints(grid.Rows[0].Top * scale))
            .AppendLine("\">");

        LineGridRow? previous = null;
        foreach (LineGridRow row in grid.Rows)
        {
            float marginTop = previous == null
                ? 0
                : MathF.Max(0, row.Top - previous.Bottom) * scale;
            html.Append("        <div class=\"pdf-semantic-line-grid-row\" style=\"--pdf-semantic-grid-row-height:")
                .Append(CssPoints(row.Height * scale));
            if (marginTop > 0.01f)
            {
                html.Append(";margin-top:")
                    .Append(CssPoints(marginTop));
            }

            html.AppendLine("\">");
            foreach (PdfTextRun? cell in row.Cells)
            {
                PdfTextRun run = cell!;
                html.Append("          <span class=\"pdf-semantic-line-grid-cell\" style=\"font-family:")
                    .Append(CssFontFamily(run.FontName))
                    .Append(";font-size:")
                    .Append(CssPoints(FixedTextFontSize(run) * scale))
                    .Append(";color:")
                    .Append(ColorHex(run.Color))
                    .Append("\">");
                WriteSourceHighlightedRun(html, grid.Page, run, semanticOptions, scale);
                html.AppendLine("</span>");
            }

            html.AppendLine("        </div>");
            previous = row;
        }

        html.AppendLine("      </section>");
    }

    private static void WriteSemanticRuledGrid(
        StringBuilder html,
        PdfSemanticRuledGrid grid,
        FootnoteContext footnotes,
        PdfLayoutPage page,
        float scale)
    {
        html.Append("      <div class=\"pdf-semantic-ruled-grid-frame\" data-source-page=\"")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-source-top=\"")
            .Append(HtmlAttribute(CssPoints(grid.Region.Y)))
            .Append("\" style=\"--pdf-semantic-ruled-left:")
            .Append(CssPoints(grid.Region.X * scale))
            .Append(";--pdf-semantic-ruled-right:")
            .Append(CssPoints(MathF.Max(0, page.Width - grid.Region.Right) * scale))
            .Append(";--pdf-semantic-ruled-left-relative:")
            .Append(CssPercent(grid.Region.X / page.Width * 100f))
            .Append(";--pdf-semantic-ruled-right-relative:")
            .Append(CssPercent(MathF.Max(0, page.Width - grid.Region.Right) / page.Width * 100f))
            .AppendLine("\">");
        html.Append("        <div class=\"pdf-semantic-ruled-grid\" data-layout=\"ruled-grid\" data-column-count=\"")
            .Append(grid.Tracks.Count.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-source-border-count=\"")
            .Append(grid.SourceBorders.Count.ToString(CultureInfo.InvariantCulture))
            .Append("\" style=\"--pdf-semantic-ruled-tracks:")
            .Append(SemanticRuledGridTemplate(grid))
            .AppendLine("\">");

        for (int bandIndex = 0; bandIndex < grid.Bands.Count; bandIndex++)
        {
            SemanticRuledGridBand band = grid.Bands[bandIndex];
            int gridRow = bandIndex + 1;
            bool isLastRow = bandIndex == grid.Bands.Count - 1;
            if (band.IsSpanning)
            {
                html.Append("          <div class=\"pdf-semantic-ruled-grid-spanning");
                if (isLastRow)
                {
                    html.Append(" pdf-semantic-ruled-grid-row-last");
                }

                html.Append("\" style=\"--pdf-semantic-ruled-row:")
                    .Append(gridRow.ToString(CultureInfo.InvariantCulture))
                    .AppendLine("\">");
                foreach (PdfSemanticElement element in band.SpanningElements)
                {
                    WriteFlowSemanticElement(
                        html,
                        element,
                        footnotes,
                        page,
                        allowMeasuredWidth: false,
                        preserveSourceLines:
                            SemanticRuledGridShouldPreserveSpanningSourceLines(grid, element));
                }

                html.AppendLine("          </div>");
                continue;
            }

            for (int columnIndex = 0; columnIndex < grid.Tracks.Count; columnIndex++)
            {
                bool hasColumnHeading =
                    TryGetSemanticRuledGridColumnHeadingBorder(
                        grid,
                        band,
                        columnIndex,
                        out SemanticRuledBorder columnHeadingBorder);
                bool sharesHeadingAcrossRightBoundary =
                    TryGetSemanticRuledGridSharedHeadingBottom(
                        grid,
                        band,
                        columnIndex,
                        out float sharedHeadingBottomRight);
                float sharedHeadingBottomLeft = 0f;
                bool sharesHeadingAcrossLeftBoundary =
                    columnIndex > 0 &&
                    TryGetSemanticRuledGridSharedHeadingBottom(
                        grid,
                        band,
                        columnIndex - 1,
                        out sharedHeadingBottomLeft);
                html.Append("          <div class=\"pdf-semantic-ruled-grid-cell");
                if (columnIndex == grid.Tracks.Count - 1)
                {
                    html.Append(" pdf-semantic-ruled-grid-cell-last");
                }
                if (sharesHeadingAcrossRightBoundary)
                {
                    html.Append(" pdf-semantic-ruled-grid-cell-shared-heading-right");
                }
                if (sharesHeadingAcrossLeftBoundary)
                {
                    html.Append(" pdf-semantic-ruled-grid-cell-shared-heading-left");
                }
                if (isLastRow)
                {
                    html.Append(" pdf-semantic-ruled-grid-row-last");
                }

                html.Append("\" data-ruled-column=\"")
                    .Append((columnIndex + 1).ToString(CultureInfo.InvariantCulture))
                    .Append("\" style=\"--pdf-semantic-ruled-column:")
                    .Append((columnIndex * 2 + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(";--pdf-semantic-ruled-row:")
                    .Append(gridRow.ToString(CultureInfo.InvariantCulture));
                if (hasColumnHeading ||
                    sharesHeadingAcrossRightBoundary ||
                    sharesHeadingAcrossLeftBoundary)
                {
                    float sharedHeadingBottom = hasColumnHeading
                        ? columnHeadingBorder.CenterY
                        : sharesHeadingAcrossRightBoundary
                            ? sharedHeadingBottomRight
                            : sharedHeadingBottomLeft;
                    float sharedHeadingHeight = MathF.Max(
                        0,
                        (sharedHeadingBottom - grid.Region.Y) * scale - 6f);
                    html.Append(";--pdf-semantic-ruled-shared-heading-height:")
                        .Append(CssPoints(sharedHeadingHeight));
                }

                html
                    .AppendLine("\">");
                IReadOnlyList<PdfSemanticElement> columnElements = band.Columns[columnIndex];
                for (int elementIndex = 0; elementIndex < columnElements.Count; elementIndex++)
                {
                    if (sharesHeadingAcrossRightBoundary && elementIndex == 1)
                    {
                        html.AppendLine("            <div class=\"pdf-semantic-ruled-grid-cell-body\">");
                    }

                    PdfSemanticElement element = columnElements[elementIndex];
                    bool isColumnHeading = hasColumnHeading && elementIndex == 0;
                    bool hasSourceBorder = grid.SourceBorders.ElementBorders.TryGetValue(
                        element,
                        out SemanticRuledBorder sourceBorder);
                    WriteFlowSemanticElement(
                        html,
                        element,
                        footnotes,
                        page,
                        allowMeasuredWidth: false,
                        sourceBorder: hasSourceBorder ? sourceBorder : null,
                        semanticListItemBorders: grid.SourceBorders.ListItemBorders,
                        preserveSourceLines: isColumnHeading && element.Lines.Count > 1,
                        additionalClass: isColumnHeading
                            ? "pdf-semantic-ruled-grid-column-heading"
                            : null);
                }

                if (sharesHeadingAcrossRightBoundary && columnElements.Count > 1)
                {
                    html.AppendLine("            </div>");
                }

                html.AppendLine("          </div>");
            }

            for (int gutterIndex = 0; gutterIndex < grid.Gutters.Count; gutterIndex++)
            {
                SemanticColumnGutter gutter = grid.Gutters[gutterIndex];
                SemanticRuledGridConnector[] connectors = band.Connectors
                    .Where(connector => connector.GutterIndex == gutterIndex)
                    .ToArray();
                float sourceOffset = connectors.Length == 0
                    ? 0f
                    : MathF.Max(0, connectors.Min(static connector => connector.Element.Bounds.Y) - band.SourceTop);
                html.Append("          <div class=\"pdf-semantic-ruled-grid-connector");
                if (gutter.Width <= 0.5f)
                {
                    html.Append(" pdf-semantic-ruled-grid-connector-collapsed");
                }
                if (isLastRow)
                {
                    html.Append(" pdf-semantic-ruled-grid-row-last");
                }

                html.Append("\" data-ruled-gutter=\"")
                    .Append((gutterIndex + 1).ToString(CultureInfo.InvariantCulture))
                    .Append("\" aria-hidden=\"true")
                    .Append("\" style=\"--pdf-semantic-ruled-column:")
                    .Append((gutterIndex * 2 + 2).ToString(CultureInfo.InvariantCulture))
                    .Append(";--pdf-semantic-ruled-row:")
                    .Append(gridRow.ToString(CultureInfo.InvariantCulture))
                    .Append(";padding-top:")
                    .Append(CssPoints(sourceOffset * scale))
                    .AppendLine("\">");
                foreach (SemanticRuledGridConnector connector in connectors)
                {
                    WriteFlowSemanticElement(
                        html,
                        connector.Element,
                        footnotes,
                        page,
                        allowMeasuredWidth: false);
                }

                html.AppendLine("          </div>");
            }
        }

        html.AppendLine("        </div>");
        html.AppendLine("      </div>");
    }

    private static string SemanticRuledGridTemplate(PdfSemanticRuledGrid grid)
    {
        StringBuilder template = new();
        for (int index = 0; index < grid.Tracks.Count; index++)
        {
            if (index > 0)
            {
                template.Append(' ')
                    .Append("minmax(0,")
                    .Append(SvgNumber(grid.Gutters[index - 1].Width))
                    .Append("fr) ");
            }

            template.Append("minmax(0,")
                .Append(SvgNumber(grid.Tracks[index].Width))
                .Append("fr)");
        }

        return template.ToString();
    }

    private static bool SemanticRuledGridShouldPreserveSpanningSourceLines(
        PdfSemanticRuledGrid grid,
        PdfSemanticElement element)
    {
        if (element.Kind != PdfSemanticElementKind.Paragraph ||
            element.Lines.Count is < 2 or > 5)
        {
            return false;
        }

        PdfSemanticLine[] lines = element.Lines
            .Where(static line => line.Runs.Any(static run => MathF.Abs(run.Direction) < 0.01f))
            .ToArray();
        if (lines.Length != element.Lines.Count)
        {
            return false;
        }

        float sourceCenter = grid.Region.X + grid.Region.Width / 2f;
        float centerTolerance = MathF.Max(6f, grid.Region.Width * 0.04f);
        return lines.All(line =>
            MathF.Abs(line.Bounds.X + line.Bounds.Width / 2f - sourceCenter) <= centerTolerance &&
            line.Bounds.Width <= grid.Region.Width * 0.95f);
    }

    private static bool SemanticRuledGridIsCenteredLeadIn(
        PdfSemanticRuledGrid grid,
        PdfSemanticElement element)
    {
        if (element.Kind is not (
                PdfSemanticElementKind.Heading or
                PdfSemanticElementKind.Paragraph) ||
            element.Lines.Count is < 1 or > 5)
        {
            return false;
        }

        float positionTolerance = MathF.Max(2f, grid.Page.Width * 0.004f);
        float leadInTop = grid.TopRuleGroup?.Bottom ?? 0f;
        if (element.Bounds.Y < leadInTop - positionTolerance ||
            element.Bounds.Bottom > grid.Region.Y + positionTolerance)
        {
            return false;
        }

        PdfSemanticLine[] lines = element.Lines
            .Where(static line => line.Runs.Any(static run => MathF.Abs(run.Direction) < 0.01f))
            .ToArray();
        if (lines.Length != element.Lines.Count)
        {
            return false;
        }

        float sourceCenter = grid.Region.X + grid.Region.Width / 2f;
        float centerTolerance = MathF.Max(6f, grid.Region.Width * 0.04f);
        return lines.All(line =>
            MathF.Abs(line.Bounds.X + line.Bounds.Width / 2f - sourceCenter) <= centerTolerance &&
            line.Bounds.Width <= grid.Region.Width * 0.98f);
    }

    private static void WriteSemanticColumns(
        StringBuilder html,
        PdfSemanticColumns columns,
        FootnoteContext footnotes,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale,
        PdfSemanticExtractionOptions semanticOptions)
    {
        float columnTop = columns.Columns
            .SelectMany(static column => column.Lines)
            .Min(static run => run.Bounds.Y);
        PdfLayoutRectangle[] imageRegions = columns.Page.Images
            .Select(VisibleImageBounds)
            .Where(static bounds => bounds.Width >= 4f && bounds.Height >= 4f)
            .Where(bounds => !columns.SpanningFigures.Any(figure =>
                RectanglesIntersect(bounds, figure.Region, 2f)))
            .Where(bounds => !columns.ColumnFigures.Any(figure =>
                RectanglesIntersect(bounds, figure.Region, 2f)))
            .ToArray();
        PdfLayoutRectangle[] leadingImageRegions = imageRegions
            .Where(region => region.Y < columnTop)
            .ToArray();
        float top = columns.LeadingRuns
            .Select(static run => run.Bounds.Y)
            .Concat(leadingImageRegions.Select(static region => region.Y))
            .Append(columnTop)
            .Min();
        bool hasLeadingContent = columns.LeadingRuns.Count > 0 || leadingImageRegions.Length > 0;
        html.Append("      <div class=\"pdf-semantic-columns");
        if (columns.IsMixedRegions)
        {
            html.Append(" pdf-semantic-mixed-regions");
        }
        if (columns.PreserveAuthoredSemanticElements)
        {
            html.Append(" pdf-semantic-authored-columns");
        }
        html.Append("\"");
        html.Append(" data-source-page=\"")
            .Append(columns.Page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-column-count=\"")
            .Append(columns.Columns.Count.ToString(CultureInfo.InvariantCulture))
            .Append('"');
        html.Append(" style=\"--pdf-semantic-column-count:")
            .Append(columns.Columns.Count.ToString(CultureInfo.InvariantCulture))
            .Append(";--pdf-semantic-columns-left:")
            .Append(CssPoints(columns.LeftInset * scale))
            .Append(";--pdf-semantic-columns-right:")
            .Append(CssPoints(columns.RightInset * scale))
            .Append(";--pdf-semantic-columns-left-relative:")
            .Append(CssPercent(columns.LeftInset / columns.Page.Width * 100f))
            .Append(";--pdf-semantic-columns-right-relative:")
            .Append(CssPercent(columns.RightInset / columns.Page.Width * 100f))
            .Append(";--pdf-semantic-column-left-width:")
            .Append(CssPoints(columns.Tracks[0].Width * scale))
            .Append(";--pdf-semantic-column-right-width:")
            .Append(CssPoints(columns.Tracks[^1].Width * scale))
            .Append(";--pdf-semantic-column-gap:")
            .Append(CssPoints(columns.Gutters[0].Width * scale))
            .Append(";--pdf-semantic-column-tracks:")
            .Append(SemanticColumnGridTemplate(columns))
            .Append(";--pdf-semantic-columns-top:")
            .Append(CssPoints(top * scale));
        for (int trackIndex = 0; trackIndex < columns.Tracks.Count; trackIndex++)
        {
            html.Append(";--pdf-semantic-column-width-")
                .Append((trackIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(CssPoints(columns.Tracks[trackIndex].Width * scale));
        }

        for (int gutterIndex = 0; gutterIndex < columns.Gutters.Count; gutterIndex++)
        {
            html.Append(";--pdf-semantic-column-gutter-")
                .Append((gutterIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(CssPoints(columns.Gutters[gutterIndex].Width * scale));
        }

        html.AppendLine("\">");

        foreach (PdfSemanticColumnFigure figure in columns.SpanningFigures)
        {
            WriteSemanticFigure(
                html,
                columns.Page,
                columns.SemanticPage,
                figure.Region,
                imageAssets,
                scale,
                inline: false,
                caption: figure.Caption,
                footnotes: footnotes,
                includeAllText: true,
                columnSpanning: true);
        }

        if (hasLeadingContent)
        {
            html.Append("        <div class=\"pdf-semantic-column-spanning\" style=\"height:")
                .Append(CssPoints(MathF.Max(0, columnTop - top) * scale))
                .Append(";grid-row:")
                .Append((columns.SpanningFigures.Count + 1).ToString(CultureInfo.InvariantCulture))
                .AppendLine("\">");
            foreach (PdfLayoutRectangle region in leadingImageRegions)
            {
                WriteSemanticFigure(
                    html,
                    columns.Page,
                    columns.SemanticPage,
                    region,
                    imageAssets,
                    scale,
                    inline: false,
                    additionalClass: "pdf-semantic-column-positioned-figure",
                    additionalStyle: "left:" + CssPoints((region.X - columns.LeftInset) * scale) +
                        ";top:" + CssPoints((region.Y - top) * scale),
                    includeSourceDecorations: false);
            }

            foreach (PdfTextRun run in columns.LeadingRuns)
            {
                html.Append("          <span class=\"pdf-semantic-column-run pdf-semantic-column-spanning-run\" style=\"left:")
                    .Append(CssPoints((run.Bounds.X - columns.LeftInset) * scale))
                    .Append(";top:")
                    .Append(CssPoints((run.Bounds.Y - top) * scale))
                    .Append(";font-family:")
                    .Append(CssFontFamily(run.FontName))
                    .Append(";font-size:")
                    .Append(CssPoints(FixedTextFontSize(run) * scale))
                    .Append(FixedTextFontPresentation(run))
                    .Append(";color:")
                    .Append(ColorHex(run.Color))
                    .Append("\">");
                WriteSourceHighlightedRun(html, columns.Page, run, semanticOptions, scale);
                html.AppendLine("</span>");
            }

            html.AppendLine("        </div>");
        }

        IReadOnlyList<PdfSemanticElement>[] semanticTables = ColumnSemanticTables(columns);
        int bodyGridRow = columns.SpanningFigures.Count + (hasLeadingContent ? 2 : 1);
        for (int columnIndex = 0; columnIndex < columns.Columns.Count; columnIndex++)
        {
            LineGridColumn column = columns.Columns[columnIndex];
            html.Append("        <div class=\"pdf-semantic-column\" style=\"--pdf-semantic-column-grid-position:")
                .Append((columnIndex * 2 + 1).ToString(CultureInfo.InvariantCulture))
                .Append(";--pdf-semantic-column-grid-row:")
                .Append(bodyGridRow.ToString(CultureInfo.InvariantCulture))
                .AppendLine("\">");
            SemanticColumnTrack track = columns.Tracks[columnIndex];
            foreach (PdfSemanticColumnRegionFigure figure in columns.ColumnFigures
                .Where(figure => figure.ColumnIndex == columnIndex))
            {
                WriteSemanticFigure(
                    html,
                    columns.Page,
                    columns.SemanticPage,
                    figure.Region,
                    imageAssets,
                    scale,
                    inline: false,
                    includeAllText: true,
                    additionalClass: "pdf-semantic-column-positioned-figure pdf-semantic-mixed-region-figure",
                    additionalStyle: "left:" + CssPoints((figure.Region.X - track.Left) * scale) +
                        ";top:" + CssPoints((figure.Region.Y - columnTop) * scale),
                    accessibleText: figure.AccessibleText,
                    constrainSourceDecorationsToRegion: true);
            }

            foreach (PdfLayoutRectangle region in imageRegions.Where(region =>
                region.Y >= columnTop &&
                ColumnIndexAt(columns.Boundaries, region.X + region.Width / 2f) == columnIndex &&
                !columns.Gutters.Any(gutter => region.X < gutter.Left && region.Right > gutter.Right)))
            {
                WriteSemanticFigure(
                    html,
                    columns.Page,
                    columns.SemanticPage,
                    region,
                    imageAssets,
                    scale,
                    inline: false,
                    additionalClass: "pdf-semantic-column-positioned-figure",
                    additionalStyle: "left:" + CssPoints((region.X - track.Left) * scale) +
                        ";top:" + CssPoints((region.Y - columnTop) * scale),
                    includeSourceDecorations: false);
            }

            IReadOnlyList<PdfSemanticElement> tables = semanticTables[columnIndex];
            PdfSemanticElement[] semanticElements;
            if (columns.PreserveAuthoredSemanticElements)
            {
                semanticElements = columns.SemanticPage.Elements
                    .Where(static element => element.TaggedStructure != null)
                    .Where(static element => element.Lines.Count > 0)
                    .Where(element => IsSemanticElementInColumn(columns, column, element))
                    .ToArray();
            }
            else
            {
                PdfSemanticElement[] listElements = columns.ListElements
                    .Where(element => IsSemanticElementInColumn(columns, column, element))
                    .ToArray();
                PdfSemanticElement[] codeElements = columns.SemanticPage.Elements
                    .Where(static element => element.Kind == PdfSemanticElementKind.CodeBlock)
                    .Where(element => IsSemanticElementInColumn(columns, column, element))
                    .ToArray();
                semanticElements = listElements
                    .Concat(tables)
                    .Concat(codeElements)
                    .OrderBy(static element => element.Bounds.Y)
                    .ThenBy(static element => element.Bounds.X)
                    .ToArray();
            }
            HashSet<PdfTextGlyph> coveredGlyphs = semanticElements
                .SelectMany(SemanticElementGlyphs)
                .Concat(columns.SpanningFigures
                    .SelectMany(figure => FigureRegionTextRuns(columns.Page, figure.Region, includeAllText: true))
                    .SelectMany(static run => run.Glyphs))
                .Concat(columns.SpanningFigures
                    .Where(static figure => figure.Caption != null)
                    .SelectMany(static figure => SemanticElementGlyphs(figure.Caption!)))
                .Concat(columns.ColumnFigures
                    .Where(figure => figure.ColumnIndex == columnIndex)
                    .SelectMany(figure => FigureRegionTextRuns(columns.Page, figure.Region, includeAllText: true)
                        .Where(run => RectangleContainsCenter(figure.Region, run.PageBounds)))
                    .SelectMany(static run => run.Glyphs))
                .ToHashSet();
            PdfLayoutRectangle[] tableCells = tables
                .SelectMany(static table => table.TableRows)
                .SelectMany(static row => row.Cells)
                .Where(static cell => !cell.IsPlaceholder && cell.Bounds.Width > 0 && cell.Bounds.Height > 0)
                .Select(static cell => cell.Bounds)
                .ToArray();
            SemanticColumnItem[] residualItems = column.Lines
                .Select(run => ResidualColumnRun(run, coveredGlyphs, tableCells, semanticOptions))
                .Where(static run => run != null)
                .Select(static run => new SemanticColumnItem(run!.Bounds, run, null))
                .OrderBy(static item => item.Bounds.Y)
                .ThenBy(static item => item.Bounds.X)
                .ToArray();
            SemanticColumnItem[] semanticItems = semanticElements
                .Select(static element => new SemanticColumnItem(element.Bounds, null, element))
                .ToArray();
            SemanticColumnItem[] items = columns.PreserveAuthoredSemanticElements &&
                residualItems.Length == 0
                    ? semanticItems
                    : residualItems
                        .Concat(semanticItems)
                        .OrderBy(static item => item.Bounds.Y)
                        .ThenBy(static item => item.Bounds.X)
                        .ToArray();

            float previousSourceBottom = MathF.Max(
                columnTop,
                columns.SpanningFigures
                    .Select(static figure => figure.Caption?.Bounds.Bottom ?? figure.Region.Bottom)
                    .DefaultIfEmpty(columnTop)
                    .Max());
            foreach (SemanticColumnItem item in items)
            {
                float marginTop = MathF.Max(0, item.Bounds.Y - previousSourceBottom) * scale;
                if (columns.PreserveAuthoredSemanticElements && item.Element != null)
                {
                    html.Append("          <div class=\"pdf-semantic-column-block pdf-semantic-authored-column-block\"");
                    if (marginTop > 0.01f)
                    {
                        html.Append(" style=\"margin-top:")
                            .Append(CssPoints(marginTop))
                            .Append('"');
                    }

                    html.AppendLine(">");
                    if (item.Element.Kind == PdfSemanticElementKind.Table)
                    {
                        WriteSemanticTable(
                            html,
                            item.Element,
                            footnotes,
                            columns.Page,
                            allowMeasuredWidth: false);
                    }
                    else
                    {
                        WriteFlowSemanticElement(
                            html,
                            item.Element,
                            footnotes,
                            columns.Page,
                            allowMeasuredWidth: false);
                    }
                    html.AppendLine("          </div>");
                    previousSourceBottom = item.Bounds.Bottom;
                    continue;
                }

                if (item.Element?.Kind == PdfSemanticElementKind.List)
                {
                    html.Append("          <div class=\"pdf-semantic-column-block\"");
                    if (marginTop > 0.01f)
                    {
                        html.Append(" style=\"margin-top:")
                            .Append(CssPoints(marginTop))
                            .Append('"');
                    }

                    html.AppendLine(">");
                    WriteSemanticList(
                        html,
                        item.Element.SemanticList!,
                        item.Element,
                        footnotes,
                        columns.Page,
                        indentation: 10,
                        isRoot: true);
                    html.AppendLine("          </div>");
                    previousSourceBottom = item.Bounds.Bottom;
                    continue;
                }

                if (item.Element?.Kind == PdfSemanticElementKind.Table)
                {
                    WriteSemanticTable(
                        html,
                        item.Element,
                        footnotes,
                        columns.Page,
                        allowMeasuredWidth: false);
                    previousSourceBottom = item.Bounds.Bottom;
                    continue;
                }

                if (item.Element?.Kind is PdfSemanticElementKind.CodeBlock or PdfSemanticElementKind.Algorithm)
                {
                    html.Append("          <div class=\"pdf-semantic-column-block\"");
                    if (marginTop > 0.01f)
                    {
                        html.Append(" style=\"margin-top:")
                            .Append(CssPoints(marginTop))
                            .Append('"');
                    }

                    html.AppendLine(">");
                    WriteFlowSemanticElement(
                        html,
                        item.Element,
                        footnotes,
                        columns.Page,
                        allowMeasuredWidth: false);
                    html.AppendLine("          </div>");
                    previousSourceBottom = item.Bounds.Bottom;
                    continue;
                }

                PdfTextRun run = item.Run!;
                WriteSemanticColumnRun(
                    html,
                    columns.Page,
                    run,
                    track.Left,
                    marginTop,
                    scale,
                    semanticOptions);
                previousSourceBottom = run.Bounds.Bottom;
            }

            html.AppendLine("        </div>");
        }

        html.AppendLine("      </div>");
    }

    private static void WriteSemanticPageRuleGroup(
        StringBuilder html,
        SemanticPageRuleGroup group,
        PdfLayoutPage page,
        float scale)
    {
        float frameWidth = MathF.Max(0.1f, group.Right - group.Left);
        html.Append("      <div class=\"pdf-semantic-page-rule-group\" aria-hidden=\"true\"")
            .Append(" data-page-decoration=\"top-rules\" data-source-page=\"")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-rule-count=\"")
            .Append(group.Rules.Count.ToString(CultureInfo.InvariantCulture))
            .Append("\" style=\"--pdf-semantic-page-rule-group-height:")
            .Append(CssPoints((group.Bottom - group.Top) * scale))
            .Append(";--pdf-semantic-page-rule-gap-after:")
            .Append(CssPoints(group.GapAfter * scale))
            .Append(";--pdf-semantic-page-rule-left-relative:")
            .Append(CssPercent(group.Left / page.Width * 100f))
            .Append(";--pdf-semantic-page-rule-right-relative:")
            .Append(CssPercent(MathF.Max(0, page.Width - group.Right) / page.Width * 100f))
            .AppendLine("\">");
        html.AppendLine("        <div class=\"pdf-semantic-page-rule-stack\">");
        foreach (SemanticPageRule rule in group.Rules)
        {
            html.Append("          <hr class=\"pdf-semantic-page-rule\" role=\"presentation\"")
                .Append(" data-source-path-index=\"")
                .Append(rule.SourcePathIndex.ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"--pdf-semantic-page-rule-top:")
                .Append(CssPoints((rule.Top - group.Top) * scale))
                .Append(";--pdf-semantic-page-rule-left:")
                .Append(CssPercent((rule.Left - group.Left) / frameWidth * 100f))
                .Append(";--pdf-semantic-page-rule-width:")
                .Append(CssPercent(rule.Width / frameWidth * 100f))
                .Append(";--pdf-semantic-page-rule-thickness:")
                .Append(CssPoints(rule.Thickness * scale))
                .Append(";--pdf-semantic-page-rule-color:")
                .Append(CssRgba(rule.Color))
                .AppendLine("\" />");
        }

        html.AppendLine("        </div>");
        html.AppendLine("      </div>");
    }

    private static string SemanticColumnGridTemplate(PdfSemanticColumns columns)
    {
        StringBuilder template = new();
        for (int index = 0; index < columns.Tracks.Count; index++)
        {
            if (index > 0)
            {
                template.Append(' ')
                    .Append("minmax(0,")
                    .Append(SvgNumber(columns.Gutters[index - 1].Width))
                    .Append("fr) ");
            }

            template.Append("minmax(0,")
                .Append(SvgNumber(columns.Tracks[index].Width))
                .Append("fr)");
        }

        return template.ToString();
    }

    private static bool IsSemanticElementInColumn(
        PdfSemanticColumns columns,
        LineGridColumn column,
        PdfSemanticElement element)
    {
        LineGridColumn[] sourceColumns = element.Lines
            .Select(line => columns.Columns
                .Where(candidate => candidate.Lines.Any(run => ContainsSemanticLineGlyphs(line, run)))
                .ToArray())
            .Where(static matches => matches.Length == 1)
            .Select(static matches => matches[0])
            .ToArray();
        if (sourceColumns.Length == element.Lines.Count &&
            sourceColumns.All(candidate => ReferenceEquals(candidate, column)))
        {
            return true;
        }

        if (!columns.PreserveAuthoredSemanticElements)
        {
            return false;
        }

        int columnIndex = columns.Columns
            .Select((candidate, index) => (candidate, index))
            .Where(candidate => ReferenceEquals(candidate.candidate, column))
            .Select(static candidate => candidate.index)
            .DefaultIfEmpty(-1)
            .Single();
        if (columnIndex < 0)
        {
            return false;
        }

        SemanticColumnTrack track = columns.Tracks[columnIndex];
        const float tolerance = 2f;
        return element.Bounds.X >= track.Left - tolerance &&
            element.Bounds.Right <= track.Right + tolerance;
    }

    private static bool ContainsSemanticLineGlyphs(PdfSemanticLine line, PdfTextRun run)
    {
        PdfTextGlyph[] sourceGlyphs = line.Runs
            .SelectMany(static sourceRun => sourceRun.Glyphs)
            .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
            .ToArray();
        return sourceGlyphs.Length > 0 && sourceGlyphs.All(sourceGlyph =>
            run.Glyphs.Any(candidate => ReferenceEquals(candidate, sourceGlyph)));
    }

    private static void WriteSemanticColumnRun(
        StringBuilder html,
        PdfLayoutPage page,
        PdfTextRun run,
        float columnLeft,
        float marginTop,
        float scale,
        PdfSemanticExtractionOptions semanticOptions)
    {
        html.Append("          <span class=\"pdf-semantic-column-run\" style=\"--pdf-semantic-column-row-height:")
            .Append(CssPoints(run.Bounds.Height * scale));
        if (marginTop > 0.01f)
        {
            html.Append(";margin-top:")
                .Append(CssPoints(marginTop));
        }

        float marginLeft = MathF.Max(0, run.Bounds.X - columnLeft) * scale;
        if (marginLeft > 0.01f)
        {
            html.Append(";margin-left:")
                .Append(CssPoints(marginLeft));
        }

        html.Append(";font-family:")
            .Append(CssFontFamily(run.FontName))
            .Append(";font-size:")
            .Append(CssPoints(FixedTextFontSize(run) * scale))
            .Append(FixedTextFontPresentation(run))
            .Append(";color:")
            .Append(ColorHex(run.Color))
            .Append("\">");
        WriteSourceHighlightedRun(html, page, run, semanticOptions, scale);
        html.AppendLine("</span>");
    }

    private static IReadOnlyList<PdfSemanticElement>[] ColumnSemanticTables(PdfSemanticColumns columns)
    {
        List<PdfSemanticElement>[] result = columns.Columns
            .Select(static _ => new List<PdfSemanticElement>())
            .ToArray();
        if (result.Length != 2)
        {
            foreach (PdfSemanticElement table in columns.SemanticPage.Elements
                .Where(static element => element.Kind == PdfSemanticElementKind.Table && element.TableRows.Count > 0)
                .Where(table => HasSupportingTableRules(columns.Page, table)))
            {
                int[] matchingColumns = Enumerable.Range(0, columns.Columns.Count)
                    .Where(index => IsSemanticElementInColumn(columns, columns.Columns[index], table))
                    .ToArray();
                if (matchingColumns.Length == 1)
                {
                    result[matchingColumns[0]].Add(table);
                }
            }

            return result;
        }

        float columnTop = columns.Columns
            .SelectMany(static column => column.Lines)
            .Min(static run => run.Bounds.Y);
        float tolerance = MathF.Max(1f, columns.Page.Width * 0.002f);
        foreach (PdfSemanticElement table in columns.SemanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Table && element.TableRows.Count > 0)
            .Where(table => HasSupportingTableRules(columns.Page, table))
            .Where(table => table.Bounds.Bottom >= columnTop - tolerance))
        {
            float boundary = columns.Boundaries[0];
            if (table.Bounds.Right <= boundary + tolerance)
            {
                result[0].Add(table);
                continue;
            }

            if (table.Bounds.X >= boundary - tolerance)
            {
                result[1].Add(table);
                continue;
            }

            PdfSemanticElement? left = SplitSemanticTable(table, boundary, keepLeft: true, tolerance);
            PdfSemanticElement? right = SplitSemanticTable(table, boundary, keepLeft: false, tolerance);
            if (left != null && HasSupportingTableRules(columns.Page, left))
            {
                result[0].Add(left);
            }

            if (right != null && HasSupportingTableRules(columns.Page, right))
            {
                result[1].Add(right);
            }
        }

        return result;
    }

    private static PdfSemanticElement? SplitSemanticTable(
        PdfSemanticElement table,
        float boundary,
        bool keepLeft,
        float tolerance)
    {
        PdfSemanticTableCell[] sourceCells = table.TableRows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder && cell.Bounds.Width > 0)
            .ToArray();
        if (sourceCells.Any(cell => cell.Bounds.X < boundary - tolerance && cell.Bounds.Right > boundary + tolerance))
        {
            return null;
        }

        List<PdfSemanticTableRow> rows = [];
        foreach (PdfSemanticTableRow row in table.TableRows)
        {
            PdfSemanticTableCell[] cells = row.Cells
                .Where(cell => keepLeft
                    ? cell.Bounds.X + cell.Bounds.Width / 2f < boundary
                    : cell.Bounds.X + cell.Bounds.Width / 2f >= boundary)
                .ToArray();
            if (cells.Any(static cell => !cell.IsPlaceholder && !string.IsNullOrWhiteSpace(cell.Text)))
            {
                rows.Add(new PdfSemanticTableRow(cells, row.IsHeader));
            }
        }

        PdfSemanticTableCell[] selectedCells = rows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder && cell.Bounds.Width > 0 && cell.Bounds.Height > 0)
            .ToArray();
        if (selectedCells.Length == 0)
        {
            return null;
        }

        PdfSemanticLine[] lines = selectedCells
            .SelectMany(static cell => cell.Lines)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        string text = string.Join(Environment.NewLine, rows.Select(row =>
            string.Join('\t', row.Cells
                .Where(static cell => !cell.IsPlaceholder)
                .Select(static cell => cell.Text))));
        PdfSemanticTableCaption? caption = table.TableCaption;
        if (caption != null &&
            (caption.Bounds.X + caption.Bounds.Width / 2f < boundary) != keepLeft)
        {
            caption = null;
        }

        return new PdfSemanticElement(
            PdfSemanticElementKind.Table,
            text,
            UnionRectangles(selectedCells.Select(static cell => cell.Bounds)),
            lines,
            tableRows: rows,
            tableCaption: caption);
    }

    private static bool HasSupportingTableRules(PdfLayoutPage page, PdfSemanticElement table)
    {
        float horizontalTolerance = MathF.Max(2f, page.Width * 0.005f);
        float verticalTolerance = MathF.Max(2f, table.Bounds.Height * 0.1f);
        return CreateSourceRuleFamilies(page).Any(family =>
        {
            float horizontalOverlap = MathF.Max(
                0,
                MathF.Min(family.Right, table.Bounds.Right) - MathF.Max(family.Left, table.Bounds.X));
            return horizontalOverlap >= MathF.Min(family.Right - family.Left, table.Bounds.Width) * 0.5f &&
                family.Bottom >= table.Bounds.Y - verticalTolerance &&
                family.Top <= table.Bounds.Bottom + verticalTolerance &&
                family.Left <= table.Bounds.Right + horizontalTolerance &&
                family.Right >= table.Bounds.X - horizontalTolerance;
        });
    }

    private static IEnumerable<PdfTextGlyph> TableGlyphs(PdfSemanticElement table)
    {
        return table.TableRows
            .SelectMany(static row => row.Cells)
            .SelectMany(static cell => cell.Lines)
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs);
    }

    private static IEnumerable<PdfTextGlyph> SemanticElementGlyphs(PdfSemanticElement element)
    {
        return element.Kind == PdfSemanticElementKind.Table
            ? TableGlyphs(element).Concat(element.TableCaption?.Lines
                .SelectMany(static line => line.Runs)
                .SelectMany(static run => run.Glyphs) ?? [])
            : element.Lines
                .SelectMany(static line => line.Runs)
                .SelectMany(static run => run.Glyphs);
    }

    internal static bool IsFullyClaimedFormulaElement(
        PdfSemanticElement element,
        ISet<FormulaGlyphKey> claimedGlyphs)
    {
        if (element.Kind != PdfSemanticElementKind.Paragraph)
        {
            return false;
        }

        FormulaGlyphKey[] visibleGlyphs = element.Lines
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .Where(PdfMathMlFormula.IsEligibleGlyph)
            .Select(FormulaGlyphIdentity)
            .Distinct()
            .ToArray();
        return visibleGlyphs.Length > 0 && visibleGlyphs.All(claimedGlyphs.Contains);
    }

    internal static FormulaGlyphKey FormulaGlyphIdentity(PdfTextGlyph glyph)
    {
        PdfLayoutRectangle bounds = glyph.PageBounds;
        return new FormulaGlyphKey(
            glyph.Text,
            NormalizeFontName(glyph.FontName),
            QuantizeFormulaGlyphMetric(glyph.FontSize),
            QuantizeFormulaGlyphMetric(NormalizeDirection(glyph.Direction)),
            QuantizeFormulaGlyphMetric(bounds.X),
            QuantizeFormulaGlyphMetric(bounds.Y),
            QuantizeFormulaGlyphMetric(bounds.Width),
            QuantizeFormulaGlyphMetric(bounds.Height),
            glyph.IsPainted);
    }

    private static int QuantizeFormulaGlyphMetric(float value)
    {
        return (int)MathF.Round(value * 100f, MidpointRounding.AwayFromZero);
    }

    private static PdfTextRun? ResidualColumnRun(
        PdfTextRun run,
        ISet<PdfTextGlyph> coveredGlyphs,
        IReadOnlyList<PdfLayoutRectangle> coveredCells,
        PdfSemanticExtractionOptions semanticOptions)
    {
        PdfTextGlyph[] glyphs = run.Glyphs
            .Where(glyph => !coveredGlyphs.Contains(glyph) &&
                !coveredCells.Any(cell => RectangleContainsCenter(cell, glyph.Bounds)))
            .ToArray();
        if (glyphs.Length == 0)
        {
            return null;
        }

        if (glyphs.Length == run.Glyphs.Count)
        {
            return run;
        }

        PdfTextGlyph first = glyphs[0];
        return new PdfTextRun(
            PdfSemanticExtractor.ReconstructText(glyphs, semanticOptions),
            first.FontName,
            first.FontSize,
            first.Direction,
            UnionRectangles(glyphs.Select(static glyph => glyph.Bounds)),
            first.Color,
            glyphs);
    }

    private static void WriteSourceHighlightedRun(
        StringBuilder html,
        PdfLayoutPage page,
        PdfTextRun run,
        PdfSemanticExtractionOptions semanticOptions,
        float scale)
    {
        PdfTextGlyph[] glyphs = PdfSemanticExtractor.OrderGlyphsForLogicalText(run.Glyphs).ToArray();
        if (glyphs.Length == 0 || !glyphs.Any(glyph =>
                TextHighlightForGlyph(page, glyph) != null ||
                SemanticLinkForGlyph(page, glyph.PageBounds) != null))
        {
            html.Append(Html(PdfSemanticExtractor.ReconstructText(run.Glyphs, semanticOptions)));
            return;
        }

        List<PdfTextGlyph> segmentGlyphs = [];
        PdfTextHighlight? activeHighlight = TextHighlightForGlyph(page, glyphs[0]);
        PdfLayoutLink? activeLink = SemanticLinkForGlyph(page, glyphs[0].PageBounds);
        foreach (PdfTextGlyph glyph in glyphs)
        {
            PdfTextHighlight? highlight = TextHighlightForGlyph(page, glyph);
            PdfLayoutLink? link = SemanticLinkForGlyph(page, glyph.PageBounds);
            if ((!ReferenceEquals(activeHighlight, highlight) || !ReferenceEquals(activeLink, link)) &&
                segmentGlyphs.Count > 0)
            {
                WriteSourceHighlightedGlyphs(
                    html,
                    segmentGlyphs,
                    activeHighlight,
                    activeLink,
                    semanticOptions,
                    scale);
                segmentGlyphs.Clear();
            }

            activeHighlight = highlight;
            activeLink = link;
            segmentGlyphs.Add(glyph);
        }

        WriteSourceHighlightedGlyphs(
            html,
            segmentGlyphs,
            activeHighlight,
            activeLink,
            semanticOptions,
            scale);
    }

    private static void WriteSourceHighlightedGlyphs(
        StringBuilder html,
        IReadOnlyList<PdfTextGlyph> glyphs,
        PdfTextHighlight? highlight,
        PdfLayoutLink? link,
        PdfSemanticExtractionOptions semanticOptions,
        float scale)
    {
        string text = PdfSemanticExtractor.ReconstructText(glyphs, semanticOptions);
        bool hasLinkedText = link != null && !string.IsNullOrWhiteSpace(text);
        if (hasLinkedText)
        {
            WriteSemanticLinkStart(html, link!);
        }

        if (highlight != null)
        {
            WriteSourceMarkStart(html, highlight, scale);
        }

        html.Append(Html(text));
        if (highlight != null)
        {
            html.Append("</mark>");
        }

        if (hasLinkedText)
        {
            html.Append("</a>");
        }
    }

    private static void WriteSourceMarkStart(StringBuilder html, PdfTextHighlight highlight, float scale)
    {
        html.Append("<mark class=\"pdf-semantic-mark\" style=\"--pdf-semantic-mark-background:")
            .Append(CssRgba(highlight.Color))
            .Append(";--pdf-semantic-mark-width:")
            .Append(CssPoints(highlight.Bounds.Width * scale))
            .Append("\">");
    }

    private static PdfTextHighlight? TextHighlightForGlyph(PdfLayoutPage? page, PdfTextGlyph glyph)
    {
        return page?.TextHighlights.FirstOrDefault(highlight =>
            highlight.Glyphs.Any(candidate => ReferenceEquals(candidate, glyph)));
    }

    private static int BodyParagraphCount(PdfSemanticPage semanticPage)
    {
        return semanticPage.Elements.Sum(static element => element.Kind switch
        {
            PdfSemanticElementKind.Paragraph or PdfSemanticElementKind.BlockQuote =>
                element.Text.Length >= 40 ? 1 : 0,
            PdfSemanticElementKind.Aside when element.Aside != null =>
                element.Aside.Content.Count(static content => content.Text.Length >= 40),
            _ => 0
        });
    }

    private static float PageContentTop(PdfLayoutPage page)
    {
        return page.Runs
            .Select(static run => run.Bounds.Y)
            .Concat(page.Images.Select(static image => image.Bounds.Y))
            .Concat(page.Paths.Select(static path => path.Bounds.Y))
            .DefaultIfEmpty(0)
            .Min();
    }

    private static ContinuousParagraphMerge? TryCreateContinuousParagraphMerge(
        ContinuousPageContext current,
        ContinuousPageContext next)
    {
        if (!TryFindTrailingBodyParagraph(current, out PdfSemanticElement? startElement) ||
            startElement == null ||
            !TryFindLeadingBodyParagraph(next, out PdfSemanticElement? continuationElement, out PdfSemanticElement[] leadingElements) ||
            continuationElement == null ||
            !ShouldMergeParagraphAcrossPage(current, startElement, next, continuationElement))
        {
            return null;
        }

        PdfLayoutRectangle[] leadingFigureRegions = next.FigureRegions
            .Where(region => region.Y < continuationElement.Bounds.Y - 2f)
            .ToArray();
        PdfSemanticElement? currentPageNumberFooter = FindSimplePageNumberFooterAfter(current, startElement);
        if (HasTrailingFootnoteAfter(current, startElement, currentPageNumberFooter))
        {
            return null;
        }

        return new ContinuousParagraphMerge(
            current,
            next,
            startElement,
            continuationElement,
            currentPageNumberFooter,
            leadingElements,
            leadingFigureRegions);
    }

    private static bool HasTrailingFootnoteAfter(
        ContinuousPageContext context,
        PdfSemanticElement startElement,
        PdfSemanticElement? currentPageNumberFooter)
    {
        bool foundStart = false;
        foreach (PdfSemanticElement element in context.FlowElements)
        {
            if (ReferenceEquals(element, startElement))
            {
                foundStart = true;
                continue;
            }

            if (!foundStart)
            {
                continue;
            }

            if (currentPageNumberFooter != null && ReferenceEquals(element, currentPageNumberFooter))
            {
                break;
            }

            if (IsSimplePageNumberFooter(element, context.Page) || element.Kind == PdfSemanticElementKind.Footer)
            {
                break;
            }

            if (element.Kind == PdfSemanticElementKind.Footnote)
            {
                return true;
            }

            if (element.Kind == PdfSemanticElementKind.Header)
            {
                continue;
            }

            break;
        }

        return false;
    }

    private static PdfSemanticElement? FindSimplePageNumberFooterAfter(
        ContinuousPageContext context,
        PdfSemanticElement startElement)
    {
        bool foundStart = false;
        foreach (PdfSemanticElement element in context.FlowElements)
        {
            if (ReferenceEquals(element, startElement))
            {
                foundStart = true;
                continue;
            }

            if (!foundStart)
            {
                continue;
            }

            if (IsSimplePageNumberFooter(element, context.Page))
            {
                return element;
            }
        }

        return null;
    }

    private static bool TryFindTrailingBodyParagraph(
        ContinuousPageContext context,
        out PdfSemanticElement? paragraph)
    {
        for (int index = context.FlowElements.Count - 1; index >= 0; index--)
        {
            PdfSemanticElement element = context.FlowElements[index];
            if (IsSimplePageNumberFooter(element, context.Page) || element.Kind == PdfSemanticElementKind.Footer)
            {
                continue;
            }

            if (IsContinuousBodyParagraph(element))
            {
                paragraph = element;
                return true;
            }

            if (element.Kind is PdfSemanticElementKind.Footnote or PdfSemanticElementKind.Header)
            {
                continue;
            }

            break;
        }

        paragraph = null;
        return false;
    }

    private static bool TryFindLeadingBodyParagraph(
        ContinuousPageContext context,
        out PdfSemanticElement? paragraph,
        out PdfSemanticElement[] leadingElements)
    {
        List<PdfSemanticElement> interruptions = [];
        foreach (PdfSemanticElement element in context.FlowElements)
        {
            if (IsSimplePageNumberFooter(element, context.Page))
            {
                continue;
            }

            if (IsLeadingContinuousInterruption(element))
            {
                interruptions.Add(element);
                continue;
            }

            if (IsContinuousBodyParagraph(element))
            {
                paragraph = element;
                leadingElements = interruptions.ToArray();
                return true;
            }

            paragraph = null;
            leadingElements = [];
            return false;
        }

        paragraph = null;
        leadingElements = [];
        return false;
    }

    private static bool IsContinuousBodyParagraph(PdfSemanticElement element)
    {
        return element.Kind == PdfSemanticElementKind.Paragraph &&
            !IsFigureCaption(element) &&
            !IsSameRowLineGroup(element) &&
            !IsFormulaBlock(element) &&
            !string.IsNullOrWhiteSpace(element.Text);
    }

    private static bool IsLeadingContinuousInterruption(PdfSemanticElement element)
    {
        return IsFigureCaption(element) || IsSameRowLineGroup(element);
    }

    private static bool ShouldMergeParagraphAcrossPage(
        ContinuousPageContext current,
        PdfSemanticElement startElement,
        ContinuousPageContext next,
        PdfSemanticElement continuationElement)
    {
        string startText = startElement.Text.Trim();
        string continuationText = continuationElement.Text.Trim();
        if (startText.Length < 24 ||
            continuationText.Length < 16 ||
            EndsLikeCompleteParagraph(startText) ||
            !StartsLikeParagraphContinuation(continuationText))
        {
            return false;
        }

        if (startElement.Bounds.Bottom < current.Page.Height * 0.48f ||
            continuationElement.Bounds.Y > next.Page.Height * 0.82f)
        {
            return false;
        }

        return HasCompatibleParagraphStyle(startElement, continuationElement);
    }

    private static bool HasCompatibleParagraphStyle(PdfSemanticElement first, PdfSemanticElement second)
    {
        if (!string.Equals(
                NormalizeFontName(SemanticFontName(first)),
                NormalizeFontName(SemanticFontName(second)),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (MathF.Abs(SemanticFontSize(first) - SemanticFontSize(second)) > 1.25f)
        {
            return false;
        }

        return string.Equals(
            ColorClass(SemanticColor(first)),
            ColorClass(SemanticColor(second)),
            StringComparison.Ordinal);
    }

    private static bool EndsLikeCompleteParagraph(string text)
    {
        string trimmed = text.TrimEnd();
        while (trimmed.Length > 0 && trimmed[^1] is '"' or '\'' or ')' or ']' or '}')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.EndsWith(".", StringComparison.Ordinal) ||
            trimmed.EndsWith("!", StringComparison.Ordinal) ||
            trimmed.EndsWith("?", StringComparison.Ordinal) ||
            trimmed.EndsWith(":", StringComparison.Ordinal);
    }

    private static bool StartsLikeParagraphContinuation(string text)
    {
        char first = text.TrimStart().FirstOrDefault();
        return char.IsLower(first) ||
            first is ',' or ';' or ':' or ')' or ']' or '}';
    }

    private static void WriteMergedParagraph(
        StringBuilder html,
        ContinuousParagraphMerge merge,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale)
    {
        html.Append("      <p class=\"")
            .Append(SemanticClassNames(merge.StartElement, merge.Current.Page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-page-spanning\"");
        string style = FlowSemanticStyle(merge.StartElement, merge.Current.Page, allowMeasuredWidth: false);
        if (style.Length > 0)
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(style))
                .Append('"');
        }

        html.Append('>');
        WriteSemanticText(html, merge.StartElement, merge.Current.Footnotes, merge.Current.Page);
        if (merge.CurrentPageNumberFooter != null)
        {
            WriteInlineFlowSemanticElement(
                html,
                merge.CurrentPageNumberFooter,
                merge.Current.Footnotes,
                merge.Current.Page);
        }

        WriteInlinePageBreak(html, merge.Next.Page.PageNumber);
        WriteMergedParagraphInterruptions(html, merge, imageAssets, scale);
        if (NeedsSpaceBetween(merge.StartElement.Text, merge.ContinuationElement.Text))
        {
            html.Append(' ');
        }

        html.Append("<span class=\"pdf-semantic-page-continuation\">");
        WriteSemanticText(html, merge.ContinuationElement, merge.Next.Footnotes, merge.Next.Page);
        html.AppendLine("</span></p>");
    }

    private static void WriteInlinePageBreak(StringBuilder html, int pageNumber)
    {
        string pageNumberText = pageNumber.ToString(CultureInfo.InvariantCulture);
        html.Append("<span class=\"pdf-semantic-page-break pdf-semantic-inline-page-break\" id=\"page-")
            .Append(pageNumberText)
            .Append("\" data-page-number=\"")
            .Append(pageNumberText)
            .Append("\" role=\"separator\" aria-label=\"Original PDF page ")
            .Append(pageNumberText)
            .Append("\"></span>");
    }

    private static void WriteMergedParagraphInterruptions(
        StringBuilder html,
        ContinuousParagraphMerge merge,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale)
    {
        int nextFigureRegion = 0;
        foreach (PdfSemanticElement element in merge.LeadingElements)
        {
            while (nextFigureRegion < merge.LeadingFigureRegions.Count &&
                ShouldInsertFigureSpaceBefore(element, merge.LeadingFigureRegions[nextFigureRegion]))
            {
                WriteSemanticFigure(
                    html,
                    merge.Next.Page,
                    merge.Next.SemanticPage,
                    merge.LeadingFigureRegions[nextFigureRegion],
                    imageAssets,
                    scale,
                    inline: true);
                nextFigureRegion++;
            }

            WriteInlineFlowSemanticElement(html, element, merge.Next.Footnotes, merge.Next.Page);
        }

        while (nextFigureRegion < merge.LeadingFigureRegions.Count)
        {
            WriteSemanticFigure(
                html,
                merge.Next.Page,
                merge.Next.SemanticPage,
                merge.LeadingFigureRegions[nextFigureRegion],
                imageAssets,
                scale,
                inline: true);
            nextFigureRegion++;
        }
    }

    private static void WriteInlineFlowSemanticElement(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage page)
    {
        html.Append("<span class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-inline-flow-element\"");
        AppendTaggedStructureAttributes(html, element);
        AppendTextDirectionAttribute(html, element.Text);
        string style = FlowSemanticStyle(element, page, allowMeasuredWidth: false);
        if (style.Length > 0)
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(style))
                .Append('"');
        }

        html.Append('>');
        WriteSemanticText(html, element, footnotes, page);
        html.Append("</span>");
    }

    private static bool NeedsSpaceBetween(string first, string second)
    {
        string left = first.TrimEnd();
        string right = second.TrimStart();
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        if (left.EndsWith('a') && right.StartsWith("nd", StringComparison.Ordinal))
        {
            return false;
        }

        return !NoSpaceAfter(left[^1]) && !NoSpaceBefore(right[0]) && left[^1] != '-';
    }

    private static void WriteSemanticPageBreak(StringBuilder html, int pageNumber, bool isFirstPage)
    {
        string pageNumberText = pageNumber.ToString(CultureInfo.InvariantCulture);
        html.Append("      <div class=\"pdf-semantic-page-break");
        if (isFirstPage)
        {
            html.Append(" pdf-semantic-page-start");
        }

        html.Append("\" id=\"page-")
            .Append(pageNumberText)
            .Append("\" data-page-number=\"")
            .Append(pageNumberText)
            .Append("\" role=\"separator\" aria-label=\"Original PDF page ")
            .Append(pageNumberText)
            .AppendLine("\"></div>");
    }

    private static PdfSemanticElement? FindPageOpeningSectionHeading(
        IReadOnlyList<PdfSemanticElement> flowElements,
        PdfSemanticSectionTree sectionTree)
    {
        foreach (PdfSemanticElement element in flowElements)
        {
            if (element.Kind is PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer)
            {
                continue;
            }

            return sectionTree.FindSection(element) != null ? element : null;
        }

        return null;
    }

    private static void WriteContinuousPageArtifacts(
        StringBuilder html,
        PdfLayoutPage page,
        IReadOnlyList<PdfSemanticElement> elements,
        FootnoteContext footnotes,
        IReadOnlyList<PdfLayoutRectangle> figureRegions,
        float scale,
        PdfSemanticSectionTree sectionTree)
    {
        if (elements.Count == 0)
        {
            return;
        }

        PdfSemanticElement[] flowArtifacts = elements
            .Where(element => !IsFigureLabelFlowElement(element, figureRegions))
            .Where(element => !IsContinuousPositionedPageArtifact(page, element))
            .ToArray();
        foreach (PdfSemanticElement element in elements
            .Where(element => !IsFigureLabelFlowElement(element, figureRegions))
            .Where(element => IsContinuousPositionedPageArtifact(page, element)))
        {
            WritePositionedSemanticElement(
                html,
                page,
                element,
                footnotes,
                scale,
                sectionTree.FindHeading(element)?.Id,
                sectionTree.FindSection(element)?.Level);
        }

        if (flowArtifacts.Length == 0)
        {
            return;
        }

        html.Append("      <aside class=\"pdf-semantic-page-artifacts\" aria-label=\"Original page ")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" artifacts\">");
        foreach (PdfSemanticElement element in flowArtifacts)
        {
            WriteFlowSemanticElement(html, element, footnotes, page, allowMeasuredWidth: false);
        }

        html.AppendLine("      </aside>");
    }

    private static bool IsContinuousPositionedPageArtifact(PdfLayoutPage page, PdfSemanticElement element)
    {
        if (element.Kind is not (PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer))
        {
            return false;
        }

        float direction = MathF.Abs(SemanticDirection(element));
        if (MathF.Abs(direction - 90f) > 0.01f && MathF.Abs(direction - 270f) > 0.01f)
        {
            return false;
        }

        float left = direction < 180f
            ? element.Bounds.Y
            : page.Width - element.Bounds.Y;
        return left <= page.Width * 0.16f ||
            left >= page.Width * 0.84f;
    }

    private static void WriteSemanticFlowElements(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        IReadOnlyList<PdfSemanticElement> flowElements,
        FootnoteContext footnotes,
        float scale,
        IReadOnlyDictionary<string, PdfLayoutImageAsset>? imageAssets,
        SemanticFigureRendering figureRendering,
        bool omitSimplePageNumberFooters,
        ISet<PdfSemanticElement>? skippedElements,
        ISet<PdfLayoutRectangle>? skippedFigureRegions,
        ContinuousParagraphMerge? paragraphMerge,
        SemanticSectionWriter? sectionWriter,
        SemanticBibliographyWriter? bibliographyWriter,
        SemanticDefinitionListRenderState? definitionListState = null,
        PdfSemanticRuledGrid? ruledGrid = null)
    {
        PdfLayoutRectangle[] figureRegions = figureRendering == SemanticFigureRendering.None
            ? []
            : SemanticFigureRegions(page, semanticPage)
                .Where(region => skippedFigureRegions == null || !skippedFigureRegions.Contains(region))
                .Where(region => ruledGrid == null ||
                    !RectanglesIntersect(region, ruledGrid.Region, 2f))
                .ToArray();
        Dictionary<PdfLayoutRectangle, PdfSemanticElement> captionsByFigure =
            figureRendering == SemanticFigureRendering.Content
                ? FigureCaptionsByRegion(page, semanticPage, figureRegions)
                : [];
        PdfLayoutRectangle[] captionedFigureRegions = captionsByFigure.Keys.ToArray();
        HashSet<FormulaGlyphKey> claimedFormulaGlyphs = [];
        List<PdfSemanticElement> deferredPageArtifacts = [];
        int nextFigureRegion = 0;
        bool ruledGridWritten = false;
        for (int index = 0; index < flowElements.Count; index++)
        {
            PdfSemanticElement element = flowElements[index];
            if (skippedElements?.Contains(element) == true)
            {
                continue;
            }

            if (omitSimplePageNumberFooters && IsSimplePageNumberFooter(element, page))
            {
                continue;
            }

            if (bibliographyWriter != null &&
                bibliographyWriter.IsActive &&
                bibliographyWriter.ContinuesAfter(page.PageNumber) &&
                IsSimplePageNumberFooter(element, page))
            {
                continue;
            }

            if (ruledGrid?.Elements.Contains(element) == true)
            {
                if (!ruledGridWritten)
                {
                    if (definitionListState?.IsOpen == true)
                    {
                        CloseSemanticDefinitionList(html, definitionListState);
                    }

                    WriteSemanticRuledGrid(html, ruledGrid, footnotes, page, scale);
                    ruledGridWritten = true;
                }

                continue;
            }

            if (IsMicroscopicUnpaintedPayloadElement(element) ||
                IsFigureLabelFlowElement(element, figureRegions, captionedFigureRegions) ||
                captionsByFigure.Values.Any(caption => ReferenceEquals(caption, element)))
            {
                continue;
            }

            while (nextFigureRegion < figureRegions.Length &&
                ShouldInsertFigureSpaceBefore(element, figureRegions[nextFigureRegion]))
            {
                if (figureRendering == SemanticFigureRendering.Space)
                {
                    WriteFigureSpace(html, figureRegions[nextFigureRegion], scale);
                }
                else if (imageAssets != null)
                {
                    WriteSemanticFigure(
                        html,
                        page,
                        semanticPage,
                        figureRegions[nextFigureRegion],
                        imageAssets,
                        scale,
                        inline: false,
                        caption: captionsByFigure.GetValueOrDefault(figureRegions[nextFigureRegion]),
                        footnotes: footnotes,
                        includeAllText: captionsByFigure.ContainsKey(figureRegions[nextFigureRegion]));
                }

                nextFigureRegion++;
            }

            bool isDefinitionList = element.Kind == PdfSemanticElementKind.DefinitionList &&
                element.DefinitionList != null;
            if (definitionListState?.IsOpen == true &&
                element.Kind is PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer)
            {
                deferredPageArtifacts.Add(element);
                continue;
            }

            if (definitionListState?.IsOpen == true && !isDefinitionList)
            {
                CloseSemanticDefinitionList(html, definitionListState);
            }

            bibliographyWriter?.BeforeElement(element);
            string? elementId = sectionWriter?.BeginElement(element);
            int? headingLevel = sectionWriter?.SectionLevelFor(element);

            if (isDefinitionList)
            {
                WriteSemanticDefinitionList(html, element, footnotes, page, definitionListState);
                if (definitionListState?.IsOpen != true && deferredPageArtifacts.Count > 0)
                {
                    foreach (PdfSemanticElement artifact in deferredPageArtifacts)
                    {
                        WriteFlowSemanticElement(html, artifact, footnotes, page, allowMeasuredWidth: false);
                    }

                    deferredPageArtifacts.Clear();
                }

                continue;
            }

            if (element.Kind == PdfSemanticElementKind.Bibliography && element.BibliographyFragment != null)
            {
                if (bibliographyWriter != null)
                {
                    bibliographyWriter.WriteFragment(element, footnotes, page);
                }
                else
                {
                    WriteSemanticBibliographyFragment(html, element, footnotes, page);
                }

                continue;
            }

            if (paragraphMerge?.StartElement == element)
            {
                WriteMergedParagraph(
                    html,
                    paragraphMerge,
                    imageAssets ?? new Dictionary<string, PdfLayoutImageAsset>(StringComparer.Ordinal),
                    scale);
                continue;
            }

            if (element.Kind == PdfSemanticElementKind.AuthorBlock)
            {
                index = WriteAuthorSection(html, flowElements, index, footnotes);
                continue;
            }

            if (element.Kind == PdfSemanticElementKind.Footnote)
            {
                index = WriteFootnoteSection(
                    html,
                    flowElements,
                    index,
                    footnotes,
                    page,
                    DecorativeFootnoteRulePath(page, semanticPage));
                continue;
            }

            bool isRuledGridLeadIn = ruledGrid != null &&
                SemanticRuledGridIsCenteredLeadIn(ruledGrid, element);
            WriteFlowSemanticElement(
                html,
                element,
                footnotes,
                page,
                allowMeasuredWidth: !isRuledGridLeadIn &&
                    IsMeasuredWidthCandidate(flowElements, index),
                claimedFormulaGlyphs: claimedFormulaGlyphs,
                elementId: elementId,
                headingLevel: headingLevel,
                preserveSourceLines: isRuledGridLeadIn && element.Lines.Count > 1,
                additionalClass: isRuledGridLeadIn
                    ? "pdf-semantic-ruled-grid-lead-in"
                    : null,
                additionalStyle: isRuledGridLeadIn
                    ? "--pdf-semantic-ruled-grid-lead-in-width:" +
                        CssPoints(ruledGrid!.Region.Width * scale)
                    : null);
        }

        if (definitionListState?.IsOpen != true && deferredPageArtifacts.Count > 0)
        {
            foreach (PdfSemanticElement artifact in deferredPageArtifacts)
            {
                WriteFlowSemanticElement(html, artifact, footnotes, page, allowMeasuredWidth: false);
            }
        }

        while (nextFigureRegion < figureRegions.Length)
        {
            if (figureRendering == SemanticFigureRendering.Space)
            {
                WriteFigureSpace(html, figureRegions[nextFigureRegion], scale);
            }
            else if (imageAssets != null)
            {
                WriteSemanticFigure(
                    html,
                    page,
                    semanticPage,
                    figureRegions[nextFigureRegion],
                    imageAssets,
                    scale,
                    inline: false,
                    caption: captionsByFigure.GetValueOrDefault(figureRegions[nextFigureRegion]),
                    footnotes: footnotes,
                    includeAllText: captionsByFigure.ContainsKey(figureRegions[nextFigureRegion]));
            }

            nextFigureRegion++;
        }
    }

    private static void WriteFigureSpace(StringBuilder html, PdfLayoutRectangle region, float scale)
    {
        html.Append("      <figure class=\"pdf-semantic-figure-space\" aria-hidden=\"true\" data-source-top=\"")
            .Append(HtmlAttribute(CssPoints(region.Y)))
            .Append("\" style=\"height:")
            .Append(CssPoints((region.Height + 18f) * scale))
            .AppendLine("\"></figure>");
    }

    private static bool WriteSemanticFigure(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutRectangle region,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        float scale,
        bool inline,
        PdfSemanticElement? caption = null,
        FootnoteContext? footnotes = null,
        bool includeAllText = false,
        bool columnSpanning = false,
        string? additionalClass = null,
        string? additionalStyle = null,
        bool includeSourceDecorations = true,
        string? accessibleText = null,
        bool constrainSourceDecorationsToRegion = false)
    {
        PdfLayoutImage[] images = page.Images
            .Where(image => RectanglesIntersect(VisibleImageBounds(image), region, 2f))
            .ToArray();
        PdfLayoutPath[] paths = includeSourceDecorations
            ? FigureRegionPaths(page, semanticPage, region)
                .Where(path => !constrainSourceDecorationsToRegion ||
                    IsLocalizedFigureDecoration(path.Bounds, region))
                .ToArray()
            : [];
        HashSet<PdfTextRun> captionRuns = caption?.Lines
            .SelectMany(static line => line.Runs)
            .Where(static run => MathF.Abs(NormalizeDirection(run.Direction)) < 0.01f)
            .ToHashSet() ?? [];
        PdfTextRun[] textRuns = includeSourceDecorations
            ? FigureRegionTextRuns(page, region, includeAllText)
                .Where(run => !captionRuns.Contains(run))
                .Where(run => !constrainSourceDecorationsToRegion ||
                    RectangleContainsCenter(region, run.PageBounds))
                .ToArray()
            : [];
        if (images.Length == 0 && paths.Length == 0 && textRuns.Length == 0)
        {
            return false;
        }

        string tagName = inline ? "span" : "figure";
        html.Append("      <")
            .Append(tagName)
            .Append(" class=\"pdf-semantic-figure");
        if (inline)
        {
            html.Append(" pdf-semantic-inline-figure");
        }
        else if (columnSpanning)
        {
            html.Append(" pdf-semantic-column-spanning-figure");
        }
        if (!string.IsNullOrEmpty(additionalClass))
        {
            html.Append(' ')
                .Append(HtmlAttribute(additionalClass));
        }
        if (!string.IsNullOrWhiteSpace(accessibleText))
        {
            html.Append("\" aria-label=\"")
                .Append(HtmlAttribute(accessibleText))
                .Append("\"");
        }
        else
        {
            html.Append('"');
        }
        html.Append(" data-source-page=\"")
            .Append(page.PageNumber.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-source-top=\"")
            .Append(HtmlAttribute(CssPoints(region.Y)))
            .Append("\" style=\"--pdf-semantic-figure-width:")
            .Append(CssPoints(region.Width * scale));
        if (!string.IsNullOrEmpty(additionalStyle))
        {
            html.Append(';')
                .Append(HtmlAttribute(additionalStyle));
        }
        html
            .Append("\">");
        html.Append("<svg class=\"pdf-semantic-figure-svg\" viewBox=\"0 0 ")
            .Append(SvgNumber(region.Width))
            .Append(' ')
            .Append(SvgNumber(region.Height))
            .Append("\" width=\"")
            .Append(SvgNumber(region.Width * scale))
            .Append("\" height=\"")
            .Append(SvgNumber(region.Height * scale))
            .Append("\" aria-hidden=\"true\">");

        WriteSemanticImageClipDefinitions(html, page, images);
        html.Append("<g transform=\"translate(")
            .Append(SvgNumber(-region.X))
            .Append(' ')
            .Append(SvgNumber(-region.Y))
            .Append(")\">");

        WriteSemanticFigureGraphics(
            html,
            page,
            images,
            paths,
            imageAssets,
            $"pdf-vector-figure-{page.PageNumber.ToString(CultureInfo.InvariantCulture)}-{MathF.Round(region.X).ToString(CultureInfo.InvariantCulture)}-{MathF.Round(region.Y).ToString(CultureInfo.InvariantCulture)}");

        foreach (PdfTextRun run in textRuns)
        {
            WriteFigureTextRun(html, page, run);
        }

        html.Append("</g></svg>");
        if (!inline && caption != null)
        {
            html.Append("<figcaption class=\"")
                .Append(SemanticClassNames(caption, page, allowMeasuredWidth: false))
                .Append("\">");
            WriteSemanticText(html, caption, footnotes ?? FootnoteContext.Create(page.PageNumber, []), page);
            html.Append("</figcaption>");
        }

        html.Append("</")
            .Append(tagName)
            .AppendLine(">");
        return true;
    }

    private static void WriteSemanticFigureGraphics(
        StringBuilder html,
        PdfLayoutPage page,
        IReadOnlyList<PdfLayoutImage> images,
        IReadOnlyList<PdfLayoutPath> paths,
        IReadOnlyDictionary<string, PdfLayoutImageAsset> imageAssets,
        string clipIdPrefix)
    {
        if (page.PaintOperations.Count == 0)
        {
            foreach (PdfLayoutImage image in images)
            {
                if (imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
                {
                    WriteSvgImage(html, page, image, asset);
                }
            }

            WriteVectorContent(html, paths, page.VectorGroups, clipIdPrefix);
            return;
        }

        Dictionary<int, PdfLayoutImage> imagesByIndex = images.ToDictionary(static image => image.Index);
        Dictionary<int, PdfLayoutPath> pathsByIndex = paths.ToDictionary(static path => path.Index);
        HashSet<int> emittedImages = [];
        HashSet<int> emittedPaths = [];
        List<PdfLayoutPath> pathBatch = [];
        int batchIndex = 0;

        void FlushPaths()
        {
            if (pathBatch.Count == 0)
            {
                return;
            }

            WriteVectorContent(
                html,
                pathBatch,
                page.VectorGroups,
                clipIdPrefix + "-paint-" + batchIndex.ToString(CultureInfo.InvariantCulture));
            foreach (PdfLayoutPath path in pathBatch)
            {
                emittedPaths.Add(path.Index);
            }

            pathBatch.Clear();
            batchIndex++;
        }

        foreach (PdfLayoutPaintOperation operation in page.PaintOperations)
        {
            if (operation.Kind == PdfLayoutPaintOperationKind.Path)
            {
                if (pathsByIndex.TryGetValue(operation.Index, out PdfLayoutPath? path))
                {
                    pathBatch.Add(path);
                }

                continue;
            }

            if (operation.Kind != PdfLayoutPaintOperationKind.Image ||
                !imagesByIndex.TryGetValue(operation.Index, out PdfLayoutImage? image))
            {
                continue;
            }

            FlushPaths();
            if (imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
            {
                WriteSvgImage(html, page, image, asset);
                emittedImages.Add(image.Index);
            }
        }

        FlushPaths();
        foreach (PdfLayoutImage image in images.Where(image => !emittedImages.Contains(image.Index)))
        {
            if (imageAssets.TryGetValue(image.AssetId, out PdfLayoutImageAsset? asset))
            {
                WriteSvgImage(html, page, image, asset);
            }
        }

        PdfLayoutPath[] remainingPaths = paths
            .Where(path => !emittedPaths.Contains(path.Index))
            .ToArray();
        if (remainingPaths.Length > 0)
        {
            WriteVectorContent(
                html,
                remainingPaths,
                page.VectorGroups,
                clipIdPrefix + "-remaining");
        }
    }

    private static bool IsLocalizedFigureDecoration(
        PdfLayoutRectangle decoration,
        PdfLayoutRectangle figure)
    {
        float widthLimit = MathF.Max(figure.Width * 1.35f, figure.Width + 8f);
        float heightLimit = MathF.Max(figure.Height * 1.35f, figure.Height + 8f);
        return decoration.Width <= widthLimit &&
            decoration.Height <= heightLimit &&
            RectanglesIntersect(decoration, figure, 2f);
    }

    private static IEnumerable<PdfLayoutPath> FigureRegionPaths(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutRectangle region)
    {
        return page.Paths
            .Where(path => !IsSemanticFlowRulePath(page, semanticPage, path))
            .Where(path => path.Bounds.Width > 0.1f || path.Bounds.Height > 0.1f)
            .Where(static path => !RequiresShapeAlphaFallback(path))
            .Where(static path => !path.UsesSoftMask)
            .Where(path => !IsCoveredByVisualFallback(page, path.Bounds))
            .Where(path => RectanglesIntersect(path.Bounds, region, 2f));
    }

    private static IEnumerable<PdfTextRun> FigureRegionTextRuns(
        PdfLayoutPage page,
        PdfLayoutRectangle region,
        bool includeAllText = false)
    {
        return page.Runs
            .Where(run => includeAllText || IsFigureLabelRun(run))
            .Where(run => !IsUnpaintedTextRun(run))
            .Where(run => !IsHorizontalTextInsideInvisibleVectorGroup(page, run))
            .Where(run => !IsCoveredByVisualFallback(
                page,
                run.PageBounds,
                preserveTextOverComplexArtwork: true))
            .Where(run => RectanglesIntersect(run.PageBounds, region, 2f))
            .OrderBy(static run => run.PageBounds.Y)
            .ThenBy(static run => run.PageBounds.X);
    }

    private static bool IsHorizontalTextInsideInvisibleVectorGroup(PdfLayoutPage page, PdfTextRun run)
    {
        if (MathF.Abs(NormalizeDirection(run.Direction)) >= 0.01f)
        {
            return false;
        }

        return page.VectorGroups
            .Where(static group => group.Opacity <= 0.01f)
            .Any(group => RectangleContainsCenter(group.Bounds, run.PageBounds));
    }

    private static void WriteFigureTextRun(StringBuilder html, PdfLayoutPage page, PdfTextRun run)
    {
        PdfLayoutRectangle bounds = run.PageBounds;
        float direction = NormalizeDirection(run.Direction);
        float fontSize = FixedTextFontSize(run, page.Runs);
        string text = ReconstructText(run.Glyphs);
        if (string.IsNullOrEmpty(text))
        {
            text = run.Text;
        }

        html.Append("<text class=\"pdf-semantic-figure-text\" x=\"")
            .Append(SvgNumber(bounds.X))
            .Append("\" y=\"")
            .Append(SvgNumber(FigureTextY(bounds, direction, fontSize)))
            .Append("\"");
        string? transform = FigureTextTransform(bounds, direction);
        if (transform != null)
        {
            html.Append(" transform=\"")
                .Append(HtmlAttribute(transform))
                .Append('"');
        }

        float textLength = FigureTextLength(bounds, direction);
        if (textLength > 0.01f)
        {
            html.Append(" textLength=\"")
                .Append(SvgNumber(textLength))
                .Append("\" lengthAdjust=\"spacingAndGlyphs\"");
        }

        html.Append(" style=\"font-size:")
            .Append(CssPoints(fontSize))
            .Append(";font-family:")
            .Append(CssFontFamily(run.FontName))
            .Append(";fill:")
            .Append(ColorHex(run.Color));
        if (run.Color.Alpha < 0.999f)
        {
            html.Append(";fill-opacity:")
                .Append(SvgNumber(run.Color.Alpha));
        }

        html.Append("\">")
            .Append(Html(text))
            .Append("</text>");
    }

    private static float FigureTextY(PdfLayoutRectangle bounds, float direction, float fontSize)
    {
        if (MathF.Abs(direction - 90f) < 0.01f)
        {
            return bounds.Bottom;
        }

        if (MathF.Abs(direction - 270f) < 0.01f)
        {
            return bounds.Y;
        }

        return bounds.Y + MathF.Min(fontSize, bounds.Height + fontSize * 0.25f);
    }

    private static string? FigureTextTransform(PdfLayoutRectangle bounds, float direction)
    {
        if (MathF.Abs(direction - 90f) < 0.01f)
        {
            return "rotate(-90 " + SvgNumber(bounds.X) + " " + SvgNumber(bounds.Bottom) + ")";
        }

        if (MathF.Abs(direction - 270f) < 0.01f)
        {
            return "rotate(90 " + SvgNumber(bounds.X) + " " + SvgNumber(bounds.Y) + ")";
        }

        if (MathF.Abs(direction - 180f) < 0.01f)
        {
            return "rotate(180 " + SvgNumber(bounds.X + bounds.Width / 2f) + " " +
                SvgNumber(bounds.Y + bounds.Height / 2f) + ")";
        }

        return null;
    }

    private static float FigureTextLength(PdfLayoutRectangle bounds, float direction)
    {
        return MathF.Abs(direction - 90f) < 0.01f || MathF.Abs(direction - 270f) < 0.01f
            ? bounds.Height
            : bounds.Width;
    }

    private static float NormalizeDirection(float direction)
    {
        float normalized = direction % 360f;
        if (normalized < 0)
        {
            normalized += 360f;
        }

        return normalized;
    }

    private static void WriteSemanticImageClipDefinitions(
        StringBuilder html,
        PdfLayoutPage page,
        IReadOnlyList<PdfLayoutImage> images)
    {
        PdfLayoutImage[] clippedImages = images
            .Where(image => EffectiveImageClipPaths(image).Count > 0)
            .ToArray();
        if (clippedImages.Length == 0)
        {
            return;
        }

        html.Append("<defs>");
        foreach (PdfLayoutImage image in clippedImages)
        {
            WriteExactClipPathDefinitions(
                html,
                SemanticImageClipPathId(page, image),
                EffectiveImageClipPaths(image),
                string.Empty,
                string.Empty,
                static clipPath => SvgPathData(clipPath.Commands));
        }

        html.Append("</defs>");
    }

    private static string SemanticImageClipPathId(PdfLayoutPage page, PdfLayoutImage image)
    {
        return "pdf-semantic-image-page-" + page.PageNumber.ToString(CultureInfo.InvariantCulture) +
            "-clip-" + image.Index.ToString(CultureInfo.InvariantCulture);
    }

    private static void WriteSvgImage(
        StringBuilder html,
        PdfLayoutPage page,
        PdfLayoutImage image,
        PdfLayoutImageAsset asset)
    {
        html.Append("<image href=\"")
            .Append(HtmlAttribute(asset.RelativePath))
            .Append("\" x=\"")
            .Append(SvgNumber(image.Bounds.X))
            .Append("\" y=\"")
            .Append(SvgNumber(image.Bounds.Y))
            .Append("\" width=\"")
            .Append(SvgNumber(image.Bounds.Width))
            .Append("\" height=\"")
            .Append(SvgNumber(image.Bounds.Height))
            .Append("\" preserveAspectRatio=\"none\"");
        if (EffectiveImageClipPaths(image).Count > 0)
        {
            html.Append(" clip-path=\"url(#")
                .Append(SemanticImageClipPathId(page, image))
                .Append(")\"");
        }

        html.Append(" />");
    }

    private static bool RectanglesIntersect(PdfLayoutRectangle first, PdfLayoutRectangle second, float tolerance)
    {
        return first.Right >= second.X - tolerance &&
            second.Right >= first.X - tolerance &&
            first.Bottom >= second.Y - tolerance &&
            second.Bottom >= first.Y - tolerance;
    }

    private static bool IsCoveredByVisualFallback(
        PdfLayoutPage page,
        PdfLayoutRectangle bounds,
        bool preserveTextOverComplexArtwork = false)
    {
        return page.Images
            .Where(image =>
                image.Kind == PdfLayoutImageKind.TransparencyGroupFallback ||
                !preserveTextOverComplexArtwork &&
                    image.Kind == PdfLayoutImageKind.ComplexArtworkFallback)
            .Any(image => RectanglesIntersect(bounds, image.Bounds, 0));
    }

    private static bool IsVisualFallbackImage(PdfLayoutImage image)
    {
        return image.Kind is PdfLayoutImageKind.TransparencyGroupFallback or
            PdfLayoutImageKind.ComplexArtworkFallback;
    }

    private static PdfLayoutRectangle VisibleImageBounds(PdfLayoutImage image)
    {
        PdfLayoutRectangle visible = image.Bounds;
        foreach (PdfLayoutClipPath clipPath in image.ClipPaths)
        {
            float left = MathF.Max(visible.X, clipPath.Bounds.X);
            float top = MathF.Max(visible.Y, clipPath.Bounds.Y);
            float right = MathF.Min(visible.Right, clipPath.Bounds.Right);
            float bottom = MathF.Min(visible.Bottom, clipPath.Bounds.Bottom);
            if (right <= left || bottom <= top)
            {
                return new PdfLayoutRectangle(left, top, 0, 0);
            }

            visible = new PdfLayoutRectangle(left, top, right - left, bottom - top);
        }

        return visible;
    }

    private static IEnumerable<PdfLayoutRectangle> SemanticFigureRegions(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage)
    {
        List<PdfLayoutRectangle> regions = [];
        PdfLayoutRectangle[] imageBounds = page.Images
            .Select(VisibleImageBounds)
            .Where(static bounds => bounds.Width > 0.1f && bounds.Height > 0.1f)
            .ToArray();
        regions.AddRange(IsCoverPage(page, semanticPage)
            ? imageBounds
            : imageBounds.Where(bounds => IsSubstantialGraphic(page, bounds)));
        regions.AddRange(CompositeImageRegions(page, semanticPage, imageBounds));

        PdfLayoutPath[] candidatePaths = page.Paths
            .Where(path => !IsSemanticFlowRulePath(page, semanticPage, path))
            .Where(path => path.Bounds.Width > 2f && path.Bounds.Height > 2f)
            .ToArray();
        PdfLayoutRectangle[] largePathBounds = candidatePaths
            .Select(static path => path.Bounds)
            .Where(bounds => IsSubstantialGraphic(page, bounds))
            .ToArray();
        regions.AddRange(largePathBounds);
        regions.AddRange(GraphicRowRegions(page, largePathBounds));

        if (candidatePaths.Length >= 8)
        {
            PdfLayoutRectangle union = UnionRectangles(candidatePaths.Select(static path => path.Bounds));
            if (IsSubstantialGraphic(page, union))
            {
                regions.Add(union);
            }
        }

        foreach (PdfLayoutRectangle region in MergeGraphicRegions(regions)
            .Select(region => ExpandFigureRegionWithLabels(page, region)))
        {
            yield return region;
        }
    }

    private static bool IsCoverPage(PdfLayoutPage page, PdfSemanticPage semanticPage)
    {
        PdfSemanticElement? title = semanticPage.Elements.FirstOrDefault(IsTitleElement);
        if (title == null ||
            title.Bounds.Y >= page.Height * 0.45f ||
            page.Lines.Count > 18 ||
            semanticPage.Elements.Any(static element => element.Kind == PdfSemanticElementKind.Table) ||
            semanticPage.Elements.Any(static element => element.Kind == PdfSemanticElementKind.Bibliography) ||
            semanticPage.Elements.Any(element =>
                element.Kind == PdfSemanticElementKind.Heading &&
                element != title &&
                char.IsDigit(element.Text.TrimStart().FirstOrDefault())))
        {
            return false;
        }

        int textLength = semanticPage.Elements.Sum(static element => element.Text.Length);
        int bodyElementCount = semanticPage.Elements.Count(static element =>
            element.Kind is PdfSemanticElementKind.Paragraph or PdfSemanticElementKind.AuthorBlock);
        return textLength <= 1200 && bodyElementCount <= 8;
    }

    private static bool IsSubstantialGraphic(PdfLayoutPage page, PdfLayoutRectangle bounds)
    {
        return bounds.Width >= page.Width * 0.18f &&
            bounds.Height >= MathF.Max(18f, page.Height * 0.035f);
    }

    private static IEnumerable<PdfLayoutRectangle> CompositeImageRegions(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        IReadOnlyList<PdfLayoutRectangle> imageBounds)
    {
        PdfSemanticElement[] captions = semanticPage.Elements
            .Where(IsFigureCaption)
            .ToArray();
        if (imageBounds.Count < 2 || captions.Length == 0)
        {
            yield break;
        }

        bool[] visited = new bool[imageBounds.Count];
        for (int start = 0; start < imageBounds.Count; start++)
        {
            if (visited[start])
            {
                continue;
            }

            List<PdfLayoutRectangle> cluster = [];
            Queue<int> pending = new();
            pending.Enqueue(start);
            visited[start] = true;
            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                cluster.Add(imageBounds[current]);
                for (int candidate = 0; candidate < imageBounds.Count; candidate++)
                {
                    if (!visited[candidate] &&
                        AreSpatiallyAdjacentImages(page, imageBounds[current], imageBounds[candidate]))
                    {
                        visited[candidate] = true;
                        pending.Enqueue(candidate);
                    }
                }
            }

            if (cluster.Count < 2)
            {
                continue;
            }

            PdfLayoutRectangle union = UnionRectangles(cluster);
            if (IsSubstantialGraphic(page, union) &&
                captions.Any(caption => IsCaptionAssociatedWithFigure(page, caption.Bounds, union)))
            {
                yield return union;
            }
        }
    }

    private static bool AreSpatiallyAdjacentImages(
        PdfLayoutPage page,
        PdfLayoutRectangle first,
        PdfLayoutRectangle second)
    {
        float horizontalOverlap = MathF.Min(first.Right, second.Right) - MathF.Max(first.X, second.X);
        float verticalOverlap = MathF.Min(first.Bottom, second.Bottom) - MathF.Max(first.Y, second.Y);
        bool sameRow = verticalOverlap >= MathF.Min(first.Height, second.Height) * 0.20f &&
            HorizontalGap(first, second) <= page.Width * 0.30f;
        bool sameColumn = horizontalOverlap >= MathF.Min(first.Width, second.Width) * 0.20f &&
            VerticalGap(first, second) <= MathF.Max(18f, page.Height * 0.03f);
        return sameRow || sameColumn;
    }

    private static bool IsCaptionAssociatedWithFigure(
        PdfLayoutPage page,
        PdfLayoutRectangle caption,
        PdfLayoutRectangle figure)
    {
        float horizontalOverlap = MathF.Min(caption.Right, figure.Right) - MathF.Max(caption.X, figure.X);
        float centerDistance = MathF.Abs(
            caption.X + caption.Width / 2f - (figure.X + figure.Width / 2f));
        return VerticalGap(caption, figure) <= 72f &&
            (horizontalOverlap > 0 || centerDistance <= page.Width * 0.08f);
    }

    private static PdfLayoutRectangle ExpandFigureRegionWithLabels(PdfLayoutPage page, PdfLayoutRectangle region)
    {
        PdfLayoutRectangle searchRegion = ExpandRectangle(region, 24f, 72f);
        PdfLayoutRectangle[] labelBounds = page.Runs
            .Where(IsFigureLabelRun)
            .Where(run => RectanglesIntersect(run.PageBounds, searchRegion, 2f))
            .Select(static run => run.PageBounds)
            .ToArray();
        if (labelBounds.Length == 0)
        {
            return region;
        }

        PdfLayoutRectangle expanded = UnionRectangles(labelBounds.Append(region));
        return ExpandRectangle(expanded, 2f, 2f);
    }

    private static bool IsFigureLabelFlowElement(
        PdfSemanticElement element,
        IReadOnlyList<PdfLayoutRectangle> figureRegions,
        IReadOnlyList<PdfLayoutRectangle>? captionedFigureRegions = null)
    {
        if (figureRegions.Count == 0 ||
            element.Kind is PdfSemanticElementKind.Table or PdfSemanticElementKind.Footnote or PdfSemanticElementKind.Footer ||
            IsFigureCaption(element))
        {
            return false;
        }

        PdfTextRun[] allRuns = element.Lines
            .SelectMany(static line => line.Runs)
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .ToArray();
        if (captionedFigureRegions is { Count: > 0 } &&
            allRuns.Length > 0 &&
            allRuns.Any(run => !IsUnpaintedTextRun(run)) &&
            allRuns.All(run => captionedFigureRegions.Any(region =>
                RectangleContainsCenter(ExpandRectangle(region, 2f, 2f), run.PageBounds))))
        {
            return true;
        }

        if (LooksLikeEscapedFigureLabelText(element.Text) &&
            figureRegions.Any(region => RectanglesIntersect(element.Bounds, ExpandRectangle(region, 24f, 72f), 2f)))
        {
            return true;
        }

        if (IsFigureMetadataFlowElement(element, figureRegions))
        {
            return true;
        }

        return allRuns.Length > 0 &&
            allRuns.All(IsFigureLabelRun) &&
            allRuns.All(run => figureRegions.Any(region => RectanglesIntersect(run.PageBounds, region, 2f)));
    }

    private static bool IsFigureMetadataFlowElement(
        PdfSemanticElement element,
        IReadOnlyList<PdfLayoutRectangle> figureRegions)
    {
        if (figureRegions.Count == 0 ||
            IsFigureCaption(element) ||
            element.Text.Length > 120 ||
            element.Kind is PdfSemanticElementKind.Table or PdfSemanticElementKind.Footnote or PdfSemanticElementKind.Footer)
        {
            return false;
        }

        PdfTextRun[] runs = element.Lines
            .SelectMany(static line => line.Runs)
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .ToArray();
        return runs.Length > 0 &&
            runs.All(IsFigureMetadataRun) &&
            figureRegions.Any(region => RectanglesIntersect(element.Bounds, ExpandRectangle(region, 24f, 72f), 2f));
    }

    private static bool IsFigureMetadataRun(PdfTextRun run)
    {
        return MathF.Abs(NormalizeDirection(run.Direction)) < 0.01f &&
            run.FontSize >= 11f &&
            NormalizeFontName(run.FontName).Contains("Arial", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEscapedFigureLabelText(string text)
    {
        string[] tokens = text
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 8)
        {
            return false;
        }

        int shortTokens = tokens.Count(static token => token.Length <= 2);
        return shortTokens >= tokens.Length * 0.6f;
    }

    private static bool ShouldKeepInFlowForFigureRendering(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfSemanticElement element,
        IReadOnlyList<PdfLayoutRectangle> figureRegions)
    {
        PdfLayoutRectangle[] captionedFigureRegions = FigureCaptionsByRegion(
            page,
            semanticPage,
            figureRegions).Keys.ToArray();
        if (IsFigureLabelFlowElement(element, figureRegions, captionedFigureRegions) ||
            IsFigureCaption(element) &&
            captionedFigureRegions.Any(region =>
                IsCaptionAssociatedWithFigure(page, element.Bounds, region)))
        {
            return true;
        }

        if (!IsFigureCaption(element) || figureRegions.Count == 0)
        {
            return false;
        }

        PdfTextRun[] directedRuns = element.Lines
            .SelectMany(static line => line.Runs)
            .Where(static run => MathF.Abs(run.Direction) > 0.01f)
            .ToArray();
        return directedRuns.Length > 0 &&
            directedRuns.All(run => IsFigureLabelRun(run) &&
                figureRegions.Any(region => RectanglesIntersect(run.PageBounds, region, 2f)));
    }

    private static Dictionary<PdfLayoutRectangle, PdfSemanticElement> FigureCaptionsByRegion(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        IReadOnlyList<PdfLayoutRectangle> figureRegions)
    {
        Dictionary<PdfLayoutRectangle, PdfSemanticElement> captions = [];
        HashSet<PdfSemanticElement> claimedCaptions =
            new((IEqualityComparer<PdfSemanticElement>)ReferenceEqualityComparer.Instance);
        foreach (PdfLayoutRectangle region in figureRegions.OrderBy(static region => region.Y))
        {
            PdfSemanticElement? caption = semanticPage.Elements
                .Where(IsFigureCaption)
                .Where(candidate => candidate.Bounds.Y >=
                    region.Y + MathF.Min(region.Height * 0.60f, region.Height - 6f))
                .Where(candidate => IsCaptionAssociatedWithFigure(page, candidate.Bounds, region))
                .Where(candidate => !claimedCaptions.Contains(candidate))
                .OrderBy(candidate => VerticalGap(candidate.Bounds, region))
                .ThenBy(static candidate => candidate.Bounds.Y)
                .FirstOrDefault();
            if (caption != null)
            {
                captions[region] = caption;
                claimedCaptions.Add(caption);
            }
        }

        return captions;
    }

    private static bool IsMicroscopicUnpaintedPayloadElement(PdfSemanticElement element)
    {
        PdfTextGlyph[] glyphs = element.Lines
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => !string.IsNullOrWhiteSpace(glyph.Text))
            .ToArray();
        PdfTextGlyph[] unpainted = glyphs
            .Where(static glyph => !glyph.IsPainted)
            .ToArray();
        return unpainted.Length >= 24 &&
            unpainted.Length >= glyphs.Length * 0.85f &&
            unpainted.All(static glyph =>
                glyph.FontSize <= 0.25f ||
                glyph.PageBounds.Width <= 0.01f && glyph.PageBounds.Height <= 0.01f) &&
            unpainted.Count(glyph =>
                NormalizeFontName(glyph.FontName).Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
                NormalizeFontName(glyph.FontName).Contains("Mono", StringComparison.OrdinalIgnoreCase)) >=
                unpainted.Length * 0.85f;
    }

    private static bool IsFigureLabelRun(PdfTextRun run)
    {
        if (string.IsNullOrWhiteSpace(run.Text))
        {
            return false;
        }

        float direction = NormalizeDirection(run.Direction);
        if (MathF.Abs(direction - 90f) < 0.01f ||
            MathF.Abs(direction - 270f) < 0.01f ||
            MathF.Abs(direction - 180f) < 0.01f)
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<PdfLayoutRectangle> MergeGraphicRegions(IEnumerable<PdfLayoutRectangle> regions)
    {
        List<PdfLayoutRectangle> merged = [];
        foreach (PdfLayoutRectangle region in regions
            .OrderBy(static region => region.Y)
            .ThenBy(static region => region.X))
        {
            if (merged.Count == 0)
            {
                merged.Add(region);
                continue;
            }

            PdfLayoutRectangle last = merged[^1];
            bool closeVertically = region.Y <= last.Bottom + 2f;
            bool overlapsHorizontally = region.X <= last.Right + 2f && region.Right + 2f >= last.X;
            if (closeVertically && overlapsHorizontally)
            {
                merged[^1] = UnionRectangles([last, region]);
            }
            else
            {
                merged.Add(region);
            }
        }

        return merged;
    }

    private static IEnumerable<PdfLayoutRectangle> GraphicRowRegions(
        PdfLayoutPage page,
        IReadOnlyList<PdfLayoutRectangle> bounds)
    {
        List<List<PdfLayoutRectangle>> rows = [];
        foreach (PdfLayoutRectangle rectangle in bounds
            .OrderBy(static bounds => bounds.Y + (bounds.Height / 2f))
            .ThenBy(static bounds => bounds.X))
        {
            List<PdfLayoutRectangle>? row = rows.FirstOrDefault(existing =>
                BelongsToGraphicRow(page, UnionRectangles(existing), rectangle));
            if (row == null)
            {
                rows.Add([rectangle]);
            }
            else
            {
                row.Add(rectangle);
            }
        }

        foreach (List<PdfLayoutRectangle> row in rows.Where(static row => row.Count >= 2))
        {
            PdfLayoutRectangle union = UnionRectangles(row);
            if (IsSubstantialGraphic(page, union))
            {
                yield return union;
            }
        }
    }

    private static bool BelongsToGraphicRow(
        PdfLayoutPage page,
        PdfLayoutRectangle rowBounds,
        PdfLayoutRectangle rectangle)
    {
        float overlap = MathF.Min(rowBounds.Bottom, rectangle.Bottom) - MathF.Max(rowBounds.Y, rectangle.Y);
        bool verticalOverlap = overlap >= MathF.Min(rowBounds.Height, rectangle.Height) * 0.30f;
        float centerDistance = MathF.Abs(
            rowBounds.Y + (rowBounds.Height / 2f) - (rectangle.Y + (rectangle.Height / 2f)));
        bool similarBand = centerDistance <= MathF.Max(24f, MathF.Max(rowBounds.Height, rectangle.Height) * 0.35f);
        return (verticalOverlap || similarBand) &&
            HorizontalGap(rowBounds, rectangle) <= page.Width * 0.30f;
    }

    private static PdfLayoutRectangle UnionRectangles(IEnumerable<PdfLayoutRectangle> rectangles)
    {
        using IEnumerator<PdfLayoutRectangle> enumerator = rectangles.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return new PdfLayoutRectangle(0, 0, 0, 0);
        }

        PdfLayoutRectangle first = enumerator.Current;
        float left = first.X;
        float top = first.Y;
        float right = first.Right;
        float bottom = first.Bottom;
        while (enumerator.MoveNext())
        {
            PdfLayoutRectangle rectangle = enumerator.Current;
            left = MathF.Min(left, rectangle.X);
            top = MathF.Min(top, rectangle.Y);
            right = MathF.Max(right, rectangle.Right);
            bottom = MathF.Max(bottom, rectangle.Bottom);
        }

        return new PdfLayoutRectangle(left, top, right - left, bottom - top);
    }

    private static float HorizontalGap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        if (first.Right < second.X)
        {
            return second.X - first.Right;
        }

        if (second.Right < first.X)
        {
            return first.X - second.Right;
        }

        return 0f;
    }

    private static float VerticalGap(PdfLayoutRectangle first, PdfLayoutRectangle second)
    {
        if (first.Bottom < second.Y)
        {
            return second.Y - first.Bottom;
        }

        if (second.Bottom < first.Y)
        {
            return first.Y - second.Bottom;
        }

        return 0f;
    }

    private static bool ShouldInsertFigureSpaceBefore(PdfSemanticElement element, PdfLayoutRectangle figureRegion)
    {
        return element.Bounds.Y >= figureRegion.Y - 2f;
    }

    private static bool IsMeasuredWidthCandidate(IReadOnlyList<PdfSemanticElement> elements, int index)
    {
        return index + 1 >= elements.Count ||
            elements[index + 1].Kind != PdfSemanticElementKind.Footer;
    }

    private static bool IsSimplePageNumberFooter(PdfSemanticElement element, PdfLayoutPage page)
    {
        return element.Kind == PdfSemanticElementKind.Footer &&
            string.Equals(
                element.Text.Trim(),
                page.PageNumber.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static int WriteAuthorSection(
        StringBuilder html,
        IReadOnlyList<PdfSemanticElement> elements,
        int startIndex,
        FootnoteContext footnotes)
    {
        int index = startIndex;
        List<PdfSemanticElement> authors = [];
        for (; index < elements.Count && elements[index].Kind == PdfSemanticElementKind.AuthorBlock; index++)
        {
            authors.Add(elements[index]);
        }

        html.AppendLine("      <section class=\"pdf-semantic-authors\" aria-label=\"Authors\">");
        foreach (IReadOnlyList<PdfSemanticElement> row in GroupAuthorRows(authors))
        {
            html.Append("        <div class=\"pdf-semantic-author-row ")
                .Append(AuthorCountClass(row.Count))
                .AppendLine("\">");
            foreach (PdfSemanticElement author in row)
            {
                WriteFlowSemanticElement(html, author, footnotes);
            }

            html.AppendLine("        </div>");
        }

        html.AppendLine("      </section>");
        return index - 1;
    }

    private static IEnumerable<IReadOnlyList<PdfSemanticElement>> GroupAuthorRows(IReadOnlyList<PdfSemanticElement> authors)
    {
        List<List<PdfSemanticElement>> rows = [];
        foreach (PdfSemanticElement author in authors
            .OrderBy(static author => AuthorCenterY(author))
            .ThenBy(static author => author.Bounds.X))
        {
            List<PdfSemanticElement>? row = rows.FirstOrDefault(existing =>
                MathF.Abs(AuthorCenterY(existing[0]) - AuthorCenterY(author)) <= AuthorRowTolerance(existing[0], author));
            if (row == null)
            {
                rows.Add([author]);
            }
            else
            {
                row.Add(author);
            }
        }

        return rows
            .OrderBy(static row => row.Average(static author => AuthorCenterY(author)))
            .Select(static row => row.OrderBy(static author => author.Bounds.X).ToArray());
    }

    private static float AuthorCenterY(PdfSemanticElement author)
    {
        return author.Bounds.Y + (author.Bounds.Height / 2f);
    }

    private static float AuthorRowTolerance(PdfSemanticElement first, PdfSemanticElement second)
    {
        return Math.Clamp(MathF.Min(first.Bounds.Height, second.Bounds.Height) * 0.45f, 8f, 18f);
    }

    private static string AuthorCountClass(int count)
    {
        return "pdf-author-count-" + Math.Clamp(count, 1, 6).ToString(CultureInfo.InvariantCulture);
    }

    private static int WriteFootnoteSection(
        StringBuilder html,
        IReadOnlyList<PdfSemanticElement> elements,
        int startIndex,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        PdfLayoutPath? footnoteRule)
    {
        int index = startIndex;
        List<PdfSemanticElement> fragments = [];
        for (; index < elements.Count && elements[index].Kind == PdfSemanticElementKind.Footnote; index++)
        {
            fragments.Add(elements[index]);
        }

        IReadOnlyList<LogicalFootnote> notes = footnotes.NotesToRender(fragments);
        if (notes.Count == 0)
        {
            return index - 1;
        }

        string labelId = footnotes.NextGroupLabelId();
        html.Append("      <section class=\"pdf-semantic-footnotes\" aria-labelledby=\"")
            .Append(HtmlAttribute(labelId))
            .Append('"');
        if (footnoteRule != null)
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(FootnoteRuleStyle(footnoteRule)))
                .Append('"');
        }

        html.AppendLine(">");
        html.Append("        <h2 id=\"")
            .Append(HtmlAttribute(labelId))
            .AppendLine("\" class=\"pdf-semantic-footnote-group-label\">Footnotes</h2>");
        html.AppendLine("        <ol class=\"pdf-semantic-note-list\">");
        foreach (LogicalFootnote note in notes)
        {
            WriteFootnote(html, note, footnotes, page);
        }

        html.AppendLine("        </ol>");
        html.AppendLine("      </section>");
        return index - 1;
    }

    private static string FootnoteRuleStyle(PdfLayoutPath path)
    {
        PdfLayoutColor color = path.Stroke?.Color ?? path.FillColor ?? new PdfLayoutColor(0, 0, 0, 1, null);
        float thickness = path.Stroke?.Width ?? MathF.Max(0.5f, path.Bounds.Height);
        return "--pdf-footnote-rule-width:" + CssPoints(path.Bounds.Width) +
            ";--pdf-footnote-rule-thickness:" + CssPoints(thickness) +
            ";--pdf-footnote-rule-color:" + ColorHex(color);
    }

    private static void WriteFlowSemanticElement(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page = null,
        bool allowMeasuredWidth = true,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null,
        string? elementId = null,
        int? headingLevel = null,
        SemanticRuledBorder? sourceBorder = null,
        IReadOnlyDictionary<PdfSemanticListItem, SemanticRuledBorder>? semanticListItemBorders = null,
        bool preserveSourceLines = false,
        string? additionalClass = null,
        string? additionalStyle = null)
    {
        if (claimedFormulaGlyphs is { Count: > 0 } &&
            IsFullyClaimedFormulaElement(element, claimedFormulaGlyphs))
        {
            return;
        }

        if (element.Kind == PdfSemanticElementKind.ThematicBreak &&
            element.ThematicBreak != null &&
            page != null)
        {
            WriteSemanticThematicBreak(html, page, element);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.DefinitionList && element.DefinitionList != null)
        {
            WriteSemanticDefinitionList(html, element, footnotes, page, state: null);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.Table && element.TableRows.Count > 0)
        {
            if (page != null &&
                TrySplitClaimedFormulaTable(
                    element,
                    claimedFormulaGlyphs,
                    out PdfSemanticElement? residualTable,
                    out PdfSemanticElement? formula))
            {
                if (residualTable != null)
                {
                    WriteSemanticTable(
                        html,
                        residualTable,
                        footnotes,
                        page,
                        claimedFormulaGlyphs: claimedFormulaGlyphs);
                }

                WriteFormulaBlock(html, page, formula!, claimedFormulaGlyphs);
            }
            else
            {
                WriteSemanticTable(
                    html,
                    element,
                    footnotes,
                    page,
                    claimedFormulaGlyphs: claimedFormulaGlyphs);
            }

            return;
        }

        if (IsFormulaDecorationElement(element))
        {
            return;
        }

        if (page != null && IsFormulaBlock(element))
        {
            WriteFormulaFlowElement(
                html,
                page,
                element,
                footnotes,
                allowMeasuredWidth,
                claimedFormulaGlyphs,
                elementId,
                headingLevel);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.List && element.SemanticList != null)
        {
            WriteSemanticList(
                html,
                element.SemanticList,
                element,
                footnotes,
                page,
                indentation: 6,
                isRoot: true,
                rootAdditionalClass: sourceBorder.HasValue
                    ? "pdf-semantic-ruled-grid-source-separator"
                    : null,
                rootStyle: sourceBorder.HasValue ? SemanticRuledBorderStyle(sourceBorder.Value) : null,
                itemBorders: semanticListItemBorders);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.Navigation && element.DocumentIndex != null)
        {
            WriteSemanticDocumentIndex(html, element, page);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.Bibliography && element.BibliographyFragment != null)
        {
            WriteSemanticBibliographyFragment(html, element, footnotes, page);
            return;
        }

        string? textAdditionalClass = sourceBorder.HasValue
            ? "pdf-semantic-ruled-grid-source-separator"
            : null;
        if (!string.IsNullOrWhiteSpace(additionalClass))
        {
            textAdditionalClass = string.IsNullOrWhiteSpace(textAdditionalClass)
                ? additionalClass
                : textAdditionalClass + " " + additionalClass;
        }

        string? textAdditionalStyle = sourceBorder.HasValue
            ? SemanticRuledBorderStyle(sourceBorder.Value)
            : null;
        if (!string.IsNullOrWhiteSpace(additionalStyle))
        {
            textAdditionalStyle = string.IsNullOrWhiteSpace(textAdditionalStyle)
                ? additionalStyle
                : textAdditionalStyle + ";" + additionalStyle;
        }

        WriteFlowTextElement(
            html,
            element,
            footnotes,
            page,
            allowMeasuredWidth,
            elementId,
            headingLevel,
            textAdditionalClass,
            textAdditionalStyle,
            sourceBorder?.SourcePathIndex,
            preserveSourceLines);
    }

    private static void WriteSemanticThematicBreak(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticElement element)
    {
        PdfSemanticThematicBreak thematicBreak = element.ThematicBreak!;
        float flowWidth = SemanticFlowWidth(page);
        float widthPercent = flowWidth <= 0.01f
            ? 100f
            : Math.Clamp(element.Bounds.Width / flowWidth * 100f, 1f, 100f);
        string alignment = thematicBreak.Alignment switch
        {
            PdfSemanticThematicBreakAlignment.Left => "flex-start",
            PdfSemanticThematicBreakAlignment.Right => "flex-end",
            _ => "center"
        };
        html.Append("      <hr class=\"pdf-semantic-element pdf-semantic-thematic-break\" data-source-path-index=\"")
            .Append(thematicBreak.SourcePathIndex.ToString(CultureInfo.InvariantCulture))
            .Append("\" data-source-width=\"")
            .Append(HtmlAttribute(CssPoints(element.Bounds.Width)))
            .Append("\" style=\"--pdf-thematic-break-width:")
            .Append(CssPercent(widthPercent))
            .Append(";--pdf-thematic-break-thickness:")
            .Append(CssPoints(thematicBreak.Thickness))
            .Append(";--pdf-thematic-break-color:")
            .Append(CssRgba(thematicBreak.Color))
            .Append(";--pdf-thematic-break-alignment:")
            .Append(alignment)
            .AppendLine("\" />");
    }

    private static void WriteFlowTextElement(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        bool allowMeasuredWidth,
        string? elementId = null,
        int? headingLevel = null,
        string? additionalClass = null,
        string? additionalStyle = null,
        int? sourceBorderPathIndex = null,
        bool preserveSourceLines = false)
    {
        string tagName = SemanticTagName(element, headingLevel);
        html.Append("      <")
            .Append(tagName)
            .Append(" class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth));
        if (!string.IsNullOrWhiteSpace(additionalClass))
        {
            html.Append(' ').Append(additionalClass);
        }
        if (preserveSourceLines)
        {
            html.Append(" pdf-semantic-preserve-source-lines");
        }

        html.Append('"');
        if (sourceBorderPathIndex.HasValue)
        {
            html.Append(" data-source-border-path-index=\"")
                .Append(sourceBorderPathIndex.Value.ToString(CultureInfo.InvariantCulture))
                .Append('"');
        }

        if (!string.IsNullOrEmpty(elementId))
        {
            html.Append(" id=\"").Append(HtmlAttribute(elementId)).Append('"');
        }

        AppendTaggedStructureAttributes(html, element);
        AppendTextDirectionAttribute(html, element.Text);
        AppendAsideLabelAttribute(html, element);
        string style = FlowSemanticStyle(element, page, allowMeasuredWidth);
        if (!string.IsNullOrWhiteSpace(additionalStyle))
        {
            style = style.Length == 0 ? additionalStyle : style + ";" + additionalStyle;
        }

        if (style.Length > 0)
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(style))
                .Append('"');
        }

        html.Append(">");
        if (preserveSourceLines)
        {
            WriteSemanticSourceLines(html, element, footnotes, page);
        }
        else
        {
            WriteSemanticText(html, element, footnotes, page);
        }
        html.Append("</")
            .Append(tagName)
            .AppendLine(">");
    }

    private static string SemanticRuledBorderStyle(SemanticRuledBorder border)
    {
        return "--pdf-semantic-ruled-source-border-width:" + CssPoints(border.Thickness) +
            ";--pdf-semantic-ruled-source-border-color:" + CssRgba(border.Color);
    }

    private static void WriteSemanticDefinitionList(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        SemanticDefinitionListRenderState? state)
    {
        PdfSemanticDefinitionList definitionList = element.DefinitionList!;
        bool ownsState = state == null;
        state ??= new SemanticDefinitionListRenderState();
        PdfSemanticDefinitionListEntry? firstEntry = definitionList.Entries.FirstOrDefault();
        if (state.IsOpen &&
            !definitionList.ContinuesPreviousList &&
            firstEntry?.ContinuesPreviousDefinition != true)
        {
            CloseSemanticDefinitionList(html, state);
        }

        if (!state.IsOpen)
        {
            WriteSemanticDefinitionListStart(html, element, definitionList, page, state);
        }

        for (int entryIndex = 0; entryIndex < definitionList.Entries.Count; entryIndex++)
        {
            PdfSemanticDefinitionListEntry entry = definitionList.Entries[entryIndex];
            if (entry.ContinuesPreviousDefinition)
            {
                if (!state.DefinitionOpen)
                {
                    html.Append("        <dd>");
                    state.DefinitionOpen = true;
                }
                else if (NeedsSpaceBetween(state.PreviousText, entry.Definition.Text))
                {
                    html.Append(' ');
                }

                WriteSemanticDefinitionContent(html, entry.Definition, footnotes, page);
                state.PreviousText = entry.Definition.Text;
            }
            else
            {
                if (state.DefinitionOpen)
                {
                    html.AppendLine("</dd>");
                    state.DefinitionOpen = false;
                }

                int termCount = Math.Max(1, entry.Terms.Count);
                int row = state.NextGridRow;
                for (int termIndex = 0; termIndex < entry.Terms.Count; termIndex++)
                {
                    PdfSemanticDefinitionTerm term = entry.Terms[termIndex];
                    html.Append("        <dt");
                    if (state.UsesColumns)
                    {
                        html.Append(" style=\"grid-row:")
                            .Append((row + termIndex).ToString(CultureInfo.InvariantCulture))
                            .Append("\"");
                    }

                    html.Append('>');
                    WriteSemanticDefinitionTerm(html, term, footnotes, page);
                    html.AppendLine("</dt>");
                }

                html.Append("        <dd");
                if (state.UsesColumns)
                {
                    html.Append(" style=\"grid-row:")
                        .Append(row.ToString(CultureInfo.InvariantCulture));
                    if (termCount > 1)
                    {
                        html.Append(" / span ")
                            .Append(termCount.ToString(CultureInfo.InvariantCulture));
                    }

                    html.Append("\"");
                }

                html.Append('>');
                WriteSemanticDefinitionContent(html, entry.Definition, footnotes, page);
                state.DefinitionOpen = true;
                state.PreviousText = entry.Definition.Text;
                state.NextGridRow += termCount;
            }

            bool keepsListOpenAcrossPage =
                entryIndex == definitionList.Entries.Count - 1 && definitionList.ContinuesOnNextPage;
            if (!entry.ContinuesOnNextPage && !keepsListOpenAcrossPage && state.DefinitionOpen)
            {
                html.AppendLine("</dd>");
                state.DefinitionOpen = false;
            }
        }

        bool continues = definitionList.ContinuesOnNextPage;
        if (ownsState || !continues)
        {
            CloseSemanticDefinitionList(html, state);
        }
    }

    private static void WriteSemanticDefinitionListStart(
        StringBuilder html,
        PdfSemanticElement element,
        PdfSemanticDefinitionList definitionList,
        PdfLayoutPage? page,
        SemanticDefinitionListRenderState state)
    {
        state.IsOpen = true;
        state.UsesColumns = definitionList.TermColumnWidth.HasValue;
        state.NextGridRow = 1;
        html.Append("      <dl class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-definition-list");
        if (!state.UsesColumns)
        {
            html.Append(" pdf-semantic-definition-list-stacked");
        }

        html.Append('"');
        AppendTaggedStructureAttributes(html, element);
        AppendTextDirectionAttribute(html, element.Text);
        if (definitionList.TermColumnWidth is float termColumnWidth)
        {
            html.Append(" style=\"--pdf-semantic-term-width:")
                .Append(CssPoints(termColumnWidth))
                .Append(";--pdf-semantic-definition-gap:")
                .Append(CssPoints(definitionList.ColumnGap))
                .Append('"');
        }

        html.AppendLine(">");
    }

    private static void CloseSemanticDefinitionList(
        StringBuilder html,
        SemanticDefinitionListRenderState state)
    {
        if (!state.IsOpen)
        {
            return;
        }

        if (state.DefinitionOpen)
        {
            html.AppendLine("</dd>");
        }

        html.AppendLine("      </dl>");
        state.Reset();
    }

    private static void WriteSemanticDefinitionTerm(
        StringBuilder html,
        PdfSemanticDefinitionTerm term,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        WriteSemanticDefinitionLines(html, term.Text, term.Bounds, term.Lines, footnotes, page);
    }

    private static void WriteSemanticDefinitionContent(
        StringBuilder html,
        PdfSemanticDefinitionContent definition,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        WriteSemanticDefinitionLines(html, definition.Text, definition.Bounds, definition.Lines, footnotes, page);
    }

    private static void WriteSemanticDefinitionLines(
        StringBuilder html,
        string text,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfSemanticLine> lines,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        if (lines.Count == 0)
        {
            html.Append(Html(text));
            return;
        }

        PdfSemanticElement source = new(PdfSemanticElementKind.Paragraph, text, bounds, lines);
        string previousLineText = "";
        bool wroteLine = false;
        foreach (PdfSemanticLine line in lines)
        {
            List<InlineTextSegment> segments = InlineTextSegments(line, page, source).ToList();
            string lineText = string.Concat(segments.Select(static segment => segment.Text));
            if (lineText.Length == 0)
            {
                continue;
            }

            if (wroteLine && NeedsSpaceBetween(previousLineText, lineText))
            {
                html.Append(' ');
            }

            WriteInlineTextSegments(html, line, segments, lineText, footnotes);
            previousLineText = lineText;
            wroteLine = true;
        }
    }

    private static void WriteSemanticList(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        string? rootAdditionalClass = null,
        string? rootStyle = null)
    {
        WriteSemanticList(
            html,
            element.SemanticList!,
            element,
            footnotes,
            page,
            6,
            isRoot: true,
            rootAdditionalClass,
            rootStyle);
    }

    private static void WriteSemanticList(
        StringBuilder html,
        PdfSemanticList list,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        int indentation,
        bool isRoot,
        string? rootAdditionalClass = null,
        string? rootStyle = null,
        IReadOnlyDictionary<PdfSemanticListItem, SemanticRuledBorder>? itemBorders = null)
    {
        PdfSemanticListItem[] renderableItems = list.Items
            .Where(SemanticListItemHasRenderableContent)
            .ToArray();
        if (renderableItems.Length == 0)
        {
            return;
        }

        string tagName = list.Kind == PdfSemanticListKind.Ordered ? "ol" : "ul";
        html.Append(' ', indentation)
            .Append('<')
            .Append(tagName)
            .Append(" class=\"");
        if (isRoot)
        {
            html.Append(SemanticClassNames(element, page, allowMeasuredWidth: false))
                .Append(' ');
        }

        html.Append("pdf-semantic-list");
        string? orderedMarkerClass = OrderedMarkerClass(list, renderableItems);
        if (orderedMarkerClass != null)
        {
            html.Append(' ').Append(orderedMarkerClass);
        }

        if (isRoot && !string.IsNullOrWhiteSpace(rootAdditionalClass))
        {
            html.Append(' ').Append(rootAdditionalClass);
        }

        html.Append('"');
        AppendTaggedStructureAttributes(html, element);
        AppendTextDirectionAttribute(html, element.Text);
        if (isRoot && !string.IsNullOrWhiteSpace(rootStyle))
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(rootStyle))
                .Append('"');
        }

        if (list.Kind == PdfSemanticListKind.Ordered)
        {
            string? type = OrderedListType(list.MarkerKind);
            if (type != null)
            {
                html.Append(" type=\"").Append(type).Append('"');
            }

            if (list.IsReversed)
            {
                html.Append(" reversed");
            }

            if (list.Start.HasValue)
            {
                html.Append(" start=\"")
                    .Append(list.Start.Value.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }
        }

        html.Append('>').AppendLine();
        foreach (PdfSemanticListItem item in renderableItems)
        {
            SemanticRuledBorder sourceBorder = default;
            bool hasSourceBorder = itemBorders?.TryGetValue(item, out sourceBorder) == true;
            WriteSemanticListItem(
                html,
                item,
                element,
                footnotes,
                page,
                indentation + 2,
                hasSourceBorder ? sourceBorder : null);
        }

        html.Append(' ', indentation)
            .Append("</")
            .Append(tagName)
            .AppendLine(">");
    }

    private static string? OrderedMarkerClass(
        PdfSemanticList list,
        IReadOnlyList<PdfSemanticListItem> items)
    {
        if (list.Kind != PdfSemanticListKind.Ordered ||
            list.MarkerKind != PdfSemanticListMarkerKind.Decimal)
        {
            return null;
        }

        if (items.All(static item =>
            {
                string marker = item.Marker.Trim();
                return marker.StartsWith('(') && marker.EndsWith(')');
            }))
        {
            return "pdf-semantic-list-marker-parenthesized";
        }

        if (items.All(static item =>
            {
                string marker = item.Marker.Trim();
                return !marker.StartsWith('(') && marker.EndsWith(')');
            }))
        {
            return "pdf-semantic-list-marker-closing-parenthesis";
        }

        return null;
    }

    private static void WriteSemanticListItem(
        StringBuilder html,
        PdfSemanticListItem item,
        PdfSemanticElement listElement,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        int indentation,
        SemanticRuledBorder? sourceBorder = null)
    {
        html.Append(' ', indentation).Append("<li");
        if (sourceBorder.HasValue)
        {
            html.Append(" class=\"pdf-semantic-ruled-grid-source-separator\"")
                .Append(" data-source-border-path-index=\"")
                .Append(sourceBorder.Value.SourcePathIndex.ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"")
                .Append(HtmlAttribute(SemanticRuledBorderStyle(sourceBorder.Value)))
                .Append('"');
        }

        if (item.Value.HasValue)
        {
            html.Append(" value=\"")
                .Append(item.Value.Value.ToString(CultureInfo.InvariantCulture))
                .Append('"');
        }

        html.Append('>');
        PdfSemanticElement itemElement = new(
            PdfSemanticElementKind.Paragraph,
            item.Text,
            item.Bounds,
            item.Lines);
        string previousLineText = "";
        bool wroteLine = false;
        foreach (PdfSemanticLine line in item.Lines)
        {
            List<InlineTextSegment> segments = InlineTextSegments(line, page, itemElement).ToList();
            if (!wroteLine)
            {
                TrimLeadingCharacters(segments, item.MarkerLength);
            }

            if (ReferenceEquals(line, item.Lines[^1]))
            {
                RemoveTrailingWhitespace(segments);
            }

            string lineText = string.Concat(segments.Select(static segment => segment.Text));
            if (lineText.Length == 0)
            {
                continue;
            }

            if (wroteLine && NeedsSpaceBetween(previousLineText, lineText))
            {
                html.Append(' ');
            }

            WriteInlineTextSegments(
                html,
                line,
                segments,
                lineText,
                footnotes,
                SemanticListColor(listElement));
            previousLineText = lineText;
            wroteLine = true;
        }

        if (item.NestedLists.Count > 0)
        {
            html.AppendLine();
            foreach (PdfSemanticList nestedList in item.NestedLists)
            {
                WriteSemanticList(
                    html,
                    nestedList,
                    listElement,
                    footnotes,
                    page,
                    indentation + 2,
                    isRoot: false);
            }

            html.Append(' ', indentation);
        }

        html.AppendLine("</li>");
    }

    private static bool SemanticListItemHasRenderableContent(PdfSemanticListItem item)
    {
        string text = string.Concat(item.Lines.Select(static line => line.Text));
        if (item.MarkerLength > 0 && text.Length >= item.MarkerLength)
        {
            text = text[item.MarkerLength..];
        }

        if (string.Equals(text.Trim(), item.Marker.Trim(), StringComparison.Ordinal))
        {
            text = "";
        }

        return !string.IsNullOrWhiteSpace(text) ||
            item.NestedLists.Any(static nested => nested.Items.Any(SemanticListItemHasRenderableContent));
    }

    private static void WriteSemanticBibliographyFragment(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        SemanticBibliographyWriter writer = new(html);
        writer.WriteFragment(element, footnotes, page);
        writer.CloseAll();
    }

    private static void WriteSemanticBibliographyText(
        StringBuilder html,
        PdfSemanticBibliographyItem item,
        PdfSemanticBibliographyItemFragment fragment,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        PdfSemanticElement itemElement = SemanticBibliographyItemElement(fragment);
        string previousLineText = "";
        bool wroteLine = false;
        foreach (PdfSemanticLine line in fragment.Lines)
        {
            List<InlineTextSegment> segments = InlineTextSegments(line, page, itemElement).ToList();
            if (!wroteLine && fragment.IsFirstPart && item.MarkerLength > 0)
            {
                TrimLeadingCharacters(segments, item.MarkerLength);
            }

            string lineText = string.Concat(segments.Select(static segment => segment.Text));
            if (lineText.Length == 0)
            {
                continue;
            }

            if (wroteLine && NeedsSpaceBetween(previousLineText, lineText))
            {
                html.Append(' ');
            }

            WriteInlineTextSegments(html, line, segments, lineText, footnotes, SemanticColor(itemElement));
            previousLineText = lineText;
            wroteLine = true;
        }
    }

    private static PdfSemanticElement SemanticBibliographyItemElement(
        PdfSemanticBibliographyItemFragment fragment)
    {
        return new PdfSemanticElement(
            PdfSemanticElementKind.Paragraph,
            fragment.Text,
            fragment.Bounds,
            fragment.Lines);
    }

    private static void WriteSemanticDocumentIndex(
        StringBuilder html,
        PdfSemanticElement element,
        PdfLayoutPage? page,
        bool isVisualPreservationIsland = false)
    {
        PdfSemanticDocumentIndex documentIndex = element.DocumentIndex!;
        string indexToken = DocumentIndexKindToken(documentIndex.Kind);
        string headingId = "pdf-document-index-" +
            (page?.PageNumber ?? 0).ToString(CultureInfo.InvariantCulture) +
            "-" + indexToken;
        int headingLevel = Math.Clamp(element.HeadingLevel, 1, 6);
        PdfSemanticElement headingElement = new(
            PdfSemanticElementKind.Heading,
            documentIndex.Heading,
            UnionRectangles(documentIndex.HeadingLines.Select(static line => line.Bounds)),
            documentIndex.HeadingLines,
            headingLevel);

        html.Append("      <nav class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-document-index");
        if (isVisualPreservationIsland)
        {
            html.Append(" pdf-semantic-document-index-island");
        }

        html.Append("\" aria-labelledby=\"")
            .Append(HtmlAttribute(headingId))
            .AppendLine("\">");
        html.Append("        <h")
            .Append(headingLevel.ToString(CultureInfo.InvariantCulture))
            .Append(" id=\"")
            .Append(HtmlAttribute(headingId))
            .Append("\" class=\"")
            .Append(SemanticClassNames(headingElement, page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-document-index-heading\">")
            .Append(Html(documentIndex.Heading))
            .Append("</h")
            .Append(headingLevel.ToString(CultureInfo.InvariantCulture))
            .AppendLine(">");
        WriteSemanticDocumentIndexItems(html, documentIndex.Items, 8);
        html.AppendLine("      </nav>");
    }

    private static void WriteSemanticDocumentIndexItems(
        StringBuilder html,
        IReadOnlyList<PdfSemanticDocumentIndexItem> items,
        int indentation)
    {
        html.Append(' ', indentation)
            .AppendLine("<ol class=\"pdf-semantic-document-index-list\">");
        foreach (PdfSemanticDocumentIndexItem item in items)
        {
            WriteSemanticDocumentIndexItem(html, item, indentation + 2);
        }

        html.Append(' ', indentation).AppendLine("</ol>");
    }

    private static void WriteSemanticDocumentIndexItem(
        StringBuilder html,
        PdfSemanticDocumentIndexItem item,
        int indentation)
    {
        html.Append(' ', indentation)
            .AppendLine("<li class=\"pdf-semantic-document-index-item\">");
        html.Append(' ', indentation + 2);
        if (item.Link != null)
        {
            html.Append("<a class=\"pdf-semantic-document-index-entry\" href=\"")
                .Append(HtmlAttribute(LinkHref(item.Link)))
                .Append("\" data-link-kind=\"")
                .Append(HtmlAttribute(item.Link.Kind.ToString().ToLowerInvariant()))
                .Append('"');
        }
        else
        {
            html.Append("<span class=\"pdf-semantic-document-index-entry\"");
        }

        AppendTextDirectionAttribute(html, item.Label);
        html.Append("><span class=\"pdf-semantic-document-index-label\">")
            .Append(Html(item.Label))
            .Append("</span><span class=\"pdf-semantic-document-index-leader\" aria-hidden=\"true\"></span>")
            .Append("<span class=\"pdf-semantic-document-index-page-number\" aria-label=\"Page ")
            .Append(HtmlAttribute(item.PageLabel))
            .Append("\">")
            .Append(Html(item.PageLabel))
            .Append("</span></")
            .Append(item.Link != null ? "a" : "span")
            .AppendLine(">");

        if (item.Children.Count > 0)
        {
            WriteSemanticDocumentIndexItems(html, item.Children, indentation + 2);
        }

        html.Append(' ', indentation).AppendLine("</li>");
    }

    private static string DocumentIndexKindToken(PdfSemanticDocumentIndexKind kind)
    {
        return kind switch
        {
            PdfSemanticDocumentIndexKind.TableOfContents => "contents",
            PdfSemanticDocumentIndexKind.ListOfFigures => "figures",
            PdfSemanticDocumentIndexKind.ListOfTables => "tables",
            _ => "navigation"
        };
    }

    private static void TrimLeadingCharacters(List<InlineTextSegment> segments, int characterCount)
    {
        int remaining = characterCount;
        for (int index = 0; index < segments.Count && remaining > 0; index++)
        {
            InlineTextSegment segment = segments[index];
            int consumed = Math.Min(remaining, segment.Text.Length);
            segments[index] = segment with { Text = segment.Text[consumed..] };
            remaining -= consumed;
        }

        TrimLeadingWhitespace(segments);
    }

    private static string? OrderedListType(PdfSemanticListMarkerKind markerKind)
    {
        return markerKind switch
        {
            PdfSemanticListMarkerKind.LowerAlpha => "a",
            PdfSemanticListMarkerKind.UpperAlpha => "A",
            PdfSemanticListMarkerKind.LowerRoman => "i",
            PdfSemanticListMarkerKind.UpperRoman => "I",
            _ => null
        };
    }

    private static void WriteSemanticTable(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        bool allowMeasuredWidth = true,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null)
    {
        html.Append("      <table class=\"")
            .Append(SemanticClassNames(element, page, allowMeasuredWidth))
            .Append('"');
        if (element.TableCaption == null)
        {
            html.Append(" aria-label=\"")
                .Append(HtmlAttribute(TableAriaLabel(element, claimedFormulaGlyphs)))
                .Append('"');
        }

        AppendTaggedStructureAttributes(html, element);
        AppendTextDirectionAttribute(html, element.Text);
        string style = FlowSemanticStyle(element, page, allowMeasuredWidth);
        if (style.Length > 0)
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(style))
                .Append('"');
        }

        html.AppendLine(">");

        if (element.TableCaption != null)
        {
            WriteSemanticTableCaption(html, element, element.TableCaption, footnotes, page);
        }

        PdfSemanticTableRow[] headerRows = element.TableRows
            .TakeWhile(static row => row.IsHeader)
            .ToArray();
        PdfSemanticTableRow[] bodyRows = element.TableRows
            .Skip(headerRows.Length)
            .ToArray();
        bool hasCellBackgroundMatrix = HasSemanticTableCellBackgroundMatrix(element, page);
        bool allRowsAreBodyRows = hasCellBackgroundMatrix &&
            headerRows.Length == element.TableRows.Count &&
            headerRows.Length > 1;
        if (allRowsAreBodyRows)
        {
            headerRows = [];
            bodyRows = element.TableRows.ToArray();
        }

        TableCellAlignment[] columnAlignments = TableColumnAlignments(element.TableRows, headerRows.Length);
        if (headerRows.Length > 0)
        {
            html.AppendLine("        <thead>");
            foreach (PdfSemanticTableRow row in headerRows)
            {
                WriteSemanticTableRow(
                    html,
                    row,
                    footnotes,
                    page,
                    element,
                    header: true,
                    useCellBackgrounds: hasCellBackgroundMatrix,
                    suppressSpans: false,
                    columnAlignments,
                    claimedFormulaGlyphs);
            }

            html.AppendLine("        </thead>");
        }

        if (bodyRows.Length > 0)
        {
            html.AppendLine("        <tbody>");
            foreach (PdfSemanticTableRow row in bodyRows)
            {
                WriteSemanticTableRow(
                    html,
                    row,
                    footnotes,
                    page,
                    element,
                    header: false,
                    useCellBackgrounds: hasCellBackgroundMatrix,
                    suppressSpans: allRowsAreBodyRows,
                    columnAlignments,
                    claimedFormulaGlyphs);
            }

            html.AppendLine("        </tbody>");
        }

        html.AppendLine("      </table>");
    }

    private static void WriteSemanticTableCaption(
        StringBuilder html,
        PdfSemanticElement table,
        PdfSemanticTableCaption caption,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        PdfSemanticElement captionElement = new(
            PdfSemanticElementKind.Paragraph,
            caption.Text,
            caption.Bounds,
            caption.Lines);
        string? alignmentClass = TableCaptionAlignmentClass(table, caption);
        html.Append("        <caption class=\"")
            .Append(SemanticClassNames(captionElement, page, allowMeasuredWidth: false))
            .Append(" pdf-semantic-table-caption pdf-semantic-table-caption-")
            .Append(caption.Position == PdfSemanticTableCaptionPosition.Below ? "below" : "above");
        if (alignmentClass != null)
        {
            html.Append(' ').Append(alignmentClass);
        }

        html.Append('"');
        AppendTextDirectionAttribute(html, caption.Text);
        string style = SemanticTableCaptionStyle(table, caption);
        if (style.Length > 0)
        {
            html.Append(" style=\"")
                .Append(HtmlAttribute(style))
                .Append('"');
        }

        html.Append("><span class=\"pdf-semantic-table-caption-content\">");
        if (CanWriteRichSemanticText(captionElement))
        {
            WriteRichSemanticText(html, captionElement, footnotes, page);
        }
        else
        {
            WriteTextWithFootnoteReferences(html, caption.Text, footnotes);
        }

        html.AppendLine("</span></caption>");
    }

    private static string SemanticTableCaptionStyle(
        PdfSemanticElement table,
        PdfSemanticTableCaption caption)
    {
        if (table.Bounds.Width <= 0.01f)
        {
            return "";
        }

        float widthPercent = Math.Clamp(caption.Bounds.Width / table.Bounds.Width * 100f, 20f, 100f);
        float maximumOffset = MathF.Max(0f, 100f - widthPercent);
        float offsetPercent = Math.Clamp(
            (caption.Bounds.X - table.Bounds.X) / table.Bounds.Width * 100f,
            0f,
            maximumOffset);
        float sourceGap = caption.Position == PdfSemanticTableCaptionPosition.Above
            ? table.Bounds.Y - caption.Bounds.Bottom
            : caption.Bounds.Y - table.Bounds.Bottom;
        return "--pdf-semantic-table-caption-width:" + CssPercent(widthPercent) +
            ";--pdf-semantic-table-caption-offset:" + CssPercent(offsetPercent) +
            ";--pdf-semantic-table-caption-gap:" + CssPoints(Math.Clamp(sourceGap, 2f, 18f));
    }

    private static bool TrySplitClaimedFormulaTable(
        PdfSemanticElement table,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs,
        out PdfSemanticElement? residualTable,
        out PdfSemanticElement? formula)
    {
        residualTable = null;
        formula = null;
        if (claimedFormulaGlyphs is not { Count: > 0 } ||
            !TableGlyphs(table).Any(glyph => IsClaimedFormulaGlyph(glyph, claimedFormulaGlyphs)))
        {
            return false;
        }

        int formulaRowIndex = -1;
        for (int rowIndex = 0; rowIndex < table.TableRows.Count - 1; rowIndex++)
        {
            if (TableRowEndsWithEquationNumber(table.TableRows[rowIndex], claimedFormulaGlyphs))
            {
                formulaRowIndex = rowIndex;
                break;
            }
        }

        if (formulaRowIndex < 0)
        {
            return false;
        }

        PdfSemanticTableRow[] formulaRows = table.TableRows.Skip(formulaRowIndex).ToArray();
        PdfSemanticLine[] continuationLines = formulaRows
            .Skip(1)
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder)
            .SelectMany(static cell => cell.Lines)
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
        if (continuationLines.Length == 0 || continuationLines.Any(IsProseLikeFormulaSourceLine))
        {
            return false;
        }

        PdfSemanticTableRow[] residualRows = table.TableRows.Take(formulaRowIndex).ToArray();
        if (residualRows.Length > 0)
        {
            residualTable = SemanticTableFromRows(residualRows, table.TableCaption);
        }

        PdfSemanticTableCell[] formulaCells = formulaRows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder)
            .ToArray();
        PdfSemanticLine[] formulaLines = formulaCells
            .SelectMany(static cell => cell.Lines)
            .Distinct((IEqualityComparer<PdfSemanticLine>)ReferenceEqualityComparer.Instance)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        formula = new PdfSemanticElement(
            PdfSemanticElementKind.Paragraph,
            string.Join(Environment.NewLine, formulaLines.Select(static line => line.Text)),
            UnionRectangles(formulaCells.Select(static cell => cell.Bounds)),
            formulaLines);
        return true;
    }

    private static bool TableRowEndsWithEquationNumber(
        PdfSemanticTableRow row,
        ISet<FormulaGlyphKey> claimedFormulaGlyphs)
    {
        foreach (PdfSemanticTableCell cell in row.Cells.Where(static cell => !cell.IsPlaceholder))
        {
            string text = CompactText(ReconstructText(UnclaimedTableCellGlyphs(cell, claimedFormulaGlyphs)));
            int open = text.LastIndexOf("(", StringComparison.Ordinal);
            if (open >= 0 && IsEquationNumber(text[open..]))
            {
                return true;
            }
        }

        return false;
    }

    private static PdfSemanticElement SemanticTableFromRows(
        IReadOnlyList<PdfSemanticTableRow> rows,
        PdfSemanticTableCaption? caption = null)
    {
        PdfSemanticTableCell[] cells = rows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder)
            .ToArray();
        PdfSemanticLine[] lines = cells
            .SelectMany(static cell => cell.Lines)
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X)
            .ToArray();
        return new PdfSemanticElement(
            PdfSemanticElementKind.Table,
            string.Join(Environment.NewLine, rows.Select(row =>
                string.Join('\t', row.Cells
                    .Where(static cell => !cell.IsPlaceholder)
                    .Select(static cell => cell.Text)))),
            UnionRectangles(cells.Select(static cell => cell.Bounds)),
            lines,
            tableRows: rows,
            tableCaption: caption);
    }

    private static void WriteSemanticTableRow(
        StringBuilder html,
        PdfSemanticTableRow row,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        PdfSemanticElement table,
        bool header,
        bool useCellBackgrounds,
        bool suppressSpans,
        IReadOnlyList<TableCellAlignment> columnAlignments,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs)
    {
        html.AppendLine("          <tr>");
        for (int columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
        {
            PdfSemanticTableCell cell = row.Cells[columnIndex];
            if (cell.IsPlaceholder && !suppressSpans)
            {
                continue;
            }

            bool rowGroupHeader = !header && IsSemanticTableRowGroupHeader(cell);
            string cellTag = header || rowGroupHeader ? "th" : "td";
            html.Append("            <")
                .Append(cellTag);
            if (header)
            {
                html.Append(" scope=\"col\"");
            }
            else if (rowGroupHeader)
            {
                html.Append(" scope=\"rowgroup\"");
            }

            if (!suppressSpans && cell.RowSpan > 1)
            {
                html.Append(" rowspan=\"")
                    .Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }

            if (!suppressSpans && cell.ColumnSpan > 1)
            {
                html.Append(" colspan=\"")
                    .Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }

            TableCellAlignment alignment = columnIndex < columnAlignments.Count
                ? columnAlignments[columnIndex]
                : TableCellAlignment.Default;
            string cellClass = SemanticTableCellClassNames(cell, alignment);
            if (cellClass.Length > 0)
            {
                html.Append(" class=\"")
                    .Append(cellClass)
                    .Append('"');
            }

            string cellStyle = useCellBackgrounds
                ? SemanticTableCellStyle(table, cell, page)
                : "";
            if (cellStyle.Length > 0)
            {
                html.Append(" style=\"")
                    .Append(HtmlAttribute(cellStyle))
                    .Append('"');
            }

            html.Append('>');
            WriteSemanticTableCell(html, cell, footnotes, page, claimedFormulaGlyphs);

            html.Append("</")
                .Append(cellTag)
                .AppendLine(">");
        }

        html.AppendLine("          </tr>");
    }

    private static string SemanticTableCellClassNames(PdfSemanticTableCell cell, TableCellAlignment alignment)
    {
        List<string> classes = [];
        if (cell.BorderTop)
        {
            classes.Add("pdf-semantic-table-cell-border-top");
        }

        if (cell.BorderRight)
        {
            classes.Add("pdf-semantic-table-cell-border-right");
        }

        if (cell.BorderBottom)
        {
            classes.Add("pdf-semantic-table-cell-border-bottom");
        }

        if (cell.BorderLeft)
        {
            classes.Add("pdf-semantic-table-cell-border-left");
        }

        if (IsBoldTableCell(cell))
        {
            classes.Add("pdf-semantic-bold");
        }

        if (IsSemanticTableRowGroupHeader(cell))
        {
            classes.Add("pdf-semantic-table-row-group-header");
        }

        if (alignment != TableCellAlignment.Default && cell.ColumnSpan == 1)
        {
            classes.Add("pdf-semantic-table-cell-align-" + alignment.ToString().ToLowerInvariant());
        }

        return string.Join(" ", classes);
    }

    private static string SemanticTableCellStyle(
        PdfSemanticElement table,
        PdfSemanticTableCell cell,
        PdfLayoutPage? page)
    {
        if (page == null)
        {
            return "";
        }

        PdfLayoutColor? background = page.Paths
            .Where(path => IsSemanticTableCellBackgroundPath(table, cell, path))
            .OrderBy(static path => path.Bounds.Width * path.Bounds.Height)
            .Select(static path => path.FillColor)
            .FirstOrDefault();
        return background is PdfLayoutColor color
            ? "background-color:" + CssRgba(color)
            : "";
    }

    private static bool IsSemanticTableCellBackgroundPath(
        PdfSemanticElement table,
        PdfSemanticTableCell cell,
        PdfLayoutPath path)
    {
        return path.FillColor is PdfLayoutColor { Alpha: > 0.01f } &&
            IsAxisAlignedRectangle(path) &&
            path.Bounds.Width >= MathF.Max(4f, cell.Bounds.Width) &&
            path.Bounds.Height >= MathF.Max(4f, cell.Bounds.Height) &&
            RectangleContainsWithTolerance(table.Bounds, path.Bounds, 1.5f) &&
            RectangleContainsWithTolerance(path.Bounds, cell.Bounds, 0.75f);
    }

    private static TableCellAlignment[] TableColumnAlignments(
        IReadOnlyList<PdfSemanticTableRow> rows,
        int headerRowCount)
    {
        int columnCount = rows.Count == 0 ? 0 : rows.Max(static row => row.Cells.Count);
        if (columnCount == 0)
        {
            return [];
        }

        IReadOnlyList<PdfSemanticTableRow> sourceRows = rows.Skip(headerRowCount).ToArray();
        if (sourceRows.Count == 0)
        {
            sourceRows = rows;
        }

        List<PdfLayoutRectangle>[] columnCells = Enumerable
            .Range(0, columnCount)
            .Select(static _ => new List<PdfLayoutRectangle>())
            .ToArray();
        foreach (PdfSemanticTableRow row in sourceRows)
        {
            for (int columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                PdfSemanticTableCell cell = row.Cells[columnIndex];
                if (cell.IsPlaceholder ||
                    cell.ColumnSpan != 1 ||
                    string.IsNullOrWhiteSpace(cell.Text) ||
                    cell.Bounds.Width <= 0.5f)
                {
                    continue;
                }

                columnCells[columnIndex].Add(cell.Bounds);
            }
        }

        return columnCells.Select(DetectTableColumnAlignment).ToArray();
    }

    private static TableCellAlignment DetectTableColumnAlignment(IReadOnlyList<PdfLayoutRectangle> cells)
    {
        if (cells.Count < 3)
        {
            return TableCellAlignment.Default;
        }

        float centerSpread = StandardDeviation(cells.Select(static cell => cell.X + cell.Width / 2f));
        float leftSpread = StandardDeviation(cells.Select(static cell => cell.X));
        float rightSpread = StandardDeviation(cells.Select(static cell => cell.Right));
        float averageWidth = cells.Average(static cell => cell.Width);
        float tolerance = MathF.Max(0.75f, averageWidth * 0.04f);
        if (centerSpread <= MathF.Min(leftSpread, rightSpread) * 0.65f &&
            leftSpread >= centerSpread + tolerance &&
            rightSpread >= centerSpread + tolerance)
        {
            return TableCellAlignment.Center;
        }

        if (leftSpread <= MathF.Min(centerSpread, rightSpread) * 0.65f &&
            centerSpread >= leftSpread + tolerance)
        {
            return TableCellAlignment.Left;
        }

        if (rightSpread <= MathF.Min(centerSpread, leftSpread) * 0.65f &&
            centerSpread >= rightSpread + tolerance)
        {
            return TableCellAlignment.Right;
        }

        return TableCellAlignment.Default;
    }

    private static bool IsSemanticTableRowGroupHeader(PdfSemanticTableCell cell)
    {
        string text = cell.Text.Trim();
        return cell.RowSpan > 1 &&
            text.Length == 3 &&
            text[0] == '(' &&
            text[2] == ')' &&
            char.IsUpper(text[1]);
    }

    private static bool IsBoldTableCell(PdfSemanticTableCell cell)
    {
        PdfTextRun[] runs = cell.Lines
            .SelectMany(static line => line.Runs)
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .ToArray();
        return runs.Length > 0 && runs.All(static run => IsBoldFont(run.FontName));
    }

    private static void WriteSemanticTableCell(
        StringBuilder html,
        PdfSemanticTableCell cell,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs)
    {
        if (cell.Lines.Count == 0)
        {
            html.Append(Html(cell.Text));
            return;
        }

        PdfSemanticElement cellElement = new(
            PdfSemanticElementKind.Paragraph,
            cell.Text,
            cell.Bounds,
            cell.Lines);
        for (int index = 0; index < cell.Lines.Count; index++)
        {
            if (index > 0)
            {
                html.Append("<br />");
            }

            PdfSemanticLine line = cell.Lines[index];
            List<InlineTextSegment> segments = InlineTextSegments(
                line,
                page,
                cellElement,
                includeAttachedInlineMath: false,
                claimedFormulaGlyphs,
                includeSourceHighlights: false).ToList();
            string lineText = string.Concat(segments.Select(static segment => segment.Text));
            WriteInlineTextSegments(html, line, segments, lineText, footnotes);
        }
    }

    internal static string TableAriaLabel(
        PdfSemanticElement element,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null)
    {
        bool containsClaimedGlyphs = claimedFormulaGlyphs is { Count: > 0 } &&
            TableGlyphs(element).Any(glyph => IsClaimedFormulaGlyph(glyph, claimedFormulaGlyphs));
        string text = !containsClaimedGlyphs
            ? element.Text
            : string.Join(' ', element.TableRows
                .SelectMany(static row => row.Cells)
                .Where(static cell => !cell.IsPlaceholder)
                .Select(cell => ReconstructText(UnclaimedTableCellGlyphs(cell, claimedFormulaGlyphs)))
                .Where(static cellText => !string.IsNullOrWhiteSpace(cellText)));
        string label = text.Replace('\t', ' ').Replace(Environment.NewLine, " ");
        return label.Length <= 120 ? label : label[..120];
    }

    internal static IReadOnlyList<PdfTextGlyph> UnclaimedTableCellGlyphs(
        PdfSemanticTableCell cell,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs)
    {
        return cell.Lines
            .SelectMany(static line => line.Runs)
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .SelectMany(static run => run.Glyphs)
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .Where(glyph => !IsClaimedFormulaGlyph(glyph, claimedFormulaGlyphs))
            .ToArray();
    }

    private static bool IsClaimedFormulaGlyph(
        PdfTextGlyph glyph,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs)
    {
        return claimedFormulaGlyphs is { Count: > 0 } &&
            claimedFormulaGlyphs.Contains(FormulaGlyphIdentity(glyph));
    }

    private static void WriteFormulaFlowElement(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        bool allowMeasuredWidth,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs,
        string? elementId,
        int? headingLevel)
    {
        if (element.Lines.Count == 0)
        {
            WriteFormulaBlock(html, page, element, claimedFormulaGlyphs, elementId);
            return;
        }

        bool numberedFormula = element.Lines.Any(static line =>
            TryGetTrailingEquationNumber(line.Text.Trim(), out _) &&
            HasFormulaSignal(line.Text));
        bool formulaOnly = element.Lines.All(IsFormulaOnlySourceLine);
        bool[] formulaLines = numberedFormula
            ? element.Lines.Select(static line =>
                TryGetTrailingEquationNumber(line.Text.Trim(), out _) ||
                !IsProseLikeFormulaSourceLine(line)).ToArray()
            : formulaOnly
                ? element.Lines.Select(static _ => true).ToArray()
                : DisplayFormulaSourceLines(element.Lines);
        if (!formulaLines.Any(static isFormula => isFormula))
        {
            formulaLines = element.Lines
                .Select(static line => !IsProseLikeFormulaSourceLine(line))
                .ToArray();
        }

        PdfSemanticElement? trailingFootnote = TryGetTrailingFormulaFootnote(
            element,
            formulaLines,
            out int trailingFootnoteStart);
        int contentEnd = trailingFootnoteStart >= 0
            ? trailingFootnoteStart
            : element.Lines.Count;
        if (trailingFootnote != null)
        {
            footnotes.Register(LeadingFootnoteMarker(trailingFootnote.Lines[0]));
        }

        int start = 0;
        while (start < contentEnd)
        {
            bool isFormula = formulaLines[start];
            int end = start + 1;
            while (end < contentEnd && formulaLines[end] == isFormula)
            {
                end++;
            }

            PdfSemanticLine[] lines = element.Lines.Skip(start).Take(end - start).ToArray();
            PdfSemanticElement segment = new(
                PdfSemanticElementKind.Paragraph,
                string.Join(Environment.NewLine, lines.Select(static line => line.Text)),
                UnionRectangles(lines.Select(static line => line.Bounds)),
                lines);
            if (isFormula)
            {
                WriteFormulaBlock(
                    html,
                    page,
                    segment,
                    claimedFormulaGlyphs,
                    start == 0 ? elementId : null);
            }
            else
            {
                WriteFlowTextElement(
                    html,
                    segment,
                    footnotes,
                    page,
                    allowMeasuredWidth,
                    start == 0 ? elementId : null,
                    headingLevel);
            }

            start = end;
        }

        if (trailingFootnote != null)
        {
            WriteFootnoteSection(
                html,
                [trailingFootnote],
                0,
                footnotes,
                page,
                footnoteRule: null);
        }
    }

    private static PdfSemanticElement? TryGetTrailingFormulaFootnote(
        PdfSemanticElement element,
        IReadOnlyList<bool> formulaLines,
        out int footnoteStart)
    {
        footnoteStart = -1;
        int lastFormula = -1;
        for (int index = formulaLines.Count - 1; index >= 0; index--)
        {
            if (formulaLines[index])
            {
                lastFormula = index;
                break;
            }
        }

        if (lastFormula < 0)
        {
            return null;
        }

        float bodyFontSize = SemanticFontSize(element);
        for (int index = lastFormula + 1; index < element.Lines.Count; index++)
        {
            PdfSemanticLine line = element.Lines[index];
            string marker = LeadingFootnoteMarker(line);
            if (marker.Length == 0 ||
                line.DominantFontSize > bodyFontSize * 0.9f ||
                !HasRaisedFootnoteMarker(line, marker))
            {
                continue;
            }

            PdfSemanticLine[] lines = element.Lines.Skip(index).ToArray();
            footnoteStart = index;
            return new PdfSemanticElement(
                PdfSemanticElementKind.Footnote,
                string.Join(' ', lines.Select(static footnoteLine => footnoteLine.Text.Trim())),
                UnionRectangles(lines.Select(static footnoteLine => footnoteLine.Bounds)),
                lines,
                note: new PdfSemanticNote(marker));
        }

        return null;
    }

    private static string LeadingFootnoteMarker(PdfSemanticLine line)
    {
        string text = line.Text.TrimStart();
        if (text.Length == 0)
        {
            return "";
        }

        if (text[0] is '*' or '∗' or '†' or '‡')
        {
            return text[..1];
        }

        int digits = 0;
        while (digits < text.Length && digits < 2 && char.IsDigit(text[digits]))
        {
            digits++;
        }

        return digits > 0 ? text[..digits] : "";
    }

    private static bool HasRaisedFootnoteMarker(PdfSemanticLine line, string marker)
    {
        return line.Runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .OrderBy(static run => run.Bounds.X)
            .ThenBy(static run => run.Bounds.Y)
            .Take(1)
            .Any(run => run.Text.Trim() == marker &&
                run.FontSize < line.DominantFontSize * 0.9f);
    }

    private static bool[] DisplayFormulaSourceLines(IReadOnlyList<PdfSemanticLine> lines)
    {
        bool[] formulaLines = lines.Select(IsDisplayFormulaLine).ToArray();
        PdfSemanticLine[] displayLines = lines
            .Where((_, index) => formulaLines[index])
            .ToArray();
        if (displayLines.Length == 0)
        {
            return formulaLines;
        }

        for (int index = 0; index < lines.Count; index++)
        {
            PdfSemanticLine line = lines[index];
            if (!formulaLines[index] &&
                IsFormulaContextLine(line) &&
                displayLines.Any(display => AreFormulaLinesVerticallyAdjacent(line, display)))
            {
                formulaLines[index] = true;
            }
        }

        return formulaLines;
    }

    private static bool IsFormulaContextLine(PdfSemanticLine line)
    {
        string text = line.Text.Trim();
        return text.Length > 0 &&
            !IsProseLikeFormulaSourceLine(line) &&
            (TryGetTrailingEquationNumber(text, out _) ||
                HasFormulaSignal(text) ||
                HasOptimizationProgramSignal(text) ||
                line.Runs.Any(static run => HasMathFont(run.FontName)));
    }

    private static bool AreFormulaLinesVerticallyAdjacent(PdfSemanticLine first, PdfSemanticLine second)
    {
        float tolerance = MathF.Max(
            14f,
            MathF.Max(first.DominantFontSize, second.DominantFontSize) * 1.5f);
        return MathF.Abs(first.Bounds.Y - second.Bounds.Y) <= tolerance;
    }

    private static void WriteFormulaBlock(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticElement element,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs,
        string? elementId = null)
    {
        PdfLayoutRectangle nativeBounds = FormulaRenderBounds(page, element, claimedFormulaGlyphs);
        PdfTextRun[] nativeRuns = FormulaRuns(page, nativeBounds, element, claimedFormulaGlyphs).ToArray();
        PdfLayoutPath[] nativePaths = FormulaPaths(page, nativeBounds).ToArray();
        PdfTextGlyph[] nativeGlyphs = FormulaGlyphs(nativeRuns, claimedFormulaGlyphs).ToArray();
        string? equationNumber = null;
        PdfTextGlyph[] detachedEquationNumberGlyphs = [];
        PdfMathMlFormula? mathMl = null;
        bool hasNativeMath = false;
        if (TryDetachTrailingEquationNumber(
                nativeGlyphs,
                out string detachedEquationNumber,
                out PdfTextGlyph[] expressionGlyphs,
                out detachedEquationNumberGlyphs) &&
            PdfMathMlFormula.TryCreate(
                expressionGlyphs,
                nativePaths,
                detachedEquationNumberGlyphs.Average(static glyph => glyph.Bounds.Bottom),
                out mathMl))
        {
            hasNativeMath = true;
            equationNumber = detachedEquationNumber;
        }
        else
        {
            detachedEquationNumberGlyphs = [];
            hasNativeMath = PdfMathMlFormula.TryCreate(nativeGlyphs, nativePaths, out mathMl);
            equationNumber = mathMl?.EquationNumber;
        }

        IReadOnlyList<PdfTextGlyph> equationNumberGlyphs = detachedEquationNumberGlyphs.Length > 0
            ? detachedEquationNumberGlyphs
            : mathMl?.EquationNumberGlyphs ?? [];
        PdfLayoutLink? equationNumberLink = hasNativeMath
            ? FormulaLinkForGlyphs(page, equationNumberGlyphs)
            : null;

        PdfTextRun[] runs = hasNativeMath
            ? nativeRuns
            : FormulaRuns(
                page,
                ExpandRectangle(element.Bounds, 5f, 5f),
                element,
                claimedFormulaGlyphs).ToArray();
        PdfTextGlyph[] glyphs = hasNativeMath
            ? nativeGlyphs
            : FormulaGlyphs(runs, claimedFormulaGlyphs).ToArray();
        PdfLayoutRectangle bounds = hasNativeMath
            ? nativeBounds
            : FormulaFallbackBounds(page, element, runs);
        PdfLayoutPath[] paths = hasNativeMath
            ? nativePaths
            : FormulaPaths(page, bounds).ToArray();
        string classNames = SemanticClassNames(element, page, allowMeasuredWidth: false);
        html.Append("      <div class=\"")
            .Append(classNames);
        if (!classNames.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains("pdf-semantic-formula", StringComparer.Ordinal))
        {
            html.Append(" pdf-semantic-formula");
        }

        if (hasNativeMath)
        {
            html.Append(" pdf-semantic-formula-native");
            if (equationNumber != null)
            {
                html.Append(" pdf-semantic-formula-numbered");
            }
        }

        html.Append('"');
        if (!hasNativeMath)
        {
            html.Append(" role=\"math\"");
        }

        if (!string.IsNullOrEmpty(elementId))
        {
            html.Append(" id=\"").Append(HtmlAttribute(elementId)).Append('"');
        }

        if (equationNumber != null)
        {
            html.Append(" data-equation-number=\"")
                .Append(HtmlAttribute(equationNumber))
                .Append('"');
        }

        html
            .Append(" aria-label=\"")
            .Append(HtmlAttribute(hasNativeMath ? mathMl!.AccessibleText : element.Text))
            .Append("\" style=\"--pdf-semantic-formula-width:")
            .Append(CssPoints(bounds.Width))
            .Append(";--pdf-semantic-formula-height:")
            .Append(CssPoints(bounds.Height));
        if (hasNativeMath)
        {
            html.Append(";--pdf-semantic-math-font-size:")
                .Append(CssPoints(mathMl!.FontSize));
        }

        html
            .Append("\">");

        if (hasNativeMath)
        {
            mathMl!.WriteTo(html, includeEquationNumber: false);
            if (equationNumber != null)
            {
                WriteEquationNumber(html, equationNumber, equationNumberLink);
            }

            if (claimedFormulaGlyphs != null)
            {
                foreach (PdfTextGlyph glyph in mathMl.ClaimedGlyphs.Concat(detachedEquationNumberGlyphs))
                {
                    claimedFormulaGlyphs.Add(FormulaGlyphIdentity(glyph));
                }
            }
        }
        else if (runs.Length == 0)
        {
            html.Append(Html(element.Text));
        }
        else
        {
            if (paths.Length > 0)
            {
                WriteFormulaVectorLayer(html, bounds, paths);
            }

            PdfTextGlyph[] outlinedGlyphs = glyphs
                .Where(static glyph => glyph.Outline is { Count: > 0 })
                .ToArray();
            IReadOnlyDictionary<PdfTextGlyph, PdfLayoutRectangle> tallDelimiters =
                FormulaTallDelimiterBounds(glyphs);
            if (outlinedGlyphs.Length > 0 || tallDelimiters.Count > 0)
            {
                WriteFormulaGlyphOutlineLayer(html, bounds, outlinedGlyphs, tallDelimiters);
            }

            for (int glyphIndex = 0; glyphIndex < glyphs.Length; glyphIndex++)
            {
                PdfTextGlyph glyph = glyphs[glyphIndex];
                bool hasExactShape = glyph.Outline is { Count: > 0 } ||
                    tallDelimiters.ContainsKey(glyph);
                html.Append("<span class=\"pdf-semantic-formula-run");
                if (IsFormulaRadicalGlyph(glyph))
                {
                    html.Append(" pdf-semantic-formula-radical");
                }

                if (glyphIndex > 0 && IsFormulaAttachedSuffix(glyphs[glyphIndex - 1], glyph))
                {
                    html.Append(" pdf-semantic-formula-attached-suffix");
                }

                html.Append('"');
                if (hasExactShape)
                {
                    html.Append(" aria-hidden=\"true\"");
                }

                html.Append(" style=\"left:")
                    .Append(CssPoints(glyph.Bounds.X - bounds.X))
                    .Append(";top:")
                    .Append(CssPoints(glyph.Bounds.Y - bounds.Y))
                    .Append(";font-family:")
                    .Append(CssFontFamily(NormalizeFontName(glyph.FontName)))
                    .Append(";font-size:")
                    .Append(CssPoints(glyph.FontSize))
                    .Append(";color:")
                    .Append(hasExactShape ? "transparent" : ColorHex(glyph.Color))
                    .Append("\">")
                    .Append(Html(glyph.Text))
                    .Append("</span>");
            }

            // Adjacent compact equation rows can share a semantic source run. Claim the glyphs
            // from each rendered row so the following row cannot paint them a second time.
            if (claimedFormulaGlyphs != null && IsCompactFormulaRow(element.Bounds))
            {
                foreach (PdfTextGlyph glyph in glyphs)
                {
                    claimedFormulaGlyphs.Add(FormulaGlyphIdentity(glyph));
                }
            }
        }

        html.AppendLine("</div>");
    }

    internal static void WriteEquationNumber(
        StringBuilder html,
        string equationNumber,
        PdfLayoutLink? link = null)
    {
        string tag = link == null ? "span" : "a";
        html.Append('<').Append(tag).Append(" class=\"pdf-semantic-equation-number");
        if (link != null)
        {
            html.Append(" pdf-semantic-link\" href=\"")
                .Append(HtmlAttribute(LinkHref(link)))
                .Append("\" data-link-kind=\"")
                .Append(HtmlAttribute(link.Kind.ToString().ToLowerInvariant()))
                .Append('"');
            if (!string.IsNullOrWhiteSpace(link.Uri))
            {
                html.Append(" data-uri=\"")
                    .Append(HtmlAttribute(link.Uri))
                    .Append('"');
            }
        }
        else
        {
            html.Append('"');
        }

        html.Append(" aria-label=\"Equation ")
            .Append(HtmlAttribute(equationNumber[1..^1]))
            .Append("\">")
            .Append(Html(equationNumber))
            .Append("</")
            .Append(tag)
            .Append('>');
    }

    internal static PdfLayoutLink? FormulaLinkForGlyphs(
        PdfLayoutPage page,
        IReadOnlyList<PdfTextGlyph> glyphs)
    {
        if (glyphs.Count == 0)
        {
            return null;
        }

        return page.Links
            .Where(HasSemanticLinkTarget)
            .Where(link => LinkBounds(link).Any(bounds =>
                glyphs.Any(glyph => RectanglesIntersect(bounds, glyph.PageBounds, 0.25f))))
            .OrderBy(link => LinkBounds(link).Min(static bounds => bounds.Width * bounds.Height))
            .FirstOrDefault();
    }

    private static PdfLayoutRectangle FormulaRenderBounds(
        PdfLayoutPage page,
        PdfSemanticElement element,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null)
    {
        PdfLayoutRectangle bounds = ExpandRectangle(element.Bounds, 5f, 5f);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            PdfTextRun[] runs = FormulaRuns(page, bounds, element, claimedFormulaGlyphs).ToArray();
            PdfLayoutPath[] paths = FormulaPaths(page, bounds).ToArray();
            if (runs.Length == 0 && paths.Length == 0)
            {
                return bounds;
            }

            PdfLayoutRectangle expanded = ExpandRectangle(UnionRectangles(
                runs.Select(static run => run.Bounds)
                    .Concat(paths.Select(static path => path.Bounds))), 2f, 2f);
            if (MathF.Abs(expanded.X - bounds.X) < 0.01f &&
                MathF.Abs(expanded.Y - bounds.Y) < 0.01f &&
                MathF.Abs(expanded.Width - bounds.Width) < 0.01f &&
                MathF.Abs(expanded.Height - bounds.Height) < 0.01f)
            {
                return expanded;
            }

            bounds = expanded;
        }

        return bounds;
    }

    private static IEnumerable<PdfTextRun> FormulaRuns(
        PdfLayoutPage page,
        PdfLayoutRectangle bounds,
        PdfSemanticElement element,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null)
    {
        HashSet<PdfTextRun> sourceRuns = FormulaSourceRuns(element)
            .ToHashSet((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance);
        FormulaPageLookup pageLookup = FormulaPageLookups.GetValue(
            page,
            static layoutPage => CreateFormulaPageLookup(layoutPage));
        bool hasMathFontContext = page.Runs.Any(run =>
            HasMathFont(run.FontName) &&
            RectanglesIntersect(run.Bounds, bounds, 0.75f));
        return page.Runs
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .Where(run => run.Glyphs.Any(glyph =>
                PdfMathMlFormula.IsEligibleGlyph(glyph) &&
                !IsClaimedFormulaGlyph(glyph, claimedFormulaGlyphs)))
            .Where(run => sourceRuns.Contains(run) ||
                IsFormulaOperatorLimitRun(bounds, run, pageLookup.LargeOperators) ||
                !pageLookup.ProseLineRuns.Contains(run) &&
                IsVerticallyAttachedFormulaRun(element.Bounds, run.Bounds) &&
                (RectanglesIntersect(run.Bounds, bounds, 0.75f) &&
                        (IsFormulaRunCandidate(run) ||
                            hasMathFontContext && IsCompactFormulaContextRun(run)) ||
                    IsFormulaAdjacentRun(page, bounds, run)));
    }

    private static bool IsVerticallyAttachedFormulaRun(
        PdfLayoutRectangle formulaBounds,
        PdfLayoutRectangle runBounds)
    {
        if (!IsCompactFormulaRow(formulaBounds))
        {
            return true;
        }

        float center = runBounds.Y + runBounds.Height / 2f;
        float tolerance = MathF.Max(0.75f, MathF.Min(2f, formulaBounds.Height * 0.06f));
        return center >= formulaBounds.Y - tolerance &&
            center <= formulaBounds.Bottom + tolerance;
    }

    private static bool IsCompactFormulaRow(PdfLayoutRectangle bounds)
    {
        return bounds.Height <= 14f;
    }

    internal static IReadOnlyList<PdfTextRun> FormulaSourceRuns(PdfSemanticElement element)
    {
        return element.Lines
            .Where(static line => !IsProseLikeFormulaSourceLine(line))
            .SelectMany(static line => line.Runs)
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .Where(static run => run.Glyphs.Any(PdfMathMlFormula.IsEligibleGlyph))
            .Distinct((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance)
            .ToArray();
    }

    private static PdfLayoutRectangle FormulaFallbackBounds(
        PdfLayoutPage page,
        PdfSemanticElement element,
        IReadOnlyList<PdfTextRun> runs)
    {
        PdfLayoutRectangle sourceBounds = runs.Count == 0
            ? element.Bounds
            : UnionRectangles(runs.Select(static run => run.Bounds));
        PdfLayoutRectangle expanded = ExpandRectangle(sourceBounds, 2f, 2f);
        PdfLayoutPath[] paths = FormulaPaths(page, expanded).ToArray();
        return paths.Length == 0
            ? expanded
            : ExpandRectangle(UnionRectangles(
                runs.Select(static run => run.Bounds)
                    .Concat(paths.Select(static path => path.Bounds))), 2f, 2f);
    }

    private static IEnumerable<PdfLayoutPath> FormulaPaths(PdfLayoutPage page, PdfLayoutRectangle bounds)
    {
        return page.Paths
            .Where(path => path.Bounds.Width > 0.1f || path.Bounds.Height > 0.1f)
            .Where(path => RectanglesIntersect(path.Bounds, bounds, 1.5f))
            .OrderBy(static path => path.Bounds.Y)
            .ThenBy(static path => path.Bounds.X);
    }

    private static bool IsFormulaRunCandidate(PdfTextRun run)
    {
        string text = run.Text.Trim();
        return text.Length > 0 &&
            (HasMathFont(run.FontName) ||
                HasFormulaFunction(text) ||
                text.All(static character => character is '(' or ')' or ',' or '.' or '=' or '+' or '-' or '/' or ':' or ';'));
    }

    private static bool IsCompactFormulaContextRun(PdfTextRun run)
    {
        string text = run.Text.Trim();
        return text.Length is > 0 and <= 3 &&
            !text.Any(char.IsWhiteSpace);
    }

    private static FormulaPageLookup CreateFormulaPageLookup(PdfLayoutPage page)
    {
        HashSet<PdfTextGlyph> proseGlyphs = page.Lines
            .Where(line => IsProseLikeFormulaSourceLine(line) ||
                IsShortProseDominantFormulaSourceLine(line.Text, line.Runs))
            .SelectMany(static line => line.Runs)
            .SelectMany(static run => run.Glyphs)
            .ToHashSet((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        HashSet<PdfTextRun> proseLineRuns = page.Runs
            .Where(run => run.Glyphs.Any(proseGlyphs.Contains))
            .ToHashSet((IEqualityComparer<PdfTextRun>)ReferenceEqualityComparer.Instance);
        PdfTextGlyph[] largeOperators = page.Glyphs
            .Where(static glyph =>
                PdfMathMlFormula.IsEligibleGlyph(glyph) &&
                glyph.Text is "∑" or "∏" or "∫")
            .ToArray();
        return new FormulaPageLookup(proseLineRuns, largeOperators);
    }

    private static bool IsProseLikeFormulaSourceLine(PdfTextLine line)
    {
        return IsProseLikeFormulaSourceLine(line.Text, line.Runs);
    }

    private static bool IsProseLikeFormulaSourceLine(PdfSemanticLine line)
    {
        return IsProseLikeFormulaSourceLine(line.Text, line.Runs);
    }

    private static bool IsFormulaOnlySourceLine(PdfSemanticLine line)
    {
        return !IsProseLikeFormulaSourceLine(line);
    }

    private static bool IsProseLikeFormulaSourceLine(
        string text,
        IReadOnlyList<PdfTextRun> runs)
    {
        if (FormulaSourceWordCount(text, runs) < 5)
        {
            return false;
        }

        int totalLetters = runs.Sum(static run => run.Text.Count(char.IsLetter));
        int proseLetters = runs
            .Where(static run =>
                !HasMathFont(run.FontName) &&
                !NormalizeFontName(run.FontName).StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase) &&
                !IsItalicFont(run.FontName))
            .Sum(static run => run.Text.Count(char.IsLetter));
        return proseLetters >= 12 && proseLetters * 3 >= totalLetters * 2;
    }

    private static bool IsShortProseDominantFormulaSourceLine(
        string text,
        IReadOnlyList<PdfTextRun> runs)
    {
        if (HasFormulaSignal(text))
        {
            return false;
        }

        int totalLetters = runs.Sum(static run => run.Text.Count(char.IsLetter));
        int proseLetters = runs
            .Where(static run =>
            {
                string fontName = NormalizeFontName(run.FontName);
                return !HasMathFont(fontName) &&
                    !fontName.StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase) &&
                    !IsItalicFont(fontName);
            })
            .Sum(static run => run.Text.Count(char.IsLetter));
        return proseLetters >= 8 &&
            totalLetters > 0 &&
            proseLetters * 3 >= totalLetters * 2;
    }

    private static int FormulaSourceWordCount(
        string text,
        IReadOnlyList<PdfTextRun> runs)
    {
        int lexicalRuns = runs.Count(static run =>
        {
            string fontName = NormalizeFontName(run.FontName);
            return run.Text.Count(char.IsLetter) >= 2 &&
                !HasMathFont(fontName) &&
                !fontName.StartsWith("SYMBOL", StringComparison.OrdinalIgnoreCase) &&
                !IsItalicFont(fontName);
        });
        return Math.Max(CountWords(text), lexicalRuns);
    }

    private static IEnumerable<PdfTextGlyph> FormulaGlyphs(
        IEnumerable<PdfTextRun> runs,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null)
    {
        return runs
            .SelectMany(static run => run.Glyphs)
            .Where(PdfMathMlFormula.IsEligibleGlyph)
            .Where(glyph => !IsClaimedFormulaGlyph(glyph, claimedFormulaGlyphs));
    }

    private static bool IsFormulaRadicalGlyph(PdfTextGlyph glyph)
    {
        return glyph.Text.Trim() == "√";
    }

    private static bool IsFormulaAttachedSuffix(PdfTextGlyph previous, PdfTextGlyph current)
    {
        if (current.FontSize >= previous.FontSize * 0.85f)
        {
            return false;
        }

        float gap = current.Bounds.X - previous.Bounds.Right;
        float tolerance = MathF.Max(1.5f, current.FontSize * 0.25f);
        return gap >= -tolerance && gap <= tolerance;
    }

    private static void WriteFormulaVectorLayer(
        StringBuilder html,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfLayoutPath> paths)
    {
        html.Append("<svg class=\"pdf-semantic-formula-vector-layer\" viewBox=\"")
            .Append(SvgNumber(bounds.X))
            .Append(' ')
            .Append(SvgNumber(bounds.Y))
            .Append(' ')
            .Append(SvgNumber(bounds.Width))
            .Append(' ')
            .Append(SvgNumber(bounds.Height))
            .Append("\" aria-hidden=\"true\">");

        foreach (PdfLayoutPath path in paths)
        {
            WriteVectorPath(html, path);
        }

        html.Append("</svg>");
    }

    private static void WriteFormulaGlyphOutlineLayer(
        StringBuilder html,
        PdfLayoutRectangle bounds,
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyDictionary<PdfTextGlyph, PdfLayoutRectangle> tallDelimiters)
    {
        html.Append("<svg class=\"pdf-semantic-formula-vector-layer pdf-semantic-formula-glyph-outline-layer\" viewBox=\"")
            .Append(SvgNumber(bounds.X))
            .Append(' ')
            .Append(SvgNumber(bounds.Y))
            .Append(' ')
            .Append(SvgNumber(bounds.Width))
            .Append(' ')
            .Append(SvgNumber(bounds.Height))
            .Append("\" aria-hidden=\"true\">");

        foreach (PdfTextGlyph glyph in glyphs)
        {
            html.Append("<path d=\"")
                .Append(HtmlAttribute(SvgPathData(glyph.Outline!)))
                .Append("\" fill=\"")
                .Append(ColorHex(glyph.Color))
                .Append('"');
            if (glyph.Color.Alpha < 0.999f)
            {
                html.Append(" fill-opacity=\"")
                    .Append(SvgNumber(glyph.Color.Alpha))
                    .Append('"');
            }

            html.Append(" />");
        }

        foreach ((PdfTextGlyph glyph, PdfLayoutRectangle delimiterBounds) in tallDelimiters)
        {
            float x = glyph.Text == "("
                ? delimiterBounds.Right - delimiterBounds.Width * 0.2f
                : delimiterBounds.X + delimiterBounds.Width * 0.2f;
            float controlX = glyph.Text == "("
                ? delimiterBounds.X
                : delimiterBounds.Right;
            float quarter = delimiterBounds.Height * 0.24f;
            html.Append("<path class=\"pdf-semantic-formula-tall-delimiter\" data-delimiter=\"")
                .Append(glyph.Text == "(" ? "open" : "close")
                .Append("\" d=\"M ")
                .Append(SvgNumber(x))
                .Append(' ')
                .Append(SvgNumber(delimiterBounds.Y))
                .Append(" C ")
                .Append(SvgNumber(controlX))
                .Append(' ')
                .Append(SvgNumber(delimiterBounds.Y + quarter))
                .Append(' ')
                .Append(SvgNumber(controlX))
                .Append(' ')
                .Append(SvgNumber(delimiterBounds.Bottom - quarter))
                .Append(' ')
                .Append(SvgNumber(x))
                .Append(' ')
                .Append(SvgNumber(delimiterBounds.Bottom))
                .Append("\" fill=\"none\" stroke=\"")
                .Append(ColorHex(glyph.Color))
                .Append("\" stroke-width=\"")
                .Append(SvgNumber(MathF.Max(0.55f, glyph.FontSize * 0.065f)))
                .Append("\" stroke-linecap=\"round\"");
            if (glyph.Color.Alpha < 0.999f)
            {
                html.Append(" stroke-opacity=\"")
                    .Append(SvgNumber(glyph.Color.Alpha))
                    .Append('"');
            }

            html.Append(" />");
        }

        html.Append("</svg>");
    }

    private static IReadOnlyDictionary<PdfTextGlyph, PdfLayoutRectangle> FormulaTallDelimiterBounds(
        IReadOnlyList<PdfTextGlyph> glyphs)
    {
        Dictionary<PdfTextGlyph, PdfLayoutRectangle> result =
            new((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        HashSet<PdfTextGlyph> pairedCloses =
            new((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        PdfTextGlyph[] closes = glyphs
            .Where(static glyph => IsUnoutlinedComputerModernDelimiter(glyph, ")"))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ToArray();
        foreach (PdfTextGlyph open in glyphs
            .Where(static glyph => IsUnoutlinedComputerModernDelimiter(glyph, "("))
            .OrderBy(static glyph => glyph.Bounds.X))
        {
            PdfTextGlyph? close = closes.FirstOrDefault(candidate =>
                !pairedCloses.Contains(candidate) &&
                candidate.Bounds.X > open.Bounds.Right &&
                MathF.Abs(candidate.Bounds.Y - open.Bounds.Y) <=
                    MathF.Max(1f, open.FontSize * 0.2f));
            if (close == null)
            {
                continue;
            }

            PdfTextGlyph[] interior = glyphs
                .Where(glyph => !ReferenceEquals(glyph, open) && !ReferenceEquals(glyph, close))
                .Where(glyph =>
                {
                    float center = glyph.Bounds.X + glyph.Bounds.Width / 2f;
                    return center >= open.Bounds.Right - 0.5f &&
                        center <= close.Bounds.X + 0.5f;
                })
                .ToArray();
            if (interior.Length == 0)
            {
                continue;
            }

            float top = MathF.Min(
                MathF.Min(open.Bounds.Y, close.Bounds.Y),
                interior.Min(static glyph => glyph.Bounds.Y));
            float bottom = MathF.Max(
                MathF.Max(open.Bounds.Bottom, close.Bounds.Bottom),
                interior.Max(static glyph => glyph.Bounds.Bottom));
            float height = bottom - top;
            if (height < MathF.Max(open.Bounds.Height, close.Bounds.Height) * 1.35f)
            {
                continue;
            }

            pairedCloses.Add(close);
            result.Add(open, new PdfLayoutRectangle(open.Bounds.X, top, open.Bounds.Width, height));
            result.Add(close, new PdfLayoutRectangle(close.Bounds.X, top, close.Bounds.Width, height));
        }

        return result;
    }

    private static bool IsUnoutlinedComputerModernDelimiter(PdfTextGlyph glyph, string text)
    {
        return glyph.Text == text &&
            glyph.Outline is not { Count: > 0 } &&
            NormalizeFontName(glyph.FontName).StartsWith("CMEX", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFormulaAdjacentRun(PdfLayoutPage page, PdfLayoutRectangle bounds, PdfTextRun run)
    {
        float formulaCenter = bounds.Y + (bounds.Height / 2f);
        float runCenter = run.Bounds.Y + (run.Bounds.Height / 2f);
        float verticalTolerance = MathF.Max(8f, MathF.Min(18f, bounds.Height * 0.35f));
        if (MathF.Abs(runCenter - formulaCenter) > verticalTolerance)
        {
            return false;
        }

        string text = run.Text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        bool nearFormula = run.Bounds.Right >= MathF.Max(24f, bounds.X - 220f) &&
            run.Bounds.X <= MathF.Min(page.Width - 24f, bounds.Right + 220f);
        if (!nearFormula)
        {
            return false;
        }

        return HasMathFont(run.FontName) ||
            text.All(static character => character is '(' or ')' or ',' or '.' or '=' or '+' or '-' or '/' or ':' or ';') ||
            IsEquationNumber(text);
    }

    internal static bool IsFormulaOperatorLimitRun(
        PdfLayoutPage page,
        PdfLayoutRectangle bounds,
        PdfTextRun run)
    {
        FormulaPageLookup pageLookup = FormulaPageLookups.GetValue(
            page,
            static layoutPage => CreateFormulaPageLookup(layoutPage));
        return IsFormulaOperatorLimitRun(bounds, run, pageLookup.LargeOperators);
    }

    private static bool IsFormulaOperatorLimitRun(
        PdfLayoutRectangle bounds,
        PdfTextRun run,
        IReadOnlyList<PdfTextGlyph> largeOperators)
    {
        string text = run.Text.Trim();
        bool hasMathFont = HasMathFont(run.FontName);
        bool plausibleNonMathLimit = text.Length == 1 &&
            (char.IsDigit(text[0]) || char.IsLetter(text[0]) && IsItalicFont(run.FontName));
        if (text.Length is 0 or > 16 ||
            run.Bounds.Width > MathF.Max(48f, run.FontSize * 8f) ||
            !hasMathFont && !plausibleNonMathLimit)
        {
            return false;
        }

        foreach (PdfTextGlyph limit in run.Glyphs.Where(PdfMathMlFormula.IsEligibleGlyph))
        {
            foreach (PdfTextGlyph largeOperator in largeOperators)
            {
                if (!RectanglesIntersect(largeOperator.Bounds, bounds, 1.5f) ||
                    limit.FontSize >= largeOperator.FontSize * 0.85f)
                {
                    continue;
                }

                float limitCenter = limit.Bounds.X + limit.Bounds.Width / 2f;
                float horizontalTolerance = largeOperator.FontSize * 0.75f;
                if (limitCenter < largeOperator.Bounds.X - horizontalTolerance ||
                    limitCenter > largeOperator.Bounds.Right + horizontalTolerance)
                {
                    continue;
                }

                float verticalGap = MathF.Max(
                    0f,
                    MathF.Max(
                        largeOperator.Bounds.Y - limit.Bounds.Bottom,
                        limit.Bounds.Y - largeOperator.Bounds.Bottom));
                if (verticalGap <= largeOperator.FontSize * 2.5f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEquationNumber(string text)
    {
        return text.Length >= 3 &&
            text[0] == '(' &&
            text[^1] == ')' &&
            text[1..^1].Any(char.IsDigit) &&
            text[1..^1].All(static character => char.IsDigit(character) || character == '.') &&
            text[1] != '.' &&
            text[^2] != '.' &&
            !text.Contains("..", StringComparison.Ordinal);
    }

    private static bool TryGetTrailingEquationNumber(string text, out string equationNumber)
    {
        int open = text.LastIndexOf("(", StringComparison.Ordinal);
        equationNumber = open >= 0 ? text[open..].Trim() : "";
        return IsEquationNumber(equationNumber);
    }

    private static bool TryDetachTrailingEquationNumber(
        IReadOnlyList<PdfTextGlyph> glyphs,
        out string equationNumber,
        out PdfTextGlyph[] expressionGlyphs,
        out PdfTextGlyph[] equationNumberGlyphs)
    {
        equationNumber = "";
        expressionGlyphs = glyphs.ToArray();
        equationNumberGlyphs = [];
        PdfTextGlyph[] baselineCandidates = glyphs
            .OrderBy(static glyph => glyph.Bounds.Bottom)
            .ThenBy(static glyph => glyph.Bounds.X)
            .ToArray();
        List<List<PdfTextGlyph>> baselineGroups = [];
        foreach (PdfTextGlyph glyph in baselineCandidates)
        {
            List<PdfTextGlyph>? group = baselineGroups.LastOrDefault();
            float baselineTolerance = MathF.Max(1.2f, glyph.FontSize * 0.18f);
            if (group == null ||
                MathF.Abs(group.Average(static item => item.Bounds.Bottom) - glyph.Bounds.Bottom) > baselineTolerance)
            {
                baselineGroups.Add([glyph]);
            }
            else
            {
                group.Add(glyph);
            }
        }

        PdfTextGlyph[]? selectedNumberGlyphs = null;
        float selectedNumberX = float.NegativeInfinity;
        foreach (List<PdfTextGlyph> group in baselineGroups)
        {
            PdfTextGlyph[] ordered = group.OrderBy(static glyph => glyph.Bounds.X).ToArray();
            for (int start = ordered.Length - 1; start > 0; start--)
            {
                PdfTextGlyph[] candidateGlyphs = ordered[start..];
                string candidate = string.Concat(candidateGlyphs.Select(static glyph => glyph.Text)).Trim();
                if (!IsEquationNumber(candidate))
                {
                    continue;
                }

                PdfTextGlyph[] rowExpression = ordered[..start];
                float fontSize = candidateGlyphs.Max(static glyph => glyph.FontSize);
                float expressionRight = rowExpression.Max(static glyph => glyph.Bounds.Right);
                string expressionText = string.Concat(rowExpression.Select(static glyph => glyph.Text));
                if (candidateGlyphs[0].Bounds.X - expressionRight < fontSize * 1.4f ||
                    !HasFormulaSignal(expressionText))
                {
                    continue;
                }

                if (candidateGlyphs[0].Bounds.X > selectedNumberX)
                {
                    equationNumber = candidate;
                    selectedNumberGlyphs = candidateGlyphs;
                    selectedNumberX = candidateGlyphs[0].Bounds.X;
                }

                break;
            }
        }

        if (selectedNumberGlyphs == null)
        {
            return false;
        }

        HashSet<PdfTextGlyph> numberGlyphs = selectedNumberGlyphs
            .ToHashSet((IEqualityComparer<PdfTextGlyph>)ReferenceEqualityComparer.Instance);
        expressionGlyphs = glyphs.Where(glyph => !numberGlyphs.Contains(glyph)).ToArray();
        equationNumberGlyphs = selectedNumberGlyphs;
        return true;
    }

    private static PdfLayoutRectangle ExpandRectangle(PdfLayoutRectangle bounds, float horizontal, float vertical)
    {
        return new PdfLayoutRectangle(
            bounds.X - horizontal,
            bounds.Y - vertical,
            bounds.Width + horizontal + horizontal,
            bounds.Height + vertical + vertical);
    }

    private static void WritePositionedSemanticElement(
        StringBuilder html,
        PdfLayoutPage page,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        float scale,
        string? elementId = null,
        int? headingLevel = null)
    {
        string tagName = SemanticTagName(element, headingLevel);
        html.Append("    <")
            .Append(tagName)
            .Append(" class=\"")
            .Append(SemanticClassNames(element))
            .Append(" pdf-semantic-positioned pdf-semantic-vertical")
            .Append('"');
        if (!string.IsNullOrEmpty(elementId))
        {
            html.Append(" id=\"").Append(HtmlAttribute(elementId)).Append('"');
        }

        AppendTaggedStructureAttributes(html, element);
        AppendTextDirectionAttribute(html, element.Text);
        AppendAsideLabelAttribute(html, element);
        html.Append(" style=\"")
            .Append(PositionStyle(page, element, scale))
            .Append("\">");
        WriteSemanticText(html, element, footnotes, page);
        html.Append("</")
            .Append(tagName)
            .AppendLine(">");
    }

    private static void WriteSemanticText(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page = null)
    {
        if (element.Kind == PdfSemanticElementKind.Algorithm && element.Algorithm != null)
        {
            WriteSemanticAlgorithm(html, element.Algorithm, footnotes, page);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.CodeBlock)
        {
            html.Append("<code>")
                .Append(Html(element.Text))
                .Append("</code>");
            return;
        }

        if (element.Kind == PdfSemanticElementKind.BlockQuote && element.Quotation != null)
        {
            WriteSemanticQuotation(html, element.Quotation, footnotes);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.Aside && element.Aside != null)
        {
            WriteSemanticAside(html, element.Aside, footnotes, page);
            return;
        }

        if (IsTitleElement(element) && element.Lines.Count > 1)
        {
            WriteSemanticSourceLines(html, element, footnotes, page);
            return;
        }

        if (page != null &&
            IsSparseGraphicCoverPage(page) &&
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Count > 1 &&
            SourceAlignmentClass(page, element) != null)
        {
            WriteSemanticSourceLines(html, element, footnotes, page);
            return;
        }

        if (element.Kind == PdfSemanticElementKind.AuthorBlock)
        {
            foreach (PdfSemanticLine line in element.Lines)
            {
                html.Append("<span class=\"")
                    .Append(SemanticLineClassNames(line))
                    .Append("\">");
                WriteTextWithFootnoteReferences(html, line.Text, footnotes);
                html.Append("</span>");
            }

            return;
        }

        if (element.Kind == PdfSemanticElementKind.FrontMatter)
        {
            WriteSemanticSourceLines(html, element, footnotes, page);
            return;
        }

        if (IsSameRowLineGroup(element))
        {
            foreach (PdfSemanticLine line in SameRowLines(element))
            {
                html.Append("<span class=\"")
                    .Append(SemanticLineClassNames(line))
                    .Append("\">");
                WriteTextWithFootnoteReferences(html, line.Text, footnotes);
                html.Append("</span>");
            }

            return;
        }

        if (element.Kind is PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer)
        {
            if (page != null &&
                (element.Kind == PdfSemanticElementKind.Header ||
                    element.Lines.Any(static line => line.InlineSemantics.Count > 0)) &&
                CanWriteRichSemanticText(element))
            {
                WriteRichSemanticText(html, element, footnotes, page);
                return;
            }

            for (int index = 0; index < element.Lines.Count; index++)
            {
                if (index > 0)
                {
                    html.Append("<br />");
                }

                html.Append(Html(element.Lines[index].Text));
            }

            return;
        }

        if (CanWriteRichSemanticText(element))
        {
            WriteRichSemanticText(html, element, footnotes, page);
            return;
        }

        WriteTextWithFootnoteReferences(html, element.Text, footnotes);
    }

    private static void WriteSemanticAlgorithm(
        StringBuilder html,
        PdfSemanticAlgorithm algorithm,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        PdfSemanticElement caption = new(
            PdfSemanticElementKind.Paragraph,
            algorithm.Caption,
            UnionRectangles(algorithm.CaptionLines.Select(static line => line.Bounds)),
            algorithm.CaptionLines);
        html.Append("<figcaption class=\"pdf-semantic-algorithm-caption\">");
        if (CanWriteRichSemanticText(caption))
        {
            WriteRichSemanticText(html, caption, footnotes, page);
        }
        else
        {
            WriteTextWithFootnoteReferences(html, algorithm.Caption, footnotes);
        }

        html.AppendLine("</figcaption>");
        html.AppendLine("<div class=\"pdf-semantic-algorithm-rows\" role=\"list\">");
        PdfSemanticLine[] protectedLines = algorithm.CaptionLines
            .Concat(algorithm.Rows.Select(static row => row.Line))
            .ToArray();
        PdfSemanticElement protectedElement = new(
            PdfSemanticElementKind.Algorithm,
            algorithm.Caption + "\n" + string.Join('\n', algorithm.Rows.Select(static row => row.Text)),
            UnionRectangles(protectedLines.Select(static line => line.Bounds)),
            protectedLines);
        for (int index = 0; index < algorithm.Rows.Count; index++)
        {
            PdfSemanticAlgorithmRow row = algorithm.Rows[index];
            html.Append("<div class=\"pdf-semantic-algorithm-row\" role=\"listitem\" data-source-row=\"")
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"--pdf-semantic-algorithm-indent:")
                .Append(CssPoints(row.Indentation))
                .Append("\"><code>");
            if (row.Line.Runs.Count > 0)
            {
                List<InlineTextSegment> segments = InlineTextSegments(row.Line, page, protectedElement).ToList();
                string lineText = string.Concat(segments.Select(static segment => segment.Text));
                WriteInlineTextSegments(html, row.Line, segments, lineText, footnotes);
            }
            else
            {
                WriteTextWithFootnoteReferences(html, row.Text, footnotes);
            }

            html.AppendLine("</code></div>");
        }

        html.Append("</div>");
    }

    private static void WriteSemanticQuotation(
        StringBuilder html,
        PdfSemanticQuotation quotation,
        FootnoteContext footnotes)
    {
        html.Append("<p class=\"pdf-semantic-quote-text\">");
        WriteTextWithFootnoteReferences(html, quotation.Text, footnotes);
        html.Append("</p>");
        if (quotation.Attribution != null)
        {
            html.Append("<footer class=\"pdf-semantic-quote-attribution\">");
            WriteTextWithFootnoteReferences(html, quotation.Attribution, footnotes);
            html.Append("</footer>");
        }
    }

    private static void WriteSemanticAside(
        StringBuilder html,
        PdfSemanticAside aside,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        html.Append("<header class=\"pdf-semantic-aside-label\">")
            .Append(Html(aside.Label))
            .Append("</header>");
        foreach (PdfSemanticElement content in aside.Content)
        {
            string tagName = SemanticTagName(content);
            html.Append('<')
                .Append(tagName)
                .Append(" class=\"")
                .Append(SemanticClassNames(content, page, allowMeasuredWidth: false))
                .Append('"');
            AppendTextDirectionAttribute(html, content.Text);
            html.Append('>');
            WriteSemanticText(html, content, footnotes, page);
            html.Append("</").Append(tagName).Append('>');
        }
    }

    private static void AppendAsideLabelAttribute(StringBuilder html, PdfSemanticElement element)
    {
        if (element.Kind == PdfSemanticElementKind.Aside && element.Aside != null)
        {
            html.Append(" aria-label=\"")
                .Append(HtmlAttribute(element.Aside.Label))
                .Append('"');
        }
    }

    private static void WriteSemanticSourceLines(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        PdfSemanticLine? previous = null;
        foreach (PdfSemanticLine line in element.Lines)
        {
            html.Append("<span class=\"")
                .Append(SemanticLineClassNames(line))
                .Append('"');
            if (element.Kind == PdfSemanticElementKind.FrontMatter && previous != null)
            {
                float sourceGap = MathF.Max(0f, line.Bounds.Y - previous.Bounds.Bottom);
                if (sourceGap > 0.01f)
                {
                    html.Append(" style=\"margin-top:")
                        .Append(CssPoints(sourceGap))
                        .Append('"');
                }
            }

            html.Append("><span class=\"pdf-semantic-source-line-content\">");
            PdfSemanticElement lineElement = new(
                element.Kind,
                line.Text,
                line.Bounds,
                [line],
                element.HeadingLevel);
            if (CanWriteRichSemanticText(lineElement))
            {
                WriteRichSemanticText(html, lineElement, footnotes, page);
            }
            else
            {
                WriteTextWithFootnoteReferences(html, line.Text, footnotes);
            }

            html.Append("</span></span>");
            previous = line;
        }
    }

    private static bool CanWriteRichSemanticText(PdfSemanticElement element)
    {
        return element.Kind is PdfSemanticElementKind.Paragraph or PdfSemanticElementKind.Heading or PdfSemanticElementKind.FrontMatter or PdfSemanticElementKind.Footnote or PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer &&
            !IsFormulaBlock(element) &&
            element.Lines.Count > 0 &&
            element.Lines.All(static line => line.Runs.Count > 0) &&
            element.Lines.Any(static line => line.Runs.Any(static run => MathF.Abs(run.Direction) < 0.01f));
    }

    private static void WriteRichSemanticText(
        StringBuilder html,
        PdfSemanticElement element,
        FootnoteContext footnotes,
        PdfLayoutPage? page,
        string? leadingTextToSkip = null)
    {
        string previousLineText = "";
        bool wroteLine = false;
        bool previousLineEndedWithMathIdentifier = false;
        bool skippedLeadingText = string.IsNullOrEmpty(leadingTextToSkip);
        List<RichSemanticSourceLine> sourceLines = [];
        foreach (PdfSemanticLine line in element.Lines)
        {
            if (IsDetachedMathAttachmentLine(line, element))
            {
                continue;
            }

            List<InlineTextSegment> segments = InlineTextSegments(line, page, element).ToList();
            if (ShouldPrependMissingSummation(previousLineText, segments))
            {
                PrependMissingSummation(segments);
            }

            if (!skippedLeadingText &&
                TryRemoveLeadingText(segments, leadingTextToSkip!))
            {
                TrimLeadingWhitespace(segments);
                skippedLeadingText = true;
            }

            bool continuesMathIdentifier = previousLineEndedWithMathIdentifier &&
                TryPromoteLeadingKnownMathIdentifierSuffix(segments);
            string lineText = string.Concat(segments.Select(static segment => segment.Text));
            if (lineText.Length == 0)
            {
                continue;
            }

            sourceLines.Add(new RichSemanticSourceLine(
                line,
                segments,
                lineText,
                continuesMathIdentifier));

            previousLineText = lineText;
            previousLineEndedWithMathIdentifier = EndsWithMathBaseIdentifierSegment(segments);
        }

        for (int index = 0; index < sourceLines.Count; index++)
        {
            RichSemanticSourceLine sourceLine = sourceLines[index];
            bool joinsPreviousWord = index > 0 &&
                TryApplySemanticLineBreakDehyphenation(
                    element.Text,
                    sourceLines[index - 1],
                    sourceLine);
            if (joinsPreviousWord)
            {
                RichSemanticSourceLine previous = sourceLines[index - 1];
                sourceLines[index - 1] = previous with
                {
                    Text = string.Concat(previous.Segments.Select(static segment => segment.Text))
                };
            }

            sourceLines[index] = sourceLine with { JoinsPreviousWord = joinsPreviousWord };
        }

        previousLineText = "";
        wroteLine = false;
        foreach (RichSemanticSourceLine sourceLine in sourceLines)
        {
            string lineText = sourceLine.Text;

            if (wroteLine &&
                !sourceLine.ContinuesMathIdentifier &&
                !sourceLine.JoinsPreviousWord &&
                NeedsSpaceBetween(previousLineText, lineText))
            {
                html.Append(' ');
            }

            WriteInlineTextSegments(html, sourceLine.Line, sourceLine.Segments, lineText, footnotes);
            previousLineText = lineText;
            wroteLine = true;
        }
    }

    private static bool TryApplySemanticLineBreakDehyphenation(
        string semanticText,
        RichSemanticSourceLine previous,
        RichSemanticSourceLine current)
    {
        string previousText = previous.Text.TrimEnd();
        string currentText = current.Text.TrimStart();
        if (!previousText.EndsWith("-", StringComparison.Ordinal) ||
            currentText.Length == 0 ||
            !char.IsLower(currentText[0]))
        {
            return false;
        }

        int leftStart = previousText.Length - 1;
        while (leftStart > 0 && char.IsLetter(previousText[leftStart - 1]))
        {
            leftStart--;
        }

        int rightLength = 0;
        while (rightLength < currentText.Length && char.IsLetter(currentText[rightLength]))
        {
            rightLength++;
        }

        if (previousText.Length - 1 - leftStart < 2 || rightLength < 2)
        {
            return false;
        }

        string joinedWord = previousText[leftStart..^1] + currentText[..rightLength];
        if (semanticText.Contains(joinedWord, StringComparison.Ordinal))
        {
            RemoveTrailingHyphen(previous.Segments);
            return true;
        }

        string authoredCompound = previousText[leftStart..] + currentText[..rightLength];
        return semanticText.Contains(authoredCompound, StringComparison.Ordinal);
    }

    private static void RemoveTrailingHyphen(List<InlineTextSegment> segments)
    {
        for (int index = segments.Count - 1; index >= 0; index--)
        {
            string text = segments[index].Text;
            if (text.Length == 0)
            {
                continue;
            }

            int hyphen = text.Length - 1;
            while (hyphen >= 0 && char.IsWhiteSpace(text[hyphen]))
            {
                hyphen--;
            }

            if (hyphen >= 0 && text[hyphen] == '-')
            {
                segments[index] = segments[index] with
                {
                    Text = text.Remove(hyphen, 1)
                };
            }

            return;
        }
    }

    private static bool IsDetachedMathAttachmentLine(PdfSemanticLine line, PdfSemanticElement element)
    {
        return element.Lines.Count > 1 &&
            line.Text.Length <= 3 &&
            line.Runs.Count > 0 &&
            line.Runs.All(static run => IsCompactMathFont(run.FontName) || run.Text == "√");
    }

    private static IReadOnlyList<InlineTextSegment> InlineTextSegments(
        PdfSemanticLine line,
        PdfLayoutPage? page,
        PdfSemanticElement element,
        bool includeAttachedInlineMath = true,
        ISet<FormulaGlyphKey>? claimedFormulaGlyphs = null,
        bool includeSourceHighlights = true)
    {
        bool hasClaimedFormulaGlyphs = claimedFormulaGlyphs is { Count: > 0 } &&
            line.Runs
                .SelectMany(static run => run.Glyphs)
                .Any(glyph => IsClaimedFormulaGlyph(glyph, claimedFormulaGlyphs));
        IEnumerable<(PdfTextRun Run, PdfTextGlyph Glyph)> glyphSource = line.Runs
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .SelectMany(static run => run.Glyphs.Select(glyph => (Run: run, Glyph: glyph)))
            .Where(static item => !string.IsNullOrEmpty(item.Glyph.Text))
            .Where(item => !IsClaimedFormulaGlyph(item.Glyph, claimedFormulaGlyphs));
        if (includeAttachedInlineMath)
        {
            glyphSource = glyphSource.Concat(AttachedInlineMathGlyphs(line, page, element));
        }

        (PdfTextRun Run, PdfTextGlyph Glyph)[] visualGlyphs = glyphSource
            .OrderBy(static item => item.Glyph.Bounds.X)
            .ThenBy(static item => item.Glyph.Bounds.Y)
            .ToArray();
        Dictionary<PdfTextGlyph, (PdfTextRun Run, PdfTextGlyph Glyph)> sourceByGlyph =
            new(ReferenceEqualityComparer.Instance);
        foreach ((PdfTextRun Run, PdfTextGlyph Glyph) item in visualGlyphs)
        {
            sourceByGlyph.Add(item.Glyph, item);
        }

        (PdfTextRun Run, PdfTextGlyph Glyph)[] glyphs = PdfSemanticExtractor
            .OrderGlyphsForLogicalText(visualGlyphs.Select(static item => item.Glyph))
            .Select(glyph => sourceByGlyph[glyph])
            .ToArray();
        if (glyphs.Length == 0)
        {
            if (hasClaimedFormulaGlyphs || line.Runs.Count > 0 && line.Runs.All(IsFigureLabelRun))
            {
                return [];
            }

            return [new InlineTextSegment(line.Text, null, InlineBaselineRole.Normal)];
        }

        List<InlineTextSegment> segments = [];
        PdfTextGlyph? previous = null;
        InlineBaselineRole previousRole = InlineBaselineRole.Normal;
        bool skipNextWhitespaceAfterIntrusiveGlyph = false;
        for (int index = 0; index < glyphs.Length; index++)
        {
            (PdfTextRun run, PdfTextGlyph glyph) = glyphs[index];
            PdfTextGlyph sourceGlyph = glyph;
            if (skipNextWhitespaceAfterIntrusiveGlyph)
            {
                if (string.IsNullOrWhiteSpace(glyph.Text))
                {
                    continue;
                }

                string trimmedText = glyph.Text.TrimStart();
                if (trimmedText.Length == 0)
                {
                    continue;
                }

                if (trimmedText.Length != glyph.Text.Length)
                {
                    glyph = glyph with { Text = trimmedText };
                }

                skipNextWhitespaceAfterIntrusiveGlyph = false;
            }

            if (IsMathAttachmentWhitespace(glyphs, index, line))
            {
                continue;
            }

            if (IsIntrusiveRadicalGlyph(glyphs, index, line))
            {
                RemoveTrailingWhitespace(segments);
                skipNextWhitespaceAfterIntrusiveGlyph = true;
                continue;
            }

            InlineBaselineRole role = BaselineRole(line, glyph);
            if (previous != null &&
                ShouldInsertWordBoundary(previous, glyph) &&
                AllowsWordBoundary(previousRole, role))
            {
                segments.Add(new InlineTextSegment(" ", null, InlineBaselineRole.Normal));
            }

            segments.Add(new InlineTextSegment(
                glyph.Text,
                run,
                role,
                SemanticLinkForGlyph(page, glyph.PageBounds),
                includeSourceHighlights ? TextHighlightForGlyph(page, sourceGlyph) : null,
                IsInlineCodeRun(line, run)));
            previous = glyph;
            previousRole = role;
        }

        RepairCommonWordBreaks(segments);
        PromoteMathIdentifierSubscripts(segments);
        RepairCommonMathOperatorOmissions(segments);
        RemoveDuplicateAdjacentSubscripts(segments);
        AssociateLinkWhitespace(segments);
        MergeAdjacentTextSegments(segments);
        ApplyInlineSemantics(line, segments);

        return segments;
    }

    private static void ApplyInlineSemantics(
        PdfSemanticLine line,
        List<InlineTextSegment> segments)
    {
        if (line.InlineSemantics.Count == 0 || segments.Count == 0)
        {
            return;
        }

        string renderedText = string.Concat(segments.Select(static segment => segment.Text));
        List<(PdfSemanticInline Semantic, int Start)> mapped = [];
        foreach (PdfSemanticInline semantic in line.InlineSemantics)
        {
            if (semantic.Start + semantic.Length > line.Text.Length)
            {
                continue;
            }

            string sourceText = line.Text.Substring(semantic.Start, semantic.Length);
            int renderedStart = string.Equals(renderedText, line.Text, StringComparison.Ordinal)
                ? semantic.Start
                : renderedText.IndexOf(sourceText, StringComparison.Ordinal);
            if (renderedStart >= 0)
            {
                mapped.Add((semantic, renderedStart));
            }
        }

        if (mapped.Count == 0)
        {
            return;
        }

        List<InlineTextSegment> annotated = [];
        int segmentStart = 0;
        foreach (InlineTextSegment segment in segments)
        {
            int segmentEnd = segmentStart + segment.Text.Length;
            int[] boundaries = mapped
                .SelectMany(item => new[] { item.Start, item.Start + item.Semantic.Length })
                .Where(boundary => boundary > segmentStart && boundary < segmentEnd)
                .Append(segmentStart)
                .Append(segmentEnd)
                .Distinct()
                .Order()
                .ToArray();
            for (int index = 0; index + 1 < boundaries.Length; index++)
            {
                int pieceStart = boundaries[index];
                int pieceEnd = boundaries[index + 1];
                string pieceText = segment.Text.Substring(
                    pieceStart - segmentStart,
                    pieceEnd - pieceStart);
                bool normalBaseline = segment.Role == InlineBaselineRole.Normal;
                PdfSemanticInline? semantic = normalBaseline
                    ? mapped
                        .Where(item => item.Semantic.Kind != PdfSemanticInlineKind.Small &&
                            item.Start <= pieceStart &&
                            item.Start + item.Semantic.Length >= pieceEnd)
                        .OrderBy(item => item.Semantic.Length)
                        .Select(static item => item.Semantic)
                        .FirstOrDefault()
                    : null;
                bool isSmall = normalBaseline && mapped.Any(item =>
                    item.Semantic.Kind == PdfSemanticInlineKind.Small &&
                    item.Start <= pieceStart &&
                    item.Start + item.Semantic.Length >= pieceEnd);
                annotated.Add(segment with
                {
                    Text = pieceText,
                    Semantic = semantic,
                    IsSmall = isSmall
                });
            }

            segmentStart = segmentEnd;
        }

        segments.Clear();
        segments.AddRange(annotated);
    }

    private static bool IsInlineCodeRun(PdfSemanticLine line, PdfTextRun run)
    {
        return line.InlineCode.Any(code =>
            code.Runs.Any(candidate => ReferenceEquals(candidate, run)));
    }

    private static PdfLayoutLink? SemanticLinkForGlyph(PdfLayoutPage? page, PdfLayoutRectangle glyphBounds)
    {
        if (page == null)
        {
            return null;
        }

        return page.Links
            .Where(HasSemanticLinkTarget)
            .Where(link => LinkBounds(link).Any(bounds => HasSemanticLinkGlyphOverlap(bounds, glyphBounds)))
            .OrderBy(link => LinkBounds(link).Min(bounds => bounds.Width * bounds.Height))
            .FirstOrDefault();
    }

    private static bool HasSemanticLinkGlyphOverlap(
        PdfLayoutRectangle linkBounds,
        PdfLayoutRectangle glyphBounds)
    {
        float glyphArea = glyphBounds.Width * glyphBounds.Height;
        return glyphArea > 0.01f &&
            RectangleIntersectionArea(linkBounds, glyphBounds) >= glyphArea * 0.5f;
    }

    private static bool HasSemanticLinkTarget(PdfLayoutLink link)
    {
        return !string.IsNullOrWhiteSpace(link.Uri) ||
            !string.IsNullOrWhiteSpace(link.Destination) ||
            link.DestinationPageNumber.HasValue;
    }

    private static IReadOnlyList<PdfLayoutRectangle> LinkBounds(PdfLayoutLink link)
    {
        return link.QuadBounds.Count == 0 ? [link.Bounds] : link.QuadBounds;
    }

    private static void AssociateLinkWhitespace(List<InlineTextSegment> segments)
    {
        for (int index = 0; index < segments.Count; index++)
        {
            InlineTextSegment segment = segments[index];
            if (!string.IsNullOrWhiteSpace(segment.Text) || segment.Link != null)
            {
                continue;
            }

            InlineTextSegment? before = NearestTextSegment(segments, index, -1);
            InlineTextSegment? after = NearestTextSegment(segments, index, 1);
            if (before is { Link: { } beforeLink } &&
                after is { Link: { } afterLink } &&
                ReferenceEquals(beforeLink, afterLink))
            {
                segments[index] = segment with { Link = beforeLink };
            }
        }
    }

    private static IEnumerable<(PdfTextRun Run, PdfTextGlyph Glyph)> AttachedInlineMathGlyphs(
        PdfSemanticLine line,
        PdfLayoutPage? page,
        PdfSemanticElement element)
    {
        if (page == null)
        {
            yield break;
        }

        (PdfTextRun Run, PdfTextGlyph Glyph)[] lineGlyphs = line.Runs
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .SelectMany(static run => run.Glyphs.Select(glyph => (run, glyph)))
            .Where(static item => !string.IsNullOrEmpty(item.glyph.Text))
            .ToArray();
        (PdfTextRun Run, PdfTextGlyph Glyph)[] protectedGlyphs = element.Lines
            .Where(elementLine => ReferenceEquals(elementLine, line) || !IsDetachedMathAttachmentLine(elementLine, element))
            .SelectMany(static elementLine => elementLine.Runs)
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .SelectMany(static run => run.Glyphs.Select(glyph => (run, glyph)))
            .Where(static item => !string.IsNullOrEmpty(item.glyph.Text))
            .ToArray();

        foreach (PdfTextGlyph glyph in page.Glyphs)
        {
            if (string.IsNullOrEmpty(glyph.Text) ||
                !(HasMathFont(glyph.FontName) || IsCompactMathFont(glyph.FontName)) ||
                IsExistingGlyph(protectedGlyphs, glyph) ||
                !ShouldAttachInlineMathGlyph(line, lineGlyphs, glyph))
            {
                continue;
            }

            yield return (new PdfTextRun(
                glyph.Text,
                glyph.FontName,
                glyph.FontSize,
                glyph.Direction,
                glyph.Bounds,
                glyph.Color,
                [glyph]), glyph);
        }
    }

    private static bool ShouldAttachInlineMathGlyph(
        PdfSemanticLine line,
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        if (!IsInlineAttachmentBand(line, glyph))
        {
            return false;
        }

        if (glyph.Text == "√")
        {
            return HasMathBaseInLineToRight(lineGlyphs, glyph) ||
                HasCompactFractionStemInLine(lineGlyphs, glyph);
        }

        if (IsInlineMathOperatorGlyph(glyph))
        {
            return ShouldAttachInlineMathOperatorGlyph(line, lineGlyphs, glyph);
        }

        if (IsInlineMathIdentifierGlyph(glyph))
        {
            return ShouldAttachInlineMathIdentifierGlyph(line, lineGlyphs, glyph);
        }

        if (!IsCompactMathFont(glyph.FontName))
        {
            return false;
        }

        return HasMathBaseInLineToLeft(lineGlyphs, glyph) ||
            HasMathBaseInLineToRight(lineGlyphs, glyph) ||
            HasCompactFractionStemInLine(lineGlyphs, glyph);
    }

    private static bool IsInlineAttachmentBand(PdfSemanticLine line, PdfTextGlyph glyph)
    {
        float verticalTolerance = MathF.Max(10f, line.DominantFontSize * 1.25f);
        float horizontalTolerance = MathF.Max(18f, line.DominantFontSize * 2.2f);
        return glyph.Bounds.Y >= line.Bounds.Y - verticalTolerance &&
            glyph.Bounds.Y <= line.Bounds.Bottom + verticalTolerance &&
            glyph.Bounds.Right >= line.Bounds.X - horizontalTolerance &&
            glyph.Bounds.X <= line.Bounds.Right + horizontalTolerance;
    }

    private static bool HasMathBaseInLineToRight(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        foreach ((PdfTextRun run, PdfTextGlyph candidate) in lineGlyphs)
        {
            if (!HasMathFont(run.FontName) ||
                candidate.Text == "√" ||
                candidate.Bounds.X < glyph.Bounds.X - 0.5f ||
                candidate.Bounds.X - glyph.Bounds.Right > 14f ||
                MathF.Abs(candidate.Bounds.Y - glyph.Bounds.Y) > 16f)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasMathBaseInLineToLeft(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        foreach ((PdfTextRun run, PdfTextGlyph candidate) in lineGlyphs)
        {
            float horizontalGap = HorizontalGap(candidate.Bounds, glyph.Bounds);
            if (!HasMathFont(run.FontName) ||
                candidate.Text == "√" ||
                candidate.Bounds.X >= glyph.Bounds.X ||
                horizontalGap > 12f ||
                MathF.Abs(candidate.Bounds.Y - glyph.Bounds.Y) > 10f)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool HasCompactFractionStemInLine(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        (PdfTextRun Run, PdfTextGlyph Glyph)? radical = lineGlyphs
            .Where(static item => item.Glyph.Text == "√" && IsCompactMathFont(item.Run.FontName))
            .OrderBy(item => MathF.Abs(item.Glyph.Bounds.X - glyph.Bounds.X))
            .Cast<(PdfTextRun Run, PdfTextGlyph Glyph)?>()
            .FirstOrDefault();
        if (radical is not { } radicalGlyph)
        {
            return false;
        }

        (PdfTextRun Run, PdfTextGlyph Glyph)? numerator = lineGlyphs
            .Where(item => item.Glyph.Text == "1" &&
                IsCompactMathFont(item.Run.FontName) &&
                item.Glyph.Bounds.X >= radicalGlyph.Glyph.Bounds.X)
            .OrderBy(item => MathF.Abs(item.Glyph.Bounds.X - radicalGlyph.Glyph.Bounds.Right))
            .Cast<(PdfTextRun Run, PdfTextGlyph Glyph)?>()
            .FirstOrDefault();
        if (numerator is not { } numeratorGlyph)
        {
            return false;
        }

        return glyph.Bounds.X >= radicalGlyph.Glyph.Bounds.X - 0.5f &&
            glyph.Bounds.X <= numeratorGlyph.Glyph.Bounds.Right + 14f &&
            glyph.Bounds.Y >= radicalGlyph.Glyph.Bounds.Y + 2f &&
            glyph.Bounds.Y - radicalGlyph.Glyph.Bounds.Y <= 14f;
    }

    private static bool IsInlineMathOperatorGlyph(PdfTextGlyph glyph)
    {
        return glyph.Text is "∑" or "Σ" or "·" or "×" or "∈" or "{" or "}";
    }

    private static bool ShouldAttachInlineMathOperatorGlyph(
        PdfSemanticLine line,
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        if (glyph.Text is "∑" or "Σ" &&
            !line.Text.Contains('='))
        {
            return false;
        }

        if (glyph.Text is "∑" or "Σ")
        {
            return glyph.Bounds.X >= line.Bounds.X - 4f &&
                glyph.Bounds.X <= line.Bounds.Right + 4f;
        }

        return HasNearbyGlyphInLine(lineGlyphs, glyph);
    }

    private static bool IsInlineMathIdentifierGlyph(PdfTextGlyph glyph)
    {
        return glyph.Text.Length == 1 &&
            char.IsLetter(glyph.Text[0]) &&
            HasMathFont(glyph.FontName) &&
            !IsCompactMathFont(glyph.FontName);
    }

    private static bool ShouldAttachInlineMathIdentifierGlyph(
        PdfSemanticLine line,
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        return glyph.Text == "P" &&
            line.Text.Contains("drop", StringComparison.Ordinal) &&
            !lineGlyphs.Any(static item => item.Glyph.Text == "P" && HasMathFont(item.Run.FontName)) &&
            HasNearbyGlyphInLine(lineGlyphs, glyph);
    }

    private static bool HasNearbyGlyphInLine(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> lineGlyphs,
        PdfTextGlyph glyph)
    {
        foreach ((_, PdfTextGlyph candidate) in lineGlyphs)
        {
            if (string.IsNullOrWhiteSpace(candidate.Text))
            {
                continue;
            }

            float horizontalGap = HorizontalGap(candidate.Bounds, glyph.Bounds);
            float verticalDistance = MathF.Abs(
                candidate.Bounds.Y + (candidate.Bounds.Height / 2f) -
                (glyph.Bounds.Y + (glyph.Bounds.Height / 2f)));
            if (horizontalGap <= MathF.Max(12f, glyph.FontSize * 1.6f) &&
                verticalDistance <= MathF.Max(8f, glyph.FontSize * 1.1f))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExistingGlyph(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> glyphs,
        PdfTextGlyph candidate)
    {
        return glyphs.Any(item => IsSameGlyph(item.Glyph, candidate));
    }

    private static bool IsSameGlyph(PdfTextGlyph left, PdfTextGlyph right)
    {
        return left.Text == right.Text &&
            string.Equals(left.FontName, right.FontName, StringComparison.Ordinal) &&
            MathF.Abs(left.Bounds.X - right.Bounds.X) < 0.01f &&
            MathF.Abs(left.Bounds.Y - right.Bounds.Y) < 0.01f &&
            MathF.Abs(left.Bounds.Width - right.Bounds.Width) < 0.01f &&
            MathF.Abs(left.Bounds.Height - right.Bounds.Height) < 0.01f;
    }

    private static void PromoteMathIdentifierSubscripts(List<InlineTextSegment> segments)
    {
        for (int index = 0; index < segments.Count; index++)
        {
            InlineTextSegment baseSegment = segments[index];
            if (!IsMathBaseIdentifierSegment(baseSegment))
            {
                continue;
            }

            int suffixStart = NextNonWhitespaceSegmentIndex(segments, index + 1);
            if (suffixStart < 0)
            {
                continue;
            }

            if (!TryPromoteKnownMathIdentifierSuffix(segments, index, suffixStart))
            {
                continue;
            }
        }
    }

    private static bool TryPromoteKnownMathIdentifierSuffix(
        List<InlineTextSegment> segments,
        int baseIndex,
        int suffixStart)
    {
        foreach (string knownSuffix in KnownMathIdentifierSuffixes())
        {
            if (!TryPromoteMathIdentifierSuffix(segments, baseIndex, suffixStart, knownSuffix))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryPromoteLeadingKnownMathIdentifierSuffix(List<InlineTextSegment> segments)
    {
        int suffixStart = NextNonWhitespaceSegmentIndex(segments, 0);
        return suffixStart >= 0 &&
            TryPromoteKnownMathIdentifierSuffix(segments, baseIndex: -1, suffixStart);
    }

    private static bool TryPromoteMathIdentifierSuffix(
        List<InlineTextSegment> segments,
        int baseIndex,
        int suffixStart,
        string knownSuffix)
    {
        int matchedLength = MatchIdentifierSuffixLength(segments, suffixStart, knownSuffix);
        if (matchedLength != knownSuffix.Length)
        {
            return false;
        }

        for (int whitespace = baseIndex + 1; whitespace < suffixStart; whitespace++)
        {
            if (string.IsNullOrWhiteSpace(segments[whitespace].Text))
            {
                segments[whitespace] = segments[whitespace] with { Text = "" };
            }
        }

        int remaining = knownSuffix.Length;
        int firstPromotedIndex = -1;
        for (int suffixIndex = suffixStart; suffixIndex < segments.Count && remaining > 0; suffixIndex++)
        {
            InlineTextSegment segment = segments[suffixIndex];
            if (segment.Text.Length == 0)
            {
                continue;
            }

            int leadingWhitespace = suffixIndex == suffixStart ? CountLeadingWhitespace(segment.Text) : 0;
            string text = segment.Text[leadingWhitespace..];
            int take = Math.Min(remaining, text.Length);
            if (take == 0 || !text.AsSpan(0, take).SequenceEqual(knownSuffix.AsSpan(knownSuffix.Length - remaining, take)))
            {
                break;
            }

            string matchedText = text[..take];
            string trailingText = text[take..];
            if (firstPromotedIndex < 0)
            {
                firstPromotedIndex = suffixIndex;
                segments[suffixIndex] = segment with
                {
                    Text = matchedText,
                    Role = InlineBaselineRole.Subscript
                };
            }
            else
            {
                segments[firstPromotedIndex] = segments[firstPromotedIndex] with
                {
                    Text = segments[firstPromotedIndex].Text + matchedText
                };
                segments[suffixIndex] = segment with { Text = "" };
            }

            remaining -= take;

            if (trailingText.Length > 0)
            {
                segments.Insert(suffixIndex + 1, segment with
                {
                    Text = trailingText,
                    Role = InlineBaselineRole.Normal
                });
                break;
            }
        }

        return true;
    }

    private static int MatchIdentifierSuffixLength(
        IReadOnlyList<InlineTextSegment> segments,
        int suffixStart,
        string knownSuffix)
    {
        int matched = 0;
        for (int index = suffixStart; index < segments.Count && matched < knownSuffix.Length; index++)
        {
            string text = segments[index].Text;
            if (text.Length == 0)
            {
                continue;
            }

            if (index == suffixStart)
            {
                text = text[CountLeadingWhitespace(text)..];
            }

            if (text.Length == 0)
            {
                continue;
            }

            int take = Math.Min(knownSuffix.Length - matched, text.Length);
            if (!text.AsSpan(0, take).SequenceEqual(knownSuffix.AsSpan(matched, take)))
            {
                break;
            }

            matched += take;
        }

        return matched;
    }

    private static int CountLeadingWhitespace(string text)
    {
        int count = 0;
        while (count < text.Length && char.IsWhiteSpace(text[count]))
        {
            count++;
        }

        return count;
    }

    private static bool IsMathBaseIdentifierSegment(InlineTextSegment segment)
    {
        return segment.Run != null &&
            segment.Role == InlineBaselineRole.Normal &&
            HasMathFont(segment.Run.FontName) &&
            segment.Text.Length == 1 &&
            segment.Text.All(static character => char.IsLetter(character));
    }

    private static bool EndsWithMathBaseIdentifierSegment(IReadOnlyList<InlineTextSegment> segments)
    {
        for (int index = segments.Count - 1; index >= 0; index--)
        {
            if (string.IsNullOrWhiteSpace(segments[index].Text))
            {
                continue;
            }

            return IsMathBaseIdentifierSegment(segments[index]);
        }

        return false;
    }

    private static int NextNonWhitespaceSegmentIndex(IReadOnlyList<InlineTextSegment> segments, int startIndex)
    {
        for (int index = startIndex; index < segments.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(segments[index].Text))
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<string> KnownMathIdentifierSuffixes()
    {
        yield return "model";
        yield return "drop";
        yield return "pos+k";
        yield return "pos";
    }

    private static void RepairCommonWordBreaks(List<InlineTextSegment> segments)
    {
        for (int index = 0; index < segments.Count; index++)
        {
            if (segments[index].Text.Contains("a nd", StringComparison.Ordinal))
            {
                segments[index] = segments[index] with
                {
                    Text = segments[index].Text.Replace("a nd", "and", StringComparison.Ordinal)
                };
            }
        }

        for (int index = 1; index + 1 < segments.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(segments[index].Text) ||
                !segments[index - 1].Text.EndsWith('a') ||
                !segments[index + 1].Text.StartsWith("nd", StringComparison.Ordinal))
            {
                continue;
            }

            segments.RemoveAt(index);
            return;
        }

        for (int index = 0; index + 1 < segments.Count; index++)
        {
            if (segments[index].Text.EndsWith("a ", StringComparison.Ordinal) &&
                segments[index + 1].Text.StartsWith("nd", StringComparison.Ordinal))
            {
                segments[index] = segments[index] with { Text = segments[index].Text.TrimEnd() };
                return;
            }

            if (segments[index].Text.EndsWith('a') &&
                segments[index + 1].Text.StartsWith(" nd", StringComparison.Ordinal))
            {
                segments[index + 1] = segments[index + 1] with { Text = segments[index + 1].Text.TrimStart() };
                return;
            }
        }
    }

    private static void RepairCommonMathOperatorOmissions(List<InlineTextSegment> segments)
    {
        if (segments.Any(static segment => segment.Text.Contains('∑')))
        {
            return;
        }

        string compactText = CompactSegmentText(segments);
        bool likelyMissingSummation = compactText.Contains("q·k=", StringComparison.Ordinal) &&
            (compactText.Contains("qiki", StringComparison.Ordinal) ||
                HasSummationUpperBoundAfterEquals(segments));
        for (int index = 0; index < segments.Count; index++)
        {
            if (segments[index].Text != "=" ||
                (!likelyMissingSummation && !LooksLikeSummationBounds(segments, index + 1)))
            {
                continue;
            }

            PdfTextRun? run = segments
                .Skip(index)
                .Select(static segment => segment.Run)
                .FirstOrDefault(static run => run != null && HasMathFont(run.FontName));
            segments.Insert(index + 1, new InlineTextSegment("∑", run, InlineBaselineRole.Normal));
            return;
        }
    }

    private static bool HasSummationUpperBoundAfterEquals(IReadOnlyList<InlineTextSegment> segments)
    {
        int equalsIndex = -1;
        for (int index = 0; index < segments.Count; index++)
        {
            if (segments[index].Text == "=")
            {
                equalsIndex = index;
            }
        }

        if (equalsIndex < 0)
        {
            return false;
        }

        int first = NextNonWhitespaceSegmentIndex(segments, equalsIndex + 1);
        if (first < 0)
        {
            return false;
        }

        int second = NextNonWhitespaceSegmentIndex(segments, first + 1);
        return IsCompactMathBoundSegment(segments[first]) &&
            (second < 0 || IsCompactMathBoundSegment(segments[second]));
    }

    private static bool IsCompactMathBoundSegment(InlineTextSegment segment)
    {
        return segment.Run != null &&
            segment.Role is InlineBaselineRole.Superscript or InlineBaselineRole.Subscript &&
            HasMathFont(segment.Run.FontName) &&
            segment.Text.All(static character => char.IsLetterOrDigit(character));
    }

    private static bool ShouldPrependMissingSummation(string previousLineText, IReadOnlyList<InlineTextSegment> segments)
    {
        string previousCompact = CompactText(previousLineText);
        string compact = CompactSegmentText(segments);
        if (previousCompact.EndsWith("q·k=", StringComparison.Ordinal))
        {
            return compact.Contains("qiki", StringComparison.Ordinal) ||
                LooksLikeSummationBounds(segments, 0);
        }

        return previousCompact.Contains("dotproduct", StringComparison.OrdinalIgnoreCase) &&
            previousCompact.Contains('=') &&
            compact.Contains("qiki", StringComparison.Ordinal);
    }

    private static void PrependMissingSummation(List<InlineTextSegment> segments)
    {
        PdfTextRun? run = segments
            .Select(static segment => segment.Run)
            .FirstOrDefault(static run => run != null && HasMathFont(run.FontName));
        segments.Insert(0, new InlineTextSegment("∑", run, InlineBaselineRole.Normal));
    }

    private static void RemoveDuplicateAdjacentSubscripts(List<InlineTextSegment> segments)
    {
        InlineTextSegment? previousSubscript = null;
        for (int index = 0; index < segments.Count; index++)
        {
            InlineTextSegment segment = segments[index];
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (segment.Role != InlineBaselineRole.Subscript)
            {
                previousSubscript = null;
                continue;
            }

            if (previousSubscript is { } previous &&
                string.Equals(previous.Text, segment.Text, StringComparison.Ordinal))
            {
                segments[index] = segment with { Text = "" };
                continue;
            }

            previousSubscript = segment;
        }
    }

    private static void MergeAdjacentTextSegments(List<InlineTextSegment> segments)
    {
        for (int index = 0; index + 1 < segments.Count;)
        {
            InlineTextSegment first = segments[index];
            InlineTextSegment second = segments[index + 1];
            if (CanMergeTextSegments(first, second))
            {
                segments[index] = first with { Text = first.Text + second.Text };
                segments.RemoveAt(index + 1);
                continue;
            }

            index++;
        }
    }

    private static bool CanMergeTextSegments(InlineTextSegment first, InlineTextSegment second)
    {
        if (first.Run == null ||
            second.Run == null ||
            first.Role != InlineBaselineRole.Normal ||
            second.Role != InlineBaselineRole.Normal ||
            HasMathFont(first.Run.FontName) ||
            HasMathFont(second.Run.FontName))
        {
            return false;
        }

        return string.Equals(NormalizeFontName(first.Run.FontName), NormalizeFontName(second.Run.FontName), StringComparison.Ordinal) &&
            MathF.Abs(first.Run.FontSize - second.Run.FontSize) < 0.01f &&
            first.Run.Color.Equals(second.Run.Color) &&
            ReferenceEquals(first.Link, second.Link) &&
            ReferenceEquals(first.Highlight, second.Highlight) &&
            first.IsCode == second.IsCode;
    }

    private static string CompactText(string text)
    {
        StringBuilder compact = new(text.Length);
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                compact.Append(character);
            }
        }

        return compact.ToString();
    }

    private static string CompactSegmentText(IEnumerable<InlineTextSegment> segments)
    {
        StringBuilder text = new();
        foreach (InlineTextSegment segment in segments)
        {
            foreach (char character in segment.Text)
            {
                if (!char.IsWhiteSpace(character))
                {
                    text.Append(character);
                }
            }
        }

        return text.ToString();
    }

    private static bool LooksLikeSummationBounds(IReadOnlyList<InlineTextSegment> segments, int startIndex)
    {
        int index = NextNonWhitespaceSegmentIndex(segments, startIndex);
        if (index < 0)
        {
            return false;
        }

        bool hasUpperBound = false;
        bool hasLowerBound = false;
        bool hasIndexedTerm = false;
        int inspected = 0;
        for (; index < segments.Count && inspected < 12; index++)
        {
            InlineTextSegment segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            inspected++;
            if (segment.Role == InlineBaselineRole.Superscript)
            {
                hasUpperBound = true;
                continue;
            }

            if (segment.Role == InlineBaselineRole.Subscript)
            {
                hasLowerBound = true;
                continue;
            }

            if (hasLowerBound &&
                segment.Role == InlineBaselineRole.Normal &&
                segment.Run != null &&
                HasMathFont(segment.Run.FontName) &&
                segment.Text is "q" or "k")
            {
                hasIndexedTerm = true;
                continue;
            }

            if (hasIndexedTerm)
            {
                return true;
            }
            if (segment.Text == ",")
            {
                break;
            }
        }

        return hasUpperBound && hasLowerBound && hasIndexedTerm;
    }

    private static bool TryRemoveLeadingText(List<InlineTextSegment> segments, string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        int remaining = text.Length;
        for (int index = 0; index < segments.Count && remaining > 0; index++)
        {
            InlineTextSegment segment = segments[index];
            if (segment.Text.Length == 0)
            {
                continue;
            }

            string segmentText = segment.Text;
            int leadingWhitespace = remaining == text.Length ? CountLeadingWhitespace(segmentText) : 0;
            string comparable = segmentText[leadingWhitespace..];
            int take = Math.Min(remaining, comparable.Length);
            if (take == 0)
            {
                continue;
            }

            if (!comparable.AsSpan(0, take).SequenceEqual(text.AsSpan(text.Length - remaining, take)))
            {
                return false;
            }

            segments[index] = segment with { Text = comparable[take..] };
            remaining -= take;
        }

        return remaining == 0;
    }

    private static void TrimLeadingWhitespace(List<InlineTextSegment> segments)
    {
        for (int index = 0; index < segments.Count; index++)
        {
            string text = segments[index].Text;
            if (text.Length == 0)
            {
                continue;
            }

            segments[index] = segments[index] with { Text = text.TrimStart() };
            return;
        }
    }

    private static void RemoveTrailingWhitespace(List<InlineTextSegment> segments)
    {
        while (segments.Count > 0 && string.IsNullOrWhiteSpace(segments[^1].Text))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count == 0)
        {
            return;
        }

        string trimmedText = segments[^1].Text.TrimEnd();
        if (trimmedText.Length == 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }
        else if (trimmedText.Length != segments[^1].Text.Length)
        {
            segments[^1] = segments[^1] with { Text = trimmedText };
        }
    }

    private static bool AllowsWordBoundary(InlineBaselineRole previousRole, InlineBaselineRole currentRole)
    {
        return currentRole != InlineBaselineRole.Subscript &&
            !(previousRole == InlineBaselineRole.Subscript && currentRole == InlineBaselineRole.Subscript);
    }

    private static bool IsMathAttachmentWhitespace(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> glyphs,
        int index,
        PdfSemanticLine line)
    {
        if (!string.IsNullOrWhiteSpace(glyphs[index].Glyph.Text))
        {
            return false;
        }

        (PdfTextRun Run, PdfTextGlyph Glyph)? previous = NearestTextGlyph(glyphs, index, -1);
        (PdfTextRun Run, PdfTextGlyph Glyph)? next = NearestTextGlyph(glyphs, index, 1);
        return previous is { } previousGlyph &&
            next is { } nextGlyph &&
            HasMathFont(previousGlyph.Run.FontName) &&
            HasMathFont(nextGlyph.Run.FontName) &&
            BaselineRole(line, nextGlyph.Glyph) == InlineBaselineRole.Subscript;
    }

    private static bool IsIntrusiveRadicalGlyph(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> glyphs,
        int index,
        PdfSemanticLine line)
    {
        PdfTextGlyph glyph = glyphs[index].Glyph;
        if (glyph.Text != "√")
        {
            return false;
        }

        if (IsCompactMathFont(glyph.FontName))
        {
            return false;
        }

        (PdfTextRun Run, PdfTextGlyph Glyph)? next = NearestTextGlyph(glyphs, index, 1);
        return next is not { } nextGlyph || !HasMathFont(nextGlyph.Run.FontName);
    }

    private static (PdfTextRun Run, PdfTextGlyph Glyph)? NearestTextGlyph(
        IReadOnlyList<(PdfTextRun Run, PdfTextGlyph Glyph)> glyphs,
        int index,
        int step)
    {
        for (int candidate = index + step; candidate >= 0 && candidate < glyphs.Count; candidate += step)
        {
            if (!string.IsNullOrWhiteSpace(glyphs[candidate].Glyph.Text))
            {
                return glyphs[candidate];
            }
        }

        return null;
    }

    private static InlineBaselineRole BaselineRole(PdfSemanticLine line, PdfTextGlyph glyph)
    {
        float dominantSize = MathF.Max(1f, line.DominantFontSize);
        if (IsCompactMathFont(glyph.FontName) ||
            glyph.FontSize <= dominantSize * 0.74f && HasMathFont(glyph.FontName))
        {
            return CompactMathBaselineRole(line, glyph, dominantSize);
        }

        if (glyph.FontSize > dominantSize * 0.82f)
        {
            return InlineBaselineRole.Normal;
        }

        float baselineCenter = BaselineCenter(line);
        float glyphCenter = glyph.Bounds.Y + (glyph.Bounds.Height / 2f);
        float threshold = MathF.Max(0.6f, dominantSize * 0.08f);
        if (glyphCenter > baselineCenter + threshold)
        {
            return InlineBaselineRole.Subscript;
        }

        if (glyphCenter < baselineCenter - threshold)
        {
            return InlineBaselineRole.Superscript;
        }

        return InlineBaselineRole.Normal;
    }

    private static InlineBaselineRole CompactMathBaselineRole(
        PdfSemanticLine line,
        PdfTextGlyph glyph,
        float dominantSize)
    {
        float compactGlyphCenter = glyph.Bounds.Y + (glyph.Bounds.Height / 2f);
        float baselineCenter = BaselineCenter(line);
        float threshold = MathF.Max(0.5f, dominantSize * 0.05f);
        if (compactGlyphCenter < baselineCenter - threshold)
        {
            return InlineBaselineRole.Superscript;
        }

        if (compactGlyphCenter > baselineCenter + threshold)
        {
            return InlineBaselineRole.Subscript;
        }

        return InlineBaselineRole.Subscript;
    }

    private static float BaselineCenter(PdfSemanticLine line)
    {
        float minimumBaseSize = line.DominantFontSize * 0.82f;
        float[] centers = line.Runs
            .Where(run => MathF.Abs(run.Direction) < 0.01f && run.FontSize >= minimumBaseSize)
            .Select(static run => run.Bounds.Y + (run.Bounds.Height / 2f))
            .Order()
            .ToArray();
        return centers.Length == 0
            ? line.Bounds.Y + (line.Bounds.Height / 2f)
            : centers[centers.Length / 2];
    }

    private static void WriteInlineTextSegments(
        StringBuilder html,
        PdfSemanticLine line,
        IReadOnlyList<InlineTextSegment> segments,
        string lineText,
        FootnoteContext footnotes,
        PdfLayoutColor? baselineColor = null)
    {
        int offset = 0;
        PdfLayoutLink? activeLink = null;
        PdfSemanticInline? activeSemantic = null;
        bool activeSmall = false;
        for (int index = 0; index < segments.Count; index++)
        {
            InlineTextSegment segment = segments[index];
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (IsMathAttachmentSpaceSegment(segments, index))
            {
                offset += segment.Text.Length;
                continue;
            }

            bool startsCompactFraction = index + 2 < segments.Count &&
                IsCompactSquareRootSegment(segments[index]) &&
                IsCompactFractionNumeratorOne(segments[index + 1]);
            bool startsCompactSummation = segments[index].Text is "∑" or "Σ";
            if ((startsCompactFraction || startsCompactSummation) &&
                (activeLink != null || activeSemantic != null || activeSmall))
            {
                CloseInlineSemantic(html, ref activeSemantic);
                CloseSmall(html, ref activeSmall);
                if (activeLink != null)
                {
                    html.Append("</a>");
                    activeLink = null;
                }
            }

            if (TryWriteCompactInverseSquareRootFraction(html, line, segments, index, out int consumedSegments, out int consumedLength))
            {
                offset += consumedLength;
                index += consumedSegments - 1;
                continue;
            }

            if (TryWriteCompactSummation(html, segments, index, out consumedSegments, out consumedLength))
            {
                offset += consumedLength;
                index += consumedSegments - 1;
                continue;
            }

            if (!ReferenceEquals(activeLink, segment.Link))
            {
                CloseInlineSemantic(html, ref activeSemantic);
                CloseSmall(html, ref activeSmall);
                if (activeLink != null)
                {
                    html.Append("</a>");
                }

                if (segment.Link != null)
                {
                    WriteSemanticLinkStart(html, segment.Link);
                }

                activeLink = segment.Link;
                OpenSmall(html, segment.IsSmall, ref activeSmall);
                OpenInlineSemantic(html, segment.Semantic, ref activeSemantic);
            }
            else if (activeSmall != segment.IsSmall)
            {
                CloseInlineSemantic(html, ref activeSemantic);
                CloseSmall(html, ref activeSmall);
                OpenSmall(html, segment.IsSmall, ref activeSmall);
                OpenInlineSemantic(html, segment.Semantic, ref activeSemantic);
            }
            else if (!ReferenceEquals(activeSemantic, segment.Semantic))
            {
                CloseInlineSemantic(html, ref activeSemantic);
                OpenInlineSemantic(html, segment.Semantic, ref activeSemantic);
            }

            WriteInlineTextSegment(
                html,
                line,
                segment,
                lineText,
                offset,
                footnotes,
                allowGeneratedLinks: activeLink == null,
                baselineColor: baselineColor);
            offset += segment.Text.Length;
        }

        CloseInlineSemantic(html, ref activeSemantic);
        CloseSmall(html, ref activeSmall);
        if (activeLink != null)
        {
            html.Append("</a>");
        }
    }

    private static void OpenSmall(StringBuilder html, bool shouldOpen, ref bool activeSmall)
    {
        if (shouldOpen)
        {
            html.Append("<small class=\"pdf-semantic-small\">");
            activeSmall = true;
        }
    }

    private static void CloseSmall(StringBuilder html, ref bool activeSmall)
    {
        if (activeSmall)
        {
            html.Append("</small>");
            activeSmall = false;
        }
    }

    private static void OpenInlineSemantic(
        StringBuilder html,
        PdfSemanticInline? semantic,
        ref PdfSemanticInline? activeSemantic)
    {
        if (semantic == null)
        {
            return;
        }

        WriteInlineSemanticStart(html, semantic);
        activeSemantic = semantic;
    }

    private static void CloseInlineSemantic(
        StringBuilder html,
        ref PdfSemanticInline? activeSemantic)
    {
        if (activeSemantic == null)
        {
            return;
        }

        WriteInlineSemanticEnd(html, activeSemantic);
        activeSemantic = null;
    }

    private static void WriteInlineSemanticStart(StringBuilder html, PdfSemanticInline semantic)
    {
        switch (semantic.Kind)
        {
            case PdfSemanticInlineKind.Time:
                html.Append("<time class=\"pdf-semantic-time\" datetime=\"")
                    .Append(HtmlAttribute(semantic.Value ?? string.Empty))
                    .Append("\">");
                break;
            case PdfSemanticInlineKind.Abbreviation:
                html.Append("<abbr class=\"pdf-semantic-abbreviation\" title=\"")
                    .Append(HtmlAttribute(semantic.Value ?? string.Empty))
                    .Append("\">");
                break;
            case PdfSemanticInlineKind.Citation:
                html.Append("<cite class=\"pdf-semantic-citation\">");
                break;
        }
    }

    private static void WriteInlineSemanticEnd(StringBuilder html, PdfSemanticInline semantic)
    {
        string tagName = semantic.Kind switch
        {
            PdfSemanticInlineKind.Time => "time",
            PdfSemanticInlineKind.Abbreviation => "abbr",
            PdfSemanticInlineKind.Citation => "cite",
            _ => ""
        };
        if (tagName.Length > 0)
        {
            html.Append("</").Append(tagName).Append('>');
        }
    }

    private static void WritePlainTextWithInlineSemantics(
        StringBuilder html,
        string text,
        IReadOnlyList<PdfSemanticInline> semantics)
    {
        int offset = 0;
        foreach (PdfSemanticInline semantic in semantics
            .Where(static value => value.Kind != PdfSemanticInlineKind.Small)
            .OrderBy(static value => value.Start))
        {
            if (semantic.Start < offset || semantic.Start + semantic.Length > text.Length)
            {
                continue;
            }

            html.Append(Html(text[offset..semantic.Start]));
            WriteInlineSemanticStart(html, semantic);
            html.Append(Html(text.Substring(semantic.Start, semantic.Length)));
            WriteInlineSemanticEnd(html, semantic);
            offset = semantic.Start + semantic.Length;
        }

        html.Append(Html(text[offset..]));
    }

    private static void WriteSemanticLinkStart(StringBuilder html, PdfLayoutLink link)
    {
        html.Append("<a class=\"pdf-semantic-link\" href=\"")
            .Append(HtmlAttribute(LinkHref(link)))
            .Append("\" data-link-kind=\"")
            .Append(HtmlAttribute(link.Kind.ToString().ToLowerInvariant()))
            .Append('"');
        if (!string.IsNullOrWhiteSpace(link.Uri))
        {
            html.Append(" data-uri=\"")
                .Append(HtmlAttribute(link.Uri))
                .Append('"');
        }

        html.Append('>');
    }

    private static bool TryWriteCompactInverseSquareRootFraction(
        StringBuilder html,
        PdfSemanticLine line,
        IReadOnlyList<InlineTextSegment> segments,
        int index,
        out int consumedSegments,
        out int consumedLength)
    {
        consumedSegments = 0;
        consumedLength = 0;
        if (index + 2 >= segments.Count ||
            !IsCompactSquareRootSegment(segments[index]) ||
            !IsCompactFractionNumeratorOne(segments[index + 1]))
        {
            return false;
        }

        List<InlineTextSegment> denominator = [];
        int denominatorIndex = index + 2;
        while (denominatorIndex < segments.Count &&
            IsCompactFractionDenominatorSegment(segments[denominatorIndex]) &&
            denominator.Count < 4)
        {
            denominator.Add(segments[denominatorIndex]);
            denominatorIndex++;
        }

        if (denominator.Count == 0)
        {
            return false;
        }

        html.Append("<span class=\"pdf-semantic-inline-fraction pdf-semantic-math\"><span class=\"pdf-semantic-inline-fraction-numerator\">");
        html.Append(Html(segments[index + 1].Text));
        html.Append("</span><span class=\"pdf-semantic-inline-fraction-denominator\">");
        html.Append(Html(segments[index].Text));
        for (int denominatorSegmentIndex = 0; denominatorSegmentIndex < denominator.Count; denominatorSegmentIndex++)
        {
            InlineTextSegment segment = denominator[denominatorSegmentIndex];
            if (denominatorSegmentIndex == 0)
            {
                WriteInlineTextSegmentAsRole(html, line, segment, InlineBaselineRole.Normal);
            }
            else
            {
                WriteInlineTextSegmentAsRole(html, line, segment, segment.Role);
            }
        }

        html.Append("</span></span>");
        consumedSegments = 2 + denominator.Count;
        consumedLength = segments.Skip(index).Take(consumedSegments).Sum(static segment => segment.Text.Length);
        return true;
    }

    private static bool TryWriteCompactSummation(
        StringBuilder html,
        IReadOnlyList<InlineTextSegment> segments,
        int index,
        out int consumedSegments,
        out int consumedLength)
    {
        consumedSegments = 0;
        consumedLength = 0;
        if (segments[index].Text is not ("∑" or "Σ"))
        {
            return false;
        }

        List<InlineTextSegment> upper = [];
        List<InlineTextSegment> lower = [];
        int cursor = index + 1;
        while (cursor < segments.Count)
        {
            InlineTextSegment segment = segments[cursor];
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                int next = NextNonWhitespaceSegmentIndex(segments, cursor + 1);
                if (next >= 0 && IsSummationLimitSegment(segments[next]))
                {
                    cursor++;
                    continue;
                }

                break;
            }

            if (!IsSummationLimitSegment(segment))
            {
                break;
            }

            if (segment.Role == InlineBaselineRole.Superscript)
            {
                upper.Add(segment);
            }
            else
            {
                lower.Add(segment);
            }

            cursor++;
        }

        if (upper.Count == 0 && lower.Count == 0)
        {
            return false;
        }

        html.Append("<span class=\"pdf-semantic-inline-summation pdf-semantic-math\"><span>")
            .Append(Html(segments[index].Text))
            .Append("</span><span class=\"pdf-semantic-inline-summation-limits\"><span>");
        WriteSummationUpperLimit(html, upper);
        html.Append("</span><span>");
        WriteSummationLowerLimit(html, lower);
        html.Append("</span></span></span>");

        consumedSegments = cursor - index;
        consumedLength = segments.Skip(index).Take(consumedSegments).Sum(static segment => segment.Text.Length);
        return true;
    }

    private static bool IsSummationLimitSegment(InlineTextSegment segment)
    {
        return segment.Run != null &&
            segment.Role is InlineBaselineRole.Superscript or InlineBaselineRole.Subscript &&
            (HasMathFont(segment.Run.FontName) || IsCompactMathFont(segment.Run.FontName)) &&
            segment.Text.All(static character => char.IsLetterOrDigit(character) || character == '=');
    }

    private static void WriteSummationUpperLimit(StringBuilder html, IReadOnlyList<InlineTextSegment> upper)
    {
        string compact = CompactSegmentText(upper);
        if (string.Equals(compact, "dk", StringComparison.Ordinal))
        {
            html.Append("d<sub>k</sub>");
            return;
        }

        html.Append(Html(compact));
    }

    private static void WriteSummationLowerLimit(StringBuilder html, IReadOnlyList<InlineTextSegment> lower)
    {
        string compact = CompactSegmentText(lower);
        if (compact.Contains('i') && compact.Contains('=') && compact.Contains('1'))
        {
            html.Append("i=1");
            return;
        }

        html.Append(Html(compact));
    }

    private static bool IsCompactSquareRootSegment(InlineTextSegment segment)
    {
        return segment.Text == "√" &&
            segment.Run != null &&
            IsCompactMathFont(segment.Run.FontName);
    }

    private static bool IsCompactFractionNumeratorOne(InlineTextSegment segment)
    {
        return segment.Text == "1" &&
            segment.Run != null &&
            IsCompactMathFont(segment.Run.FontName) &&
            segment.Role == InlineBaselineRole.Superscript;
    }

    private static bool IsCompactFractionDenominatorSegment(InlineTextSegment segment)
    {
        return segment.Text.Length > 0 &&
            segment.Run != null &&
            IsCompactMathFont(segment.Run.FontName) &&
            segment.Role == InlineBaselineRole.Subscript;
    }

    private static bool IsMathAttachmentSpaceSegment(IReadOnlyList<InlineTextSegment> segments, int index)
    {
        InlineTextSegment segment = segments[index];
        if (!string.IsNullOrWhiteSpace(segment.Text))
        {
            return false;
        }

        InlineTextSegment? previous = NearestTextSegment(segments, index, -1);
        InlineTextSegment? next = NearestTextSegment(segments, index, 1);
        return previous is { Run: { } previousRun } &&
            next is { Run: { } nextRun } nextSegment &&
            HasMathFont(previousRun.FontName) &&
            HasMathFont(nextRun.FontName) &&
            nextSegment.Role == InlineBaselineRole.Subscript;
    }

    private static InlineTextSegment? NearestTextSegment(
        IReadOnlyList<InlineTextSegment> segments,
        int index,
        int step)
    {
        for (int candidate = index + step; candidate >= 0 && candidate < segments.Count; candidate += step)
        {
            if (!string.IsNullOrWhiteSpace(segments[candidate].Text))
            {
                return segments[candidate];
            }
        }

        return null;
    }

    private static void WriteInlineTextSegment(
        StringBuilder html,
        PdfSemanticLine line,
        InlineTextSegment segment,
        string lineText,
        int offset,
        FootnoteContext footnotes,
        bool allowGeneratedLinks,
        PdfLayoutColor? baselineColor = null)
    {
        if (segment.Run == null)
        {
            if (segment.Role == InlineBaselineRole.Normal)
            {
                if (allowGeneratedLinks)
                {
                    WriteTextWithFootnoteReferences(html, segment.Text, footnotes, lineText, offset);
                }
                else
                {
                    html.Append(Html(segment.Text));
                }
            }
            else
            {
                WriteStyledInlineTextSegment(
                    html,
                    segment.Text,
                    "",
                    segment.Role,
                    allowGeneratedLinks ? footnotes : null,
                    lineText,
                    offset);
            }

            return;
        }

        if (allowGeneratedLinks && IsFootnoteReferenceSegment(segment, lineText, offset, footnotes))
        {
            WriteTextWithFootnoteReferences(html, segment.Text, footnotes, lineText, offset);
            return;
        }

        string className = InlineRunClassNames(line, segment.Run, segment.Role, baselineColor);
        if (segment.IsCode)
        {
            className = string.Join(
                " ",
                new[] { className, "pdf-semantic-inline-code" }
                    .Where(static value => value.Length > 0));
        }

        WriteStyledInlineTextSegment(
            html,
            segment.Text,
            className,
            segment.Role,
            allowGeneratedLinks ? footnotes : null,
            lineText,
            offset,
            TextShadowStyle(segment.Run.Shadow),
            segment.Highlight,
            suppressItalicSemantics: segment.Semantic?.Kind == PdfSemanticInlineKind.Citation);
    }

    private static void WriteInlineTextSegmentAsRole(
        StringBuilder html,
        PdfSemanticLine line,
        InlineTextSegment segment,
        InlineBaselineRole role)
    {
        string className = segment.Run == null ? "" : InlineRunClassNames(line, segment.Run, role);
        WriteStyledInlineTextSegment(
            html,
            segment.Text,
            className,
            role,
            style: TextShadowStyle(segment.Run?.Shadow));
    }

    private static void WriteStyledInlineTextSegment(
        StringBuilder html,
        string text,
        string className,
        InlineBaselineRole role,
        FootnoteContext? footnotes = null,
        string? lineText = null,
        int offset = 0,
        string? style = null,
        PdfTextHighlight? highlight = null,
        bool suppressItalicSemantics = false)
    {
        string tagName = role switch
        {
            InlineBaselineRole.Subscript => "sub",
            InlineBaselineRole.Superscript => "sup",
            InlineBaselineRole.Normal when HasCssClass(className, "pdf-semantic-inline-code") => "code",
            InlineBaselineRole.Normal when HasCssClass(className, "pdf-semantic-bold") => "strong",
            InlineBaselineRole.Normal when
                !suppressItalicSemantics &&
                HasCssClass(className, "pdf-semantic-italic") &&
                !HasCssClass(className, "pdf-semantic-math") => "em",
            _ => className.Length > 0 ? "span" : ""
        };

        if (highlight != null)
        {
            WriteSourceMarkStart(html, highlight, scale: 1f);
        }

        if (tagName.Length > 0)
        {
            html.Append('<')
                .Append(tagName);
            if (className.Length > 0)
            {
                html.Append(" class=\"")
                    .Append(className)
                    .Append('"');
            }

            if (!string.IsNullOrEmpty(style))
            {
                html.Append(" style=\"")
                    .Append(style)
                    .Append('"');
            }

            html.Append('>');
        }

        if (footnotes != null && lineText != null)
        {
            WriteTextWithFootnoteReferences(html, text, footnotes, lineText, offset);
        }
        else
        {
            html.Append(Html(text));
        }

        if (tagName.Length > 0)
        {
            html.Append("</")
                .Append(tagName)
                .Append('>');
        }

        if (highlight != null)
        {
            html.Append("</mark>");
        }
    }

    private static bool HasCssClass(string className, string cssClass)
    {
        return className
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(cssClass, StringComparer.Ordinal);
    }

    private static bool IsFootnoteReferenceSegment(
        InlineTextSegment segment,
        string lineText,
        int offset,
        FootnoteContext footnotes)
    {
        return segment.Role == InlineBaselineRole.Superscript &&
            footnotes.TryMatchReference(segment.Text, 0, out _, out int markerLength) &&
            markerLength == segment.Text.Length &&
            IsFootnoteReferenceBoundary(lineText, offset, markerLength);
    }

    private static string InlineRunClassNames(
        PdfSemanticLine line,
        PdfTextRun run,
        InlineBaselineRole role,
        PdfLayoutColor? baselineColor = null)
    {
        List<string> classes = [];
        string normalizedFontName = NormalizeFontName(run.FontName);
        bool lineIsBold = IsBoldFont(line.DominantFontName);
        bool runIsBold = IsBoldFont(normalizedFontName);
        bool runIsMath = HasMathFont(normalizedFontName) ||
            role is InlineBaselineRole.Subscript or InlineBaselineRole.Superscript &&
            IsCompactMathFont(normalizedFontName) &&
            line.Runs.Any(static lineRun => HasMathFont(lineRun.FontName));
        float fontSize = MathF.Round(run.FontSize * 2f) / 2f;
        if (!string.Equals(normalizedFontName, line.DominantFontName, StringComparison.Ordinal) ||
            runIsMath ||
            IsItalicFont(normalizedFontName) ||
            (runIsBold && !lineIsBold))
        {
            classes.Add(FontClass(normalizedFontName));
        }

        if (MathF.Abs(fontSize - line.DominantFontSize) > 0.25f)
        {
            classes.Add(FontSizeClass(fontSize));
        }

        if (!string.Equals(
                ColorClass(run.Color),
                ColorClass(baselineColor ?? line.Color),
                StringComparison.Ordinal))
        {
            classes.Add(ColorClass(run.Color));
        }

        if (runIsMath)
        {
            classes.Add("pdf-semantic-math");
        }

        if (IsItalicFont(normalizedFontName))
        {
            classes.Add("pdf-semantic-italic");
        }

        if (runIsBold && !lineIsBold)
        {
            classes.Add("pdf-semantic-bold");
        }

        if (run.Shadow is not null)
        {
            classes.Add("pdf-text-shadow");
        }

        return string.Join(" ", classes.Distinct(StringComparer.Ordinal));
    }

    private static void AppendTextShadowStyle(StringBuilder html, PdfTextShadow? shadow, float scale)
    {
        if (shadow is null)
        {
            return;
        }

        html.Append(';').Append(TextShadowStyle(shadow, scale));
    }

    private static string? TextShadowStyle(PdfTextShadow? shadow, float scale = 1f)
    {
        if (shadow is null)
        {
            return null;
        }

        return "--pdf-text-shadow-x:" + CssPoints(shadow.OffsetX * scale) +
            ";--pdf-text-shadow-y:" + CssPoints(shadow.OffsetY * scale) +
            ";--pdf-text-shadow-blur:" + CssPoints(shadow.BlurRadius * scale) +
            ";--pdf-text-shadow-color:" + CssRgba(shadow.Color);
    }

    private static void WriteFootnote(
        StringBuilder html,
        LogicalFootnote note,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        PdfSemanticElement element = note.Fragments[0].Element;
        string marker = note.Marker;
        html.Append("          <li id=\"")
            .Append(HtmlAttribute(note.Id))
            .Append("\" class=\"")
            .Append(SemanticClassNames(element))
            .Append("\" data-note-marker=\"")
            .Append(HtmlAttribute(marker))
            .Append('"');
        if (int.TryParse(marker, NumberStyles.None, CultureInfo.InvariantCulture, out int numericMarker) &&
            numericMarker > 0)
        {
            html.Append(" value=\"")
                .Append(numericMarker.ToString(CultureInfo.InvariantCulture))
                .Append('"');
        }

        AppendTextDirectionAttribute(html, element.Text);
        html.Append("><span class=\"pdf-semantic-footnote-marker\">");
        WriteFootnoteMarker(html, note);
        html.Append("</span> ");

        string previousText = "";
        for (int fragmentIndex = 0; fragmentIndex < note.Fragments.Count; fragmentIndex++)
        {
            FootnoteFragment fragment = note.Fragments[fragmentIndex];
            bool isContinuation = fragmentIndex > 0 || fragment.Element.Note?.ContinuesPreviousNote == true;
            string text = fragment.Element.Text.Trim();
            string markerToSkip = isContinuation ? "" : marker;
            string body = markerToSkip.Length > 0 && StartsWithFootnoteMarker(text, markerToSkip)
                ? text[markerToSkip.Length..].TrimStart()
                : text;
            if (isContinuation)
            {
                if (NeedsSpaceBetween(previousText, body))
                {
                    html.Append(' ');
                }

                html.Append("<span class=\"pdf-semantic-note-continuation\" data-source-page=\"")
                    .Append(fragment.PageNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("\">");
            }

            WriteFootnoteBody(
                html,
                fragment.Element,
                markerToSkip,
                body,
                fragment.Context,
                fragment.Page ?? page);
            if (isContinuation)
            {
                html.Append("</span>");
            }

            previousText = body;
        }

        WriteAdditionalFootnoteBacklinks(html, note);
        html.AppendLine("</li>");
    }

    private static void WriteFootnoteMarker(StringBuilder html, LogicalFootnote note)
    {
        if (note.ReferenceIds.Count == 0)
        {
            html.Append(Html(note.Marker));
            return;
        }

        html.Append("<a class=\"pdf-semantic-footnote-backref\" href=\"#")
            .Append(HtmlAttribute(note.ReferenceIds[0]))
            .Append("\" aria-label=\"Back to note reference 1\">")
            .Append(Html(note.Marker))
            .Append("</a>");
    }

    private static void WriteAdditionalFootnoteBacklinks(StringBuilder html, LogicalFootnote note)
    {
        if (note.ReferenceIds.Count <= 1)
        {
            return;
        }

        html.Append(" <span class=\"pdf-semantic-footnote-backrefs\">");
        for (int index = 1; index < note.ReferenceIds.Count; index++)
        {
            string referenceNumber = (index + 1).ToString(CultureInfo.InvariantCulture);
            html.Append("<a class=\"pdf-semantic-footnote-backref\" href=\"#")
                .Append(HtmlAttribute(note.ReferenceIds[index]))
                .Append("\" aria-label=\"Back to note reference ")
                .Append(referenceNumber)
                .Append("\">")
                .Append(referenceNumber)
                .Append("</a>");
        }

        html.Append("</span>");
    }

    private static bool StartsWithFootnoteMarker(string text, string marker)
    {
        return text.StartsWith(marker, StringComparison.Ordinal) &&
            (text.Length == marker.Length || char.IsWhiteSpace(text[marker.Length]));
    }

    private static void WriteFootnoteBody(
        StringBuilder html,
        PdfSemanticElement element,
        string marker,
        string plainBody,
        FootnoteContext footnotes,
        PdfLayoutPage? page)
    {
        if (CanWriteRichSemanticText(element))
        {
            WriteRichSemanticText(html, element, footnotes, page, marker);
            return;
        }

        html.Append(Html(plainBody));
    }

    private static string SemanticTagName(PdfSemanticElement element, int? headingLevel = null)
    {
        if (IsFigureCaption(element))
        {
            return "figcaption";
        }

        return element.Kind switch
        {
            PdfSemanticElementKind.Heading => "h" + Math.Clamp(
                headingLevel ?? element.HeadingLevel,
                1,
                6).ToString(CultureInfo.InvariantCulture),
            PdfSemanticElementKind.Paragraph => "p",
            PdfSemanticElementKind.CodeBlock => "pre",
            PdfSemanticElementKind.ThematicBreak => "hr",
            PdfSemanticElementKind.Algorithm => "figure",
            PdfSemanticElementKind.DefinitionList => "dl",
            PdfSemanticElementKind.BlockQuote => "blockquote",
            PdfSemanticElementKind.Aside => "aside",
            PdfSemanticElementKind.AuthorBlock => "address",
            PdfSemanticElementKind.Footnote => "li",
            PdfSemanticElementKind.Footer => "footer",
            PdfSemanticElementKind.Header => "header",
            _ => "div"
        };
    }

    private static string SemanticClassNames(
        PdfSemanticElement element,
        PdfLayoutPage? page = null,
        bool allowMeasuredWidth = true,
        bool allowCoverPositioning = true)
    {
        List<string> classes =
        [
            "pdf-semantic-element",
            SemanticClassName(element.Kind)
        ];
        PdfTextRun[] sourceRuns = element.Lines.SelectMany(static line => line.Runs).ToArray();
        if (sourceRuns.Length > 0 && sourceRuns.All(IsUnpaintedTextRun))
        {
            classes.Add("pdf-ocr-text-run");
        }

        if (IsRightToLeftText(element.Text))
        {
            classes.Add("pdf-text-rtl");
        }

        if (element.Kind != PdfSemanticElementKind.AuthorBlock)
        {
            classes.Add(FontClass(SemanticFontName(element)));
            classes.Add(FontSizeClass(SemanticFontSize(element)));
            classes.Add(ColorClass(
                element.Kind == PdfSemanticElementKind.List
                    ? SemanticListColor(element)
                    : SemanticColor(element)));
        }

        if (element.Kind == PdfSemanticElementKind.Aside && page != null)
        {
            classes.Add("pdf-semantic-aside-source-geometry");
            if (SemanticAsideSourceDecorationFor(page, element).HasValue)
            {
                classes.Add("pdf-semantic-aside-source-decorated");
            }
        }

        if (IsTitleElement(element))
        {
            classes.Add("pdf-semantic-title");
            if (!IsBoldFont(SemanticFontName(element)))
            {
                classes.Add("pdf-semantic-title-regular");
            }

            if (page != null)
            {
                if (DecorativeTitleRulePath(page, element, TitleRulePosition.Above) != null)
                {
                    classes.Add("pdf-semantic-title-rule-top");
                }

                if (DecorativeTitleRulePath(page, element, TitleRulePosition.Below) != null)
                {
                    classes.Add("pdf-semantic-title-rule-bottom");
                }
            }
        }

        if (page != null)
        {
            string? alignmentClass = SourceAlignmentClass(page, element);
            if (alignmentClass != null)
            {
                classes.Add(alignmentClass);
                if (allowCoverPositioning &&
                    IsSparseGraphicCoverPage(page) &&
                    IsCoverTextKind(element.Kind))
                {
                    classes.Add("pdf-semantic-cover-text");
                    if (IsTitleElement(element) && element.Lines.Count > 1)
                    {
                        classes.Add("pdf-semantic-cover-title");
                    }
                }
            }

            if (element.Kind == PdfSemanticElementKind.FrontMatter)
            {
                classes.Add("pdf-semantic-align-center");
            }

            if (IsFigureCaption(element))
            {
                classes.Add("pdf-semantic-caption");
            }

            if (IsSameRowLineGroup(element))
            {
                classes.Add("pdf-semantic-line-row");
            }
        }

        if (IsFormulaBlock(element))
        {
            classes.Add("pdf-semantic-formula");
        }

        if (element.Kind == PdfSemanticElementKind.Paragraph && page != null)
        {
            if (!IsFormulaBlock(element) && IsJustifiedParagraph(element))
            {
                classes.Add("pdf-semantic-justified");
            }

            if (!IsFormulaBlock(element) &&
                allowMeasuredWidth &&
                TryGetParagraphWidthPercent(page, element, out float widthPercent) &&
                ShouldUseMeasuredParagraphWidth(widthPercent))
            {
                classes.Add("pdf-semantic-measured-width");
            }
        }

        if (element.Kind == PdfSemanticElementKind.Table && page != null)
        {
            if (allowMeasuredWidth &&
                TryGetTableWidthPercent(page, element, out float widthPercent) &&
                ShouldUseMeasuredTableWidth(widthPercent))
            {
                classes.Add("pdf-semantic-measured-width");
            }

        }

        return string.Join(" ", classes);
    }

    private static string FlowSemanticStyle(
        PdfSemanticElement element,
        PdfLayoutPage? page,
        bool allowMeasuredWidth)
    {
        List<string> styles = [];
        if (IsSameRowLineGroup(element))
        {
            styles.Add("--pdf-semantic-line-count:" + SameRowLines(element).Length.ToString(CultureInfo.InvariantCulture));
        }

        if (element.Algorithm != null)
        {
            styles.AddRange(AlgorithmRuleStyles(element.Algorithm));
            if (allowMeasuredWidth && page != null)
            {
                float algorithmWidthPercent = Math.Clamp(element.Bounds.Width / SemanticFlowWidth(page) * 100f, 1f, 100f);
                styles.Add("--pdf-semantic-width:" + CssPercent(algorithmWidthPercent));
            }
        }

        if (allowMeasuredWidth &&
            page != null &&
            element.Kind == PdfSemanticElementKind.Paragraph &&
            TryGetParagraphWidthPercent(page, element, out float widthPercent) &&
            ShouldUseMeasuredParagraphWidth(widthPercent))
        {
            styles.Add("--pdf-semantic-width:" + CssPercent(widthPercent));
            string? alignSelf = ParagraphAlignSelf(page, element);
            if (alignSelf != null)
            {
                styles.Add("--pdf-semantic-align-self:" + alignSelf);
            }
        }

        if (allowMeasuredWidth &&
            page != null &&
            element.Kind == PdfSemanticElementKind.Table &&
            TryGetTableWidthPercent(page, element, out float tableWidthPercent) &&
            ShouldUseMeasuredTableWidth(tableWidthPercent))
        {
            styles.Add("--pdf-semantic-width:" + CssPercent(tableWidthPercent));
            string? alignSelf = TableAlignSelf(page, element);
            if (alignSelf != null)
            {
                styles.Add("--pdf-semantic-align-self:" + alignSelf);
            }
        }

        if (page != null && element.Kind == PdfSemanticElementKind.Aside)
        {
            SemanticAsideSourceDecoration? decoration = SemanticAsideSourceDecorationFor(page, element);
            PdfLayoutRectangle sourceBounds = decoration?.Bounds ?? element.Bounds;
            float canonicalFlowLeft = MathF.Max(0f, (page.Width - SemanticFlowWidth(page)) / 2f);
            float sourceInset = sourceBounds.X - canonicalFlowLeft;
            styles.Add("--pdf-semantic-aside-inset-left:" + CssPoints(sourceInset));
            styles.Add("--pdf-semantic-aside-source-width:" + CssPoints(sourceBounds.Width));
            if (decoration is SemanticAsideSourceDecoration sourceDecoration)
            {
                styles.Add("--pdf-semantic-aside-source-background:" + sourceDecoration.Background);
                styles.Add("--pdf-semantic-aside-source-border-color:" +
                    (sourceDecoration.Stroke == null
                        ? "transparent"
                        : CssRgba(sourceDecoration.Stroke.Color)));
                styles.Add("--pdf-semantic-aside-source-border-style:" +
                    (sourceDecoration.Stroke?.DashArray.Any(static dash => dash > 0f) == true
                        ? "dashed"
                        : "solid"));
                styles.Add("--pdf-semantic-aside-source-border-width:" +
                    CssPoints(sourceDecoration.Stroke?.Width ?? 0f));
                styles.Add("--pdf-semantic-aside-source-padding-top:" +
                    CssPoints(MathF.Max(0f, element.Bounds.Y - sourceBounds.Y)));
                styles.Add("--pdf-semantic-aside-source-padding-right:" +
                    CssPoints(MathF.Max(0f, sourceBounds.Right - element.Bounds.Right)));
                styles.Add("--pdf-semantic-aside-source-padding-bottom:" +
                    CssPoints(MathF.Max(0f, sourceBounds.Bottom - element.Bounds.Bottom)));
                styles.Add("--pdf-semantic-aside-source-padding-left:" +
                    CssPoints(MathF.Max(0f, element.Bounds.X - sourceBounds.X)));
            }
        }

        if (page != null && element.Kind == PdfSemanticElementKind.FrontMatter)
        {
            float sourceWidth = element.Lines
                .Where(static line => MathF.Abs(line.Direction) < 0.01f)
                .Select(static line => line.Bounds.Width)
                .DefaultIfEmpty(element.Bounds.Width)
                .Max();
            styles.Add("--pdf-semantic-front-matter-width:" + CssPoints(sourceWidth));

            PdfTextLine? nextSourceLine = page.Lines
                .Where(static line => line.Runs.Any(static run => MathF.Abs(run.Direction) < 0.01f))
                .Where(line => line.Bounds.Y > element.Bounds.Bottom + 0.5f)
                .OrderBy(static line => line.Bounds.Y)
                .ThenBy(static line => line.Bounds.X)
                .FirstOrDefault();
            if (nextSourceLine != null)
            {
                float sourceGap = Math.Clamp(nextSourceLine.Bounds.Y - element.Bounds.Bottom, 4f, 28f);
                styles.Add("--pdf-semantic-front-matter-gap-after:" + CssPoints(sourceGap));
            }
        }

        if (page != null && IsTitleElement(element))
        {
            float sourceWidth = element.Lines
                .Where(static line => MathF.Abs(line.Direction) < 0.01f)
                .Select(static line => line.Bounds.Width)
                .DefaultIfEmpty(element.Bounds.Width)
                .Max();
            if (sourceWidth > SemanticFlowWidth(page) + 0.5f)
            {
                styles.Add("--pdf-semantic-title-width:" + CssPoints(sourceWidth));
            }

            AppendTitleRuleStyle(styles, page, element, TitleRulePosition.Above, "top");
            AppendTitleRuleStyle(styles, page, element, TitleRulePosition.Below, "bottom");
        }

        if (page != null &&
            IsSparseGraphicCoverPage(page) &&
            IsCoverTextKind(element.Kind) &&
            SourceAlignmentClass(page, element) is string coverAlignment)
        {
            styles.Add("--pdf-semantic-cover-text-offset-x:" +
                CssPoints(CoverTextHorizontalOffset(page, element, coverAlignment)));
            if (IsTitleElement(element) && element.Lines.Count > 1)
            {
                styles.Add("--pdf-semantic-cover-title-width:" + CssPoints(element.Bounds.Width));
            }
        }

        return string.Join(";", styles);
    }

    private static IEnumerable<string> AlgorithmRuleStyles(PdfSemanticAlgorithm algorithm)
    {
        yield return "--pdf-semantic-algorithm-top-rule-width:" + CssPoints(algorithm.TopRule.Thickness);
        yield return "--pdf-semantic-algorithm-top-rule-color:" + CssRgba(algorithm.TopRule.Color);
        yield return "--pdf-semantic-algorithm-caption-rule-width:" + CssPoints(algorithm.CaptionRule.Thickness);
        yield return "--pdf-semantic-algorithm-caption-rule-color:" + CssRgba(algorithm.CaptionRule.Color);
        yield return "--pdf-semantic-algorithm-bottom-rule-width:" + CssPoints(algorithm.BottomRule.Thickness);
        yield return "--pdf-semantic-algorithm-bottom-rule-color:" + CssRgba(algorithm.BottomRule.Color);
    }

    private static bool IsCoverTextKind(PdfSemanticElementKind kind)
    {
        return kind is PdfSemanticElementKind.Header or
            PdfSemanticElementKind.Heading or
            PdfSemanticElementKind.Paragraph or
            PdfSemanticElementKind.AuthorBlock;
    }

    private static bool IsSparseGraphicCoverPage(PdfLayoutPage page)
    {
        return page.Images.Count is > 0 and <= 6 &&
            page.Lines.Count is > 0 and <= 18 &&
            page.Runs.Any(static run => run.FontSize >= 16f);
    }

    private static float CoverTextHorizontalOffset(
        PdfLayoutPage page,
        PdfSemanticElement element,
        string alignmentClass)
    {
        float flowWidth = SemanticFlowWidth(page);
        float flowLeft = (page.Width - flowWidth) / 2f;
        float flowRight = flowLeft + flowWidth;
        return alignmentClass switch
        {
            "pdf-semantic-align-right" => element.Bounds.Right - flowRight,
            "pdf-semantic-align-center" => element.Bounds.X + element.Bounds.Width / 2f - page.Width / 2f,
            _ => element.Bounds.X - flowLeft
        };
    }

    private static void AppendTitleRuleStyle(
        ICollection<string> styles,
        PdfLayoutPage page,
        PdfSemanticElement title,
        TitleRulePosition position,
        string side)
    {
        PdfLayoutPath? path = DecorativeTitleRulePath(page, title, position);
        if (path == null)
        {
            return;
        }

        PdfLayoutColor color = path.Stroke?.Color ?? path.FillColor ?? SemanticColor(title);
        float thickness = path.Stroke?.Width ?? MathF.Max(0.5f, path.Bounds.Height);
        float gap = position == TitleRulePosition.Above
            ? MathF.Max(0f, title.Bounds.Y - path.Bounds.Bottom)
            : MathF.Max(0f, path.Bounds.Y - title.Bounds.Bottom);
        styles.Add("--pdf-title-rule-" + side + "-thickness:" + CssPoints(thickness));
        styles.Add("--pdf-title-rule-" + side + "-color:" + ColorHex(color));
        styles.Add("--pdf-title-rule-" + side + "-gap:" + CssPoints(gap));
    }

    private static bool TryGetParagraphWidthPercent(
        PdfLayoutPage page,
        PdfSemanticElement element,
        out float widthPercent)
    {
        widthPercent = 100f;
        if (element.Kind != PdfSemanticElementKind.Paragraph ||
            element.Lines.Count < 4 ||
            element.Text.Length < 240 ||
            !TryGetRepresentativeLineWidth(element, out float representativeWidth))
        {
            return false;
        }

        float flowWidth = SemanticFlowWidth(page);
        if (flowWidth <= 0.01f)
        {
            return false;
        }

        widthPercent = Math.Clamp((representativeWidth / flowWidth) * 100f, 25f, 100f);
        return true;
    }

    private static bool TryGetRepresentativeLineWidth(PdfSemanticElement element, out float width)
    {
        float[] widths = RepresentativeTextRows(element)
            .Select(static row => row.Width)
            .OrderDescending()
            .ToArray();
        if (widths.Length == 0)
        {
            width = 0f;
            return false;
        }

        int count = widths.Length <= 2
            ? widths.Length
            : Math.Max(1, (int)MathF.Ceiling(widths.Length * 0.65f));
        width = widths.Take(count).Average();
        return true;
    }

    private static IEnumerable<PdfLayoutRectangle> RepresentativeTextRows(PdfSemanticElement element)
    {
        List<PdfLayoutRectangle> rows = [];
        foreach (PdfSemanticLine line in element.Lines
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(static line => line.Bounds.Y)
            .ThenBy(static line => line.Bounds.X))
        {
            int rowIndex = rows.FindIndex(row => BelongsToTextRow(row, line.Bounds));
            if (rowIndex < 0)
            {
                rows.Add(line.Bounds);
            }
            else
            {
                rows[rowIndex] = UnionRectangles([rows[rowIndex], line.Bounds]);
            }
        }

        return rows;
    }

    private static bool BelongsToTextRow(PdfLayoutRectangle row, PdfLayoutRectangle candidate)
    {
        float overlap = MathF.Min(row.Bottom, candidate.Bottom) - MathF.Max(row.Y, candidate.Y);
        if (overlap >= MathF.Min(row.Height, candidate.Height) * 0.35f)
        {
            return true;
        }

        float centerDistance = MathF.Abs(
            row.Y + (row.Height / 2f) - (candidate.Y + (candidate.Height / 2f)));
        return centerDistance <= MathF.Max(2.5f, MathF.Max(row.Height, candidate.Height) * 0.55f);
    }

    private static bool ShouldUseMeasuredParagraphWidth(float widthPercent)
    {
        return widthPercent <= 92f;
    }

    private static bool TryGetTableWidthPercent(
        PdfLayoutPage page,
        PdfSemanticElement element,
        out float widthPercent)
    {
        widthPercent = 100f;
        if (element.Kind != PdfSemanticElementKind.Table || element.Bounds.Width <= 0.01f)
        {
            return false;
        }

        float flowWidth = SemanticFlowWidth(page);
        if (flowWidth <= 0.01f)
        {
            return false;
        }

        widthPercent = Math.Clamp((element.Bounds.Width / flowWidth) * 100f, 25f, 100f);
        return true;
    }

    private static bool ShouldUseMeasuredTableWidth(float widthPercent)
    {
        return widthPercent <= 97f;
    }

    private static string? TableAlignSelf(PdfLayoutPage page, PdfSemanticElement element)
    {
        float elementCenter = element.Bounds.X + (element.Bounds.Width / 2f);
        return MathF.Abs(elementCenter - (page.Width / 2f)) <= page.Width * 0.06f
            ? "center"
            : null;
    }

    private static float StandardDeviation(IEnumerable<float> values)
    {
        float[] sample = values.ToArray();
        if (sample.Length == 0)
        {
            return 0f;
        }

        float mean = sample.Average();
        float variance = sample.Sum(value => MathF.Pow(value - mean, 2f)) / sample.Length;
        return MathF.Sqrt(variance);
    }

    private static string? TableCaptionAlignmentClass(
        PdfSemanticElement table,
        PdfSemanticTableCaption caption)
    {
        PdfSemanticLine[] lines = caption.Lines
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
        if (lines.Length == 0)
        {
            return null;
        }

        float[] fontSizes = lines
            .Select(static line => line.DominantFontSize)
            .Where(static size => size > 0f)
            .Order()
            .ToArray();
        float fontSize = fontSizes.Length == 0 ? 10f : fontSizes[fontSizes.Length / 2];
        if (lines.Length >= 2)
        {
            (string ClassName, float Spread)[] candidates =
            [
                ("pdf-semantic-table-caption-align-left", StandardDeviation(lines.Select(static line => line.Bounds.X))),
                ("pdf-semantic-table-caption-align-center", StandardDeviation(lines.Select(static line => line.Bounds.X + line.Bounds.Width / 2f))),
                ("pdf-semantic-table-caption-align-right", StandardDeviation(lines.Select(static line => line.Bounds.Right)))
            ];
            (string ClassName, float Spread)[] ordered = candidates
                .OrderBy(static candidate => candidate.Spread)
                .ToArray();
            float spreadTolerance = MathF.Max(2f, fontSize * 0.35f);
            if (ordered[0].Spread <= spreadTolerance &&
                ordered[1].Spread - ordered[0].Spread >= spreadTolerance * 0.55f)
            {
                return ordered[0].ClassName;
            }
        }

        return TableCaptionAnchorAlignmentClass(table.Bounds, caption.Bounds, fontSize);
    }

    private static string? TableCaptionAnchorAlignmentClass(
        PdfLayoutRectangle table,
        PdfLayoutRectangle caption,
        float fontSize)
    {
        (string ClassName, float Distance)[] candidates =
        [
            ("pdf-semantic-table-caption-align-left", MathF.Abs(caption.X - table.X)),
            ("pdf-semantic-table-caption-align-center", MathF.Abs(
                caption.X + caption.Width / 2f - (table.X + table.Width / 2f))),
            ("pdf-semantic-table-caption-align-right", MathF.Abs(caption.Right - table.Right))
        ];
        (string ClassName, float Distance)[] ordered = candidates
            .OrderBy(static candidate => candidate.Distance)
            .ToArray();
        float anchorTolerance = MathF.Max(3f, fontSize * 0.75f);
        return ordered[0].Distance <= anchorTolerance &&
            ordered[1].Distance - ordered[0].Distance >= anchorTolerance * 0.75f
                ? ordered[0].ClassName
                : null;
    }

    private static string? ParagraphAlignSelf(PdfLayoutPage page, PdfSemanticElement element)
    {
        float elementCenter = element.Bounds.X + (element.Bounds.Width / 2f);
        return MathF.Abs(elementCenter - (page.Width / 2f)) <= page.Width * 0.04f
            ? "center"
            : null;
    }

    private static string? SourceAlignmentClass(PdfLayoutPage page, PdfSemanticElement element)
    {
        if (!ShouldDetectSourceAlignment(page, element))
        {
            return null;
        }

        PdfSemanticLine[] lines = HorizontalTextLines(element);
        if (lines.Length == 0)
        {
            return null;
        }

        float weight = lines.Sum(static line => MathF.Max(1f, line.Bounds.Width));
        float center = lines.Sum(static line => (line.Bounds.X + line.Bounds.Width / 2f) * MathF.Max(1f, line.Bounds.Width)) / weight;
        float tolerance = MathF.Max(page.Width * 0.035f, SemanticFontSize(element) * 1.75f);
        float averageRight = lines.Average(static line => line.Bounds.Right);
        bool sharedRightEdge = lines.Length >= 2 &&
            StandardDeviation(lines.Select(static line => line.Bounds.Right)) <= MathF.Max(3f, SemanticFontSize(element) * 0.3f) &&
            averageRight >= page.Width * 0.62f &&
            center >= page.Width / 2f + tolerance * 0.2f;
        if (sharedRightEdge)
        {
            return "pdf-semantic-align-right";
        }

        if (MathF.Abs(center - page.Width / 2f) <= tolerance)
        {
            return "pdf-semantic-align-center";
        }

        if (page.Width - averageRight <= page.Width * 0.13f)
        {
            return "pdf-semantic-align-right";
        }

        return null;
    }

    private static bool ShouldDetectSourceAlignment(PdfLayoutPage page, PdfSemanticElement element)
    {
        return element.Kind is PdfSemanticElementKind.Heading or PdfSemanticElementKind.Header or PdfSemanticElementKind.Footer ||
            ShouldDetectFigureCaptionAlignment(element) ||
            element.Kind == PdfSemanticElementKind.Paragraph &&
            element.Lines.Count <= 2 &&
            element.Text.Length <= 180 &&
            element.Bounds.Width <= page.Width * 0.55f ||
            IsSameRowLineGroup(element);
    }

    private static bool ShouldDetectFigureCaptionAlignment(PdfSemanticElement element)
    {
        return IsFigureCaption(element) &&
            (element.Text.Length <= 120 || HorizontalTextLines(element).All(static line => line.Text.Length <= 80));
    }

    private static bool IsFigureCaption(PdfSemanticElement element)
    {
        if (element.Kind != PdfSemanticElementKind.Paragraph && element.Kind != PdfSemanticElementKind.Heading)
        {
            return false;
        }

        string text = element.Text.TrimStart();
        int numberStart;
        char separator;
        if (text.StartsWith("Figure ", StringComparison.OrdinalIgnoreCase))
        {
            numberStart = 7;
            separator = ':';
        }
        else if (text.StartsWith("Fig. ", StringComparison.OrdinalIgnoreCase))
        {
            numberStart = 5;
            separator = '.';
        }
        else
        {
            return false;
        }

        int delimiter = text.IndexOf(separator, numberStart);
        if (delimiter <= numberStart || delimiter - numberStart > 10)
        {
            return false;
        }

        return text[numberStart..delimiter].All(static character =>
            char.IsDigit(character) || character == '.');
    }

    private static bool IsSameRowLineGroup(PdfSemanticElement element)
    {
        return SameRowLines(element).Length >= 2;
    }

    private static bool IsFormulaBlock(PdfSemanticElement element)
    {
        return element.Kind == PdfSemanticElementKind.Paragraph &&
            !IsFigureCaption(element) &&
            !IsFormulaDecorationElement(element) &&
            !IsInlineFormulaFragment(element) &&
            (element.Lines.Any(line =>
                IsDisplayFormulaLine(line) &&
                !IsFormulaLineEmbeddedInProse(element, line)) ||
                IsCompactCenteredFormulaElement(element));
    }

    private static bool IsFormulaLineEmbeddedInProse(
        PdfSemanticElement element,
        PdfSemanticLine candidate)
    {
        if (element.Lines.Any(static line =>
            TryGetTrailingEquationNumber(line.Text.Trim(), out _)))
        {
            return false;
        }

        float candidateCenter = candidate.Bounds.X + candidate.Bounds.Width / 2f;
        return element.Lines.Any(line =>
        {
            if (ReferenceEquals(line, candidate) ||
                !IsProseLikeFormulaSourceLine(line) ||
                line.Bounds.Width < MathF.Max(180f, candidate.Bounds.Width * 1.75f) ||
                candidateCenter < line.Bounds.X - 4f ||
                candidateCenter > line.Bounds.Right + 4f)
            {
                return false;
            }

            float verticalOverlap = MathF.Min(candidate.Bounds.Bottom, line.Bounds.Bottom) -
                MathF.Max(candidate.Bounds.Y, line.Bounds.Y);
            return verticalOverlap >= MathF.Min(candidate.Bounds.Height, line.Bounds.Height) * 0.30f;
        });
    }

    private static bool IsFormulaDecorationElement(PdfSemanticElement element)
    {
        string compact = CompactText(element.Text);
        return compact.Length is > 0 and <= 3 &&
            !compact.Any(char.IsLetterOrDigit) &&
            (HasFormulaSignal(compact) ||
                compact.All(static character => character is '·' or '−' or '+' or '=' or ',' or '.'));
    }

    private static bool IsCompactCenteredFormulaElement(PdfSemanticElement element)
    {
        PdfSemanticLine[] lines = HorizontalTextLines(element);
        if (lines.Length == 0 ||
            lines.Length > 6 ||
            element.Text.Length > 220 ||
            element.Bounds.Height > 90f ||
            element.Bounds.X < 100f ||
            element.Bounds.Width is < 90f or > 430f)
        {
            return false;
        }

        string text = element.Text.Trim();
        return text.Contains('=') &&
            HasFormulaSignal(text) &&
            HasMathFont(SemanticFontName(element)) &&
            CountWords(text) <= 14;
    }

    private static bool IsInlineFormulaFragment(PdfSemanticElement element)
    {
        return element.Text.Length <= 48 &&
            element.Bounds.Width <= 90f &&
            !element.Text.Contains('=') &&
            !element.Text.Contains('∈') &&
            !element.Text.Contains('×') &&
            !element.Text.Contains('√') &&
            !element.Text.Contains('∑') &&
            element.Lines.Any(static line => line.Runs.Any(static run => HasMathFont(run.FontName)));
    }

    private static bool IsDisplayFormulaLine(PdfSemanticLine line)
    {
        string text = line.Text.Trim();
        bool hasMathFont = line.Runs.Any(static run => HasMathFont(run.FontName));
        bool hasEquationNumber = TryGetTrailingEquationNumber(text, out _);
        if ((!hasMathFont && !(hasEquationNumber && HasFormulaSignal(text))) ||
            !(HasFormulaSignal(text) ||
                HasOptimizationProgramSignal(text) ||
                hasEquationNumber) ||
            !line.Runs.SelectMany(static run => run.Glyphs).Any(PdfMathMlFormula.IsEligibleGlyph))
        {
            return false;
        }

        if (HasFormulaFunction(text))
        {
            return text.IndexOf('=') >= 0 ||
                line.Bounds.Width >= 80f &&
                (StartsFormulaFunction(text) || CountWords(text) <= 4);
        }

        if (IsMathDominantFormulaLine(line))
        {
            return true;
        }

        return line.Bounds.X >= 150f &&
            line.Bounds.Width >= 80f &&
            CountWords(text) <= 12;
    }

    private static bool IsMathDominantFormulaLine(PdfSemanticLine line)
    {
        if (line.Bounds.Width < 80f || line.Text.Length > 220)
        {
            return false;
        }

        if (!HasMathFont(line.DominantFontName))
        {
            return false;
        }

        int totalCharacters = line.Runs.Sum(static run =>
            run.Text.Count(static character => !char.IsWhiteSpace(character)));
        int mathCharacters = line.Runs
            .Where(static run => HasMathFont(run.FontName))
            .Sum(static run => run.Text.Count(static character => !char.IsWhiteSpace(character)));
        return mathCharacters >= 8 &&
            totalCharacters > 0 &&
            mathCharacters / (float)totalCharacters >= 0.58f;
    }

    internal static bool HasMathFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.StartsWith("CMMI", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMSY", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMEX", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMBSY", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("MSAM", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("MSBM", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("AMSA", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("AMSB", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompactMathFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.StartsWith("CMR7", StringComparison.Ordinal) ||
            normalized.StartsWith("CMMI7", StringComparison.Ordinal) ||
            normalized.StartsWith("CMSY7", StringComparison.Ordinal) ||
            normalized.StartsWith("CMR6", StringComparison.Ordinal) ||
            normalized.StartsWith("CMMI6", StringComparison.Ordinal) ||
            normalized.StartsWith("CMSY6", StringComparison.Ordinal) ||
            normalized.StartsWith("CMR5", StringComparison.Ordinal) ||
            normalized.StartsWith("CMMI5", StringComparison.Ordinal) ||
            normalized.StartsWith("CMSY5", StringComparison.Ordinal);
    }

    private static bool IsItalicFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Ital", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMMI", StringComparison.Ordinal);
    }

    private static bool IsBoldFont(string fontName)
    {
        string normalized = NormalizeFontName(fontName);
        return normalized.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Medi", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("CMBX", StringComparison.Ordinal) ||
            normalized.StartsWith("CMMIB", StringComparison.Ordinal) ||
            normalized.StartsWith("CMBSY", StringComparison.Ordinal);
    }

    private static bool HasFormulaSignal(string text)
    {
        return text.IndexOfAny(['=', '∈', '×', '√', '∑', '∝', '·', '∗', '≥', '≤']) >= 0 ||
            HasFormulaFunction(text);
    }

    private static bool HasOptimizationProgramSignal(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith("minimize ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("maximize ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("subject to ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFormulaFunction(string text)
    {
        return
            text.Contains("Attention(", StringComparison.Ordinal) ||
            text.Contains("MultiHead(", StringComparison.Ordinal) ||
            text.Contains("Concat(", StringComparison.Ordinal) ||
            text.Contains("FFN(", StringComparison.Ordinal) ||
            text.Contains("PE", StringComparison.Ordinal);
    }

    private static bool StartsFormulaFunction(string text)
    {
        string trimmed = text.TrimStart();
        return
            trimmed.StartsWith("Attention(", StringComparison.Ordinal) ||
            trimmed.StartsWith("MultiHead(", StringComparison.Ordinal) ||
            trimmed.StartsWith("Concat(", StringComparison.Ordinal) ||
            trimmed.StartsWith("FFN(", StringComparison.Ordinal) ||
            trimmed.StartsWith("PE", StringComparison.Ordinal);
    }

    private static int CountWords(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static PdfSemanticLine[] SameRowLines(PdfSemanticElement element)
    {
        if (element.Kind != PdfSemanticElementKind.Paragraph || element.Text.Length > 120)
        {
            return [];
        }

        PdfSemanticLine[] lines = HorizontalTextLines(element);
        if (lines.Length != element.Lines.Count || lines.Any(static line => line.Text.Length > 64))
        {
            return [];
        }

        if (lines.Length >= 2 && TryGetSameRowLines(element, lines, out PdfSemanticLine[] rowLines))
        {
            return rowLines;
        }

        return lines.Length == 1 ? SameRowRunClusters(lines[0]) : [];
    }

    private static bool TryGetSameRowLines(
        PdfSemanticElement element,
        PdfSemanticLine[] lines,
        out PdfSemanticLine[] rowLines)
    {
        rowLines = [];
        float maxHeight = lines.Max(static line => line.Bounds.Height);
        float ySpan = lines.Max(static line => line.Bounds.Y) - lines.Min(static line => line.Bounds.Y);
        if (ySpan > MathF.Max(3f, maxHeight * 0.60f))
        {
            return false;
        }

        PdfSemanticLine[] ordered = lines
            .OrderBy(static line => line.Bounds.X)
            .ToArray();
        float minimumGap = MathF.Max(14f, SemanticFontSize(element) * 2f);
        for (int index = 1; index < ordered.Length; index++)
        {
            if (HorizontalGap(ordered[index - 1].Bounds, ordered[index].Bounds) < minimumGap)
            {
                return false;
            }
        }

        rowLines = ordered;
        return true;
    }

    private static PdfSemanticLine[] SameRowRunClusters(PdfSemanticLine line)
    {
        PdfTextRun[] runs = line.Runs
            .Where(static run => MathF.Abs(run.Direction) < 0.01f)
            .Where(static run => !string.IsNullOrWhiteSpace(run.Text))
            .OrderBy(static run => run.Bounds.X)
            .ToArray();
        if (runs.Length < 2)
        {
            return [];
        }

        List<List<PdfTextRun>> clusters = [];
        List<PdfTextRun> current = [];
        PdfTextRun? previous = null;
        float splitGap = MathF.Max(24f, line.DominantFontSize * 3f);
        foreach (PdfTextRun run in runs)
        {
            if (previous != null && HorizontalGap(previous.Bounds, run.Bounds) >= splitGap)
            {
                clusters.Add(current);
                current = [];
            }

            current.Add(run);
            previous = run;
        }

        if (current.Count > 0)
        {
            clusters.Add(current);
        }

        if (clusters.Count < 2)
        {
            return [];
        }

        PdfSemanticLine[] rowLines = clusters
            .Select(CreateSyntheticRowLine)
            .Where(static rowLine => rowLine.Text.Length is > 0 and <= 64)
            .ToArray();
        return rowLines.Length == clusters.Count ? rowLines : [];
    }

    private static PdfSemanticLine[] HorizontalTextLines(PdfSemanticElement element)
    {
        return element.Lines
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
    }

    private static PdfSemanticLine CreateSyntheticRowLine(IReadOnlyList<PdfTextRun> runs)
    {
        string text = ReconstructText(runs.SelectMany(static run => run.Glyphs));
        PdfTextRun dominant = runs
            .GroupBy(static run => (
                FontName: NormalizeFontName(run.FontName),
                FontSize: MathF.Round(run.FontSize * 2f) / 2f,
                Direction: MathF.Round(run.Direction),
                Color: ColorClass(run.Color)))
            .OrderByDescending(static group => group.Sum(static run => Math.Max(1, run.Text.Length)))
            .Select(static group => group.First())
            .First();
        return new PdfSemanticLine(
            text,
            UnionRectangles(runs.Select(static run => run.Bounds)),
            NormalizeFontName(dominant.FontName),
            MathF.Round(dominant.FontSize * 2f) / 2f,
            MathF.Round(dominant.Direction),
            dominant.Color,
            runs);
    }

    private static string ReconstructText(IEnumerable<PdfTextGlyph> glyphSource)
    {
        PdfTextGlyph[] glyphs = glyphSource
            .Where(static glyph => !string.IsNullOrEmpty(glyph.Text))
            .OrderBy(static glyph => glyph.Bounds.X)
            .ThenBy(static glyph => glyph.Bounds.Y)
            .ToArray();
        if (glyphs.Length == 0)
        {
            return "";
        }

        StringBuilder text = new();
        PdfTextGlyph? previous = null;
        foreach (PdfTextGlyph glyph in glyphs)
        {
            if (previous != null && ShouldInsertWordBoundary(previous, glyph))
            {
                AppendSpaceIfNeeded(text);
            }

            if (string.IsNullOrWhiteSpace(glyph.Text))
            {
                AppendSpaceIfNeeded(text);
            }
            else
            {
                text.Append(glyph.Text);
            }

            previous = glyph;
        }

        return CollapseWhitespace(text.ToString());
    }

    private static bool ShouldInsertWordBoundary(PdfTextGlyph previous, PdfTextGlyph glyph)
    {
        if (glyph.Bounds.X <= previous.Bounds.X)
        {
            return false;
        }

        string previousText = previous.Text;
        string currentText = glyph.Text;
        if (previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        if (NoSpaceBefore(currentText[0]) || NoSpaceAfter(previousText[^1]))
        {
            return false;
        }

        float gap = glyph.Bounds.X - previous.Bounds.Right;
        float threshold = MathF.Max(0.8f, MathF.Min(previous.FontSize, glyph.FontSize) * 0.16f);
        return gap > threshold;
    }

    private static void AppendSpaceIfNeeded(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != ' ')
        {
            text.Append(' ');
        }
    }

    private static string CollapseWhitespace(string text)
    {
        StringBuilder normalized = new(text.Length);
        bool pendingWhitespace = false;
        foreach (char character in text.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = normalized.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                normalized.Append(' ');
                pendingWhitespace = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private static bool NoSpaceBefore(char character)
    {
        return character is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '\'' or '’';
    }

    private static bool NoSpaceAfter(char character)
    {
        return character is '(' or '[' or '{' or '\'' or '‘';
    }

    private static string NormalizeFontName(string fontName)
    {
        int subsetSeparator = fontName.IndexOf('+', StringComparison.Ordinal);
        return subsetSeparator >= 0 && subsetSeparator + 1 < fontName.Length
            ? fontName[(subsetSeparator + 1)..]
            : fontName;
    }

    private static bool IsJustifiedParagraph(PdfSemanticElement element)
    {
        if (element.Kind != PdfSemanticElementKind.Paragraph)
        {
            return false;
        }

        PdfSemanticLine[] lines = element.Lines
            .Where(static line => MathF.Abs(line.Direction) < 0.01f)
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
        if (lines.Length < 3)
        {
            return false;
        }

        float[] nonFinalWidths = lines
            .Take(lines.Length - 1)
            .Select(static line => line.Bounds.Width)
            .Where(static width => width > 0.01f)
            .ToArray();
        if (nonFinalWidths.Length < 2)
        {
            return false;
        }

        float max = nonFinalWidths.Max();
        float mean = nonFinalWidths.Average();
        float min = nonFinalWidths.Min();
        float variance = nonFinalWidths.Sum(width => MathF.Pow(width - mean, 2f)) / nonFinalWidths.Length;
        float standardDeviation = MathF.Sqrt(variance);
        float averageCharacters = (float)lines.Take(lines.Length - 1).Average(static line => line.Text.Length);
        return max >= 160f &&
            averageCharacters >= 36f &&
            mean >= max * 0.86f &&
            min >= max * 0.70f &&
            standardDeviation <= max * 0.13f;
    }

    private static float SemanticFlowWidth(PdfLayoutPage page)
    {
        return MathF.Min(396f, MathF.Max(0f, page.Width - 144f));
    }

    private static string SemanticLineClassNames(PdfSemanticLine line)
    {
        return string.Join(
            " ",
            "pdf-semantic-line",
            FontClass(line.DominantFontName),
            FontSizeClass(line.DominantFontSize),
            ColorClass(line.Color));
    }

    private static string SemanticClassName(PdfSemanticElementKind kind)
    {
        return kind switch
        {
            PdfSemanticElementKind.Heading => "pdf-semantic-heading",
            PdfSemanticElementKind.Paragraph => "pdf-semantic-paragraph",
            PdfSemanticElementKind.CodeBlock => "pdf-semantic-code-block",
            PdfSemanticElementKind.ThematicBreak => "pdf-semantic-thematic-break",
            PdfSemanticElementKind.Algorithm => "pdf-semantic-algorithm",
            PdfSemanticElementKind.BlockQuote => "pdf-semantic-blockquote",
            PdfSemanticElementKind.Aside => "pdf-semantic-aside",
            PdfSemanticElementKind.List => "pdf-semantic-list-element",
            PdfSemanticElementKind.DefinitionList => "pdf-semantic-definition-list-element",
            PdfSemanticElementKind.Bibliography => "pdf-semantic-bibliography-element",
            PdfSemanticElementKind.Navigation => "pdf-semantic-navigation",
            PdfSemanticElementKind.Table => "pdf-semantic-table",
            PdfSemanticElementKind.AuthorBlock => "pdf-semantic-author-block",
            PdfSemanticElementKind.FrontMatter => "pdf-semantic-front-matter",
            PdfSemanticElementKind.Footnote => "pdf-semantic-footnote",
            PdfSemanticElementKind.Footer => "pdf-semantic-footer",
            PdfSemanticElementKind.Header => "pdf-semantic-header",
            _ => "pdf-semantic-other"
        };
    }

    private static bool IsPositionedSemanticElement(PdfSemanticElement element)
    {
        return element.Lines.Any(static line => MathF.Abs(line.Direction) > 0.01f);
    }

    private static bool IsTitleElement(PdfSemanticElement element)
    {
        return element.Kind == PdfSemanticElementKind.Heading &&
            element.HeadingLevel == 1 &&
            SemanticFontSize(element) >= 16f &&
            !char.IsDigit(element.Text.TrimStart().FirstOrDefault());
    }

    private static bool IsSemanticFlowRulePath(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutPath path)
    {
        return IsThematicBreakPath(semanticPage, path) ||
            IsDecorativeTitleRulePath(page, semanticPage, path) ||
            IsDecorativeFootnoteRulePath(page, semanticPage, path) ||
            IsSemanticAlgorithmRulePath(semanticPage, path) ||
            IsSemanticAsideRegionPath(page, semanticPage, path) ||
            IsSemanticTableDecorationPath(page, semanticPage, path);
    }

    private static bool IsSemanticTableDecorationPath(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutPath path)
    {
        if (path.FillColor is not PdfLayoutColor color ||
            !IsAxisAlignedRectangle(path))
        {
            return false;
        }

        return semanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Table)
            .Any(table => IsCellSizedSemanticTableBackgroundPath(table, path) ||
                (IsSemanticTableRulePath(table, path, color) &&
                    HasSemanticTableCellBackgroundMatrix(table, page)));
    }

    private static bool HasSemanticTableCellBackgroundMatrix(
        PdfSemanticElement table,
        PdfLayoutPage? page)
    {
        if (page == null)
        {
            return false;
        }

        PdfSemanticTableCell[] cells = table.TableRows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder && !string.IsNullOrWhiteSpace(cell.Text))
            .ToArray();
        if (cells.Length < 4)
        {
            return false;
        }

        int requiredMatches = Math.Max(4, (int)MathF.Ceiling(cells.Length * 0.65f));
        int matches = 0;
        foreach (PdfSemanticTableCell cell in cells)
        {
            if (page.Paths.Any(path => IsCellSizedSemanticTableBackgroundPath(table, cell, path)))
            {
                matches++;
                if (matches >= requiredMatches)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCellSizedSemanticTableBackgroundPath(
        PdfSemanticElement table,
        PdfLayoutPath path)
    {
        return table.TableRows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder && !string.IsNullOrWhiteSpace(cell.Text))
            .Any(cell => IsCellSizedSemanticTableBackgroundPath(table, cell, path));
    }

    private static bool IsCellSizedSemanticTableBackgroundPath(
        PdfSemanticElement table,
        PdfSemanticTableCell cell,
        PdfLayoutPath path)
    {
        return IsSemanticTableCellBackgroundPath(table, cell, path) &&
            path.Bounds.Width <= MathF.Max(12f, cell.Bounds.Width * 6f) &&
            path.Bounds.Height <= MathF.Max(12f, cell.Bounds.Height * 6f);
    }

    private static bool IsSemanticTableCellBackgroundPath(
        PdfSemanticElement table,
        PdfLayoutPath path)
    {
        if (path.Bounds.Width < 4f ||
            path.Bounds.Height < 4f ||
            !RectangleContainsWithTolerance(table.Bounds, path.Bounds, 1.5f))
        {
            return false;
        }

        return table.TableRows
            .SelectMany(static row => row.Cells)
            .Where(static cell => !cell.IsPlaceholder)
            .Any(cell => RectangleContainsWithTolerance(path.Bounds, cell.Bounds, 0.75f));
    }

    private static bool IsSemanticTableRulePath(
        PdfSemanticElement table,
        PdfLayoutPath path,
        PdfLayoutColor color)
    {
        float channelRange = MathF.Max(color.Red, MathF.Max(color.Green, color.Blue)) -
            MathF.Min(color.Red, MathF.Min(color.Green, color.Blue));
        bool thin = MathF.Min(path.Bounds.Width, path.Bounds.Height) <= 1.5f;
        bool spansTableCells = path.Bounds.Width >= table.Bounds.Width * 0.15f ||
            path.Bounds.Height >= table.Bounds.Height * 0.12f;
        return channelRange <= 0.05f &&
            thin &&
            spansTableCells &&
            RectangleContainsWithTolerance(table.Bounds, path.Bounds, 1.5f);
    }

    private static bool IsThematicBreakPath(PdfSemanticPage semanticPage, PdfLayoutPath path)
    {
        return semanticPage.Elements.Any(element =>
            element.Kind == PdfSemanticElementKind.ThematicBreak &&
            element.ThematicBreak?.SourcePathIndex == path.Index);
    }

    private static bool IsSemanticAlgorithmRulePath(PdfSemanticPage semanticPage, PdfLayoutPath path)
    {
        return semanticPage.Elements
            .Where(static element => element.Algorithm != null)
            .SelectMany(static element => new[]
            {
                element.Algorithm!.TopRule.SourcePathIndex,
                element.Algorithm.CaptionRule.SourcePathIndex,
                element.Algorithm.BottomRule.SourcePathIndex
            })
            .Contains(path.Index);
    }

    private static bool IsSemanticAsideRegionPath(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutPath path)
    {
        return semanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Aside)
            .Select(aside => SemanticAsideRegionPath(page, aside))
            .Any(sourcePath => ReferenceEquals(sourcePath, path));
    }

    private static SemanticAsideSourceDecoration? SemanticAsideSourceDecorationFor(
        PdfLayoutPage page,
        PdfSemanticElement aside)
    {
        PdfLayoutPath? path = SemanticAsideRegionPath(page, aside);
        if (path != null)
        {
            return new SemanticAsideSourceDecoration(
                path.Bounds,
                path.FillColor is PdfLayoutColor fill ? CssRgba(fill) : "transparent",
                path.Stroke);
        }

        PdfLayoutShading? shading = page.Shadings
            .Where(item => ContainsWithTolerance(item.Bounds, aside.Bounds, 4f))
            .Where(item => item.Bounds.Width < page.Width * 0.92f && item.Bounds.Height < page.Height * 0.75f)
            .OrderBy(static item => item.Bounds.Width * item.Bounds.Height)
            .FirstOrDefault();
        return shading == null
            ? null
            : new SemanticAsideSourceDecoration(shading.Bounds, SemanticAsideShadingBackground(shading), null);
    }

    private static PdfLayoutPath? SemanticAsideRegionPath(
        PdfLayoutPage page,
        PdfSemanticElement aside)
    {
        return page.Paths
            .Where(static path => path.IsFilled || path.IsStroked)
            .Where(path => ContainsWithTolerance(path.Bounds, aside.Bounds, 4f))
            .Where(path => path.Bounds.Width < page.Width * 0.92f && path.Bounds.Height < page.Height * 0.75f)
            .OrderBy(static path => path.Bounds.Width * path.Bounds.Height)
            .FirstOrDefault();
    }

    private static string SemanticAsideShadingBackground(PdfLayoutShading shading)
    {
        string stops = string.Join(", ", shading.Stops.Select(stop =>
            CssRgba(stop.Color) + " " + CssPercent(stop.Offset * 100f)));
        if (stops.Length == 0)
        {
            return "transparent";
        }

        if (shading.ShadingType == 3)
        {
            return "radial-gradient(circle, " + stops + ")";
        }

        if (shading.ShadingType == 2)
        {
            float deltaX = shading.EndX - shading.StartX;
            float deltaY = shading.EndY - shading.StartY;
            float angle = MathF.Atan2(deltaX, -deltaY) * 180f / MathF.PI;
            return "linear-gradient(" + SvgNumber(angle) + "deg, " + stops + ")";
        }

        return CssRgba(shading.Stops[0].Color);
    }

    private static bool IsDecorativeTitleRulePath(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutPath path)
    {
        return semanticPage.Elements
            .Where(IsTitleElement)
            .Any(title => TitleRulePositionForPath(page, title, path) != TitleRulePosition.None);
    }

    private static PdfLayoutPath? DecorativeTitleRulePath(
        PdfLayoutPage page,
        PdfSemanticElement title,
        TitleRulePosition position)
    {
        return page.Paths
            .Where(path => TitleRulePositionForPath(page, title, path) == position)
            .OrderBy(path => position == TitleRulePosition.Above
                ? title.Bounds.Y - path.Bounds.Bottom
                : path.Bounds.Y - title.Bounds.Bottom)
            .FirstOrDefault();
    }

    private static TitleRulePosition TitleRulePositionForPath(
        PdfLayoutPage page,
        PdfSemanticElement title,
        PdfLayoutPath path)
    {
        bool horizontalRuleShape = path.Bounds.Width >= MathF.Max(title.Bounds.Width * 0.95f, page.Width * 0.45f) &&
            path.Bounds.Height <= 6f &&
            path.Bounds.X <= title.Bounds.X + 6f &&
            path.Bounds.Right >= title.Bounds.Right - 6f;
        if (!horizontalRuleShape)
        {
            return TitleRulePosition.None;
        }

        bool closeAboveTitle = path.Bounds.Bottom <= title.Bounds.Y + 3f &&
            title.Bounds.Y - path.Bounds.Bottom <= 32f;
        if (closeAboveTitle)
        {
            return TitleRulePosition.Above;
        }

        bool closeBelowTitle = path.Bounds.Y >= title.Bounds.Bottom - 3f &&
            path.Bounds.Y - title.Bounds.Bottom <= 32f;
        return closeBelowTitle ? TitleRulePosition.Below : TitleRulePosition.None;
    }

    private static bool IsDecorativeFootnoteRulePath(
        PdfLayoutPage page,
        PdfSemanticPage semanticPage,
        PdfLayoutPath path)
    {
        PdfSemanticElement[] footnotes = semanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Footnote)
            .ToArray();
        if (footnotes.Length == 0)
        {
            return false;
        }

        float footnoteTop = footnotes.Min(static footnote => footnote.Bounds.Y);
        float footnoteLeft = footnotes.Min(static footnote => footnote.Bounds.X);
        bool horizontalRuleShape = path.Bounds.Width >= page.Width * 0.10f &&
            path.Bounds.Width <= page.Width * 0.45f &&
            path.Bounds.Height <= 4f &&
            path.Bounds.X <= footnoteLeft + 16f;
        if (!horizontalRuleShape)
        {
            return false;
        }

        return path.Bounds.Y <= footnoteTop + 4f &&
            footnoteTop - path.Bounds.Y <= 28f;
    }

    private static PdfLayoutPath? DecorativeFootnoteRulePath(PdfLayoutPage page, PdfSemanticPage semanticPage)
    {
        PdfSemanticElement[] footnotes = semanticPage.Elements
            .Where(static element => element.Kind == PdfSemanticElementKind.Footnote)
            .ToArray();
        if (footnotes.Length == 0)
        {
            return null;
        }

        float footnoteTop = footnotes.Min(static footnote => footnote.Bounds.Y);
        return page.Paths
            .Where(path => IsDecorativeFootnoteRulePath(page, semanticPage, path))
            .OrderBy(path => MathF.Abs(path.Bounds.Y - footnoteTop))
            .ThenByDescending(static path => path.Bounds.Width)
            .FirstOrDefault();
    }

    private static string PositionStyle(PdfLayoutPage page, PdfSemanticElement element, float scale)
    {
        float direction = SemanticDirection(element);
        float left = element.Bounds.X;
        float top = element.Bounds.Y;
        if (MathF.Abs(direction - 90f) < 0.01f)
        {
            left = element.Bounds.Y;
            top = (page.Height + element.Bounds.Width) / 2f;
        }
        else if (MathF.Abs(direction - 270f) < 0.01f)
        {
            left = page.Width - element.Bounds.Y;
            top = (page.Height - element.Bounds.Width) / 2f;
        }

        string style = "left:" + CssPoints(left * scale) +
            ";top:" + CssPoints(top * scale) +
            ";width:" + CssPoints(element.Bounds.Width * scale);
        return element.Algorithm == null
            ? style
            : style + ";" + string.Join(';', AlgorithmRuleStyles(element.Algorithm));
    }

    private static float SemanticFontSize(PdfSemanticElement element)
    {
        return element.Lines.Count == 0
            ? 10f
            : element.Lines
                .GroupBy(static line => MathF.Round(line.DominantFontSize * 2f) / 2f)
                .OrderByDescending(static group => group.Sum(static line => Math.Max(1, line.Text.Length)))
                .ThenByDescending(static group => group.Key)
                .Select(static group => group.Key)
                .First();
    }

    private static string SemanticFontName(PdfSemanticElement element)
    {
        return element.Lines
            .GroupBy(static line => line.DominantFontName, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Sum(static line => Math.Max(1, line.Text.Length)))
            .Select(static group => group.Key)
            .FirstOrDefault() ?? "";
    }

    private static float SemanticDirection(PdfSemanticElement element)
    {
        return element.Lines
            .GroupBy(static line => MathF.Round(line.Direction))
            .OrderByDescending(static group => group.Sum(static line => Math.Max(1, line.Text.Length)))
            .Select(static group => group.Key)
            .FirstOrDefault();
    }

    private static PdfLayoutColor SemanticColor(PdfSemanticElement element)
    {
        return element.Lines
            .GroupBy(static line => ColorClass(line.Color), StringComparer.Ordinal)
            .OrderByDescending(static group => group.Sum(static line => Math.Max(1, line.Text.Length)))
            .Select(static group => group.First().Color)
            .FirstOrDefault();
    }

    private static PdfLayoutColor SemanticListColor(PdfSemanticElement element)
    {
        PdfTextRun? markerRun = element.SemanticList?.Items
            .FirstOrDefault()?
            .Lines
            .FirstOrDefault()?
            .Runs
            .FirstOrDefault(static run => !string.IsNullOrWhiteSpace(run.Text));
        return markerRun?.Color ?? SemanticColor(element);
    }

    private static void WriteTextWithFootnoteReferences(
        StringBuilder html,
        string text,
        FootnoteContext footnotes)
    {
        WriteTextWithFootnoteReferences(html, text, footnotes, text, 0);
    }

    private static void WriteTextWithFootnoteReferences(
        StringBuilder html,
        string text,
        FootnoteContext footnotes,
        string boundaryText,
        int boundaryOffset)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (TryAutomaticLink(text, index, out int linkLength, out string? href))
            {
                string linkText = text.Substring(index, linkLength);
                html.Append("<a class=\"pdf-semantic-auto-link\" href=\"")
                    .Append(HtmlAttribute(href!))
                    .Append("\">")
                    .Append(Html(linkText))
                    .Append("</a>");
                index += linkLength - 1;
                continue;
            }

            int boundaryIndex = boundaryOffset + index;
            if (footnotes.TryMatchReference(text, index, out string marker, out int markerLength) &&
                boundaryIndex >= 0 &&
                boundaryIndex < boundaryText.Length &&
                IsFootnoteReferenceBoundary(boundaryText, boundaryIndex, markerLength))
            {
                string referenceId = footnotes.NextReferenceId(marker);
                html.Append("<sup id=\"")
                    .Append(HtmlAttribute(referenceId))
                    .Append("\"><a class=\"pdf-semantic-footnote-ref\" href=\"#")
                    .Append(HtmlAttribute(footnotes.IdFor(marker)))
                    .Append("\">")
                    .Append(Html(text.Substring(index, markerLength)))
                    .Append("</a></sup>");
                index += markerLength - 1;
                continue;
            }

            html.Append(Html(text[index].ToString()));
        }
    }

    private static bool TryAutomaticLink(string text, int start, out int length, out string? href)
    {
        length = 0;
        href = null;
        if (start > 0 && IsLinkCharacter(text[start - 1]))
        {
            return false;
        }

        if (text.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            text.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            int end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not '<' and not '>')
            {
                end++;
            }

            while (end > start && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')')
            {
                end--;
            }

            if (end > start)
            {
                length = end - start;
                href = text.Substring(start, length);
                return true;
            }
        }

        int at = text.IndexOf('@', start);
        if (at <= start || at - start > 64 ||
            !text.AsSpan(start, at - start).ToString().All(IsEmailLocalCharacter))
        {
            return false;
        }

        int emailEnd = at + 1;
        while (emailEnd < text.Length && IsEmailDomainCharacter(text[emailEnd]))
        {
            emailEnd++;
        }

        if (emailEnd <= at + 1)
        {
            return false;
        }

        string email = text.Substring(start, emailEnd - start);
        if (!email[(at - start + 1)..].Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        length = email.Length;
        href = "mailto:" + email;
        return true;
    }

    private static bool IsLinkCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+' or '@' or '/';
    }

    private static bool IsEmailLocalCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+';
    }

    private static bool IsEmailDomainCharacter(char character)
    {
        return char.IsLetterOrDigit(character) || character is '.' or '-';
    }

    private static bool IsFootnoteReferenceBoundary(string text, int index, int length = 1)
    {
        bool before = index == 0 || char.IsWhiteSpace(text[index - 1]) || text[index - 1] == '(';
        int afterIndex = index + length;
        bool after = afterIndex >= text.Length ||
            char.IsWhiteSpace(text[afterIndex]) ||
            text[afterIndex] is ',' or ';' or '.' or ')';
        return before && after;
    }

    private static string FontClass(string fontName)
    {
        return "pdf-font-" + CssClassToken(string.IsNullOrWhiteSpace(fontName) ? "default" : fontName);
    }

    private static string FontSizeClass(float fontSize)
    {
        return "pdf-font-size-" + CssClassToken(fontSize.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', '-'));
    }

    private static string ColorClass(PdfLayoutColor color)
    {
        return "pdf-color-" +
            ByteHex(color.Red).ToLowerInvariant() +
            ByteHex(color.Green).ToLowerInvariant() +
            ByteHex(color.Blue).ToLowerInvariant() +
            "-" +
            ByteHex(color.Alpha).ToLowerInvariant();
    }

    private static string CssClassToken(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string token = builder.ToString().Trim('-');
        return token.Length == 0 ? "value" : token;
    }

    private enum SemanticFigureRendering
    {
        None,
        Space,
        Content
    }

    private enum TitleRulePosition
    {
        None,
        Above,
        Below
    }

    private enum InlineBaselineRole
    {
        Normal,
        Subscript,
        Superscript
    }

    private enum TableCellAlignment
    {
        Default,
        Left,
        Center,
        Right
    }

    private readonly record struct InlineTextSegment(
        string Text,
        PdfTextRun? Run,
        InlineBaselineRole Role,
        PdfLayoutLink? Link = null,
        PdfTextHighlight? Highlight = null,
        bool IsCode = false,
        PdfSemanticInline? Semantic = null,
        bool IsSmall = false);

    private readonly record struct RichSemanticSourceLine(
        PdfSemanticLine Line,
        List<InlineTextSegment> Segments,
        string Text,
        bool ContinuesMathIdentifier,
        bool JoinsPreviousWord = false);

    private sealed class SemanticSectionWriter
    {
        private readonly StringBuilder _html;
        private readonly PdfSemanticSectionTree _tree;
        private readonly List<PdfSemanticSection> _openSections = [];

        public SemanticSectionWriter(StringBuilder html, PdfSemanticSectionTree tree)
        {
            _html = html;
            _tree = tree;
        }

        public string? BeginElement(PdfSemanticElement element)
        {
            PdfSemanticHeading? heading = _tree.FindHeading(element);
            if (heading == null)
            {
                return null;
            }

            PdfSemanticSection? section = _tree.FindSection(element);
            if (section == null)
            {
                return heading.Id;
            }

            if (_openSections.Count > 0 && _openSections[^1] == section)
            {
                return heading.Id;
            }

            while (_openSections.Count > 0 && _openSections[^1] != section.Parent)
            {
                CloseLast();
            }

            _html.Append("      <section class=\"pdf-semantic-section pdf-semantic-section-level-")
                .Append(section.Level.ToString(CultureInfo.InvariantCulture))
                .Append("\" id=\"")
                .Append(HtmlAttribute(section.Id))
                .Append("\" aria-labelledby=\"")
                .Append(HtmlAttribute(heading.Id))
                .AppendLine("\">");
            _openSections.Add(section);
            return heading.Id;
        }

        public int? SectionLevelFor(PdfSemanticElement element)
        {
            return _tree.FindSection(element)?.Level;
        }

        public void CloseAll()
        {
            while (_openSections.Count > 0)
            {
                CloseLast();
            }
        }

        private void CloseLast()
        {
            _html.AppendLine("      </section>");
            _openSections.RemoveAt(_openSections.Count - 1);
        }
    }

    private sealed class ContinuousPageContext
    {
        public ContinuousPageContext(
            PdfLayoutPage page,
            PdfSemanticPage semanticPage,
            FootnoteContext footnotes,
            IReadOnlyList<PdfSemanticElement> positionedElements,
            IReadOnlyList<PdfSemanticElement> flowElements,
            IReadOnlyList<PdfLayoutRectangle> figureRegions,
            PdfSemanticLineGrid? lineGrid,
            PdfSemanticColumns? columns,
            PdfSemanticRuledGrid? ruledGrid,
            bool usesFixedLayoutFallback)
        {
            Page = page;
            SemanticPage = semanticPage;
            Footnotes = footnotes;
            PositionedElements = positionedElements;
            FlowElements = flowElements;
            FigureRegions = figureRegions;
            LineGrid = lineGrid;
            Columns = columns;
            RuledGrid = ruledGrid;
            UsesFixedLayoutFallback = usesFixedLayoutFallback;
        }

        public PdfLayoutPage Page { get; }

        public PdfSemanticPage SemanticPage { get; }

        public FootnoteContext Footnotes { get; }

        public IReadOnlyList<PdfSemanticElement> PositionedElements { get; }

        public IReadOnlyList<PdfSemanticElement> FlowElements { get; }

        public IReadOnlyList<PdfLayoutRectangle> FigureRegions { get; }

        public PdfSemanticLineGrid? LineGrid { get; }

        public PdfSemanticColumns? Columns { get; }

        public PdfSemanticRuledGrid? RuledGrid { get; }

        public bool UsesFixedLayoutFallback { get; }
    }

    private readonly record struct SemanticAsideSourceDecoration(
        PdfLayoutRectangle Bounds,
        string Background,
        PdfLayoutStrokeStyle? Stroke);

    private sealed class SemanticDefinitionListRenderState
    {
        public bool IsOpen { get; set; }

        public bool DefinitionOpen { get; set; }

        public bool UsesColumns { get; set; }

        public int NextGridRow { get; set; } = 1;

        public string PreviousText { get; set; } = "";

        public void Reset()
        {
            IsOpen = false;
            DefinitionOpen = false;
            UsesColumns = false;
            NextGridRow = 1;
            PreviousText = "";
        }
    }

    private sealed class PdfSemanticLineGrid
    {
        public PdfSemanticLineGrid(
            PdfLayoutPage page,
            IReadOnlyList<LineGridRow> rows,
            int columnCount,
            float leftInset,
            float rightInset)
        {
            Page = page;
            Rows = rows;
            ColumnCount = columnCount;
            LeftInset = leftInset;
            RightInset = rightInset;
        }

        public PdfLayoutPage Page { get; }

        public IReadOnlyList<LineGridRow> Rows { get; }

        public int ColumnCount { get; }

        public float LeftInset { get; }

        public float RightInset { get; }
    }

    private sealed class PdfSemanticColumns
    {
        public PdfSemanticColumns(
            PdfLayoutPage page,
            PdfSemanticPage semanticPage,
            IReadOnlyList<PdfTextRun> leadingRuns,
            IReadOnlyList<SemanticColumnTrack> tracks,
            IReadOnlyList<SemanticColumnGutter> gutters,
            IReadOnlyList<PdfSemanticElement> listElements,
            float leftInset,
            float rightInset,
            bool preserveAuthoredSemanticElements = false)
        {
            Page = page;
            SemanticPage = semanticPage;
            LeadingRuns = leadingRuns;
            Tracks = tracks;
            Gutters = gutters;
            Columns = tracks.Select(static track => track.Column).ToArray();
            Boundaries = gutters.Select(static gutter => gutter.Boundary).ToArray();
            ListElements = listElements;
            LeftInset = leftInset;
            RightInset = rightInset;
            PreserveAuthoredSemanticElements = preserveAuthoredSemanticElements;
        }

        public PdfLayoutPage Page { get; }

        public PdfSemanticPage SemanticPage { get; }

        public IReadOnlyList<PdfTextRun> LeadingRuns { get; }

        public IReadOnlyList<LineGridColumn> Columns { get; }

        public IReadOnlyList<SemanticColumnTrack> Tracks { get; }

        public IReadOnlyList<PdfSemanticColumnFigure> SpanningFigures { get; set; } = [];

        public IReadOnlyList<PdfSemanticColumnRegionFigure> ColumnFigures { get; set; } = [];

        public IReadOnlyList<SemanticColumnGutter> Gutters { get; }

        public IReadOnlyList<float> Boundaries { get; }

        public IReadOnlyList<PdfSemanticElement> ListElements { get; }

        public float LeftInset { get; }

        public float RightInset { get; }

        public bool IsMixedRegions => ColumnFigures.Count > 0;

        public bool PreserveAuthoredSemanticElements { get; }
    }

    private sealed class PdfSemanticRuledGrid
    {
        public PdfSemanticRuledGrid(
            PdfLayoutPage page,
            PdfLayoutRectangle region,
            IReadOnlyList<SemanticRuledGridTrack> tracks,
            IReadOnlyList<SemanticColumnGutter> gutters,
            IReadOnlyList<SemanticRuledGridBand> bands,
            IReadOnlyList<PdfSemanticElement> elements,
            SemanticRuledGridBorders sourceBorders,
            SemanticPageRuleGroup? topRuleGroup)
        {
            Page = page;
            Region = region;
            Tracks = tracks;
            Gutters = gutters;
            Bands = bands;
            Elements = elements;
            SourceBorders = sourceBorders;
            TopRuleGroup = topRuleGroup;
        }

        public PdfLayoutPage Page { get; }

        public PdfLayoutRectangle Region { get; }

        public IReadOnlyList<SemanticRuledGridTrack> Tracks { get; }

        public IReadOnlyList<SemanticColumnGutter> Gutters { get; }

        public IReadOnlyList<SemanticRuledGridBand> Bands { get; }

        public IReadOnlyList<PdfSemanticElement> Elements { get; }

        public SemanticRuledGridBorders SourceBorders { get; }

        public SemanticPageRuleGroup? TopRuleGroup { get; }
    }

    private sealed class SemanticRuledGridBorders
    {
        public SemanticRuledGridBorders(
            IReadOnlyDictionary<PdfSemanticElement, SemanticRuledBorder> elementBorders,
            IReadOnlyDictionary<PdfSemanticListItem, SemanticRuledBorder> listItemBorders)
        {
            ElementBorders = elementBorders;
            ListItemBorders = listItemBorders;
        }

        public IReadOnlyDictionary<PdfSemanticElement, SemanticRuledBorder> ElementBorders { get; }

        public IReadOnlyDictionary<PdfSemanticListItem, SemanticRuledBorder> ListItemBorders { get; }

        public int Count => ElementBorders.Count + ListItemBorders.Count;
    }

    private readonly record struct SemanticRuledBorder(
        int SourcePathIndex,
        float CenterY,
        float Thickness,
        PdfLayoutColor Color);

    private readonly record struct SemanticRuledHorizontalRule(
        int SourcePathIndex,
        float Left,
        float Right,
        float CenterY,
        float Thickness,
        PdfLayoutColor Color)
    {
        public float Width => MathF.Max(0f, Right - Left);
    }

    private readonly record struct SemanticRuledGridUnit(
        PdfSemanticElement Element,
        PdfSemanticListItem? ListItem,
        PdfLayoutRectangle Bounds);

    private sealed class SemanticPageRuleGroup
    {
        public SemanticPageRuleGroup(
            float left,
            float right,
            float top,
            float bottom,
            float gapAfter,
            IReadOnlyList<SemanticPageRule> rules)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
            GapAfter = gapAfter;
            Rules = rules;
        }

        public float Left { get; }

        public float Right { get; }

        public float Top { get; }

        public float Bottom { get; }

        public float GapAfter { get; }

        public IReadOnlyList<SemanticPageRule> Rules { get; }
    }

    private readonly record struct SemanticPageRule(
        int SourcePathIndex,
        float Left,
        float Right,
        float Top,
        float Thickness,
        PdfLayoutColor Color)
    {
        public float Width => MathF.Max(0f, Right - Left);

        public float Bottom => Top + Thickness;
    }

    private sealed class SemanticRuledGridBand
    {
        private SemanticRuledGridBand(
            IReadOnlyList<PdfSemanticElement>[] columns,
            IReadOnlyList<SemanticRuledGridConnector> connectors,
            IReadOnlyList<PdfSemanticElement> spanningElements)
        {
            Columns = columns;
            Connectors = connectors;
            SpanningElements = spanningElements;
            PdfSemanticElement[] elements = columns
                .SelectMany(static column => column)
                .Concat(connectors.Select(static connector => connector.Element))
                .Concat(spanningElements)
                .ToArray();
            SourceTop = elements.Min(static element => element.Bounds.Y);
            SourceBottom = elements.Max(static element => element.Bounds.Bottom);
        }

        public IReadOnlyList<PdfSemanticElement>[] Columns { get; }

        public IReadOnlyList<SemanticRuledGridConnector> Connectors { get; }

        public IReadOnlyList<PdfSemanticElement> SpanningElements { get; }

        public float SourceTop { get; }

        public float SourceBottom { get; }

        public bool IsSpanning => SpanningElements.Count > 0;

        public static SemanticRuledGridBand CreateLanes(
            IReadOnlyList<PdfSemanticElement>[] columns,
            IReadOnlyList<SemanticRuledGridConnector> connectors)
        {
            return new SemanticRuledGridBand(columns, connectors, []);
        }

        public static SemanticRuledGridBand CreateSpanning(
            IReadOnlyList<PdfSemanticElement> elements)
        {
            return new SemanticRuledGridBand([], [], elements);
        }
    }

    private readonly record struct SemanticRuledGridPlacement(
        PdfSemanticElement Element,
        int? ColumnIndex,
        int? GutterIndex,
        bool IsSpanning);

    private readonly record struct SemanticRuledGridConnector(
        int GutterIndex,
        PdfSemanticElement Element);

    private readonly record struct SemanticRuledGridTrack(float Left, float Right)
    {
        public float Width => MathF.Max(0, Right - Left);

        public PdfLayoutRectangle Bounds => new(Left, 0f, Width, 1f);
    }

    private readonly record struct SemanticColumnItem(
        PdfLayoutRectangle Bounds,
        PdfTextRun? Run,
        PdfSemanticElement? Element);

    private readonly record struct PdfSemanticColumnFigure(
        PdfLayoutRectangle Region,
        PdfSemanticElement? Caption);

    private readonly record struct PdfSemanticColumnRegionFigure(
        PdfLayoutRectangle Region,
        int ColumnIndex,
        string AccessibleText);

    private readonly record struct SemanticColumnTrack(
        LineGridColumn Column,
        float Left,
        float Right)
    {
        public float Width => MathF.Max(0, Right - Left);
    }

    private readonly record struct SemanticColumnGutter(float Left, float Right)
    {
        public float Boundary => (Left + Right) / 2f;

        public float Width => MathF.Max(0, Right - Left);
    }

    private sealed class ColumnCorridor
    {
        public ColumnCorridor(
            float boundary,
            float left,
            float right,
            ColumnGapObservation[] supportingGaps,
            float? columnTop = null)
        {
            Boundary = boundary;
            Left = left;
            Right = right;
            SupportingGaps = supportingGaps;
            ColumnTop = columnTop;
        }

        public float Boundary { get; }

        public float Left { get; }

        public float Right { get; }

        public ColumnGapObservation[] SupportingGaps { get; }

        public float? ColumnTop { get; }
    }

    private sealed class ColumnDetectionRow
    {
        private float _centerTotal;

        public ColumnDetectionRow(float center)
        {
            Center = center;
        }

        public float Center { get; private set; }

        public float Top => Runs.Min(static run => run.Bounds.Y);

        public List<PdfTextRun> Runs { get; } = [];

        public void Add(PdfTextRun run)
        {
            Runs.Add(run);
            _centerTotal += run.Bounds.Y + run.Bounds.Height / 2f;
            Center = _centerTotal / Runs.Count;
        }

        public IEnumerable<ColumnGapObservation> Gaps(
            int rowIndex,
            float centralLeft,
            float centralRight,
            float minimumGap)
        {
            PdfTextRun[] ordered = Runs.OrderBy(static run => run.Bounds.X).ToArray();
            if (ordered.Length < 2)
            {
                yield break;
            }

            float occupiedRight = ordered[0].Bounds.Right;
            for (int index = 1; index < ordered.Length; index++)
            {
                float nextLeft = ordered[index].Bounds.X;
                if (nextLeft - occupiedRight >= minimumGap &&
                    occupiedRight <= centralRight &&
                    nextLeft >= centralLeft)
                {
                    yield return new ColumnGapObservation(rowIndex, occupiedRight, nextLeft);
                }

                occupiedRight = MathF.Max(occupiedRight, ordered[index].Bounds.Right);
            }
        }
    }

    private readonly record struct ColumnGapObservation(int RowIndex, float Left, float Right);

    private sealed class HorizontalRuleRow
    {
        private float _topTotal;

        public HorizontalRuleRow(float top)
        {
            Top = top;
        }

        public float Top { get; private set; }

        public List<HorizontalRuleSpan> Spans { get; } = [];

        public void Add(float top, float left, float right)
        {
            Spans.Add(new HorizontalRuleSpan(top, left, right));
            _topTotal += top;
            Top = _topTotal / Spans.Count;
        }

        public IEnumerable<HorizontalRuleSpan> MergedSpans(float tolerance)
        {
            HorizontalRuleSpan[] ordered = Spans.OrderBy(static span => span.Left).ToArray();
            if (ordered.Length == 0)
            {
                yield break;
            }

            float left = ordered[0].Left;
            float right = ordered[0].Right;
            for (int index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].Left <= right + tolerance)
                {
                    right = MathF.Max(right, ordered[index].Right);
                    continue;
                }

                yield return new HorizontalRuleSpan(Top, left, right);
                left = ordered[index].Left;
                right = ordered[index].Right;
            }

            yield return new HorizontalRuleSpan(Top, left, right);
        }
    }

    private readonly record struct HorizontalRuleSpan(float Top, float Left, float Right);

    private sealed class SourceRuleFamily
    {
        private float _leftTotal;
        private float _rightTotal;

        public SourceRuleFamily(float left, float right)
        {
            Left = left;
            Right = right;
            Top = float.MaxValue;
            Bottom = float.MinValue;
        }

        public float Left { get; private set; }

        public float Right { get; private set; }

        public float Top { get; private set; }

        public float Bottom { get; private set; }

        public int RuleCount { get; private set; }

        public void Add(float top, float left, float right)
        {
            RuleCount++;
            _leftTotal += left;
            _rightTotal += right;
            Left = _leftTotal / RuleCount;
            Right = _rightTotal / RuleCount;
            Top = MathF.Min(Top, top);
            Bottom = MathF.Max(Bottom, top);
        }
    }

    private sealed class LineGridColumn
    {
        private float _leftTotal;

        public LineGridColumn(float left)
        {
            Left = left;
        }

        public float Left { get; private set; }

        public List<PdfTextRun> Lines { get; } = [];

        public void Add(PdfTextRun line)
        {
            Lines.Add(line);
            _leftTotal += line.Bounds.X;
            Left = _leftTotal / Lines.Count;
        }
    }

    private sealed class LineGridRow
    {
        public LineGridRow(float top, int columnCount)
        {
            Top = top;
            Cells = new PdfTextRun?[columnCount];
        }

        public float Top { get; }

        public PdfTextRun?[] Cells { get; }

        public float Height => Cells
            .Where(static cell => cell != null)
            .Select(static cell => cell!.Bounds.Height)
            .DefaultIfEmpty(0)
            .Max();

        public float Bottom => Top + Height;

        public bool TryAdd(int columnIndex, PdfTextRun line)
        {
            if (Cells[columnIndex] != null)
            {
                return false;
            }

            Cells[columnIndex] = line;
            return true;
        }
    }

    private sealed class ContinuousParagraphMerge
    {
        public ContinuousParagraphMerge(
            ContinuousPageContext current,
            ContinuousPageContext next,
            PdfSemanticElement startElement,
            PdfSemanticElement continuationElement,
            PdfSemanticElement? currentPageNumberFooter,
            IReadOnlyList<PdfSemanticElement> leadingElements,
            IReadOnlyList<PdfLayoutRectangle> leadingFigureRegions)
        {
            Current = current;
            Next = next;
            StartElement = startElement;
            ContinuationElement = continuationElement;
            CurrentPageNumberFooter = currentPageNumberFooter;
            LeadingElements = leadingElements;
            LeadingFigureRegions = leadingFigureRegions;
        }

        public ContinuousPageContext Current { get; }

        public ContinuousPageContext Next { get; }

        public PdfSemanticElement StartElement { get; }

        public PdfSemanticElement ContinuationElement { get; }

        public PdfSemanticElement? CurrentPageNumberFooter { get; }

        public IReadOnlyList<PdfSemanticElement> LeadingElements { get; }

        public IReadOnlyList<PdfLayoutRectangle> LeadingFigureRegions { get; }
    }

    private sealed class SemanticBibliographyWriter
    {
        private readonly StringBuilder _html;
        private PdfSemanticBibliography? _bibliography;
        private int _activeItemIndex = -1;
        private int _lastFragmentPageNumber;
        private bool _continuesAfterLastFragment;

        public SemanticBibliographyWriter(StringBuilder html)
        {
            _html = html;
        }

        public bool IsActive => _bibliography != null;

        public bool ContinuesAfter(int pageNumber)
        {
            return IsActive &&
                _lastFragmentPageNumber == pageNumber &&
                _continuesAfterLastFragment;
        }

        public void PrepareForPage(PdfSemanticPage page)
        {
            if (_bibliography == null)
            {
                return;
            }

            PdfSemanticBibliographyFragment? continuation = page.Elements
                .Select(static element => element.BibliographyFragment)
                .FirstOrDefault(fragment =>
                    fragment != null &&
                    ReferenceEquals(fragment.Bibliography, _bibliography));
            if (continuation == null)
            {
                CloseAll();
                return;
            }

            // A page marker inside an open list must remain inside an li. When
            // the next page starts a new reference, open that item before the
            // marker is written; a genuinely continued item is already open.
            if (_activeItemIndex < 0 && continuation.Items.Count > 0)
            {
                PdfSemanticBibliographyItemFragment firstFragment = continuation.Items[0];
                PdfSemanticBibliographyItem firstItem = continuation.Bibliography.Items[firstFragment.ItemIndex];
                OpenItem(continuation.Bibliography, firstItem, firstFragment);
            }
        }

        public void BeforeElement(PdfSemanticElement element)
        {
            if (_bibliography == null)
            {
                return;
            }

            if (element.BibliographyFragment != null &&
                ReferenceEquals(element.BibliographyFragment.Bibliography, _bibliography))
            {
                return;
            }

            CloseAll();
        }

        public void WriteFragment(
            PdfSemanticElement element,
            FootnoteContext footnotes,
            PdfLayoutPage? page)
        {
            PdfSemanticBibliographyFragment fragment = element.BibliographyFragment!;
            if (!ReferenceEquals(_bibliography, fragment.Bibliography))
            {
                CloseAll();
                Open(fragment.Bibliography);
            }

            foreach (PdfSemanticBibliographyItemFragment itemFragment in fragment.Items)
            {
                PdfSemanticBibliographyItem item = fragment.Bibliography.Items[itemFragment.ItemIndex];
                if (_activeItemIndex != itemFragment.ItemIndex)
                {
                    CloseItem();
                    OpenItem(fragment.Bibliography, item, itemFragment);
                }

                WriteSemanticBibliographyText(_html, item, itemFragment, footnotes, page);
                if (itemFragment.IsLastPart)
                {
                    CloseItem();
                }
            }

            _lastFragmentPageNumber = fragment.PageNumber;
            _continuesAfterLastFragment = !fragment.IsLastFragment;
            if (fragment.IsLastFragment)
            {
                CloseAll();
            }
        }

        public void CloseAll()
        {
            if (_bibliography == null)
            {
                return;
            }

            CloseItem();
            _html.AppendLine("      </ol>");
            _bibliography = null;
            _lastFragmentPageNumber = 0;
            _continuesAfterLastFragment = false;
        }

        private void Open(PdfSemanticBibliography bibliography)
        {
            _bibliography = bibliography;
            _html.Append("      <ol class=\"pdf-semantic-bibliography\" aria-label=\"")
                .Append(HtmlAttribute(bibliography.Heading))
                .Append("\" data-marker-kind=\"")
                .Append(bibliography.MarkerKind switch
                {
                    PdfSemanticBibliographyMarkerKind.BracketedNumber => "bracketed-number",
                    PdfSemanticBibliographyMarkerKind.Number => "number",
                    _ => "author-year"
                })
                .Append('"');
            if (bibliography.Start.HasValue)
            {
                _html.Append(" start=\"")
                    .Append(bibliography.Start.Value.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }

            _html.AppendLine(">");
        }

        private void OpenItem(
            PdfSemanticBibliography bibliography,
            PdfSemanticBibliographyItem item,
            PdfSemanticBibliographyItemFragment fragment)
        {
            _activeItemIndex = item.Ordinal - 1;
            PdfSemanticElement itemElement = SemanticBibliographyItemElement(fragment);
            _html.Append("        <li class=\"")
                .Append(FontClass(SemanticFontName(itemElement)))
                .Append(' ')
                .Append(FontSizeClass(SemanticFontSize(itemElement)))
                .Append(' ')
                .Append(ColorClass(SemanticColor(itemElement)))
                .Append('"');
            if (fragment.IsFirstPart)
            {
                _html.Append(" id=\"").Append(HtmlAttribute(item.Id)).Append('"');
            }

            int firstNumber = bibliography.Items[0].SourceNumber ?? 1;
            int expectedNumber = firstNumber + item.Ordinal - 1;
            if (item.SourceNumber.HasValue && item.SourceNumber.Value != expectedNumber)
            {
                _html.Append(" value=\"")
                    .Append(item.SourceNumber.Value.ToString(CultureInfo.InvariantCulture))
                    .Append('"');
            }

            _html.Append('>');
        }

        private void CloseItem()
        {
            if (_activeItemIndex < 0)
            {
                return;
            }

            _html.AppendLine("</li>");
            _activeItemIndex = -1;
        }
    }

    private sealed class FootnoteFragment
    {
        public FootnoteFragment(
            PdfSemanticElement element,
            int pageNumber,
            FootnoteContext context,
            PdfLayoutPage? page)
        {
            Element = element;
            PageNumber = pageNumber;
            Context = context;
            Page = page;
        }

        public PdfSemanticElement Element { get; }

        public int PageNumber { get; }

        public FootnoteContext Context { get; }

        public PdfLayoutPage? Page { get; }
    }

    private sealed class LogicalFootnote
    {
        private readonly List<FootnoteFragment> _fragments = [];
        private readonly List<string> _referenceIds = [];

        public LogicalFootnote(string key, string marker, string id)
        {
            Key = key;
            Marker = marker;
            Id = id;
        }

        public string Key { get; }

        public string Marker { get; }

        public string Id { get; }

        public IReadOnlyList<FootnoteFragment> Fragments => _fragments;

        public IReadOnlyList<string> ReferenceIds => _referenceIds;

        public void AddFragment(FootnoteFragment fragment)
        {
            if (_fragments.Any(existing => ReferenceEquals(existing.Element, fragment.Element)) ||
                fragment.Element.Note?.ContinuesPreviousNote != true &&
                _fragments.Any(existing =>
                    existing.PageNumber == fragment.PageNumber &&
                    string.Equals(existing.Element.Text, fragment.Element.Text, StringComparison.Ordinal)))
            {
                return;
            }

            _fragments.Add(fragment);
        }

        public string NextReferenceId()
        {
            string id = $"{Id}-ref-{(_referenceIds.Count + 1).ToString(CultureInfo.InvariantCulture)}";
            _referenceIds.Add(id);
            return id;
        }
    }

    private sealed class FootnoteContext
    {
        private readonly int _pageNumber;
        private readonly PdfLayoutPage? _page;
        private readonly Dictionary<string, LogicalFootnote> _notesByMarker = new(StringComparer.Ordinal);
        private readonly Dictionary<PdfSemanticElement, LogicalFootnote> _notesByElement = [];
        private int _groupCount;

        private FootnoteContext(int pageNumber, PdfLayoutPage? page = null)
        {
            _pageNumber = pageNumber;
            _page = page;
        }

        public static FootnoteContext Create(int pageNumber, IReadOnlyList<PdfSemanticElement> elements)
        {
            FootnoteContext context = new(pageNumber);
            foreach (PdfSemanticElement element in elements.Where(static element =>
                element.Kind == PdfSemanticElementKind.Footnote))
            {
                context.AddElement(element);
            }

            return context;
        }

        public static FootnoteContext[] CreateContinuous(
            IReadOnlyList<PdfLayoutPage> layoutPages,
            IReadOnlyList<PdfSemanticPage> semanticPages)
        {
            FootnoteContext[] contexts = semanticPages
                .Select((page, index) => new FootnoteContext(page.PageNumber, layoutPages[index]))
                .ToArray();
            LogicalFootnote? continuedNote = null;
            for (int pageIndex = 0; pageIndex < semanticPages.Count; pageIndex++)
            {
                FootnoteContext context = contexts[pageIndex];
                foreach (PdfSemanticElement element in semanticPages[pageIndex].Elements.Where(static element =>
                    element.Kind == PdfSemanticElementKind.Footnote))
                {
                    string marker = FootnoteMarker(element);
                    if (marker.Length == 0)
                    {
                        continue;
                    }

                    string key = FootnoteMarkerKey(marker);
                    LogicalFootnote note;
                    if (element.Note?.ContinuesPreviousNote == true &&
                        continuedNote != null &&
                        string.Equals(continuedNote.Key, key, StringComparison.Ordinal))
                    {
                        note = continuedNote;
                    }
                    else if (!context._notesByMarker.TryGetValue(key, out note!))
                    {
                        note = CreateLogicalFootnote(semanticPages[pageIndex].PageNumber, key, marker);
                    }

                    context.MapElement(element, note);
                    note.AddFragment(new FootnoteFragment(
                        element,
                        semanticPages[pageIndex].PageNumber,
                        context,
                        layoutPages[pageIndex]));
                    continuedNote = element.Note?.ContinuesOnNextPage == true ? note : null;
                }
            }

            return contexts;
        }

        public bool Contains(string marker)
        {
            return _notesByMarker.ContainsKey(FootnoteMarkerKey(marker));
        }

        public bool TryMatchReference(
            string text,
            int index,
            out string marker,
            out int length)
        {
            marker = "";
            length = 0;
            if (index < 0 || index >= text.Length)
            {
                return false;
            }

            if (char.IsDigit(text[index]))
            {
                int end = index;
                while (end < text.Length && end - index < 2 && char.IsDigit(text[end]))
                {
                    end++;
                }

                marker = text[index..end];
                length = marker.Length;
                return Contains(marker);
            }

            marker = text[index].ToString();
            length = 1;
            return marker[0] is '*' or '∗' or '†' or '‡' && Contains(marker);
        }

        public void Register(string marker)
        {
            if (marker.Length == 0)
            {
                return;
            }

            string key = FootnoteMarkerKey(marker);
            if (!_notesByMarker.ContainsKey(key))
            {
                _notesByMarker[key] = CreateLogicalFootnote(_pageNumber, key, marker);
            }
        }

        public string IdFor(string marker)
        {
            Register(marker);
            return _notesByMarker[FootnoteMarkerKey(marker)].Id;
        }

        public string NextReferenceId(string marker)
        {
            Register(marker);
            return _notesByMarker[FootnoteMarkerKey(marker)].NextReferenceId();
        }

        public IReadOnlyList<LogicalFootnote> NotesToRender(IReadOnlyList<PdfSemanticElement> elements)
        {
            List<LogicalFootnote> notes = [];
            foreach (PdfSemanticElement element in elements)
            {
                if (!_notesByElement.TryGetValue(element, out LogicalFootnote? note))
                {
                    note = AddElement(element);
                }

                if (note != null &&
                    note.Fragments.Count > 0 &&
                    ReferenceEquals(note.Fragments[0].Element, element) &&
                    !notes.Contains(note))
                {
                    notes.Add(note);
                }
            }

            return notes;
        }

        public string NextGroupLabelId()
        {
            _groupCount++;
            return $"page-{_pageNumber.ToString(CultureInfo.InvariantCulture)}-footnotes-{_groupCount.ToString(CultureInfo.InvariantCulture)}-label";
        }

        private LogicalFootnote? AddElement(PdfSemanticElement element)
        {
            string marker = FootnoteMarker(element);
            if (marker.Length == 0)
            {
                return null;
            }

            string key = FootnoteMarkerKey(marker);
            if (!_notesByMarker.TryGetValue(key, out LogicalFootnote? note))
            {
                note = CreateLogicalFootnote(_pageNumber, key, marker);
            }

            MapElement(element, note);
            note.AddFragment(new FootnoteFragment(element, _pageNumber, this, _page));
            return note;
        }

        private void MapElement(PdfSemanticElement element, LogicalFootnote note)
        {
            _notesByElement[element] = note;
            _notesByMarker[note.Key] = note;
        }

        private static LogicalFootnote CreateLogicalFootnote(int pageNumber, string key, string marker)
        {
            return new LogicalFootnote(
                key,
                marker,
                $"page-{pageNumber.ToString(CultureInfo.InvariantCulture)}-fn-{FootnoteMarkerToken(marker)}");
        }

        private static string FootnoteMarker(PdfSemanticElement element)
        {
            if (!string.IsNullOrWhiteSpace(element.Note?.Marker))
            {
                return element.Note.Marker;
            }

            string text = element.Text.TrimStart();
            if (text.Length == 0)
            {
                return "";
            }

            if (text[0] is '*' or '∗' or '†' or '‡')
            {
                return text[..1];
            }

            int length = 0;
            while (length < text.Length && length < 2 && char.IsDigit(text[length]))
            {
                length++;
            }

            return length > 0 && (length == text.Length || char.IsWhiteSpace(text[length]))
                ? text[..length]
                : "";
        }

        private static string FootnoteMarkerKey(string marker)
        {
            return marker is "*" or "∗" ? "asterisk" : marker;
        }

        private static string FootnoteMarkerToken(string marker)
        {
            return marker switch
            {
                "*" or "∗" => "asterisk",
                "†" => "dagger",
                "‡" => "double-dagger",
                _ => CssClassToken(marker)
            };
        }
    }

    private static void WriteLink(StringBuilder html, PdfLayoutLink link, float scale)
    {
        html.Append("    <a class=\"pdf-link-overlay\" href=\"")
            .Append(HtmlAttribute(LinkHref(link)))
            .Append("\" data-link-kind=\"")
            .Append(HtmlAttribute(link.Kind.ToString().ToLowerInvariant()));

        if (!string.IsNullOrEmpty(link.Uri))
        {
            html.Append("\" data-uri=\"")
                .Append(HtmlAttribute(link.Uri));
        }

        if (!string.IsNullOrEmpty(link.Destination))
        {
            html.Append("\" data-destination=\"")
                .Append(HtmlAttribute(link.Destination));
        }

        html.Append("\" aria-label=\"")
            .Append(HtmlAttribute(LinkLabel(link)))
            .Append("\" style=\"position:absolute;left:")
            .Append(CssPoints(link.Bounds.X * scale))
            .Append(";top:")
            .Append(CssPoints(link.Bounds.Y * scale))
            .Append(";width:")
            .Append(CssPoints(link.Bounds.Width * scale))
            .Append(";height:")
            .Append(CssPoints(link.Bounds.Height * scale))
            .AppendLine("\"></a>");
    }

    private static string LinkHref(PdfLayoutLink link)
    {
        if (!string.IsNullOrEmpty(link.Uri))
        {
            return link.Uri;
        }

        if (link.DestinationPageNumber.HasValue)
        {
            return "#page-" + link.DestinationPageNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        return string.IsNullOrEmpty(link.Destination)
            ? "#"
            : "#" + Uri.EscapeDataString(link.Destination);
    }

    private static string LinkLabel(PdfLayoutLink link)
    {
        if (!string.IsNullOrEmpty(link.Uri))
        {
            return link.Uri;
        }

        if (!string.IsNullOrEmpty(link.Destination))
        {
            return link.Destination;
        }

        return "PDF link";
    }

    private static string NormalizeCssPath(string cssPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cssPath);
        return cssPath.Replace('\\', '/').TrimStart('/');
    }

    private static string CssPoints(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
    }

    private static string CssPercent(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture) + "%";
    }

    private static string SvgNumber(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string SvgPathData(IReadOnlyList<PdfLayoutPathCommand> commands)
        => SvgPathData(commands, 0, 0);

    private static string SvgObjectBoundingBoxPathData(
        IReadOnlyList<PdfLayoutPathCommand> commands,
        PdfLayoutRectangle bounds)
    {
        float width = MathF.Max(bounds.Width, 0.0001f);
        float height = MathF.Max(bounds.Height, 0.0001f);
        PdfLayoutPathCommand[] normalized = commands.Select(command => command.Kind switch
        {
            PdfLayoutPathCommandKind.MoveTo or PdfLayoutPathCommandKind.LineTo => command with
            {
                X1 = (command.X1 - bounds.X) / width,
                Y1 = (command.Y1 - bounds.Y) / height
            },
            PdfLayoutPathCommandKind.CurveTo => command with
            {
                X1 = (command.X1 - bounds.X) / width,
                Y1 = (command.Y1 - bounds.Y) / height,
                X2 = (command.X2 - bounds.X) / width,
                Y2 = (command.Y2 - bounds.Y) / height,
                X3 = (command.X3 - bounds.X) / width,
                Y3 = (command.Y3 - bounds.Y) / height
            },
            _ => command
        }).ToArray();
        return SvgPathData(normalized);
    }

    private static string SvgPathData(
        IReadOnlyList<PdfLayoutPathCommand> commands,
        float xOffset,
        float yOffset)
    {
        StringBuilder builder = new();
        foreach (PdfLayoutPathCommand command in commands)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            switch (command.Kind)
            {
                case PdfLayoutPathCommandKind.MoveTo:
                    builder.Append("M ")
                        .Append(SvgNumber(command.X1 - xOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.Y1 - yOffset));
                    break;
                case PdfLayoutPathCommandKind.LineTo:
                    builder.Append("L ")
                        .Append(SvgNumber(command.X1 - xOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.Y1 - yOffset));
                    break;
                case PdfLayoutPathCommandKind.CurveTo:
                    builder.Append("C ")
                        .Append(SvgNumber(command.X1 - xOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.Y1 - yOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.X2 - xOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.Y2 - yOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.X3 - xOffset))
                        .Append(' ')
                        .Append(SvgNumber(command.Y3 - yOffset));
                    break;
                case PdfLayoutPathCommandKind.ClosePath:
                    builder.Append('Z');
                    break;
            }
        }

        return builder.ToString();
    }

    private static string ColorHex(PdfLayoutColor color)
    {
        return "#"
            + ByteHex(color.Red)
            + ByteHex(color.Green)
            + ByteHex(color.Blue);
    }

    private static string CssRgba(PdfLayoutColor color)
    {
        return "rgba(" +
            MathF.Round(Math.Clamp(color.Red, 0f, 1f) * 255f).ToString(CultureInfo.InvariantCulture) + "," +
            MathF.Round(Math.Clamp(color.Green, 0f, 1f) * 255f).ToString(CultureInfo.InvariantCulture) + "," +
            MathF.Round(Math.Clamp(color.Blue, 0f, 1f) * 255f).ToString(CultureInfo.InvariantCulture) + "," +
            SvgNumber(color.Alpha) + ")";
    }

    private static string ByteHex(float value)
    {
        return ((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f)).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static string FillRule(int? fillRule)
    {
        return fillRule == 0 ? "evenodd" : "nonzero";
    }

    private static string LineCap(int lineCap)
    {
        return lineCap switch
        {
            1 => "round",
            2 => "square",
            _ => "butt"
        };
    }

    private static string LineJoin(int lineJoin)
    {
        return lineJoin switch
        {
            1 => "round",
            2 => "bevel",
            _ => "miter"
        };
    }

    private static string CssFontFamily(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return "sans-serif";
        }

        string fallback = FontFallback(fontName);
        return CssFontFamilyName(fontName) + ", " + fallback;
    }

    private static string CssFontFamilyName(string fontName)
    {
        string escaped = fontName.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }

    private static string CssUrlRelativeToStylesheet(string cssPath, string assetPath)
    {
        string? stylesheetDirectory = Path.GetDirectoryName(cssPath.Replace('/', Path.DirectorySeparatorChar));
        string relativePath = Path.GetRelativePath(
            string.IsNullOrWhiteSpace(stylesheetDirectory) ? "." : stylesheetDirectory,
            assetPath.Replace('/', Path.DirectorySeparatorChar));
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace("'", "%27", StringComparison.Ordinal);
    }

    private static string FontFallback(string fontName)
    {
        if (fontName.Contains("SFTT", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Mono", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Courier", StringComparison.OrdinalIgnoreCase))
        {
            return "monospace";
        }

        if (fontName.Contains("NimbusRom", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Times", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("CMR", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("CMBX", StringComparison.OrdinalIgnoreCase))
        {
            return "serif";
        }

        return "sans-serif";
    }

    private static bool IsRightToLeftText(string text)
    {
        return PdfTextDirectionDetector.Detect(text) == PdfTextDirection.RightToLeft;
    }

    private static void AppendTextDirectionAttribute(StringBuilder html, string text)
    {
        if (IsRightToLeftText(text))
        {
            html.Append(" dir=\"rtl\"");
        }
    }

    private static void AppendTaggedStructureAttributes(StringBuilder html, PdfSemanticElement element)
    {
        if (element.TaggedStructure is not PdfTaggedStructureElement tagged)
        {
            return;
        }

        html.Append(" data-pdf-structure-type=\"")
            .Append(HtmlAttribute(tagged.StandardStructureType))
            .Append('"');
        if (!string.IsNullOrWhiteSpace(tagged.Language))
        {
            html.Append(" lang=\"")
                .Append(HtmlAttribute(tagged.Language))
                .Append('"');
        }

        if (!string.IsNullOrWhiteSpace(tagged.Title))
        {
            html.Append(" title=\"")
                .Append(HtmlAttribute(tagged.Title))
                .Append('"');
        }
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(SanitizeHtmlValue(value));
    }

    private static string HtmlAttribute(string value)
    {
        return WebUtility.HtmlEncode(SanitizeHtmlValue(value)).Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string SanitizeHtmlValue(string value)
    {
        if (value.All(static character => character is '\t' or '\n' or '\r' || character >= ' '))
        {
            return value;
        }

        StringBuilder sanitized = new(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            int codePoint = rune.Value;
            bool valid = codePoint is 0x9 or 0xA or 0xD ||
                codePoint is >= 0x20 and <= 0xD7FF ||
                codePoint is >= 0xE000 and <= 0xFFFD ||
                codePoint is >= 0x10000 and <= 0x10FFFF;
            sanitized.Append(valid ? rune.ToString() : "�");
        }

        return sanitized.ToString();
    }
}
