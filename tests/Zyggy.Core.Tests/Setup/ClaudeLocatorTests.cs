using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>Spec 42 AC-7 and Edge Cases: <c>claude.exe</c> on the process <c>PATH</c>, then in <c>&lt;UserProfile&gt;/.local/bin/</c>; never <c>claude.cmd</c>.</summary>
public sealed class ClaudeLocatorTests : IDisposable
{
    private readonly SetupTestFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public void Find_OnPath()
    {
        // Arrange
        string exe = _files.FakeClaudeExe();
        _files.FakeClaudeInLocalBin();

        // Act
        string? found = ClaudeLocator.Find(_files.Environment(), _files.UserProfile);

        // Assert
        found.Should().Be(SetupTestFiles.Slashes(Path.GetFullPath(exe)));
    }

    [Fact]
    public void Find_UserProfileLocalBin()
    {
        // Arrange
        string exe = _files.FakeClaudeInLocalBin();

        // Act
        string? found = ClaudeLocator.Find(_files.Environment(), _files.UserProfile);

        // Assert
        found.Should().Be(SetupTestFiles.Slashes(Path.GetFullPath(exe)));
    }

    [Fact]
    public void Find_None_Null()
    {
        // Act
        string? found = ClaudeLocator.Find(_files.Environment(), _files.UserProfile);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void Find_ClaudeCmdOnly_Null()
    {
        // Arrange
        _files.FakeClaudeExe("claude.cmd");

        // Act
        string? found = ClaudeLocator.Find(_files.Environment(), _files.UserProfile);

        // Assert
        found.Should().BeNull();
    }
}
