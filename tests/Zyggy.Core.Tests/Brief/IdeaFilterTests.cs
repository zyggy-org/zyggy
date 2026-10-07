using Zyggy.Core.Brief;
using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Brief;

/// <summary>
/// Spec 35 AC-34: the binary keeps a suggestion only when its area is allowed, its basis line occurs in an allowed memory file, it was not
/// shown recently or answered "not interested" / "later", its area was not shown on the previous brief day (unless due within 7 days), and
/// its text carries no link, address, contact detail or secret — then the cap; never padded.
/// </summary>
public sealed class IdeaFilterTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly string[] AllAreas = ["career", "business", "client", "zyggy", "family", "travel", "home", "hobbies"];

    private readonly MemoryTree _tree = new(
        ("business/clients/acme-corp.md", "# Acme Corp\n- [observed] 2026-09-20: Acme renewal talks start in November\n"),
        ("private/family/holidays.md", "- [stated] 2026-09-01: school autumn holiday from 26 October to 1 November\n"),
        ("inbox/github-inventory-2026-10-05.md", "- zyggy-core: three open issues labelled roadmap\n"),
        ("inbox/m365-mail-2026-10-05.md", "- Carol sent the invoice for the September work\n"));

    private readonly SecretPatterns _secrets = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    public void Dispose() => _tree.Dispose();

    private static IdeaSuggestion S(
        string id = "acme-renewal-offer",
        string area = "client",
        string file = "business/clients/acme-corp.md",
        string line = "Acme renewal talks start in November",
        DateOnly? deadline = null,
        string text = "Prepare the Acme renewal offer",
        string whyNow = "The talks start in November",
        string? prepare = "an outline of the offer") =>
        new(id, area, text, whyNow, [new IdeaBasis(file, line)], deadline, prepare);

    private IdeaFilterContext Context(IReadOnlyList<IdeaRow>? history = null, IReadOnlyCollection<string>? areas = null, int cap = 3) =>
        new(Today, areas ?? AllAreas, cap, RepeatDays: 14, SuppressDays: 90, history ?? [], _tree.Paths, _secrets);

    private static IdeaRow Shown(string date, string id, string area, string? deadline = null) =>
        new(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), "shown", id, area, deadline is null ? null : DateOnly.Parse(deadline, System.Globalization.CultureInfo.InvariantCulture), null, null);

    private static IdeaRow Answer(string date, string id, string answer, string? until = null) =>
        new(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), "answer", id, "client", null, answer, until is null ? null : DateOnly.Parse(until, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("area-unknown", "area")]
    [InlineData("area-weekend", "area")]
    [InlineData("basis-line-absent", "basis")]
    [InlineData("basis-line-too-short", "basis")]
    [InlineData("basis-file-absent", "basis")]
    [InlineData("basis-outside-principal", "basis")]
    [InlineData("basis-denied-inbox", "basis")]
    [InlineData("basis-dream-state", "basis")]
    [InlineData("repeat", "repeat")]
    [InlineData("not-interested", "answered")]
    [InlineData("later-future", "answered")]
    [InlineData("area-yesterday", "area-yesterday")]
    [InlineData("text-url", "text")]
    [InlineData("text-address", "text")]
    [InlineData("text-secret", "text")]
    [InlineData("text-phone", "text")]
    [InlineData("basis-line-secret", "text")]
    public void Filter_DropReason(string scenario, string reason)
    {
        // Arrange
        var (suggestion, context) = scenario switch
        {
            "area-unknown" => (S(area: "politics"), Context()),
            "area-weekend" => (S(), Context(areas: ["family", "travel", "home", "hobbies"])),
            "basis-line-absent" => (S(line: "Acme cancelled the contract last week"), Context()),
            "basis-line-too-short" => (S(line: "Acme"), Context()),
            "basis-file-absent" => (S(file: "career/goals.md"), Context()),
            "basis-outside-principal" => (S(file: "../bob/business/clients/acme-corp.md"), Context()),
            "basis-denied-inbox" => (S(file: "inbox/m365-mail-2026-10-05.md", line: "Carol sent the invoice for the September work"), Context()),
            "basis-dream-state" => (S(file: ".dream/ledger.json", line: "Acme renewal talks start in November"), Context()),
            "repeat" => (S(), Context([Shown("2026-09-30", "acme-renewal-offer", "client")])),
            "not-interested" => (S(), Context([Answer("2026-08-01", "acme-renewal-offer", "not-interested")])),
            "later-future" => (S(), Context([Answer("2026-10-01", "acme-renewal-offer", "later", "2026-11-01")])),
            "area-yesterday" => (S(), Context([Shown("2026-10-05", "acme-other", "client")])),
            "text-url" => (S(text: "Read https://example.org/renewal first"), Context()),
            "text-address" => (S(prepare: "a mail to carol@example.org"), Context()),
            "text-secret" => (S(whyNow: "pay BE71 0961 2345 6769 first"), Context()),
            "text-phone" => (S(prepare: "a call to +32 470 12 34 56"), Context()),
            "basis-line-secret" => (S(line: "token=abcdef123456 renewal"), Context()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        if (scenario == "basis-line-secret")
        {
            _tree.Write("business/clients/acme-corp.md", "- token=abcdef123456 renewal\n");
        }

        // Act
        var (kept, dropped) = IdeaFilter.Filter([suggestion], context);

        // Assert
        kept.Should().BeEmpty();
        dropped.Should().Equal(new Dictionary<string, int> { [reason] = 1 });
    }

    [Fact]
    public void Filter_Valid_Kept()
    {
        // Act
        var (kept, dropped) = IdeaFilter.Filter([S()], Context());

        // Assert
        kept.Should().ContainSingle().Which.Should().BeEquivalentTo(S());
        dropped.Should().BeEmpty();
    }

    [Fact]
    public void Filter_NeverPads()
    {
        // Act
        var (kept, dropped) = IdeaFilter.Filter([S(), S(id: "invented", area: "career", file: "career/goals.md")], Context(cap: 3));

        // Assert
        kept.Should().ContainSingle().Which.Id.Should().Be("acme-renewal-offer");
        dropped.Should().Equal(new Dictionary<string, int> { ["basis"] = 1 });
    }

    [Fact]
    public void Filter_OverCap_FirstKeptRestDroppedAsCap()
    {
        // Arrange
        var five = Enumerable.Range(1, 5).Select(i => S(id: $"idea-{i}")).ToList();

        // Act
        var (kept, dropped) = IdeaFilter.Filter(five, Context(cap: 3));

        // Assert
        kept.Select(k => k.Id).Should().Equal("idea-1", "idea-2", "idea-3");
        dropped.Should().Equal(new Dictionary<string, int> { ["cap"] = 2 });
    }

    [Fact]
    public void Filter_SameIdTwiceInOneAnswer_SecondDroppedAsRepeat()
    {
        // Act
        var (kept, dropped) = IdeaFilter.Filter([S(), S(text: "Prepare it again")], Context());

        // Assert
        kept.Should().ContainSingle();
        dropped.Should().Equal(new Dictionary<string, int> { ["repeat"] = 1 });
    }

    [Fact]
    public void Filter_DeadlineEarlier_RepeatAllowed()
    {
        // Arrange: shown on 2026-09-30 with a deadline of 1 December; now due on 1 November
        var history = new[] { Shown("2026-09-30", "acme-renewal-offer", "client", "2026-12-01") };

        // Act
        var (kept, _) = IdeaFilter.Filter([S(deadline: new DateOnly(2026, 11, 1))], Context(history));

        // Assert
        kept.Should().ContainSingle();
    }

    [Fact]
    public void Filter_ShownBeforeRepeatWindow_Kept()
    {
        // Act
        var (kept, _) = IdeaFilter.Filter([S()], Context([Shown("2026-09-21", "acme-renewal-offer", "client")]));

        // Assert
        kept.Should().ContainSingle();
    }

    [Fact]
    public void Filter_NotInterestedBeforeSuppressWindow_Kept()
    {
        // Act
        var (kept, _) = IdeaFilter.Filter([S()], Context([Answer("2026-06-01", "acme-renewal-offer", "not-interested")]));

        // Assert
        kept.Should().ContainSingle();
    }

    [Fact]
    public void Filter_LaterUntilPassed_Kept()
    {
        // Act
        var (kept, _) = IdeaFilter.Filter([S()], Context([Answer("2026-09-01", "acme-renewal-offer", "later", "2026-10-05")]));

        // Assert
        kept.Should().ContainSingle();
    }

    [Fact]
    public void Filter_AreaYesterdayDeadlineIn7Days_Allowed()
    {
        // Act
        var (kept, _) = IdeaFilter.Filter([S(deadline: new DateOnly(2026, 10, 13))], Context([Shown("2026-10-05", "acme-other", "client")]));

        // Assert
        kept.Should().ContainSingle();
    }

    [Fact]
    public void Filter_AreaShownTwoDaysAgo_YesterdayAnotherArea_Kept()
    {
        // Arrange: yesterday (2026-10-05) showed a family idea; client was shown on 2026-10-02
        var history = new[] { Shown("2026-10-02", "acme-other", "client"), Shown("2026-10-05", "family-trip", "family") };

        // Act
        var (kept, _) = IdeaFilter.Filter([S()], Context(history));

        // Assert
        kept.Should().ContainSingle();
    }

    [Fact]
    public void Filter_BasisInGithubInventory_Kept()
    {
        // Act
        var (kept, _) = IdeaFilter.Filter([S(id: "zyggy-roadmap", area: "zyggy", file: "inbox/github-inventory-2026-10-05.md", line: "zyggy-core: three open issues labelled roadmap")], Context());

        // Assert
        kept.Should().ContainSingle();
    }
}
