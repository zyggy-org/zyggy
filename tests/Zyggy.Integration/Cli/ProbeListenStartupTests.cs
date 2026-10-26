using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 04 AC-27: a bad node.json or checkout makes <c>zyggy probe listen</c> exit 3 with one line, touching nothing.</summary>
public sealed class ProbeListenStartupTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly ScratchDirectory _scratch = new();

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [MachineName.Parse("central"), HomeLaptop], CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _scratch.Dispose();
        await _bus.DisposeAsync();
    }

    [Theory]
    [InlineData("no-tenant", "tenant missing")]
    [InlineData("uppercase-tenant", "tenant invalid")]
    [InlineData("not-a-repo", "bus.localPath is not a git repository")]
    [InlineData("dev-branch", "checkout is on 'dev', expected 'main'")]
    public async Task ProbeListen_BadConfiguration_ExitThreeOneStderrLineNothingTouched(string variant, string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string localPath = _bus.CloneDir;
        string? tenantOverride = null;
        bool omitTenant = false;
        switch (variant)
        {
            case "no-tenant":
                omitTenant = true;
                break;
            case "uppercase-tenant":
                tenantOverride = "Acme";
                break;
            case "not-a-repo":
                localPath = _scratch.File("empty");
                Directory.CreateDirectory(localPath);
                break;
            default:
                await _bus.RunGitAsync(_bus.CloneDir, ["switch", "-q", "-c", "dev"], ct);
                break;
        }

        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, KeyId.Parse("acme/1"), localPath, "https://example.invalid/acme-org/acme-bus.git", _scratch.File("secrets"), tenantOverride: tenantOverride, omitTenant: omitTenant);
        string headBefore = await _bus.HeadShaAsync(ct);
        string fetchHead = Path.Combine(_bus.CloneDir, ".git", "FETCH_HEAD");
        DateTime? fetchHeadBefore = File.Exists(fetchHead) ? File.GetLastWriteTimeUtc(fetchHead) : null;
        await File.WriteAllTextAsync(Path.Combine(_bus.CloneDir, "dirty.txt"), "dirty\n", ct);

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "listen", "--config", nodeJson], new Dictionary<string, string?>(), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(3, run.Stderr);
        // spec 09 AC-3: on Windows the harness's `secrets.store: file` announces itself once (a two-line warn block) before the refusal
        run.Stderr.TrimEnd().Split('\n').Where(l => !l.StartsWith("warn:", StringComparison.Ordinal) && !l.StartsWith("      ", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain(expected);
        (await _bus.RunGitAsync(_bus.CloneDir, ["stash", "list"], ct)).Should().BeEmpty("nothing stashed");
        (await _bus.HeadShaAsync(ct)).Should().Be(headBefore);
        (File.Exists(fetchHead) ? File.GetLastWriteTimeUtc(fetchHead) : (DateTime?)null).Should().Be(fetchHeadBefore, "nothing pulled");
    }

    [Fact]
    public async Task ProbeListen_SecondInstance_ExitSix()
    {
        // Arrange: patch 0.8.1 — a first listener holds the checkout's listen lock
        var ct = TestContext.Current.CancellationToken;
        string secrets = _scratch.File("secrets");
        await using (ServiceProvider seed = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = secrets).BuildServiceProvider())
        {
            await seed.GetRequiredService<ISecretStore>().SetAsync(Acme, Zyggy.Core.Tests.Infrastructure.TestKeys.Acme1.SecretName, Zyggy.Core.Tests.Infrastructure.TestKeys.Secret1, ct);
        }

        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, KeyId.Parse("acme/1"), _bus.CloneDir, _bus.BareDir.Replace('\\', '/'), secrets);
        var env = new Dictionary<string, string?> { ["ZYGGY_BUS_API_BASE"] = FakeGitHubApi.ClosedPortAddress(), ["GIT_CONFIG_GLOBAL"] = Path.Combine(_bus.RootDir, "gitconfig") };
        using CapturedProcess first = ZyggyCli.StartCapturing(["probe", "listen", "--config", nodeJson], env, _scratch.Path);
        try
        {
            for (int i = 0; i < 100 && !first.Stderr.Any(l => l.Contains("listening as", StringComparison.Ordinal)); i++)
            {
                await Task.Delay(100, ct);
            }

            first.Stderr.Should().Contain(l => l.Contains("listening as", StringComparison.Ordinal));

            // Act
            ZyggyRun second = await ZyggyCli.RunAsync(["probe", "listen", "--config", nodeJson], env, null, _scratch.Path, ct);

            // Assert
            second.ExitCode.Should().Be(6, second.Stderr);
            second.Stderr.Should().Contain("another zyggy probe listen already runs in this checkout");
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProbeListen_SignWithMissing_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, null, _bus.CloneDir, "https://example.invalid/acme-org/acme-bus.git", _scratch.File("secrets"));

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "listen", "--config", nodeJson], new Dictionary<string, string?>(), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Contain("signWith missing");
    }

    [Fact]
    public async Task ProbeListen_KeyNotInStore_ExitSix()
    {
        // Arrange: a valid configuration whose secret store holds no hmac/1
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, KeyId.Parse("acme/1"), _bus.CloneDir, "https://example.invalid/acme-org/acme-bus.git", _scratch.File("secrets"));

        // Act
        ZyggyRun run = await ZyggyCli.RunAsync(["probe", "listen", "--config", nodeJson], new Dictionary<string, string?>(), null, _scratch.Path, ct);

        // Assert
        run.ExitCode.Should().Be(6, run.Stderr);
        run.Stderr.Should().Contain("signing key acme/1 is not in the file; run: zyggy secret set hmac/1 --hex");
    }

    [Fact]
    public async Task Listen_StoreUnavailable_ExitSixOneLine()
    {
        // Arrange: spec 09 AC-2 — the binary cannot take a substituted store, so this one fact runs in process; the probe verbs
        // write to the process console (04), captured here for the duration of the call
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = NodeJson.Write(_scratch.File("cfg"), Acme, HomeLaptop, KeyId.Parse("acme/1"), _bus.CloneDir, "https://example.invalid/acme-org/acme-bus.git", _scratch.File("secrets"));
        TextWriter original = Console.Error;
        var captured = new StringWriter();
        int exit;

        // Act
        Console.SetError(captured);
        try
        {
            (exit, _) = await ZyggyCliInProcess.RunAsync(["probe", "listen", "--config", nodeJson], new Dictionary<string, string?>(), s => s.AddSingleton<ISecretStore>(new ThrowingSecretStore()), null, ct);
        }
        finally
        {
            Console.SetError(original);
        }

        // Assert
        exit.Should().Be(6, captured.ToString());
        captured.ToString().Should().Contain("zyggy: secret store unavailable: credential-manager: 1312 No such logon session");
    }
}
