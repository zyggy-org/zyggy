using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Node;

/// <summary>Plan 14 Step 8 (AC-19, AC-20, AC-27, AC-28): the production host publishes its registry at start and on change.</summary>
public sealed class NodeHostRegistryTests : IAsyncLifetime
{
    private const string Description = "Diagnoses failing environments";

    private NodeHostHarness _harness = null!;

    public async ValueTask InitializeAsync() => _harness = await NodeHostHarness.CreateAsync(CancellationToken.None);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    private async Task<int> RegistryCommitsAsync(string machine, CancellationToken ct) =>
        (await _harness.CommitSubjectsAsync(ct)).Count(s => s == "acme registry " + machine);

    private async Task SeedTwoProjectsAsync(CancellationToken ct)
    {
        await _harness.ProjectAsync(ct, "calizr", agent: "env-debugger", agentDescription: Description);
        await _harness.ProjectAsync(ct, "client-portal");
    }

    [Fact]
    public async Task Start_TwoProjects_OneRegistryCommitAndLogLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        DateTimeOffset started = DateTimeOffset.UtcNow.AddSeconds(-1);

        // Act
        int exit = await _harness.RunUntilAsync(
            async () => await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct) is not null
                && _harness.LogLines.Any(l => l.Contains("registry published", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(60),
            ct);

        // Assert
        exit.Should().Be(0);
        (await RegistryCommitsAsync("home-laptop", ct)).Should().Be(1);
        RegistryDocument document = (await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct))!;
        document.Tenant.Should().Be(NodeHostHarness.Acme);
        document.Machine.Should().Be(NodeHostHarness.HomeLaptop);
        document.Os.Should().Be(RegistryOsExtensions.Current());
        document.Projects.Select(p => p.Name).Should().Equal("calizr", "client-portal");
        document.Projects.Should().OnlyContain(p => p.Path != null && p.DefaultBranch == "main");
        document.Projects[0].Agents.Should().Equal(new RegistryEntry("env-debugger", Description));
        document.LastSeen.Should().BeOnOrAfter(started.AddSeconds(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
        document.Health.Should().NotBeNull().And.NotBe(HealthLevel.Red, "the first publish waits for the first poll (A2)");
        _harness.LogLines.Where(l => l.Contains("registry published: 2 project(s)", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain(" discover ");
        _harness.LogLines.Should().NotContain(l => l.Contains(Description, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_harness.LogLines);
    }

    [Fact]
    public async Task Rescan_UnchangedThenAddedProject_ExactlyTwoRegistryCommits()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        _harness.DiscoveryInterval = TimeSpan.FromSeconds(2);
        bool added = false;

        // Act
        int exit = await _harness.RunUntilAsync(
            async () =>
            {
                RegistryDocument? document = await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct);
                if (document is null)
                {
                    return false;
                }

                if (!added)
                {
                    if (_harness.LogLines.Count(l => l.Contains("registry unchanged", StringComparison.Ordinal)) < 2)
                    {
                        return false;
                    }

                    added = true;
                    await _harness.ProjectAsync(ct, "billing");
                    return false;
                }

                // The commit reaches main a moment before the publisher logs it: wait for both, so the host is not stopped in between.
                return document.Projects.Count == 3
                    && _harness.LogLines.Count(l => l.Contains("registry published", StringComparison.Ordinal)) == 2;
            },
            TimeSpan.FromSeconds(90),
            ct);

        // Assert
        exit.Should().Be(0);
        (await RegistryCommitsAsync("home-laptop", ct)).Should().Be(2);
        (await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct))!.Projects.Select(p => p.Name).Should().Equal("billing", "calizr", "client-portal");
        _harness.LogLines.Count(l => l.Contains("[INF]", StringComparison.Ordinal) && l.Contains("registry published", StringComparison.Ordinal)).Should().Be(2);
        SecretSweep.AssertAbsent(_harness.LogLines);
    }

    [Fact]
    public async Task Start_JobWaitingInInbox_ScannedBeforeClaim()
    {
        // Arrange: a job naming calizr by registry name is waiting before the host starts
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        await _harness.Bus.SeedSignedJobAsync(NodeHostHarness.Central, NodeHostHarness.HomeLaptop, TestKeys.Acme1, _harness.Store,
            "Read README.md; change nothing.\n", fields: new JobFields("calizr", TimeoutMinutes: 2, AllowedTools: ["Read"]), ct: ct);

        // Act
        int exit = await _harness.RunUntilAsync(
            () => Task.FromResult(_harness.LogLines.Any(l => l.Contains("claimed job ", StringComparison.Ordinal))),
            TimeSpan.FromSeconds(60),
            ct);

        // Assert
        exit.Should().Be(0);
        IReadOnlyList<string> lines = _harness.LogLines;
        int scanned = lines.ToList().FindIndex(l => l.Contains("discover: scanned 2 projects", StringComparison.Ordinal));
        int claimed = lines.ToList().FindIndex(l => l.Contains("claimed job ", StringComparison.Ordinal));
        scanned.Should().BeGreaterThanOrEqualTo(0);
        claimed.Should().BeGreaterThan(scanned, "the first scan completes before the listener admits any job (A1)");
    }

    [Fact]
    public async Task ForeignRegistryCommits_NodeProcessesNothingWritesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        string workLaptopFile = BusPaths.Registry(NodeHostHarness.Acme, MachineName.Parse("work-laptop"));
        const string OtherTenantFile = "tenants/other-org/registry/x.yaml";
        byte[] foreign = Encoding.UTF8.GetBytes("machine: work-laptop\nos: windows\nprojects: []\nschema: 1\ntenant: acme\n");
        bool seeded = false;
        int commitsAfterSeed = 0;
        int linesAfterSeed = 0;
        DateTime seededAt = DateTime.MaxValue;

        // Act
        int exit = await _harness.RunUntilAsync(
            async () =>
            {
                if (!seeded)
                {
                    if (await _harness.RegistryOnMainAsync(NodeHostHarness.HomeLaptop, ct) is null)
                    {
                        return false;
                    }

                    await _harness.Bus.SeedRawFileAsync(workLaptopFile, foreign, "acme registry work-laptop", ct);
                    await _harness.Bus.SeedRawFileAsync(OtherTenantFile, foreign, "other-org registry x", ct);
                    seeded = true;
                    seededAt = DateTime.UtcNow;
                    commitsAfterSeed = (await _harness.CommitSubjectsAsync(ct)).Count;
                    linesAfterSeed = _harness.LogLines.Count(l => l.Contains("[INF]", StringComparison.Ordinal));
                    return false;
                }

                return DateTime.UtcNow - seededAt > TimeSpan.FromSeconds(5);
            },
            TimeSpan.FromSeconds(60),
            ct);

        // Assert
        exit.Should().Be(0);
        (await _harness.CommitSubjectsAsync(ct)).Count.Should().Be(commitsAfterSeed, "the node commits nothing in reaction to foreign registry files");
        (await _harness.Bus.ShowBytesAsync(workLaptopFile, ct)).Should().Equal(foreign);
        (await _harness.Bus.ShowBytesAsync(OtherTenantFile, ct)).Should().Equal(foreign);
        _harness.LogLines.Count(l => l.Contains("[INF]", StringComparison.Ordinal) && l.Contains("registry", StringComparison.Ordinal))
            .Should().Be(1, "only the node's own publish");
        _harness.LogLines.Skip(0).Count(l => l.Contains("[INF]", StringComparison.Ordinal)).Should().BeLessThanOrEqualTo(linesAfterSeed + 4, "only the doorbell's once-per-change lines may follow");
        _harness.Hooks.Calls.SelectMany(c => c.Arguments).Should().NotContain(a => a.Contains("other-org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Central_NoDevRoots_PublishesEmptyProjects()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var memory = new PickupMemory();
        _harness.WriteNodeJson(machine: NodeHostHarness.Central, devRoots: []);
        _harness.Environment = memory.Env();

        // Act
        int exit = await _harness.RunUntilAsync(async () => await _harness.RegistryOnMainAsync(NodeHostHarness.Central, ct) is not null, TimeSpan.FromSeconds(60), ct);

        // Assert
        exit.Should().Be(0);
        (await RegistryCommitsAsync("central", ct)).Should().Be(1);
        RegistryDocument document = (await _harness.RegistryOnMainAsync(NodeHostHarness.Central, ct))!;
        document.Projects.Should().BeEmpty();
        document.WorkHours.Should().BeNull();
        document.Health.Should().NotBeNull();
    }

    [Fact]
    public async Task StartupRefused_NoRegistryCommit()
    {
        // Arrange: the checkout is on the wrong branch
        var ct = TestContext.Current.CancellationToken;
        await SeedTwoProjectsAsync(ct);
        await _harness.Bus.RunGitAsync(_harness.Bus.CloneDir, ["checkout", "-q", "-b", "elsewhere"], ct);

        // Act
        int exit = await _harness.RunAsync(TimeSpan.FromSeconds(60), ct);

        // Assert
        exit.Should().Be(6);
        (await RegistryCommitsAsync("home-laptop", ct)).Should().Be(0);
        _harness.LogLines.Should().Contain(l => l.Contains("discover: scanned 2 projects", StringComparison.Ordinal), "the first scan ran and the host did not hang");
    }
}
