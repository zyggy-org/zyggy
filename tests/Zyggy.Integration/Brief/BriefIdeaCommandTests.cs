using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Brief;

/// <summary>Spec 35 AC-36 (I): <c>zyggy brief idea</c> from the built binary records the owner's answer, 0600 on Linux.</summary>
public sealed class BriefIdeaCommandTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public BriefIdeaCommandTests()
    {
        Directory.CreateDirectory(BriefDirectory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(BriefDirectory, $"brief-{today}.json"),
            "{\"schema\":1,\"date\":\"" + today + "\",\"generated\":\"" + today + "T04:30:00Z\",\"mode\":\"weekday\",\"watermark\":\"\",\"audit\":\"ok\",\"auditReasons\":[]," +
            "\"counts\":{\"urgent\":0,\"important\":0,\"other\":0,\"files\":0,\"filesOther\":0},\"page_exceeded\":false,\"items\":[]," +
            "\"ideas\":[{\"n\":1,\"id\":\"acme-renewal-offer\",\"area\":\"client\"},{\"n\":2,\"id\":\"family-autumn-trip\",\"area\":\"family\"}]}");
    }

    private string BriefDirectory => Path.Combine(_scratch.Path, "state", "brief");

    public void Dispose() => _scratch.Dispose();

    private Dictionary<string, string?> Env() => new()
    {
        ["HOME"] = Path.Combine(_scratch.Path, "home"),
        ["ZYGGY_STATE_DIR"] = Path.Combine(_scratch.Path, "state"),
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["XDG_CONFIG_HOME"] = Path.Combine(_scratch.Path, "home", ".config"),
        ["XDG_STATE_HOME"] = Path.Combine(_scratch.Path, "home", ".local", "state"),
    };

    private Task<ZyggyRun> Brief(params string[] args) =>
        ZyggyCli.RunAsync(["brief", .. args], Env(), null, _scratch.Path, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Idea_Binary_RecordsRow()
    {
        // Act
        var run = await Brief("idea", "2", "not-interested");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Should().Be("recorded: family-autumn-trip not-interested\n");
        var log = Path.Combine(BriefDirectory, "ideas.jsonl");
        File.ReadAllLines(log).Should().ContainSingle().Which.Should().Contain("\"kind\":\"answer\",\"id\":\"family-autumn-trip\",\"area\":\"family\",\"answer\":\"not-interested\"");
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(log).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Idea_Binary_UnknownNumber_ExitFive()
    {
        // Act
        var run = await Brief("idea", "7", "good");

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stderr.Should().StartWith("brief: no suggestion 7 in the brief of ");
        File.Exists(Path.Combine(BriefDirectory, "ideas.jsonl")).Should().BeFalse();
    }

    [Fact]
    public async Task Idea_Binary_LaterWithoutUntil_ExitFour()
    {
        // Act
        var run = await Brief("idea", "1", "later");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Contain("later needs --until with a future date");
    }
}
