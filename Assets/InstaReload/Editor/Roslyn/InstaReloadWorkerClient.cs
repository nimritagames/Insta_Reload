using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.Roslyn
{
    internal enum InstaReloadWorkerState
    {
        Disabled,
        Idle,
        Building,
        Starting,
        Connecting,
        Connected,
        Failed
    }

    internal static class InstaReloadWorkerClient
    {
        private const int ProtocolVersion = 1;
        private const int ConnectTimeoutMs = 5000;
        private const int MaxMessageSize = 64 * 1024 * 1024;

        private static readonly object Sync = new object();
        private static readonly SemaphoreSlim RequestLock = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        private static InstaReloadWorkerState _state = InstaReloadWorkerState.Disabled;
        private static string _lastError = string.Empty;
        private static TcpClient _client;
        private static NetworkStream _stream;
        private static Process _workerProcess;
        private static Task _connectTask;
        private static CompileContext _desiredContext;
        private static string _activeContextHash;
        private static bool _shutdownRequested;
        private static int _mainThreadId;
        private static bool _mainThreadHooked;

        internal static InstaReloadWorkerState State => _state;
        internal static string LastError => _lastError;
        internal static bool IsConnected => _state == InstaReloadWorkerState.Connected;

        internal static bool EnsureReady()
        {
            EnsureMainThreadPump();

            var settings = InstaReloadSettings.GetOrCreateSettings();
            if (settings == null || !settings.Enabled || !settings.UseExternalWorker)
            {
                SetState(InstaReloadWorkerState.Disabled, string.Empty);
                Shutdown();
                return false;
            }

            var context = BuildContext(settings);
            if (context == null || context.References.Count == 0)
            {
                SetState(InstaReloadWorkerState.Failed, "Missing compilation references");
                return false;
            }

            var contextHash = ComputeContextHash(context);
            bool needsRestart = false;
            lock (Sync)
            {
                _desiredContext = context;
                if (_state == InstaReloadWorkerState.Connected && _activeContextHash != contextHash)
                {
                    needsRestart = true;
                }
            }

            if (needsRestart)
            {
                Shutdown();
            }

            lock (Sync)
            {
                if (_state == InstaReloadWorkerState.Connected && _activeContextHash == contextHash)
                {
                    return true;
                }

                if (_connectTask != null && !_connectTask.IsCompleted)
                {
                    return false;
                }

                _shutdownRequested = false;
                _connectTask = Task.Run(() => ConnectAsync(context, contextHash, settings));
                return false;
            }
        }

        internal static async Task<CompilationResult> CompileAsync(
            string sourceCode,
            string assemblyName,
            string fileName,
            bool isFastPath)
        {
            if (!IsConnected || _stream == null)
            {
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = "Worker not connected",
                    UsedFastPath = isFastPath
                };
            }

            await RequestLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var request = new CompileRequest
                {
                    type = "compile",
                    requestId = Guid.NewGuid().ToString("N"),
                    assemblyName = assemblyName,
                    fileName = fileName,
                    sourceCode = sourceCode,
                    isFastPath = isFastPath
                };

                await WriteMessageAsync(_stream, request).ConfigureAwait(false);

                var responseJson = await ReadMessageAsync(_stream).ConfigureAwait(false);
                if (string.IsNullOrEmpty(responseJson))
                {
                    SetState(InstaReloadWorkerState.Failed, "Worker disconnected");
                    return new CompilationResult
                    {
                        Success = false,
                        ErrorMessage = "Worker disconnected",
                        UsedFastPath = isFastPath
                    };
                }

                var messageType = GetMessageType(responseJson);
                if (!string.Equals(messageType, "compile_result", StringComparison.OrdinalIgnoreCase))
                {
                    return new CompilationResult
                    {
                        Success = false,
                        ErrorMessage = "Unexpected worker response",
                        UsedFastPath = isFastPath
                    };
                }

                var response = JsonUtility.FromJson<CompileResponse>(responseJson);
                return BuildCompilationResult(response, isFastPath);
            }
            catch (Exception ex)
            {
                SetState(InstaReloadWorkerState.Failed, ex.Message);
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = $"Worker error: {ex.Message}",
                    UsedFastPath = isFastPath
                };
            }
            finally
            {
                RequestLock.Release();
            }
        }

        internal static string GetStatusLine()
        {
            if (_state == InstaReloadWorkerState.Disabled)
            {
                return string.Empty;
            }

            var status = _state switch
            {
                InstaReloadWorkerState.Connected => "Connected",
                InstaReloadWorkerState.Connecting => "Connecting",
                InstaReloadWorkerState.Starting => "Starting",
                InstaReloadWorkerState.Building => "Building",
                InstaReloadWorkerState.Failed => "Failed",
                _ => "Idle"
            };

            return $"Worker: {status}";
        }

        internal static void Shutdown()
        {
            lock (Sync)
            {
                _shutdownRequested = true;
                _activeContextHash = null;
            }

            try
            {
                _stream?.Dispose();
            }
            catch
            {
                // Ignore shutdown errors
            }

            try
            {
                _client?.Close();
            }
            catch
            {
                // Ignore shutdown errors
            }

            _stream = null;
            _client = null;

            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    _workerProcess.Kill();
                }
            }
            catch
            {
                // Ignore shutdown errors
            }

            _workerProcess = null;
            if (_state != InstaReloadWorkerState.Disabled)
            {
                SetState(InstaReloadWorkerState.Idle, string.Empty);
            }
        }

        private static async Task ConnectAsync(CompileContext context, string contextHash, InstaReloadSettings settings)
        {
            try
            {
                SetState(InstaReloadWorkerState.Starting, string.Empty);
                if (!EnsureWorkerProcess(settings, context, out var workerError))
                {
                    SetState(InstaReloadWorkerState.Failed, workerError);
                    return;
                }

                SetState(InstaReloadWorkerState.Connecting, string.Empty);
                var client = new TcpClient();
                var connectTask = client.ConnectAsync("127.0.0.1", settings.WorkerPort);
                var timeoutTask = Task.Delay(ConnectTimeoutMs);
                var completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    throw new TimeoutException("Worker connection timed out");
                }
                await connectTask.ConfigureAwait(false);
                client.NoDelay = true;

                var stream = client.GetStream();
                var initRequest = new InitRequest
                {
                    type = "init",
                    protocolVersion = ProtocolVersion,
                    references = context.References,
                    defines = context.Defines
                };

                await WriteMessageAsync(stream, initRequest).ConfigureAwait(false);
                var initJson = await ReadMessageAsync(stream).ConfigureAwait(false);
                if (string.IsNullOrEmpty(initJson))
                {
                    SetState(InstaReloadWorkerState.Failed, "Worker init failed");
                    return;
                }

                var initResponse = JsonUtility.FromJson<InitResponse>(initJson);
                if (initResponse == null || !initResponse.success)
                {
                    SetState(InstaReloadWorkerState.Failed, initResponse?.error ?? "Worker init failed");
                    return;
                }

                lock (Sync)
                {
                    if (_shutdownRequested)
                    {
                        client.Close();
                        return;
                    }

                    _client = client;
                    _stream = stream;
                    _activeContextHash = contextHash;
                }

                SetState(InstaReloadWorkerState.Connected, string.Empty);
            }
            catch (Exception ex)
            {
                SetState(InstaReloadWorkerState.Failed, ex.Message);
            }
        }

        private static bool EnsureWorkerProcess(InstaReloadSettings settings, CompileContext context, out string error)
        {
            error = string.Empty;
            if (_workerProcess != null && !_workerProcess.HasExited)
            {
                return true;
            }

            var workerProjectPath = context.WorkerProjectPath;
            var workerDllPath = context.WorkerDllPath;

            if (settings.AutoStartWorker && !File.Exists(workerDllPath))
            {
                SetState(InstaReloadWorkerState.Building, string.Empty);
                if (!BuildWorker(workerProjectPath, out error))
                {
                    return false;
                }
            }

            if (!File.Exists(workerDllPath))
            {
                error = "Worker binary not found";
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{workerDllPath}\" --port {settings.WorkerPort} --parentPid {Process.GetCurrentProcess().Id}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = settings.VerboseLogging,
                    RedirectStandardError = settings.VerboseLogging
                };

                _workerProcess = Process.Start(startInfo);
                if (_workerProcess == null)
                {
                    error = "Failed to start worker";
                    return false;
                }

                if (settings.VerboseLogging)
                {
                    _workerProcess.OutputDataReceived += (_, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            InstaReloadLogger.LogVerbose($"[Worker] {args.Data}");
                        }
                    };
                    _workerProcess.ErrorDataReceived += (_, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            InstaReloadLogger.LogWarning($"[Worker] {args.Data}");
                        }
                    };
                    _workerProcess.BeginOutputReadLine();
                    _workerProcess.BeginErrorReadLine();
                }
            }
            catch (Exception ex)
            {
                error = $"Worker start failed: {ex.Message}";
                return false;
            }

            return true;
        }

        private static bool BuildWorker(string projectPath, out string error)
        {
            error = string.Empty;
            try
            {
                if (!File.Exists(projectPath))
                {
                    error = "Worker project file missing";
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{projectPath}\" -c Release",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    error = "Worker build failed to start";
                    return false;
                }

                if (!process.WaitForExit(30000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill errors
                    }

                    error = "Worker build timed out";
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    error = process.StandardError.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error = "Worker build failed";
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Worker build failed: {ex.Message}";
                return false;
            }

            return true;
        }

        private static CompileContext BuildContext(InstaReloadSettings settings)
        {
            var references = ReferenceResolver.GetAllReferences();
            var defines = GetDefineSymbols(settings);
            var workerProjectPath = GetWorkerProjectPath();
            var workerDllPath = GetWorkerDllPath();
            return new CompileContext(references, defines, workerProjectPath, workerDllPath);
        }

        private static List<string> GetDefineSymbols(InstaReloadSettings settings)
        {
            var defineSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            defineSet.Add("UNITY_EDITOR");

            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defineString = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
            if (!string.IsNullOrEmpty(defineString))
            {
                foreach (var define in defineString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    defineSet.Add(define.Trim());
                }
            }

            foreach (var unityDefine in GetUnityCompilationDefines())
            {
                defineSet.Add(unityDefine);
            }

            return new List<string>(defineSet);
        }

        private static IEnumerable<string> GetUnityCompilationDefines()
        {
            try
            {
                var internalUtility = Type.GetType("UnityEditorInternal.InternalEditorUtility, UnityEditor");
                if (internalUtility == null)
                {
                    return Array.Empty<string>();
                }

                var method = internalUtility.GetMethod("GetCompilationDefines", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method == null)
                {
                    return Array.Empty<string>();
                }

                var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
                var buildTarget = EditorUserBuildSettings.activeBuildTarget;
                var parameters = method.GetParameters();
                object[] args;
                if (parameters.Length == 2)
                {
                    args = new object[] { buildTargetGroup, buildTarget };
                }
                else if (parameters.Length == 3)
                {
                    args = new object[] { buildTargetGroup, buildTarget, false };
                }
                else
                {
                    return Array.Empty<string>();
                }

                var result = method.Invoke(null, args) as string[];
                return result ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string ComputeContextHash(CompileContext context)
        {
            if (context == null)
            {
                return string.Empty;
            }

            using var sha = SHA256.Create();
            var data = string.Join("|", context.References) + "::" + string.Join(";", context.Defines);
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }

        private static CompilationResult BuildCompilationResult(CompileResponse response, bool isFastPath)
        {
            if (response == null)
            {
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = "Worker returned no response",
                    UsedFastPath = isFastPath
                };
            }

            byte[] assemblyBytes = null;
            if (response.success && !string.IsNullOrEmpty(response.assemblyBytes))
            {
                try
                {
                    assemblyBytes = Convert.FromBase64String(response.assemblyBytes);
                }
                catch
                {
                    assemblyBytes = null;
                }
            }

            return new CompilationResult
            {
                Success = response.success,
                CompiledAssembly = assemblyBytes,
                ErrorMessage = response.errorMessage,
                CompilationTime = response.compilationTimeMs,
                ParseTimeMs = response.parseTimeMs,
                AddTreeTimeMs = response.addTreeTimeMs,
                EmitTimeMs = response.emitTimeMs,
                OutputSize = response.outputSize,
                UsedFastPath = response.isFastPath,
                Errors = response.errors ?? new List<string>(),
                Warnings = response.warnings ?? new List<string>()
            };
        }

        private static string GetMessageType(string json)
        {
            try
            {
                var envelope = JsonUtility.FromJson<MessageEnvelope>(json);
                return envelope?.type;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> ReadMessageAsync(NetworkStream stream)
        {
            var lengthBuffer = new byte[4];
            int lengthRead = await ReadExactlyAsync(stream, lengthBuffer, 0, lengthBuffer.Length).ConfigureAwait(false);
            if (lengthRead == 0)
            {
                return null;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length <= 0 || length > MaxMessageSize)
            {
                return null;
            }

            var payload = new byte[length];
            int payloadRead = await ReadExactlyAsync(stream, payload, 0, length).ConfigureAwait(false);
            if (payloadRead == 0)
            {
                return null;
            }

            return Encoding.UTF8.GetString(payload);
        }

        private static async Task WriteMessageAsync(NetworkStream stream, object message)
        {
            var json = JsonUtility.ToJson(message);
            var payload = Encoding.UTF8.GetBytes(json);
            var lengthBuffer = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);
            await stream.WriteAsync(lengthBuffer, 0, lengthBuffer.Length).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead).ConfigureAwait(false);
                if (read == 0)
                {
                    return totalRead;
                }

                totalRead += read;
            }

            return totalRead;
        }

        private static void SetState(InstaReloadWorkerState state, string error)
        {
            _state = state;
            _lastError = error ?? string.Empty;

            if (IsMainThread())
            {
                UpdateMetricsForState(state);
                return;
            }

            EnqueueMainThread(() => UpdateMetricsForState(state));
        }

        private static void UpdateMetricsForState(InstaReloadWorkerState state)
        {
            if (state == InstaReloadWorkerState.Connected)
            {
                InstaReloadSessionMetrics.SetStatus(InstaReloadOperationStatus.Idle, "Worker connected");
            }
            else if (state == InstaReloadWorkerState.Building)
            {
                InstaReloadSessionMetrics.SetStatus(InstaReloadOperationStatus.Idle, "Building worker");
            }
            else if (state == InstaReloadWorkerState.Starting)
            {
                InstaReloadSessionMetrics.SetStatus(InstaReloadOperationStatus.Idle, "Starting worker");
            }
            else if (state == InstaReloadWorkerState.Connecting)
            {
                InstaReloadSessionMetrics.SetStatus(InstaReloadOperationStatus.Idle, "Connecting worker");
            }
            else if (state == InstaReloadWorkerState.Failed)
            {
                InstaReloadSessionMetrics.SetStatus(InstaReloadOperationStatus.Failed, "Worker failed");
            }
        }

        private static void EnsureMainThreadPump()
        {
            if (_mainThreadHooked)
            {
                return;
            }

            _mainThreadHooked = true;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update += PumpMainThreadQueue;
        }

        private static bool IsMainThread()
        {
            return _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        private static void EnqueueMainThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            MainThreadQueue.Enqueue(action);
        }

        private static void PumpMainThreadQueue()
        {
            while (MainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    InstaReloadLogger.LogWarning($"[Worker] Main-thread callback failed: {ex.Message}");
                }
            }
        }

        private static string GetWorkerProjectPath()
        {
            var rootPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(rootPath, "Tools", "InstaReloadWorker", "InstaReloadWorker.csproj");
        }

        private static string GetWorkerDllPath()
        {
            var rootPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(rootPath, "Tools", "InstaReloadWorker", "bin", "Release", "net8.0", "InstaReloadWorker.dll");
        }

        private sealed class CompileContext
        {
            public CompileContext(List<string> references, List<string> defines, string workerProjectPath, string workerDllPath)
            {
                References = references ?? new List<string>();
                Defines = defines ?? new List<string>();
                WorkerProjectPath = workerProjectPath ?? string.Empty;
                WorkerDllPath = workerDllPath ?? string.Empty;
            }

            public List<string> References { get; }
            public List<string> Defines { get; }
            public string WorkerProjectPath { get; }
            public string WorkerDllPath { get; }
        }

        [Serializable]
        private sealed class MessageEnvelope
        {
            public string type;
        }

        [Serializable]
        private sealed class InitRequest
        {
            public string type;
            public int protocolVersion;
            public List<string> references = new List<string>();
            public List<string> defines = new List<string>();
        }

        [Serializable]
        private sealed class InitResponse
        {
            public string type;
            public bool success;
            public string error;
        }

        [Serializable]
        private sealed class CompileRequest
        {
            public string type;
            public string requestId;
            public string assemblyName;
            public string fileName;
            public string sourceCode;
            public bool isFastPath;
        }

        [Serializable]
        private sealed class CompileResponse
        {
            public string type;
            public string requestId;
            public bool success;
            public string errorMessage;
            public List<string> errors = new List<string>();
            public List<string> warnings = new List<string>();
            public string assemblyBytes;
            public double compilationTimeMs;
            public double parseTimeMs;
            public double addTreeTimeMs;
            public double emitTimeMs;
            public int outputSize;
            public bool isFastPath;
        }
    }
}
