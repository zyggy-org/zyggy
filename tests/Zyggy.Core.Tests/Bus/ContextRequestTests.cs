using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 12 AC-5, AC-12 and A3/A4: the stated fact becomes one line, or the field and detail are named.</summary>
public sealed class ContextRequestTests
{
    private static readonly DateOnly Date = new(2026, 10, 28);

    private static ContextRequest Ok(string? scope, string? fact)
    {
        ContextRequest.TryCreateStated(scope, fact, Date, out var request).Should().BeNull();
        return request!;
    }

    private static ContextFactError Error(string? scope, string? fact)
    {
        var error = ContextRequest.TryCreateStated(scope, fact, Date, out var request);
        request.Should().BeNull();
        return error!;
    }

    [Fact]
    public void TryCreateStated_NoScope_GeneralAndLineWithoutHint()
    {
        // Act
        var request = Ok(null, "Marie prefers tea");

        // Assert
        request.Scope.Should().Be(new ContextScope(ContextScopeKind.General, null));
        request.Lines.Should().Equal("- [stated] 2026-10-28: Marie prefers tea");
    }

    [Fact]
    public void TryCreateStated_ScopeMachine_HintMachine()
    {
        // Act
        var request = Ok("machine", "The laptop sleeps at 23:00");

        // Assert
        request.Scope.Kind.Should().Be(ContextScopeKind.Machine);
        request.Lines.Should().Equal("- [stated] 2026-10-28 (machine): The laptop sleeps at 23:00");
    }

    [Fact]
    public void TryCreateStated_ScopeProject_HintProjectName()
    {
        // Act
        var request = Ok("project:calizr", "The staging database is restored from Sunday's snapshot");

        // Assert
        request.Scope.Should().Be(new ContextScope(ContextScopeKind.Project, "calizr"));
        request.Lines.Should().Equal("- [stated] 2026-10-28 (project:calizr): The staging database is restored from Sunday's snapshot");
    }

    [Theory]
    [InlineData("project:")]
    [InlineData("project:Has Space")]
    [InlineData("project:Calizr")]
    [InlineData("project:-x")]
    public void TryCreateStated_ScopeProjectInvalidLabel_ScopeInvalidProjectName(string scope)
    {
        // Act + Assert
        Error(scope, "fact").Should().Be(new ContextFactError("scope", "invalid project name in --scope"));
    }

    [Theory]
    [InlineData("people/marie")]
    [InlineData("Machine")]
    [InlineData("")]
    public void TryCreateStated_ScopeUnknownWord_ScopeMustBe(string scope)
    {
        // Act + Assert
        Error(scope, "fact").Should().Be(new ContextFactError("scope", "--scope must be general, project:<name> or machine"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void TryCreateStated_FactEmptyOrWhitespace_FactEmpty(string? fact)
    {
        // Act + Assert
        Error(null, fact).Should().Be(new ContextFactError("fact", "empty fact"));
    }

    [Fact]
    public void TryCreateStated_FactOver1000Chars_FactTooLong()
    {
        // Act + Assert
        Error(null, new string('a', 1001)).Should().Be(new ContextFactError("fact", "fact longer than 1000 characters"));
        Ok(null, new string('a', 1000)).Lines.Should().ContainSingle();
        Ok(null, string.Concat(Enumerable.Repeat("é", 1000))).Lines.Should().ContainSingle("Unicode scalars, not UTF-16 units");
    }

    [Fact]
    public void TryCreateStated_FactWithCrLfAndTabs_CollapsedToOneLine()
    {
        // Act
        var request = Ok(null, "a\r\n b\t\tc");

        // Assert
        request.Lines.Should().Equal("- [stated] 2026-10-28: a b c");
    }

    [Fact]
    public void Body_LinesJoinedWithLfAndTrailingLf_NoBom()
    {
        // Arrange
        var request = new ContextRequest(new ContextScope(ContextScopeKind.General, null), ["- one", "- two"]);

        // Act
        var body = request.Body();

        // Assert
        body.Should().Equal(Encoding.UTF8.GetBytes("- one\n- two\n"));
    }

    [Fact]
    public void CheckSender_Central_ToThisMachineIsCentral()
    {
        // Act + Assert
        ContextRequest.CheckSender(MachineName.Parse("central")).Should().Be(new ContextFactError("to", "this machine is central; use zyggy memory remember"));
    }

    [Fact]
    public void CheckSender_HomeLaptop_Null()
    {
        // Act + Assert
        ContextRequest.CheckSender(MachineName.Parse("home-laptop")).Should().BeNull();
    }

    [Fact]
    public void MachineName_Central_IsTheLabelCentral()
    {
        // Assert
        MachineName.Central.Value.Should().Be("central");
        MachineName.Central.Should().Be(MachineName.Parse("central"));
    }
}
