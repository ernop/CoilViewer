using System;

namespace CoilViewer;

internal enum MouseWheelAction
{
    None,
    NavigatePrevious,
    NavigateNext,
    PanVertical,
    ZoomIn,
    ZoomOut
}

internal enum ArrowKeyAction
{
    Navigate,
    Pan,
    JumpHalf
}

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

    internal static double ClampZoomScale(double requestedScale, double fitScale, double maxZoom)
    {
        var minimum = IsPositiveFinite(fitScale) ? fitScale : 1.0;
        var maximum = IsPositiveFinite(maxZoom) ? Math.Max(maxZoom, minimum) : minimum;
        var requested = IsPositiveFinite(requestedScale) ? requestedScale : minimum;
        return Math.Clamp(requested, minimum, maximum);
    }

    internal static bool IsZoomed(double zoomScale, double fitScale)
    {
        if (!IsPositiveFinite(zoomScale) || !IsPositiveFinite(fitScale))
        {
            return false;
        }

        var tolerance = Math.Max(0.000000001, Math.Abs(fitScale) * 0.001);
        return zoomScale > fitScale + tolerance;
    }

    internal static MouseWheelAction GetMouseWheelAction(int delta, bool controlPressed, bool isZoomed)
    {
        if (delta == 0)
        {
            return MouseWheelAction.None;
        }

        if (controlPressed)
        {
            return delta > 0 ? MouseWheelAction.ZoomIn : MouseWheelAction.ZoomOut;
        }

        if (isZoomed)
        {
            return MouseWheelAction.PanVertical;
        }

        return delta > 0 ? MouseWheelAction.NavigatePrevious : MouseWheelAction.NavigateNext;
    }

    internal static ArrowKeyAction GetArrowKeyAction(bool isZoomed, bool quadraticModifierPressed)
    {
        if (quadraticModifierPressed)
        {
            return ArrowKeyAction.JumpHalf;
        }

        return isZoomed ? ArrowKeyAction.Pan : ArrowKeyAction.Navigate;
    }

    private static bool IsPositiveFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
    }

    internal static (double x, double y) ClampScrollOffsets(double offsetX, double offsetY, double scrollableW, double scrollableH)
    {
        if (double.IsNaN(offsetX) || double.IsInfinity(offsetX)) offsetX = 0;
        if (double.IsNaN(offsetY) || double.IsInfinity(offsetY)) offsetY = 0;
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

        // When content is smaller than the viewport, the ScrollViewer centers it.
        // The centering gap must be subtracted to convert a viewport-space anchor
        // into a content-space coordinate.
        var oldGapX = Math.Max(0, (viewportW - oldContentW) * 0.5);
        var oldGapY = Math.Max(0, (viewportH - oldContentH) * 0.5);

        var anchorContentX = oldClamped.x + anchorXInViewport - oldGapX;
        var anchorContentY = oldClamped.y + anchorYInViewport - oldGapY;

        // Scale around content origin.
        var ratio = newScale / oldScale;
        var newAnchorContentX = anchorContentX * ratio;
        var newAnchorContentY = anchorContentY * ratio;

        // Account for the new centering gap after zoom.
        var newGapX = Math.Max(0, (viewportW - newContentW) * 0.5);
        var newGapY = Math.Max(0, (viewportH - newContentH) * 0.5);

        var newOffsetX = newAnchorContentX - anchorXInViewport + newGapX;
        var newOffsetY = newAnchorContentY - anchorYInViewport + newGapY;

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
