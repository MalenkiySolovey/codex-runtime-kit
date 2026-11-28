using Codex.RuntimeKit.Configuration;
using Codex.RuntimeKit.Tests.TestSupport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Unit test fixture data.")]
public sealed class CodexRoutingConfigStoreTests
{
    private readonly TestConfigFileSystem _fs = new();

    private const string ComplexToml = @"# Top-level configuration for Codex
cli_auth_credentials_store = ""file""
model_provider = ""openai""
model = ""gpt-5.3-codex""
temperature = 0.2
unmanaged_root_key = ""should_be_preserved_123""

# Features table
[features]
enable_telemetry = false
fast_mode = true

# MCP servers configuration
[mcp_servers.filesystem]
command = ""npx""
args = [""-y"", ""@modelcontextprotocol/server-filesystem"", ""C:\\workspace""]
env = { DEBUG = ""true"", LOG_LEVEL = ""info"" }

# User-defined model provider
[model_providers.custom_company]
name = ""Internal Company LLM""
base_url = ""https://llm.internal.corp/v1""
wire_api = ""responses""

[model_providers.custom_company.auth]
command = ""cmd.exe""
args = [""/c"", ""echo token""]

# Quoted table name
[""profiles"".""developer-mode""]
instructions = """"
You are a senior engineer.
Preserve all formatting and comments.
""""
";

    [Fact]
    public void ReadRoutingState_OnComplexToml_ExtractsRootKeysAndFingerprint()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var state = store.ReadRoutingState(configPath);

        Assert.Equal("openai", state.ModelProvider);
        Assert.Equal("gpt-5.3-codex", state.Model);
        Assert.NotEmpty(state.Fingerprint);
        Assert.Empty(state.ManagedProviders);
    }

    [Fact]
    public void ApplyManagedRouting_PreservesAllUnrelatedTablesAndComments()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "test_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:\Example\broker.exe",
            new[] { "--key-id", "d047e1ff-3e5f-4a0b-96ce-906cbdf09f3e" },
            5000);

        var newFp = store.ApplyManagedRouting(configPath, provider, "gpt-5.6-sol");
        Assert.NotEmpty(newFp);

        var content = File.ReadAllText(configPath);

        // Check new routing
        var state = store.ReadRoutingState(configPath);
        Assert.Equal("test_112233445566", state.ModelProvider);
        Assert.Equal("gpt-5.6-sol", state.Model);
        Assert.True(state.ManagedProviders.ContainsKey("test_112233445566"));

        // Check preservation
        Assert.Contains("# Top-level configuration", content);
        Assert.Contains(@"unmanaged_root_key = ""should_be_preserved_123""", content);
        Assert.Contains("# MCP servers configuration", content);
        Assert.Contains(@"[model_providers.custom_company]", content);
        Assert.Contains(@"[""profiles"".""developer-mode""]", content);
        Assert.Contains("Preserve all formatting and comments.", content);
    }

    [Fact]
    public void Routing_config_backups_keep_only_ten_exact_generated_generations()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, ComplexToml);
        Directory.CreateDirectory(paths.BackupsDirectory);
        var sentinel = Path.Combine(paths.BackupsDirectory, "config.toml.keep.bak");
        File.WriteAllText(sentinel, "unrelated recovery data");

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "test_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            "key-broker.exe",
            new[] { "--key-id", "profile-key" },
            5_000);

        for (var index = 0; index < 12; index++)
        {
            store.ApplyManagedRouting(configPath, provider, $"model-{index}");
            Thread.Sleep(2);
        }

        var generatedBackups = Directory.GetFiles(paths.BackupsDirectory, "config.toml.*.bak")
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(path), @"^config\.toml\.\d{17}\.bak$"))
            .ToArray();

        Assert.Equal(10, generatedBackups.Length);
        Assert.Equal("unrelated recovery data", File.ReadAllText(sentinel));
    }

    [Fact]
    public void ConcurrentModification_ThrowsConcurrentModificationException()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "test_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:roker.exe",
            new[] { "--key-id", "guid" },
            5000);

        // Pass an outdated expected fingerprint
        Assert.Throws<ConcurrentModificationException>(() =>
            store.ApplyManagedRouting(configPath, provider, "gpt-5.6-sol", expectedFingerprint: "stale_hash_123"));
    }

    [Fact]
    public void UpdateProviderRoute_ChangesBaseUrlOnly()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "test_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:roker.exe",
            new[] { "--key-id", "guid" },
            5000);

        store.ApplyManagedRouting(configPath, provider, "gpt-5.6-sol");

        store.UpdateProviderRoute(configPath, "test_112233445566", "https://direct.router-cheap.com/v1");

        var state = store.ReadRoutingState(configPath);
        Assert.Equal("https://direct.router-cheap.com/v1", state.ManagedProviders["test_112233445566"].BaseUrl);
    }

    [Fact]
    public void ReturnToOpenAi_SetsModelProviderToOpenAi_PreservesProviderBlocks()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "test_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:roker.exe",
            new[] { "--key-id", "guid" },
            5000);

        store.ApplyManagedRouting(configPath, provider, "gpt-5.6-sol");
        store.ReturnToOpenAi(configPath);

        var state = store.ReadRoutingState(configPath);
        Assert.Equal("openai", state.ModelProvider);
        // Preferred hygiene: Zero switchboard provider blocks when ChatGPT is active
        Assert.Empty(state.ManagedProviders);
    }

    [Fact]
    public void ApplyManagedRouting_WithTwoManagedProviders_RetainsOnlyActiveBlock()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");
        File.WriteAllText(configPath, "# Config\nmodel_provider = \"openai\"\n[model_providers.openrouter]\nname = \"OpenRouter\"\nbase_url = \"https://openrouter.ai/api/v1\"\n");

        var store = new CodexRoutingConfigStore(_fs, paths);
        var providerA = new CodexProviderBlock(
            "test_aaaa11112222", "Provider A", "https://a.example/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-a" });
        var providerB = new CodexProviderBlock(
            "test_bbbb33334444", "Provider B", "https://b.example/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-b" });

        store.ApplyManagedRouting(configPath, providerA, "model-a");
        store.ApplyManagedRouting(configPath, providerB, "model-b");

        var state = store.ReadRoutingState(configPath);
        Assert.Equal("test_bbbb33334444", state.ModelProvider);
        Assert.Equal("model-b", state.Model);
        // Exactly ONE switchboard provider block remains
        Assert.Single(state.ManagedProviders);
        Assert.True(state.ManagedProviders.ContainsKey("test_bbbb33334444"));
        Assert.False(state.ManagedProviders.ContainsKey("test_aaaa11112222"));

        // User provider block openrouter is untouched
        var rawToml = File.ReadAllText(configPath);
        Assert.Contains("[model_providers.openrouter]", rawToml);
    }

    [Fact]
    public void ApplyManagedRouting_OnMissingConfig_CreatesValidToml()
    {
        using var temp = new TemporaryDirectory();
        var paths = new CodexRoutingConfigOptions(
            Path.Combine(temp.Path, "routing-state.json"), Path.Combine(temp.Path, "backups"),
            static id => id.StartsWith("test_", StringComparison.Ordinal));
        var configPath = Path.Combine(temp.Path, "config.toml");

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "test_12345678abcd", "Provider Fresh", "https://fresh.example/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-fresh" });

        store.ApplyManagedRouting(configPath, provider, "model-fresh");

        Assert.True(File.Exists(configPath));
        var state = store.ReadRoutingState(configPath);
        Assert.Equal("test_12345678abcd", state.ModelProvider);
        Assert.Equal("model-fresh", state.Model);
    }
}
