using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>Spec 08 AC-11: <c>zyggy verify</c> in process on the bare repository — a tampered report, a tenant rewrite and a second tenant directory detected; the checkout and a running listener untouched.</summary>
public sealed partial class VerifyCommandTests : IAsyncLifetime
{
    private CliHarness _harness = null!;

    private int _envelopes;

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _harness = await CliHarness.CreateAsync(ct);
        BusRepoFixture bus = _harness.Bus;
        var store = _harness.Store;
        SeededJob job1 = await bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, store, "probe\n", ct: ct);
        SeededJob job2 = await bus.SeedSignedJobAsync(CliHarness.HomeLaptop, CliHarness.Central, TestKeys.Acme1, store, "probe\n", ct: ct);
        await bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, job1.Id, TestKeys.Acme1, store, "ok\n", created: DateTimeOffset.UtcNow.AddMinutes(-2), ct: ct);
        await bus.SeedSignedReportAsync(CliHarness.Central, CliHarness.HomeLaptop, job2.Id, TestKeys.Acme1, store, "ok\n", created: DateTimeOffset.UtcNow.AddMinutes(-1), ct: ct);
        var contextId = EnvelopeId.New(DateTimeOffset.UtcNow);
        Model context = Model.CreateContext(new EnvelopeHeader(CliHarness.Acme, contextId, CliHarness.HomeLaptop, CliHarness.Central, DateTimeOffset.UtcNow), new ContextFields(new ContextScope(ContextScopeKind.General, null)), "- a fact\n"u8.ToArray());
        EnvelopeResult signedContext = await new EnvelopeSigner(store).SignAsync(context, TestKeys.Acme1, ct);
        await bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Context(CliHarness.Acme, CliHarness.HomeLaptop), contextId), EnvelopeWriter.Serialize(signedContext.Envelope!), "acme context " + contextId.Value, ct);
        var archivedId = EnvelopeId.New(DateTimeOffset.UtcNow.AddDays(-40));
        byte[] archived = await TestEnvelopes.SignedReportBytesAsync(CliHarness.HomeLaptop, CliHarness.Central, EnvelopeId.New(DateTimeOffset.UtcNow.AddDays(-41)), store, TestKeys.Acme1, "archived\n", ct, id: archivedId, created: DateTimeOffset.UtcNow.AddDays(-40));
        await bus.SeedRawFileAsync(BusPaths.EnvelopeFile(BusPaths.Archive(CliHarness.Acme, 2026, 9), archivedId), archived, "seed: archive", ct);
        await bus.PullCloneAsync(ct);
        _envelopes = 6;
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task Verify_CleanCheckout_ExitZeroSummaryOnly()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        int keeps = Directory.EnumerateFiles(Path.Combine(_harness.Bus.CloneDir, "tenants", "acme"), "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(".md", StringComparison.Ordinal) && !f.EndsWith(".yaml", StringComparison.Ordinal));
        string head = (await _harness.Bus.HeadShaAsync(ct)).Trim()[..12];
        console.Stdout.Should().Be($"verified {_envelopes} ok, 0 failed, {keeps} skipped; 1 tenant directory; HEAD {head}\n");
        console.Stderr.Should().BeEmpty();
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().BeEmpty();
        SecretSweep.AssertAbsent([console.Stdout]);
    }

    [Fact]
    public async Task Verify_Verbose_OkAndSkipLinesThenSummary()
    {
        // Act
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await _harness.RunAsync(["verify", "--verbose"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        string[] lines = console.Stdout.TrimEnd('\n').Split('\n');
        lines.Count(l => l.StartsWith("OK tenants/acme/", StringComparison.Ordinal)).Should().Be(_envelopes);
        lines.Should().Contain(l => l.StartsWith("SKIP tenants/acme/", StringComparison.Ordinal));
        lines[^1].Should().StartWith($"verified {_envelopes} ok, 0 failed,");
        lines.Take(lines.Length - 1).Should().OnlyContain(l => l.StartsWith("OK ", StringComparison.Ordinal) || l.StartsWith("SKIP ", StringComparison.Ordinal) || l.EndsWith(" ok", StringComparison.Ordinal), "with --verbose: OK and SKIP lines, then the per-folder ok counts");
        lines.Should().Contain(l => l.StartsWith("tenants/acme/nodes/home-laptop/reports: ", StringComparison.Ordinal) && l.EndsWith(" ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_TamperedReportOnMain_ExitSixFailInvalidSignature()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob tampered = await _harness.Bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, EnvelopeId.New(DateTimeOffset.UtcNow), TestKeys.Acme1, _harness.Store, "ok\n", mutate: FlipSignature, ct: ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        string[] lines = console.Stdout.TrimEnd('\n').Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Be($"FAIL {tampered.RelativePath} invalid_signature (sig)");
        lines[1].Should().StartWith($"verified {_envelopes} ok, 1 failed,");
    }

    [Fact]
    public async Task Verify_ReportRewrittenWithOtherTenant_ExitSixTenantMismatch()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        SeededJob rewritten = await _harness.Bus.SeedSignedReportAsync(CliHarness.HomeLaptop, CliHarness.Central, EnvelopeId.New(DateTimeOffset.UtcNow), TestKeys.Acme1, _harness.Store, "ok\n", mutate: b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("tenant: acme", "tenant: other-org", StringComparison.Ordinal)), ct: ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stdout.Should().StartWith($"FAIL {rewritten.RelativePath} tenant_mismatch (tenant)\n");
    }

    [Fact]
    public async Task Verify_SecondTenantDirectory_ExitSixWarnAndTwoDirectoriesNotRead()
    {
        // Arrange: ex-OQ4 of 04 — a second tenant's file in the same checkout
        var ct = TestContext.Current.CancellationToken;
        string foreignPath = "tenants/other-org/nodes/home-laptop/jobs/" + EnvelopeId.New(DateTimeOffset.UtcNow).Value + ".md";
        byte[] foreignBytes = "---\ntenant: other-org\n---\nnot ours\n"u8.ToArray();
        await _harness.Bus.SeedRawFileAsync(foreignPath, foreignBytes, "seed: another tenant", ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        string[] lines = console.Stdout.TrimEnd('\n').Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Be("WARN tenants/other-org/ second tenant directory (§13: one bus repository per tenant); not read");
        lines[1].Should().MatchRegex($@"^verified {_envelopes} ok, 0 failed, \d+ skipped; 2 tenant directories; HEAD [0-9a-f]{{12}}$");
        (await _harness.Bus.ShowBytesAsync(foreignPath, ct)).Should().Equal(foreignBytes);
        (await File.ReadAllBytesAsync(BusPaths.Resolve(_harness.Bus.CloneDir, foreignPath), ct)).Should().Equal(foreignBytes);
    }

    [Fact]
    public async Task Verify_JobAddressedToAnotherMachine_FailWriterMismatchTo()
    {
        // Arrange: a job for central seeded at home-laptop's jobs/ path
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(DateTimeOffset.UtcNow);
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), id);
        byte[] bytes = await TestEnvelopes.SignedJobBytesAsync(CliHarness.HomeLaptop, CliHarness.Central, _harness.Store, TestKeys.Acme1, "probe\n", ct, id: id);
        await _harness.Bus.SeedRawFileAsync(path, bytes, "seed: misplaced job", ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stdout.Should().Contain($"FAIL {path} writer_mismatch (to)\n");
    }

    [Fact]
    public async Task Verify_IdDiffersFromFileName_FailIdDiffers()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string path = BusPaths.EnvelopeFile(BusPaths.Jobs(CliHarness.Acme, CliHarness.HomeLaptop), EnvelopeId.New(DateTimeOffset.UtcNow.AddMinutes(-7)));
        await _harness.Bus.SeedSignedJobAsync(CliHarness.Central, CliHarness.HomeLaptop, TestKeys.Acme1, _harness.Store, "probe\n", fileNameOverride: path, ct: ct);
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(6);
        console.Stdout.Should().Contain($"FAIL {path} id_differs (id)\n");
    }

    [Fact]
    public async Task Verify_NeverWritesNeverTakesListenLock()
    {
        // Arrange: a listener holds the listen lock on the same checkout
        var ct = TestContext.Current.CancellationToken;
        await using ServiceProvider other = TestHost.BuildListener(_harness.Bus, CliHarness.HomeLaptop, _harness.Store);
        using BusListener listener = other.GetRequiredService<BusListener>();
        (await listener.StartAsync(ct)).Should().Be(BusStartupResult.Clean);
        int commits = await _harness.CommitCountOnMainAsync(ct);
        string porcelain = await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        (await _harness.PorcelainAsync(_harness.Bus.CloneDir, ct)).Should().Be(porcelain);
        (await _harness.CommitCountOnMainAsync(ct)).Should().Be(commits);
        _harness.Hooks.Calls.Select(HookedProcessRunner.SubCommand).Should().NotContain(c => c == "add" || c == "commit" || c == "push" || c == "pull" || c == "stash");
    }

    [Fact]
    public async Task Verify_WriteLockHeldBrieflyThenReleased_Completes()
    {
        // Arrange: the write lock is held for a second — verify waits, it does not fail
        var ct = TestContext.Current.CancellationToken;
        IDisposable? held = await BusCheckoutLock.AcquireAsync(_harness.Bus.CloneDir, BusCheckoutLock.WriteLockName, TimeSpan.Zero, TimeProvider.System, ct);
        held.Should().NotBeNull();
        Task release = Task.Run(async () =>
        {
            await Task.Delay(1000, ct);
            held!.Dispose();
        }, ct);
        var started = DateTime.UtcNow;

        // Act
        var (exit, console) = await _harness.RunAsync(["verify"], _harness.HomeLaptopNodeJson, ct: ct);
        await release;

        // Assert
        exit.Should().Be(0, console.Stderr);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Verify_NoTenantDirectory_ExitThree()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var foreign = new BusRepoFixture(TenantId.Parse("other-org"));
        await foreign.InitializeAsync();
        try
        {
            await foreign.SeedTenantAsync(TenantId.Parse("other-org"), [CliHarness.HomeLaptop], ct);
            string nodeJson = _harness.WriteNodeJson(CliHarness.HomeLaptop, foreign.CloneDir, "home-foreign");

            // Act
            var (exit, console) = await _harness.RunAsync(["verify"], nodeJson, ct: ct);

            // Assert
            exit.Should().Be(3);
            console.Stderr.Should().Be("zyggy: configuration error: no tenants/acme/ directory in bus.localPath\n");
        }
        finally
        {
            await foreign.DisposeAsync();
        }
    }

    private static byte[] FlipSignature(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("sig: hmac-sha256:", StringComparison.Ordinal) + "sig: hmac-sha256:".Length;
        return Encoding.UTF8.GetBytes(text[..at] + (text[at] == '0' ? '1' : '0') + text[(at + 1)..]);
    }
}
