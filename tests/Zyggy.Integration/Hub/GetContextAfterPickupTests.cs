using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Hub;

/// <summary>Spec 13 AC-26 and AC-28: a line Central's pickup copied from the bare repository a moment earlier is returned by <c>get_context</c>; the digest never shows it.</summary>
public sealed class GetContextAfterPickupTests : IAsyncLifetime
{
    private const string Fact = "- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot";

    private const string InboxHeading = "## inbox (picked up, not yet filed by the dream)";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 42, 7, TimeSpan.Zero);

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly PickupMemory _memory = new();

    private InMemorySecretStore _store = null!;

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [MachineName.Central, HomeLaptop], CancellationToken.None);
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
        _services = TestHost.Build(_bus, MachineName.Central, _store, extend: s => s.AddBusPickup(_memory.Configuration));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _bus.DisposeAsync();
        _memory.Dispose();
    }

    [Fact]
    public async Task GetContext_AfterPickupOfContextFromHomeLaptop_LineVerbatimUnderInboxHeading()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedSignedContextAsync(HomeLaptop, MachineName.Central, TestKeys.Acme1, _store, Fact + "\n", created: Created, ct: ct);
        await PickupAsync(ct);
        await using InProcessHub hub = await InProcessHub.StartAsync(_memory.Paths, TimeZoneInfo.Utc);

        // Act
        string text = await hub.CallGetContextAsync("staging database");

        // Assert
        text.Should().StartWith("<zyggy-context tenant=\"acme\" user=\"alice\" generated=\"");
        text.Should().Contain(InboxHeading + "\n" + Fact + "\n").And.EndWith("</zyggy-context>\n");
    }

    [Fact]
    public async Task GetContext_AfterPickupOfAReport_ItsOneObservedLineReturned()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var job = EnvelopeId.New(Created.AddHours(-1));
        await _bus.SeedSignedReportAsync(HomeLaptop, MachineName.Central, job, TestKeys.Acme1, _store, "Root cause is the expired staging certificate\n- files_changed: none\n", created: Created, ct: ct);
        await PickupAsync(ct);
        await using InProcessHub hub = await InProcessHub.StartAsync(_memory.Paths, TimeZoneInfo.Utc);

        // Act
        string text = await hub.CallGetContextAsync("staging certificate");

        // Assert
        text.Should().Contain(InboxHeading + "\n" + $"- [observed] 2026-10-29 [report {job.Value[..8]}]: Root cause is the expired staging certificate\n");
        text.Should().NotContain("files_changed");
    }

    [Fact]
    public async Task GetContext_BeforeAnyPickup_NoInboxBlock()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedSignedContextAsync(HomeLaptop, MachineName.Central, TestKeys.Acme1, _store, Fact + "\n", created: Created, ct: ct);
        await using InProcessHub hub = await InProcessHub.StartAsync(_memory.Paths, TimeZoneInfo.Utc);

        // Act
        string text = await hub.CallGetContextAsync("staging database");

        // Assert
        text.Should().NotContain(InboxHeading);
    }

    [Fact]
    public async Task Digest_AfterPickup_NoInboxLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedSignedContextAsync(HomeLaptop, MachineName.Central, TestKeys.Acme1, _store, Fact + "\n", created: Created, ct: ct);
        await PickupAsync(ct);
        _memory.InboxFiles().Should().ContainSingle();

        // Act
        var outputs = new List<string>();
        foreach (string section in new[] { "identity", "index", "daily" })
        {
            var (exit, console) = await ZyggyCliInProcess.RunAsync(["memory", "digest", section], _memory.Env(), _ => { }, null, ct);
            exit.Should().Be(0, console.Stderr);
            outputs.Add(console.Stdout);
        }

        // Assert
        outputs.Should().AllSatisfy(o => o.Should().NotContain("staging database"));
    }

    private async Task PickupAsync(CancellationToken ct)
    {
        await _bus.PullCloneAsync(ct);
        (await _services.GetRequiredService<BusPickup>().RunAsync(ct)).Inboxed.Should().Be(1);
    }
}
