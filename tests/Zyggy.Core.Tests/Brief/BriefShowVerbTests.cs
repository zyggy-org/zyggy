using System.Runtime.Versioning;

using Zyggy.Core.Brief;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-12, AC-56, AC-61: the verb's exit codes, the <c>last-shown</c> write after a complete print, the settings.</summary>
public sealed class BriefShowVerbTests : IDisposable
{
    private readonly BriefFixture _f = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsWindows => OperatingSystem.IsWindows();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Run_Today_StdoutEqualsGoldenExitZeroLastShownWritten()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        _f.WriteWatermark("2026-10-05T04:30:00Z");

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be(BriefFixture.GoldenText("show-weekday.txt"));
        console.Stderr.Should().BeEmpty();
        File.ReadAllText(_f.Paths.LastShown).Should().Be("2026-10-06\n");
    }

    [Fact]
    public async Task Run_FullAndDate_PrintsThatBriefComplete()
    {
        // Arrange
        _f.WriteGoldenBrief(new DateOnly(2026, 10, 3));
        _f.WriteWatermark("2026-10-05T04:30:00Z");

        // Act
        var (exit, console) = await _f.RunAsync("--full", "2026-10-03");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be(BriefFixture.GoldenText("show-weekday-full.txt")
            .Replace("date=\"2026-10-06\" generated=\"2026-10-06T04:31:00Z\"", "date=\"2026-10-03\" generated=\"2026-10-03T04:31:00Z\"", StringComparison.Ordinal));
        File.ReadAllText(_f.Paths.LastShown).Should().Be("2026-10-03\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Run_OnLinux_LastShown0600()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);

        // Act
        await _f.RunAsync();

        // Assert
        File.GetUnixFileMode(_f.Paths.LastShown).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task Run_StdoutWriteFails_LastShownNotWritten()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        var io = new VerbIo(new StringReader(string.Empty), new ThrowingWriter(), new StringWriter(), false);

        // Act
        var exit = await new BriefVerbHost(_f.Environment, _f.Clock, _f.FindTimeZone).RunAsync(["show"], io, TestContext.Current.CancellationToken);

        // Assert
        exit.Should().Be(3);
        File.Exists(_f.Paths.LastShown).Should().BeFalse();
    }

    [Fact]
    public async Task Run_LastShownWriteFails_BriefPrintedOneStderrLineExitZero()
    {
        // Arrange: a directory where the file goes
        _f.WriteGoldenBrief(BriefFixture.Today);
        Directory.CreateDirectory(_f.Paths.LastShown);

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().StartWith(BriefPayload.Header);
        console.Stderr.Should().Be($"brief: could not write {_f.Paths.LastShown}; the brief was printed\n");
    }

    [Fact]
    public async Task Run_StateDirMissing_ExitThreeNamesPath()
    {
        // Arrange
        Directory.Delete(_f.Paths.Directory, recursive: true);

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be($"brief: {_f.Paths.Directory} is missing — runbook 13 \"Brief run failed\"\n");
        console.Stdout.Should().BeEmpty();
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "an exclusive file lock: Windows only")]
    public async Task Run_BriefUnreadable_ExitThreeNamesPath()
    {
        // Arrange
        _f.WriteGoldenBrief(BriefFixture.Today);
        using var locked = new FileStream(_f.Paths.Markdown(BriefFixture.Today), FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Contain("cannot be read — runbook 13 \"Brief run failed\"");
        File.Exists(_f.Paths.LastShown).Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "2026-13-40" }, "unexpected argument '2026-13-40'")]
    [InlineData(new[] { "tomorrow" }, "unexpected argument 'tomorrow'")]
    [InlineData(new[] { "2026-10-03", "2026-10-04" }, "unexpected argument '2026-10-04'")]
    [InlineData(new[] { "--full", "--full" }, "unexpected argument '--full'")]
    [InlineData(new[] { "--all" }, "unexpected argument '--all'")]
    public async Task Run_BadDateOrExtraArgument_ExitFour(string[] args, string message)
    {
        // Act
        var (exit, console) = await _f.RunAsync(args);

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be($"brief: {message} (usage: zyggy brief show [--full] [<YYYY-MM-DD>])\n");
    }

    [Theory]
    [InlineData(null, "brief: configuration error: ZYGGY_TIMEZONE is not set\n")]
    [InlineData("Mars/Olympus", "brief: configuration error: ZYGGY_TIMEZONE 'Mars/Olympus' is not a known time zone\n")]
    public async Task Run_TimezoneMissingOrUnknown_ExitThree(string? zone, string stderr)
    {
        // Arrange
        _f.Environment["ZYGGY_TIMEZONE"] = zone;

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be(stderr);
    }

    [Theory]
    [InlineData("""{"brief":{"expect_by":"7am"}}""", "brief.expect_by is not a time HH:MM")]
    [InlineData("""{"brief":{"brief_keep_days":0}}""", "brief.brief_keep_days is not an integer in 1..365")]
    [InlineData("""{"brief":{"weekend_days":["funday"]}}""", "brief.weekend_days is not an array of day names")]
    [InlineData("""{"brief":[]}""", "brief is not an object")]
    public async Task Run_MalformedBriefSettings_ExitThreeNamesKey(string json, string message)
    {
        // Arrange
        File.WriteAllText(Path.Combine(_f.Root, "instance", "m365.json"), json);

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be($"brief: configuration error: {message}\n");
    }

    [Fact]
    public async Task Run_SettingsExpectByLater_IdentityPartNeverRead()
    {
        // Arrange: an m365.json whose identity part is garbage; only the brief block matters to show
        File.WriteAllText(Path.Combine(_f.Root, "instance", "m365.json"), """{"tenant_id":"not-a-guid","cert":{"expires":"never"},"brief":{"expect_by":"08:30","brief_keep_days":7,"weekend_days":["sunday"]}}""");

        // Act
        var (exit, console) = await _f.RunAsync();

        // Assert
        exit.Should().Be(0);
        console.Stdout.Should().Be("today's brief is not ready yet (expected by 08:30)\n");
    }

    [Fact]
    public async Task Host_UnknownVerbOrNone_ExitFour()
    {
        // Act
        var none = new VerbConsole();
        var exitNone = await new BriefVerbHost(_f.Environment, _f.Clock, _f.FindTimeZone).RunAsync([], none.Io, TestContext.Current.CancellationToken);
        var unknown = new VerbConsole();
        var exitUnknown = await new BriefVerbHost(_f.Environment, _f.Clock, _f.FindTimeZone).RunAsync(["inject"], unknown.Io, TestContext.Current.CancellationToken);

        // Assert
        exitNone.Should().Be(4);
        none.Stderr.Should().Be("brief: no verb given (usage: zyggy brief show [--full] [<YYYY-MM-DD>])\n");
        exitUnknown.Should().Be(4);
        unknown.Stderr.Should().Be("brief: unknown verb 'inject' (usage: zyggy brief show [--full] [<YYYY-MM-DD>])\n");
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => throw new IOException("broken pipe");

        public override void Write(string? value) => throw new IOException("broken pipe");

        public override Task WriteAsync(string? value) => throw new IOException("broken pipe");
    }
}
