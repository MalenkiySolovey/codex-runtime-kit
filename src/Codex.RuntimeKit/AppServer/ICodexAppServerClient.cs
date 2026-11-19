using System.Text.Json;

namespace Codex.RuntimeKit.AppServer;

/// <summary>Bidirectional stdio client for the local Codex app-server.</summary>
public interface ICodexAppServerClient : IAsyncDisposable, IDisposable
{
    bool IsRunning { get; }
    event EventHandler<AppServerEventReceivedEventArgs>? EventReceived;
    event EventHandler<AppServerProcessExitedEventArgs>? ProcessExited;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}