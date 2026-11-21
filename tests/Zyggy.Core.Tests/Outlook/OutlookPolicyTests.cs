using System.Text.Json;

using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

// AC-5: with no policy record (neither the copied work-boundary.json nor the node.json mirror), or outlook.enabled false,
// every tool refuses and touches nothing in Outlook; with the copied file present it is used, else the mirror.
public sealed class OutlookPolicyTests
{
    private const string WorkBoundary = """
        {
          "neverList": ["national register numbers", "patient personal information", "medical information"],
          "mail": {
            "policyConfirmed": {
              "date": "2026-11-21",
              "scope": ["read", "send", "move"],
              "statement": "placeholder statement"
            }
          }
        }
        """;

    private static readonly string[] AllScopes = ["read", "send", "move"];

    private const string WorkBoundaryWithoutMail = """{ "neverList": ["national register numbers"] }""";

    private static OutlookSettings Node(string json) => OutlookSettings.FromNodeJson(JsonDocument.Parse(json).RootElement.Clone());

    private static readonly OutlookSettings EnabledNoMirror = Node("""{ "enabled": true }""");

    private static readonly OutlookSettings EnabledWithReadMirror = Node("""{ "enabled": true, "policyConfirmed": { "date": "2026-11-20", "scope": ["read"] } }""");

    // Source and refusal as their enum names: the enums are internal, a public theory cannot take them as parameters.
    public static TheoryData<string, string, string> Sources() => new()
    {
        { "file only", nameof(OutlookPolicySource.WorkBoundaryFile), "allowed" },
        { "mirror only", nameof(OutlookPolicySource.NodeJsonMirror), "allowed" },
        { "file and mirror", nameof(OutlookPolicySource.WorkBoundaryFile), "allowed" },
        { "file without record, mirror", nameof(OutlookPolicySource.None), nameof(OutlookRefusal.PolicyNotConfirmed) },
        { "neither", nameof(OutlookPolicySource.None), nameof(OutlookRefusal.PolicyNotConfirmed) },
    };

    private static OutlookPolicyDecision Evaluate(string situation) => situation switch
    {
        "file only" => OutlookPolicy.Evaluate(WorkBoundary, EnabledNoMirror),
        "mirror only" => OutlookPolicy.Evaluate(null, EnabledWithReadMirror),
        "file and mirror" => OutlookPolicy.Evaluate(WorkBoundary, EnabledWithReadMirror),
        "file without record, mirror" => OutlookPolicy.Evaluate(WorkBoundaryWithoutMail, EnabledWithReadMirror),
        _ => OutlookPolicy.Evaluate(null, EnabledNoMirror),
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void Evaluate_RecordSources_TheCopiedFileWinsElseTheMirror(string situation, string expectedSource, string expectedRefusal)
    {
        // Act
        var decision = Evaluate(situation);

        // Assert
        (decision.Source.ToString(), decision.RefusalFor("read")?.ToString() ?? "allowed").Should().Be((expectedSource, expectedRefusal));
    }

    [Fact]
    public void Evaluate_WorkBoundaryRecord_ReadsDateAndScope()
    {
        // Act
        var decision = OutlookPolicy.Evaluate(WorkBoundary, EnabledNoMirror);

        // Assert
        decision.Record.Should().BeEquivalentTo(new { Date = new DateOnly(2026, 11, 21), Scope = AllScopes });
    }

    [Fact]
    public void Evaluate_EnabledFalse_RefusesNotEnabled()
    {
        // Act
        var decision = OutlookPolicy.Evaluate(WorkBoundary, Node("""{ "enabled": false }"""));

        // Assert
        decision.RefusalFor("read").Should().Be(OutlookRefusal.NotEnabled);
    }

    [Fact]
    public void Evaluate_NoOutlookSection_RefusesNotEnabled()
    {
        // Act
        var decision = OutlookPolicy.Evaluate(WorkBoundary, OutlookSettings.FromNodeJson(null));

        // Assert
        decision.RefusalFor("read").Should().Be(OutlookRefusal.NotEnabled);
    }

    [Fact]
    public void Evaluate_MirrorWithReadOnlyScope_RefusesSend()
    {
        // Act
        var decision = OutlookPolicy.Evaluate(null, EnabledWithReadMirror);

        // Assert
        decision.RefusalFor("send").Should().Be(OutlookRefusal.PolicyNotConfirmed);
    }

    [Theory]
    [InlineData("""{ "mail": { "policyConfirmed": { "date": "21/11/2026", "scope": ["read"] } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": { "date": "2026-02-30", "scope": ["read"] } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": { "scope": ["read"] } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": { "date": "2026-11-21" } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": "read" } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": ["read", "delete"] } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": [] } } }""")]
    [InlineData("""{ "mail": { "policyConfirmed": true } }""")]
    [InlineData("""{ "mail": "confirmed" }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Evaluate_MalformedRecordInTheFile_FailsClosed(string workBoundary)
    {
        // Act
        var decision = OutlookPolicy.Evaluate(workBoundary, EnabledWithReadMirror);

        // Assert
        decision.RefusalFor("read").Should().Be(OutlookRefusal.PolicyNotConfirmed);
    }

    [Theory]
    [InlineData("""{ "enabled": "yes" }""")]
    [InlineData("""{ "enabled": 1 }""")]
    [InlineData("""{ }""")]
    public void FromNodeJson_EnabledNotTrue_IsDisabled(string json)
    {
        // Act
        var settings = Node(json);

        // Assert
        settings.Enabled.Should().BeFalse();
    }

    [Fact]
    public void FromNodeJson_ReadCaps_AreRead()
    {
        // Act
        var settings = Node("""{ "enabled": true, "read": { "bodyMaxChars": 1200, "maxItems": 7 } }""");

        // Assert
        (settings.ReadBodyMaxChars, settings.ReadMaxItems).Should().Be((1200, 7));
    }

    [Theory]
    [InlineData("""{ "read": { "bodyMaxChars": 0, "maxItems": -1 } }""")]
    [InlineData("""{ "read": { "bodyMaxChars": "many", "maxItems": 2.5 } }""")]
    [InlineData("""{ "read": 3 }""")]
    public void FromNodeJson_InvalidReadCaps_FallBackToTheDefaults(string json)
    {
        // Act
        var settings = Node(json);

        // Assert
        (settings.ReadBodyMaxChars, settings.ReadMaxItems).Should().Be((OutlookSettings.DefaultReadBodyMaxChars, OutlookSettings.DefaultReadMaxItems));
    }
}
