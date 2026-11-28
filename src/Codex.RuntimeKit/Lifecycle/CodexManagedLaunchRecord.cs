using System.Text.Json;
using Codex.RuntimeKit.Desktop.Lifecycle;

namespace Codex.RuntimeKit.Lifecycle;

/// <summary>Codex executable surface used by a launch profile.</summary>
public enum CodexLaunchRuntimeKind
{
    Cli = 0,
    Desktop = 1,
}

/// <summary>Durable process identity shared by launch recovery and the Desktop supervisor.</summary>
public class CodexManagedLaunchRecord
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public Guid SessionId { get; set; }
    public CodexLaunchRuntimeKind? RuntimeKind { get; set; }
    public int ProcessId { get; set; }
    public DateTimeOffset ProcessStartedAt { get; set; }
    public bool? ProcessStartMayHaveBeenIssued { get; set; }
    public string? ProcessExecutablePath { get; set; }
    public DesktopExitSupervisorIdentity? DesktopExitSupervisor { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, GetType(), JsonOptions);

    public static T? Deserialize<T>(string contents) where T : CodexManagedLaunchRecord =>
        JsonSerializer.Deserialize<T>(contents, JsonOptions);
}
