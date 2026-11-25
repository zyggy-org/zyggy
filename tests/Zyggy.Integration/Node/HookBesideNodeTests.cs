using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 61 slices 7-9 in CI: a real <c>zyggy-node</c> started out of process uses the <c>zyggy</c> beside itself as its job hook (spec 15),
/// as the release folder ships them. In this test output that <c>zyggy</c> must start and pass the hook's self-test on every OS; on Linux
/// the shared framework of the node had dropped the CLI's package assemblies from this folder, so every out-of-process job failed with
/// "hook unavailable" before its model started (the NodeStop tests timed out waiting for the model-process record).
/// </summary>
public sealed class HookBesideNodeTests
{
    // AC-24/AC-26 precondition: the hook beside the node refuses an empty call (exit 2), so the node's jobs reach their model
    [Fact]
    public async Task HookRefusesAnEmptyCall_ZyggyBesideTheNode_True()
    {
        // Arrange
        string hook = Path.Join(Path.GetDirectoryName(ZyggyNode.ExecutablePath), OperatingSystem.IsWindows() ? "zyggy.exe" : "zyggy");

        // Act
        bool refused = await JobRunner.HookRefusesAnEmptyCallAsync(new ProcessRunner(TimeProvider.System), hook, TestContext.Current.CancellationToken);

        // Assert
        refused.Should().BeTrue();
    }
}
