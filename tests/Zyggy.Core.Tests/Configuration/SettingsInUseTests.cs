using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Configuration;

/// <summary>
/// Spec 55 slice 5: a running node applies a changed <c>node.json</c> — the five keys replaced as one unit within the check period
/// (AC-11), a refused file or another key changed (AC-12), and the scan and the job pre-flight using the values in use (AC-12b).
/// </summary>
public sealed class SettingsInUseTests : IDisposable
{
    private static readonly string[] HomeAndTmp = ["home", "tmp*"];
    private static readonly LogLevel[] WarningThenInformation = [LogLevel.Warning, LogLevel.Information];
    private static readonly string[] RestartWarning = ["settings: claude.maxTurns, maxConcurrentJobs changed and need a restart of the node; they keep their old value"];

    private readonly SettingsTree _tree = new();
    private readonly RecordingLogger<SettingsInUse> _log = new();
    private NodeConfiguration? _started;
    private int _writes;

    public void Dispose() => _tree.Dispose();

    // AC-11
    [Fact]
    public async Task RunAsync_ValidChange_FiveNewValuesInUseWithinTheCheckPeriod()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        Task run = settings.RunAsync(stop.Token);
        Rewrite(_tree.Write(extra: $$"""
            "devRoots": ["{{SettingsTree.J(_tree.Parent)}}", "{{SettingsTree.J(_tree.Other)}}"],
              "discovery": { "exclude": ["home", "tmp*"] },
              "supervision": { "project": "calizr", "workMachines": ["work-laptop"], "outboundPaths": ["{{SettingsTree.J(_tree.Business)}}"] }
            """));

        // Act
        _tree.Clock.Advance(SettingsInUse.CheckPeriod);
        await UntilAsync(() => settings.Current.Project == "calizr", ct);
        await stop.CancelAsync();
        await run;

        // Assert
        settings.Current.Should().BeEquivalentTo(new
        {
            DevRoots = new[] { _tree.Parent, _tree.Other },
            Exclude = HomeAndTmp,
            Project = "calizr",
            WorkMachines = new[] { MachineName.Parse("work-laptop") },
            OutboundPaths = new[] { _tree.Business },
        });
    }

    // AC-11: before the period has passed, the values in use stay.
    [Fact]
    public async Task RunAsync_ChangeBeforeThePeriod_OldValuesStay()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        Task run = settings.RunAsync(stop.Token);
        Rewrite(_tree.Config, File.ReadAllText(_tree.Config).Replace("\"instance\"", "\"calizr\"", StringComparison.Ordinal));

        // Act
        _tree.Clock.Advance(SettingsInUse.CheckPeriod - TimeSpan.FromSeconds(1));
        await SettleAsync(ct);
        await stop.CancelAsync();
        await run;

        // Assert
        settings.Current.Project.Should().Be("instance");
    }

    // AC-11
    [Fact]
    public async Task CheckAsync_ValidChangeThenUnchangedFile_OneInformationLineNamingTheChangedKeys()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        Rewrite(_tree.WriteCentralShaped(supervisionExtra: $$"""
            "workMachines": ["work-laptop"], "outboundPaths": ["{{SettingsTree.J(_tree.Business)}}"]
            """));

        // Act
        await settings.CheckAsync(ct);
        await settings.CheckAsync(ct);
        await settings.CheckAsync(ct);

        // Assert
        _log.Entries.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Level = LogLevel.Information, Message = "settings: applied supervision.workMachines, supervision.outboundPaths" });
    }

    // AC-11: the work-boundary keys are named, their values never logged.
    [Fact]
    public async Task CheckAsync_WorkBoundaryChange_NoValueInTheLog()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        Rewrite(_tree.WriteCentralShaped(supervisionExtra: $$"""
            "workMachines": ["work-laptop"], "outboundPaths": ["{{SettingsTree.J(_tree.Business)}}"]
            """));

        // Act
        await settings.CheckAsync(ct);

        // Assert
        string.Join("\n", _log.Messages).Should().NotContainAny("work-laptop", _tree.Business, "business");
    }

    // AC-11: a touched file with the same values logs nothing and applies nothing.
    [Fact]
    public async Task CheckAsync_FileTouchedSameValues_NothingLogged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        Rewrite(_tree.WriteCentralShaped());

        // Act
        await settings.CheckAsync(ct);

        // Assert
        _log.Entries.Should().BeEmpty();
    }

    // AC-11: a change of the roots or the excludes asks for a scan right away; a change of the project does not.
    [Theory]
    [InlineData("exclude", 1)]
    [InlineData("project", 0)]
    public async Task CheckAsync_ChangedKey_ScanRequestedOnlyForRootsAndExcludes(string change, int scans)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        int requested = 0;
        settings.ScanNeeded = _ =>
        {
            requested++;
            return Task.CompletedTask;
        };
        string text = File.ReadAllText(_tree.Config);
        Rewrite(_tree.Config, change == "exclude"
            ? text.Replace("[\"home\"]", "[\"home\", \"tmp*\"]", StringComparison.Ordinal)
            : text.Replace("\"instance\"", "\"calizr\"", StringComparison.Ordinal));

        // Act
        await settings.CheckAsync(ct);

        // Assert
        requested.Should().Be(scans);
    }

    // AC-12
    [Fact]
    public async Task CheckAsync_RefusedTwiceThenValid_ValuesStayOneWarningForTheStreakThenOneInformation()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        string valid = File.ReadAllText(_tree.Config);

        // Act
        Rewrite(_tree.Config, "{ not json");
        await settings.CheckAsync(ct);
        IReadOnlyList<string> during = settings.Current.DevRoots;
        Rewrite(_tree.Config, valid.Replace("\"acme\"", "\"Not A Tenant\"", StringComparison.Ordinal));
        await settings.CheckAsync(ct);
        Rewrite(_tree.Config, valid);
        await settings.CheckAsync(ct);

        // Assert
        new
        {
            During = during,
            Levels = _log.Entries.Select(e => e.Level).ToArray(),
            Valid = _log.Entries[^1].Message,
        }.Should().BeEquivalentTo(new
        {
            During = new[] { _tree.Parent },
            Levels = WarningThenInformation,
            Valid = "settings: node.json valid again",
        });
    }

    // AC-12: the Warning carries the reader's line.
    [Fact]
    public async Task CheckAsync_SecretLikeKey_WarningWithTheReadersLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        Rewrite(_tree.Write(extra: "\"apiToken\": \"x\""));

        // Act
        await settings.CheckAsync(ct);

        // Assert
        new { Warning = _log.AtLevel(LogLevel.Warning).Single().Message, settings.Current.Project }
            .Should().BeEquivalentTo(new { Warning = "settings: node.json refused while running, the values in use stay: " + NodeConfigurationReader.Read(_tree.Config), Project = "instance" });
    }

    // AC-12: another key changed beside a valid change of the five: the five apply, the other is named once and keeps its old value.
    [Fact]
    public async Task CheckAsync_OtherKeyChangedToo_OneWarningNamingItAndItsOldValueStays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.Write(extra: $$"""
            "claude": { "maxTurns": 40 },
              "devRoots": ["{{SettingsTree.J(_tree.Parent)}}"]
            """));
        var policy = new NodeJsonPolicySource(_started!, settings);
        Rewrite(_tree.Write(extra: $$"""
            "claude": { "maxTurns": 70 },
              "maxConcurrentJobs": 3,
              "devRoots": ["{{SettingsTree.J(_tree.Other)}}"]
            """));

        // Act
        await settings.CheckAsync(ct);
        await settings.CheckAsync(ct);
        NodePolicy inUse = await policy.GetAsync(ct);

        // Assert
        new
        {
            Warnings = _log.AtLevel(LogLevel.Warning).Select(e => e.Message).ToArray(),
            inUse.MaxTurns,
            inUse.DevRoots,
        }.Should().BeEquivalentTo(new
        {
            Warnings = RestartWarning,
            MaxTurns = 40,
            DevRoots = new[] { _tree.Other },
        });
    }

    // AC-12b: the scan after a valid change uses the new roots and excludes.
    [Fact]
    public async Task ScanAsync_AfterAValidChange_NewRootsAndExcludes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        var policy = new NodeJsonPolicySource(_started!, settings);
        DiscoveryScanner scanner = Scanner(settings);
        Rewrite(_tree.Write(extra: $$"""
            "devRoots": ["{{SettingsTree.J(_tree.Parent)}}", "{{SettingsTree.J(_tree.Other)}}"],
              "discovery": { "exclude": ["home", "instance"] }
            """));
        await settings.CheckAsync(ct);

        // Act
        ScanResult scan = await scanner.ScanAsync((await policy.GetAsync(ct)).DevRoots, DiscoveryDetail.Names, ct);

        // Assert
        scan.Projects.Select(p => p.Name).Should().Equal("calizr");
    }

    // AC-12b: a job that names a path under an added root is accepted.
    [Fact]
    public async Task RunAsync_PathUnderAnAddedRoot_Accepted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using ScratchProject project = ScratchProject.Create();
        SettingsInUse settings = Start(_tree.WriteCentralShaped());
        var harness = new JobRunnerHarness();
        Rewrite(_tree.Write(extra: $$"""
            "devRoots": ["{{SettingsTree.J(_tree.Parent)}}", "{{SettingsTree.J(ScratchProject.ParentRoot)}}"]
            """));
        await settings.CheckAsync(ct);
        var job = await JobRunnerHarness.JobAsync(new JobFields(project.Root));

        // Act
        var reply = await Runner(harness, settings).RunAsync(job, ct);

        // Assert
        reply.Status.Should().Be(ReportStatus.Done);
    }

    // AC-12b: a job that names a path under a removed root is answered outside devRoots.
    [Fact]
    public async Task RunAsync_PathUnderARemovedRoot_OutsideDevRoots()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using ScratchProject project = ScratchProject.Create();
        SettingsInUse settings = Start(_tree.Write(extra: $$"""
            "devRoots": ["{{SettingsTree.J(_tree.Parent)}}", "{{SettingsTree.J(ScratchProject.ParentRoot)}}"]
            """));
        var harness = new JobRunnerHarness();
        Rewrite(_tree.Write(extra: $$"""
            "devRoots": ["{{SettingsTree.J(_tree.Parent)}}"]
            """));
        await settings.CheckAsync(ct);
        var job = await JobRunnerHarness.JobAsync(new JobFields(project.Root));

        // Act
        var reply = await Runner(harness, settings).RunAsync(job, ct);

        // Assert
        Encoding.UTF8.GetString(reply.Body.Span).Should().Be("unknown_project: outside devRoots\n");
    }

    // AC-12b: the production registrations read the one holder; the host's watched holder replaces the seeded one.
    [Fact]
    public void AddSettingsInUse_WithTheNodeServices_PolicyScannerAndHolderShareIt()
    {
        // Arrange
        _tree.WriteCentralShaped();
        NodeConfiguration configuration = NodeConfigurationReader.Read(_tree.Config).Configuration!;
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<TimeProvider>(_tree.Clock);
        services.AddNodeJsonPolicy();
        services.AddSettingsInUse(_tree.Config);

        // Act
        using ServiceProvider provider = services.BuildServiceProvider();

        // Assert
        provider.GetRequiredService<SettingsInUse>().IsWatching.Should().BeTrue();
    }

    private SettingsInUse Start(string path)
    {
        _started = NodeConfigurationReader.Read(path).Configuration!;
        return new SettingsInUse(_started, path, _tree.Clock, _log);
    }

    // Each write gets its own last-change time, so two writes within the file system's time resolution are still seen.
    private void Rewrite(string path) => File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc).AddSeconds(++_writes));

    private void Rewrite(string path, string text)
    {
        File.WriteAllText(path, text);
        Rewrite(path);
    }

    private DiscoveryScanner Scanner(SettingsInUse settings) =>
        new(new GitClient(new RecordingProcessRunner(), new GitClientOptions(), _tree.Clock), null, new DiscoveryOptions(), _tree.Clock, new RecordingLogger<DiscoveryScanner>())
        {
            Settings = settings,
        };

    private JobRunner Runner(JobRunnerHarness h, SettingsInUse settings) =>
        new(h.Model, new NodeJsonPolicySource(_started!, settings), new GitClient(h.Processes, new GitClientOptions(), h.Clock),
            h.Processes, h.Registry, Options.Create(h.Options), h.Clock, h.Log);

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(5, ct);
        }

        condition().Should().BeTrue();
    }

    private static async Task SettleAsync(CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(5, ct);
        }
    }
}
