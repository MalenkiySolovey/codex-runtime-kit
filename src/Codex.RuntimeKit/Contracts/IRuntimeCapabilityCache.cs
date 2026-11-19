using Codex.RuntimeKit.Capabilities;

namespace Codex.RuntimeKit.Contracts;

/// <summary>Stores capability observations by executable identity. Implementations must not store credentials.</summary>
public interface IRuntimeCapabilityCache
{
    RuntimeCapabilities? GetCapabilities(string executableIdentity);
    void SetCapabilities(string executableIdentity, RuntimeCapabilities capabilities);
    void Invalidate(string executableIdentity);
    void Clear();
}
