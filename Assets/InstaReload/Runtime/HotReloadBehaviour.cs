using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nimrita.InstaReload
{
    public abstract class HotReloadBehaviour : MonoBehaviour
    {
        private static readonly Dictionary<string, int> MethodIdCache = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();
        private int _awakeMethodId;
        private bool _awakeMethodIdInitialized;
        private int _startMethodId;
        private bool _startMethodIdInitialized;
        private int _onEnableMethodId;
        private bool _onEnableMethodIdInitialized;
        private int _onDisableMethodId;
        private bool _onDisableMethodIdInitialized;
        private int _onDestroyMethodId;
        private bool _onDestroyMethodIdInitialized;
        private int _updateMethodId;
        private bool _updateMethodIdInitialized;
        private int _fixedUpdateMethodId;
        private bool _fixedUpdateMethodIdInitialized;
        private int _lateUpdateMethodId;
        private bool _lateUpdateMethodIdInitialized;

        protected virtual void Awake()
        {
            DispatchVoid(ref _awakeMethodIdInitialized, ref _awakeMethodId, nameof(Awake));
        }

        protected virtual void Start()
        {
            DispatchVoid(ref _startMethodIdInitialized, ref _startMethodId, nameof(Start));
        }

        protected virtual void OnEnable()
        {
            DispatchVoid(ref _onEnableMethodIdInitialized, ref _onEnableMethodId, nameof(OnEnable));
        }

        protected virtual void OnDisable()
        {
            DispatchVoid(ref _onDisableMethodIdInitialized, ref _onDisableMethodId, nameof(OnDisable));
        }

        protected virtual void OnDestroy()
        {
            DispatchVoid(ref _onDestroyMethodIdInitialized, ref _onDestroyMethodId, nameof(OnDestroy));
        }

        protected virtual void Update()
        {
            DispatchVoid(ref _updateMethodIdInitialized, ref _updateMethodId, nameof(Update));
        }

        protected virtual void FixedUpdate()
        {
            DispatchVoid(ref _fixedUpdateMethodIdInitialized, ref _fixedUpdateMethodId, nameof(FixedUpdate));
        }

        protected virtual void LateUpdate()
        {
            DispatchVoid(ref _lateUpdateMethodIdInitialized, ref _lateUpdateMethodId, nameof(LateUpdate));
        }

        private void DispatchVoid(ref bool initialized, ref int cachedMethodId, string methodName)
        {
            if (!initialized)
            {
                cachedMethodId = GetMethodId(GetType(), methodName);
                initialized = true;
            }

            HotReloadDispatcher.Invoke(this, cachedMethodId, null);
        }

        private static int GetMethodId(Type type, string methodName)
        {
            var typeName = type != null ? (type.FullName ?? type.Name) : string.Empty;
            var methodKey = $"{typeName}::{methodName}`0()=>System.Void";

            lock (CacheLock)
            {
                if (!MethodIdCache.TryGetValue(methodKey, out var methodId))
                {
                    methodId = ComputeMethodId(methodKey);
                    MethodIdCache[methodKey] = methodId;
                }

                return methodId;
            }
        }

        private static int ComputeMethodId(string methodKey)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < methodKey.Length; i++)
                {
                    hash ^= methodKey[i];
                    hash *= 16777619;
                }

                return (int)hash;
            }
        }
    }
}
