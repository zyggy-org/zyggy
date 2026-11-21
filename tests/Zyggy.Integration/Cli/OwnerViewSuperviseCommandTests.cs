using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 18 slice 13 through the real verbs over a bare repository: <c>zyggy supervise stop</c> on Central writes the owner's marker and
/// revokes every live mandate, also unattended (a kill switch); <c>start</c> removes the marker, attended only (AC-30). <c>zyggy work
/// --waiting</c> prints what waits for the owner as fixed lines, never a text an agent wrote (the owner's view).
/// </summary>
public sealed class OwnerViewSuperviseCommandTests : IAsyncLifetime
{
    private const string Project = "shop";

    private const string Marker = "CANARY-41f7-never-on-the-list";

    private static readonly Dictionary<string, string?> Unattended = new() { ["ZYGGY_HOOKS"] = "off" };

    private CliHarness _harness = null!;

    private string CentralMarker => SupervisionSwitch.MarkerPath(_harness.NodeDirOf("central"));

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        var registry = new RegistryDocument(CliHarness.Acme, CliHarness.HomeLaptop, RegistryOs.Windows, DateTimeOffset.UtcNow.AddMinutes(-1), null, null, [])
        {
            Capabilities = [RegistryDocument.SupervisionCapability],
        };
        await _harness.Bus.SeedRawFileAsync(BusPaths.Registry(CliHarness.Acme, CliHarness.HomeLaptop), registry.Serialize(), "seed: home-laptop registry", CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-30
    [Fact]
    public async Task SuperviseStop_OnCentral_RevokesEveryLiveMandate_AndWritesTheMarker()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string first = await GrantAsync(ct);
        string second = await GrantAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["supervise", "stop"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new
        {
            Exit = exit,
            Live = await FilesOnMainAsync(BusPaths.Mandates(CliHarness.Acme), ct),
            Revoked = (await FilesOnMainAsync(BusPaths.RevokedMandates(CliHarness.Acme), ct)).Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray(),
            Marker = File.Exists(CentralMarker),
        }.Should().BeEquivalentTo(new
        {
            Exit = 0,
            Live = Array.Empty<string>(),
            Revoked = new[] { first, second }.Order(StringComparer.Ordinal).ToArray(),
            Marker = true,
        }, console.Stdout + console.Stderr);
    }

    // AC-30: a kill switch also works from an unattended run.
    [Fact]
    public async Task SuperviseStop_Unattended_StillStops()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(ct);

        // Act
        var (exit, console) = await _harness.RunAsync(["supervise", "stop"], _harness.CentralNodeJson, extraEnvironment: Unattended, ct: ct);

        // Assert
        new { Exit = exit, Revoked = await FilesOnMainAsync(BusPaths.RevokedMandates(CliHarness.Acme), ct), Marker = File.Exists(CentralMarker) }
            .Should().BeEquivalentTo(new { Exit = 0, Revoked = new[] { BusPaths.EnvelopeFile(BusPaths.RevokedMandates(CliHarness.Acme), EnvelopeId.Parse(mandate)) }, Marker = true }, console.Stderr);
    }

    // AC-30
    [Fact]
    public async Task SuperviseStart_Attended_RemovesTheMarker()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (await _harness.RunAsync(["supervise", "stop"], _harness.CentralNodeJson, ct: ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await _harness.RunAsync(["supervise", "start"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Marker = File.Exists(CentralMarker) }.Should().BeEquivalentTo(new { Exit = 0, Marker = false }, console.Stderr);
    }

    // AC-30: only the owner starts again; an unattended run cannot lift the stop.
    [Fact]
    public async Task SuperviseStart_Unattended_RefusedExit3_MarkerKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        (await _harness.RunAsync(["supervise", "stop"], _harness.CentralNodeJson, ct: ct)).Exit.Should().Be(0);

        // Act
        var (exit, console) = await _harness.RunAsync(["supervise", "start"], _harness.CentralNodeJson, extraEnvironment: Unattended, ct: ct);

        // Assert
        new { Exit = exit, Marker = File.Exists(CentralMarker) }.Should().BeEquivalentTo(new { Exit = 3, Marker = true }, console.Stderr);
    }

    // The owner's view: a paused mandate is one fixed line, without any report text.
    [Fact]
    public async Task WorkWaiting_PausedMandate_OneFixedLine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        string mandate = await GrantAsync(ct, maxJobs: "10");
        for (var i = 3; i >= 1; i--)
        {
            await SeedFailedReportAsync(mandate, DateTimeOffset.UtcNow.AddMinutes(-5 * i), ct);
        }

        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--waiting"], _harness.CentralNodeJson, ct: ct);

        // Assert
        new { Exit = exit, Out = console.Stdout }.Should().BeEquivalentTo(new { Exit = 0, Out = "mandate, home-laptop, " + Project + ", " + mandate + ", 5 min\n" }, console.Stderr);
    }

    // The owner's view: an empty list says so.
    [Fact]
    public async Task WorkWaiting_NothingWaits_SaysSo()
    {
        // Act
        var (exit, console) = await _harness.RunAsync(["work", "--waiting"], _harness.CentralNodeJson, ct: TestContext.Current.CancellationToken);

        // Assert
        new { Exit = exit, Out = console.Stdout }.Should().BeEquivalentTo(new { Exit = 0, Out = "nothing waits for you\n" }, console.Stderr);
    }

    private async Task<string> GrantAsync(CancellationToken ct, string maxJobs = "3")
    {
        string[] args =
        [
            "mandate", "grant", "--to", "home-laptop", "--project", Project, "--goal", "Build the shop", "--spec", "_specs/12-shop.md", "--spec-commit", "0123456789abcdef0123456789abcdef01234567",
            "--allowed-tools", "Read,Edit", "--max-jobs", maxJobs, "--expires", "3d",
        ];
        var (exit, console) = await _harness.RunAsync(args, _harness.CentralNodeJson, ct: ct);
        exit.Should().Be(0, console.Stderr);
        return console.Stdout.Split(' ', 3)[1].Trim();
    }

    private Task<SeededJob> SeedFailedReportAsync(string mandate, DateTimeOffset created, CancellationToken ct) =>
        _harness.Bus.SeedSignedReportAsync(
            CliHarness.HomeLaptop,
            CliHarness.Central,
            EnvelopeId.New(created.AddMinutes(-1)),
            TestKeys.Acme1,
            _harness.Store,
            "claude_error: " + Marker + "\n",
            created: created,
            fields: new ReportFields(ReportStatus.Failed, "claude_error", Mandate: EnvelopeId.Parse(mandate), Step: "slice 1"),
            ct: ct);

    private async Task<IReadOnlyList<string>> FilesOnMainAsync(string folder, CancellationToken ct)
    {
        string listing = await _harness.Bus.RunGitAsync(_harness.Bus.BareDir, ["ls-tree", "-r", "--name-only", "main", "--", folder], ct);
        return [.. listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.EndsWith(".md", StringComparison.Ordinal) && string.Equals(p[..p.LastIndexOf('/')], folder, StringComparison.Ordinal))];
    }
}
