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
- 2025-__-__:
  Decision:
  Context: 
  Options considered: 
  Outcome: 
  Follow-ups: 
