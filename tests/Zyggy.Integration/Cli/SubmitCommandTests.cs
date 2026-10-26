using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 08 AC-7 and AC-8: <c>zyggy submit</c> in process from <c>central</c>'s <c>node.json</c> over the sender clone, on the bare repository.</summary>
public sealed partial class SubmitCommandTests : IAsyncLifetime
{
    private const string Body = "Read README.md and finish with a REPORT section.\n";

    private CliHarness _harness = null!;

    private ScratchProject _project = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _project = await ScratchProject.CreateAsync(_harness.Bus, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _project.Dispose();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Submit_PositionalBody_ExitZeroJobOnMainVerifiedSubjectAndCleanSender()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, "--timeout", "5", "--allowed-tools", "Read,Grep", "--priority", "high", Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Match line = SubmittedLine().Match(console.Stdout);
        line.Success.Should().BeTrue(console.Stdout);
        var id = EnvelopeId.Parse(line.Groups["id"].Value);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id);
        console.Stdout.Should().Be($"submitted {id.Value} to home-laptop ({path})\n");
        console.Stderr.Should().BeEmpty();
        (await _harness.Bus.ExistsOnMainAsync(path, ct)).Should().BeTrue();
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be($"acme job {id.Value} central→home-laptop");
        Model job = await _harness.ParseOnMainAsync(path, ct);
        job.Header.From.Should().Be(CliHarness.Central);
        job.Header.To.Should().Be(CliHarness.HomeLaptop);
        job.Header.Priority.Should().Be(EnvelopePriority.High);
        job.Header.Created.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(5));
        job.Job!.Project.Should().Be(_project.Path);
        job.Job.TimeoutMinutes.Should().Be(5);
        job.Job.AllowedTools.Should().Equal("Read", "Grep");
        job.Body.ToArray().Should().Equal(Encoding.UTF8.GetBytes(Body));
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
        string log = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "-p", "-1", "main"], ct);
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr, Encoding.UTF8.GetString(await _harness.Bus.ShowBytesAsync(path, ct)), log]);
    }

    [Fact]
    public async Task Submit_BodyOnStdin_IdenticalToPositional()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path], _harness.CentralNodeJson, stdin: Body, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var id = EnvelopeId.Parse(SubmittedLine().Match(console.Stdout).Groups["id"].Value);
        Model job = await _harness.ParseOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id), ct);
        job.Body.ToArray().Should().Equal(Encoding.UTF8.GetBytes(Body));
        job.Job!.Project.Should().Be(_project.Path);
    }

    [Fact]
    public async Task Submit_BodyPositionalAndStdin_PositionalWins()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, "the positional body\n"], _harness.CentralNodeJson, stdin: "the stdin body\n", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var id = EnvelopeId.Parse(SubmittedLine().Match(console.Stdout).Groups["id"].Value);
        Model job = await _harness.ParseOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id), ct);
        job.BodyText.Should().Be("the positional body\n");
    }

    [Fact]
    public async Task Submit_SelfAddressed_ExitZeroUnderOwnJobs()
    {
        // Act: ex-Q10
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "central", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var id = EnvelopeId.Parse(SubmittedLine().Match(console.Stdout).Groups["id"].Value);
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.Central), id), ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Submit_ToNotALabel_ExitTwoNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "Home Laptop", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        Lines(console.Stderr).Should().ContainSingle().Which.Should().StartWith("zyggy: to:");
        console.Stdout.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_NoBodyNoStdin_ExitTwoBody()
    {
        // Act: no positional, stdin not redirected
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().Be("zyggy: body: give the body as the last argument or on stdin\n");
    }

    [Fact]
    public async Task Submit_SignWithMissing_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.Central, _harness.Bus.SenderDir, "central-nosign", omitSignWith: true);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: signWith missing\n");
    }

    [Fact]
    public async Task Submit_KeyAbsentFromStore_ExitSixHintNothingCommitted()
    {
        // Arrange: an empty secrets root
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.Central, _harness.Bus.SenderDir, "central-nokey", secretsRoot: _harness.Scratch.File("empty-secrets"));
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().Be("zyggy: signing key acme/1 is not in the secret store; run: zyggy secret set hmac/1 --hex\n");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_StoreUnavailable_ExitSixOneLineNothingCommitted()
    {
        // Arrange: spec 09 AC-2 — the sender's store cannot be opened
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(
            ["submit", "--to", "home-laptop", "--project", _project.Path, Body],
            _harness.CentralNodeJson,
            more: s => s.AddSingleton<ISecretStore>(new ThrowingSecretStore()),
            ct: ct);

        // Assert
        exit.Should().Be(6, console.Stderr);
        console.Stderr.Should().Be("zyggy: secret store unavailable: credential-manager: 1312 No such logon session\n");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_LocalPathNotARepository_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string notARepo = _harness.Scratch.File("not-a-repo");
        Directory.CreateDirectory(notARepo);
        string nodeJson = _harness.WriteNodeJson(CliHarness.Central, notARepo, "central-norepo");

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: bus.localPath is not a git repository\n");
    }

    [Fact]
    public async Task Submit_CheckoutOnAnotherBranch_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["switch", "-c", "other"], ct);
        try
        {
            // Act
            var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

            // Assert
            exit.Should().Be(3);
            console.Stderr.Should().Be("zyggy: configuration error: checkout is on 'other', expected 'main'\n");
        }
        finally
        {
            await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["switch", "main"], ct);
        }
    }

    [Fact]
    public async Task Submit_NoTenantDirectory_ExitThree()
    {
        // Arrange: a checkout of a bus seeded for another tenant only
        var ct = TestContext.Current.CancellationToken;
        var foreign = new BusRepoFixture(TenantId.Parse("other-org"));
        await foreign.InitializeAsync();
        try
        {
            await foreign.SeedTenantAsync(TenantId.Parse("other-org"), [CliHarness.Central], ct);
            string nodeJson = _harness.WriteNodeJson(CliHarness.Central, foreign.CloneDir, "central-foreign");
            string porcelainBefore = await _harness.PorcelainAsync(foreign.CloneDir, ct);

            // Act
            var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], nodeJson, ct: ct);

            // Assert
            exit.Should().Be(3);
            console.Stderr.Should().Be("zyggy: configuration error: no tenants/acme/ directory in bus.localPath\n");
            (await _harness.PorcelainAsync(foreign.CloneDir, ct)).Should().Be(porcelainBefore);
        }
        finally
        {
            await foreign.DisposeAsync();
        }
    }

    [Fact]
    public async Task Submit_PullFails_ExitSixWorkingCopyRestored()
    {
        // Arrange: the remote is gone, so the write sequence's pull fails before anything is written
        var ct = TestContext.Current.CancellationToken;
        string porcelainBefore = await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct);
        _harness.Bus.DeleteBare();

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stderr.Should().StartWith("zyggy: submit failed at pull:");
        console.Stdout.Should().BeEmpty();
        Directory.EnumerateFiles(Path.Combine(_harness.Bus.SenderDir, "tenants", "acme", "nodes", "home-laptop", "jobs"), "*.md").Should().BeEmpty();
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().Be(porcelainBefore);
        SecretSweep.AssertAbsent([console.Stderr]);
    }

    [Fact]
    public async Task Submit_DirtySenderCheckout_ExitZeroLocalEditKept()
    {
        // Arrange: plan A7 revisited — main is one commit ahead and the sender holds a locally modified tracked file. Observed: git's
        // `pull --rebase` carries the local change across the rebase here, so the submit goes through and the user's edit is never reset;
        // the pull-failure path is proven by the fact above (an unreachable remote).
        var ct = TestContext.Current.CancellationToken;
        await _harness.Bus.AdvanceMainFromSenderAsync(ct);
        await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["reset", "--hard", "HEAD~1"], ct);
        string protocol = Path.Combine(_harness.Bus.SenderDir, "tenants", "acme", "PROTOCOL.md");
        await File.AppendAllTextAsync(protocol, "a local edit\n", ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        var id = EnvelopeId.Parse(SubmittedLine().Match(console.Stdout).Groups["id"].Value);
        (await _harness.Bus.ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id), ct)).Should().BeTrue();
        (await File.ReadAllTextAsync(protocol, ct)).Should().EndWith("a local edit\n");
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().Contain("PROTOCOL.md");
    }

    [Fact]
    public async Task Submit_UntrackedFileUnderTenant_ExitZero()
    {
        // Arrange: an untracked file alone does not block the pull (recorded for A7)
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_harness.Bus.SenderDir, "tenants", "acme", "stray.txt"), "stray\n", ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
    }

    [Fact]
    public async Task Submit_EveryPushRefused_ExitSevenCommitLocalNotOnMain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;
        int commits = await _harness.CommitCountOnMainAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "home-laptop", "--project", _project.Path, Body], _harness.CentralNodeJson, ct: ct);

        // Assert
        exit.Should().Be(7);
        Match line = SubmittedLine().Match(console.Stdout);
        line.Success.Should().BeTrue(console.Stdout);
        console.Stdout.Should().EndWith(" (committed locally, not yet pushed: the next bus write on this machine pushes it)\n");
        (await _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["log", "-1", "--format=%s"], ct)).Trim().Should().StartWith("acme job ");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        (await _harness.PorcelainAsync(_harness.Bus.SenderDir, ct)).Should().BeEmpty();
        _harness.Hooks.Calls.Count(c => HookedProcessRunner.SubCommand(c) == "push").Should().Be(4);
    }

    [Fact]
    public async Task Submit_ConfigurationErrorWinsOverArgumentError()
    {
        // Arrange: a bad --to and a node.json without signWith
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = _harness.WriteNodeJson(CliHarness.Central, _harness.Bus.SenderDir, "central-nosign2", omitSignWith: true);

        // Act
        var (exit, console) = await _harness.RunAsync(["submit", "--to", "Home Laptop", "--project", _project.Path, Body], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(3);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"^submitted (?<id>[0-9A-HJKMNP-TV-Z]{26}) to (?<to>[a-z0-9-]+) \((?<path>\S+)\)")]
    private static partial Regex SubmittedLine();
}
