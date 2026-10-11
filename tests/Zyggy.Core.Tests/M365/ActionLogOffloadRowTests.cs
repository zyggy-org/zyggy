using Zyggy.Core.M365.Guard;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 38 AC-15: one body-free row per offload write in 33's action log, byte for byte the hand-derived golden.</summary>
public sealed class ActionLogOffloadRowTests
{
    private static readonly DateTimeOffset RunStart = new(2026, 10, 1, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    public void BuildOffloadRow_Rows_EqualGolden()
    {
        // Arrange
        var golden = File.ReadAllText(Path.Combine(Golden.Directory, "archive", "offload", "actions-rows.jsonl"));

        // Act
        var rows = new[]
        {
            ActionLog.BuildOffloadRow(RunStart.AddSeconds(1), RunStart, "archive-offload-folder", "b!onedrive0001", "01ROOTARCH", "zyggy", 0, null),
            ActionLog.BuildOffloadRow(RunStart.AddSeconds(2), RunStart, "archive-offload-upload", "b!onedrive0001", "01PROJZYGGY", "quote-2026.txt", 68, null),
            ActionLog.BuildOffloadRow(RunStart.AddSeconds(3), RunStart, "archive-offload-upload", "b!onedrive0001", "01PROJZYGGY", "quote-2026.txt", 68, "nameAlreadyExists"),
        };

        // Assert
        string.Concat(rows.Select(r => r + "\n")).Should().Be(golden);
    }

    [Fact]
    public void BuildOffloadRow_ErrorCodeSanitised()
    {
        // Act
        var row = ActionLog.BuildOffloadRow(RunStart, RunStart, "archive-offload-upload", "b!onedrive0001", "01PROJZYGGY", "quote-2026.txt", 68, "bad code\"; " + new string('x', 60));

        // Assert
        row.Should().EndWith($"\"status\":\"error: badcode{new string('x', 33)}\"}}");
    }

    [Fact]
    public void BuildOffloadRow_NeverBearerOrBody()
    {
        // Act
        var row = ActionLog.BuildOffloadRow(RunStart, RunStart, "archive-offload-upload", "b!onedrive0001", "01PROJZYGGY", "quote-2026.txt", 68, null);

        // Assert
        row.Should().NotContain("Bearer").And.NotContain("content").And.NotContain("Quote for");
    }

    [Fact]
    public void BuildOffloadRow_OtherTool_Throws()
    {
        // Act
        var act = () => ActionLog.BuildOffloadRow(RunStart, RunStart, "upload-file-content", "b!onedrive0001", "01PROJZYGGY", "x.txt", 1, null);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
