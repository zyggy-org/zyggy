using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

namespace Zyggy.Integration.Node;

/// <summary>Spec 07 AC-13, AC-21 and AC-22 (this step's half): the host built in process from an accepted <c>node.json</c> — the Serilog file, the banner line, the exit codes.</summary>
public sealed class NodeHostStartupTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly ScratchDirectory _scratch = new();

    [Fact]
    public async Task RunAsync_AcceptedConfiguration_CancelledAfterTwoSeconds_ExitZeroAndLogFileHoldsNodeStarted()
    {
        string nodeDir = _scratch.File("node");
        string path = WriteNodeJson(nodeDir);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        int exitCode = await NodeHost.RunAsync(["--config", path], cts.Token);

        exitCode.Should().Be(0);
        string[] logFiles = Directory.GetFiles(Path.Combine(nodeDir, "logs"));
        logFiles.Should().ContainSingle().Which.Should().MatchRegex(@"zyggy-\d{8}\.log$");
        string[] lines = await File.ReadAllLinesAsync(logFiles[0], TestContext.Current.CancellationToken);
        lines.Where(l => l.Contains("node started: tenant acme, machine home-laptop, version ", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Contain(" acme home-laptop ", "the template carries the enriched tenant and machine");
        SecretSweep.AssertAbsent(lines);
    }

    [Fact]
    public async Task RunAsync_LogDirectoryNotCreatable_ExitSixStderr()
    {
        string nodeDir = _scratch.File("not-a-directory");
        await File.WriteAllTextAsync(nodeDir, "a file where the node directory should be", TestContext.Current.CancellationToken);
        string path = WriteNodeJson(nodeDir);

        int exitCode = await NodeHost.RunAsync(["--config", path], TestContext.Current.CancellationToken);
        Action build = () => NodeHost.Build(["--config", path]);

        exitCode.Should().Be(6);
        build.Should().Throw<NodeStartupException>().Which.Should().Match<NodeStartupException>(e =>
            e.ExitCode == 6 && e.Message.Contains(Path.Join(nodeDir, "logs"), StringComparison.Ordinal));
    }

    [Fact]
    public void Build_AcceptedConfiguration_RegistersNodeConfigurationAndSerilogLoggerFactory()
    {
        string path = WriteNodeJson(_scratch.File("node"));

        using IHost host = NodeHost.Build(["--config", path]);

        NodeConfiguration configuration = host.Services.GetRequiredService<NodeConfiguration>();
        configuration.Tenant.Should().Be(Acme);
        configuration.Machine.Should().Be(HomeLaptop);
        configuration.SignWith.Should().Be(TestKeys.Acme1);
        host.Services.GetRequiredService<ILoggerFactory>().Should().BeOfType<SerilogLoggerFactory>();
    }

    public void Dispose() => _scratch.Dispose();

    private string WriteNodeJson(string nodeDir) =>
        NodeJson.Write(
            _scratch.File("config"),
            Acme,
            HomeLaptop,
            TestKeys.Acme1,
            busLocalPath: _scratch.File("bus"),
            remote: "https://example.invalid/acme-org/acme-bus.git",
            secretsRoot: _scratch.File("secrets"),
            nodeDir: nodeDir);
}
