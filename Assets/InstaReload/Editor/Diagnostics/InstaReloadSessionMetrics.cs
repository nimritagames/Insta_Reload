using System;
using System.Collections.Generic;
using UnityEditor;

namespace Nimrita.InstaReload.Editor
{
    internal enum InstaReloadOperationStatus
    {
        Idle,
        Compiling,
        Patching,
        Succeeded,
        Failed
    }

    internal enum InstaReloadCompilePath
    {
        Unknown,
        Fast,
        Slow
    }

    internal sealed class InstaReloadSessionSnapshot
    {
        public InstaReloadOperationStatus Status { get; internal set; }
        public string StatusDetail { get; internal set; }
        public string LastFileName { get; internal set; }
        public string LastAssemblyName { get; internal set; }
        public double LastCompileMs { get; internal set; }
        public InstaReloadCompilePath LastCompilePath { get; internal set; }
        public double FastCompileAverageMs { get; internal set; }
        public int FastCompileCount { get; internal set; }
        public double SlowCompileAverageMs { get; internal set; }
        public int SlowCompileCount { get; internal set; }
        public double LastPatchMs { get; internal set; }
        public double PatchAverageMs { get; internal set; }
        public int PatchAttemptCount { get; internal set; }
        public int PatchSuccessCount { get; internal set; }
        public int PatchFailureCount { get; internal set; }
        public int LastPatchedCount { get; internal set; }
        public int LastDispatchedCount { get; internal set; }
        public int LastTrampolineCount { get; internal set; }
        public int LastSkippedCount { get; internal set; }
        public int LastErrorCount { get; internal set; }
        public string LastErrorSummary { get; internal set; }
        public double LastUpdateTime { get; internal set; }
    }

    internal static class InstaReloadSessionMetrics
    {
        private static readonly object Sync = new object();
        private static InstaReloadOperationStatus _status = InstaReloadOperationStatus.Idle;
        private static string _statusDetail = "Idle";
        private static string _lastFileName = string.Empty;
        private static string _lastAssemblyName = string.Empty;
        private static double _lastCompileMs;
        private static InstaReloadCompilePath _lastCompilePath = InstaReloadCompilePath.Unknown;
        private static int _fastCompileCount;
        private static double _fastCompileTotalMs;
        private static int _slowCompileCount;
        private static double _slowCompileTotalMs;
        private static double _lastPatchMs;
        private static int _patchAttemptCount;
        private static int _patchSuccessCount;
        private static int _patchFailureCount;
        private static double _patchTotalMs;
        private static int _lastPatchedCount;
        private static int _lastDispatchedCount;
        private static int _lastTrampolineCount;
        private static int _lastSkippedCount;
        private static int _lastErrorCount;
        private static string _lastErrorSummary = string.Empty;
        private static double _lastUpdateTime;

        internal static void Reset()
        {
            lock (Sync)
            {
                _status = InstaReloadOperationStatus.Idle;
                _statusDetail = "Idle";
                _lastFileName = string.Empty;
                _lastAssemblyName = string.Empty;
                _lastCompileMs = 0;
                _lastCompilePath = InstaReloadCompilePath.Unknown;
                _fastCompileCount = 0;
                _fastCompileTotalMs = 0;
                _slowCompileCount = 0;
                _slowCompileTotalMs = 0;
                _lastPatchMs = 0;
                _patchAttemptCount = 0;
                _patchSuccessCount = 0;
                _patchFailureCount = 0;
                _patchTotalMs = 0;
                _lastPatchedCount = 0;
                _lastDispatchedCount = 0;
                _lastTrampolineCount = 0;
                _lastSkippedCount = 0;
                _lastErrorCount = 0;
                _lastErrorSummary = string.Empty;
                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static void SetStatus(InstaReloadOperationStatus status, string detail = null)
        {
            lock (Sync)
            {
                _status = status;
                _statusDetail = string.IsNullOrEmpty(detail) ? status.ToString() : detail;
                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static void RecordCompileStart(string filePath, bool isFastPath)
        {
            lock (Sync)
            {
                _status = InstaReloadOperationStatus.Compiling;
                _statusDetail = isFastPath ? "Compiling (fast)" : "Compiling (slow)";
                _lastFileName = SafeFileName(filePath);
                _lastCompilePath = isFastPath ? InstaReloadCompilePath.Fast : InstaReloadCompilePath.Slow;
                _lastErrorSummary = string.Empty;
                _lastErrorCount = 0;
                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static void RecordCompileResult(
            bool success,
            double compileMs,
            bool isFastPath,
            IReadOnlyList<string> errors,
            string errorMessage = null)
        {
            lock (Sync)
            {
                _lastCompileMs = compileMs;
                if (isFastPath)
                {
                    _fastCompileCount++;
                    _fastCompileTotalMs += compileMs;
                    _lastCompilePath = InstaReloadCompilePath.Fast;
                }
                else
                {
                    _slowCompileCount++;
                    _slowCompileTotalMs += compileMs;
                    _lastCompilePath = InstaReloadCompilePath.Slow;
                }

                if (!success)
                {
                    _status = InstaReloadOperationStatus.Failed;
                    _statusDetail = "Compilation failed";
                    _lastErrorSummary = BuildErrorSummary("Compile", errors, errorMessage);
                    _lastErrorCount = errors != null && errors.Count > 0 ? errors.Count : 1;
                }
                else
                {
                    _lastErrorSummary = string.Empty;
                    _lastErrorCount = 0;
                }

                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static void RecordPatchStart(string assemblyName)
        {
            lock (Sync)
            {
                _status = InstaReloadOperationStatus.Patching;
                _statusDetail = "Patching";
                _lastAssemblyName = assemblyName ?? string.Empty;
                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static void RecordPatchResult(PatchApplyResult result, double patchMs)
        {
            lock (Sync)
            {
                _lastPatchMs = patchMs;
                _patchTotalMs += patchMs;
                _patchAttemptCount++;

                if (result == null)
                {
                    _patchFailureCount++;
                    _status = InstaReloadOperationStatus.Failed;
                    _statusDetail = "Patch failed";
                    _lastErrorSummary = "Patch failed (no result)";
                    _lastErrorCount = 1;
                    _lastUpdateTime = EditorApplication.timeSinceStartup;
                    return;
                }

                _lastPatchedCount = result.PatchedCount;
                _lastDispatchedCount = result.DispatchedCount;
                _lastTrampolineCount = result.TrampolineCount;
                _lastSkippedCount = result.SkippedCount;
                _lastErrorCount = result.Errors != null ? result.Errors.Count : 0;

                if (result.AppliedAny)
                {
                    _patchSuccessCount++;
                    _status = InstaReloadOperationStatus.Succeeded;
                    _statusDetail = "Patch applied";
                }
                else
                {
                    _patchFailureCount++;
                    _status = InstaReloadOperationStatus.Failed;
                    _statusDetail = _lastErrorCount > 0 ? "Patch failed" : "No methods updated";
                }

                if (_lastErrorCount > 0)
                {
                    _lastErrorSummary = BuildErrorSummary("Patch", result.Errors);
                }
                else
                {
                    _lastErrorSummary = string.Empty;
                }

                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static void RecordFailure(string message)
        {
            lock (Sync)
            {
                _status = InstaReloadOperationStatus.Failed;
                _statusDetail = "Failed";
                _lastErrorSummary = message ?? "Failure";
                _lastErrorCount = 1;
                _lastUpdateTime = EditorApplication.timeSinceStartup;
            }
        }

        internal static InstaReloadSessionSnapshot GetSnapshot()
        {
            lock (Sync)
            {
                return new InstaReloadSessionSnapshot
                {
                    Status = _status,
                    StatusDetail = _statusDetail,
                    LastFileName = _lastFileName,
                    LastAssemblyName = _lastAssemblyName,
                    LastCompileMs = _lastCompileMs,
                    LastCompilePath = _lastCompilePath,
                    FastCompileAverageMs = _fastCompileCount > 0 ? _fastCompileTotalMs / _fastCompileCount : 0,
                    FastCompileCount = _fastCompileCount,
                    SlowCompileAverageMs = _slowCompileCount > 0 ? _slowCompileTotalMs / _slowCompileCount : 0,
                    SlowCompileCount = _slowCompileCount,
                    LastPatchMs = _lastPatchMs,
                    PatchAverageMs = _patchAttemptCount > 0 ? _patchTotalMs / _patchAttemptCount : 0,
                    PatchAttemptCount = _patchAttemptCount,
                    PatchSuccessCount = _patchSuccessCount,
                    PatchFailureCount = _patchFailureCount,
                    LastPatchedCount = _lastPatchedCount,
                    LastDispatchedCount = _lastDispatchedCount,
                    LastTrampolineCount = _lastTrampolineCount,
                    LastSkippedCount = _lastSkippedCount,
                    LastErrorCount = _lastErrorCount,
                    LastErrorSummary = _lastErrorSummary,
                    LastUpdateTime = _lastUpdateTime
                };
            }
        }

        private static string SafeFileName(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return string.Empty;
            }

            try
            {
                return System.IO.Path.GetFileName(filePath);
            }
            catch
            {
                return filePath;
            }
        }

        private static string BuildErrorSummary(string prefix, IReadOnlyList<string> errors, string fallbackMessage = null)
        {
            if (errors != null && errors.Count > 0 && !string.IsNullOrEmpty(errors[0]))
            {
                return $"{prefix}: {errors[0]}";
            }

            if (!string.IsNullOrEmpty(fallbackMessage))
            {
                return $"{prefix}: {fallbackMessage}";
            }

            return $"{prefix}: error reported";
        }
    }
}
