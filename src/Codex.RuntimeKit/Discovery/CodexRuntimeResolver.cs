using Codex.RuntimeKit.Capabilities;
using Codex.RuntimeKit.Contracts;

namespace Codex.RuntimeKit.Discovery;

/// <summary>Discovers Codex executables, validates overrides, and probes runtime capabilities.</summary>
public sealed class CodexRuntimeResolver
{
    private readonly IRuntimeCapabilityCache _capabilityCache;
    private readonly IRuntimeInspector _inspector;
    private readonly RuntimeDiscoveryOptions _options;

    public CodexRuntimeResolver(
        IRuntimeCapabilityCache? capabilityCache = null,
        IRuntimeInspector? inspector = null,
        RuntimeDiscoveryOptions? options = null)
    {
        _options = options ?? new RuntimeDiscoveryOptions();
        _capabilityCache = capabilityCache ?? new InMemoryRuntimeCapabilityCache();
        _inspector = inspector ?? new CodexRuntimeInspector(_options);
    }

    public RuntimeInfo ResolveCurrentRuntime(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return ResolveCandidate(overridePath);
        }

        var installed = EnumerateHashedCandidates();
        if (installed.Count > 0)
        {
            return ResolveCandidate(installed[0].Path, installed[0]);
        }

        foreach (var path in EnumerateWellKnownPaths())
        {
            if (File.Exists(path))
            {
                return ResolveCandidate(path);
            }
        }

        var pathCandidate = FindExecutableOnPath();
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            return ResolveCandidate(pathCandidate);
        }

        return new RuntimeInfo(
            string.Empty,
            "not_found",
            null,
            new RuntimeCapabilities(
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported));
    }

    public IReadOnlyList<RuntimeCandidate> EnumerateCandidates()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seenPaths = new HashSet<string>(comparer);
        var candidates = new List<RuntimeCandidate>();

        foreach (var candidate in EnumerateHashedCandidates())
        {
            if (seenPaths.Add(candidate.Path))
            {
                candidates.Add(candidate);
            }
        }

        foreach (var path in EnumerateWellKnownPaths())
        {
            if (File.Exists(path) && seenPaths.Add(path))
            {
                candidates.Add(CreateCandidate(path, RuntimeCandidateSource.OfficialWellKnown));
            }
        }

        foreach (var path in FindAllExecutablesOnPath())
        {
            if (seenPaths.Add(path))
            {
                candidates.Add(CreateCandidate(path, RuntimeCandidateSource.Path));
            }
        }

        return candidates;
    }

    public RuntimeValidationResult ValidateExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new RuntimeValidationResult(false, null, "Path is empty or null.");
        }

        if (!File.Exists(path))
        {
            return new RuntimeValidationResult(false, null, $"File not found: {path}");
        }

        try
        {
            var inspection = InspectCandidate(path);
            if (string.IsNullOrWhiteSpace(inspection.Version) || string.Equals(inspection.Version, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeValidationResult(false, null, "Could not extract Codex version from executable.");
            }

            return new RuntimeValidationResult(true, inspection.Version, null);
        }
        catch (Exception ex)
        {
            return new RuntimeValidationResult(false, null, ex.Message);
        }
    }

    public static string ProbeVersion(string executablePath) => CodexRuntimeInspector.ProbeVersion(executablePath);

    public static string GetExecutableIdentity(string path) => ExecutableIdentity.CreateCacheKey(path);

    private RuntimeInfo ResolveCandidate(string path, RuntimeCandidate? candidate = null)
    {
        var inspection = InspectCandidate(path);
        var fileIdentity = candidate is null
            ? ExecutableIdentity.FromFile(path, inspection.Version).Value
            : ExecutableIdentity.CreateValue(path, candidate.FileSize, candidate.LastWriteTimeUtc, candidate.Version);
        return new RuntimeInfo(
            path,
            inspection.Version ?? "unknown",
            fileIdentity,
            GetCapabilities(path, inspection));
    }

    private RuntimeCandidate CreateCandidate(string path, RuntimeCandidateSource source)
    {
        var inspection = InspectCandidate(path);
        var file = new FileInfo(path);
        return new RuntimeCandidate(
            path,
            inspection.Version,
            file.Length,
            file.LastWriteTimeUtc,
            true,
            source);
    }

    private RuntimeInspection InspectCandidate(string path)
    {
        var identity = ExecutableIdentity.CreateCacheKey(path);
        var cached = _capabilityCache.GetCapabilities(identity);
        var inspection = _inspector.Inspect(path, probeCapabilities: cached is null);
        if (cached is not null)
        {
            return inspection with { AccountUsageRead = cached.AccountUsageRead };
        }

        var capabilities = inspection.AccountUsageRead == CapabilityStatus.Supported
            ? RuntimeCapabilities.ModernFull
            : RuntimeCapabilities.LegacyUnsupportedUsage;
        _capabilityCache.SetCapabilities(identity, capabilities);
        return inspection with { AccountUsageRead = capabilities.AccountUsageRead };
    }

    private RuntimeCapabilities GetCapabilities(string path, RuntimeInspection inspection)
    {
        var cached = _capabilityCache.GetCapabilities(ExecutableIdentity.CreateCacheKey(path));
        if (cached is not null)
        {
            return cached;
        }

        return inspection.AccountUsageRead == CapabilityStatus.Supported
            ? RuntimeCapabilities.ModernFull
            : RuntimeCapabilities.LegacyUnsupportedUsage;
    }

    private List<RuntimeCandidate> EnumerateHashedCandidates()
    {
        var candidates = new List<RuntimeCandidate>();
        foreach (var binDirectory in GetInstallBinDirectories())
        {
            try
            {
                if (!Directory.Exists(binDirectory))
                {
                    continue;
                }

                foreach (var subdirectory in Directory.GetDirectories(binDirectory))
                {
                    foreach (var executableName in GetInstalledExecutableNames())
                    {
                        var executable = Path.Combine(subdirectory, executableName);
                        if (File.Exists(executable))
                        {
                            var candidate = CreateCandidate(executable, RuntimeCandidateSource.OfficialHashDirectory);
                            candidates.Add(candidate);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        candidates.Sort(CompareCandidates);
        return candidates;
    }

    private IEnumerable<string> EnumerateWellKnownPaths()
    {
        foreach (var binDirectory in GetInstallBinDirectories())
        {
            foreach (var executableName in GetInstalledExecutableNames())
            {
                yield return Path.Combine(binDirectory, executableName);
            }
        }
    }

    private IReadOnlyList<string> GetInstallBinDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_options.InstallBinDirectory))
        {
            return [_options.InstallBinDirectory];
        }

        var directories = new List<string>();
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            directories.Add(Path.Combine(localApplicationData, "OpenAI", "Codex", "bin"));
        }

        if (!OperatingSystem.IsWindows())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                directories.Add(Path.Combine(home, ".local", "bin"));
            }
        }

        return directories.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<string> GetInstalledExecutableNames() => _options.InstalledExecutableNames ??
        (OperatingSystem.IsWindows() ? ["codex.exe"] : ["codex"]);

    private IReadOnlyList<string> GetPathExecutableNames() => _options.PathExecutableNames ??
        (OperatingSystem.IsWindows() ? ["codex.exe", "codex.cmd", "codex.bat", "codex.ps1", "codex"] : ["codex"]);

    private string? FindExecutableOnPath() => FindAllExecutablesOnPath().FirstOrDefault();

    private IReadOnlyList<string> FindAllExecutablesOnPath()
    {
        var results = new List<string>();
        try
        {
            var pathValue = _options.PathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var directories = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directory in directories)
            {
                foreach (var name in GetPathExecutableNames())
                {
                    try
                    {
                        var fullPath = Path.Combine(directory, name);
                        if (File.Exists(fullPath))
                        {
                            results.Add(fullPath);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }

        return results;
    }

    private static int CompareCandidates(RuntimeCandidate left, RuntimeCandidate right)
    {
        var leftVersion = ParseSemanticVersion(left.Version);
        var rightVersion = ParseSemanticVersion(right.Version);
        if (leftVersion is not null && rightVersion is not null)
        {
            var comparison = rightVersion.CompareTo(leftVersion);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        else if (leftVersion is not null)
        {
            return -1;
        }
        else if (rightVersion is not null)
        {
            return 1;
        }

        return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
    }

    private static Version? ParseSemanticVersion(string? value)
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

        return Version.TryParse(clean, out var version) ? version : null;
    }
}