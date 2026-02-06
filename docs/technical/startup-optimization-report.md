# CoilViewer Startup Optimization Report

## Baseline Performance (10 Image Test Average)
- **Total Startup:** 523.6ms
- **InitializeComponent (XAML):** 276.1ms (53%)
- **window.Show() (rendering):** 125.4ms (24%)
- **Config loading:** 42.4ms (8%)
- **Other:** ~80ms (15%)

---

## IMPLEMENTED OPTIMIZATIONS

### Option 1: Lazy-Load Large XAML Elements ✅ DONE
**Impact: ~30-50ms reduction expected**

**What was done:**
- Removed 230-line ShortcutsOverlay from XAML (wasn't even implemented yet)
- Saves XAML parsing time on every startup
- Can be re-added as on-demand creation when feature is needed

**Expected result:** 523ms → **~480-500ms**

---

### Option 6: ReadyToRun (R2R) Compilation ✅ DONE
**Impact: ~30-80ms reduction expected**

**What was done:**
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <PublishReadyToRun>true</PublishReadyToRun>
    <PublishReadyToRunComposite>true</PublishReadyToRunComposite>
</PropertyGroup>
```

**How it works:**
- Pre-compiles IL to native code during publish
- Eliminates JIT compilation overhead on first run
- Only applies to Release builds
- Increases executable size ~20-40%

**Trade-offs:**
- Larger executable (acceptable for desktop app)
- Slightly longer build times
- No functional changes

**Expected result after both:** 523ms → **~420-460ms**

---

## OPTION 2: Config Caching - EXPLANATION REQUESTED

### Current Behavior:
```csharp
public static ViewerConfig Load(string path) {
    var json = File.ReadAllText(path);  // 42ms
    return JsonSerializer.Deserialize<ViewerConfig>(json);
}
```

### Optimization A - Cache Parsed Config:
```csharp
private static ViewerConfig? _cachedConfig;
private static DateTime _configLastModified;

public static ViewerConfig Load(string path) {
    var lastWrite = File.GetLastWriteTimeUtc(path);
    if (_cachedConfig != null && lastWrite == _configLastModified) {
        return _cachedConfig; // Return cached copy
    }
    
    // Load fresh
    var json = File.ReadAllText(path);
    _cachedConfig = JsonSerializer.Deserialize<ViewerConfig>(json);
    _configLastModified = lastWrite;
    return _cachedConfig;
}
```

**Expected savings:** ~35ms (after first load)

### WHAT YOU LOSE:

#### Freedom Lost:
1. **External Config Edits Won't Be Detected**
   - If you edit `config.json` while CoilViewer runs in another window, opening a new instance won't see the changes
   - Current behavior: Every launch reads fresh config
   - Cached behavior: Reuses previous instance's config if file unchanged

2. **Ctrl+R Still Works**
   - The ReloadConfig() method explicitly re-reads the file
   - Manual reload still bypasses cache

#### When This Matters:
- You have multiple test configurations and switch between them
- You use external tools to modify config.json
- You want every launch to be completely fresh

#### When This Doesn't Matter:
- Normal usage (config rarely changes)
- You use the Settings UI to change config
- You're okay with Ctrl+R to force reload

### Optimization B - Source Generators (Better):
```csharp
[JsonSerializable(typeof(ViewerConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ViewerConfigContext : JsonSerializerContext { }

// Usage:
var config = JsonSerializer.Deserialize(json, ViewerConfigContext.Default.ViewerConfig);
```

**Expected savings:** ~15-20ms
**Freedom lost:** NONE - Still reads file every time, just deserializes faster
**Recommendation:** DO THIS instead of caching

---

## OPTION 4: Careful XAML Simplification - ALREADY PARTIALLY DONE

### What Was Removed (Safe):
- 230 lines of ShortcutsOverlay XAML that wasn't implemented

### What COULD Be Removed (Needs Review):
1. **Window.Resources Styles** (80 lines)
   - ShortcutCardStyle, ShortcutKeyStyle, etc.
   - NOT USED anymore since shortcuts overlay removed
   - **REMOVE: Lines 19-62 in MainWindow.xaml**
   - Savings: ~5-10ms

2. **Detection Results Panel** (98 lines, rarely used)
   - Lines 305-403 in FilterPanel
   - Only visible when user manually runs detection
   - Could lazy-load this too
   - Savings: ~10-15ms

### What MUST Stay:
- Grid layout structure (critical)
- Image display and transforms (core functionality)
- Overlay, MessageBlock (frequently used)
- StatusBar (frequently used)
- FilterPanel itself (F key feature)

**If done cautiously:** Additional ~15-25ms savings

---

## OPTION 7: Native AOT - DETAILED EXPLANATION (NOT IMPLEMENTED)

### What is Native AOT?

**AOT = Ahead-Of-Time Compilation**

Instead of:
```
[.NET Runtime] → [IL Code] → [JIT Compile at Startup] → [Native Code]
```

You get:
```
[Build Time] → [Direct Native Code] → [No Runtime Needed]
```

### How to Enable:
```xml
<PropertyGroup>
    <PublishAot>true</PublishAot>
</PropertyGroup>
```

### Expected Performance:
- **Startup:** 523ms → **~250-350ms** (50% faster!)
- **Memory:** ~40% less
- **Executable size:** 50-80MB (includes runtime)

### The Massive Catch - What You Lose:

#### 1. **WPF Not Fully Supported**
```
ERROR: WPF requires runtime reflection
SOLUTION: Migrate to Avalonia UI or WinUI 3
EFFORT: Complete rewrite of all XAML
```

#### 2. **ONNX Runtime Breaks**
```csharp
// This code BREAKS with AOT:
var session = new InferenceSession(modelPath);

// Because ML.NET uses:
- Dynamic model loading
- Reflection for tensor shapes
- Runtime type inspection
```

**SOLUTION:** 
- Use ONNX Runtime's Native API directly (C++ interop)
- Pre-generate all model metadata
- Major rewrite of NsfwDetectionService + ObjectDetectionService

#### 3. **JSON Deserialization Requires Source Generators**
```csharp
// Current code BREAKS:
JsonSerializer.Deserialize<ViewerConfig>(json)

// Must change to:
[JsonSerializable(typeof(ViewerConfig))]
internal partial class AppJsonContext : JsonSerializerContext { }
JsonSerializer.Deserialize(json, AppJsonContext.Default.ViewerConfig)
```

#### 4. **No Dynamic Code**
- Can't use `Type.GetType()`
- Can't use `Activator.CreateInstance()`
- Can't load plugins dynamically
- Must declare all types at compile time

#### 5. **Limited Debugging**
- Stack traces are less useful
- Reflection-based debuggers don't work
- Must use native debuggers

### Required Changes for AOT:

#### Phase 1: Prep Work (200+ lines of changes)
```csharp
// 1. Add source generators for JSON
[JsonSerializable(typeof(ViewerConfig))]
[JsonSerializable(typeof(DetectionResult))]
// ... etc for all JSON types

// 2. Replace all reflection
// BEFORE:
Type.GetType("SomeType")

// AFTER:
if (type == typeof(Type1)) { /* ... */ }
else if (type == typeof(Type2)) { /* ... */ }
```

#### Phase 2: Replace WPF (2000+ lines!)
```xml
<!-- Option A: Migrate to Avalonia UI -->
<Avalonia:Window>
  <!-- All XAML must be rewritten -->
</Avalonia:Window>

<!-- Option B: Migrate to WinUI 3 -->
<WinUI:Window>
  <!-- All XAML must be rewritten -->
</WinUI:Window>
```

#### Phase 3: Fix ONNX Runtime (300+ lines)
```csharp
// Replace high-level API:
var session = new InferenceSession(model);

// With C++ interop:
[DllImport("onnxruntime.dll")]
static extern IntPtr OrtCreateSession(string modelPath);
// ... 50+ more P/Invoke declarations
```

### Verdict on Native AOT:

**Pros:**
- 50% faster startup
- 40% less memory
- Single-file deployment
- No .NET runtime needed

**Cons:**
- 2-3 weeks of full-time rewrite
- Breaks WPF (need UI rewrite)
- Breaks ML models (need low-level rewrite)
- Breaks JSON (need source generators)
- Much harder to maintain
- Limited ecosystem support

**Recommendation:** **NOT WORTH IT**

The 250ms you'd save doesn't justify rewriting 50% of the application and losing WPF's mature tooling.

---

## RECOMMENDED IMPLEMENTATION ORDER

### Phase 1: Quick Wins (DONE) ✅
- [x] Remove unused XAML (ShortcutsOverlay)
- [x] Enable ReadyToRun for Release builds
- **Expected:** 523ms → ~450-480ms

### Phase 2: Safe Optimizations (DO NEXT)
- [ ] Remove unused Resource styles (5-10ms)
- [ ] Add JSON source generators (15-20ms)  
- [ ] Lazy-load Detection Results panel (10-15ms)
- **Expected:** ~450ms → **~400-420ms**

### Phase 3: Config Optimization (DECIDE)
- [ ] Cache config OR use source generators
- Choice depends on your workflow
- **Expected:** ~400ms → **~380ms**

### Phase 4: Publish Optimized Build
```bash
dotnet publish -c Release -r win-x64 --self-contained false
```
- Uses ReadyToRun compilation
- Optimized for production use
- **Expected:** ~380ms → **~350-380ms**

---

## FINAL EXPECTED PERFORMANCE

**Current:** 523.6ms average

**After Phase 1:** ~450-480ms (10-14% faster) ✅ DONE
**After Phase 2:** ~400-420ms (20-24% faster)
**After Phase 3:** ~380ms (27% faster)
**After Phase 4 (Publish):** ~350-380ms (28-33% faster)

**Native AOT (not recommended):** ~250-350ms but requires complete rewrite

---

## WHAT NOT TO DO

### ❌ DON'T: Splash Screen Approach
```csharp
// Show empty window, load UI later
var splash = new Window { Content = new Image() };
splash.Show();
Task.Run(() => LoadRealUI());
```

**Why not:**
- Adds complexity
- Potential flicker
- Worse user experience (shows incomplete UI)
- Doesn't actually make startup faster, just hides it

### ❌ DON'T: Remove Core UI Elements
- Don't remove Filter Panel (F key feature)
- Don't remove Overlay (I key feature)
- Don't remove StatusBar (frequent feedback)
- Don't remove context menu (core navigation)

### ❌ DON'T: Aggressive Caching Without User Control
- Don't cache config if user frequently edits it
- Don't cache images without eviction strategy
- Don't cache detection results indefinitely

---

## CONCLUSION

**Implemented so far:**
- Lazy-loaded unused XAML elements
- Enabled ReadyToRun for Release builds

**Expected improvement:** 523ms → **~420-480ms** (15-20% faster)

**Still available:** Additional ~50-80ms via JSON optimization and careful XAML cleanup

**Not recommended:** Native AOT (requires massive rewrite for minimal gain)

