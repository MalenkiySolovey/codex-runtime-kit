using System.Diagnostics;
using System.Globalization;
using Codex.RuntimeKit.Desktop.Discovery;

namespace Codex.RuntimeKit.Desktop.Lifecycle;

/// <summary>Applies the verified Windows Desktop quit-on-parent-exit launch option.</summary>
public static class CodexDesktopParentLifetime
{
    private const string ParentProcessIdVariable = "CODEX_ELECTRON_DEV_PARENT_PID";
    private const string WebviewProcessIdVariable = "CODEX_ELECTRON_DEV_WEBVIEW_PID";
    private static readonly HashSet<Version> VerifiedVersions = [new(26, 1117, 4957, 0), new(26, 1120, 6357, 0)];

    /// <summary>Checks a previously verified installation against the tested Desktop build.</summary>
    public static bool IsSupported(CodexDesktopInstallation installation) =>
        OperatingSystem.IsWindows() &&
        installation.PackageFamilyName == "OpenAI.Codex_2p2nqsd0c76g0" &&
        VerifiedVersions.Contains(installation.Version);

    /// <summary>Clears inherited development lifetime settings and applies the owned supervisor when supported.</summary>
    public static bool ConfigureLaunch(
        ProcessStartInfo startInfo,
        CodexDesktopInstallation installation,
        int? supervisorProcessId = null)
    {
        startInfo.Environment.Remove(ParentProcessIdVariable);
        startInfo.Environment.Remove(WebviewProcessIdVariable);
        if (supervisorProcessId is not > 0 ||
            !IsSupported(installation) ||
            !string.Equals(startInfo.FileName, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        startInfo.Environment[ParentProcessIdVariable] = supervisorProcessId.Value.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
