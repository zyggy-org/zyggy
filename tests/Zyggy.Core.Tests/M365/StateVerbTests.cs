using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// <c>zyggy m365 state</c> against the three <c>state:</c> cases of the template's <c>m365.bats</c> (spec 33 AC-28): the shell's
/// file names, value grammars, messages and exit codes; only the usage line names the verb.
/// </summary>
public sealed class StateVerbTests : IDisposable
{
    private const string Usage = " (usage: zyggy m365 state get <key> [<arg>] | set <key> [<arg>] <value> | reset <key> [<arg>])\n";

    private readonly MemoryTree _tree = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _environment;

    public StateVerbTests()
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ZYGGY_MEMORY_ROOT"] = _tree.Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_STATE_DIR"] = Path.Combine(_tree.Root, "state"),
        };
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string State => Path.Combine(_tree.Root, "state", "m365");

    public static TheoryData<string[], string> UsageErrors() => new()
    {
        { ["set", "mail-watermark"], "set needs <key> [<arg>] <value>" + Usage },
        { ["set", "mail-watermark", "2026-09-30T08:00:00Z", "extra"], "'2026-09-30T08:00:00Z' is not a valid argument" + Usage },
        { ["set", "nonsense", "x"], "unknown key 'nonsense'" + Usage },
        { ["get", "nonsense"], "unknown key 'nonsense'" + Usage },
        { ["reset", "nonsense"], "unknown key 'nonsense'" + Usage },
        { ["get", "drive-token", "../x"], "'../x' is not a valid argument" + Usage },
        { ["get", "drive-token"], "drive-token needs <drive>" + Usage },
        { ["get", "drive-token", "a/b"], "'a/b' is not a valid argument" + Usage },
        { ["set", "drive-token", "d1"], "drive-token needs <drive>" + Usage },
        { ["get", "backfill-watermark"], "backfill-watermark needs <folder>" + Usage },
        { ["get", "replied", "2026-9-30"], "'2026-9-30' is not a date (YYYY-MM-DD)" + Usage },
        { ["set", "replied", "2026-09-30"], "replied needs <date>" + Usage },
        { ["set", "replied", "2026-09-30", "m1", "m2"], "set needs <key> [<arg>] <value>" + Usage },
        { ["get", "mail-watermark", "extra"], "mail-watermark takes no argument" + Usage },
        { ["get"], "get needs <key> [<arg>]" + Usage },
        { ["list"], "unknown verb 'list'" + Usage },
        { ["list", "drafts"], "unknown verb 'list'" + Usage },
        { ["list", "proposals", "--status", "pending"], "unknown verb 'list'" + Usage },
        { ["mark", "p1", "executed"], "unknown verb 'mark'" + Usage },
        { ["x"], "unknown verb 'x'" + Usage },
        { [], "no verb given" + Usage },
        { ["set", "files-backfill-watermark", "2026-09-01T08:00:00Z"], "files-backfill-watermark needs <drive>" + Usage },
        { ["get", "drive-token", new string('a', 201)], $"'{new string('a', 40)}' is not a valid argument" + Usage },
    };

    public static TheoryData<string[], string> BadValues() => new()
    {
        { ["set", "mail-watermark", "not-a-date"], "invalid value for mail-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "mail-watermark", "2026-09-30"], "invalid value for mail-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "mail-watermark", "/etc/passwd"], "invalid value for mail-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "mail-watermark", "2026-09-30T08:00:00Z\nx"], "invalid value for mail-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "mail-watermark", "2026-09-30T08:00:00Z\n"], "invalid value for mail-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "mail-watermark", new string('a', 5000)], "invalid value for mail-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "drive-token", "d1", "opaque-abc"], "invalid value for drive-token: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "backfill-watermark", "inbox", "2026-09-30"], "invalid value for backfill-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ)\n" },
        { ["set", "replied", "2026-09-30", "a b"], "invalid value for replied: expected one message id\n" },
        {
            ["set", "files-backfill-watermark", "b!onedrive0001", "delta-link-xyz"],
            "invalid value for files-backfill-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ), optionally followed by |<item-id>\n"
        },
        {
            ["set", "files-backfill-watermark", "b!onedrive0001", "2026-09-01T08:00:00Z|"],
            "invalid value for files-backfill-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ), optionally followed by |<item-id>\n"
        },
        {
            ["set", "files-backfill-watermark", "b!onedrive0001", "2026-09-01T08:00:00Z|a b"],
            "invalid value for files-backfill-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ), optionally followed by |<item-id>\n"
        },
        {
            ["set", "files-backfill-watermark", "b!onedrive0001", "2026-09-01|01F10"],
            "invalid value for files-backfill-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ), optionally followed by |<item-id>\n"
        },
        {
            ["set", "files-backfill-watermark", "b!onedrive0001", "2026-09-01T08:00:00Z|01F10|x"],
            "invalid value for files-backfill-watermark: expected an ISO timestamp (YYYY-MM-DDTHH:MM:SSZ), optionally followed by |<item-id>\n"
        },
    };

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Get_MailWatermarkAbsent_NowMinus24h()
    {
        // Act
        var (exit, console) = await RunAsync("get", "mail-watermark");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be("2026-09-29T10:00:00Z\n");
        console.Stderr.Should().BeEmpty();
        Directory.Exists(State).Should().BeFalse();
    }

    [Theory]
    [InlineData("mail-watermark", null, "2026-09-30T08:00:00Z", "mail-watermark")]
    [InlineData("backfill-watermark", "inbox", "2026-09-01T00:00:00Z", "backfill-inbox.watermark")]
    [InlineData("drive-token", "b!onedrive0001", "2026-09-29T00:00:00Z", "drive-b!onedrive0001.token")]
    [InlineData("files-backfill-watermark", "b!onedrive0001", "2026-09-01T08:00:00Z|01F10", "files-backfill-b!onedrive0001.watermark")]
    [InlineData("replied", "2026-09-30", "m1", "replied-2026-09-30.ids")]
    public async Task SetThenGet_EveryKey_RoundTripsAndFileNameAsShell(string key, string? arg, string value, string fileName)
    {
        // Arrange
        string[] keyArgs = arg is null ? [key] : [key, arg];

        // Act
        var (setExit, set) = await RunAsync(["set", .. keyArgs, value]);
        var (_, get) = await RunAsync(["get", .. keyArgs]);
        var (resetExit, _) = await RunAsync(["reset", .. keyArgs]);

        // Assert
        setExit.Should().Be(0);
        set.Stdout.Should().BeEmpty();
        set.Stderr.Should().BeEmpty();
        get.Stdout.Should().Be(value + "\n");
        resetExit.Should().Be(0);
        File.Exists(Path.Combine(State, fileName)).Should().BeFalse();
    }

    [Fact]
    public async Task Set_WritesTheShellFileBytes()
    {
        // Act
        await RunAsync("set", "drive-token", "b!onedrive0001", "2026-09-29T00:00:00Z");

        // Assert
        File.ReadAllText(Path.Combine(State, "drive-b!onedrive0001.token")).Should().Be("2026-09-29T00:00:00Z\n");
    }

    [Fact]
    public async Task Set_Replied_AppendsEachIdOnce()
    {
        // Act
        await RunAsync("set", "replied", "2026-09-30", "m1");
        await RunAsync("set", "replied", "2026-09-30", "m2");
        await RunAsync("set", "replied", "2026-09-30", "m1");
        var (_, get) = await RunAsync("get", "replied", "2026-09-30");

        // Assert
        get.Stdout.Should().Be("m1\nm2\n");
    }

    [Fact]
    public async Task Get_AbsentKeyOtherThanMailWatermark_EmptyExitZero()
    {
        // Act
        var (exit, console) = await RunAsync("get", "files-backfill-watermark", "b!onedrive0001");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task UnknownKeyOrMissingArg_ExitsFour(string[] args, string message)
    {
        // Act
        var (exit, console) = await RunAsync(args);

        // Assert
        exit.Should().Be(4);
        console.Stdout.Should().BeEmpty();
        console.Stderr.Should().Be("m365-state: " + message);
        Directory.Exists(State).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(BadValues))]
    public async Task Set_BadValue_ExitsFourWithShellMessage(string[] args, string message)
    {
        // Act
        var (exit, console) = await RunAsync(args);

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be("m365-state: " + message);
        Directory.Exists(State).Should().BeFalse();
    }

    [Fact]
    public async Task RemovedD6Verbs_ListMark_ExitFourNamedKeysUnchanged()
    {
        // Arrange
        await RunAsync("set", "mail-watermark", "2026-09-30T06:00:00Z");

        // Act
        var (exit, _) = await RunAsync("mark", "p1", "executed");

        // Assert
        exit.Should().Be(4);
        (await RunAsync("get", "mail-watermark")).Console.Stdout.Should().Be("2026-09-30T06:00:00Z\n");
        Directory.EnumerateFiles(State).Select(Path.GetFileName).Should().Equal("mail-watermark");
    }

    [Fact]
    public async Task FilesBackfillWatermark_PlainIsoAndIsoPipeId_BothAcceptedApartFromDriveToken()
    {
        // Arrange
        await RunAsync("set", "drive-token", "b!onedrive0001", "2026-09-29T06:00:00Z");

        // Act
        var (plain, _) = await RunAsync("set", "files-backfill-watermark", "b!onedrive0001", "2026-09-01T08:00:00Z");
        var (cursor, _) = await RunAsync("set", "files-backfill-watermark", "b!onedrive0001", "2026-09-01T08:00:00Z|01F10");
        await RunAsync("reset", "files-backfill-watermark", "b!onedrive0001");

        // Assert
        plain.Should().Be(0);
        cursor.Should().Be(0);
        (await RunAsync("get", "drive-token", "b!onedrive0001")).Console.Stdout.Should().Be("2026-09-29T06:00:00Z\n");
    }

    [Fact]
    public async Task Get_FileWrittenByShell_ReturnedUnchanged()
    {
        // Arrange: the shell's bytes, including a file without a final newline
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "backfill-AAMkAGI2.watermark"), "2026-09-12T07:15:00Z\n");
        File.WriteAllText(Path.Combine(State, "replied-2026-10-01.ids"), "AAMkAGI2m1\nAAMkAGI2m2");

        // Act
        var (_, watermark) = await RunAsync("get", "backfill-watermark", "AAMkAGI2");
        var (_, replied) = await RunAsync("get", "replied", "2026-10-01");
        await RunAsync("set", "replied", "2026-10-01", "AAMkAGI2m3");

        // Assert
        watermark.Stdout.Should().Be("2026-09-12T07:15:00Z\n");
        replied.Stdout.Should().Be("AAMkAGI2m1\nAAMkAGI2m2");
        File.ReadAllText(Path.Combine(State, "replied-2026-10-01.ids")).Should().Be("AAMkAGI2m1\nAAMkAGI2m2AAMkAGI2m3\n");
    }

    [Fact]
    public async Task Set_LeavesNoTmp()
    {
        // Act
        await RunAsync("set", "mail-watermark", "2026-09-30T08:00:00Z");
        await RunAsync("set", "replied", "2026-09-30", "m1");

        // Assert
        Directory.EnumerateFiles(State).Select(Path.GetFileName).Should().BeEquivalentTo(["mail-watermark", "replied-2026-09-30.ids"]);
    }

    [Fact]
    public async Task TenantUnset_ExitsThree()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var (exit, console) = await RunAsync("get", "mail-watermark");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("m365-state: configuration error: ZYGGY_TENANT is not set\n");
    }

    [Fact]
    public async Task UsageBeforeConfiguration_ExitsFourEvenWithTenantUnset()
    {
        // Arrange
        _environment.Remove("ZYGGY_TENANT");

        // Act
        var (exit, _) = await RunAsync("get", "nonsense");

        // Assert
        exit.Should().Be(4);
    }

    [Fact]
    public async Task HooksOff_Accepted()
    {
        // Arrange
        _environment["ZYGGY_HOOKS"] = "off";

        // Act
        var (exit, console) = await RunAsync("get", "mail-watermark");

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be("2026-09-29T10:00:00Z\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    public async Task Set_OnLinux_File0600Dir0700()
    {
        // Act
        await RunAsync("set", "mail-watermark", "2026-09-30T08:00:00Z");

        // Assert
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(State).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(Path.Combine(State, "mail-watermark")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "an uncreatable directory: Linux only")]
    public async Task Set_StateDirectoryCannotBeCreated_ExitsThree()
    {
        // Arrange
        _environment["ZYGGY_STATE_DIR"] = "/proc/none/zyggy";

        // Act
        var (exit, console) = await RunAsync("set", "mail-watermark", "2026-09-30T08:00:00Z");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("m365-state: state directory /proc/none/zyggy/m365 cannot be created\n");
    }

    private Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args) => M365Run.RunAsync(_environment, _clock, ["state", .. args]);
}
