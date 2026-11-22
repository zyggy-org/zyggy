using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Outlook;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Outlook;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 58 AC-5 through the real <c>zyggy</c> entry point (in process) with a work-laptop <c>node.json</c> and a fake Outlook access
/// registered at the internal seam: with no policy record (neither the copied <c>work-boundary.json</c> nor the <c>node.json</c>
/// mirror), or <c>outlook.enabled</c> false, every tool refuses and touches nothing in Outlook; with the copied file present it is
/// used, else the mirror.
/// </summary>
public sealed class OutlookCommandTests : IAsyncLifetime
{
    private const string Mirror = "\"outlook\": { \"enabled\": true, \"policyConfirmed\": { \"date\": \"2026-11-20\", \"scope\": [\"read\"] } }";

    private const string EnabledOnly = "\"outlook\": { \"enabled\": true }";

    private const string Disabled = "\"outlook\": { \"enabled\": false, \"policyConfirmed\": { \"date\": \"2026-11-20\", \"scope\": [\"read\"] } }";

    private const string WorkBoundary = """{ "neverList": ["national register numbers"], "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": ["read", "send", "move"] } } }""";

    private static readonly string[] Tools = ["count", "list", "read m1"];

    private CliHarness _harness = null!;

    private FakeOutlookAccess _access = null!;

    public static TheoryData<string> ReadTools() => [.. Tools];

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _access = new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Unspecified)), body: "body")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Drafts");
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Theory]
    [MemberData(nameof(ReadTools))]
    public async Task Tool_NoRecordAnywhere_ExitFive_TouchesNothing(string tool)
    {
        // Arrange
        string config = WorkLaptop("no-record", EnabledOnly);

        // Act
        var (exit, console) = await RunAsync(tool, config);

        // Assert
        (exit, console.Stderr, console.Stdout, _access.Calls.Count).Should().Be((5, "outlook: refused: policy not confirmed\n", string.Empty, 0));
    }

    [Theory]
    [MemberData(nameof(ReadTools))]
    public async Task Tool_NotEnabled_ExitFive_TouchesNothing(string tool)
    {
        // Arrange
        string config = WorkLaptop("disabled", Disabled);
        WriteCopy("disabled");

        // Act
        var (exit, console) = await RunAsync(tool, config);

        // Assert
        (exit, console.Stderr, _access.Calls.Count).Should().Be((5, "outlook: refused: outlook not enabled\n", 0));
    }

    [Fact]
    public async Task Count_CopiedFilePresent_UsedBeforeTheMirror()
    {
        // Arrange
        string config = WorkLaptop("file-and-mirror", Mirror);
        WriteCopy("file-and-mirror");

        // Act
        var (countExit, count) = await RunAsync("count", config);
        var (_, policy) = await RunAsync("policy", config);

        // Assert
        (countExit, count.Stderr, count.Stdout.Contains("\"count\": 1", StringComparison.Ordinal), policy.Stdout.Split('\n')[0], policy.Stdout.Contains("date: 2026-11-21\n", StringComparison.Ordinal))
            .Should().Be((0, string.Empty, true, "policy: work-boundary.json copy at " + Path.GetFullPath(Path.Combine(_harness.NodeDirOf("file-and-mirror"), "work-boundary.json")), true));
    }

    [Fact]
    public async Task Count_NoCopy_TheMirrorIsUsed()
    {
        // Arrange
        string config = WorkLaptop("mirror-only", Mirror);

        // Act
        var (countExit, count) = await RunAsync("count", config);
        var (_, policy) = await RunAsync("policy", config);

        // Assert
        (countExit, count.Stderr, count.Stdout.Contains("\"count\": 1", StringComparison.Ordinal), policy.Stdout.Split('\n')[0], policy.Stdout.Contains("date: 2026-11-20\n", StringComparison.Ordinal))
            .Should().Be((0, string.Empty, true, "policy: node.json mirror (outlook.policyConfirmed)", true));
    }

    private string WorkLaptop(string name, string outlookJson) =>
        NodeJson.Write(
            _harness.Scratch.File("config-" + name),
            CliHarness.Acme,
            CliHarness.WorkLaptop,
            TestKeys.Acme1,
            busLocalPath: _harness.Bus.CloneDir,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            nodeDir: _harness.NodeDirOf(name),
            extraTopLevelJson: outlookJson);

    private void WriteCopy(string name)
    {
        string nodeDir = _harness.NodeDirOf(name);
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "work-boundary.json"), WorkBoundary);
    }

    private Task<(int Exit, VerbConsole Console)> RunAsync(string call, string config) =>
        _harness.RunAsync(
            ["outlook", .. call.Split(' '), "--config", config],
            config,
            more: s => s.AddSingleton<IOutlookAccess>(_access),
            ct: TestContext.Current.CancellationToken);
}
