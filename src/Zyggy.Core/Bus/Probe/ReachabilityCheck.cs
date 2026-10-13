using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;

namespace Zyggy.Core.Bus.Probe;

/// <summary>
/// The reachability matrix of <c>zyggy probe reach</c> (deliverable 04, founding spec §1 "verified in P0"): four checks against
/// the bus host — the API doorbell, <c>git ls-remote</c> over HTTPS, <c>git push --dry-run</c> over HTTPS, <c>git ls-remote</c>
/// over SSH — each through the owner's own proxy, TLS backend and credential helper, none writing anything to the remote.
/// The static members classify git's and the API's answers and redact evidence; Zyggy never passes a credential to git.
/// </summary>
public sealed partial class ReachabilityCheck
{
    private const int DetailLimit = 300;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] AuthPatterns =
    [
        "Authentication failed",
        "could not read Username",
        "could not read Password",
        "Permission denied (publickey)",
        "Permission to ",
        "HTTP 403",
        "403 Forbidden",
        "The requested URL returned error: 403",
        "Invalid username or token",
    ];

    private static readonly string[] TlsPatterns =
    [
        "SSL certificate problem",
        "schannel:",
        "SSL_ERROR",
        "certificate verify failed",
        "unable to get local issuer certificate",
    ];

    private readonly IBusProvider _provider;
    private readonly GitClient _git;
    private readonly string _checkoutRoot;
    private readonly ILogger<ReachabilityCheck> _log;

    internal ReachabilityCheck(IBusProvider provider, GitClient git, string checkoutRoot, ILogger<ReachabilityCheck> log)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkoutRoot);
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _checkoutRoot = checkoutRoot;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Runs the four checks; each is bounded to 30 s and a timeout is <c>unreachable (timeout)</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matrix.</returns>
    public async Task<ReachReport> RunAsync(CancellationToken ct)
    {
        BusRemote remote = _provider.Remote;
        Uri https = remote.HttpsUrl ?? throw new InvalidOperationException("the provider did not fill the remote's HTTPS URL");
        string? gitProxyRaw = await _git.ConfigGetAsync(_checkoutRoot, "http.proxy", ct).ConfigureAwait(false);
        string sslBackend = await ConfigAsync("http.sslBackend", ct).ConfigureAwait(false);
        string credentialHelper = await ConfigAsync("credential.helper", ct).ConfigureAwait(false);
        string systemProxy = ProxyDisplay(HttpClient.DefaultProxy.GetProxy(_provider.ApiBaseUri));
        string gitProxyDisplay = gitProxyRaw is null ? "none" : Uri.TryCreate(gitProxyRaw, UriKind.Absolute, out Uri? proxyUri) ? ProxyDisplay(proxyUri) : Redact(gitProxyRaw);

        var rows = new List<ReachResult>(4);

        (ReachStatus apiStatus, string apiDetail) = ClassifyApi(await _provider.ProbeHeadAsync(null, ct).ConfigureAwait(false));
        rows.Add(new ReachResult("api", _provider.ApiBaseUri.Host, apiStatus, apiDetail, systemProxy));

        rows.Add(await GitCheckAsync("fetch", https, token => _git.LsRemoteAsync(_checkoutRoot, https, remote.Branch, new Dictionary<string, string?>(), token), gitProxyDisplay, ct).ConfigureAwait(false));
        rows.Add(await GitCheckAsync("push", https, token => _git.PushDryRunAsync(_checkoutRoot, https, "HEAD:refs/heads/" + remote.Branch, token), gitProxyDisplay, ct).ConfigureAwait(false));

        if (remote.SshUrl is null)
        {
            rows.Add(new ReachResult("ssh", "-", ReachStatus.Skipped, "no ssh remote configured", "none"));
        }
        else
        {
            var env = new Dictionary<string, string?> { ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new" };
            rows.Add(await GitCheckAsync("ssh", remote.SshUrl, token => _git.LsRemoteAsync(_checkoutRoot, remote.SshUrl, remote.Branch, env, token), "none", ct).ConfigureAwait(false));
        }

        var report = new ReachReport(rows, gitProxyDisplay, sslBackend, credentialHelper, systemProxy);
        string summary = string.Join(", ", rows.Select(r => r.Check + "=" + ReachStatusWire.ToWire(r.Status)));
        LogReport(report.ExitCode, summary);
        return report;
    }

    /// <summary>Classifies a git process result (the <c>fetch</c>, <c>push</c> and <c>ssh</c> checks).</summary>
    /// <param name="exitCode">The exit code; <see langword="null"/> when the process did not start or timed out.</param>
    /// <param name="stderr">Git's standard error.</param>
    /// <returns>The status.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stderr"/> is null.</exception>
    public static ReachStatus ClassifyGit(int? exitCode, string stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        if (exitCode == 0)
        {
            return ReachStatus.Ok;
        }

        if (exitCode is null)
        {
            return ReachStatus.Unreachable;
        }

        if (TlsPatterns.Any(p => stderr.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return ReachStatus.TlsFailed;
        }

        if (AuthPatterns.Any(p => stderr.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return ReachStatus.AuthFailed;
        }

        return ReachStatus.Unreachable;
    }

    /// <summary>Classifies the API check's probe outcome.</summary>
    /// <param name="probe">What the doorbell said.</param>
    /// <returns>The status and a short detail.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="probe"/> is null.</exception>
    public static (ReachStatus Status, string Detail) ClassifyApi(HeadProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return probe switch
        {
            Changed or Unchanged => (ReachStatus.Ok, "answered"),
            Throttled => (ReachStatus.Ok, "answered (rate limited)"),
            Unreachable { Detail: "unauthorized" } => (ReachStatus.AuthFailed, "unauthorized"),
            Unreachable { Detail: var d } when d.StartsWith("no github-pat", StringComparison.Ordinal) => (ReachStatus.AuthFailed, "no token"),
            Unreachable { Detail: var d } when d.Contains("AuthenticationException", StringComparison.Ordinal) => (ReachStatus.TlsFailed, d),
            Unreachable { Detail: var d } => (ReachStatus.Unreachable, d),
            _ => (ReachStatus.Unreachable, probe.GetType().Name),
        };
    }

    /// <summary>Strips user information from any URL in <paramref name="text"/> and keeps the first 300 characters.</summary>
    /// <param name="text">Raw evidence (git's stderr).</param>
    /// <returns>The redacted text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string redacted = UrlUserInfo().Replace(text.Trim(), "://<redacted>@");
        return redacted.Length <= DetailLimit ? redacted : redacted[..DetailLimit];
    }

    /// <summary>The proxy as <c>host:port</c>, never its user information; <c>none</c> when there is no proxy.</summary>
    /// <param name="proxy">The proxy URI, or <see langword="null"/>.</param>
    /// <returns>The display text.</returns>
    public static string ProxyDisplay(Uri? proxy) =>
        proxy is null ? "none" : string.Create(CultureInfo.InvariantCulture, $"{proxy.Host}:{proxy.Port}");

    private static async Task<ReachResult> GitCheckAsync(string check, Uri endpoint, Func<CancellationToken, Task<GitResult>> run, string proxy, CancellationToken ct)
    {
        string shown = endpoint.IsAbsoluteUri && !string.IsNullOrEmpty(endpoint.UserInfo) ? endpoint.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped) : endpoint.ToString();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            GitResult result = await run(timeout.Token).ConfigureAwait(false);
            ReachStatus status = ClassifyGit(result.ExitCode, result.Stderr);
            string detail = status == ReachStatus.Ok ? "ok" : Redact(result.Stderr.Length > 0 ? result.Stderr : result.Stdout);
            return new ReachResult(check, shown, status, detail, proxy);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReachResult(check, shown, ReachStatus.Unreachable, "timeout", proxy);
        }
    }

    private async Task<string> ConfigAsync(string key, CancellationToken ct) =>
        await _git.ConfigGetAsync(_checkoutRoot, key, ct).ConfigureAwait(false) is { } value ? Redact(value) : "none";

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UrlUserInfo();

    [LoggerMessage(Level = LogLevel.Information, Message = "reach: exit {ExitCode} ({Rows})")]
    private partial void LogReport(int exitCode, string rows);
}
