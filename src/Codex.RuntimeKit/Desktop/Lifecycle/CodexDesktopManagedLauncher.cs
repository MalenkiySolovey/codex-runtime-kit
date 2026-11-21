using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Codex.RuntimeKit.Desktop.DebugTransport;
using Codex.RuntimeKit.Desktop.Discovery;

namespace Codex.RuntimeKit.Desktop.Lifecycle;

public enum CodexDesktopLifecycleState
{
    Started = 0,
    ConsentRequired = 1,
    ExistingInstance = 2,
    InstallationUnverified = 3,
    ProcessInventoryUnavailable = 4,
    LaunchFailed = 5,
}

public sealed record CodexDesktopManagedLaunchRequest(
    CodexDesktopInstallation Installation,
    bool UserConsentGranted,
    string? ManagedProfileRoot = null,
    bool AllowConcurrentIsolatedInstance = false);

public sealed record CodexDesktopManagedLaunchResult(
    CodexDesktopLifecycleState State,
    CodexDesktopManagedSession? Session,
    string DiagnosticCode)
{
    public bool Started => State == CodexDesktopLifecycleState.Started && Session is not null;
}

public sealed record CodexDesktopProcessInventoryResult(bool IsCertain, bool HasOfficialProcess);

public interface ICodexDesktopProcessInventory
{
    CodexDesktopProcessInventoryResult Inspect(CodexDesktopInstallation installation);
}

public interface ICodexDesktopProcessStarter
{
    Process? Start(ProcessStartInfo startInfo);
}

/// <summary>Starts only a separately profiled, explicitly consented MSIX instance.</summary>
public sealed class CodexDesktopManagedLauncher
{
    private readonly CodexDesktopDiscovery _discovery;
    private readonly ICodexDesktopProcessInventory _processInventory;
    private readonly ICodexDesktopProcessStarter _processStarter;

    public CodexDesktopManagedLauncher(
        CodexDesktopDiscovery? discovery = null,
        ICodexDesktopProcessInventory? processInventory = null,
        ICodexDesktopProcessStarter? processStarter = null)
    {
        _discovery = discovery ?? new CodexDesktopDiscovery();
        _processInventory = processInventory ?? new WindowsCodexDesktopProcessInventory();
        _processStarter = processStarter ?? new SystemCodexDesktopProcessStarter();
    }

    public async Task<CodexDesktopManagedLaunchResult> StartAsync(
        CodexDesktopManagedLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.UserConsentGranted)
        {
            return new CodexDesktopManagedLaunchResult(
                CodexDesktopLifecycleState.ConsentRequired,
                null,
                "desktop.lifecycle.consent-required");
        }

        if (!_discovery.IsVerifiedInstallation(request.Installation))
        {
            return new CodexDesktopManagedLaunchResult(
                CodexDesktopLifecycleState.InstallationUnverified,
                null,
                "desktop.lifecycle.installation-unverified");
        }

        var inventory = _processInventory.Inspect(request.Installation);
        if (!inventory.IsCertain)
        {
            return new CodexDesktopManagedLaunchResult(
                CodexDesktopLifecycleState.ProcessInventoryUnavailable,
                null,
                "desktop.lifecycle.process-inventory-unavailable");
        }

        if (inventory.HasOfficialProcess && !request.AllowConcurrentIsolatedInstance)
        {
            return new CodexDesktopManagedLaunchResult(
                CodexDesktopLifecycleState.ExistingInstance,
                null,
                "desktop.lifecycle.existing-instance-requires-user-action");
        }

        try
        {
            if (request.ManagedProfileRoot is not null &&
                (!Path.IsPathFullyQualified(request.ManagedProfileRoot) || string.IsNullOrWhiteSpace(request.ManagedProfileRoot)))
            {
                return new CodexDesktopManagedLaunchResult(
                    CodexDesktopLifecycleState.LaunchFailed,
                    null,
                    "desktop.lifecycle.profile-root-invalid");
            }

            var profileRoot = Path.GetFullPath(request.ManagedProfileRoot ?? DefaultProfileRoot());
            if (Directory.Exists(profileRoot) && (File.GetAttributes(profileRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return new CodexDesktopManagedLaunchResult(
                    CodexDesktopLifecycleState.LaunchFailed,
                    null,
                    "desktop.lifecycle.profile-root-untrusted");
            }

            Directory.CreateDirectory(profileRoot);
            if ((File.GetAttributes(profileRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return new CodexDesktopManagedLaunchResult(
                    CodexDesktopLifecycleState.LaunchFailed,
                    null,
                    "desktop.lifecycle.profile-root-untrusted");
            }

            var profile = Path.Combine(profileRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            var codexHome = Path.Combine(profile, "codex-home");
            var launchStartedAt = DateTime.UtcNow;
            var startInfo = new ProcessStartInfo
            {
                FileName = request.Installation.ExecutablePath,
                UseShellExecute = false,
                WorkingDirectory = request.Installation.InstallLocation,
                CreateNoWindow = false,
            };
            startInfo.ArgumentList.Add($"--user-data-dir={profile}");
            startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.Environment["CODEX_HOME"] = codexHome;
            startInfo.Environment.Remove("OPENAI_API_KEY");
            startInfo.Environment.Remove("CODEX_API_KEY");

            var process = _processStarter.Start(startInfo);
            if (process is null || process.HasExited)
            {
                process?.Dispose();
                Directory.Delete(profile);
                return new CodexDesktopManagedLaunchResult(
                    CodexDesktopLifecycleState.LaunchFailed,
                    null,
                    "desktop.lifecycle.launch-failed");
            }

            if (!HasExpectedExecutable(process, request.Installation.ExecutablePath))
            {
                var expectedProcess = await WaitForManagedProfileProcessAsync(
                    profile,
                    request.Installation.ExecutablePath,
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);
                if (expectedProcess is null)
                {
                    process.Dispose();
                    return new CodexDesktopManagedLaunchResult(
                        CodexDesktopLifecycleState.LaunchFailed,
                        null,
                        "desktop.lifecycle.process-image-unverified");
                }

                process.Dispose();
                process = expectedProcess;
            }

            var session = new CodexDesktopManagedSession(process, profile, request.Installation.ExecutablePath, launchStartedAt);
            return new CodexDesktopManagedLaunchResult(
                CodexDesktopLifecycleState.Started,
                session,
                inventory.HasOfficialProcess
                    ? "desktop.lifecycle.started-isolated-profile-alongside-existing-instance"
                    : "desktop.lifecycle.started-isolated-profile");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return new CodexDesktopManagedLaunchResult(
                CodexDesktopLifecycleState.LaunchFailed,
                null,
                "desktop.lifecycle.launch-failed");
        }
    }

    private static async Task<Process?> WaitForManagedProfileProcessAsync(
        string profileDirectory,
        string expectedExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            return null;
        }

        var ownerResolver = new WindowsCodexDesktopListenerOwnerResolver();
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryResolveManagedProfileProcess(profileDirectory, expectedExecutablePath, ownerResolver, out var process))
            {
                return process;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    internal static bool TryResolveManagedProfileProcess(
        string profileDirectory,
        string expectedExecutablePath,
        ICodexDesktopListenerOwnerResolver ownerResolver,
        out Process? process)
    {
        process = null;
        try
        {
            var fullProfile = Path.GetFullPath(profileDirectory);
            var activePortPath = Path.Combine(fullProfile, "DevToolsActivePort");
            var file = new FileInfo(activePortPath);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 256)
            {
                return false;
            }

            var lines = File.ReadAllLines(activePortPath);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 ||
                !Regex.IsMatch(lines[1], "^/devtools/browser/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
            {
                return false;
            }

            var ownerProcessId = ownerResolver.GetLoopbackListenerOwnerProcessId(port);
            if (ownerProcessId is null or <= 0)
            {
                return false;
            }

            var candidate = Process.GetProcessById(ownerProcessId.Value);
            if (!HasExpectedExecutable(candidate, expectedExecutablePath))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            process = null;
            return false;
        }
    }

    private static string DefaultProfileRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexRuntimeKit",
        "ManagedDesktopProfiles");

    internal static bool HasExpectedExecutable(Process process, string expectedExecutablePath)
    {
        try
        {
            var actualExecutablePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actualExecutablePath) &&
                   string.Equals(
                       Path.GetFullPath(actualExecutablePath),
                       Path.GetFullPath(expectedExecutablePath),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return false;
        }
    }
}

public sealed class CodexDesktopManagedSession : IAsyncDisposable
{
    private readonly Process _process;
    private bool _disposed;

    internal CodexDesktopManagedSession(Process process, string profileDirectory, string executablePath, DateTime startedAt)
    {
        _process = process;
        ProcessId = process.Id;
        ProfileDirectory = profileDirectory;
        ExecutablePath = executablePath;
        StartedAt = startedAt;
    }

    public int ProcessId { get; }
    public string ProfileDirectory { get; }
    public string ExecutablePath { get; }
    public DateTime StartedAt { get; }

    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>Requests a normal window close and never force-terminates the process.</summary>
    public async Task<bool> RequestGracefulStopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!IsRunning)
        {
            return true;
        }

        _process.CloseMainWindow();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return !IsRunning;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _process.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class SystemCodexDesktopProcessStarter : ICodexDesktopProcessStarter
{
    public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}

public sealed class WindowsCodexDesktopProcessInventory : ICodexDesktopProcessInventory
{
    public CodexDesktopProcessInventoryResult Inspect(CodexDesktopInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!OperatingSystem.IsWindows())
        {
            return new(false, false);
        }

        var executableName = Path.GetFileNameWithoutExtension(installation.ExecutablePath);
        try
        {
            var processPaths = new List<string?>();
            foreach (var process in Process.GetProcessesByName(executableName))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return new(false, false);
                        }

                        processPaths.Add(path);
                    }
                    catch (Win32Exception)
                    {
                        return new(false, false);
                    }
                    catch (InvalidOperationException)
                    {
                        return new(false, false);
                    }
                }
            }

            return ClassifyProcessPaths(processPaths, installation);
        }
        catch (Win32Exception)
        {
            return new(false, false);
        }
    }

    internal static CodexDesktopProcessInventoryResult ClassifyProcessPaths(
        IEnumerable<string?> executablePaths,
        CodexDesktopInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(executablePaths);
        ArgumentNullException.ThrowIfNull(installation);

        foreach (var executablePath in executablePaths)
        {
            if (!IsInExpectedPackageFamily(executablePath, installation))
            {
                // A same-named process from another path makes the inventory ambiguous.
                return new(false, false);
            }

            return new(true, true);
        }

        return new(true, false);
    }

    private static bool IsInExpectedPackageFamily(string? executablePath, CodexDesktopInstallation installation)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var packageNamePrefix = installation.PackageName + "_";
            if (!installation.PackageFamilyName.StartsWith(packageNamePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var familySuffix = "__" + installation.PackageFamilyName[packageNamePrefix.Length..];
            var packageRoot = Path.GetFullPath(installation.InstallLocation);
            var packagesRoot = Directory.GetParent(packageRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(packagesRoot))
            {
                return false;
            }

            var fullExecutablePath = Path.GetFullPath(executablePath);
            var packageRootPrefix = Path.GetFullPath(packagesRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullExecutablePath.StartsWith(packageRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = Path.GetRelativePath(packagesRoot, fullExecutablePath);
            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 ||
                !segments[0].StartsWith(packageNamePrefix, StringComparison.Ordinal) ||
                !segments[0].EndsWith(familySuffix, StringComparison.Ordinal) ||
                !string.Equals(
                    Path.Combine(segments.Skip(1).ToArray()),
                    Path.GetRelativePath(packageRoot, installation.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
