using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Outlook;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 slice 7 (AC-10, AC-11, AC-12, AC-13): the outlook action tools over the fake Outlook — <c>zyggy outlook guard</c> (the
/// PreToolUse hook: refuses outside policy with a row, else sends the call to the owner's permission prompt with the full message),
/// <c>zyggy outlook send | move</c> (acts only on a call the guard passed, exactly as shown, then one row) and <c>zyggy outlook settle</c>
/// (the Stop hook: a call the guard passed that never ran was denied at the prompt — one "denied" row). No test sends or moves anything
/// real: the access is the fake.
/// </summary>
public sealed class OutlookActionVerbTests : IDisposable
{
    private const string Record = """{ "date": "2026-11-21", "scope": ["read", "send", "move"] }""";

    private const string BodyCanary = "CANARY-BODY-7f3a";

    private static readonly DateTimeOffset T = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Reply =
        ["send", "--to", "colleague@example.test", "--subject", "RE: Planning", "--body", "Thanks, Thursday works. " + BodyCanary + "\n\n-----Original Message-----\nCan we meet Thursday?"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(T);
    private readonly FakeOutlookAccess _access = OutlookGuardTestData.Mailbox();

    public OutlookActionVerbTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(NodeDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        WriteNodeJson("""{ "enabled": true, "actions": ["send", "move"], "move": { "excludedFolders": ["Private"] } }""");
        File.WriteAllText(Path.Combine(NodeDir, "work-boundary.json"), "{ \"mail\": { \"policyConfirmed\": " + Record + " } }");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NodeDir => Path.Combine(_root, "node");

    // The node's own node.json at the default path (LOCALAPPDATA or XDG_CONFIG_HOME of the test's environment): the only one the
    // action tools read (verify 01M81R40G1YGA46142HYTV5H7T).
    private string AppData => Path.Combine(_root, "appdata");

    private string Config => Path.Combine(AppData, "zyggy", "node.json");

    private string LogPath => Path.Combine(NodeDir, "work", "acme", "outlook", "actions.jsonl");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- the guard hook: within policy, to the prompt with the full message (AC-12) ----

    [Fact]
    public async Task Guard_SendWithinPolicy_AsksWithTheFullMessage()
    {
        // Act
        var (exit, console) = await GuardAsync(Command(Reply));

        // Assert
        var (decision, reason) = HookOutput(console.Stdout);
        (exit, decision, reason.Contains("To: colleague@example.test\n", StringComparison.Ordinal), reason.Contains("Subject: RE: Planning\n", StringComparison.Ordinal),
            reason.Contains("Thanks, Thursday works. " + BodyCanary + "\n\n-----Original Message-----\nCan we meet Thursday?", StringComparison.Ordinal))
            .Should().Be((0, "ask", true, true, true));
    }

    [Fact]
    public async Task Guard_SendWithinPolicy_NoRowYetAndNothingSent()
    {
        // Act
        await GuardAsync(Command(Reply));

        // Assert
        (Rows().Count, _access.Sent.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task Guard_MoveWithinPolicy_AsksNamingTheMailAndTheFolder()
    {
        // Act
        var (_, console) = await GuardAsync(Command("move", "m1", "--to", "Deleted Items"));

        // Assert
        var (decision, reason) = HookOutput(console.Stdout);
        (decision, reason.Contains("Sender m1 <m1@example.test>", StringComparison.Ordinal), reason.Contains("to: Deleted Items", StringComparison.Ordinal))
            .Should().Be(("ask", true, true));
    }

    // ---- the guard hook: outside policy, refused before the prompt with one row (AC-10) ----

    public static TheoryData<string?, string[], string> Refusals() => new()
    {
        { """{ "enabled": true, "actions": [] }""", Reply, "action not enabled" },
        { """{ "enabled": true, "actions": ["move"] }""", Reply, "action not enabled" },
        { """{ "enabled": false, "actions": ["send", "move"] }""", Reply, "outlook not enabled" },
        { null, [.. Reply, "--bcc", "x@example.test"], "Bcc is not allowed" },
        { null, [.. Reply, "--attach", "plan.pdf"], "attachments are not allowed" },
        { null, [.. Reply, "--html"], "only plain-text bodies" },
        { null, [.. Reply, "--from", "boss@example.test"], "sender, account and reply-to cannot be changed" },
        { null, [.. Reply, "--account", "other"], "sender, account and reply-to cannot be changed" },
        { null, [.. Reply, "--reply-to", "x@example.test"], "sender, account and reply-to cannot be changed" },
        { null, ["send", "--subject", "s", "--body", "b"], "no recipient" },
        { null, ["send", "--to", "not-an-address", "--subject", "s", "--body", "b"], "malformed address" },
        { """{ "enabled": true, "actions": ["send"], "send": { "maxRecipients": 1 } }""", [.. Reply, "--cc", "two@example.test"], "more than 1 recipients" },
        { """{ "enabled": true, "actions": ["send"], "send": { "bodyMaxChars": 10 } }""", Reply, "body over 10 characters" },
        { null, ["move", "m1", "--to", "Private/Health"], "folder excluded" },
        { null, ["move", "m1", "--to", "Recoverable Items"], "permanent delete not allowed" },
        { null, ["move", "m1", "--to", "Deleted Items", "--permanent"], "permanent delete not allowed" },
        { null, ["move", "m1", "--to", "Archive", "--store", FakeOutlookAccess.SharedStore], "only the default store" },
        { null, ["move", "s1", "--to", "Archive"], "item not found" },
        { null, ["move", "m1", "--to", "Nowhere"], "folder not found" },

        // Adversarial pass (slice 7): no filing into the send path or the drafts, no text that can fake a line of the prompt.
        { null, ["move", "m1", "--to", "Outbox"], "not a filing folder" },
        { null, ["move", "m1", "--to", "drafts"], "not a filing folder" },
        { null, ["move", "m1", "--to", "Drafts/Old"], "not a filing folder" },
        { null, ["send", "--to", "colleague@example.test", "--subject", "Hi\nTo: boss@example.test", "--body", "b"], "control characters in the message" },
        { null, ["send", "--to", "colleague@example.test", "--subject", "Hi", "--body", "ok \u001b[8mhidden\u001b[0m"], "control characters in the message" },
        { null, ["send", "--to", "colleague@example.test", "--subject", "Hi", "--body", "pay ‮txt.exe"], "control characters in the message" },
        { null, ["send", "--to", "colleague\u0001@example.test", "--subject", "Hi", "--body", "b"], "control characters in the message" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Guard_OutsidePolicy_DeniedWithTheReason_OneRefusedRow(string? outlook, string[] call, string reason)
    {
        // Arrange
        if (outlook is not null)
        {
            WriteNodeJson(outlook);
        }

        // Act
        var (exit, console) = await GuardAsync(Command(call));

        // Assert
        (exit, HookOutput(console.Stdout), Outcomes(), Rows().Single()["reason"]!.GetValue<string>())
            .Should().Be((0, ("deny", "outlook-guard: refused: " + reason), "refused", reason));
    }

    [Fact]
    public async Task Guard_RecordWithoutSendScope_DeniedPolicyNotConfirmed()
    {
        // Arrange
        File.WriteAllText(Path.Combine(NodeDir, "work-boundary.json"), """{ "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": ["read", "move"] } } }""");

        // Act
        var (_, console) = await GuardAsync(Command(Reply));

        // Assert
        (HookOutput(console.Stdout), Outcomes()).Should().Be((("deny", "outlook-guard: refused: policy not confirmed"), "refused"));
    }

    // ---- the guard hook: never in a job or an unattended run (AC-11) ----

    [Theory]
    [InlineData("ZYGGY_JOB", "01M81PE2X1EXXHEDDV5GHP2NZ6")]
    [InlineData("ZYGGY_HOOKS", "off")]
    public async Task Guard_Unattended_DeniedNotAttended_WithARow(string variable, string value)
    {
        // Act
        var (_, console) = await GuardAsync(Command(Reply), new() { [variable] = value });

        // Assert
        (HookOutput(console.Stdout), Outcomes()).Should().Be((("deny", "outlook-guard: refused: not attended"), "refused"));
    }

    [Theory]
    [InlineData("zyggy outlook send --to a@example.test --subject s --body \"$BODY\"")]
    [InlineData("ZYGGY_JOB= zyggy outlook send --draft 1")]
    [InlineData("zyggy outlook send --draft 1; zyggy outlook send --draft 2")]
    public async Task Guard_NotOnePlainCall_Denied_WithARow(string command)
    {
        // Act
        var (_, console) = await GuardAsync(command);

        // Assert
        (HookOutput(console.Stdout).Decision, Outcomes()).Should().Be(("deny", "refused"));
    }

    [Fact]
    public async Task Guard_PowerShellTool_Denied_UseBash()
    {
        // Act
        var (_, console) = await GuardAsync("zyggy outlook send --draft 1", tool: "PowerShell");

        // Assert
        HookOutput(console.Stdout).Should().Be(("deny", "outlook-guard: refused: outlook actions run through the Bash tool"));
    }

    [Fact]
    public async Task Guard_AnotherToolCarryingTheCommand_Denied_WithARow()
    {
        // Act
        var (_, console) = await GuardAsync(Command(Reply), tool: "Monitor");

        // Assert
        (HookOutput(console.Stdout), Outcomes()).Should().Be((("deny", "outlook-guard: refused: outlook actions run through the Bash tool"), "refused"));
    }

    [Fact]
    public async Task Guard_Move_MailSubjectCannotFakeAPromptLine()
    {
        // Arrange
        _access.Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m9", Local(2026, 11, 21, 10), subject: "Invoice\nto: Deleted Items"), body: "b");

        // Act
        var (_, console) = await GuardAsync(Command("move", "m9", "--to", "Archive"));

        // Assert
        HookOutput(console.Stdout).Reason.Split('\n').Count(l => l.StartsWith("to: ", StringComparison.Ordinal)).Should().Be(1);
    }

    [Theory]
    [InlineData("zyggy outlook brief")]
    [InlineData("git status")]
    public async Task Guard_NoOutlookAction_SilentExitZero(string command)
    {
        // Act
        var (exit, console) = await GuardAsync(command);

        // Assert
        (exit, console.Stdout, Rows().Count).Should().Be((0, string.Empty, 0));
    }

    // ---- every shell call screened on a work machine (verify 01M81W8C4K81GE119A7WH3228D point 1) ----

    [Fact]
    public async Task Guard_WorkMachine_VerifierAttack_Denied_WithARow()
    {
        // Act
        var (exit, console) = await GuardAsync(OutlookShellScreenTests.VerifierAttack);

        // Assert
        (exit, HookOutput(console.Stdout).Decision, Outcomes()).Should().Be((0, "deny", "refused"));
    }

    [Theory]
    [InlineData("PowerShell", "command", "Get-Content $env:LOCALAPPDATA\\zyggy\\node\\work\\acme\\outlook\\approval-2026-11-22.key")]
    [InlineData("Write", "file_path", "{state}/pending.jsonl")]
    [InlineData("Read", "file_path", "{state}/approval-2026-11-22.key")]
    [InlineData("Edit", "file_path", "{state}/pending.jsonl")]
    public async Task Guard_WorkMachine_ToolNamingTheStateFolder_Denied(string tool, string property, string value)
    {
        // Act
        var (_, console) = await GuardInputAsync(tool, new JsonObject { [property] = value.Replace("{state}", StateDir, StringComparison.Ordinal) });

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("deny");
    }

    [Theory]
    [InlineData("Read", "file_path", "../outlook/approval-2026-11-22.key")]
    [InlineData("Write", "file_path", "./../outlook/./pending.jsonl")]
    [InlineData("Glob", "path", "..\\outlook")]
    [InlineData("Bash", "command", "cp -r ../outlook /tmp/x")]
    public async Task Guard_WorkMachine_RelativePathIntoTheStateFolder_Denied(string tool, string property, string value)
    {
        // Arrange: the session sits next to the state folder (verify 01M81XMXM722ESMJ8M0Y6VBP6N): the guard compares full paths.
        string cwd = Path.Combine(NodeDir, "work", "acme", "brief");
        Directory.CreateDirectory(cwd);

        // Act
        var (_, console) = await GuardInputAsync(tool, new JsonObject { [property] = value }, cwd);

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("deny");
    }

    // Verify 01M81Z8AHR6ZWCDWQS92TYSA54: a search rooted at the state folder or any parent of it reaches the pass file and the key;
    // Grep's glob and Glob's pattern are screened as paths from that root.
    [Theory]
    [InlineData("Glob", """{ "pattern": "**/*.key", "path": "{node}" }""", "{project}")]
    [InlineData("Grep", """{ "pattern": "nonce", "path": "{node}/work" }""", "{project}")]
    [InlineData("Grep", """{ "pattern": "nonce" }""", "{node}")]
    [InlineData("Glob", """{ "pattern": "**/*" }""", "{node}")]
    [InlineData("Glob", """{ "pattern": "{node}/*/*/*/*" }""", "{project}")]
    [InlineData("Grep", """{ "pattern": "nonce", "glob": "../outlook/*" }""", "{brief}")]
    [InlineData("Glob", """{ "pattern": "../*/*" }""", "{brief}")]
    public async Task Guard_WorkMachine_SearchReachingTheStateFolder_Denied(string tool, string input, string cwd)
    {
        // Act
        var (_, console) = await GuardInputAsync(tool, JsonNode.Parse(Placeholders(input))!.AsObject(), Placeholders(cwd));

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("deny");
    }

    [Theory]
    [InlineData("Glob", """{ "pattern": "**/*.cs" }""")]
    [InlineData("Grep", """{ "pattern": "nonce", "path": "src", "glob": "*.cs" }""")]
    [InlineData("Grep", """{ "pattern": "nonce", "glob": "**/*.md" }""")]
    public async Task Guard_WorkMachine_SearchElsewhere_Silent(string tool, string input)
    {
        // Act
        var (_, console) = await GuardInputAsync(tool, JsonNode.Parse(Placeholders(input))!.AsObject(), Placeholders("{project}"));

        // Assert
        console.Stdout.Should().BeEmpty();
    }

    private string Placeholders(string text) => text
        .Replace("{node}", NodeDir.Replace('\\', '/'), StringComparison.Ordinal)
        .Replace("{project}", Directory.CreateDirectory(Path.Combine(_root, "project")).FullName.Replace('\\', '/'), StringComparison.Ordinal)
        .Replace("{brief}", Directory.CreateDirectory(Path.Combine(NodeDir, "work", "acme", "brief")).FullName.Replace('\\', '/'), StringComparison.Ordinal);

    [Fact]
    public async Task Guard_WorkMachine_ScriptFileRunningOutlook_Denied()
    {
        // Arrange: the script was written by an earlier call; the hook reads it relative to the session's directory.
        string cwd = Path.Combine(_root, "project");
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "s.sh"), "#!/bin/sh\nZ=zyggy\n\"$Z\" outlook send --draft 1\n");

        // Act
        var (_, console) = await GuardInputAsync("Bash", new JsonObject { ["command"] = "bash s.sh" }, cwd);

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Guard_NotAWorkMachine_DisguisedCommand_Silent()
    {
        // Arrange
        WriteNodeJson("""{ "enabled": false }""");

        // Act
        var (exit, console) = await GuardAsync(OutlookShellScreenTests.VerifierAttack);

        // Assert
        (exit, console.Stdout).Should().Be((0, string.Empty));
    }

    [Fact]
    public async Task Send_ForgedPassLineWithOwnNonce_RefusedNothingSent()
    {
        // Arrange: the attack's second half — a hand-written line for this exact request and the hash of the attacker's own nonce.
        string key = OutlookActionCall.Parse(Reply, out _)!.Request!.Key;
        string file = Path.Combine(StateDir, "pending.jsonl");
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(file, "{\"key\":\"" + key + "\",\"nonce\":\"" + OutlookActionPending.NonceHash("mine") + "\",\"ts\":\"2026-11-22T09:00:00.0000000Z\",\"action\":\"send\",\"to\":[\"colleague@example.test\"],\"subject\":\"RE: Planning\"}\n");

        // Act
        var (exit, _) = await ActAsync(Reply, "mine");

        // Assert
        (exit, _access.Sent.Count).Should().Be((5, 0));
    }

    [Fact]
    public async Task Guard_BadHookInput_ExitTwo_BlocksTheCall()
    {
        // Act
        var (exit, console) = await RunAsync(["guard"], stdin: "not json");

        // Assert
        (exit, console.Stderr.StartsWith("outlook-guard: ", StringComparison.Ordinal)).Should().Be((2, true));
    }

    // ---- the session's settings (spec 58 Q24): every tool beyond Read and the read verbs stays on the owner's prompt ----

    [Fact]
    public async Task Guard_RecommendedProjectSettings_Asks()
    {
        // Arrange
        WriteSettings(Project, "settings.json", """{ "permissions": { "defaultMode": "default", "allow": ["Read", "Bash(zyggy outlook brief *)"], "deny": ["Write", "Edit", "NotebookEdit"] } }""");

        // Act
        var (_, console) = await GuardAsync(Command(Reply));

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("ask");
    }

    [Theory]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public async Task Guard_SessionNotInDefaultMode_DeniedWithOneRow(string mode)
    {
        // Act
        var (_, console) = await GuardAsync(Command(Reply), mode: mode);

        // Assert
        (HookOutput(console.Stdout).Decision, HookOutput(console.Stdout).Reason.Contains("permission mode", StringComparison.Ordinal), Outcomes(), _access.Sent.Count)
            .Should().Be(("deny", true, "refused", 0));
    }

    [Theory]
    [InlineData("home", "settings.json", "Bash")]
    [InlineData("project", "settings.local.json", "PowerShell")]
    [InlineData("project", "settings.json", "Write")]
    [InlineData("home", "settings.json", "Edit")]
    public async Task Guard_SettingsAutoAllowCode_MoveDenied(string where, string file, string rule)
    {
        // Arrange
        WriteSettings(where == "home" ? Home : Project, file, "{ \"permissions\": { \"allow\": [\"" + rule + "\"] } }");

        // Act
        var (_, console) = await GuardAsync(Command("move", "m1", "--to", "Deleted Items"));

        // Assert
        (HookOutput(console.Stdout).Decision, HookOutput(console.Stdout).Reason.Contains(rule, StringComparison.Ordinal), Outcomes()).Should().Be(("deny", true, "refused"));
    }

    [Fact]
    public async Task Guard_SettingsFileNotJson_Denied()
    {
        // Arrange
        WriteSettings(Project, "settings.local.json", "{ not json");

        // Act
        var (_, console) = await GuardAsync(Command(Reply));

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("deny");
    }

    [Fact]
    public async Task Guard_ReadVerbWithCodeAutoAllowed_StillSilent()
    {
        // Arrange: the settings check is for send and move; a read verb is not the guard's.
        WriteSettings(Home, "settings.json", """{ "permissions": { "allow": ["Bash"] } }""");

        // Act
        var (exit, console) = await GuardAsync("zyggy outlook brief");

        // Assert
        (exit, console.Stdout).Should().Be((0, string.Empty));
    }

    // ---- send: only what the guard passed, exactly once, then one row (AC-12) ----

    [Fact]
    public async Task Send_AfterTheGuardPassed_SendsExactlyTheShownMessageOnce()
    {
        // Arrange
        string nonce = await PassAsync(Reply);

        // Act
        var (exit, _) = await ActAsync(Reply, nonce);

        // Assert
        (exit, _access.Sent).Should().BeEquivalentTo((0, new[] { new OutlookOutgoing(["colleague@example.test"], [], "RE: Planning", Reply[6]) }));
    }

    // ---- the pass's nonce (verify 01M81R40G1YGA46142HYTV5H7T point 1) ----

    [Fact]
    public async Task Guard_Ask_UpdatesTheCommandWithARandomNonce_KeepingTheOtherInput()
    {
        // Act
        var (_, console) = await GuardAsync(Command(Reply));

        // Assert
        var updated = JsonNode.Parse(console.Stdout)!["hookSpecificOutput"]!["updatedInput"]!;
        string command = updated["command"]!.GetValue<string>();
        (command.StartsWith(Command(Reply) + " --nonce ", StringComparison.Ordinal), System.Text.RegularExpressions.Regex.IsMatch(Nonce(console.Stdout), "^[0-9a-f]{64}$"), updated["description"]!.GetValue<string>())
            .Should().Be((true, true, "send the reply"));
    }

    [Fact]
    public async Task Guard_TwoPasses_TwoDifferentNonces()
    {
        // Arrange
        string first = await PassAsync(Reply);

        // Act
        string second = await PassAsync(Reply);

        // Assert
        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Guard_PendingFileKeepsTheNonceHashOnly()
    {
        // Act
        string nonce = await PassAsync(Reply);

        // Assert
        string pending = File.ReadAllText(Path.Combine(NodeDir, "work", "acme", "outlook", "pending.jsonl"));
        (pending.Contains(nonce, StringComparison.Ordinal), pending.Contains(OutlookActionPending.NonceHash(nonce), StringComparison.Ordinal)).Should().Be((false, true));
    }

    [Fact]
    public async Task Send_SameCommandWithoutTheNonce_RefusedNothingSent_ThePassStays()
    {
        // Arrange: another process runs the very command the owner was shown, without the nonce.
        string nonce = await PassAsync(Reply);
        var (other, _) = await RunAsync(Reply);

        // Act: the owner's Allow still sends.
        var (owner, _) = await ActAsync(Reply, nonce);

        // Assert
        (other, owner, _access.Sent.Count).Should().Be((5, 0, 1));
    }

    [Fact]
    public async Task Send_WrongNonce_RefusedNothingSent()
    {
        // Arrange
        await PassAsync(Reply);

        // Act
        var (exit, _) = await ActAsync(Reply, new string('0', 64));

        // Assert
        (exit, _access.Sent.Count).Should().Be((5, 0));
    }

    [Fact]
    public async Task Send_AfterDeny_TheNextGuardedCallInvalidatesThePass()
    {
        // Arrange: the owner denies (the tool never runs); the session goes on with another shell call.
        string nonce = await PassAsync(Reply);
        await GuardAsync("git status");

        // Act: the nonce, known from the prompt, no longer sends.
        var (exit, _) = await ActAsync(Reply, nonce);

        // Assert
        (exit, _access.Sent.Count, Outcomes()).Should().Be((5, 0, "denied refused"));
    }

    [Fact]
    public async Task Guard_CommandCarryingANonce_DeniedAndThePassIsGone()
    {
        // Arrange: after a Deny the session retries the command it was shown, nonce and all.
        string nonce = await PassAsync(Reply);
        var (_, retry) = await GuardAsync(Command([.. Reply, "--nonce", nonce]));

        // Act
        var (exit, _) = await ActAsync(Reply, nonce);

        // Assert
        (HookOutput(retry.Stdout).Decision, exit, _access.Sent.Count).Should().Be(("deny", 5, 0));
    }

    [Fact]
    public async Task Send_ABodyThatReadsNonce_IsTheBody_SentWithThePassOnly()
    {
        // Arrange: "--nonce" as an option's value is that value, not a nonce.
        string[] call = ["send", "--to", "colleague@example.test", "--subject", "--nonce", "--body", "--nonce"];
        string nonce = await PassAsync(call);

        // Act
        var (exit, _) = await ActAsync(call, nonce);

        // Assert
        (exit, _access.Sent).Should().BeEquivalentTo((0, new[] { new OutlookOutgoing(["colleague@example.test"], [], "--nonce", "--nonce") }));
    }

    // ---- the node's own node.json only (verify 01M81R40G1YGA46142HYTV5H7T point 2) ----

    [Fact]
    public async Task Guard_ZyggyNodeJsonNamesAnotherFile_TheDefaultNodeJsonIsRead()
    {
        // Arrange: a node.json elsewhere with the actions off; the node's own has them on.
        string decoy = Path.Combine(_root, "decoy.json");
        File.WriteAllText(decoy, File.ReadAllText(Config).Replace("\"enabled\": true", "\"enabled\": false", StringComparison.Ordinal));

        // Act
        var (_, console) = await GuardAsync(Command(Reply), new() { ["ZYGGY_NODE_JSON"] = decoy });

        // Assert
        HookOutput(console.Stdout).Decision.Should().Be("ask");
    }

    [Fact]
    public async Task Send_ZyggyNodeJsonNamesAnotherNodeDir_ThePassOfTheNodesOwnStateIsTaken()
    {
        // Arrange: a node.json elsewhere whose node directory holds no pass.
        string nonce = await PassAsync(Reply);
        string decoy = Path.Combine(_root, "decoy.json");
        File.WriteAllText(decoy, File.ReadAllText(Config).Replace(NodeDir.Replace('\\', '/'), Path.Combine(_root, "other").Replace('\\', '/'), StringComparison.Ordinal));

        // Act
        var (exit, _) = await ActAsync(Reply, nonce, new() { ["ZYGGY_NODE_JSON"] = decoy });

        // Assert
        (exit, _access.Sent.Count).Should().Be((0, 1));
    }

    [Fact]
    public async Task Send_AfterTheGuardPassed_OneDoneRowWithoutTheBody()
    {
        // Arrange
        string nonce = await PassAsync(Reply);

        // Act
        await ActAsync(Reply, nonce);

        // Assert
        (Outcomes(), File.ReadAllText(LogPath).Contains(BodyCanary, StringComparison.Ordinal), Rows().Single()["subject"]!.GetValue<string>())
            .Should().Be(("done", false, "RE: Planning"));
    }

    [Fact]
    public async Task Send_TheGuardNeverPassedIt_RefusedNothingSent_OneRow()
    {
        // Act
        var (exit, console) = await RunAsync(Reply);

        // Assert
        (exit, console.Stderr, _access.Sent.Count, Outcomes()).Should().Be((5, "outlook: refused: no prompt for this call (the outlook guard hook did not pass it)\n", 0, "refused"));
    }

    [Fact]
    public async Task Send_ADifferentMessageThanTheGuardPassed_Refused()
    {
        // Arrange
        string nonce = await PassAsync(Reply);
        string[] other = [.. Reply[..5], "--body", "Something else entirely."];

        // Act
        var (exit, _) = await ActAsync(other, nonce);

        // Assert
        (exit, _access.Sent.Count).Should().Be((5, 0));
    }

    [Fact]
    public async Task Send_PassedTwiceSentOnce_SecondRunRefused()
    {
        // Arrange
        string nonce = await PassAsync(Reply);
        await ActAsync(Reply, nonce);

        // Act
        var (exit, _) = await ActAsync(Reply, nonce);

        // Assert
        (exit, _access.Sent.Count).Should().Be((5, 1));
    }

    [Theory]
    [InlineData("ZYGGY_JOB", "01M81PE2X1EXXHEDDV5GHP2NZ6")]
    [InlineData("ZYGGY_HOOKS", "off")]
    public async Task Send_Unattended_RefusedNotAttended_NothingSent(string variable, string value)
    {
        // Arrange: a pass from an attended session does not carry into an unattended run.
        string nonce = await PassAsync(Reply);

        // Act
        var (exit, console) = await ActAsync(Reply, nonce, new() { [variable] = value });

        // Assert
        (exit, console.Stderr, _access.Sent.Count).Should().Be((5, "outlook: refused: not attended\n", 0));
    }

    [Fact]
    public async Task Send_PassOlderThanTheLifetime_Refused_AndThePassSettledAsDenied()
    {
        // Arrange
        string nonce = await PassAsync(Reply);
        _clock.Advance(OutlookActionPending.Lifetime + TimeSpan.FromSeconds(1));

        // Act
        var (exit, _) = await ActAsync(Reply, nonce);

        // Assert
        (exit, _access.Sent.Count, Outcomes()).Should().Be((5, 0, "denied refused"));
    }

    [Fact]
    public async Task Send_OutlookFails_ExitSix_OneFailedRow()
    {
        // Arrange
        string nonce = await PassAsync(Reply);
        _access.ActionsFail = true;

        // Act
        var (exit, _) = await ActAsync(Reply, nonce);

        // Assert
        (exit, Outcomes(), Rows().Single()["reason"]!.GetValue<string>()).Should().Be((6, "failed", "Outlook not available"));
    }

    [Fact]
    public async Task Send_ConfigOption_UsageError_TheNodesOwnNodeJsonOnly()
    {
        // Act
        var (exit, console) = await RunAsync([.. Reply, "--config", Config]);

        // Assert
        (exit, console.Stderr.Contains("--config", StringComparison.Ordinal), _access.Sent.Count).Should().Be((2, true, 0));
    }

    // ---- send by draft number (D-Q17): the kept draft, to its sender, quoted history as received ----

    [Fact]
    public async Task SendDraft_AfterTheGuardPassed_SendsTheDraftTextAndQuotedHistoryToTheSender()
    {
        // Arrange
        KeepDraft();
        string nonce = await PassAsync(["send", "--draft", "1"]);

        // Act
        var (exit, _) = await ActAsync(["send", "--draft", "1"], nonce);

        // Assert
        (exit, _access.Sent).Should().BeEquivalentTo((0, new[] { new OutlookOutgoing(["m1@example.test"], [], "RE: Subject", "I confirm.\n\n-----Original Message-----\nquoted as received") }));
    }

    [Fact]
    public async Task GuardDraft_AsksWithTheDraftsWholeMessage()
    {
        // Arrange
        KeepDraft();

        // Act
        var (_, console) = await GuardAsync(Command("send", "--draft", "1"));

        // Assert
        HookOutput(console.Stdout).Reason.Should().Contain("To: m1@example.test\n").And.Contain("I confirm.\n\n-----Original Message-----\nquoted as received");
    }

    [Fact]
    public async Task SendDraft_DraftChangedAfterThePrompt_Refused()
    {
        // Arrange
        KeepDraft();
        string nonce = await PassAsync(["send", "--draft", "1"]);
        KeepDraft(text: "I decline.");

        // Act
        var (exit, _) = await ActAsync(["send", "--draft", "1"], nonce);

        // Assert
        (exit, _access.Sent.Count).Should().Be((5, 0));
    }

    [Fact]
    public async Task GuardDraft_NoSuchDraft_DeniedDraftNotFound_WithARow()
    {
        // Act
        var (_, console) = await GuardAsync(Command("send", "--draft", "4"));

        // Assert
        (HookOutput(console.Stdout), Outcomes()).Should().Be((("deny", "outlook-guard: refused: draft not found"), "refused"));
    }

    [Fact]
    public async Task SendDraft_WithMessageOptions_UsageError()
    {
        // Act
        var (exit, _) = await RunAsync(["send", "--draft", "1", "--to", "x@example.test"]);

        // Assert
        exit.Should().Be(2);
    }

    // ---- settle: a passed call that never ran was denied at the prompt (AC-12 Deny) ----

    [Fact]
    public async Task Settle_PassedButNeverRun_OneDeniedRow_NothingSent()
    {
        // Arrange
        await GuardAsync(Command(Reply));

        // Act
        var (exit, _) = await RunAsync(["settle"]);

        // Assert
        (exit, Outcomes(), _access.Sent.Count, Rows().Single()["to"]!.AsArray().Single()!.GetValue<string>()).Should().Be((0, "denied", 0, "colleague@example.test"));
    }

    [Fact]
    public async Task Settle_AfterTheSend_NoSecondRow()
    {
        // Arrange
        string nonce = await PassAsync(Reply);
        await ActAsync(Reply, nonce);

        // Act
        await RunAsync(["settle"]);

        // Assert
        Outcomes().Should().Be("done");
    }

    [Fact]
    public async Task TwoSendsInOneSentence_TwoPromptsTwoRows()
    {
        // Arrange: Claude Code runs each Bash call through its hook, its prompt and the tool before the next one.
        string[] second = [.. Reply[..5], "--body", "Second reply."];
        await ActAsync(Reply, await PassAsync(Reply));
        string nonce = await PassAsync(second);

        // Act
        await ActAsync(second, nonce);

        // Assert
        (_access.Sent.Count, Outcomes()).Should().Be((2, "done done"));
    }

    // ---- move (AC-13) ----

    [Fact]
    public async Task Move_ToDeletedItems_TheMailIsInDeletedItems_OneDoneRow()
    {
        // Arrange
        string nonce = await PassAsync(["move", "m1", "--to", "Deleted Items"]);

        // Act
        var (exit, _) = await ActAsync(["move", "m1", "--to", "Deleted Items"], nonce);

        // Assert
        (exit, _access.FolderOf("m1"), Outcomes(), Rows().Single()["destination"]!.GetValue<string>()).Should().Be((0, "Deleted Items", "done", "Deleted Items"));
    }

    [Fact]
    public async Task Move_ToAProjectFolder_MovesExactlyThatMail()
    {
        // Arrange
        string nonce = await PassAsync(["move", "m1", "--to", "Inbox/Projects"]);

        // Act
        await ActAsync(["move", "m1", "--to", "Inbox/Projects"], nonce);

        // Assert
        (_access.FolderOf("m1"), _access.Calls.Count(c => c.StartsWith("Move ", StringComparison.Ordinal))).Should().Be(("Inbox/Projects", 1));
    }

    [Fact]
    public async Task Move_InAJob_RefusedNotAttended_NotMoved()
    {
        // Act
        var (exit, _) = await RunAsync(["move", "m1", "--to", "Archive"], new() { ["ZYGGY_JOB"] = "01M81PE2X1EXXHEDDV5GHP2NZ6" });

        // Assert
        (exit, _access.FolderOf("m1"), Outcomes()).Should().Be((5, "Inbox", "refused"));
    }

    // ---- helpers ----

    private static string Command(params string[] args) => "zyggy outlook " + string.Join(' ', args.Select(Quote));

    private static string Quote(string arg) => "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static (string Decision, string Reason) HookOutput(string stdout)
    {
        var output = JsonNode.Parse(stdout)!["hookSpecificOutput"]!;
        return (output["permissionDecision"]!.GetValue<string>(), output["permissionDecisionReason"]!.GetValue<string>());
    }

    private static string? UpdatedCommand(string stdout) =>
        JsonNode.Parse(stdout)!["hookSpecificOutput"]!["updatedInput"]?["command"]?.GetValue<string>();

    // The nonce the guard appended to the command it passed to the prompt (the last word of the updated command).
    private static string Nonce(string stdout) => UpdatedCommand(stdout)!.Split(' ')[^1];

    // The guard passes the call to the prompt; returns the nonce of the pass.
    private async Task<string> PassAsync(string[] call)
    {
        var (_, console) = await GuardAsync(Command(call));
        return Nonce(console.Stdout);
    }

    // The tool as Claude Code runs it after Allow: the command the guard updated, with its nonce.
    private Task<(int Exit, VerbConsole Console)> ActAsync(string[] call, string nonce, Dictionary<string, string?>? extra = null) =>
        RunAsync([.. call, "--nonce", nonce], extra);

    private void KeepDraft(string text = "I confirm.")
    {
        var draft = new OutlookDraft(1, "m1", "Sender m1", "m1@example.test", "RE: Subject", text, "-----Original Message-----\nquoted as received");
        string path = OutlookDrafts.StatePath(NodeDir, TenantId.Parse("acme"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OutlookDrafts.Save(path, new OutlookBriefState(null, [draft]));
    }

    private List<JsonObject> Rows() =>
        File.Exists(LogPath) ? [.. File.ReadAllLines(LogPath).Select(l => JsonNode.Parse(l)!.AsObject())] : [];

    private string Outcomes() => string.Join(' ', Rows().Select(r => r["outcome"]!.GetValue<string>()));

    private string StateDir => Path.Combine(NodeDir, "work", "acme", "outlook");

    // The session's home (user settings in .claude/settings.json) and its project folder (.claude/settings.json, settings.local.json).
    private string Home => Path.Combine(_root, "home");

    private string Project => Path.Combine(_root, "project");

    private static void WriteSettings(string folder, string file, string json)
    {
        Directory.CreateDirectory(Path.Combine(folder, ".claude"));
        File.WriteAllText(Path.Combine(folder, ".claude", file), json);
    }

    private Task<(int Exit, VerbConsole Console)> GuardInputAsync(string tool, JsonObject input, string? cwd = null)
    {
        string hook = new JsonObject
        {
            ["session_id"] = "s1",
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = cwd ?? _root,
            ["permission_mode"] = "default",
            ["tool_name"] = tool,
            ["tool_input"] = input,
            ["tool_use_id"] = "toolu_1",
        }.ToJsonString();
        return RunAsync(["guard"], stdin: hook);
    }

    private Task<(int Exit, VerbConsole Console)> GuardAsync(string command, Dictionary<string, string?>? extra = null, string tool = "Bash", string mode = "default")
    {
        string hook = new JsonObject
        {
            ["session_id"] = "s1",
            ["hook_event_name"] = "PreToolUse",
            ["cwd"] = Project,
            ["permission_mode"] = mode,
            ["tool_name"] = tool,
            ["tool_input"] = new JsonObject { ["command"] = command, ["description"] = "send the reply" },
            ["tool_use_id"] = "toolu_1",
        }.ToJsonString();
        return RunAsync(["guard"], extra, hook);
    }

    private void WriteNodeJson(string outlookSection)
    {
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "acme",
              "machine": "work-laptop",
              "nodeDir": "{{NodeDir.Replace('\\', '/')}}",
              "outlook": {{outlookSection}},
              "bus": { "owner": "acme-org", "repo": "acme-bus", "localPath": "{{_root.Replace('\\', '/')}}/bus", "remote": "https://example.invalid/acme-org/acme-bus.git" },
              "secrets": { "store": "file", "root": "{{_root.Replace('\\', '/')}}/secrets" }
            }
            """);
        File.WriteAllText(Config, json);
    }

    private async Task<(int Exit, VerbConsole Console)> RunAsync(string[] args, Dictionary<string, string?>? extra = null, string? stdin = null)
    {
        var console = new VerbConsole(stdin);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["LOCALAPPDATA"] = AppData, ["XDG_CONFIG_HOME"] = AppData, ["USERPROFILE"] = Home, ["HOME"] = Home };
        foreach (var (key, value) in extra ?? [])
        {
            environment[key] = value;
        }

        var host = new OutlookVerbHost(environment, _clock, Zone, () => _access);
        int exit = await host.RunAsync(args, console.Io, Ct);
        return (exit, console);
    }
}
