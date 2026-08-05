using UnityEngine;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// Test fixture for METHOD REMOVAL and SIGNATURE CHANGE hot reload.
    ///
    /// SETUP: attach BOTH this script AND TestInstaReloadCaller to the SAME GameObject.
    /// (This file cannot add the caller itself — see the note above Update().)
    ///
    /// On entering Play Mode you must see ALL THREE of these once per second:
    ///     [Test] tick ...
    ///     [Test] DoomedMethod ORIGINAL
    ///     [Test] SharedHelper ORIGINAL      &lt;-- proves the cross-file caller is attached
    ///
    /// If that third line is missing, TestInstaReloadCaller is not on the GameObject and
    /// TEST 4 will not be exercised. Attach it before trusting the TEST 4 result.
    ///
    /// TURN OFF "Collapse" IN THE CONSOLE before running these tests. With Collapse on, a
    /// repeated identical message folds into one row, so code that is still running looks like
    /// it stopped. Every log below carries a frame number for the same reason.
    ///
    /// Run the tests IN ORDER without leaving Play Mode.
    ///
    /// ===================================================================================
    /// TEST 1 — Delete a method nothing else calls
    /// ===================================================================================
    ///   EDIT:   Delete the whole UnusedMethod() below.
    ///   EXPECT: Reload APPLIES (used to be rejected outright).
    ///           Warning names UnusedMethod. Ticks continue.
    ///
    /// ===================================================================================
    /// TEST 2 — Delete a method that a method you also edit was calling
    /// ===================================================================================
    ///   EDIT:   a) Delete the DoomedMethod() call inside Update().
    ///           b) Delete DoomedMethod() itself.
    ///   EXPECT: Reload APPLIES. "DoomedMethod ORIGINAL" STOPS.
    ///           Cleanest case — Update was recompiled without the call.
    ///
    /// ===================================================================================
    /// TEST 3 — Change a method signature   ** used to force a Play Mode exit **
    /// ===================================================================================
    ///   EDIT:   a) Add(int a, int b)  ->  Add(int a, int b, int c) { return a + b + c; }
    ///           b) Update the call: Add(2, 3)  ->  Add(2, 3, 10).
    ///   EXPECT: Reload APPLIES. "Add = 5" becomes "Add = 15".
    ///           Warning names the OLD Add(int,int); the new 3-arg version is registered as
    ///           a new method and reached through the dispatcher.
    ///
    /// ===================================================================================
    /// TEST 4 — Delete a method a DIFFERENT, unedited file still calls
    /// ===================================================================================
    ///   The honest cost of the design. Read the expectation carefully.
    ///
    ///   EDIT:   Delete SharedHelper() below. Do NOT touch TestInstaReloadCaller.cs.
    ///   EXPECT: Reload APPLIES and the warning names SharedHelper,
    ///           BUT "[Test] SharedHelper ORIGINAL" KEEPS LOGGING every second.
    ///
    ///           That is intended. TestInstaReloadCaller.cs was not recompiled, so its call
    ///           site still points at the original method — still in memory, still running its
    ///           old body. Nothing crashes; the code is simply stale.
    ///
    ///           NOTE: on EXITING Play Mode, Unity recompiles everything and you get a normal
    ///           compile error in TestInstaReloadCaller.cs. That is the backstop — the stale
    ///           window is bounded by Play Mode, and the compiler catches it afterwards.
    ///           Undo the deletion (Ctrl+Z) to clear it.
    /// </summary>
    public sealed class TestInstaReload : MonoBehaviour
    {
        private float timer;

        // NOTE: this file deliberately does NOT reference TestInstaReloadCaller.
        // InstaReload compiles ONE FILE at a time, against the last assembly Unity built
        // successfully. A type defined in another file that Unity has not compiled yet cannot
        // be resolved, and the hot compile fails with CS0246 instead of patching. The caller
        // references this class; this class must never reference the caller.
        private void Update()
        {
            timer += Time.deltaTime;
            if (timer < 1.0f)
            {
                return;
            }

            timer = 0f;
            Debug.Log($"[Test] tick {Time.time:F1}s");

            // TEST 3 edits this call.
            Debug.Log($"[Test] Add = {Add(2, 3)}  (frame {Time.frameCount})");

            // TEST 2 deletes this call.
            DoomedMethod();
        }

        // ---- TEST 1 target: delete this, nothing calls it -----------------------------
        private void UnusedMethod()
        {
            Debug.Log($"[Test] UnusedMethod ORIGINAL (frame {Time.frameCount})");
        }

        // ---- TEST 2 target: delete this AND its call in Update() ----------------------
        private void DoomedMethod()
        {
            Debug.Log($"[Test] DoomedMethod ORIGINAL (frame {Time.frameCount})");
        }

        // ---- TEST 3 target: change this signature to (int a, int b, int c) ------------
        private int Add(int a, int b)
        {
            return a + b;
        }

        // ---- TEST 4 target: delete this. TestInstaReloadCaller.cs still calls it. -----
        // The frame number matters: it makes every call a DISTINCT console message, so Unity's
        // Console "Collapse" cannot fold the post-deletion calls into the pre-deletion row and
        // make surviving stale code look like it stopped.
        public void SharedHelper()
        {
            Debug.Log($"[Test] SharedHelper ORIGINAL (frame {Time.frameCount})");
        }
    }
}
