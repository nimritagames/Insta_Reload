using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Nimrita.InstaReload.Tests
{
    /// <summary>
    /// MANUAL probe for the one thing InstaReloadSuite cannot reach: a STRUCTURAL edit to an async
    /// method. The suite grades a marker flip, which only changes a constant inside MoveNext. Adding
    /// a local that lives across an await, or adding another await, changes the state machine's
    /// FIELD SET and STATE COUNT - a different and much riskier thing to patch, and the area that
    /// killed the Editor twice before async was enabled.
    ///
    /// HOW TO USE: attach to a GameObject, enter Play Mode, then edit Probe() below WHILE PLAYING -
    /// add an await, add a local, change what _seen is set to - and watch the [ASYNC] line. Its own
    /// log prefix keeps the result readable next to the suite's once-per-second line.
    ///
    /// MEASURED 2026-08-06, Play Mode, every edit made while running:
    ///   +1 string local across an await, +1 await  ->  seen=v2s    patched, no crash
    ///   +2 locals across awaits, +1 more await     ->  seen=v3sx   patched, no crash
    ///   +1 INT local, string-concatenated          ->  REFUSED, loudly:
    ///       "Missing field address access not supported: &lt;stamp&gt;5__1:System.Int32"
    ///
    /// That refusal is NOT async-specific. `"x" + someInt` emits ldflda to reach Int32.ToString(),
    /// and IsFieldRewriteSupported refuses address access to any field missing from the runtime map
    /// - which every NEWLY ADDED field is, async or not. It fails safe: the old body keeps running
    /// and the counter below keeps climbing.
    /// </summary>
    public sealed class AsyncShapeProbe : MonoBehaviour
    {
        private float _timer;
        private string _seen = "(pending)";
        private int _completions;

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 1.0f)
            {
                return;
            }

            _timer = 0f;

            try
            {
                _ = Probe();
            }
            catch (Exception ex)
            {
                _seen = "THREW:" + ex.GetType().Name;
            }

            // Frame number so a console that folds duplicates cannot fake "still running", and
            // completions so a corrupted state machine shows up as a counter that stopped moving.
            Debug.Log($"[ASYNC] seen={_seen} completions={_completions} (frame {Time.frameCount})");
        }

        // EDIT ME while Play Mode is running. Baseline shape.
        private async Task Probe()
        {
            await Task.Yield();
            _completions++;
            _seen = "v1";
        }
    }
}
