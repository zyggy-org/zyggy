using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
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
        run.Stderr.TrimEnd().Split('\n').Should().ContainSingle().Which.Should().Contain(expected);
        (await _bus.RunGitAsync(_bus.CloneDir, ["stash", "list"], ct)).Should().BeEmpty("nothing stashed");
        (await _bus.HeadShaAsync(ct)).Should().Be(headBefore);
        (File.Exists(fetchHead) ? File.GetLastWriteTimeUtc(fetchHead) : (DateTime?)null).Should().Be(fetchHeadBefore, "nothing pulled");
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
        run.Stderr.Should().Contain("signing key acme/1 is not in the secret store");
    }
}
