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

            Logger.Log($"[SELFTEST] Completed: {(allPassed ? "PASS" : "FAIL")}");
            return allPassed;
        }
        catch (Exception ex)
        {
            Logger.LogError("[SELFTEST] Exception running math self-test", ex);
            return false;
        }
    }
}

