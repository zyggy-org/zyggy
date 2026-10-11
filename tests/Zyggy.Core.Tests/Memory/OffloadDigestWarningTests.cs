using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-23: the warning line while anything needs attention — parked items, or a run that failed as a whole (A12).</summary>
public sealed class OffloadDigestWarningTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    [Fact]
    public void Line_NothingParkedLastRunOk_Null()
    {
        // Arrange
        var state = new OffloadState();
        state.RecordTransient("zyggy/a", OffloadReason.TransportError, 3, Day);
        state.RecordRun(DateTimeOffset.UnixEpoch, false, null);

        // Act / Assert
        OffloadDigestWarning.Line(state).Should().BeNull();
    }

    [Fact]
    public void Line_TwoParked_TwoItems()
    {
        // Arrange
        var state = new OffloadState();
        state.Park("zyggy/a", OffloadReason.TargetConflict, Day);
        state.Park("zyggy/b", OffloadReason.VerifyFailed, Day);

        // Act / Assert
        OffloadDigestWarning.Line(state).Should().StartWith("[warning] archive offload: 2 item(s) need attention");
    }

    [Fact]
    public void Line_LastRunFailedNoneParked_OneItem()
    {
        // Arrange
        var state = new OffloadState();
        state.RecordRun(DateTimeOffset.UnixEpoch, true, OffloadReason.RootMissing);

        // Act / Assert
        OffloadDigestWarning.Line(state).Should().StartWith("[warning] archive offload: 1 item(s) need attention");
    }

    [Fact]
    public void Line_Exact_Text()
    {
        // Arrange
        var state = new OffloadState();
        state.Park("zyggy/a", OffloadReason.TargetConflict, Day);

        // Act / Assert
        OffloadDigestWarning.Line(state).Should().Be(
            "[warning] archive offload: 1 item(s) need attention — zyggy memory archive list; runbook \"Offload upload failed\"");
    }

    [Fact]
    public void Line_Environment_MissingOrBrokenStateFile_Null()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string?> { ["HOME"] = root, ["ZYGGY_STATE_DIR"] = root };
        var missing = OffloadDigestWarning.Line(environment);
        Directory.CreateDirectory(Path.Combine(root, "archive"));
        File.WriteAllText(Path.Combine(root, "archive", "offload-state.json"), "{broken");

        // Act
        var broken = OffloadDigestWarning.Line(environment);

        // Assert
        missing.Should().BeNull();
        broken.Should().BeNull("the digest never fails on the state file");
        Directory.Delete(root, recursive: true);
    }
}
