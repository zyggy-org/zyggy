using Zyggy.Core.Bus;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 61 slice 5: <c>zyggy withdraw</c> warns when the claiming machine does not stop a running job — its registry <c>version</c> is
/// older than 0.30.1, absent or unreadable. A pre-release of 0.30.1 is this code; build metadata is ignored.
/// </summary>
public sealed class JobWithdrawerTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    [InlineData("0.30.0", false)]
    [InlineData("0.29.9+abc123", false)]
    [InlineData("0.30", false)]
    [InlineData("0.30.1", true)]
    [InlineData("0.30.1-alpha.0.4", true)]
    [InlineData("0.31.0+abc123", true)]
    [InlineData("1.0.0", true)]
    public void StopsRunningJobs_RegistryVersion_TrueFrom0301(string? version, bool expected)
    {
        // Act
        bool stops = JobWithdrawer.StopsRunningJobs(version);

        // Assert
        stops.Should().Be(expected);
    }
}
