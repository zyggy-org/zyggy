using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 04 AC-28: <c>zyggy probe reach</c> from the built binary against the bare repository and a loopback API.</summary>
public sealed class ProbeReachTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    private FakeGitHubApi _api = null!;

    private string _secrets = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [MachineName.Parse("central"), HomeLaptop], CancellationToken.None);
        _api = new FakeGitHubApi(async ct =>
        {
            try
            {
                return await _bus.HeadShaAsync(ct);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
        _secrets = _scratch.File("secrets");
        await using ServiceProvider services = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = _secrets).BuildServiceProvider();
        await services.GetRequiredService<ISecretStore>().SetAsync(Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Fact]
    public async Task Reach_ApiAnswers_FourRowsFetchPushOkExitZeroBareUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, _bus.BareDir.Replace('\\', '/'), _secrets);
        string headBefore = await _bus.HeadShaAsync(ct);

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "reach", "--config", nodeJson], Env(_api.BaseAddress), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        string[] lines = run.Stdout.TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToArray();
        lines[0].Should().StartWith("git http.proxy: none; http.sslBackend: none; credential.helper: none; system proxy for the API: ");
        lines[2].Should().Be("| check | endpoint | result | detail | proxy |");
        lines.Skip(4).Select(l => l.Split('|')[1].Trim()).Should().Equal("api", "fetch", "push", "ssh");
        lines[4].Should().Contain("| ok | answered |");
        lines[5].Should().Contain("| ok | ok |");
        lines[6].Should().Contain("| ok | ok |");
        lines[7].Should().Contain("| skipped | no ssh remote configured |");
        (await _bus.HeadShaAsync(ct)).Should().Be(headBefore, "a dry-run push writes nothing");
        _api.Requests.Should().ContainSingle().Which.Headers["Authorization"].Should().Be("Bearer " + TestKeys.TestPat);
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact]
    public async Task Reach_ApiRefused_ApiUnreachableExitSix()
    {
        // Arrange: nothing listens on the API port
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, _bus.BareDir.Replace('\\', '/'), _secrets);

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "reach", "--config", nodeJson], Env(FakeGitHubApi.ClosedPortAddress()), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(6);
        string api = run.Stdout.Split('\n').First(l => l.StartsWith("| api", StringComparison.Ordinal));
        api.Should().Contain("| unreachable |").And.Contain("HttpRequestException");
        run.Stdout.Split('\n').First(l => l.StartsWith("| fetch", StringComparison.Ordinal)).Should().Contain("| ok |");
    }

    [Fact]
    public async Task Reach_NoPatInStore_ApiAuthFailedNoToken()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, _bus.BareDir.Replace('\\', '/'), _scratch.File("empty-secrets"));

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "reach", "--config", nodeJson], Env(_api.BaseAddress), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(6);
        run.Stdout.Split('\n').First(l => l.StartsWith("| api", StringComparison.Ordinal)).Should().Contain("| auth failed | no token |");
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reach_ProxyUserinfo_NeverPrinted()
    {
        // Arrange: a proxy with a password in the environment; the API base is plain http on loopback, so the request itself is not proxied
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, _bus.BareDir.Replace('\\', '/'), _secrets);
        var env = Env(_api.BaseAddress);
        env["HTTPS_PROXY"] = "http://user:" + TestKeys.TestPat + "@proxy.local:3128";
        env["NO_PROXY"] = "127.0.0.1";

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "reach", "--config", nodeJson], env, null, _scratch.Path, ct);

        // Assert
        run.Stdout.Should().NotContain(TestKeys.TestPat).And.NotContain("user:");
        SecretSweep.AssertAbsent([run.Stdout, run.Stderr]);
    }

    [Fact]
    public async Task Reach_SshRemoteConfiguredButUnreachable_RowUnreachableExitZero()
    {
        // Arrange: an SSH remote nothing answers on; the HTTPS form the provider derives points at GitHub, so fetch/push are not ok here —
        // the fact under test is only that the ssh row never changes the exit code, so it is asserted against the api/fetch/push rows' own verdict.
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, "ssh://git@127.0.0.1:1/acme-org/acme-bus.git", _secrets);

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "reach", "--config", nodeJson], Env(FakeGitHubApi.ClosedPortAddress()), null, _scratch.Path, ct);

        // Assert
        string[] rows = run.Stdout.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| check", StringComparison.Ordinal)).ToArray();
        rows.Should().HaveCount(4);
        rows[3].Should().StartWith("| ssh |").And.Contain("| unreachable |");
        bool othersOk = rows.Take(3).All(r => r.Contains("| ok |", StringComparison.Ordinal));
        run.ExitCode.Should().Be(othersOk ? 0 : 6);
    }

    [Fact]
    public async Task Reach_NeverWritesGitConfig()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, _bus.BareDir.Replace('\\', '/'), _secrets);
        string before = await _bus.RunGitAsync(_bus.CloneDir, ["config", "--list", "--local"], ct);

        // Act
        await ZyggyCli.RunAsync(["probe", "reach", "--config", nodeJson], Env(_api.BaseAddress), null, _scratch.Path, ct);

        // Assert
        (await _bus.RunGitAsync(_bus.CloneDir, ["config", "--list", "--local"], ct)).Should().Be(before);
    }

    private Dictionary<string, string?> Env(string apiBase) => new()
    {
        ["ZYGGY_BUS_API_BASE"] = apiBase,
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(_bus.RootDir, "gitconfig"),
    };
}
