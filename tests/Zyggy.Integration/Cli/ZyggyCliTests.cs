using System.Reflection;

using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

public sealed class ZyggyCliTests
{
    private static readonly Dictionary<string, string?> NoEnv = [];

    [Fact]
    public async Task Version_Flag_PrintsInformationalVersionAndExitsZero()
    {
        // Arrange
        var assemblyPath = Path.Combine(Path.GetDirectoryName(ZyggyCli.ExecutablePath)!, "zyggy.dll");
        var expected = Assembly.LoadFrom(assemblyPath).GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // Act
        var run = await ZyggyCli.RunAsync(["--version"], NoEnv, null, AppContext.BaseDirectory, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(0);
        run.Stdout.Trim().Should().Be(expected);
    }

    [Fact]
    public async Task UnknownVerb_ExitsTwoWithOneUsageErrorOnStderr()
    {
        // Act
        var run = await ZyggyCli.RunAsync(["frobnicate"], NoEnv, null, AppContext.BaseDirectory, TestContext.Current.CancellationToken);

        // Assert
        run.ExitCode.Should().Be(2);
        run.Stderr.Should().Contain("frobnicate");
        run.Stdout.Should().BeEmpty();
    }
}
