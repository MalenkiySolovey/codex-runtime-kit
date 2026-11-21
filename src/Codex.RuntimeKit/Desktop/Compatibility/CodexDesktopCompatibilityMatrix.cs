namespace Codex.RuntimeKit.Desktop.Compatibility;

public enum CodexDesktopCompatibilityState
{
    Supported = 0,
    Unsupported = 1,
    Unverified = 2,
}

public sealed record CodexDesktopCompatibilityRule(
    Version MinimumVersion,
    Version MaximumVersion,
    string RendererContractId,
    bool RendererContractVerified);

public sealed record CodexDesktopCompatibilityResult(
    CodexDesktopCompatibilityState State,
    string DiagnosticCode);

/// <summary>Explicit build compatibility rules; unknown builds never inherit support from nearby versions.</summary>
public sealed class CodexDesktopCompatibilityMatrix
{
    private readonly IReadOnlyList<CodexDesktopCompatibilityRule> _rules;

    public CodexDesktopCompatibilityMatrix(IEnumerable<CodexDesktopCompatibilityRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        if (_rules.Any(rule => rule.MinimumVersion is null || rule.MaximumVersion is null ||
                               rule.MinimumVersion > rule.MaximumVersion ||
                               string.IsNullOrWhiteSpace(rule.RendererContractId)))
        {
            throw new ArgumentException("Desktop compatibility rules are invalid.", nameof(rules));
        }

        var sorted = _rules.OrderBy(rule => rule.MinimumVersion).ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index - 1].MaximumVersion >= sorted[index].MinimumVersion)
            {
                throw new ArgumentException("Desktop compatibility rules must not overlap.", nameof(rules));
            }
        }
    }

    public CodexDesktopCompatibilityResult Evaluate(Version? version)
    {
        if (version is null)
        {
            return new(CodexDesktopCompatibilityState.Unverified, "desktop.compatibility.version-missing");
        }

        var rule = _rules.SingleOrDefault(item => version >= item.MinimumVersion && version <= item.MaximumVersion);
        if (rule is null)
        {
            return new(CodexDesktopCompatibilityState.Unverified, "desktop.compatibility.version-unverified");
        }

        return rule.RendererContractVerified
            ? new(CodexDesktopCompatibilityState.Supported, "desktop.compatibility.supported")
            : new(CodexDesktopCompatibilityState.Unsupported, "desktop.compatibility.anchor-unverified");
    }
}
