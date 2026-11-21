using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.RuntimeKit.Desktop.DebugTransport;

namespace Codex.RuntimeKit.Desktop.Extensions;

internal interface ICodexDesktopExtensionChannel
{
    bool IsVerifiedCodexRenderer { get; }
    Task<string?> AddDocumentScriptAsync(string script, CancellationToken cancellationToken);
    Task<bool> EvaluateExtensionAsync(string expression, CancellationToken cancellationToken);
    Task<bool> RemoveDocumentScriptAsync(string identifier, CancellationToken cancellationToken);
}

public sealed record CodexDesktopExtensionPackage(
    string Id,
    Version Version,
    Version MinimumDesktopVersion,
    Version MaximumDesktopVersion,
    string Sha256,
    string JavaScript);

public enum CodexDesktopExtensionResultState
{
    Installed = 0,
    Updated = 1,
    Removed = 2,
    NotInstalled = 3,
    Rejected = 4,
    Failed = 5,
}

public sealed record CodexDesktopExtensionResult(
    CodexDesktopExtensionResultState State,
    string DiagnosticCode)
{
    public bool Succeeded => State is CodexDesktopExtensionResultState.Installed
        or CodexDesktopExtensionResultState.Updated
        or CodexDesktopExtensionResultState.Removed
        or CodexDesktopExtensionResultState.NotInstalled;
}

/// <summary>
/// A narrow host for bundled, hash-allowlisted renderer extensions. It does not
/// load from provider catalogs, disk paths, or arbitrary user-supplied sources.
/// </summary>
public sealed class CodexDesktopExtensionHost
{
    public const int MaximumScriptBytes = 64 * 1024;
    public const int MaximumPayloadBytes = 32 * 1024;

    private readonly IReadOnlyDictionary<string, string> _approvedHashes;
    private readonly Dictionary<string, InstalledScript> _installed = new(StringComparer.Ordinal);

    public CodexDesktopExtensionHost(IReadOnlyDictionary<string, string> approvedHashes)
    {
        ArgumentNullException.ThrowIfNull(approvedHashes);
        _approvedHashes = approvedHashes.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    }

    public async Task<CodexDesktopExtensionResult> InstallOrUpdateAsync(
        CodexDesktopExtensionPackage package,
        Version desktopVersion,
        CodexDesktopCdpSession channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return await InstallOrUpdateAsync(package, desktopVersion, channel.ExtensionChannel, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CodexDesktopExtensionResult> InstallOrUpdateAsync(
        CodexDesktopExtensionPackage package,
        Version desktopVersion,
        ICodexDesktopExtensionChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(desktopVersion);
        ArgumentNullException.ThrowIfNull(channel);

        var validation = Validate(package, desktopVersion, channel);
        if (validation is not null)
        {
            return validation;
        }

        var installExpression = CreateInstallExpression(package);
        var documentScript = CreateDocumentScript(package, initialPayloadJson: null);
        string? documentScriptId = null;
        try
        {
            documentScriptId = await channel.AddDocumentScriptAsync(documentScript, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(documentScriptId))
            {
                return new(CodexDesktopExtensionResultState.Failed, "desktop.extension.document-script-registration-failed");
            }

            var applied = await channel.EvaluateExtensionAsync(installExpression, cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                await channel.RemoveDocumentScriptAsync(documentScriptId, cancellationToken).ConfigureAwait(false);
                return new(CodexDesktopExtensionResultState.Failed, "desktop.extension.renderer-install-failed");
            }

            var existed = _installed.TryGetValue(package.Id, out var previous);
            if (existed && previous is not null)
            {
                if (!ReferenceEquals(previous.Channel, channel))
                {
                    await TryRemoveExtensionAsync(previous.Channel, package.Id).ConfigureAwait(false);
                }

                await TryRemoveDocumentScriptsAsync(previous.Channel, previous.DocumentScriptIds).ConfigureAwait(false);
            }

            _installed[package.Id] = new InstalledScript(package, [documentScriptId], channel);

            return new(
                existed ? CodexDesktopExtensionResultState.Updated : CodexDesktopExtensionResultState.Installed,
                existed ? "desktop.extension.updated" : "desktop.extension.installed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(documentScriptId))
            {
                await TryRemoveDocumentScriptsAsync(channel, [documentScriptId]).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception)
        {
            if (!string.IsNullOrWhiteSpace(documentScriptId))
            {
                await TryRemoveDocumentScriptsAsync(channel, [documentScriptId]).ConfigureAwait(false);
            }

            return new(CodexDesktopExtensionResultState.Failed, "desktop.extension.operation-failed");
        }
    }

    public async Task<CodexDesktopExtensionResult> RemoveAsync(
        string extensionId,
        CodexDesktopCdpSession channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return await RemoveAsync(extensionId, channel.ExtensionChannel, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CodexDesktopExtensionResult> RemoveAsync(
        string extensionId,
        ICodexDesktopExtensionChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsValidId(extensionId))
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.id-invalid");
        }

        if (!channel.IsVerifiedCodexRenderer)
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.renderer-unverified");
        }

        var removed = await channel.EvaluateExtensionAsync(CreateRemoveExpression(extensionId), cancellationToken).ConfigureAwait(false);
        var hadRegisteredScript = false;
        if (_installed.Remove(extensionId, out var installed))
        {
            hadRegisteredScript = await TryRemoveDocumentScriptsAsync(
                installed.Channel,
                installed.DocumentScriptIds,
                cancellationToken).ConfigureAwait(false);
        }

        return removed || hadRegisteredScript
            ? new(CodexDesktopExtensionResultState.Removed, "desktop.extension.removed")
            : new(CodexDesktopExtensionResultState.NotInstalled, "desktop.extension.not-installed");
    }

    public Task<CodexDesktopExtensionResult> UpdatePayloadAsync(
        string extensionId,
        JsonElement payload,
        CodexDesktopCdpSession channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return UpdatePayloadAsync(extensionId, payload, channel.ExtensionChannel, cancellationToken);
    }

    internal async Task<CodexDesktopExtensionResult> UpdatePayloadAsync(
        string extensionId,
        JsonElement payload,
        ICodexDesktopExtensionChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsValidId(extensionId))
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.id-invalid");
        }

        if (!channel.IsVerifiedCodexRenderer)
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.renderer-unverified");
        }

        if (!_installed.TryGetValue(extensionId, out var installed) || !ReferenceEquals(installed.Channel, channel))
        {
            return new(CodexDesktopExtensionResultState.NotInstalled, "desktop.extension.not-installed");
        }

        var serializedPayload = JsonSerializer.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(serializedPayload) > MaximumPayloadBytes)
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.payload-too-large");
        }

        var expression = $"(() => {{ const update = window.__CODEX_RUNTIME_KIT_EXTENSIONS__?.[{JsonSerializer.Serialize(extensionId)}]?.update; if (typeof update !== 'function') return false; update({serializedPayload}); return true; }})()";
        var updated = await channel.EvaluateExtensionAsync(expression, cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            return new(CodexDesktopExtensionResultState.Failed, "desktop.extension.payload-update-failed");
        }

        string? documentScriptId;
        try
        {
            documentScriptId = await channel.AddDocumentScriptAsync(
                CreateDocumentScript(installed.Package, serializedPayload),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new(CodexDesktopExtensionResultState.Failed, "desktop.extension.reload-payload-registration-failed");
        }

        if (string.IsNullOrWhiteSpace(documentScriptId))
        {
            return new(CodexDesktopExtensionResultState.Failed, "desktop.extension.reload-payload-registration-failed");
        }

        var documentScriptIds = installed.DocumentScriptIds.ToList();
        documentScriptIds.Add(documentScriptId);
        foreach (var oldId in installed.DocumentScriptIds)
        {
            try
            {
                if (await channel.RemoveDocumentScriptAsync(oldId, cancellationToken).ConfigureAwait(false))
                {
                    documentScriptIds.Remove(oldId);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Keep the identifier so clean shutdown can retry; the current renderer has the new payload.
            }
        }

        _installed[extensionId] = installed with
        {
            DocumentScriptIds = documentScriptIds,
        };
        return new(CodexDesktopExtensionResultState.Updated, "desktop.extension.payload-updated");
    }

    public static string ComputeSha256(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    private CodexDesktopExtensionResult? Validate(
        CodexDesktopExtensionPackage package,
        Version desktopVersion,
        ICodexDesktopExtensionChannel channel)
    {
        if (!IsValidId(package.Id))
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.id-invalid");
        }

        if (!channel.IsVerifiedCodexRenderer)
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.renderer-unverified");
        }

        if (package.Version is null || package.MinimumDesktopVersion is null || package.MaximumDesktopVersion is null ||
            package.Version.Major < 0 || package.MinimumDesktopVersion > package.MaximumDesktopVersion ||
            desktopVersion < package.MinimumDesktopVersion || desktopVersion > package.MaximumDesktopVersion)
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.version-unsupported");
        }

        if (string.IsNullOrEmpty(package.JavaScript) ||
            Encoding.UTF8.GetByteCount(package.JavaScript) > MaximumScriptBytes ||
            !Regex.IsMatch(package.Sha256, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.package-invalid");
        }

        var actualHash = ComputeSha256(package.JavaScript);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(package.Sha256)))
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.hash-mismatch");
        }

        if (!_approvedHashes.TryGetValue(package.Id, out var approvedHash) ||
            !string.Equals(approvedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            return new(CodexDesktopExtensionResultState.Rejected, "desktop.extension.not-allowlisted");
        }

        return null;
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        Regex.IsMatch(id, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private static string CreateInstallExpression(CodexDesktopExtensionPackage package)
    {
        var id = JsonSerializer.Serialize(package.Id);
        var version = JsonSerializer.Serialize(package.Version.ToString());
        return $"(() => {{ if (location.href !== 'app://-/index.html') return false; const g = window.__CODEX_RUNTIME_KIT_EXTENSIONS__ ||= Object.create(null); const id = {id}; const version = {version}; const prior = g[id]; if (prior?.version === version) return true; prior?.cleanup?.(); const instance = ({package.JavaScript}); const cleanup = typeof instance === 'function' ? instance : instance?.cleanup; if (typeof cleanup !== 'function') return false; g[id] = {{ version, cleanup, update: typeof instance?.update === 'function' ? instance.update : null }}; return true; }})()";
    }

    private static string CreateDocumentScript(CodexDesktopExtensionPackage package, string? initialPayloadJson)
    {
        var initialPayload = initialPayloadJson is null
            ? "null"
            : $"JSON.parse({JsonSerializer.Serialize(initialPayloadJson)})";
        return $$"""
            (() => {
              const id = {{JsonSerializer.Serialize(package.Id)}};
              const version = {{JsonSerializer.Serialize(package.Version.ToString())}};
              const initialPayload = {{initialPayload}};
              let attempts = 0;
              const run = () => {
                if (location.href !== 'app://-/index.html') return;
                const controls = [...document.querySelectorAll('button,[role=tab],[role=combobox]')].slice(0, 256);
                const codex = controls.some(e => /codex/i.test([e.getAttribute('aria-label'),e.getAttribute('title'),e.getAttribute('data-testid')].filter(Boolean).join(' ')));
                const ready = !!document.querySelector('main') && !!document.querySelector('[data-testid*=composer],[contenteditable=true],textarea') && codex;
                if (!ready) {
                  if (attempts++ < 100) setTimeout(run, 100);
                  return;
                }
                const g = window.__CODEX_RUNTIME_KIT_EXTENSIONS__ ||= Object.create(null);
                if (g[id]?.version === version) {
                  if (initialPayload !== null) g[id].update?.(initialPayload);
                  return;
                }
                g[id]?.cleanup?.();
                const instance = ({{package.JavaScript}});
                const cleanup = typeof instance === 'function' ? instance : instance?.cleanup;
                if (typeof cleanup !== 'function') return;
                g[id] = { version, cleanup, update: typeof instance?.update === 'function' ? instance.update : null };
                if (initialPayload !== null) g[id].update?.(initialPayload);
              };
              if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', run, { once: true });
              else run();
            })()
            """;
    }

    private static string CreateRemoveExpression(string extensionId) =>
        $"(() => {{ const g = window.__CODEX_RUNTIME_KIT_EXTENSIONS__; const id = {JsonSerializer.Serialize(extensionId)}; const value = g?.[id]; if (!value) return false; try {{ value.cleanup?.(); }} finally {{ delete g[id]; }} return true; }})()";

    private static async Task<bool> TryRemoveDocumentScriptsAsync(
        ICodexDesktopExtensionChannel channel,
        IReadOnlyList<string> identifiers,
        CancellationToken cancellationToken = default)
    {
        var removedAny = false;
        foreach (var identifier in identifiers)
        {
            try
            {
                removedAny |= await channel.RemoveDocumentScriptAsync(identifier, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A closed renderer drops its document scripts with the target.
            }
        }

        return removedAny;
    }

    private static async Task TryRemoveExtensionAsync(ICodexDesktopExtensionChannel channel, string extensionId)
    {
        try
        {
            await channel.EvaluateExtensionAsync(CreateRemoveExpression(extensionId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A closed renderer is already detached from the extension lifecycle.
        }
    }

    private sealed record InstalledScript(
        CodexDesktopExtensionPackage Package,
        IReadOnlyList<string> DocumentScriptIds,
        ICodexDesktopExtensionChannel Channel);
}
