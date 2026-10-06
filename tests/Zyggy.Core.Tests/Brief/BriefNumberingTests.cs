using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-17: Z numbers are the binary's — urgent, important, discards, then the one file-other item that always fits.</summary>
public sealed class BriefNumberingTests
{
    private static ZCandidate Candidate(ZKind kind, MailClass cls, int order, string id) =>
        new(kind, kind == ZKind.DiscardDraft ? null : id, kind == ZKind.FileOther ? ["o1", "o2"] : null, kind == ZKind.DiscardDraft ? id : null, ZDestination.Archive, "S " + id, new ZSender("N", "n@example.org"), "2026-10-06T06:00:00Z", "why", cls, order);

    [Fact]
    public void Numbers_UrgentThenImportantThenDiscardThenFileOther()
    {
        // Arrange: listed in received order, mixed classes
        var candidates = new[]
        {
            Candidate(ZKind.Move, MailClass.Important, 1, "i1"),
            Candidate(ZKind.FileOther, MailClass.Other, int.MaxValue, "fo"),
            Candidate(ZKind.Send, MailClass.Urgent, 2, "u1"),
            Candidate(ZKind.DiscardDraft, MailClass.Important, 3, "d1"),
            Candidate(ZKind.Move, MailClass.Important, 4, "i2"),
            Candidate(ZKind.Move, MailClass.Urgent, 5, "u2"),
        };

        // Act
        var (items, dropped) = BriefNumbering.Number(candidates, 10);

        // Assert
        items.Select(i => (i.N, i.MessageId ?? i.DraftId ?? "fo")).Should().Equal((1, "u1"), (2, "u2"), (3, "i1"), (4, "i2"), (5, "d1"), (6, "fo"));
        items.Where(i => i.FromUrgent).Select(i => i.N).Should().Equal(1, 2);
        dropped.Should().BeEmpty();
    }

    [Fact]
    public void Numbers_CappedAtSuggestionCap_FileOtherAlwaysFits()
    {
        // Arrange
        var candidates = Enumerable.Range(1, 5).Select(i => Candidate(ZKind.Move, MailClass.Important, i, $"i{i}")).Append(Candidate(ZKind.FileOther, MailClass.Other, int.MaxValue, "fo")).ToList();

        // Act
        var (items, dropped) = BriefNumbering.Number(candidates, 3);

        // Assert
        items.Select(i => i.Kind).Should().Equal(ZKind.Move, ZKind.Move, ZKind.FileOther);
        items[^1].N.Should().Be(3);
        dropped.Select(c => c.MessageId).Should().Equal("i3", "i4", "i5");
    }

    [Fact]
    public void Numbers_EmptyWhenNoZ()
    {
        // Act
        var (items, dropped) = BriefNumbering.Number([], 10);

        // Assert
        items.Should().BeEmpty();
        dropped.Should().BeEmpty();
    }
}
