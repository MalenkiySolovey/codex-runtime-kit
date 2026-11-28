using Codex.RuntimeKit.Configuration;

namespace Codex.RuntimeKit.Tests.TestSupport;

internal sealed class TestConfigFileSystem : ICodexConfigFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllBytesAtomic(string path, byte[] contents) => File.WriteAllBytes(path, contents);
    public void WriteAllTextAtomic(string path, string contents) => File.WriteAllText(path, contents);
    public void Delete(string path) => File.Delete(path);
    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern) =>
        Directory.GetFiles(directory, searchPattern);
}
