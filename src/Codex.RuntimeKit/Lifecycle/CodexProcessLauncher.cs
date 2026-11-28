using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codex.RuntimeKit.Configuration;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Lifecycle;

namespace Codex.RuntimeKit.Lifecycle;

public sealed record CodexProcessLaunchOptions(
    string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory, string CodexHome,
    string? SharedSqliteHome = null, Func<string?>? ReadApiKey = null, string? ApiKeyEnvironmentVariable = null,
    CodexDesktopInstallation? DesktopInstallation = null, bool EnableLoopbackDebugging = false,
    int? DesktopSupervisorProcessId = null);

/// <summary>Starts the selected Codex executable in a caller-owned runtime home.</summary>
public static class CodexProcessLauncher
{
    public static Process Start(CodexProcessLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (executable, arguments, workingDirectory, codexHome, sharedSqliteHome, readApiKey, apiKeyVariable,
            desktopInstallation, enableLoopbackDebugging, desktopSupervisorProcessId) = options;
        if (!Path.IsPathFullyQualified(codexHome))
            throw new ArgumentException("Launch requires an absolute path to an isolated Codex home.", nameof(options));
        if (desktopInstallation is not null &&
            !string.Equals(executable, desktopInstallation.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The executable does not match the verified Desktop installation.", nameof(options));

        ValidateArguments(arguments);
        var apiKey = readApiKey?.Invoke();
        var extension = Path.GetExtension(executable);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = workingDirectory,
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        if (string.IsNullOrWhiteSpace(sharedSqliteHome))
        {
            startInfo.Environment.Remove(CodexSqliteHome.EnvironmentVariable);
        }
        else
        {
            startInfo.Environment[CodexSqliteHome.EnvironmentVariable] = sharedSqliteHome;
        }

        if (apiKeyVariable is not null) startInfo.Environment.Remove(apiKeyVariable);
        CodexDesktopManagedLauncher.ConfigureProfileStart(startInfo,
            desktopInstallation is null ? null : Path.Combine(codexHome, "user-data"), enableLoopbackDebugging);

        if (apiKeyVariable is not null && !string.IsNullOrWhiteSpace(apiKey))
        {
            startInfo.Environment[apiKeyVariable] = apiKey;
        }

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = ResolvePowerShellPath();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-EncodedCommand");

            var payload = JsonSerializer.Serialize(new
            {
                executable,
                workingDirectory,
                arguments,
            });
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            var script = $"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payloadBase64}')); $d=ConvertFrom-Json -InputObject $p; Set-Location -LiteralPath $d.workingDirectory; $a=@($d.arguments); & $d.executable @a; exit $LASTEXITCODE";
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (desktopInstallation is not null)
        {
            CodexDesktopParentLifetime.ConfigureLaunch(startInfo, desktopInstallation, desktopSupervisorProcessId);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Windows did not start {(desktopInstallation is not null ? "Codex Desktop" : "Codex CLI")}.");
            }

            // The child process has its own copy of the environment block now. Do
            // not keep the plaintext key reachable through Process.StartInfo for
            // the lifetime of the managed session.
            if (apiKeyVariable is not null) startInfo.Environment.Remove(apiKeyVariable);
            return process;
        }
        catch
        {
            if (apiKeyVariable is not null) startInfo.Environment.Remove(apiKeyVariable);
            process.Dispose();
            throw;
        }
    }

    private static string ResolvePowerShellPath()
    {
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            return windowsPowerShell;
        }

        return "pwsh.exe";
    }

    public static string ResolveWorkingDirectory(string? configuredPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(configuredPath.Trim());
        if (!Directory.Exists(candidate))
        {
            throw new InvalidOperationException($"Working directory does not exist: {candidate}");
        }

        return candidate;
    }

    public static IReadOnlyList<string> ParseProfileArguments(string? commandLine)
    {
        var arguments = ParseArguments(commandLine);
        ValidateArguments(arguments);
        return arguments;
    }

    private static List<string> ParseArguments(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var argumentStarted = false;
        for (var i = 0; i < commandLine.Length;)
        {
            if (char.IsWhiteSpace(commandLine[i]) && !inQuotes)
            {
                if (argumentStarted)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    argumentStarted = false;
                }

                i++;
                continue;
            }

            var slashCount = 0;
            while (i < commandLine.Length && commandLine[i] == '\\')
            {
                slashCount++;
                i++;
            }

            if (i < commandLine.Length && commandLine[i] == '"')
            {
                current.Append('\\', slashCount / 2);
                argumentStarted = true;
                if (slashCount % 2 == 0)
                {
                    inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('"');
                }

                i++;
                continue;
            }

            current.Append('\\', slashCount);
            if (i < commandLine.Length)
            {
                current.Append(commandLine[i++]);
                argumentStarted = true;
            }
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("Arguments contain an unmatched double quote.");
        }

        if (argumentStarted)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static void ValidateArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Equals("-m", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--model", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--config", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--profile", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-C", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--cd", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--cd=", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--remote-debugging-address", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--remote-debugging-port", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--api-key", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A launch argument attempts to override the profile's account, model, CODEX_HOME, Electron data directory, or working directory.");
            }
        }
    }
}
