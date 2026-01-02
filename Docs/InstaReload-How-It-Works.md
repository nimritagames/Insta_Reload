# InstaReload (WAY-1) - How It Works

This document explains the current hot reload pipeline exactly as it exists in
this repo. It is based on the code in `Assets/InstaReload/` and the runtime
bridge in `Assets/InstaReload/RuntimeBridge/`.

## Overview (end to end)

1) Enter Play Mode
- `UnityCompilationSuppressor` blocks Unity auto-refresh and assembly reload so
  Unity cannot recompile and wipe hot reload patches.
- The dispatcher logging configuration is pushed to runtime and the dispatcher
  table is cleared for a clean play session.

2) File change detected
- `FileChangeDetector` uses `FileSystemWatcher` to catch `*.cs` changes before
  Unity.
- It debounces changes and batches them on the main thread via
  `EditorApplication.update`.

3) Change classification
- `ChangeAnalyzer` compares a structural signature hash against a cached value.
- If signatures match, the change is body-only and can use the fast path.

4) Compilation
- `RoslynCompiler` compiles the single changed file into IL bytes.
- Fast path uses Debug optimization; slow path uses Release optimization.

5) Patching and dispatch
- `InstaReloadPatcher` loads the compiled module with Mono.Cecil and maps it to
  the runtime assembly.
- Unity entry points are detoured to stable trampolines.
- New or rewritten methods are registered with the dispatcher.
- Existing methods get IL body replacement via `ILHook`.
- Field accesses to missing runtime fields are rewritten to `HotReloadFieldStore`.

6) Runtime invocation
- Trampolines call `HotReloadBridge.Invoke` -> `HotReloadDispatcher.Invoke`.
- The dispatcher executes the latest invoker for the method id.

## Components (what each part does)

### UnityCompilationSuppressor (Editor)
File: `Assets/InstaReload/Editor/Core/UnityCompilationSuppressor.cs`

Purpose:
- Prevent Unity from compiling and reloading assemblies during Play Mode.
- This avoids domain reload which would wipe IL patches and state.

Key behavior:
- `AssetDatabase.DisallowAutoRefresh()` blocks Unity import/compile.
- `EditorApplication.LockReloadAssemblies()` blocks assembly reload.
- On exiting Play Mode, both are restored and `AssetDatabase.Refresh()` runs.
- Calls `HotReloadDispatcher.ConfigureLogging` and `HotReloadDispatcher.Clear`
  on EnteredPlayMode.

### FileChangeDetector (Editor)
File: `Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs`

Purpose:
- Orchestrate the hot reload pipeline from file change to patch.

Key behavior:
- Watches `Assets/` for `*.cs` changes using `FileSystemWatcher`.
- Debounce window is 300ms to avoid recompiling every save event.
- Skips editor code and generated files (Editor folders, .g.cs, .designer.cs).
- Determines which assembly a file belongs to using
  `CompilationPipeline.GetAssemblies()`, then falls back to AppDomain lookup.
- Writes the compiled bytes to a temp dll so Mono.Cecil can read it.
- Reuses a per-assembly `InstaReloadPatcher`.
- Invokes hot reload callbacks after patches are applied.

### ChangeAnalyzer (Editor)
File: `Assets/InstaReload/Editor/Core/ChangeAnalyzer.cs`

Purpose:
- Decide if a change is structural or body-only.

How it works:
- Extracts type, method, field signatures by simple text parsing.
- Hashes the signature list with SHA256 and compares against a persisted cache.
- Cache file: `Library/InstaReloadSignatureCache.dat`.

Outcomes:
- `MethodBodyOnly` -> fast path (skip structural validation).
- `MethodSignatureChanged` -> slow path (full validation).

### RoslynCompiler (Editor)
File: `Assets/InstaReload/Editor/Roslyn/RoslynCompiler.cs`

Purpose:
- Compile a single changed file into IL bytes.

How it works:
- Loads Roslyn assemblies via reflection to avoid version pinning.
- Collects metadata references once (cached forever).
- Creates two compilation instances:
  - Release optimization (slow path).
  - Debug optimization (fast path).

Fast path:
- Debug optimization avoids expensive emit optimizations.
- Produces IL quickly and is good enough for hot reload.

### InstaReloadPatcher (Editor)
File: `Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs`

Purpose:
- Map compiled methods to runtime methods and apply patches.

Important data:
- Method key format:
  `TypeName::MethodName` + "`" + generic arity + "(paramTypes)=>ReturnType`
- Method id:
  32-bit FNV-1a hash of the method key (`GetMethodId`).

Main steps in `ApplyAssembly`:
1) Find the runtime assembly for the target name. Prefer an assembly with
   a valid `Location` to avoid using the in-memory Roslyn one.
2) Load the compiled assembly bytes into the AppDomain so method metadata exists.
3) Read the compiled assembly via Mono.Cecil (`ModuleDefinition.ReadModule`).
4) If not fast path, validate:
   - New types are not allowed (runtime type must exist).
   - Field set must match exactly.
   - Methods cannot be removed.
   - New methods are allowed (but need dispatcher).
5) Build maps:
   - Runtime method map (MethodKey -> MethodBase).
   - Runtime field map (FieldKey -> FieldInfo).
   - Method id map (MethodKey -> int).
   - Dispatch key set (Unity entry points or methods missing at runtime).
6) Resolve dispatcher invoke method:
   - Prefer `HotReloadBridge.Invoke` from the runtime assembly.
   - Fallback to `HotReloadDispatcher.Invoke`.
7) For each patchable method:
   - If it is a Unity entry point, install a trampoline detour.
   - Register a dispatcher invoker for that method.
   - If it exists in runtime, use `ILHook` to replace the method body.
   - If it does not exist in runtime, register an invoker only.

#### Trampolines (Unity entry points)
- Unity entry points must exist before Play Mode (Unity caches them).
- We detour those methods to a stable trampoline that only calls the dispatcher.
- Trampolines are created as `DynamicMethod` and installed with
  `MonoMod.RuntimeDetour.Hook`.
- Only instance, void, parameterless methods qualify.

Trampoline IL flow:
1) Load `this` (boxed if needed).
2) Load method id.
3) Load `null` for args (no parameters).
4) Call dispatcher invoke.
5) Pop return and return.

#### IL body replacement (non entry points)
- Uses `ILHook` and `MonoMod.IL` to replace method bodies at runtime.
- `CloneMethodBody` clones each Cecil instruction and imports references.
- `TryRewriteCallInstruction` rewrites calls to "dispatch keys" so
  newly added or changed methods can still be invoked.

Dispatcher call rewrite:
- If a call is to a method that should be dispatched:
  - Box all arguments (if needed), store in locals.
  - Build `object[]` of arguments.
  - Call `DispatcherInvoke(instance, methodId, args)`.
  - Pop or unbox the return value as needed.

### HotReloadDispatcher (Runtime)
File: `Assets/InstaReload/Runtime/HotReloadDispatcher.cs`

Purpose:
- Own the runtime dispatch table for method invokers.

Key behavior:
- `Register(methodId, invoker)` stores `Func<object, object[], object>`.
- `Invoke` calls the latest invoker for that method id.
- Diagnostics are gated by log category and level.
- `ConfigureLogging` and `Clear` are called on EnteredPlayMode.

### HotReloadBridge (Runtime)
File: `Assets/InstaReload/RuntimeBridge/HotReloadBridge.cs`

Purpose:
- Provide an assembly-local entry point for the dispatcher invoke call.
- The patcher resolves this method from the runtime assembly to avoid
  metadata reference mismatch in IL rewriting.

### HotReloadFieldStore (Runtime)
File: `Assets/InstaReload/Runtime/HotReloadFieldStore.cs`

Purpose:
- Provide a backing store for fields added or reshaped at runtime.
- When a compiled field does not exist in the runtime type, the patcher rewrites
  field loads/stores to call the field store instead of the real field.

Behavior:
- Instance fields are stored per-instance in a `ConditionalWeakTable`.
- Static fields are stored per-field key in a shared dictionary.
- Missing value-type fields return a default value (`Activator.CreateInstance`).
- Field initializers are not re-run for existing instances.

### HotReloadBehaviour (Runtime)
File: `Assets/InstaReload/Runtime/HotReloadBehaviour.cs`

Purpose:
- Predeclare Unity lifecycle entry points so Unity registers them before Play Mode.
- Forward those entry points to the dispatcher using the derived type's method id.
Note: The base class currently covers the core lifecycle methods (Awake/Start/OnEnable/OnDisable/OnDestroy/Update/FixedUpdate/LateUpdate).

Usage:
```
public class HotTest : Nimrita.InstaReload.HotReloadBehaviour
{
    // Add or change Update during Play Mode and it will dispatch correctly.
}
```

### HotReloadEntryPointManager (Runtime)
Files:
- `Assets/InstaReload/Runtime/HotReloadEntryPointManager.cs`
- `Assets/InstaReload/Runtime/HotReloadEntryPointProxy.cs`
- `Assets/InstaReload/Runtime/HotReloadEntryPointScanner.cs`

Purpose:
- Provide a fallback path for MonoBehaviours that do not inherit from HotReloadBehaviour.
- When a Unity entry point is added during Play Mode and no runtime method exists,
  the patcher registers it here and proxies dispatch it each frame.

Coverage:
- The fallback proxy supports a broad set of Unity message methods (lifecycle, rendering,
  physics, input, animation, UI, particles). The exact signatures and caveats are
  documented in `Docs/InstaReload-Unity-Message-Support.md`.
- Messages not in the fallback map still require a restart or HotReloadBehaviour/IL weaving.
- Initialization/destruction messages (`Awake`, `Start`, `OnEnable`, `OnDisable`, `OnDestroy`)
  are intentionally excluded from fallback dispatch to avoid replaying one-time setup.

### Hot reload callbacks (Runtime attributes)
File: `Assets/InstaReload/Runtime/HotReloadPatchCallbacks.cs`

Purpose:
- Allow code to react immediately when hot reload patches are applied.

Attributes:
- `[InvokeOnHotReload]` -> invoked after any patch batch is applied.
- `[InvokeOnHotReloadLocal]` -> invoked only when the method itself was patched.

Supported signatures:
- `void MethodName()` (no parameters)
- `void MethodName(IReadOnlyList<HotReloadMethodPatch> patches)` (global callbacks)
- `void MethodName(HotReloadMethodPatch patch)` (local callbacks)

Instance callbacks:
- Instance callbacks are only supported for `UnityEngine.Object` types (MonoBehaviour or ScriptableObject).
- If no live instances exist, the callback is skipped and a warning is logged.

Patch payload:
- `HotReloadMethodPatch.MethodKey` is the stable method signature key.
- `HotReloadMethodPatch.Kind` indicates whether the method was patched, dispatched, or trampoline-backed.
- Callbacks also fire when cached patches are replayed on Play Mode entry.

### Logging and settings (Editor)
Files:
- `Assets/InstaReload/Editor/Core/InstaReloadLogger.cs`
- `Assets/InstaReload/Editor/Settings/InstaReloadSettings.cs`
- `Assets/InstaReload/Editor/UI/InstaReloadWindow.cs`
- `Assets/InstaReload/Editor/Settings/InstaReloadSettingsProvider.cs`

Key details:
- Log levels: `InstaReloadLogLevel` (Info, Warning, Error, Verbose).
- Log categories: `InstaReloadLogCategory` (General, Roslyn, FileDetector,
  Patcher, Suppressor, ChangeAnalyzer, Dispatcher, UI).
- `InstaReloadLogger` filters based on settings and adds category prefixes.
- Runtime dispatcher logging is controlled by the Dispatcher category.
- You can change levels and categories in the window or project settings.

## Method ids and dispatch keys

Method key example:
```
MyNamespace.MyType::Update`0()=>System.Void
```

Method id:
- `GetMethodId` uses a 32-bit FNV-1a hash of the method key for stable lookup.

Dispatch key rules:
- Unity entry points (Update, Start, etc) are always in the dispatch set.
- Any method missing in runtime is also added to the dispatch set.
- Calls to dispatch keys are rewritten to call the dispatcher at runtime.

## Fast path vs slow path

Fast path:
- `ChangeAnalyzer` returns `MethodBodyOnly`.
- `RoslynCompiler` uses Debug optimization.
- `InstaReloadPatcher` skips structural validation.

Slow path:
- `ChangeAnalyzer` returns structural change.
- `RoslynCompiler` uses Release optimization.
- `InstaReloadPatcher` validates type compatibility and method removals; field differences are handled via the field store.

## Limitations (current behavior)

- No runtime type additions without exiting Play Mode.
- Field changes use `HotReloadFieldStore`, but initializers do not re-run for existing instances.
- Missing-field address access (`ldflda`/`ldsflda`) is not supported and will skip patching that method.
- Removed methods are not allowed during Play Mode.
- New methods are only callable through dispatch (trampolines or rewritten calls).
- Generic methods and byref or pointer parameters are not supported in dispatch.
- IL2CPP is not supported because runtime detours require Mono/CLR behavior.

## Key files (quick reference)

- `Assets/InstaReload/Editor/Core/UnityCompilationSuppressor.cs`
- `Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs`
- `Assets/InstaReload/Editor/Core/ChangeAnalyzer.cs`
- `Assets/InstaReload/Editor/Roslyn/RoslynCompiler.cs`
- `Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs`
- `Assets/InstaReload/Runtime/HotReloadDispatcher.cs`
- `Assets/InstaReload/RuntimeBridge/HotReloadBridge.cs`
- `Assets/InstaReload/Runtime/HotReloadFieldStore.cs`
- `Assets/InstaReload/Runtime/HotReloadPatchCallbacks.cs`
- `Assets/InstaReload/Editor/Core/InstaReloadLogger.cs`
- `Assets/InstaReload/Editor/Settings/InstaReloadSettings.cs`
