using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus.Probe;

/// <summary>Spec 04 AC-28 (the runner) over a substitute provider and a recorded git.</summary>
public sealed class ReachabilityRunTests
{
    private static readonly Uri Https = new("https://example.invalid/acme-org/acme-bus.git");

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly RecordingLogger<ReachabilityCheck> _log = new();

    public ReachabilityRunTests()
    {
        _provider.Remote.Returns(new BusRemote("acme-org", "acme-bus", "main", Https, null));
        _provider.ApiBaseUri.Returns(new Uri("https://api.example.invalid"));
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unchanged(4999));
    }

    [Fact]
    public async Task RunAsync_BuildsFourRowsInSpecOrder()
    {
        // Act
        ReachReport report = await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        report.Rows.Select(r => r.Check).Should().Equal("api", "fetch", "push", "ssh");
        report.Rows.Select(r => r.Status).Should().Equal(ReachStatus.Ok, ReachStatus.Ok, ReachStatus.Ok, ReachStatus.Skipped);
        report.Rows[0].Endpoint.Should().Be("api.example.invalid");
        report.Rows[1].Endpoint.Should().Be(Https.ToString());
        report.ExitCode.Should().Be(0);
        report.GitProxy.Should().Be("none");
        report.GitSslBackend.Should().Be("none");
        report.CredentialHelper.Should().Be("none");
        _processes.Calls.Select(c => string.Join(' ', c.Arguments)).Should().Equal(
            "config --get http.proxy",
            "config --get http.sslBackend",
            "config --get credential.helper",
            "ls-remote --exit-code " + Https + " refs/heads/main",
            "push --dry-run " + Https + " HEAD:refs/heads/main");
    }

    [Fact]
    public async Task RunAsync_SshNotConfigured_SkippedExitUnaffected()
    {
        // Act
        ReachReport report = await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        report.Rows[3].Should().Be(new ReachResult("ssh", "-", ReachStatus.Skipped, "no ssh remote configured", "none"));
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SshFails_ExitStillZeroWhenOthersOk()
    {
        // Arrange
        _provider.Remote.Returns(new BusRemote("acme-org", "acme-bus", "main", Https, new Uri("ssh://git@example.invalid/acme-org/acme-bus.git")));
        _processes.On("ls-remote", RecordingProcessRunner.Ok(), RecordingProcessRunner.Fail(128, "ssh: connect to host example.invalid port 22: Connection timed out"));

        // Act
        ReachReport report = await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        report.Rows[3].Status.Should().Be(ReachStatus.Unreachable);
        report.Rows[3].Detail.Should().Contain("Connection timed out");
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_FetchAuthFailed_ExitSix()
    {
        // Arrange
        _processes.On("ls-remote", RecordingProcessRunner.Fail(128, "fatal: Authentication failed for 'https://example.invalid/acme-org/acme-bus.git/'"));

        // Act
        ReachReport report = await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        report.Rows[1].Status.Should().Be(ReachStatus.AuthFailed);
        report.ExitCode.Should().Be(6);
    }

    [Fact]
    public async Task RunAsync_ApiUnreachable_ExitSix()
    {
        // Arrange
        _provider.ProbeHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Unreachable("HttpRequestException"));

        // Act
        ReachReport report = await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        report.Rows[0].Status.Should().Be(ReachStatus.Unreachable);
        report.Rows[0].Detail.Should().Be("HttpRequestException");
        report.ExitCode.Should().Be(6);
    }

    [Fact]
    public async Task RunAsync_GitEnvForSsh_ExactBatchModeCommand()
    {
        // Arrange
        var ssh = new Uri("ssh://git@example.invalid/acme-org/acme-bus.git");
        _provider.Remote.Returns(new BusRemote("acme-org", "acme-bus", "main", Https, ssh));

        // Act
        await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        ProcessSpec sshCall = _processes.CallsOf("ls-remote").Last();
        sshCall.Arguments.Should().Equal("ls-remote", "--exit-code", ssh.ToString(), "refs/heads/main");
        sshCall.Environment.Should().Contain("GIT_SSH_COMMAND", "ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new");
        _processes.CallsOf("ls-remote").First().Environment.Should().NotContainKey("GIT_SSH_COMMAND");
    }

    [Fact]
    public async Task RunAsync_GitProxyAndHelper_ReportedRedacted()
    {
        // Arrange
        _processes.On("config", RecordingProcessRunner.Ok("http://user:" + TestKeys.TestPat + "@proxy.example.invalid:3128\n"), RecordingProcessRunner.Ok("schannel\n"), RecordingProcessRunner.Ok("manager\n"));

        // Act
        ReachReport report = await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert
        report.GitProxy.Should().Be("proxy.example.invalid:3128");
        report.GitSslBackend.Should().Be("schannel");
        report.CredentialHelper.Should().Be("manager");
        report.Rows[1].Proxy.Should().Be("proxy.example.invalid:3128");
        SecretSweep.AssertAbsent([report.RenderMarkdown()]);
    }

    [Fact]
    public async Task RunAsync_NeverPassesCredentialToGit()
    {
        // Act
        await Check().RunAsync(TestContext.Current.CancellationToken);

        // Assert: no `-c credential.*` or `-c http.extraHeader`, no userinfo in a URL, no GIT_ASKPASS set by Zyggy (reading credential.helper is allowed)
        _processes.Calls.Should().AllSatisfy(c =>
        {
            for (int i = 0; i + 1 < c.Arguments.Count; i++)
            {
                if (c.Arguments[i] == "-c")
                {
                    c.Arguments[i + 1].Should().NotStartWith("credential.").And.NotStartWith("http.extraHeader");
                }
            }

            string.Join(' ', c.Arguments).Should().NotMatchRegex("://[^/@ ]+@");
            c.Environment.Should().NotContainKey("GIT_ASKPASS");
        });
    }

    [Fact]
    public void RenderMarkdown_HeaderAndColumns()
    {
        // Arrange
        var report = new ReachReport(
            [new ReachResult("api", "api.example.invalid", ReachStatus.Ok, "answered", "none"), new ReachResult("ssh", "-", ReachStatus.Skipped, "no ssh remote configured", "none")],
            "none",
            "none",
            "manager",
            "proxy.local:3128");

        // Act
        string[] lines = report.RenderMarkdown().Split('\n');

        // Assert
        lines[0].Should().Be("git http.proxy: none; http.sslBackend: none; credential.helper: manager; system proxy for the API: proxy.local:3128");
        lines[2].Should().Be("| check | endpoint | result | detail | proxy |");
        lines[4].Should().Be("| api | api.example.invalid | ok | answered | none |");
        lines[5].Should().Be("| ssh | - | skipped | no ssh remote configured | none |");
    }

    private ReachabilityCheck Check() =>
        new(_provider, new GitClient(_processes, new GitClientOptions(), new FakeTimeProvider()), "/bus", _log);
}
