using System.Collections.Concurrent;
using Codex.RuntimeKit.Capabilities;

namespace Codex.RuntimeKit.Contracts;

/// <summary>Thread-safe in-memory capability cache with no persistent or credential-bearing state.</summary>
public sealed class InMemoryRuntimeCapabilityCache : IRuntimeCapabilityCache
{
    private readonly ConcurrentDictionary<string, RuntimeCapabilities> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeCapabilities? GetCapabilities(string executableIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableIdentity);
        return _cache.TryGetValue(executableIdentity, out var capabilities) ? capabilities : null;
    }

    public void SetCapabilities(string executableIdentity, RuntimeCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableIdentity);
        ArgumentNullException.ThrowIfNull(capabilities);
        _cache[executableIdentity] = capabilities;
    }

    public void Invalidate(string executableIdentity)
    {
        if (!string.IsNullOrWhiteSpace(executableIdentity))
        {
            _cache.TryRemove(executableIdentity, out _);
        }
    }

    public void Clear() => _cache.Clear();
}
