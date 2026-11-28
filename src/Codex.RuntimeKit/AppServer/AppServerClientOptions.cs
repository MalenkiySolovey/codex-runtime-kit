using System.Diagnostics;
using System.Text.Json;

namespace Codex.RuntimeKit.AppServer;

/// <summary>Metadata sent during the app-server initialize handshake.</summary>
public sealed record AppServerClientInfo(string Name, string Title, string Version);

/// <summary>Creates one isolated local app-server connection.</summary>
public sealed record AppServerClientOptions
{
    public string ExecutablePath { get; init; } = string.Empty;
    public string CodexHome { get; init; } = string.Empty;
    public IReadOnlyList<string> ExecutableArgumentPrefix { get; init; } = [];
    public bool ExplicitStdioListener { get; init; } = true;
    public bool AllowNonJsonOutput { get; init; }
    public AppServerClientInfo ClientInfo { get; init; } = new("Codex.RuntimeKit", "Codex Runtime Kit", "0.1.0");
    public TimeSpan ProcessStartTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DefaultRequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ForcedShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public IAppServerProcessLifecycleObserver? ProcessLifecycleObserver { get; init; }
}

/// <summary>Optional host-owned process registration hooks. The kit invokes these only for the child it starts.</summary>
public interface IAppServerProcessLifecycleObserver
{
    ValueTask<IDisposable?> AcquireStartLeaseAsync(CancellationToken cancellationToken);
    void RegisterStartedProcess(Process process);
    void UnregisterExitedProcess(int processId);
}

/// <summary>An app-server notification or server-initiated request received on stdio.</summary>
public sealed class AppServerEventReceivedEventArgs : EventArgs
{
    internal AppServerEventReceivedEventArgs(string method, JsonElement? parameters, JsonElement? requestId, JsonElement rawMessage)
    {
        Method = method;
        Parameters = parameters;
        RequestId = requestId;
        RawMessage = rawMessage;
    }

    public string Method { get; }
    public JsonElement? Parameters { get; }
    public JsonElement? RequestId { get; }
    public bool IsServerInitiatedRequest => RequestId.HasValue;
    public JsonElement RawMessage { get; }
}

/// <summary>Raised when the owned app-server process exits.</summary>
public sealed class AppServerProcessExitedEventArgs(int processId, int? exitCode) : EventArgs
{
    public int ProcessId { get; } = processId;
    public int? ExitCode { get; } = exitCode;
}
