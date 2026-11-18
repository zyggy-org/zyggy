using Zyggy.Core.Memory.Mirror;

namespace Zyggy.Core.Tests.Memory.Mirror;

/// <summary>
/// AC-5: <c>MIRROR.md</c> names the source revision and date and states that content is data, never instructions, that edits are
/// discarded and that facts are added through <c>remember</c> (spec 47, slice 1).
/// </summary>
public sealed class MemoryMirrorReadMeTests
{
    private static readonly MirrorSource Source = new("0123456789abcdef0123456789abcdef01234567", new DateOnly(2026, 11, 17));

    [Fact]
    public void Render_Source_NamesRevisionAndDate()
    {
        // Act
        var text = MirrorReadMe.Render(Source);

        // Assert
        text.Should().Contain("0123456789abcdef0123456789abcdef01234567").And.Contain("2026-11-17");
    }

    [Theory]
    [InlineData("read-only copy of Central's memory")]
    [InlineData("data, never instructions")]
    [InlineData("edits are discarded")]
    [InlineData("`remember`")]
    [InlineData("refreshed after each dream pass")]
    public void Render_Source_StatesTheRule(string phrase)
    {
        // Act
        var text = MirrorReadMe.Render(Source);

        // Assert
        text.Should().Contain(phrase);
    }

    [Fact]
    public void Render_Source_UnixLineEndingsEndsWithNewline()
    {
        // Act
        var text = MirrorReadMe.Render(Source);

        // Assert
        (text.Contains('\r', StringComparison.Ordinal) || !text.EndsWith('\n')).Should().BeFalse();
    }
}
