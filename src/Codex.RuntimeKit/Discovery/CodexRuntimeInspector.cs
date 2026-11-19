using System.Diagnostics;
using System.Text.RegularExpressions;
using Codex.RuntimeKit.Capabilities;

namespace Codex.RuntimeKit.Discovery;

/// <summary>Runs bounded, isolated Codex CLI version and schema probes.</summary>
public sealed class CodexRuntimeInspector : IRuntimeInspector
{
    private static readonly Regex VersionPattern = new(@"(\d+\.\d+\.\d+(?:-[a-zA-Z0-9\.]+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaximumCapturedCharacters = 64 * 1024;
    private readonly RuntimeDiscoveryOptions _options;

    public CodexRuntimeInspector(RuntimeDiscoveryOptions? options = null)
    {
        _options = options ?? new RuntimeDiscoveryOptions();
        ValidateTimeout(_options.VersionProbeTimeout, nameof(_options.VersionProbeTimeout));
        ValidateTimeout(_options.CapabilityProbeTimeout, nameof(_options.CapabilityProbeTimeout));
        ValidateTimeout(_options.ProcessCleanupTimeout, nameof(_options.ProcessCleanupTimeout));
    }

    public RuntimeInspection Inspect(string executablePath, bool probeCapabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var version = ProbeVersion(executablePath, _options.VersionProbeTimeout, _options.ProcessCleanupTimeout);
        var capability = probeCapabilities
            ? ProbeAccountUsageRead(executablePath, version)
            : CapabilityStatus.Unknown;
        return new RuntimeInspection(version, capability);
    }

    public static string ProbeVersion(string executablePath) =>
        new CodexRuntimeInspector().ProbeVersion(executablePath, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));

    public static string GetExecutableIdentity(string path) => ExecutableIdentity.CreateCacheKey(path);

    private string ProbeVersion(string executablePath, TimeSpan timeout, TimeSpan cleanupTimeout)
    {
        var probeHome = CreateTemporaryDirectory("codex-runtime-version-");
        var result = RunProcess(executablePath, _options.ExecutableArgumentPrefix.Concat(["--version"]).ToArray(), timeout, cleanupTimeout, probeHome);
        if (result?.ProcessExited == true)
        {
            TryDeleteTemporaryDirectory(probeHome);
        }
        else if (result is null)
        {
            TryDeleteTemporaryDirectory(probeHome);
        }

        if (result is { ProcessExited: true, ExitCode: 0 })
        {
            var match = VersionPattern.Match(result.StandardOutput);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        try
        {
            var productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(productVersion))
            {
                var match = VersionPattern.Match(productVersion);
                return match.Success ? match.Groups[1].Value : productVersion;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return "unknown";
    }

    private CapabilityStatus ProbeAccountUsageRead(string executablePath, string? version)
    {
        var tempDirectory = CreateTemporaryDirectory("codex-runtime-schema-");
        var probeHome = Path.Combine(tempDirectory, "home");
        Directory.CreateDirectory(probeHome);
        var outputDirectory = Path.Combine(tempDirectory, "schema");
        Directory.CreateDirectory(outputDirectory);

        var result = RunProcess(
            executablePath,
            _options.ExecutableArgumentPrefix.Concat(["app-server", "generate-json-schema", "--out", outputDirectory]).ToArray(),
            _options.CapabilityProbeTimeout,
            _options.ProcessCleanupTimeout,
            probeHome);
        if (result?.ProcessExited == true)
        {
            try
            {
                if (result.ExitCode == 0)
                {
                    var files = Directory.GetFiles(outputDirectory, "*.json", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        foreach (var file in files)
                        {
                            var schema = File.ReadAllText(file);
                            if (schema.Contains("account/usage/read", StringComparison.OrdinalIgnoreCase))
                            {
                                return CapabilityStatus.Supported;
                            }
                        }

                        return CapabilityStatus.Unsupported;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
            finally
            {
                TryDeleteTemporaryDirectory(tempDirectory);
            }
        }
        else if (result is null)
        {
            TryDeleteTemporaryDirectory(tempDirectory);
        }

        var parsedVersion = TryParseVersion(version);
        return parsedVersion is not null && parsedVersion >= new Version(0, 140, 0)
            ? CapabilityStatus.Supported
            : CapabilityStatus.Unsupported;
    }

    private static ProcessRunResult? RunProcess(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        TimeSpan cleanupTimeout,
        string isolatedCodexHome)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CODEX_HOME"] = isolatedCodexHome;
        startInfo.Environment.Remove("CODEX_SQLITE_HOME");
        startInfo.Environment.Remove("OPENAI_API_KEY");
        startInfo.Environment.Remove("CODEX_API_KEY");

        Process? process = null;
        var cleanupTransferred = false;
        using var readCancellation = new CancellationTokenSource();
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumCapturedCharacters, readCancellation.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaximumCapturedCharacters, readCancellation.Token);
            try
            {
                process.WaitForExitAsync().WaitAsync(timeout).GetAwaiter().GetResult();
                var stdout = stdoutTask.GetAwaiter().GetResult();
                _ = stderrTask.GetAwaiter().GetResult();
                return new ProcessRunResult(true, process.ExitCode, stdout);
            }
            catch (TimeoutException)
            {
                readCancellation.Cancel();
                var exited = TryStopOwnedProcess(process, cleanupTimeout);
                if (!exited)
                {
                    cleanupTransferred = true;
                    _ = DisposeWhenExitedAsync(process);
                }

                return new ProcessRunResult(exited, null, string.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            if (process is not null && HasExited(process) == false)
            {
                readCancellation.Cancel();
                if (!TryStopOwnedProcess(process, cleanupTimeout))
                {
                    cleanupTransferred = true;
                    _ = DisposeWhenExitedAsync(process);
                }
            }

            return process is not null && HasExited(process)
                ? new ProcessRunResult(true, TryGetExitCode(process), string.Empty)
                : null;
        }
        finally
        {
            if (process is not null && !cleanupTransferred)
            {
                process.Dispose();
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 1024));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return builder.ToString();
    }

    private static bool TryStopOwnedProcess(Process process, TimeSpan cleanupTimeout)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExitAsync().WaitAsync(cleanupTimeout).GetAwaiter().GetResult();
            }

            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException)
        {
            return HasExited(process);
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task DisposeWhenExitedAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = value.TrimStart('v');
        var prerelease = clean.IndexOf('-');
        if (prerelease > 0)
        {
            clean = clean[..prerelease];
        }

        return Version.TryParse(clean, out var parsed) ? parsed : null;
    }

    private static void ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(name, "Probe timeouts must be finite and greater than zero.");
        }
    }

    private sealed record ProcessRunResult(bool ProcessExited, int? ExitCode, string StandardOutput);
}
