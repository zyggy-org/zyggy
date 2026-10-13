using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>Spec 04 AC-21 and AC-22: the §5 retry rule and the deferred commit, against the bare repository with the real git.</summary>
public sealed class PushRetryTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private InMemorySecretStore _store = null!;

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _services = TestHost.Build(_bus, HomeLaptop, _store, _hooks, logging: _log);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task ClaimAsync_RemoteAdvancedBetweenPullAndPush_RetriedOnceAndOnTop()
    {
        // Arrange: the sender moves main right before the SUT's first push
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job, _) = await PulledJobAsync(ct);
        bool advanced = false;
        _hooks.BeforeRun = async spec =>
        {
            if (!advanced && HookedProcessRunner.SubCommand(spec) == "push")
            {
                advanced = true;
                await _bus.AdvanceMainFromSenderAsync(ct);
            }
        };

        // Act
        WriteOutcome outcome = await repository.ClaimAsync(job, ct);

        // Assert
        outcome.Should().Be(new Pushed(2));
        var subjects = (await _bus.RunGitAsync(_bus.BareDir, ["log", "--format=%s", "-2", "main"], ct)).Split('\n');
        subjects[0].Should().Be($"acme claim {job.File.Id} central→home-laptop");
        subjects[1].Should().Be("noise 1");
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), job.File.Id), ct)).Should().BeTrue();
        _hooks.Calls.Count(c => HookedProcessRunner.SubCommand(c) == "push").Should().Be(2);
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task ClaimAsync_EveryPushFails_DeferredCleanTreeOneUnpushedCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job, string headBefore) = await PulledJobAsync(ct);
        _hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;

        // Act
        WriteOutcome outcome = await repository.ClaimAsync(job, ct);

        // Assert
        outcome.Should().BeOfType<Deferred>().Which.Attempts.Should().Be(4);
        (await _bus.RevListCountAsync("origin/main..HEAD", ct)).Should().Be(1);
        (await _bus.RunGitAsync(_bus.CloneDir, ["status", "--porcelain"], ct)).Should().BeEmpty();
        (await _bus.HeadShaAsync(ct)).Should().Be(headBefore);
        _log.AtLevel(LogLevel.Error).Should().ContainSingle();
        _hooks.Calls.Count(c => HookedProcessRunner.SubCommand(c) == "push").Should().Be(4);
        _hooks.Calls.Select(HookedProcessRunner.SubCommand).Should().NotContain("reset");
        SecretSweep.AssertAbsent(_hooks.Calls.SelectMany(SecretSweep.TextsOf));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task WriteAfterDeferred_PushesTheDeferredCommitToo()
    {
        // Arrange: a deferred claim, then the hooks are removed
        var ct = TestContext.Current.CancellationToken;
        (BusRepository repository, AcceptedJob job, string headBefore) = await PulledJobAsync(ct);
        _hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        (await repository.ClaimAsync(job, ct)).Should().BeOfType<Deferred>();
        _hooks.Override = null;

        // Act
        WriteOutcome outcome = await repository.ReportAsync(job, new ReportFields(ReportStatus.Done), Encoding.UTF8.GetBytes("ok\n"), TestKeys.Acme1, ct);

        // Assert: both commits on main, nothing left unpushed
        outcome.Should().Be(new Pushed(1));
        (await _bus.RevListCountAsync("origin/main..HEAD", ct)).Should().Be(0);
        (await _bus.RunGitAsync(_bus.BareDir, ["rev-list", "--count", headBefore + "..main"], ct)).Should().Be("2");
        var subjects = (await _bus.RunGitAsync(_bus.BareDir, ["log", "--format=%s", "-2", "main"], ct)).Split('\n');
        subjects[0].Should().StartWith("acme report ");
        subjects[1].Should().Be($"acme claim {job.File.Id} central→home-laptop");
    }

    private async Task<(BusRepository Repository, AcceptedJob Job, string Head)> PulledJobAsync(CancellationToken ct)
    {
        await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, _store, "probe\n", ct: ct);
        string head = await _bus.HeadShaAsync(ct);
        BusRepository repository = _services.GetRequiredService<BusRepository>();
        (await _services.GetRequiredService<IBusProvider>().PullAsync(ct)).Succeeded.Should().BeTrue();
        var job = (AcceptedJob)await repository.VerifyIntakeAsync((await repository.ListNewJobsAsync(ct)).Single(), ct);
        return (repository, job, head);
    }
}
