using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Processes;

/// <summary>Spec 33 AC-3: a process can be started with exactly the variables given; the default (28's) is unchanged.</summary>
public sealed class ProcessSpecReplaceEnvironmentTests
{
    [Fact]
    public void StartInfo_Replace_ExactlyGivenVariables()
    {
        // Arrange
        var spec = new ProcessSpec("/bin/true", [], ".")
        {
            ReplaceEnvironment = true,
            Environment = new Dictionary<string, string?> { ["HOME"] = "/h", ["LC_ALL"] = "C", ["DROPPED"] = null },
        };

        // Act
        var info = ProcessRunner.StartInfo(spec);

        // Assert
        info.Environment.Keys.Order(StringComparer.Ordinal).Should().Equal("HOME", "LC_ALL");
        info.Environment["HOME"].Should().Be("/h");
    }

    [Fact]
    public void StartInfo_Default_InheritsPlusAdditions()
    {
        // Arrange
        var inherited = Environment.GetEnvironmentVariables().Count;
        var spec = new ProcessSpec("/bin/true", [], ".") { Environment = new Dictionary<string, string?> { ["ZYGGY_EXTRA_TEST"] = "1" } };

        // Act
        var info = ProcessRunner.StartInfo(spec);

        // Assert
        info.Environment.Should().ContainKey("ZYGGY_EXTRA_TEST");
        info.Environment.Count.Should().BeGreaterThanOrEqualTo(inherited);
    }
}
