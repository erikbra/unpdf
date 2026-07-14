using PdfBox.Net.PDModel.Graphics;

namespace PdfBox.Net.Layout;

/// <summary>
/// A PDF form group encountered while collecting page vector graphics.
/// </summary>
/// <remarks>
/// Form XObjects establish inherited clipping and transparency groups apply the
/// graphics state in effect at invocation after their children have been composited.
/// Retaining this hierarchy lets HTML emit matching nested SVG groups instead of
/// flattening clip and opacity state onto individual paths.
/// </remarks>
public sealed class PdfLayoutVectorGroup
{
    public PdfLayoutVectorGroup(
        int index,
        int? parentIndex,
        int firstPathIndex,
        int lastPathIndex,
        PdfLayoutRectangle bounds,
        PdfLayoutRectangle? clipBounds,
        float opacity,
        BlendMode blendMode,
        bool isIsolated,
        bool isKnockout,
        IReadOnlyList<PdfLayoutClipPath>? clipPaths = null)
    {
        Index = index;
        ParentIndex = parentIndex;
        FirstPathIndex = firstPathIndex;
        LastPathIndex = lastPathIndex;
        Bounds = bounds;
        ClipBounds = clipBounds;
        Opacity = Math.Clamp(opacity, 0f, 1f);
        BlendMode = blendMode;
        IsIsolated = isIsolated;
        IsKnockout = isKnockout;
        ClipPaths = clipPaths?.ToArray() ?? [];
    }

    /// <summary>
    /// Gets the zero-based form group index on the page.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the containing form group index, when this group is nested.
    /// </summary>
    public int? ParentIndex { get; }

    /// <summary>
    /// Gets the first flattened vector path painted by this group.
    /// </summary>
    public int FirstPathIndex { get; }

    /// <summary>
    /// Gets the last flattened vector path painted by this group.
    /// </summary>
    public int LastPathIndex { get; }

    /// <summary>
    /// Gets the normalized page bounds of the paths painted by this group.
    /// </summary>
    public PdfLayoutRectangle Bounds { get; }

    /// <summary>
    /// Gets the conservative rectangular bounds of the clipping paths inherited by this form.
    /// </summary>
    public PdfLayoutRectangle? ClipBounds { get; }

    /// <summary>
    /// Gets exact clipping paths inherited when this form was invoked.
    /// </summary>
    public IReadOnlyList<PdfLayoutClipPath> ClipPaths { get; }

    /// <summary>
    /// Gets the group compositing opacity from its invoking graphics state.
    /// </summary>
    public float Opacity { get; }

    /// <summary>
    /// Gets the blend mode from the graphics state that invoked the group.
    /// </summary>
    public BlendMode BlendMode { get; }

    /// <summary>
    /// Gets whether the group is isolated from its backdrop.
    /// </summary>
    public bool IsIsolated { get; }

    /// <summary>
    /// Gets whether the group uses knockout compositing.
    /// </summary>
    public bool IsKnockout { get; }

    /// <summary>
    /// Gets whether this group contains at least one vector path.
    /// </summary>
    public bool HasPaths => LastPathIndex >= FirstPathIndex;
}
