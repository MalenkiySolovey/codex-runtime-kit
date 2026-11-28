namespace Codex.RuntimeKit.Configuration;

/// <summary>Host file access used by config transactions. Writes replace the target atomically.</summary>
public interface ICodexConfigFileSystem
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    byte[] ReadAllBytes(string path);
    string ReadAllText(string path);
    void WriteAllBytesAtomic(string path, byte[] contents);
    void WriteAllTextAtomic(string path, string contents);
    void Delete(string path);
    IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern);
}

/// <summary>Product-owned routing state locations, provider ownership, and recovery rules.</summary>
public sealed record CodexRoutingConfigOptions(
    string BaselinePath,
    string BackupsDirectory,
    Func<string, bool> OwnsProvider)
{
    public Func<string, bool> IsManagedCatalog { get; init; } = static _ => false;
    public Func<string, bool> IsLegacyBaselineCatalog { get; init; } = static _ => false;
    public Func<string, bool> IsLegacyBaselineModel { get; init; } = static _ => false;
}
