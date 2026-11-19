namespace Codex.RuntimeKit.Discovery;

/// <summary>Environment overrides for deterministic runtime discovery and synthetic tests.</summary>
public sealed record RuntimeDiscoveryOptions
{
    /// <summary>Overrides the normal OpenAI Codex installation bin directory.</summary>
    public string? InstallBinDirectory { get; init; }

    /// <summary>Overrides PATH. Null reads the process PATH; an empty value searches no PATH entries.</summary>
    public string? PathValue { get; init; }

    /// <summary>Optional arguments prepended to probe arguments, useful for executable wrappers.</summary>
    public IReadOnlyList<string> ExecutableArgumentPrefix { get; init; } = [];

    /// <summary>Overrides executable names searched under the installation directory.</summary>
    public IReadOnlyList<string>? InstalledExecutableNames { get; init; }

    /// <summary>Overrides executable names searched in PATH directories.</summary>
    public IReadOnlyList<string>? PathExecutableNames { get; init; }

    public TimeSpan VersionProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan CapabilityProbeTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan ProcessCleanupTimeout { get; init; } = TimeSpan.FromSeconds(1);
}