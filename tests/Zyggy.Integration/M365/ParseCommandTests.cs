using System.Runtime.Versioning;
using System.Text;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 6 (spec 33 AC-29): <c>zyggy m365 parse</c> from the built binary — on Linux a fake MarkItDown under the real <c>prlimit</c>;
/// elsewhere the verb refuses with exit 3.
/// </summary>
public sealed class ParseCommandTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static bool IsNotLinux => !OperatingSystem.IsLinux();

    public void Dispose() => _fixture.Dispose();

    private Task<ZyggyRun> Parse(Dictionary<string, string?> env, params string[] args) =>
        ZyggyCli.RunAsync(["m365", "parse", .. args], env, null, _fixture.Root, TestContext.Current.CancellationToken);

    // A MarkItDown stand-in: prints the fixture text for its file name, else fails like the real CLI.
    [SupportedOSPlatform("linux")]
    private static void InstallMarkitdown(string directory)
    {
        var fixtures = Path.Combine(directory, "fixtures");
        Directory.CreateDirectory(fixtures);
        File.Copy(M365InstanceFixture.Golden("m365", "fixtures", "parsed-report.docx.txt"), Path.Combine(fixtures, "parsed-report.docx.txt"));
        M365InstanceFixture.WriteScript(Path.Combine(directory, "markitdown"), """
            #!/bin/sh
            here="$(cd "$(dirname "$0")" && pwd)"
            printf 'argv=%s\n' "$*" >> "$here/markitdown.log"
            text="$here/fixtures/parsed-$(basename "$1").txt"
            if [ -f "$text" ]; then cat "$text"; else
              printf 'markitdown: UnsupportedFormatException: could not convert %s\n' "$(basename "$1")" >&2; exit 1; fi
            """);
    }

    private string Input(string name)
    {
        var path = Path.Combine(_fixture.RunDirectory, name);
        File.WriteAllText(path, "PK fake docx bytes\n");
        return path;
    }

    private Dictionary<string, string?> EnvWithPath(string directory)
    {
        var env = _fixture.Env();
        env["PATH"] = directory + ":" + Environment.GetEnvironmentVariable("PATH");
        return env;
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "MarkItDown under prlimit: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Parse_OnLinux_DocxInRunDir_ExitZeroTextWithheldLineInputDeleted()
    {
        // Arrange
        var bin = Path.Combine(_fixture.Root, "bin");
        InstallMarkitdown(bin);
        var input = Input("report.docx");
        var expected = File.ReadAllText(M365InstanceFixture.Golden("m365", "fixtures", "parsed-report.docx.txt"))
            .Replace("Access for the reporting tool: password: hunter2secret", "[line withheld: matches secret pattern credential-assignment]", StringComparison.Ordinal)
            .Replace("\a", string.Empty, StringComparison.Ordinal);

        // Act
        var run = await Parse(EnvWithPath(bin), input);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be(expected);
        run.Stderr.Should().Be("parse: report.docx 29 lines, 1 withheld\n");
        File.Exists(input).Should().BeFalse();
        File.ReadAllLines(Path.Combine(bin, "markitdown.log")).Should().Equal($"argv={input}");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "MarkItDown under prlimit: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Parse_OnLinux_MarkitdownOnlyInHomeLocalBin_Found()
    {
        // Arrange: a systemd unit's PATH does not carry ~/.local/bin
        InstallMarkitdown(_fixture.UserBin);
        var input = Input("report.docx");

        // Act
        var run = await Parse(_fixture.Env().Also("PATH", "/usr/bin:/bin"), input);

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().Be("parse: report.docx 29 lines, 1 withheld\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "MarkItDown under prlimit: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Parse_OnLinux_MarkitdownFails_ExitSixFirstLine()
    {
        // Arrange
        var bin = Path.Combine(_fixture.Root, "bin");
        InstallMarkitdown(bin);
        var input = Input("notes.txt");

        // Act
        var run = await Parse(EnvWithPath(bin), input);

        // Assert
        run.ExitCode.Should().Be(6);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be("parse: markitdown failed (markitdown: UnsupportedFormatException: could not convert notes.txt)\n");
        File.Exists(input).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "MarkItDown under prlimit: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Parse_OnLinux_FileOutsideRunDir_ExitFiveFileKept()
    {
        // Arrange
        var bin = Path.Combine(_fixture.Root, "bin");
        InstallMarkitdown(bin);
        var outside = Path.Combine(_fixture.Root, "report.docx");
        File.WriteAllText(outside, "keep me");

        // Act
        var run = await Parse(EnvWithPath(bin), outside);

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stderr.Should().Be("parse: refused: not in the run directory\n");
        File.ReadAllText(outside).Should().Be("keep me");
        File.Exists(Path.Combine(bin, "markitdown.log")).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Linux only")]
    public async Task Parse_OnLinux_BadConfiguration_ExitThreeInputDeleted()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_fixture.InstanceDirectory, "m365.json"), "{");
        var input = Input("report.docx");

        // Act
        var run = await Parse(_fixture.Env(), input);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().StartWith("parse: configuration error: ").And.EndWith("is not valid JSON\n");
        File.Exists(input).Should().BeFalse();
    }

    [Fact]
    public async Task Parse_RunDirUnset_ExitThree()
    {
        // Arrange
        var env = _fixture.Env();
        env["ZYGGY_M365_RUN_DIR"] = null;
        var input = Input("report.docx");

        // Act
        var run = await Parse(env, input);

        // Assert
        run.ExitCode.Should().Be(3);
        if (OperatingSystem.IsLinux())
        {
            run.Stderr.Should().Be("parse: configuration error: ZYGGY_M365_RUN_DIR is not set\n");
        }

        File.Exists(input).Should().BeTrue();
    }

    [Fact(SkipUnless = nameof(IsNotLinux), Skip = "the refusal off Linux")]
    public async Task Parse_OnWindows_ExitThreeNotSupported()
    {
        // Arrange
        var input = Input("report.docx");

        // Act
        var run = await Parse(_fixture.Env(), input);

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("parse: not supported on this platform\n");
        File.Exists(input).Should().BeTrue();
    }

    [Fact]
    public async Task Parse_NoArgument_ExitFour()
    {
        // Act
        var none = await Parse(_fixture.Env());
        var two = await Parse(_fixture.Env(), "a.docx", "b.docx");

        // Assert
        none.ExitCode.Should().Be(4);
        none.Stderr.Should().Be("parse: no file given (usage: zyggy m365 parse <file inside ZYGGY_M365_RUN_DIR>)\n");
        two.ExitCode.Should().Be(4);
        two.Stderr.Should().Be("parse: one file only (usage: zyggy m365 parse <file inside ZYGGY_M365_RUN_DIR>)\n");
        Encoding.UTF8.GetByteCount(none.Stdout + two.Stdout).Should().Be(0);
    }
}

internal static class EnvironmentExtensions
{
    public static Dictionary<string, string?> Also(this Dictionary<string, string?> env, string key, string? value)
    {
        env[key] = value;
        return env;
    }
}
