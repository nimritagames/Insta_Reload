using System;
using System.Collections.Generic;
using System.Reflection;
using Nimrita.InstaReload;

namespace Nimrita.InstaReload.Editor
{
    /// <summary>
    /// Canonical type name for the keys used to match a Cecil-read assembly against the loaded
    /// runtime assembly (field keys, method keys, dispatch keys).
    ///
    /// WHY THIS EXISTS. Cecil and reflection spell constructed generics differently:
    ///     Cecil      System.Collections.Generic.List`1&lt;System.Int32&gt;
    ///     reflection System.Collections.Generic.List`1[[System.Int32, mscorlib, Version=...]]
    /// so for ANY generic-typed field or parameter the two keys could never match. The field was
    /// then classified as new, its ldfld/stfld were rewritten to HotReloadFieldStore, and the
    /// store was empty because the live value sat in the real CLR field — so every List/
    /// Dictionary/Func field read back null inside a patched method, silently. Methods with
    /// generic parameters mismatched the same way.
    ///
    /// The name is rebuilt STRUCTURALLY from the generic definition plus its arguments, producing
    /// Cecil's shape by construction rather than by string-munging reflection's output.
    ///
    /// Deliberately centralised: two copies of key generation that must agree byte-for-byte is
    /// precisely the defect above, so the patcher and the callback invoker both call this.
    /// </summary>
    internal static class TypeKeyName
    {
        internal static string For(Type type)
        {
            if (type == null)
            {
                return string.Empty;
            }

            // Open generic parameters (T) carry no useful full name; Cecil emits the bare name.
            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            // Arrays/byref/pointers must recurse, otherwise an element type that IS generic
            // (List<int>[], ref List<int>) would fall through to the mismatching FullName.
            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                var commas = rank > 1 ? new string(',', rank - 1) : string.Empty;
                return $"{For(type.GetElementType())}[{commas}]";
            }

            if (type.IsByRef)
            {
                return $"{For(type.GetElementType())}&";
            }

            if (type.IsPointer)
            {
                return $"{For(type.GetElementType())}*";
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var definition = type.GetGenericTypeDefinition();
                var definitionName = definition.FullName ?? definition.Name;
                var arguments = type.GetGenericArguments();
                var rendered = new string[arguments.Length];
                for (int i = 0; i < arguments.Length; i++)
                {
                    rendered[i] = For(arguments[i]);
                }

                return $"{definitionName}<{string.Join(",", rendered)}>";
            }

            return type.FullName ?? type.Name;
        }
    }

    /// <summary>
    /// Per-phase timings for one ApplyAssembly call, in milliseconds.
    ///
    /// `patch` is now ~76% of total reload latency, and it covers five very different kinds of
    /// work — file parsing, AppDomain loading, reflection map building, and codegen. Attributing
    /// it before optimising it is the same discipline that turned the old `queue` number from a
    /// guess into a fix.
    ///
    /// Fields are mutable and filled in as ApplyAssembly progresses, so an early return still
    /// yields partial (honest) data rather than nothing.
    /// </summary>
    internal sealed class PatchPhaseTimings
    {
        internal double CecilReadMs;
        internal double ValidateMs;
        internal double AssemblyLoadMs;
        internal double MapBuildMs;
        internal double HookApplyMs;

        internal string BuildLine()
        {
            return
                $"cecil {CecilReadMs:F0} | validate {ValidateMs:F0} | load {AssemblyLoadMs:F0} | " +
                $"maps {MapBuildMs:F0} | hooks {HookApplyMs:F0}";
        }
    }

    internal sealed class PatchApplyResult
    {
        public PatchApplyResult(
            string assemblyName,
            Guid runtimeModuleMvid,
            IReadOnlyList<MethodTokenPair> tokenPairs,
            int patchedCount,
            int dispatchedCount,
            int trampolineCount,
            int skippedCount,
            IReadOnlyList<string> errors,
            IReadOnlyList<MethodPatchRecord> methodPatches = null,
            IReadOnlyList<string> skippedGenericMethods = null)
        {
            AssemblyName = assemblyName;
            RuntimeModuleMvid = runtimeModuleMvid;
            TokenPairs = tokenPairs ?? Array.Empty<MethodTokenPair>();
            PatchedCount = patchedCount;
            DispatchedCount = dispatchedCount;
            TrampolineCount = trampolineCount;
            SkippedCount = skippedCount;
            Errors = errors ?? Array.Empty<string>();
            MethodPatches = methodPatches ?? Array.Empty<MethodPatchRecord>();
            SkippedGenericMethods = skippedGenericMethods ?? Array.Empty<string>();
        }

        public string AssemblyName { get; }
        public Guid RuntimeModuleMvid { get; }
        public IReadOnlyList<MethodTokenPair> TokenPairs { get; }
        public int PatchedCount { get; }
        public int DispatchedCount { get; }
        public int TrampolineCount { get; }
        public int SkippedCount { get; }
        public IReadOnlyList<string> Errors { get; }
        public IReadOnlyList<MethodPatchRecord> MethodPatches { get; }

        /// <summary>Generic methods filtered out before patching. Separate from SkippedCount
        /// because they are dropped upstream and never reach the counter.</summary>
        public IReadOnlyList<string> SkippedGenericMethods { get; }

        public bool AppliedAny => PatchedCount > 0 || DispatchedCount > 0 || TrampolineCount > 0;
    }

    internal readonly struct MethodPatchRecord
    {
        public MethodPatchRecord(string methodKey, HotReloadPatchKind kind, MethodBase runtimeMethod)
        {
            MethodKey = methodKey ?? string.Empty;
            Kind = kind;
            RuntimeMethod = runtimeMethod;
        }

        public string MethodKey { get; }
        public HotReloadPatchKind Kind { get; }
        public MethodBase RuntimeMethod { get; }
    }

    internal readonly struct MethodTokenPair
    {
        public MethodTokenPair(int patchToken, int runtimeToken, string methodKey)
        {
            PatchToken = patchToken;
            RuntimeToken = runtimeToken;
            MethodKey = methodKey ?? string.Empty;
        }

        public int PatchToken { get; }
        public int RuntimeToken { get; }
        public string MethodKey { get; }
    }

    internal sealed class PatchReplayContext
    {
        public PatchReplayContext(Guid runtimeModuleMvid, IReadOnlyDictionary<int, int> patchToRuntimeTokens)
        {
            RuntimeModuleMvid = runtimeModuleMvid;
            PatchToRuntimeTokens = patchToRuntimeTokens ?? new Dictionary<int, int>();
        }

        public Guid RuntimeModuleMvid { get; }
        public IReadOnlyDictionary<int, int> PatchToRuntimeTokens { get; }

        public bool CanUseTokens(Guid currentMvid)
        {
            return currentMvid == RuntimeModuleMvid && PatchToRuntimeTokens.Count > 0;
        }

        public bool TryGetRuntimeToken(int patchToken, out int runtimeToken)
        {
            return PatchToRuntimeTokens.TryGetValue(patchToken, out runtimeToken);
        }
    }
}
