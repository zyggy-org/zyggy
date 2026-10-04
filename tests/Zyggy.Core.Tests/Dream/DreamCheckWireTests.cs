using Zyggy.Core.Dream;

namespace Zyggy.Core.Tests.Dream;

public sealed class DreamCheckWireTests
{
    public static TheoryData<DreamCheck, string> Checks => new()
    {
        { DreamCheck.PathRefused, "path_refused" },
        { DreamCheck.SlugInvalid, "slug_invalid" },
        { DreamCheck.SlugDuplicate, "slug_duplicate" },
        { DreamCheck.CategoryInvalid, "category_invalid" },
        { DreamCheck.CategoryCap, "category_cap" },
        { DreamCheck.FormatInvalid, "format_invalid" },
        { DreamCheck.ForeignTag, "foreign_tag" },
        { DreamCheck.ProvenanceMissing, "provenance_missing" },
        { DreamCheck.TagUpgrade, "tag_upgrade" },
        { DreamCheck.IdentityObserved, "identity_observed" },
        { DreamCheck.IdentityShrink, "identity_shrink" },
        { DreamCheck.SecretPattern, "secret_pattern" },
        { DreamCheck.ContactDetail, "contact_detail" },
        { DreamCheck.EditMismatch, "edit_mismatch" },
        { DreamCheck.RemovalLimit, "removal_limit" },
        { DreamCheck.Coverage, "coverage" },
        { DreamCheck.StatedDropped, "stated_dropped" },
        { DreamCheck.FactNotFound, "fact_not_found" },
        { DreamCheck.ConcurrentEdit, "concurrent_edit" },
        { DreamCheck.CompressRejected, "compress_rejected" },
        { DreamCheck.MigrationRejected, "migration_rejected" },
        { DreamCheck.RunRemovalLimit, "run_removal_limit" },
        { DreamCheck.UnfiledDeletion, "unfiled_deletion" },
        { DreamCheck.DirtyPending, "dirty_pending" },
    };

    [Theory]
    [MemberData(nameof(Checks))]
    public void ToWire_Member_ReturnsSnakeCase(DreamCheck check, string wire)
    {
        // Assert
        DreamCheckWire.ToWire(check).Should().Be(wire);
        DreamCheckWire.TryFromWire(wire, out var back).Should().BeTrue();
        back.Should().Be(check);
    }

    [Fact]
    public void EveryMember_HasATestRow()
    {
        // Assert
        Checks.Count.Should().Be(Enum.GetValues<DreamCheck>().Length).And.Be(24);
        DreamCheckWire.TryFromWire("PathRefused", out _).Should().BeFalse();
    }
}
