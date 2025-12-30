# Zoom/Pan Debugging Postmortem - Opus Analysis

Date: 2025-12-30

This is my independent analysis of the zoom/pan debugging session, written after reviewing the conversation transcript and final code. I am being honest about what went wrong, why it took multiple attempts, and what lessons emerge.

---

## The Problem Statement

The user reported three distinct issues with the image viewer:

1. **Zoom targeting was broken for tall images**: When pressing zoom keys on a tall/narrow image (e.g., 500x4000 pixels), the viewport would jump to the lower-right corner of the image instead of staying centered or following the mouse cursor.

2. **Panning allowed scrolling into black**: Arrow key navigation could push the viewport into empty canvas areas (the black background), sometimes allowing only "a tiny bit of the image" to remain visible.

3. **Asymmetric pan limits**: The user could pan further left than right, further up than down. The limits did not feel balanced.

---

## Root Cause Analysis

After reviewing the code and transcript, I identify **one fundamental architectural mistake** that caused all three symptoms:

### The Sin: Mixing Multiple Competing Coordinate Systems

The original implementation stacked three independent mechanisms:

```
1. Image.Stretch="Uniform"     <- WPF's internal fit/center logic (invisible transform)
2. LayoutTransform              <- A ScaleTransform for "fit to window"  
3. RenderTransform              <- Another ScaleTransform for "zoom" + TranslateTransform for "pan"
```

This created a situation where:

- WPF applied its own hidden scaling and centering via `Stretch="Uniform"`
- The code applied additional transforms on top
- Pan limits were calculated against `ImageDisplay.RenderSize`, which under `Stretch="Uniform"` can reflect the element's container size rather than the actual bitmap's rendered bounds

**The coordinate model was internally inconsistent.** Any anchor math computed in one space (e.g., viewport pixels) would be subtly wrong when applied in another space (e.g., render-transformed image coordinates).

### Why Tall Images Exposed the Bug

For wide images that nearly fill the horizontal viewport, the error was small. But for a 500x4000 image on a 1920x1080 screen:

- Fit scale = 1080/4000 = 0.27
- Displayed size = 135 x 1080 pixels (very narrow pillar in the center)
- The gap between "where WPF thinks the element is" and "where the anchor math thinks it is" becomes large
- Result: zoom anchor lands in the wrong place (typically toward an edge)

---

## The Debugging Journey: What Went Wrong

Reading through the transcript, I count roughly **6 significant attempts** before the solution was found. Here is my honest assessment of why each was insufficient:

### Attempt 1: Fix the anchor math

The previous agent tried to add helper functions to estimate "image bounds inside viewport" and adjust anchor calculations.

**Why it failed**: This was treating a symptom. The underlying coordinate model was still broken. Any bounds calculation that assumed a certain centering or scale relationship would be fragile because WPF's internal `Stretch="Uniform"` logic was unpredictable.

### Attempt 2: Abandon mouse anchor, use center-only zoom

To sidestep the anchor bug, the code was changed to always zoom around the center.

**Why it failed**: Still a band-aid. The pan offset (`_panOffset`) was defined in an ambiguous space, so even center zoom could drift. And it lost a useful feature (mouse-targeted zoom).

### Attempt 3: Fix content size calculation

The agent discovered that `TryGetContentSize` was using `ImageDisplay.RenderSize`, which is misleading under `Stretch="Uniform"`. Changed to compute content size from bitmap dimensions times scale.

**Why it failed**: Closer to the truth, but not enough. Even with correct content size, if the "viewport size" or the assumed centering offset was wrong, pan limits would still be asymmetric.

### Attempt 4: Remove Stretch, use single RenderTransform scale

Set `Stretch="None"` and consolidated fit+zoom into one `ScaleTransform` on `RenderTransform`.

**Why it failed**: This was the right *direction* but wrong *implementation*. **RenderTransform does not participate in WPF layout.** The Image element was still laid out at its unscaled bitmap size, then visually scaled afterward. WPF's centering logic saw the wrong size, causing severe positioning errors.

### Attempt 5: Move scale to LayoutTransform

Moved `ImageScaleTransform` from RenderTransform to LayoutTransform. Pan remained a TranslateTransform on RenderTransform.

**What improved**: Zoom targeting started working! The user reported "the zoom target is working well now." This was the breakthrough for Issue 1.

**What was still broken**: Panning still allowed drift into black areas. The TranslateTransform pan was fighting against WPF's centering, and the manually computed pan limits were anchored to the wrong reference point.

### Attempt 6: Remove ScrollViewer, use plain Grid

Hypothesis: maybe ScrollViewer's internal content presenter was adding unexpected offsets.

**Why it failed**: This made things harder, not easier. Without ScrollViewer, all panning had to be manual, requiring perfect replication of what ScrollViewer already does correctly.

---

## The Solution That Worked

The final fix came from a key insight: **stop fighting WPF; let ScrollViewer be the authoritative panning system.**

### Final Architecture

```
XAML:
  ScrollViewer (hidden scrollbars, centered content)
    Image (Stretch="None", HorizontalAlignment="Center", VerticalAlignment="Center")
      LayoutTransform: ScaleTransform (totalZoomScale)
      RenderTransform: (none - removed)
```

**Zoom**: Applied as `LayoutTransform`. This means the Image element's *layout size* equals bitmap_size * scale. WPF arranges and centers this correctly. ScrollViewer's extents update automatically.

**Pan**: Applied via `ScrollViewer.ScrollToHorizontalOffset()` and `ScrollToVerticalOffset()`. Not a TranslateTransform.

**Pan limits**: Derived from `ScrollViewer.ScrollableWidth` and `ScrollableHeight`. These are authoritative values computed by WPF, not guessed by our code.

**Zoom anchoring**: Computed in ScrollViewer's content coordinate space:
```
anchorContentX = currentOffset + anchorInViewport
newAnchorContentX = anchorContentX * (newScale / oldScale)  
newOffset = newAnchorContentX - anchorInViewport
newOffset = clamp(newOffset, 0, scrollableWidth)
```

### Why This Works

1. **One coordinate system**: ScrollViewer's content coordinate system is the single source of truth. Content starts at (0,0), viewport is what you see, offsets are how far you have scrolled.

2. **Layout consistency**: Using LayoutTransform means the Image element's measured/arranged size matches its visual size. No hidden discrepancies.

3. **Structural enforcement of limits**: ScrollViewer offsets are clamped by definition to [0, ScrollableWidth/Height]. You physically cannot scroll past the content. "No black drift" is guaranteed, not hoped for.

4. **No Stretch="Uniform"**: We took full control. The Image shows at exactly the scale we specify, no hidden transforms.

---

## The Self-Test Saga

The user demanded self-testing. The previous agent attempted to run a WPF-based self-test with a hidden MainWindow, but it crashed because WPF requires a real window handle and message pump.

The solution was to create a **pure math self-test** (`ZoomPanSelfTest.cs` and `ZoomPanMath.cs`) that validates the core calculations:

- Fit scale computation
- Offset clamping  
- Anchor zoom math
- "If content overflows, it must cover the viewport" invariant

This runs headlessly and passed for multiple aspect ratios (tall, wide, landscape, square).

---

## Honest Assessment: What Could Have Been Done Better

### 1. Establish the coordinate model first

Before writing any fix, the agent should have drawn out the exact coordinate spaces:
- Bitmap space (native pixels)
- Content space (DIPs, scaled by zoom, origin at top-left of content)
- Viewport space (DIPs, what the user sees)
- Screen space (if relevant)

And documented exactly how each transform maps between them.

### 2. Recognize Stretch="Uniform" as a trap earlier

The combination of `Stretch="Uniform"` + explicit transforms is inherently fragile. This should have been identified and removed in Attempt 1, not Attempt 4.

### 3. RenderTransform vs LayoutTransform is fundamental

The difference matters enormously:
- **RenderTransform**: Visual only. Element is laid out at original size, then visually transformed. Layout, centering, hit-testing all use the wrong size.
- **LayoutTransform**: Affects layout. Element is measured and arranged at transformed size.

For zoom in an image viewer where centering matters, LayoutTransform is almost always correct.

### 4. Use platform primitives

When WPF already has ScrollViewer with built-in extent tracking, offset clamping, and centering, don't reinvent it with TranslateTransform and manual clamp math. Use the platform.

---

## Files Modified

Based on the transcript:

| File | Changes |
|------|---------|
| MainWindow.xaml | Replaced Grid with ScrollViewer; removed RenderTransform; moved scale to LayoutTransform |
| MainWindow.xaml.cs | Rewrote SetZoom, ApplyPan, TryGetPanLimits to use ScrollViewer offsets; added debug helpers (later cleaned up) |
| App.xaml.cs | Added --selftest argument handling |
| ZoomPanMath.cs | New file - pure math functions for zoom/pan calculations |
| ZoomPanSelfTest.cs | New file - headless self-test runner |
| ZOOM_PAN_POSTMORTEM.md | New file - original postmortem from previous agent |

---

## Key Takeaways

1. **Coordinate system consistency is paramount.** A single inconsistent transform can cascade into multiple confusing bugs.

2. **WPF's Stretch property is deceptive.** It applies a hidden internal transform that can conflict with explicit transforms.

3. **LayoutTransform and RenderTransform serve fundamentally different purposes.** Choose based on whether you need layout to reflect the transform.

4. **Prefer platform primitives.** ScrollViewer exists specifically to solve "content larger than viewport" problems. Use it.

5. **When debugging gets iterative, step back.** Multiple failed attempts often indicate a wrong mental model, not just wrong code.

---

## Conclusion

This was a debugging session that took longer than ideal because the early attempts treated symptoms rather than causes. The fundamental issue - mixing coordinate systems through Stretch + multi-layer transforms - should have been identified and addressed at the start.

The final solution is clean: one LayoutTransform for zoom, ScrollViewer for pan, and ScrollViewer's native properties for limits. This is both simpler and more robust than the original design.

The lesson is not "WPF is hard" but rather "be precise about coordinate systems and use platform abstractions correctly."
