using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Outlook;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Outlook;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 58 slice 7, AC-12 and AC-13 through the real <c>zyggy</c> entry point (in process) on a work-laptop <c>node.json</c> with the
/// fake Outlook at the internal seam: the PreToolUse hook <c>zyggy outlook guard</c> sends a send within policy to the owner's prompt
/// with the full message; Allow runs <c>zyggy outlook send</c>, which sends exactly that message once and writes one row without the
/// body; Deny never runs it and the Stop hook <c>zyggy outlook settle</c> writes one <c>denied</c> row; a move to Deleted Items leaves
/// the mail there. Nothing real is sent or moved.
/// </summary>
public sealed class OutlookActionCommandTests : IAsyncLifetime
{
    private const string WorkBoundary = """{ "neverList": ["national register numbers"], "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": ["read", "send", "move"] } } }""";

    private const string BodyCanary = "CANARY-SEND-BODY-2b9e";

    private static readonly string[] Reply =
        ["send", "--to", "m1@example.test", "--subject", "RE: Budget", "--body", "Confirmed. " + BodyCanary + "\n\n-----Original Message-----\nCan you confirm?"];

    private CliHarness _harness = null!;

    private FakeOutlookAccess _access = null!;

    private string _config = null!;

    private string _appData = null!;

    private string LogPath => Path.Combine(_harness.NodeDirOf("actions"), "work", "acme", "outlook", "actions.jsonl");

    public async ValueTask InitializeAsync()
    {
        _harness = await CliHarness.CreateAsync(CancellationToken.None);
        _access = new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Unspecified)), body: "Can you confirm?")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Deleted Items");
        // The node's own node.json at the default path: the only one the action tools read (ZYGGY_NODE_JSON is ignored).
        _appData = _harness.Scratch.File("appdata");
        Directory.CreateDirectory(Path.Combine(_appData, "zyggy"));
        _config = NodeJson.Write(
            Path.Combine(_appData, "zyggy"),
            CliHarness.Acme,
            CliHarness.WorkLaptop,
            TestKeys.Acme1,
            busLocalPath: _harness.Bus.CloneDir,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            nodeDir: _harness.NodeDirOf("actions"),
            extraTopLevelJson: "\"outlook\": { \"enabled\": true, \"actions\": [\"send\", \"move\"] }");
        Directory.CreateDirectory(_harness.NodeDirOf("actions"));
        await File.WriteAllTextAsync(Path.Combine(_harness.NodeDirOf("actions"), "work-boundary.json"), WorkBoundary);
    }

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    // AC-12: one prompt with the full message, then Allow sends exactly it once, one row without the body.
    [Fact]
    public async Task GuardThenSend_Allowed_SentOnceAsShown_OneDoneRowWithoutTheBody()
    {
        // Arrange
        var (_, guard) = await RunAsync(["guard"], Hook(Command(Reply)));

        // Act: Claude Code runs the command the guard updated, with its nonce.
        var (exit, _) = await RunAsync([.. Reply, "--nonce", Nonce(guard.Stdout)]);

        // Assert
        (guard.Stdout.Contains("\"permissionDecision\":\"ask\"", StringComparison.Ordinal), exit, _access.Sent.Count, _access.Sent[0].Body, Outcomes(), File.ReadAllText(LogPath).Contains(BodyCanary, StringComparison.Ordinal))
            .Should().Be((true, 0, 1, Reply[6], "done", false));
    }

    // AC-12: Deny — the tool never runs; the Stop hook writes the "denied" row; nothing is sent.
    [Fact]
    public async Task GuardThenSettle_Denied_NothingSent_OneDeniedRow()
    {
        // Arrange
        await RunAsync(["guard"], Hook(Command(Reply)));

        // Act
        var (exit, _) = await RunAsync(["settle"]);

        // Assert
        (exit, _access.Sent.Count, Outcomes()).Should().Be((0, 0, "denied"));
    }

    // AC-13: a move to Deleted Items leaves the mail in Deleted Items; never a permanent delete.
    [Fact]
    public async Task GuardThenMove_DeletedItems_TheMailIsThere_OneDoneRow()
    {
        // Arrange
        string[] move = ["move", "m1", "--to", "Deleted Items"];
        var (_, guard) = await RunAsync(["guard"], Hook(Command(move)));

        // Act
        var (exit, _) = await RunAsync([.. move, "--nonce", Nonce(guard.Stdout)]);

        // Assert
        (exit, _access.FolderOf("m1"), Outcomes()).Should().Be((0, "Deleted Items", "done"));
    }

    // AC-11: a job's environment (the node sets ZYGGY_JOB and ZYGGY_HOOKS=off) is refused "not attended" by the hook, with a row.
    [Fact]
    public async Task Guard_JobEnvironment_DeniedNotAttended_OneRow()
    {
        // Act
        var (_, guard) = await RunAsync(["guard"], Hook(Command(Reply)), new Dictionary<string, string?> { ["ZYGGY_JOB"] = "01M81PE2X1EXXHEDDV5GHP2NZ6", ["ZYGGY_HOOKS"] = "off" });

        // Assert
        (guard.Stdout.Contains("outlook-guard: refused: not attended", StringComparison.Ordinal), Outcomes(), _access.Sent.Count).Should().Be((true, "refused", 0));
    }

    // A pass can be used only with the nonce the guard put into the updated command; another process running the shown command
    // without it takes nothing (verify 01M81R40G1YGA46142HYTV5H7T).
    [Fact]
    public async Task GuardThenSend_SameCommandWithoutTheNonce_RefusedNothingSent()
    {
        // Arrange
        await RunAsync(["guard"], Hook(Command(Reply)));

        // Act
        var (exit, _) = await RunAsync(Reply);

        // Assert
        (exit, _access.Sent.Count).Should().Be((5, 0));
    }

    private static string Nonce(string guardStdout) =>
        JsonNode.Parse(guardStdout)!["hookSpecificOutput"]!["updatedInput"]!["command"]!.GetValue<string>().Split(' ')[^1];

    private static string Command(string[] args) => "zyggy outlook " + string.Join(' ', args.Select(a => "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));

    private static string Hook(string command) => new JsonObject
    {
        ["session_id"] = "s1",
        ["hook_event_name"] = "PreToolUse",
        ["tool_name"] = "Bash",
        ["tool_input"] = new JsonObject { ["command"] = command },
    }.ToJsonString();

    private string Outcomes() =>
        File.Exists(LogPath) ? string.Join(' ', File.ReadAllLines(LogPath).Select(l => JsonNode.Parse(l)!["outcome"]!.GetValue<string>())) : string.Empty;

    private Task<(int Exit, VerbConsole Console)> RunAsync(string[] args, string? stdin = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var extra = new Dictionary<string, string?>(environment ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = _appData,
            ["XDG_CONFIG_HOME"] = _appData,
        };
        return _harness.RunAsync(
            ["outlook", .. args],
            _config,
            stdin: stdin,
            extraEnvironment: extra,
            more: s => s.AddSingleton<IOutlookAccess>(_access),
            ct: TestContext.Current.CancellationToken);
    }
}
