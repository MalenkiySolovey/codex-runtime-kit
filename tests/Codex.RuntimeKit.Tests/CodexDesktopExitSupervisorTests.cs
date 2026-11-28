using System.Diagnostics;
using Codex.RuntimeKit.Desktop.Lifecycle;

namespace Codex.RuntimeKit.Tests;

public sealed class CodexDesktopExitSupervisorTests
{
    [Fact]
    public void StopSignalsOnlyTheSelectedSessionWithTheExactSupervisorIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;

        var owner = new CodexDesktopExitSupervisor(".test-launch.json", "RuntimeKit.TestStop");
        using var process = Process.GetCurrentProcess();
        var supervisor = new DesktopExitSupervisorIdentity(
            process.Id, process.StartTime.ToUniversalTime(), process.MainModule!.FileName);
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        using var stop = new EventWaitHandle(
            false, EventResetMode.ManualReset, owner.StopName(sessionId));
        using var otherStop = new EventWaitHandle(
            false, EventResetMode.ManualReset, owner.StopName(otherSessionId));

        Assert.Null(owner.TryRequestStop(
            sessionId, supervisor with { StartedAt = supervisor.StartedAt.AddSeconds(-1) }));
        Assert.Null(owner.TryRequestStop(
            sessionId, supervisor with { ExecutablePath = "unrelated.exe" }));
        Assert.False(stop.WaitOne(0));

        using var verified = owner.TryRequestStop(sessionId, supervisor);
        Assert.Equal(process.Id, verified?.Id);
        Assert.True(stop.WaitOne(0));
        Assert.False(otherStop.WaitOne(0));
    }
}
