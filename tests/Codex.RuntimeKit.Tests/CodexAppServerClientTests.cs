using System.Collections.Concurrent;
using System.Diagnostics;
using Codex.RuntimeKit.AppServer;
using Codex.RuntimeKit.Tests.TestSupport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public async Task StartAsync_PerformsHandshakeAndCorrelatesConcurrentRequestsAndEvents()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("OPENAI_API_KEY", "synthetic-openai-marker"),
            ("CODEX_API_KEY", "synthetic-codex-marker")).Set();
        var options = CreateOptions(temp.Path, new TestLifecycleObserver());
        await using var client = new CodexAppServerClient(options);
        var events = new ConcurrentQueue<AppServerEventReceivedEventArgs>();
        var initialized = NewEventCompletion();
        var ready = NewEventCompletion();
        var environmentReported = NewEventCompletion();
        var serverRequest = NewEventCompletion();
        var notificationObserved = NewEventCompletion();
        client.EventReceived += (_, args) =>
        {
            events.Enqueue(args);
            switch (args.Method)
            {
                case "fixture/initializeObserved": initialized.TrySetResult(args); break;
                case "fixture/ready": ready.TrySetResult(args); break;
                case "fixture/environment": environmentReported.TrySetResult(args); break;
                case "fixture/serverRequest": serverRequest.TrySetResult(args); break;
                case "fixture/clientNotificationObserved": notificationObserved.TrySetResult(args); break;
            }
        };

        await client.StartAsync();

        var initializeEvent = await initialized.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var clientInfo = initializeEvent.Parameters!.Value.GetProperty("name").GetString();
        Assert.Equal("CodexSwitchboard", clientInfo);
        Assert.NotNull(await ready.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var incomingRequest = await serverRequest.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(incomingRequest.IsServerInitiatedRequest);
        Assert.Equal(99, incomingRequest.RequestId!.Value.GetInt32());

        var environmentEvent = await environmentReported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var environmentData = environmentEvent.Parameters!.Value;
        Assert.Equal(temp.Path, environmentData.GetProperty("home").GetString());
        Assert.False(environmentData.GetProperty("hasOpenAiKey").GetBoolean());
        Assert.False(environmentData.GetProperty("hasCodexKey").GetBoolean());

        var requests = Enumerable.Range(0, 8)
            .Select(async index =>
            {
                var value = $"request-{index}";
                var delayMilliseconds = (7 - index) * 15;
                var response = await client.RequestAsync("fixture/echo", new { value, delayMilliseconds });
                return (value, Actual: response.GetProperty("value").GetString());
            })
            .ToArray();
        var responses = await Task.WhenAll(requests);
        Assert.All(responses, response => Assert.Equal(response.value, response.Actual));

        await client.NotifyAsync("fixture/clientNotification", new { source = "test" });
        var observedNotification = await notificationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(observedNotification.RequestId);
        Assert.Contains(events, received => received.Method == "fixture/serverRequest");

        var lifecycle = Assert.IsType<TestLifecycleObserver>(options.ProcessLifecycleObserver);
        var processId = Assert.Single(lifecycle.RegisteredProcessIds);
        await client.StopAsync();
        Assert.False(client.IsRunning);
        Assert.True(await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(3)));
        Assert.True(await WaitUntilAsync(() => lifecycle.UnregisteredProcessIds.Count == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, lifecycle.StartLeasesAcquired);
        Assert.Equal(1, lifecycle.StartLeasesReleased);
        Assert.Equal(processId, Assert.Single(lifecycle.UnregisteredProcessIds));
    }

    [Fact]
    public async Task RequestAsync_PropagatesRpcErrorsAndHandlesTimeoutAndCancellation()
    {
        using var temp = new TemporaryDirectory();
        await using var client = new CodexAppServerClient(CreateOptions(temp.Path));
        await client.StartAsync();

        var rpcError = await Assert.ThrowsAsync<CodexAppServerRpcException>(
            () => client.RequestAsync("fixture/error"));
        Assert.Equal(-32600, rpcError.Code);
        Assert.True(rpcError.ErrorData!.Value.GetProperty("fixture").GetBoolean());

        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync(
            "fixture/delay",
            timeout: TimeSpan.FromMilliseconds(75)));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync(
            "fixture/delay",
            cancellationToken: cancellation.Token));

        var echo = await client.RequestAsync("fixture/echo", new { value = "still-connected" });
        Assert.Equal("still-connected", echo.GetProperty("value").GetString());
    }

    [Fact]
    public async Task RequestAsync_FailsPendingRequestWhenTheOwnedProcessExits()
    {
        using var temp = new TemporaryDirectory();
        var observer = new TestLifecycleObserver();
        await using var client = new CodexAppServerClient(CreateOptions(temp.Path, observer));
        await client.StartAsync();

        var exception = await Assert.ThrowsAsync<CodexAppServerProcessExitedException>(
            () => client.RequestAsync("fixture/exit", timeout: TimeSpan.FromSeconds(5)));

        Assert.True(exception.ProcessId > 0);
        Assert.Equal(17, exception.ExitCode);
        Assert.True(await WaitUntilAsync(() => observer.UnregisteredProcessIds.Count == 1, TimeSpan.FromSeconds(3)));
        Assert.Single(observer.UnregisteredProcessIds);
    }

    [Fact]
    public async Task RequestAsync_RejectsMalformedTransportMessagesAndTerminatesOnlyItsChild()
    {
        using var temp = new TemporaryDirectory();
        var observer = new TestLifecycleObserver();
        await using var client = new CodexAppServerClient(CreateOptions(temp.Path, observer));
        await client.StartAsync();

        await Assert.ThrowsAsync<CodexAppServerProtocolException>(
            () => client.RequestAsync("fixture/malformed", timeout: TimeSpan.FromSeconds(5)));

        var processId = Assert.Single(observer.RegisteredProcessIds);
        Assert.True(await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(3)));
        Assert.True(await WaitUntilAsync(() => observer.UnregisteredProcessIds.Count == 1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task StopAsync_ForceStopsOnlyTheOwnedChildWithinTheConfiguredBound()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "fixture-ignore-eof"), "synthetic scenario marker");
        var observer = new TestLifecycleObserver();
        await using var client = new CodexAppServerClient(CreateOptions(
            temp.Path,
            observer,
            shutdownGrace: TimeSpan.FromMilliseconds(100),
            forcedShutdown: TimeSpan.FromSeconds(2)));
        await client.StartAsync();
        var processId = Assert.Single(observer.RegisteredProcessIds);
        var stopwatch = Stopwatch.StartNew();

        await client.StopAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Shutdown took {stopwatch.Elapsed}.");
        Assert.True(await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(3)));
        Assert.True(await WaitUntilAsync(() => observer.UnregisteredProcessIds.Count == 1, TimeSpan.FromSeconds(3)));
        Assert.Single(observer.UnregisteredProcessIds);
    }

    private static AppServerClientOptions CreateOptions(
        string codexHome,
        IAppServerProcessLifecycleObserver? observer = null,
        TimeSpan? shutdownGrace = null,
        TimeSpan? forcedShutdown = null) => new()
        {
            ExecutablePath = RuntimeTestServer.ExecutablePath,
            CodexHome = codexHome,
            ClientInfo = new AppServerClientInfo("CodexSwitchboard", "Codex Switchboard", "1.0.0"),
            ProcessStartTimeout = TimeSpan.FromSeconds(5),
            InitializationTimeout = TimeSpan.FromSeconds(5),
            DefaultRequestTimeout = TimeSpan.FromSeconds(5),
            ShutdownGracePeriod = shutdownGrace ?? TimeSpan.FromSeconds(2),
            ForcedShutdownTimeout = forcedShutdown ?? TimeSpan.FromSeconds(2),
            ProcessLifecycleObserver = observer,
        };

    private static TaskCompletionSource<AppServerEventReceivedEventArgs> NewEventCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return condition();
    }

    private sealed class TestLifecycleObserver : IAppServerProcessLifecycleObserver
    {
        private readonly ConcurrentQueue<int> _registeredProcessIds = new();
        private readonly ConcurrentQueue<int> _unregisteredProcessIds = new();
        private int _startLeasesAcquired;
        private int _startLeasesReleased;

        public IReadOnlyCollection<int> RegisteredProcessIds => _registeredProcessIds.ToArray();
        public IReadOnlyCollection<int> UnregisteredProcessIds => _unregisteredProcessIds.ToArray();
        public int StartLeasesAcquired => Volatile.Read(ref _startLeasesAcquired);
        public int StartLeasesReleased => Volatile.Read(ref _startLeasesReleased);

        public ValueTask<IDisposable?> AcquireStartLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _startLeasesAcquired);
            return ValueTask.FromResult<IDisposable?>(new StartLease(() => Interlocked.Increment(ref _startLeasesReleased)));
        }

        public void RegisterStartedProcess(Process process) => _registeredProcessIds.Enqueue(process.Id);

        public void UnregisterExitedProcess(int processId) => _unregisteredProcessIds.Enqueue(processId);

        private sealed class StartLease(Action release) : IDisposable
        {
            private Action? _release = release;

            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
