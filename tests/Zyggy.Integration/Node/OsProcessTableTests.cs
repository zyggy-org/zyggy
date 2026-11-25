using System.Diagnostics;

using Zyggy.Core.Processes;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 61 slice 7 (AC-23, the real edge): the operating system's process table on this OS — the owner check (the token user on Windows,
/// <c>/proc/&lt;pid&gt;</c>'s uid on Linux), the start time that tells a reused pid apart, and ending a process tree.
/// </summary>
public sealed class OsProcessTableTests
{
    [Fact]
    public void Find_ThisProcess_OwnedByCurrentUserWithStartTime()
    {
        // Act
        ProcessFacts? self = OsProcessTable.Instance.Find(Environment.ProcessId);

        // Assert
        self.Should().Match<ProcessFacts>(f => f.OwnedByCurrentUser && f.StartedUtc != null && f.Name.Length > 0);
    }

    [Fact]
    public void Find_ExitedProcess_Null()
    {
        // Arrange
        using Process child = StartChild(delayMs: 0);
        child.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();

        // Act
        ProcessFacts? gone = OsProcessTable.Instance.Find(child.Id);

        // Assert
        gone.Should().BeNull();
    }

    [Fact]
    public void EndTree_RunningChild_GoneAndNoReason()
    {
        // Arrange
        using Process child = StartChild(delayMs: 120_000);
        OsProcessTable.Instance.Find(child.Id)!.OwnedByCurrentUser.Should().BeTrue();

        // Act
        string? why = OsProcessTable.Instance.EndTree(child.Id, TimeSpan.FromSeconds(10));

        // Assert
        (why, child.HasExited).Should().Be((null, true));
    }

    private static Process StartChild(int delayMs)
    {
        var info = new ProcessStartInfo(FakeClaude.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-p");
        info.Environment["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done";
        info.Environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = delayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Process child = Process.Start(info)!;
        child.StandardInput.Close();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        return child;
    }
}
