using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace InstaReloadWorker
{
    internal static class Program
    {
        private const int DefaultPort = 53530;
        private const int ProtocolVersion = 2;
        private const int MaxMessageSize = 64 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private static CompilationContext _context = new CompilationContext();

        /// <summary>Clients are now handled concurrently so a half-open connection cannot block
        /// the accept loop. _context is shared, so init and compile stay serialized behind this.</summary>
        private static readonly object ContextLock = new object();

        private static int _clientCounter;
        private static int _parentPid = -1;

        public static async Task<int> Main(string[] args)
        {
            int port = DefaultPort;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var parsedPort))
                    {
                        port = parsedPort;
                    }
                    i++;
                }
                else if (string.Equals(args[i], "--parentPid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var parsedPid))
                    {
                        _parentPid = parsedPid;
                    }
                    i++;
                }
            }

            TryLowerPriority();
            StartParentWatch();

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Worker accept error: {ex.Message}");
                    continue;
                }

                // Each client gets its own handler rather than being awaited inline. The worker
                // now outlives play mode, so a connection abandoned by a domain reload can sit
                // half-open indefinitely; awaiting it here would block the accept loop and
                // starve the live editor, hanging every compile with no error surfaced.
                _ = HandleClientLifetimeAsync(client);
            }
        }

        private static async Task HandleClientLifetimeAsync(TcpClient client)
        {
            var clientId = Interlocked.Increment(ref _clientCounter);
            Console.WriteLine($"Client {clientId} connected");
            try
            {
                await HandleClientAsync(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client {clientId} error: {ex.Message}");
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // Already torn down by the peer.
                }

                Console.WriteLine($"Client {clientId} disconnected");
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            using var stream = client.GetStream();

            while (client.Connected)
            {
                var json = await ReadMessageAsync(stream).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json))
                {
                    return;
                }

                var messageType = GetMessageType(json);
                if (string.IsNullOrEmpty(messageType))
                {
                    await WriteMessageAsync(stream, new ErrorResponse
                    {
                        Type = "error",
                        Error = "Missing message type"
                    }).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(messageType, "init", StringComparison.OrdinalIgnoreCase))
                {
                    var request = JsonSerializer.Deserialize<InitRequest>(json, JsonOptions);
                    InitResponse response;
                    lock (ContextLock)
                    {
                        response = HandleInit(request);
                    }
                    await WriteMessageAsync(stream, response).ConfigureAwait(false);
                }
                else if (string.Equals(messageType, "compile", StringComparison.OrdinalIgnoreCase))
                {
                    var request = JsonSerializer.Deserialize<CompileRequest>(json, JsonOptions);
                    CompileResponse response;
                    lock (ContextLock)
                    {
                        response = HandleCompile(request);
                    }
                    await WriteMessageAsync(stream, response).ConfigureAwait(false);
                }
                else if (string.Equals(messageType, "shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                else
                {
                    await WriteMessageAsync(stream, new ErrorResponse
                    {
                        Type = "error",
                        Error = $"Unknown message type: {messageType}"
                    }).ConfigureAwait(false);
                }
            }
        }

        private static InitResponse HandleInit(InitRequest? request)
        {
            if (request == null)
            {
                return new InitResponse
                {
                    Type = "init_ack",
                    Success = false,
                    Error = "Init request missing"
                };
            }

            if (request.ProtocolVersion != ProtocolVersion)
            {
                return new InitResponse
                {
                    Type = "init_ack",
                    Success = false,
                    Error = $"Protocol mismatch: {request.ProtocolVersion}"
                };
            }

            try
            {
                var projectPath = request.ProjectPath ?? string.Empty;

                // The worker now outlives play mode, so it can be adopted by a reconnecting
                // editor. It must never be re-pointed at a DIFFERENT project: that would
                // silently compile one project's edits against another's references.
                if (_context != null && _context.IsReady &&
                    !string.IsNullOrEmpty(_context.ProjectPath) &&
                    !string.Equals(_context.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Init rejected: worker is bound to {_context.ProjectPath}, caller is {projectPath}");
                    return new InitResponse
                    {
                        Type = "init_ack",
                        Success = false,
                        Error = $"Worker is bound to a different project: {_context.ProjectPath}"
                    };
                }

                // Rebuilding MetadataReferences re-reads every assembly and throws away the
                // warm state that persisting the process exists to preserve. Skip it when the
                // reconnecting editor sends the identical reference/define set.
                var contextKey = CompilationContext.BuildKey(request.References, request.Defines, projectPath);
                if (_context != null && _context.IsReady && _context.ContextKey == contextKey)
                {
                    Console.WriteLine($"Init: reusing warm context ({_context.References.Count} references)");
                    return new InitResponse
                    {
                        Type = "init_ack",
                        Success = true,
                        Error = string.Empty,
                        ContextReused = true,
                        ReferenceCount = _context.References.Count,
                        ProjectPath = projectPath
                    };
                }

                _context = CompilationContext.Create(request.References, request.Defines, projectPath);
                Console.WriteLine($"Init: built context ({_context.References.Count} references)");
                return new InitResponse
                {
                    Type = "init_ack",
                    Success = true,
                    Error = string.Empty,
                    ContextReused = false,
                    ReferenceCount = _context.References.Count,
                    ProjectPath = projectPath
                };
            }
            catch (Exception ex)
            {
                return new InitResponse
                {
                    Type = "init_ack",
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        private static CompileResponse HandleCompile(CompileRequest? request)
        {
            var response = new CompileResponse
            {
                Type = "compile_result",
                RequestId = request?.RequestId ?? string.Empty,
                IsFastPath = request != null && request.IsFastPath
            };

            if (request == null)
            {
                response.Success = false;
                response.ErrorMessage = "Compile request missing";
                return response;
            }

            if (_context == null || !_context.IsReady)
            {
                response.Success = false;
                response.ErrorMessage = "Worker not initialized";
                return response;
            }

            var totalTimer = Stopwatch.StartNew();

            try
            {
                var parseTimer = Stopwatch.StartNew();
                var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: _context.Defines);
                var syntaxTree = CSharpSyntaxTree.ParseText(request.SourceCode ?? string.Empty, parseOptions, request.FileName);
                parseTimer.Stop();

                // DEBUG ON BOTH PATHS. Release emit is what made async unpatchable: Roslyn emits an
                // async state machine as a STRUCT under Release and as a CLASS under Debug, while
                // Unity's own build is Debug. The slow path therefore produced a state machine that
                // structurally disagreed with the runtime one - logged as "base class changed
                // (System.Object -> System.ValueType)" plus a phantom removed .ctor - and patching
                // the outer method ended in a StackOverflowException that killed the Editor.
                // Matching Unity's own emit is also the honest default: we are patching INTO a Debug
                // build, so compiling the replacement as Release was never comparing like with like.
                var optimization = OptimizationLevel.Debug;
                var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization);
                var compilation = CSharpCompilation.Create(request.AssemblyName ?? "InstaReloadPatch", new[] { syntaxTree }, _context.References, compilationOptions);

                var emitTimer = Stopwatch.StartNew();
                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);
                emitTimer.Stop();

                totalTimer.Stop();

                response.ParseTimeMs = parseTimer.Elapsed.TotalMilliseconds;
                response.EmitTimeMs = emitTimer.Elapsed.TotalMilliseconds;
                response.AddTreeTimeMs = 0;
                response.CompilationTimeMs = totalTimer.Elapsed.TotalMilliseconds;

                if (emitResult.Success)
                {
                    response.Success = true;
                    response.OutputSize = (int)ms.Length;
                    response.AssemblyBytes = Convert.ToBase64String(ms.ToArray());
                }
                else
                {
                    response.Success = false;
                    response.Errors = new List<string>();
                    response.Warnings = new List<string>();

                    foreach (var diagnostic in emitResult.Diagnostics)
                    {
                        if (diagnostic.Severity == DiagnosticSeverity.Error)
                        {
                            response.Errors.Add(diagnostic.ToString());
                        }
                        else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                        {
                            response.Warnings.Add(diagnostic.ToString());
                        }
                    }

                    response.ErrorMessage = response.Errors.Count > 0
                        ? response.Errors[0]
                        : "Compilation failed";
                }
            }
            catch (Exception ex)
            {
                totalTimer.Stop();
                response.Success = false;
                response.ErrorMessage = ex.Message;
                response.CompilationTimeMs = totalTimer.Elapsed.TotalMilliseconds;
            }

            return response;
        }

        private static string? GetMessageType(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("type", out var typeElement))
                {
                    return typeElement.GetString();
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static async Task<string?> ReadMessageAsync(NetworkStream stream)
        {
            var lengthBuffer = new byte[4];
            int lengthRead = await ReadExactlyAsync(stream, lengthBuffer, 0, lengthBuffer.Length).ConfigureAwait(false);
            if (lengthRead < lengthBuffer.Length)
            {
                // Stream closed mid-header or returned 0 — treat as disconnect.
                return null;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length <= 0 || length > MaxMessageSize)
            {
                return null;
            }

            var payload = new byte[length];
            int payloadRead = await ReadExactlyAsync(stream, payload, 0, length).ConfigureAwait(false);
            if (payloadRead < length)
            {
                // Stream closed mid-payload — incomplete message, treat as disconnect.
                return null;
            }

            return Encoding.UTF8.GetString(payload);
        }

        private static async Task WriteMessageAsync(NetworkStream stream, object message)
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
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

        private static void TryLowerPriority()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Ignore priority failures
            }
        }

        private static void StartParentWatch()
        {
            if (_parentPid <= 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var parent = Process.GetProcessById(_parentPid);
                        if (parent.HasExited)
                        {
                            Environment.Exit(0);
                        }
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }
            });
        }

        private sealed class CompilationContext
        {
            public List<MetadataReference> References { get; private set; } = new List<MetadataReference>();
            public string[] Defines { get; private set; } = Array.Empty<string>();
            public bool IsReady { get; private set; }

            /// <summary>Identity of the inputs this context was built from, so a reconnecting
            /// editor sending the same set can reuse it instead of forcing a rebuild.</summary>
            public string ContextKey { get; private set; } = string.Empty;

            /// <summary>Project this worker is bound to. Guards against a second project
            /// adopting this worker and re-pointing it at different references.</summary>
            public string ProjectPath { get; private set; } = string.Empty;

            public static string BuildKey(IEnumerable<string>? references, IEnumerable<string>? defines, string projectPath)
            {
                var refs = references == null ? Array.Empty<string>() : new List<string>(references).ToArray();
                var defs = defines == null ? Array.Empty<string>() : new List<string>(defines).ToArray();
                Array.Sort(refs, StringComparer.Ordinal);
                Array.Sort(defs, StringComparer.Ordinal);
                return (projectPath ?? string.Empty) + "::" +
                       string.Join("|", refs) + "::" +
                       string.Join(";", defs);
            }

            public static CompilationContext Create(IEnumerable<string> references, IEnumerable<string> defines, string projectPath)
            {
                var context = new CompilationContext
                {
                    Defines = defines == null ? Array.Empty<string>() : new List<string>(defines).ToArray(),
                    ContextKey = BuildKey(references, defines, projectPath),
                    ProjectPath = projectPath ?? string.Empty
                };

                if (references != null)
                {
                    foreach (var path in references)
                    {
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        if (!File.Exists(path))
                        {
                            continue;
                        }

                        context.References.Add(MetadataReference.CreateFromFile(path));
                    }
                }

                context.IsReady = context.References.Count > 0;
                return context;
            }
        }

        private sealed class InitRequest
        {
            public string Type { get; set; } = string.Empty;
            public int ProtocolVersion { get; set; }
            public List<string> References { get; set; } = new List<string>();
            public List<string> Defines { get; set; } = new List<string>();
            public string ProjectPath { get; set; } = string.Empty;
        }

        private sealed class InitResponse
        {
            public string Type { get; set; } = string.Empty;
            public bool Success { get; set; }
            public string Error { get; set; } = string.Empty;
            public bool ContextReused { get; set; }
            public int ReferenceCount { get; set; }
            public string ProjectPath { get; set; } = string.Empty;
        }

        private sealed class CompileRequest
        {
            public string Type { get; set; } = string.Empty;
            public string RequestId { get; set; } = string.Empty;
            public string AssemblyName { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string SourceCode { get; set; } = string.Empty;
            public bool IsFastPath { get; set; }
        }

        private sealed class CompileResponse
        {
            public string Type { get; set; } = string.Empty;
            public string RequestId { get; set; } = string.Empty;
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
            public string AssemblyBytes { get; set; } = string.Empty;
            public double CompilationTimeMs { get; set; }
            public double ParseTimeMs { get; set; }
            public double AddTreeTimeMs { get; set; }
            public double EmitTimeMs { get; set; }
            public int OutputSize { get; set; }
            public bool IsFastPath { get; set; }
        }

        private sealed class ErrorResponse
        {
            public string Type { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
        }
    }
}
