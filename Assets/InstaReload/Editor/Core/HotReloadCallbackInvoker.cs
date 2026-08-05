using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nimrita.InstaReload;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    internal static class HotReloadCallbackInvoker
    {
        private const BindingFlags HotTypeMethodFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void InvokeCallbacks(PatchApplyResult result)
        {
            if (result == null || !result.AppliedAny || result.MethodPatches == null || result.MethodPatches.Count == 0)
            {
                return;
            }

            var patches = BuildPatchList(result.MethodPatches);
            var patchKeys = new HashSet<string>(
                result.MethodPatches.Select(patch => patch.MethodKey),
                StringComparer.Ordinal);

            InvokeGlobalCallbacks(patches);
            InvokeLocalCallbacks(patches, patchKeys);
        }

        private static List<HotReloadMethodPatch> BuildPatchList(IReadOnlyList<MethodPatchRecord> records)
        {
            var result = new List<HotReloadMethodPatch>(records.Count);
            foreach (var record in records)
            {
                result.Add(new HotReloadMethodPatch(record.MethodKey, record.RuntimeMethod, record.Kind));
            }
            return result;
        }

        private static void InvokeGlobalCallbacks(List<HotReloadMethodPatch> patches)
        {
            foreach (var method in FindAttributedMethods(typeof(InvokeOnHotReloadAttribute)))
            {
                InvokeMethod(method, patches, null);
            }
        }

        private static void InvokeLocalCallbacks(List<HotReloadMethodPatch> patches, HashSet<string> patchKeys)
        {
            foreach (var method in FindAttributedMethods(typeof(InvokeOnHotReloadLocalAttribute)))
            {
                var methodKey = GetMethodKey(method);
                if (!patchKeys.Contains(methodKey))
                {
                    continue;
                }

                var patch = patches.FirstOrDefault(item => string.Equals(item.MethodKey, methodKey, StringComparison.Ordinal));
                InvokeMethod(method, patches, patch);
            }
        }

        private static void InvokeMethod(MethodInfo method, List<HotReloadMethodPatch> patches, HotReloadMethodPatch localPatch)
        {
            if (method == null || method.IsAbstract)
            {
                return;
            }

            var parameters = method.GetParameters();
            object[] args = null;
            if (parameters.Length == 1)
            {
                var paramType = parameters[0].ParameterType;
                if (paramType == typeof(HotReloadMethodPatch))
                {
                    if (localPatch == null)
                    {
                        InstaReloadLogger.LogWarning(InstaReloadLogCategory.General, $"Callbacks: local patch parameter used on global callback {method.DeclaringType?.FullName}.{method.Name}");
                        return;
                    }

                    args = new object[] { localPatch };
                }
                else if (paramType.IsArray && paramType.GetElementType() == typeof(HotReloadMethodPatch))
                {
                    args = new object[] { patches.ToArray() };
                }
                else if (typeof(IReadOnlyList<HotReloadMethodPatch>).IsAssignableFrom(paramType) ||
                         typeof(IList<HotReloadMethodPatch>).IsAssignableFrom(paramType) ||
                         typeof(IEnumerable<HotReloadMethodPatch>).IsAssignableFrom(paramType))
                {
                    args = new object[] { patches };
                }
                else
                {
                    InstaReloadLogger.LogWarning(InstaReloadLogCategory.General, $"Callbacks: unsupported parameter type on {method.DeclaringType?.FullName}.{method.Name}");
                    return;
                }
            }
            else if (parameters.Length > 1)
            {
                InstaReloadLogger.LogWarning(InstaReloadLogCategory.General, $"Callbacks: too many parameters on {method.DeclaringType?.FullName}.{method.Name}");
                return;
            }

            if (method.IsStatic)
            {
                TryInvoke(method, null, args);
                return;
            }

            var declaringType = method.DeclaringType;
            if (declaringType == null || !typeof(UnityEngine.Object).IsAssignableFrom(declaringType))
            {
                InstaReloadLogger.LogWarning(InstaReloadLogCategory.General, $"Callbacks: instance callback skipped (no Unity instance): {method.DeclaringType?.FullName}.{method.Name}");
                return;
            }

            var targets = Resources.FindObjectsOfTypeAll(declaringType);
            foreach (var target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                TryInvoke(method, target, args);
            }
        }

        private static void TryInvoke(MethodInfo method, object target, object[] args)
        {
            try
            {
                method.Invoke(target, args);
            }
            catch (Exception ex)
            {
                var name = method.DeclaringType != null ? method.DeclaringType.FullName : method.Name;
                InstaReloadLogger.LogWarning(InstaReloadLogCategory.General, $"Callbacks: invoke failed for {name}.{method.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds every method carrying <paramref name="attributeType"/>.
        ///
        /// This used to walk AppDomain.CurrentDomain.GetAssemblies() -> GetTypes() ->
        /// GetMethods() -> IsDefined(inherit: true). In the Unity 6 Editor AppDomain that
        /// measured ~3.5s per sweep, and InvokeCallbacks runs two sweeps (global + local),
        /// so it accounted for ~6.9s of a ~7.4s hot reload — 94% of total latency, recomputing
        /// a set that only changes when assemblies change.
        ///
        /// TypeCache is the index Unity already maintains for exactly this query and rebuilds
        /// on domain reload, so the sweep cost disappears instead of being cached by hand.
        /// </summary>
        private static IEnumerable<MethodInfo> FindAttributedMethods(Type attributeType)
        {
            foreach (var method in TypeCache.GetMethodsWithAttribute(attributeType))
            {
                yield return method;
            }

            // TypeCache only indexes assemblies Unity compiled. Types introduced during this
            // play session live in a hot assembly loaded via Assembly.Load(byte[]) and are
            // invisible to it, so they are scanned directly. HotTypeRegistry only ever holds
            // types added by hot reload, so this stays small — it is not the old full sweep.
            foreach (var hotType in HotTypeRegistry.GetAll())
            {
                if (hotType == null)
                {
                    continue;
                }

                foreach (var method in hotType.GetMethods(HotTypeMethodFlags))
                {
                    if (method.IsDefined(attributeType, inherit: true))
                    {
                        yield return method;
                    }
                }
            }
        }

        private static string GetMethodKey(MethodBase method)
        {
            var typeName = method.DeclaringType != null ? method.DeclaringType.FullName : method.Name;
            var parameters = method.GetParameters().Select(p => GetTypeName(p.ParameterType));
            var returnType = method is MethodInfo mi ? GetTypeName(mi.ReturnType) : "System.Void";
            var genericArity = method.IsGenericMethod ? method.GetGenericArguments().Length : 0;
            return $"{NormalizeTypeName(typeName)}::{method.Name}`{genericArity}({string.Join(",", parameters)})=>{NormalizeTypeName(returnType)}";
        }

        // Shared with InstaReloadPatcher via TypeKeyName. These two key generators must agree
        // byte-for-byte; when they diverged on generics, every generic-typed field silently read
        // back null inside patched methods.
        private static string GetTypeName(Type type)
        {
            return TypeKeyName.For(type);
        }

        private static string NormalizeTypeName(string name)
        {
            return name?.Replace("/", "+");
        }
    }
}
