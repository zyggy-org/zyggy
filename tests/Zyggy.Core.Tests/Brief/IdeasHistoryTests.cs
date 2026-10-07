using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>Spec 35 AC-35, AC-42: <c>ideas.jsonl</c> — one <c>shown</c> row per kept suggestion, pruned after <c>ideas_suppress_days</c> except a future <c>later</c>.</summary>
public sealed class IdeasHistoryTests : IDisposable
{
    private readonly BriefFixture _fixture = new();

    private string File_ => _fixture.Paths.IdeasLog;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void AppendShown_Rows()
    {
        // Arrange
        var history = new IdeasHistory(_fixture.Paths);

        // Act
        history.AppendShown(new DateOnly(2026, 10, 6), [("acme-renewal-offer", "client", new DateOnly(2026, 10, 30)), ("family-trip", "family", null)]);

        // Assert
        File.ReadAllText(File_).Should().Be(
            "{\"date\":\"2026-10-06\",\"kind\":\"shown\",\"id\":\"acme-renewal-offer\",\"area\":\"client\",\"deadline\":\"2026-10-30\"}\n" +
            "{\"date\":\"2026-10-06\",\"kind\":\"shown\",\"id\":\"family-trip\",\"area\":\"family\"}\n");
        history.Read().Should().Equal(
            new IdeaRow(new DateOnly(2026, 10, 6), "shown", "acme-renewal-offer", "client", new DateOnly(2026, 10, 30), null, null),
            new IdeaRow(new DateOnly(2026, 10, 6), "shown", "family-trip", "family", null, null, null));
        if (OperatingSystem.IsLinux())
        {
            File.GetUnixFileMode(File_).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Read_MissingFile_EmptyMalformedRowsSkipped()
    {
        // Arrange
        var history = new IdeasHistory(_fixture.Paths);
        history.Read().Should().BeEmpty();
        Directory.CreateDirectory(_fixture.Paths.Directory);
        File.WriteAllText(File_, "not json\n{\"date\":\"2026-10-01\",\"kind\":\"answer\",\"id\":\"home-gutter\",\"area\":\"home\",\"answer\":\"not-interested\"}\n{\"date\":\"x\"}\n");

        // Act
        var rows = history.Read();

        // Assert
        rows.Should().Equal(new IdeaRow(new DateOnly(2026, 10, 1), "answer", "home-gutter", "home", null, "not-interested", null));
    }

    [Fact]
    public void Prune_OldRowsButFutureLaterKept()
    {
        // Arrange: suppress 90 days on 2026-10-06 keeps rows from 2026-07-08 on
        Directory.CreateDirectory(_fixture.Paths.Directory);
        File.WriteAllText(File_,
            "{\"date\":\"2026-06-01\",\"kind\":\"shown\",\"id\":\"old\",\"area\":\"home\"}\n" +
            "{\"date\":\"2026-06-01\",\"kind\":\"answer\",\"id\":\"later-future\",\"area\":\"travel\",\"answer\":\"later\",\"until\":\"2027-01-01\"}\n" +
            "{\"date\":\"2026-06-01\",\"kind\":\"answer\",\"id\":\"later-past\",\"area\":\"travel\",\"answer\":\"later\",\"until\":\"2026-09-01\"}\n" +
            "{\"date\":\"2026-07-08\",\"kind\":\"shown\",\"id\":\"edge\",\"area\":\"career\"}\n" +
            "{\"date\":\"2026-09-01\",\"kind\":\"shown\",\"id\":\"recent\",\"area\":\"career\"}\n");
        var history = new IdeasHistory(_fixture.Paths);

        // Act
        history.Prune(new DateOnly(2026, 10, 6), suppressDays: 90);

        // Assert
        history.Read().Select(r => r.Id).Should().Equal("later-future", "edge", "recent");
    }
}
