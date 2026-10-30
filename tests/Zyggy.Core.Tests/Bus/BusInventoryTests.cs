using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 08 AC-5: the tenant's bus inventoried and one envelope traced, read-only, under the write lock, over a scratch tree.</summary>
public sealed class BusInventoryTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly RecordingLogger<BusInventory> _log = new();

    private InMemorySecretStore _both = null!;

    private InMemorySecretStore _oneOnly = null!;

    // The tree (ids ordered by their instants): central has two reports; home-laptop one queued, one claimed, one rejected (+ its report),
    // three reports of which one is signed with acme/2 (absent from the node's store); the archive holds one old done report.
    private EnvelopeId _queued = null!;
    private EnvelopeId _claimed = null!;
    private EnvelopeId _rejected = null!;
    private EnvelopeId _done = null!;
    private EnvelopeId _archivedJob = null!;
    private EnvelopeId _unverifiedJob = null!;
    private readonly List<string> _reportPaths = [];

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _both = await TestKeys.StoreWithBothAsync(ct);
        _oneOnly = await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);
        _queued = EnvelopeId.New(T.AddMinutes(-50));
        _claimed = EnvelopeId.New(T.AddMinutes(-40));
        _rejected = EnvelopeId.New(T.AddMinutes(-30));
        _done = EnvelopeId.New(T.AddMinutes(-20));
        _archivedJob = EnvelopeId.New(T.AddDays(-40));
        _unverifiedJob = EnvelopeId.New(T.AddMinutes(-10));

        // home-laptop's inbox, claimed, rejected (tampered bytes)
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), _queued), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _both, TestKeys.Acme1, "probe\n", ct, id: _queued));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), _claimed), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _both, TestKeys.Acme1, "probe\n", ct, id: _claimed));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), _rejected), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _both, TestKeys.Acme1, "probe\n", ct, b => Flip(b), _rejected));

        // home-laptop's reports: the rejection report, the done report with a cost, the acme/2 one
        Report(BusPaths.Reports(Acme, HomeLaptop), await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, _rejected, _both, TestKeys.Acme1, "rejected\n", ct, status: ReportStatus.Rejected, reason: "invalid_signature", created: T.AddMinutes(-29)));
        Report(BusPaths.Reports(Acme, HomeLaptop), await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, _done, _both, TestKeys.Acme1, "REPORT\nall good\n", ct, costUsd: 0.0123m, created: T.AddMinutes(-19)));
        Report(BusPaths.Reports(Acme, HomeLaptop), await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, _unverifiedJob, _both, TestKeys.Acme2, "signed with the other key\n", ct, created: T.AddMinutes(-9)));

        // central's two reports (answers to jobs that are long gone)
        Report(BusPaths.Reports(Acme, Central), await TestEnvelopes.SignedReportBytesAsync(Central, HomeLaptop, EnvelopeId.New(T.AddHours(-3)), _both, TestKeys.Acme1, "ok\n", ct, created: T.AddHours(-2)));
        Report(BusPaths.Reports(Acme, Central), await TestEnvelopes.SignedReportBytesAsync(Central, HomeLaptop, EnvelopeId.New(T.AddHours(-5)), _both, TestKeys.Acme1, "ok\n", ct, created: T.AddHours(-4)));

        // the archive
        Report(BusPaths.Archive(Acme, 2026, 9), await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, _archivedJob, _both, TestKeys.Acme1, "archived\n", ct, created: T.AddDays(-39)));

        // noise the inventory must ignore: a non-envelope file in reports/, a stray directory under nodes/
        _checkout.Drop(BusPaths.Reports(Acme, HomeLaptop) + "/notes.txt", [1]);
        Directory.CreateDirectory(BusPaths.Resolve(_checkout.Root, BusPaths.Nodes(Acme) + "/Not A Machine"));
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ListAsync_Counts_MachinesOrdinalAndExact()
    {
        // Act
        InventoryReport report = await Inventory().ListAsync(10, TestContext.Current.CancellationToken);

        // Assert
        report.Machines.Should().Equal(
            new MachineCounts(Central, 0, 0, 0, 2, 0),
            new MachineCounts(HomeLaptop, 1, 1, 1, 3, 0));
        report.HeadSha.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public async Task ListAsync_RecentTen_SixReportsUlidDescendingUnverifiableTagged()
    {
        // Act
        InventoryReport report = await Inventory().ListAsync(10, TestContext.Current.CancellationToken);

        // Assert: five live reports and, since spec 13 (AC-25), the archived one
        report.RecentReports.Should().HaveCount(6);
        report.RecentReports.Select(r => r.Id.Value).Should().BeInDescendingOrder(StringComparer.Ordinal);
        EnvelopeSummary unverified = report.RecentReports.Single(r => !r.Verified);
        unverified.Problem.Should().Be("unknown_key");
        unverified.From.Should().Be(HomeLaptop);
        unverified.To.Should().Be(Central);
        unverified.InReplyTo.Should().Be(_unverifiedJob);
        report.RecentReports.Where(r => r.Verified).Should().HaveCount(5).And.AllSatisfy(r => r.Problem.Should().BeNull());
        report.RecentReports.Single(r => r.InReplyTo == _done).CostUsd.Should().Be(0.0123m);
    }

    // Spec 13 AC-25: archived reports are in the recent list and the total; the per-machine table stays live counts.
    [Fact]
    public async Task ListAsync_ArchivedReports_InRecentAndInTotal_LiveCountsUnchanged()
    {
        // Arrange: a second archived report (newest of all) and an archived context that is not a report
        var ct = TestContext.Current.CancellationToken;
        Report(BusPaths.Archive(Acme, 2026, 10), await TestEnvelopes.SignedReportBytesAsync(HomeLaptop, Central, EnvelopeId.New(T), _both, TestKeys.Acme1, "picked up\n", ct, created: T.AddMinutes(-1)));
        var contextId = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), contextId), await TestEnvelopes.SignedContextBytesAsync(HomeLaptop, Central, _both, TestKeys.Acme1, "- a fact\n", T, ct, id: contextId));

        // Act
        InventoryReport report = await Inventory().ListAsync(10, ct);

        // Assert
        report.ReportsTotal.Should().Be(7);
        report.RecentReports.Should().HaveCount(7);
        report.RecentReports[0].RelativePath.Should().StartWith(BusPaths.Archive(Acme, 2026, 10));
        report.RecentReports.Select(r => r.Id.Value).Should().BeInDescendingOrder(StringComparer.Ordinal).And.NotContain(contextId.Value);
        report.Machines.Should().Equal(
            new MachineCounts(Central, 0, 0, 0, 2, 0),
            new MachineCounts(HomeLaptop, 1, 1, 1, 3, 0));
    }

    [Fact]
    public async Task ListAsync_TenantDirectories_NamesUnderTenants()
    {
        // Arrange: plan 08 A12 — the directory names under tenants/, so `status` can say how many there are without a second walk
        Directory.CreateDirectory(BusPaths.Resolve(_checkout.Root, "tenants/other-org"));

        // Act
        InventoryReport report = await Inventory().ListAsync(0, TestContext.Current.CancellationToken);

        // Assert
        report.TenantDirectories.Should().Equal("acme", "other-org");
    }

    [Fact]
    public async Task ListAsync_RecentTwo_NewestTwo()
    {
        // Act
        InventoryReport report = await Inventory().ListAsync(2, TestContext.Current.CancellationToken);

        // Assert
        report.RecentReports.Should().HaveCount(2);
        report.RecentReports[0].InReplyTo.Should().Be(_unverifiedJob);
        report.RecentReports[1].InReplyTo.Should().Be(_done);
    }

    [Fact]
    public async Task ListAsync_NoNodesDirectory_EmptyTables()
    {
        // Arrange
        using var empty = new TempCheckout(Acme, Central);
        Directory.Delete(BusPaths.Resolve(empty.Root, BusPaths.Nodes(Acme)), recursive: true);

        // Act
        InventoryReport report = await Inventory(empty.Root).ListAsync(10, TestContext.Current.CancellationToken);

        // Assert
        report.Machines.Should().BeEmpty();
        report.RecentReports.Should().BeEmpty();
    }

    [Theory]
    [InlineData("queued", TraceState.Queued)]
    [InlineData("claimed", TraceState.Claimed)]
    [InlineData("rejected", TraceState.Rejected)]
    [InlineData("done", TraceState.Reported)]
    [InlineData("archived", TraceState.Reported)]
    public async Task TraceAsync_States(string which, TraceState expected)
    {
        // Arrange
        EnvelopeId id = which switch { "queued" => _queued, "claimed" => _claimed, "rejected" => _rejected, "done" => _done, _ => _archivedJob };

        // Act
        EnvelopeTrace? trace = await Inventory().TraceAsync(id, TestContext.Current.CancellationToken);

        // Assert
        trace.Should().NotBeNull();
        trace!.Id.Should().Be(id);
        trace.State.Should().Be(expected);
        trace.Machine.Should().Be(HomeLaptop);
        switch (which)
        {
            case "queued":
            case "claimed":
                trace.Job!.Verified.Should().BeTrue();
                trace.Reports.Should().BeEmpty();
                trace.ReportBody.Should().BeNull();
                break;
            case "rejected":
                trace.Job!.Verified.Should().BeFalse("the file in rejected/ is tampered and read leniently");
                trace.Reports.Should().ContainSingle().Which.Reason.Should().Be("invalid_signature");
                break;
            case "done":
                trace.Job.Should().BeNull("the job file is gone once reported");
                trace.Reports[^1].CostUsd.Should().Be(0.0123m);
                trace.ReportBody.Should().Be("REPORT\nall good\n");
                break;
            default:
                trace.Reports.Should().ContainSingle().Which.RelativePath.Should().StartWith(BusPaths.ArchiveRoot(Acme));
                trace.ReportBody.Should().Be("archived\n");
                break;
        }
    }

    [Fact]
    public async Task TraceAsync_Unknown_Null()
    {
        // Act
        EnvelopeTrace? trace = await Inventory().TraceAsync(EnvelopeId.New(T.AddYears(-1)), TestContext.Current.CancellationToken);

        // Assert
        trace.Should().BeNull();
    }

    [Fact]
    public async Task TraceAsync_UnverifiedReportOnly_NotReportedAndNoBody()
    {
        // Arrange: the job is still in the inbox; the only report answering it is signed with a key this node does not hold
        var ct = TestContext.Current.CancellationToken;
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), _unverifiedJob), await TestEnvelopes.SignedJobBytesAsync(Central, HomeLaptop, _both, TestKeys.Acme1, "probe\n", ct, id: _unverifiedJob));

        // Act
        EnvelopeTrace? trace = await Inventory().TraceAsync(_unverifiedJob, ct);

        // Assert
        trace!.State.Should().Be(TraceState.Queued, "an unverified report proves nothing");
        trace.Reports.Should().ContainSingle().Which.Verified.Should().BeFalse();
        trace.ReportBody.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_WriteLockHeldPast60Seconds_ThrowsBusBusy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<InventoryReport> task = Inventory().ListAsync(10, ct);
        for (int i = 0; i < 200 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, ct);
        }

        Func<Task> act = () => task;

        // Assert
        await act.Should().ThrowAsync<BusBusyException>();
    }

    [Fact]
    public async Task ListAsync_TakesTheWriteLockAndNeverTheListenLock()
    {
        // Arrange: a secret store that holds the first verification until released, so the lock state can be observed mid-read
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = Substitute.For<ISecretStore>();
        gated.GetAsync(Arg.Any<TenantId>(), Arg.Any<SecretName>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await gate.Task;
            return await _both.GetAsync(call.ArgAt<TenantId>(0), call.ArgAt<SecretName>(1), call.ArgAt<CancellationToken>(2));
        });

        // Act
        Task<InventoryReport> task = Inventory(store: gated).ListAsync(10, ct);
        for (int i = 0; i < 100 && !File.Exists(Path.Combine(_checkout.Root, ".git", BusCheckoutLock.WriteLockName)); i++)
        {
            await Task.Delay(10, ct);
        }

        bool writeLockHeld = File.Exists(Path.Combine(_checkout.Root, ".git", BusCheckoutLock.WriteLockName));
        bool listenLockHeld = File.Exists(Path.Combine(_checkout.Root, ".git", BusCheckoutLock.ListenLockName));
        gate.SetResult();
        await task;

        // Assert
        writeLockHeld.Should().BeTrue();
        listenLockHeld.Should().BeFalse();
        File.Exists(Path.Combine(_checkout.Root, ".git", BusCheckoutLock.WriteLockName)).Should().BeFalse("released when the read is done");
    }

    [Fact]
    public async Task ListAsync_WritesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Dictionary<string, byte[]> before = Snapshot();

        // Act
        await Inventory().ListAsync(10, ct);
        await Inventory().TraceAsync(_done, ct);

        // Assert
        Snapshot().Should().BeEquivalentTo(before);
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().OnlyContain(c => c == "rev-parse");
    }

    [Fact]
    public async Task ListAsync_NeverOpensAnotherTenant()
    {
        // Arrange: a "file" of another tenant that is a directory — opening it would throw
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(BusPaths.Resolve(_checkout.Root, "tenants/other-org/nodes/x/reports/" + EnvelopeId.New(T).Value + ".md"));

        // Act
        Func<Task> act = async () =>
        {
            await Inventory().ListAsync(10, ct);
            await Inventory().TraceAsync(_done, ct);
        };

        // Assert
        await act.Should().NotThrowAsync();
    }

    private BusInventory Inventory(string? root = null, ISecretStore? store = null)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(store ?? _oneOnly);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), root ?? _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return new BusInventory(bus, root ?? _checkout.Root, signer, _clock, _log);
    }

    private void Report(string folder, byte[] bytes)
    {
        EnvelopeId id = EnvelopeId.Parse(Encoding.UTF8.GetString(bytes).Split('\n').First(l => l.StartsWith("id: ", StringComparison.Ordinal))[4..]);
        string path = BusPaths.EnvelopeFile(folder, id);
        _checkout.Drop(path, bytes);
        _reportPaths.Add(path);
    }

    private Dictionary<string, byte[]> Snapshot() =>
        Directory.EnumerateFiles(_checkout.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(f => f, File.ReadAllBytes, StringComparer.Ordinal);

    private static byte[] Flip(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("sig: hmac-sha256:", StringComparison.Ordinal) + "sig: hmac-sha256:".Length;
        return Encoding.UTF8.GetBytes(text[..at] + (text[at] == '0' ? '1' : '0') + text[(at + 1)..]);
    }
}
