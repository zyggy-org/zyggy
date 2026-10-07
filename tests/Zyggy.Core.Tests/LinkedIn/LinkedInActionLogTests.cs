using System.Runtime.Versioning;

using Zyggy.Core.LinkedIn;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The action log (spec 36 AC-22): one line per call in the contract's key order, 0600 in 0700, locked appends, the 24 h lookup.</summary>
public sealed class LinkedInActionLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly LinkedInActionLog _log;
    private readonly LinkedInPaths _paths;

    public LinkedInActionLogTests()
    {
        _paths = new LinkedInPaths(new Dictionary<string, string?> { ["HOME"] = _root, ["ZYGGY_STATE_DIR"] = _root });
        _log = new LinkedInActionLog(_paths);
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Append_KeyOrderExact_OneLinePerCall()
    {
        // Act
        _log.Append(Row(LinkedInFixture.Now, "abc", ActionRow.Ok, "urn:li:share:7000000000000000001", "Hello \"world\" ✨\nbye"));
        _log.Append(Row(LinkedInFixture.Now, "def", "refused: phone number", null, null));

        // Assert
        File.ReadAllText(_paths.ActionLog).Should().Be(
            "{\"schema\":1,\"ts\":\"2026-10-07T08:00:00Z\",\"tool\":\"publish_post\",\"urn\":\"urn:li:share:7000000000000000001\",\"visibility\":\"PUBLIC\",\"chars\":5,\"sha256\":\"abc\",\"text\":\"Hello \\\"world\\\" ✨\\nbye\",\"status\":\"ok\"}\n"
            + "{\"schema\":1,\"ts\":\"2026-10-07T08:00:00Z\",\"tool\":\"publish_post\",\"urn\":null,\"visibility\":\"PUBLIC\",\"chars\":5,\"sha256\":\"def\",\"text\":null,\"status\":\"refused: phone number\"}\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void Append_OnLinux_File0600Dir0700()
    {
        // Act
        _log.Append(Row(LinkedInFixture.Now, "abc", ActionRow.Ok, "urn:li:share:1", "x"));

        // Assert
        File.GetUnixFileMode(_paths.ActionLog).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_paths.StateDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void RecentOk_Within24h_FoundOutside_Null()
    {
        // Arrange
        _log.Append(Row(LinkedInFixture.Now, "abc", ActionRow.Ok, "urn:li:share:1", "x"));

        // Act
        var within = _log.RecentOk("abc", LinkedInFixture.Now.AddHours(-24));
        var boundary = _log.RecentOk("abc", LinkedInFixture.Now);
        var outside = _log.RecentOk("abc", LinkedInFixture.Now.AddSeconds(1));
        var other = _log.RecentOk("xyz", LinkedInFixture.Now.AddHours(-24));

        // Assert
        within!.Urn.Should().Be("urn:li:share:1");
        boundary.Should().NotBeNull();
        outside.Should().BeNull();
        other.Should().BeNull();
    }

    [Fact]
    public void RecentOk_IgnoresRefusedRowsAndMalformedLines()
    {
        // Arrange
        _log.Append(Row(LinkedInFixture.Now, "abc", "outcome_unknown: the post may exist", null, "x"));
        File.AppendAllText(_paths.ActionLog, "not json\n{\"schema\":1}\n");

        // Act
        var found = _log.RecentOk("abc", LinkedInFixture.Now.AddHours(-24));

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void RecentOk_NoFile_Null()
    {
        // Assert
        _log.RecentOk("abc", LinkedInFixture.Now).Should().BeNull();
    }

    [Fact]
    public void Append_ContentRefusal_TextNull()
    {
        // Act
        _log.Append(Row(LinkedInFixture.Now, "abc", "refused: e-mail address", null, null));

        // Assert
        File.ReadAllText(_paths.ActionLog).Should().Contain("\"text\":null");
    }

    [Fact]
    public async Task Append_ConcurrentWriters_NoInterleaving()
    {
        // Arrange
        var text = new string('y', 4000);

        // Act
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 10; n++)
            {
                new LinkedInActionLog(_paths).Append(Row(LinkedInFixture.Now, $"s{i}-{n}", ActionRow.Ok, "urn:li:share:1", text));
            }
        })));

        // Assert
        var lines = File.ReadAllLines(_paths.ActionLog);
        lines.Should().HaveCount(80);
        lines.Should().OnlyContain(l => l.StartsWith("{\"schema\":1,", StringComparison.Ordinal) && l.EndsWith("\"status\":\"ok\"}", StringComparison.Ordinal));
    }

    private static ActionRow Row(DateTimeOffset ts, string sha, string status, string? urn, string? text) =>
        new(1, ts, "publish_post", urn, "PUBLIC", 5, sha, text, status);
}
