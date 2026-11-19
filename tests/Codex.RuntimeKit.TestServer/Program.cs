using System.Text.Json;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        WriteEnvironmentReport(args);
        WriteProcessMarker(args);

        var delay = ReadIntArgument(args, "--version-sleep=");
        if (delay > 0 && args.Contains("--version", StringComparer.Ordinal))
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("codex 0.153.4");
            return 0;
        }

        var appServerIndex = Array.IndexOf(args, "app-server");
        if (appServerIndex >= 0 && args.Length > appServerIndex + 1 && args[appServerIndex + 1] == "generate-json-schema")
        {
            var outputIndex = Array.IndexOf(args, "--out");
            if (outputIndex < 0 || outputIndex + 1 >= args.Length)
            {
                return 2;
            }

            var outputDirectory = args[outputIndex + 1];
            Directory.CreateDirectory(outputDirectory);
            if (!args.Contains("--capability-no-files", StringComparer.Ordinal))
            {
                var support = !args.Contains("--capability-unsupported", StringComparer.Ordinal);
                var schema = support ? "{\"method\":\"account/usage/read\"}" : "{\"method\":\"account/read\"}";
                await File.WriteAllTextAsync(Path.Combine(outputDirectory, "schema.json"), schema).ConfigureAwait(false);
            }
            return 0;
        }

        if (appServerIndex >= 0)
        {
            return await RunAppServerAsync(ReadScenario(args)).ConfigureAwait(false);
        }

        return 3;
    }

    private static async Task<int> RunAppServerAsync(string scenario)
    {
        using var outputLock = new SemaphoreSlim(1, 1);
        var responseTasks = new List<Task>();
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            if (root.TryGetProperty("jsonrpc", out _))
            {
                return 11;
            }

            if (method == "initialize")
            {
                var id = root.GetProperty("id").GetInt64();
                await WriteAsync(outputLock, new { id, result = new { ready = true } }).ConfigureAwait(false);
                var clientInfo = root.TryGetProperty("params", out var initParams) && initParams.TryGetProperty("clientInfo", out var info)
                    ? info.Clone()
                    : default;
                await WriteAsync(outputLock, new { method = "fixture/initializeObserved", @params = clientInfo }).ConfigureAwait(false);
                continue;
            }

            if (method == "initialized")
            {
                await WriteAsync(outputLock, new { method = "fixture/ready", @params = new { ready = true } }).ConfigureAwait(false);
                await WriteAsync(outputLock, new { id = 99, method = "fixture/serverRequest", @params = new { prompt = "synthetic" } }).ConfigureAwait(false);
                var home = Environment.GetEnvironmentVariable("CODEX_HOME");
                var hasOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null;
                var hasCodexKey = Environment.GetEnvironmentVariable("CODEX_API_KEY") is not null;
                await WriteAsync(outputLock, new { method = "fixture/environment", @params = new { home, hasOpenAiKey, hasCodexKey } }).ConfigureAwait(false);
                if (scenario == "malformed")
                {
                    Console.Out.WriteLine("this is not json");
                    Console.Out.Flush();
                    return 12;
                }
                continue;
            }

            if (method == "fixture/clientNotification")
            {
                await WriteAsync(outputLock, new { method = "fixture/clientNotificationObserved", @params = new { observed = true } }).ConfigureAwait(false);
                continue;
            }

            if (method is null)
            {
                continue;
            }

            var requestId = root.GetProperty("id").GetInt64();
            if (method == "fixture/error" || scenario == "rpc-error")
            {
                await WriteAsync(outputLock, new { id = requestId, error = new { code = -32600, message = "synthetic error", data = new { fixture = true } } }).ConfigureAwait(false);
            }
            else if (method == "fixture/delay")
            {
                responseTasks.Add(RespondAsync(outputLock, requestId, new { completed = true }, TimeSpan.FromMilliseconds(700)));
            }
            else if (method == "fixture/exit")
            {
                Environment.ExitCode = 17;
                return 17;
            }
            else if (method == "fixture/malformed")
            {
                Console.Out.WriteLine("this is not json");
                Console.Out.Flush();
            }
            else if (method == "fixture/environment")
            {
                var home = Environment.GetEnvironmentVariable("CODEX_HOME");
                var hasOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null;
                var hasCodexKey = Environment.GetEnvironmentVariable("CODEX_API_KEY") is not null;
                await WriteAsync(outputLock, new { id = requestId, result = new { home, hasOpenAiKey, hasCodexKey } }).ConfigureAwait(false);
            }
            else
            {
                var parameters = root.TryGetProperty("params", out var value) ? value : default;
                var delayMilliseconds = parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("delayMilliseconds", out var delayElement) && delayElement.TryGetInt32(out var parsedDelay)
                    ? Math.Clamp(parsedDelay, 0, 2_000)
                    : 0;
                responseTasks.Add(RespondAsync(
                    outputLock,
                    requestId,
                    parameters.ValueKind == JsonValueKind.Undefined ? (object)new { } : parameters.Clone(),
                    TimeSpan.FromMilliseconds(delayMilliseconds)));
            }
        }

        if (scenario == "ignore-eof")
        {
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }

        await Task.WhenAll(responseTasks).ConfigureAwait(false);
        return 0;
    }

    private static async Task RespondAsync<T>(SemaphoreSlim outputLock, long id, T result, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }

        await WriteAsync(outputLock, new { id, result }).ConfigureAwait(false);
    }

    private static async Task WriteAsync<T>(SemaphoreSlim outputLock, T message)
    {
        await outputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            outputLock.Release();
        }
    }

    private static string ReadScenario(IReadOnlyList<string> args)
    {
        var configured = ReadStringArgument(args, "--scenario=");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(home) && File.Exists(Path.Combine(home, "fixture-ignore-eof")))
        {
            return "ignore-eof";
        }

        if (!string.IsNullOrWhiteSpace(home) && File.Exists(Path.Combine(home, "fixture-malformed")))
        {
            return "malformed";
        }

        return "normal";
    }

    private static void WriteEnvironmentReport(IReadOnlyList<string> args)
    {
        var path = Environment.GetEnvironmentVariable("CODEX_RUNTIME_KIT_FIXTURE_REPORT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var report = new
            {
                executable = Environment.ProcessPath,
                arguments = args,
                codexHome = Environment.GetEnvironmentVariable("CODEX_HOME"),
                openAiKeyPresent = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null,
                codexApiKeyPresent = Environment.GetEnvironmentVariable("CODEX_API_KEY") is not null,
                sqliteHomePresent = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME") is not null,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void WriteProcessMarker(IReadOnlyList<string> args)
    {
        var path = ReadStringArgument(args, "--pid-marker=");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try { File.WriteAllText(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static int ReadIntArgument(IReadOnlyList<string> args, string prefix)
    {
        var value = ReadStringArgument(args, prefix);
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static string? ReadStringArgument(IReadOnlyList<string> args, string prefix) =>
        args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}
