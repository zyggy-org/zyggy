using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Spec 33 AC-30..AC-34 (binary): the model-run verbs from the built <c>zyggy</c> — the backfills refuse an unattended run first, a wrong
/// version pin stops all three before any request or model run, the settings file supplies the backfills' principal.
/// </summary>
public sealed class ModelRunCommandTests : IDisposable
{
    private const string WrongPin = """{ "version": "0.0.1", "sha256": {} }""";

    private readonly M365RunHarness _run = new();

    public void Dispose() => _run.Dispose();

    private string StdinCapture => Path.Combine(_run.Fixture.Root, "stdin.bin");

    private Dictionary<string, string?> Env()
    {
        var env = _run.Env();
        env["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = StdinCapture;
        return env;
    }

    private Task<ZyggyRun> Zyggy(Dictionary<string, string?> env, params string[] args) =>
        ZyggyCli.RunAsync(["m365", .. args], env, null, _run.Fixture.Root, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("mail-backfill")]
    [InlineData("files-backfill")]
    public async Task Backfill_HooksOff_ExitFiveBeforeAnything(string verb)
    {
        // Arrange: not even a configuration
        var env = new Dictionary<string, string?> { ["ZYGGY_HOOKS"] = "off" };

        // Act
        var run = await Zyggy(env, verb, "--bogus");

        // Assert
        run.ExitCode.Should().Be(5);
        run.Stderr.Should().Be($"{verb}: refused: unattended run (ZYGGY_HOOKS=off)\n");
        run.Stdout.Should().BeEmpty();
    }

    [Theory]
    [InlineData("brief", "m365-brief")]
    [InlineData("mail-backfill", "mail-backfill")]
    [InlineData("files-backfill", "files-backfill")]
    public async Task ModelRunVerb_PinMismatch_ExitThreeVersionMismatchNoRequest(string verb, string prefix)
    {
        // Arrange
        File.WriteAllText(Path.Combine(_run.Fixture.InstanceDirectory, "zyggy.json"), WrongPin);

        // Act
        var run = await Zyggy(Env(), verb);

        // Assert
        run.ExitCode.Should().Be(3, run.Stderr);
        run.Stderr.Should().StartWith($"{prefix}: configuration error: version_mismatch: ").And.NotContain("key:");
        run.Stdout.Should().BeEmpty();
        File.Exists(StdinCapture).Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "brief", "now" }, "m365-brief: brief takes no argument (usage: zyggy m365 brief)\n")]
    [InlineData(new[] { "mail-backfill", "--folder" }, "mail-backfill: --folder needs <name> (usage: zyggy m365 mail-backfill [--folder <name>] [--reset])\n")]
    [InlineData(new[] { "files-backfill", "--drive", "a", "--drive", "b" }, "files-backfill: --drive given twice (usage: zyggy m365 files-backfill [--drive <name>] [--reset])\n")]
    public async Task ModelRunVerb_BadArgument_ExitFour(string[] args, string stderr)
    {
        // Act
        var run = await Zyggy(Env(), args);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be(stderr);
    }

    [Fact]
    public async Task Brief_ConfigMissing_ExitThree()
    {
        // Arrange
        File.Delete(Path.Combine(_run.Fixture.InstanceDirectory, "m365.json"));

        // Act
        var run = await Zyggy(Env(), "brief");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().StartWith("m365-brief: configuration error: ").And.Contain("m365.json");
        File.Exists(StdinCapture).Should().BeFalse();
    }

    [Fact]
    public async Task Brief_DeliveryKey_ExitThreeRemoved()
    {
        // Arrange: a 33-era instance still naming the Draft delivery
        var path = Path.Combine(_run.Fixture.InstanceDirectory, "m365.json");
        var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        config["brief"]!["delivery"] = "draft";
        File.WriteAllText(path, config.ToJsonString());

        // Act
        var run = await Zyggy(Env(), "brief");

        // Assert
        run.ExitCode.Should().Be(3);
        run.Stderr.Should().Be("m365-brief: configuration error: brief.delivery is removed (spec 35: the brief is shown in the session; rollback restores the Draft brief)\n");
        File.Exists(StdinCapture).Should().BeFalse();
    }

    [Fact]
    public async Task MailBackfill_PrincipalOnlyInSettingsLocalJson_PassesConfigurationThenStopsAtPin()
    {
        // Arrange: a plain shell — the principal only in the checkout's settings file
        var env = Env();
        var principal = new Dictionary<string, string?>();
        foreach (var key in new[] { "ZYGGY_MEMORY_ROOT", "ZYGGY_TENANT", "ZYGGY_USER", "ZYGGY_TIMEZONE" })
        {
            principal[key] = env[key];
            env[key] = null;
        }

        File.WriteAllText(Path.Combine(_run.Fixture.InstanceDirectory, "zyggy.json"), WrongPin);
        var withoutSettings = await Zyggy(env, "mail-backfill");
        File.WriteAllText(
            Path.Combine(_run.Fixture.Checkout, ".claude", "settings.local.json"),
            System.Text.Json.JsonSerializer.Serialize(new { env = principal }));

        // Act
        var run = await Zyggy(env, "mail-backfill");

        // Assert
        withoutSettings.ExitCode.Should().Be(3);
        withoutSettings.Stderr.Should().NotContain("version_mismatch");
        run.ExitCode.Should().Be(3, run.Stderr);
        run.Stderr.Should().StartWith("mail-backfill: configuration error: version_mismatch: ");
    }
}
