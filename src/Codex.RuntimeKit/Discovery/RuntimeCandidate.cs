namespace Codex.RuntimeKit.Discovery;

/// <summary>Where a runtime executable candidate was discovered.</summary>
public enum RuntimeCandidateSource
{
    CustomOverride = 0,
    OfficialHashDirectory = 1,
    OfficialWellKnown = 2,
    Path = 3,
}

/// <summary>A discovered Codex runtime executable.</summary>
public sealed record RuntimeCandidate(
    string Path,
    string? Version,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc,
    bool IsAvailable,
    RuntimeCandidateSource Source,
    string? Error = null);