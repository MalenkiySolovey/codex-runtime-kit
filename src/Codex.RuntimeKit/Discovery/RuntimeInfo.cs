using Codex.RuntimeKit.Capabilities;

namespace Codex.RuntimeKit.Discovery;

/// <summary>Resolved executable and its identity-specific runtime observations.</summary>
public sealed record RuntimeInfo(
    string ExecutablePath,
    string? Version,
    string? FileIdentity,
    RuntimeCapabilities Capabilities)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>Typed result for validating a custom executable path.</summary>
public sealed record RuntimeValidationResult(bool IsValid, string? Version, string? ErrorMessage);