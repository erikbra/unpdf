namespace PdfBox.Net.Layout;

/// <summary>
/// Options for extracting a page layout model from a PDF document.
/// </summary>
public sealed class PdfLayoutOptions
{
    /// <summary>
    /// Gets or sets whether positioned text should be collected.
    /// </summary>
    public bool IncludeText { get; init; } = true;

    /// <summary>
    /// Gets or sets whether link annotations should be collected.
    /// </summary>
    public bool IncludeLinks { get; init; } = true;

    /// <summary>
    /// Gets or sets whether supported AcroForm widgets should be collected as semantic form controls.
    /// XFA packets are not interpreted and continue to use visual annotation fallbacks.
    /// </summary>
    public bool IncludeFormControls { get; init; } = true;

    /// <summary>
    /// Gets or sets whether image placements should be collected.
    /// </summary>
    public bool IncludeImages { get; init; } = true;

    /// <summary>
    /// Gets or sets whether vector path paint operations should be collected.
    /// </summary>
    public bool IncludePaths { get; init; } = true;

    /// <summary>
    /// Gets or sets whether collected images should also be exported as browser-safe assets.
    /// </summary>
    public bool IncludeImageAssets { get; init; }

    /// <summary>
    /// Gets or sets whether embedded TrueType and OpenType font programs should be exported as browser-safe assets.
    /// Raw Type 1 and CFF programs remain text-outline fallbacks because browsers cannot load them directly.
    /// </summary>
    public bool IncludeFontAssets { get; init; }

    /// <summary>
    /// Gets or sets whether compact knockout transparency groups should be rasterized as image fallbacks when a
    /// rendering backend is registered. Disabled by default so normal HTML conversion continues to preserve vectors.
    /// </summary>
    public bool IncludeTransparencyGroupFallbacks { get; init; }

    /// <summary>
    /// Gets or sets whether annotation appearances should be exported as positioned image assets when
    /// a rendering backend is registered.
    /// </summary>
    public bool IncludeAnnotationAppearances { get; init; } = true;

    /// <summary>
    /// Gets or sets whether text positions should be sorted into visual reading order.
    /// </summary>
    public bool SortTextByPosition { get; init; } = true;

    /// <summary>
    /// Gets or sets whether duplicate overlapping text should be suppressed by the underlying text stripper.
    /// </summary>
    public bool SuppressDuplicateOverlappingText { get; init; } = true;

    /// <summary>
    /// Gets or sets whether article beads should split text collection.
    /// </summary>
    public bool SeparateByBeads { get; init; }

    /// <summary>
    /// Gets or sets the maximum top-coordinate delta for glyphs to be grouped into the same line.
    /// </summary>
    public float SameLineTolerance { get; init; } = 2.0f;

    /// <summary>
    /// Gets or sets the multiplier applied to a glyph space width before inserting an inferred run boundary.
    /// </summary>
    public float WordSpacingMultiplier { get; init; } = 0.5f;
}
