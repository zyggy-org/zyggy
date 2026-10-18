using System.Text.Json;

using Zyggy.Node;

namespace Zyggy.Integration.Node;

/// <summary>Spec 07 AC-11: the <c>/health</c> body is the §11 field list, camelCase, nulls written, timestamps in whole UTC seconds with <c>Z</c> (plan A3: lives here because the unit project references only Core).</summary>
public sealed class NodeHealthJsonTests
{
    private static readonly string[] Section11Fields =
    [
        "version", "tenant", "machine", "lastPollUtc", "lastEtag", "lastPullUtc", "busHeadSha", "jobsRunning", "jobsClaimedByMe",
        "lastReportUtc", "rateLimitRemaining", "backoffUntilUtc", "claudeAuthOk", "agentCoreSha",
    ];

    [Fact]
    public void Serialize_AllNull_WritesFourteenCamelCaseKeysWithNulls()
    {
        // Arrange
        var health = new NodeHealth("1.2.3", "acme", "home-laptop", null, null, null, null, 0, 0, null, null, null, null, null);

        // Act
        using JsonDocument document = JsonDocument.Parse(HealthServer.Serialize(health));

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
        using JsonDocument document = JsonDocument.Parse(HealthServer.Serialize(health));

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
        string json = HealthServer.Serialize(health);

        // Assert
        json.Should().Contain("\"jobsRunning\":2").And.Contain("\"jobsClaimedByMe\":3").And.Contain("\"rateLimitRemaining\":4999").And.Contain("\"claudeAuthOk\":false");
    }
}
