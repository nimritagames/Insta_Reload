using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEditor;
using UnityEngine;
using CecilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace Nimrita.InstaReload.Editor
{
    /// <summary>
    /// PROBE: does an ILHook take effect in EDIT MODE?
    ///
    /// This is the one load-bearing unknown behind every Edit Mode estimate, and no documentation
    /// answers it for our architecture. Research established the rest: the commercial tool ships
    /// Edit Mode support, its patcher contains ZERO play-mode branching, and the suppression side is
    /// a known Unity preference (kAutoRefreshMode). What none of that proves is whether OUR detour
    /// mechanism actually applies when the Editor is idle rather than playing.
    ///
    /// DELIBERATELY ISOLATED. Two questions get conflated in "does Edit Mode work":
    ///   1. Does a detour take effect in Edit Mode?            <- this probe, the real unknown
    ///   2. Can we stop Unity recompiling in Edit Mode?        <- known engineering, not tested here
    /// Mixing them would make a negative result unattributable. So this touches no file watcher, no
    /// suppression, and no compile: it hooks a method directly and asks whether the new body runs.
    ///
    /// The hook is held in a STATIC field on purpose - the project learned the hard way that letting
    /// an ILHook be collected silently removes the patch.
    /// </summary>
    internal static class EditModeProbe
    {
        private static ILHook _hook;

        [MenuItem("Tools/InstaReload/Probe Edit Mode Patching")]
        private static void Run()
        {
            var playing = EditorApplication.isPlaying;
            var before = Target();

            var method = typeof(EditModeProbe).GetMethod(
                nameof(Target),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                Debug.LogError("[EDITPROBE] could not reflect Target - probe inconclusive, not negative");
                return;
            }

            string installError = null;
            try
            {
                _hook?.Dispose();
                _hook = new ILHook(method, Rewrite);
            }
            catch (System.Exception ex)
            {
                installError = $"{ex.GetType().Name}: {ex.Message}";
            }

            var after = Target();

            // Stated as an explicit verdict rather than left for a human to infer from two strings.
            var verdict = installError != null
                ? "INSTALL-FAILED"
                : after == "PATCHED" ? "DETOUR APPLIES IN EDIT MODE"
                : after == before ? "DETOUR HAD NO EFFECT"
                : "UNEXPECTED";

            Debug.Log(
                $"[EDITPROBE] isPlaying={playing} before={before} after={after} => {verdict}" +
                (installError != null ? $" ({installError})" : string.Empty));
        }

        [MenuItem("Tools/InstaReload/Probe Edit Mode Patching (undo)")]
        private static void Undo()
        {
            _hook?.Dispose();
            _hook = null;
            Debug.Log($"[EDITPROBE] hook removed, Target now returns {Target()}");
        }

        private static void Rewrite(ILContext context)
        {
            context.Body.Instructions.Clear();
            context.Body.ExceptionHandlers.Clear();

            var cursor = new ILCursor(context);
            cursor.Emit(CecilOpCodes.Ldstr, "PATCHED");
            cursor.Emit(CecilOpCodes.Ret);
        }

        /// <summary>
        /// NoInlining so the call cannot be folded into the caller. Without it a "no effect" result
        /// would be ambiguous between "the detour did not apply" and "the call never happened" -
        /// the exact confusion that cost this project four commits.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Target()
        {
            return "ORIGINAL";
        }
    }
}
