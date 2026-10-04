using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamOptionsTests
{
    [Fact]
    public void Defaults_MatchSpecTable()
    {
        // Act
        var o = new DreamOptions();

        // Assert
        (o.BatchMaxLines, o.BatchMinLines, o.BatchMaxBytes, o.MaxBatchesPerRun).Should().Be((150, 10, 60_000, 20));
        (o.CallTimeoutMinutes, o.CallMaxTurns, o.CallMaxBudgetUsd).Should().Be((15, 30, 5m));
        (o.RunMaxMinutes, o.RunMaxBudgetUsd).Should().Be((150, 100m));
        (o.CompressAboveLines, o.CompressMaxRemovedRatio, o.MaxCompressionsPerRun).Should().Be((300, 0.5, 5));
        (o.BatchMaxRemovedRatio, o.BatchMaxRemovedLines, o.RunMaxRemovedRatio, o.IdentityMaxShrinkRatio).Should().Be((0.25, 40, 0.10, 0.10));
        (o.MaxCategoriesPerSide, o.MaxNewCategoriesPerRun).Should().Be((12, 3));
        (o.InboxDeleteGraceDays, o.DailyRollupDays, o.QuarantineAfter).Should().Be((7, 30, 3));
        o.Model.Should().BeNull();
        o.Validate().Should().BeEmpty();
    }

    public static TheoryData<DreamOptions, string> AboveCeiling => new()
    {
        { new DreamOptions { BatchMaxLines = 401 }, "batchMaxLines" },
        { new DreamOptions { BatchMaxBytes = 200_001 }, "batchMaxBytes" },
        { new DreamOptions { MaxBatchesPerRun = 61 }, "maxBatchesPerRun" },
        { new DreamOptions { CallTimeoutMinutes = 46 }, "callTimeoutMinutes" },
        { new DreamOptions { CallMaxTurns = 81 }, "callMaxTurns" },
        { new DreamOptions { CallMaxBudgetUsd = 25.01m }, "callMaxBudgetUsd" },
        { new DreamOptions { RunMaxMinutes = 361 }, "runMaxMinutes" },
        { new DreamOptions { RunMaxBudgetUsd = 501m }, "runMaxBudgetUsd" },
        { new DreamOptions { CompressAboveLines = 301 }, "compressAboveLines" },
        { new DreamOptions { CompressMaxRemovedRatio = 0.61 }, "compressMaxRemovedRatio" },
        { new DreamOptions { MaxCompressionsPerRun = 21 }, "maxCompressionsPerRun" },
        { new DreamOptions { BatchMaxRemovedRatio = 0.41 }, "batchMaxRemovedRatio" },
        { new DreamOptions { BatchMaxRemovedLines = 121 }, "batchMaxRemovedLines" },
        { new DreamOptions { RunMaxRemovedRatio = 0.21 }, "runMaxRemovedRatio" },
        { new DreamOptions { IdentityMaxShrinkRatio = 0.21 }, "identityMaxShrinkRatio" },
        { new DreamOptions { MaxCategoriesPerSide = 21 }, "maxCategoriesPerSide" },
        { new DreamOptions { MaxNewCategoriesPerRun = 6 }, "maxNewCategoriesPerRun" },
        { new DreamOptions { QuarantineAfter = 6 }, "quarantineAfter" },
    };

    [Theory]
    [MemberData(nameof(AboveCeiling))]
    public void Validate_AboveCeiling_NamesKey(DreamOptions options, string key)
    {
        // Act
        var offending = options.Validate();

        // Assert
        offending.Should().Equal(key);
    }

    [Fact]
    public void Validate_InboxGraceBelowThree_NamesKey()
    {
        // Assert
        new DreamOptions { InboxDeleteGraceDays = 2 }.Validate().Should().Equal("inboxDeleteGraceDays");
        new DreamOptions { DailyRollupDays = 29 }.Validate().Should().Equal("dailyRollupDays");
        new DreamOptions { CallTimeoutMinutes = 0 }.Validate().Should().Equal("callTimeoutMinutes");
        new DreamOptions { BatchMinLines = 200 }.Validate().Should().Equal("batchMinLines");
    }
}
