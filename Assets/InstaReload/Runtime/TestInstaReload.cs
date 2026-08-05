using UnityEngine;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// Hot reload smoke test. Attach to any GameObject and enter Play Mode.
    ///
    /// It logs one line per second. While Play Mode is RUNNING, edit the string returned by
    /// Message() below and save. The next line should carry your new text, with the frame
    /// number still climbing and no domain reload.
    ///
    /// Turn OFF "Collapse" in the Console first. With Collapse on, repeated identical messages
    /// fold into a single row, so code that is still running looks like it stopped. That is also
    /// why every line carries a frame number.
    /// </summary>
    public sealed class TestInstaReload : MonoBehaviour
    {
        private float _timer;

        // Keep this fixture self-contained. InstaReload compiles ONE file at a time against the
        // last assembly Unity built successfully, so referencing a type from another file that
        // Unity has not compiled yet fails with CS0246 instead of patching.
        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 1.0f)
            {
                return;
            }

            _timer = 0f;
            Debug.Log($"[Test] {Message()}  (frame {Time.frameCount})");
        }

        // EDIT ME during Play Mode.
        private string Message()
        {
            return "ORIGINAL";
        }
    }
}
