using System;
using System.Collections.Generic;
using System.Reflection;
using Nimrita.InstaReload;

namespace Nimrita.InstaReload.Editor
{
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
            IReadOnlyList<MethodPatchRecord> methodPatches = null)
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
