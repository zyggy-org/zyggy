using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Cli;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Registry;

/// <summary>Plan 14 Step 4 (AC-21, dry-run half): <c>zyggy discover --dry-run</c> prints the registry document and writes nothing.</summary>
public sealed class DiscoverCommandTests : IAsyncLifetime
{
    private const string Summary = "Multi-tenant booking SaaS, .NET/Blazor";

    private CliHarness _harness = null!;
    private string _root = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _root = _harness.Scratch.File("projects");
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private async Task SeedTwoProjectsAsync(CancellationToken ct)
    {
        await ScratchProject.CreateInAsync(_harness.Bus, _root, "calizr", ct, agent: "env-debugger", agentDescription: "Diagnoses failing environments", claudeMd: "# Calizr\n" + Summary + "\n");
        await ScratchProject.CreateInAsync(_harness.Bus, _root, "client-portal", ct);
    }

    private string NodeJsonFor(IReadOnlyList<string> roots, string? discoveryJson = null) =>
        _harness.WriteNodeJson(CliHarness.HomeLaptop, _harness.Bus.CloneDir, "home-laptop-discover", devRoots: roots, discoveryJson: discoveryJson);

    [Fact]
    public async Task DryRun_TwoProjects_DocumentOnStdoutNothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        string head = await _harness.Bus.HeadShaAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["discover", "--dry-run"], NodeJsonFor([_root]), ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().BeEmpty();
        RegistryParse parse = RegistryDocument.Parse(Encoding.UTF8.GetBytes(console.Stdout));
        parse.IsOk.Should().BeTrue(parse.Detail);
        RegistryDocument document = parse.Document!;
        document.Tenant.Should().Be(CliHarness.Acme);
        document.Machine.Should().Be(CliHarness.HomeLaptop);
        document.Os.Should().Be(RegistryOsExtensions.Current());
        document.Health.Should().BeNull();
        document.Projects.Select(p => p.Name).Should().Equal("calizr", "client-portal");
        RegistryProject calizr = document.Projects[0];
        calizr.Path.Should().Be(Path.Combine(_root, "calizr").Replace('\\', '/'));
        calizr.DefaultBranch.Should().Be("main");
        calizr.Agents.Should().Equal(new RegistryEntry("env-debugger", "Diagnoses failing environments"));
        calizr.Summary.Should().Be(Summary);
        document.Projects[1].Agents.Should().BeEmpty();
        document.Projects[1].Summary.Should().BeNull();
        (await _harness.Bus.HeadShaAsync(ct)).Should().Be(head);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        File.Exists(BusPaths.Resolve(_harness.Bus.CloneDir, BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop))).Should().BeFalse();
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
    }

    [Fact]
    public async Task DryRun_DuplicateName_StderrNoticeBothPaths()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        string second = _harness.Scratch.File("more");
        Directory.CreateDirectory(second);
        await ScratchProject.CreateInAsync(_harness.Bus, second, "Calizr", ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["discover", "--dry-run"], NodeJsonFor([_root, second]), ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        RegistryDocument document = RegistryDocument.Parse(Encoding.UTF8.GetBytes(console.Stdout)).Document!;
        document.Projects.Select(p => p.Name).Should().Equal("client-portal");
        string expected = string.Join("; ", new[] { Path.Combine(_root, "calizr"), Path.Combine(second, "Calizr") }
            .Select(p => p.Replace('\\', '/')).Order(StringComparer.Ordinal));
        console.Stderr.Should().Be($"discover: duplicate name calizr: {expected}\n");
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
    }

    [Fact]
    public async Task DryRun_NoSecretPatterns_NamesOnlyAndStderrNote()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["discover", "--dry-run"], NodeJsonFor([_root]), withPatterns: false, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        RegistryDocument document = RegistryDocument.Parse(Encoding.UTF8.GetBytes(console.Stdout)).Document!;
        document.Projects.Should().OnlyContain(p => p.Path == null && p.Summary == null && p.Agents.Count == 0 && p.Skills.Count == 0);
        document.Projects.Select(p => p.DefaultBranch).Should().AllBe("main");
        console.Stderr.Should().StartWith("zyggy: discover: secret patterns not registered (").And.EndWith("); publishing names only\n");
        console.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        SecretSweep.AssertAbsent([console.Stdout, console.Stderr]);
    }

    [Fact]
    public async Task DryRun_BadConfig_ExitThree()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["discover", "--dry-run"], NodeJsonFor([_root], "{ \"detail\": \"all\" }"), ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("zyggy: configuration error: discovery.detail: must be full or names\n");
    }

    // Plan 14 Step 6 (AC-21, AC-29 CLI half): publishing.
    private static Dictionary<string, string?> NoNode()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new() { ["ZYGGY_HEALTH_URL"] = $"http://127.0.0.1:{port}/health" };
    }

    private async Task<int> RegistryCommitsAsync(CancellationToken ct) =>
        (await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["log", "--format=%s", "main"], ct))
            .Split('\n').Count(s => s == "acme registry home-laptop");

    private async Task<RegistryDocument> OnMainAsync(CancellationToken ct) =>
        RegistryDocument.Parse(await _harness.Bus.ShowBytesAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), ct)).Document!;

    [Fact]
    public async Task Publish_FirstThenSecondRun_OneCommitThenUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        string nodeJson = NodeJsonFor([_root]);

        // Act
        var (firstExit, first) = await _harness.RunAsync(["discover"], nodeJson, extraEnvironment: NoNode(), ct: ct);
        var (secondExit, second) = await _harness.RunAsync(["discover"], nodeJson, extraEnvironment: NoNode(), ct: ct);

        // Assert
        firstExit.Should().Be(0, first.Stderr);
        first.Stdout.Should().Be("registry: 2 project(s), published\n");
        secondExit.Should().Be(0, second.Stderr);
        second.Stdout.Should().Be("registry: 2 project(s), unchanged\n");
        (await RegistryCommitsAsync(ct)).Should().Be(1);
        (await _harness.Bus.LastCommitSubjectAsync(ct)).Should().Be("acme registry home-laptop");
        (await OnMainAsync(ct)).Projects.Select(p => p.Name).Should().Equal("calizr", "client-portal");
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        SecretSweep.AssertAbsent([first.Stdout, first.Stderr, second.Stdout, second.Stderr]);
        SecretSweep.AssertAbsent(_harness.Hooks.Calls.SelectMany(SecretSweep.TextsOf));
    }

    [Fact]
    public async Task Publish_DryRunAfterAddingProject_NothingWritten()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        string nodeJson = NodeJsonFor([_root]);
        (await _harness.RunAsync(["discover"], nodeJson, extraEnvironment: NoNode(), ct: ct)).Exit.Should().Be(0);
        string head = await _harness.Bus.HeadShaAsync(ct);
        await ScratchProject.CreateInAsync(_harness.Bus, _root, "billing", ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["discover", "--dry-run"], nodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        RegistryDocument.Parse(Encoding.UTF8.GetBytes(console.Stdout)).Document!.Projects.Should().HaveCount(3);
        (await _harness.Bus.HeadShaAsync(ct)).Should().Be(head);
    }

    [Fact]
    public async Task Publish_NodeAnswersGreen_HealthGreen()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        string version = Zyggy.Cli.Commands.BusHost.InformationalVersion();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string body = Zyggy.Core.Health.NodeHealthJson.Serialize(new Zyggy.Core.Health.NodeHealth(
            version, "acme", "home-laptop", now, "\"etag\"", now, "0123456789abcdef", 0, 0, null, 4000, null, true, null));
        await using var stub = new LoopbackHealthStub(body);

        // Act
        var (exit, console) = await _harness.RunAsync(["discover"], NodeJsonFor([_root]), extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HEALTH_URL"] = stub.Url }, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await OnMainAsync(ct)).Health.Should().Be(Zyggy.Core.Health.HealthLevel.Green);
    }

    [Fact]
    public async Task Publish_NoNodeAnswering_HealthOmittedOnFirstPublish()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["discover"], NodeJsonFor([_root]), extraEnvironment: NoNode(), ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        Encoding.UTF8.GetString(await _harness.Bus.ShowBytesAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), ct))
            .Should().NotContain("health:");
    }

    [Fact]
    public async Task Publish_NonLoopbackHealthUrl_ExitThree()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["discover"], NodeJsonFor([_root]), extraEnvironment: new Dictionary<string, string?> { ["ZYGGY_HEALTH_URL"] = "http://example.invalid:4711/health" }, ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: health url must be loopback\n");
    }

    [Fact]
    public async Task Publish_EveryPushFails_ExitOneCommitLocal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        _harness.Hooks.Override = spec => HookedProcessRunner.SubCommand(spec) == "push"
            ? new Zyggy.Core.Processes.ProcessResult(1, string.Empty, "! [rejected] https://user:tok@host.invalid/acme-bus.git main -> main (fetch first)", false, false, false, TimeSpan.Zero)
            : null;

        // Act
        var (exit, console) = await _harness.RunAsync(["discover"], NodeJsonFor([_root]), extraEnvironment: NoNode(), ct: ct);

        // Assert
        exit.Should().Be(1);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().StartWith("zyggy: registry: push failed: ").And.NotContain("tok@").And.Contain("://<redacted>@");
        (await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["rev-list", "--count", "origin/main..HEAD"], ct)).Trim().Should().Be("1");
    }

    [Fact]
    public async Task Publish_CheckoutLockHeld_ExitOneBusy()
    {
        // Arrange: the CLI runs on a fake clock the test advances while another process holds the write lock
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        var clock = new TimerWatchingClock(DateTimeOffset.UtcNow);
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_harness.Bus.CloneDir, BusCheckoutLock.WriteLockName, TimeSpan.Zero, TimeProvider.System, ct);
        other.Should().NotBeNull();

        // Act
        Task<(int Exit, VerbConsole Console)> run = _harness.RunAsync(
            ["discover"], NodeJsonFor([_root]), extraEnvironment: NoNode(),
            more: s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<TimeProvider>(s, clock), ct: ct);
        // Hold the clock still until the CLI has finished the scan and sits in the lock wait (its first timer on this clock), so a slow
        // machine cannot push the scan past the slow-scan threshold and add a notice to stderr.
        await clock.FirstTimer.WaitAsync(ct);
        while (!run.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(run, Task.Delay(20, ct));
        }

        var (exit, console) = await run;

        // Assert
        exit.Should().Be(1);
        console.Stderr.Should().Be("zyggy: registry: checkout busy\n");
        (await RegistryCommitsAsync(ct)).Should().Be(0);
    }

    private sealed class TimerWatchingClock(DateTimeOffset start) : Microsoft.Extensions.Time.Testing.FakeTimeProvider(start)
    {
        private readonly TaskCompletionSource _firstTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstTimer => _firstTimer.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            _firstTimer.TrySetResult();
            return timer;
        }
    }

    [Fact]
    public async Task Publish_BadConfig_ExitThree()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["discover"], NodeJsonFor([_root], "{ \"intervalMinutes\": 0 }"), ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("zyggy: configuration error: discovery.intervalMinutes: must be an integer from 1 to 1440\n");
    }

    [Fact]
    public async Task DryRun_MissingConfig_ExitThree()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["discover", "--dry-run"], _harness.Scratch.File("absent/node.json"), ct: ct);

        // Assert
        exit.Should().Be(3);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().StartWith("zyggy: configuration error: ");
    }
}
