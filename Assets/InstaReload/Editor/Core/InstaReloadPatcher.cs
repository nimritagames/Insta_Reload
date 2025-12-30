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

        internal void ApplyAssembly(string assemblyPath, bool skipValidation = false)
        {
            try
            {
                var runtimeAssembly = FindRuntimeAssembly();
                if (runtimeAssembly == null)
                {
                    InstaReloadLogger.LogError($"Assembly '{_assemblyName}' not loaded - make sure it's referenced in your project");
                    return;
                }

                // THE MAGIC: Load compiled assembly so new methods exist at runtime
                try
                {
                    var assemblyBytes = System.IO.File.ReadAllBytes(assemblyPath);
                    System.Reflection.Assembly.Load(assemblyBytes);
                    InstaReloadLogger.Log("[Patcher] Compiled assembly loaded for IL extraction");
                }
                catch (Exception ex)
                {
                    InstaReloadLogger.LogWarning($"Failed to load compiled assembly: {ex.Message}");
                }

                ModuleDefinition updatedModule = null;
                try
                {
                    var resolver = new DefaultAssemblyResolver();
                    resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(assemblyPath));
                    resolver.AddSearchDirectory(UnityEditor.EditorApplication.applicationContentsPath + "/NetStandard/ref/2.1.0");
                    resolver.AddSearchDirectory(UnityEditor.EditorApplication.applicationContentsPath + "/Managed");

                    updatedModule = ModuleDefinition.ReadModule(
                        assemblyPath,
                        new ReaderParameters
                        {
                            ReadSymbols = false,
                            ReadingMode = ReadingMode.Immediate,
                            AssemblyResolver = resolver
                        });
                }
                catch (Exception ex)
                {
                    InstaReloadLogger.LogError($"Failed to load compiled assembly: {ex.Message}");
                    return;
                }

                using (updatedModule)
                {
                    // OPTIMIZATION: Skip expensive structural validation on fast path
                    // ChangeAnalyzer already verified only method bodies changed
                    if (!skipValidation)
                    {
                        if (!IsCompatible(runtimeAssembly, updatedModule, out var reason))
                        {
                            InstaReloadLogger.LogWarning($"⚠ Structural change: {reason}");
                            InstaReloadLogger.LogWarning(InstaReloadLogCategory.Patcher, "→ Exit Play Mode to apply this change");
                            return;
                        }
                    }
                    else
                    {
                        InstaReloadLogger.Log("[Patcher] ⚡ Fast path - skipping structural validation (trusted)");
                    }

                    var runtimeMethods = BuildRuntimeMethodMap(runtimeAssembly);
                    var runtimeFields = BuildRuntimeFieldMap(runtimeAssembly);
                    var methodIds = BuildMethodIdMap(updatedModule);
                    var dispatchKeys = BuildDispatchKeySet(updatedModule, runtimeMethods);

                    var dispatcherInvokeMethod = ResolveDispatcherInvokeMethod(runtimeAssembly);
                    if (dispatcherInvokeMethod == null)
                    {
                        InstaReloadLogger.LogError("[Patcher] Dispatcher Invoke method not found");
                        return;
                    }

                    lock (_sync)
                    {
                        DisposeMethodHooks();

                        int patched = 0;
                        int skipped = 0;
                        int newMethods = 0;
                        int dispatched = 0;
                        int trampolines = 0;
                        var errors = new List<string>();
                        var newMethodNames = new List<string>();
                        var missingEntryPoints = new List<string>();

                        foreach (var method in GetPatchableMethods(updatedModule))
                        {
                            var methodName = GetMethodKey(method);

                            // Skip Unity-generated methods
                            if (method.DeclaringType.Name.StartsWith("UnitySourceGenerated"))
                            {
                                skipped++;
                                continue;
                            }

                            if (!IsMethodBodySupported(method))
                            {
                                skipped++;
                                continue;
                            }

                            var key = methodName;

                            if (IsUnityEntryPoint(method))
                            {
                                try
                                {
                                    if (methodIds.TryGetValue(key, out var methodId))
                                    {
                                        if (runtimeMethods.TryGetValue(key, out var runtimeEntryPointMethod))
                                        {
                                            if (EnsureTrampoline(runtimeEntryPointMethod, key, dispatcherInvokeMethod, methodId))
                                            {
                                                trampolines++;
                                            }
                                        }
                                        else
                                        {
                                            missingEntryPoints.Add(methodName);
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

                            if (runtimeMethods.TryGetValue(key, out var runtimeTargetMethod))
                            {
                                try
                                {
                                    var hook = new ILHook(
                                        runtimeTargetMethod,
                                        ctx => ReplaceMethodBody(ctx, method, runtimeAssembly, runtimeMethods, runtimeFields, methodIds, dispatchKeys, dispatcherInvokeMethod));
                                    _methodHooks[key] = hook;
                                    patched++;
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
                                if (TryRegisterDispatcher(method, runtimeAssembly, runtimeMethods, runtimeFields, methodIds, dispatchKeys, dispatcherInvokeMethod, out var error))
                                {
                                    newMethods++;
                                    dispatched++;
                                    newMethodNames.Add(methodName);
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

                        if (missingEntryPoints.Count > 0)
                        {
                            InstaReloadLogger.LogWarning($"[Patcher] {missingEntryPoints.Count} Unity entry point(s) missing at runtime (added during Play Mode):");
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
                            InstaReloadLogger.Log(message);

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

                        if (errors.Count > 0)
                        {
                            InstaReloadLogger.LogError($"Failed to patch {errors.Count} method(s):");
                            foreach (var error in errors.Take(5)) // Show max 5 errors
                            {
                                InstaReloadLogger.LogError($"  -> {error}");
                            }
                            if (errors.Count > 5)
                            {
                                InstaReloadLogger.LogError($"  ... and {errors.Count - 5} more");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogError($"Hot reload failed: {ex.Message}");
                InstaReloadLogger.LogError($"→ Try exiting Play Mode and re-entering");
            }
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

        private static Dictionary<string, MethodBase> BuildRuntimeMethodMap(Assembly runtimeAssembly)
        {
            var map = new Dictionary<string, MethodBase>(StringComparer.Ordinal);
            foreach (var type in runtimeAssembly.GetTypes())
            {
                var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(flags))
                {
                    map[GetMethodKey(method)] = method;
                }

                foreach (var ctor in type.GetConstructors(flags))
                {
                    map[GetMethodKey(ctor)] = ctor;
                }

                if (type.TypeInitializer != null)
                {
                    map[GetMethodKey(type.TypeInitializer)] = type.TypeInitializer;
                }
            }

            return map;
        }

        private static IEnumerable<MethodDefinition> GetPatchableMethods(ModuleDefinition module)
        {
            foreach (var type in module.Types)
            {
                foreach (var method in GetPatchableMethods(type))
                {
                    yield return method;
                }
            }
        }

        private static IEnumerable<MethodDefinition> GetPatchableMethods(TypeDefinition type)
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
                    InstaReloadLogger.LogVerbose($"Skipping generic method: {GetMethodKey(method)}.");
                    continue;
                }

                yield return method;
            }

            foreach (var nested in type.NestedTypes)
            {
                foreach (var method in GetPatchableMethods(nested))
                {
                    yield return method;
                }
            }
        }

        private static bool IsCompatible(Assembly runtimeAssembly, ModuleDefinition updatedModule, out string reason)
        {
            reason = string.Empty;

            var runtimeTypes = runtimeAssembly.GetTypes().ToDictionary(t => t.FullName, t => t);
            var updatedTypes = GetAllTypes(updatedModule)
                .Where(t => t.Name != "<Module>")
                .ToList();

            // CRITICAL FIX: When compiling a single file, the updated assembly will have fewer types
            // than the runtime assembly. We only need to validate the types that ARE in the update.
            // Don't check if type sets are equal - that will always fail for single-file compilation!

            foreach (var updatedType in updatedTypes)
            {
                var runtimeName = NormalizeTypeName(updatedType.FullName);
                if (!runtimeTypes.TryGetValue(runtimeName, out var runtimeType))
                {
                    // This is a NEW type - it doesn't exist in runtime yet
                    reason = $"New type added: {runtimeName}";
                    return false;
                }

                if (!FieldSetsMatch(updatedType, runtimeType, out var fieldReason))
                {
                    reason = fieldReason;
                    return false;
                }

                if (!MethodSetsMatch(updatedType, runtimeType, out var methodReason))
                {
                    reason = methodReason;
                    return false;
                }
            }

            // All types in the update are compatible with runtime versions
            return true;
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
                reason = $"Field set changed in {runtimeType.FullName}.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool MethodSetsMatch(TypeDefinition updatedType, Type runtimeType, out string reason)
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

            // Check for REMOVED methods (exists in runtime but NOT in updated) - NOT ALLOWED
            var removedMethods = runtimeMethods.Except(updatedMethods).ToList();
            if (removedMethods.Count > 0)
            {
                reason = $"Method(s) removed from {runtimeType.Name}: {removedMethods.First()}";
                return false;
            }

            // NEW methods (exists in updated but NOT in runtime) - ALLOWED!
            // We'll add these dynamically at runtime

            reason = string.Empty;
            return true;
        }

        private static bool IsMethodBodySupported(MethodDefinition method)
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (!IsOperandSupported(instruction.Operand))
                {
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

        private static Dictionary<string, FieldInfo> BuildRuntimeFieldMap(Assembly runtimeAssembly)
        {
            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            foreach (var type in runtimeAssembly.GetTypes())
            {
                var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var field in type.GetFields(flags))
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

            CloneMethodBody(dmd.Definition, updatedMethod, context);

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
                targetIncludesThis: false);

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
            }

            public ModuleDefinition TargetModule { get; }
            public Assembly RuntimeAssembly { get; }
            public IReadOnlyDictionary<string, MethodBase> RuntimeMethods { get; }
            public IReadOnlyDictionary<string, FieldInfo> RuntimeFields { get; }
            public IReadOnlyDictionary<string, int> MethodIds { get; }
            public ISet<string> DispatchKeys { get; }
            public bool TargetIncludesThis { get; }
            public MethodReference DispatcherInvoke { get; }
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

            foreach (var instruction in updatedMethod.Body.Instructions)
            {
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

        private static Type ResolveRuntimeType(TypeReference type, Assembly runtimeAssembly)
        {
            if (type == null)
            {
                return null;
            }

            if (type is GenericParameter)
            {
                return null;
            }

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

            if (runtimeAssembly != null)
            {
                var runtimeType = runtimeAssembly.GetType(normalizedName);
                if (runtimeType != null)
                {
                    return runtimeType;
                }
            }

            var systemType = Type.GetType(normalizedName);
            if (systemType != null)
            {
                return systemType;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                var resolved = assembly.GetType(normalizedName);
                if (resolved != null)
                {
                    return resolved;
                }
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

        private static string GetTypeName(TypeReference type)
        {
            if (type is GenericParameter genericParameter)
            {
                return genericParameter.Name;
            }

            return type.FullName;
        }

        private static string GetTypeName(Type type)
        {
            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            return type.FullName ?? type.Name;
        }

        private static string NormalizeTypeName(string name)
        {
            return name?.Replace("/", "+");
        }
    }
}
