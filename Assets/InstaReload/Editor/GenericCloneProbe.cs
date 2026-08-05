using System;
using System.Collections.Generic;
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

        [MenuItem("Tools/InstaReload Clone Probe/4 - Report generic CLASS")]
        public static void ReportGenericClass()
        {
            Debug.Log(
                "[CProbe] GENERIC CLASS (GenericContainer<T>.Describe)\n" +
                $"   <string>     (ref, call site)     = {InvokeContainer(typeof(string), "x")}\n" +
                $"   <GameObject> (ref, NO call site)  = {InvokeContainer(typeof(GameObject), null)}\n" +
                $"   <int>        (value, call site)   = {InvokeContainer(typeof(int), 7)}\n" +
                $"   <float>      (value, NO site)     = {InvokeContainer(typeof(float), 1.5f)}\n" +
                $"   <decimal>    (value, NO site)     = {InvokeContainer(typeof(decimal), 2.5m)}");
        }

        private static string InvokeContainer(Type argument, object value)
        {
            try
            {
                var constructed = typeof(GenericContainer<>).MakeGenericType(argument);
                var instance = Activator.CreateInstance(constructed);
                return (string)constructed.GetMethod("Describe").Invoke(instance, new[] { value });
            }
            catch (Exception ex)
            {
                return $"THREW {ex.GetType().Name}: {ex.InnerException?.GetType().Name ?? ""}";
            }
        }

        [MenuItem("Tools/InstaReload Clone Probe/5 - Report generic COMBINATIONS")]
        public static void ReportCombos()
        {
            var combos = new GenericCombos();

            Debug.Log(
                "[CProbe] 1) MULTIPLE TYPE PARAMETERS  TwoParams<T,U>\n" +
                $"   <int,string>   (call site)  = {Invoke2(combos, typeof(int), typeof(string), 1, "x")}\n" +
                $"   <string,float> (call site)  = {Invoke2(combos, typeof(string), typeof(float), "a", 2.5f)}\n" +
                $"   <string,object>(both ref)   = {Invoke2(combos, typeof(string), typeof(object), "a", null)}\n" +
                $"   <long,long>    (NO site)    = {Invoke2(combos, typeof(long), typeof(long), 1L, 2L)}");

            Debug.Log(
                "[CProbe] 2) VALUE-TYPE CONSTRAINT  StructOnly<T> where T : struct\n" +
                $"   <int>   (call site) = {Invoke1(combos, "StructOnly", typeof(int), 7)}\n" +
                $"   <float> (NO site)   = {Invoke1(combos, "StructOnly", typeof(float), 1.5f)}");

            Debug.Log(
                "[CProbe] 3) NESTED GENERIC ARGUMENT  Nested<T>\n" +
                $"   <List<int>> (call site) = {Invoke1(combos, "Nested", typeof(List<int>), new List<int>())}\n" +
                $"   <List<string>> (NO site, ref) = {Invoke1(combos, "Nested", typeof(List<string>), new List<string>())}");

            Debug.Log(
                "[CProbe] 4) GENERIC METHOD ON GENERIC TYPE  GenericHolder<T>.Both<U>\n" +
                $"   <int>.Both<float>  (call site) = {InvokeBoth(typeof(int), typeof(float), 1, 2f)}\n" +
                $"   <string>.Both<object> (ref/ref) = {InvokeBoth(typeof(string), typeof(object), "a", null)}");
        }

        private static string Invoke1(GenericCombos target, string name, Type argument, object value)
        {
            try
            {
                return (string)typeof(GenericCombos).GetMethod(name)
                    .MakeGenericMethod(argument).Invoke(target, new[] { value });
            }
            catch (Exception ex)
            {
                return $"THREW {ex.GetType().Name}: {ex.InnerException?.GetType().Name ?? ""}";
            }
        }

        private static string Invoke2(GenericCombos target, Type a, Type b, object va, object vb)
        {
            try
            {
                return (string)typeof(GenericCombos).GetMethod("TwoParams")
                    .MakeGenericMethod(a, b).Invoke(target, new[] { va, vb });
            }
            catch (Exception ex)
            {
                return $"THREW {ex.GetType().Name}: {ex.InnerException?.GetType().Name ?? ""}";
            }
        }

        private static string InvokeBoth(Type typeArg, Type methodArg, object first, object second)
        {
            try
            {
                var holder = typeof(GenericHolder<>).MakeGenericType(typeArg);
                var instance = Activator.CreateInstance(holder);
                return (string)holder.GetMethod("Both")
                    .MakeGenericMethod(methodArg).Invoke(instance, new[] { first, second });
            }
            catch (Exception ex)
            {
                return $"THREW {ex.GetType().Name}: {ex.InnerException?.GetType().Name ?? ""}";
            }
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
                $"   string     (ref, shared hook)      = {Invoke(target, typeof(string), "x")}\n" +
                $"   GameObject (ref, shared hook)      = {Invoke(target, typeof(GameObject), null)}\n" +
                $"   int        (value, AT call site)   = {Invoke(target, typeof(int), 7)}\n" +
                $"   float      (value, AT call site)   = {Invoke(target, typeof(float), 1.5f)}\n" +
                $"   decimal    (value, NO call site)   = {Invoke(target, typeof(decimal), 2.5m)}");
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
