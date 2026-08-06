# Issues

Format: date / task / status

## Entries

- Date: 2026-02-22
  Task: Full codebase analysis — map pipeline, key files, performance numbers, limitations
  Status: Done

- Date: 2026-02-22
  Task: Set up project workflow (git, memory, dev process, multi-AI orchestration)
  Status: Done

- Date: 2026-02-22
  Task: New type support — Task 1: Modify IsCompatible to allow new types through validation
  Status: Done
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs

- Date: 2026-02-22
  Task: New type support — Task 2: Create HotTypeRegistry
  Status: Done
  Files: Assets/InstaReload/Runtime/HotTypeRegistry.cs, Assets/InstaReload/Editor/Core/UnityCompilationSuppressor.cs

- Date: 2026-02-22
  Task: New type support — Task 3: Register new types from hot assembly after Assembly.Load
  Status: Done
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs (RegisterNewTypes, BuildRuntimeMethodMap, AddMethodsFromTypes)

- Date: 2026-02-22
  Task: New type support — Task 4: Resolve new type references in IL cloning via HotTypeRegistry
  Status: Done
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs (ResolveRuntimeType)

- Date: 2026-02-22
  Task: New type support — Task 5: Handle new MonoBehaviour types via entry point system
  Status: Done
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs (RegisterMonoBehaviourEntryPoints, IsMonoBehaviourSubclass)

- Date: 2026-02-22
  Task: New type support — Task 6: Commit and document
  Status: Done
  Commit: fecb842 on dev

- Date: 2026-02-22
  Task: Fix build errors — CS0103 HotTypeRegistry not found, CS0433 BinaryPrimitives ambiguity
  Status: Done
  Files: Assets/InstaReload/Runtime/HotTypeRegistry.cs.meta (created), Assets/InstaReload/Editor/Roslyn/InstaReloadWorkerClient.cs (BinaryPrimitives → bit shifts)
  Commit: 4899cb4 on dev

- Date: 2026-02-22
  Task: New instance field + new static field on existing type — document, instrument, verify
  Status: Done
  Notes: Core IL rewriting was pre-existing. Added per-field logging to FieldSetsMatch, rewrite-count log per method in CloneMethodBody, full doc comment to TryRewriteFieldInstruction.
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs
  Commit: e8b6363 on dev

- Date: 2026-02-22
  Task: Fix fields-on-new-type bug — constructor/reader used different backing stores
  Status: Done
  Notes: new PlayerStats() wrote CLR fields via .ctor; ldfld in patched methods read from FieldStore (empty) → returned default(T). Fixed by including hot type fields in BuildRuntimeFieldMap so all access uses direct CLR, consistent with the constructor.
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs (BuildRuntimeFieldMap)
  Commit: 05b54e7 on dev

- Date: 2026-08-05
  Task: End-to-end latency instrumentation — measure every pipeline stage, not just compile
  Status: Done — verified in play mode (Unity 6000.3.10f1)
  Notes: Console only ever reported RoslynCompiler's own timing, so an 11ms compile could sit
    inside a multi-second felt latency with nothing to show where the time went. Patch time was
    already stopwatched but only fed the editor window, never printed.
  Files: Assets/InstaReload/Editor/Diagnostics/ReloadTimeline.cs (new),
    Assets/InstaReload/Editor/Diagnostics/InstaReloadSessionMetrics.cs (RecordTimeline + snapshot),
    Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs (probes + TrackChangedFile + ReportTimeline),
    Assets/InstaReload/Editor/UI/InstaReloadWindow.cs (End-to-End Timing card)

- Date: 2026-08-05
  Task: Fix the 94% bottleneck the instrumentation found — AppDomain reflection sweep in callbacks
  Status: Done — verified in play mode. Warm reload 7137ms -> ~140ms (51x). Replay 10401ms -> 201ms.
  Notes: TypeCache.GetMethodsWithAttribute + HotTypeRegistry.GetAll() replaces the full
    AppDomain -> types -> methods -> IsDefined sweep that ran twice per reload.
  Files: Assets/InstaReload/Editor/Core/HotReloadCallbackInvoker.cs (FindAttributedMethods),
    plus a history/callbacks probe split in ReloadTimeline + FileChangeDetector + InstaReloadWindow

- Date: 2026-08-05
  Task: Eliminate per-play-session cold start — persist the worker, adopt on reconnect, pre-heat at load
  Status: Done — verified in play mode. Warm reload avg 138ms. Play-mode replay 10401ms -> 4ms.
    Per-session cold start gone; ~600-800ms paid once per editor session in background.
  Notes: Lifecycle logging added on purpose, and it immediately caught a real defect — AssetImportWorker
    processes were connecting to the compile worker and could have starved the live editor.
  Files: Assets/InstaReload/Editor/Core/UnityCompilationSuppressor.cs (no kill on play exit),
    Assets/InstaReload/Editor/Roslyn/InstaReloadWorkerClient.cs (adopt-then-spawn, per-project port,
    warmup, lifecycle logs), Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs (import-worker
    guard, pre-heat), Tools/InstaReloadWorker/Program.cs (idempotent init, project binding,
    concurrent clients), Assets/InstaReload/Editor/UI/InstaReloadWindow.cs (port tooltip)

- Date: 2026-08-05
  Task: Kill the `queue` cost — cache file→assembly lookup and worker compile context per domain
  Status: Done — verified in play mode. queue 69ms -> 0ms. Fast path 138ms -> 58ms.
  Notes: Split the `queue` probe into `assembly` + `queue` BEFORE fixing, which showed
    CompilationPipeline.GetAssemblies() alone costs 119ms cold. Worth remembering as a pattern:
    measure the split first, then fix, so attribution is never guessed.
  Files: Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs (_assemblyNameByFile cache),
    Assets/InstaReload/Editor/Roslyn/InstaReloadWorkerClient.cs (GetOrBuildContext),
    Assets/InstaReload/Editor/Diagnostics/ReloadTimeline.cs (assembly probe),
    Assets/InstaReload/Editor/UI/InstaReloadWindow.cs (row)

- Date: 2026-08-05
  Task: Instrument `patch` sub-phases, then cut the Cecil cost
  Status: Done — verified in play mode. cecil 41ms -> 29ms. Total ~59ms (was ~58ms, within noise).
  Notes: The sub-phase split was the valuable part — it disproved two of my guesses. Assembly.Load
    costs 1ms (predicted "significant"); map building costs ~1ms (predicted bad on big projects —
    still unproven either way, this project has one user class); Cecil's file read was the real
    cost at ~75% of patch. LESSON: on this stage my estimates were optimistic twice running.
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatchTypes.cs (PatchPhaseTimings),
    Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs (phase probes, Deferred, shared resolver),
    Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs (patch breakdown log line)

- Date: 2026-08-05
  Task: FEATURE — allow method removal and signature change during Play Mode
  Status: Done — all four cases verified in play mode, plus the safety-net fix.
  Notes: Removal is no longer a blocker; the removed method simply keeps its original body.
    Signature change works for free through the existing new-method dispatcher.
    Testing also exposed a pre-existing ChangeAnalyzer bug that silently skipped the removal
    warning — fixed by re-baselining signatures from disk on play mode enter.
    METHODOLOGY NOTE: Test 4 took four attempts and the CODE was correct every time. Failures
    were all harness artifacts — component not attached, cross-file type reference the
    single-file compiler couldn't resolve, Unity Console "Collapse" hiding repeated logs, and a
    stale analyzer baseline. Verify the measurement before doubting the code.
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs (CompatibilityResult,
    MethodSetsMatch, removal warning), Assets/InstaReload/Editor/Core/ChangeAnalyzer.cs
    (RefreshFromDisk), Assets/InstaReload/Editor/Core/UnityCompilationSuppressor.cs (call it),
    Assets/InstaReload/Runtime/TestInstaReload.cs + TestInstaReloadCaller.cs (4-case fixture)

- Date: 2026-08-06
  Task: Fix the regression suite's validity flaw (44eaad2) before any further generic work
  Status: Done - verified in Play Mode, clean session, two hot-reload generations
  Notes: Graded observation now goes through reflection on the runtime type, so a PASS means the
    METHOD was patched rather than the call site producing a value some other way. Direct call kept
    as a cross-check; a disagreement prints a LEAK line. Also added NoInlining to every case and
    made every case read instance state, as 44eaad2 asked - necessary but not sufficient by itself.
    Baseline 22/22, M0->M1 22/22, M1->M2 22/22, no LEAK in any clean run.
    The commit's inlining diagnosis did NOT hold up: inlining an unpatched method yields the OLD
    marker, so it cannot fake a patch. The one reproduction was in a contaminated multi-generation
    session and was off by exactly one generation. See decisions.md - undiagnosed, now visible.
  Files: Assets/InstaReload/Tests/InstaReloadSuite.cs

- Date: 2026-08-06
  Task: Merge feature/generic-methods into dev
  Status: Done - merged as 1fafefa, verified in Play Mode (baseline 22/22, after flip 22/22)
  Notes: The branch tip's "generics regressed" verdict was a harness artefact, not a product bug -
    the old suite's one-line cases were inlined, so it read unpatched bodies and reported failure.
    Generic methods, generic classes, multiple type params, where T : struct and nested generic
    args all patch. Generic METHOD on a generic TYPE is still refused (loudly). Nine suite
    expectations flipped Stale -> Patched. Two further harness holes closed: object-origin checking
    (HOT-OBJECT) and Awake-constructed generic targets.
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs,
    Assets/InstaReload/Tests/InstaReloadSuite.cs

- Date: 2026-08-06
  Task: Fix the two remaining generic call-site bugs (hot-copy calls, one-generation lag)
  Status: Done - a5499bd, verified in Play Mode across three generations plus the newobj check
  Notes: They were ONE bug, and the same one as the newobj fall-through: a generic method reference
    stayed bound to the assembly just compiled instead of being rebuilt against the runtime type.
    NeedsRuntimeRetarget now covers generic instantiations of our types AND generic methods on our
    types, every opcode; external generics excluded. Every LEAK line is gone - gen1 and gen2 were
    1 and 10 before. The suite header now says any leak at all is a regression.
    Two dead ends recorded in bugs.md: declaring-type-only retarget (made it worse, six new leaks)
    and per-reference-type instantiation hooking (green, but A/B showed it was not load-bearing).
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs

- Date: 2026-08-06
  Task: Generic method on a generic type (Boxed<T>.BothAxes<U>) - the last refused generic shape
  Status: Done - verified in Play Mode, baseline + two generations 22/22, zero LEAK lines
  Notes: Two defects stacked. The declaring type was constructed but the METHOD stayed open, so
    ILHook refused it; fixed by closing the method too and hooking the (type args x method args)
    cross product with both substitution channels passed together for the first time. That exposed
    the second: the call-site harvest key spelled the parameter "!!0" where the definition said "U",
    because Cecil leaves a constructed reference's generic parameter unnamed and Name falls back to
    position. GetInstantiationKey now keys positionally. Fourth Cecil-vs-reflection key mismatch.
    BothAxes reports 4 instantiations; its expectation flipped Stale -> Patched. async is the only
    Stale case left.
  Files: Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs,
    Assets/InstaReload/Tests/InstaReloadSuite.cs

- Date: 2026-08-06
  Task: Async/await hot reload - the last documented limitation the suite could grade
  Status: Done - verified in Play Mode, baseline + two generations 22/22, no crash, no errors
  Notes: Root cause was one line in the worker - Release emit on the slow path made the async state
    machine a STRUCT where Unity's Debug build makes it a CLASS. Debug on both paths now. Proved the
    shape change with async still refused (zero risk): the "base class changed" line and the phantom
    removed .ctor both vanished, and the .ctor came back as a real method.
    The backlog's plan - patch only the OUTER method - is a NO-OP and was measured as one: the
    compiler puts essentially all user code in MoveNext. MoveNext had to be allowed, and it works.
    Side effect: slow path ~750ms -> ~270-410ms, which answers the separate "try Debug emit" item.
    The suite now has ZERO Stale cases.
  Files: Tools/InstaReloadWorker/Program.cs, Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs,
    Assets/InstaReload/Tests/InstaReloadSuite.cs

## Improvement Backlog

- LATENCY WORK IS DONE (2026-08-05). 7365ms -> ~59ms, 125x. Noise floor reached: identical work
  varies 46-70ms run to run. Do NOT keep optimising here — risk without perceptible gain.
- Only untried latency idea, low confidence: skip the temp file entirely. Compiled bytes are in
  memory, get written to disk, read back by Cecil, then read AGAIN by Assembly.Load. Might take
  cecil 29ms -> ~10ms. Weigh against the fact that nobody can feel 59ms vs 40ms.
- THE REAL NEXT WINS ARE CAPABILITY, NOT SPEED. Each of these forces a play-mode exit, and one
  exit costs more than hundreds of saves' worth of the optimisation done today:
  - No Edit mode support
  - Generic methods/classes: SOLVED on feature/generic-methods (40ba1fe, c215d84, d6ee90b),
    NOT merged. On dev they still do not patch but WARN loudly. Remaining before merge:
    delete scaffolding (CloneTarget, GenericContainer, GenericCloneProbe); test multiple type
    parameters, `where T : struct`, nested generic args, and a generic METHOD on a generic
    TYPE (both axes at once — most likely to break, never exercised).
  - [superseded note kept for history] Generic methods still do not patch — they WARN and name the
    method (commit 1f5810f), so the failure is visible instead of looking like success.
    SUPPORT is not built. Route A if the warning proves frequent: extend
    HotReloadDispatcher.Invoke with Type[] typeArgs + MakeGenericMethod, reroute generic call
    sites in patched methods. Days of work, touches the dispatch table, and unpatched callers
    still run old bodies. Deliberately deferred to let the new warning MEASURE how often this
    actually happens before spending the days.
  - Base class / interface changes: SILENTLY IGNORED, not broken. Verified 2026-08-05 in Play
    Mode. CheckCompatibility never looked at BaseType/Interfaces, so the reload reported
    "patched 5, dispatched 2, trampolines 1" while runtimeType.BaseType and `is IFoo` stayed
    exactly as before. The CLR fixes a type's hierarchy at load; method-body patching cannot
    change it. Now WARNS (both cases) — applying it is not possible without a domain reload.
    WORST PART, and why this outranked generics: a new interface METHOD does get registered for
    dispatch, so it is callable, while `obj is IFoo` stays false. GetComponents<IDamageable>()
    and every is/as cast silently skip the object — an extremely common Unity pattern.
    NOT covered: interface REMOVAL is not warned (runtime type keeps implementing it, so
    `is IFoo` stays true when the source says otherwise — wrong, but extra capability rather
    than missing). The check lives in CheckCompatibility = slow path only; correct today since
    inheritance changes are always structural, but it would go quiet if the analyzer ever
    mis-routed one to the fast path.
  - No field add/remove on existing types beyond the FieldStore cases
  - Cross-file changes need multiple cycles
  - (Method removal / signature change: SHIPPED 2026-08-05, no longer a blocker — see above)
- RESOLVED 2026-08-05: Worker lifecycle logs demoted to Verbose (persistence proven).
- RESOLVED 2026-08-05: all three CS0618 deprecations cleared. GetScriptingDefineSymbolsForGroup →
  GetScriptingDefineSymbols(NamedBuildTarget), guarded on BuildTargetGroup.Unknown because
  NamedBuildTarget.FromBuildTargetGroup THROWS there where the old overload returned "".
  Both FindObjectsOfType → FindObjectsByType(..., FindObjectsSortMode.None).
- WATCH (Unity API churn, not yet actionable on 6.3): FindObjectsSortMode is itself deprecated —
  every overload taking it is obsolete as of 6.6 docs, because InstanceID is being replaced by
  EntityId and "previous sort order cannot be maintained". The sortMode-less overloads DO NOT
  EXIST on our 6000.3.10f1, so None is the closest we can get today; the eventual migration is
  deleting the argument. Underlying timeline (DaxodeUnity, Unity Discussions): 6.2 added
  GetEntityId(), 6.3-6.4 int versions obsolete-with-warning, 6.5 those warnings become ERRORS.
  InstaReload uses no GetInstanceID/InstanceID/EntityId anywhere, so the 6.5 wave only reaches us
  through these two FindObjects call sites.
- RESOLVED 2026-08-05 (1f5810f): the debounce was only HALF dead. The TrackChangedFile stamp ran
  on the watcher's background thread (dead, removed); the ProcessChangedFiles/RequeueFile stamps
  run from OnEditorUpdate on the main thread and DO work — they are a retry backoff for files the
  editor is still writing. Deleting the whole mechanism would have made InstaReload spin on locked
  files. Renamed to _lastRetryQueuedTime / RetryBackoffSeconds; timeline DebounceMs -> WaitingMs.
  Do NOT "restore" a typing debounce: it would add 300ms to a ~59ms pipeline.
- TOOLING 2026-08-05 (d851233): mcp-unity installed — the Editor console and recompile are now
  readable from the agent side, so verification no longer depends on pasted logs. Requires Unity
  OPEN with the server started (Tools > MCP Unity > Server Window). Do not trigger a refresh while
  Play Mode suppression is active. .mcp.json embeds the PackageCache git hash and breaks on package
  update — regenerate from the Server Window.
- MEASUREMENT LESSON (repeat offender — THREE times on 2026-08-05 alone). Verify the measurement
  before doubting the code:
    1. Console flooding at ~180 logs/sec evicted InstaReload output from Unity's ring buffer.
    2. Grepping a .unity file for a class NAME finds nothing — scenes reference scripts by GUID.
    3. WORST: probing with `this is IFoo` produced a FALSE POSITIVE ("interface changes work!").
       That expression is statically decidable, so Roslyn folds it to a constant — it measured the
       COMPILER, not the runtime type. A CS0184 warning exposed it. Rewriting through an
       object-typed local (forces isinst) plus GetType().BaseType reversed the conclusion.
       RULE for future probes: never assert on an expression the compiler can fold. Route through
       `object`/reflection so the check happens at runtime.
  Also: `strings` finds nothing in Unity-built .dll files — use grep on the binary instead.
- TOOLING GOTCHA: mcp-unity `recompile_scripts` does NOT pick up newly CREATED files — it reports
  success having silently ignored them. Run Assets/Refresh (execute_menu_item) first, then
  recompile. Symptom: no .meta generated and the type is absent from the built assembly.
- Slow path bottleneck: Release emit ~750ms — evaluate using Debug emit on slow path too
- Signature hashing: text-based, not AST — comments/whitespace may trigger slow path incorrectly
- No Edit mode support
- New type/field support — DONE (new types: Tasks 1-6; new fields on existing types: pre-existing infra, documented + instrumented)
- Cross-file change scope: each file compiled independently
