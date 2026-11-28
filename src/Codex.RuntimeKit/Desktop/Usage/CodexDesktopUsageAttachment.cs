using System.Text.Json;
using Codex.RuntimeKit.Desktop.DebugTransport;
using Codex.RuntimeKit.Desktop.Discovery;
using Codex.RuntimeKit.Desktop.Extensions;
using Codex.RuntimeKit.Desktop.Renderer;

namespace Codex.RuntimeKit.Desktop.Usage;

public enum CodexDesktopUsageSurface
{
    Native,
    Fallback,
}

public enum CodexDesktopUsageResult
{
    Presented,
    NoApiRoute,
    AmbiguousAnchors,
    InstallFailed,
    NoPresentation,
    PlacementFailed,
    NativeAmbiguous,
    AssetRejected,
    NativeUnsupported,
    DesktopUnavailable,
    AnchorUnverified,
}

/// <summary>Owns the verified renderer connection and the reversible UI for one managed session.</summary>
public sealed class CodexDesktopUsageAttachment(
    CodexDesktopDebugEndpoint endpoint,
    CodexDesktopCdpSession renderer,
    CodexDesktopInstallation installation,
    CodexDesktopNativeUsagePackage nativePackages,
    bool preferNative) : IAsyncDisposable
{
    private CodexDesktopExtensionHost? _host;
    private string? _extensionId;
    private bool _installed;
    private CodexDesktopNativeUsagePackage.Resolution? _nativeResolution;
    private DateTimeOffset? _nativePlacementDeadline;
    private DateTimeOffset _nextNativeAttempt;

    public CodexDesktopUsageResult? NativeDiagnostic { get; private set; }

    public bool IsNativeActive => _installed && _extensionId == CodexDesktopNativeUsagePackage.Id;

    public CodexDesktopInstallation Installation => installation;

    public bool Matches(CodexDesktopDebugEndpoint activeEndpoint, CodexDesktopRendererTarget activeTarget, bool currentPreference) =>
        endpoint == activeEndpoint && renderer.Target == activeTarget && preferNative == currentPreference;

    public async Task<CodexDesktopUsageResult> PresentAsync(
        Func<CodexDesktopUsageSurface, JsonElement?> getCurrentPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getCurrentPayload);
        var native = await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.NativeUsageLabelSelector,
            cancellationToken).ConfigureAwait(false);
        if (preferNative && native.VisibleCount > 0)
            return native.IsUniqueVisible ? CodexDesktopUsageResult.NoApiRoute : CodexDesktopUsageResult.AmbiguousAnchors;

        if (preferNative && DateTimeOffset.UtcNow >= _nextNativeAttempt)
        {
            _nativeResolution ??= await nativePackages.ResolveAsync(installation, cancellationToken).ConfigureAwait(false);
            if (_nativeResolution.Package is { } package)
            {
                if (_extensionId == CodexDesktopBalanceRendererExtension.Id &&
                    !await RemoveExtensionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return CodexDesktopUsageResult.InstallFailed;
                }

                var failure = await PresentNativeAsync(package, getCurrentPayload, cancellationToken).ConfigureAwait(false);
                if (failure == CodexDesktopUsageResult.Presented)
                {
                    _nativePlacementDeadline = null;
                    NativeDiagnostic = null;
                    return CodexDesktopUsageResult.Presented;
                }
                if (failure == CodexDesktopUsageResult.NoPresentation) return failure;

                var now = DateTimeOffset.UtcNow;
                if (failure == CodexDesktopUsageResult.PlacementFailed)
                {
                    _nativePlacementDeadline ??= now.AddSeconds(12);
                    if (now < _nativePlacementDeadline) return failure;
                }

                NativeDiagnostic = failure;
                _nativePlacementDeadline = null;
                _nextNativeAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            else
            {
                NativeDiagnostic = _nativeResolution.State switch
                {
                    CodexDesktopNativeUsagePackage.State.AmbiguousContract => CodexDesktopUsageResult.NativeAmbiguous,
                    CodexDesktopNativeUsagePackage.State.CorruptSource => CodexDesktopUsageResult.AssetRejected,
                    CodexDesktopNativeUsagePackage.State.UnsupportedContract => CodexDesktopUsageResult.NativeUnsupported,
                    _ => CodexDesktopUsageResult.DesktopUnavailable,
                };
                if (_nativeResolution.State == CodexDesktopNativeUsagePackage.State.Unavailable)
                {
                    _nativeResolution = null;
                    _nextNativeAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
                }
            }
        }

        if (_extensionId == CodexDesktopNativeUsagePackage.Id &&
            !await RemoveExtensionAsync(cancellationToken).ConfigureAwait(false))
        {
            return CodexDesktopUsageResult.InstallFailed;
        }

        var toolbar = !native.IsUniqueVisible
            ? await renderer.ProbeUniqueVisibleAnchorAsync(CodexDesktopBalanceAnchorPolicy.HeaderToolbarSelector,
                cancellationToken).ConfigureAwait(false)
            : null;
        var anchor = CodexDesktopBalanceAnchorPolicy.SelectAnchor(native, toolbar);
        if (anchor is not null)
            return await PresentFallbackAsync(anchor, getCurrentPayload, cancellationToken).ConfigureAwait(false);

        return native.TotalCount > 1 || toolbar?.TotalCount > 1
            ? CodexDesktopUsageResult.AmbiguousAnchors
            : CodexDesktopUsageResult.AnchorUnverified;
    }

    private async Task<CodexDesktopUsageResult> PresentNativeAsync(CodexDesktopExtensionPackage package,
        Func<CodexDesktopUsageSurface, JsonElement?> getCurrentPayload,
        CancellationToken cancellationToken)
    {
        var needsInstall = !_installed;
        if (needsInstall)
        {
            if (!await InstallAsync(package, cancellationToken).ConfigureAwait(false))
                return CodexDesktopUsageResult.InstallFailed;
        }

        if (getCurrentPayload(CodexDesktopUsageSurface.Native) is not { } payload) return CodexDesktopUsageResult.NoPresentation;
        if (!await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false))
            return CodexDesktopUsageResult.InstallFailed;

        // Loading the original asynchronous usage chunk and committing its React root can take a turn.
        for (var attempt = 0; attempt < (needsInstall ? 12 : 3); attempt++)
        {
            // The renderer sets this marker after the native control is visible above the profile.
            var ready = await renderer.ProbeUniqueVisibleAnchorAsync($"[{CodexDesktopNativeUsagePackage.RootAttribute}]",
                cancellationToken).ConfigureAwait(false);
            if (ready.IsUniqueVisible)
            {
                return CodexDesktopUsageResult.Presented;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }

        return CodexDesktopUsageResult.PlacementFailed;
    }

    private async Task<CodexDesktopUsageResult> PresentFallbackAsync(string anchor, Func<CodexDesktopUsageSurface, JsonElement?> getCurrentPayload, CancellationToken cancellationToken)
    {
        if (!_installed)
        {
            var package = CodexDesktopBalanceRendererExtension.CreatePackage();
            if (package is null) return CodexDesktopUsageResult.AssetRejected;
            if (!await InstallAsync(package, cancellationToken).ConfigureAwait(false))
                return CodexDesktopUsageResult.InstallFailed;
        }

        if (getCurrentPayload(CodexDesktopUsageSurface.Fallback) is not { } payload) return CodexDesktopUsageResult.NoPresentation;
        if (!await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false))
            return CodexDesktopUsageResult.InstallFailed;

        return await renderer.IsExtensionAdjacentToAnchorAsync(anchor, CodexDesktopBalanceRendererExtension.RootAttribute, cancellationToken)
            .ConfigureAwait(false) ? CodexDesktopUsageResult.Presented : CodexDesktopUsageResult.PlacementFailed;
    }

    private async Task<bool> InstallAsync(CodexDesktopExtensionPackage package, CancellationToken cancellationToken)
    {
        // Native scripts are generated only from a verified Store package. The host still checks
        // the digest and the renderer on every installation.
        _host = new(new Dictionary<string, string>(StringComparer.Ordinal) { [package.Id] = package.Sha256 });
        _extensionId = package.Id;
        var result = await _host.InstallOrUpdateAsync(package, installation.Version, renderer, cancellationToken).ConfigureAwait(false);
        _installed = result.Succeeded;
        return _installed;
    }

    private async Task<bool> SendPayloadAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var result = await _host!.UpdatePayloadAsync(_extensionId!, payload,
            renderer, cancellationToken).ConfigureAwait(false);
        return result.State == CodexDesktopExtensionResultState.Updated;
    }

    private async Task<bool> RemoveExtensionAsync(CancellationToken cancellationToken)
    {
        if (_extensionId is not { } id) return true;
        var result = await _host!.RemoveAsync(id, renderer, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return false;

        _extensionId = null;
        _host = null;
        _installed = false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_extensionId is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await RemoveExtensionAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A disconnected renderer drops its live presentation when its lease expires.
        }
        finally
        {
            await renderer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
