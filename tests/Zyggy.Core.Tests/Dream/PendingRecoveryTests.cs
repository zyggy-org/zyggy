using System.Text.Json.Nodes;

using Zyggy.Core.Dream;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-26: a run killed between write and commit is undone by the next run, unless someone edited a path since.</summary>
public sealed class PendingRecoveryTests : IDisposable
{
    private readonly MemoryTree _tree = new(
        ("private/people/carol.md", "- [stated] 2026-09-28: Carol.\n- [stated] 2026-09-29: written by the dead run.\n"),
        ("business/clients/acme.md", "- [observed] 2026-09-29 [m365-mail 2026-09-29]: created by the dead run.\n"));

    public void Dispose() => _tree.Dispose();

    private string Hash(string relative) => MemorySnapshot.Hash(File.ReadAllBytes(_tree.Full(relative)));

    private void Marker()
    {
        var marker = new JsonObject
        {
            ["run"] = "01JDEAD",
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = "private/people/carol.md", ["sha256_written"] = Hash("private/people/carol.md"), ["existed_before"] = true, ["sha256_before"] = new string('a', 64) },
                new JsonObject { ["path"] = "business/clients/acme.md", ["sha256_written"] = Hash("business/clients/acme.md"), ["existed_before"] = false, ["sha256_before"] = null },
                new JsonObject { ["path"] = "inbox/old.md", ["sha256_written"] = null, ["existed_before"] = true, ["sha256_before"] = new string('b', 64) }),
        };
        _tree.Write(".dream/pending.json", marker.ToJsonString());
    }

    [Fact]
    public void Recover_PathsStillHoldWrittenContent_RestoredToBeforeAndMarkerRemoved()
    {
        // Arrange
        Marker();

        // Act
        var result = PendingRecovery.Plan(_tree.Paths)!;

        // Assert
        result.Dirty.Should().BeNull();
        result.Run.Should().Be("01JDEAD");
        result.Restore.Should().Equal("private/people/carol.md");
        result.Delete.Should().Equal("business/clients/acme.md");
    }

    [Fact]
    public void Recover_PathEditedSince_DirtyPendingNothingTouched()
    {
        // Arrange
        Marker();
        File.AppendAllText(_tree.Full("private/people/carol.md"), "- [stated] 2026-10-04: edited by hand after the crash.\n");

        // Act
        var result = PendingRecovery.Plan(_tree.Paths)!;

        // Assert
        result.Dirty.Should().Be("private/people/carol.md");
        File.Exists(_tree.Full("business/clients/acme.md")).Should().BeTrue();
        File.Exists(_tree.Paths.Pending).Should().BeTrue();
    }

    [Fact]
    public void Recover_NoMarker_NoOp()
    {
        // Act
        var result = PendingRecovery.Plan(_tree.Paths);

        // Assert
        result.Should().BeNull();
    }
}
