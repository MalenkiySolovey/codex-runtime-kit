using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codex.RuntimeKit.AppServer;

/// <summary>Starts Codex app-server over stdio and implements its newline-delimited JSON request transport.</summary>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppServerClientOptions _options;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private long _nextId;
    private int _stopping;
    private int _disposed;
    private int _registeredProcessId;
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readerTask;
    private Task? _stderrTask;

    public CodexAppServerClient(AppServerClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null)
            {
                return false;
            }

            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public event EventHandler<AppServerEventReceivedEventArgs>? EventReceived;
    public event EventHandler<AppServerProcessExitedEventArgs>? ProcessExited;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("Client has already been started.");
            }

            Volatile.Write(ref _stopping, 0);
            var process = new Process
            {
                StartInfo = CreateStartInfo(),
                EnableRaisingEvents = true,
            };
            IDisposable? startLease = null;
            var processRegistered = false;
            var cleanupTransferred = false;
            try
            {
                if (_options.ProcessLifecycleObserver is not null)
                {
                    startLease = await _options.ProcessLifecycleObserver.AcquireStartLeaseAsync(cancellationToken).ConfigureAwait(false);
                }

                var startTask = Task.Run(process.Start, CancellationToken.None);
                bool started;
                try
                {
                    started = await startTask.WaitAsync(_options.ProcessStartTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _ = CleanupLateStartAsync(process, startTask, startLease);
                    cleanupTransferred = true;
                    startLease = null;
                    throw new TimeoutException("Timed out while starting Codex app-server.");
                }
                catch (OperationCanceledException)
                {
                    _ = CleanupLateStartAsync(process, startTask, startLease);
                    cleanupTransferred = true;
                    startLease = null;
                    throw;
                }

                if (!started)
                {
                    throw new InvalidOperationException("Windows or the operating system refused to start Codex app-server.");
                }

                var processId = process.Id;
                if (_options.ProcessLifecycleObserver is not null)
                {
                    Interlocked.Exchange(ref _registeredProcessId, processId);
                    processRegistered = true;
                }
                _options.ProcessLifecycleObserver?.RegisterStartedProcess(process);

                process.Exited += (_, _) => OnProcessExited(process, processId);
                _process = process;
                _stdin = process.StandardInput;
                _readerTask = ReadLoopAsync(process);
                _stderrTask = DrainStderrAsync(process);
            }
            catch
            {
                if (processRegistered)
                {
                    UnregisterOwnedProcess(process.Id);
                }

                if (!cleanupTransferred)
                {
                    TryKillOwnedProcess(process);
                    if (HasExited(process))
                    {
                        process.Dispose();
                    }
                    else
                    {
                        _ = DisposeWhenExitedAsync(process);
                    }
                }

                throw;
            }
            finally
            {
                if (!cleanupTransferred)
                {
                    startLease?.Dispose();
                }
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        try
        {
            await RequestAsync(
                "initialize",
                new { clientInfo = new { name = _options.ClientInfo.Name, title = _options.ClientInfo.Title, version = _options.ClientInfo.Version } },
                _options.InitializationTimeout,
                cancellationToken).ConfigureAwait(false);
            await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception startupException)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                throw new AggregateException("App-server initialization failed and bounded process cleanup also failed.", startupException, stopException);
            }

            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfDisposed();
        if (!IsRunning)
        {
            throw new InvalidOperationException("Codex app-server is not running.");
        }

        var limit = timeout ?? _options.DefaultRequestTimeout;
        ValidateTimeout(limit, nameof(timeout));
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Could not allocate a unique app-server request ID.");
        }

        var message = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"RPC request timed out waiting for '{method}'.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ThrowIfDisposed();
        if (!IsRunning)
        {
            throw new InvalidOperationException("Codex app-server is not running.");
        }

        var message = new Dictionary<string, object?>
        {
            ["method"] = method,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0 && _process is null)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var process = _process;
            if (process is null)
            {
                return;
            }

            FailAllPending(new OperationCanceledException("Codex app-server is stopping."));
            try { _stdin?.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            OperationCanceledException? cancellation = null;
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(_options.ShutdownGracePeriod, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TryKillOwnedProcess(process);
            }
            catch (OperationCanceledException ex)
            {
                cancellation = ex;
                TryKillOwnedProcess(process);
            }

            if (!HasExited(process))
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(_options.ForcedShutdownTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("Codex app-server did not exit within the bounded shutdown period.");
                }
            }

            CompleteExitedProcess(process);
            if (_readerTask is not null)
            {
                try { await _readerTask.WaitAsync(_options.ForcedShutdownTimeout).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (_stderrTask is not null)
            {
                try { await _stderrTask.WaitAsync(_options.ForcedShutdownTimeout).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }

            if (cancellation is not null)
            {
                throw cancellation;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _stopping, 1);
        FailAllPending(new ObjectDisposedException(nameof(CodexAppServerClient)));
        try { _stdin?.Close(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        var process = _process;
        if (process is not null)
        {
            TryKillOwnedProcess(process);
            if (HasExited(process))
            {
                CompleteExitedProcess(process);
            }
            else
            {
                _ = DisposeWhenExitedAsync(process);
            }
        }

        _writeLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _options.CodexHome,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
        };
        foreach (var argument in _options.ExecutableArgumentPrefix)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("app-server");
        if (_options.ExplicitStdioListener)
        {
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");
        }
        startInfo.Environment["CODEX_HOME"] = _options.CodexHome;
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("CODEX_API_KEY");
        return startInfo;
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var stdin = _stdin;
        if (stdin is null)
        {
            throw new CodexAppServerProtocolException("Codex app-server standard input is not open.");
        }

        var serialized = JsonSerializer.Serialize(payload, JsonOptions);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                throw new IOException("Codex app-server exited before the message could be written.");
            }

            await stdin.WriteAsync(serialized.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    HandleLine(line);
                }
                catch (JsonException) when (_options.AllowNonJsonOutput)
                {
                    // Some executable wrappers emit ordinary diagnostic lines before starting stdio.
                }
                catch (JsonException ex)
                {
                    var protocolException = new CodexAppServerProtocolException("Codex app-server emitted malformed JSON on stdout.", ex);
                    FailAllPending(protocolException);
                    TryKillOwnedProcess(process);
                    return;
                }
                catch (CodexAppServerProtocolException ex)
                {
                    FailAllPending(ex);
                    TryKillOwnedProcess(process);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                FailAllPending(new CodexAppServerProtocolException("Codex app-server stdout transport ended unexpectedly.", ex));
            }
        }
        finally
        {
            if (Volatile.Read(ref _stopping) == 0)
            {
                var processId = TryGetProcessId(process);
                if (await WaitForProcessExitAsync(process, _options.ForcedShutdownTimeout).ConfigureAwait(false))
                {
                    FailAllPending(new CodexAppServerProcessExitedException(processId, TryGetExitCode(process)));
                }
                else
                {
                    FailAllPending(new CodexAppServerProtocolException("Codex app-server stdout closed while its child process was still running."));
                    TryKillOwnedProcess(process);
                }
            }
        }
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // Drain stderr so the child cannot block. It can contain sensitive diagnostics and is not logged.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void HandleLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new CodexAppServerProtocolException("Codex app-server message must be a JSON object.");
        }

        if (root.TryGetProperty("method", out var methodElement))
        {
            if (methodElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                throw new CodexAppServerProtocolException("Codex app-server method name is missing or invalid.");
            }

            JsonElement? requestId = root.TryGetProperty("id", out var incomingId) ? incomingId.Clone() : null;
            JsonElement? parameters = root.TryGetProperty("params", out var incomingParameters) ? incomingParameters.Clone() : null;
            RaiseEvent(EventReceived, new AppServerEventReceivedEventArgs(methodElement.GetString()!, parameters, requestId, root.Clone()));
            return;
        }

        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id))
        {
            throw new CodexAppServerProtocolException("Codex app-server response is missing a numeric request ID.");
        }

        if (!root.TryGetProperty("result", out var result) && !root.TryGetProperty("error", out _))
        {
            throw new CodexAppServerProtocolException("Codex app-server response contains neither result nor error.");
        }

        if (!_pending.TryRemove(id, out var pending))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = "Unknown app-server RPC error.";
            int? code = null;
            JsonElement? data = null;
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
                {
                    message = messageElement.GetString() ?? message;
                }
                if (error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var numericCode))
                {
                    code = numericCode;
                }
                if (error.TryGetProperty("data", out var dataElement))
                {
                    data = dataElement.Clone();
                }
            }

            pending.TrySetException(new CodexAppServerRpcException($"app-server error: {message}", code, data));
        }
        else
        {
            pending.TrySetResult(result.Clone());
        }
    }

    private void OnProcessExited(Process process, int processId)
    {
        UnregisterOwnedProcess(processId);
        int? exitCode = TryGetExitCodeAfterExit(process);
        if (Volatile.Read(ref _stopping) == 0)
        {
            FailAllPending(new CodexAppServerProcessExitedException(processId, exitCode));
        }
        RaiseEvent(ProcessExited, new AppServerProcessExitedEventArgs(processId, exitCode));
    }

    private void CompleteExitedProcess(Process process)
    {
        var processId = TryGetProcessId(process);
        if (processId > 0)
        {
            UnregisterOwnedProcess(processId);
        }

        try { process.Dispose(); } catch (InvalidOperationException) { }
        if (ReferenceEquals(_process, process))
        {
            _process = null;
            _stdin = null;
            _readerTask = null;
            _stderrTask = null;
        }
    }

    private void UnregisterOwnedProcess(int processId)
    {
        if (processId <= 0 || Interlocked.CompareExchange(ref _registeredProcessId, 0, processId) != processId)
        {
            return;
        }

        try { _options.ProcessLifecycleObserver?.UnregisterExitedProcess(processId); }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var (id, completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void RaiseEvent<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args) where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
            {
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static int TryGetProcessId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return 0; }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private static int? TryGetExitCodeAfterExit(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                return null;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            return HasExited(process);
        }
    }

    private static void TryKillOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static async Task DisposeWhenExitedAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static async Task CleanupLateStartAsync(Process process, Task<bool> startTask, IDisposable? startLease)
    {
        try
        {
            if (await startTask.ConfigureAwait(false))
            {
                TryKillOwnedProcess(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
        }
        finally
        {
            process.Dispose();
            startLease?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static void ValidateOptions(AppServerClientOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CodexHome);
        ArgumentNullException.ThrowIfNull(options.ClientInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientInfo.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientInfo.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientInfo.Version);
        ValidateTimeout(options.ProcessStartTimeout, nameof(options.ProcessStartTimeout));
        ValidateTimeout(options.InitializationTimeout, nameof(options.InitializationTimeout));
        ValidateTimeout(options.DefaultRequestTimeout, nameof(options.DefaultRequestTimeout));
        ValidateTimeout(options.ShutdownGracePeriod, nameof(options.ShutdownGracePeriod));
        ValidateTimeout(options.ForcedShutdownTimeout, nameof(options.ForcedShutdownTimeout));
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(name, "Timeouts must be finite and greater than zero.");
        }
    }
}
