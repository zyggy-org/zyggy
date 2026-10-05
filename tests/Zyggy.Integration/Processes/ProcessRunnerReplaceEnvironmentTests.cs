using Zyggy.Core.Processes;

namespace Zyggy.Integration.Processes;

/// <summary>Spec 33 AC-3: the real process runner starts a child with exactly the given variables when asked, and inherits otherwise.</summary>
public sealed class ProcessRunnerReplaceEnvironmentTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(SkipUnless = nameof(IsLinux), Skip = "/usr/bin/env as the probe child: Linux only")]
    public async Task RunAsync_OnLinux_ReplaceEnvironment_ChildSeesExactlyGiven()
    {
        // Arrange
        var spec = new ProcessSpec("/usr/bin/env", [], "/")
        {
            ReplaceEnvironment = true,
            Environment = new Dictionary<string, string?> { ["LC_ALL"] = "C", ["ZYGGY_ONLY"] = "1" },
            Timeout = TimeSpan.FromSeconds(10),
        };

        // Act
        var result = await new ProcessRunner(TimeProvider.System).RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.ExitCode.Should().Be(0);
        result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).Should().Equal("LC_ALL=C", "ZYGGY_ONLY=1");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "/usr/bin/env as the probe child: Linux only")]
    public async Task RunAsync_Default_ChildInheritsParent()
    {
        // Arrange
        var spec = new ProcessSpec("/usr/bin/env", [], "/") { Timeout = TimeSpan.FromSeconds(10) };

        // Act
        var result = await new ProcessRunner(TimeProvider.System).RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        result.Stdout.Should().Contain("PATH=");
    }
}
