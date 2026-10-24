using System.Text;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 08 AC-4: the §8 audit over the AC-4 scratch tree — every §4 outcome first, then verify's own checks; read-only, under the write lock, never outside this tenant.</summary>
public sealed class BusAuditorTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly RecordingLogger<BusAuditor> _log = new();

    private InMemorySecretStore _both = null!;

    private InMemorySecretStore _oneOnly = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _both = await TestKeys.StoreWithBothAsync(ct);
        _oneOnly = await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_Ac4Tree_EntriesInPathOrderWithTheExpectedOutcomes()
    {
        // Arrange: the AC-4 tree
        var ct = TestContext.Current.CancellationToken;
        var jobs = BusPaths.Jobs(Acme, HomeLaptop);
        var reports = BusPaths.Reports(Acme, HomeLaptop);
        var okJob = EnvelopeId.New(T.AddMinutes(-60));
        _checkout.Drop(BusPaths.EnvelopeFile(jobs, okJob), await Job(okJob, ct));
        var okReport = EnvelopeId.New(T.AddMinutes(-59));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, okReport), await ReportBytes(okReport, okJob, ct));
        var okContext = EnvelopeId.New(T.AddMinutes(-58));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), okContext), await Context(okContext, ct));
        var okArchived = EnvelopeId.New(T.AddDays(-40));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 9), okArchived), await ReportBytes(okArchived, EnvelopeId.New(T.AddDays(-41)), ct, created: T.AddDays(-40)));

        var flipped = EnvelopeId.New(T.AddMinutes(-50));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, flipped), Flip(await ReportBytes(flipped, okJob, ct)));
        var otherTenant = EnvelopeId.New(T.AddMinutes(-49));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, otherTenant), Replace(await ReportBytes(otherTenant, okJob, ct), "tenant: acme", "tenant: other-org"));
        var otherKeyTenant = EnvelopeId.New(T.AddMinutes(-48));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, otherKeyTenant), Replace(await ReportBytes(otherKeyTenant, okJob, ct), "key_id: acme/1", "key_id: other-org/1"));
        var absentKey = EnvelopeId.New(T.AddMinutes(-47));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, absentKey), await ReportBytes(absentKey, okJob, ct, signWith: TestKeys.Acme2));
        var idDiffers = EnvelopeId.New(T.AddMinutes(-46));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, idDiffers), await ReportBytes(EnvelopeId.New(T.AddMinutes(-45)), okJob, ct));
        var reportInJobs = EnvelopeId.New(T.AddMinutes(-44));
        _checkout.Drop(BusPaths.EnvelopeFile(jobs, reportInJobs), await ReportBytes(reportInJobs, okJob, ct));
        var reportFromCentral = EnvelopeId.New(T.AddMinutes(-43));
        _checkout.Drop(BusPaths.EnvelopeFile(reports, reportFromCentral), await ReportBytes(reportFromCentral, okJob, ct, from: Central, to: HomeLaptop));
        var jobToCentral = EnvelopeId.New(T.AddMinutes(-42));
        _checkout.Drop(BusPaths.EnvelopeFile(jobs, jobToCentral), await Job(jobToCentral, ct, to: Central));

        _checkout.Drop(jobs + "/notes.txt", "notes\n"u8.ToArray());
        _checkout.Drop(jobs + "/README.md", "# not an envelope\n"u8.ToArray());
        _checkout.Drop(BusPaths.Registry(Acme, HomeLaptop), "machine: home-laptop\n"u8.ToArray());
        _checkout.Drop(BusPaths.Tenant(Acme) + "/policy.yaml", "version: 1\n"u8.ToArray());
        _checkout.Drop("tenants/other-org/nodes/x/jobs/" + EnvelopeId.New(T).Value + ".md", "garbage\n"u8.ToArray());
        Directory.CreateDirectory(BusPaths.Resolve(_checkout.Root, "tenants/Bad Name"));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert: every entry in ordinal path order (claimed/ before jobs' files, etc.)
        var expected = new List<(string Path, AuditOutcome Outcome, string? Field)>
        {
            (BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 9), okArchived), AuditOutcome.Ok, null),
            (BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), okContext), AuditOutcome.Ok, null),
            (BusPaths.EnvelopeFile(jobs, okJob), AuditOutcome.Ok, null),
            (BusPaths.EnvelopeFile(jobs, reportInJobs), AuditOutcome.TypeFolderMismatch, "type"),
            (BusPaths.EnvelopeFile(jobs, jobToCentral), AuditOutcome.WriterMismatch, "to"),
            (jobs + "/README.md", AuditOutcome.Malformed, null),
            (jobs + "/notes.txt", AuditOutcome.Skipped, null),
            (BusPaths.EnvelopeFile(reports, okReport), AuditOutcome.Ok, null),
            (BusPaths.EnvelopeFile(reports, flipped), AuditOutcome.InvalidSignature, "sig"),
            (BusPaths.EnvelopeFile(reports, otherTenant), AuditOutcome.TenantMismatch, "tenant"),
            (BusPaths.EnvelopeFile(reports, otherKeyTenant), AuditOutcome.KeyIdTenantMismatch, "key_id"),
            (BusPaths.EnvelopeFile(reports, absentKey), AuditOutcome.UnknownKey, "key_id"),
            (BusPaths.EnvelopeFile(reports, idDiffers), AuditOutcome.IdDiffers, "id"),
            (BusPaths.EnvelopeFile(reports, reportFromCentral), AuditOutcome.WriterMismatch, "from"),
            (BusPaths.Tenant(Acme) + "/policy.yaml", AuditOutcome.Skipped, null),
            (BusPaths.Registry(Acme, HomeLaptop), AuditOutcome.Skipped, null),
        };
        report.Entries.Select(e => (e.RelativePath, e.Outcome, e.Field)).Should().Equal(expected.OrderBy(e => e.Path, StringComparer.Ordinal));
        report.Entries.Single(e => e.Outcome == AuditOutcome.UnknownKey).Detail.Should().Contain("acme/2");
        report.Entries.Should().NotContain(e => e.RelativePath.StartsWith("tenants/other-org/", StringComparison.Ordinal));
        report.ForeignTenants.Should().Equal("other-org");
        report.UnexpectedTopLevel.Should().Equal("tenants/Bad Name/");
        report.Ok.Should().Be(4);
        report.Failed.Should().Be(9);
        report.Skipped.Should().Be(3, "notes.txt, the registry file and the root policy.yaml (owner decision 2026-10-24: skipped until deliverable 18)");
        report.IsClean.Should().BeFalse();
        report.HeadSha.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public async Task RunAsync_NeverOpensAForeignTenantFile()
    {
        // Arrange: a "file" of another tenant that is a directory — opening it would throw
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(BusPaths.Resolve(_checkout.Root, "tenants/other-org/nodes/x/reports/" + EnvelopeId.New(T).Value + ".md"));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        report.ForeignTenants.Should().Equal("other-org");
        report.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_CleanTree_IsCleanAndHeadSha()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id), await Job(id, ct));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        report.IsClean.Should().BeTrue();
        report.Ok.Should().Be(1);
        report.HeadSha.Should().Be("0123456789abcdef0123456789abcdef01234567");
    }

    [Fact]
    public async Task RunAsync_ArchiveFileSignedWithAbsentKey_UnknownKeyNamesIt()
    {
        // Arrange: ex-Q3 — after a rotation, old archived files report the retired key by name
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T.AddDays(-40));
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 9), id), await ReportBytes(id, EnvelopeId.New(T.AddDays(-41)), ct, signWith: TestKeys.Acme2, created: T.AddDays(-40)));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        AuditEntry entry = report.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(AuditOutcome.UnknownKey);
        entry.Detail.Should().Contain("acme/2");
    }

    [Fact]
    public async Task RunAsync_RejectedFolderHoldsAReport_NoTypeCheck()
    {
        // Arrange: plan A3 — rejected/ holds whatever was refused; no type-per-folder rule there
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), id), await ReportBytes(id, EnvelopeId.New(T.AddMinutes(-1)), ct, from: Central, to: HomeLaptop));

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert
        report.Entries.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Ok);
    }

    [Fact]
    public async Task RunAsync_GitkeepFiles_Skipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _checkout.Drop(BusPaths.Jobs(Acme, HomeLaptop) + "/.gitkeep", []);
        _checkout.Drop(BusPaths.Claimed(Acme, HomeLaptop) + "/.gitkeep", []);
        _checkout.Drop(BusPaths.RegistryFolder(Acme) + "/.gitkeep", []);
        _checkout.Drop(BusPaths.Archive(Acme, 2026, 9) + "/.gitkeep", []);

        // Act
        AuditReport report = await Auditor().RunAsync(null, ct);

        // Assert: the fixture's keep files in every §4 folder, the registry and the archive included, are not drift
        report.Entries.Should().HaveCount(4).And.AllSatisfy(e => e.Outcome.Should().Be(AuditOutcome.Skipped));
        report.IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WritesNothingAndOnlyRevParse()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var id = EnvelopeId.New(T);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), id), await Job(id, ct));
        Dictionary<string, byte[]> before = Snapshot();

        // Act
        await Auditor().RunAsync(null, ct);

        // Assert
        Snapshot().Should().BeEquivalentTo(before);
        _processes.Calls.Select(RecordingProcessRunner.SubCommand).Should().OnlyContain(c => c == "rev-parse");
    }

    [Fact]
    public async Task RunAsync_WriteLockHeldPast60Seconds_ThrowsBusBusy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));
        using IDisposable? other = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.WriteLockName, TimeSpan.Zero, _clock, ct);

        // Act
        Task<AuditReport> task = Auditor().RunAsync(null, ct);
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
    public async Task RunAsync_Progress_EveryFiveHundredFiles()
    {
        // Arrange: 1,001 files
        var ct = TestContext.Current.CancellationToken;
        string folder = BusPaths.Context(Acme, HomeLaptop);
        for (int i = 0; i < 1001; i++)
        {
            _checkout.Drop(folder + "/file-" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ".txt", []);
        }

        var seen = new List<int>();
        var progress = new SynchronousProgress(seen.Add);

        // Act
        AuditReport report = await Auditor().RunAsync(progress, ct);

        // Assert
        seen.Should().Equal(500, 1000);
        report.Skipped.Should().Be(1001);
    }

    private BusAuditor Auditor()
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (home-laptop)", "home-laptop@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_oneOnly);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, HomeLaptop), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return new BusAuditor(bus, _checkout.Root, signer, _clock, _log);
    }

    private Task<byte[]> Job(EnvelopeId id, CancellationToken ct, MachineName? to = null) =>
        TestEnvelopes.SignedJobBytesAsync(Central, to ?? HomeLaptop, _both, TestKeys.Acme1, "probe\n", ct, id: id);

    private Task<byte[]> ReportBytes(EnvelopeId id, EnvelopeId inReplyTo, CancellationToken ct, MachineName? from = null, MachineName? to = null, KeyId? signWith = null, DateTimeOffset? created = null) =>
        TestEnvelopes.SignedReportBytesAsync(from ?? HomeLaptop, to ?? Central, inReplyTo, _both, signWith ?? TestKeys.Acme1, "ok\n", ct, id: id, created: created ?? T);

    private async Task<byte[]> Context(EnvelopeId id, CancellationToken ct)
    {
        var header = new EnvelopeHeader(Acme, id, HomeLaptop, Central, T);
        Model context = Model.CreateContext(header, new ContextFields(new ContextScope(ContextScopeKind.General, null)), "- a fact\n"u8.ToArray());
        EnvelopeResult signed = await new EnvelopeSigner(_both).SignAsync(context, TestKeys.Acme1, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        return EnvelopeWriter.Serialize(signed.Envelope!);
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

    private static byte[] Replace(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread (the BCL's posts to the synchronization context).</summary>
    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
