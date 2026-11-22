using Zyggy.Core.Outlook;

using static Zyggy.Core.Tests.Outlook.OutlookTestData;

namespace Zyggy.Core.Tests.Outlook;

// AC-5: without a policy record, or with outlook.enabled false, every read tool refuses and touches nothing in Outlook.
public sealed class OutlookReaderRefusalTests
{
    private static readonly string[] AllTools = ["list", "read", "count"];

    public static TheoryData<string> Tools() => [.. AllTools];

    public static TheoryData<string, string> RefusedPolicies() => new()
    {
        { "no record", "policy not confirmed" },
        { "disabled", "outlook not enabled" },
        { "scope without read", "policy not confirmed" },
    };

    private static OutlookPolicyDecision Policy(string name) => name switch
    {
        "no record" => new(null, OutlookPolicySource.None, Enabled: true),
        "disabled" => new(ReadSendMove, OutlookPolicySource.WorkBoundaryFile, Enabled: false),
        _ => new(new OutlookPolicyRecord(new DateOnly(2026, 11, 21), new HashSet<string>(["send"], StringComparer.Ordinal)), OutlookPolicySource.WorkBoundaryFile, Enabled: true),
    };

    private static string? Run(OutlookReader reader, string tool) => tool switch
    {
        "list" => reader.List("Inbox", Since(2026, 11, 1)).Message,
        "read" => reader.Read("m1").Message,
        _ => reader.Count([]).Message,
    };

    private static FakeOutlookAccess Mail() =>
        new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("m1", Local(2026, 11, 21, 9)), body: "body")
            .AddFolder(FakeOutlookAccess.DefaultStore, "Drafts");

    [Theory]
    [MemberData(nameof(RefusedPolicies))]
    public void EveryTool_RefusedPolicy_ReturnsTheRefusal(string policy, string expected)
    {
        // Arrange
        var access = Mail();

        // Act
        var messages = AllTools.Select(tool => Run(Reader(access, Policy(policy)), tool));

        // Assert
        messages.Should().Equal(expected, expected, expected);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Tool_NoPolicyRecord_TouchesNothingInOutlook(string tool)
    {
        // Arrange
        var access = Mail();

        // Act
        Run(Reader(access, Policy("no record")), tool);

        // Assert
        access.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Tool_Disabled_TouchesNothingInOutlook(string tool)
    {
        // Arrange
        var access = Mail();

        // Act
        Run(Reader(access, Policy("disabled")), tool);

        // Assert
        access.Calls.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void Tool_PolicyAllowsRead_Answers(string tool)
    {
        // Arrange
        var access = Mail();

        // Act
        var message = Run(Reader(access), tool);

        // Assert
        message.Should().BeNull();
    }
}
