using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Codex.RuntimeKit.Desktop.Discovery;

public enum CodexDesktopDiscoveryState
{
    Available = 0,
    NotInstalled = 1,
    Unsupported = 2,
    InspectionFailed = 3,
}

public sealed record CodexDesktopPackageIdentity(
    string PackageName,
    string PackageFamilyName,
    string Publisher,
    string ApplicationId);

public sealed record CodexDesktopInstallation(
    string PackageName,
    string PackageFamilyName,
    string ApplicationUserModelId,
    string InstallLocation,
    string ExecutablePath,
    Version Version);

public sealed record CodexDesktopDiscoveryResult(
    CodexDesktopDiscoveryState State,
    CodexDesktopInstallation? Installation,
    string DiagnosticCode)
{
    public bool IsAvailable => State == CodexDesktopDiscoveryState.Available && Installation is not null;
}

public sealed record CodexDesktopDiscoveryOptions
{
    public string WindowsAppsRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    public IReadOnlyList<CodexDesktopPackageIdentity> PackageIdentities { get; init; } =
    [
        new(
            "OpenAI.Codex",
            "OpenAI.Codex_2p2nqsd0c76g0",
            "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",
            "App"),
    ];
}

public interface ICodexDesktopPackageLocationProvider
{
    IReadOnlyList<CodexDesktopRegisteredPackageLocation> GetCurrentUserPackages(CodexDesktopPackageIdentity identity);
}

public sealed record CodexDesktopRegisteredPackageLocation(string PackageFullName, string InstallLocation);

public enum CodexDesktopPackageIntegrity
{
    Unavailable,
    Invalid,
    Verified,
}

/// <summary>Platform access to Store signature, registration status, and package content verification.</summary>
public interface ICodexDesktopStorePackageVerifier
{
    bool IsTrusted(CodexDesktopInstallation installation);
    ValueTask<CodexDesktopPackageIntegrity> VerifyContentAsync(
        CodexDesktopInstallation installation, CancellationToken cancellationToken);
}

/// <summary>Reads only current-user MSIX registration; it never inspects other users' package records.</summary>
public sealed class WindowsCodexDesktopPackageLocationProvider : ICodexDesktopPackageLocationProvider
{
    private const string RepositoryKeyPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public IReadOnlyList<CodexDesktopRegisteredPackageLocation> GetCurrentUserPackages(CodexDesktopPackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<CodexDesktopRegisteredPackageLocation>();
        }

        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var repository = currentUser.OpenSubKey(RepositoryKeyPath, writable: false);
            if (repository is null)
            {
                return Array.Empty<CodexDesktopRegisteredPackageLocation>();
            }

            var familySuffix = "__" + identity.PackageFamilyName[(identity.PackageName.Length + 1)..];
            var packagePrefix = identity.PackageName + "_";
            var locations = new List<CodexDesktopRegisteredPackageLocation>();
            foreach (var packageFullName in repository.GetSubKeyNames())
            {
                if (!packageFullName.StartsWith(packagePrefix, StringComparison.Ordinal) ||
                    !packageFullName.EndsWith(familySuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                using var package = repository.OpenSubKey(packageFullName, writable: false);
                var installLocation = package?.GetValue("PackageRootFolder") as string;
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    locations.Add(new(packageFullName, installLocation));
                }
            }

            return locations;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Array.Empty<CodexDesktopRegisteredPackageLocation>();
        }
    }
}

/// <summary>
/// Discovers only MSIX packages whose signed manifest identity and executable path
/// match an explicitly configured official package identity.
/// </summary>
public sealed class CodexDesktopDiscovery
{
    private const string ManifestName = "AppxManifest.xml";
    private readonly CodexDesktopDiscoveryOptions _options;
    private readonly ICodexDesktopPackageLocationProvider _packageLocationProvider;

    public CodexDesktopDiscovery(
        CodexDesktopDiscoveryOptions? options = null,
        ICodexDesktopPackageLocationProvider? packageLocationProvider = null)
    {
        _options = options ?? new();
        _packageLocationProvider = packageLocationProvider ?? new WindowsCodexDesktopPackageLocationProvider();
    }

    public CodexDesktopDiscoveryResult Discover()
    {
        if (string.IsNullOrWhiteSpace(_options.WindowsAppsRoot) || _options.PackageIdentities.Count == 0)
        {
            return new(CodexDesktopDiscoveryState.InspectionFailed, null, "desktop.discovery.configuration-invalid");
        }

        try
        {
            if (!Directory.Exists(_options.WindowsAppsRoot))
            {
                return new(CodexDesktopDiscoveryState.NotInstalled, null, "desktop.package.not-found");
            }

            var verified = new List<CodexDesktopInstallation>();
            var sawUnverifiedCandidate = false;
            foreach (var identity in _options.PackageIdentities)
            {
                if (!IsValidIdentity(identity))
                {
                    return new(CodexDesktopDiscoveryState.InspectionFailed, null, "desktop.discovery.configuration-invalid");
                }

                var registeredVerifiedCount = verified.Count;
                foreach (var registered in _packageLocationProvider.GetCurrentUserPackages(identity))
                {
                    if (!IsRegisteredPackageRoot(registered, identity, out var packageRoot))
                    {
                        // Registrations outside the configured WindowsApps root are out of scope.
                        continue;
                    }

                    if (TryReadInstallation(packageRoot, identity, out var installation))
                    {
                        verified.Add(installation);
                    }
                    else
                    {
                        sawUnverifiedCandidate = true;
                    }
                }

                if (verified.Count > registeredVerifiedCount)
                {
                    // Per-user registration avoids enumerating WindowsApps, which is often ACL-protected.
                    continue;
                }

                var pattern = $"{identity.PackageName}_*__{identity.PackageFamilyName[(identity.PackageName.Length + 1)..]}";
                foreach (var packageRoot in Directory.EnumerateDirectories(_options.WindowsAppsRoot, pattern))
                {
                    if (TryReadInstallation(packageRoot, identity, out var installation))
                    {
                        verified.Add(installation);
                    }
                    else
                    {
                        sawUnverifiedCandidate = true;
                    }
                }
            }

            var latest = verified.OrderByDescending(item => item.Version).FirstOrDefault();
            if (latest is not null)
            {
                return new(CodexDesktopDiscoveryState.Available, latest, "desktop.package.verified");
            }

            return sawUnverifiedCandidate
                ? new(CodexDesktopDiscoveryState.Unsupported, null, "desktop.package.identity-unverified")
                : new(CodexDesktopDiscoveryState.NotInstalled, null, "desktop.package.not-found");
        }
        catch (UnauthorizedAccessException)
        {
            return new(CodexDesktopDiscoveryState.InspectionFailed, null, "desktop.discovery.access-denied");
        }
        catch (IOException)
        {
            return new(CodexDesktopDiscoveryState.InspectionFailed, null, "desktop.discovery.io-error");
        }
        catch (XmlException)
        {
            return new(CodexDesktopDiscoveryState.Unsupported, null, "desktop.package.manifest-invalid");
        }
    }

    public bool IsVerifiedInstallation(CodexDesktopInstallation? candidate)
    {
        if (candidate is null || !Directory.Exists(candidate.InstallLocation))
        {
            return false;
        }

        var identity = _options.PackageIdentities.FirstOrDefault(item =>
            string.Equals(item.PackageName, candidate.PackageName, StringComparison.Ordinal) &&
            string.Equals(item.PackageFamilyName, candidate.PackageFamilyName, StringComparison.Ordinal) &&
            string.Equals(candidate.ApplicationUserModelId, $"{item.PackageFamilyName}!{item.ApplicationId}", StringComparison.Ordinal));
        if (identity is null || !TryReadInstallation(candidate.InstallLocation, identity, out var verified))
        {
            return false;
        }

        return string.Equals(verified.ExecutablePath, Path.GetFullPath(candidate.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(verified.InstallLocation, Path.GetFullPath(candidate.InstallLocation), StringComparison.OrdinalIgnoreCase) &&
               verified.Version == candidate.Version;
    }

    /// <summary>Finds the installed package of an exact, still-running Desktop process.</summary>
    public CodexDesktopInstallation? FindForProcess(
        int processId, DateTimeOffset startedAt, ICodexDesktopStorePackageVerifier storeVerifier)
    {
        ArgumentNullException.ThrowIfNull(storeVerifier);
        using var process = Process.GetProcessById(processId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != startedAt.UtcDateTime ||
            process.MainModule?.FileName is not { } executable)
        {
            return null;
        }

        var relative = Path.GetRelativePath(_options.WindowsAppsRoot, executable);
        var directory = relative.Split(Path.DirectorySeparatorChar, 2);
        if (directory.Length != 2) return null;

        var packageRoot = Path.Combine(_options.WindowsAppsRoot, directory[0]);
        foreach (var identity in _options.PackageIdentities)
        {
            if (directory[0].StartsWith(identity.PackageName + "_", StringComparison.Ordinal) &&
                TryReadInstallation(packageRoot, identity, out var installation) &&
                string.Equals(installation.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase) &&
                storeVerifier.IsTrusted(installation))
            {
                return installation;
            }
        }

        return null;
    }

    private static bool IsValidIdentity(CodexDesktopPackageIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.PackageName) &&
        !string.IsNullOrWhiteSpace(identity.PackageFamilyName) &&
        identity.PackageFamilyName.StartsWith(identity.PackageName + "_", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(identity.Publisher) &&
        !string.IsNullOrWhiteSpace(identity.ApplicationId) &&
        identity.PackageName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private bool IsRegisteredPackageRoot(
        CodexDesktopRegisteredPackageLocation registered,
        CodexDesktopPackageIdentity identity,
        out string packageRoot)
    {
        packageRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(registered.PackageFullName) ||
            string.IsNullOrWhiteSpace(registered.InstallLocation))
        {
            return false;
        }

        var packagePrefix = identity.PackageName + "_";
        var familySuffix = "__" + identity.PackageFamilyName[(identity.PackageName.Length + 1)..];
        if (!registered.PackageFullName.StartsWith(packagePrefix, StringComparison.Ordinal) ||
            !registered.PackageFullName.EndsWith(familySuffix, StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFileName(registered.InstallLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                registered.PackageFullName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(_options.WindowsAppsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            packageRoot = Path.GetFullPath(registered.InstallLocation);
            return packageRoot.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(packageRoot, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            packageRoot = string.Empty;
            return false;
        }
    }

    private static bool TryReadInstallation(
        string packageRoot,
        CodexDesktopPackageIdentity expected,
        out CodexDesktopInstallation installation)
    {
        installation = null!;
        var fullRoot = Path.GetFullPath(packageRoot);
        var manifestPath = Path.Combine(fullRoot, ManifestName);
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1_000_000,
        };

        using var reader = XmlReader.Create(manifestPath, settings);
        var manifest = XDocument.Load(reader, LoadOptions.None);
        var identity = manifest.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Identity");
        var name = (string?)identity?.Attribute("Name");
        var publisher = (string?)identity?.Attribute("Publisher");
        var versionText = (string?)identity?.Attribute("Version");
        if (!string.Equals(name, expected.PackageName, StringComparison.Ordinal) ||
            !string.Equals(publisher, expected.Publisher, StringComparison.Ordinal) ||
            !Version.TryParse(versionText, out var version))
        {
            return false;
        }

        var application = manifest.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Application" &&
            string.Equals((string?)element.Attribute("Id"), expected.ApplicationId, StringComparison.Ordinal));
        var executableRelativePath = (string?)application?.Attribute("Executable");
        if (string.IsNullOrWhiteSpace(executableRelativePath) || Path.IsPathRooted(executableRelativePath))
        {
            return false;
        }

        var executablePath = Path.GetFullPath(Path.Combine(
            fullRoot,
            executableRelativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)));
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!executablePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(executablePath))
        {
            return false;
        }

        installation = new CodexDesktopInstallation(
            expected.PackageName,
            expected.PackageFamilyName,
            $"{expected.PackageFamilyName}!{expected.ApplicationId}",
            fullRoot,
            executablePath,
            version);
        return true;
    }
}
