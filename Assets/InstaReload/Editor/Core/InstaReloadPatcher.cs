/*
 * ============================================================================
 * INSTARELOAD - IL PATCHER (THE PATCHING ENGINE)
 * ============================================================================
 *
 * PURPOSE:
 *   Patches runtime method IL using MonoMod's ILHook system.
 *   THIS IS THE FINAL STEP THAT APPLIES CHANGES WITHOUT DOMAIN RELOAD.
 *
 * THE PROBLEM WE'RE SOLVING:
 *   After compilation, we have:
 *   - Compiled IL bytes (from RoslynCompiler)
 *   - Runtime assembly in memory (Unity's loaded Assembly)
 *
 *   But we CAN'T:
 *   - Replace the entire assembly (requires domain reload)
 *   - Modify metadata (type structure, fields)
 *   - Add new types to existing assemblies
 *
 *   We need:
 *   - Patch individual method BODIES only
 *   - No assembly reload
 *   - Changes persist until exit play mode
 *
 * THE ROOT CAUSE:
 *   .NET CLR doesn't support runtime type modification (by design).
 *   Changing type structure requires reloading assemblies.
 *   Assembly reload triggers Unity's domain reload.
 *   We're stuck with the original type structure.
 *
 * THE SOLUTION:
 *   Use MonoMod.RuntimeDetour.ILHook to patch method IL at runtime:
 *   - Read compiled IL from Roslyn's output (Mono.Cecil)
 *   - Find matching runtime methods (System.Reflection)
 *   - Install IL hooks (MonoMod patches execution)
 *   - Store hooks in static dictionary (prevent GC disposal)
 *
 * HOW IT WORKS (PATCHING PROCESS):
 *
 *   STEP 1: Load Compiled Assembly
 *   - Read IL bytes from temporary file
 *   - Use Mono.Cecil to parse assembly structure
 *   - Extract all method definitions with IL bodies
 *
 *   STEP 2: Validate Compatibility (Slow Path Only)
 *   - Fast path: Skip validation (trust ChangeAnalyzer)
 *   - Slow path: Verify type/field/method sets match
 *   - Reject if new types, removed fields, etc.
 *
 *   STEP 3: Build Runtime Method Map
 *   - Iterate all types in runtime assembly
 *   - Build dictionary: MethodKey → MethodBase
 *   - Key format: "TypeName::MethodName`GenericArity(params)=>returnType"
 *
 *   STEP 4: Patch Each Method
 *   - For each method in compiled assembly:
 *     a. Find matching runtime method by key
 *     b. Create ILHook that replaces IL body
 *     c. Store hook in _methodHooks dictionary (CRITICAL: prevents GC!)
 *
 *   STEP 5: Hook Lifetime Management
 *   - ILHook stays alive → patch persists
 *   - ILHook gets GC'd → patch disappears!
 *   - Solution: Static dictionary keeps hooks alive
 *
 * WHAT IL HOOKING LOOKS LIKE:
 *
 *   Before patch (original runtime method):
 *   void Update() {
 *       IL_0000: ldstr "Old"         ← Original IL
 *       IL_0005: call Debug.Log
 *       IL_000a: ret
 *   }
 *
 *   After patch (MonoMod injects JMP):
 *   void Update() {
 *       IL_0000: jmp NewUpdate       ← MonoMod inserted!
 *   }
 *
 *   NewUpdate (our patched method):
 *   void NewUpdate() {
 *       IL_0000: ldstr "New"         ← Our compiled IL
 *       IL_0005: call Debug.Log
 *       IL_000a: ret
 *   }
 *
 * CRITICAL DECISIONS:
 *
 *   DECISION 1: Fast Path Skips Validation
 *   WHY: Structural validation takes 50-100ms (type/field/method set comparison)
 *   PROBLEM: ChangeAnalyzer already verified only method bodies changed
 *   SOLUTION: skipValidation parameter from FileChangeDetector
 *   RESULT: Fast path saves 50-100ms → total ~30ms instead of ~130ms
 *
 *   DECISION 2: Store Hooks in Static Dictionary
 *   WHY: ILHook is IDisposable - GC will dispose and remove patch!
 *   PROBLEM: After GC, patches disappear mysteriously
 *   SOLUTION: Dictionary<string, ILHook> _methodHooks (static field)
 *   RESULT: Hooks stay alive until we explicitly dispose them
 *
 *   DECISION 3: Only Validate Updated Types
 *   WHY: Single-file compilation has 1 type, runtime assembly has 30+ types
 *   PROBLEM: SetEquals() always fails (different type counts)
 *   SOLUTION: Only check types that ARE in the update
 *   RESULT: Single-file compilation works correctly
 *
 *   DECISION 4: Allow New Methods (But Warn)
 *   WHY: User might add new methods during development
 *   PROBLEM: New methods have no call sites (Unity compiled before they existed)
 *   SOLUTION: Detect new methods, apply patch, but warn won't be callable
 *   RESULT: Graceful degradation instead of failure
 *
 *   DECISION 5: Clone All IL Instructions
 *   WHY: Can't directly copy IL from one assembly to another (references differ)
 *   SOLUTION: Clone each instruction, importing references to new module
 *   RESULT: IL executes correctly in runtime assembly context
 *
 *   DECISION 6: Dispose Hooks on Reset
 *   WHY: Exit play mode → need to clean up patches
 *   SOLUTION: Dispose all hooks in _methodHooks.Values
 *   RESULT: Clean state for next play session
 *
 * DEPENDENCIES:
 *   - MonoMod.RuntimeDetour.ILHook: Runtime IL patching
 *   - Mono.Cecil: IL reading and manipulation
 *   - System.Reflection: Runtime method discovery
 *   - InstaReloadLogger: Logging patch results
 *
 * LIMITATIONS:
 *   - Can only patch method BODIES (not signatures/types/fields)
 *   - New methods aren't callable (no call sites in Unity's code)
 *   - Generic methods not supported (complex type resolution)
 *   - Can't patch abstract methods (no IL body)
 *   - Requires Mono runtime (doesn't work with IL2CPP)
 *
 * PERFORMANCE:
 *   - Load compiled assembly: 5-10ms (Mono.Cecil parsing)
 *   - Structural validation: 50-100ms (skipped on fast path!)
 *   - Build method map: 10-20ms (reflection)
 *   - Patch each method: ~5ms per method
 *   - Total (fast path): ~20ms for typical file (3-5 methods)
 *   - Total (slow path): ~70ms for typical file
 *
 * TESTING:
 *   - Edit method body → check "✓ Hot reload complete - N method(s) updated"
 *   - Verify changes apply immediately in running game
 *   - Add new method → check warning about not callable
 *   - Exit/enter play mode → verify hooks cleaned up and recreated
 *   - Check game continues running (no crashes, no domain reload)
 *
 * FUTURE IMPROVEMENTS:
 *   - Support for generic methods (resolve type parameters)
 *   - Async/await state machine patching
 *   - Property/event patching (currently only methods)
 *   - Parallel patching for multiple methods
 *   - Better error messages for incompatible changes
 *   - Virtual method table for new methods (make them callable)
 *
 * HISTORY:
 *   - 2025-12-27: Created - Initial IL patching implementation
 *   - 2025-12-28: Added fast path validation skip
 *   - 2025-12-28: Fixed single-file compilation compatibility check
 *   - Result: Hot reload works without domain reload!
 *
 * ============================================================================
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Mono.Cecil.Rocks;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using Nimrita.InstaReload;
using EntryPointKind = Nimrita.InstaReload.HotReloadEntryPointManager.EntryPointKind;
using EmitDynamicMethod = System.Reflection.Emit.DynamicMethod;
using EmitOpCodes = System.Reflection.Emit.OpCodes;
using CecilExceptionHandler = Mono.Cecil.Cil.ExceptionHandler;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace Nimrita.InstaReload.Editor
{
    internal sealed class InstaReloadPatcher : IDisposable
    {
        private static readonly HashSet<string> UnityEntryPointNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake",
            "Start",
            "Update",
            "FixedUpdate",
            "LateUpdate",
            "OnEnable",
            "OnDisable",
            "OnDestroy"
        };
        private const string DispatcherBridgeTypeName = "Nimrita.InstaReload.HotReloadBridge";
        private const string HotReloadBehaviourTypeName = "Nimrita.InstaReload.HotReloadBehaviour";
        private static readonly EntryPointSignature[] FallbackEntryPoints =
        {
            new EntryPointSignature("Update", EntryPointKind.Update),
            new EntryPointSignature("FixedUpdate", EntryPointKind.FixedUpdate),
            new EntryPointSignature("LateUpdate", EntryPointKind.LateUpdate),
            new EntryPointSignature("OnGUI", EntryPointKind.OnGUI),
            new EntryPointSignature("OnApplicationFocus", EntryPointKind.OnApplicationFocus, "System.Boolean"),
            new EntryPointSignature("OnApplicationPause", EntryPointKind.OnApplicationPause, "System.Boolean"),
            new EntryPointSignature("OnApplicationQuit", EntryPointKind.OnApplicationQuit),
            new EntryPointSignature("OnBecameVisible", EntryPointKind.OnBecameVisible),
            new EntryPointSignature("OnBecameInvisible", EntryPointKind.OnBecameInvisible),
            new EntryPointSignature("OnPreCull", EntryPointKind.OnPreCull),
            new EntryPointSignature("OnPreRender", EntryPointKind.OnPreRender),
            new EntryPointSignature("OnPostRender", EntryPointKind.OnPostRender),
            new EntryPointSignature("OnRenderObject", EntryPointKind.OnRenderObject),
            new EntryPointSignature("OnWillRenderObject", EntryPointKind.OnWillRenderObject),
            new EntryPointSignature("OnRenderImage", EntryPointKind.OnRenderImage, "UnityEngine.RenderTexture", "UnityEngine.RenderTexture"),
            new EntryPointSignature("OnDrawGizmos", EntryPointKind.OnDrawGizmos),
            new EntryPointSignature("OnDrawGizmosSelected", EntryPointKind.OnDrawGizmosSelected),
            new EntryPointSignature("Reset", EntryPointKind.Reset),
            new EntryPointSignature("OnValidate", EntryPointKind.OnValidate),
            new EntryPointSignature("OnAnimatorMove", EntryPointKind.OnAnimatorMove),
            new EntryPointSignature("OnAnimatorIK", EntryPointKind.OnAnimatorIK, "System.Int32"),
            new EntryPointSignature("OnTransformChildrenChanged", EntryPointKind.OnTransformChildrenChanged),
            new EntryPointSignature("OnTransformParentChanged", EntryPointKind.OnTransformParentChanged),
            new EntryPointSignature("OnRectTransformDimensionsChange", EntryPointKind.OnRectTransformDimensionsChange),
            new EntryPointSignature("OnCanvasGroupChanged", EntryPointKind.OnCanvasGroupChanged),
            new EntryPointSignature("OnCanvasHierarchyChanged", EntryPointKind.OnCanvasHierarchyChanged),
            new EntryPointSignature("OnDidApplyAnimationProperties", EntryPointKind.OnDidApplyAnimationProperties),
            new EntryPointSignature("OnCollisionEnter", EntryPointKind.OnCollisionEnter, "UnityEngine.Collision"),
            new EntryPointSignature("OnCollisionExit", EntryPointKind.OnCollisionExit, "UnityEngine.Collision"),
            new EntryPointSignature("OnCollisionStay", EntryPointKind.OnCollisionStay, "UnityEngine.Collision"),
            new EntryPointSignature("OnCollisionEnter2D", EntryPointKind.OnCollisionEnter2D, "UnityEngine.Collision2D"),
            new EntryPointSignature("OnCollisionExit2D", EntryPointKind.OnCollisionExit2D, "UnityEngine.Collision2D"),
            new EntryPointSignature("OnCollisionStay2D", EntryPointKind.OnCollisionStay2D, "UnityEngine.Collision2D"),
            new EntryPointSignature("OnTriggerEnter", EntryPointKind.OnTriggerEnter, "UnityEngine.Collider"),
            new EntryPointSignature("OnTriggerExit", EntryPointKind.OnTriggerExit, "UnityEngine.Collider"),
            new EntryPointSignature("OnTriggerStay", EntryPointKind.OnTriggerStay, "UnityEngine.Collider"),
            new EntryPointSignature("OnTriggerEnter2D", EntryPointKind.OnTriggerEnter2D, "UnityEngine.Collider2D"),
            new EntryPointSignature("OnTriggerExit2D", EntryPointKind.OnTriggerExit2D, "UnityEngine.Collider2D"),
            new EntryPointSignature("OnTriggerStay2D", EntryPointKind.OnTriggerStay2D, "UnityEngine.Collider2D"),
            new EntryPointSignature("OnControllerColliderHit", EntryPointKind.OnControllerColliderHit, "UnityEngine.ControllerColliderHit"),
            new EntryPointSignature("OnJointBreak", EntryPointKind.OnJointBreak, "System.Single"),
            new EntryPointSignature("OnJointBreak2D", EntryPointKind.OnJointBreak2D, "UnityEngine.Joint2D"),
            new EntryPointSignature("OnParticleCollision", EntryPointKind.OnParticleCollision, "UnityEngine.GameObject"),
            new EntryPointSignature("OnParticleTrigger", EntryPointKind.OnParticleTrigger),
            new EntryPointSignature("OnParticleSystemStopped", EntryPointKind.OnParticleSystemStopped),
            new EntryPointSignature("OnParticleSystemPaused", EntryPointKind.OnParticleSystemPaused),
            new EntryPointSignature("OnParticleSystemResumed", EntryPointKind.OnParticleSystemResumed),
            new EntryPointSignature("OnParticleSystemPlaybackStateChanged", EntryPointKind.OnParticleSystemPlaybackStateChanged),
            new EntryPointSignature("OnMouseDown", EntryPointKind.OnMouseDown),
            new EntryPointSignature("OnMouseUp", EntryPointKind.OnMouseUp),
            new EntryPointSignature("OnMouseEnter", EntryPointKind.OnMouseEnter),
            new EntryPointSignature("OnMouseExit", EntryPointKind.OnMouseExit),
            new EntryPointSignature("OnMouseOver", EntryPointKind.OnMouseOver),
            new EntryPointSignature("OnMouseDrag", EntryPointKind.OnMouseDrag),
            new EntryPointSignature("OnMouseUpAsButton", EntryPointKind.OnMouseUpAsButton),
            new EntryPointSignature("OnBeforeRender", EntryPointKind.OnBeforeRender)
        };
        private static readonly Dictionary<string, EntryPointSignature[]> FallbackEntryPointsByName =
            BuildFallbackEntryPointMap();

        private readonly struct EntryPointSignature
        {
            public EntryPointSignature(string name, EntryPointKind kind, params string[] parameterTypes)
            {
                Name = name;
                Kind = kind;
                ParameterTypes = parameterTypes ?? Array.Empty<string>();
            }

            public string Name { get; }
            public EntryPointKind Kind { get; }
            public string[] ParameterTypes { get; }
        }

        private sealed class TrampolineHook
        {
            public TrampolineHook(Hook hook, MethodInfo trampolineMethod)
            {
                Hook = hook;
                TrampolineMethod = trampolineMethod;
            }

            public Hook Hook { get; }
            public MethodInfo TrampolineMethod { get; }
        }

        // Result of CheckCompatibility.
        //
        // Why a struct instead of out-params?
        //   The old IsCompatible had (out string reason) which conflated two concerns:
        //   "is there a hard blocker?" and "what new types were found?". Mixing those
        //   into a single out-param makes the call site awkward and the method hard to
        //   extend. A result struct keeps each concern in its own field, adds no heap
        //   allocation for the struct itself, and reads cleanly at the call site.
        //
        // Why readonly?
        //   The result is produced once and consumed once. Making it readonly prevents
        //   accidental mutation and signals intent clearly.
        private readonly struct CompatibilityResult
        {
            // True  → the hot assembly can be applied (may still contain new types).
            // False → a hard blocker was found (e.g. removed methods); abort patching.
            public readonly bool IsCompatible;

            // Human-readable reason when IsCompatible is false. Empty otherwise.
            public readonly string BlockingReason;

            // Types present in the compiled assembly but absent from the runtime assembly.
            // These are new types the developer added — not a blocker. They will be
            // registered into HotTypeRegistry after the hot assembly loads (Task 3).
            // Includes compiler-generated types (closures, async state machines) that
            // accompany new or changed methods.
            public readonly IReadOnlyList<string> NewTypeFullNames;

            // Methods present in the runtime assembly but absent from the compiled source —
            // the developer deleted or renamed them, or changed a signature (which reads as
            // remove-old + add-new). NOT a blocker: a JIT'd method cannot be removed from
            // memory anyway, so the runtime keeps its original body and existing call sites
            // stay valid. Reported so the developer knows that code is now stale.
            public readonly IReadOnlyList<string> RemovedMethodKeys;

            // Factory: validation passed, here are the new types and removed methods found.
            public static CompatibilityResult Compatible(
                IReadOnlyList<string> newTypes,
                IReadOnlyList<string> removedMethods)
                => new CompatibilityResult(
                    true,
                    string.Empty,
                    newTypes ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    removedMethods ?? (IReadOnlyList<string>)Array.Empty<string>());

            // Factory: a hard structural blocker was found; patching must be aborted.
            public static CompatibilityResult Incompatible(string reason)
                => new CompatibilityResult(false, reason, Array.Empty<string>(), Array.Empty<string>());

            private CompatibilityResult(
                bool isCompatible,
                string reason,
                IReadOnlyList<string> newTypes,
                IReadOnlyList<string> removedMethods)
            {
                IsCompatible = isCompatible;
                BlockingReason = reason;
                NewTypeFullNames = newTypes;
                RemovedMethodKeys = removedMethods;
            }
        }

        private readonly string _assemblyName;
        private readonly Dictionary<string, ILHook> _methodHooks = new Dictionary<string, ILHook>(StringComparer.Ordinal);
        private readonly Dictionary<string, TrampolineHook> _trampolineHooks = new Dictionary<string, TrampolineHook>(StringComparer.Ordinal);
        private readonly object _sync = new object();

        internal InstaReloadPatcher(string assemblyName)
        {
            _assemblyName = assemblyName;
        }

        public void Dispose()
        {
            Reset();
        }

        internal void Reset()
        {
            lock (_sync)
            {
                DisposeAllHooks();
            }
        }

        /// <summary>
        /// Phase breakdown of the most recent ApplyAssembly call. Exposed as state rather than
        /// threaded through the return value because ApplyAssembly has many early-return paths;
        /// this way a failed apply still reports how far it got and what that cost.
        /// Main-thread only, one apply at a time, so a plain field is sufficient.
        /// </summary>
        internal PatchPhaseTimings LastPhaseTimings { get; private set; }

        /// <summary>
        /// Cecil assembly resolver, kept across reloads.
        ///
        /// A fresh DefaultAssemblyResolver was built on every ApplyAssembly. Its internal cache
        /// started empty each time, so every reload re-resolved UnityEngine, mscorlib and friends
        /// from disk. The search directories are fixed for the editor session, so one resolver
        /// serves every reload and stays warm. Rebuilt only if the patch directory changes.
        /// </summary>
        private static DefaultAssemblyResolver _sharedResolver;
        private static string _sharedResolverPatchDirectory;

        private static DefaultAssemblyResolver GetSharedResolver(string assemblyPath)
        {
            var patchDirectory = System.IO.Path.GetDirectoryName(assemblyPath);

            if (_sharedResolver != null &&
                string.Equals(_sharedResolverPatchDirectory, patchDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return _sharedResolver;
            }

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(patchDirectory);
            resolver.AddSearchDirectory(UnityEditor.EditorApplication.applicationContentsPath + "/NetStandard/ref/2.1.0");
            resolver.AddSearchDirectory(UnityEditor.EditorApplication.applicationContentsPath + "/Managed");

            _sharedResolver = resolver;
            _sharedResolverPatchDirectory = patchDirectory;
            return resolver;
        }

        internal PatchApplyResult ApplyAssembly(
            string assemblyPath,
            bool skipValidation = false,
            PatchReplayContext replayContext = null,
            bool preserveExistingHooks = false)
        {
            Assembly runtimeAssembly = null;
            try
            {
                runtimeAssembly = FindRuntimeAssembly();
                if (runtimeAssembly == null)
                {
                    var error = $"Assembly '{_assemblyName}' not loaded - make sure it's referenced in your project";
                    InstaReloadLogger.LogError(InstaReloadLogCategory.Patcher, error);
                    return CreateFailureResult(Guid.Empty, error);
                }

                // Read the compiled assembly's IL structure via Cecil so we can inspect
                // types and methods before loading anything into the AppDomain.
                // We parse first, validate second, load third — this way we never pollute
                // the AppDomain with an assembly that will be rejected by compatibility checks.
                // Fresh timings per apply. Fields are filled as each phase completes, so the
                // early-return paths below still leave partial, truthful data behind.
                var phases = new PatchPhaseTimings();
                LastPhaseTimings = phases;
                var phaseWatch = System.Diagnostics.Stopwatch.StartNew();

                ModuleDefinition updatedModule = null;
                try
                {
                    updatedModule = ModuleDefinition.ReadModule(
                        assemblyPath,
                        new ReaderParameters
                        {
                            ReadSymbols = false,

                            // Deferred, not Immediate. Immediate eagerly reads every type and
                            // method body in the module and resolves its references — for a 3KB
                            // patch assembly where only a handful of methods are touched, that
                            // work is thrown away. Measured at ~41ms per reload, over half of
                            // total hot reload latency.
                            ReadingMode = ReadingMode.Deferred,

                            AssemblyResolver = GetSharedResolver(assemblyPath)
                        });
                }
                catch (Exception ex)
                {
                    var error = $"Failed to read compiled assembly: {ex.Message}";
                    InstaReloadLogger.LogError(InstaReloadLogCategory.Patcher, error);
                    return CreateFailureResult(runtimeAssembly.ManifestModule.ModuleVersionId, error);
                }

                phases.CecilReadMs = phaseWatch.Elapsed.TotalMilliseconds;
                phaseWatch.Restart();

                using (updatedModule)
                {
                    // newTypeNamesForRegistry collects types the developer added that don't
                    // yet exist in the runtime assembly. On the fast path these are always
                    // empty (ChangeAnalyzer only sends method-body-only changes there).
                    // On the slow path CheckCompatibility fills this list, and Task 3 will
                    // read it after the hot assembly loads to register types in HotTypeRegistry.
                    IReadOnlyList<string> newTypeNamesForRegistry = Array.Empty<string>();

                    if (!skipValidation)
                    {
                        // Full structural validation — checks for removed methods (hard blocker)
                        // and collects new types (not a blocker, just needs registration).
                        var compat = CheckCompatibility(runtimeAssembly, updatedModule);
                        if (!compat.IsCompatible)
                        {
                            var error = $"Structural change: {compat.BlockingReason}";
                            InstaReloadLogger.LogWarning(InstaReloadLogCategory.Patcher, error);
                            InstaReloadLogger.LogWarning(InstaReloadLogCategory.Patcher, "→ Exit Play Mode to apply this change");
                            return CreateFailureResult(runtimeAssembly.ManifestModule.ModuleVersionId, error);
                        }

                        newTypeNamesForRegistry = compat.NewTypeFullNames;

                        // Removed / renamed / re-signatured methods keep their original bodies
                        // in memory. Nothing breaks, but callers that weren't patched in this
                        // cycle now run stale code, so say so rather than letting it be silent.
                        if (compat.RemovedMethodKeys.Count > 0)
                        {
                            InstaReloadLogger.LogWarning(
                                InstaReloadLogCategory.Patcher,
                                $"{compat.RemovedMethodKeys.Count} method(s) no longer in source — the running build keeps their OLD implementation until you exit Play Mode:");
                            foreach (var removedKey in compat.RemovedMethodKeys.Take(3))
                            {
                                InstaReloadLogger.LogWarning(InstaReloadLogCategory.Patcher, $"  -> {removedKey}");
                            }
                            if (compat.RemovedMethodKeys.Count > 3)
                            {
                                InstaReloadLogger.LogWarning(
                                    InstaReloadLogCategory.Patcher,
                                    $"  ... and {compat.RemovedMethodKeys.Count - 3} more");
                            }
                        }

                        if (newTypeNamesForRegistry.Count > 0)
                        {
                            InstaReloadLogger.Log($"[Patcher] {newTypeNamesForRegistry.Count} new type(s) detected — registering after hot assembly load");
                            foreach (var name in newTypeNamesForRegistry)
                                InstaReloadLogger.LogVerbose($"  + {name}");
                        }
                    }
                    else
                    {
                        // Fast path: ChangeAnalyzer already confirmed only method bodies changed,
                        // so no new types are possible and structural validation can be skipped.
                        InstaReloadLogger.LogVerbose("[Patcher] ⚡ Fast path - skipping structural validation (trusted)");
                    }

                    // Load the compiled assembly into the AppDomain AFTER validation so we
                    // never pollute the AppDomain with a rejected assembly.
                    // We capture the returned Assembly reference — Task 3 will use it to
                    // look up new types by full name and register them in HotTypeRegistry.
                    // Without capturing it here, the reference is lost and we'd have to do
                    // an expensive AppDomain scan to find it again.
                    phases.ValidateMs = phaseWatch.Elapsed.TotalMilliseconds;
                    phaseWatch.Restart();

                    Assembly hotAssembly = null;
                    try
                    {
                        var assemblyBytes = System.IO.File.ReadAllBytes(assemblyPath);
                        hotAssembly = System.Reflection.Assembly.Load(assemblyBytes);
                        InstaReloadLogger.LogVerbose($"[Patcher] Hot assembly loaded: {hotAssembly.GetName().Name}");
                    }
                    catch (Exception ex)
                    {
                        // Loading failure is non-fatal for existing method patches —
                        // IL cloning still works from the Cecil module. But new types
                        // won't be available, so log a warning instead of hard-failing.
                        InstaReloadLogger.LogWarning($"[Patcher] Failed to load hot assembly — new types unavailable: {ex.Message}");
                    }

                    // Register new types BEFORE building the method map so that methods
                    // on those new types are included in the map for this cycle's patching.
                    // If we registered after, calls from patched methods into new types
                    // would fail to resolve at IL-clone time within the same hot reload cycle.
                    if (hotAssembly != null && newTypeNamesForRegistry.Count > 0)
                        RegisterNewTypes(hotAssembly, newTypeNamesForRegistry);

                    // BuildRuntimeMethodMap now folds in all types from HotTypeRegistry,
                    // which covers both types just registered above AND types registered in
                    // previous hot reload cycles within this session.
                    phases.AssemblyLoadMs = phaseWatch.Elapsed.TotalMilliseconds;
                    phaseWatch.Restart();

                    var runtimeMethods = BuildRuntimeMethodMap(runtimeAssembly);
                    var runtimeMethodTokens = BuildRuntimeMethodTokenMap(runtimeAssembly);
                    var runtimeFields = BuildRuntimeFieldMap(runtimeAssembly);
                    var methodIds = BuildMethodIdMap(updatedModule);
                    var dispatchKeys = BuildDispatchKeySet(updatedModule, runtimeMethods);
                    var runtimeMvid = runtimeAssembly.ManifestModule.ModuleVersionId;
                    var useTokenReplay = replayContext != null && replayContext.CanUseTokens(runtimeMvid);

                    var dispatcherInvokeMethod = ResolveDispatcherInvokeMethod(runtimeAssembly);
                    if (dispatcherInvokeMethod == null)
                    {
                        var error = "Dispatcher Invoke method not found";
                        InstaReloadLogger.LogError(InstaReloadLogCategory.Patcher, error);
                        return CreateFailureResult(runtimeAssembly.ManifestModule.ModuleVersionId, error);
                    }

                    phases.MapBuildMs = phaseWatch.Elapsed.TotalMilliseconds;
                    phaseWatch.Restart();

                    lock (_sync)
                    {
                        if (!preserveExistingHooks)
                        {
                            DisposeMethodHooks();
                        }

                        int patched = 0;
                        int skipped = 0;
                        int newMethods = 0;
                        int dispatched = 0;
                        int trampolines = 0;
                        var errors = new List<string>();

                        // Constructs we deliberately refuse to patch (async state machines today).
                        // Kept separate from `errors` so a known limitation is not reported as a
                        // failure — see the note at the IsMethodBodySupported call site.
                        var unsupportedNotes = new List<string>();
                        var newMethodNames = new List<string>();
                        var missingEntryPoints = new List<string>();
                        var tokenPairs = new Dictionary<int, MethodTokenPair>();
                        var patchRecords = new Dictionary<string, MethodPatchRecord>(StringComparer.Ordinal);

                        void RecordPatch(string methodKey, HotReloadPatchKind kind, MethodBase runtimeMethod)
                        {
                            if (string.IsNullOrEmpty(methodKey))
                            {
                                return;
                            }

                            if (patchRecords.TryGetValue(methodKey, out var existing))
                            {
                                var mergedKind = existing.Kind | kind;
                                var mergedMethod = existing.RuntimeMethod ?? runtimeMethod;
                                patchRecords[methodKey] = new MethodPatchRecord(methodKey, mergedKind, mergedMethod);
                                return;
                            }

                            patchRecords[methodKey] = new MethodPatchRecord(methodKey, kind, runtimeMethod);
                        }

                        var skippedGenerics = new List<string>();
                        foreach (var method in GetPatchableMethods(updatedModule, skippedGenerics))
                        {
                            var methodName = GetMethodKey(method);

                            // Skip Unity-generated methods
                            if (method.DeclaringType.Name.StartsWith("UnitySourceGenerated"))
                            {
                                skipped++;
                                continue;
                            }

                            if (!IsMethodBodySupported(method, runtimeFields, out var unsupportedReason))
                            {
                                skipped++;
                                // A deliberate refusal is not a failure. Routing these through
                                // `errors` printed "Failed to patch N method(s)" in red, which
                                // reads as breakage when it is a known, documented limitation.
                                if (!string.IsNullOrEmpty(unsupportedReason))
                                {
                                    unsupportedNotes.Add($"{methodName}: {unsupportedReason}");
                                }
                                continue;
                            }

                            var key = methodName;

                            if (IsUnityEntryPoint(method))
                            {
                                try
                                {
                                    var runtimeEntryPointMethod = ResolveRuntimeMethod(
                                        method,
                                        key,
                                        runtimeMethods,
                                        runtimeMethodTokens,
                                        replayContext,
                                        useTokenReplay);

                                    if (methodIds.TryGetValue(key, out var methodId))
                                    {
                                        if (runtimeEntryPointMethod != null)
                                        {
                                            if (EnsureTrampoline(runtimeEntryPointMethod, key, dispatcherInvokeMethod, methodId))
                                            {
                                                trampolines++;
                                                RecordPatch(key, HotReloadPatchKind.Trampoline, runtimeEntryPointMethod);
                                            }
                                            TryTrackTokenPair(tokenPairs, method, runtimeEntryPointMethod, key);
                                        }
                                        else
                                        {
                                            if (InheritsHotReloadBehaviour(method.DeclaringType))
                                            {
                                                InstaReloadLogger.LogVerbose($"[Patcher] Entry point {methodName} dispatched via HotReloadBehaviour");
                                            }
                                            else if (TryGetFallbackEntryPointKind(method, out var entryPointKind) &&
                                                     TryRegisterMissingEntryPoint(method, runtimeAssembly, entryPointKind, methodId))
                                            {
                                                InstaReloadLogger.LogVerbose($"[Patcher] Unity message {methodName} dispatched via fallback proxy");
                                            }
                                            else
                                            {
                                                missingEntryPoints.Add(methodName);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        skipped++;
                                        errors.Add($"{methodName}: Method id not found");
                                        continue;
                                    }

                                    if (TryRegisterDispatcher(method, runtimeAssembly, runtimeMethods, runtimeFields, methodIds, dispatchKeys, dispatcherInvokeMethod, out var error))
                                    {
                                        dispatched++;
                                        RecordPatch(key, HotReloadPatchKind.Dispatched, runtimeEntryPointMethod);
                                    }
                                    else
                                    {
                                        skipped++;
                                        if (!string.IsNullOrEmpty(error))
                                        {
                                            errors.Add($"{methodName}: {error}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    skipped++;
                                    errors.Add($"{methodName}: {ex.Message}");
                                }

                                continue;
                            }

                            var runtimeTargetMethod = ResolveRuntimeMethod(
                                method,
                                key,
                                runtimeMethods,
                                runtimeMethodTokens,
                                replayContext,
                                useTokenReplay);

                            if (runtimeTargetMethod != null)
                            {
                                try
                                {
                                    var hook = new ILHook(
                                        runtimeTargetMethod,
                                        ctx => ReplaceMethodBody(ctx, method, runtimeAssembly, runtimeMethods, runtimeFields, methodIds, dispatchKeys, dispatcherInvokeMethod));
                                    if (_methodHooks.TryGetValue(key, out var existingHook))
                                    {
                                        existingHook.Dispose();
                                        _methodHooks.Remove(key);
                                    }
                                    _methodHooks[key] = hook;
                                    patched++;
                                    RecordPatch(key, HotReloadPatchKind.Patched, runtimeTargetMethod);
                                    TryTrackTokenPair(tokenPairs, method, runtimeTargetMethod, key);
                                }
                                catch (Exception ex)
                                {
                                    skipped++;
                                    errors.Add($"{methodName}: {ex.Message}");
                                }

                                continue;
                            }

                            try
                            {
                                if (methodIds.TryGetValue(key, out var methodId) &&
                                    TryGetFallbackEntryPointKind(method, out var entryPointKind))
                                {
                                    if (TryRegisterMissingEntryPoint(method, runtimeAssembly, entryPointKind, methodId))
                                    {
                                        InstaReloadLogger.LogVerbose($"[Patcher] Unity message {methodName} dispatched via fallback proxy");
                                    }
                                    else
                                    {
                                        missingEntryPoints.Add(methodName);
                                    }
                                }

                                if (TryRegisterDispatcher(method, runtimeAssembly, runtimeMethods, runtimeFields, methodIds, dispatchKeys, dispatcherInvokeMethod, out var error))
                                {
                                    newMethods++;
                                    dispatched++;
                                    newMethodNames.Add(methodName);
                                    RecordPatch(key, HotReloadPatchKind.Dispatched, runtimeTargetMethod);
                                }
                                else
                                {
                                    skipped++;
                                    if (!string.IsNullOrEmpty(error))
                                    {
                                        errors.Add($"{methodName}: {error}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                skipped++;
                                errors.Add($"{methodName}: {ex.Message}");
                            }
                        }

                        if (newMethods > 0)
                        {
                            InstaReloadLogger.Log($"[Patcher] {newMethods} new method(s) registered for dispatcher");
                            foreach (var name in newMethodNames.Take(3))
                            {
                                InstaReloadLogger.Log($"  -> {name}");
                            }
                            if (newMethodNames.Count > 3)
                            {
                                InstaReloadLogger.Log($"  ... and {newMethodNames.Count - 3} more");
                            }
                        }

                        phases.HookApplyMs = phaseWatch.Elapsed.TotalMilliseconds;

                        if (missingEntryPoints.Count > 0)
                        {
                            InstaReloadLogger.LogWarning($"[Patcher] {missingEntryPoints.Count} Unity message method(s) missing at runtime (added during Play Mode):");
                            foreach (var name in missingEntryPoints.Take(3))
                            {
                                InstaReloadLogger.LogWarning($"  -> {name}");
                            }
                            if (missingEntryPoints.Count > 3)
                            {
                                InstaReloadLogger.LogWarning($"  ... and {missingEntryPoints.Count - 3} more");
                            }
                        }

                        if (patched > 0 || dispatched > 0 || trampolines > 0)
                        {
                            var message = $"[Patcher] Hot reload complete - patched: {patched}, dispatched: {dispatched}";
                            if (trampolines > 0)
                            {
                                message += $", trampolines: {trampolines}";
                            }
                            // Counts also travel out on PatchApplyResult and are printed by the
                            // single per-reload summary line, so this stays available for
                            // debugging without duplicating that line.
                            InstaReloadLogger.LogVerbose(message);

                            var overlayType = System.Type.GetType("Nimrita.InstaReload.Editor.UI.InstaReloadStatusOverlay, InstaReload.Editor");
                            if (overlayType != null)
                            {
                                var showMethod = overlayType.GetMethod(
                                    "ShowMessage",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                showMethod?.Invoke(null, new object[] { $"Reloaded {patched + dispatched} method(s)", true });
                            }
                        }
                        else if (skipped > 0)
                        {
                            InstaReloadLogger.LogWarning($"[Patcher] No methods updated ({skipped} skipped)");
                        }

                        // Generic methods never reach the patch loop, so they never increment
                        // `skipped` — without this the reload reports a confident success while
                        // silently leaving the edited method running its old body.
                        if (skippedGenerics.Count > 0)
                        {
                            InstaReloadLogger.LogWarning(
                                $"[Patcher] {skippedGenerics.Count} generic method(s) NOT patched - your edits to these have NO effect until you exit Play Mode:");
                            foreach (var name in skippedGenerics.Take(3))
                            {
                                InstaReloadLogger.LogWarning($"  -> {name}");
                            }
                            if (skippedGenerics.Count > 3)
                            {
                                InstaReloadLogger.LogWarning($"  ... and {skippedGenerics.Count - 3} more");
                            }
                        }

                        if (unsupportedNotes.Count > 0)
                        {
                            InstaReloadLogger.LogWarning(
                                $"[Patcher] {unsupportedNotes.Count} method(s) NOT patched (unsupported construct):");
                            foreach (var note in unsupportedNotes.Take(3))
                            {
                                InstaReloadLogger.LogWarning($"  -> {note}");
                            }
                            if (unsupportedNotes.Count > 3)
                            {
                                InstaReloadLogger.LogWarning($"  ... and {unsupportedNotes.Count - 3} more");
                            }
                        }

                        if (errors.Count > 0)
                        {
                            InstaReloadLogger.LogError($"[Patcher] Failed to patch {errors.Count} method(s) in {_assemblyName}:");
                            foreach (var error in errors.Take(5)) // Show max 5 errors
                            {
                                InstaReloadLogger.LogError($"  -> {error}");
                            }
                            if (errors.Count > 5)
                            {
                                InstaReloadLogger.LogError($"  ... and {errors.Count - 5} more");
                            }
                        }

                        return new PatchApplyResult(
                            _assemblyName,
                            runtimeMvid,
                            tokenPairs.Values.ToList(),
                            patched,
                            dispatched,
                            trampolines,
                            skipped,
                            errors,
                            patchRecords.Values.ToList(),
                            skippedGenerics);
                    }
                }
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogError($"[Patcher] Hot reload failed for {_assemblyName}: {ex.Message}");
                InstaReloadLogger.LogError("[Patcher] Try exiting Play Mode and re-entering");
                var runtimeMvid = runtimeAssembly != null ? runtimeAssembly.ManifestModule.ModuleVersionId : Guid.Empty;
                return CreateFailureResult(runtimeMvid, $"Hot reload failed: {ex.Message}");
            }
        }

        private PatchApplyResult CreateFailureResult(Guid runtimeMvid, string error)
        {
            var errors = string.IsNullOrEmpty(error)
                ? Array.Empty<string>()
                : new[] { error };

            return new PatchApplyResult(
                _assemblyName,
                runtimeMvid,
                Array.Empty<MethodTokenPair>(),
                0,
                0,
                0,
                0,
                errors);
        }

        private void DisposeAllHooks()
        {
            DisposeMethodHooks();
            DisposeTrampolineHooks();
        }

        private void DisposeMethodHooks()
        {
            foreach (var hook in _methodHooks.Values)
            {
                hook.Dispose();
            }

            _methodHooks.Clear();
        }

        private void DisposeTrampolineHooks()
        {
            foreach (var hook in _trampolineHooks.Values)
            {
                hook.Hook.Dispose();
            }

            _trampolineHooks.Clear();
        }

        private Assembly FindRuntimeAssembly()
        {
            var matches = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(asm => string.Equals(asm.GetName().Name, _assemblyName, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            var withLocation = matches.FirstOrDefault(asm => !string.IsNullOrEmpty(asm.Location));
            return withLocation ?? matches[0];
        }

        private static MethodInfo ResolveDispatcherInvokeMethod(Assembly runtimeAssembly)
        {
            if (runtimeAssembly != null)
            {
                var bridgeType = runtimeAssembly.GetType(DispatcherBridgeTypeName);
                var bridgeMethod = bridgeType?.GetMethod(
                    "Invoke",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (bridgeMethod != null)
                {
                    InstaReloadLogger.LogVerbose($"[Patcher] Using dispatcher bridge {DispatcherBridgeTypeName}.Invoke");
                    return bridgeMethod;
                }
            }

            var fallback = typeof(HotReloadDispatcher).GetMethod(
                "Invoke",
                BindingFlags.Public | BindingFlags.Static);
            if (fallback != null)
            {
                InstaReloadLogger.LogVerbose("[Patcher] Using runtime HotReloadDispatcher.Invoke");
            }

            return fallback;
        }

        // Looks up each new type name in the hot assembly and registers it in HotTypeRegistry.
        //
        // WHY A SEPARATE METHOD:
        //   The registration loop needs a compiler-generated type fallback (linear scan)
        //   because the C# compiler mangles names like "<>c__DisplayClass3_0" in ways that
        //   don't always round-trip through Assembly.GetType(). Isolating that logic here
        //   keeps ApplyAssembly readable and lets us reason about registration in one place.
        //
        // WHY WE USE NORMALIZED NAMES AS KEYS:
        //   Cecil uses '/' as the nested-type separator; reflection uses '+'. NormalizeTypeName
        //   converts '/' → '+' before names reach this method, so Assembly.GetType() works
        //   directly with the key — no extra conversion needed here.
        private static void RegisterNewTypes(Assembly hotAssembly, IReadOnlyList<string> newTypeNames)
        {
            foreach (var fullName in newTypeNames)
            {
                // Primary lookup: direct by normalized full name.
                var type = hotAssembly.GetType(fullName);

                if (type == null)
                {
                    // Fallback: linear scan for compiler-generated types whose names
                    // are mangled (e.g. "<MyMethod>d__1", "<>c__DisplayClass2_0").
                    // These are valid hot types — they carry closures and async state
                    // machines that accompany new or changed methods.
                    var allTypes = hotAssembly.GetTypes();
                    for (int i = 0; i < allTypes.Length; i++)
                    {
                        if (NormalizeTypeName(allTypes[i].FullName) == fullName)
                        {
                            type = allTypes[i];
                            break;
                        }
                    }
                }

                if (type == null)
                {
                    InstaReloadLogger.LogWarning($"[Patcher] New type '{fullName}' not found in hot assembly — IL references to it will fall back to import");
                    continue;
                }

                HotTypeRegistry.Register(fullName, type);
                InstaReloadLogger.LogVerbose($"[Patcher] Registered new type: {fullName}");

                // If the new type is a MonoBehaviour, register its lifecycle entry points
                // so the proxy scanner can attach dispatchers to any instances created at
                // runtime (e.g. via AddComponent). See RegisterMonoBehaviourEntryPoints
                // for a detailed explanation of why this is needed.
                RegisterMonoBehaviourEntryPoints(type);
            }
        }

        // Builds a lookup map of MethodKey → MethodBase covering:
        //   1. All types in the original runtime assembly (the compiled project DLL).
        //   2. All types in HotTypeRegistry — types introduced by previous hot reload
        //      cycles in this session.
        //
        // WHY WE INCLUDE HOT TYPES HERE:
        //   Each hot reload cycle may introduce new types. A subsequent cycle may patch
        //   a method that calls into one of those previously-hot types. Without including
        //   hot types in the method map, CloneInstruction can't resolve those callees
        //   and IL rewriting silently falls back to importing the Cecil reference, which
        //   may not bind correctly at runtime. Including them here closes that gap.
        //
        // NOTE: Types registered in THIS cycle are added to HotTypeRegistry in
        //   RegisterNewTypes(), which is called before this method, so they are
        //   already present in the registry by the time we call HotTypeRegistry.GetAll().
        private static Dictionary<string, MethodBase> BuildRuntimeMethodMap(Assembly runtimeAssembly)
        {
            var map = new Dictionary<string, MethodBase>(StringComparer.Ordinal);

            // Original runtime assembly types.
            AddMethodsFromTypes(map, runtimeAssembly.GetTypes());

            // Types introduced by hot reload in previous (and current) cycles.
            // GetAll() returns a snapshot list so we don't hold the registry lock
            // while iterating and calling reflection APIs.
            var hotTypes = HotTypeRegistry.GetAll();
            if (hotTypes.Count > 0)
            {
                AddMethodsFromTypes(map, hotTypes);
                InstaReloadLogger.LogVerbose($"[Patcher] Method map includes {hotTypes.Count} hot type(s) from previous reload cycles");
            }

            return map;
        }

        // Shared helper: adds all methods, constructors, and type initializers from a
        // collection of types into the method map. Extracted to avoid duplication between
        // the runtime assembly pass and the hot-type pass above.
        private static void AddMethodsFromTypes(Dictionary<string, MethodBase> map, IEnumerable<Type> types)
        {
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(flags))
                    map[GetMethodKey(method)] = method;

                foreach (var ctor in type.GetConstructors(flags))
                    map[GetMethodKey(ctor)] = ctor;

                if (type.TypeInitializer != null)
                    map[GetMethodKey(type.TypeInitializer)] = type.TypeInitializer;
            }
        }

        // Registers Unity lifecycle entry points for a new MonoBehaviour type.
        //
        // WHY THIS IS NEEDED FOR NEW TYPES (not just modified ones):
        //   When the developer adds a new MonoBehaviour class and hot reloads, Unity can
        //   instantiate it via AddComponent(type) and call its lifecycle methods natively —
        //   because the hot assembly is a real loaded assembly. That part works without any
        //   registration.
        //
        //   However, two things require explicit registration:
        //   1. PROXY SCANNER: HotReloadEntryPointManager scans for existing instances of
        //      registered types every 0.5s and attaches HotReloadEntryPointProxy components.
        //      If the developer creates an instance from patched code during play mode,
        //      the scanner will find it and ensure the dispatch chain is attached.
        //   2. SUBSEQUENT PATCHES: When the developer later edits the new MonoBehaviour's
        //      lifecycle method, the patcher needs a trampoline on the runtime method.
        //      The trampoline is installed the first time the method is patched. Registering
        //      with HotReloadEntryPointManager here means the proxy is already set up, so
        //      the second-cycle patch can attach its trampoline immediately without waiting
        //      for the scanner's next tick.
        //
        // WHY WE WALK BASE TYPES BY NAME (not via typeof(MonoBehaviour)):
        //   Using typeof(UnityEngine.MonoBehaviour) creates a hard compile-time dependency
        //   on the UnityEngine module. Walking the base type chain by name works across all
        //   Unity versions and doesn't break if the module layout changes.
        private static void RegisterMonoBehaviourEntryPoints(Type newType)
        {
            if (newType == null || !IsMonoBehaviourSubclass(newType))
                return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var methods = newType.GetMethods(flags);
            int registered = 0;

            foreach (var method in methods)
            {
                // Skip generic methods and methods with unsupported signatures early.
                if (method.IsGenericMethod || method.IsGenericMethodDefinition)
                    continue;

                if (!FallbackEntryPointsByName.TryGetValue(method.Name, out var signatures))
                    continue;

                var parameters = method.GetParameters();
                foreach (var sig in signatures)
                {
                    if (sig.ParameterTypes.Length != parameters.Length)
                        continue;

                    bool matches = true;
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        if (!string.Equals(GetTypeName(parameters[p].ParameterType), sig.ParameterTypes[p], StringComparison.Ordinal))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (!matches)
                        continue;

                    var methodKey = GetMethodKey(method);
                    var methodId = GetMethodId(methodKey);
                    if (HotReloadEntryPointManager.TryRegisterMissingEntryPoint(newType, sig.Kind, methodId))
                        registered++;

                    break; // matched this method — move to the next method
                }
            }

            if (registered > 0)
                InstaReloadLogger.Log($"[Patcher] New MonoBehaviour '{newType.Name}': registered {registered} Unity entry point(s) for proxy dispatch");
        }

        // Checks whether a type is a subclass of MonoBehaviour by walking its base type
        // chain and matching by name. Avoids a hard typeof(MonoBehaviour) dependency.
        private static bool IsMonoBehaviourSubclass(Type type)
        {
            const string monoBehaviourName = "UnityEngine.MonoBehaviour";
            const string componentName = "UnityEngine.Component";

            var current = type.BaseType;
            while (current != null)
            {
                var name = current.FullName;
                if (string.Equals(name, monoBehaviourName, StringComparison.Ordinal) ||
                    string.Equals(name, componentName, StringComparison.Ordinal))
                    return true;

                current = current.BaseType;
            }

            return false;
        }

        /// <param name="skippedGenerics">Optional collector. Generic methods are dropped here
        /// before they ever reach the patch loop, so they never increment its skipped counter and
        /// a reload that ignored them still reports success. Pass a list to make that visible.</param>
        private static IEnumerable<MethodDefinition> GetPatchableMethods(
            ModuleDefinition module,
            List<string> skippedGenerics = null)
        {
            foreach (var type in module.Types)
            {
                foreach (var method in GetPatchableMethods(type, skippedGenerics))
                {
                    yield return method;
                }
            }
        }

        private static IEnumerable<MethodDefinition> GetPatchableMethods(
            TypeDefinition type,
            List<string> skippedGenerics = null)
        {
            if (type.Name == "<Module>")
            {
                yield break;
            }

            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                if (method.IsAbstract || method.IsPInvokeImpl)
                {
                    continue;
                }

                if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters)
                {
                    skippedGenerics?.Add(GetMethodKey(method));
                    InstaReloadLogger.LogVerbose($"Skipping generic method: {GetMethodKey(method)}.");
                    continue;
                }

                yield return method;
            }

            foreach (var nested in type.NestedTypes)
            {
                foreach (var method in GetPatchableMethods(nested, skippedGenerics))
                {
                    yield return method;
                }
            }
        }

        // Checks whether the compiled assembly is structurally compatible with the runtime assembly.
        //
        // "Compatible" does NOT mean identical — it means patchable without a domain reload.
        // Specifically:
        //   ALLOWED  → new types (collected into NewTypeFullNames for HotTypeRegistry, Task 3)
        //   ALLOWED  → new methods on existing types (registered via dispatcher)
        //   ALLOWED  → field set changes on existing types (routed to HotReloadFieldStore)
        //   BLOCKED  → removed methods on existing types (call sites in runtime would crash)
        //
        // Why we only validate types present in the compiled update:
        //   We compile one file at a time. The compiled assembly contains only the types
        //   defined in that file — always fewer than the full runtime assembly. Comparing
        //   the full type sets would always fail, so we only walk the types we compiled.
        private static CompatibilityResult CheckCompatibility(Assembly runtimeAssembly, ModuleDefinition updatedModule)
        {
            var runtimeTypes = runtimeAssembly.GetTypes().ToDictionary(t => t.FullName, t => t);
            var updatedTypes = GetAllTypes(updatedModule)
                .Where(t => t.Name != "<Module>")
                .ToList();

            // Accumulate new type names rather than blocking on them.
            // New types are valid — they just need to be registered in HotTypeRegistry
            // after the hot assembly loads so that patched methods can reference them.
            // This includes compiler-generated types (closures, async state machines)
            // that the C# compiler emits alongside new or changed methods.
            var newTypeNames = new List<string>();
            var removedMethodKeys = new List<string>();

            foreach (var updatedType in updatedTypes)
            {
                var runtimeName = NormalizeTypeName(updatedType.FullName);
                if (!runtimeTypes.TryGetValue(runtimeName, out var runtimeType))
                {
                    // Type is genuinely new — collect it for registration, then keep going.
                    newTypeNames.Add(runtimeName);
                    continue;
                }

                // For existing types, check field and method compatibility.
                // Field changes are tolerated (routed to HotReloadFieldStore) — FieldSetsMatch
                // logs a warning but always returns true, so this never blocks.
                if (!FieldSetsMatch(updatedType, runtimeType, out var fieldReason))
                    return CompatibilityResult.Incompatible(fieldReason);

                // A loaded CLR type's base class and interface list are fixed — patching method
                // bodies cannot change them. Such an edit is therefore a silent no-op, and left
                // unreported it reads as success: the reload prints "patched N" while every
                // interface-based lookup (GetComponents<IFoo>(), `is IFoo`) still skips the
                // object. Warn rather than block, consistent with removed methods and fields.
                WarnOnInheritanceChange(updatedType, runtimeType);

                // Removed methods are tolerated and reported — see MethodSetsMatch for why they
                // cannot crash. New methods are dispatched dynamically via HotReloadDispatcher.
                if (!MethodSetsMatch(updatedType, runtimeType, out var methodReason, out var removedFromType))
                    return CompatibilityResult.Incompatible(methodReason);

                removedMethodKeys.AddRange(removedFromType);
            }

            return CompatibilityResult.Compatible(newTypeNames, removedMethodKeys);
        }

        private static IEnumerable<TypeDefinition> GetAllTypes(ModuleDefinition module)
        {
            foreach (var type in module.Types)
            {
                foreach (var nested in GetAllTypes(type))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<TypeDefinition> GetAllTypes(TypeDefinition type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
            {
                foreach (var child in GetAllTypes(nested))
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// Reports base-class and interface changes on an existing type. These cannot be applied —
        /// the CLR fixes a type's hierarchy when it loads — so the edit takes no effect until the
        /// next domain reload. Verified 2026-08-05: changing the base class and adding an interface
        /// both left runtimeType.BaseType and `is IFoo` untouched while the reload reported success.
        ///
        /// Compares against runtimeType.GetInterfaces(), which includes inherited interfaces, so an
        /// interface the type already gets from a base does not produce a false warning.
        /// </summary>
        private static void WarnOnInheritanceChange(TypeDefinition updatedType, Type runtimeType)
        {
            var updatedBase = updatedType.BaseType == null
                ? null
                : NormalizeTypeName(updatedType.BaseType.FullName);
            var runtimeBase = runtimeType.BaseType == null
                ? null
                : NormalizeTypeName(runtimeType.BaseType.FullName);

            if (!string.Equals(updatedBase, runtimeBase, StringComparison.Ordinal))
            {
                InstaReloadLogger.LogWarning(
                    $"[Patcher] {runtimeType.Name}: base class changed ({runtimeBase ?? "none"} -> {updatedBase ?? "none"}) " +
                    "- NOT applied, the running type keeps its original base until you exit Play Mode");
            }

            var runtimeInterfaces = new HashSet<string>(
                runtimeType.GetInterfaces().Select(i => NormalizeTypeName(i.FullName)),
                StringComparer.Ordinal);

            foreach (var added in updatedType.Interfaces
                .Select(i => NormalizeTypeName(i.InterfaceType.FullName))
                .Where(name => !runtimeInterfaces.Contains(name)))
            {
                InstaReloadLogger.LogWarning(
                    $"[Patcher] {runtimeType.Name}: interface {added} added but NOT applied - " +
                    "`is`/`as` casts and GetComponents<T>() will keep skipping this object until you exit Play Mode");
            }
        }

        private static bool FieldSetsMatch(TypeDefinition updatedType, Type runtimeType, out string reason)
        {
            var updatedFields = new HashSet<string>(
                updatedType.Fields.Select(GetFieldKey),
                StringComparer.Ordinal);

            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var runtimeFields = new HashSet<string>(
                runtimeType.GetFields(flags).Select(GetFieldKey),
                StringComparer.Ordinal);

            if (!updatedFields.SetEquals(runtimeFields))
            {
                // Fields can be added or removed without blocking hot reload.
                //
                // ADDED fields: the runtime type has no memory slot for them, so any IL that
                // reads/writes them gets rewritten by TryRewriteFieldInstruction to call
                // HotReloadFieldStore instead. Instance fields use ConditionalWeakTable (keyed
                // by instance), static fields use a plain Dictionary. Both are O(1) and survive
                // for the rest of the play-mode session.
                //
                // REMOVED fields: the runtime type still carries the physical field (we can't
                // shrink it). Any code in the hot assembly that used to reference the removed
                // field simply won't compile it anymore, so nothing calls it. Unpatched methods
                // in OTHER files still compile against the original runtime type and continue to
                // work normally.
                var added   = updatedFields.Except(runtimeFields).ToList();
                var removed = runtimeFields.Except(updatedFields).ToList();

                if (added.Count > 0)
                {
                    InstaReloadLogger.Log(InstaReloadLogCategory.Patcher,
                        $"[Patcher] {runtimeType.Name}: {added.Count} new field(s) → HotReloadFieldStore");
                    foreach (var f in added)
                        InstaReloadLogger.LogVerbose(InstaReloadLogCategory.Patcher, $"  + {f}");
                }

                if (removed.Count > 0)
                {
                    InstaReloadLogger.Log(InstaReloadLogCategory.Patcher,
                        $"[Patcher] {runtimeType.Name}: {removed.Count} removed field(s) — runtime copy retained (no domain reload needed)");
                    foreach (var f in removed)
                        InstaReloadLogger.LogVerbose(InstaReloadLogCategory.Patcher, $"  - {f}");
                }
            }

            reason = string.Empty;
            return true;
        }

        private static bool MethodSetsMatch(
            TypeDefinition updatedType,
            Type runtimeType,
            out string reason,
            out List<string> removed)
        {
            var updatedMethods = new HashSet<string>(
                updatedType.Methods.Select(GetMethodKey),
                StringComparer.Ordinal);

            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var runtimeMethods = new HashSet<string>(
                runtimeType.GetMethods(flags).Select(GetMethodKey),
                StringComparer.Ordinal);

            foreach (var ctor in runtimeType.GetConstructors(flags))
            {
                runtimeMethods.Add(GetMethodKey(ctor));
            }

            if (runtimeType.TypeInitializer != null)
            {
                runtimeMethods.Add(GetMethodKey(runtimeType.TypeInitializer));
            }

            // REMOVED methods (exists in runtime but NOT in updated) — ALLOWED, reported.
            //
            // This used to be a hard blocker on the theory that existing call sites would jump
            // into nothing. They can't: a JIT'd method is never removed from memory, and we
            // simply don't patch a method that no longer exists in source. Its original body
            // stays live, so unpatched callers keep working exactly as before.
            //
            // The real consequence is staleness, not a crash — code that still calls the
            // removed method runs the OLD implementation until the next domain reload. That is
            // strictly better than the previous behaviour, which threw away the entire edit.
            //
            // Signature changes land here too: Foo(int) -> Foo(string) reads as remove-old plus
            // add-new. The old overload keeps its body for old callers; the new one is picked up
            // by the existing new-method dispatcher path.
            removed = runtimeMethods.Except(updatedMethods).ToList();

            // NEW methods (exists in updated but NOT in runtime) - ALLOWED!
            // We'll add these dynamically at runtime

            reason = string.Empty;
            return true;
        }

        private static bool IsMethodBodySupported(
            MethodDefinition method,
            IReadOnlyDictionary<string, FieldInfo> runtimeFields,
            out string reason)
        {
            // Async state machines clone to INVALID IL and crash the Editor. Observed 2026-08-05:
            //   <RunAsync>d__18::MoveNext: Invalid IL code ... IL_0047: call 0x00000011
            // a raw, unremapped metadata token. The patch still reported success, installed the
            // broken method, and the next dispatch through Update recursed until
            // StackOverflowException killed the Mono runtime — force-quit, unsaved work lost.
            //
            // Refuse instead. The async method keeps its previous body, exactly like an
            // already-running coroutine does, which is a limitation rather than a crash.
            //
            // Deliberately keyed on IAsyncStateMachine, NOT on the MoveNext name: ITERATOR state
            // machines also have MoveNext and they clone correctly (coroutines, including
            // already-running ones, are verified working). Only the async ones are broken.
            // The OUTER async method must be refused too, not just the state machine's own methods.
            // Our slow path uses Release emit, which emits an async state machine as a STRUCT while
            // Unity's runtime build has it as a CLASS - logged as "base class changed
            // (System.Object -> System.ValueType)" plus a phantom "removed" .ctor. Patching the
            // outer method leaves its IL manipulating a type that disagrees with the runtime one,
            // which ends in a StackOverflowException that kills the Editor.
            if (HasAsyncStateMachineAttribute(method))
            {
                reason =
                    $"async method ({method.DeclaringType.Name}.{method.Name}) cannot be patched yet - " +
                    "its state machine is emitted differently by our compile than by Unity's, so it " +
                    "keeps its previous body until you exit Play Mode";
                return false;
            }

            if (IsAsyncStateMachine(method.DeclaringType))
            {
                reason =
                    $"async state machine ({method.DeclaringType.Name}.{method.Name}) cannot be patched yet - " +
                    "the async method keeps its previous body until you exit Play Mode";
                return false;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (!IsOperandSupported(instruction.Operand))
                {
                    reason = $"Unsupported operand in {method.Name}.";
                    return false;
                }
            }

            if (!IsFieldRewriteSupported(method, runtimeFields, out reason))
            {
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// True for a compiler-generated async state machine (the &lt;Method&gt;d__N type behind
        /// async/await). Iterator state machines implement IEnumerator instead and are NOT matched,
        /// because those clone correctly.
        /// </summary>
        /// <summary>
        /// True for a method the compiler rewrote into an async state machine. Detected via
        /// AsyncStateMachineAttribute, which the compiler puts on the OUTER method.
        /// </summary>
        private static bool HasAsyncStateMachineAttribute(MethodDefinition method)
        {
            if (method == null || !method.HasCustomAttributes)
            {
                return false;
            }

            foreach (var attribute in method.CustomAttributes)
            {
                if (string.Equals(
                        attribute.AttributeType.FullName,
                        "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAsyncStateMachine(TypeDefinition type)
        {
            if (type == null || !type.HasInterfaces)
            {
                return false;
            }

            foreach (var implementation in type.Interfaces)
            {
                if (string.Equals(
                        implementation.InterfaceType.FullName,
                        "System.Runtime.CompilerServices.IAsyncStateMachine",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsFieldRewriteSupported(
            MethodDefinition method,
            IReadOnlyDictionary<string, FieldInfo> runtimeFields,
            out string reason)
        {
            reason = string.Empty;
            if (runtimeFields == null || runtimeFields.Count == 0)
            {
                return true;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (!(instruction.Operand is FieldReference fieldReference))
                {
                    continue;
                }

                var fieldKey = GetFieldLookupKey(fieldReference);
                if (instruction.OpCode == CecilOpCodes.Ldsfld || instruction.OpCode == CecilOpCodes.Stsfld)
                {
                    fieldKey = OverrideFieldKeyStatic(fieldKey, isStatic: true);
                }
                else if (instruction.OpCode == CecilOpCodes.Ldfld || instruction.OpCode == CecilOpCodes.Stfld)
                {
                    fieldKey = OverrideFieldKeyStatic(fieldKey, isStatic: false);
                }
                if (runtimeFields.ContainsKey(fieldKey))
                {
                    continue;
                }

                if (instruction.OpCode == CecilOpCodes.Ldflda ||
                    instruction.OpCode == CecilOpCodes.Ldsflda)
                {
                    reason = $"Missing field address access not supported: {fieldKey}.";
                    return false;
                }
            }

            return true;
        }

        private static bool IsOperandSupported(object operand)
        {
            if (operand == null)
            {
                return true;
            }

            return operand is Instruction ||
                   operand is Instruction[] ||
                   operand is ParameterDefinition ||
                   operand is VariableDefinition ||
                   operand is MethodReference ||
                   operand is FieldReference ||
                   operand is TypeReference ||
                   operand is sbyte ||
                   operand is byte ||
                   operand is int ||
                   operand is long ||
                   operand is float ||
                   operand is double ||
                   operand is string;
        }

        private static Dictionary<string, int> BuildMethodIdMap(ModuleDefinition module)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var method in GetPatchableMethods(module))
            {
                var key = GetMethodKey(method);
                if (!map.ContainsKey(key))
                {
                    map[key] = GetMethodId(key);
                }
            }

            return map;
        }

        private static Dictionary<int, MethodBase> BuildRuntimeMethodTokenMap(Assembly runtimeAssembly)
        {
            var map = new Dictionary<int, MethodBase>();
            foreach (var type in runtimeAssembly.GetTypes())
            {
                var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(flags))
                {
                    map[method.MetadataToken] = method;
                }

                foreach (var ctor in type.GetConstructors(flags))
                {
                    map[ctor.MetadataToken] = ctor;
                }

                if (type.TypeInitializer != null)
                {
                    map[type.TypeInitializer.MetadataToken] = type.TypeInitializer;
                }
            }

            return map;
        }

        private static MethodBase ResolveRuntimeMethod(
            MethodDefinition patchMethod,
            string methodKey,
            IReadOnlyDictionary<string, MethodBase> runtimeMethods,
            IReadOnlyDictionary<int, MethodBase> runtimeMethodTokens,
            PatchReplayContext replayContext,
            bool useTokenReplay)
        {
            if (useTokenReplay && patchMethod != null)
            {
                var patchToken = patchMethod.MetadataToken.ToInt32();
                if (patchToken != 0 && replayContext != null &&
                    replayContext.TryGetRuntimeToken(patchToken, out var runtimeToken))
                {
                    if (runtimeMethodTokens.TryGetValue(runtimeToken, out var runtimeMethod))
                    {
                        return runtimeMethod;
                    }
                }
            }

            runtimeMethods.TryGetValue(methodKey, out var resolved);
            return resolved;
        }

        private static void TryTrackTokenPair(
            IDictionary<int, MethodTokenPair> tokenPairs,
            MethodDefinition patchMethod,
            MethodBase runtimeMethod,
            string methodKey)
        {
            if (patchMethod == null || runtimeMethod == null || tokenPairs == null)
            {
                return;
            }

            var patchToken = patchMethod.MetadataToken.ToInt32();
            if (patchToken == 0 || tokenPairs.ContainsKey(patchToken))
            {
                return;
            }

            try
            {
                var runtimeToken = runtimeMethod.MetadataToken;
                tokenPairs[patchToken] = new MethodTokenPair(patchToken, runtimeToken, methodKey);
            }
            catch
            {
                // Ignore token tracking failures.
            }
        }

        private static Dictionary<string, FieldInfo> BuildRuntimeFieldMap(Assembly runtimeAssembly)
        {
            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (var type in runtimeAssembly.GetTypes())
            {
                foreach (var field in type.GetFields(flags))
                {
                    map[GetFieldLookupKey(field)] = field;
                }
            }

            // Hot types (brand-new classes added during play mode) must also have their
            // fields in this map so that field accesses pass through as direct CLR reads/writes
            // instead of being redirected to HotReloadFieldStore.
            //
            // WHY: when 'new PlayerStats()' runs, the CLR calls the hot assembly's .ctor
            // natively (newobj is not rewritten — only call/callvirt are). That .ctor sets
            // CLR field slots directly via stfld. If we didn't include hot type fields here,
            // TryRewriteFieldInstruction would route every subsequent 'ldfld health' to
            // FieldStore.GetInstanceField — which has nothing — returning default(T) instead
            // of the value the constructor just wrote. The two sides would be out of sync.
            //
            // Existing types with NEW fields are NOT in HotTypeRegistry and are NOT affected
            // by this loop — their new fields still correctly get the FieldStore path.
            foreach (var hotType in HotTypeRegistry.GetAll())
            {
                foreach (var field in hotType.GetFields(flags))
                {
                    map[GetFieldLookupKey(field)] = field;
                }
            }

            return map;
        }

        private static HashSet<string> BuildDispatchKeySet(
            ModuleDefinition module,
            IReadOnlyDictionary<string, MethodBase> runtimeMethods)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in GetPatchableMethods(module))
            {
                var key = GetMethodKey(method);
                if (IsUnityEntryPoint(method) || !runtimeMethods.ContainsKey(key))
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static Dictionary<string, EntryPointSignature[]> BuildFallbackEntryPointMap()
        {
            return FallbackEntryPoints
                .GroupBy(entry => entry.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        }

        private static bool TryGetFallbackEntryPointKind(MethodDefinition method, out EntryPointKind kind)
        {
            kind = default;

            if (method == null)
            {
                return false;
            }

            if (method.IsStatic)
            {
                return false;
            }

            if (method.ReturnType.MetadataType != MetadataType.Void)
            {
                return false;
            }

            if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters)
            {
                return false;
            }

            if (!FallbackEntryPointsByName.TryGetValue(method.Name, out var signatures))
            {
                return false;
            }

            var parameterCount = method.Parameters.Count;
            for (int i = 0; i < signatures.Length; i++)
            {
                var signature = signatures[i];
                if (signature.ParameterTypes.Length != parameterCount)
                {
                    continue;
                }

                bool matches = true;
                for (int p = 0; p < parameterCount; p++)
                {
                    var parameterType = method.Parameters[p].ParameterType;
                    if (parameterType is ByReferenceType || parameterType is PointerType)
                    {
                        matches = false;
                        break;
                    }

                    var paramTypeName = NormalizeTypeName(GetTypeName(parameterType));
                    if (!string.Equals(paramTypeName, signature.ParameterTypes[p], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    kind = signature.Kind;
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnityEntryPoint(MethodDefinition method)
        {
            if (!UnityEntryPointNames.Contains(method.Name))
            {
                return false;
            }

            if (method.IsStatic)
            {
                return false;
            }

            if (method.Parameters.Count != 0)
            {
                return false;
            }

            return method.ReturnType.MetadataType == MetadataType.Void;
        }

        private static bool InheritsHotReloadBehaviour(TypeDefinition type)
        {
            var current = type;
            while (current != null)
            {
                var baseType = current.BaseType;
                if (baseType == null)
                {
                    return false;
                }

                var baseName = NormalizeTypeName(baseType.FullName);
                if (string.Equals(baseName, HotReloadBehaviourTypeName, StringComparison.Ordinal))
                {
                    return true;
                }

                try
                {
                    current = baseType.Resolve();
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryRegisterMissingEntryPoint(
            MethodDefinition method,
            Assembly runtimeAssembly,
            EntryPointKind entryPointKind,
            int methodId)
        {
            if (method == null || runtimeAssembly == null)
            {
                return false;
            }

            var runtimeType = ResolveRuntimeType(method.DeclaringType, runtimeAssembly);
            if (runtimeType == null)
            {
                return false;
            }

            return HotReloadEntryPointManager.TryRegisterMissingEntryPoint(runtimeType, entryPointKind, methodId);
        }

        private bool EnsureTrampoline(MethodBase runtimeMethod, string methodKey, MethodInfo dispatcherInvokeMethod, int methodId)
        {
            if (_trampolineHooks.ContainsKey(methodKey))
            {
                return false;
            }

            var trampolineMethod = CreateTrampolineMethod(runtimeMethod, dispatcherInvokeMethod, methodId);
            if (trampolineMethod == null)
            {
                InstaReloadLogger.LogWarning($"[Patcher] Failed to build trampoline for {methodKey}");
                return false;
            }

            var hook = new Hook(runtimeMethod, trampolineMethod);
            hook.Apply();
            _trampolineHooks[methodKey] = new TrampolineHook(hook, trampolineMethod);
            InstaReloadLogger.LogVerbose($"[Patcher] Trampoline installed {methodKey} -> {methodId}");
            return true;
        }

        private static MethodInfo CreateTrampolineMethod(MethodBase runtimeMethod, MethodInfo dispatcherInvokeMethod, int methodId)
        {
            if (!(runtimeMethod is MethodInfo runtimeInfo))
            {
                return null;
            }

            if (runtimeInfo.IsStatic || runtimeInfo.ReturnType != typeof(void))
            {
                return null;
            }

            if (runtimeInfo.GetParameters().Length != 0)
            {
                return null;
            }

            if (dispatcherInvokeMethod == null)
            {
                return null;
            }

            var declaringType = runtimeInfo.DeclaringType ?? typeof(object);
            var dynamicMethod = new EmitDynamicMethod(
                $"InstaReloadTrampoline_{declaringType.Name}_{runtimeInfo.Name}",
                typeof(void),
                new[] { declaringType },
                typeof(InstaReloadPatcher),
                true);

            var il = dynamicMethod.GetILGenerator();
            il.Emit(EmitOpCodes.Ldarg_0);
            if (declaringType.IsValueType)
            {
                il.Emit(EmitOpCodes.Box, declaringType);
            }

            il.Emit(EmitOpCodes.Ldc_I4, methodId);
            il.Emit(EmitOpCodes.Ldnull);
            il.Emit(EmitOpCodes.Call, dispatcherInvokeMethod);
            il.Emit(EmitOpCodes.Pop);
            il.Emit(EmitOpCodes.Ret);

            return dynamicMethod;
        }

        private static bool TryRegisterDispatcher(
            MethodDefinition method,
            Assembly runtimeAssembly,
            IReadOnlyDictionary<string, MethodBase> runtimeMethods,
            IReadOnlyDictionary<string, FieldInfo> runtimeFields,
            IReadOnlyDictionary<string, int> methodIds,
            ISet<string> dispatchKeys,
            MethodInfo dispatcherInvokeMethod,
            out string error)
        {
            error = null;

            var methodKey = GetMethodKey(method);
            if (!methodIds.TryGetValue(methodKey, out var methodId))
            {
                error = "Method id missing";
                return false;
            }

            var dynamicMethod = CreateDynamicMethod(method, runtimeAssembly, runtimeMethods, runtimeFields, methodIds, dispatchKeys, dispatcherInvokeMethod);
            if (dynamicMethod == null)
            {
                error = "Failed to build dynamic method";
                return false;
            }

            var invoker = CreateInvoker(method, dynamicMethod);
            if (invoker == null)
            {
                error = "Failed to build invoker";
                return false;
            }

            HotReloadDispatcher.Register(methodId, invoker);
            InstaReloadLogger.LogVerbose($"[Patcher] Dispatch registered {methodKey} -> {methodId}");
            return true;
        }

        private static MethodInfo CreateDynamicMethod(
            MethodDefinition updatedMethod,
            Assembly runtimeAssembly,
            IReadOnlyDictionary<string, MethodBase> runtimeMethods,
            IReadOnlyDictionary<string, FieldInfo> runtimeFields,
            IReadOnlyDictionary<string, int> methodIds,
            ISet<string> dispatchKeys,
            MethodInfo dispatcherInvokeMethod)
        {
            if (updatedMethod == null || updatedMethod.Body == null)
            {
                return null;
            }

            if (updatedMethod.HasGenericParameters)
            {
                return null;
            }

            var declaringRuntimeType = ResolveRuntimeType(updatedMethod.DeclaringType, runtimeAssembly);
            if (!updatedMethod.IsStatic && declaringRuntimeType == null)
            {
                return null;
            }

            var parameterTypes = new List<Type>();
            if (!updatedMethod.IsStatic)
            {
                parameterTypes.Add(declaringRuntimeType);
            }

            foreach (var parameter in updatedMethod.Parameters)
            {
                var runtimeParamType = ResolveRuntimeType(parameter.ParameterType, runtimeAssembly);
                if (runtimeParamType == null)
                {
                    return null;
                }

                parameterTypes.Add(runtimeParamType);
            }

            var returnType = ResolveRuntimeType(updatedMethod.ReturnType, runtimeAssembly) ?? typeof(void);

            var dmd = new DynamicMethodDefinition(
                $"{updatedMethod.DeclaringType.Name}_{updatedMethod.Name}_InstaReload",
                returnType,
                parameterTypes.ToArray());

            var context = new MethodRewriteContext(
                dmd.Module,
                runtimeAssembly,
                runtimeMethods,
                runtimeFields,
                methodIds,
                dispatchKeys,
                dispatcherInvokeMethod,
                targetIncludesThis: !updatedMethod.IsStatic);

            try
            {
                CloneMethodBody(dmd.Definition, updatedMethod, context);
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogWarning($"[Patcher] Failed to clone method body for dispatcher: {ex.Message}");
                return null;
            }

            return dmd.Generate();
        }

        private static Func<object, object[], object> CreateInvoker(MethodDefinition method, MethodInfo target)
        {
            if (target == null)
            {
                return null;
            }

            var dynamicMethod = new EmitDynamicMethod(
                $"InstaReloadInvoker_{target.Name}",
                typeof(object),
                new[] { typeof(object), typeof(object[]) },
                typeof(InstaReloadPatcher),
                true);

            var il = dynamicMethod.GetILGenerator();
            var targetParameters = target.GetParameters();
            int paramOffset = 0;

            if (!method.IsStatic)
            {
                var instanceType = targetParameters[0].ParameterType;
                il.Emit(EmitOpCodes.Ldarg_0);
                il.Emit(EmitOpCodes.Castclass, instanceType);
                paramOffset = 1;
            }

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                il.Emit(EmitOpCodes.Ldarg_1);
                il.Emit(EmitOpCodes.Ldc_I4, i);
                il.Emit(EmitOpCodes.Ldelem_Ref);

                var paramType = targetParameters[i + paramOffset].ParameterType;
                if (paramType.IsValueType)
                {
                    il.Emit(EmitOpCodes.Unbox_Any, paramType);
                }
                else
                {
                    il.Emit(EmitOpCodes.Castclass, paramType);
                }
            }

            il.Emit(EmitOpCodes.Call, target);

            if (method.ReturnType.MetadataType == MetadataType.Void)
            {
                il.Emit(EmitOpCodes.Ldnull);
            }
            else if (target.ReturnType.IsValueType)
            {
                il.Emit(EmitOpCodes.Box, target.ReturnType);
            }

            il.Emit(EmitOpCodes.Ret);

            return (Func<object, object[], object>)dynamicMethod.CreateDelegate(typeof(Func<object, object[], object>));
        }

        private static void ReplaceMethodBody(
            ILContext context,
            MethodDefinition updatedMethod,
            Assembly runtimeAssembly,
            IReadOnlyDictionary<string, MethodBase> runtimeMethods,
            IReadOnlyDictionary<string, FieldInfo> runtimeFields,
            IReadOnlyDictionary<string, int> methodIds,
            ISet<string> dispatchKeys,
            MethodInfo dispatcherInvokeMethod)
        {
            var rewriteContext = new MethodRewriteContext(
                context.Method.Module,
                runtimeAssembly,
                runtimeMethods,
                runtimeFields,
                methodIds,
                dispatchKeys,
                dispatcherInvokeMethod,
                // DERIVED, not assumed. MonoMod sometimes hands back a STATIC clone with `this` as
                // an explicit first parameter and sometimes an instance one. Hardcoding false
                // shifted EVERY parameter reference by one in the static case, so a method reading
                // its 4th argument actually read its 3rd - silently, with no crash and no warning.
                // Methods that merely null-checked a parameter looked correct, which is how it went
                // unnoticed. Comparing counts self-corrects for both shapes.
                targetIncludesThis: context.Method.Parameters.Count > updatedMethod.Parameters.Count);

            CloneMethodBody(context.Method, updatedMethod, rewriteContext);
        }

        private sealed class MethodRewriteContext
        {
            public MethodRewriteContext(
                ModuleDefinition targetModule,
                Assembly runtimeAssembly,
                IReadOnlyDictionary<string, MethodBase> runtimeMethods,
                IReadOnlyDictionary<string, FieldInfo> runtimeFields,
                IReadOnlyDictionary<string, int> methodIds,
                ISet<string> dispatchKeys,
                MethodInfo dispatcherInvokeMethod,
                bool targetIncludesThis)
            {
                TargetModule = targetModule;
                RuntimeAssembly = runtimeAssembly;
                RuntimeMethods = runtimeMethods;
                RuntimeFields = runtimeFields;
                MethodIds = methodIds;
                DispatchKeys = dispatchKeys;
                TargetIncludesThis = targetIncludesThis;
                DispatcherInvoke = dispatcherInvokeMethod != null
                    ? targetModule.ImportReference(dispatcherInvokeMethod)
                    : null;
                TypeGetTypeFromHandle = ImportMethodReference(
                    targetModule,
                    typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }));
                FieldStoreGetInstance = ImportMethodReference(
                    targetModule,
                    typeof(HotReloadFieldStore).GetMethod(
                        "GetInstanceField",
                        new[] { typeof(object), typeof(string), typeof(Type) }));
                FieldStoreSetInstance = ImportMethodReference(
                    targetModule,
                    typeof(HotReloadFieldStore).GetMethod(
                        "SetInstanceField",
                        new[] { typeof(object), typeof(string), typeof(object) }));
                FieldStoreGetStatic = ImportMethodReference(
                    targetModule,
                    typeof(HotReloadFieldStore).GetMethod(
                        "GetStaticField",
                        new[] { typeof(string), typeof(Type) }));
                FieldStoreSetStatic = ImportMethodReference(
                    targetModule,
                    typeof(HotReloadFieldStore).GetMethod(
                        "SetStaticField",
                        new[] { typeof(string), typeof(object) }));
            }

            public ModuleDefinition TargetModule { get; }
            public Assembly RuntimeAssembly { get; }
            public IReadOnlyDictionary<string, MethodBase> RuntimeMethods { get; }
            public IReadOnlyDictionary<string, FieldInfo> RuntimeFields { get; }
            public IReadOnlyDictionary<string, int> MethodIds { get; }
            public ISet<string> DispatchKeys { get; }
            public bool TargetIncludesThis { get; }
            public MethodReference DispatcherInvoke { get; }
            public MethodReference TypeGetTypeFromHandle { get; }
            public MethodReference FieldStoreGetInstance { get; }
            public MethodReference FieldStoreSetInstance { get; }
            public MethodReference FieldStoreGetStatic { get; }
            public MethodReference FieldStoreSetStatic { get; }

            public bool HasFieldStore =>
                TypeGetTypeFromHandle != null &&
                FieldStoreGetInstance != null &&
                FieldStoreSetInstance != null &&
                FieldStoreGetStatic != null &&
                FieldStoreSetStatic != null;
        }

        private static void CloneMethodBody(
            MethodDefinition targetMethod,
            MethodDefinition updatedMethod,
            MethodRewriteContext context)
        {
            var body = targetMethod.Body;
            body.Variables.Clear();
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();

            body.InitLocals = true;
            body.MaxStackSize = Math.Max(updatedMethod.Body.MaxStackSize, 16);

            var module = context.TargetModule;
            foreach (var variable in updatedMethod.Body.Variables)
            {
                body.Variables.Add(new VariableDefinition(ImportTypeReference(context, variable.VariableType)));
            }

            var il = body.GetILProcessor();
            var instructionMap = new Dictionary<Instruction, Instruction>(updatedMethod.Body.Instructions.Count);

            // Count field rewrites so we can emit a single summary log per method rather
            // than one log line per opcode. Verbose mode shows each rewritten field name.
            int fieldStoreRewrites = 0;

            foreach (var instruction in updatedMethod.Body.Instructions)
            {
                var fieldRewrite = TryRewriteFieldInstruction(
                    instruction,
                    targetMethod,
                    context,
                    out var fieldEmitted,
                    out var fieldError);
                if (fieldRewrite == FieldRewriteResult.Rewritten)
                {
                    fieldStoreRewrites++;
                    instructionMap[instruction] = fieldEmitted[0];
                    foreach (var emittedInstruction in fieldEmitted)
                    {
                        il.Append(emittedInstruction);
                    }
                    continue;
                }
                if (fieldRewrite == FieldRewriteResult.Unsupported)
                {
                    throw new NotSupportedException(fieldError ?? "Unsupported field rewrite.");
                }

                if (TryRewriteCallInstruction(instruction, targetMethod, context, out var emitted))
                {
                    instructionMap[instruction] = emitted[0];
                    foreach (var emittedInstruction in emitted)
                    {
                        il.Append(emittedInstruction);
                    }
                    continue;
                }

                var cloned = CloneInstruction(instruction, targetMethod, context);
                instructionMap[instruction] = cloned;
                il.Append(cloned);
            }

            // Log a summary after cloning so the developer can see that hot-field-store
            // rewrites happened. One line per method keeps the console readable; verbose
            // mode shows the full key list (emitted by FieldSetsMatch).
            if (fieldStoreRewrites > 0)
            {
                var typeName  = targetMethod.DeclaringType?.Name ?? "?";
                var methodName = targetMethod.Name;
                InstaReloadLogger.LogVerbose(InstaReloadLogCategory.Patcher,
                    $"[Patcher] {typeName}.{methodName}: {fieldStoreRewrites} hot-field access(es) routed via HotReloadFieldStore");
            }

            foreach (var instruction in updatedMethod.Body.Instructions)
            {
                if (instruction.Operand is Instruction target)
                {
                    instructionMap[instruction].Operand = instructionMap[target];
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    instructionMap[instruction].Operand = targets.Select(t => instructionMap[t]).ToArray();
                }
            }

            foreach (var handler in updatedMethod.Body.ExceptionHandlers)
            {
                var newHandler = new CecilExceptionHandler(handler.HandlerType)
                {
                    CatchType = handler.CatchType != null ? module.ImportReference(handler.CatchType) : null,
                    TryStart = handler.TryStart != null ? instructionMap[handler.TryStart] : null,
                    TryEnd = handler.TryEnd != null ? instructionMap[handler.TryEnd] : null,
                    HandlerStart = handler.HandlerStart != null ? instructionMap[handler.HandlerStart] : null,
                    HandlerEnd = handler.HandlerEnd != null ? instructionMap[handler.HandlerEnd] : null,
                    FilterStart = handler.FilterStart != null ? instructionMap[handler.FilterStart] : null
                };
                body.ExceptionHandlers.Add(newHandler);
            }

            body.OptimizeMacros();
        }

        private enum FieldRewriteResult
        {
            None,       // field exists in runtime assembly — leave the original opcode as-is
            Rewritten,  // field is new (not in runtime) — rewrote to HotReloadFieldStore calls
            Unsupported // new field but rewrite is impossible (e.g. ldflda address-of)
        }

        // Transparently rewrites field access IL for fields that don't exist in the runtime
        // assembly (new instance or static fields added during hot reload).
        //
        // WHY THIS IS NEEDED:
        //   The runtime type's physical layout is fixed the moment the CLR JITs it. We
        //   cannot add memory slots to an existing type at runtime. Any `ldfld`/`stfld`
        //   that references a field offset the CLR doesn't know about would crash.
        //
        // HOW IT WORKS:
        //   We intercept at IL level, not at runtime. When CloneMethodBody encounters a
        //   field access opcode (ldfld / stfld / ldsfld / stsfld) whose key is ABSENT from
        //   context.RuntimeFields (the map of fields the runtime type actually has), we
        //   replace that one opcode with a call sequence into HotReloadFieldStore:
        //
        //     ldfld  T Foo::_x      →  call GetInstanceField(instance, key, typeof(T))
        //     stfld  T Foo::_x      →  call SetInstanceField(instance, key, boxed_value)
        //     ldsfld T Foo::_y      →  call GetStaticField(key, typeof(T))
        //     stsfld T Foo::_y      →  call SetStaticField(key, boxed_value)
        //
        //   Instance fields use ConditionalWeakTable (data lives as long as the instance).
        //   Static fields use a plain Dictionary (data lives for the entire play-mode session).
        //   Both storage strategies are transparent to developer code — they write normal C#
        //   and the patcher handles the indirection invisibly.
        //
        // FIELD KEY FORMAT:
        //   "DeclaringType.FullName::FieldName:FieldTypeName:instance|static"
        //   Including the declaring type avoids collisions when two different types have
        //   fields with the same name. Including the field type prevents collisions between
        //   fields that share a name but have different types across hot-reload cycles.
        //
        // LIMITATION:
        //   ldflda / ldsflda (take address of field) cannot be rewritten — field-store
        //   values have no stable memory address. These are blocked and skip the method.
        private static FieldRewriteResult TryRewriteFieldInstruction(
            Instruction instruction,
            MethodDefinition targetMethod,
            MethodRewriteContext context,
            out List<Instruction> emitted,
            out string error)
        {
            emitted = null;
            error = null;

            if (!(instruction.Operand is FieldReference fieldReference))
            {
                return FieldRewriteResult.None;
            }

            if (instruction.OpCode != CecilOpCodes.Ldfld &&
                instruction.OpCode != CecilOpCodes.Stfld &&
                instruction.OpCode != CecilOpCodes.Ldsfld &&
                instruction.OpCode != CecilOpCodes.Stsfld &&
                instruction.OpCode != CecilOpCodes.Ldflda &&
                instruction.OpCode != CecilOpCodes.Ldsflda)
            {
                return FieldRewriteResult.None;
            }

            var fieldKey = GetFieldLookupKey(fieldReference);
            if (instruction.OpCode == CecilOpCodes.Ldsfld || instruction.OpCode == CecilOpCodes.Stsfld)
            {
                fieldKey = OverrideFieldKeyStatic(fieldKey, isStatic: true);
            }
            else if (instruction.OpCode == CecilOpCodes.Ldfld || instruction.OpCode == CecilOpCodes.Stfld)
            {
                fieldKey = OverrideFieldKeyStatic(fieldKey, isStatic: false);
            }
            if (context.RuntimeFields != null &&
                context.RuntimeFields.TryGetValue(fieldKey, out _))
            {
                return FieldRewriteResult.None;
            }

            if (!context.HasFieldStore)
            {
                error = $"Field store unavailable for {fieldKey}.";
                return FieldRewriteResult.Unsupported;
            }

            if (instruction.OpCode == CecilOpCodes.Ldflda ||
                instruction.OpCode == CecilOpCodes.Ldsflda)
            {
                error = $"Missing field address access not supported: {fieldKey}.";
                return FieldRewriteResult.Unsupported;
            }

            var fieldType = ImportTypeReference(context, fieldReference.FieldType);
            var objectType = context.TargetModule.ImportReference(typeof(object));
            var newInstructions = new List<Instruction>();

            if (instruction.OpCode == CecilOpCodes.Ldfld)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldstr, fieldKey));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldtoken, fieldType));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.TypeGetTypeFromHandle));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.FieldStoreGetInstance));

                if (fieldReference.FieldType.IsValueType)
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Unbox_Any, fieldType));
                }
                else
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Castclass, fieldType));
                }

                emitted = newInstructions;
                return FieldRewriteResult.Rewritten;
            }

            if (instruction.OpCode == CecilOpCodes.Stfld)
            {
                var instanceLocal = new VariableDefinition(objectType);
                var valueLocal = new VariableDefinition(objectType);
                targetMethod.Body.Variables.Add(valueLocal);
                targetMethod.Body.Variables.Add(instanceLocal);

                if (fieldReference.FieldType.IsValueType)
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Box, fieldType));
                }
                newInstructions.Add(Instruction.Create(CecilOpCodes.Stloc, valueLocal));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Stloc, instanceLocal));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldloc, instanceLocal));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldstr, fieldKey));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldloc, valueLocal));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.FieldStoreSetInstance));

                emitted = newInstructions;
                return FieldRewriteResult.Rewritten;
            }

            if (instruction.OpCode == CecilOpCodes.Ldsfld)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldstr, fieldKey));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldtoken, fieldType));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.TypeGetTypeFromHandle));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.FieldStoreGetStatic));

                if (fieldReference.FieldType.IsValueType)
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Unbox_Any, fieldType));
                }
                else
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Castclass, fieldType));
                }

                emitted = newInstructions;
                return FieldRewriteResult.Rewritten;
            }

            if (instruction.OpCode == CecilOpCodes.Stsfld)
            {
                var valueLocal = new VariableDefinition(objectType);
                targetMethod.Body.Variables.Add(valueLocal);

                if (fieldReference.FieldType.IsValueType)
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Box, fieldType));
                }
                newInstructions.Add(Instruction.Create(CecilOpCodes.Stloc, valueLocal));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldstr, fieldKey));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldloc, valueLocal));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.FieldStoreSetStatic));

                emitted = newInstructions;
                return FieldRewriteResult.Rewritten;
            }

            return FieldRewriteResult.None;
        }

        private static bool TryRewriteCallInstruction(
            Instruction instruction,
            MethodDefinition targetMethod,
            MethodRewriteContext context,
            out List<Instruction> emitted)
        {
            emitted = null;

            if (instruction.OpCode != CecilOpCodes.Call && instruction.OpCode != CecilOpCodes.Callvirt)
            {
                return false;
            }

            if (!(instruction.Operand is MethodReference methodReference))
            {
                return false;
            }

            var methodKey = GetMethodKey(methodReference);
            if (!context.DispatchKeys.Contains(methodKey))
            {
                return false;
            }

            if (context.DispatcherInvoke == null || !context.MethodIds.TryGetValue(methodKey, out var methodId))
            {
                return false;
            }

            foreach (var param in methodReference.Parameters)
            {
                if (param.ParameterType.IsByReference || param.ParameterType is PointerType)
                {
                    return false;
                }
            }

            if (methodReference.HasThis && methodReference.DeclaringType.IsValueType)
            {
                return false;
            }

            var body = targetMethod.Body;
            var objectType = context.TargetModule.ImportReference(typeof(object));

            VariableDefinition instanceLocal = null;
            if (methodReference.HasThis)
            {
                instanceLocal = new VariableDefinition(objectType);
                body.Variables.Add(instanceLocal);
            }

            var parameterLocals = new VariableDefinition[methodReference.Parameters.Count];
            for (int i = 0; i < methodReference.Parameters.Count; i++)
            {
                var local = new VariableDefinition(objectType);
                body.Variables.Add(local);
                parameterLocals[i] = local;
            }

            var newInstructions = new List<Instruction>();

            for (int i = methodReference.Parameters.Count - 1; i >= 0; i--)
            {
                var param = methodReference.Parameters[i];
                if (param.ParameterType.IsValueType)
                {
                    newInstructions.Add(Instruction.Create(CecilOpCodes.Box, ImportTypeReference(context, param.ParameterType)));
                }

                newInstructions.Add(Instruction.Create(CecilOpCodes.Stloc, parameterLocals[i]));
            }

            if (methodReference.HasThis)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Stloc, instanceLocal));
            }

            if (methodReference.HasThis)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldloc, instanceLocal));
            }
            else
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldnull));
            }

            newInstructions.Add(Instruction.Create(CecilOpCodes.Ldc_I4, methodId));
            newInstructions.Add(Instruction.Create(CecilOpCodes.Ldc_I4, methodReference.Parameters.Count));
            newInstructions.Add(Instruction.Create(CecilOpCodes.Newarr, objectType));

            for (int i = 0; i < methodReference.Parameters.Count; i++)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Dup));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldc_I4, i));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Ldloc, parameterLocals[i]));
                newInstructions.Add(Instruction.Create(CecilOpCodes.Stelem_Ref));
            }

            newInstructions.Add(Instruction.Create(CecilOpCodes.Call, context.DispatcherInvoke));

            if (methodReference.ReturnType.MetadataType == MetadataType.Void)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Pop));
            }
            else if (methodReference.ReturnType.IsValueType)
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Unbox_Any, ImportTypeReference(context, methodReference.ReturnType)));
            }
            else
            {
                newInstructions.Add(Instruction.Create(CecilOpCodes.Castclass, ImportTypeReference(context, methodReference.ReturnType)));
            }

            emitted = newInstructions;
            return true;
        }

        private static Instruction CloneInstruction(Instruction source, MethodDefinition targetMethod, MethodRewriteContext context)
        {
            var operand = source.Operand;
            if (operand == null)
            {
                return Instruction.Create(source.OpCode);
            }

            if (operand is Instruction)
            {
                return Instruction.Create(source.OpCode, Instruction.Create(CecilOpCodes.Nop));
            }

            if (operand is Instruction[] targets)
            {
                return Instruction.Create(source.OpCode, new Instruction[targets.Length]);
            }

            if (operand is ParameterDefinition parameter)
            {
                var index = parameter.Index + (context.TargetIncludesThis ? 1 : 0);
                return Instruction.Create(source.OpCode, targetMethod.Parameters[index]);
            }

            if (operand is VariableDefinition variable)
            {
                return Instruction.Create(source.OpCode, targetMethod.Body.Variables[variable.Index]);
            }

            var module = context.TargetModule;
            if (operand is MethodReference methodReference)
            {
                var methodKey = GetMethodKey(methodReference);
                if (context.RuntimeMethods.TryGetValue(methodKey, out var runtimeMethod))
                {
                    return Instruction.Create(source.OpCode, module.ImportReference(runtimeMethod));
                }

                return Instruction.Create(source.OpCode, module.ImportReference(methodReference));
            }

            if (operand is FieldReference fieldReference)
            {
                if (context.RuntimeFields != null)
                {
                    var fieldKey = GetFieldLookupKey(fieldReference);
                    if (context.RuntimeFields.TryGetValue(fieldKey, out var runtimeField))
                    {
                        return Instruction.Create(source.OpCode, module.ImportReference(runtimeField));
                    }
                }

                return Instruction.Create(source.OpCode, module.ImportReference(fieldReference));
            }

            if (operand is TypeReference typeReference)
            {
                return Instruction.Create(source.OpCode, ImportTypeReference(context, typeReference));
            }

            if (operand is sbyte sbyteValue)
            {
                return Instruction.Create(source.OpCode, sbyteValue);
            }

            if (operand is byte byteValue)
            {
                return Instruction.Create(source.OpCode, byteValue);
            }

            if (operand is int intValue)
            {
                return Instruction.Create(source.OpCode, intValue);
            }

            if (operand is long longValue)
            {
                return Instruction.Create(source.OpCode, longValue);
            }

            if (operand is float floatValue)
            {
                return Instruction.Create(source.OpCode, floatValue);
            }

            if (operand is double doubleValue)
            {
                return Instruction.Create(source.OpCode, doubleValue);
            }

            if (operand is string stringValue)
            {
                return Instruction.Create(source.OpCode, stringValue);
            }

            throw new NotSupportedException($"Unsupported operand type: {operand.GetType().FullName}");
        }

        private static string GetFieldKey(FieldDefinition field)
        {
            return $"{field.Name}:{NormalizeTypeName(field.FieldType.FullName)}:{(field.IsStatic ? "static" : "instance")}";
        }

        private static string GetFieldKey(FieldInfo field)
        {
            return $"{field.Name}:{GetTypeName(field.FieldType)}:{(field.IsStatic ? "static" : "instance")}";
        }

        private static string GetFieldLookupKey(FieldReference field)
        {
            var typeName = NormalizeTypeName(field.DeclaringType.FullName);
            var fieldType = NormalizeTypeName(GetTypeName(field.FieldType));
            bool isStatic = false;

            try
            {
                var definition = field.Resolve();
                if (definition != null)
                {
                    isStatic = definition.IsStatic;
                }
            }
            catch
            {
                // Ignore resolution failures.
            }

            return $"{typeName}::{field.Name}:{fieldType}:{(isStatic ? "static" : "instance")}";
        }

        private static string OverrideFieldKeyStatic(string fieldKey, bool isStatic)
        {
            if (string.IsNullOrEmpty(fieldKey))
            {
                return fieldKey;
            }

            var lastColon = fieldKey.LastIndexOf(':');
            if (lastColon < 0)
            {
                return fieldKey;
            }

            return $"{fieldKey.Substring(0, lastColon + 1)}{(isStatic ? "static" : "instance")}";
        }

        private static string GetFieldLookupKey(FieldInfo field)
        {
            var typeName = field.DeclaringType != null ? NormalizeTypeName(field.DeclaringType.FullName) : string.Empty;
            var fieldType = NormalizeTypeName(GetTypeName(field.FieldType));
            return $"{typeName}::{field.Name}:{fieldType}:{(field.IsStatic ? "static" : "instance")}";
        }

        private static string GetMethodKey(MethodDefinition method)
        {
            var typeName = NormalizeTypeName(method.DeclaringType.FullName);
            var paramTypes = method.Parameters.Select(p => NormalizeTypeName(GetTypeName(p.ParameterType)));
            var returnType = NormalizeTypeName(GetTypeName(method.ReturnType));
            var genericArity = method.HasGenericParameters ? method.GenericParameters.Count : 0;
            return $"{typeName}::{method.Name}`{genericArity}({string.Join(",", paramTypes)})=>{returnType}";
        }

        private static string GetMethodKey(MethodReference method)
        {
            var typeName = NormalizeTypeName(method.DeclaringType.FullName);
            var paramTypes = method.Parameters.Select(p => NormalizeTypeName(GetTypeName(p.ParameterType)));
            var returnType = NormalizeTypeName(GetTypeName(method.ReturnType));
            var genericArity = method.HasGenericParameters ? method.GenericParameters.Count : 0;
            return $"{typeName}::{method.Name}`{genericArity}({string.Join(",", paramTypes)})=>{returnType}";
        }

        private static string GetMethodKey(MethodBase method)
        {
            var typeName = method.DeclaringType != null ? method.DeclaringType.FullName : method.Name;
            var parameters = method.GetParameters().Select(p => GetTypeName(p.ParameterType));
            var returnType = method is MethodInfo mi ? GetTypeName(mi.ReturnType) : "System.Void";
            var genericArity = method.IsGenericMethod ? method.GetGenericArguments().Length : 0;
            return $"{typeName}::{method.Name}`{genericArity}({string.Join(",", parameters)})=>{returnType}";
        }

        private static int GetMethodId(string methodKey)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < methodKey.Length; i++)
                {
                    hash ^= methodKey[i];
                    hash *= 16777619;
                }

                return (int)hash;
            }
        }

        // Resolves a Cecil TypeReference to a System.Type from the live runtime.
        //
        // Resolution order:
        //   1. HotTypeRegistry  — O(1), always returns the latest hot-reloaded version.
        //   2. Runtime assembly — the original compiled project DLL (fast, authoritative).
        //   3. Type.GetType    — system types (mscorlib, System.*).
        //   4. AppDomain scan  — all other loaded assemblies (slow, last resort).
        //
        // WHY HotTypeRegistry IS CHECKED FIRST (not after the runtime assembly):
        //   A new type doesn't exist in the original runtime assembly at all, so checking
        //   there first would always miss and fall through to the AppDomain scan.
        //   The AppDomain scan is O(loaded assemblies × types per assembly) — expensive.
        //   Worse, if the same file was hot-reloaded multiple times, multiple assemblies
        //   with that type name exist in the AppDomain; the scan returns whichever it
        //   encounters first, which may be a stale version.
        //   HotTypeRegistry always holds the latest version (Register() overwrites) and
        //   resolves in O(1). Checking it first avoids both problems.
        private static Type ResolveRuntimeType(TypeReference type, Assembly runtimeAssembly)
        {
            if (type == null)
                return null;

            if (type is GenericParameter)
                return null;

            // Structural types (byref, pointer, array) wrap an element type.
            // Resolve the element first, then re-wrap. We do this before the
            // name-based lookups below because these types have no FullName entry
            // in the registry — only their unwrapped element type would be there.
            if (type is ByReferenceType byReferenceType)
            {
                var elementType = ResolveRuntimeType(byReferenceType.ElementType, runtimeAssembly);
                return elementType != null ? elementType.MakeByRefType() : null;
            }

            if (type is PointerType pointerType)
            {
                var elementType = ResolveRuntimeType(pointerType.ElementType, runtimeAssembly);
                return elementType != null ? elementType.MakePointerType() : null;
            }

            if (type is ArrayType arrayType)
            {
                var elementType = ResolveRuntimeType(arrayType.ElementType, runtimeAssembly);
                return elementType != null ? elementType.MakeArrayType(arrayType.Rank) : null;
            }

            var normalizedName = NormalizeTypeName(type.FullName);

            // 1. HotTypeRegistry — fastest path for developer-added types.
            if (HotTypeRegistry.TryGet(normalizedName, out var hotType))
                return hotType;

            // 2. Original runtime assembly — fastest path for pre-existing project types.
            if (runtimeAssembly != null)
            {
                var runtimeType = runtimeAssembly.GetType(normalizedName);
                if (runtimeType != null)
                    return runtimeType;
            }

            // 3. System types — unqualified Type.GetType works for mscorlib and System.*.
            var systemType = Type.GetType(normalizedName);
            if (systemType != null)
                return systemType;

            // 4. AppDomain scan — last resort for third-party assemblies not covered above.
            //    Skips dynamic assemblies (Reflection.Emit outputs) which don't expose
            //    GetType() in a useful way and would cause exceptions.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                var resolved = assembly.GetType(normalizedName);
                if (resolved != null)
                    return resolved;
            }

            return null;
        }

        private static TypeReference ImportTypeReference(MethodRewriteContext context, TypeReference type)
        {
            var runtimeType = ResolveRuntimeType(type, context.RuntimeAssembly);
            if (runtimeType != null)
            {
                return context.TargetModule.ImportReference(runtimeType);
            }

            return context.TargetModule.ImportReference(type);
        }

        private static MethodReference ImportMethodReference(ModuleDefinition module, MethodInfo method)
        {
            return method != null ? module.ImportReference(method) : null;
        }

        private static string GetTypeName(TypeReference type)
        {
            if (type is GenericParameter genericParameter)
            {
                return genericParameter.Name;
            }

            return type.FullName;
        }

        // Delegates to TypeKeyName so this and HotReloadCallbackInvoker cannot drift apart — see
        // the note there for why a mismatch silently nulled every generic-typed field.
        private static string GetTypeName(Type type)
        {
            return TypeKeyName.For(type);
        }

        private static string NormalizeTypeName(string name)
        {
            return name?.Replace("/", "+");
        }
    }
}
