namespace Codex.RuntimeKit.Discovery;

/// <summary>Deterministic identities for cache entries and resolved executable observations.</summary>
public sealed record ExecutableIdentity(
    string Path,
    long? FileSize,
    DateTimeOffset? LastWriteTimeUtc,
    string? Version)
{
    /// <summary>Matches the cache key used by Control Center before extraction.</summary>
    public string CacheKey => FileSize.HasValue && LastWriteTimeUtc.HasValue
        ? $"{Path}|{LastWriteTimeUtc.Value.Ticks}|{FileSize.Value}"
        : $"{Path}|unknown";

    /// <summary>Matches the resolved-runtime identity used by Control Center before extraction.</summary>
    public string Value => FileSize.HasValue && LastWriteTimeUtc.HasValue
        ? CreateValue(Path, FileSize.Value, LastWriteTimeUtc.Value, Version)
        : $"{Path}|unknown|unknown|{Version ?? "unknown"}";

    public static ExecutableIdentity FromFile(string path, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return new ExecutableIdentity(path, file.Length, file.LastWriteTimeUtc, version);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return new ExecutableIdentity(path, null, null, version);
    }

    public static string CreateValue(string path, long fileSize, DateTimeOffset lastWriteTimeUtc, string? version) =>
        $"{path}|{fileSize}|{lastWriteTimeUtc.UtcTicks}|{version ?? "unknown"}";

    public static string CreateCacheKey(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromFile(path, null).CacheKey;
    }
}