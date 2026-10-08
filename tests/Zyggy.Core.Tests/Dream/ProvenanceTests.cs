using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

/// <summary>
/// Appendix B provenance tokens: the backfills' long tokens ("m365-mail D &lt;subject&gt;", "m365-file D &lt;drive path&gt; D") are
/// satisfied by a target token with the same "&lt;source&gt; &lt;date&gt;", because a merged line of at most 400 characters must
/// shorten them; a different source or date is not.
/// </summary>
public sealed class ProvenanceTests
{
    private static DreamBatchLine Observed(string provenance) =>
        new("L1", "inbox/m365-mail-backfill-2026-10-03.md", $"- [observed] 2026-10-03 [{provenance}]: The car lease was renewed.", "h",
            DreamLineClass.ObservedInbox, new DateOnly(2026, 10, 3), 1);

    private static string Target(string provenance) => $"- [observed] 2026-10-03 [{provenance}]: Company car lease renewed.\n";

    [Theory]
    [InlineData("m365-mail 2026-10-03 RE: Update leasing contract", "m365-mail 2026-10-03 RE: Update leasing contract")]
    [InlineData("m365-mail 2026-10-03 RE: Update leasing contract", "m365-mail 2026-10-03")]
    [InlineData("m365-mail 2026-10-03 RE: Update leasing contract", "remember 2026-10-04; m365-mail 2026-10-03 leasing")]
    [InlineData("m365-file b!abc:/Documents/Acme/car/invoice.pdf 2026-09-01", "m365-file 2026-09-01")]
    [InlineData("github-inventory 2026-10-01", "github-inventory 2026-10-01")]
    public void Satisfied_SameSourceAndDate_True(string source, string target)
    {
        // Assert
        DreamChecks.ProvenanceSatisfied(Observed(source), Target(target)).Should().BeTrue();
    }

    [Theory]
    [InlineData("m365-mail 2026-10-03 RE: Update leasing contract", "m365-mail 2026-10-02")]
    [InlineData("m365-mail 2026-10-03 RE: Update leasing contract", "m365-file 2026-10-03")]
    [InlineData("m365-mail 2026-10-03 RE: Update leasing contract", "remember 2026-10-03")]
    [InlineData("github-inventory 2026-10-01", "github-inventory")]
    public void Satisfied_OtherSourceOrDate_False(string source, string target)
    {
        // Assert
        DreamChecks.ProvenanceSatisfied(Observed(source), Target(target)).Should().BeFalse();
    }

    [Fact]
    public void Satisfied_FileDateAfterLongPath_UsesTheSourceKindAndFirstDate()
    {
        // Arrange: the files backfill puts the drive path between the kind and the date.
        var source = Observed("m365-file b!abc:/Documents/Acme/car/alice (2023 to 2024)/Invoices/2024-01.pdf 2026-10-03");

        // Assert
        DreamChecks.ProvenanceSatisfied(source, Target("m365-file 2026-10-03")).Should().BeTrue();
        DreamChecks.ProvenanceSatisfied(source, Target("m365-file 2023-01-01")).Should().BeFalse();
    }
}
