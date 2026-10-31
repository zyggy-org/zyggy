using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Node;

/// <summary>Spec 07 AC-22 and AC-15 (first half): the published <c>zyggy-node</c> as a process — its version, and the exit-3 refusals with one stderr line.</summary>
public sealed partial class ZyggyNodeProcessTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    private readonly ScratchDirectory _scratch = new();

    [Fact]
    public async Task Version_PrintsInformationalVersionExitZero()
    {
        ZyggyRun run = await ZyggyNode.RunAsync(["--version"], NoEnvironment, _scratch.Path, TestContext.Current.CancellationToken);

        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Trim().Should().MatchRegex(@"^\d+\.\d+\.\d+");
        run.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Config_TenantMissing_ExitThreeOneStderrLine()
    {
        string nodeDir = _scratch.File("node");
        string path = WriteNodeJson(signWith: TestKeys.Acme1, nodeDir: nodeDir, omitTenant: true);

        ZyggyRun run = await ZyggyNode.RunAsync(["--config", path], NoEnvironment, _scratch.Path, TestContext.Current.CancellationToken);

        run.ExitCode.Should().Be(3);
        Lines(run.Stderr).Should().Equal("zyggy-node: configuration error: tenant: tenant missing");
        run.Stdout.Should().BeEmpty();
        Directory.Exists(Path.Combine(nodeDir, "logs")).Should().BeFalse("nothing starts after a refused node.json");
    }

    [Fact]
    public async Task Config_SignWithMissing_ExitThreeNamesSignWith()
    {
        string path = WriteNodeJson(signWith: null, nodeDir: _scratch.File("node"));

        ZyggyRun run = await ZyggyNode.RunAsync(["--config", path], NoEnvironment, _scratch.Path, TestContext.Current.CancellationToken);

        run.ExitCode.Should().Be(3);
        Lines(run.Stderr).Should().Equal("zyggy-node: configuration error: signWith missing: the node must sign its reports");
        run.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task Config_SecretLookingKey_ExitThree()
    {
        const string value = "ghp_not_a_real_token_0123456789";
        string path = WriteNodeJson(signWith: TestKeys.Acme1, nodeDir: _scratch.File("node"), extra: $"\"githubToken\": \"{value}\"");

        ZyggyRun run = await ZyggyNode.RunAsync(["--config", path], NoEnvironment, _scratch.Path, TestContext.Current.CancellationToken);

        run.ExitCode.Should().Be(3);
        Lines(run.Stderr).Should().ContainSingle().Which.Should().Contain("githubToken").And.NotContain(value);
    }

    [Fact]
    public async Task NoArgs_NoNodeJsonAtDefault_ExitThree()
    {
        string missing = _scratch.File("absent.json");
        var env = new Dictionary<string, string?> { ["ZYGGY_NODE_JSON"] = missing };

        ZyggyRun run = await ZyggyNode.RunAsync([], env, _scratch.Path, TestContext.Current.CancellationToken);

        run.ExitCode.Should().Be(3);
        Lines(run.Stderr).Should().ContainSingle().Which.Should().StartWith("zyggy-node: configuration error: node.json not found at ");
    }

    // Spec 13 AC-16 through the real binary: Central without the drop-in's variables refuses to start.
    [Fact]
    public async Task ZyggyNode_CentralWithoutPickupConfiguration_ExitThreeOneStderrLine()
    {
        string nodeDir = _scratch.File("node");
        string path = NodeJson.Write(
            _scratch.File("config"),
            Acme,
            MachineName.Central,
            TestKeys.Acme1,
            busLocalPath: _scratch.File("bus"),
            remote: "https://example.invalid/acme-org/acme-bus.git",
            secretsRoot: _scratch.File("secrets"),
            nodeDir: nodeDir);

        ZyggyRun run = await ZyggyNode.RunAsync(["--config", path], NoEnvironment, _scratch.Path, TestContext.Current.CancellationToken);

        run.ExitCode.Should().Be(3);
        Lines(run.Stderr).Should().Equal("zyggy-node: configuration error: pickup: ZYGGY_MEMORY_ROOT is not set");
        run.Stdout.Should().BeEmpty();
        Directory.Exists(Path.Combine(nodeDir, "logs")).Should().BeFalse();
    }

    public void Dispose() => _scratch.Dispose();

    private static string[] Lines(string text) => LineBreak().Split(text.TrimEnd('\r', '\n')).Where(l => l.Length > 0).ToArray();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreak();

    private string WriteNodeJson(KeyId? signWith, string nodeDir, bool omitTenant = false, string? extra = null) =>
        NodeJson.Write(
            _scratch.File("config"),
            Acme,
            HomeLaptop,
            signWith,
            busLocalPath: _scratch.File("bus"),
            remote: "https://example.invalid/acme-org/acme-bus.git",
            secretsRoot: _scratch.File("secrets"),
            omitTenant: omitTenant,
            nodeDir: nodeDir,
            extraTopLevelJson: extra);
}
