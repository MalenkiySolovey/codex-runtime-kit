namespace Codex.RuntimeKit.Configuration;

public sealed record CodexIsolatedProviderRoute(
    string Id, string Name, string BaseUrl, string? WireApi, string ApiKeyEnvironmentVariable,
    bool? AllowHostedWebSearch = null, bool AllowStandaloneWebSearch = false,
    ulong? RequestMaxRetries = null, ulong? StreamMaxRetries = null,
    ulong? StreamIdleTimeoutMs = null, ulong? WebSocketConnectTimeoutMs = null,
    bool? SupportsWebSockets = null, bool? SupportsStandaloneWebSearch = null,
    IReadOnlyDictionary<string, string>? QueryParams = null,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    IReadOnlyDictionary<string, string>? EnvHttpHeaders = null);

/// <summary>Configuration rendered into a caller-owned isolated Codex home.</summary>
public sealed record CodexIsolatedConfig(
    string? Model,
    CodexIsolatedProviderRoute? Provider = null,
    CodexModelOverrides? ModelOverrides = null,
    string? CatalogPath = null,
    string? SharedSqliteHome = null)
{
    public static string Render(CodexIsolatedConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (model, provider, modelOverrides, catalogPath, sharedSqliteHome) = options;
        var lines = new List<string> { "cli_auth_credentials_store = \"file\"" };
        if (!string.IsNullOrWhiteSpace(sharedSqliteHome))
        {
            lines.Add($"sqlite_home = {TomlString(sharedSqliteHome)}");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            lines.Add($"model = {TomlString(model)}");
        }

        if (provider is not null)
        {
            lines.Add($"model_provider = {TomlString(provider.Id)}");
            if (!string.IsNullOrWhiteSpace(catalogPath))
            {
                lines.Add($"model_catalog_json = {TomlString(catalogPath)}");
            }

            if (modelOverrides?.ContextWindowTokens is { } contextWindow)
            {
                lines.Add($"model_context_window = {contextWindow.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (modelOverrides?.AutoCompactTokenLimit is { } compactLimit)
            {
                lines.Add($"model_auto_compact_token_limit = {compactLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (modelOverrides?.AutoCompactTokenLimitScope is { } compactScope && compactScope != CompactLimitScope.Default)
            {
                var scope = compactScope == CompactLimitScope.BodyAfterPrefix ? "body_after_prefix" : "total";
                lines.Add($"model_auto_compact_token_limit_scope = {TomlString(scope)}");
            }
            if (modelOverrides?.ReasoningEffort is { } selectedEffort && selectedEffort != CodexReasoningEffort.Default)
            {
                lines.Add($"model_reasoning_effort = {TomlString(selectedEffort.ToString().ToLowerInvariant())}");
            }
            if (modelOverrides?.ReasoningSummary is { } selectedSummary && selectedSummary != CodexReasoningSummary.Default)
            {
                lines.Add($"model_reasoning_summary = {TomlString(selectedSummary.ToString().ToLowerInvariant())}");
            }
            if (modelOverrides?.Verbosity is { } selectedVerbosity && selectedVerbosity != CodexVerbosity.Default)
            {
                lines.Add($"model_verbosity = {TomlString(selectedVerbosity.ToString().ToLowerInvariant())}");
            }
            if (modelOverrides?.ToolOutputTokenLimit is { } outputLimit)
            {
                lines.Add($"tool_output_token_limit = {outputLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (provider.AllowHostedWebSearch == false)
            {
                lines.Add("web_search = \"disabled\"");
            }

            lines.Add(string.Empty);
            lines.Add($"[model_providers.{provider.Id}]");
            lines.Add($"name = {TomlString(provider.Name)}");
            lines.Add($"base_url = {TomlString(provider.BaseUrl)}");
            lines.Add($"wire_api = {TomlString(string.IsNullOrWhiteSpace(provider.WireApi) ? "responses" : provider.WireApi)}");
            lines.Add($"env_key = {TomlString(provider.ApiKeyEnvironmentVariable)}");

            if (provider.RequestMaxRetries is { } requestRetries)
            {
                lines.Add($"request_max_retries = {requestRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (provider.StreamMaxRetries is { } streamRetries)
            {
                lines.Add($"stream_max_retries = {streamRetries.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (provider.StreamIdleTimeoutMs is { } idleTimeout)
            {
                lines.Add($"stream_idle_timeout_ms = {idleTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (provider.WebSocketConnectTimeoutMs is { } websocketTimeout)
            {
                lines.Add($"websocket_connect_timeout_ms = {websocketTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (provider.SupportsWebSockets is { } supportsWebSockets)
            {
                lines.Add($"supports_websockets = {supportsWebSockets.ToString().ToLowerInvariant()}");
            }
            if (provider.AllowStandaloneWebSearch && provider.SupportsStandaloneWebSearch == true)
            {
                lines.Add("supports_standalone_web_search = true");
            }

            AppendTomlMap(lines, $"model_providers.{provider.Id}.query_params", provider.QueryParams);
            AppendTomlMap(lines, $"model_providers.{provider.Id}.http_headers", provider.HttpHeaders);
            AppendTomlMap(lines, $"model_providers.{provider.Id}.env_http_headers", provider.EnvHttpHeaders);
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string TomlString(string value)
        => $"\"{CodexRoutingConfigStore.TomlEscape(value)}\"";

    private static void AppendTomlMap(
        List<string> lines,
        string tableName,
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is not { Count: > 0 })
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"[{tableName}]");
        foreach (var (key, value) in values)
        {
            lines.Add($"{TomlString(key)} = {TomlString(value)}");
        }
    }
}
