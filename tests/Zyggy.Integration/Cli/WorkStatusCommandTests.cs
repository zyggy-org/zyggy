using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 18 slice 2 through the real verb, real git and two checkouts of one bare repository: a session's hooks publish, replace and
/// remove its status (AC-4), <c>zyggy work</c> on Central's checkout shows the session and the jobs (AC-5), and <c>zyggy verify</c>
/// judges the status folder (AC-9).
/// </summary>
public sealed class WorkStatusCommandTests : IAsyncLifetime
{
    private const string Session = "0b7c3d1e";

    private static readonly DateTimeOffset T = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(T);

    private CliHarness _harness = null!;

    private string _project = null!;

    private static string StatusFolder => BusPaths.Status(CliHarness.Acme, CliHarness.HomeLaptop);

    private string StateFile => Path.Combine(_harness.NodeDirOf("home-laptop"), "work", "acme", Session + ".state");

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _harness = await CliHarness.CreateAsync(ct);
        _project = Path.Combine(_harness.Scratch.File("projects"), "Calizr");
        Directory.CreateDirectory(Path.Combine(_project, "_specs"));
        await File.WriteAllTextAsync(Path.Combine(_project, "_specs", "12-booking-pool.md"), "# Spec\n", ct);
        await _harness.Bus.RunGitAsync(_project, ["init", "-b", "feature/12-booking-pool"], ct);
        await _harness.Bus.RunGitAsync(_project, ["add", "-A"], ct);
        await _harness.Bus.RunGitAsync(_project, ["-c", "user.name=zyggy test", "-c", "user.email=test@zyggy.org", "commit", "-m", "seed"], ct);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-4
    [Fact]
    public async Task SessionStart_OneStatusCommitOnTheBus_VerifiesAndCarriesTheSessionFacts_NothingPrinted()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await HookAsync("--session-start", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
        string path = (await StatusFilesOnMainAsync(ct)).Should().ContainSingle().Subject;
        Model status = await _harness.ParseOnMainAsync(path, ct);
        status.Header.From.Should().Be(CliHarness.HomeLaptop);
        status.Header.To.Should().Be(CliHarness.Central);
        status.Status!.Project.Should().Be("calizr");
        status.Status.Session.Should().Be(Session);
        status.Status.Since.Should().Be(T);
        status.Status.Activity.Should().Be(T);
        status.Status.Branch.Should().Be("feature/12-booking-pool");
        status.Status.Head.Should().MatchRegex("^[0-9a-f]{7}$");
        status.Status.Dirty.Should().Be(0);
        status.Status.Spec.Should().Be("_specs/12-booking-pool.md");
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().StartWith("acme status ").And.EndWith("home-laptop→central");
    }

    [Fact]
    public async Task TurnEnd_InsideStatusMinutes_NoNewCommit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await HookAsync("--session-start", ct: ct);
        int commits = await _harness.CommitCountOnMainAsync(ct);
        _clock.Advance(TimeSpan.FromMinutes(9));

        // Act
        var (exit, console) = await HookAsync("--turn-end", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task TurnEnd_AfterStatusMinutes_OneCommitReplacesTheSessionsFile()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await HookAsync("--session-start", ct: ct);
        string first = (await StatusFilesOnMainAsync(ct)).Single();
        int commits = await _harness.CommitCountOnMainAsync(ct);
        _clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        var (exit, console) = await HookAsync("--turn-end", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits + 1);
        string second = (await StatusFilesOnMainAsync(ct)).Should().ContainSingle().Subject;
        second.Should().NotBe(first);
        Model status = await _harness.ParseOnMainAsync(second, ct);
        status.Status!.Since.Should().Be(T);
        status.Status.Activity.Should().Be(T.AddMinutes(10));
    }

    [Fact]
    public async Task SessionEnd_StatusFileGoneFromTheBus_LocalStateRemoved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await HookAsync("--session-start", ct: ct);
        File.Exists(StateFile).Should().BeTrue();

        // Act
        var (exit, console) = await HookAsync("--session-end", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().BeEmpty();
        (await StatusFilesOnMainAsync(ct)).Should().BeEmpty();
        File.Exists(StateFile).Should().BeFalse();
    }

    [Fact]
    public async Task SessionStart_OwnStatusOlderThan24Hours_RemovedWithTheWrite()
    {
        // Arrange: a session of this machine that ended without saying so, 25 hours ago
        var ct = TestContext.Current.CancellationToken;
        string crashed = await SeedStatusAsync(CliHarness.HomeLaptop, CliHarness.HomeLaptop, "dead0000", T.AddHours(-25), ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        await HookAsync("--session-start", ct: ct);

        // Assert
        (await StatusFilesOnMainAsync(ct)).Should().ContainSingle().Which.Should().NotBe(crashed);
    }

    [Fact]
    public async Task Hooks_BusUnreachable_ExitZeroOneLineOnce_NextTurnEndWritesWhenItIsBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string away = _harness.Bus.BareDir + ".away";
        Directory.Move(_harness.Bus.BareDir, away);

        // Act
        var (startExit, start) = await HookAsync("--session-start", ct: ct);
        var (againExit, again) = await HookAsync("--turn-end", ct: ct);
        Directory.Move(away, _harness.Bus.BareDir);
        var (backExit, back) = await HookAsync("--turn-end", ct: ct);

        // Assert
        (startExit, againExit, backExit).Should().Be((0, 0, 0));
        start.Stdout.Should().BeEmpty();
        start.Stderr.Trim().Split('\n').Should().ContainSingle().Which.Should().StartWith("work: status not published");
        again.Stderr.Should().BeEmpty("a failing streak is reported once");
        back.Stderr.Should().BeEmpty();
        (await StatusFilesOnMainAsync(ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task SessionStart_HooksOff_NothingWrittenAnywhere()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        var (exit, console) = await HookAsync("--session-start", env: new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" }, ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stderr.Should().BeEmpty();
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        File.Exists(StateFile).Should().BeFalse();
    }

    [Fact]
    public async Task SessionStart_DetailFixed_FourFieldsOnTheBus_NoGitCallInTheProject()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string nodeJson = WriteNodeJson(CliHarness.HomeLaptop, _harness.Bus.CloneDir, "home-laptop", "\"supervision\": { \"detail\": \"fixed\" }");

        // Act
        var (exit, console) = await HookAsync("--session-start", nodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Model status = await _harness.ParseOnMainAsync((await StatusFilesOnMainAsync(ct)).Single(), ct);
        status.Status.Should().Be(new StatusFields("calizr", Session, T, T));
        _harness.Hooks.Calls.Should().NotContain(c => PathEquals(c.WorkingDirectory, _project));
    }

    [Fact]
    public async Task SessionStart_UnparseableInput_ExitZeroOneLine_NothingWritten()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        int commits = await _harness.CommitCountOnMainAsync(ct);
        var (exit, console) = await _harness.RunAsync(["work", "--session-start"], _harness.HomeLaptopNodeJson, stdin: "not json", more: s => s.AddSingleton<TimeProvider>(_clock), ct: ct);

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("work: unparseable hook input\n");
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
    }

    [Fact]
    public async Task Step_Stated_CarriedByTheNextStatus()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await HookAsync("--session-start", ct: ct);
        _clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--step", "slice 2", "--session", HookJson.SessionId], _harness.HomeLaptopNodeJson, more: s => s.AddSingleton<TimeProvider>(_clock), ct: ct);
        await HookAsync("--turn-end", ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Model status = await _harness.ParseOnMainAsync((await StatusFilesOnMainAsync(ct)).Single(), ct);
        status.Status!.Step.Should().Be("slice 2");
    }

    [Fact]
    public async Task Step_BadLabel_ExitTwo_NothingRecorded()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["work", "--step", "Slice_2", "--session", HookJson.SessionId], _harness.HomeLaptopNodeJson, more: s => s.AddSingleton<TimeProvider>(_clock), ct: ct);

        // Assert
        exit.Should().Be(2);
        console.Stderr.Should().StartWith("zyggy: step:");
        File.Exists(StateFile).Should().BeFalse();
    }

    // AC-5
    [Theory]
    [InlineData(9, "working")]
    [InlineData(10, "idle")]
    [InlineData(59, "idle")]
    [InlineData(60, "silent")]
    public async Task Work_OnCentral_ShowsTheLaptopSessionByItsAge(int minutesLater, string state)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await HookAsync("--session-start", ct: ct);
        await PullCentralAsync(ct);
        _clock.Advance(TimeSpan.FromMinutes(minutesLater));

        // Act
        var (exit, console) = await WorkAsync(_harness.CentralNodeJson, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Contain("home-laptop\n");
        string line = console.Stdout.Split('\n').Should().ContainSingle(l => l.Contains("session " + Session, StringComparison.Ordinal)).Subject;
        line.Should().Contain("calizr").And.Contain("  " + state + "  ").And.Contain("feature/12-booking-pool @ ").And.Contain("_specs/12-booking-pool.md");
    }

    [Fact]
    public async Task Work_ClaimedJobQueuedJobAndReport_ShownWithAgeStatusAndReason()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model claimed = await TestEnvelopes.SignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, _harness.Store, TestKeys.Acme1, "probe\n", ct, id: EnvelopeId.New(T.AddMinutes(-12)));
        Model queued = await TestEnvelopes.SignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, _harness.Store, TestKeys.Acme1, "probe\n", ct, id: EnvelopeId.New(T.AddMinutes(-3)));
        var reportId = EnvelopeId.New(T.AddMinutes(-5));
        byte[] report = await TestEnvelopes.SignedReportBytesAsync(CliHarness.HomeLaptop, CliHarness.Central, EnvelopeId.New(T.AddMinutes(-30)), _harness.Store, TestKeys.Acme1, "ok\n", ct, id: reportId, created: T.AddMinutes(-5));
        await _harness.Bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Claimed(CliHarness.Acme, CliHarness.HomeLaptop), claimed.Header.Id), EnvelopeWriter.Serialize(claimed), "seed: claimed", ct);
        await _harness.Bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), queued.Header.Id), EnvelopeWriter.Serialize(queued), "seed: queued", ct);
        await _harness.Bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Reports(CliHarness.Acme, CliHarness.HomeLaptop), reportId), report, "seed: report", ct);

        // Act
        var (exit, console) = await WorkAsync(_harness.CentralNodeJson, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.Split('\n');
        lines.Should().ContainSingle(l => l.Contains("job " + claimed.Header.Id.Value, StringComparison.Ordinal)).Which.Should().Contain("claimed").And.Contain("12 min ago");
        lines.Should().ContainSingle(l => l.Contains("job " + queued.Header.Id.Value, StringComparison.Ordinal)).Which.Should().Contain("queued").And.Contain("3 min ago");
        lines.Should().ContainSingle(l => l.Contains("last report " + reportId.Value, StringComparison.Ordinal)).Which.Should().Contain("done").And.Contain("5 min ago");
    }

    [Fact]
    public async Task Work_StatusWithABrokenSignatureOrInAnotherMachinesFolder_NotShownButCounted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedStatusAsync(CliHarness.HomeLaptop, CliHarness.HomeLaptop, "aaaa1111", T, ct, tamper: true);
        await SeedStatusAsync(CliHarness.HomeLaptop, CliHarness.Central, "bbbb2222", T, ct);
        await SeedStatusAsync(CliHarness.HomeLaptop, CliHarness.HomeLaptop, "cccc3333", T, ct);

        // Act
        var (exit, console) = await WorkAsync(_harness.CentralNodeJson, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Contain("session cccc3333").And.NotContain("aaaa1111").And.NotContain("bbbb2222");
        console.Stdout.Should().Contain("2 status file(s) not shown");
    }

    [Fact]
    public async Task Work_MachineInWorkMachines_OnlyTheFourFixedFieldsShown()
    {
        // Arrange: a full status in work-laptop's folder; Central lists work-laptop as a work machine
        var ct = TestContext.Current.CancellationToken;
        await _harness.Bus.SeedTenantAsync(CliHarness.Acme, [CliHarness.Central, CliHarness.HomeLaptop, CliHarness.WorkLaptop], ct);
        await SeedStatusAsync(CliHarness.WorkLaptop, CliHarness.WorkLaptop, "dddd4444", T, ct);
        // Central names the folders a work machine is answered from, or it does not start (spec 18 slice 11).
        string outbound = _harness.Scratch.File("outbound").Replace('\\', '/');
        string nodeJson = WriteNodeJson(CliHarness.Central, _harness.Bus.SenderDir, "central", "\"supervision\": { \"workMachines\": [\"work-laptop\"], \"outboundPaths\": [\"" + outbound + "\"] }");

        // Act
        var (exit, console) = await WorkAsync(nodeJson, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string line = console.Stdout.Split('\n').Should().ContainSingle(l => l.Contains("session dddd4444", StringComparison.Ordinal)).Subject;
        line.Should().Contain("calizr").And.NotContain("booking-pool").And.NotContain("9f2c1ab").And.NotContain("slice 2");
    }

    [Fact]
    public async Task Work_NothingLive_SaysSo()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await WorkAsync(_harness.CentralNodeJson, ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("work as of 2026-11-15T10:00:00Z\nno live sessions or jobs\n");
    }

    // AC-9
    [Fact]
    public async Task Verify_GoodStatusPasses_StatusInAnotherMachinesFolderIsReported()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string good = await SeedStatusAsync(CliHarness.HomeLaptop, CliHarness.HomeLaptop, "cccc3333", T, ct);
        string foreign = await SeedStatusAsync(CliHarness.HomeLaptop, CliHarness.Central, "bbbb2222", T, ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (_, console) = await _harness.RunAsync(["verify", "--verbose"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        string[] lines = console.Stdout.Split('\n');
        lines.Should().Contain(l => l.StartsWith("ok", StringComparison.OrdinalIgnoreCase) && l.Contains(good, StringComparison.Ordinal));
        lines.Should().ContainSingle(l => l.Contains(foreign, StringComparison.Ordinal)).Which.Should().StartWith("FAIL").And.Contain("writer_mismatch");
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private Task<(int Exit, VerbConsole Console)> HookAsync(string mode, string? nodeJson = null, IReadOnlyDictionary<string, string?>? env = null, CancellationToken ct = default) =>
        _harness.RunAsync(["work", mode], nodeJson ?? _harness.HomeLaptopNodeJson, stdin: HookJson.Stop(null, cwd: _project), extraEnvironment: env, more: s => s.AddSingleton<TimeProvider>(_clock), ct: ct);

    private async Task<(int Exit, VerbConsole Console)> WorkAsync(string nodeJson, CancellationToken ct)
    {
        await PullCentralAsync(ct);
        return await _harness.RunAsync(["work"], nodeJson, more: s => s.AddSingleton<TimeProvider>(_clock), ct: ct);
    }

    private Task<string> PullCentralAsync(CancellationToken ct) => _harness.Bus.RunGitAsync(_harness.Bus.SenderDir, ["pull", "--rebase", "origin", "main"], ct);

    private async Task<IReadOnlyList<string>> StatusFilesOnMainAsync(CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", StatusFolder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Pushes a signed full status sent by <paramref name="from"/> into <paramref name="folder"/>'s status folder; returns its path.</summary>
    private async Task<string> SeedStatusAsync(MachineName folder, MachineName from, string session, DateTimeOffset at, CancellationToken ct, bool tamper = false)
    {
        var id = EnvelopeId.New(at);
        Model status = Model.CreateStatus(
            new EnvelopeHeader(CliHarness.Acme, id, from, CliHarness.Central, at),
            new StatusFields("calizr", session, at.AddHours(-1), at, "feature/12-booking-pool", "9f2c1ab", 3, "_specs/12-booking-pool.md", "slice 2"));
        EnvelopeResult signed = await new EnvelopeSigner(_harness.Store).SignAsync(status, TestKeys.Acme1, ct);
        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        if (tamper)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("dirty: 3", "dirty: 4", StringComparison.Ordinal));
        }

        string path = BusPaths.EnvelopeFile(BusPaths.Status(CliHarness.Acme, folder), id);
        await _harness.Bus.SeedRawFileAsync(path, bytes, "seed: status " + id.Value, ct);
        return path;
    }

    private string WriteNodeJson(MachineName machine, string busLocalPath, string name, string extraTopLevelJson) =>
        NodeJson.Write(
            _harness.Scratch.File("config-work-" + name),
            CliHarness.Acme,
            machine,
            TestKeys.Acme1,
            busLocalPath: busLocalPath,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: _harness.NodeDirOf(name),
            extraTopLevelJson: extraTopLevelJson);
}
