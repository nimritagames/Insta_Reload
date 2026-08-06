using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nimrita.InstaReload.Editor
{
    /// <summary>
    /// STRUCTURED EVENT SINK — the machine-readable half of "nothing succeeds silently".
    ///
    /// The console is for a human and must stay at one line per reload; volume has destroyed
    /// evidence here twice (a component logging ~180 lines/second evicted InstaReload's own output
    /// from Unity's ring buffer and produced a wrong conclusion, and fdb4283 exists to cut noise).
    /// So decisions are recorded HERE instead, one JSON object per line, and the console keeps its
    /// summary.
    ///
    /// WHY A FILE AND NOT THE CONSOLE: Unity's console is a ring buffer and it LOST patcher output
    /// during the 2026-08-06 session - the lines existed and then did not. A file does not evict.
    /// It is also greppable, countable, and joinable, which the console is not.
    ///
    /// WHY EVERY RECORD CARRIES A RELOAD ID: on 2026-08-06 the regression suite reported
    /// Boxed`1.BothAxes as patched while the patcher had logged "NO instantiation patched" for the
    /// same method in the same reload. Both were speaking; nothing tied them together, so resolving
    /// the contradiction took manual detective work. With a shared reload id it is one query.
    ///
    /// WHY REASON CODES AND NOT SENTENCES: "async state machine (X.MoveNext) cannot be patched yet"
    /// cannot be counted or grepped reliably and breaks the moment someone improves the wording.
    /// A record carries a stable <see cref="Reason"/> code; the sentence goes in `detail`.
    ///
    /// THIS CLASS OBEYS ITS OWN RULE. A sink that swallows its own failures would be the exact bug
    /// it exists to prevent, so a write failure is reported to the console once per session rather
    /// than caught and ignored.
    /// </summary>
    internal static class InstaReloadEvents
    {
        /// <summary>Stable event codes. Never reword these - they are queried.</summary>
        internal static class Ev
        {
            internal const string MethodPatched = "method.patched";
            internal const string MethodRefused = "method.refused";
            internal const string MethodFailed = "method.failed";
            internal const string HookInstalled = "hook.installed";
            internal const string TokenFallthrough = "token.fallthrough";
            internal const string LookupMiss = "lookup.miss";
            internal const string ResolveFailed = "resolve.failed";
            internal const string TypeChangeIgnored = "type.change_ignored";
            internal const string ReloadSummary = "reload.summary";
            internal const string SuiteResult = "suite.result";
        }

        /// <summary>Stable reason codes. Never reword these - they are counted.</summary>
        internal static class Reason
        {
            internal const string AsyncStateMachine = "async_state_machine";
            internal const string FieldAddressAccess = "field_address_access";
            internal const string OpenGenericMethod = "open_generic_method";
            internal const string UnsupportedOperand = "unsupported_operand";
            internal const string MissingRuntimeMethod = "missing_runtime_method";
            internal const string ExternalAssembly = "external_assembly";
            internal const string ResolveThrew = "resolve_threw";
            internal const string BaseTypeChanged = "base_type_changed";
            internal const string InterfacesChanged = "interfaces_changed";
            internal const string Exception = "exception";
        }

        internal const string SeverityInfo = "info";
        internal const string SeverityWarn = "warn";
        internal const string SeverityError = "error";

        // Buffered because patching runs on a ~59ms budget and per-event file I/O would show up in
        // it. Records are held in memory and flushed when the reload ends.
        private static readonly List<string> Pending = new List<string>(64);
        private static readonly object Sync = new object();

        // file -> how many times it has been reloaded this domain. The one-generation-stale bug on
        // 2026-08-06 was only visible because generations could be told apart, so it is a field.
        private static readonly Dictionary<string, int> Generations =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static int _nextReloadId = 1;
        private static int _externalFallthroughs;
        private static bool _sinkFailureReported;

        private const long MaxBytes = 8 * 1024 * 1024;

        internal static string LogPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "InstaReload", "events.jsonl");

        /// <summary>
        /// The reload currently being applied, or 0 outside one. Ambient ON PURPOSE: the patcher is
        /// a wall of static helpers, and threading an id through ~20 signatures in the file that has
        /// regressed on five consecutive fixes buys nothing and risks a great deal. Patching runs on
        /// the main thread, one reload at a time, so an ambient value is well defined here.
        /// </summary>
        internal static int CurrentReload { get; private set; }

        internal static void BeginReload(int reloadId)
        {
            CurrentReload = reloadId;
            lock (Sync)
            {
                _externalFallthroughs = 0;
            }
        }

        /// <summary>
        /// A benign fall-through - a reference to an assembly that can only have one copy, so
        /// leaving it alone is correct. COUNTED, NOT RECORDED: the first version of this sink wrote
        /// one line each and produced 94KB for a single reload, 100% of it noise (33x
        /// System.Object::.ctor(), 32x Type::GetTypeFromHandle). That is the console-flooding
        /// failure this file exists to avoid, rebuilt in a file. The count still reaches the reload
        /// summary, so the information is not lost - only the repetition is.
        /// The interesting case, a fall-through binding back into OUR assembly, is still recorded
        /// individually, because that one means something is wrong.
        /// </summary>
        internal static void CountExternalFallthrough()
        {
            lock (Sync)
            {
                _externalFallthroughs++;
            }
        }

        internal static int ExternalFallthroughCount
        {
            get { lock (Sync) { return _externalFallthroughs; } }
        }

        /// <summary>
        /// The most recently completed reload. Play Mode code grades CONTINUOUSLY while a reload is
        /// a moment, so a harness verdict almost never lands inside a reload scope - it lands after
        /// one. Attributing it to reload 0 was technically honest and practically useless: it broke
        /// the very join the reload id exists to provide. A verdict is attributed to the reload it
        /// is reporting ON.
        /// </summary>
        internal static int LastCompletedReload { get; private set; }

        /// <summary>
        /// Which reload a runtime observation belongs to: the one in progress if there is one,
        /// otherwise the last one that finished.
        /// </summary>
        internal static int AttributionReload =>
            CurrentReload != 0 ? CurrentReload : LastCompletedReload;

        /// <summary>Ends the current reload and writes its records. Always pair with BeginReload.</summary>
        internal static void EndReload()
        {
            Flush();
            LastCompletedReload = CurrentReload;
            CurrentReload = 0;
        }

        /// <summary>Records against the reload in progress. The form call sites should use.</summary>
        internal static void Record(
            string phase,
            string eventCode,
            string severity,
            string file = null,
            string target = null,
            string reason = null,
            string detail = null,
            string extraJson = null)
        {
            Record(CurrentReload, phase, eventCode, severity, file, target, reason, detail, extraJson);
        }

        /// <summary>Monotonic per-domain id. Every record from one reload shares it.</summary>
        internal static int NextReloadId()
        {
            lock (Sync)
            {
                return _nextReloadId++;
            }
        }

        /// <summary>How many times this file has been reloaded in this domain, 1-based.</summary>
        internal static int NextGeneration(string filePath)
        {
            var key = filePath ?? string.Empty;
            lock (Sync)
            {
                Generations.TryGetValue(key, out var count);
                count++;
                Generations[key] = count;
                return count;
            }
        }

        /// <param name="extraJson">
        /// Pre-built JSON fragment WITHOUT braces, e.g. <c>"instantiations":4,"scope":"netstandard"</c>.
        /// Raw on purpose: this runs inside the patch loop and a dictionary per record would cost
        /// more than the record is worth.
        /// </param>
        internal static void Record(
            int reload,
            string phase,
            string eventCode,
            string severity,
            string file = null,
            string target = null,
            string reason = null,
            string detail = null,
            string extraJson = null)
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"ts\":\"")
              .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
              .Append("\",\"reload\":").Append(reload.ToString(CultureInfo.InvariantCulture));

            // Reload 0 means "emitted outside any reload scope" - the play-mode-entry patch replay
            // does not build a timeline. Marked explicitly so it can never be read as reload #0 or
            // quietly mis-attributed to whichever reload happens to flush it.
            if (reload == 0)
            {
                sb.Append(",\"unscoped\":true");
            }

            AppendField(sb, "phase", phase);
            AppendField(sb, "event", eventCode);
            AppendField(sb, "severity", severity);
            AppendField(sb, "file", file);
            AppendField(sb, "target", target);
            AppendField(sb, "reason", reason);
            AppendField(sb, "detail", detail);

            if (!string.IsNullOrEmpty(extraJson))
            {
                sb.Append(',').Append(extraJson);
            }

            sb.Append('}');

            lock (Sync)
            {
                Pending.Add(sb.ToString());
            }
        }

        /// <summary>
        /// Writes everything buffered. Called at the end of a reload, so the file is complete by the
        /// time anyone reads it.
        /// </summary>
        internal static void Flush()
        {
            string[] batch;
            lock (Sync)
            {
                if (Pending.Count == 0)
                {
                    return;
                }

                batch = Pending.ToArray();
                Pending.Clear();
            }

            try
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Keep one generation of history rather than growing without bound.
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    var previous = path + ".prev";
                    File.Delete(previous);
                    File.Move(path, previous);
                }

                File.AppendAllLines(path, batch);
            }
            catch (Exception ex)
            {
                // A sink that hides its own failure is the bug this class exists to prevent, so say
                // so - once, because a per-reload error would itself become noise.
                if (!_sinkFailureReported)
                {
                    _sinkFailureReported = true;
                    InstaReloadLogger.LogError(
                        $"[Events] structured event sink is NOT recording ({ex.GetType().Name}: {ex.Message}). " +
                        $"Console output is now the only record. Path: {LogPath}");
                }
            }
        }

        private static void AppendField(StringBuilder sb, string name, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            sb.Append(",\"").Append(name).Append("\":\"");
            Escape(sb, value);
            sb.Append('"');
        }

        private static void Escape(StringBuilder sb, string value)
        {
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }
        }
    }
}
