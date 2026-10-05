using System.Text;
using System.Text.Json;

using Zyggy.Core.Processes;

namespace Zyggy.Core.M365.Mcp;

/// <summary>The probe's report lines, or its failure (exit and message without the prefix).</summary>
internal sealed record ProbeOutcome(int Exit, string? Stdout, string? Error);

/// <summary>
/// <c>mcp-server.sh --probe</c> (spec 23 D8, spec 33 AC-26) against the running server on <c>http://127.0.0.1:&lt;port&gt;/mcp</c>: an
/// unauthenticated <c>initialize</c> must answer 401; with a header minted in process (never an argument or a file), <c>initialize</c> and
/// <c>tools/list</c> must answer 200 and the offered tools must be exactly the enabled list. Reports the tools, the listen address and the
/// server's variable names.
/// </summary>
internal sealed class McpProbe(HeaderHelper helper, IReadOnlyList<string> enabled, int port, HttpMessageHandler? handler = null)
{
    private const string DownHint = "runbook 13 \"MCP server down\"";
    private const string InstallHint = "runbook 13 \"Install or upgrade the MCP server\"";
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"zyggy-probe","version":"1"}}}""";
    private const string ToolsList = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    public async Task<ProbeOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var listen = $"127.0.0.1:{port}";
        using var client = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var url = new Uri($"http://{listen}/mcp");
        if (!url.IsLoopback)
        {
            throw new InvalidOperationException("The probe talks to loopback only.");
        }

        var (status, _, error) = await PostAsync(client, url, Initialize, bearer: null, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return Fail($"no server at {listen} ({error}) — {DownHint}");
        }

        if (status != 401)
        {
            return Fail($"an unauthenticated request got {status}, not 401 — {DownHint}");
        }

        var (initStatus, _, initError) = await AuthedAsync(client, url, Initialize, cancellationToken).ConfigureAwait(false);
        if (initError is not null)
        {
            return initStatus < 0 ? new ProbeOutcome(-initStatus, null, initError) : Fail($"initialize failed ({initError})");
        }

        if (initStatus != 200)
        {
            return Fail($"initialize answered {initStatus} — {DownHint}");
        }

        var (listStatus, body, listError) = await AuthedAsync(client, url, ToolsList, cancellationToken).ConfigureAwait(false);
        if (listError is not null)
        {
            return listStatus < 0 ? new ProbeOutcome(-listStatus, null, listError) : Fail("tools/list failed");
        }

        if (listStatus != 200)
        {
            return Fail($"tools/list answered {listStatus} — {DownHint}");
        }

        var names = ToolNames(body).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var expected = enabled.Order(StringComparer.Ordinal).ToList();
        var unexpected = names.Except(expected, StringComparer.Ordinal).ToList();
        var missing = expected.Except(names, StringComparer.Ordinal).ToList();
        if (unexpected.Count > 0)
        {
            return Fail($"server offered tools outside ENABLED_TOOLS ({string.Join(' ', unexpected.Take(5))}) — {InstallHint}");
        }

        if (missing.Count > 0)
        {
            return Fail($"server did not offer {string.Join(' ', missing.Take(5))} — {InstallHint}");
        }

        var report = new StringBuilder();
        report.Append("tools: ").Append(names.Count).Append('\n');
        foreach (var name in names)
        {
            report.Append(name).Append('\n');
        }

        report.Append("listen: ").Append(listen).Append('\n');
        report.Append("env: ").Append(ServerVariableNames(listen)).Append('\n');
        return new ProbeOutcome(0, report.ToString(), null);
    }

    // Each authenticated POST gets its own header from the helper, held in memory only. A helper failure is its own exit (negative status).
    private async Task<(int Status, string Body, string? Error)> AuthedAsync(HttpClient client, Uri url, string json, CancellationToken cancellationToken)
    {
        var header = await helper.RunAsync(cancellationToken).ConfigureAwait(false);
        if (header.StdoutLine is null)
        {
            return (-header.Exit, string.Empty, header.StderrLine!["m365: ".Length..]);
        }

        using var document = JsonDocument.Parse(header.StdoutLine);
        var authorization = document.RootElement.GetProperty("Authorization").GetString()!;
        return await PostAsync(client, url, json, authorization, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int Status, string Body, string? Error)> PostAsync(HttpClient client, Uri url, string json, string? bearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", bearer);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (0, string.Empty, ex.Message.Split('\n')[0]);
        }
    }

    // jq -r '.result.tools[]?.name' — the body as JSON, or the data line of a server-sent event.
    private static IEnumerable<string> ToolNames(string body)
    {
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).FirstOrDefault() ?? "{}";
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array
                ? [.. tools.EnumerateArray().Where(t => t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String).Select(t => t.GetProperty("name").GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // The server's variable names (names only), from the process of this user listening with the contracted argv.
    private static string ServerVariableNames(string listen)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return "unknown";
        }

        var me = UnixNative.EffectiveUserId();
        foreach (var proc in Directory.EnumerateDirectories("/proc").Where(d => Path.GetFileName(d).All(char.IsAsciiDigit)))
        {
            try
            {
                if (UnixNative.Owner(proc) != me)
                {
                    continue;
                }

                var cmdline = File.ReadAllText(Path.Join(proc, "cmdline")).Replace('\0', ' ');
                if (!cmdline.Contains($" --http {listen} ", StringComparison.Ordinal))
                {
                    continue;
                }

                return string.Join(' ', File.ReadAllText(Path.Join(proc, "environ")).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Split('=')[0]).Order(StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A process that went away or is not readable.
            }
        }

        return "unknown";
    }

    private static ProbeOutcome Fail(string message) => new(6, null, message);
}
