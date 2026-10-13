using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 04 AC-19, AC-20 and AC-23: the four §4 rejections, the malformed file and the foreign tenant, against the bare repository.</summary>
public sealed class RejectionTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly TenantId Globex = TenantId.Parse("globex");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _inner = null!;

    private ISecretStore _store = null!;

    private ServiceProvider _services = null!;

    public static TheoryData<string, string> Cases => new()
    {
        { "flipped", "invalid_signature" },
        { "globex", "tenant_mismatch" },
        { "to-central", "wrong_recipient" },
        { "schema2", "schema_unsupported" },
    };

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _inner = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _store = Substitute.For<ISecretStore>();
        _store.GetAsync(Arg.Any<TenantId>(), Arg.Any<SecretName>(), Arg.Any<CancellationToken>())
            .Returns(call => _inner.GetAsync(call.ArgAt<TenantId>(0), call.ArgAt<SecretName>(1), call.ArgAt<CancellationToken>(2)));
        _services = TestHost.Build(_bus, HomeLaptop, _store, _hooks, logging: _log);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task VerifyThenReject_FourSpecCases_RejectedOnMainWithReasonReport(string variant, string reason)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob seeded = variant switch
        {
            "flipped" => await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _inner, "probe\n", mutate: b => { b[^2] ^= 0x01; return b; }, ct: ct),
            "globex" => await SeedGlobexSignedJobInAcmeInboxAsync(ct),
            "to-central" => await _bus.SeedSignedJobAsync(Central, Central, TestKeys.Acme1, _inner, "probe\n", fileNameOverride: null, ct: ct),
            "schema2" => await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _inner, "probe\n", mutate: b => Rewrite(b, "schema: 1", "schema: 2"), ct: ct),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        if (variant == "to-central")
        {
            // the sender mis-filed a job addressed to central into home-laptop's inbox
            seeded = await MoveSeededIntoMyInboxAsync(seeded, ct);
        }

        BusRepository repository = _services.GetRequiredService<BusRepository>();
        (await _services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        JobIntake intake = await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);

        // Act
        var rejected = intake.Should().BeOfType<RejectedJob>().Subject;
        WriteOutcome outcome = await repository.RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), seeded.Id), ct)).Should().BeTrue();
        (await _bus.ExistsOnMainAsync(seeded.RelativePath, ct)).Should().BeFalse();
        Model report = await SingleReportOnMainAsync(ct);
        report.Report!.Status.Should().Be(ReportStatus.Rejected);
        report.Report.Reason.Should().Be(reason);
        report.Header.InReplyTo.Should().Be(seeded.Id);
        report.Header.To.Should().Be(Central);
        report.Header.From.Should().Be(HomeLaptop);
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be($"acme reject {seeded.Id} home-laptop→central");
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle();
        await _store.DidNotReceive().GetAsync(Globex, Arg.Any<SecretName>(), Arg.Any<CancellationToken>());
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task Reject_NonEnvelopeFile_MovedNoReportOneWarningNamingFileAndMalformed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.Jobs(Acme, HomeLaptop) + "/weird.md";
        await _bus.SeedRawFileAsync(path, Encoding.UTF8.GetBytes("not an envelope\n"), "acme raw", ct);
        BusRepository repository = _services.GetRequiredService<BusRepository>();
        (await _services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        BusFile file = (await repository.ListNewJobsAsync(ct)).Single();
        var rejected = (RejectedJob)await repository.VerifyIntakeAsync(file, ct);

        // Act
        WriteOutcome outcome = await repository.RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        (await _bus.ExistsOnMainAsync(path, ct)).Should().BeFalse();
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), file.Id), ct)).Should().BeTrue();
        (await ReportPathsOnMainAsync(ct)).Should().BeEmpty();
        var warning = _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain("weird.md").And.Contain("malformed");
        (await _bus.LastCommitSubjectAsync(ct)).Should().EndWith("home-laptop→unknown");
    }

    [Fact]
    public async Task Reject_InvalidFromAndBadSig_MovedNoReport()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob seeded = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _inner, "probe\n", mutate: b => Rewrite(b, "from: central", "from: Not-A-Label"), ct: ct);
        BusRepository repository = _services.GetRequiredService<BusRepository>();
        (await _services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        var rejected = (RejectedJob)await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);

        // Act
        WriteOutcome outcome = await repository.RejectAsync(rejected, TestKeys.Acme1, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        rejected.From.Should().BeNull();
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), seeded.Id), ct)).Should().BeTrue();
        (await ReportPathsOnMainAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListNewJobsAsync_GlobexJobBesideAcme_OnlyAcmeListedGlobexUntouchedUnlogged()
    {
        // Arrange: a second tenant's prefix in the same checkout, with a signed job for a machine of the same name
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedTenantAsync(Globex, [HomeLaptop], ct);
        var globexStore = new InMemorySecretStore();
        await globexStore.SetAsync(Globex, new KeyId(Globex, 1).SecretName, TestKeys.Secret1, ct);
        SeededJob foreign = await _bus.SeedSignedJobAsync(MachineName.Parse("x"), HomeLaptop, new KeyId(Globex, 1), globexStore, "foreign\n", ct: ct);
        SeededJob mine = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _inner, "probe\n", ct: ct);
        BusRepository repository = _services.GetRequiredService<BusRepository>();
        (await _services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();

        // Act
        IReadOnlyList<BusFile> files = await repository.ListNewJobsAsync(ct);
        var job = (AcceptedJob)await repository.VerifyIntakeAsync(files.Single(), ct);
        await repository.ClaimAsync(job, ct);

        // Assert
        files.Select(f => f.Id).Should().Equal(mine.Id);
        foreign.RelativePath.Should().StartWith(BusPaths.Tenant(Globex) + "/");
        (await _bus.ShowBytesAsync(foreign.RelativePath, ct)).Should().Equal(foreign.Bytes);
        _log.Messages.Should().NotContain(m => m.Contains("globex", StringComparison.Ordinal));
        await _store.DidNotReceive().GetAsync(Globex, Arg.Any<SecretName>(), Arg.Any<CancellationToken>());
    }

    private static byte[] Rewrite(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    private async Task<SeededJob> SeedGlobexSignedJobInAcmeInboxAsync(CancellationToken ct)
    {
        var globexStore = new InMemorySecretStore();
        await globexStore.SetAsync(Globex, new KeyId(Globex, 1).SecretName, TestKeys.Secret1, ct);
        var id = EnvelopeId.New(DateTimeOffset.UnixEpoch.AddDays(1));
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id);
        byte[] bytes = await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, globexStore, new KeyId(Globex, 1), "probe\n", ct, id: id);
        await _bus.SeedRawFileAsync(path, bytes, "globex job in the acme inbox", ct);
        return new SeededJob(id, path, bytes);
    }

    private async Task<SeededJob> MoveSeededIntoMyInboxAsync(SeededJob seeded, CancellationToken ct)
    {
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), seeded.Id);
        await _bus.SeedRawFileAsync(path, seeded.Bytes, "misfiled job", ct);
        await _bus.RunGitAsync(_bus.SenderDir, ["rm", "-q", "--", seeded.RelativePath], ct);
        await _bus.RunGitAsync(_bus.SenderDir, ["commit", "-q", "-m", "remove the original"], ct);
        await _bus.RunGitAsync(_bus.SenderDir, ["push", "-q", "origin", "HEAD:main"], ct);
        return new SeededJob(seeded.Id, path, seeded.Bytes);
    }

    private async Task<IReadOnlyList<string>> ReportPathsOnMainAsync(CancellationToken ct)
    {
        string folder = BusPaths.Reports(Acme, HomeLaptop);
        string output = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", folder + "/"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).ToList();
    }

    private async Task<Model> SingleReportOnMainAsync(CancellationToken ct)
    {
        string path = (await ReportPathsOnMainAsync(ct)).Should().ContainSingle().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_inner).ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }
}
