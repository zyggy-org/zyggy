using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 AC-8..AC-11: the doorbell is rung the §5 way, with the PAT only ever in the Authorization header.</summary>
public sealed class GitHubBusProviderTests : IDisposable
{
    private const string Version = "1.2.3-test";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly BusIdentity Me = new(Acme, MachineName.Parse("home-laptop"));

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private static readonly DateTimeOffset T = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly StubGitHubHandler _http = new();

    private readonly RecordingProcessRunner _processes = new();

    private readonly RecordingLogger<GitHubBusProvider> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    public static TheoryData<string, string[], HeadProbe> Responses => new()
    {
        { "304", ["x-ratelimit-remaining: 4321"], new Unchanged(4321) },
        { "200", ["ETag: W/\"new\"", "x-ratelimit-remaining: 4000"], new Changed("W/\"new\"", 4000) },
        { "403", ["x-ratelimit-remaining: 0", "x-ratelimit-reset: 1791277200"], new Throttled(DateTimeOffset.FromUnixTimeSeconds(1791277200), null, 0) },
        { "429", ["retry-after: 60"], new Throttled(null, TimeSpan.FromSeconds(60), null) },
        { "401", [], new Unreachable("unauthorized") },
        { "500", [], new Unreachable("http 500") },
        { "502", ["x-ratelimit-remaining: 10"], new Unreachable("http 502") },
    };

    [Fact]
    public async Task ProbeHeadAsync_WithETag_SendsOneSpecRequest()
    {
        // Arrange
        _http.Respond(304);
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        await provider.ProbeHeadAsync("W/\"old\"", TestContext.Current.CancellationToken);

        // Assert
        RecordedRequest request = _http.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(new Uri("https://api.github.com/repos/acme-org/acme-bus/commits?sha=main&per_page=1"));
        request.Headers["Authorization"].Should().Be("Bearer " + TestKeys.TestPat);
        request.Headers["If-None-Match"].Should().Be("W/\"old\"");
        request.Headers["User-Agent"].Should().Be("zyggy/" + Version);
        request.Headers["Accept"].Should().Be("application/vnd.github+json");
        request.Headers["X-GitHub-Api-Version"].Should().Be("2022-11-28");
    }

    [Fact]
    public async Task ProbeHeadAsync_NoETag_OmitsIfNoneMatch()
    {
        // Arrange
        _http.Respond(200, "ETag: W/\"first\"");
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        _http.Requests.Single().Headers.Should().NotContainKey("If-None-Match");
        probe.Should().Be(new Changed("W/\"first\"", null));
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task ProbeHeadAsync_Response_MapsToHeadProbe(string status, string[] headers, HeadProbe expected)
    {
        // Arrange
        _http.Respond(int.Parse(status, CultureInfo.InvariantCulture), headers);
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync("W/\"old\"", TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(expected);
    }

    [Fact]
    public async Task ProbeHeadAsync_HttpRequestException_IsUnreachable()
    {
        // Arrange
        _http.Throw(new HttpRequestException("connection refused"));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.Should().BeOfType<Unreachable>().Which.Detail.Should().Be("HttpRequestException");
    }

    [Fact]
    public async Task ProbeHeadAsync_HandlerCancels_IsUnreachable()
    {
        // Arrange
        _http.Throw(new TaskCanceledException("the handler gave up"));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.Should().BeOfType<Unreachable>().Which.Detail.Should().Be("TaskCanceledException");
    }

    [Theory]
    [InlineData(304)]
    [InlineData(200)]
    public async Task ProbeHeadAsync_PollIntervalHeader_SetsMinimumInterval(int status)
    {
        // Arrange
        _http.Respond(status, "ETag: W/\"e\"", "x-poll-interval: 30");
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.MinimumInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ProbeHeadAsync_RetryAfterAsHttpDate_ParsedToDelay()
    {
        // Arrange: an HTTP-date 90 s after the fake clock
        string date = T.AddSeconds(90).ToString("r", CultureInfo.InvariantCulture);
        _http.Respond(429, "retry-after: " + date);
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.Should().BeOfType<Throttled>().Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task ProbeHeadAsync_GarbageHeaders_NeverThrow()
    {
        // Arrange
        _http.Respond(403, "x-ratelimit-remaining: lots", "x-ratelimit-reset: soon", "retry-after: later", "x-poll-interval: -5");
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(new Throttled(null, null, null));
    }

    [Fact]
    public async Task ProbeHeadAsync_200WithoutETag_ChangedEmptyLoggedOnce()
    {
        // Arrange
        _http.Respond(200).Respond(200);
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        HeadProbe first = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);
        HeadProbe second = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        first.Should().Be(new Changed(string.Empty, null));
        second.Should().Be(new Changed(string.Empty, null));
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
    }

    [Fact]
    public async Task ProbeHeadAsync_NeverThrows()
    {
        // Arrange: every scripted shape in one provider lifetime
        _http.Respond(304).Respond(200, "ETag: \"x\"").Respond(403).Respond(429).Respond(401).Respond(500)
            .Throw(new HttpRequestException("x")).Throw(new TaskCanceledException()).Throw(new InvalidOperationException("odd handler"));
        GitHubBusProvider provider = await ProviderAsync();
        var probes = new List<HeadProbe>();

        // Act
        for (int i = 0; i < 9; i++)
        {
            probes.Add(await provider.ProbeHeadAsync("\"x\"", TestContext.Current.CancellationToken));
        }

        // Assert
        probes.Should().HaveCount(9);
        probes.Skip(6).Should().AllBeOfType<Unreachable>();
    }

    [Fact]
    public async Task ProbeHeadAsync_DetailNeverContainsTokenOrHeaderValues()
    {
        // Arrange
        _http.Respond(401, "www-authenticate: Bearer realm=\"" + TestKeys.TestPat + "\"").Respond(500, "ETag: W/\"leak\"").Throw(new HttpRequestException("boom " + TestKeys.TestPat));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        var details = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            details.Add(((Unreachable)await provider.ProbeHeadAsync("W/\"old\"", TestContext.Current.CancellationToken)).Detail);
        }

        // Assert
        SecretSweep.AssertAbsent(details);
        SecretSweep.AssertAbsent(_log.Messages);
        details.Should().OnlyContain(d => !d.Contains("W/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeHeadAsync_NoPatInStore_UnreachableWithoutRequest()
    {
        // Arrange
        GitHubBusProvider provider = Provider(new InMemorySecretStore());

        // Act
        HeadProbe probe = await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        probe.Should().Be(new Unreachable("no github-pat for tenant acme"));
        _http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeHeadAsync_TwoRequests_ReadsPatTwice()
    {
        // Arrange
        ISecretStore store = Substitute.For<ISecretStore>();
        store.GetAsync(Acme, TestKeys.GitHubPat, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReadOnlyMemory<byte>?>(Encoding.UTF8.GetBytes(TestKeys.TestPat)));
        _http.Respond(304).Respond(304);
        GitHubBusProvider provider = Provider(store);

        // Act
        await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);
        await provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Assert
        await store.Received(2).GetAsync(Acme, TestKeys.GitHubPat, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_TimeoutIsThirtySeconds()
    {
        // Arrange
        _http.Hang();
        GitHubBusProvider provider = await ProviderAsync();
        Task<HeadProbe> pending = provider.ProbeHeadAsync(null, TestContext.Current.CancellationToken);

        // Act
        _clock.Advance(TimeSpan.FromSeconds(29));
        bool stillPending = !pending.IsCompleted;
        _clock.Advance(TimeSpan.FromSeconds(2));
        HeadProbe probe = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        stillPending.Should().BeTrue();
        _http.Tokens.Single().IsCancellationRequested.Should().BeTrue();
        probe.Should().Be(new Unreachable("timeout"));
    }

    [Fact]
    public async Task ProbeHeadAsync_CallerCancels_Propagates()
    {
        // Arrange
        _http.Hang();
        GitHubBusProvider provider = await ProviderAsync();
        using var cts = new CancellationTokenSource();
        Task<HeadProbe> pending = provider.ProbeHeadAsync(null, cts.Token);

        // Act
        await cts.CancelAsync();
        Func<Task> act = () => pending;

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(1, "! [rejected] main -> main (fetch first)", typeof(Rejected))]
    [InlineData(1, "error: failed to push some refs\nhint: Updates were rejected because the tip is behind (non-fast-forward)", typeof(Rejected))]
    [InlineData(128, "fatal: Authentication failed for 'https://example.invalid/acme-org/acme-bus.git/'", typeof(PushFailed))]
    [InlineData(0, "", typeof(PushSucceeded))]
    public async Task PushAsync_Stderr_Classifies(int exitCode, string stderr, Type expected)
    {
        // Arrange
        _processes.On("push", exitCode == 0 ? RecordingProcessRunner.Ok() : RecordingProcessRunner.Fail(exitCode, stderr));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        PushResult result = await provider.PushAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType(expected);
        _processes.Calls.Should().ContainSingle().Which.Arguments.Should().Equal("push", "origin", "HEAD:main");
    }

    [Fact]
    public async Task PushAsync_ProcessDidNotStart_IsPushFailed()
    {
        // Arrange: exit code null = git did not start or timed out
        _processes.On("push", new Zyggy.Core.Processes.ProcessResult(null, "", "", TimedOut: true, StartFailed: false, StdoutTruncated: false, TimeSpan.Zero));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        PushResult result = await provider.PushAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<PushFailed>().Which.Detail.Should().Contain("not finish");
    }

    [Fact]
    public async Task PullAsync_Failure_IsResultNotException()
    {
        // Arrange
        _processes.On("pull", RecordingProcessRunner.Fail(1, "fatal: unable to access 'https://example.invalid/': Could not resolve host"));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        BusSyncResult result = await provider.PullAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("Could not resolve host");
        _processes.Calls.Should().ContainSingle().Which.Arguments.Should().Equal("pull", "--rebase", "origin", "main");
    }

    [Fact]
    public async Task PullAsync_Success_IsEmptyDetail()
    {
        // Arrange
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        BusSyncResult result = await provider.PullAsync(TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new BusSyncResult(true, string.Empty));
    }

    [Fact]
    public async Task PushAsync_DetailNeverContainsUrlUserinfo()
    {
        // Arrange: a credential leaked into a remote URL by a misconfigured machine must not reach a result or a log
        _processes.On("push", RecordingProcessRunner.Fail(128, "fatal: unable to access 'https://user:" + TestKeys.TestPat + "@example.invalid/acme-org/acme-bus.git/': 403\n" + new string('x', 500)));
        GitHubBusProvider provider = await ProviderAsync();

        // Act
        PushResult result = await provider.PushAsync(TestContext.Current.CancellationToken);

        // Assert
        string detail = result.Should().BeOfType<PushFailed>().Which.Detail;
        SecretSweep.AssertAbsent([detail]);
        detail.Should().Contain("https://<redacted>@example.invalid/");
        detail.Length.Should().BeLessThanOrEqualTo(300);
    }

    [Fact]
    public void Remote_IsTheConfiguredOne()
    {
        // Act
        GitHubBusProvider provider = Provider(new InMemorySecretStore());

        // Assert
        provider.Remote.Should().Be(Remote);
    }

    public void Dispose() => _http.Dispose();

    private async Task<GitHubBusProvider> ProviderAsync() => Provider(await TestKeys.StoreWithPatAsync(Acme, TestContext.Current.CancellationToken));

    private GitHubBusProvider Provider(ISecretStore store) =>
        new(_http, new GitClient(_processes, new GitClientOptions(), _clock), Remote, store, Me, "/bus", Version, new Uri("https://api.github.com"), _clock, _log);
}
