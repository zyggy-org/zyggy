using System.Text;

using Zyggy.Core.Secrets;
using Zyggy.Core.Setup;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 AC-19/AC-22: every needed secret named present with its fingerprint or missing with the command that sets it; never a value.</summary>
public sealed class SecretReportTests : IDisposable
{
    private readonly SetupTestFiles _files = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task ReportAsync_BothPresent_TwoAlreadyDoneLinesWithFingerprints_NoSpikeLine()
    {
        // Arrange
        InMemorySecretStore store = await StoreAsync(signingKey: true, pat: true);

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme1, SecretStoreKind.Os, Ct);

        // Assert
        lines.Should().Equal(
            new SetupLine(SetupItem.SecretSigningKey, SetupOutcome.AlreadyDone, "present (fingerprint " + SecretFingerprint.Of(TestKeys.Secret1) + ")"),
            new SetupLine(SetupItem.SecretGitHubPat, SetupOutcome.AlreadyDone, "present (fingerprint " + SecretFingerprint.Of(Encoding.UTF8.GetBytes(TestKeys.TestPat)) + ")"));
    }

    [Fact]
    public async Task ReportAsync_SigningKeyMissing_MissingLineNamesSecretSetHmacNHex()
    {
        // Arrange
        InMemorySecretStore store = await StoreAsync(signingKey: false, pat: true);

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme1, SecretStoreKind.Os, Ct);

        // Assert
        lines[0].Should().Be(new SetupLine(SetupItem.SecretSigningKey, SetupOutcome.Missing, "run: zyggy secret set hmac/1 --hex"));
    }

    [Fact]
    public async Task ReportAsync_OnlyPatMissing_MissingLineNamesSecretSetGithubPat()
    {
        // Arrange
        InMemorySecretStore store = await StoreAsync(signingKey: true, pat: false);

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme1, SecretStoreKind.Os, Ct);

        // Assert
        lines[0].Outcome.Should().Be(SetupOutcome.AlreadyDone);
        lines[1].Should().Be(new SetupLine(SetupItem.SecretGitHubPat, SetupOutcome.Missing, "run: zyggy secret set github-pat"));
    }

    [Fact]
    public async Task ReportAsync_SpikeFolderPresentAndStoreOs_OneInfoLine_NothingReadFromIt()
    {
        // Arrange: the spike folder holds a file whose content is a needle; it must never be read into any line
        string spike = SetupPaths.SpikeStore(_files.LocalAppData, SetupTestFiles.Acme);
        Directory.CreateDirectory(spike);
        await File.WriteAllTextAsync(Path.Combine(spike, "hmac-1.key"), TestKeys.TestPat, Ct);
        InMemorySecretStore store = await StoreAsync(signingKey: false, pat: false);

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme1, SecretStoreKind.Os, Ct);

        // Assert
        lines.Should().HaveCount(3);
        lines[2].Should().Be(new SetupLine(SetupItem.SpikeFileStore, SetupOutcome.LeftAsIs, $"a spike file store exists at {spike}; its keys are retired (rotate-hmac.md step 0) and not imported"));
        SecretSweep.AssertAbsent(lines.Select(l => l.Render(TestKeys.Acme1)));
        File.Exists(Path.Combine(spike, "hmac-1.key")).Should().BeTrue();
    }

    [Fact]
    public async Task ReportAsync_SpikeFolderPresentAndStoreFile_NoSpikeLine()
    {
        // Arrange
        Directory.CreateDirectory(SetupPaths.SpikeStore(_files.LocalAppData, SetupTestFiles.Acme));
        InMemorySecretStore store = await StoreAsync(signingKey: true, pat: true);

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme1, SecretStoreKind.File, Ct);

        // Assert
        lines.Select(l => l.Item).Should().Equal(SetupItem.SecretSigningKey, SetupItem.SecretGitHubPat);
    }

    [Fact]
    public async Task ReportAsync_StoreUnavailable_FailedLinesWithDetail_NoThrow()
    {
        // Arrange
        var store = new ThrowingSecretStore();

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme1, SecretStoreKind.Os, Ct);

        // Assert
        lines.Should().Equal(
            new SetupLine(SetupItem.SecretSigningKey, SetupOutcome.Failed, "secret store unavailable: credential-manager: 1312 No such logon session"),
            new SetupLine(SetupItem.SecretGitHubPat, SetupOutcome.Failed, "secret store unavailable: credential-manager: 1312 No such logon session"));
    }

    [Fact]
    public async Task ReportAsync_NoValueInAnyLine_SecretSweep()
    {
        // Arrange
        InMemorySecretStore store = await TestKeys.StoreWithBothAsync(Ct);
        await store.SetAsync(SetupTestFiles.Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);

        // Act
        IReadOnlyList<SetupLine> lines = await SecretReport.ReportAsync(store, _files.Request(), TestKeys.Acme2, SecretStoreKind.Os, Ct);

        // Assert
        lines.Should().HaveCount(2);
        SecretSweep.AssertAbsent(lines.Select(l => l.Render(TestKeys.Acme2)).Concat(lines.Select(l => l.Detail)));
    }

    private static async Task<InMemorySecretStore> StoreAsync(bool signingKey, bool pat)
    {
        var store = new InMemorySecretStore();
        if (signingKey)
        {
            await store.SetAsync(SetupTestFiles.Acme, TestKeys.Acme1.SecretName, TestKeys.Secret1, Ct);
        }

        if (pat)
        {
            await store.SetAsync(SetupTestFiles.Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), Ct);
        }

        return store;
    }
}
