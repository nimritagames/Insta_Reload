# Decisions

Format: date / decision / context / outcome

## Entries

- Date: 2026-02-22
  Decision: Use FileSystemWatcher instead of AssetPostprocessor for change detection
  Context: AssetPostprocessor fires after Unity processes the change; FileSystemWatcher fires before Unity, allowing the patcher to win the race
  Outcome: Patches applied before Unity triggers domain reload

- Date: 2026-02-22
  Decision: Text-based signature parsing (not AST) for fast/slow path detection
  Context: Roslyn AST parsing costs 74ms+ per file; text parsing costs 2-5ms
  Outcome: 90% of edits (method body only) take the 7ms fast path instead of 700ms slow path

- Date: 2026-02-22
  Decision: Store ILHooks in static dictionary (never dispose during play mode)
  Context: ILHook.Dispose() removes the patch; GC would silently undo patches if stored as locals
  Outcome: Patches persist for entire play mode session

- Date: 2026-02-22
  Decision: Suppress Unity compilation via DisallowAutoRefresh + LockReloadAssemblies
  Context: Without suppression, Unity races the patcher and triggers domain reload, wiping patches
  Outcome: Unity waits; patches applied cleanly; Unity catches up on play mode exit

- Date: 2026-02-22
  Decision: Dual Roslyn compilation (Debug for fast, Release for slow path)
  Context: Release emit = 722ms (IL optimizations), Debug emit = 6ms (no optimizations)
  Outcome: Fast path achieves ~7ms compilation; slow path remains ~700ms

- Date: 2026-02-22
  Decision: Persist signature cache to disk (Library/InstaReloadSignatureCache.dat)
  Context: Domain reloads wipe in-memory state; cache must survive to avoid cold-start on every play
  Outcome: Cache reloaded on domain reload, fast path resumes immediately

- Date: 2026-02-22
  Decision: Optional external worker process (.NET 8.0) via TCP
  Context: In-process Roslyn blocks Unity editor thread during slow-path compilation
  Outcome: Worker offloads compilation to separate CPU; editor remains responsive

- Date: 2026-02-22
  Decision: Walk base type chain by name for MonoBehaviour detection (not typeof(MonoBehaviour))
  Context: A hard typeof(UnityEngine.MonoBehaviour) reference creates a compile-time dependency on a specific UnityEngine module. Walking by name works across all Unity versions and module layouts.
  Outcome: IsMonoBehaviourSubclass() checks the inheritance chain by FullName — zero hard Unity type dependencies in the patcher.

- Date: 2026-02-22
  Decision: Register new MonoBehaviour lifecycle entry points after type registration (not during patching loop)
  Context: The patching loop handles existing types; new types have no runtime method to ILHook. Registering entry points in RegisterNewTypes ensures the proxy scanner and subsequent-cycle trampolines are ready from the moment the type is known.
  Outcome: RegisterMonoBehaviourEntryPoints() called per new type in RegisterNewTypes(); HotReloadEntryPointManager knows about new MonoBehaviour types from the first cycle.

- Date: 2026-02-22
  Decision: Check HotTypeRegistry first in ResolveRuntimeType, before runtimeAssembly and before AppDomain scan
  Context: New types don't exist in the original runtime assembly, so checking it first always misses. The AppDomain scan (fallback) is O(loaded assemblies × types) and would return stale types if the same file was reloaded multiple times. HotTypeRegistry is O(1) and always holds the latest version.
  Outcome: New types resolve in O(1) on the fast path with no AppDomain scan needed. Stale-version problem eliminated.

- Date: 2026-02-22
  Decision: Register new types before BuildRuntimeMethodMap, not after
  Context: If registration happened after building the method map, methods on new types from THIS cycle wouldn't be in the map. The registration → map-build order ensures new-type methods are resolvable within the same hot reload cycle that introduced them.
  Outcome: RegisterNewTypes() called first, BuildRuntimeMethodMap() folds in HotTypeRegistry.GetAll() covering both current and previous cycle types.

- Date: 2026-02-22
  Decision: Extract AddMethodsFromTypes() helper from BuildRuntimeMethodMap
  Context: BuildRuntimeMethodMap needed to iterate two type collections (runtime assembly + hot types). Extracting the inner loop avoids duplication and makes the two passes explicit and readable.
  Outcome: Clean separation; easy to add a third pass later if needed.

- Date: 2026-02-22
  Decision: Create HotTypeRegistry as a Runtime static class (not Editor-only)
  Context: IL cloning (Editor) and MonoBehaviour dispatch (Runtime) both need to look up new types. Placing it in Runtime/ makes it accessible to both assemblies, consistent with HotReloadDispatcher and HotReloadFieldStore. Keys are pre-normalized full names; normalization stays in the patcher to keep the registry convention-free.
  Outcome: Central lookup table for all new types discovered across hot reloads in a session. Clear() wired into UnityCompilationSuppressor alongside Dispatcher and EntryPointManager.

- Date: 2026-02-22
  Decision: Replace IsCompatible(out string) → bool with CheckCompatibility() → CompatibilityResult (readonly struct)
  Context: Old signature conflated "hard blocker found" with "new types found" into a single out-param, making it impossible to allow new types without restructuring. A result struct separates the two concerns cleanly, adds no heap allocation, and is easy to extend.
  Outcome: New types are now collected into NewTypeFullNames instead of blocking validation. Assembly.Load() moved after validation so rejected assemblies never enter the AppDomain. hotAssembly reference captured (was previously discarded) for use by HotTypeRegistry in Task 3.

- Date: 2026-02-22
  Decision: Route new instance/static fields on existing types through HotReloadFieldStore via IL opcode rewriting (pre-existing infrastructure, documented this session)
  Context: Runtime type layout is fixed once JIT'd — can't add memory slots. New fields would crash if accessed via normal ldfld/stfld. The field store rewrites those opcodes at IL clone time to call HotReloadFieldStore instead. Instance fields use ConditionalWeakTable (GC-safe, lifetime tied to instance). Static fields use a plain Dictionary (session lifetime).
  Outcome: ldfld/stfld → GetInstanceField/SetInstanceField. ldsfld/stsfld → GetStaticField/SetStaticField. Field key = "DeclaringType::FieldName:FieldTypeName:instance|static" (includes declaring type to prevent cross-type collisions). ldflda/ldsflda are blocked (no stable address for store values). Static .cctor-based initializers won't re-run for new fields on existing types — values start at default (known limitation).

- Date: 2026-02-22
  Decision: Fix CS0433 BinaryPrimitives ambiguity with manual bit shifts instead of extern alias
  Context: Both System.Memory and mscorlib define System.Buffers.Binary.BinaryPrimitives in Unity's csproj — fully-qualifying doesn't help when the full name itself is duplicated across assemblies. extern alias requires csproj edits that Unity regenerates.
  Outcome: Replaced both BinaryPrimitives calls in InstaReloadWorkerClient with manual little-endian bit shifts. Identical behavior, zero ambiguity, no imports needed.

- Date: 2026-02-22
  Decision: Add HotTypeRegistry.cs.meta manually instead of waiting for Unity import
  Context: HotTypeRegistry.cs was created outside Unity (no editor open). Without a .meta file Unity never imports the file and the generated Editor.csproj omits it, causing CS0103 errors on every dotnet build.
  Outcome: Created .meta file manually with a fresh GUID following the MonoImporter format used by adjacent Runtime files. Unity will include the file on next import and regenerate the csproj correctly.

- Date: 2026-02-22
  Decision: Include hot type fields in BuildRuntimeFieldMap (alongside runtime assembly fields)
  Context: Fields on brand-new types (added via hot reload) must use direct CLR access, not HotReloadFieldStore. The hot assembly's .ctor runs natively via newobj (not rewritten — only call/callvirt are intercepted), so it writes CLR field slots directly. If hot type fields were absent from RuntimeFields, TryRewriteFieldInstruction would route all reads to FieldStore (empty) while the constructor wrote to CLR — two different backing stores, values lost.
  Outcome: BuildRuntimeFieldMap iterates HotTypeRegistry.GetAll() after scanning runtimeAssembly. Hot type fields get direct CLR access everywhere — consistent with .ctor behavior. Existing types with new fields are NOT in HotTypeRegistry, so their new fields still correctly use FieldStore.

- Date: 2026-08-05
  Decision: Method removal and signature change are allowed, not blocked — do nothing to the removed method
  Context: MethodSetsMatch rejected the whole reload if any method disappeared from source, on the
    theory that existing call sites would jump into nothing. That theory is wrong: a JIT'd method
    is never removed from memory, and GetPatchableMethods iterates the NEW module, so a method
    gone from source is simply never touched. Its original body stays live and callable.
    An earlier plan to emit tombstone stubs was dropped — nothing needs to be emitted at all.
  Outcome: VERIFIED in play mode, four cases:
    (1) delete unused method → applies + warns (was rejected outright)
    (2) delete method AND its call → applies, no stale code, cleanest case
    (3) SIGNATURE CHANGE → applies live. Add(int,int) -> Add(int,int,int) gave "Add = 5" -> 15.
        Reads as remove-old + add-new: old overload keeps its body for old callers, new one is
        picked up by the EXISTING new-method dispatcher path. No new machinery needed.
    (4) delete a method an unedited file still calls → applies; that caller keeps running the OLD
        body (frames 4914..6647 observed, no MonoMod wrappers in its stack) until Play Mode ends.
  Cost, stated honestly: stale behaviour, never a crash. Bounded by Play Mode — on exit Unity
    recompiles and reports a normal CS1061 for the dangling cross-file reference. That compiler
    backstop was discovered during testing and is the reason the cost is acceptable.

- Date: 2026-08-05
  Decision: Re-baseline ChangeAnalyzer signatures from disk on Play Mode enter
  Context: Found while testing the above — the removal warning silently did NOT fire. Root cause:
    OnEditorUpdate discards pending changes when not in play mode, so Analyze() never sees edits
    made outside Play Mode and the cached signature can describe source that no longer exists.
    Deleting a method, restoring it outside Play Mode, then deleting it again produced a hash
    matching the stale baseline → classified MethodBodyOnly → fast path → validation skipped →
    no removal warning. A pre-existing bug, but it silently disabled the new feature's safety net.
  Outcome: VERIFIED. ChangeAnalyzer.RefreshFromDisk() runs from UnityCompilationSuppressor on
    play mode enter. Logged "Refreshed 1 signature(s) that changed outside Play Mode", the same
    edit then classified MethodSignatureChanged, took the slow path, and the warning fired.
  NOTE: the fast path still skips validation by design, so removal detection depends on the
    analyzer classifying correctly. Keeping the baseline honest is what makes that safe.

- Date: 2026-08-05
  Decision: Cecil reads Deferred instead of Immediate, and shares one assembly resolver
  Context: Splitting `patch` into sub-phases showed Cecil's ModuleDefinition.ReadModule was ~41ms
    of a ~55ms patch — over half of total reload latency. Two causes: ReadingMode.Immediate
    (eagerly reads every type and method body, plus resolves references) and a brand-new empty
    DefaultAssemblyResolver constructed on EVERY reload, so UnityEngine/mscorlib were re-resolved
    from disk each save. Immediate was a deliberate override; Deferred is Cecil's own default.
  Outcome: cecil 41ms -> 29ms. Real but MODEST — and well short of the "single digits" predicted.
    Most of the 13ms came from the resolver, not from Deferred: the patch assembly is ~3KB, so
    there were barely any method bodies to defer. Total reload landed at ~59ms vs ~58ms before,
    i.e. inside the run-to-run noise band.
  DECISION TO STOP HERE: spread is 46-70ms for identical work, so disk/OS scheduling noise now
    exceeds the remaining headroom. Two consecutive estimates for this stage were wrong in the
    same direction, which means there is no reliable model left of where the time goes. Further
    changes would carry correctness risk for no perceptible gain — 59ms is already far under the
    ~100ms human "instant" threshold.

- Date: 2026-08-05
  Decision: Cache the file→assembly lookup and the worker compile context per domain
  Context: `queue` was 69ms/reload and hid two unrelated costs, so a probe was split out first
    (`assembly` vs `queue`) rather than guessing. Both turned out to be invariants recomputed on
    the hot path: GetAssemblyNameForFile called CompilationPipeline.GetAssemblies() plus an
    O(assemblies × sourceFiles) string scan on EVERY save, and EnsureReady rebuilt the whole
    compile context per job (reflection for Unity's internal compilation defines, a PlayerSettings
    query, SHA256 over all reference paths) purely to ask "did anything change?".
  Outcome: VERIFIED. queue 69ms -> 0ms; assembly 0ms when cached. Fast path 138ms -> 58ms.
    Splitting the probe first was the right call — it showed GetAssemblies() alone costs 119ms on
    its first cold call, worse than the entire old queue average suggested.
    Both values only change via edits that force a domain reload, which clears the statics;
    ReferenceResolver already cached its half on the same assumption. The one input reachable
    WITHOUT a domain reload is WorkerPort (the window's field calls EnsureReady with no Shutdown),
    so the context cache re-checks it on every hit. Shutdown() clears the cache for all other paths.

- Date: 2026-08-05
  Decision: Keep the external worker alive across play mode exits; adopt it on reconnect; pre-heat at editor load
  Context: UnityCompilationSuppressor.DisableSuppression() killed the worker on every play mode exit,
    so each play session spawned a fresh process and paid ~850ms re-JITting Roslyn and re-reading
    metadata on the first save. The worker is a separate process that a domain reload cannot touch,
    and it already self-terminates via --parentPid, so the kill bought nothing.
  Outcome: VERIFIED. Four coordinated changes, all needed:
    (1) Suppressor no longer kills the worker on play mode exit.
    (2) ConnectAsync probes the port BEFORE spawning, so a reloaded domain adopts the warm worker.
    (3) Worker HandleInit is idempotent — same references+defines returns the existing context
        instead of rebuilding 33 MetadataReferences (without this the win is mostly lost).
    (4) Pre-heat: FileChangeDetector.Initialize calls EnsureReady at editor load, and the client
        runs a throwaway compile after connect. Connecting alone is NOT enough — a freshly spawned
        worker has never run Roslyn's emitter, so the JIT cost has to be paid by something.
    Result: per-play-session cold start eliminated. ~600-800ms once per editor session, in
    background. Adoption ~6-9ms. Play-mode patch replay 10401ms -> 4ms.

- Date: 2026-08-05
  Decision: Derive the worker port per project, and bind the worker to a project path
  Context: Port was a fixed 53530. That was only briefly contended when workers died at play mode
    exit. Once workers persist AND are adopted on reconnect, a second project would adopt the first
    project's worker and re-Init it with different references — silently compiling against the wrong
    reference set.
  Outcome: Port = configured base + (SHA256(projectPath) % 64). Worker also records ProjectPath at
    Init and rejects a caller from a different project, so a collision inside the span fails loudly
    instead of corrupting compiles. ProtocolVersion bumped 1 -> 2.

- Date: 2026-08-05
  Decision: Skip InstaReload entirely in AssetImportWorker processes, and handle worker clients concurrently
  Context: Found via the new lifecycle logs — TWO Unity processes were connected to one compile
    worker. Unity runs [InitializeOnLoad] inside AssetImportWorker processes, so pre-heating at
    editor load made every import worker connect too. Combined with the worker's serial accept loop
    (`await HandleClientAsync` inline), a connection abandoned by a domain reload could sit half-open
    and starve the live editor — hanging every compile with no error surfaced.
  Outcome: FileChangeDetector.Initialize early-returns on AssetDatabase.IsAssetImportWorkerProcess()
    (the watcher and patcher were pure overhead there even before this change). Worker accept loop
    now dispatches each client to its own task with _context access serialized behind a lock.
    Verified: one ESTABLISHED connection, previous ones in TIME_WAIT (clean FIN, not half-open).

- Date: 2026-08-05
  Decision: Replace HotReloadCallbackInvoker's AppDomain reflection sweep with UnityEditor.TypeCache
  Context: FindAttributedMethods walked AppDomain.CurrentDomain.GetAssemblies() -> GetTypes() ->
    GetMethods() -> IsDefined(inherit:true), and InvokeCallbacks runs it TWICE (global + local
    attribute). Measured ~3.5s per sweep in the Unity 6 Editor AppDomain = ~6.9s of a ~7.1s warm
    reload, 94% of total latency — recomputing a set that only changes when assemblies change.
    TypeCache is the index Unity already maintains for this exact query and rebuilds on domain
    reload, so the cost disappears rather than being hand-cached (no invalidation logic to get wrong).
  Outcome: VERIFIED in play mode. callbacks 6948ms -> 0ms. Warm reload 7137ms -> ~140ms (51x).
    Play-mode-entry patch replay 10401ms -> 201ms (same root cause, same call).
    TypeCache does not index assemblies Unity never compiled, so new types introduced by hot reload
    are covered by also scanning HotTypeRegistry.GetAll() — small by construction, it only ever
    holds hot-added types. Residual known gap: attributes inside a DLL loaded at runtime by a
    third-party plugin that is neither Unity-compiled nor hot-registered. Empty in practice; if it
    ever matters the fallback is a cached full sweep invalidated on AppDomain.AssemblyLoad.
    Side benefit: removed two latent double-invokes — the old sweep found callbacks on both the
    original AND the recompiled hot copy of a type, and IsDefined(inherit:true) matched both a
    virtual base method and its override (whose instances FindObjectsOfTypeAll returns for both).

- Date: 2026-08-05
  Decision: Measure the whole save -> patch pipeline with a per-reload ReloadTimeline, not just compile
  Context: The console printed only RoslynCompiler's own number. Debounce, queue waits, main-thread
    pickup, patching and post-patch work were all invisible, so a reported "11ms" reload could sit
    inside seconds of felt latency with no way to attribute it. Optimising against that number was
    tuning ~1% of the cost.
  Outcome: ReloadTimeline (Editor/Diagnostics) stamps 7 stage boundaries and travels on CompileJob.
    Clock is a static Stopwatch, NOT EditorApplication.timeSinceStartup — T0 is stamped on the
    FileSystemWatcher's background thread where Unity APIs aren't valid, and timeSinceStartup is
    frame-quantized. Compile end is stamped inside a ContinueWith so it happens-before the main
    thread observes IsCompleted; the gap to pickup is main-thread starvation. Reported as two
    console lines per reload plus an End-to-End Timing card, and averaged in SessionMetrics.
    A deliberately reported "unaccounted" remainder catches costs no probe covers yet.

- Date: 2026-08-05
  Decision: Count watcher events per reload burst and start the timeline at the FIRST one
  Context: _lastChangeTime resets on every FileSystemWatcher event, so a save that fires several
    events (VS/Rider write temp-file + rename) keeps restarting the 300ms debounce window. Starting
    the measurement when the window finally closes would hide exactly that cost.
  Outcome: TrackChangedFile opens one timeline per file on the first event and only increments a
    counter afterwards. WatcherEventCount > 1 is surfaced in the window, making a sliding debounce
    window directly observable instead of hypothetical.

- Date: 2026-02-22
  Decision: Field support matrix — what works, what doesn't, and why
  Context: Three distinct cases with different semantics; important to document precisely to avoid re-investigating.
  Outcome:
    (1) Fields on new type → WORKS. Constructor sets CLR fields natively (newobj). Reads/writes in patched methods use direct CLR access (hot type in RuntimeFieldMap). Reflection bypasses FieldStore and sees actual CLR values too.
    (2) New instance field on existing type → WORKS via FieldStore. Runtime type has no slot; ldfld/stfld rewritten to GetInstanceField/SetInstanceField (ConditionalWeakTable keyed on instance). Constructor field initializers work if the method is patched.
    (3) New static field on existing type → WORKS via FieldStore; static initializer value LOST. .cctor already ran before hot reload. Field starts at default(T). All subsequent reads/writes via FieldStore work correctly.
    BLOCKED: ldflda/ldsflda on missing fields (no stable address for store values). Removed methods (existing call sites would crash). Changed method signatures. Generic new types.

- Date: 2026-08-05
  Decision: One Info line per reload; everything diagnostic demoted to Verbose
  Context: A single save printed ~14 console lines, four of them outright duplicates (Analysis +
    FAST PATH; [Roslyn] compiled in + [FileDetector] Roslyn compiled in; Applying patches with
    fast path + Patcher fast-path skip; Timing TOTAL + Patcher hot reload complete). Entering
    Play Mode added 6 [Suppressor] state lines, and every domain reload added 7 [Roslyn] init
    lines. Signal was drowning in the instrumentation built to find it.
  Outcome: ReportTimeline now emits the only Info line a successful reload produces —
    "[InstaReload] File.cs — 59ms (fast path) · patched N, dispatched N" — taking counts from
    PatchApplyResult so the Patcher's own completion line could drop to Verbose. Stage and patch
    breakdowns, suppressor state, worker adopt/spawn/warmup, Roslyn init, and per-file
    detect/analyse chatter are all LogVerbose: retained, off by default (enabledLogLevels=7).
    Kept at Info because each marks a real state change, not a step: file monitoring active,
    ChangeAnalyzer refreshed-N-signatures, new type/method/field registration, Unity-compilation
    fallback, and all user-initiated menu/settings actions. No Warning or Error was touched —
    removal warnings, patch errors and worker failures are exactly what the noise was hiding.

- Date: 2026-08-05
  Decision: Generic method hot reload IS feasible with the mechanism we already have. Design
    validated empirically before writing any of it.
  Context: it was the biggest gap vs commercial Hot Reload. Research first, then a probe.
  RESEARCH FINDINGS:
    * .NET's OFFICIAL hot reload (ApplyUpdate metadata deltas, which Mono's own hot_reload
      component implements) CANNOT update generic methods at all — rude edit, open issues
      dotnet/runtime#82791 and #82792. Microsoft has not solved this with full runtime control.
      Also unsupported there: adding lambdas, await, async methods, instance fields on existing
      types, nested classes. InstaReload ALREADY does several of those.
    * The tools that DO support generics use DETOURS, not metadata deltas.
    * Mono shares ONE native body across all reference-type instantiations (gshared; constraint
      `object` matches all reference types). Harmony documents this as a defect for MODDING
      ("patching one method patches it for all types of T") — for HOT RELOAD it is exactly what
      we want.
    * Value-type instantiations get their own specialised native code, JITted on demand.
  PROBE RESULTS (Unity 6000.3.10f1, Editor menu items, no Play Mode needed):
    * MonoMod `Hook` REFUSES: ArgumentException "Source method is generic, generic hooks are not
      supported". Dead end.
    * MonoMod `ILHook` WORKS on a constructed generic MethodInfo. This is the SAME mechanism the
      patcher already uses for ordinary methods.
    * ILHook on Describe<object> -> string, object AND GameObject all returned PATCHED-SHARED,
      while int and float stayed ORIGINAL. One hook covers every reference-type instantiation.
    * ILHook on Describe<int> worked INDEPENDENTLY and did not clobber the shared hook (both live
      at once) — disposes of the Harmony#426 "instantiations overwrite each other" concern.
    * Dispose restored ORIGINAL for all five. Re-patch cycles are safe.
  IMPLEMENTATION PLAN (not built yet):
    1. Do NOT hook the open definition — it has no native code, which is why all four skip sites
       bail today. Construct instantiations instead.
    2. One ILHook per changed generic method, on the instantiation built with a REFERENCE type
       (e.g. object) -> covers all reference-type uses.
    3. Plus one ILHook per VALUE-TYPE instantiation discovered in the edited assembly's IL (Cecil
       can read GenericInstanceMethod operands at call sites).
    4. Key hooks PER-INSTANTIATION, not per method-key string — today's _methodHooks keying would
       collapse them.
    5. Warn for value-type instantiations not discoverable (first used after the edit). The
       warning machinery from 1f5810f already fits.
  Outcome: replaces "generic methods are a technical wall" with a validated, incremental design.
    Constraint that remains real: a value-type instantiation first JITted after the edit runs
    stale code until Play Mode exits.

- Date: 2026-08-05
  Decision: RECORD how commercial Hot Reload for Unity actually works. Their Unity-side source is
    PUBLIC (gitlab.com/singularitygroup/hot-reload-for-unity) — I wrongly called it closed.
    Server/{win,osx,linux}-x64/CodePatcherCLI.exe is a prebuilt binary, but Runtime/ is full C#
    and the mechanism is visible there.
  THEIR ARCHITECTURE (Runtime/CodePatcher.cs, SymbolResolver.cs, MethodCompatiblity.cs):
    * They vendor a FORK OF HARMONY (SingularityGroup.HotReload.HarmonyLib) and call its low-level
      DetourApi.DetourMethod(original, patchMethod) directly — NOT MonoMod Hook (which refuses
      generics outright: "Source method is generic, generic hooks are not supported").
    * Target resolution: Module.ResolveMethod(metadataToken, genericTypeArgs, genericMethodArgs).
      They resolve in a GENERIC CONTEXT rather than enumerating instantiations, and rely on Mono
      sharing — the same behaviour our probe verified.
    * Token drift between compilations is handled by searching neighbouring tokens (state.offset).
    * Patch methods are STATIC with an explicit `this` first parameter (MethodCompatiblity treats
      that as the instance-method case). Same shape our NativeMethodSwapper probe used.
    * They skip Burst-compiled methods (BurstChecker) and TryUndoPatch before re-detouring.
  THE KEY ARCHITECTURAL DIFFERENCE, and it explains EVERY failure we hit today:
    * THEY never rewrite IL. The server compiles the changed file into a whole new assembly, then
      detours the ORIGINAL method to a REAL, PROPERLY COMPILED method in that new assembly.
    * WE clone IL with Cecil and synthesise a replacement body, so we must remap every metadata
      token, field reference and type reference by hand.
    * Therefore: our async crash (invalid IL, "call 0x00000011") , our generic field-key mismatch,
      and our skipped generic methods are all symptoms of ONE root choice — IL cloning. Their
      approach cannot hit any of them, because the compiler produced the IL.
  IMPLICATION FOR ROADMAP: "detour to a compiled method" is strictly more robust than "clone and
    remap IL". Worth seriously evaluating as a direction, not just for generics/async. It is a
    large change (needs whole-assembly compile + a resolver keyed on metadata tokens with offset
    search), so it is a strategic decision, not a task.
  Our validated ILHook-on-constructed-instantiation finding still stands and is the CHEAP path to
    generics inside the current architecture.

- Date: 2026-08-05
  Decision: CORRECTION + verified conclusion on generic hot reload upstream.
  I claimed twice that dotnet/runtime#82791 and #82792 were still open for Mono. WRONG — both are
    CLOSED AS COMPLETED by commit 018a9bd ("[mono] refactor metadata update code", #85177), authors
    fanyang-mono and lambdageek. It adds GenericAddMethodToExistingType and GenericUpdateMethod
    capabilities to Mono. My error: I read the OLD tracking issue (#57365) which lists them as
    future work, and never checked the issues themselves.
  BUT — measured in the running Editor, not assumed:
      runtime = Mono 6.13.0 (Visual Studio built mono),  Unity 6000.3.10f1
      System.Reflection.Metadata.MetadataUpdater               -> NOT FOUND
      System.Reflection.Metadata.MetadataUpdateHandlerAttribute -> NOT FOUND
      MetadataUpdateOriginalTypeAttribute                       -> NOT FOUND
      DOTNET_MODIFIABLE_ASSEMBLIES = (not set)
  Unity ships Mono 6.13, a fork from the pre-donation lineage. The metadata-update work landed in
    dotnet/runtime's Mono (v7/8/9), a different lineage entirely. So the proper fix EXISTS and is
    UNREACHABLE here — none of the machinery is present in Unity's runtime.
  CONSEQUENCES:
    * On Unity 6.3 today, DETOUR-based patching is the only available route. Not a design
      preference, a runtime constraint.
    * Our validated finding stands as the practical path: ILHook on a CONSTRUCTED instantiation,
      with Mono's reference-type sharing covering all reference-type uses in one hook.
    * The proper solution arrives with Unity's CoreCLR migration, not before. Worth re-checking
      this probe (MetadataUpdater.IsSupported) whenever the Editor runtime changes.

- Date: 2026-08-05
  Decision: GENERIC METHOD HOT RELOAD WORKS END-TO-END. Proven through the real pipeline, not a
    synthetic hook. Experiment is UNCOMMITTED in the working tree.
  What made it work (two changes on top of the validated hooking mechanism):
    1. Hook site: ILHook refuses an open generic definition (NotSupportedException), so construct a
       reference-type instantiation - MakeGenericMethod(object, ...) - and hook that.
    2. Cloner: ImportTypeReference now SUBSTITUTES generic parameters instead of importing them.
       Cecil's context-free ImportReference throws NullReferenceException at
       ImportGenericContext.MethodParameter on a GenericParameter. Also recurses through
       GenericInstanceType / ArrayType / ByReferenceType, since those can CONTAIN a T (List<T>,
       T[], ref T) and would hit the same NRE.
  MEASURED, not assumed: MonoMod hands us a NON-GENERIC target for a constructed instantiation
    ("target=Describe targetGeneric=False targetParams=0"), so T is already substituted and the
    correct substitution is to `object`, NOT position-mapping onto target generic parameters. The
    position-mapping branch is kept for the case where a generic target does appear.
  RESULT: "CloneTarget.cs - 214ms (fast path) - patched 2, dispatched 0" and
      string = SUBSTITUTION-FIX:Object:1     <- hot reloaded
      GameObject = SUBSTITUTION-FIX:Object:2 <- hot reloaded
      int / float = old body                 <- value types, expected
  INHERENT COST FOUND: typeof(T).Name reports "Object" not "String" inside the patched body. The
    shared reference-type body genuinely does not know which reference type it was invoked with.
    Not a bug in the substitution - the price of one-hook-covers-all. Any code branching on
    typeof(T) will behave differently after a hot reload than after a real compile. MUST be
    documented as a limitation if this ships.
  STILL TO DO before this is shippable:
    * value-type instantiations: harvest GenericInstanceMethod operands from the edited assembly's
      IL and hook each; warn for value types first used after the edit (those cannot be reached).
    * key hooks PER-INSTANTIATION - _methodHooks is keyed by method-key string and would collapse
      the shared hook and each value-type hook onto one entry.
    * generic DECLARING TYPES still skipped (methods on List<T>-style types) - untested, harder.
    * remove the EXPERIMENT warnings and the temporary full-stack error logging at ~line 772.

- Date: 2026-08-06
  Decision: GENERIC CLASSES now hot reload too (feature/generic-methods). Second axis done.
  Mechanism: an open type definition has no native code, so the patch site CONSTRUCTS the declaring
    type before hooking - Container<object> for the shared reference body, Container<int> etc for
    value types harvested from the assembly. CollectGenericTypeInstantiations reads
    GenericInstanceType from both direct type operands (newobj/castclass/newarr) and the declaring
    type of member references. MethodBase.GetMethodFromHandle(openMethod.MethodHandle,
    constructedType.TypeHandle) gets the method as it exists on the constructed type.
  BLOCKER FOUND AND FIXED - third instance of the SAME bug class in one day: Cecil spells the
    declaring type of a member inside a generic type as a GenericInstanceType ("Container`1<T>")
    while reflection reports the open definition ("Container`1"). Keys never matched, so every field
    on a generic type looked MISSING, and any method touching one was refused with
    "Missing field address access not supported". (String concat of an int field emits ldflda to
    call ToString, which is the blocked address-access path.) Fixed with GetDeclaringTypeKeyName,
    collapsing a GenericInstanceType declaring type to its ElementType. Applied to BOTH field and
    method-reference keys.
    PATTERN WORTH REMEMBERING: b47b42e was generic FIELD TYPES, this is generic DECLARING TYPES.
    Any place that builds a key from both Cecil and reflection is suspect.
  VERIFIED in Play Mode:
      <string>     (ref, call site)    = DECLKEY-FIXED:Object:1
      <GameObject> (ref, NO call site) = DECLKEY-FIXED:Object:1   <- shared body reaches it anyway
      <int>        (value, call site)  = DECLKEY-FIXED:Int32:1    <- correct type argument
      <float>/<decimal> (no call site) = previous body            <- boundary, warned
  NOTE: reference-type sharing applies to generic TYPES as well as generic METHODS - a type
    instantiation with no call site anywhere still got patched.
  STILL OPEN: multiple type parameters, constraints (where T : struct), nested generic arguments,
    and a generic METHOD on a generic TYPE (both axes at once) are all untested.

- Date: 2026-08-06
  Decision: TESTED the four previously-untested generic combinations (feature/generic-methods,
    commit 7f31f1d). 3 of 4 work. Measured in Play Mode, not reasoned about.
  1. MULTIPLE TYPE PARAMETERS - WORKS, better than assumed. Mixed value/reference sets substitute
     each argument individually:
       <int,string>    (call site) = Int32+String
       <string,float>  (call site) = String+Single
       <string,object> (both ref)  = Object+Object   <- shared body
       <long,long>     (NO site)   = old body        <- boundary, warned
  2. CONSTRAINT where T : struct - WORKS. object is not a legal argument so there is NO shared
     reference body; the catch for that fires and value instantiations are still hooked.
     <int> patched, <float> (no call site) stale.
  3. NESTED GENERIC ARGUMENT (Nested<List<int>>) - WORKS with no harvesting needed, because
     List<T> is a REFERENCE type: the single shared hook covers <List<int>> AND <List<string>>.
     typeof(T) still reports Object there, as documented.
  4. GENERIC METHOD ON A GENERIC TYPE (Holder<T>.Both<U>) - FAILS. Predicted and confirmed.
     Constructing the declaring type leaves the METHOD open and ILHook refuses:
       "Both`1(T,U)|type<System.Int32> failed - Specified method is not supported."
     FIX DIRECTION: after MethodBase.GetMethodFromHandle yields the method on the constructed type,
     close the METHOD too via MakeGenericMethod. Needs the cross product of type args and method
     args, and both substitution channels at once (GenericArguments + DeclaringTypeArguments) -
     they exist but have only ever been exercised separately.
  UNPREDICTED FAILURE, separate problem: a NON-generic method on a generic type whose body
     CONSTRUCTS generic instantiations fails with a MONO runtime error, not a MonoMod one:
       "HolderCallSites|type<System.Object> failed - Method with open type while not compiling gshared"
     The body builds GenericHolder<int> and calls Both<float>. Creating closed instantiations
     inside a shared (gshared) body appears to defeat the JIT. Failed safely. NOT diagnosed.
  All four failures were loud and non-fatal - the per-instantiation reporting stated exactly what
    was and was not covered, and the Editor survived throughout.

- Date: 2026-08-06
  Decision: WORKFLOW HAZARD introduced by tracking Docs/project_notes on dev only.
  The notes are tracked on dev and NOT on feature branches, so `git checkout feature/...` DELETES
    them from the working tree, and `git checkout dev` restores them. Nothing is lost (they live in
    dev history) but a script writing to Docs/project_notes while on a feature branch fails with
    FileNotFoundError - which happened. Also note the directory is `Docs` with a CAPITAL D as git
    recorded it; a lowercase path silently matches nothing on Windows.
  HOW TO WORK WITH IT: record branch findings in the COMMIT MESSAGE, then update the notes from
    dev. Do not try to edit Docs/project_notes while on a feature branch.

- Date: 2026-08-06
  Decision: THE SUITE NOW GRADES THE RUNTIME METHOD, NOT THE CALL SITE. Every case is invoked by
    name through reflection on the live object's type; the ordinary direct call is still made and
    kept only as a cross-check. When the two disagree the suite prints a LEAK line naming the case.
  Context: 44eaad2 recorded that the suite could not tell "this method was patched" from "this call
    was inlined into a patched caller", and prescribed [MethodImpl(NoInlining)] plus an instance
    field read. That fix is necessary but NOT sufficient on its own - it does not cover the second
    way a call site can lie. InstaReloadPatcher.CloneInstruction remaps each method token to the
    runtime method and, ON A LOOKUP MISS, keeps the reference pointing at the freshly compiled hot
    assembly. A patched caller can therefore call a hot copy of a method the patcher explicitly
    REFUSED, and the refused method reports as patched. Reflection resolves from the live type at
    call time, so it is immune to both inlining and token fall-through.
    All three defences are in place now: NoInlining on every case, every case reads instance state
    before returning the marker, and the graded observation goes through reflection.
  MEASURED (Unity 6000.3.10f1, Play Mode, clean single-generation session):
    * baseline, nothing patched              = 22/22 PASS, no LEAK   <- the control
    * flip M0 -> M1, patched 37/dispatched 2 = 22/22 PASS, marker=M1, no LEAK
    * flip M1 -> M2, second generation       = 22/22 PASS, marker=M2, no LEAK
    Both directions are exercised: if reflection could not see patches the 10 Patched cases would
    fail; if it wrongly saw generics as patched the 12 Stale cases would fail. Neither happened.
  THE COMMIT'S STATED CAUSE WAS NOT CONFIRMED, and the mechanism is still open. Inlining cannot
    manufacture a fake PASS in the first place: an inlined copy of an UNPATCHED method carries the
    OLD marker, so inlining produces false STALEs, not false patches. What did reproduce - once, in
    a CONTAMINATED session (several hot-reload generations stacked up, plus a structural edit whose
    Awake failed to patch) - was six generic cases where the direct call site returned M1 while the
    runtime method returned M0: off by exactly one hot-reload generation. That points at
    cross-generation staleness (an older hot assembly's copy still being reached), NOT at inlining.
    It did NOT reproduce in a clean session across two generations. NOT DIAGNOSED. It no longer
    matters for trust, because a divergence of that kind now prints a LEAK line instead of passing
    silently, but it is a real product lead worth pulling.
  ONE-CYCLE SETTLE, documented in the suite header so nobody chases it: "coroutine ongoing" can
    report the previous marker on the FIRST grade after a patch, because the already-running
    iterator resumes every 0.25s and may not have re-entered its body yet. Observed once, passed on
    the next line. The observation is correct; only the timing is imprecise.
