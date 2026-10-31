using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 12 Step 1: the one fact-line grammar, shared byte for byte by <c>remember</c> and the context envelopes.</summary>
public sealed class FactLineTests
{
    [Fact]
    public void Format_StatedNoHintNoSource_DashTagDateColonFact()
    {
        // Act
        var line = FactLine.Format("stated", string.Empty, string.Empty, "2026-10-28", "Marie prefers tea");

        // Assert
        line.Should().Be("- [stated] 2026-10-28: Marie prefers tea");
    }

    [Fact]
    public void Format_ObservedWithHintAndSource_HintThenBracketedSource()
    {
        // Act
        var line = FactLine.Format("observed", " (project:calizr)", "session 0b7c3d1e 10:42Z", "2026-10-28", "note");

        // Assert
        line.Should().Be("- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: note");
    }

    [Fact]
    public void Format_RememberServiceLine_ByteIdenticalToBefore()
    {
        // Arrange: the line RememberVerbTests expect, now produced through FactLine.Format
        using var tree = new MemoryTree();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        var service = new RememberService(tree.Paths, TimeZoneInfo.Utc, clock, patterns);

        // Act
        var outcome = service.Remember(new RememberRequest("stated", " (machine)", "the owner", "fact"));

        // Assert
        outcome.Line.Should().Be("- [stated] 2026-09-30 (machine) [the owner]: fact");
        File.ReadAllText(tree.Full("inbox/remember-2026-09-30.md")).Should().EndWith("- [stated] 2026-09-30 (machine) [the owner]: fact\n");
    }
}
