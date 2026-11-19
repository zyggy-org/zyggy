using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 13 AC-1, AC-3, AC-14..AC-16 and O2 through the production host: Central at <c>activeSeconds: 1</c> picks a pushed context up
/// within seconds; without the drop-in's variables it refuses to start (exit 3); a laptop never builds a pickup.
/// </summary>
public sealed class NodeHostPickupTests
{
    private const string Fact = "- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot\n";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    // The run limit of a test that pushes while the host runs: counted from the host's start, it pays the start-up (11 s seen under the
    // suite's load, up to the 20 s the tests wait for the first publish) before the push, the poll and the pickup. At 30 s it ran out
    // with no fault in the node's log; the other host tests allow 60 to 120 s.
    private static readonly TimeSpan WhileRunningLimit = TimeSpan.FromSeconds(60);

    // Spec 13 AC-1/O2: a context pushed while Central runs is picked up by the 1 s poll that sees it (production jitter), not by a slower
    // path. The safety fetch (the only other trigger while running) is set out of the run's reach, and a change it had to find would log
    // its warning; so a pickup within the run is the poll's. This replaces a 10 s wall-clock bound that failed under load; the speed
    // bound is counted in polls instead: the doorbell probes the inbox at every head request, and from the second request after the
    // push on the inbox file is there (one poll to see the push, one more when a push of the node's own had rebased onto it).
    [Fact]
    public async Task Central_ContextPushedWhileRunning_InboxFileAndArchiveOnMain_PickedUpByTheNextPoll_ProductionJitterAtOneSecond()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central, activeSeconds: 1, idleSeconds: 60, safetyFetchMinutes: 60);
        harness.Environment = memory.Env();
        var before = memory.FingerprintOutsideInbox();
        SeededJob? context = null;
        string? archived = null;
        int pushedAtRequest = 0;
        var started = DateTimeOffset.UtcNow;
        harness.Head.Probe = () => memory.InboxFiles().Count == 1;

        // Act
        int exit = await harness.RunUntilAsync(
            async () =>
            {
                if (context is null)
                {
                    if (!harness.LogLines.Any(l => l.Contains("registry published", StringComparison.Ordinal)) && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20))
                    {
                        return false;
                    }

                    context = await harness.Bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, Fact, created: Created, ct: ct);
                    pushedAtRequest = harness.Head.RequestCount;
                    archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id);
                    return false;
                }

                // the Information line follows the push: wait for it too, or the host may stop in between
                return memory.InboxFiles().Count == 1 && await harness.Bus.ExistsOnMainAsync(archived!, ct)
                    && harness.LogLines.Any(l => l.Contains("picked up 1 file(s)", StringComparison.Ordinal));
            },
            WhileRunningLimit,
            ct,
            s => s.AddSingleton(sp => sp.GetRequiredService<NodeConfiguration>().Poll.ToPollOptions()));

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        harness.LogLines.Should().NotContain(l => l.Contains("safety fetch", StringComparison.Ordinal), "the poll, not the safety fetch, saw the push");
        harness.Head.ProbedSince(pushedAtRequest).Skip(2).Should().NotContain(
            false,
            "the first poll after the push sees it (the second when a push of the node's own rebased onto it), so the inbox file is there by the poll after");
        memory.InboxFiles().Should().Equal($"bus-context-home-laptop-{context!.Id}-2026-10-29.md");
        harness.LogLines.Count(l => l.Contains("picked up 1 file(s)", StringComparison.Ordinal)).Should().Be(1);
        harness.LogLines.Should().NotContain(l => l.Contains("staging database", StringComparison.Ordinal));
        SecretSweep.AssertAbsent(harness.LogLines);
        memory.FingerprintOutsideInbox().Should().Equal(before);
    }

    [Fact]
    public async Task Central_ContextAlreadyWaitingAtStart_PickedUpAfterTheStartupPull()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        SeededJob context = await harness.Bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, Fact, created: Created, ct: ct);
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id);

        // Act
        int exit = await harness.RunUntilAsync(async () => await harness.Bus.ExistsOnMainAsync(archived, ct), TimeSpan.FromSeconds(30), ct);

        // Assert
        exit.Should().Be(0);
        memory.InboxFiles().Should().ContainSingle();
        (await harness.Bus.ExistsOnMainAsync(context.RelativePath, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Central_ReportFromHomeLaptopToCentral_PickedUpAndArchived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        var job = EnvelopeId.New(Created.AddHours(-1));
        SeededJob report = await harness.Bus.SeedSignedReportAsync(HomeLaptop, Central, job, TestKeys.Acme1, harness.Store, "Root cause is the expired staging certificate\n", created: Created, ct: ct);
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), report.Id);

        // Act
        int exit = await harness.RunUntilAsync(async () => await harness.Bus.ExistsOnMainAsync(archived, ct), TimeSpan.FromSeconds(30), ct);

        // Assert
        exit.Should().Be(0);
        memory.InboxText($"bus-report-home-laptop-{job}-{report.Id}-2026-10-29.md").Split("---\n")[2]
            .Should().Be($"- [observed] 2026-10-29 [report {job.Value[..8]}]: Root cause is the expired staging certificate\n");
    }

    [Theory]
    [InlineData("root-unset")]
    [InlineData("principal-missing")]
    [InlineData("tenant-other")]
    [InlineData("zone-unknown")]
    [InlineData("patterns-missing")]
    [InlineData("patterns-malformed")]
    public async Task Central_PickupEnvironmentMissingOrWrong_BuildThrowsConfigurationPickupDetail(string kind)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central);
        Dictionary<string, string?> env = memory.Env();
        string missing = harness.Scratch.File("absent.txt");
        string malformed = harness.Scratch.File("malformed.txt");
        File.WriteAllText(malformed, "no-tab-here\n");
        string expected;
        switch (kind)
        {
            case "root-unset":
                env.Remove("ZYGGY_MEMORY_ROOT");
                expected = "pickup: ZYGGY_MEMORY_ROOT is not set";
                break;
            case "principal-missing":
                env["ZYGGY_USER"] = "bob";
                expected = "pickup: memory directory ";
                break;
            case "tenant-other":
                env["ZYGGY_TENANT"] = "other-org";
                Directory.CreateDirectory(Path.Combine(memory.Root, "other-org", "alice"));
                expected = "pickup: ZYGGY_TENANT 'other-org' is not node.json's tenant 'acme'";
                break;
            case "zone-unknown":
                env["ZYGGY_TIMEZONE"] = "Mars/Olympus";
                expected = "pickup: ZYGGY_TIMEZONE 'Mars/Olympus' is not a known time zone";
                break;
            case "patterns-missing":
                env["ZYGGY_SECRET_PATTERNS"] = missing;
                expected = $"pickup: secret patterns {missing} are missing (ZYGGY_SECRET_PATTERNS)";
                break;
            default:
                env["ZYGGY_SECRET_PATTERNS"] = malformed;
                expected = $"pickup: {malformed}: malformed line";
                break;
        }

        harness.Environment = env;

        // Act
        Func<IHost> build = () => harness.Build();
        int exit = await harness.RunAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        build.Should().Throw<NodeStartupException>().Which.Should().Match<NodeStartupException>(e => e.ExitCode == 3 && e.Message.StartsWith(expected, StringComparison.Ordinal));
        exit.Should().Be(3);
        Directory.Exists(Path.Combine(harness.NodeDir, "logs")).Should().BeFalse("nothing starts after a refused pickup configuration");
    }

    [Fact]
    public async Task Laptop_PickupVariablesSetOrNot_NoPickupBuilt_InboxNeverTouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson();
        SeededJob own = await harness.Bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, Fact, created: Created, ct: ct);
        var before = TreeFingerprint.Of(memory.Root);

        // Act
        harness.Environment = new Dictionary<string, string?>();
        using (IHost bare = harness.Build())
        {
            bare.Services.GetService<BusPickup>().Should().BeNull();
        }

        harness.Environment = memory.Env();
        using (IHost withVariables = harness.Build())
        {
            withVariables.Services.GetService<BusPickup>().Should().BeNull();
        }

        int exit = await harness.RunAsync(TimeSpan.FromSeconds(4), ct);

        // Assert
        exit.Should().Be(0);
        (await harness.Bus.ExistsOnMainAsync(own.RelativePath, ct)).Should().BeTrue();
        TreeFingerprint.Of(memory.Root).Should().Equal(before);
    }

    // Plan 41: an item pushed while Central runs is filed by the 1 s poll that sees it, not by a slower path. The safety fetch (the
    // only other trigger while running) is set out of the run's reach, and a change it had to find would log its warning; so a
    // filing within the run is the poll's. This replaces a 10 s wall-clock bound that failed under load (12.3 s, correctly filed);
    // the speed bound is counted in polls instead, by the doorbell's probe of the filed file in the memory clone (see the context test).
    [Fact]
    public async Task Central_WithInstanceDir_BusItemPushedWhileRunning_FiledByTheNextPoll()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        await using var memory = new PickupArchiveMemory();
        await memory.InitializeAsync();
        harness.WriteNodeJson(machine: Central, activeSeconds: 1, idleSeconds: 60, safetyFetchMinutes: 60);
        harness.Environment = memory.Env();
        SeededJob? item = null;
        int pushedAtRequest = 0;
        var started = DateTimeOffset.UtcNow;
        harness.Head.Probe = () => memory.Exists("archive/calizr/staging-restore-notes.txt");

        // Act
        int exit = await harness.RunUntilAsync(
            async () =>
            {
                if (item is null)
                {
                    if (!harness.LogLines.Any(l => l.Contains("registry published", StringComparison.Ordinal)) && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20))
                    {
                        return false;
                    }

                    item = await harness.Bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, ItemFieldsFor(Notes), Notes, Created, ct: ct);
                    pushedAtRequest = harness.Head.RequestCount;
                    return false;
                }

                return !await harness.Bus.ExistsOnMainAsync(item.RelativePath, ct)
                    && harness.LogLines.Any(l => l.Contains("items filed 1", StringComparison.Ordinal));
            },
            WhileRunningLimit,
            ct,
            s => s.AddSingleton(sp => sp.GetRequiredService<NodeConfiguration>().Poll.ToPollOptions()));

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        harness.LogLines.Should().NotContain(l => l.Contains("safety fetch", StringComparison.Ordinal), "the poll, not the safety fetch, saw the push");
        harness.Head.ProbedSince(pushedAtRequest).Skip(2).Should().NotContain(
            false,
            "the first poll after the push sees it (the second when a push of the node's own rebased onto it), so the file is filed by the poll after");
        (await memory.Repo.ShowBytesAsync("acme/alice/archive/calizr/staging-restore-notes.txt", ct)).Should().Equal(Notes);
        harness.LogLines.Should().NotContain(l => l.Contains("Sunday's snapshot", StringComparison.Ordinal));
        SecretSweep.AssertAbsent(harness.LogLines);
    }

    [Fact]
    public async Task Central_WithoutInstanceDir_StartsAndPicksUpContexts_ItemWaitsErrorOnce()
    {
        // Arrange: plan 41 A8 — the archive part missing never stops start-up (exit 0, not 3).
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        await using var memory = new PickupArchiveMemory();
        await memory.InitializeAsync();
        harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env(withInstanceDir: false);
        SeededJob item = await harness.Bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, ItemFieldsFor(Notes), Notes, Created, ct: ct);
        SeededJob context = await harness.Bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, Fact, created: Created, ct: ct);
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id);
        string memoryHead = await memory.Repo.GitAsync(memory.Repo.BareDir, ["rev-parse", "main"], ct);

        // Act
        int exit = await harness.RunUntilAsync(
            async () => await harness.Bus.ExistsOnMainAsync(archived, ct) && harness.LogLines.Any(l => l.Contains("pickup: archive configuration missing", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30),
            ct);

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        (await harness.Bus.ExistsOnMainAsync(item.RelativePath, ct)).Should().BeTrue();
        harness.LogLines.Count(l => l.Contains("pickup: archive configuration missing: archive configuration missing (ZYGGY_INSTANCE_DIR not set)", StringComparison.Ordinal)).Should().Be(1);
        (await memory.Repo.GitAsync(memory.Repo.BareDir, ["rev-parse", "main"], ct)).Should().Be(memoryHead);
    }

    [Fact]
    public async Task Central_WithInstanceAndStubGraph_OneDriveItemPushedWhileRunning_Filed_NoDriveWriterInContainer()
    {
        // Arrange: plan 41 Step 12 — the production host with the instance's M365 configuration, the stub Graph handler injected.
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        await using var memory = new PickupArchiveMemory(m365: true);
        await memory.InitializeAsync();
        memory.Drive.Add(memory.Drive.Add(StubOneDrive.RootId, "calizr"), "staging-restore-notes.txt", Notes);
        harness.WriteNodeJson(machine: Central, activeSeconds: 1, idleSeconds: 60);
        harness.Environment = memory.Env();
        using (IHost host = harness.Build(s => s.AddSingleton(memory.DriveOptions)))
        {
            host.Services.GetService<BusPickup>().Should().NotBeNull();
            host.Services.GetService<Zyggy.Core.M365.Graph.IGraphDriveWriter>().Should().BeNull();
        }

        SeededJob? item = null;
        var started = DateTimeOffset.UtcNow;

        // Act
        int exit = await harness.RunUntilAsync(
            async () =>
            {
                if (item is null)
                {
                    if (!harness.LogLines.Any(l => l.Contains("registry published", StringComparison.Ordinal)) && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20))
                    {
                        return false;
                    }

                    item = await harness.Bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, ItemFieldsFor(Notes, ItemRoute.OneDrive), [], DateTimeOffset.UtcNow, ct: ct);
                    return false;
                }

                return !await harness.Bus.ExistsOnMainAsync(item.RelativePath, ct) && harness.LogLines.Any(l => l.Contains("items filed 1", StringComparison.Ordinal));
            },
            WhileRunningLimit,
            ct,
            s =>
            {
                s.AddSingleton(sp => sp.GetRequiredService<NodeConfiguration>().Poll.ToPollOptions());
                s.AddSingleton(memory.DriveOptions);
            });

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        (await memory.Repo.ShowBytesAsync("acme/alice/archive/calizr/staging-restore-notes.txt", ct)).Should().Equal(Notes);
        memory.Graph.Requests.Should().Contain(r => r.Uri.Host == StubGraphHandler.Download)
            .And.NotContain(r => r.Method != HttpMethod.Get && !(r.Method == HttpMethod.Post && r.Uri.Host == "login.microsoftonline.com"));
        memory.Drive.Puts.Should().Be(0);
        harness.LogLines.Should().NotContain(l => l.Contains("Sunday's snapshot", StringComparison.Ordinal));
        SecretSweep.AssertAbsent(harness.LogLines);
    }

    [Fact]
    public async Task Central_NoM365Config_OneDriveItemWaits_ErrorNotConfiguredOnce_BusItemFiled()
    {
        // Arrange: spec 41 AC-37 — the instance has archive.json but no m365.json.
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        await using var memory = new PickupArchiveMemory();
        await memory.InitializeAsync();
        harness.WriteNodeJson(machine: Central);
        harness.Environment = memory.Env();
        SeededJob oneDrive = await harness.Bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, ItemFieldsFor(Notes, ItemRoute.OneDrive), [], DateTimeOffset.UtcNow, ct: ct);
        SeededJob bus = await harness.Bus.SeedSignedItemAsync(
            HomeLaptop, Central, TestKeys.Acme1, harness.Store, ItemFieldsFor(Notes, ItemRoute.Bus, "staging-notes-bus"), Notes, DateTimeOffset.UtcNow.AddSeconds(1), ct: ct);

        // Act
        int exit = await harness.RunUntilAsync(
            async () => !await harness.Bus.ExistsOnMainAsync(bus.RelativePath, ct)
                && harness.LogLines.Any(l => l.Contains("pickup: OneDrive fetch not configured", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30),
            ct,
            s => s.AddSingleton(memory.DriveOptions));

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        (await harness.Bus.ExistsOnMainAsync(oneDrive.RelativePath, ct)).Should().BeTrue();
        (await memory.Repo.ShowBytesAsync("acme/alice/archive/calizr/staging-notes-bus.txt", ct)).Should().Equal(Notes);
        harness.LogLines.Count(l => l.Contains("pickup: OneDrive fetch not configured", StringComparison.Ordinal)).Should().Be(1);
        memory.Graph.Requests.Should().BeEmpty();
    }

    private static readonly byte[] Notes = "# Staging restore\n\nRestore the staging database from Sunday's snapshot.\n"u8.ToArray();

    private static ItemFields ItemFieldsFor(byte[] file, ItemRoute via, string slug = "staging-restore-notes") => new(
        Zyggy.Core.Memory.Slug.Parse("calizr"),
        Zyggy.Core.Memory.Slug.Parse(slug),
        "Staging restore notes",
        "How the staging database was restored",
        "restore-notes.md",
        file.Length,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)),
        via,
        via == ItemRoute.OneDrive ? slug + ".txt" : null);

    private static ItemFields ItemFieldsFor(byte[] file) => new(
        Zyggy.Core.Memory.Slug.Parse("calizr"),
        Zyggy.Core.Memory.Slug.Parse("staging-restore-notes"),
        "Staging restore notes",
        "How the staging database was restored",
        "restore-notes.md",
        file.Length,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)),
        ItemRoute.Bus,
        null);
}
