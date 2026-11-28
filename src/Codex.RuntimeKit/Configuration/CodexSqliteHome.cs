using System.Text.RegularExpressions;
using Codex.RuntimeKit.Lifecycle;

namespace Codex.RuntimeKit.Configuration;

/// <summary>Resolves Codex SQLite storage with configuration, environment, and home precedence.</summary>
public static class CodexSqliteHome
{
    public const string EnvironmentVariable = "CODEX_SQLITE_HOME";
    private static readonly Regex TomlStringAssignmentPattern = new(
        @"^(?<key>[A-Za-z0-9_-]+)\s*=\s*""(?<value>(?:\\.|[^""])*)""\s*(?:#.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TomlProfileSectionPattern = new(
        @"^\[profiles\.(?:""(?<quoted>(?:\\.|[^""])*)""|(?<bare>[^.\]]+))\]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Resolve(string codexHome, string? configToml, string? environmentOverride)
    {
        var selectedPath = ReadConfiguredSqliteHome(configToml ?? string.Empty) ?? environmentOverride ?? codexHome;
        if (string.IsNullOrWhiteSpace(selectedPath) || !Path.IsPathFullyQualified(selectedPath))
            throw new InvalidOperationException("The Codex SQLite home is not an absolute path.");

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
    }

    private static string? ReadConfiguredSqliteHome(string configToml)
    {
        var rootProfile = (string?)null;
        var rootSqliteHome = (string?)null;
        var profileSqliteHomes = new Dictionary<string, string>(StringComparer.Ordinal);
        var atRoot = true;
        string? currentProfile = null;
        foreach (var rawLine in configToml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                atRoot = false;
                currentProfile = TryParseProfileTableName(line);
                continue;
            }

            if (atRoot)
            {
                if (TryReadTomlString(line, "profile", out var profileName))
                {
                    rootProfile = profileName;
                }
                else if (TryReadTomlString(line, "sqlite_home", out var sqliteHome))
                {
                    rootSqliteHome = sqliteHome;
                }
            }
            else if (currentProfile is not null && TryReadTomlString(line, "sqlite_home", out var profileHome))
            {
                profileSqliteHomes[currentProfile] = profileHome;
            }
        }

        if (rootProfile is not null && profileSqliteHomes.TryGetValue(rootProfile, out var activeProfileSqliteHome))
        {
            return activeProfileSqliteHome;
        }

        return rootSqliteHome;
    }

    private static string? TryParseProfileTableName(string line)
    {
        var match = TomlProfileSectionPattern.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["quoted"].Success
            ? CodexRoutingConfigStore.TomlUnescape(match.Groups["quoted"].Value)
            : match.Groups["bare"].Value.Trim();
    }

    private static bool TryReadTomlString(string line, string key, out string value)
    {
        var match = TomlStringAssignmentPattern.Match(line);
        if (match.Success && string.Equals(match.Groups["key"].Value, key, StringComparison.Ordinal))
        {
            value = CodexRoutingConfigStore.TomlUnescape(match.Groups["value"].Value);
            return true;
        }

        value = string.Empty;
        return false;
    }
}
