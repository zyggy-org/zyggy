using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Git;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.Bus;

/// <summary>
/// The GitHub implementation of <see cref="IBusProvider"/> — the only type that knows GitHub's API, URL shapes and headers
/// (founding spec §5 poll loop items 1–2, §9 seam rule). One conditional <c>GET</c> per tick through the system proxy; the
/// token is read from <see cref="ISecretStore"/> on every request and travels only in the <c>Authorization</c> header.
/// Never throws for an expected failure; a caller's own cancellation propagates.
/// </summary>
internal sealed partial class GitHubBusProvider : IBusProvider, IDisposable
{
    private const string PatSecret = "github-pat";

    private static readonly Uri DefaultApiBaseUri = new("https://api.github.com");

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly GitClient _git;
    private readonly ISecretStore _secrets;
    private readonly BusIdentity _me;
    private readonly string _checkoutRoot;
    private readonly string _version;
    private readonly Uri _apiBaseUri;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubBusProvider> _log;
    private bool _warnedMissingETag;

    public GitHubBusProvider(
        HttpMessageHandler handler,
        GitClient git,
        BusRemote remote,
        ISecretStore secrets,
        BusIdentity me,
        string checkoutRoot,
        string version,
        Uri? apiBaseUri,
        TimeProvider clock,
        ILogger<GitHubBusProvider> log)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentException.ThrowIfNullOrEmpty(checkoutRoot);
        ArgumentException.ThrowIfNullOrEmpty(version);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _git = git ?? throw new ArgumentNullException(nameof(git));
        Remote = remote;
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _checkoutRoot = checkoutRoot;
        _version = version;
        _apiBaseUri = apiBaseUri ?? DefaultApiBaseUri;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public BusRemote Remote { get; }

    public async Task<HeadProbe> ProbeHeadAsync(string? etag, CancellationToken ct)
    {
        ReadOnlyMemory<byte>? token = await _secrets.GetAsync(_me.Tenant, SecretName.Parse(PatSecret), ct).ConfigureAwait(false);
        if (token is null)
        {
            return new Unreachable("no " + PatSecret + " for tenant " + _me.Tenant.Value);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, HeadUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(token.Value.Span));
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", "zyggy/" + _version);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            return Map(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return new Unreachable("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return new Unreachable(ex.GetType().Name);
        }
    }

    public async Task<BusSyncResult> PullAsync(CancellationToken ct)
    {
        GitResult result = await _git.PullRebaseAsync(_checkoutRoot, Remote.Branch, ct).ConfigureAwait(false);
        return result.Succeeded
            ? new BusSyncResult(true, string.Empty)
            : new BusSyncResult(false, Detail(result));
    }

    public async Task<PushResult> PushAsync(CancellationToken ct)
    {
        GitResult result = await _git.PushAsync(_checkoutRoot, Remote.Branch, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return new Pushed();
        }

        if (result.ExitCode is null)
        {
            return new PushFailed("git push did not finish (not started or timed out)");
        }

        string stderr = result.Stderr;
        bool rejected = stderr.Contains("[rejected]", StringComparison.Ordinal)
            || stderr.Contains("non-fast-forward", StringComparison.Ordinal)
            || stderr.Contains("fetch first", StringComparison.Ordinal);
        return rejected ? new Rejected(Detail(result)) : new PushFailed(Detail(result));
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The first 300 characters of git's error output with any URL user information redacted (§8: never a credential in a result or log).</summary>
    private static string Detail(GitResult result)
    {
        string text = UrlUserInfo().Replace(result.Stderr.Trim(), "://<redacted>@");
        return text.Length <= 300 ? text : text[..300];
    }

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UrlUserInfo();

    [LoggerMessage(Level = LogLevel.Warning, Message = "the bus host answered 200 without an ETag; every tick pulls until it sends one")]
    private partial void LogMissingETag();

    private Uri HeadUri() =>
        new(_apiBaseUri, string.Create(CultureInfo.InvariantCulture, $"/repos/{Remote.Owner}/{Remote.Repository}/commits?sha={Remote.Branch}&per_page=1"));

    private HeadProbe Map(HttpResponseMessage response)
    {
        RateLimitHeaders limits = RateLimitHeaders.Read(response, _clock.GetUtcNow());
        HeadProbe probe = response.StatusCode switch
        {
            HttpStatusCode.NotModified => new Unchanged(limits.Remaining),
            HttpStatusCode.OK => new Changed(ETagOf(response), limits.Remaining),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new Throttled(limits.ResetUtc, limits.RetryAfter, limits.Remaining),
            HttpStatusCode.Unauthorized => new Unreachable("unauthorized"),
            _ => new Unreachable("http " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)),
        };
        return probe with { MinimumInterval = limits.PollInterval };
    }

    private string ETagOf(HttpResponseMessage response)
    {
        string? etag = response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;
        if (etag is null && !_warnedMissingETag)
        {
            _warnedMissingETag = true;
            LogMissingETag();
        }

        return etag ?? string.Empty;
    }

    /// <summary>GitHub's pacing headers of one response, parsed leniently: anything unparseable is simply absent.</summary>
    /// <param name="Remaining">The remaining request budget (<c>x-ratelimit-remaining</c>).</param>
    /// <param name="ResetUtc">When the budget resets (<c>x-ratelimit-reset</c>, epoch seconds).</param>
    /// <param name="RetryAfter">The <c>retry-after</c> delay, relative to <c>now</c> when sent as a date.</param>
    /// <param name="PollInterval">The <c>x-poll-interval</c> the host asked for between checks.</param>
    internal sealed record RateLimitHeaders(int? Remaining, DateTimeOffset? ResetUtc, TimeSpan? RetryAfter, TimeSpan? PollInterval)
    {
        internal static RateLimitHeaders Read(HttpResponseMessage response, DateTimeOffset now)
        {
            HttpResponseHeaders headers = response.Headers;
            int? remaining = headers.TryGetValues("x-ratelimit-remaining", out var remainingValues)
                && int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int r)
                ? r
                : null;
            DateTimeOffset? reset = headers.TryGetValues("x-ratelimit-reset", out var resetValues)
                && long.TryParse(resetValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long epoch)
                && epoch <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;
            TimeSpan? pollInterval = headers.TryGetValues("x-poll-interval", out var pollValues)
                && int.TryParse(pollValues.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
            return new RateLimitHeaders(remaining, reset, RetryAfterOf(headers.RetryAfter, now), pollInterval);
        }

        private static TimeSpan? RetryAfterOf(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
        {
            if (retryAfter is null)
            {
                return null;
            }

            if (retryAfter.Delta is { } delta)
            {
                return delta >= TimeSpan.Zero ? delta : null;
            }

            if (retryAfter.Date is { } date)
            {
                TimeSpan delay = date - now;
                return delay >= TimeSpan.Zero ? delay : TimeSpan.Zero;
            }

            return null;
        }
    }
}
