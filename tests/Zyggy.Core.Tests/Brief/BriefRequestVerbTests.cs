using System.Runtime.Versioning;

namespace Zyggy.Core.Tests.Brief;

/// <summary><c>zyggy brief request</c>: the request file the path unit watches, its mode, the printed line, usage.</summary>
public sealed class BriefRequestVerbTests : IDisposable
{
    private readonly BriefFixture _f = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Run_WritesRequestFileBesideTheBriefDirectoryPrintsExitZero()
    {
        // Act
        var (exit, console) = await _f.RunBriefAsync("request");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("brief requested\n");
        console.Stderr.Should().BeEmpty();
        _f.Paths.Request.Should().Be(Path.Join(Path.Combine(_f.Root, "state"), "brief.request"));
        File.Exists(_f.Paths.Request).Should().BeTrue();
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Run_OnLinux_RequestFileIsOwnerOnly()
    {
        Assert.SkipUnless(IsLinux, "Unix file modes");

        // Act
        await _f.RunBriefAsync("request");

        // Assert
        File.GetUnixFileMode(_f.Paths.Request).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task Run_Twice_OneFileExitZero()
    {
        // Act
        await _f.RunBriefAsync("request");
        var (exit, _) = await _f.RunBriefAsync("request");

        // Assert
        exit.Should().Be(0);
        File.Exists(_f.Paths.Request).Should().BeTrue();
    }

    [Fact]
    public async Task Run_WithArgument_ExitFourNoFile()
    {
        // Act
        var (exit, console) = await _f.RunBriefAsync("request", "now");

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().Be("brief: request takes no argument (usage: zyggy brief request)\n");
        File.Exists(_f.Paths.Request).Should().BeFalse();
    }

    [Fact]
    public async Task Run_StateRootMissing_CreatedExitZero()
    {
        // Arrange
        Directory.Delete(Path.Combine(_f.Root, "state"), recursive: true);

        // Act
        var (exit, console) = await _f.RunBriefAsync("request");

        // Assert
        exit.Should().Be(0, console.Stderr);
        File.Exists(_f.Paths.Request).Should().BeTrue();
    }
}
