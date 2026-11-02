using System.Text;

using Microsoft.Extensions.Logging;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.M365;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 41 AC-29, AC-31, AC-32, AC-36, AC-37 (plan 41 Step 11, A12–A15): Central's pickup fetches a <c>via: onedrive</c> item read-only
/// through the real <see cref="GraphReader"/> over the stub Graph handler and the in-memory OneDrive, with a fake clock — one lookup per
/// item per minute, a 429 holding every fetch until <c>Retry-After</c>, <c>drive_missing</c> only on a 404 after the wait.
/// </summary>
public sealed class ItemDriveFetcherTests : IDisposable
{
    private const string DrivePath = "Zyggy/archive/calizr/staging-restore-notes.txt";

    private const string Lookup = "root:/Zyggy/archive/calizr/staging-restore-notes\\.txt\\?";

    private static readonly DateTimeOffset T = new(2026, 10, 31, 14, 5, 0, TimeSpan.Zero);

    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("# Restore\n\nSteps for the staging restore.\n");

    private readonly GraphFixture _graph = new(retries: 0);

    private readonly StubOneDrive _drive = new();

    private readonly RecordingLogger<BusPickup> _log = new();

    private readonly string _project;

    public ItemDriveFetcherTests()
    {
        _graph.Clock.SetUtcNow(T);
        _graph.Stub.ReadDrive(_drive);
        _project = _drive.Add(StubOneDrive.RootId, "calizr");
    }

    public static TheoryData<string, LogLevel> TransientCauses() => new()
    {
        { "server_error", LogLevel.Warning },
        { "transport_error", LogLevel.Warning },
        { "forbidden", LogLevel.Error },
    };

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Stub.Requests.Should().NotContain(r => r.Method != HttpMethod.Get && r.Uri.Host != "login.microsoftonline.com");
        _graph.Dispose();
    }

    private int Lookups => _graph.Stub.To(Lookup).Count();

    private int ContentRequests => _graph.Stub.To(":/content").Count();

    [Fact]
    public async Task FetchAsync_FilePresent_SizeAndShaMatch_Fetched()
    {
        // Arrange
        _drive.Add(_project, "staging-restore-notes.txt", Notes);

        // Act
        DriveFetch fetch = await Fetcher().FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().BeOfType<DriveFetched>().Which.Should().Match<DriveFetched>(f => f.Bytes.SequenceEqual(Notes) && f.DrivePath == DrivePath);
        _drive.Downloads.Should().Be(1);
    }

    [Fact]
    public async Task FetchAsync_404_WaitsLoggedOnce_SecondCallWithinMinute_NotDue_NoGraphRequest_AfterMinute_OneLookup()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        ItemDriveFetcher fetcher = Fetcher();
        Model item = Item();

        // Act
        DriveFetch first = await fetcher.FetchAsync(item, ct);
        int afterFirst = _graph.Stub.Requests.Count;
        _graph.Clock.Advance(TimeSpan.FromSeconds(59));
        DriveFetch second = await fetcher.FetchAsync(item, ct);
        int afterSecond = _graph.Stub.Requests.Count;
        _graph.Clock.Advance(TimeSpan.FromSeconds(1));
        DriveFetch third = await fetcher.FetchAsync(item, ct);

        // Assert
        first.Should().Be(new DriveWaiting("missing"));
        second.Should().BeOfType<DriveNotDue>();
        afterSecond.Should().Be(afterFirst);
        third.Should().Be(new DriveWaiting("missing"));
        Lookups.Should().Be(2);
        _log.Messages.Should().Equal($"pickup: item {item.Header.Id.Value} waits for OneDrive");
        _log.Entries.Single().Level.Should().Be(LogLevel.Information);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(1)]
    public async Task FetchAsync_404AfterWaitHours_DriveMissing(int hours)
    {
        // Arrange
        _graph.Clock.Advance(TimeSpan.FromHours(hours));

        // Act
        DriveFetch fetch = await Fetcher(new ArchiveOptions { ItemDriveWaitHours = hours }).FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().Be(new DriveRefused("drive_missing", $"not on OneDrive after {hours} h", DrivePath));
    }

    [Fact]
    public async Task FetchAsync_404JustBeforeWaitHours_StillWaits()
    {
        // Arrange
        _graph.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));

        // Act
        DriveFetch fetch = await Fetcher().FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().Be(new DriveWaiting("missing"));
    }

    [Fact]
    public async Task FetchAsync_MetadataSizeDiffers_DriveMismatchSize_NoDownload()
    {
        // Arrange
        _drive.Add(_project, "staging-restore-notes.txt", [.. Notes, (byte)'x']);

        // Act
        DriveFetch fetch = await Fetcher().FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().Be(new DriveRefused("drive_mismatch", "size", DrivePath));
        ContentRequests.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_MetadataSizeOverCentralCap_TooLarge_NoDownload()
    {
        // Arrange
        _drive.Add(_project, "staging-restore-notes.txt", Notes);

        // Act
        DriveFetch fetch = await Fetcher(new ArchiveOptions { ItemMaxBytes = 10, ProjectMaxBytes = 10, TotalMaxBytes = 10 }).FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().Be(new DriveRefused("too_large", $"{Notes.Length} > 10", DrivePath));
        ContentRequests.Should().Be(0);
    }

    [Fact]
    public async Task FetchAsync_DownloadedShaDiffers_DriveMismatchSha256()
    {
        // Arrange: the same length, different bytes (edited in OneDrive before Central fetched it).
        byte[] edited = [.. Notes];
        edited[2] = (byte)'r';
        _drive.Add(_project, "staging-restore-notes.txt", edited);

        // Act
        DriveFetch fetch = await Fetcher().FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().Be(new DriveRefused("drive_mismatch", "sha256", DrivePath));
        _drive.Downloads.Should().Be(1);
    }

    [Fact]
    public async Task FetchAsync_429WithRetryAfter_HoldsEveryItemUntilThen()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _drive.Add(_project, "staging-restore-notes.txt", Notes);
        _graph.Stub.Once("GET", Lookup, 429, "{}", "Retry-After: 120");
        ItemDriveFetcher fetcher = Fetcher();
        Model first = Item();
        Model second = Item();

        // Act
        DriveFetch throttled = await fetcher.FetchAsync(first, ct);
        int requests = _graph.Stub.Requests.Count;
        DriveFetch held = await fetcher.FetchAsync(second, ct);
        _graph.Clock.Advance(TimeSpan.FromSeconds(119));
        DriveFetch stillHeld = await fetcher.FetchAsync(second, ct);
        int requestsWhileHeld = _graph.Stub.Requests.Count;
        _graph.Clock.Advance(TimeSpan.FromSeconds(1));
        DriveFetch released = await fetcher.FetchAsync(second, ct);

        // Assert
        throttled.Should().Be(new DriveWaiting("throttled"));
        held.Should().BeOfType<DriveNotDue>();
        stillHeld.Should().BeOfType<DriveNotDue>();
        requestsWhileHeld.Should().Be(requests);
        released.Should().BeOfType<DriveFetched>();
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().StartWith("pickup: OneDrive fetch throttled");
        _graph.Waits.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TransientCauses))]
    public async Task FetchAsync_5xx_Network_403_Waits_OneLinePerCause_403IsError_NeverRefusedBeforeDeadline(string cause, LogLevel level)
    {
        // Arrange: past the deadline, so only a 404 could refuse — a transient failure never does (A12).
        var ct = TestContext.Current.CancellationToken;
        _graph.Clock.Advance(TimeSpan.FromHours(25));
        FailLookup(cause);
        FailLookup(cause);
        ItemDriveFetcher fetcher = Fetcher();
        Model item = Item();

        // Act
        DriveFetch first = await fetcher.FetchAsync(item, ct);
        _graph.Clock.Advance(TimeSpan.FromMinutes(1));
        DriveFetch second = await fetcher.FetchAsync(item, ct);

        // Assert
        first.Should().Be(new DriveWaiting(cause));
        second.Should().Be(new DriveWaiting(cause));
        Lookups.Should().Be(2);
        _log.Entries.Should().ContainSingle().Which.Should().Match<LogEntry>(e => e.Level == level
            && e.Message.StartsWith($"pickup: OneDrive fetch failing for item {item.Header.Id.Value}: {cause}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_ReaderNull_Or_SignInFails_NotConfigured_ReaderNull()
    {
        // Arrange
        var fetcher = new ItemDriveFetcher(() => (null, "configuration error: ZYGGY_INSTANCE_DIR is not set"), new ArchiveOptions(), _graph.Clock, _log);

        // Act
        DriveFetch fetch = await fetcher.FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().Be(new DriveNotConfigured("configuration error: ZYGGY_INSTANCE_DIR is not set"));
        _graph.Stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_ReaderNull_Or_SignInFails_NotConfigured_SignInFails()
    {
        // Arrange
        _graph.Stub.Once("POST", "oauth2/v2\\.0/token$", 401, File.ReadAllText(StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        DriveFetch fetch = await Fetcher().FetchAsync(Item(), TestContext.Current.CancellationToken);

        // Assert
        fetch.Should().BeOfType<DriveNotConfigured>().Which.Detail.Should().NotBeNullOrEmpty();
        _graph.Stub.Requests.Should().NotContain(r => r.Uri.Host == "graph.microsoft.com");
    }

    private StubGraphHandler FailLookup(string cause) => cause switch
    {
        "server_error" => _graph.Stub.Once("GET", Lookup, 500, """{"error":{"code":"generalException"}}"""),
        "transport_error" => _graph.Stub.OnceThrow("GET", Lookup, "Connection reset by peer"),
        _ => _graph.Stub.Once("GET", Lookup, 403, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json"))),
    };

    private ItemDriveFetcher Fetcher(ArchiveOptions? options = null) =>
        new(() => (_graph.Reader, null), options ?? new ArchiveOptions(), _graph.Clock, _log);

    private Model Item()
    {
        var id = EnvelopeId.New(_graph.Clock.GetUtcNow());
        var header = new EnvelopeHeader(TenantId.Parse("acme"), id, MachineName.Parse("home-laptop"), MachineName.Central, T);
        var fields = new ItemFields(
            Slug.Parse("calizr"),
            Slug.Parse("staging-restore-notes"),
            "Staging restore notes",
            "How the staging database was restored",
            "restore-notes.md",
            Notes.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Notes)),
            ItemRoute.OneDrive,
            "staging-restore-notes.txt");
        _graph.Clock.Advance(TimeSpan.FromMilliseconds(1));
        return Model.CreateItem(header, fields, ReadOnlyMemory<byte>.Empty);
    }
}
