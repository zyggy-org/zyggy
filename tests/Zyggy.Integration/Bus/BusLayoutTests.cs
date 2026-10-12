using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 04 AC-17 (arrange): the test bus is seeded through the production <see cref="BusPaths"/> and every assertion reads
/// the bare repository, never the clone.
/// </summary>
public sealed class BusLayoutTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly BusRepoFixture _bus = new(Acme);

    public ValueTask InitializeAsync() => _bus.InitializeAsync();

    public ValueTask DisposeAsync() => _bus.DisposeAsync();

    [Fact]
    public async Task SeedTenantAsync_Acme_CreatesEveryFolderOfTheSpecLayoutOnMain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        var tree = (await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "-r", "--name-only", "main"], ct)).Split('\n').Order(StringComparer.Ordinal).ToList();

        // Assert: the §4 layout, derived from BusPaths member by member
        var expected = new List<string> { BusPaths.Protocol, Path.GetDirectoryName(BusPaths.Registry(Acme, Central))!.Replace('\\', '/') + "/.gitkeep", BusPaths.Archive(Acme, 2026, 9) + "/.gitkeep" };
        foreach (MachineName machine in new[] { Central, HomeLaptop })
        {
            expected.Add(BusPaths.Jobs(Acme, machine) + "/.gitkeep");
            expected.Add(BusPaths.Claimed(Acme, machine) + "/.gitkeep");
            expected.Add(BusPaths.Rejected(Acme, machine) + "/.gitkeep");
            expected.Add(BusPaths.Reports(Acme, machine) + "/.gitkeep");
            expected.Add(BusPaths.Context(Acme, machine) + "/.gitkeep");
        }

        tree.Should().Equal(expected.Order(StringComparer.Ordinal));
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be("seed acme");
    }

    [Fact]
    public async Task SeedTenantAsync_Globex_NeverTouchesAcme()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var globex = TenantId.Parse("globex");
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        var acmeBefore = await _bus.RunGitAsync(_bus.BareDir, ["rev-parse", "main:" + BusPaths.Tenant(Acme)], ct);

        // Act
        await _bus.SeedTenantAsync(globex, [MachineName.Parse("x")], ct);

        // Assert: the acme tree object is unchanged; the globex prefix exists beside it
        (await _bus.RunGitAsync(_bus.BareDir, ["rev-parse", "main:" + BusPaths.Tenant(Acme)], ct)).Should().Be(acmeBefore);
        (await _bus.ExistsOnMainAsync(BusPaths.Jobs(globex, MachineName.Parse("x")) + "/.gitkeep", ct)).Should().BeTrue();
    }

    [Fact]
    public async Task SeedSignedJobAsync_ToHomeLaptop_FileOnMainParsesAndVerifiesWithAcme1()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        var store = await TestKeys.StoreWithBothAsync(ct);
        var signer = new EnvelopeSigner(store);

        // Act
        SeededJob job = await _bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, store, "probe body\n", ct: ct);
        byte[] onMain = await _bus.ShowBytesAsync(job.RelativePath, ct);
        EnvelopeResult result = await signer.ParseAndVerifyAsync(onMain, Acme, ct);

        // Assert
        job.RelativePath.Should().Be(BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), job.Id));
        onMain.Should().Equal(job.Bytes);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.Header.To.Should().Be(HomeLaptop);
        result.Envelope.Header.From.Should().Be(Central);
        result.Envelope.Header.Id.Should().Be(job.Id);
        result.Envelope.Job!.Project.Should().Be("probe");
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be($"acme job {job.Id} central→home-laptop");
    }

    [Fact]
    public async Task SeedSignedJobAsync_Mutated_FileOnMainIsTheMutatedBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        var store = await TestKeys.StoreWithBothAsync(ct);
        var signer = new EnvelopeSigner(store);

        // Act: flip one byte of the body after signing
        SeededJob job = await _bus.SeedSignedJobAsync(
            Central,
            HomeLaptop,
            TestKeys.Acme1,
            store,
            "probe body\n",
            mutate: bytes => { bytes[^2] ^= 0x01; return bytes; },
            ct: ct);
        byte[] onMain = await _bus.ShowBytesAsync(job.RelativePath, ct);
        EnvelopeResult result = await signer.ParseAndVerifyAsync(onMain, Acme, ct);

        // Assert
        onMain.Should().Equal(job.Bytes);
        result.IsAccepted.Should().BeFalse();
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidSignature);
    }

    [Fact]
    public async Task SeedRawFileAsync_NotAnEnvelope_IsOnMainByteForByte()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        byte[] raw = Encoding.UTF8.GetBytes("not an envelope\n");
        string path = BusPaths.Jobs(Acme, HomeLaptop) + "/" + EnvelopeId.New(DateTimeOffset.UnixEpoch).Value + ".md";

        // Act
        await _bus.SeedRawFileAsync(path, raw, "acme raw", ct);

        // Assert
        (await _bus.ShowBytesAsync(path, ct)).Should().Equal(raw);
        (await _bus.LastCommitSubjectAsync(ct)).Should().Be("acme raw");
    }

    [Fact]
    public async Task AdvanceMainFromSenderAsync_MovesHeadSha()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _bus.SeedTenantAsync(Acme, [Central], ct);
        string before = await _bus.HeadShaAsync(ct);

        // Act
        await _bus.AdvanceMainFromSenderAsync(ct);

        // Assert: one more commit on main, the SUT clone untouched (it has not pulled)
        (await _bus.HeadShaAsync(ct)).Should().NotBe(before);
        (await _bus.RevListCountAsync("HEAD..origin/main", ct)).Should().Be(0, "the clone has not fetched yet");
        await _bus.RunGitAsync(_bus.CloneDir, ["fetch", "origin"], ct);
        (await _bus.RevListCountAsync("HEAD..origin/main", ct)).Should().Be(1);
    }
}
