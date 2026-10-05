using System.Runtime.Versioning;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>Step 4 (spec 33 AC-28): <c>zyggy m365 state</c> from the built binary over the shell's own state files.</summary>
public sealed class StateCommandTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _scratch.Dispose();

    private string State => Path.Combine(_scratch.Path, "state", "m365");

    private Dictionary<string, string?> Env()
    {
        var root = Path.Combine(_scratch.Path, "memory");
        Directory.CreateDirectory(Path.Combine(root, "acme", "alice"));
        return new()
        {
            ["ZYGGY_MEMORY_ROOT"] = root,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_STATE_DIR"] = Path.Combine(_scratch.Path, "state"),
        };
    }

    private Task<ZyggyRun> M365(Dictionary<string, string?> env, params string[] args) =>
        ZyggyCli.RunAsync(["m365", .. args], env, null, _scratch.Path, TestContext.Current.CancellationToken);

    // The shell's files, in the shapes state.sh writes (values from the m365.bats cases).
    private void WriteShellFiles()
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "mail-watermark"), "2026-09-30T08:00:00Z\n");
        File.WriteAllText(Path.Combine(State, "backfill-AAMkAGI2.watermark"), "2026-09-01T00:00:00Z\n");
        File.WriteAllText(Path.Combine(State, "files-backfill-b!onedrive0001.watermark"), "2026-09-01T08:00:00Z|01F10\n");
        File.WriteAllText(Path.Combine(State, "files-backfill-b!onedrive0002.watermark"), "2026-09-01T08:00:00Z\n");
        File.WriteAllText(Path.Combine(State, "replied-2026-10-01.ids"), "m1\nm2\n");
    }

    [Fact]
    public async Task State_GetShellWrittenFiles_PrintedUnchanged()
    {
        // Arrange
        WriteShellFiles();
        var env = Env();

        // Act
        var watermark = await M365(env, "state", "get", "mail-watermark");
        var backfill = await M365(env, "state", "get", "backfill-watermark", "AAMkAGI2");
        var cursor = await M365(env, "state", "get", "files-backfill-watermark", "b!onedrive0001");
        var plain = await M365(env, "state", "get", "files-backfill-watermark", "b!onedrive0002");
        var replied = await M365(env, "state", "get", "replied", "2026-10-01");

        // Assert
        watermark.ExitCode.Should().Be(0, watermark.Stderr);
        watermark.Stdout.Should().Be("2026-09-30T08:00:00Z\n");
        backfill.Stdout.Should().Be("2026-09-01T00:00:00Z\n");
        cursor.Stdout.Should().Be("2026-09-01T08:00:00Z|01F10\n");
        plain.Stdout.Should().Be("2026-09-01T08:00:00Z\n");
        replied.Stdout.Should().Be("m1\nm2\n");
    }

    [Fact]
    public async Task State_SetCursorThenGet_RoundTrips()
    {
        // Arrange
        var env = Env();

        // Act
        var set = await M365(env, "state", "set", "files-backfill-watermark", "b!onedrive0001", "2026-09-02T09:00:00Z|01F11");
        var get = await M365(env, "state", "get", "files-backfill-watermark", "b!onedrive0001");

        // Assert
        set.ExitCode.Should().Be(0, set.Stderr);
        set.Stdout.Should().BeEmpty();
        get.Stdout.Should().Be("2026-09-02T09:00:00Z|01F11\n");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task State_ReplacedFile_OnLinux_Mode0600NoTmp()
    {
        // Arrange
        WriteShellFiles();
        File.SetUnixFileMode(Path.Combine(State, "mail-watermark"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        // Act
        var run = await M365(Env(), "state", "set", "mail-watermark", "2026-10-01T06:00:00Z");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        File.GetUnixFileMode(Path.Combine(State, "mail-watermark")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(State).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.EnumerateFiles(State, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task State_UnknownKey_ExitFourUsageNamesVerb()
    {
        // Act
        var run = await M365(Env(), "state", "get", "nonsense");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stdout.Should().BeEmpty();
        run.Stderr.Should().Be(
            "m365-state: unknown key 'nonsense' (usage: zyggy m365 state get <key> [<arg>] | set <key> [<arg>] <value> | reset <key> [<arg>])\n");
    }

    [Fact]
    public async Task State_TenantUnset_ExitThree()
    {
        // Arrange
        var env = Env();
        env["ZYGGY_TENANT"] = null;

        // Act
        var run = await M365(env, "state", "get", "mail-watermark");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("m365-state: configuration error: ZYGGY_TENANT is not set\n");
    }

    [Fact]
    public async Task M365_UnknownVerb_ExitFour()
    {
        // Act
        var run = await M365(Env(), "propose", "--approved");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("m365: unknown verb 'propose' (usage: zyggy m365 <verb> …)\n");
    }
}
