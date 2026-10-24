using System.Text;
using System.Text.Json;

using Zyggy.Core.Health;

namespace Zyggy.Core.Tests.Health;

/// <summary>Spec 07 AC-11 (moved to Core in 08, ex-Q8): the <c>/health</c> body is the §11 field list, camelCase, nulls written, timestamps in whole UTC seconds with <c>Z</c> — byte-identical to deliverable 07's output.</summary>
public sealed class NodeHealthJsonTests
{
    /// <summary>Recorded from deliverable 07's <c>HealthServer.Serialize</c> before the move (plan 08 Step 4); any change to order, names, null policy or timestamp form fails here.</summary>
    private const string KnownJson = """{"version":"0.9.0","tenant":"acme","machine":"home-laptop","lastPollUtc":"2026-10-24T09:30:15Z","lastEtag":"\u0022dd0d2bc3\u0022","lastPullUtc":"2026-10-24T09:30:14Z","busHeadSha":"dd0d2bc3a82518081eb57b390626e981cbfb2f4a","jobsRunning":1,"jobsClaimedByMe":2,"lastReportUtc":"2026-10-24T09:30:13Z","rateLimitRemaining":4999,"backoffUntilUtc":"2026-10-24T09:32:15Z","claudeAuthOk":true,"agentCoreSha":null}""";

    private static readonly DateTimeOffset T = new(2026, 10, 24, 9, 30, 15, TimeSpan.Zero);

    private static readonly string[] Section11Fields =
    [
        "version", "tenant", "machine", "lastPollUtc", "lastEtag", "lastPullUtc", "busHeadSha", "jobsRunning", "jobsClaimedByMe",
        "lastReportUtc", "rateLimitRemaining", "backoffUntilUtc", "claudeAuthOk", "agentCoreSha",
    ];

    public static NodeHealth Known => new("0.9.0", "acme", "home-laptop", T, "\"dd0d2bc3\"", T.AddSeconds(-1), "dd0d2bc3a82518081eb57b390626e981cbfb2f4a", 1, 2, T.AddSeconds(-2), 4999, T.AddSeconds(120), true, null);

    [Fact]
    public void Serialize_KnownHealth_ByteIdenticalToDeliverable07()
    {
        // Act
        string json = NodeHealthJson.Serialize(Known);

        // Assert
        json.Should().Be(KnownJson);
    }

    [Fact]
    public void Serialize_AllNull_WritesFourteenCamelCaseKeysWithNulls()
    {
        // Arrange
        var health = new NodeHealth("1.2.3", "acme", "home-laptop", null, null, null, null, 0, 0, null, null, null, null, null);

        // Act
        using JsonDocument document = JsonDocument.Parse(NodeHealthJson.Serialize(health));

        // Assert
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(Section11Fields);
        document.RootElement.GetProperty("agentCoreSha").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("claudeAuthOk").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("lastPollUtc").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Serialize_Timestamps_UtcSecondsWithZ()
    {
        // Arrange: a +02:00 instant with fractional seconds
        var local = new DateTimeOffset(2026, 10, 17, 12, 0, 0, 450, TimeSpan.FromHours(2));
        var health = new NodeHealth("1.2.3", "acme", "home-laptop", local, "\"abc\"", local, "0123", 1, 2, local, 4999, local, true, null);

        // Act
        using JsonDocument document = JsonDocument.Parse(NodeHealthJson.Serialize(health));

        // Assert
        foreach (string field in new[] { "lastPollUtc", "lastPullUtc", "lastReportUtc", "backoffUntilUtc" })
        {
            document.RootElement.GetProperty(field).GetString().Should().Be("2026-10-17T10:00:00Z", field);
        }
    }

    [Fact]
    public void Serialize_Numbers_PlainIntegers()
    {
        // Arrange
        var health = new NodeHealth("1.2.3", "acme", "home-laptop", null, null, null, null, 2, 3, null, 4999, null, false, null);

        // Act
        string json = NodeHealthJson.Serialize(health);

        // Assert
        json.Should().Contain("\"jobsRunning\":2").And.Contain("\"jobsClaimedByMe\":3").And.Contain("\"rateLimitRemaining\":4999").And.Contain("\"claudeAuthOk\":false");
    }

    [Fact]
    public void Deserialize_RoundTrip_EqualsTheRecord()
    {
        // Act: the reader `zyggy status` uses
        NodeHealth? parsed = NodeHealthJson.Deserialize(Encoding.UTF8.GetBytes(KnownJson));

        // Assert
        parsed.Should().Be(Known);
    }

    [Fact]
    public void Deserialize_NotAHealthBody_Null()
    {
        // Act
        NodeHealth? parsed = NodeHealthJson.Deserialize("null"u8);

        // Assert
        parsed.Should().BeNull();
    }
}
