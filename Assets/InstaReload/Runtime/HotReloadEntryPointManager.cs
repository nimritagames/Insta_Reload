using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nimrita.InstaReload
{
    public static class HotReloadEntryPointManager
    {
        private const float ScanIntervalSeconds = 0.5f;
        private static readonly Dictionary<Type, EntryPointRegistration> Registrations = new Dictionary<Type, EntryPointRegistration>();
        private static readonly object LockObj = new object();
        private static float _nextScanTime;
        private static HotReloadEntryPointScanner _scanner;

        public static bool TryRegisterMissingEntryPoint(Type type, EntryPointKind kind, int methodId)
        {
            if (type == null)
            {
                return false;
            }

            lock (LockObj)
            {
                if (!Registrations.TryGetValue(type, out var registration))
                {
                    registration = new EntryPointRegistration();
                    Registrations[type] = registration;
                }

                registration.SetMethodId(kind, methodId);
            }

            EnsureScanner();
            return true;
        }

        public static void Clear()
        {
            lock (LockObj)
            {
                Registrations.Clear();
            }

            _nextScanTime = 0f;
            if (_scanner != null)
            {
                var go = _scanner.gameObject;
                _scanner = null;
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
        }

        internal static void ScanIfNeeded()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            lock (LockObj)
            {
                if (Registrations.Count == 0)
                {
                    return;
                }
            }

            if (Time.unscaledTime < _nextScanTime)
            {
                return;
            }

            _nextScanTime = Time.unscaledTime + ScanIntervalSeconds;

            List<KeyValuePair<Type, EntryPointRegistration>> snapshot;
            lock (LockObj)
            {
                snapshot = new List<KeyValuePair<Type, EntryPointRegistration>>(Registrations);
            }

            foreach (var entry in snapshot)
            {
                AttachProxiesForType(entry.Key, entry.Value);
            }
        }

        private static void EnsureScanner()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (_scanner != null)
            {
                return;
            }

            var go = new GameObject("InstaReloadEntryPointScanner");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _scanner = go.AddComponent<HotReloadEntryPointScanner>();
        }

        private static void AttachProxiesForType(Type type, EntryPointRegistration registration)
        {
            if (type == null)
            {
                return;
            }

            var objects = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < objects.Length; i++)
            {
                var component = objects[i] as Component;
                if (component == null)
                {
                    continue;
                }

                var proxies = component.GetComponents<HotReloadEntryPointProxy>();
                HotReloadEntryPointProxy proxy = null;
                for (int p = 0; p < proxies.Length; p++)
                {
                    if (proxies[p] != null && proxies[p].Target == component)
                    {
                        proxy = proxies[p];
                        break;
                    }
                }

                if (proxy == null)
                {
                    proxy = component.gameObject.AddComponent<HotReloadEntryPointProxy>();
                }

                proxy.Configure(component, registration);
            }
        }

        public enum EntryPointKind
        {
            Update,
            FixedUpdate,
            LateUpdate,
            OnGUI,
            OnApplicationFocus,
            OnApplicationPause,
            OnApplicationQuit,
            OnBecameVisible,
            OnBecameInvisible,
            OnPreCull,
            OnPreRender,
            OnPostRender,
            OnRenderObject,
            OnWillRenderObject,
            OnRenderImage,
            OnDrawGizmos,
            OnDrawGizmosSelected,
            Reset,
            OnValidate,
            OnAnimatorMove,
            OnAnimatorIK,
            OnTransformChildrenChanged,
            OnTransformParentChanged,
            OnRectTransformDimensionsChange,
            OnCanvasGroupChanged,
            OnCanvasHierarchyChanged,
            OnDidApplyAnimationProperties,
            OnCollisionEnter,
            OnCollisionExit,
            OnCollisionStay,
            OnCollisionEnter2D,
            OnCollisionExit2D,
            OnCollisionStay2D,
            OnTriggerEnter,
            OnTriggerExit,
            OnTriggerStay,
            OnTriggerEnter2D,
            OnTriggerExit2D,
            OnTriggerStay2D,
            OnControllerColliderHit,
            OnJointBreak,
            OnJointBreak2D,
            OnParticleCollision,
            OnParticleTrigger,
            OnParticleSystemStopped,
            OnParticleSystemPaused,
            OnParticleSystemResumed,
            OnParticleSystemPlaybackStateChanged,
            OnMouseDown,
            OnMouseUp,
            OnMouseEnter,
            OnMouseExit,
            OnMouseOver,
            OnMouseDrag,
            OnMouseUpAsButton,
            OnBeforeRender
        }

        internal sealed class EntryPointRegistration
        {
            private readonly Dictionary<EntryPointKind, int> _methodIds = new Dictionary<EntryPointKind, int>();

            internal void SetMethodId(EntryPointKind kind, int methodId)
            {
                _methodIds[kind] = methodId;
            }

            internal bool TryGetMethodId(EntryPointKind kind, out int methodId)
            {
                return _methodIds.TryGetValue(kind, out methodId);
            }
        }
    }
}
