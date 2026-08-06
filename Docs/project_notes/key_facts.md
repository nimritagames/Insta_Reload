# Key Facts

## Project
- **Name:** InstaReload (Hot Reload for Unity)
- **Type:** Unity Editor Package
- **Runtime:** Mono (IL2CPP not supported)
- **Unity Mode:** Play mode only (Edit mode not supported)
- **Assets path:** `Assets/InstaReload/`

## Performance Targets
- Fast path (method body change): ~30-50ms total
- Slow path (structural change): ~270-410ms (was ~750ms; Debug emit on both paths, 2026-08-06)
- Roslyn init (once at startup): ~150ms
- Debounce delay: 300ms
- Baseline Unity domain reload: 3-30 seconds

## Observed Performance (Unity 6000.3.10f1, 2026-08-05)
The targets above were measured on Unity 2022.3.62f2. After the Unity 6 upgrade:
- Fast path compile, warm: 11-15ms (was ~7ms — Unity 6 references more assemblies)
- Slow path compile, cold: ~1070ms | fast path compile, cold: ~987ms
- **Roslyn is cold once per PLAY SESSION, not once ever.** Exiting play mode triggers a domain
  reload that wipes statics, so the first reload of every session pays ~1s regardless of the
  signature cache being warm. Proof: Parse 116ms/Emit 840ms on the first compile vs 1ms/10ms
  on the second, same file, same session.
- `Library/InstaReloadSignatureCache.dat` lives in Library/, so any Library rebuild (Unity
  upgrade, Reimport All) resets every file to FirstAnalysis → one slow path per file.
- COMPILE TIME IS NOT END-TO-END TIME. Use the `[Timing]` console lines (see below).

## Current Performance (2026-08-05, AFTER Cecil fix) — VERIFIED IN PLAY MODE
Warm fast-path reload: **46-70ms (avg 59ms)**, from 7365ms at session start. **125x.**
Breakdown avg: debounce 1 | analyze 4 | assembly 0 | queue 0 | compile 5 | pickup 2 |
**patch 41** | history 3 | callbacks 0 | unaccounted 1
Patch sub-phases avg: **cecil 29** | validate 0 | load 1 | maps 1 | hooks 9

STOP POINT. Run-to-run spread is 46-70ms for identical work — disk/OS scheduling noise now
exceeds anything left to win. Two consecutive optimisation estimates for this stage were too
optimistic (predicted cecil "single digits", got 29ms; predicted total 30ms, got 59ms), which is
itself evidence there is no good model left of where the remaining time goes.

Remaining untried idea (NOT done, expected value uncertain): the compiled bytes are already in
memory, then get written to a temp file, read back by Cecil, and read AGAIN by Assembly.Load.
One write + two reads of data already in RAM. Passing bytes straight through might take cecil
29ms -> ~10ms. Guess, not forecast.

## Previous milestone (AFTER queue fix)
Warm fast-path reload: **58ms** (single sample this run), from 7365ms at session start. **127x.**
Breakdown: debounce 1 | analyze 2 | assembly 0 | queue 0 | compile 6 | pickup 0 |
**patch 44 (76%)** | history 3 | callbacks 0 | unaccounted 2
Slow path (signature change): 234ms — compile 29ms (was 298ms; worker stays warm).
`patch` is now the only thing left worth attacking; everything else combined is ~14ms.
NOTE on cold numbers: `assembly` 119ms and patch-replay 190ms are FIRST-call-after-domain-reload
costs (CompilationPipeline and MonoMod cold respectively), paid once per domain, not per save.

## Previous milestone (AFTER worker persistence + pre-heat)
Warm fast-path reload: **128-146ms (avg 138ms)**, from 7137ms originally. **52x.**
Breakdown avg: debounce 1 | analyze 4 | **queue 69** | compile 11 | pickup 2 | **patch 48** |
history 3 | callbacks 0 | unaccounted 2
Slow path (signature change, Release emit): ~690ms — compile 298 + patch 258.
Patch replay on Play-mode entry: **4ms**, from 10401ms.
**Per-play-session cold start is GONE.** The worker is spawned + warmed once per EDITOR session
at load (~600-800ms, in background, off the critical path); every play session after that adopts
it warm. Adoption costs ~6-9ms.
Remaining budget: `queue` 69ms (50%) + `patch` 48ms (34%) = 84%.

## Previous milestone (AFTER the TypeCache fix, BEFORE worker persistence)
Warm fast-path reload: **135-144ms end to end** (avg ~140ms), from 7137ms. **51x faster.**
Breakdown avg: debounce 2 | analyze 2 | queue 78 | compile 10 | pickup 5 | patch 39 |
history 3 | **callbacks 0** | unaccounted 2
First reload of a play session: 1080ms (cold Roslyn — compile 866ms; everything else warm).
Patch replay on Play-mode entry: **201ms**, from 10401ms.
Remaining budget is now just two stages: `queue` 78ms (56%) + `patch` 39ms (28%) = 84%.

## Measured Baseline (2026-08-05, 4 warm fast-path reloads, BEFORE the fix)
Averages: **TOTAL 7365ms** | debounce 4 | analyze 5 | queue 97 | compile 266 (warm 10) |
pickup 2 | **post-patch 6948 (94.3%)** | unaccounted 2
Patch replay on Play-mode entry: 10401ms for ONE cached record.
Root cause of both: `HotReloadCallbackInvoker.FindAttributedMethods` walks
`AppDomain.CurrentDomain.GetAssemblies()` → `GetTypes()` → `GetMethods()` → `IsDefined(inherit:true)`
and is called TWICE per reload (global + local attribute). ~3.5s per full sweep in the Unity 6
Editor AppDomain. Compile/patch/debounce are all noise by comparison.
REFUTED by this data: the console log flood was NOT the bottleneck — `pickup` averaged 1.5ms,
so the main thread was never starved.
ALSO FOUND: the 300ms debounce never actually waits (measured 0-12ms). `_lastChangeTime` is
assigned `EditorApplication.timeSinceStartup` from the FileSystemWatcher's background thread,
where the value is 0/stale, so `timeSinceLastChange` is always huge and the window is always
already satisfied.

## End-to-End Timing Stages (ReloadTimeline)
Printed per reload as `[InstaReload] [Timing]`, and shown in the window's End-to-End Timing card:
1. `debounce` — first watcher event (T0) → debounce window satisfied (300ms floor; higher if a
   save fires multiple watcher events, since each one restarts the window)
2. `analyze` — file read + ChangeAnalyzer signature hash
3. `assembly` — file → owning assembly lookup (CompilationPipeline; cached per domain)
4. `queue` — job enqueued → compile actually starts (includes external-worker connect wait)
5. `compile` — Roslyn (the only number the console used to show)
6. `pickup` — compile finished → main thread's EditorApplication.update poll picked it up
7. `patch` — InstaReloadPatcher.ApplyAssembly (Assembly.Load + Cecil + MonoMod hooks)
8. `history` — patch history persistence
9. `callbacks` — [InvokeOnHotReload] dispatch
Plus `unaccounted` (total minus the sum — if large, a real cost has no probe yet) and
`watcher events` (>1 means the debounce window slid).
Patch replay on play-mode entry is timed separately and logged as its own `[Timing]` line.

## Pipeline Stages
1. FileSystemWatcher → FileChangeDetector (bg → main thread, debounce 300ms)
2. ChangeAnalyzer → SHA256 signature hash → fast or slow path decision
3. RoslynCompiler → Debug emit (6ms fast) or Release emit (722ms slow)
4. InstaReloadPatcher → MonoMod ILHook (IL clone + apply)
5. HotReloadCallbackInvoker → [InvokeOnHotReload] / [InvokeOnHotReloadLocal]

## Key Files
- `Assets/InstaReload/Editor/Roslyn/FileChangeDetector.cs` — orchestrator, entry point
- `Assets/InstaReload/Editor/Core/ChangeAnalyzer.cs` — fast/slow path decision engine
- `Assets/InstaReload/Editor/Roslyn/RoslynCompiler.cs` — C# compilation
- `Assets/InstaReload/Editor/Core/InstaReloadPatcher.cs` — IL patching via MonoMod
- `Assets/InstaReload/Editor/Core/UnityCompilationSuppressor.cs` — blocks Unity compile
- `Assets/InstaReload/Editor/Roslyn/InstaReloadWorkerClient.cs` — external worker TCP client
- `Assets/InstaReload/Editor/Settings/InstaReloadSettings.asset` — modified (git status)
- `Library/InstaReloadSignatureCache.dat` — signature hash cache (survives domain reload)

## Configuration (InstaReloadSettings.cs)
- Enabled toggle
- VerboseLogging / EnabledLogLevels / EnabledLogCategories flags
- UseExternalWorker / AutoStartWorker / WorkerPort (default 53530)
- AutoApplyPlayModeSettings

## External Worker
- Project: `Tools/InstaReloadWorker/InstaReloadWorker.csproj`
- Target: `.NET 8.0`
- Protocol: TCP JSON (4-byte length header + UTF-8 payload)
- Port: 53530 (default)
- Messages: Init → InitResponse → Compile → CompileResult

## What Hot Reloads (verified 2026-08-05 — the old list here was WRONG)
WORKS:
- Method body edits (the main case, ~59ms)
- Unity lifecycle methods: Awake/Start/Update/FixedUpdate/LateUpdate/OnEnable/OnDisable/OnDestroy
  get trampolines, plus a fallback proxy for methods added mid-session
- ADDING new methods — callable from any method that is itself patched (routed via
  HotReloadDispatcher). Old note claiming "new methods aren't callable" was stale.
- ADDING new types (classes/structs), incl. compiler-generated closures/async state machines
- ADDING fields to existing types (via HotReloadFieldStore); fields on new types use direct CLR
- REMOVING a method — allowed since 2026-08-05, see decisions.md
- CHANGING a method signature — allowed since 2026-08-05; reads as remove-old + add-new

- FIELDS OF GENERIC TYPE (List<int>, Dictionary<K,V>, Func<T>) — fixed 2026-08-05 (b47b42e).
  Before that they read back null/default inside any patched method, silently.
- METHODS WITH GENERIC PARAMETERS (void Foo(List<int>)) — same fix.

REPORTED, NOT APPLIED (warns and names the method — never silent since 2026-08-05):
- Generic methods and methods on generic types — on `dev`. SUPPORTED on
  feature/generic-methods, see below.
- Base class changed, or an interface ADDED, on an existing type. The CLR fixes a type's
  hierarchy at load; `is`/`as` and GetComponents<T>() keep skipping the object.
- ASYNC/AWAIT methods — refused deliberately (0e910e9). Their state machine cloned to
  invalid IL and crashed the Editor with StackOverflowException. Iterator state machines
  are fine; coroutines patch correctly, including ALREADY-RUNNING ones.

ON feature/generic-methods ONLY (3 commits, not merged):
- Generic METHODS and generic CLASSES both hot reload. One hook on the reference-type
  instantiation covers every reference-type use (Mono shares that native body) — including
  types never used before the edit. Value-type instantiations each need their own hook and
  are harvested from the edited assembly's IL.
- Value type with NO call site in the assembly stays stale, and says so.
- typeof(T) inside the shared reference body reports System.Object, not the caller's type.
  Inherent to the sharing, not a bug. Code branching on typeof(T) diverges from a real compile.

SILENTLY SKIPPED (that method doesn't update, rest of the reload still applies — no error):
- `ldflda`/`ldsflda` on a NEW field (FieldStore has no stable address)
- Calls to new methods with ref/out/pointer params, or new methods on structs

STALE, NOT BROKEN:
- A removed/renamed method still called by an UNEDITED file keeps running its OLD body until
  Play Mode exits. Unity's own compile then reports CS1061 — that is the backstop.

GENUINELY IMPOSSIBLE:
- IL2CPP (AOT, no JIT to hook)
- Static initializer values for NEW static fields on existing types (.cctor already ran)

NOT BUILT YET (all feasible):
- Edit mode support
- Cross-file changes in one cycle (compilation scope is ONE FILE)
- An edited file can only reference types present in the last assembly Unity built SUCCESSFULLY.
  Add a type in a new file and hot-reload another file that uses it → CS0246, patch skipped.
- Changing a base class / interfaces: UNTESTED, assume broken. CheckCompatibility only compares
  fields and methods, never the inheritance chain.
- Text-based signature parsing (not AST — comments can affect slow/fast path decision)

## Branches
- `main` — production-ready
- `dev` — active development
- `Way-1` — experimental branch (purpose TBD)
