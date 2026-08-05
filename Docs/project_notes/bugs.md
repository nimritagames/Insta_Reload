# Bugs

Format: date / issue / cause / fix

## Entries

- Date: 2026-02-22
  Issue: CS0103 'HotTypeRegistry' does not exist in current context (4 locations in Editor assembly)
  Cause: HotTypeRegistry.cs was created outside Unity with no .meta file. Unity never imported it, so the generated Editor.csproj excluded it entirely.
  Fix: Created HotTypeRegistry.cs.meta manually using MonoImporter format with a fresh GUID. Unity will import the file on next open and regenerate the csproj correctly.

- Date: 2026-02-22
  Issue: CS0433 'BinaryPrimitives' exists in both System.Memory and mscorlib (pre-existing)
  Cause: Unity's csproj references both assemblies. Both define System.Buffers.Binary.BinaryPrimitives with the same full name — fully qualifying doesn't resolve the ambiguity because the name itself is duplicated. extern alias would require csproj changes Unity regenerates.
  Fix: Replaced both BinaryPrimitives calls in InstaReloadWorkerClient with manual little-endian bit shifts. Identical behavior, zero ambiguity, no imports needed.

- Date: 2026-02-22
  Issue: Fields on new types (e.g. PlayerStats.health) returned default(T) instead of initializer values
  Cause: BuildRuntimeFieldMap only scanned the original runtimeAssembly. New types (PlayerStats) weren't in it, so their fields were absent from context.RuntimeFields. TryRewriteFieldInstruction treated those fields as "missing" and routed all ldfld/stfld to HotReloadFieldStore. Meanwhile, 'new PlayerStats()' calls the hot assembly's .ctor natively via newobj (not rewritten) — writing CLR field slots directly. Constructor wrote to CLR; reads went to FieldStore (empty). Returned 0 instead of 100.
  Fix: BuildRuntimeFieldMap now also iterates HotTypeRegistry.GetAll() and adds hot type fields to the map. Those fields are then passed through as direct CLR access (no FieldStore rewrite), consistent with how the constructor sets them. Existing types with new fields are NOT in HotTypeRegistry and continue to use FieldStore correctly.

- Date: 2026-08-05
  Issue: FIXED 2026-08-05 (was HIGH SEVERITY). Every GENERIC-TYPED FIELD read as null/default inside any patched
    method. List<T>, Dictionary<K,V>, HashSet<T>, Queue<T>, Func<>, Action<>, UnityEvent<> — all
    affected. Non-generic fields are fine.
  Repro: field set in Awake, read in Update, then edit ANY method body in that file during Play
    Mode. Observed: "plainInt=4242 | GENERIC-LIST-IS-NULL | LAMBDA-IS-NULL". Only the lambda body
    was edited; the untouched List<int> broke too — it is the field TYPE that matters, not the edit.
  Cause: the two GetFieldKey overloads disagree for generics (InstaReloadPatcher ~2665-2673).
    Cecil  : NormalizeTypeName(field.FieldType.FullName) -> List`1<System.Int32>
    Reflect: GetTypeName(field.FieldType)                -> List`1[[System.Int32, mscorlib, Version=...]]
    Angle-bracket/unqualified vs square-bracket/assembly-qualified. Keys never match, so the field
    is classified NEW, its ldfld/stfld are rewritten to HotReloadFieldStore, and the store is empty
    because the live value sits in the real CLR field written by unpatched code -> null/default.
    Same failure class as the 2026-02-22 PlayerStats bug (05b54e7); that fix covered new-type
    fields and left this path.
  Why it is worse than a crash: SILENT. No warning. FieldSetsMatch runs on the SLOW path only, so
    an ordinary body edit skips validation entirely. It corrupts observed state rather than
    failing, so the user debugs their own code first.
  Fix: new TypeKeyName.For(Type) in InstaReloadPatchTypes.cs, now the single source of truth for
    both InstaReloadPatcher.GetTypeName(Type) and HotReloadCallbackInvoker's duplicate. Rebuilds
    the name STRUCTURALLY from the generic definition + arguments (producing Cecil's shape by
    construction, not by string-munging reflection output), recursing through arrays, byref and
    pointers so a generic element type (List<int>[], ref List<int>) cannot slip through.
    Non-generic names are byte-identical to before, so nothing that already matched can regress.
    Centralised deliberately: two copies that must agree byte-for-byte WAS the defect.
  ALSO FIXED by the same change: methods with generic-typed PARAMETERS (void Foo(List<int>))
    mismatched identically via GetMethodKey, so they were treated as new instead of patched.
  Verified in Play Mode: "plainInt=4242 | listCount=3 | dictCount=2 | lambda PATCHED |
    param PATCHED(3)" — every generic field and a generic parameter survive a patch, and the code
    actually reloaded (ORIGINAL -> PATCHED) with frames climbing. 0 errors, 0 warnings.
  Checked and safe: HotReloadBehaviour.GetMethodId builds keys only for parameterless void methods
    on non-generic types, so it needs no change.

- Date: 2026-08-05
  Issue: ROOT-CAUSED 2026-08-05, OPEN. Recursive dispatch -> StackOverflowException -> native crash. Editor froze
    (PID Responding=False, force-quit required, unsaved work lost).
  Repro: probe with `while(true) { ...; yield return new WaitForSeconds(0.25f); }` started in Awake,
    patched during Play Mode. Six constructs were patched in one save, so attribution is NOT proven
    — the coroutine is the prime suspect only because it is the sole unbounded loop.
  Hypothesis: patching the compiler-generated iterator MoveNext corrupts the state machine so it no
    longer suspends at the yield; Unity's scheduler then calls MoveNext forever on the main thread.
  RESULT 2026-08-05, after the generic field-key fix (b47b42e): coroutines PATCH CORRECTLY and did
    NOT hang. Isolated probe with a bounded guard (counter as a STATIC on the MonoBehaviour, not an
    iterator local, so a corrupted state machine cannot reset it):
      [Coro] ongoing PATCHED | fresh PATCHED | iterations=132   (4/sec, guard never tripped)
    An ALREADY RUNNING iterator picked up the patch — better than commercial Hot Reload, which
    documents ongoing coroutines as not updating. Freshly started iterators patch too.
  STATUS: the hang is UNREPRODUCED, not proven fixed. Leading hypothesis is that the generic
    field-key bug caused it (present then, fixed now) — the hang probe also held a Func<> field and
    an async Task. Do NOT record this as solved. If it ever recurs, the bounded-guard probe above
    is the tool: iterations exploding + GUARD-TRIPPED means a corrupted state machine.
  STILL UNVERIFIED: property getters, event add/remove accessors, async methods. They were in the
    batch wiped out by the NRE, so nothing was learned about them. That NRE was very likely the
    generic field bug, so they may already be fine — cheap to check.

- Date: 2026-08-05
  Issue: FIXED 2026-08-05 (0e910e9). ASYNC STATE MACHINE -> invalid IL -> recursive dispatch -> crash. Patching a file where Update() ends
    up BOTH trampolined AND dispatched produces infinite recursion -> StackOverflowException ->
    native crash in the mono runtime -> Editor hangs, force-quit required, unsaved work lost.
  Evidence (Editor.log, not guessed):
      Native Crash Reporting / Got a UNKNOWN while executing native code
      exception inside UnhandledException handler: ... type:StackOverflowException
    and the cycle in the final stack:
      [Dispatcher] Invoked -981957947
      HotReloadDispatcher:Invoke (object,int,object[])
      (dynamic) InstaReloadPatcher:InstaReloadTrampoline_CapabilityProbe_Update
      (dynamic) DynamicMethodDefinition:SyncProxy<void CapabilityProbe:Update()>
    Reload line: "patched 25, dispatched 2, trampolines 2" — Update got a trampoline AND a
    dispatch entry, and they call each other.
  NOT a spin/infinite loop. My bounded-iteration guard was useless because nothing loops — it
    recurses. Wrong hypothesis, wrong guard; the log settled it in one read.
  REPRO: six constructs in one file (lambda + property getter + event add accessor + async Task +
    ongoing coroutine + fresh coroutine), all patched in one save. Reproduced twice.
  IMPORTANT — the generic field-key fix (b47b42e) UNMASKED this; it did not cause it. Previously the
    null lambda threw an NRE early in Update, aborting each tick before recursion could build. That
    NRE was accidentally protective. With the lambda working, execution reaches the cycle.
  RULED OUT: coroutines alone (patch fine, incl. already-running iterators); the generic field bug.
  ACTUAL CAUSE (my trampoline/dispatch hypothesis was WRONG): the async state machine MoveNext
    cloned to INVALID IL - "IL_0047: call 0x00000011", a raw unremapped metadata token. The patch
    reported success and installed the broken method; the next dispatch through Update recursed to
    stack exhaustion. Trampoline+dispatch together is BY DESIGN for Unity entry points and is fine.
    The dispatcher's lookup-miss path is also fine (warns, returns null, no fallback recursion).
  FIX: IsMethodBodySupported refuses any method whose declaring type implements IAsyncStateMachine,
    so the corrupt IL is never installed. Keyed on the interface, NOT the MoveNext name - ITERATOR
    state machines also have MoveNext and clone correctly.
  ALSO: deliberate refusals no longer print "Failed to patch N method(s)" in red; they are warnings
    under "N method(s) NOT patched (unsupported construct)". Real failures still error.
  NOTE: b47b42e (generic field fix) UNMASKED this, did not cause it. The previously-null lambda
    threw an NRE early in Update and aborted each tick before recursion could build.
  VERIFIED on the exact six-construct repro: Editor survives, Responding=True.
  NEWLY CONFIRMED WORKING as a side effect: property getters, event add/remove accessors, lambdas.
  STILL OPEN: in that six-construct combination the FRESHLY STARTED coroutine stayed ORIGINAL even
    though it patched in isolation, and NO skip was reported for it. Unexplained, not a crash.
  METHODOLOGY: Editor.log (and Editor-prev.log after a restart overwrites it) is the tool that
    cracked this. Console output and process state could only ever say THAT it froze. Reach for the
    log first on any hang/crash. Note Unity overwrites Editor.log on start - the crashed session
    lives in Editor-prev.log.

- Date: 2026-08-06
  Issue: After a hot reload, `new SomeGeneric<T>()` inside a PATCHED method constructs an instance
    of the HOT assembly's type, not the runtime type. Non-generic `new Combos()` remaps correctly.
  Cause: InstaReloadPatcher.CloneInstruction looks the member reference up in the runtime method map
    and, ON A MISS, falls through to `module.ImportReference(methodReference)` - which keeps the
    reference pointing at the freshly compiled hot assembly. A generic type's .ctor is spelled by
    Cecil with a GenericInstanceType declaring type while reflection reports the open definition, so
    the key never matches and every generic newobj takes the fall-through.
  Measured: the suite's origin check reported HOT-OBJECT:InstaReloadSuite for all four Boxed<T>
    cases while `new Combos()` was fine. Same fall-through also leaves a REFUSED method callable
    through its hot copy - that is the standing LEAK line on "generic method on generic type",
    where the call site reports the new marker while the runtime method correctly reports the old.
  Impact: the hot instance is a DIFFERENT Type from the runtime one. It works while it stays in a
    local inside the patched body, which is why nothing crashed. Assigning it to a runtime-typed
    field, or any is/as/cast against the runtime type, would not behave.
  Fix: NOT DONE. Collapse the declaring type to its ElementType when building the key for member
    references, the same way GetDeclaringTypeKeyName already does on the generic path, and apply it
    to the newobj/ctor lookup too. A miss should arguably also WARN instead of silently falling
    through to the hot assembly - the silence is what let this live undetected.
