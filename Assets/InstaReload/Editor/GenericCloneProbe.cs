using System;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEditor;
using UnityEngine;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace Nimrita.InstaReload.Editor
{
    /// <summary>
    /// TEMPORARY probe. Delete when done.
    ///
    /// Step 1 (cheap, could simplify everything): does ILHook accept the OPEN GENERIC DEFINITION?
    /// Already known: it accepts a CONSTRUCTED instantiation, and one hook on a reference-type
    /// instantiation covers all reference types. If the open definition also works, we would not
    /// need to construct instantiations at all.
    ///
    /// Step 2 asks whether a hook survives a NEW value-type instantiation being created AFTER the
    /// hook is installed - the case we assumed we could never reach and would have to warn about.
    /// </summary>
    public static class GenericCloneProbe
    {
        private static ILHook _definitionHook;

        [MenuItem("Tools/InstaReload Clone Probe/1 - ILHook the OPEN generic definition")]
        public static void HookOpenDefinition()
        {
            var definition = typeof(CloneTarget).GetMethod(nameof(CloneTarget.Describe));
            try
            {
                _definitionHook = new ILHook(definition, ctx =>
                {
                    var cursor = new ILCursor(ctx);
                    cursor.Goto(0);
                    cursor.Emit(CecilOpCodes.Ldstr, "PATCHED-VIA-DEFINITION");
                    cursor.Emit(CecilOpCodes.Ret);
                });
                Debug.Log("[CProbe] ILHook on OPEN DEFINITION -> INSTALLED OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CProbe] ILHook on OPEN DEFINITION -> FAILED: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            Report("AFTER HOOKING OPEN DEFINITION");
        }

        private static ILHook _constructedHook;

        [MenuItem("Tools/InstaReload Clone Probe/1b - ILHook constructed then test LATE types")]
        public static void HookConstructedThenLate()
        {
            var constructed = typeof(CloneTarget)
                .GetMethod(nameof(CloneTarget.Describe))
                .MakeGenericMethod(typeof(object));

            try
            {
                _constructedHook = new ILHook(constructed, ctx =>
                {
                    var cursor = new ILCursor(ctx);
                    cursor.Goto(0);
                    cursor.Emit(CecilOpCodes.Ldstr, "PATCHED-SHARED");
                    cursor.Emit(CecilOpCodes.Ret);
                });
                Debug.Log("[CProbe] ILHook on constructed <object> -> INSTALLED OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CProbe] ILHook on constructed <object> -> FAILED: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // These types were NEVER instantiated before the hook went in. This bounds the real
            // limitation: if late REFERENCE types are covered (they share the hooked body) and only
            // late VALUE types are stale, the gap is far narrower than assumed.
            var target = new CloneTarget();
            Debug.Log(
                $"[CProbe] LATE instantiations, first used AFTER the hook\n" +
                $"   StringBuilder (ref)   = {Invoke(target, typeof(System.Text.StringBuilder), null)}\n" +
                $"   Uri (ref)             = {Invoke(target, typeof(Uri), null)}\n" +
                $"   decimal (value)       = {Invoke(target, typeof(decimal), 1.5m)}\n" +
                $"   long (value)          = {Invoke(target, typeof(long), 5L)}");
        }

        [MenuItem("Tools/InstaReload Clone Probe/3 - Dispose")]
        public static void Dispose()
        {
            _definitionHook?.Dispose();
            _definitionHook = null;
            _constructedHook?.Dispose();
            _constructedHook = null;
            Debug.Log("[CProbe] disposed");
            Report("AFTER DISPOSE");
        }

        private static void Report(string phase)
        {
            var target = new CloneTarget();
            Debug.Log(
                $"[CProbe] {phase}\n" +
                $"   string     = {Invoke(target, typeof(string), "x")}\n" +
                $"   GameObject = {Invoke(target, typeof(GameObject), null)}\n" +
                $"   int        = {Invoke(target, typeof(int), 7)}\n" +
                $"   float      = {Invoke(target, typeof(float), 1.5f)}");
        }

        private static string Invoke(CloneTarget target, Type argument, object value)
        {
            try
            {
                return (string)typeof(CloneTarget)
                    .GetMethod(nameof(CloneTarget.Describe))
                    .MakeGenericMethod(argument)
                    .Invoke(target, new[] { value });
            }
            catch (Exception ex)
            {
                return $"THREW {ex.GetType().Name}: {ex.InnerException?.GetType().Name ?? ""}";
            }
        }
    }
}
