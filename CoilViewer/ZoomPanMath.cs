using System;

namespace CoilViewer;

internal static class ZoomPanMath
{
    internal static double ComputeFitScale(double viewportW, double viewportH, double imageW, double imageH)
    {
        if (viewportW <= 0 || viewportH <= 0 || imageW <= 0 || imageH <= 0)
        {
            return 1.0;
        }

        var scaleX = viewportW / imageW;
        var scaleY = viewportH / imageH;
        var fit = Math.Min(1.0, Math.Min(scaleX, scaleY));
        if (double.IsNaN(fit) || double.IsInfinity(fit) || fit <= 0)
        {
            return 1.0;
        }

        return fit;
    }

    internal static (double x, double y) ClampScrollOffsets(double offsetX, double offsetY, double scrollableW, double scrollableH)
    {
        if (double.IsNaN(scrollableW) || scrollableW < 0) scrollableW = 0;
        if (double.IsNaN(scrollableH) || scrollableH < 0) scrollableH = 0;

        // ScrollViewer offsets cannot be negative.
        var x = Math.Clamp(offsetX, 0, scrollableW);
        var y = Math.Clamp(offsetY, 0, scrollableH);
        return (x, y);
    }

    internal static (double newOffsetX, double newOffsetY) ComputeScrollOffsetsAfterZoom(
        double anchorXInViewport,
        double anchorYInViewport,
        double viewportW,
        double viewportH,
        double oldOffsetX,
        double oldOffsetY,
        double oldScale,
        double newScale,
        double imageW,
        double imageH)
    {
        if (viewportW <= 0 || viewportH <= 0 || oldScale <= 0 || newScale <= 0)
        {
            return (0, 0);
        }

        // Content size before/after zoom (in DIPs)
        var oldContentW = imageW * oldScale;
        var oldContentH = imageH * oldScale;
        var newContentW = imageW * newScale;
        var newContentH = imageH * newScale;

        var oldScrollableW = Math.Max(0, oldContentW - viewportW);
        var oldScrollableH = Math.Max(0, oldContentH - viewportH);
        var newScrollableW = Math.Max(0, newContentW - viewportW);
        var newScrollableH = Math.Max(0, newContentH - viewportH);

        // Clamp anchor inside viewport.
        anchorXInViewport = Math.Clamp(anchorXInViewport, 0, viewportW);
        anchorYInViewport = Math.Clamp(anchorYInViewport, 0, viewportH);

        // Clamp old offsets.
        var oldClamped = ClampScrollOffsets(oldOffsetX, oldOffsetY, oldScrollableW, oldScrollableH);

        // Content coordinate of the anchor before zoom.
        var anchorContentX = oldClamped.x + anchorXInViewport;
        var anchorContentY = oldClamped.y + anchorYInViewport;

        // Scale around content origin.
        var ratio = newScale / oldScale;
        var newAnchorContentX = anchorContentX * ratio;
        var newAnchorContentY = anchorContentY * ratio;

        // Solve offsets so that same content point remains under the anchor.
        var newOffsetX = newAnchorContentX - anchorXInViewport;
        var newOffsetY = newAnchorContentY - anchorYInViewport;

        var newClamped = ClampScrollOffsets(newOffsetX, newOffsetY, newScrollableW, newScrollableH);
        return (newClamped.x, newClamped.y);
    }

    internal static bool BoundsCoverViewportWhenOverflowing(
        double viewportW,
        double viewportH,
        double contentW,
        double contentH,
        double scrollOffsetX,
        double scrollOffsetY)
    {
        // Model ScrollViewer: content positioned at (-offsetX, -offsetY)
        // Viewport is [0, viewportW] x [0, viewportH]
        var left = -scrollOffsetX;
        var top = -scrollOffsetY;
        var right = left + contentW;
        var bottom = top + contentH;

        var overflowX = contentW > viewportW + 0.01;
        var overflowY = contentH > viewportH + 0.01;

        if (overflowX)
        {
            if (!(left <= 0.01 && right >= viewportW - 0.01))
            {
                return false;
            }
        }

        if (overflowY)
        {
            if (!(top <= 0.01 && bottom >= viewportH - 0.01))
            {
                return false;
            }
        }

        return true;
    }
}

