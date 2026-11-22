# Codex Runtime Kit

`Codex.RuntimeKit` is a small .NET library for discovering local Codex CLI executables, probing runtime capabilities, connecting to the Codex app-server over stdio, and managing a verified Codex Desktop renderer extension session.

- Target framework: `net10.0`
- Package ID: `Codex.RuntimeKit`
- Package version: `0.1.0-preview.3`
- Dependencies: none in the library package

The library owns runtime discovery, the app-server transport, and Desktop app control. The host keeps account selection, credential storage, routing policy, application settings, UI, and application process registries.

## Codex Desktop discovery and compatibility

`CodexDesktopDiscovery` scans only explicitly configured MSIX package identities. It verifies the package name, publisher, application ID, manifest version, and that the executable resolves inside the package root. The default identity matches the official `OpenAI.Codex` MSIX package family. Discovery returns a short diagnostic code instead of exposing local paths in errors.

```csharp
using Codex.RuntimeKit.Desktop.Compatibility;
using Codex.RuntimeKit.Desktop.Discovery;

var discovered = new CodexDesktopDiscovery().Discover();
if (!discovered.IsAvailable)
{
    throw new InvalidOperationException(discovered.DiagnosticCode);
}

var compatibility = new CodexDesktopCompatibilityMatrix(verifiedBuildRules);
var support = compatibility.Evaluate(discovered.Installation!.Version);
```

Compatibility rules are explicit version ranges with a renderer-contract identifier. An unlisted build is `Unverified`; a rule with an unverified renderer contract is `Unsupported`. Nearby versions do not inherit support.

## Managed lifecycle and CDP

`CodexDesktopManagedLauncher` requires explicit consent, revalidates the MSIX identity, and refuses to start when it cannot determine whether an official app process already exists. By default it also refuses when one is running. A separate isolated instance is possible only when the caller explicitly sets `AllowConcurrentIsolatedInstance`; the existing instance is never redirected, closed, or reconfigured. Each managed launch uses a new per-run renderer profile and a distinct per-run `CODEX_HOME`, loopback-only CDP, and an ephemeral port. Set `ManagedProfileRoot` when the profile must live on a chosen volume; both data paths are placed beneath that root. It strips inherited `OPENAI_API_KEY` and `CODEX_API_KEY` variables. The returned session can request a graceful window close; RuntimeKit never force-terminates a process.

`CodexDesktopParentLifetime` clears inherited development lifetime settings from managed launch environments. On the verified Windows build `26.1117.4957.0`, a caller can explicitly bind a new launch to an owned supervisor process. The installed app uses its development parent-liveness option to request a normal Quit when that process exits. The caller owns the supervisor and must observe the actual Desktop process exit.

On Windows, discovery first reads the current user's MSIX package registration, then validates the package manifest identity, publisher, version, and executable path. It falls back to package-directory scanning when no current-user registration is available. If the MSIX entrypoint is a short-lived launcher, managed startup identifies the actual process from the loopback DevTools listener in that run's unique profile and verifies that process's executable image before returning a session.

`CodexDesktopDebugEndpoint.TryReadManagedProfile` accepts the browser endpoint only when the supplied profile matches the managed session and the loopback listener PID and executable match that session. `CodexDesktopDebugTransport` bounds target-list size and only attaches to a unique `app://-/index.html` page. Before exposing the session, it checks independent app-scheme, main-content, composer, and visible Codex-mode signals. Auxiliary pages and ambiguous targets are rejected.

```csharp
using Codex.RuntimeKit.Desktop.DebugTransport;

var ownerResolver = new WindowsCodexDesktopListenerOwnerResolver();
if (!CodexDesktopDebugEndpoint.TryReadManagedProfile(
        managedSession.ProfileDirectory,
        managedSession,
        ownerResolver,
        out var endpoint,
        out var diagnostic))
{
    throw new InvalidOperationException(diagnostic);
}

var transport = new CodexDesktopDebugTransport();
var targets = await transport.GetTargetsAsync(endpoint!);
var attached = await transport.AttachToMainRendererAsync(endpoint!, targets);
if (attached.Session is null)
{
    throw new InvalidOperationException(attached.DiagnosticCode);
}
```

The attached session intentionally has no public raw `Runtime.evaluate` or generic CDP command method.

## Bundled renderer extensions

`CodexDesktopExtensionHost` accepts only a caller-bundled package whose identifier and SHA-256 are present in the host's compiled allowlist. Scripts have a 64 KiB size cap, a tested desktop-version range, and must return a cleanup function. The host registers a removable new-document script, injects idempotently into the verified main renderer, and removes the old script on update or disable. The bootstrap rechecks the main app URL, content signals, and Codex mode before running after navigation. There is no provider-catalog script execution, remote source loading, marketplace, or file-path based loader.

The extension source still owns its specific UI anchor and must remove every root, event handler, and observer in its cleanup function. It should use a bounded observer, semantic anchors, text-only value updates, and fail closed when an anchor is missing or ambiguous. RuntimeKit does not choose an anchor or own provider/account data.

Set `CodexDesktopExtensionPackage.RestorePayloadOnReload` to false when the caller must revalidate a live binding before sending the next payload after a reload. Removal unregisters the reload script before cleaning the current document and can be retried on the same renderer channel when script removal fails.

The currently installed Desktop build was observed as MSIX `26.1117.3838.0`. RuntimeKit does not mark that build as compatible by default: a product integration must verify its status-slot anchor and visible cleanup/reload behavior before adding an explicit rule. Plugin-extension surfaces do not provide a documented bottom status pill.

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
