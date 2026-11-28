using System.ComponentModel;
using System.Diagnostics;
using Codex.RuntimeKit.Desktop.Lifecycle;

namespace Codex.RuntimeKit.Lifecycle;

public enum CodexProcessRecoveryState
{
    Exited,
    Running,
    Unverified,
}

/// <summary>Verifies a persisted process identity and stops only a caller-owned process.</summary>
public static class CodexProcessLifecycle
{
    private static readonly TimeSpan DesktopQuitTimeout = TimeSpan.FromSeconds(18);

    public static CodexProcessRecoveryState Recover(CodexManagedLaunchRecord record, out Process? runningProcess)
    {
        ArgumentNullException.ThrowIfNull(record);
        runningProcess = null;
        if (record.ProcessId <= 0)
            return record.ProcessStartMayHaveBeenIssued == false ? CodexProcessRecoveryState.Exited : CodexProcessRecoveryState.Unverified;
        if (record.ProcessStartedAt == default)
            return CodexProcessRecoveryState.Unverified;

        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(record.ProcessId);
            var startedAt = TryGetStartTime(candidate);
            if (startedAt is null || startedAt < record.ProcessStartedAt) return CodexProcessRecoveryState.Unverified;
            if (startedAt > record.ProcessStartedAt || candidate.HasExited) return CodexProcessRecoveryState.Exited;
            if (record.ProcessExecutablePath is { } executable &&
                !CodexDesktopManagedLauncher.HasExpectedExecutable(candidate, executable))
                return CodexProcessRecoveryState.Unverified;

            runningProcess = candidate;
            candidate = null;
            return CodexProcessRecoveryState.Running;
        }
        catch (ArgumentException)
        {
            return CodexProcessRecoveryState.Exited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            return CodexProcessRecoveryState.Unverified;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    public static async Task StopAsync(Process process, CodexLaunchRuntimeKind runtimeKind, Guid sessionId,
        CodexDesktopExitSupervisor supervisor, DesktopExitSupervisorIdentity? identity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var desktop = runtimeKind == CodexLaunchRuntimeKind.Desktop;
        identity = desktop ? identity : null;
        try
        {
            if (!HasExitedOrDetached(process))
            {
                using var supervisorHandle = supervisor.TryRequestStop(sessionId, identity);
                var graceful = await RequestGracefulStopAsync(process, identity is null,
                    identity is null ? TimeSpan.FromSeconds(10) : DesktopQuitTimeout, cancellationToken).ConfigureAwait(false);
                if (!graceful)
                {
                    if (desktop)
                        throw new TimeoutException("The managed Codex Desktop process is still running. Use that instance's Quit command to finish closing the profile.");

                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Windows could not stop the managed Codex process.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await WaitForExitOrDetachedAsync(process, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The managed Codex process did not exit. Credential write-back was left pending.");
        }
    }

    public static async Task<bool> StopFailedLaunchAsync(Process process, CodexLaunchRuntimeKind runtimeKind,
        bool supervisorOwnsLifetime)
    {
        try
        {
            if (process.HasExited) return true;
            if (runtimeKind == CodexLaunchRuntimeKind.Desktop)
                return await RequestGracefulStopAsync(process, !supervisorOwnsLifetime,
                    supervisorOwnsLifetime ? DesktopQuitTimeout : TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException or ArgumentException)
        {
            return HasExitedOrDetached(process);
        }

        return process.HasExited;
    }

    public static DateTimeOffset? TryGetStartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    public static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    internal static async Task<bool> RequestGracefulStopAsync(
        Process process, bool requestWindowClose, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExitedOrDetached(process)) return true;
        if (requestWindowClose)
        {
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) when (HasExitedOrDetached(process)) { return true; }
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await WaitForExitOrDetachedAsync(process, timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HasExitedOrDetached(process);
        }
    }

    private static bool HasExitedOrDetached(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static async Task WaitForExitOrDetachedAsync(Process process, CancellationToken cancellationToken)
    {
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException)
        {
            // The owner can dispose its process handle after receiving Exited.
        }
    }
}
