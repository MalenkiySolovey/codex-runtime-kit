using System.Text.Json;

namespace Codex.RuntimeKit.AppServer;

/// <summary>An RPC error returned by app-server.</summary>
public sealed class CodexAppServerRpcException(string message, int? code, JsonElement? data)
    : InvalidOperationException(message)
{
    public int? Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;
}

/// <summary>The child emitted malformed or structurally invalid app-server transport data.</summary>
public sealed class CodexAppServerProtocolException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>The owned app-server process exited while the connection was in use.</summary>
public sealed class CodexAppServerProcessExitedException(int processId, int? exitCode)
    : IOException($"Codex app-server process {processId} exited unexpectedly (exit code: {exitCode?.ToString() ?? "unknown"}).")
{
    public int ProcessId { get; } = processId;
    public int? ExitCode { get; } = exitCode;
}
