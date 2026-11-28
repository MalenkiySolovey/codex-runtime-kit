using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Codex.RuntimeKit.Lifecycle;

namespace Codex.RuntimeKit.Desktop.Lifecycle;

public sealed record DesktopExitSupervisorIdentity(int ProcessId, DateTimeOffset StartedAt, string ExecutablePath);

/// <summary>A per-session host process that signals verified Desktop shutdown on exit.</summary>
public sealed class CodexDesktopExitSupervisor(string manifestFileName, string eventPrefix)
{
    private const string RunArgument = "--managed-desktop-exit-supervisor";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(30);

    public DesktopExitSupervisorIdentity? TryStart(
        string executable,
        string manifestPath,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(executable) ||
            !Path.IsPathFullyQualified(manifestPath) || Path.GetFileName(manifestPath) != manifestFileName || sessionId == Guid.Empty)
        {
            return null;
        }

        try
        {
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName(sessionId), out var createdStop);
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(sessionId), out var createdReady);
            if (!createdStop || !createdReady)
            {
                return null;
            }

            var started = false;
            try
            {
                var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                startInfo.ArgumentList.Add(RunArgument);
                startInfo.ArgumentList.Add(manifestPath);
                startInfo.ArgumentList.Add(sessionId.ToString("N"));
                startInfo.Environment.Clear();
                startInfo.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                using var supervisor = Process.Start(startInfo);
                if (supervisor is null ||
                    WaitHandle.WaitAny([ready, cancellationToken.WaitHandle], ReadyTimeout) != 0 ||
                    supervisor.HasExited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }

                var identity = new DesktopExitSupervisorIdentity(
                    supervisor.Id, supervisor.StartTime.ToUniversalTime(), executable);
                started = true;
                return identity;
            }
            finally
            {
                if (!started)
                {
                    stop.Set();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"[DesktopExit] Supervisor startup unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    // Retain the verified process handle until Desktop has had time to observe its exit.
    public Process? TryRequestStop(Guid sessionId, DesktopExitSupervisorIdentity? identity)
    {
        if (!OperatingSystem.IsWindows() || identity is null || sessionId == Guid.Empty)
        {
            return null;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            _ = process.SafeHandle;
            if (process.HasExited ||
                process.StartTime.ToUniversalTime() != identity.StartedAt ||
                !string.Equals(process.MainModule?.FileName, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !EventWaitHandle.TryOpenExisting(StopName(sessionId), out var stop))
            {
                return null;
            }

            using (stop)
            {
                stop.Set();
            }

            var owned = process;
            process = null;
            return owned;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"[DesktopExit] Supervisor signal unavailable: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public bool TryRun(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length == 0 || args[0] != RunArgument)
        {
            return false;
        }

        if (args is [_, var path, var id] &&
            Path.IsPathFullyQualified(path) &&
            Path.GetFileName(path) == manifestFileName &&
            Guid.TryParseExact(id, "N", out var sessionId) &&
            sessionId != Guid.Empty)
        {
            try
            {
                RunAsync(path, sessionId).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                Debug.WriteLine($"[DesktopExit] Supervisor ended: {ex.GetType().Name}");
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private async Task RunAsync(string path, Guid sessionId)
    {
        if (!EventWaitHandle.TryOpenExisting(StopName(sessionId), out var stop))
        {
            return;
        }

        using (stop)
        {
            if (!EventWaitHandle.TryOpenExisting(ReadyName(sessionId), out var ready))
            {
                return;
            }

            using (ready)
            {
                if (ReadRecord(path, sessionId) is null)
                {
                    return;
                }

                ready.Set();
            }

            using var desktop = WaitForDesktop(path, sessionId, stop);
            if (desktop is null)
            {
                return;
            }

            var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = ThreadPool.RegisterWaitForSingleObject(
                stop, (_, _) => stopRequested.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                await Task.WhenAny(desktop.WaitForExitAsync(), stopRequested.Task).ConfigureAwait(false);
            }
            finally
            {
                wait.Unregister(null);
            }
        }
    }

    private Process? WaitForDesktop(string path, Guid sessionId, EventWaitHandle stop)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < BindTimeout && !stop.WaitOne(TimeSpan.FromMilliseconds(200)))
        {
            var record = ReadRecord(path, sessionId);
            if (record is null)
            {
                return null;
            }

            if (record.ProcessId > 0 && record.ProcessStartedAt != default)
            {
                Process? desktop = Process.GetProcessById(record.ProcessId);
                try
                {
                    if (!desktop.HasExited && desktop.StartTime.ToUniversalTime() == record.ProcessStartedAt &&
                        (record.ProcessExecutablePath is null || CodexDesktopManagedLauncher.HasExpectedExecutable(desktop, record.ProcessExecutablePath)))
                    {
                        var owned = desktop;
                        desktop = null;
                        return owned;
                    }
                }
                finally
                {
                    desktop?.Dispose();
                }

                return null;
            }
        }

        return null;
    }

    private static CodexManagedLaunchRecord? ReadRecord(string path, Guid sessionId)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var record = CodexManagedLaunchRecord.Deserialize<CodexManagedLaunchRecord>(reader.ReadToEnd());
        return record is { RuntimeKind: CodexLaunchRuntimeKind.Desktop } && record.SessionId == sessionId ? record : null;
    }

    internal string StopName(Guid sessionId) => $@"Local\{eventPrefix}.{sessionId:N}";
    private string ReadyName(Guid sessionId) => $@"Local\{eventPrefix}.Ready.{sessionId:N}";
}
