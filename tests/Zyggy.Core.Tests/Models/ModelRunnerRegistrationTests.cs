using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Zyggy.Core.Models;
using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Models;

public sealed class ModelRunnerRegistrationTests
{
    [Fact]
    public void AddClaudeCodeModelRunner_Resolves_IModelRunner()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IProcessRunner>());

        // Act
        services.AddClaudeCodeModelRunner(o => o.Path = "/opt/claude/claude");
        using var provider = services.BuildServiceProvider();

        // Assert
        provider.GetRequiredService<IModelRunner>().Should().BeOfType<ClaudeCodeCliRunner>();
    }
}
