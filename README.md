# Codex Runtime Kit

`Codex.RuntimeKit` is a small .NET library for discovering local Codex CLI executables, probing runtime capabilities, and connecting to the Codex app-server over stdio.

- Target framework: `net10.0`
- Package ID: `Codex.RuntimeKit`
- Initial package version: `0.1.0-preview.1`
- Dependencies: none in the library package

The library owns runtime discovery and the app-server transport. The host keeps account selection, credential storage, routing policy, application settings, UI, and application process registries.

## Runtime discovery

```csharp
using Codex.RuntimeKit.Discovery;

var resolver = new CodexRuntimeResolver();
var runtime = resolver.ResolveCurrentRuntime(overridePath: null);

if (!runtime.IsAvailable)
{
    throw new InvalidOperationException("No Codex executable was found.");
}

Console.WriteLine($"{runtime.ExecutablePath} ({runtime.Version})");
```

Discovery checks an existing explicit override first, then versioned installer directories, the well-known installer path, and PATH. Capability results are cached by executable file identity. `RuntimeDiscoveryOptions` can override paths, executable names, probe timeouts, and executable argument prefixes for wrappers and synthetic tests.

## App-server transport

```csharp
using Codex.RuntimeKit.AppServer;

await using var client = new CodexAppServerClient(new AppServerClientOptions
{
    ExecutablePath = runtime.ExecutablePath,
    CodexHome = isolatedCodexHome,
    ClientInfo = new AppServerClientInfo("ExampleHost", "Example Host", "1.0.0"),
});

client.EventReceived += (_, eventArgs) =>
    Console.WriteLine($"Received app-server event: {eventArgs.Method}");

await client.StartAsync();
var account = await client.RequestAsync("account/read", new { refreshToken = false });
await client.StopAsync();
```

The transport starts only the executable supplied by the caller, performs the `initialize` → `initialized` handshake, and reads newline-delimited JSON messages. It does not add a `jsonrpc` envelope. Requests are correlated by ID; notifications and server-initiated messages are exposed through `EventReceived`. Callers can provide an `IAppServerProcessLifecycleObserver` to connect process start and exit to host-owned lifecycle tracking.

The client sets the child process `CODEX_HOME` to the supplied directory and removes inherited `OPENAI_API_KEY` and `CODEX_API_KEY` variables. It does not read, create, or select user accounts. Probe processes use a temporary `CODEX_HOME`, remove inherited credential variables, and use bounded timeouts and output capture.

See the [Codex app-server protocol documentation](https://developers.openai.com/codex/app-server/) for the current wire protocol.

## Building and testing

The repository pins .NET SDK `10.0.404` in `global.json`.

```powershell
dotnet restore Codex.RuntimeKit.slnx
dotnet test Codex.RuntimeKit.slnx --configuration Release
pwsh -NoProfile -File eng/quality/verify.ps1
```

The test server is synthetic. Tests do not require a signed-in Codex session or an installed user runtime.
