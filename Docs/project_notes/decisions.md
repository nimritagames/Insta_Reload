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

- Date: 2026-08-06
  Decision: MERGED feature/generic-methods into dev (1fafefa). Generic hot reload ships.
  THE REGRESSION AT THE BRANCH TIP WAS NOT REAL. 1911d3a recorded "the 9 generic cases stopped
    patching" and 88e647e stopped at "Not fixed", having ruled out the parameter offset and
    nominated the trampolines 2 -> 0 drop as the next thread. Re-measured on the merged tree with
    the reflection-based suite: generics patch correctly. Nothing had regressed. The verdict came
    from the OLD harness - one-line `return Marker;` cases with no NoInlining, which Mono inlined
    into the caller, so the suite read the unpatched body's marker and called it a failure.
    44eaad2 named inlining correctly but predicted the wrong DIRECTION: it faked a FAIL, not a PASS,
    and the victim was the whole generic branch rather than the trivial dev cases.
    Four commits were spent chasing a bug that was never in the product. This is the single
    strongest argument for the "fix the harness first" instinct - the harness was costing days.
  ALSO KILLED: the trampolines 2 -> 0 lead. dev carries the same parameter-offset fix, also reports
    TrampolineCount 0 (FileChangeDetector only prints it when > 0), and passes every entry-point,
    coroutine and event case. Trampolines were not the thread.
  MEASURED, and the suite now agrees line for line with the patcher's own reporting:
    GenericMethod`1 patched 4 instantiation(s) Int32|Single|Double · Boxed`1::Read`0 2 ·
    Boxed`1::WithOpenList 2 · Combos::TwoParams`2 2 · Combos::StructOnly`1 1 (+Int32) ·
    Combos::Nested`1 1 · Boxed`1::BothAxes`1 NO instantiation patched (refused, keeps old body).
    Baseline 22/22, after the flip 22/22, zero errors, Editor stable. Nine expectations flipped
    Stale -> Patched; BothAxes stays Stale, exactly as 7f31f1d documented.
  TWO MORE HARNESS HOLES, found and closed during the merge:
    1. ORIGIN. Reflection proves which METHOD ran, never which OBJECT it ran on. Targets built
       inside patched Evaluate came back as HOT-ASSEMBLY instances, so BOTH channels interrogated
       the same wrong object, agreed, and scored BothAxes a pass while the patcher logged it
       refused. Reflected() now compares the target's assembly to `this` and reports HOT-OBJECT.
       GENERAL RULE: agreement between two observations is not evidence when both can share the
       same wrong input.
    2. Generic-class targets are constructed in Awake now, before any patch exists, so they are
       unambiguously runtime-assembly objects.
  STILL OPEN, and now the top lead: the generic-newobj token fall-through (see bugs.md). It is the
    same silent CloneInstruction miss behind the standing LEAK line, and a miss should warn rather
    than quietly bind to the hot assembly.

- Date: 2026-08-06
  Decision: GENERIC METHOD ON A GENERIC TYPE now hot reloads. The last refused generic shape is
    done, so generics are feature-complete for everything the suite can reach.
  Two separate defects, and the second only became visible once the first was fixed:
  1. THE METHOD STAYED OPEN. ApplyGenericTypeMethodHooks constructed the declaring TYPE and stopped
     there, so GetMethodFromHandle returned Boxed<int>.BothAxes<U> with U still unbound and ILHook
     rejected it outright - "Specified method is not supported", the exact error 7f31f1d recorded.
     Fixed by closing the method too via MakeGenericMethod, and by hooking the full cross product of
     (type args x method args), each pair under its own key. Both substitution channels
     (GenericArguments + DeclaringTypeArguments) are now passed together, which had never happened
     before - they existed but had only ever been exercised separately.
  2. NO VALUE-TYPE METHOD ARG WAS EVER FOUND. With (1) fixed the hooks installed but the suite still
     read stale, and the report said "patched 2" with no method type args covered - only the shared
     object body. The call-site harvest key did not match. Proved with a temporary log rather than
     guessed:
       want ...Boxed`1::BothAxes`1(U)=>System.String
       have ...Boxed`1::BothAxes`1(!!0)=>System.String
     Cecil leaves a generic parameter UNNAMED when the reference was constructed rather than
     resolved, and GenericParameter.Name then falls back to its position. So the same parameter is
     "U" in the definition and "!!0" at a call site reached through a generic INSTANCE declaring
     type. Methods on NON-generic types matched by name, which is why this stayed hidden.
     Fixed with GetInstantiationKey, which spells generic parameters POSITIONALLY on both sides.
     FOURTH instance of the Cecil-vs-reflection key mismatch in this project, after generic field
     types (b47b42e), generic declaring types (d6ee90b) and the collapsed method key. The pattern
     holds: any key built from both Cecil and reflection is suspect. Names are the recurring
     culprit - positions and element types are stable, spellings are not.
  MEASURED: BothAxes reports "patched 4 instantiation(s) - value types covered: System.Int32,
    method type args covered: System.Int32" - the full 2x2 cross product. Suite baseline 22/22,
    gen1 22/22, gen2 22/22, zero LEAK lines, no errors. Its expectation is flipped Stale -> Patched,
    so async is now the ONLY Stale case left in the suite.

- Date: 2026-08-06
  Decision: ASYNC/AWAIT HOT RELOADS. The suite now has ZERO Stale cases - everything a marker flip
    can reach picks up an edit.
  ROOT CAUSE was one line in the worker: `request.IsFastPath ? Debug : Release`. Roslyn emits an
    async state machine as a STRUCT under Release and as a CLASS under Debug, and Unity's own build
    is Debug. So the slow path built a state machine shaped differently from the running one, and
    patching against that mismatch is what produced the StackOverflowException that killed the
    Editor (plus the invalid IL with a raw unremapped token). The worker emits Debug on BOTH paths
    now. Compiling as Release was never right in the first place - we patch INTO a Debug build, so
    it was not comparing like with like.
  PROVED BEFORE TOUCHING THE REFUSAL, with async still refused so the probe could not crash
    anything. Same file, before and after, from Editor.log:
      before: "<RunAsync>d__40: base class changed (System.Object -> System.ValueType) - NOT applied"
              "1 method(s) no longer in source: -> <RunAsync>d__40::.ctor`0()"
      after:  neither line; the .ctor appears as a REAL method instead of a phantom removal
    A struct has no constructor and a class does, so the constructor reappearing IS the shape
    changing. That was the whole hypothesis, measured without risk.
  THE BACKLOG'S PLAN WAS WRONG, and measuring it cost nothing. "Patch the OUTER method so the next
    call builds a new state machine" is a NO-OP: the compiler moves essentially all user code into
    MoveNext, leaving the outer method a stub that constructs the machine and starts it. Tried it -
    Editor survived, suite reported 22/22 with async still stale, i.e. nothing happened. MoveNext is
    where an async body actually lives, so MoveNext is what has to be patchable. Refusing it and
    allowing only the outer method is precisely backwards.
  MEASURED with MoveNext allowed: async tracked the edit across two generations (M1 then M2), no
    crash, no errors, no StackOverflow. Expectation flipped Stale -> Patched; baseline 22/22, gen1
    22/22, gen2 22/22.
  SEMANTICS: a task ALREADY IN FLIGHT resumes into the new MoveNext, so it finishes under the new
    code from its next await onward; a task started after the edit runs the new body throughout.
    Same bounded staleness an already-running coroutine has, and accepted on the same grounds.
  SIDE EFFECT, and it closes a separate backlog item: the slow path fell from ~750ms to ~270-410ms,
    because Debug emit skips the optimiser. Backlog item "evaluate Debug emit on the slow path" is
    answered - do it, it is both faster and more correct.
  ORPHANS REMOVED: HasAsyncStateMachineAttribute and IsAsyncStateMachine, unused once the refusals
    went. The Stale branch of the suite's Check() is deliberately KEPT despite having no users - it
    is what a future limitation gets graded with, and what makes "this silently started working" a
    detectable event.

- Date: 2026-08-06
  Decision: STRUCTURAL async edits tested directly, not left as an open question. Async is safe for
    real work, with one pre-existing limit that is not async-specific.
  WHY THIS NEEDED ITS OWN PROBE: the suite grades a marker flip, which only changes a CONSTANT
    inside MoveNext. That says nothing about adding an await or a local, which change the state
    machine's FIELD SET and STATE COUNT - a different and much riskier patch, and the exact area
    that killed the Editor twice. New file Assets/InstaReload/Tests/AsyncShapeProbe.cs, own [ASYNC]
    log prefix so the result reads cleanly next to the suite's once-per-second line.
  MEASURED IN PLAY MODE, every edit made while running:
    * +1 string local living across an await, +1 await   -> seen=v2s     patched, no crash
    * +2 locals across awaits, +1 more await (3 total)   -> seen=v3sx    patched, no crash
    * reverted all the way back to the baseline shape    -> seen=v1      patched, no crash
      (so structural edits work in BOTH directions - removing awaits and locals too)
    * +1 INT local, string-concatenated                  -> REFUSED, loudly:
        "Missing field address access not supported: <stamp>5__1:System.Int32"
    Throughout: Editor alive, completions counter kept climbing (so no corrupted state machine),
    and the suite stayed 22/22 - no collateral damage.
  THE ONE REFUSAL IS NOT ASYNC-SPECIFIC, and the first run of this probe confounded the two. A new
    local becomes a new state machine field; `"x" + someInt` emits ldflda to reach Int32.ToString();
    IsFieldRewriteSupported refuses address access to any field missing from the runtime map, and
    every newly added field is missing by definition. Nothing in that path knows what async is. It
    fails SAFE - the old body keeps running - which is the correct outcome for a real limitation.
    LESSON, again: the first result looked like an async finding and was not. Isolate the variable
    before naming a cause. Rerunning with a STRING local turned the refusal into a pass.
  NOTE ON HOW THIS GOT TESTED: the probe, the edits and the log reads were all driven through
    mcp-unity rather than handed to Amritanshu as a manual checklist. Anything reachable by "write
    a script, run it, read the console, change it, read again" should be done directly - asking him
    to do it was the wrong call and he said so.

- Date: 2026-08-06
  Decision: STRUCTURED EVENT SINK - Library/InstaReload/events.jsonl. First step of the standing
    directive "nothing succeeds silently". Console keeps its one-line-per-reload contract; decisions
    are recorded as one JSON object per line for querying.
  WHY A FILE, NOT THE CONSOLE: Unity's console is a ring buffer and it LOST patcher output during
    this session - lines existed, then did not. It also cannot be joined or counted. A file can.
  WHY EVERY RECORD CARRIES A RELOAD ID: the suite once reported Boxed`1.BothAxes as patched while
    the patcher logged "NO instantiation patched" for the same method in the same reload. Both were
    speaking; nothing tied them together, so the contradiction took manual detective work. Reload id
    lives on ReloadTimeline, which is already created exactly once per reload - a second counter
    could drift out of step, which is the bug class being removed. Opened in the constructor on
    purpose so "records with no reload id" is unrepresentable.
  WHY REASON CODES, NOT SENTENCES: prose cannot be counted, and breaks when someone improves the
    wording. Stable codes in InstaReloadEvents.Reason; the sentence goes in `detail`.
  THREE SILENT SITES FIXED, found by auditing for swallowed exceptions:
    * GetFieldLookupKey swallowed a Resolve() failure and defaulted isStatic=false - a GUESS that
      flips half the key, misses the lookup, and falls through to a hot-assembly reference. That is
      exactly how the newobj bug worked. It still guesses (the key must say something) but the guess
      is now on record.
    * TryTrackTokenPair swallowed everything - losing a pair makes later diagnostics blind, so the
      blindness itself is now visible.
    * InheritsHotReloadBehaviour returned false on an unresolvable base chain: "could not determine"
      reported as "determined: no", silently downgrading an entry point.
  THE INSTRUMENT IMMEDIATELY CAUGHT ITSELF. First run: 94KB for ONE reload, 356 records, 100% benign
    (33x System.Object::.ctor(), 32x Type::GetTypeFromHandle) - the console-flooding failure that
    caused a wrong conclusion on 2026-08-05, faithfully rebuilt in a file. Plus 109 records with no
    reload id at all.
    FIXED by applying the directive's own escape clause - anything folded into a reported aggregate
    need not speak individually. Benign external fall-throughs are COUNTED into the reload summary;
    only a fall-through binding back into OUR assembly gets its own record, because only that one
    means something is wrong. Unscoped records are now marked `"unscoped":true` so they can never be
    read as reload #0.
    RESULT: 94KB -> 495 bytes for the same two reloads, zero orphans, suite still 22/22.
  A SECOND, INDEPENDENT CONFIRMATION FELL OUT OF IT: across 355 fall-throughs, every single one was
    external_assembly and ZERO bound back into our assembly. That is the generic retarget fix
    verified by a different instrument than the suite that drove it.
  The sink obeys its own rule: a write failure is reported to the console once per session rather
    than caught and ignored, because a sink that hides its own failure is the bug it exists to stop.

- Date: 2026-08-06
  Decision: THE LAST SILENT GAP IS CLOSED. Method removal, signature change, new type and new field
    are gradeable now, via Assets/InstaReload/Tests/StructuralProbe.cs plus structured records.
  WHY THEY WERE UNGRADEABLE: InstaReloadSuite grades a MARKER FLIP, which only alters a constant
    inside existing bodies. It can never remove a member or add one, so these four were hand-checked
    once each - February and August - and never since. Worse, the patcher reported them as console
    PROSE, and the new-type line was Verbose-only, i.e. invisible at default log level. Nothing could
    assert on any of it.
  WHAT CHANGED: each of the four now emits a structured record - method.removed, field.added,
    type.added, plus the removal record that a signature change produces for the old signature - so
    a probe can be graded from events.jsonl instead of by reading the console.
  MEASURED, Play Mode, each variant applied while running:
    A removal   -> method.removed StructuralProbe::Removable reason=removed_from_source;
                   [STRUCT] removable=(deleted), old body retained by design
    B signature -> [STRUCT] sig=23 (was 20), dispatched=2; method.removed for the OLD 1-arg form
    C new type  -> type.added Nimrita.InstaReload.Tests.AddedType; [STRUCT] type=added, so the new
                   type is constructible AND callable from a patched body
    D new field -> field.added reason=routed_to_field_store; [STRUCT] field=0
  NEW SEMANTIC FOUND BY VARIANT D, and it is worth knowing: a field added during Play Mode reads its
    DEFAULT, not its initializer, on any instance that already exists. Field initializers run in the
    constructor and an existing object's constructor does not re-run, so the field routes to
    HotReloadFieldStore starting at default(T). Objects created after the edit are fine. Not a
    defect - but completely invisible unless someone looks, which is the whole point of this work.
  WHY A PROBE RATHER THAN SUITE CASES: a marker flip is reversible and idempotent, so the suite can
    run it every second forever. These four change the SHAPE of the assembly and cannot be un-applied
    without another reload, so they are driven deliberately and graded from the event log.

- Date: 2026-08-06
  Decision: EDIT MODE PATCHING PROBED. Detours DO apply in Edit Mode. Measured, not reasoned about.
    [EDITPROBE] isPlaying=False before=ORIGINAL after=PATCHED => DETOUR APPLIES IN EDIT MODE
    [EDITPROBE] hook removed, Target now returns ORIGINAL
  So the mechanism works and is reversible: an ILHook installs, the new body runs, disposing it
    restores the original. No play-mode dependency in the detour itself.
  DELIBERATELY ISOLATED, because "does Edit Mode work" conflates two questions:
    1. does a detour take effect in Edit Mode      <- this probe, the actual unknown
    2. can Unity be stopped from recompiling       <- known engineering (kAutoRefreshMode)
    Testing them together would have made a negative result unattributable. The probe touches no
    file watcher, no suppression and no compile - it hooks a method directly and asks one question.
    Target is [MethodImpl(NoInlining)] so "no effect" could not be confused with "the call never
    happened", which is the ambiguity that cost four commits earlier in this project.
  WHAT THIS SETTLES: Edit Mode is not blocked by anything in the patching engine. It is blocked by
    OUR suppression design - a global AssetDatabase.DisallowAutoRefresh + LockReloadAssemblies that
    only works because Play Mode guarantees an exit. The commercial tool's patcher has ZERO
    play-mode branching and got Edit Mode for free; ours is play-mode-shaped by choice, not by
    necessity. Self-inflicted, and now proven so rather than suspected.
  REMAINING WORK IS PLUMBING AND UX, NOT RESEARCH: swap the global lock for the kAutoRefreshMode
    preference, own an explicit reconcile path, restore the preference if the tool or Editor dies
    (their own troubleshooting page tells users to restore it by hand, so this failure is real), and
    handle editor windows / custom inspectors being recreated on a domain reload. Days, not weeks -
    but note FastScriptReload has shipped its edit-mode support as EXPERIMENTAL for years, so the
    long tail is real.
  Probe kept at Assets/InstaReload/Editor/Diagnostics/EditModeProbe.cs behind two menu items.
