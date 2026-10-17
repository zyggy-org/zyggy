using Zyggy.Core.Configuration;

namespace Zyggy.Core.Tests.Configuration;

/// <summary>Spec 07: the <c>poll</c> section's third knob, the safety-fetch period, reaches the poller options.</summary>
public sealed class PollSettingsTests
{
    [Fact]
    public void ToPollOptions_SafetyFetchMinutes_BecomesInterval()
    {
        // Act
        var one = new PollSettings(10, 60, 1).ToPollOptions();
        var defaults = new PollSettings().ToPollOptions();

        // Assert
        one.SafetyFetchInterval.Should().Be(TimeSpan.FromMinutes(1));
        defaults.SafetyFetchInterval.Should().Be(TimeSpan.FromMinutes(15));
        defaults.ActiveInterval.Should().Be(TimeSpan.FromSeconds(10));
    }
}
