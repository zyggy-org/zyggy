using System.Runtime.Versioning;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 AC-19..AC-21: consecutive failures counted, parked at the maximum or at once for a permanent reason, a retry clears a park.</summary>
public sealed class OffloadStateTests : IDisposable
{
    private const string Key = "zyggy/quote-2026";
    private static readonly DateOnly Today = new(2026, 10, 1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly ArchiveStatePaths _paths;

    public OffloadStateTests() =>
        _paths = new ArchiveStatePaths(new Dictionary<string, string?> { ["HOME"] = _root, ["ZYGGY_STATE_DIR"] = Path.Combine(_root, "state") });

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Paths_StateDirUnderZyggyStateDir()
    {
        // Assert
        _paths.StateFile.Should().Be(Path.Join(_root, "state", "archive", "offload-state.json"));
        _paths.LockFile.Should().Be(Path.Join(_root, "state", "archive", "offload.lock"));
        new ArchiveStatePaths(new Dictionary<string, string?> { ["HOME"] = _root }).StateDirectory
            .Should().Be(Path.Join(_root, ".local", "state", "zyggy", "archive"));
    }

    [Fact]
    public void Load_Missing_Empty()
    {
        // Act
        var load = OffloadState.Load(_paths);

        // Assert
        load.Error.Should().BeNull();
        load.State!.Items.Should().BeEmpty();
        load.State.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void Load_Invalid_ErrorNamesFile()
    {
        // Arrange
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(_paths.StateFile, """{"items":{"zyggy/x":{"failures":"two"}}}""");

        // Act
        var load = OffloadState.Load(_paths);

        // Assert
        load.State.Should().BeNull();
        load.Error.Should().Contain("offload-state.json");
    }

    [Fact]
    public void RecordTransient_Twice_CountTwoNotParked()
    {
        // Arrange
        var state = new OffloadState();

        // Act
        state.RecordTransient(Key, OffloadReason.TransportError, 3, Today);
        var item = state.RecordTransient(Key, OffloadReason.Throttled, 3, Today.AddDays(1));

        // Assert
        item.Should().Be(new OffloadStateItem(2, OffloadReason.Throttled, false, Today));
    }

    [Fact]
    public void RecordTransient_ThirdAtMaxThree_Parked()
    {
        // Arrange
        var state = new OffloadState();

        // Act
        for (var i = 0; i < 3; i++)
        {
            state.RecordTransient(Key, OffloadReason.ServerError, 3, Today);
        }

        // Assert
        state.Items[Key].Parked.Should().BeTrue();
        state.Parked.Should().Equal(Key);
        state.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public void Park_Permanent_ParkedAtOnce()
    {
        // Arrange
        var state = new OffloadState();

        // Act
        var item = state.Park(Key, OffloadReason.TargetConflict, Today);

        // Assert
        item.Should().Be(new OffloadStateItem(1, OffloadReason.TargetConflict, true, Today));
    }

    [Fact]
    public void Clear_AfterSuccess_ItemGone()
    {
        // Arrange
        var state = new OffloadState();
        state.RecordTransient(Key, OffloadReason.TransportError, 3, Today);

        // Act
        state.Clear(Key);

        // Assert
        state.Items.Should().BeEmpty();
    }

    [Fact]
    public void TryRetry_Parked_ClearedTrue()
    {
        // Arrange
        var state = new OffloadState();
        state.Park(Key, OffloadReason.VerifyFailed, Today);

        // Act
        var retried = state.TryRetry(Key);

        // Assert
        retried.Should().BeTrue();
        state.Items.Should().BeEmpty();
    }

    [Fact]
    public void TryRetry_Unknown_False()
    {
        // Act / Assert
        new OffloadState().TryRetry(Key).Should().BeFalse();
    }

    [Fact]
    public void TryRetry_NotParked_False()
    {
        // Arrange
        var state = new OffloadState();
        state.RecordTransient(Key, OffloadReason.TransportError, 3, Today);

        // Act
        var retried = state.TryRetry(Key);

        // Assert
        retried.Should().BeFalse();
        state.Items.Should().ContainKey(Key);
    }

    [Fact]
    public void Save_RoundTrip_EqualsJsonShape()
    {
        // Arrange
        var state = new OffloadState();
        state.RecordTransient("zyggy/fence-quote", OffloadReason.Throttled, 3, Today);
        state.Park(Key, OffloadReason.TargetConflict, Today);
        state.RecordRun(new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero), true, null);

        // Act
        state.Save(_paths);
        var load = OffloadState.Load(_paths);

        // Assert
        File.ReadAllText(_paths.StateFile).Should().Be("""
            {
              "items": {
                "zyggy/fence-quote": {
                  "failures": 1,
                  "reason": "throttled",
                  "parked": false,
                  "since": "2026-10-01"
                },
                "zyggy/quote-2026": {
                  "failures": 1,
                  "reason": "target_conflict",
                  "parked": true,
                  "since": "2026-10-01"
                }
              },
              "last_run": {
                "at": "2026-10-01T03:30:00Z",
                "failed": true,
                "reason": null
              }
            }

            """.ReplaceLineEndings("\n"));
        load.State!.Items.Should().BeEquivalentTo(state.Items);
        load.State.LastRun.Should().Be(state.LastRun);
        Directory.EnumerateFiles(_paths.StateDirectory).Should().ContainSingle("no temporary file is left");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "Unix file modes: Linux only")]
    [SupportedOSPlatform("linux")]
    public void Save_OnLinux_File0600Dir0700()
    {
        // Act
        new OffloadState().Save(_paths);

        // Assert
        File.GetUnixFileMode(_paths.StateFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_paths.StateDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void NeedsAttention_ParkedOrLastRunFailed()
    {
        // Arrange
        var failedRun = new OffloadState();
        failedRun.RecordRun(DateTimeOffset.UnixEpoch, true, OffloadReason.RootMissing);
        var goodRun = new OffloadState();
        goodRun.RecordRun(DateTimeOffset.UnixEpoch, false, null);
        goodRun.RecordTransient(Key, OffloadReason.TransportError, 3, Today);

        // Assert
        failedRun.NeedsAttention.Should().BeTrue();
        goodRun.NeedsAttention.Should().BeFalse("a counted transient failure is retried next run, not the owner's concern yet");
    }
}
