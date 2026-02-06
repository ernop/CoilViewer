# .NET Runtime Version Detection Issue - Root Cause and Solution

## Problem Summary

The application was showing an error message indicating ".NET 8 is required" even though .NET 8 was installed. This was NOT actually a missing .NET installation problem, but rather a **version pinning issue** in the project configuration.

## Root Cause Analysis

### The Issue

1. **Project Configuration** (CoilViewer.csproj):
   ```xml
   <RuntimeFrameworkVersion>8.0.20</RuntimeFrameworkVersion>
   <WindowsDesktopSharedFrameworkVersion>8.0.20</WindowsDesktopSharedFrameworkVersion>
   ```

2. **Generated Runtime Config** (CoilViewer.runtimeconfig.json):
   ```json
   {
     "runtimeOptions": {
       "frameworks": [
         {
           "name": "Microsoft.NETCore.App",
           "version": "8.0.20"  // Exact version required
         },
         {
           "name": "Microsoft.WindowsDesktop.App",
           "version": "8.0.20"  // Exact version required
         }
       ]
     }
   }
   ```

3. **Installed Runtime**:
   - Only `Microsoft.NETCore.App 8.0.8` and `Microsoft.WindowsDesktop.App 8.0.8` were installed
   - The runtime host looks for EXACT version match (8.0.20)
   - Since 8.0.20 is not installed, the app fails to start BEFORE any code runs

### Why This Happens

When you specify `RuntimeFrameworkVersion` in a .NET project:
- MSBuild generates a `runtimeconfig.json` with the exact version specified
- The .NET runtime host (`hostfxr.dll`) reads this file BEFORE launching the application
- It looks for an exact version match in the installed runtimes
- If the exact version is not found, it fails with a "you need .NET X" error message
- **The application code never executes** - this happens at the runtime host level

### Why Roll-Forward Didn't Work

By default, .NET uses a "roll-forward" policy that allows using a compatible newer patch version:
- App requires 8.0.0, user has 8.0.8 → ✅ Works (rolls forward)
- App requires 8.0.20, user has 8.0.8 → ❌ Fails (cannot roll backward)

However, when you explicitly specify a version using `RuntimeFrameworkVersion`, it pins to that exact version and disables automatic roll-forward.

## Solution

### Fix Applied

**Removed the explicit version pinning** from `CoilViewer.csproj`:

```xml
<!-- REMOVED: These lines caused exact version matching -->
<!-- <RuntimeFrameworkVersion>8.0.20</RuntimeFrameworkVersion> -->
<!-- <WindowsDesktopSharedFrameworkVersion>8.0.20</WindowsDesktopSharedFrameworkVersion> -->
```

### How This Fixes It

1. Without `RuntimeFrameworkVersion`, MSBuild generates `runtimeconfig.json` with:
   - Target framework moniker (TFM): `net8.0`
   - No exact version specified

2. The .NET runtime host uses its roll-forward policy:
   - Looks for any compatible `net8.0` runtime
   - Finds installed `8.0.8` version
   - Successfully launches the application

3. The app now works with ANY .NET 8.x patch version installed on the user's machine

## Diagnostic Logging Added

### Build-Time Diagnostics

Added a build target that logs runtime framework information during build:

```xml
<Target Name="LogRuntimeFrameworkInfo" BeforeTargets="BeforeBuild">
  <Message Importance="High" Text="[RUNTIME-DIAG] TargetFramework: $(TargetFramework)" />
  <Message Importance="High" Text="[RUNTIME-DIAG] RuntimeFrameworkVersion: $(RuntimeFrameworkVersion)" />
  ...
</Target>
```

### Runtime Diagnostics

Added runtime environment logging in `App.xaml.cs` that logs:
- Framework description
- Environment.Version
- Process and OS architecture
- RuntimeConfig.json contents

This runs immediately at startup (before any other initialization) to help diagnose runtime issues.

## When Should You Pin Runtime Versions?

You should **only** specify `RuntimeFrameworkVersion` if:

1. **You need a specific patch version** for a bug fix or feature
2. **You're testing compatibility** with a specific version
3. **You're distributing a framework-dependent app** and want to ensure users have a minimum version

For most applications, **omitting `RuntimeFrameworkVersion` is recommended** because:
- ✅ Works with any compatible patch version
- ✅ Users don't need to install exact patch versions
- ✅ Better user experience (fewer installation failures)
- ✅ Follows .NET best practices for framework-dependent apps

## Verification

After applying the fix:

1. **Check installed runtimes**:
   ```powershell
   dotnet --list-runtimes
   ```

2. **Check generated runtimeconfig.json**:
   - Should NOT have exact version numbers in `frameworks[].version`
   - Should only specify TFM: `"tfm": "net8.0"`

3. **Build and run**:
   ```powershell
   dotnet build CoilViewer/CoilViewer.csproj -c Debug
   .\CoilViewer\bin\Debug\net8.0-windows\CoilViewer.exe
   ```

4. **Check logs** for `[RUNTIME-ENV]` entries showing the actual runtime version used

5. **Verify runtimeconfig.json**:
   - After fix: Should show `"version": "8.0.0"` (baseline version, allows roll-forward)
   - Before fix: Showed `"version": "8.0.20"` (exact version, fails if not installed)

## Important Note About Version in runtimeconfig.json

After removing `RuntimeFrameworkVersion`, the generated `runtimeconfig.json` will still show a version number (typically `8.0.0` for `net8.0`). This is **normal and correct**:

- `8.0.0` is the **baseline version** for the `net8.0` TFM
- .NET's roll-forward policy allows using any compatible **newer** patch version
- So `8.0.0` → `8.0.8` works perfectly (rolls forward)
- But `8.0.20` → `8.0.8` fails (cannot roll backward)

The key difference is:
- **Baseline version (8.0.0)**: Minimum required, allows forward compatibility
- **Exact version pinning (8.0.20)**: Requires exact match, no roll-forward

## Additional Notes

- This issue affects **framework-dependent deployments** (default for .NET)
- **Self-contained deployments** bundle the runtime and don't have this issue
- The error message "you need .NET 8" is misleading - it should say "you need .NET 8.0.20" to be more accurate
- The runtime host error occurs BEFORE any application code runs, so logging in application code won't help diagnose it
