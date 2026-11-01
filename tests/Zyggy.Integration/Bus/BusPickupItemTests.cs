using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Bus;

/// <summary>
/// Spec 41 AC-28, AC-30, AC-33, AC-35, AC-38..AC-40 against a bare bus repository and a memory repository with a bare remote, with the
/// real git (plan 41 Step 10): Central's pickup files a <c>via: bus</c> item through the one archive writer, pushes the memory commit,
/// then removes the <c>items/</c> file and adds its signed report in one bus commit.
/// </summary>
public sealed class BusPickupItemTests : IAsyncLifetime
{
    private const string Fact = "- [stated] 2026-10-31 (project:calizr): The staging database is restored from Sunday's snapshot\n";

    private const string AwsKey = "AKIAABCDEFGHIJKLMNOP";

    private const string Item = "archive/calizr/staging-restore-notes.txt";

    private const string Sidecar = "archive/calizr/staging-restore-notes.md";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Created = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Staging restore\n\nRestore the staging database from Sunday's snapshot, then rerun the migrations.\n");

    private readonly BusRepoFixture _bus = new(Acme);

    private readonly HookedProcessRunner _hooks = TestHost.Hooks();

    private readonly RecordingLoggerProvider _log = new();

    private readonly PickupArchiveMemory _memory = new();

    private InMemorySecretStore _store = null!;

    private ServiceProvider? _services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _bus.InitializeAsync();
        await _bus.SeedTenantAsync(Acme, [Central, HomeLaptop, WorkLaptop], CancellationToken.None);
        await _memory.InitializeAsync();
        _store = await TestHost.StoreAsync(Acme, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _bus.DisposeAsync();
        await _memory.DisposeAsync();
    }

    [Fact]
    public async Task RunAsync_BusItemFromHomeLaptop_FiledPushedOnMemoryRemote_ItemsRemovedAndReportInOneBusCommit()
    {
        // Arrange
        SeededJob item = await SeedItemAsync(Notes);

        // Act
        PickupResult result = await PassAsync();

        // Assert: memory — item, sidecar and the archive add commit on the bare remote; the index line in the inbox.
        result.ItemsFiled.Should().Be(1);
        result.Archive.Should().Be(new Pushed(1));
        (await _memory.Repo.ShowBytesAsync("acme/alice/" + Item, Ct)).Should().Equal(Notes);
        string sidecar = await _memory.Repo.ShowAsync("acme/alice/" + Sidecar, Ct);
        sidecar.Should().Contain("source_name: restore-notes.md").And.Contain("media_type: text/markdown").And.Contain("sha256: " + Sha(Notes));
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "-1", "--format=%B", "main"], Ct)).Should().Be(
            ArchiveCommitMessage.Add(Slug.Parse("calizr"), Slug.Parse("staging-restore-notes"), "txt", ArchiveMediaType.TextMarkdown, Notes.Length, Sha(Notes)).TrimEnd('\n'));
        InboxText().Should().Contain($"(project:calizr): Archived \"Staging restore notes\" (text/markdown, {Notes.Length} B) at {Item} — How the staging database was restored");

        // Assert: bus — the items/ file gone and Central's report added, in one commit.
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (await _bus.LastCommitSubjectAsync(Ct)).Should().Be("acme archive 1 central→archive");
        string body = (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%b", "main"], Ct)).Trim();
        body.Should().Be($"{item.RelativePath} → filed {Item}");
        (string reportPath, Model report) = await SingleReportAsync();
        (await _bus.RunGitAsync(_bus.BareDir, ["show", "--name-only", "--format=", "main"], Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().BeEquivalentTo(item.RelativePath, reportPath);
        report.Header.From.Should().Be(Central);
        report.Header.To.Should().Be(HomeLaptop);
        report.Header.InReplyTo.Should().Be(item.Id);
        report.Report!.Status.Should().Be(ReportStatus.Done);
        string memorySha = await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["rev-parse", "main"], Ct);
        report.BodyText.Should().Be($"archived: {Item}\nsidecar: {Sidecar}\n{IndexLine()}\ncommit: {memorySha} pushed\n");
        _log.AtLevel(LogLevel.Information).Where(e => e.Category.EndsWith(nameof(BusPickup), StringComparison.Ordinal)).Should().ContainSingle();
        TestContext.Current.TestOutputHelper?.WriteLine($"bus commit:\n{await _bus.LastCommitSubjectAsync(Ct)}\n\n{body}\n\nreport {reportPath}:\n{await _bus.ShowAsync(reportPath, Ct)}");
    }

    [Fact]
    public async Task RunAsync_TypeNotAllowedByArchiveJson_FailedArchiveRefused_MemoryUntouched()
    {
        // Arrange
        await File.WriteAllTextAsync(Path.Combine(_memory.InstanceDir, "archive.json"), """{"allowed_types": ["application/pdf"]}""", Ct);
        SeededJob item = await SeedItemAsync(Notes);
        string before = await _memory.Repo.TreeFingerprintAsync(Ct);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsRefused.Should().Be(1);
        (await _memory.Repo.TreeFingerprintAsync(Ct)).Should().Be(before);
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (string reportPath, Model report) = await SingleReportAsync();
        report.Report!.Status.Should().Be(ReportStatus.Failed);
        report.Report.Reason.Should().Be("archive_refused");
        report.BodyText.Should().Be("archive: refused: type_not_allowed (text/markdown)\n");
        (await _bus.RunGitAsync(_bus.BareDir, ["log", "-1", "--format=%b", "main"], Ct)).Trim().Should().Be($"{item.RelativePath} → refused type_not_allowed");
        TestContext.Current.TestOutputHelper?.WriteLine($"report {reportPath}:\n{await _bus.ShowAsync(reportPath, Ct)}");
    }

    [Fact]
    public async Task RunAsync_CraftedSecretItemValidlySigned_FailedArchiveRefusedSecretPattern_SecretNowhere()
    {
        // Arrange: a validly signed item a checked laptop would never send.
        await SeedItemAsync(Encoding.UTF8.GetBytes($"Staging notes.\nkey {AwsKey}\n"));
        string before = await _memory.Repo.TreeFingerprintAsync(Ct);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsRefused.Should().Be(1);
        (await _memory.Repo.TreeFingerprintAsync(Ct)).Should().Be(before);
        (_, Model report) = await SingleReportAsync();
        report.Report!.Reason.Should().Be("archive_refused");
        report.BodyText.Should().Be("archive: refused: secret_pattern aws-access-key (line 2)\n");
        (await BusMainTextsAsync()).Should().NotBeEmpty().And.OnlyContain(t => !t.Contains(AwsKey, StringComparison.Ordinal));
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "--all", "--format=%H"], Ct)).Should().NotBeEmpty();
        Directory.EnumerateFiles(_memory.Repo.CloneDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Should().OnlyContain(f => !File.ReadAllText(f).Contains(AwsKey, StringComparison.Ordinal));
        _log.Messages.Should().NotContain(m => m.Contains(AwsKey, StringComparison.Ordinal));
        SecretSweep.AssertAbsent(_log.Messages);
    }

    [Fact]
    public async Task RunAsync_TamperedItem_And_WrongTenantItem_LeftOnMain_NoReport_NoMemoryCommit_WarningOnce()
    {
        // Arrange
        SeededJob tampered = await SeedItemAsync(Notes, mutate: FlipSig);
        SeededJob wrongTenant = await SeedItemAsync(Notes, mutate: b => Replace(b, "tenant: acme", "tenant: other-org"));
        string memoryHead = await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["rev-parse", "main"], Ct);

        // Act
        PickupResult first = await PassAsync();
        await PassAsync();

        // Assert
        first.Left.Should().Be(2);
        (await _bus.ExistsOnMainAsync(tampered.RelativePath, Ct)).Should().BeTrue();
        (await _bus.ExistsOnMainAsync(wrongTenant.RelativePath, Ct)).Should().BeTrue();
        (await ReportPathsAsync()).Should().BeEmpty();
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["rev-parse", "main"], Ct)).Should().Be(memoryHead);
        _memory.Exists(Item).Should().BeFalse();
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().BeEquivalentTo(
            $"pickup: left {tampered.RelativePath}: invalid_signature (sig)",
            $"pickup: left {wrongTenant.RelativePath}: tenant_mismatch (tenant)");
    }

    [Fact]
    public async Task RunAsync_MemoryDetachedHead_ItemWaits_NoReport_ContextStillArchived_ThenOnBranch_FiledOnce()
    {
        // Arrange
        await _memory.Repo.GitAsync(_memory.Repo.CloneDir, ["checkout", "-q", "--detach"], Ct);
        SeededJob item = await SeedItemAsync(Notes);
        SeededJob context = await _bus.SeedSignedContextAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fact, created: Created, ct: Ct);

        // Act
        PickupResult waiting = await PassAsync();
        await _memory.Repo.GitAsync(_memory.Repo.CloneDir, ["checkout", "-q", "main"], Ct);
        PickupResult filed = await PassAsync();

        // Assert
        waiting.ItemsWaiting.Should().Be(1);
        waiting.Inboxed.Should().Be(1);
        (await _bus.ExistsOnMainAsync(context.RelativePath, Ct)).Should().BeFalse();
        filed.ItemsFiled.Should().Be(1);
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (await ReportPathsAsync()).Should().ContainSingle();
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "--format=%s", "main"], Ct)).Split('\n')
            .Count(s => s == "archive add calizr/staging-restore-notes.txt").Should().Be(1);
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Equal($"pickup: item {item.Id} waits: memory not_on_branch");
    }

    [Fact]
    public async Task RunAsync_BusCommitRefused_NextPass_AlreadyFiled_DoneReportOnce_NoSecondIndexLine()
    {
        // Arrange: a pre-commit hook in Central's bus checkout refuses the first pass commit, after the memory commit was pushed.
        SeededJob item = await SeedItemAsync(Notes);
        string hook = Path.Combine(_bus.CloneDir, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 1\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        PickupResult refused = await PassAsync();
        File.Delete(hook);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        refused.Archive.Should().BeOfType<WriteFailed>();
        result.ItemsFiled.Should().Be(1);
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (_, Model report) = await SingleReportAsync();
        report.Report!.Status.Should().Be(ReportStatus.Done);
        report.BodyText.Should().Be($"archived: {Item}\nsidecar: {Sidecar}\nalready filed\n");
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "--format=%s", "main"], Ct)).Split('\n')
            .Count(s => s == "archive add calizr/staging-restore-notes.txt").Should().Be(1);
        InboxText().Split('\n').Count(l => l.Contains("Archived \"Staging restore notes\"", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_TwoItemsSameBytes_OneArchivedItemTwoDoneReports()
    {
        // Arrange: the laptop re-ran the same archive call after exit 7, so two items/ envelopes carry the same file.
        SeededJob first = await SeedItemAsync(Notes);
        SeededJob second = await SeedItemAsync(Notes, created: Created.AddMinutes(2));

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsFiled.Should().Be(2);
        (await _bus.ExistsOnMainAsync(first.RelativePath, Ct)).Should().BeFalse();
        (await _bus.ExistsOnMainAsync(second.RelativePath, Ct)).Should().BeFalse();
        IReadOnlyList<Model> reports = await ReportsAsync();
        reports.Select(r => r.Header.InReplyTo).Should().BeEquivalentTo([first.Id, second.Id]);
        reports.Should().OnlyContain(r => r.Report!.Status == ReportStatus.Done);
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["ls-tree", "-r", "--name-only", "main", "acme/alice/archive/"], Ct)).Split('\n')
            .Should().BeEquivalentTo("acme/alice/" + Sidecar, "acme/alice/" + Item);
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "--format=%s", "main"], Ct)).Split('\n')
            .Count(s => s.StartsWith("archive add ", StringComparison.Ordinal)).Should().Be(1);
        InboxText().Split('\n').Count(l => l.Contains("Archived \"Staging restore notes\"", StringComparison.Ordinal)).Should().Be(1);
        (await _bus.LastCommitSubjectAsync(Ct)).Should().Be("acme archive 2 central→archive");
    }

    [Fact]
    public async Task RunAsync_MemoryPushRejectedUnrecoverably_ReportEndsPushDeferred()
    {
        // Arrange: the remote already holds a different file at the item's path, so the one rebase conflicts.
        await _memory.Repo.PushFromSecondCloneAsync("acme/alice/" + Item, "written elsewhere\n", Ct);
        SeededJob item = await SeedItemAsync(Notes);

        // Act
        PickupResult result = await PassAsync();

        // Assert
        result.ItemsFiled.Should().Be(1);
        (await _bus.ExistsOnMainAsync(item.RelativePath, Ct)).Should().BeFalse();
        (_, Model report) = await SingleReportAsync();
        report.BodyText.Should().MatchRegex("commit: [0-9a-f]{40} push deferred\n$");
        _log.AtLevel(LogLevel.Warning).Select(e => e.Message).Should().Contain($"pickup: item {item.Id} filed; memory push deferred");
    }

    [Fact]
    public async Task RunAsync_SameFileArchivedOnCentralAndThroughBus_ItemSidecarIndexLineCommitMessageIdentical()
    {
        // Arrange: the same file archived by `memory archive add` in a second memory repository.
        await using var central = new PickupArchiveMemory();
        await central.InitializeAsync();
        string source = await central.Repo.WriteSourceAsync("restore-notes.md", Notes);
        ZyggyRun add = await central.Repo.ArchiveAsync(
            Ct, null, ["add", "--project", "calizr", "--name", "Staging restore notes", "--description", "How the staging database was restored", "--file", source, "--slug", "staging-restore-notes"]);
        add.ExitCode.Should().Be(0, add.Stderr + add.Stdout);
        await SeedItemAsync(Notes);

        // Act
        await PassAsync();

        // Assert
        (await _memory.Repo.ShowBytesAsync("acme/alice/" + Item, Ct)).Should().Equal(await central.Repo.ShowBytesAsync("acme/alice/" + Item, Ct));
        (await _memory.Repo.ShowBytesAsync("acme/alice/" + Sidecar, Ct)).Should().Equal(await central.Repo.ShowBytesAsync("acme/alice/" + Sidecar, Ct));
        InboxText().Should().Be(File.ReadAllText(Directory.EnumerateFiles(central.Paths.InboxDirectory).Single()));
        (await _memory.Repo.GitAsync(_memory.Repo.BareDir, ["log", "-1", "--format=%B", "main"], Ct))
            .Should().Be(await central.Repo.GitAsync(central.Repo.BareDir, ["log", "-1", "--format=%B", "main"], Ct));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Replace(byte[] bytes, string from, string to) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from, to, StringComparison.Ordinal));

    private static byte[] FlipSig(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        int at = text.IndexOf("hmac-sha256:", text.IndexOf("\nsig: ", StringComparison.Ordinal), StringComparison.Ordinal) + "hmac-sha256:".Length;
        char c = text[at] == '0' ? '1' : '0';
        return Encoding.UTF8.GetBytes(text[..at] + c + text[(at + 1)..]);
    }

    private static ItemFields Fields(byte[] file) => new(
        Slug.Parse("calizr"),
        Slug.Parse("staging-restore-notes"),
        "Staging restore notes",
        "How the staging database was restored",
        "restore-notes.md",
        file.Length,
        Sha(file),
        ItemRoute.Bus,
        null);

    private static string IndexLine() =>
        $"- [stated] {DateTime.UtcNow:yyyy-MM-dd} (project:calizr): Archived \"Staging restore notes\" (text/markdown, {Notes.Length} B) at {Item} — How the staging database was restored";

    private string InboxText() => File.ReadAllText(Directory.EnumerateFiles(_memory.Paths.InboxDirectory).Single());

    private Task<SeededJob> SeedItemAsync(byte[] file, Func<byte[], byte[]>? mutate = null, DateTimeOffset? created = null) =>
        _bus.SeedSignedItemAsync(HomeLaptop, Central, TestKeys.Acme1, _store, Fields(file), file, created ?? Created, mutate, Ct);

    private async Task<List<string>> BusMainTextsAsync()
    {
        var texts = new List<string>();
        foreach (string path in (await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "-r", "--name-only", "main"], Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            texts.Add(await _bus.ShowAsync(path, Ct));
        }

        return texts;
    }

    private async Task<IReadOnlyList<string>> ReportPathsAsync()
    {
        string folder = BusPaths.Reports(Acme, Central);
        string listing = await _bus.RunGitAsync(_bus.BareDir, ["ls-tree", "--name-only", "main", folder + "/"], Ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal))];
    }

    private async Task<IReadOnlyList<Model>> ReportsAsync()
    {
        var signer = new EnvelopeSigner(_store);
        var reports = new List<Model>();
        foreach (string path in await ReportPathsAsync())
        {
            EnvelopeResult verified = await signer.ParseAndVerifyAsync(await _bus.ShowBytesAsync(path, Ct), Acme, Ct);
            verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
            reports.Add(verified.Envelope!);
        }

        return reports;
    }

    private async Task<(string Path, Model Report)> SingleReportAsync()
    {
        string path = (await ReportPathsAsync()).Should().ContainSingle().Subject;
        return (path, (await ReportsAsync()).Single());
    }

    private async Task<PickupResult> PassAsync()
    {
        _services ??= TestHost.Build(_bus, Central, _store, _hooks, logging: _log, extend: s =>
        {
            s.AddSingleton(TestKeys.Acme1);
            s.AddBusPickup(_memory.Configuration(TestHost.NodeConfigurationFor(_bus, Central)));
        });
        await _bus.PullCloneAsync(Ct);
        return await _services.GetRequiredService<BusPickup>().RunAsync(Ct);
    }
}
