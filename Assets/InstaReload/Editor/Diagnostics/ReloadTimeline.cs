using System.Diagnostics;

namespace Nimrita.InstaReload.Editor
{
    /// <summary>
    /// Per-reload stage timer. One instance travels with a single save -> patch cycle and
    /// stamps every stage boundary, so end-to-end latency is measured instead of inferred
    /// from the compile number alone.
    ///
    /// WHY THIS EXISTS:
    ///   The console previously reported only RoslynCompiler's own timing. Everything else
    ///   in the pipeline (debounce, queue waits, main-thread pickup, patching, callbacks)
    ///   was invisible, so an 11ms compile could sit inside a multi-second felt latency
    ///   with nothing in the logs to show where the time went.
    /// </summary>
    internal sealed class ReloadTimeline
    {
        /// <summary>
        /// Monotonic, thread-safe clock shared by all timelines.
        /// EditorApplication.timeSinceStartup is main-thread only and frame-quantized;
        /// T0 is stamped from the FileSystemWatcher's background thread.
        /// </summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private const double NotStamped = -1d;

        private readonly object _sync = new object();

        private double _detectedAtMs = NotStamped;
        private double _debounceEndMs = NotStamped;
        private double _analyzeEndMs = NotStamped;
        private double _assemblyResolvedMs = NotStamped;
        private double _compileStartMs = NotStamped;
        private double _compileEndMs = NotStamped;
        private double _pickupMs = NotStamped;
        private double _patchStartMs = NotStamped;
        private double _patchEndMs = NotStamped;
        private double _historyEndMs = NotStamped;
        private double _postPatchEndMs = NotStamped;
        private int _watcherEventCount;

        internal ReloadTimeline(string filePath)
        {
            FilePath = filePath;
        }

        internal string FilePath { get; }

        internal bool IsFastPath { get; private set; }

        internal int WatcherEventCount
        {
            get { lock (_sync) { return _watcherEventCount; } }
        }

        private static double Now => Clock.Elapsed.TotalMilliseconds;

        /// <summary>
        /// Called for every FileSystemWatcher event in a burst. The first call stamps T0;
        /// later calls only raise the counter, so a single save that fires several watcher
        /// events (temp-file + rename, as VS and Rider do) is still measured from the
        /// moment the user actually saved.
        /// </summary>
        internal void MarkWatcherEvent()
        {
            lock (_sync)
            {
                if (_detectedAtMs < 0)
                {
                    _detectedAtMs = Now;
                }

                _watcherEventCount++;
            }
        }

        internal void MarkDebounceEnd()
        {
            lock (_sync) { _debounceEndMs = Now; }
        }

        internal void MarkAnalyzeEnd(bool isFastPath)
        {
            lock (_sync)
            {
                _analyzeEndMs = Now;
                IsFastPath = isFastPath;
            }
        }

        /// <summary>
        /// Closes the span covering the file -> owning-assembly lookup, which is separate from
        /// the queue wait that follows it. Both used to be lumped into "queue", which hid which
        /// of the two was actually costing anything.
        /// </summary>
        internal void MarkAssemblyResolved()
        {
            lock (_sync) { _assemblyResolvedMs = Now; }
        }

        internal void MarkCompileStart()
        {
            lock (_sync) { _compileStartMs = Now; }
        }

        /// <summary>
        /// Stamped from inside the compile task so it always happens-before the main thread
        /// observes completion. The gap between this and <see cref="MarkPickup"/> is time the
        /// result sat finished while EditorApplication.update was starved.
        /// </summary>
        internal void MarkCompileEnd()
        {
            lock (_sync) { _compileEndMs = Now; }
        }

        internal void MarkPickup()
        {
            lock (_sync) { _pickupMs = Now; }
        }

        internal void MarkPatchStart()
        {
            lock (_sync) { _patchStartMs = Now; }
        }

        internal void MarkPatchEnd()
        {
            lock (_sync) { _patchEndMs = Now; }
        }

        /// <summary>
        /// Splits the post-patch span: everything before this mark is patch-history
        /// persistence, everything after is [InvokeOnHotReload] callback dispatch.
        /// If never stamped, the whole span is reported as callbacks.
        /// </summary>
        internal void MarkHistoryEnd()
        {
            lock (_sync) { _historyEndMs = Now; }
        }

        /// <summary>
        /// Closes the span covering everything after the IL patch lands: patch history
        /// persistence plus [InvokeOnHotReload] callbacks.
        /// </summary>
        internal void MarkPostPatchEnd()
        {
            lock (_sync) { _postPatchEndMs = Now; }
        }

        internal ReloadTimingSample BuildSample()
        {
            lock (_sync)
            {
                var endMs = LatestStampUnsafe();
                var totalMs = _detectedAtMs >= 0 && endMs >= 0 ? endMs - _detectedAtMs : 0d;

                var sample = new ReloadTimingSample
                {
                    FilePath = FilePath,
                    IsFastPath = IsFastPath,
                    WatcherEventCount = _watcherEventCount,
                    DebounceMs = SpanUnsafe(_detectedAtMs, _debounceEndMs),
                    AnalyzeMs = SpanUnsafe(_debounceEndMs, _analyzeEndMs),
                    AssemblyMs = SpanUnsafe(_analyzeEndMs, _assemblyResolvedMs),
                    QueueMs = _assemblyResolvedMs >= 0
                        ? SpanUnsafe(_assemblyResolvedMs, _compileStartMs)
                        : SpanUnsafe(_analyzeEndMs, _compileStartMs),
                    CompileMs = SpanUnsafe(_compileStartMs, _compileEndMs),
                    PickupMs = SpanUnsafe(_compileEndMs, _pickupMs),
                    PatchMs = SpanUnsafe(_patchStartMs, _patchEndMs),
                    PostPatchMs = SpanUnsafe(_patchEndMs, _postPatchEndMs),
                    HistoryMs = SpanUnsafe(_patchEndMs, _historyEndMs),
                    TotalMs = totalMs
                };

                // Anything the stages above didn't account for — main-thread stalls between
                // pickup and patch start, temp-file IO, console rendering. If this number is
                // large, the instrumentation is missing a real cost and should be extended.
                var accounted =
                    sample.DebounceMs + sample.AnalyzeMs + sample.AssemblyMs + sample.QueueMs +
                    sample.CompileMs + sample.PickupMs + sample.PatchMs + sample.PostPatchMs;
                sample.UnaccountedMs = totalMs > accounted ? totalMs - accounted : 0d;

                return sample;
            }
        }

        private double LatestStampUnsafe()
        {
            if (_postPatchEndMs >= 0) return _postPatchEndMs;
            if (_patchEndMs >= 0) return _patchEndMs;
            if (_pickupMs >= 0) return _pickupMs;
            if (_compileEndMs >= 0) return _compileEndMs;
            if (_analyzeEndMs >= 0) return _analyzeEndMs;
            if (_debounceEndMs >= 0) return _debounceEndMs;
            return NotStamped;
        }

        private static double SpanUnsafe(double fromMs, double toMs)
        {
            if (fromMs < 0 || toMs < 0 || toMs < fromMs)
            {
                return 0d;
            }

            return toMs - fromMs;
        }
    }

    /// <summary>
    /// Immutable snapshot of one reload's stage timings, in milliseconds.
    /// </summary>
    internal sealed class ReloadTimingSample
    {
        internal string FilePath { get; set; }
        internal bool IsFastPath { get; set; }
        internal int WatcherEventCount { get; set; }
        internal double DebounceMs { get; set; }
        internal double AnalyzeMs { get; set; }
        internal double AssemblyMs { get; set; }
        internal double QueueMs { get; set; }
        internal double CompileMs { get; set; }
        internal double PickupMs { get; set; }
        internal double PatchMs { get; set; }
        internal double PostPatchMs { get; set; }
        internal double HistoryMs { get; set; }
        internal double UnaccountedMs { get; set; }
        internal double TotalMs { get; set; }

        /// <summary>Callback dispatch = the post-patch span minus history persistence.</summary>
        internal double CallbacksMs => PostPatchMs - HistoryMs;

        internal string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(FilePath))
                {
                    return "<unknown>";
                }

                try
                {
                    return System.IO.Path.GetFileName(FilePath);
                }
                catch
                {
                    return FilePath;
                }
            }
        }

        internal string BuildBreakdownLine()
        {
            return
                $"debounce {DebounceMs:F0} | analyze {AnalyzeMs:F0} | assembly {AssemblyMs:F0} | queue {QueueMs:F0} | " +
                $"compile {CompileMs:F0} | pickup {PickupMs:F0} | patch {PatchMs:F0} | " +
                $"history {HistoryMs:F0} | callbacks {CallbacksMs:F0} | unaccounted {UnaccountedMs:F0}   " +
                $"(watcher events: {WatcherEventCount})";
        }
    }
}
