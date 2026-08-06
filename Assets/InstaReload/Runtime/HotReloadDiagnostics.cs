using System;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// RUNTIME SEAM for the structured event sink, so a harness running in Play Mode can record
    /// into the same file the patcher does, under the same reload id.
    ///
    /// WHY A SEAM AND NOT A DIRECT CALL: the sink lives in the Nimrita.InstaReload.Editor assembly
    /// definition, and a predefined runtime assembly cannot reference it - nor should it, or the
    /// project would stop building for a player. So the runtime side exposes a delegate and the
    /// Editor fills it in, exactly like HotReloadBridge does for dispatch.
    ///
    /// WHY IT MATTERS: on 2026-08-06 the regression suite reported Boxed`1.BothAxes as patched while
    /// the patcher had logged "NO instantiation patched" for the same method in the same reload.
    /// Both were speaking. Nothing joined them, so resolving the contradiction took manual detective
    /// work. Once both write to one file under one reload id, that is a single query.
    ///
    /// NOT A SILENT DROP when nothing is subscribed: callers of this seam already log their result
    /// to the console, so an unsubscribed sink loses the JOIN, never the fact. In a player build
    /// there is no Editor to subscribe and no patcher to join against, which is the correct
    /// outcome rather than a missing one.
    /// </summary>
    public static class HotReloadDiagnostics
    {
        /// <summary>
        /// Set by the Editor. Parameters: event code, severity, and a JSON fragment WITHOUT braces.
        /// </summary>
        public static Action<string, string, string> Sink;

        /// <summary>
        /// Set by the Editor alongside <see cref="Sink"/>. A runtime observation has no reload end
        /// to ride along with, so without this it would sit in the buffer until some later reload
        /// happened to flush - appearing minutes late, or never.
        /// </summary>
        public static Action SinkFlush;

        public static bool IsConnected => Sink != null;

        private static bool _unconnectedReported;

        public static void Report(string eventCode, string severity, string extraJson)
        {
            var sink = Sink;
            if (sink == null)
            {
                // NOT SILENT. This return dropped every harness verdict during bring-up and looked
                // exactly like "the harness never reported" - the caller had already resolved this
                // method successfully, so its own guard could not see the problem. In a player build
                // an unconnected sink is correct and expected; in the Editor it means the seam was
                // never filled in, which is a defect. Say so once either way.
                if (!_unconnectedReported)
                {
                    _unconnectedReported = true;
                    UnityEngine.Debug.LogWarning(
                        $"[InstaReload] diagnostics seam not connected - '{eventCode}' and later runtime " +
                        "observations stay on the console only, and will not join the patcher's records.");
                }

                return;
            }

            _unconnectedReported = false;

            try
            {
                sink(eventCode, severity, extraJson);
                SinkFlush?.Invoke();
            }
            catch (Exception ex)
            {
                // Reporting must never take down the thing being observed, but a swallowed failure
                // here would be the exact bug this whole mechanism exists to prevent - so it goes
                // to the console, which is always available, and then gets out of the way.
                UnityEngine.Debug.LogWarning(
                    $"[InstaReload] diagnostics sink threw and was disconnected: {ex.GetType().Name}: {ex.Message}");
                Sink = null;
            }
        }
    }
}
