using Zyggy.Core.Setup;

namespace Zyggy.Core.Tests.Setup;

/// <summary>
/// Spec 58 D-Q7 (slice 2): <c>zyggy setup --instance-path</c> copies the instance's <c>work-boundary.json</c> to the laptop's node
/// directory, where the outlook tools read the policy record; without the option nothing is printed or copied; a missing source is
/// left as is; an identical copy is already done.
/// </summary>
public sealed class WorkBoundaryCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public WorkBoundaryCopyTests() => Directory.CreateDirectory(_root);

    private string Instance => Path.Combine(_root, "instance");

    private string NodeDir => Path.Combine(_root, "node");

    private string Source => Path.Combine(Instance, "work-boundary.json");

    private string Target => Path.Combine(NodeDir, "work-boundary.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Apply_NoInstancePath_NoLineNothingCopied()
    {
        // Act
        SetupLine? line = WorkBoundaryCopy.Apply(null, NodeDir);

        // Assert
        (line, Directory.Exists(NodeDir)).Should().Be((null, false));
    }

    [Fact]
    public void Apply_SourceMissing_LeftAsIs()
    {
        // Arrange
        Directory.CreateDirectory(Instance);

        // Act
        SetupLine? line = WorkBoundaryCopy.Apply(Instance, NodeDir);

        // Assert
        (line!.Render(null), File.Exists(Target)).Should().Be(("setup: work-boundary: left as is - no work-boundary.json in " + Instance, false));
    }

    [Fact]
    public void Apply_SourcePresent_CopiedToTheNodeDirectory()
    {
        // Arrange
        Directory.CreateDirectory(Instance);
        File.WriteAllText(Source, "{ \"mail\": { \"policyConfirmed\": { \"date\": \"2026-11-21\", \"scope\": [\"read\"] } } }");

        // Act
        SetupLine? line = WorkBoundaryCopy.Apply(Instance, NodeDir);

        // Assert
        (line!.Render(null), File.ReadAllText(Target)).Should().Be(("setup: work-boundary: done - copied from " + Source + " to " + Target, File.ReadAllText(Source)));
    }

    [Fact]
    public void Apply_IdenticalCopy_AlreadyDone()
    {
        // Arrange
        Directory.CreateDirectory(Instance);
        File.WriteAllText(Source, "{ }");
        WorkBoundaryCopy.Apply(Instance, NodeDir);
        DateTime written = File.GetLastWriteTimeUtc(Target);

        // Act
        SetupLine? line = WorkBoundaryCopy.Apply(Instance, NodeDir);

        // Assert
        (line!.Render(null), File.GetLastWriteTimeUtc(Target)).Should().Be(("setup: work-boundary: already done", written));
    }

    [Fact]
    public void Apply_ChangedSource_CopiedAgain()
    {
        // Arrange
        Directory.CreateDirectory(Instance);
        File.WriteAllText(Source, "{ \"v\": 1 }");
        WorkBoundaryCopy.Apply(Instance, NodeDir);
        File.WriteAllText(Source, "{ \"v\": 2 }");

        // Act
        SetupLine? line = WorkBoundaryCopy.Apply(Instance, NodeDir);

        // Assert
        (line!.Outcome, File.ReadAllText(Target)).Should().Be((SetupOutcome.Done, "{ \"v\": 2 }"));
    }
}
