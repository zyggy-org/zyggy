using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary>
/// Spec 58 slice 4, AC-8 and AC-14 (Central side, D-Q15, D-Q16): the nightly dream files a work mail line the work laptop shared —
/// picked up into the inbox, body canary included — on the business side; a proposal that files it under <c>private/</c> is refused
/// (<c>path_refused</c>) and nothing reaches the private side, the repository or the ledger.
/// </summary>
public sealed class WorkMailContentDreamTests : IAsyncLifetime
{
    private const string Canary = "CANARY-BODY-7f3a";

    private readonly MemoryRepoFixture _repo = new();

    private string _head = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream-work-mail", Ct);
        _head = await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    // AC-8, AC-14: the work mail line, body included, lands under business/ and nowhere under private/.
    [Fact]
    public async Task Dream_WorkMailLine_FiledUnderBusiness_BodyCanaryNotOnThePrivateSide()
    {
        // Act
        var run = await _repo.DreamAsync("dream-file-work-mail", Ct);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr + run.Stdout);
        (await _repo.ShowAsync("acme/alice/business/areas/q4-budget.md", Ct)).Should().Contain(Canary);
        PrivateFilesHolding(Canary).Should().BeEmpty();
    }

    // AC-8: a filing of it under private/ is refused; nothing is committed and no private file holds it.
    [Fact]
    public async Task Dream_WorkMailLineIntoPrivate_ExitFivePathRefused_NothingWritten()
    {
        // Act
        var run = await _repo.DreamAsync("dream-file-work-mail-private", Ct);

        // Assert
        run.ExitCode.Should().Be(5, run.Stderr + run.Stdout);
        var record = DreamRunRecordStore.ReadLast(_repo.StateDir)!;
        (record.Outcome, record.Check, await _repo.GitAsync(_repo.BareDir, ["rev-parse", "main"], Ct), PrivateFilesHolding(Canary).Count)
            .Should().Be(("aborted", "path_refused", _head, 0));
    }

    private List<string> PrivateFilesHolding(string text) =>
        [.. Directory.EnumerateFiles(Path.Combine(_repo.PrincipalDir, "private"), "*", SearchOption.AllDirectories).Where(f => File.ReadAllText(f).Contains(text, StringComparison.Ordinal))];
}
