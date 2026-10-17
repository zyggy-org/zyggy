using System.Text.Json;

using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Models;

/// <summary>
/// Spec 06 AC-26: <c>tools/fake-claude/scenarios/done.jsonl</c> has the real <c>stream-json</c> shape. Red until the owner's capture
/// (AC-27) replaces the placeholder — never turned green by a hand-written file (plan 06 "the one sanctioned red test").
/// </summary>
public sealed class FakeClaudeDoneScenarioTests
{
    [Fact]
    public void DoneScenario_IsRealStreamJsonWithoutPlaceholder()
    {
        // Arrange
        var path = Path.Combine(RepoRoot.Find(), "tools", "fake-claude", "scenarios", "done.jsonl");
        var lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Act
        var reader = new StreamJsonReader(2 * 1024 * 1024);
        foreach (var line in lines)
        {
            reader.Accept(line);
        }

        // Assert
        lines.Should().NotContain(l => l.Contains("\"placeholder\"", StringComparison.Ordinal));

        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.GetProperty("type").GetString().Should().Be("system");
        first.RootElement.GetProperty("subtype").GetString().Should().Be("init");
        first.RootElement.GetProperty("model").ValueKind.Should().Be(JsonValueKind.String);

        lines.Should().Contain(l => AssistantTextContainsReport(l));

        using var last = JsonDocument.Parse(lines[^1]);
        var result = last.RootElement;
        result.GetProperty("type").GetString().Should().Be("result");
        result.GetProperty("subtype").GetString().Should().Be("success");
        result.GetProperty("is_error").GetBoolean().Should().BeFalse();
        result.TryGetProperty("total_cost_usd", out _).Should().BeTrue();
        result.TryGetProperty("duration_ms", out _).Should().BeTrue();
        result.TryGetProperty("num_turns", out _).Should().BeTrue();
        result.GetProperty("usage").TryGetProperty("input_tokens", out _).Should().BeTrue();
        result.GetProperty("usage").TryGetProperty("output_tokens", out _).Should().BeTrue();
        result.TryGetProperty("result", out _).Should().BeTrue();

        reader.SawResult.Should().BeTrue();
        reader.CostUsd.Should().NotBeNull();
        reader.NumTurns.Should().NotBeNull();
        reader.Model.Should().NotBeNull();
        reader.InputTokens.Should().NotBeNull();
        reader.OutputTokens.Should().NotBeNull();
    }

    private static bool AssistantTextContainsReport(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "assistant"
            || !root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return content.EnumerateArray().Any(block =>
            block.TryGetProperty("type", out var type) && type.GetString() == "text"
            && block.TryGetProperty("text", out var text) && text.GetString()?.Contains("REPORT", StringComparison.Ordinal) == true);
    }
}
