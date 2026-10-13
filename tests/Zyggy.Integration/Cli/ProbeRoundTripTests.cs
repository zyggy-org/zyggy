using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 04 AC-26 and AC-29: the probe verbs end to end from the built binary, two processes, one bare repository, a loopback API.</summary>
public sealed class ProbeRoundTripTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private FakeGitHubApi _api = null!;

    private string _senderNodeJson = null!;

    private string _listenerNodeJson = null!;

    private string _ledger = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        _api = new FakeGitHubApi(async token =>
        {
            try
            {
                return await _bus.HeadShaAsync(token);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
        _ledger = _scratch.File("sent.jsonl");
        string remote = _bus.BareDir.Replace('\\', '/');
        _senderNodeJson = NodeJson.Write(_scratch.File("central"), Acme, Central, KeyId.Parse("acme/1"), _bus.SenderDir, remote, _scratch.File("central-secrets"));
        _listenerNodeJson = NodeJson.Write(_scratch.File("home"), Acme, HomeLaptop, KeyId.Parse("acme/1"), _bus.CloneDir, remote, _scratch.File("home-secrets"));
        await SeedSecretsAsync(_scratch.File("central-secrets"), ct);
        await SeedSecretsAsync(_scratch.File("home-secrets"), ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task Send_NoListener_ExitSixJobOnMainLedgerTimeout()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "send", "--to", "home-laptop", "--timeout", "3", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(6, run.Stderr);
        string[] lines = run.Stdout.TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToArray();
        lines.Should().HaveCount(2);
        lines[0].Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{26} home-laptop timeout$");
        lines[1].Should().Be("sent 1, done 0, rejected 0, timeout 1, push_failed 0");
        string id = lines[0].Split(' ')[0];
        (await _bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), EnvelopeId.Parse(id)), ct)).Should().BeTrue();
        File.ReadAllText(_ledger).Should().Contain("\"outcome\":\"timeout\"").And.Contain("\"id\":\"" + id + "\"");
        _api.Requests.Should().NotBeEmpty();
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact]
    public async Task Send_WithListener_ExitZeroLedgerDoneRoundTripPositive()
    {
        // Arrange: the listener first, seen polling
        var ct = TestContext.Current.CancellationToken;
        using CapturedProcess listener = ZyggyCli.StartCapturing(["probe", "listen", "--config", _listenerNodeJson], Env(), _scratch.Path);
        try
        {
            await WaitForRequestsAsync(1, ct);

            // Act
            ZyggyRun run = await ZyggyCli.RunAsync(["probe", "send", "--to", "home-laptop", "--timeout", "30", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct);
            await Task.Delay(500, ct);
            await listener.StopAsync(ct);

            // Assert
            run.ExitCode.Should().Be(0, run.Stderr + string.Join('\n', listener.Stderr));
            string[] lines = run.Stdout.TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToArray();
            lines[0].Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{26} home-laptop done [0-9]+ ms$");
            lines[1].Should().Be("sent 1, done 1, rejected 0, timeout 0, push_failed 0");
            string ledger = File.ReadAllText(_ledger);
            ledger.Should().Contain("\"outcome\":\"done\"");
            long ms = long.Parse(lines[0].Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture);
            ms.Should().BeGreaterThan(0);
            var reports = (await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct)).Split('\n').Where(p => p.EndsWith(".md", StringComparison.Ordinal));
            reports.Should().ContainSingle();
            listener.Stderr.Should().Contain(l => l.Contains("claimed job", StringComparison.Ordinal));
            listener.Stderr.Should().Contain(l => l.Contains("reported job", StringComparison.Ordinal));
            AssertNoSecretAnywhere(run, listener);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stats_AfterTimeoutAndDone_OneRowCountTwoDoneOneTimeoutOne()
    {
        // Arrange: one timeout without a listener, then one done with it
        var ct = TestContext.Current.CancellationToken;
        (await ZyggyCli.RunAsync(["probe", "send", "--to", "home-laptop", "--timeout", "2", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct)).ExitCode.Should().Be(6);
        using CapturedProcess listener = ZyggyCli.StartCapturing(["probe", "listen", "--config", _listenerNodeJson], Env(), _scratch.Path);
        try
        {
            await WaitForRequestsAsync(_api.Requests.Count + 1, ct);
            (await ZyggyCli.RunAsync(["probe", "send", "--to", "home-laptop", "--timeout", "30", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct)).ExitCode.Should().Be(0);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }

        // Act
        ZyggyRun stats = await ZyggyCli.RunAsync(["probe", "stats", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct);

        // Assert
        stats.ExitCode.Should().Be(0, stats.Stderr);
        string[] lines = stats.Stdout.TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToArray();
        lines[0].Should().Be("| to | count | done | rejected | timeout | push_failed | p50 ms | p95 ms | min ms | max ms |");
        lines.Should().HaveCount(3);
        lines[2].Should().MatchRegex(@"^\| home-laptop \| 2 \| 1 \| 0 \| 1 \| 0 \| [0-9]+ \| [0-9]+ \| [0-9]+ \| [0-9]+ \|$");
    }

    [Fact]
    public async Task Stats_EmptyLedger_ExitOneNoProbes()
    {
        // Act
        ZyggyRun stats = await ZyggyCli.RunAsync(["probe", "stats", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, TestContext.Current.CancellationToken);

        // Assert
        stats.ExitCode.Should().Be(1);
        stats.Stdout.Trim().Should().Be("no probes");
    }

    [Fact]
    public async Task Send_CountTwoNoListener_ContinuesSummaryExitSix()
    {
        // Act: two probes, no interval, both time out — the run continues to the summary
        var ct = TestContext.Current.CancellationToken;
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "send", "--to", "home-laptop", "--count", "2", "--interval", "0", "--timeout", "2", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(6);
        run.Stdout.TrimEnd().Split('\n').Should().HaveCount(3);
        run.Stdout.Should().Contain("sent 2, done 0, rejected 0, timeout 2, push_failed 0");
        new Zyggy.Core.Bus.Probe.ProbeLedger(_ledger).ReadAll().Should().HaveCount(2);
    }

    [Fact]
    public async Task GitConfig_NeverWrittenByZyggy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string senderBefore = await _bus.RunGitAsync(_bus.SenderDir, ["config", "--list", "--local"], ct);
        string cloneBefore = await _bus.RunGitAsync(_bus.CloneDir, ["config", "--list", "--local"], ct);

        // Act
        await ZyggyCli.RunAsync(["probe", "send", "--to", "home-laptop", "--timeout", "2", "--config", _senderNodeJson, "--ledger", _ledger], Env(), null, _scratch.Path, ct);

        // Assert
        (await _bus.RunGitAsync(_bus.SenderDir, ["config", "--list", "--local"], ct)).Should().Be(senderBefore);
        (await _bus.RunGitAsync(_bus.CloneDir, ["config", "--list", "--local"], ct)).Should().Be(cloneBefore);
    }

    private void AssertNoSecretAnywhere(ZyggyRun sender, CapturedProcess listener)
    {
        SecretSweep.AssertAbsent([sender.Stdout, sender.Stderr, .. listener.Stdout, .. listener.Stderr]);
        SecretSweep.AssertAbsent([File.ReadAllText(_senderNodeJson), File.ReadAllText(_listenerNodeJson)]);
        foreach (FakeApiRequest request in _api.Requests)
        {
            request.Headers.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bearer " + TestKeys.TestPat);
            SecretSweep.AssertAbsent([request.Target, .. request.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value)]);
        }

        string log = _bus.RunGitAsync(_bus.BareDir, ["log", "-p", "main"], CancellationToken.None).GetAwaiter().GetResult();
        SecretSweep.AssertAbsent([log]);
    }

    private Dictionary<string, string?> Env() => new()
    {
        ["ZYGGY_BUS_API_BASE"] = _api.BaseAddress,
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(_bus.RootDir, "gitconfig"),
    };

    private async Task WaitForRequestsAsync(int count, CancellationToken ct)
    {
        for (int i = 0; i < 300; i++)
        {
            if (_api.Requests.Count >= count)
            {
                return;
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("the listener never rang the doorbell");
    }

    private static async Task SeedSecretsAsync(string root, CancellationToken ct)
    {
        await using ServiceProvider services = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = root).BuildServiceProvider();
        ISecretStore store = services.GetRequiredService<ISecretStore>();
        await store.SetAsync(Acme, TestKeys.Acme1.SecretName, TestKeys.Secret1, ct);
        await store.SetAsync(Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), ct);
    }
}
