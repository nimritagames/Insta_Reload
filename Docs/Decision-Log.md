# Decision Log

Capture meaningful decisions during the prototype so we can keep the project
professional without slowing iteration. Keep entries short and actionable.

## Template
- Date:
- Decision:
- Context:
- Options considered:
- Outcome:
- Follow-ups:

## Entries
- 2025-12-30:
  Decision: Add a runtime dispatcher with Unity entrypoint trampolines for hot reload.
  Context: IL-only patching could not execute new methods because Unity only calls known entrypoints.
  Options considered: Keep IL-only patching; require Play Mode restart; defer to native swapping (WAY-2).
  Outcome: Implemented HotReloadDispatcher, entrypoint trampolines, dynamic method dispatch, and call rewriting for missing methods.
  Follow-ups: Expand entrypoint coverage (parameterized callbacks) and tighten dispatch performance.
- 2025-12-30:
  Decision: Route trampolines through a script-assembly bridge to avoid new metadata references.
  Context: Trampoline calls into a different assembly were not executed during Play Mode.
  Options considered: Keep direct dispatcher calls; use calli/function pointers; add a bridge in Assembly-CSharp.
  Outcome: Added HotReloadBridge in Assembly-CSharp and resolve dispatcher calls through it.
  Follow-ups: Validate that dispatcher calls reach runtime on first hot reload and refine bridge setup if Unity changes assembly layouts.
- 2025-12-30:
  Decision: Use method detours (Hook) for Unity entrypoint trampolines instead of ILHook.
  Context: ILHook trampolines were installed but Unity entrypoints did not invoke the dispatcher.
  Options considered: Keep ILHook; patch entrypoints directly; detour entrypoints to a dynamic trampoline.
  Outcome: Replaced entrypoint trampolines with Hook-based detours to a dynamic method.
  Follow-ups: Confirm dispatcher invocation on Update/Start during Play Mode and review detour stability.
- 2025-12-30:
  Decision: Add log levels and log categories with per-category toggles.        
  Context: Dispatcher diagnostics and editor logs needed to be configurable without code changes.
  Options considered: Keep a single verbose toggle; hard-code categories in strings.
  Outcome: Introduced log level/category enums, settings UI for toggles, and dispatcher logging configuration.
  Follow-ups: Review category coverage and adjust defaults based on usage.      
- 2025-12-30:
  Decision: Add a HotReloadBehaviour base class to predeclare Unity lifecycle entry points.
  Context: Unity only wires message methods at domain load, so entry points added mid-play are never called.
  Options considered: Manual stubs in every script; codegen to inject message stubs; base class with dispatcher forwarding.
  Outcome: Added HotReloadBehaviour in runtime and suppressed missing-entrypoint warnings for inheritors.
  Follow-ups: Expand the base entrypoint list or generate it from a curated Unity message table.
- 2025-12-30:
  Decision: Add a fallback entry point manager for MonoBehaviour scripts without inheritance.
  Context: Users wanted Update to work when added mid-play without requiring a base class.
  Options considered: IL weaving into compiled assemblies; global update driver; per-instance proxy components.
  Outcome: Added a runtime entry point manager that attaches proxy components and dispatches Update/FixedUpdate/LateUpdate.
  Follow-ups: Expand coverage (or keep limited) and watch proxy scan cost in larger scenes.
- 2025-12-31:
  Decision: Expand fallback entry point coverage to most Unity message methods.
  Context: MonoBehaviour users wanted new Unity messages (not just Update) to work when added mid-play.
  Options considered: Keep Update-only fallback; require HotReloadBehaviour; implement IL weaving.
  Outcome: Added a wider EntryPointKind set, signature matching in the patcher, and proxy dispatch for lifecycle, rendering, physics, UI, and particle callbacks.
  Follow-ups: Monitor proxy scan overhead and consider opt-out or scoped registration if needed.
- 2025-12-31:
  Decision: Exclude initialization/destruction messages from fallback dispatch.
  Context: `Awake`, `Start`, `OnEnable`, `OnDisable`, and `OnDestroy` have one-time or stateful semantics and are unsafe to auto-replay via a proxy.
  Options considered: Keep them in fallback; add an opt-in attribute; remove from fallback by default.
  Outcome: Removed these methods from the fallback map and proxy dispatch path.
  Follow-ups: Consider an explicit opt-in replay mechanism if needed later.
- 2025-12-31:
  Decision: Persist hot reload patches and replay them at Play Mode entry with token-aware method resolution.
  Context: Domain reloads clear IL hooks; replaying patches improves reliability, and token mapping is more robust than name-only lookup when reapplying.
  Options considered: Rely on Unity's next compile; recompile from source on reload; cache patch assemblies and reuse them with token mapping.
  Outcome: Added PatchHistoryStore for patch persistence, replay on Play Mode enter, token-pair caching, and token-based runtime method resolution with MVID validation.
  Follow-ups: Monitor replay cache growth and consider pruning or UI controls if needed.
- 2025-12-31:
  Decision: Allow field set changes via a runtime field store.
  Context: Field additions or type changes previously forced a Play Mode restart.
  Options considered: Keep strict field-set validation; only allow new fields; virtualize missing fields through a separate store.
  Outcome: Added HotReloadFieldStore and IL rewrites for missing fields, with safeguards against missing field address access.
  Follow-ups: Consider initializer replay or opt-in migration for more complex field initialization.
- 2025-12-31:
  Decision: Add hot reload callbacks via attributes.
  Context: Users want to react when patches are applied (e.g., refresh caches).
  Options considered: Polling APIs; manual hooks; attribute-driven callbacks.
  Outcome: Added InvokeOnHotReload and InvokeOnHotReloadLocal attributes with an editor invoker.
  Follow-ups: Add more callback context if needed (per-method patch details).
- 2026-01-02:
  Decision: Automate Unity Play Mode option setup for InstaReload with opt-in auto-apply.
  Context: Hot reload requires Enter Play Mode Options with domain/scene reload disabled, and manual setup is easy to miss.
  Options considered: Keep instructions only; prompt every Play Mode entry; auto-apply when enabled.
  Outcome: Added a setting toggle and one-click apply in the editor UI, plus auto-apply on Play Mode entry when enabled.
  Follow-ups: Consider warning in the overlay if settings drift.
- 2026-01-03:
  Decision: Move Roslyn compilation to a background task and keep patching on the main thread.
  Context: Synchronous compilation stalled the editor during hot reload, especially on slow path edits.
  Options considered: Keep synchronous compile; reduce logging only; background compile with main-thread patch apply.
  Outcome: Added a compile job queue, background compilation, and stale-change checks before patching.
  Follow-ups: Monitor patch-time stalls and consider parallel compiles for multi-file changes.
- 2026-01-03:
  Decision: Introduce an external worker process to compile hot reload patches.
  Context: Even background compilation could starve Unity's UI thread on slower machines.
  Options considered: Keep in-process compile; throttle compilation; offload compile to a worker over loopback.
  Outcome: Added a worker process with a loopback protocol and editor-side manager to compile outside Unity.
  Follow-ups: Add richer protocol diagnostics and consider parallel compile workers.
- 2025-__-__:
  Decision:
  Context:
  Options considered:
  Outcome:
  Follow-ups: 
