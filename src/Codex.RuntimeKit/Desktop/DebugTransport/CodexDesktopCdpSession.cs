using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codex.RuntimeKit.Desktop.Extensions;
using Codex.RuntimeKit.Desktop.Renderer;

namespace Codex.RuntimeKit.Desktop.DebugTransport;

public sealed class CodexDesktopDebugTransport
{
    private const int MaximumTargetResponseBytes = 256 * 1024;

    public async Task<IReadOnlyList<CodexDesktopRendererTarget>> GetTargetsAsync(
        CodexDesktopDebugEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsSafeLoopbackHttpEndpoint(endpoint.BaseAddress, endpoint.Port))
        {
            throw new InvalidOperationException("The verified CDP endpoint is invalid.");
        }

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.GetAsync(
            new Uri(endpoint.BaseAddress, "json/list"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumTargetResponseBytes)
        {
            return Array.Empty<CodexDesktopRendererTarget>();
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumTargetResponseBytes)
            {
                return Array.Empty<CodexDesktopRendererTarget>();
            }

            buffer.Write(chunk, 0, read);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 128)
        {
            return Array.Empty<CodexDesktopRendererTarget>();
        }

        var targets = new List<CodexDesktopRendererTarget>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!TryReadTarget(item, out var target))
            {
                continue;
            }

            targets.Add(target);
        }

        return targets;
    }

    public async Task<CodexDesktopRendererAttachResult> AttachToMainRendererAsync(
        CodexDesktopDebugEndpoint endpoint,
        IReadOnlyList<CodexDesktopRendererTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(targets);

        var selection = CodexDesktopRendererVerifier.SelectMainRenderer(targets, endpoint.Port);
        if (selection.State != CodexDesktopRendererSelectionState.Selected || selection.Target is null)
        {
            return new(selection.State, null, selection.DiagnosticCode);
        }

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(selection.Target.WebSocketDebuggerUrl, connectTimeout.Token).ConfigureAwait(false);
            var session = new CodexDesktopCdpSession(endpoint, selection.Target, socket);
            await session.SendCommandAsync("Runtime.enable", new { }, cancellationToken).ConfigureAwait(false);
            await session.SendCommandAsync("Page.enable", new { }, cancellationToken).ConfigureAwait(false);
            var result = await session.SendCommandAsync("Runtime.evaluate", new
            {
                expression = CodexDesktopRendererProbe.Expression,
                returnByValue = true,
                awaitPromise = false,
                userGesture = false,
            }, cancellationToken).ConfigureAwait(false);

            if (!TryReadSignals(result, out var signals) || !CodexDesktopRendererVerifier.AcceptSemanticSignals(signals))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new(CodexDesktopRendererSelectionState.InvalidTarget, null, "desktop.renderer.semantic-signals-missing");
            }

            session.IsVerifiedCodexRenderer = true;
            return new(CodexDesktopRendererSelectionState.Selected, session, "desktop.renderer.attached");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception)
        {
            socket.Dispose();
            return new(CodexDesktopRendererSelectionState.InvalidTarget, null, "desktop.renderer.attach-failed");
        }
    }

    private static bool TryReadTarget(JsonElement item, out CodexDesktopRendererTarget target)
    {
        target = null!;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("webSocketDebuggerUrl", out var socketValue) || socketValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var id = idValue.GetString();
        var type = typeValue.GetString();
        var pageUrl = urlValue.GetString();
        var socketText = socketValue.GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
            !Regex.IsMatch(id, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(type) || type.Length > 32 ||
            string.IsNullOrWhiteSpace(pageUrl) || pageUrl.Length > 2048 ||
            !Uri.TryCreate(socketText, UriKind.Absolute, out var socket))
        {
            return false;
        }

        target = new CodexDesktopRendererTarget(id, type, pageUrl, socket);
        return true;
    }

    private static bool TryReadSignals(JsonElement response, out CodexDesktopRendererSignals? signals)
    {
        signals = null;
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("result", out var remoteResult) ||
            !remoteResult.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        signals = new CodexDesktopRendererSignals(
            ReadBoolean(value, "isOfficialAppScheme"),
            ReadBoolean(value, "hasMainContent"),
            ReadBoolean(value, "hasComposer"),
            ReadBoolean(value, "hasCodexModeLabel"));
        return true;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True;

    private static bool IsSafeLoopbackHttpEndpoint(Uri uri, int port) =>
        string.Equals(uri.Scheme, "http", StringComparison.Ordinal) &&
        IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address) &&
        uri.Port == port && string.IsNullOrEmpty(uri.UserInfo) && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private sealed class CodexDesktopRendererProbe
    {
        public const string Expression = """
            (() => {
              const visible = e => { const r=e.getBoundingClientRect(),s=getComputedStyle(e); return r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none'; };
              const controls=[...document.querySelectorAll('button,[role=tab],[role=combobox]')].filter(visible);
              const codex=controls.some(e=>/codex/i.test([e.getAttribute('aria-label'),e.getAttribute('title'),e.getAttribute('data-testid')].filter(Boolean).join(' ')));
              return { isOfficialAppScheme: location.protocol==='app:', hasMainContent: !!document.querySelector('main'), hasComposer: !!document.querySelector('[data-testid*=composer],[contenteditable=true],textarea'), hasCodexModeLabel: codex };
            })()
            """;
    }
}

public sealed record CodexDesktopRendererAttachResult(
    CodexDesktopRendererSelectionState State,
    CodexDesktopCdpSession? Session,
    string DiagnosticCode);

/// <summary>A CDP page session whose target and semantic Codex signals were verified at attach time.</summary>
public sealed class CodexDesktopCdpSession : IAsyncDisposable
{
    private const int MaximumCdpMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly ICodexDesktopExtensionChannel _extensionChannel;
    private int _nextCommandId;
    private bool _disposed;

    internal CodexDesktopCdpSession(
        CodexDesktopDebugEndpoint endpoint,
        CodexDesktopRendererTarget target,
        ClientWebSocket socket)
    {
        Endpoint = endpoint;
        Target = target;
        _socket = socket;
        _extensionChannel = new ExtensionChannelAdapter(this);
    }

    public CodexDesktopDebugEndpoint Endpoint { get; }
    public CodexDesktopRendererTarget Target { get; }
    public bool IsVerifiedCodexRenderer { get; internal set; }
    internal ICodexDesktopExtensionChannel ExtensionChannel => _extensionChannel;

    /// <summary>
    /// Inspects one narrowly-scoped data attribute selector without returning page text or content.
    /// The selector is restricted to a single data-* attribute to keep the probe bounded.
    /// </summary>
    public async Task<CodexDesktopAnchorProbeResult> ProbeUniqueVisibleAnchorAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        EnsureVerified();
        var expression = CreateUniqueVisibleAnchorProbeExpression(selector);
        var result = await SendCommandAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = false,
            userGesture = false,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("result", out var remote) ||
            !remote.TryGetProperty("result", out var value) ||
            !value.TryGetProperty("value", out var fields) ||
            fields.ValueKind != JsonValueKind.Object)
        {
            return new(0, 0, null, null, null);
        }

        return new(
            ReadCount(fields, "totalCount"),
            ReadCount(fields, "visibleCount"),
            ReadText(fields, "tagName"),
            ReadText(fields, "parentTagName"),
            ReadText(fields, "parentClassNames"));
    }

    internal static string CreateUniqueVisibleAnchorProbeExpression(string selector)
    {
        if (!Regex.IsMatch(selector ?? string.Empty, "^\\[data-[a-z0-9-]+\\]$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Only a single data-* attribute selector can be probed.", nameof(selector));
        }

        return """
            (() => {
              const selector = __SELECTOR__;
              const nodes = [...document.querySelectorAll(selector)];
              const visible = nodes.filter(e => {
                const r = e.getBoundingClientRect(), s = getComputedStyle(e);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
              });
              if (visible.length !== 1) return { totalCount: nodes.length, visibleCount: visible.length };
              const e = visible[0], p = e.parentElement;
              const safeClass = n => n ? [...n.classList].filter(x => /^[A-Za-z0-9_:-]{1,48}$/.test(x)).slice(0, 8).join(' ') : '';
              return { totalCount: nodes.length, visibleCount: visible.length, tagName: e.tagName.slice(0, 24), parentTagName: p?.tagName.slice(0, 24) ?? '', parentClassNames: safeClass(p) };
            })()
            """.Replace("__SELECTOR__", JsonSerializer.Serialize(selector), StringComparison.Ordinal);
    }

    /// <summary>Checks that an owned extension node is the immediate sibling after one visible native anchor.</summary>
    public async Task<bool> IsExtensionAdjacentToAnchorAsync(
        string selector,
        string extensionRootAttribute,
        CancellationToken cancellationToken = default)
    {
        EnsureVerified();
        var expression = CreateExtensionAdjacencyProbeExpression(selector, extensionRootAttribute);
        var result = await SendCommandAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = false,
            userGesture = false,
        }, cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("result", out var remote) &&
               remote.TryGetProperty("result", out var value) &&
               value.TryGetProperty("value", out var adjacent) &&
               adjacent.ValueKind == JsonValueKind.True &&
               !remote.TryGetProperty("exceptionDetails", out _);
    }

    internal static string CreateExtensionAdjacencyProbeExpression(string selector, string extensionRootAttribute)
    {
        if (!Regex.IsMatch(selector ?? string.Empty, "^\\[data-[a-z0-9-]+\\]$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(extensionRootAttribute ?? string.Empty, "^data-[a-z0-9-]+$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("The anchor and extension attribute are invalid.");
        }

        return """
            (() => {
              const anchors = [...document.querySelectorAll(__SELECTOR__)].filter(e => {
                const r = e.getBoundingClientRect(), s = getComputedStyle(e);
                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
              });
              const roots = [...document.querySelectorAll('[' + __ROOT_ATTRIBUTE__ + ']')];
              return anchors.length === 1 && roots.length === 1 && anchors[0].parentElement === roots[0].parentElement && anchors[0].nextElementSibling === roots[0];
            })()
            """.Replace("__SELECTOR__", JsonSerializer.Serialize(selector), StringComparison.Ordinal)
               .Replace("__ROOT_ATTRIBUTE__", JsonSerializer.Serialize(extensionRootAttribute), StringComparison.Ordinal);
    }

    private static int ReadCount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) && count >= 0 && count <= 128
            ? count
            : 0;

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 192 } text
            ? text
            : null;

    internal async Task<string?> AddDocumentScriptAsync(string script, CancellationToken cancellationToken)
    {
        EnsureVerified();
        var result = await SendCommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = script }, cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("result", out var value) &&
               value.TryGetProperty("identifier", out var identifier) && identifier.ValueKind == JsonValueKind.String
            ? identifier.GetString()
            : null;
    }

    internal async Task<bool> EvaluateExtensionAsync(string expression, CancellationToken cancellationToken)
    {
        EnsureVerified();
        var response = await SendCommandAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
            userGesture = false,
        }, cancellationToken).ConfigureAwait(false);

        return response.TryGetProperty("result", out var result) &&
               result.TryGetProperty("result", out var remoteResult) &&
               remoteResult.TryGetProperty("value", out var value) &&
               value.ValueKind == JsonValueKind.True &&
               !result.TryGetProperty("exceptionDetails", out _);
    }

    internal async Task<bool> RemoveDocumentScriptAsync(string identifier, CancellationToken cancellationToken)
    {
        EnsureVerified();
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 256)
        {
            return false;
        }

        await SendCommandAsync("Page.removeScriptToEvaluateOnNewDocument", new { identifier }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async Task<JsonElement> SendCommandAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextCommandId);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            while (true)
            {
                var message = await ReceiveMessageAsync(timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number || idValue.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out _))
                {
                    throw new InvalidOperationException("The local CDP command was rejected.");
                }

                return root.Clone();
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commandLock.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Codex RuntimeKit detached", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _socket.Abort();
        }

        _socket.Dispose();
    }

    private async Task<byte[]> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("The local CDP connection closed.");
            }

            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaximumCdpMessageBytes)
            {
                throw new WebSocketException("The local CDP message exceeded the allowed format or size.");
            }

            stream.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                return stream.ToArray();
            }
        }
    }

    private void EnsureVerified()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsVerifiedCodexRenderer)
        {
            throw new InvalidOperationException("The renderer has not passed Codex verification.");
        }
    }

    private sealed class ExtensionChannelAdapter(CodexDesktopCdpSession session) : ICodexDesktopExtensionChannel
    {
        public bool IsVerifiedCodexRenderer => session.IsVerifiedCodexRenderer;
        public Task<string?> AddDocumentScriptAsync(string script, CancellationToken cancellationToken) =>
            session.AddDocumentScriptAsync(script, cancellationToken);
        public Task<bool> EvaluateExtensionAsync(string expression, CancellationToken cancellationToken) =>
            session.EvaluateExtensionAsync(expression, cancellationToken);
        public Task<bool> RemoveDocumentScriptAsync(string identifier, CancellationToken cancellationToken) =>
            session.RemoveDocumentScriptAsync(identifier, cancellationToken);
    }
}

public sealed record CodexDesktopAnchorProbeResult(
    int TotalCount,
    int VisibleCount,
    string? TagName,
    string? ParentTagName,
    string? ParentClassNames)
{
    public bool IsUniqueVisible => TotalCount == 1 && VisibleCount == 1;
}
