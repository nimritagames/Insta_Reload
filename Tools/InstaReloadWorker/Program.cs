using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace InstaReloadWorker
{
    internal static class Program
    {
        private const int DefaultPort = 53530;
        private const int ProtocolVersion = 1;
        private const int MaxMessageSize = 64 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private static CompilationContext _context = new CompilationContext();
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
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    await HandleClientAsync(client).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Worker error: {ex.Message}");
                }
                finally
                {
                    client?.Close();
                }
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
                    var response = HandleInit(request);
                    await WriteMessageAsync(stream, response).ConfigureAwait(false);
                }
                else if (string.Equals(messageType, "compile", StringComparison.OrdinalIgnoreCase))
                {
                    var request = JsonSerializer.Deserialize<CompileRequest>(json, JsonOptions);
                    var response = HandleCompile(request);
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
                _context = CompilationContext.Create(request.References, request.Defines);
                return new InitResponse
                {
                    Type = "init_ack",
                    Success = true,
                    Error = string.Empty
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

                var optimization = request.IsFastPath ? OptimizationLevel.Debug : OptimizationLevel.Release;
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

            public static CompilationContext Create(IEnumerable<string> references, IEnumerable<string> defines)
            {
                var context = new CompilationContext
                {
                    Defines = defines == null ? Array.Empty<string>() : new List<string>(defines).ToArray()
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
        }

        private sealed class InitResponse
        {
            public string Type { get; set; } = string.Empty;
            public bool Success { get; set; }
            public string Error { get; set; } = string.Empty;
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
