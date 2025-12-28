# Roslyn Setup for InstaReload

To enable **instant hot reload (<300ms)**, we need Roslyn DLLs in the project.

## Option 1: Auto-Install (Recommended)

1. Go to `InstaReload → Install Roslyn Compiler`
2. Wait for download and installation
3. Done! Restart Unity

## Option 2: Manual Installation

Download these NuGet packages:
- `Microsoft.CodeAnalysis.CSharp` (version 4.3.0 or higher)
- `Microsoft.CodeAnalysis.Common` (version 4.3.0)

### Steps:

1. Download from NuGet.org:
   - https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/
   - https://www.nuget.org/packages/Microsoft.CodeAnalysis.Common/

2. Extract `.nupkg` files (they're just ZIP files)

3. Copy these DLLs to `Assets/InstaReload/Editor/Roslyn/Libs/`:
   ```
   - Microsoft.CodeAnalysis.dll
   - Microsoft.CodeAnalysis.CSharp.dll
   - System.Collections.Immutable.dll
   - System.Reflection.Metadata.dll
   - System.Runtime.CompilerServices.Unsafe.dll
   - System.Memory.dll
   - System.Text.Encoding.CodePages.dll
   ```

4. Restart Unity

## Why do we need this?

Unity's compilation takes 2-3 seconds and shows a popup. Roslyn allows us to:
- Compile **only changed files** (not entire assembly)
- Compile **in-memory** (no disk I/O)
- Compile in **<100ms** (real-time feel!)

## Verification

After installation, check the console:
```
[Roslyn] Initialized successfully!
[FileDetector] Real-time file monitoring active
```

Now when you change code, you'll see:
```
[FileDetector] ⚡ Detected change: TestScript.cs
[FileDetector] ✓ Compiled in 47ms
✓ Hot reload complete - 3 method(s) updated
```

**No Unity compilation popup!** ⚡
