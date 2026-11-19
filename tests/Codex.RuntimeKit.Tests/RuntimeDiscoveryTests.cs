using System.Diagnostics;
using System.Text.Json;
using Codex.RuntimeKit.Capabilities;
using Codex.RuntimeKit.Contracts;
using Codex.RuntimeKit.Discovery;
using Codex.RuntimeKit.Tests.TestSupport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

public sealed class RuntimeDiscoveryTests
{
    [Fact]
    public void ResolveCurrentRuntime_UsesAnExistingExplicitOverrideAndBuildsStableIdentity()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("override/codex.exe", "synthetic executable");
        var inspector = new StubRuntimeInspector((_, probe) => new RuntimeInspection(
            "0.153.4",
            probe ? CapabilityStatus.Supported : CapabilityStatus.Unknown));
        var resolver = new CodexRuntimeResolver(inspector: inspector, options: EmptySearchOptions(temp.Path));

        var runtime = resolver.ResolveCurrentRuntime(executable);
        var file = new FileInfo(executable);

        Assert.Equal(executable, runtime.ExecutablePath);
        Assert.Equal("0.153.4", runtime.Version);
        Assert.Equal(ExecutableIdentity.CreateValue(executable, file.Length, file.LastWriteTimeUtc, "0.153.4"), runtime.FileIdentity);
        Assert.Equal(RuntimeCapabilities.ModernFull, runtime.Capabilities);
        Assert.Equal((executable, true), Assert.Single(inspector.Calls));
    }

    [Fact]
    public void ResolveCurrentRuntime_FallsBackFromInvalidOverrideToHighestVersionedInstallBeforeWellKnownAndPath()
    {
        using var temp = new TemporaryDirectory();
        var install = System.IO.Path.Combine(temp.Path, "install");
        var older = temp.CreateFile("install/hash-a/codex.exe");
        var newer = temp.CreateFile("install/hash-b/codex.exe");
        var wellKnown = temp.CreateFile("install/codex.exe");
        var pathExecutable = temp.CreateFile("path/codex.exe");
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [older] = "0.153.4",
            [newer] = "0.154.0-beta.2",
            [wellKnown] = "1.2.3",
            [pathExecutable] = "2.0.0",
        };
        var resolver = new CodexRuntimeResolver(
            inspector: new StubRuntimeInspector((path, _) => new RuntimeInspection(
                versions[path], CapabilityStatus.Unsupported)),
            options: new RuntimeDiscoveryOptions
            {
                InstallBinDirectory = install,
                InstalledExecutableNames = ["codex.exe"],
                PathExecutableNames = ["codex.exe"],
                PathValue = System.IO.Path.Combine(temp.Path, "path"),
            });

        var runtime = resolver.ResolveCurrentRuntime(System.IO.Path.Combine(temp.Path, "missing.exe"));
        var candidates = resolver.EnumerateCandidates();

        Assert.Equal(newer, runtime.ExecutablePath);
        Assert.Equal("0.154.0-beta.2", runtime.Version);
        Assert.Equal(RuntimeCandidateSource.OfficialHashDirectory, candidates[0].Source);
        Assert.Equal(new[] { newer, older, wellKnown, pathExecutable }, candidates.Select(candidate => candidate.Path));
    }

    [Fact]
    public void EnumerateCandidates_DeduplicatesRepeatedPathEntries()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("path/codex.exe");
        var pathDirectory = System.IO.Path.GetDirectoryName(executable)!;
        var resolver = new CodexRuntimeResolver(
            inspector: new StubRuntimeInspector((_, _) => new RuntimeInspection("0.153.4", CapabilityStatus.Supported)),
            options: new RuntimeDiscoveryOptions
            {
                InstallBinDirectory = System.IO.Path.Combine(temp.Path, "no-install"),
                InstalledExecutableNames = ["codex.exe"],
                PathExecutableNames = ["codex.exe"],
                PathValue = string.Join(System.IO.Path.PathSeparator, pathDirectory, pathDirectory),
            });

        var candidates = resolver.EnumerateCandidates();

        Assert.Equal(executable, Assert.Single(candidates).Path);
        Assert.Equal(RuntimeCandidateSource.Path, candidates[0].Source);
    }

    [Fact]
    public void ResolveCurrentRuntime_ReusesCachedCapabilitiesUntilExecutableIdentityChanges()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("codex.exe", "one");
        var cache = new InMemoryRuntimeCapabilityCache();
        cache.SetCapabilities(
            ExecutableIdentity.CreateCacheKey(executable),
            RuntimeCapabilities.LegacyUnsupportedUsage);
        var inspector = new StubRuntimeInspector((_, probe) => new RuntimeInspection(
            "0.153.4",
            probe ? CapabilityStatus.Supported : CapabilityStatus.Unknown));
        var resolver = new CodexRuntimeResolver(cache, inspector, EmptySearchOptions(temp.Path));

        var first = resolver.ResolveCurrentRuntime(executable);
        File.WriteAllText(executable, "a changed synthetic executable with a different length");
        var second = resolver.ResolveCurrentRuntime(executable);

        Assert.Equal(CapabilityStatus.Unsupported, first.Capabilities.AccountUsageRead);
        Assert.Equal(CapabilityStatus.Supported, second.Capabilities.AccountUsageRead);
        Assert.Collection(inspector.Calls,
            call => Assert.False(call.ProbeCapabilities),
            call => Assert.True(call.ProbeCapabilities));
    }

    [Fact]
    public void ValidateExecutable_ReturnsTypedFailureAndSuccessResults()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("codex.exe");
        var resolver = new CodexRuntimeResolver(
            inspector: new StubRuntimeInspector((_, _) => new RuntimeInspection("0.153.4", CapabilityStatus.Supported)),
            options: EmptySearchOptions(temp.Path));

        var valid = resolver.ValidateExecutable(executable);
        var missing = resolver.ValidateExecutable(System.IO.Path.Combine(temp.Path, "missing.exe"));
        var empty = resolver.ValidateExecutable(" ");

        Assert.True(valid.IsValid);
        Assert.Equal("0.153.4", valid.Version);
        Assert.Null(valid.ErrorMessage);
        Assert.False(missing.IsValid);
        Assert.Contains("File not found", missing.ErrorMessage, StringComparison.Ordinal);
        Assert.False(empty.IsValid);
        Assert.NotNull(empty.ErrorMessage);
    }

    [Fact]
    public void CapabilityCache_SupportsExplicitInvalidationAndClear()
    {
        var cache = new InMemoryRuntimeCapabilityCache();
        cache.SetCapabilities("first", RuntimeCapabilities.ModernFull);
        cache.SetCapabilities("second", RuntimeCapabilities.LegacyUnsupportedUsage);

        cache.Invalidate("first");
        Assert.Null(cache.GetCapabilities("first"));
        Assert.NotNull(cache.GetCapabilities("second"));

        cache.Clear();
        Assert.Null(cache.GetCapabilities("second"));
    }

    [Fact]
    public void Inspector_UsesAnIsolatedHomeAndRemovesInheritedSecretsForProbes()
    {
        using var temp = new TemporaryDirectory();
        var reportPath = System.IO.Path.Combine(temp.Path, "environment-report.json");
        using var environment = new EnvironmentVariableScope(
            ("CODEX_RUNTIME_KIT_FIXTURE_REPORT", reportPath),
            ("OPENAI_API_KEY", "synthetic-openai-marker"),
            ("CODEX_API_KEY", "synthetic-codex-marker"),
            ("CODEX_SQLITE_HOME", System.IO.Path.Combine(temp.Path, "must-not-be-inherited"))).Set();
        var inspector = new CodexRuntimeInspector(new RuntimeDiscoveryOptions
        {
            VersionProbeTimeout = TimeSpan.FromSeconds(5),
            CapabilityProbeTimeout = TimeSpan.FromSeconds(5),
        });

        var inspection = inspector.Inspect(RuntimeTestServer.ExecutablePath, probeCapabilities: true);
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = report.RootElement;
        var probeHome = root.GetProperty("codexHome").GetString();

        Assert.Equal("0.153.4", inspection.Version);
        Assert.Equal(CapabilityStatus.Supported, inspection.AccountUsageRead);
        Assert.False(root.GetProperty("openAiKeyPresent").GetBoolean());
        Assert.False(root.GetProperty("codexApiKeyPresent").GetBoolean());
        Assert.False(root.GetProperty("sqliteHomePresent").GetBoolean());
        Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), System.IO.Path.GetFullPath(probeHome!), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
        Assert.False(Directory.Exists(probeHome));
    }

    [Fact]
    public void Inspector_RespectsAnExplicitUnsupportedSchemaAndVersionFallback()
    {
        var unsupported = new CodexRuntimeInspector(new RuntimeDiscoveryOptions
        {
            ExecutableArgumentPrefix = ["--capability-unsupported"],
            VersionProbeTimeout = TimeSpan.FromSeconds(5),
            CapabilityProbeTimeout = TimeSpan.FromSeconds(5),
        }).Inspect(RuntimeTestServer.ExecutablePath, probeCapabilities: true);
        var fallback = new CodexRuntimeInspector(new RuntimeDiscoveryOptions
        {
            ExecutableArgumentPrefix = ["--capability-no-files"],
            VersionProbeTimeout = TimeSpan.FromSeconds(5),
            CapabilityProbeTimeout = TimeSpan.FromSeconds(5),
        }).Inspect(RuntimeTestServer.ExecutablePath, probeCapabilities: true);

        Assert.Equal(CapabilityStatus.Unsupported, unsupported.AccountUsageRead);
        Assert.Equal(CapabilityStatus.Supported, fallback.AccountUsageRead);
    }

    [Fact]
    public async Task Inspector_TimesOutAndCleansUpOnlyItsSyntheticProbeProcess()
    {
        using var temp = new TemporaryDirectory();
        var pidMarker = System.IO.Path.Combine(temp.Path, "probe.pid");
        var started = Stopwatch.StartNew();
        var inspector = new CodexRuntimeInspector(new RuntimeDiscoveryOptions
        {
            ExecutableArgumentPrefix = ["--version-sleep=10000", $"--pid-marker={pidMarker}"],
            VersionProbeTimeout = TimeSpan.FromMilliseconds(200),
            CapabilityProbeTimeout = TimeSpan.FromMilliseconds(200),
            ProcessCleanupTimeout = TimeSpan.FromSeconds(2),
        });

        _ = inspector.Inspect(RuntimeTestServer.ExecutablePath, probeCapabilities: false);
        started.Stop();

        Assert.True(File.Exists(pidMarker));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Probe took {started.Elapsed}.");
        var processId = int.Parse(File.ReadAllText(pidMarker), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(3)));
    }

    private static RuntimeDiscoveryOptions EmptySearchOptions(string installBinDirectory) => new()
    {
        InstallBinDirectory = installBinDirectory,
        InstalledExecutableNames = ["codex.exe"],
        PathExecutableNames = ["codex.exe"],
        PathValue = string.Empty,
    };

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
}
