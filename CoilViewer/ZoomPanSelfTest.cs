using System;

namespace CoilViewer;

// Pure math self-test so it can run in headless environments.
internal static class ZoomPanSelfTest
{
    internal static bool Run()
    {
        try
        {
            Logger.Log("[SELFTEST] Starting zoom/pan math self-test...");

            bool allPassed = true;

            // (viewportW, viewportH, imageW, imageH) in DIPs
            var cases = new (double vw, double vh, double iw, double ih, string name)[]
            {
                (1280, 720, 500, 4000, "Tall 500x4000"),
                (1280, 720, 4000, 500, "Wide 4000x500"),
                (1280, 720, 1920, 1080, "Landscape 1920x1080"),
                (1280, 720, 800, 800, "Square 800x800"),
            };

            foreach (var c in cases)
            {
                var fit = ZoomPanMath.ComputeFitScale(c.vw, c.vh, c.iw, c.ih);
                var oldScale = fit;
                var newScale = Math.Min(fit * 4.0, 8.0);

                // Start centered at old scale.
                var oldContentW = c.iw * oldScale;
                var oldContentH = c.ih * oldScale;
                var oldScrollableW = Math.Max(0, oldContentW - c.vw);
                var oldScrollableH = Math.Max(0, oldContentH - c.vh);
                var oldOffsetX = oldScrollableW * 0.5;
                var oldOffsetY = oldScrollableH * 0.5;

                // Zoom around center.
                var centerX = c.vw * 0.5;
                var centerY = c.vh * 0.5;
                var (newOffsetX, newOffsetY) = ZoomPanMath.ComputeScrollOffsetsAfterZoom(
                    anchorXInViewport: centerX,
                    anchorYInViewport: centerY,
                    viewportW: c.vw,
                    viewportH: c.vh,
                    oldOffsetX: oldOffsetX,
                    oldOffsetY: oldOffsetY,
                    oldScale: oldScale,
                    newScale: newScale,
                    imageW: c.iw,
                    imageH: c.ih
                );

                // Now slam offsets beyond range and clamp.
                var newContentW = c.iw * newScale;
                var newContentH = c.ih * newScale;
                var newScrollableW = Math.Max(0, newContentW - c.vw);
                var newScrollableH = Math.Max(0, newContentH - c.vh);

                var clamped1 = ZoomPanMath.ClampScrollOffsets(1e9, 1e9, newScrollableW, newScrollableH);
                var ok1 = ZoomPanMath.BoundsCoverViewportWhenOverflowing(c.vw, c.vh, newContentW, newContentH, clamped1.x, clamped1.y);

                var clamped2 = ZoomPanMath.ClampScrollOffsets(-1e9, -1e9, newScrollableW, newScrollableH);
                var ok2 = ZoomPanMath.BoundsCoverViewportWhenOverflowing(c.vw, c.vh, newContentW, newContentH, clamped2.x, clamped2.y);

                var okCase = ok1 && ok2 && !double.IsNaN(newOffsetX) && !double.IsNaN(newOffsetY);
                allPassed &= okCase;

                Logger.Log($"[SELFTEST] {c.name}: fit={fit:F6} zoom={newScale:F6} scrollable=({newScrollableW:F2},{newScrollableH:F2}) pass={okCase}");
            }

            allPassed &= ExpectTinyFitScaleIsPreserved();
            allPassed &= ExpectRelativeZoomDetection();
            allPassed &= ExpectAnchorZoomMatrix();
            allPassed &= ExpectZoomButtonSequences();
            allPassed &= ExpectPanClampMatrix();
            allPassed &= ExpectMouseWheelRules();
            allPassed &= ExpectArrowKeyRules();

            Logger.Log($"[SELFTEST] Completed: {(allPassed ? "PASS" : "FAIL")}");
            return allPassed;
        }
        catch (Exception ex)
        {
            Logger.LogError("[SELFTEST] Exception running math self-test", ex);
            return false;
        }
    }

    private static bool ExpectTinyFitScaleIsPreserved()
    {
        const double viewportW = 1200;
        const double viewportH = 800;
        const double imageW = 300000;
        const double imageH = 1000;

        var fit = ZoomPanMath.ComputeFitScale(viewportW, viewportH, imageW, imageH);
        var clamped = ZoomPanMath.ClampZoomScale(fit, fit, maxZoom: 8.0);
        var contentW = imageW * clamped;
        var contentH = imageH * clamped;

        var ok = Math.Abs(clamped - fit) < 0.000000001
            && contentW <= viewportW + 0.01
            && contentH <= viewportH + 0.01;
        Logger.Log($"[SELFTEST] Tiny fit preserved: fit={fit:F9} clamped={clamped:F9} content=({contentW:F2},{contentH:F2}) pass={ok}");
        return ok;
    }

    private static bool ExpectRelativeZoomDetection()
    {
        const double fit = 0.003;
        var zoomedScale = fit * 1.25;
        var nearFitScale = fit * 1.0000001;

        var ok = ZoomPanMath.IsZoomed(zoomedScale, fit)
            && !ZoomPanMath.IsZoomed(nearFitScale, fit)
            && !ZoomPanMath.IsZoomed(fit, fit);
        Logger.Log($"[SELFTEST] Relative zoom detection: fit={fit:F9} zoomed={zoomedScale:F9} nearFit={nearFitScale:F9} pass={ok}");
        return ok;
    }

    private static bool ExpectMouseWheelRules()
    {
        var ok = ZoomPanMath.GetMouseWheelAction(120, controlPressed: false, isZoomed: false) == MouseWheelAction.NavigatePrevious
            && ZoomPanMath.GetMouseWheelAction(-120, controlPressed: false, isZoomed: false) == MouseWheelAction.NavigateNext
            && ZoomPanMath.GetMouseWheelAction(120, controlPressed: false, isZoomed: true) == MouseWheelAction.PanVertical
            && ZoomPanMath.GetMouseWheelAction(-120, controlPressed: false, isZoomed: true) == MouseWheelAction.PanVertical
            && ZoomPanMath.GetMouseWheelAction(120, controlPressed: true, isZoomed: true) == MouseWheelAction.ZoomIn
            && ZoomPanMath.GetMouseWheelAction(-120, controlPressed: true, isZoomed: false) == MouseWheelAction.ZoomOut
            && ZoomPanMath.GetMouseWheelAction(0, controlPressed: false, isZoomed: true) == MouseWheelAction.None
            && ZoomPanMath.GetMouseWheelAction(0, controlPressed: true, isZoomed: false) == MouseWheelAction.None;
        Logger.Log($"[SELFTEST] Mouse wheel rules pass={ok}");
        return ok;
    }

    private static bool ExpectArrowKeyRules()
    {
        var ok = ZoomPanMath.GetArrowKeyAction(isZoomed: false, quadraticModifierPressed: false) == ArrowKeyAction.Navigate
            && ZoomPanMath.GetArrowKeyAction(isZoomed: true, quadraticModifierPressed: false) == ArrowKeyAction.Pan
            && ZoomPanMath.GetArrowKeyAction(isZoomed: false, quadraticModifierPressed: true) == ArrowKeyAction.JumpHalf
            && ZoomPanMath.GetArrowKeyAction(isZoomed: true, quadraticModifierPressed: true) == ArrowKeyAction.JumpHalf;
        Logger.Log($"[SELFTEST] Arrow key rules pass={ok}");
        return ok;
    }

    private static bool ExpectAnchorZoomMatrix()
    {
        var viewports = new (double w, double h, string name)[]
        {
            (1280, 720, "HD"),
            (1920, 1080, "FHD"),
            (3440, 1440, "Ultrawide"),
            (800, 1200, "Portrait")
        };

        var images = new (double w, double h, string name)[]
        {
            (800, 800, "small-square"),
            (20000, 20000, "20k-square"),
            (20000, 5000, "20k-wide"),
            (5000, 20000, "20k-tall"),
            (50000, 30000, "huge-landscape"),
            (300000, 1000, "extreme-panorama"),
            (1000, 300000, "extreme-tall")
        };

        var relativeScales = new[] { 1.0, 1.25, 2.0, 7.5 };
        var zoomFactors = new[] { 1.25, 1.0 / 1.25, 3.0 };

        var allPassed = true;
        var checkedCases = 0;
        foreach (var viewport in viewports)
        {
            var anchors = new (double x, double y, string name)[]
            {
                (viewport.w * 0.5, viewport.h * 0.5, "center"),
                (0, 0, "top-left"),
                (viewport.w, viewport.h, "bottom-right"),
                (viewport.w * 0.15, viewport.h * 0.85, "lower-leftish"),
                (viewport.w * 0.95, viewport.h * 0.1, "upper-rightish"),
                (-100, viewport.h * 0.5, "outside-left"),
                (viewport.w + 100, viewport.h + 100, "outside-bottom-right")
            };

            foreach (var image in images)
            {
                var fit = ZoomPanMath.ComputeFitScale(viewport.w, viewport.h, image.w, image.h);
                foreach (var relativeScale in relativeScales)
                {
                    var oldScale = ZoomPanMath.ClampZoomScale(fit * relativeScale, fit, maxZoom: 8.0);
                    var oldContentW = image.w * oldScale;
                    var oldContentH = image.h * oldScale;
                    var oldScrollableW = Math.Max(0, oldContentW - viewport.w);
                    var oldScrollableH = Math.Max(0, oldContentH - viewport.h);
                    var starts = new (double x, double y, string name)[]
                    {
                        (0, 0, "start"),
                        (oldScrollableW * 0.5, oldScrollableH * 0.5, "middle"),
                        (oldScrollableW, oldScrollableH, "end")
                    };

                    foreach (var zoomFactor in zoomFactors)
                    {
                        var newScale = ZoomPanMath.ClampZoomScale(oldScale * zoomFactor, fit, maxZoom: 8.0);
                        var newContentW = image.w * newScale;
                        var newContentH = image.h * newScale;
                        var newScrollableW = Math.Max(0, newContentW - viewport.w);
                        var newScrollableH = Math.Max(0, newContentH - viewport.h);

                        foreach (var start in starts)
                        {
                            foreach (var anchor in anchors)
                            {
                                checkedCases++;
                                var result = ZoomPanMath.ComputeScrollOffsetsAfterZoom(
                                    anchor.x,
                                    anchor.y,
                                    viewport.w,
                                    viewport.h,
                                    start.x,
                                    start.y,
                                    oldScale,
                                    newScale,
                                    image.w,
                                    image.h);

                                var inRange = IsFinite(result.newOffsetX)
                                    && IsFinite(result.newOffsetY)
                                    && result.newOffsetX >= -0.01
                                    && result.newOffsetY >= -0.01
                                    && result.newOffsetX <= newScrollableW + 0.01
                                    && result.newOffsetY <= newScrollableH + 0.01;
                                var covers = ZoomPanMath.BoundsCoverViewportWhenOverflowing(
                                    viewport.w,
                                    viewport.h,
                                    newContentW,
                                    newContentH,
                                    result.newOffsetX,
                                    result.newOffsetY);
                                var preservesAnchor = AnchorPreservationIsValid(
                                    viewport.w,
                                    viewport.h,
                                    anchor.x,
                                    anchor.y,
                                    start.x,
                                    start.y,
                                    oldContentW,
                                    oldContentH,
                                    newContentW,
                                    newContentH,
                                    oldScale,
                                    newScale,
                                    result.newOffsetX,
                                    result.newOffsetY);
                                var okCase = inRange && covers && preservesAnchor;
                                if (!okCase)
                                {
                                    Logger.Log($"[SELFTEST] Anchor matrix failure viewport={viewport.name} image={image.name} rel={relativeScale:F2} factor={zoomFactor:F3} start={start.name} anchor={anchor.name} result=({result.newOffsetX:F2},{result.newOffsetY:F2}) pass={okCase}");
                                }

                                allPassed &= okCase;
                            }
                        }
                    }
                }
            }
        }

        Logger.Log($"[SELFTEST] Anchor zoom matrix cases={checkedCases} pass={allPassed}");
        return allPassed;
    }

    private static bool ExpectZoomButtonSequences()
    {
        var allPassed = true;
        var cases = new (double vw, double vh, double iw, double ih, string name)[]
        {
            (1280, 720, 20000, 20000, "20k-square"),
            (1280, 720, 300000, 1000, "extreme-wide"),
            (1280, 720, 1000, 300000, "extreme-tall"),
            (1920, 1080, 800, 800, "small-square")
        };

        foreach (var c in cases)
        {
            var fit = ZoomPanMath.ComputeFitScale(c.vw, c.vh, c.iw, c.ih);
            var scale = fit;
            var sawZoomed = false;

            for (var i = 0; i < 20; i++)
            {
                scale = ZoomPanMath.ClampZoomScale(scale * 1.25, fit, maxZoom: 8.0);
                sawZoomed |= ZoomPanMath.IsZoomed(scale, fit);
                if (scale < fit - 0.000000001 || scale > 8.0 + 0.000000001)
                {
                    allPassed = false;
                }
            }

            for (var i = 0; i < 24; i++)
            {
                scale = ZoomPanMath.ClampZoomScale(scale / 1.25, fit, maxZoom: 8.0);
                if (scale < fit - 0.000000001 || scale > 8.0 + 0.000000001)
                {
                    allPassed = false;
                }
            }

            var reset = ZoomPanMath.ClampZoomScale(fit, fit, maxZoom: 8.0);
            var okCase = sawZoomed
                && Math.Abs(reset - fit) < 0.000000001
                && !ZoomPanMath.IsZoomed(reset, fit);
            Logger.Log($"[SELFTEST] Zoom button sequence {c.name}: fit={fit:F9} final={scale:F9} reset={reset:F9} pass={okCase}");
            allPassed &= okCase;
        }

        return allPassed;
    }

    private static bool ExpectPanClampMatrix()
    {
        var scrollables = new[] { -1.0, 0.0, 1.0, 500.0, 1_000_000_000.0, double.NaN };
        var offsets = new[] { -1_000_000_000.0, -1.0, 0.0, 1.0, 500.0, 1_000_000_000.0, double.NaN };
        var allPassed = true;
        var checkedCases = 0;

        foreach (var scrollableW in scrollables)
        {
            foreach (var scrollableH in scrollables)
            {
                var expectedMaxW = double.IsNaN(scrollableW) || scrollableW < 0 ? 0 : scrollableW;
                var expectedMaxH = double.IsNaN(scrollableH) || scrollableH < 0 ? 0 : scrollableH;
                foreach (var offsetX in offsets)
                {
                    foreach (var offsetY in offsets)
                    {
                        checkedCases++;
                        var clamped = ZoomPanMath.ClampScrollOffsets(offsetX, offsetY, scrollableW, scrollableH);
                        var okCase = IsFinite(clamped.x)
                            && IsFinite(clamped.y)
                            && clamped.x >= 0
                            && clamped.y >= 0
                            && clamped.x <= expectedMaxW
                            && clamped.y <= expectedMaxH;
                        allPassed &= okCase;
                    }
                }
            }
        }

        Logger.Log($"[SELFTEST] Pan clamp matrix cases={checkedCases} pass={allPassed}");
        return allPassed;
    }

    // Oracle mirrors WPF ScrollViewer semantics: content smaller than the viewport
    // is centered, so a viewport-space anchor must be shifted by the centering gap
    // before scaling and shifted back by the new gap afterwards.
    private static bool AnchorPreservationIsValid(
        double viewportW,
        double viewportH,
        double anchorX,
        double anchorY,
        double oldOffsetX,
        double oldOffsetY,
        double oldContentW,
        double oldContentH,
        double newContentW,
        double newContentH,
        double oldScale,
        double newScale,
        double actualOffsetX,
        double actualOffsetY)
    {
        var oldScrollableW = Math.Max(0, oldContentW - viewportW);
        var oldScrollableH = Math.Max(0, oldContentH - viewportH);
        var newScrollableW = Math.Max(0, newContentW - viewportW);
        var newScrollableH = Math.Max(0, newContentH - viewportH);

        var clampedAnchorX = Math.Clamp(anchorX, 0, viewportW);
        var clampedAnchorY = Math.Clamp(anchorY, 0, viewportH);
        var oldClamped = ZoomPanMath.ClampScrollOffsets(oldOffsetX, oldOffsetY, oldScrollableW, oldScrollableH);

        var oldGapX = Math.Max(0, (viewportW - oldContentW) * 0.5);
        var oldGapY = Math.Max(0, (viewportH - oldContentH) * 0.5);
        var newGapX = Math.Max(0, (viewportW - newContentW) * 0.5);
        var newGapY = Math.Max(0, (viewportH - newContentH) * 0.5);

        var ratio = newScale / oldScale;
        var rawExpectedX = (oldClamped.x + clampedAnchorX - oldGapX) * ratio - clampedAnchorX + newGapX;
        var rawExpectedY = (oldClamped.y + clampedAnchorY - oldGapY) * ratio - clampedAnchorY + newGapY;
        var expected = ZoomPanMath.ClampScrollOffsets(rawExpectedX, rawExpectedY, newScrollableW, newScrollableH);

        return Math.Abs(actualOffsetX - expected.x) <= 0.01
            && Math.Abs(actualOffsetY - expected.y) <= 0.01;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

