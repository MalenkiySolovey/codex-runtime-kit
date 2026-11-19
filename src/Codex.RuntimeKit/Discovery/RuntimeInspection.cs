using Codex.RuntimeKit.Capabilities;

namespace Codex.RuntimeKit.Discovery;

/// <summary>Version and capability observations from an executable probe.</summary>
public sealed record RuntimeInspection(string? Version, CapabilityStatus AccountUsageRead);

/// <summary>Probes an executable without retaining any output other than version and capability facts.</summary>
public interface IRuntimeInspector
{
    RuntimeInspection Inspect(string executablePath, bool probeCapabilities);
}