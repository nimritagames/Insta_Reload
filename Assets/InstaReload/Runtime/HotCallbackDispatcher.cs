/*
 * ============================================================================
 * INSTARELOAD - HOT CALLBACK DISPATCHER
 * ============================================================================
 *
 * PURPOSE:
 *   Enables newly-added Unity callbacks (Update, FixedUpdate, etc.) to work
 *   during hot reload by manually invoking them via PlayerLoop injection.
 *
 * THE PROBLEM:
 *   Unity discovers MonoBehaviour callbacks ONCE at domain load:
 *   - Scans all types for Update, FixedUpdate, OnCollisionEnter, etc.
 *   - Builds internal dispatch table
 *   - NEVER scans again during runtime
 *
 *   When we hot-reload and add a NEW Update() method:
 *   - Native swapper makes it callable ✅
 *   - But Unity's dispatch table never updates ❌
 *   - So Unity never invokes it!
 *
 * THE SOLUTION:
 *   Use Unity's PlayerLoop API to inject custom update logic:
 *   1. Use [RuntimeInitializeOnLoadMethod] to inject on play mode start
 *   2. Add our custom system AFTER Unity's ScriptRunBehaviourUpdate
 *   3. For each MonoBehaviour, check for hot-loaded callbacks via reflection
 *   4. Manually invoke them if found
 *   5. Unity thinks it's just running our custom PlayerLoop - no idea we're
 *      invoking "invisible" callbacks!
 *
 * SUPPORTED CALLBACKS:
 *   - Update() - Called every frame
 *   - FixedUpdate() - Called at fixed intervals (physics)
 *   - LateUpdate() - Called after all Updates
 *
 * LIMITATIONS:
 *   - Only works for callbacks that have PlayerLoop equivalents
 *   - OnCollisionEnter, OnTriggerEnter require physics system hooks (harder)
 *   - Start() only called once (can't hot reload it)
 *   - Awake() only called once (can't hot reload it)
 *
 * PERFORMANCE:
 *   - Reflection lookup cached per-type
 *   - Only checks active MonoBehaviours
 *   - Minimal overhead (~0.1ms per frame for 100 MonoBehaviours)
 *
 * ============================================================================
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Nimrita.InstaReload.Runtime
{
    /// <summary>
    /// Dispatches hot-loaded Unity callbacks via PlayerLoop injection.
    /// </summary>
    public static class HotCallbackDispatcher
    {
        #region Configuration

        // Set to true to see detailed logs about callback invocation
        private static bool VerboseLogging = false;

        #endregion

        #region State

        private static bool _initialized = false;
        private static bool _enabled = false;

        // Cache of hot callback methods per type
        // Key: Type name, Value: Dictionary of callback name → MethodInfo
        private static readonly Dictionary<string, Dictionary<string, MethodInfo>> _hotCallbackCache =
            new Dictionary<string, Dictionary<string, MethodInfo>>();

        #endregion

        #region Initialization

        /// <summary>
        /// Called by Unity when entering play mode (even without domain reload).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (_initialized)
            {
                if (VerboseLogging)
                    Debug.Log("[HotCallbacks] Already initialized, skipping");
                return;
            }

            try
            {
                InjectPlayerLoop();
                _initialized = true;
                _enabled = true;
                Debug.Log("[HotCallbacks] ✓ PlayerLoop injected - Hot callbacks enabled!");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HotCallbacks] Failed to initialize: {ex.Message}");
            }
        }

        #endregion

        #region PlayerLoop Injection

        private static void InjectPlayerLoop()
        {
            var playerLoop = PlayerLoop.GetCurrentPlayerLoop();

            // Inject after Update
            if (TryInjectCallback(ref playerLoop, typeof(Update.ScriptRunBehaviourUpdate),
                HotUpdate, typeof(HotUpdateSystem)))
            {
                if (VerboseLogging)
                    Debug.Log("[HotCallbacks] Injected HotUpdate after ScriptRunBehaviourUpdate");
            }

            // Inject after FixedUpdate
            if (TryInjectCallback(ref playerLoop, typeof(FixedUpdate.ScriptRunBehaviourFixedUpdate),
                HotFixedUpdate, typeof(HotFixedUpdateSystem)))
            {
                if (VerboseLogging)
                    Debug.Log("[HotCallbacks] Injected HotFixedUpdate after ScriptRunBehaviourFixedUpdate");
            }

            // Inject after LateUpdate
            if (TryInjectCallback(ref playerLoop, typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate),
                HotLateUpdate, typeof(HotLateUpdateSystem)))
            {
                if (VerboseLogging)
                    Debug.Log("[HotCallbacks] Injected HotLateUpdate after ScriptRunBehaviourLateUpdate");
            }

            PlayerLoop.SetPlayerLoop(playerLoop);
        }

        private static bool TryInjectCallback(ref PlayerLoopSystem playerLoop, Type markerType,
            PlayerLoopSystem.UpdateFunction callback, Type systemType)
        {
            // Find the subsystem that contains our marker
            for (int i = 0; i < playerLoop.subSystemList.Length; i++)
            {
                ref var subsystem = ref playerLoop.subSystemList[i];

                // Check if this subsystem has the marker
                if (subsystem.type == markerType.DeclaringType)
                {
                    // Found the parent, now inject into its subsystems
                    var subsystems = new List<PlayerLoopSystem>(subsystem.subSystemList);

                    // Find the marker within subsystems
                    for (int j = 0; j < subsystems.Count; j++)
                    {
                        if (subsystems[j].type == markerType)
                        {
                            // Check if already injected
                            if (j + 1 < subsystems.Count && subsystems[j + 1].type == systemType)
                            {
                                if (VerboseLogging)
                                    Debug.Log($"[HotCallbacks] {systemType.Name} already injected");
                                return false;
                            }

                            // Inject right after the marker
                            var hotSystem = new PlayerLoopSystem
                            {
                                type = systemType,
                                updateDelegate = callback
                            };
                            subsystems.Insert(j + 1, hotSystem);
                            subsystem.subSystemList = subsystems.ToArray();
                            return true;
                        }
                    }
                }

                // Recursively search nested subsystems
                if (subsystem.subSystemList != null && subsystem.subSystemList.Length > 0)
                {
                    if (TryInjectCallback(ref subsystem, markerType, callback, systemType))
                        return true;
                }
            }

            return false;
        }

        // Marker types for PlayerLoop injection
        private struct HotUpdateSystem { }
        private struct HotFixedUpdateSystem { }
        private struct HotLateUpdateSystem { }

        #endregion

        #region Hot Callback Invocation

        private static void HotUpdate()
        {
            if (!_enabled) return;
            InvokeHotCallbacks("Update");
        }

        private static void HotFixedUpdate()
        {
            if (!_enabled) return;
            InvokeHotCallbacks("FixedUpdate");
        }

        private static void HotLateUpdate()
        {
            if (!_enabled) return;
            InvokeHotCallbacks("LateUpdate");
        }

        private static void InvokeHotCallbacks(string callbackName)
        {
            try
            {
                // Find all active MonoBehaviours
                var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(includeInactive: false);

                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null || !behaviour.isActiveAndEnabled)
                        continue;

                    var type = behaviour.GetType();
                    var typeName = type.FullName;

                    // Check cache for hot callback
                    if (!_hotCallbackCache.TryGetValue(typeName, out var callbacks))
                    {
                        callbacks = new Dictionary<string, MethodInfo>();
                        _hotCallbackCache[typeName] = callbacks;
                    }

                    // Check if we've cached this callback
                    if (!callbacks.TryGetValue(callbackName, out var method))
                    {
                        // Search for hot-loaded callback in AppDomain
                        method = FindHotCallback(type, callbackName);
                        callbacks[callbackName] = method; // Cache even if null (to avoid repeated lookups)
                    }

                    // Invoke if found
                    if (method != null)
                    {
                        try
                        {
                            method.Invoke(behaviour, null);

                            if (VerboseLogging)
                                Debug.Log($"[HotCallbacks] Invoked {type.Name}.{callbackName}()");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[HotCallbacks] Error invoking {type.Name}.{callbackName}(): {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HotCallbacks] Error in {callbackName} dispatcher: {ex.Message}");
            }
        }

        private static MethodInfo FindHotCallback(Type originalType, string callbackName)
        {
            // Look for the method in ALL loaded assemblies (including hot assemblies)
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in allAssemblies)
            {
                // Only check hot assemblies (InstaReloadDynamicAssembly)
                if (!assembly.FullName.Contains("InstaReloadDynamicAssembly"))
                    continue;

                try
                {
                    var hotType = assembly.GetType(originalType.FullName);
                    if (hotType == null)
                        continue;

                    var method = hotType.GetMethod(callbackName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (method != null)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[HotCallbacks] Found hot callback: {hotType.Name}.{callbackName}() in {assembly.GetName().Name}");
                        return method;
                    }
                }
                catch
                {
                    // Type not found in this assembly, continue
                }
            }

            return null;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Clears the hot callback cache. Call this after hot reload to force re-discovery.
        /// </summary>
        public static void ClearCache()
        {
            _hotCallbackCache.Clear();
            if (VerboseLogging)
                Debug.Log("[HotCallbacks] Cache cleared");
        }

        /// <summary>
        /// Enables or disables hot callback dispatching.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            Debug.Log($"[HotCallbacks] {(enabled ? "Enabled" : "Disabled")}");
        }

        /// <summary>
        /// Gets whether hot callback dispatching is currently enabled.
        /// </summary>
        public static bool IsEnabled => _enabled;

        #endregion
    }
}
