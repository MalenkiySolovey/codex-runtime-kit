using System.Net;
using System.Text.RegularExpressions;

namespace Codex.RuntimeKit.Desktop.Renderer;

public sealed record CodexDesktopRendererTarget(
    string Id,
    string Type,
    string Url,
    Uri WebSocketDebuggerUrl);

public enum CodexDesktopRendererSelectionState
{
    Selected = 0,
    NotFound = 1,
    Ambiguous = 2,
    InvalidTarget = 3,
}

public sealed record CodexDesktopRendererSelection(
    CodexDesktopRendererSelectionState State,
    CodexDesktopRendererTarget? Target,
    string DiagnosticCode);

public sealed record CodexDesktopRendererSignals(
    bool IsOfficialAppScheme,
    bool HasMainContent,
    bool HasComposer,
    bool HasCodexModeLabel)
{
    public bool IsCodexRenderer =>
        IsOfficialAppScheme && HasMainContent && HasComposer && HasCodexModeLabel;
}

/// <summary>Identifies a single main Codex renderer and rejects auxiliary or ambiguous pages.</summary>
public static class CodexDesktopRendererVerifier
{
    private const string MainRendererUrl = "app://-/index.html";

    public static CodexDesktopRendererSelection SelectMainRenderer(
        IReadOnlyList<CodexDesktopRendererTarget> targets,
        int expectedPort)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (expectedPort is < 1 or > 65535)
        {
            return new(CodexDesktopRendererSelectionState.InvalidTarget, null, "desktop.renderer.invalid-port");
        }

        var candidates = targets.Where(IsMainRenderer).ToArray();
        if (candidates.Length == 0)
        {
            return new(CodexDesktopRendererSelectionState.NotFound, null, "desktop.renderer.not-found");
        }

        if (candidates.Length != 1)
        {
            return new(CodexDesktopRendererSelectionState.Ambiguous, null, "desktop.renderer.ambiguous");
        }

        var target = candidates[0];
        if (!IsSafeTargetSocket(target.WebSocketDebuggerUrl, expectedPort))
        {
            return new(CodexDesktopRendererSelectionState.InvalidTarget, null, "desktop.renderer.socket-invalid");
        }

        return new(CodexDesktopRendererSelectionState.Selected, target, "desktop.renderer.selected");
    }

    public static bool AcceptSemanticSignals(CodexDesktopRendererSignals? signals) =>
        signals?.IsCodexRenderer == true;

    private static bool IsMainRenderer(CodexDesktopRendererTarget target) =>
        string.Equals(target.Type, "page", StringComparison.Ordinal) &&
        string.Equals(target.Url, MainRendererUrl, StringComparison.Ordinal);

    private static bool IsSafeTargetSocket(Uri uri, int expectedPort) =>
        string.Equals(uri.Scheme, "ws", StringComparison.Ordinal) &&
        IPAddress.TryParse(uri.Host, out var address) &&
        IPAddress.IsLoopback(address) &&
        uri.Port == expectedPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 &&
        Regex.IsMatch(uri.AbsolutePath, "^/devtools/page/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
}
