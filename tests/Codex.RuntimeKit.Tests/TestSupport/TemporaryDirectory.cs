namespace Codex.RuntimeKit.Tests.TestSupport;

using Codex.RuntimeKit.Discovery;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-runtime-kit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string relativePath, string contents = "fixture")
    {
        var normalizedRelativePath = relativePath
            .Replace('/', System.IO.Path.DirectorySeparatorChar)
            .Replace('\\', System.IO.Path.DirectorySeparatorChar);
        var path = System.IO.Path.Combine(Path, normalizedRelativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class RuntimeTestServer
{
    public static string ExecutablePath
    {
        get
        {
            var executableName = OperatingSystem.IsWindows()
                ? "Codex.RuntimeKit.TestServer.exe"
                : "Codex.RuntimeKit.TestServer";
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestServer", executableName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The synthetic Codex runtime test server was not copied to the test output directory.", path);
            }

            return path;
        }
    }
}

internal sealed class EnvironmentVariableScope(params (string Name, string? Value)[] values) : IDisposable
{
    private readonly Dictionary<string, string?> _previousValues = values.ToDictionary(
        value => value.Name,
        value => Environment.GetEnvironmentVariable(value.Name),
        StringComparer.OrdinalIgnoreCase);

    public EnvironmentVariableScope Set()
    {
        foreach (var value in values)
        {
            Environment.SetEnvironmentVariable(value.Name, value.Value);
        }

        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previousValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

internal sealed class StubRuntimeInspector(Func<string, bool, RuntimeInspection> inspect) : IRuntimeInspector
{
    private readonly List<(string Path, bool ProbeCapabilities)> _calls = [];

    public IReadOnlyList<(string Path, bool ProbeCapabilities)> Calls => _calls;

    public RuntimeInspection Inspect(string executablePath, bool probeCapabilities)
    {
        _calls.Add((executablePath, probeCapabilities));
        return inspect(executablePath, probeCapabilities);
    }
}
