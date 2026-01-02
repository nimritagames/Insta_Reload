using System;
using System.Reflection;

namespace Nimrita.InstaReload
{
    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class InvokeOnHotReloadAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = true)]
    public sealed class InvokeOnHotReloadLocalAttribute : Attribute
    {
    }

    [Flags]
    public enum HotReloadPatchKind
    {
        None = 0,
        Patched = 1 << 0,
        Dispatched = 1 << 1,
        Trampoline = 1 << 2
    }

    public sealed class HotReloadMethodPatch
    {
        public HotReloadMethodPatch(string methodKey, MethodBase runtimeMethod, HotReloadPatchKind kind)
        {
            MethodKey = methodKey ?? string.Empty;
            RuntimeMethod = runtimeMethod;
            Kind = kind;
        }

        public string MethodKey { get; }
        public MethodBase RuntimeMethod { get; }
        public HotReloadPatchKind Kind { get; }
    }
}
