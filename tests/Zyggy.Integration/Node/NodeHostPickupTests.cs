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

    [Fact]
    public async Task Central_ContextPushedWhileRunning_InboxFileAndArchiveOnMainWithinTenSeconds_ProductionJitterAtOneSecond()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using var memory = new PickupMemory();
        harness.WriteNodeJson(machine: Central, activeSeconds: 1, idleSeconds: 60);
        harness.Environment = memory.Env();
        var before = memory.FingerprintOutsideInbox();
        SeededJob? context = null;
        string? archived = null;
        var started = DateTimeOffset.UtcNow;
        TimeSpan delay = TimeSpan.Zero;

        // Act
        int exit = await harness.RunUntilAsync(
            async () =>
            {
                if (context is null)
                {
                    if (!harness.LogLines.Any(l => l.Contains("listening", StringComparison.Ordinal) || l.Contains(" poll ", StringComparison.Ordinal)) && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(3))
                    {
                        return false;
                    }

                    context = await harness.Bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, harness.Store, Fact, created: Created, ct: ct);
                    archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), context.Id);
                    started = DateTimeOffset.UtcNow;
                    return false;
                }

                // the Information line follows the push: wait for it too, or the host may stop in between
                bool done = memory.InboxFiles().Count == 1 && await harness.Bus.ExistsOnMainAsync(archived!, ct)
                    && harness.LogLines.Any(l => l.Contains("picked up 1 file(s)", StringComparison.Ordinal));
                if (done)
                {
                    delay = DateTimeOffset.UtcNow - started;
                }

                return done;
            },
            TimeSpan.FromSeconds(30),
            ct,
            s => s.AddSingleton(sp => sp.GetRequiredService<NodeConfiguration>().Poll.ToPollOptions()));

        // Assert
        exit.Should().Be(0, string.Join('\n', harness.LogLines));
        delay.Should().BeLessThan(TimeSpan.FromSeconds(10), string.Join('\n', harness.LogLines));
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
}
