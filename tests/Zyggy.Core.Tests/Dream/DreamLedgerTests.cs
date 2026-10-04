using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamLedgerTests
{
    private static readonly DateOnly Day = new(2026, 10, 4);

    [Fact]
    public void Serialize_ThenLoad_RoundTrips()
    {
        // Arrange
        var ledger = DreamLedger.Empty();
        ledger.Consume("inbox/remember-2026-10-03.md", ["bbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaa"], Day);
        ledger.Consume("daily/2026-10-03.md", ["cccccccccccccccc"], Day.AddDays(-1));

        // Act
        var json = ledger.Serialize();
        var back = DreamLedger.Load(json);

        // Assert
        back.Status.Should().Be(DreamLedgerLoadStatus.Loaded);
        back.Ledger!.Serialize().Should().Be(json);
        back.Ledger.IsConsumed("inbox/remember-2026-10-03.md", "aaaaaaaaaaaaaaaa").Should().BeTrue();
        back.Ledger.IsConsumed("inbox/remember-2026-10-03.md", "cccccccccccccccc").Should().BeFalse();
        json.Should().Be(
            "{\n  \"schema\": 1,\n  \"files\": {\n" +
            "    \"daily/2026-10-03.md\": {\n      \"consumed\": [\n        \"cccccccccccccccc\"\n      ],\n      \"lastConsumed\": \"2026-10-03\"\n    },\n" +
            "    \"inbox/remember-2026-10-03.md\": {\n      \"consumed\": [\n        \"aaaaaaaaaaaaaaaa\",\n        \"bbbbbbbbbbbbbbbb\"\n      ],\n      \"lastConsumed\": \"2026-10-04\"\n    }\n" +
            "  }\n}\n");
    }

    [Fact]
    public void Load_SchemaTwo_ReturnsUnsupported()
    {
        // Act
        var result = DreamLedger.Load("{\"schema\": 2, \"files\": {}}");

        // Assert
        result.Status.Should().Be(DreamLedgerLoadStatus.Unsupported);
        result.Ledger.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema\": 1, \"files\": []}")]
    public void Load_Malformed_ReturnsInvalid(string json)
    {
        // Act
        var result = DreamLedger.Load(json);

        // Assert
        result.Status.Should().Be(DreamLedgerLoadStatus.Invalid);
    }

    [Fact]
    public void Load_Null_ReturnsEmptyLedger()
    {
        // Act
        var result = DreamLedger.Load(null);

        // Assert
        result.Status.Should().Be(DreamLedgerLoadStatus.Loaded);
        result.Ledger!.Files.Should().BeEmpty();
    }

    [Fact]
    public void Consume_SetsLastConsumed()
    {
        // Arrange
        var ledger = DreamLedger.Empty();

        // Act
        ledger.Consume("inbox/a.md", ["aaaaaaaaaaaaaaaa"], Day);
        ledger.Consume("inbox/a.md", ["bbbbbbbbbbbbbbbb"], Day.AddDays(2));

        // Assert
        ledger.LastConsumed("inbox/a.md").Should().Be(Day.AddDays(2));
        ledger.AllConsumed("inbox/a.md", ["aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb"]).Should().BeTrue();
        ledger.AllConsumed("inbox/a.md", ["aaaaaaaaaaaaaaaa", "cccccccccccccccc"]).Should().BeFalse();
        ledger.LastConsumed("inbox/other.md").Should().BeNull();
    }

    [Fact]
    public void Remove_DropsFileEntry()
    {
        // Arrange
        var ledger = DreamLedger.Empty();
        ledger.Consume("inbox/a.md", ["aaaaaaaaaaaaaaaa"], Day);

        // Act
        ledger.Remove("inbox/a.md");

        // Assert
        ledger.Files.Should().BeEmpty();
        ledger.IsConsumed("inbox/a.md", "aaaaaaaaaaaaaaaa").Should().BeFalse();
    }
}
