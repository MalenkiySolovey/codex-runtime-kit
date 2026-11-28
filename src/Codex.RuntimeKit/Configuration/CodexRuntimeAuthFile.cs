namespace Codex.RuntimeKit.Configuration;

/// <summary>Applies caller-supplied credential bytes to a caller-owned isolated runtime home.</summary>
public static class CodexRuntimeAuthFile
{
    public static string GetPath(string isolatedCodexHome)
    {
        if (!Path.IsPathFullyQualified(isolatedCodexHome))
            throw new ArgumentException("An isolated Codex home must be an absolute path.", nameof(isolatedCodexHome));

        return Path.Combine(isolatedCodexHome, "auth.json");
    }

    public static void Apply(string isolatedCodexHome, byte[] credential, ICodexConfigFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(files);
        files.WriteAllBytesAtomic(GetPath(isolatedCodexHome), credential);
    }
}
