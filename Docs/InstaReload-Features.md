# InstaReload Features

This document lists what InstaReload supports today, what is partially supported,
and what still requires a Play Mode restart. It reflects the current WAY-1
pipeline in this repo and applies to Play Mode hot reload (not Edit Mode).

## Supported (today)

Editing function bodies (MonoBehaviour, normal classes, static classes)
- We recompile the file and replace method IL bodies at runtime.

Unity message entry points
- Existing entry points are detoured to dispatcher trampolines.
- Messages added mid-play can be forwarded by the fallback proxy (see caveats).

New methods (added mid-play)
- New methods are registered with the dispatcher.
- Calls to new methods work when the call site is also hot-reloaded.

Properties / events / indexers / operators
- Getter/setter/add/remove bodies are patched like normal methods.

Constructors (existing)
- Instance constructor bodies can be patched when the constructor already exists.

Local functions
- Added or edited local functions work when their call sites are hot-reloaded.

Lambdas (partial)
- Lambda bodies are patched like normal methods when their generated methods exist.
- If a new closure shape is introduced, recreate the lambda to pick up new captures.

Async/await and iterators (partial)
- New invocations use the updated logic.
- Existing state machines keep running the old logic.

Fields (via runtime field store)
- Added/reshaped fields are backed by `HotReloadFieldStore` when missing at runtime.
- Removed fields are tolerated (runtime fields remain until restart).

Using directives, syntax updates, nullable
- Any C# syntax supported by the compiler is supported as long as the resulting
  IL can be patched.

Hot reload callbacks
- `[InvokeOnHotReload]` and `[InvokeOnHotReloadLocal]` fire after patch batches.

Patch replay on Play Mode entry
- Cached patches are reapplied with token-aware resolution.

## Supported with caveats

Unity message additions mid-play
- The fallback proxy forwards a curated list of Unity messages.
- `Awake`, `Start`, `OnEnable`, `OnDisable`, `OnDestroy` are intentionally not
  replayed by the proxy (unsafe to re-run).
- See `Docs/InstaReload-Unity-Message-Support.md` for the exact signatures.

Field initializers
- Initializers run only for newly created instances.
- Existing instances keep their current values (or default values from the store).

Missing-field address access
- `ldflda`/`ldsflda` on missing fields is not supported and will skip patching.

External call sites
- Calls from code that was not hot-reloaded may still target old methods.
- New methods are only callable when their call sites are patched or already
  dispatch through trampolines.

## Not supported (requires restart)

New types or new files
- Adding classes/structs/enums in Play Mode requires a restart.

Method removals or renames
- Removing or renaming methods is treated as a structural change.

Signature changes
- Adding/removing/reordering parameters, changing return type, changing ref/out/in
  are not supported yet.

Static/instance changes
- Changing a method from instance to static (or vice versa) is not supported.

Generic dispatch
- Generic methods can be patched when they already exist, but dispatcher-based
  calls do not support generics or byref/pointer parameters.

IL2CPP and Burst
- Runtime detours require Mono/CLR behavior.
- Burst-compiled code is not hot-reloaded.

## Roadmap (not implemented)

- Signature-change support (parameters, return type, ref/out/in)
- Type additions and file additions during Play Mode
- Ongoing coroutine/state machine migration
- Edit Mode hot reload
