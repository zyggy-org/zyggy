using Zyggy.Core.Memory.Mirror;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Memory.Mirror;

/// <summary>
/// AC-3 (unit): a work machine whose reach leaves <c>supervision.outboundPaths</c>, an empty <c>outboundPaths</c>, or a target whose
/// repository equals the memory remote in SSH or HTTPS form refuses the publisher's start (spec 47, slice 1).
/// </summary>
public sealed class MemoryMirrorConfigurationTests
{
    private const string MemoryRemote = "git@example.test:zyggy-org/zyggy-memory.git";

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly MachineName[] WorkMachines = [WorkLaptop];

    private static readonly string[] Outbound = ["business/"];

    [Fact]
    public void Check_HomeDefaultAndWorkBusiness_Accepted()
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Home(), Work(MirrorReach.Of(["business/"]))], WorkMachines, Outbound, MemoryRemote);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Check_WorkReachInsideOutboundFolder_Accepted()
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Work(MirrorReach.Of(["business/clients/"]))], WorkMachines, Outbound, MemoryRemote);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData("private/")]
    [InlineData("profile.md")]
    [InlineData("businessplans/")]
    [InlineData("daily/")]
    public void Check_WorkReachOutsideOutbound_Refused(string path)
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Work(MirrorReach.Of(["business/", path]))], WorkMachines, Outbound, MemoryRemote);

        // Assert
        refusal.Should().Be(new MirrorRefusal(MirrorRefusalReason.ReachOutsideOutbound, WorkLaptop, path));
    }

    [Fact]
    public void Check_WorkMachineDefaultReach_Refused()
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Work(MirrorReach.Default)], WorkMachines, Outbound, MemoryRemote);

        // Assert
        refusal!.Reason.Should().Be(MirrorRefusalReason.ReachOutsideOutbound);
    }

    [Fact]
    public void Check_WorkMachineEmptyOutbound_Refused()
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Home(), Work(MirrorReach.Of(["business/"]))], WorkMachines, [], MemoryRemote);

        // Assert
        refusal.Should().Be(new MirrorRefusal(MirrorRefusalReason.OutboundEmpty, WorkLaptop, "supervision.outboundPaths"));
    }

    [Fact]
    public void Check_NoWorkMachineAmongTargetsEmptyOutbound_Accepted()
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Home()], WorkMachines, [], MemoryRemote);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData("git@example.test:zyggy-org/zyggy-memory.git")]
    [InlineData("git@example.test:zyggy-org/zyggy-memory")]
    [InlineData("ssh://git@example.test/zyggy-org/zyggy-memory.git")]
    [InlineData("https://example.test/zyggy-org/zyggy-memory.git")]
    [InlineData("https://example.test/Zyggy-Org/Zyggy-Memory/")]
    public void Check_TargetEqualToMemoryRemote_Refused(string repository)
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Home() with { Repository = repository }], WorkMachines, Outbound, MemoryRemote);

        // Assert
        refusal.Should().Be(new MirrorRefusal(MirrorRefusalReason.TargetIsMemoryRepository, HomeLaptop, "repository"));
    }

    [Fact]
    public void Check_MemoryRemoteInHttpsFormTargetInSshForm_Refused()
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check(
            [Home() with { Repository = "git@example.test:zyggy-org/zyggy-memory.git" }], WorkMachines, Outbound, "https://example.test/zyggy-org/zyggy-memory");

        // Assert
        refusal!.Reason.Should().Be(MirrorRefusalReason.TargetIsMemoryRepository);
    }

    [Theory]
    [InlineData("git@example.test:zyggy-org/zyggy-memory-archive.git")]
    [InlineData("git@other.test:zyggy-org/zyggy-memory.git")]
    [InlineData("git@example.test:someone/zyggy-memory.git")]
    public void Check_OtherRepository_Accepted(string repository)
    {
        // Act
        var refusal = MirrorConfigurationCheck.Check([Home() with { Repository = repository }], WorkMachines, Outbound, MemoryRemote);

        // Assert
        refusal.Should().BeNull();
    }

    private static MirrorTarget Home() =>
        new(HomeLaptop, "git@example.test:zyggy-org/zyggy-mirror-home-laptop.git", "/keys/zyggy_zyggy-mirror-home-laptop_ed25519", MirrorReach.Default);

    private static MirrorTarget Work(MirrorReach reach) =>
        new(WorkLaptop, "git@example.test:zyggy-org/zyggy-mirror-work-laptop.git", "/keys/zyggy_zyggy-mirror-work-laptop_ed25519", reach);
}
