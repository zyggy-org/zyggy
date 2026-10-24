using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 08 AC-2: the bytes a submit would write are signed, re-parsed, re-verified and compared with the request before any checkout is touched.</summary>
public sealed class JobSubmitterBuildTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    /// <summary>A clock with milliseconds, so the seconds truncation of <c>created</c> is observable.</summary>
    private static readonly DateTimeOffset T = new(2026, 10, 24, 10, 0, 0, 345, TimeSpan.Zero);

    private static readonly string[] Section4Keys =
    [
        "schema", "id", "tenant", "type", "from", "to", "created", "in_reply_to", "priority", "project", "agent", "worktree",
        "allowed_tools", "report_back", "timeout_minutes", "deadline", "attempt", "key_id", "sig",
    ];

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<JobSubmitter> _log = new();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task BuildAsync_ValidRequest_BytesVerifyAndEqualTheRequest()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        JobRequest request = Request("/home/u/src/calizr", agent: "reviewer", timeout: 15, tools: ["Read, Grep"], priority: "high", body: "Investigate 502.\r\n");

        // Act
        BuildResult result = await Submitter().BuildAsync(request, ct);

        // Assert
        var built = result.Should().BeOfType<Built>().Subject;
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(built.Bytes, Acme, ct);
        check.IsAccepted.Should().BeTrue(check.Rejection?.ToString());
        Model job = check.Envelope!;
        job.Type.Should().Be(EnvelopeType.Job);
        job.Header.Tenant.Should().Be(Acme);
        job.Header.From.Should().Be(Central);
        job.Header.To.Should().Be(HomeLaptop);
        job.Header.Priority.Should().Be(EnvelopePriority.High);
        job.Header.Created.Should().Be(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        job.Header.Id.Timestamp.Should().Be(T);
        job.Job!.Project.Should().Be("/home/u/src/calizr");
        job.Job.Agent.Should().Be("reviewer");
        job.Job.Worktree.Should().BeFalse();
        job.Job.AllowedTools.Should().Equal("Read", "Grep");
        job.Job.ReportBack.Should().BeNull();
        job.Job.TimeoutMinutes.Should().Be(15);
        job.Job.Deadline.Should().BeNull();
        job.Job.Attempt.Should().Be(1);
        job.Body.Span.SequenceEqual(request.Body.Span).Should().BeTrue();
        job.BodyText.Should().Be("Investigate 502.\n");
        job.FrontMatter.Entries.Keys.Should().BeSubsetOf(Section4Keys);
        built.Envelope.Header.Id.Should().Be(job.Header.Id);
        _processes.Calls.Should().BeEmpty("building touches no checkout");
    }

    [Fact]
    public async Task BuildAsync_BodyStartingWithFrontMatterDelimiter_EntirelyBody()
    {
        // Act
        BuildResult result = await Submitter().BuildAsync(Request("calizr", body: "---\nnot front matter\n"), TestContext.Current.CancellationToken);

        // Assert
        var built = result.Should().BeOfType<Built>().Subject;
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(built.Bytes, Acme, TestContext.Current.CancellationToken);
        check.Envelope!.BodyText.Should().Be("---\nnot front matter\n");
        check.Envelope.Job!.Project.Should().Be("calizr");
    }

    [Fact]
    public async Task BuildAsync_BashToolWithSpacesAndParentheses_RoundTrips()
    {
        // Act
        BuildResult result = await Submitter().BuildAsync(Request("calizr", tools: ["Read", "Bash(dotnet *)"]), TestContext.Current.CancellationToken);

        // Assert
        var built = result.Should().BeOfType<Built>().Subject;
        Encoding.UTF8.GetString(built.Bytes).Should().Contain("Bash(dotnet *)");
        EnvelopeResult check = await new EnvelopeSigner(_store).ParseAndVerifyAsync(built.Bytes, Acme, TestContext.Current.CancellationToken);
        check.Envelope!.Job!.AllowedTools.Should().Equal("Read", "Bash(dotnet *)");
    }

    [Fact]
    public async Task BuildAsync_SerializerAltersAField_RefusedAndNothingElseHappens()
    {
        // Arrange: plan A1 — the serializer hook stands in for a broken emitter that writes a field differently from what was signed.
        // The signature covers every field, so the self-check surfaces it on `sig`; the field comparison behind it is belt and braces.
        JobSubmitter submitter = Submitter(serializer: e => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(EnvelopeWriter.Serialize(e)).Replace("project: calizr", "project: calizr2", StringComparison.Ordinal)));

        // Act
        BuildResult result = await submitter.BuildAsync(Request("calizr"), TestContext.Current.CancellationToken);

        // Assert
        var refused = result.Should().BeOfType<BuildRefused>().Subject;
        refused.Field.Should().BeOneOf("project", "sig");
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_SerializerCorruptsBody_RefusedOnTheSignature()
    {
        // Arrange: a byte appended after signing — the signature covers the body, so the self-check fails on `sig` first
        JobSubmitter submitter = Submitter(serializer: e => [.. EnvelopeWriter.Serialize(e), (byte)'x']);

        // Act
        BuildResult result = await submitter.BuildAsync(Request("calizr"), TestContext.Current.CancellationToken);

        // Assert: either way the self-check catches it before any write
        var refused = result.Should().BeOfType<BuildRefused>().Subject;
        refused.Field.Should().BeOneOf("sig", "body");
    }

    [Fact]
    public async Task BuildAsync_NeverLogsTheBody()
    {
        // Arrange
        const string token = "body-token-9c41-do-not-log";

        // Act
        await Submitter().BuildAsync(Request("calizr", body: "Investigate " + token + "\n"), TestContext.Current.CancellationToken);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains(token, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    private JobSubmitter Submitter(Func<Model, byte[]>? serializer = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return serializer is null
            ? new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, _log)
            : new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, _log) { Serializer = serializer };
    }

    private static JobRequest Request(string project, string? agent = null, int? timeout = null, string[]? tools = null, string? priority = null, string body = "Investigate 502.\n")
    {
        JobRequest.TryCreate("home-laptop", project, agent, timeout, tools, priority, body, out JobRequest? request).Should().BeNull();
        return request!;
    }
}
