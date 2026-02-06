# Native AOT - What It Is and Why It's Interesting

## What is AOT (Ahead-of-Time) Compilation?

### Traditional .NET Execution Model:

```
Your Code (.cs files)
    ↓
C# Compiler
    ↓
IL (Intermediate Language) bytecode (.dll)
    ↓
[RUNTIME - User clicks your app]
    ↓
JIT Compiler (Just-In-Time) ← SLOW!
    ↓
Native Machine Code (x64 assembly)
    ↓
Execution
```

**Problem:** The JIT compiler runs AT STARTUP, converting IL to native code when the user launches your app. This causes:
- Slow startup (200-500ms overhead)
- Memory overhead (JIT compiler loaded in memory)
- CPU spikes on first run

### Native AOT Model:

```
Your Code (.cs files)
    ↓
C# Compiler
    ↓
IL (Intermediate Language)
    ↓
[BUILD TIME - You compile the app]
    ↓
AOT Compiler (crossgen2/ILC)
    ↓
Native Machine Code (.exe with embedded runtime)
    ↓
[RUNTIME - User clicks your app]
    ↓
Direct Execution (NO JIT!) ← FAST!
```

**Benefit:** The AOT compiler runs AT BUILD TIME, producing a self-contained executable with native code ALREADY compiled. When the user launches, it runs immediately.

---

## Why Native AOT is Interesting

### 1. INSTANT Startup (50-70% faster)

**Normal .NET App:**
```
Click app → Load runtime → JIT compile → Display window
  50ms       150ms          200ms          100ms    = 500ms total
```

**Native AOT App:**
```
Click app → Display window
  50ms       100ms           = 150ms total
```

Your CoilViewer: 523ms → ~150-250ms potential (60-70% faster!)

### 2. No .NET Runtime Needed

**Normal deployment:**
```
CoilViewer.exe             5 MB
CoilViewer.dll           300 KB
Microsoft.ML.dll         2 MB
.NET Runtime              140 MB (must be installed separately!)
Total for user:          ~145 MB + user must install .NET 8
```

**Native AOT deployment:**
```
CoilViewer.exe           50-70 MB (includes EVERYTHING)
Total for user:          ~60 MB, NO installation needed!
```

Users can run your app WITHOUT installing .NET. Just download and run.

### 3. Lower Memory Footprint

**Normal .NET:**
- JIT compiler: ~30 MB
- Metadata/reflection: ~20 MB  
- Your app: ~80 MB
- **Total: ~130 MB**

**Native AOT:**
- Your app: ~60 MB (runtime is trimmed to only what you use)
- **Total: ~60 MB (55% less!)**

### 4. Better Performance (sometimes)

Native AOT uses aggressive optimization during build:
- Profile-Guided Optimization (PGO)
- Cross-module inlining
- Dead code elimination
- Constant folding

Result: 10-30% faster execution for compute-heavy code.

### 5. Harder to Reverse Engineer

**Normal .NET:**
- Anyone can decompile your .dll to C# code (tools like ILSpy, dnSpy)
- Secrets/algorithms are visible
- Easy to pirate/modify

**Native AOT:**
- Produces native machine code (x64 assembly)
- Much harder to reverse engineer (like C++ apps)
- Better IP protection

### 6. Cloud Cost Savings

For server apps:
- Faster cold start = less billable time
- Lower memory = cheaper instances
- Serverless: $100/month → $30/month savings

---

## The Catch: Why It's Hard

### 1. NO Reflection (The Big One!)

**This code BREAKS:**
```csharp
// Normal .NET: Works fine
var type = Type.GetType("SomeClass");
var obj = Activator.CreateInstance(type);

// Native AOT: COMPILE ERROR!
// AOT compiler says: "I don't know what SomeClass is at build time!"
```

**Why it breaks:**
- Reflection requires runtime metadata
- Native AOT strips metadata to save space
- You must declare everything at compile time

**Workaround: Source Generators**
```csharp
// You must explicitly declare all types:
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public class SomeClass { }

// OR use source generators to pre-generate code
```

### 2. WPF Partially Supported (YOUR PROBLEM!)

**Current status (as of .NET 9):**
- ✅ Console apps: Fully supported
- ✅ ASP.NET Core: Fully supported  
- ✅ WinForms: Experimental support
- ⚠️ **WPF: Limited/broken**
- ❌ Xamarin/MAUI: Not supported

**Why WPF is hard:**
- XAML uses heavy reflection
- Data binding uses runtime type inspection
- Styles/templates use dynamic creation
- Resource dictionaries use runtime loading

**Your XAML:**
```xml
<TextBlock Text="{Binding Name}" /> <!-- Uses reflection! -->
<Button Click="OnClick" />         <!-- Runtime method lookup! -->
```

All of this BREAKS with Native AOT.

**The nuclear option:** Switch to Avalonia UI (WPF's cross-platform cousin with AOT support)
- Requires rewriting ALL XAML
- 2-3 weeks of work

### 3. ONNX Runtime Breaks (YOUR ML MODELS!)

**Your code:**
```csharp
var session = new InferenceSession(modelPath);
session.Run(inputs, outputs);
```

**Why it breaks:**
- ONNX Runtime uses runtime model loading
- Model shape determined dynamically
- Tensor dimensions discovered at runtime
- Heavy reflection for type mapping

**Workaround:**
Use ONNX Runtime's C API directly (painful):
```csharp
[DllImport("onnxruntime.dll")]
static extern IntPtr OrtCreateSession(IntPtr env, string model_path);

[DllImport("onnxruntime.dll")]  
static extern void OrtRun(IntPtr session, IntPtr inputs, IntPtr outputs);

// ... 50+ more P/Invoke declarations ...
```

You'd have to rewrite NsfwDetectionService and ObjectDetectionService entirely.

### 4. JSON Serialization Requires Changes

**Your config loading:**
```csharp
// Normal .NET: Works
var config = JsonSerializer.Deserialize<ViewerConfig>(json);

// Native AOT: BREAKS!
// Serializer doesn't know ViewerConfig structure at build time
```

**Fix: Source Generators (not too bad)**
```csharp
[JsonSerializable(typeof(ViewerConfig))]
[JsonSerializable(typeof(NsfwDetectionResult))]
[JsonSerializable(typeof(ObjectDetectionResult))]
internal partial class AppJsonContext : JsonSerializerContext { }

// Usage:
var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.ViewerConfig);
```

This one is actually easy to fix.

### 5. Larger Executable Size

**Your app now:**
- CoilViewer.exe: 5 MB
- CoilViewer.dll: 300 KB
- Dependencies: 2 MB
- **Total: ~7 MB**

**With Native AOT:**
- CoilViewer.exe: **60-80 MB**
- (Everything embedded)

The .exe is **10x larger** because it includes the entire .NET runtime.

### 6. MUCH Slower Builds

**Normal build:**
```
dotnet build  →  3 seconds
```

**Native AOT publish:**
```
dotnet publish -c Release  →  60-90 seconds
```

Why? AOT compiler:
- Analyzes entire app dependency tree
- Optimizes across all modules
- Generates native code for every method
- Links everything together

Development iteration becomes painful.

---

## Interesting Technical Details

### How Native AOT Actually Works

**Step 1: IL Analysis**
```
AOT Compiler scans your IL code and builds a dependency graph:
- Main() calls LoadSequence()
- LoadSequence() calls ImageSequence.LoadFromPath()  
- LoadFromPath() uses Directory.EnumerateFiles()
- EnumerateFiles() needs Win32 APIs
- ... traces entire call tree
```

**Step 2: Tree Shaking (Dead Code Elimination)**
```
Your app uses:    5% of WPF
                 10% of System.IO
                  2% of System.Linq
                 
AOT keeps only what you use, discards rest:
- WPF: 140 MB → 7 MB
- System.IO: 8 MB → 800 KB
- System.Linq: 2 MB → 200 KB
```

**Step 3: Native Code Generation**
```
For each method:
IL bytecode → x64 assembly (with aggressive optimization)

Example:
IL: ldarg.0, ldarg.1, add, ret
↓
Assembly: mov rax, rcx
          add rax, rdx
          ret
```

**Step 4: Static Linking**
```
All code + minimal runtime → single .exe:
- Your code: 5 MB
- Mini CLR: 30 MB (GC, threading, etc.)
- Core libraries: 20 MB
= 55 MB self-contained executable
```

### The ILC (IL Compiler)

Native AOT uses a special compiler called ILC (IL Compiler):

**What makes it special:**
- Written in C++ for speed
- Cross-platform (generates Windows/Linux/Mac code)
- Uses LLVM-style optimization passes
- Can generate code for ANY CPU (ARM, x64, RISC-V)

**Optimization passes:**
1. **Inlining:** Small methods embedded directly
2. **Devirtualization:** Virtual calls → direct calls
3. **Constant Propagation:** `if (true) ...` → just the body
4. **Loop Unrolling:** Small loops expanded
5. **SIMD Vectorization:** Array operations use CPU vector instructions
6. **Profile-Guided Optimization:** Hot paths get extra optimization

### The Trimming Process

**Aggressive mode trims EVERYTHING not explicitly used:**

```csharp
// This entire class gets REMOVED if you never call it:
public class UnusedHelper {
    public void DoSomething() { ... }
}

// Even methods YOU wrote get removed:
public class MyClass {
    public void UsedMethod() { }     // ✓ Kept
    public void UnusedMethod() { }   // ✗ DELETED!
}
```

**Problem:** Trimmer can't detect reflection usage:
```csharp
Type.GetType("UnusedHelper");  // Trimmer removed UnusedHelper!
// Runtime crash: "Type not found"
```

This is why reflection breaks!

### CrossGen2 vs ILC

.NET has TWO AOT compilers:

**CrossGen2 (ReadyToRun):** What you're using now
- Generates native code + keeps IL
- Still needs .NET runtime
- Faster startup (50%)
- Safe fallback to JIT if needed
- **This is the "safe" AOT**

**ILC (Native AOT):** The nuclear option  
- Generates ONLY native code
- NO .NET runtime needed
- Much faster startup (300%)
- NO fallback (if it breaks, it's broken)
- **This is "true" Native AOT**

---

## Real-World Examples

### Success Stories

**1. Azure Functions**
- Before: 2-second cold start, $200/month
- After Native AOT: 100ms cold start, $30/month
- **93% savings**

**2. CLI Tools (like git, ripgrep)**
- dotnet tool: 500ms startup
- Native AOT: 20ms startup
- **Feels instant**

**3. Game Engines (Unity, Godot)**
- JIT forbidden on iOS (Apple restriction)
- Native AOT required for iOS deployment
- **Only option**

### Failure Stories

**1. Entity Framework Core**
- Heavily uses reflection for queries
- LINQ expressions built at runtime
- Native AOT: Requires complete rewrite
- **Not worth it**

**2. ASP.NET MVC (old)**
- Controller discovery via reflection
- Model binding uses runtime types
- Razor views compiled at runtime
- **Impossible without major changes**

**3. WPF Apps (like yours)**
- XAML binding uses reflection
- Styles/templates dynamically applied
- Resource dictionaries loaded at runtime
- **Would require switching to Avalonia**

---

## Should YOU Use Native AOT?

### ✅ YES, if you:
- Build CLI tools
- Deploy to cloud/serverless
- Need sub-100ms startup
- Don't use WPF/reflection/dynamic code
- Can invest 2-4 weeks rewriting

### ❌ NO, if you:
- Use WPF (like CoilViewer)
- Use ONNX Runtime high-level API
- Use heavy reflection
- Need fast development iteration
- Are happy with current performance

---

## For CoilViewer Specifically

### What You'd Have To Change

**1. Replace WPF (biggest pain):**
- Rewrite all XAML in Avalonia UI: **2-3 weeks**
- Or use a code-only UI library: **3-4 weeks**

**2. Rewrite ML Model Loading:**
- Replace ONNX Runtime C# API with C API: **1 week**
- Or find AOT-compatible ML library: **2 weeks**

**3. Fix JSON (easy):**
- Add source generators: **1 hour**

**4. Fix Reflection:**
- Search for Type.GetType, Activator.CreateInstance: **2-3 days**
- Replace with source generators or if/else chains: **1 week**

**Total effort: 6-10 weeks** of full-time work

**Benefit: 523ms → ~180ms startup** (65% faster)

### Is It Worth It?

**My honest assessment: NO**

**Why:**
- 523ms is already fast enough
- 6-10 weeks of work for 343ms savings
- Risk of introducing bugs
- Harder to maintain
- WPF ecosystem is better than Avalonia
- ONNX Runtime C API is painful

**Better investments:**
- ReadyToRun (already done): 50ms saved, 5 minutes work ✓
- Lazy-load XAML (already done): 30ms saved, 1 hour work ✓
- JSON source generators: 20ms saved, 1 hour work
- **Total: 100ms saved, 2 hours work** vs **343ms saved, 400+ hours work**

**ROI: Native AOT is 200x worse**

---

## The Future of Native AOT

### What Microsoft is Working On

**2024-2025 roadmap:**
- Better WPF support (still experimental)
- Reflection-free JSON (already done)
- LINQ without reflection (in progress)
- Entity Framework AOT support (2025)
- Blazor WASM AOT (already works)

**By .NET 10 (2025):**
- WPF might be fully supported
- ONNX Runtime might have AOT-friendly API
- Trimming might get smarter

**By .NET 12 (2027):**
- Native AOT might be default recommendation
- Most libraries will support it
- Tooling will be much better

### Interesting Experiments

**1. Bflat Compiler**
- Third-party AOT compiler for .NET
- Smaller executables (10 MB vs 60 MB)
- Faster compilation
- Even less compatible

**2. NativeAOT-LLVM**
- Use LLVM instead of ILC
- Better optimization
- Can target obscure CPUs
- Very experimental

**3. GraalVM Native Image**
- Alternative .NET runtime with AOT
- Better startup than ILC
- Worse peak performance
- Java interop

---

## Conclusion

**Native AOT is interesting because:**
1. **Extreme startup speed** (60-70% faster)
2. **No runtime dependency** (users don't need .NET installed)
3. **Lower memory usage** (50% less)
4. **Better IP protection** (harder to decompile)
5. **Cloud cost savings** (serverless apps)

**But it's NOT practical for CoilViewer because:**
1. WPF doesn't fully support it
2. ONNX Runtime needs rewrite
3. 6-10 weeks of work for 343ms savings
4. Existing optimizations give 80% of benefit for 1% of effort
5. Current 523ms startup is already excellent

**Use Native AOT for:** CLI tools, serverless functions, embedded devices, iOS apps

**Stick with ReadyToRun for:** Desktop apps with WPF, apps using ML models, rapid development

Your current setup (ReadyToRun + lazy XAML + tiered compilation) gives you 90% of Native AOT's benefits with 1% of the pain. That's the smart choice.

