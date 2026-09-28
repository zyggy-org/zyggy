using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Envelope;

public sealed class ContextScopeTests
{
    [Fact]
    public void TryParse_ProjectScope_ReturnsKindAndName()
    {
        // Act
        bool ok = ContextScope.TryParse("project:calizr", out ContextScope? scope);

        // Assert
        ok.Should().BeTrue();
        scope.Should().Be(new ContextScope(ContextScopeKind.Project, "calizr"));
    }

    [Theory]
    [InlineData("machine", ContextScopeKind.Machine)]
    [InlineData("general", ContextScopeKind.General)]
    public void TryParse_MachineAndGeneral_ReturnsKindWithoutName(string value, ContextScopeKind kind)
    {
        // Act
        bool ok = ContextScope.TryParse(value, out ContextScope? scope);

        // Assert
        ok.Should().BeTrue();
        scope!.Kind.Should().Be(kind);
        scope.ProjectName.Should().BeNull();
    }

    [Theory]
    [InlineData("project:")]
    [InlineData("project:a b")]
    [InlineData("project:a:b")]
    [InlineData("Project:calizr")]
    [InlineData("other")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_Invalid_ReturnsFalse(string? value)
    {
        // Act
        bool ok = ContextScope.TryParse(value, out ContextScope? scope);

        // Assert
        ok.Should().BeFalse();
        scope.Should().BeNull();
    }

    [Theory]
    [InlineData("project:calizr")]
    [InlineData("machine")]
    [InlineData("general")]
    public void ToString_RoundTrips(string value)
    {
        // Act
        string text = ContextScope.Parse(value).ToString();

        // Assert
        text.Should().Be(value);
    }
}
