namespace Codex.RuntimeKit.Configuration;

public sealed record CodexRoutingState(
    string? ModelProvider,
    string? Model,
    IReadOnlyDictionary<string, CodexProviderBlock> ManagedProviders,
    string Fingerprint,
    string? ModelCatalogJson = null);

public sealed record CodexProviderBlock(
    string ProviderId,
    string Name,
    string BaseUrl,
    string WireApi,
    string BrokerCommand,
    IReadOnlyList<string> BrokerArgs,
    int TimeoutMs = 5000,
    ulong? RequestMaxRetries = null,
    ulong? StreamMaxRetries = null,
    ulong? StreamIdleTimeoutMs = null,
    ulong? WebSocketConnectTimeoutMs = null,
    bool? SupportsWebSockets = null,
    bool? SupportsStandaloneWebSearch = null,
    IReadOnlyDictionary<string, string>? QueryParams = null,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    IReadOnlyDictionary<string, string>? EnvHttpHeaders = null,
    CodexConfigToolOptions? ToolPolicy = null);

public sealed record CodexConfigToolOptions(bool AllowHostedWebSearch, bool AllowMultiAgent);

public class ConcurrentModificationException : InvalidOperationException
{
    public ConcurrentModificationException(string message) : base(message) { }
}

/// <summary>
/// Semantic owner of Codex routing configuration in config.toml.
/// Provides narrow, lossless mutations for root model_provider, model, and managed provider tables.
/// Protects against concurrent modifications and preserves all unrelated configuration tables/comments.
/// </summary>
public interface ICodexRoutingConfigStore
{
    string ComputeFingerprint(string configTomlPath);

    CodexRoutingState ReadRoutingState(string configTomlPath);

    string ApplyManagedRouting(
        string configTomlPath,
        CodexProviderBlock providerBlock,
        string model,
        CodexModelOverrides? modelOverrides = null,
        string? modelCatalogJson = null,
        string? expectedFingerprint = null);

    string UpdateProviderRoute(
        string configTomlPath,
        string providerId,
        string newBaseUrl,
        string? expectedFingerprint = null);

    string ReturnToOpenAi(
        string configTomlPath,
        string? model = null,
        string? expectedFingerprint = null);

    void RestoreExactBytes(string configTomlPath, byte[] exactBytes);

    void CleanOrphanProviderBlocks(string configTomlPath, IReadOnlySet<string> knownProviderIds);

    void ReconcileAndRepairContaminatedConfig(string configTomlPath, IReadOnlySet<string> knownProviderIds);
}
