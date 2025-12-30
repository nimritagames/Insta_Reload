using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// Runtime dispatch table for hot-reloaded method invokers.
    /// </summary>
    public static class HotReloadDispatcher
    {
        private static readonly Dictionary<int, Func<object, object[], object>> _table =
            new Dictionary<int, Func<object, object[], object>>();
        private static readonly HashSet<int> _invokedDiagnostics = new HashSet<int>();
        private static readonly HashSet<int> _missingDiagnostics = new HashSet<int>();
        private static InstaReloadLogCategory _enabledCategories = InstaReloadLogCategory.None;
        private static InstaReloadLogLevel _enabledLevels = InstaReloadLogLevel.None;

        public static void Register(int methodId, Func<object, object[], object> invoker)
        {
            if (invoker == null)
            {
                return;
            }

            _table[methodId] = invoker;
        }

        public static object Invoke(object instance, int methodId, object[] args)
        {
            if (_table.TryGetValue(methodId, out var invoker))
            {
                if (ShouldLog(InstaReloadLogLevel.Info) && _invokedDiagnostics.Add(methodId))
                {
                    Debug.Log($"[InstaReload] [Dispatcher] Invoked {methodId} (args: {(args == null ? 0 : args.Length)})");
                }

                return invoker(instance, args ?? Array.Empty<object>());
            }

            if (ShouldLog(InstaReloadLogLevel.Warning) && _missingDiagnostics.Add(methodId))
            {
                Debug.LogWarning($"[InstaReload] [Dispatcher] Missing invoker for {methodId}");
            }

            return null;
        }

        public static void ConfigureLogging(InstaReloadLogCategory categories, InstaReloadLogLevel levels)
        {
            _enabledCategories = categories;
            _enabledLevels = levels;
            _invokedDiagnostics.Clear();
            _missingDiagnostics.Clear();
        }

        public static void Clear()
        {
            _table.Clear();
            _invokedDiagnostics.Clear();
            _missingDiagnostics.Clear();
        }

        private static bool ShouldLog(InstaReloadLogLevel level)
        {
            if ((_enabledCategories & InstaReloadLogCategory.Dispatcher) == 0)
            {
                return false;
            }

            return (_enabledLevels & level) != 0;
        }
    }
}
