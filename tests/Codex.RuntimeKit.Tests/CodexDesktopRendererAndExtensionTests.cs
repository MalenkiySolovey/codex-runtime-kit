using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Codex.RuntimeKit.Desktop.DebugTransport;
using Codex.RuntimeKit.Desktop.Compatibility;
using Codex.RuntimeKit.Desktop.Extensions;
using Codex.RuntimeKit.Desktop.Lifecycle;
using Codex.RuntimeKit.Desktop.Renderer;
using Codex.RuntimeKit.Tests.TestSupport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

public sealed class CodexDesktopRendererAndExtensionTests
{
    private static readonly Version DesktopVersion = new(26, 1117, 3838, 0);

    [Fact]
    public async Task Endpoint_RequiresTheManagedProcessImageToOwnTheEphemeralLoopbackPort()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "DevToolsActivePort"), "53123\n/devtools/browser/test_123\n");
        var executablePath = Path.Combine(temp.Path, "ChatGPT.exe");
        await using var session = new CodexDesktopManagedSession(Process.GetCurrentProcess(), temp.Path, executablePath, DateTime.UtcNow);

        var valid = CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path,
            session,
            new FakeListenerOwnerResolver(session.ProcessId, executablePath),
            out var endpoint,
            out var validDiagnostic);
        var mismatched = CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path,
            session,
            new FakeListenerOwnerResolver(session.ProcessId + 1, executablePath),
            out var mismatchEndpoint,
            out var mismatchDiagnostic);
        var wrongExecutable = CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path,
            session,
            new FakeListenerOwnerResolver(session.ProcessId, Path.Combine(temp.Path, "NotChatGPT.exe")),
            out var wrongExecutableEndpoint,
            out var wrongExecutableDiagnostic);
        var wrongProfile = CodexDesktopDebugEndpoint.TryReadManagedProfile(
            Path.Combine(temp.Path, "another-profile"),
            session,
            new FakeListenerOwnerResolver(session.ProcessId, executablePath),
            out var wrongProfileEndpoint,
            out var wrongProfileDiagnostic);

        Assert.True(valid);
        Assert.Equal("desktop.cdp.endpoint-verified", validDiagnostic);
        Assert.Equal(53123, endpoint?.Port);
        Assert.Equal(session.ProcessId, endpoint?.ProcessId);
        Assert.Equal("127.0.0.1", endpoint?.BaseAddress.Host);
        Assert.False(mismatched);
        Assert.Null(mismatchEndpoint);
        Assert.Equal("desktop.cdp.owner-mismatch", mismatchDiagnostic);
        Assert.False(wrongExecutable);
        Assert.Null(wrongExecutableEndpoint);
        Assert.Equal("desktop.cdp.executable-mismatch", wrongExecutableDiagnostic);
        Assert.False(wrongProfile);
        Assert.Null(wrongProfileEndpoint);
        Assert.Equal("desktop.cdp.profile-mismatch", wrongProfileDiagnostic);
    }

    [Fact]
    public async Task Endpoint_RejectsInvalidPortFileAndBrowserSocketPath()
    {
        using var temp = new TemporaryDirectory();
        var portFile = Path.Combine(temp.Path, "DevToolsActivePort");
        File.WriteAllText(portFile, "0\n/devtools/browser/test\n");
        var executablePath = Path.Combine(temp.Path, "ChatGPT.exe");
        await using var session = new CodexDesktopManagedSession(Process.GetCurrentProcess(), temp.Path, executablePath, DateTime.UtcNow);

        Assert.False(CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path, session, new FakeListenerOwnerResolver(session.ProcessId, executablePath), out _, out var invalidPortCode));
        Assert.Equal("desktop.cdp.active-port-invalid", invalidPortCode);

        File.WriteAllText(portFile, "53123\n/devtools/browser/../page\n");
        Assert.False(CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path, session, new FakeListenerOwnerResolver(session.ProcessId, executablePath), out _, out var invalidPathCode));
        Assert.Equal("desktop.cdp.active-port-invalid", invalidPathCode);
    }

    [Fact]
    public void WindowsListenerOwnerResolver_ResolvesOnlyItsLoopbackListener()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var process = Process.GetCurrentProcess();
        var resolver = new WindowsCodexDesktopListenerOwnerResolver();
        var ownerProcessId = resolver.GetLoopbackListenerOwnerProcessId(port);

        Assert.Equal(Environment.ProcessId, ownerProcessId);
        Assert.True(resolver.ProcessExecutableMatches(ownerProcessId!.Value, process.MainModule!.FileName!));
    }

    [Fact]
    public void RendererSelection_RequiresOneMainAppPageAndLoopbackTargetSocket()
    {
        var valid = Target("page-1", "app://-/index.html", "ws://127.0.0.1:53123/devtools/page/page-1");
        var auxiliary = Target("avatar", "app://-/avatar-overlay", "ws://127.0.0.1:53123/devtools/page/avatar");

        var selected = CodexDesktopRendererVerifier.SelectMainRenderer([auxiliary, valid], 53123);
        var ambiguous = CodexDesktopRendererVerifier.SelectMainRenderer([valid, valid with { Id = "page-2" }], 53123);
        var nonLoopback = CodexDesktopRendererVerifier.SelectMainRenderer(
            [Target("page-3", "app://-/index.html", "ws://192.168.1.20:53123/devtools/page/page-3")],
            53123);

        Assert.Equal(CodexDesktopRendererSelectionState.Selected, selected.State);
        Assert.Equal("page-1", selected.Target?.Id);
        Assert.Equal(CodexDesktopRendererSelectionState.Ambiguous, ambiguous.State);
        Assert.Equal(CodexDesktopRendererSelectionState.InvalidTarget, nonLoopback.State);
    }

    [Fact]
    public void RendererSemanticProbe_FailsClosedWhenAnyIndependentSignalIsMissing()
    {
        Assert.True(CodexDesktopRendererVerifier.AcceptSemanticSignals(new(true, true, true, true)));
        Assert.False(CodexDesktopRendererVerifier.AcceptSemanticSignals(new(true, true, true, false)));
        Assert.False(CodexDesktopRendererVerifier.AcceptSemanticSignals(new(true, false, true, true)));
        Assert.False(CodexDesktopRendererVerifier.AcceptSemanticSignals(null));
    }

    [Fact]
    public void CompatibilityMatrix_DoesNotAssumeSupportForNeighboringBuildsOrUnverifiedAnchors()
    {
        var matrix = new CodexDesktopCompatibilityMatrix(
        [
            new(new Version(26, 1117, 3838, 0), new Version(26, 1117, 3838, 0), "main-renderer-v1", true),
            new(new Version(26, 1117, 3839, 0), new Version(26, 1117, 3839, 0), "main-renderer-v2", false),
        ]);

        Assert.Equal(CodexDesktopCompatibilityState.Supported, matrix.Evaluate(DesktopVersion).State);
        Assert.Equal(CodexDesktopCompatibilityState.Unsupported, matrix.Evaluate(new Version(26, 1117, 3839, 0)).State);
        Assert.Equal(CodexDesktopCompatibilityState.Unverified, matrix.Evaluate(new Version(26, 1117, 3840, 0)).State);
        Assert.Throws<ArgumentException>(() => new CodexDesktopCompatibilityMatrix(
        [
            new(new Version(26, 1, 0), new Version(26, 3, 0), "overlap-a", true),
            new(new Version(26, 2, 0), new Version(26, 4, 0), "overlap-b", true),
        ]));
    }

    [Fact]
    public async Task ExtensionHost_InstallsUpdatesAndRemovesOnlyAnAllowlistedBundledScript()
    {
        const string script = "(() => ({ cleanup: () => {}, update: (_payload) => true }))()";
        var hash = CodexDesktopExtensionHost.ComputeSha256(script);
        var host = new CodexDesktopExtensionHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codex.api-balance"] = hash,
        });
        var channel = new FakeExtensionChannel();
        var package = Package("codex.api-balance", new Version(1, 0, 0), script, hash);

        var installed = await host.InstallOrUpdateAsync(package, DesktopVersion, channel);
        var updated = await host.InstallOrUpdateAsync(package with { Version = new Version(1, 1, 0) }, DesktopVersion, channel);
        using var payloadDocument = System.Text.Json.JsonDocument.Parse("{\"state\":\"Live\",\"remaining\":6.01}");
        var payloadUpdated = await host.UpdatePayloadAsync("codex.api-balance", payloadDocument.RootElement, channel);
        var removed = await host.RemoveAsync("codex.api-balance", channel);

        Assert.Equal(CodexDesktopExtensionResultState.Installed, installed.State);
        Assert.Equal(CodexDesktopExtensionResultState.Updated, updated.State);
        Assert.Equal("desktop.extension.payload-updated", payloadUpdated.DiagnosticCode);
        Assert.Equal(CodexDesktopExtensionResultState.Removed, removed.State);
        Assert.Equal(3, channel.AddedScripts.Count);
        Assert.Equal(4, channel.EvaluatedExpressions.Count);
        Assert.Equal(3, channel.RemovedScriptIds.Count);
        Assert.Contains("__CODEX_RUNTIME_KIT_EXTENSIONS__", channel.EvaluatedExpressions[0], StringComparison.Ordinal);
        Assert.Contains("\"remaining\":6.01", channel.EvaluatedExpressions[2], StringComparison.Ordinal);
        Assert.Contains("app://-/index.html", channel.AddedScripts[0], StringComparison.Ordinal);
        Assert.Contains("JSON.parse", channel.AddedScripts[2], StringComparison.Ordinal);
        Assert.Contains("remaining", channel.AddedScripts[2], StringComparison.Ordinal);
        Assert.Contains("initialPayload !== null", channel.AddedScripts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionHost_CanRequireLivePayloadAfterReloadAndRetryFailedRemoval()
    {
        const string id = "codex.api-balance";
        const string script = "(() => () => {})()";
        var hash = CodexDesktopExtensionHost.ComputeSha256(script);
        var package = Package(id, new Version(1, 0, 0), script, hash) with { RestorePayloadOnReload = false };
        var host = new CodexDesktopExtensionHost(new Dictionary<string, string> { [id] = hash });
        var owner = new FakeExtensionChannel();
        using var payload = System.Text.Json.JsonDocument.Parse("""{"windowValue":37}""");

        await host.InstallOrUpdateAsync(package, DesktopVersion, owner);
        var update = await host.UpdatePayloadAsync(id, payload.RootElement, owner);
        var foreignRemoval = await host.RemoveAsync(id, new FakeExtensionChannel());
        owner.RemovalResult = false;
        var failedRemoval = await host.RemoveAsync(id, owner);
        owner.RemovalResult = true;
        var retriedRemoval = await host.RemoveAsync(id, owner);

        Assert.Equal(CodexDesktopExtensionResultState.Updated, update.State);
        Assert.DoesNotContain("windowValue", Assert.Single(owner.AddedScripts), StringComparison.Ordinal);
        Assert.Contains("windowValue", owner.EvaluatedExpressions[1], StringComparison.Ordinal);
        Assert.Equal(CodexDesktopExtensionResultState.Rejected, foreignRemoval.State);
        Assert.Equal(CodexDesktopExtensionResultState.Failed, failedRemoval.State);
        Assert.Equal(CodexDesktopExtensionResultState.Removed, retriedRemoval.State);
        Assert.Equal(new[] { "script-1", "script-1" }, owner.RemovedScriptIds);
    }

    [Fact]
    public async Task ExtensionHost_RejectsUntrustedHashUnsupportedBuildAndUnverifiedRenderer()
    {
        const string script = "(() => () => {})()";
        var hash = CodexDesktopExtensionHost.ComputeSha256(script);
        var package = Package("codex.api-balance", new Version(1, 0, 0), script, hash);
        var host = new CodexDesktopExtensionHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codex.api-balance"] = hash,
        });
        var channel = new FakeExtensionChannel();

        var badHash = await host.InstallOrUpdateAsync(package with { Sha256 = new string('0', 64) }, DesktopVersion, channel);
        var unsupported = await host.InstallOrUpdateAsync(package, new Version(26, 9999, 0, 0), channel);
        channel.IsVerifiedCodexRenderer = false;
        var unverified = await host.InstallOrUpdateAsync(package, DesktopVersion, channel);

        Assert.Equal("desktop.extension.hash-mismatch", badHash.DiagnosticCode);
        Assert.Equal("desktop.extension.version-unsupported", unsupported.DiagnosticCode);
        Assert.Equal("desktop.extension.renderer-unverified", unverified.DiagnosticCode);
        Assert.Empty(channel.AddedScripts);
        Assert.Empty(channel.EvaluatedExpressions);
    }

    [Fact]
    public async Task ExtensionHost_RollsBackNewDocumentScriptWhenRendererUpdateFails()
    {
        const string script = "(() => () => {})()";
        var hash = CodexDesktopExtensionHost.ComputeSha256(script);
        var host = new CodexDesktopExtensionHost(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codex.api-balance"] = hash,
        });
        var channel = new FakeExtensionChannel { EvaluationResult = false };

        var result = await host.InstallOrUpdateAsync(
            Package("codex.api-balance", new Version(1, 0, 0), script, hash),
            DesktopVersion,
            channel);

        Assert.Equal(CodexDesktopExtensionResultState.Failed, result.State);
        Assert.Equal("desktop.extension.renderer-install-failed", result.DiagnosticCode);
        Assert.Equal(new[] { "script-1" }, channel.RemovedScriptIds);
    }

    private static CodexDesktopRendererTarget Target(string id, string url, string socket) =>
        new(id, "page", url, new Uri(socket));

    private static CodexDesktopExtensionPackage Package(string id, Version version, string script, string hash) =>
        new(id, version, new Version(26, 1117, 0, 0), new Version(26, 1117, 9999, 9999), hash, script);

    private sealed class FakeListenerOwnerResolver(int processId, string? executablePath) : ICodexDesktopListenerOwnerResolver
    {
        public int? GetLoopbackListenerOwnerProcessId(int port) => port == 53123 ? processId : null;

        public bool ProcessExecutableMatches(int candidateProcessId, string expectedExecutablePath) =>
            candidateProcessId == processId &&
            !string.IsNullOrWhiteSpace(executablePath) &&
            string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeExtensionChannel : ICodexDesktopExtensionChannel
    {
        public bool IsVerifiedCodexRenderer { get; set; } = true;
        public bool EvaluationResult { get; set; } = true;
        public bool RemovalResult { get; set; } = true;
        public List<string> AddedScripts { get; } = [];
        public List<string> EvaluatedExpressions { get; } = [];
        public List<string> RemovedScriptIds { get; } = [];

        public Task<string?> AddDocumentScriptAsync(string script, CancellationToken cancellationToken)
        {
            AddedScripts.Add(script);
            return Task.FromResult<string?>($"script-{AddedScripts.Count}");
        }

        public Task<bool> EvaluateExtensionAsync(string expression, CancellationToken cancellationToken)
        {
            EvaluatedExpressions.Add(expression);
            return Task.FromResult(EvaluationResult);
        }

        public Task<bool> RemoveDocumentScriptAsync(string identifier, CancellationToken cancellationToken)
        {
            RemovedScriptIds.Add(identifier);
            return Task.FromResult(RemovalResult);
        }
    }
}
