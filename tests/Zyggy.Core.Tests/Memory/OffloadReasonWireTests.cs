using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 <c>OffloadReason</c>: the closed set, its snake_case wire names and its transient / permanent / run-level classes.</summary>
public sealed class OffloadReasonWireTests
{
    public static TheoryData<string, string> Rows() => new()
    {
        { nameof(OffloadReason.TransportError), "transport_error" },
        { nameof(OffloadReason.ServerError), "server_error" },
        { nameof(OffloadReason.Throttled), "throttled" },
        { nameof(OffloadReason.AuthFailed), "auth_failed" },
        { nameof(OffloadReason.GitError), "git_error" },
        { nameof(OffloadReason.PushDeferred), "push_deferred" },
        { nameof(OffloadReason.ShaMismatch), "sha_mismatch" },
        { nameof(OffloadReason.WriteRefused), "write_refused" },
        { nameof(OffloadReason.TargetConflict), "target_conflict" },
        { nameof(OffloadReason.VerifyFailed), "verify_failed" },
        { nameof(OffloadReason.TypeBlocked), "type_blocked" },
        { nameof(OffloadReason.TooLarge), "too_large" },
        { nameof(OffloadReason.Forbidden), "forbidden" },
        { nameof(OffloadReason.RootMissing), "root_missing" },
        { nameof(OffloadReason.RootNotFolder), "root_not_folder" },
        { nameof(OffloadReason.ConfigError), "config_error" },
        { nameof(OffloadReason.LockHeld), "lock_held" },
        { nameof(OffloadReason.LogUnwritable), "log_unwritable" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void ToWire_Member_SnakeCase(string member, string wire)
    {
        // Arrange
        var reason = Enum.Parse<OffloadReason>(member);

        // Act
        var text = OffloadReasonWire.ToWire(reason);

        // Assert
        text.Should().Be(wire);
        OffloadReasonWire.TryFromWire(wire, out var back).Should().BeTrue();
        back.Should().Be(reason);
    }

    [Fact]
    public void EveryMember_HasATestRow()
    {
        // Assert
        Rows().Select(r => r.Data.Item1).Should().BeEquivalentTo(Enum.GetNames<OffloadReason>());
    }

    [Fact]
    public void Permanent_SetMatchesSpec()
    {
        // Act
        var permanent = Enum.GetValues<OffloadReason>().Where(OffloadReasonWire.IsPermanent).Select(OffloadReasonWire.ToWire);

        // Assert
        permanent.Should().BeEquivalentTo("sha_mismatch", "write_refused", "target_conflict", "verify_failed", "type_blocked", "too_large", "forbidden");
    }

    [Fact]
    public void RunLevel_SetMatchesSpec()
    {
        // Act
        var runLevel = Enum.GetValues<OffloadReason>().Where(OffloadReasonWire.IsRunLevel).Select(OffloadReasonWire.ToWire);

        // Assert
        runLevel.Should().BeEquivalentTo("root_missing", "root_not_folder", "config_error", "lock_held", "log_unwritable");
    }
}
