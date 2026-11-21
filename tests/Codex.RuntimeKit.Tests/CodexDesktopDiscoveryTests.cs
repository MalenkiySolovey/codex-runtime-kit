using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Tests.TestSupport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

public sealed class CodexDesktopDiscoveryTests
{
    private static readonly CodexDesktopPackageIdentity Identity = new(
        "OpenAI.Codex",
        "OpenAI.Codex_2p2nqsd0c76g0",
        "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",
        "App");

    [Fact]
    public void Discover_VerifiesPackageManifestAndContainedExecutable()
    {
        using var temp = new TemporaryDirectory();
        var root = CreatePackage(temp.Path, "26.1117.3838.0", Identity);

        var result = new CodexDesktopDiscovery(Options(temp.Path)).Discover();

        Assert.Equal(CodexDesktopDiscoveryState.Available, result.State);
        var installation = Assert.IsType<CodexDesktopInstallation>(result.Installation);
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", installation.ApplicationUserModelId);
        Assert.Equal(new Version(26, 1117, 3838, 0), installation.Version);
        Assert.Equal(Path.Combine(root, "app", "ChatGPT.exe"), installation.ExecutablePath);
        Assert.Equal("desktop.package.verified", result.DiagnosticCode);
    }

    [Fact]
    public void Discover_SelectsTheNewestVerifiedPackageVersion()
    {
        using var temp = new TemporaryDirectory();
        CreatePackage(temp.Path, "26.1111.9664.0", Identity);
        CreatePackage(temp.Path, "26.1117.3838.0", Identity);

        var result = new CodexDesktopDiscovery(Options(temp.Path)).Discover();

        Assert.Equal(new Version(26, 1117, 3838, 0), result.Installation?.Version);
    }

    [Fact]
    public void Discover_UsesVerifiedCurrentUserPackageRegistrationWithoutEnumeratingWindowsApps()
    {
        using var temp = new TemporaryDirectory();
        var packageRoot = CreatePackage(temp.Path, "26.1117.3838.0", Identity);
        var packageName = Path.GetFileName(packageRoot);
        var provider = new FixedPackageLocationProvider(
            new CodexDesktopRegisteredPackageLocation(packageName, packageRoot));

        var result = new CodexDesktopDiscovery(Options(temp.Path), provider).Discover();

        Assert.Equal(CodexDesktopDiscoveryState.Available, result.State);
        Assert.Equal(new Version(26, 1117, 3838, 0), result.Installation?.Version);
    }

    [Fact]
    public void Discover_RejectsRegisteredPackageRootsOutsideWindowsApps()
    {
        using var windowsApps = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var outsideRoot = CreatePackage(outside.Path, "26.1117.3838.0", Identity);
        var provider = new FixedPackageLocationProvider(new CodexDesktopRegisteredPackageLocation(
            Path.GetFileName(outsideRoot),
            outsideRoot));

        var result = new CodexDesktopDiscovery(Options(windowsApps.Path), provider).Discover();

        Assert.Equal(CodexDesktopDiscoveryState.NotInstalled, result.State);
        Assert.Null(result.Installation);
    }

    [Fact]
    public void Discover_RejectsPublisherMismatchAndPathTraversal()
    {
        using var temp = new TemporaryDirectory();
        var wrongPublisher = Identity with { Publisher = "CN=Untrusted" };
        CreatePackage(temp.Path, "26.1117.3838.0", wrongPublisher);

        var result = new CodexDesktopDiscovery(Options(temp.Path)).Discover();

        Assert.Equal(CodexDesktopDiscoveryState.Unsupported, result.State);
        Assert.Null(result.Installation);
        Assert.Equal("desktop.package.identity-unverified", result.DiagnosticCode);

        using var traversal = new TemporaryDirectory();
        CreatePackage(traversal.Path, "26.1117.3838.0", Identity, "..\\outside.exe");
        var traversalResult = new CodexDesktopDiscovery(Options(traversal.Path)).Discover();
        Assert.Equal(CodexDesktopDiscoveryState.Unsupported, traversalResult.State);
        Assert.Null(traversalResult.Installation);
    }

    [Fact]
    public void Discover_ReportsNoInstalledPackageWithoutLeakingLocalPaths()
    {
        using var temp = new TemporaryDirectory();

        var result = new CodexDesktopDiscovery(Options(temp.Path)).Discover();

        Assert.Equal(CodexDesktopDiscoveryState.NotInstalled, result.State);
        Assert.Null(result.Installation);
        Assert.Equal("desktop.package.not-found", result.DiagnosticCode);
        Assert.DoesNotContain(temp.Path, result.DiagnosticCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_ProhibitsDtdAndExternalEntityManifestContent()
    {
        using var temp = new TemporaryDirectory();
        var packageRoot = Path.Combine(temp.Path, "OpenAI.Codex_26.1117.3838.0_x64__2p2nqsd0c76g0");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "AppxManifest.xml"), """
            <!DOCTYPE package [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <Package><Identity Name="OpenAI.Codex" Publisher="CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B" Version="26.1117.3838.0" />&xxe;</Package>
            """);

        var result = new CodexDesktopDiscovery(Options(temp.Path)).Discover();

        Assert.Equal(CodexDesktopDiscoveryState.Unsupported, result.State);
        Assert.Equal("desktop.package.manifest-invalid", result.DiagnosticCode);
    }

    private static CodexDesktopDiscoveryOptions Options(string root) => new()
    {
        WindowsAppsRoot = root,
        PackageIdentities = [Identity],
    };

    private static string CreatePackage(string root, string version, CodexDesktopPackageIdentity identity, string executableRelative = "app/ChatGPT.exe")
    {
        var packageRoot = Path.Combine(root, $"{identity.PackageName}_{version}_x64__2p2nqsd0c76g0");
        Directory.CreateDirectory(packageRoot);
        var executablePath = Path.GetFullPath(Path.Combine(packageRoot, executableRelative.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, "synthetic executable");
        var manifest = $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{identity.PackageName}" Publisher="{identity.Publisher}" Version="{version}" />
              <Applications><Application Id="{identity.ApplicationId}" Executable="{executableRelative}" /></Applications>
            </Package>
            """;
        File.WriteAllText(Path.Combine(packageRoot, "AppxManifest.xml"), manifest);
        return packageRoot;
    }

    private sealed class FixedPackageLocationProvider(params CodexDesktopRegisteredPackageLocation[] locations)
        : ICodexDesktopPackageLocationProvider
    {
        public IReadOnlyList<CodexDesktopRegisteredPackageLocation> GetCurrentUserPackages(CodexDesktopPackageIdentity identity) => locations;
    }
}
