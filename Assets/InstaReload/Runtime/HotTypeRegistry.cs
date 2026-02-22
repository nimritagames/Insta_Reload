using System;
using System.Collections.Generic;

namespace Nimrita.InstaReload
{
    // Central registry for types that were added during a play mode session via hot reload.
    //
    // WHY THIS EXISTS:
    //   When the patcher compiles a file that introduces a new class, it loads the compiled
    //   assembly into the AppDomain via Assembly.Load(). The new type exists in memory, but
    //   nothing else knows about it — the original runtime assembly has no record of it, and
    //   the IL cloning pipeline resolves types against the runtime assembly by default.
    //
    //   HotTypeRegistry bridges that gap. The patcher registers new types here immediately
    //   after the hot assembly loads. The IL cloning code (CloneInstruction) checks here
    //   before falling back to the runtime assembly, so patched methods that reference new
    //   types get the correct runtime Type object in their IL operands.
    //
    // LIFETIME:
    //   Types accumulate across hot reloads within a single play mode session. Clear() is
    //   called on play mode exit (alongside HotReloadDispatcher.Clear() and
    //   HotReloadEntryPointManager.Clear()), so stale types from a previous session never
    //   bleed into the next one.
    //
    // THREAD SAFETY:
    //   All mutations go through _lock. Reads (TryGet, Contains, GetAll) also lock so
    //   callers always see a consistent snapshot. The Registry is written once per hot
    //   reload cycle (on the main thread) and read during IL cloning (also main thread),
    //   but the lock is cheap insurance for correctness.
    //
    // KEY FORMAT:
    //   Keys are fully-qualified type names with nested-type separator normalized to '+'.
    //   Example: "MyNamespace.OuterClass+InnerClass"
    //   Normalization happens at the call site (InstaReloadPatcher) before Register() is
    //   called, keeping this class free of naming-convention knowledge.
    public static class HotTypeRegistry
    {
        private static readonly Dictionary<string, Type> _types =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private static readonly object _lock = new object();

        // Register a new type discovered in a hot assembly.
        // Called by InstaReloadPatcher immediately after Assembly.Load() succeeds,
        // once per new type per hot reload cycle.
        // Silently overwrites if the same type name is registered again (re-compile
        // of the same file produces a fresh type in a new assembly load — the newest
        // one should win).
        public static void Register(string fullName, Type type)
        {
            if (string.IsNullOrEmpty(fullName) || type == null)
                return;

            lock (_lock)
            {
                _types[fullName] = type;
            }
        }

        // Look up a type by its fully-qualified name.
        // Returns true and sets 'type' when found; returns false otherwise.
        // Used by CloneInstruction to resolve TypeReference and MethodReference
        // operands that point to new types not in the original runtime assembly.
        public static bool TryGet(string fullName, out Type type)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                type = null;
                return false;
            }

            lock (_lock)
            {
                return _types.TryGetValue(fullName, out type);
            }
        }

        // Quick existence check without out param — used in hot paths where
        // only a yes/no answer is needed before doing more expensive work.
        public static bool Contains(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return false;

            lock (_lock)
            {
                return _types.ContainsKey(fullName);
            }
        }

        // Returns a snapshot of all currently registered types.
        // Used by Task 5 (HandleNewMonoBehaviourTypes) to iterate new types and
        // register Unity lifecycle callbacks for any that extend MonoBehaviour.
        // Returns a new list (not the live dictionary) so callers can iterate
        // safely without holding the lock.
        public static IReadOnlyList<Type> GetAll()
        {
            lock (_lock)
            {
                return new List<Type>(_types.Values);
            }
        }

        // Clears all registered types.
        // Must be called on play mode exit to prevent stale types from a previous
        // session appearing in the next one. Call order: same point where
        // HotReloadDispatcher.Clear() and HotReloadEntryPointManager.Clear() are called.
        public static void Clear()
        {
            lock (_lock)
            {
                _types.Clear();
            }
        }
    }
}
