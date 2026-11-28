using System.Text.Json;

namespace Codex.RuntimeKit.AppServer;

public sealed record CodexLoginResult(bool Success, string? Error = null);

public interface ICodexLoginSession : IAsyncDisposable
{
    string AuthUrl { get; }
    string LoginId { get; }
    Task<CodexLoginResult> Completion { get; }
}

/// <summary>Runs one browser login through an owned, isolated app-server connection.</summary>
public sealed class CodexAppServerLoginSession : ICodexLoginSession
{
    private readonly ICodexAppServerClient _client;
    private readonly TaskCompletionSource<CodexLoginResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private CodexAppServerLoginSession(ICodexAppServerClient client)
    {
        _client = client;
        _client.EventReceived += OnEvent;
        _client.ProcessExited += OnExit;
    }

    public string AuthUrl { get; private set; } = string.Empty;
    public string LoginId { get; private set; } = string.Empty;
    public Task<CodexLoginResult> Completion => _completion.Task;

    public static async Task<CodexAppServerLoginSession> StartAsync(
        AppServerClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.CodexHome))
            throw new ArgumentException("Login requires an absolute path to an isolated Codex home.", nameof(options));

        var session = new CodexAppServerLoginSession(new CodexAppServerClient(options));
        try
        {
            await session._client.StartAsync(cancellationToken).ConfigureAwait(false);
            var result = await session._client.RequestAsync("account/login/start",
                new { type = "chatgpt" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            session.LoginId = GetString(result, "loginId") ?? string.Empty;
            session.AuthUrl = GetString(result, "authUrl")
                ?? throw new InvalidOperationException("App-server did not return the authorization URL.");
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnEvent(object? sender, AppServerEventReceivedEventArgs message)
    {
        if (message.IsServerInitiatedRequest || message.Method != "account/login/completed" ||
            message.Parameters is not { ValueKind: JsonValueKind.Object } payload)
        {
            return;
        }

        var receivedId = GetString(payload, "loginId");
        if (LoginId.Length > 0 && receivedId is not null && receivedId != LoginId)
            return;

        var success = payload.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True;
        _completion.TrySetResult(new CodexLoginResult(success, GetString(payload, "error")));
    }

    private void OnExit(object? sender, AppServerProcessExitedEventArgs args) =>
        _completion.TrySetResult(new CodexLoginResult(false, "Codex exited before login completed."));

    private static string? GetString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _client.EventReceived -= OnEvent;
        _client.ProcessExited -= OnExit;
        try
        {
            if (LoginId.Length > 0 && _client.IsRunning && !Completion.IsCompleted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _client.RequestAsync("account/login/cancel", new { loginId = LoginId },
                    TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            // Cancellation is best effort. Disposal still closes the owned connection.
        }
        finally
        {
            _completion.TrySetResult(new CodexLoginResult(false, "Login canceled."));
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
