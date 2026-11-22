using System.Diagnostics;
using Codex.RuntimeKit.Desktop.DebugTransport;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Lifecycle;
using Codex.RuntimeKit.Tests.TestSupport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

public sealed class CodexDesktopLifecycleTests
{
    private static readonly CodexDesktopPackageIdentity Identity = new(
        "OpenAI.Codex",
        "OpenAI.Codex_2p2nqsd0c76g0",
        "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",
        "App");

    [Fact]
    public async Task ManagedLaunch_RequiresConsentBeforeInspectingOrStarting()
    {
        using var temp = new TemporaryDirectory();
        var installation = CreateInstallation(temp.Path);
        var inventory = new FakeProcessInventory { Result = new(true, false) };
        var starter = new FakeProcessStarter();
        var launcher = new CodexDesktopManagedLauncher(CreateDiscovery(temp.Path), inventory, starter);

        var result = await launcher.StartAsync(new CodexDesktopManagedLaunchRequest(installation, false));

        Assert.Equal(CodexDesktopLifecycleState.ConsentRequired, result.State);
        Assert.Equal(0, inventory.CallCount);
        Assert.Equal(0, starter.CallCount);
    }

    [Fact]
    public async Task ManagedLaunch_RefusesAnExistingOrUnverifiableOfficialInstance()
    {
        using var temp = new TemporaryDirectory();
        var installation = CreateInstallation(temp.Path);
        var existingInventory = new FakeProcessInventory { Result = new(true, true) };
        var starter = new FakeProcessStarter();
        var launcher = new CodexDesktopManagedLauncher(CreateDiscovery(temp.Path), existingInventory, starter);

        var existing = await launcher.StartAsync(new CodexDesktopManagedLaunchRequest(installation, true));
        existingInventory.Result = new(false, false);
        var unknown = await launcher.StartAsync(new CodexDesktopManagedLaunchRequest(installation, true));

        Assert.Equal(CodexDesktopLifecycleState.ExistingInstance, existing.State);
        Assert.Equal("desktop.lifecycle.existing-instance-requires-user-action", existing.DiagnosticCode);
        Assert.Equal(CodexDesktopLifecycleState.ProcessInventoryUnavailable, unknown.State);
        Assert.Equal(0, starter.CallCount);
    }

    [Fact]
    public async Task ManagedLaunch_AllowsAnAdditionalIsolatedInstanceOnlyWithExplicitConsent()
    {
        using var temp = new TemporaryDirectory();
        var installation = CreateInstallation(temp.Path);
        var inventory = new FakeProcessInventory { Result = new(true, true) };
        var starter = new FakeProcessStarter();
        var launcher = new CodexDesktopManagedLauncher(CreateDiscovery(temp.Path), inventory, starter);

        var result = await launcher.StartAsync(new CodexDesktopManagedLaunchRequest(
            installation,
            UserConsentGranted: true,
            ManagedProfileRoot: temp.Path,
            AllowConcurrentIsolatedInstance: true));

        Assert.Equal(CodexDesktopLifecycleState.LaunchFailed, result.State);
        Assert.Equal("desktop.lifecycle.launch-failed", result.DiagnosticCode);
        Assert.Equal(1, inventory.CallCount);
        Assert.Equal(1, starter.CallCount);
        var startInfo = Assert.IsType<ProcessStartInfo>(starter.StartInfo);
        var profileArgument = Assert.Single(startInfo.ArgumentList, argument => argument.StartsWith("--user-data-dir=", StringComparison.Ordinal));
        var profile = profileArgument["--user-data-dir=".Length..];
        Assert.StartsWith(Path.GetFullPath(temp.Path), profile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(profile, "codex-home"), startInfo.Environment["CODEX_HOME"]);
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public async Task ManagedLaunch_RevalidatesTheSignedPackageBeforeCallingTheProcessStarter()
    {
        using var temp = new TemporaryDirectory();
        var installation = CreateInstallation(temp.Path);
        File.WriteAllText(Path.Combine(installation.InstallLocation, "AppxManifest.xml"), "<Package />");
        var inventory = new FakeProcessInventory { Result = new(true, false) };
        var starter = new FakeProcessStarter();
        var launcher = new CodexDesktopManagedLauncher(CreateDiscovery(temp.Path), inventory, starter);

        var result = await launcher.StartAsync(new CodexDesktopManagedLaunchRequest(installation, true));

        Assert.Equal(CodexDesktopLifecycleState.InstallationUnverified, result.State);
        Assert.Equal(0, inventory.CallCount);
        Assert.Equal(0, starter.CallCount);
    }

    [Fact]
    public async Task ManagedLaunch_UsesAnIsolatedCodexHomeUnderTheConfiguredStorageRoot()
    {
        using var temp = new TemporaryDirectory();
        var installation = CreateInstallation(temp.Path);
        var inventory = new FakeProcessInventory { Result = new(true, false) };
        var starter = new FakeProcessStarter();
        var launcher = new CodexDesktopManagedLauncher(CreateDiscovery(temp.Path), inventory, starter);

        var result = await launcher.StartAsync(new CodexDesktopManagedLaunchRequest(installation, true, temp.Path));

        Assert.Equal(CodexDesktopLifecycleState.LaunchFailed, result.State);
        Assert.Equal("desktop.lifecycle.launch-failed", result.DiagnosticCode);
        var startInfo = Assert.IsType<ProcessStartInfo>(starter.StartInfo);
        var profileArgument = Assert.Single(startInfo.ArgumentList, argument => argument.StartsWith("--user-data-dir=", StringComparison.Ordinal));
        var profile = profileArgument["--user-data-dir=".Length..];
        Assert.StartsWith(Path.GetFullPath(temp.Path), profile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(profile, "codex-home"), startInfo.Environment["CODEX_HOME"]);
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void ManagedLaunch_ProcessImageVerificationRequiresTheExpectedExecutablePath()
    {
        using var process = Process.GetCurrentProcess();
        var actualExecutablePath = process.MainModule?.FileName;
        Assert.False(string.IsNullOrWhiteSpace(actualExecutablePath));

        Assert.True(CodexDesktopManagedLauncher.HasExpectedExecutable(process, actualExecutablePath!));
        Assert.False(CodexDesktopManagedLauncher.HasExpectedExecutable(process, Path.Combine(Path.GetTempPath(), "ChatGPT.exe")));
    }

    [Fact]
    public void ParentLifetime_ReplacesOnlyTheOwnedSupervisorOnTheVerifiedBuild()
    {
        var installation = new CodexDesktopInstallation(
            "OpenAI.Codex", "OpenAI.Codex_2p2nqsd0c76g0", "synthetic", "synthetic",
            "ChatGPT.exe", new Version(26, 1117, 4957, 0));
        var start = new ProcessStartInfo { FileName = installation.ExecutablePath };
        start.Environment["CODEX_ELECTRON_DEV_PARENT_PID"] = "1";
        start.Environment["CODEX_ELECTRON_DEV_WEBVIEW_PID"] = "2";

        var configured = CodexDesktopParentLifetime.ConfigureLaunch(start, installation, 1234);

        Assert.Equal(OperatingSystem.IsWindows(), configured);
        Assert.False(start.Environment.ContainsKey("CODEX_ELECTRON_DEV_WEBVIEW_PID"));
        Assert.Equal(configured, start.Environment.TryGetValue("CODEX_ELECTRON_DEV_PARENT_PID", out var parentId));
        if (configured)
        {
            Assert.Equal("1234", parentId);
        }

        var unverifiedBuild = installation with { Version = new Version(26, 1117, 4958, 0) };
        Assert.False(CodexDesktopParentLifetime.ConfigureLaunch(start, unverifiedBuild, 1234));
        Assert.False(start.Environment.ContainsKey("CODEX_ELECTRON_DEV_PARENT_PID"));
    }

    [Fact]
    public void ManagedLaunch_ResolvesTheActualRendererProcessFromItsUniqueProfileListener()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllLines(Path.Combine(temp.Path, "DevToolsActivePort"), ["9222", "/devtools/browser/synthetic"]);
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName;
        Assert.False(string.IsNullOrWhiteSpace(executable));

        var resolved = CodexDesktopManagedLauncher.TryResolveManagedProfileProcess(
            temp.Path,
            executable!,
            new FixedListenerOwnerResolver(current.Id),
            out var process);

        using (process)
        {
            Assert.True(resolved);
            Assert.Equal(current.Id, process?.Id);
        }
    }

    [Fact]
    public void ManagedLaunch_RejectsAProfileListenerOwnedByANonmatchingExecutable()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllLines(Path.Combine(temp.Path, "DevToolsActivePort"), ["9222", "/devtools/browser/synthetic"]);
        using var current = Process.GetCurrentProcess();

        var resolved = CodexDesktopManagedLauncher.TryResolveManagedProfileProcess(
            temp.Path,
            Path.Combine(Path.GetTempPath(), "unrelated.exe"),
            new FixedListenerOwnerResolver(current.Id),
            out var process);

        Assert.False(resolved);
        Assert.Null(process);
    }

    [Fact]
    public void DebugEndpoint_CanResolveAProfileWithoutTheShortLivedStarterSession()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllLines(Path.Combine(temp.Path, "DevToolsActivePort"), ["9222", "/devtools/browser/synthetic"]);
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName;
        Assert.False(string.IsNullOrWhiteSpace(executable));

        var resolved = CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path,
            executable!,
            new FixedListenerOwnerResolver(current.Id, executable),
            out var endpoint,
            out var diagnostic);

        Assert.True(resolved);
        Assert.Equal("desktop.cdp.endpoint-verified", diagnostic);
        Assert.Equal(current.Id, endpoint?.ProcessId);
        Assert.Equal(9222, endpoint?.Port);
    }

    [Fact]
    public void DebugEndpoint_RejectsAProfileWithTheWrongExecutableImage()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllLines(Path.Combine(temp.Path, "DevToolsActivePort"), ["9222", "/devtools/browser/synthetic"]);
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName;

        var resolved = CodexDesktopDebugEndpoint.TryReadManagedProfile(
            temp.Path,
            Path.Combine(Path.GetTempPath(), "unrelated.exe"),
            new FixedListenerOwnerResolver(current.Id, executable),
            out var endpoint,
            out var diagnostic);

        Assert.False(resolved);
        Assert.Null(endpoint);
        Assert.Equal("desktop.cdp.executable-mismatch", diagnostic);
    }

    [Fact]
    public void ProcessInventory_RecognizesOnlyTheExpectedPackageFamilyAndExecutable()
    {
        using var temp = new TemporaryDirectory();
        var installation = CreateInstallation(temp.Path);
        var olderPackagePath = Path.Combine(
            temp.Path,
            "OpenAI.Codex_26.1100.1000.0_x64__2p2nqsd0c76g0",
            "app",
            "ChatGPT.exe");

        var empty = WindowsCodexDesktopProcessInventory.ClassifyProcessPaths([], installation);
        var official = WindowsCodexDesktopProcessInventory.ClassifyProcessPaths([olderPackagePath], installation);
        var unrelated = WindowsCodexDesktopProcessInventory.ClassifyProcessPaths([Path.Combine(temp.Path, "ChatGPT.exe")], installation);
        var unknown = WindowsCodexDesktopProcessInventory.ClassifyProcessPaths([null], installation);

        Assert.Equal(new CodexDesktopProcessInventoryResult(true, false), empty);
        Assert.Equal(new CodexDesktopProcessInventoryResult(true, true), official);
        Assert.Equal(new CodexDesktopProcessInventoryResult(false, false), unrelated);
        Assert.Equal(new CodexDesktopProcessInventoryResult(false, false), unknown);
    }

    private static CodexDesktopDiscovery CreateDiscovery(string root) => new(new CodexDesktopDiscoveryOptions
    {
        WindowsAppsRoot = root,
        PackageIdentities = [Identity],
    });

    private static CodexDesktopInstallation CreateInstallation(string root)
    {
        var packageRoot = Path.Combine(root, "OpenAI.Codex_26.1117.3838.0_x64__2p2nqsd0c76g0");
        var executablePath = Path.Combine(packageRoot, "app", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, "synthetic executable");
        File.WriteAllText(Path.Combine(packageRoot, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="OpenAI.Codex" Publisher="CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B" Version="26.1117.3838.0" />
              <Applications><Application Id="App" Executable="app/ChatGPT.exe" /></Applications>
            </Package>
            """);
        var result = CreateDiscovery(root).Discover();
        return Assert.IsType<CodexDesktopInstallation>(result.Installation);
    }

    private sealed class FakeProcessInventory : ICodexDesktopProcessInventory
    {
        public CodexDesktopProcessInventoryResult Result { get; set; } = new(true, false);
        public int CallCount { get; private set; }

        public CodexDesktopProcessInventoryResult Inspect(CodexDesktopInstallation installation)
        {
            CallCount++;
            return Result;
        }
    }

    private sealed class FakeProcessStarter : ICodexDesktopProcessStarter
    {
        public int CallCount { get; private set; }
        public ProcessStartInfo? StartInfo { get; private set; }

        public Process? Start(ProcessStartInfo startInfo)
        {
            CallCount++;
            StartInfo = startInfo;
            return null;
        }
    }

    private sealed class FixedListenerOwnerResolver(int processId, string? executablePath = null) : ICodexDesktopListenerOwnerResolver
    {
        public int? GetLoopbackListenerOwnerProcessId(int port) => port == 9222 ? processId : null;
        public bool ProcessExecutableMatches(int processId, string expectedExecutablePath) =>
            executablePath is not null && string.Equals(executablePath, expectedExecutablePath, StringComparison.OrdinalIgnoreCase);
    }
}
