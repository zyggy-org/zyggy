using System.Globalization;
using System.Runtime.Versioning;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

/// <summary>Step 2 (spec 33 AC-6..AC-8): <c>zyggy memory remember</c> from the built binary, real file system and clock.</summary>
public sealed class RememberCommandTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _scratch.Dispose();

    private string Root => Path.Combine(_scratch.Path, "memory");

    private Dictionary<string, string?> Env()
    {
        Directory.CreateDirectory(Path.Combine(Root, "acme", "alice"));
        return new()
        {
            ["ZYGGY_MEMORY_ROOT"] = Root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_SECRET_PATTERNS"] = Path.Combine(AppContext.BaseDirectory, "golden", "secret-patterns", "secret-patterns.txt"),
        };
    }

    private string InboxDirectory => Path.Combine(Root, "acme", "alice", "inbox");

    private Task<ZyggyRun> Remember(Dictionary<string, string?> env, params string[] args) =>
        ZyggyCli.RunAsync(["memory", "remember", .. args], env, null, _scratch.Path, TestContext.Current.CancellationToken);

    private static string UtcToday() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Remember_Default_ExitZeroStdoutRememberedAndFileLine()
    {
        // Arrange
        var env = Env();
        var before = UtcToday();

        // Act
        var run = await Remember(env, "--", "Marie prefers tea");

        // Assert
        var after = UtcToday();
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stderr.Should().BeEmpty();
        var date = run.Stdout.Split('\n')[1]["- [stated] ".Length..][..10];
        date.Should().BeOneOf(before, after);
        var file = Path.Combine(InboxDirectory, $"remember-{date}.md");
        run.Stdout.Should().Be($"remembered: {file}\n- [stated] {date}: Marie prefers tea\n");
        File.ReadAllText(file).Should().Be(
            $"---\nname: remember {date}\ndescription: facts stated by the owner on {date} (remember skill)\nupdated: {date}\n---\n- [stated] {date}: Marie prefers tea\n");
    }

    [Fact]
    public async Task Remember_SecretSample_ExitTwoStderrNamesPatternNothingWritten()
    {
        // Act
        var run = await Remember(Env(), "--", "AKIAABCDEFGHIJKLMNOP");

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().StartWith("refused: matches secret pattern ").And.EndWith("\n").And.NotContain("AKIAABCDEFGHIJKLMNOP");
        Directory.Exists(InboxDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Remember_UnknownOption_ExitFourUsageNamesVerb()
    {
        // Act
        var run = await Remember(Env(), "--bogus", "--", "fact");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().StartWith("remember: unexpected argument '--bogus' (usage: zyggy memory remember ");
        run.Stderr.Count(c => c == '\n').Should().Be(1);
    }

    [Fact]
    public async Task Remember_TenantUnset_ExitThree()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_TENANT"] = null;

        // Act
        var run = await Remember(env, "--", "a fact");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("remember: configuration error: ZYGGY_TENANT is not set\n");
    }

    [Fact]
    public async Task Remember_HooksOff_ExitZeroNoOutput()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_HOOKS"] = "off";

        // Act
        var run = await Remember(env, "--", "a fact");

        // Assert
        run.ExitCode.Should().Be(0);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().BeEmpty();
        Directory.Exists(InboxDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Remember_DoubleDashThenDashedWords_FactKeptVerbatim()
    {
        // Act
        var run = await Remember(Env(), "--", "--not-an-option", "text");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Split('\n')[1].Should().EndWith(": --not-an-option text");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "a PATH shell shim: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Remember_NeverInvokesGit()
    {
        // Arrange
        var bin = Path.Combine(_scratch.Path, "bin");
        var marker = Path.Combine(_scratch.Path, "git-was-called");
        Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, "git");
        File.WriteAllText(shim, $"#!/bin/sh\ntouch '{marker}'\nexit 99\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var env = Env();
        env["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");

        // Act
        var run = await Remember(env, "--", "a fact");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        File.Exists(marker).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task Remember_OnLinux_FileMode0644AndNoTempFile()
    {
        // Act
        var run = await Remember(Env(), "--", "a fact");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var file = Directory.EnumerateFiles(InboxDirectory).Should().ContainSingle().Subject;
        File.GetUnixFileMode(file).Should().Be(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
}
