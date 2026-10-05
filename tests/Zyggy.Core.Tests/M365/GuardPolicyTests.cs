using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Core.M365.Guard;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// The guard's decision table — <c>m365-guard.sh</c>'s matrix (spec 33 AC-20; bats "guard (AC-35)") over the copied hook fixtures,
/// with Graph read through the stubbed handler: clean calls pass (the ask rule prompts), every out-of-policy call is denied with the
/// shell's reason. The guard never allows and never asks.
/// </summary>
public sealed class GuardPolicyTests : IDisposable
{
    private readonly GraphFixture _graph = new();

    public static TheoryData<string> Passes() =>
    [
        "send-clean", "send-save-true", "send-lowercase-keys", "send-4000", "send-10-recipients",
        "upload-clean",
        "move-deleteditems", "move-archive", "move-inbox", "move-folder-id", "move-lowercase-key",
    ];

    public static TheoryData<string, string> Denials() => new()
    {
        { "send-attachment", "attachments are not allowed" },
        { "send-bcc", "Bcc is not allowed" },
        { "send-html", "only plain-text bodies" },
        { "send-no-contenttype", "only plain-text bodies" },
        { "send-long", "body over 4000 characters" },
        { "send-11-recipients", "more than 10 recipients" },
        { "send-save-false", "saveToSentItems must stay true" },
        { "send-other-mailbox", "only the configured mailbox" },
        { "send-malformed-address", "malformed address" },
        { "send-no-recipient", "no recipient" },
        { "send-duplicate-keys", "the body names a field twice" },
        { "send-stray-arg", "unexpected argument bccRecipients" },
        { "send-from", "from, sender and replyTo are not allowed" },
        { "send-headers", "message field not allowed" },
        { "upload-exists", "target exists (would overwrite)" },
        { "upload-item-id", "only the new-file form <parent-id>:/<name>:" },
        { "upload-foreign-drive", "drive not allowed" },
        { "upload-name-slash", "invalid file name" },
        { "upload-name-backslash", "invalid file name" },
        { "upload-name-dotdot", "invalid file name" },
        { "upload-name-control", "invalid file name" },
        { "upload-docx", "extension not allowed" },
        { "upload-not-base64", "content is not base64" },
        { "upload-parent-file", "parent is not a folder" },
        { "upload-parent-absent", "parent is not a folder" },
        { "move-recoverable", "destination not allowed" },
        { "move-purges", "destination not allowed" },
        { "move-junkemail", "destination not allowed" },
        { "move-excluded-id", "destination not allowed" },
        { "move-unknown-id", "destination not allowed" },
        { "move-no-destination", "destination not allowed" },
        { "move-other-mailbox", "only the configured mailbox" },
    };

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
    }

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task Evaluate_CleanCall_PassAtMostTwoReads(string fixture)
    {
        // Act
        var decision = await EvaluateAsync(Hook(fixture));

        // Assert
        decision.Should().Be(GuardDecision.Pass);
        GraphGets.Should().BeLessThanOrEqualTo(2);
    }

    [Theory]
    [MemberData(nameof(Denials))]
    public async Task Evaluate_OutOfPolicy_DenyWithShellReason(string fixture, string reason)
    {
        // Act
        var decision = await EvaluateAsync(Hook(fixture));

        // Assert
        decision.Should().Be(new GuardDecision.Deny(reason));
        GraphGets.Should().BeLessThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(262144, true)]
    [InlineData(262145, false)]
    public async Task Evaluate_UploadSize_CapInclusive(int bytes, bool passes)
    {
        // Arrange: the size cases are built, not committed (bats upload_of_size)
        var hook = JsonNode.Parse(File.ReadAllText(M365Run.Golden("fixtures", "hook-upload-clean.json")))!;
        hook["tool_input"]!["body"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(new string('x', bytes)));

        // Act
        var decision = await EvaluateAsync(JsonDocument.Parse(hook.ToJsonString()).RootElement);

        // Assert
        decision.Should().Be(passes ? GuardDecision.Pass : new GuardDecision.Deny("content over 262144 bytes"));
    }

    [Fact]
    public async Task Evaluate_Send_NoGraphRead()
    {
        // Act
        await EvaluateAsync(Hook("send-clean"));

        // Assert
        _graph.Stub.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""["send","move"]""", "upload-clean")]
    [InlineData("[]", "send-clean")]
    [InlineData("[]", "move-archive")]
    public async Task Evaluate_ActionDisabled_Deny(string enabled, string fixture)
    {
        // Arrange
        using var narrowed = new GraphFixture($$$"""{"actions":{"enabled":{{{enabled}}}}}""");
        var policy = new GuardPolicy(narrowed.Configuration, narrowed.Reader);
        var hook = Hook(fixture);

        // Act
        var decision = await policy.EvaluateAsync(GuardPolicy.ActionOf(hook.GetProperty("tool_name").GetString()!)!.Value, hook.GetProperty("tool_input"), CancellationToken.None);

        // Assert
        decision.Should().Be(new GuardDecision.Deny("action disabled for this instance"));
    }

    [Fact]
    public void ActionOf_NonActionTool_Null()
    {
        // Assert
        GuardPolicy.ActionOf(Hook("other-tool").GetProperty("tool_name").GetString()!).Should().BeNull();
        GuardPolicy.ActionOf("mcp__m365__send-shared-mailbox-mail").Should().Be(GuardAction.Send);
        GuardPolicy.ActionOf("mcp__m365__upload-file-content").Should().Be(GuardAction.Upload);
        GuardPolicy.ActionOf("mcp__m365__move-shared-mailbox-message").Should().Be(GuardAction.Move);
    }

    [Theory]
    [InlineData("upload-clean", "items/01PARENT0001\\?", "item-kind")]
    [InlineData("move-folder-id", "mailFolders\\?", "mail-folders")]
    public async Task Evaluate_ReaderFails_Fail(string fixture, string route, string read)
    {
        // Arrange
        _graph.Stub.Once("GET", route, 500, "{}");

        // Act
        var decision = await EvaluateAsync(Hook(fixture));

        // Assert
        decision.Should().Be(new GuardDecision.Fail($"graph read {read} failed"));
    }

    [Fact]
    public async Task Evaluate_ExactDuplicateKey_KeptOnceAsJq()
    {
        // Arrange: jq keeps one of two identical keys; only different case is a duplicate
        var json = File.ReadAllText(M365Run.Golden("fixtures", "hook-send-clean.json"))
            .Replace("\"userId\":\"alice@acme.example\"", "\"userId\":\"x@y.example\",\"userId\":\"alice@acme.example\"", StringComparison.Ordinal);

        // Act
        var decision = await EvaluateAsync(JsonDocument.Parse(json).RootElement);

        // Assert
        decision.Should().Be(GuardDecision.Pass);
    }

    [Fact]
    public void Decision_NeverAllowsOrAsks()
    {
        // Assert: the only outcomes are pass (the ask rule prompts), deny and fail
        typeof(GuardDecision).GetNestedTypes().Select(t => t.Name).Should().BeEquivalentTo("PassDecision", "Deny", "Fail");
    }

    private int GraphGets => _graph.Stub.Requests.Count(r => r.Method == HttpMethod.Get);

    private static JsonElement Hook(string name) => JsonDocument.Parse(File.ReadAllText(M365Run.Golden("fixtures", $"hook-{name}.json"))).RootElement;

    private Task<GuardDecision> EvaluateAsync(JsonElement hook)
    {
        var policy = new GuardPolicy(_graph.Configuration, _graph.Reader);
        return policy.EvaluateAsync(GuardPolicy.ActionOf(hook.GetProperty("tool_name").GetString()!)!.Value, hook.GetProperty("tool_input"), CancellationToken.None);
    }
}
