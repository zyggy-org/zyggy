using Zyggy.Core.Brief;

namespace Zyggy.Core.Tests.Brief;

/// <summary>
/// Spec 35 AC-36: <c>zyggy brief idea &lt;n&gt; good|skip|not-interested|later|do-it [--until &lt;d&gt;] [--date &lt;d&gt;]</c> resolves the
/// number from that date's item list and appends one <c>answer</c> row under the lock; <c>later</c> needs a future date.
/// </summary>
public sealed class BriefIdeaVerbTests : IDisposable
{
    private readonly BriefFixture _fixture = new();

    public BriefIdeaVerbTests() => File.Copy(BriefFixture.Golden("items-sidecar.json"), _fixture.Paths.Sidecar(BriefFixture.Today));

    public void Dispose() => _fixture.Dispose();

    private string[] Rows() => File.ReadAllLines(_fixture.Paths.IdeasLog);

    [Fact]
    public async Task Idea_Two_NotInterested_RowAppended()
    {
        // Act
        var (exit, console) = await _fixture.RunBriefAsync("idea", "2", "not-interested");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("recorded: family-autumn-trip not-interested\n");
        Rows().Should().Equal("{\"date\":\"2026-10-06\",\"kind\":\"answer\",\"id\":\"family-autumn-trip\",\"area\":\"family\",\"answer\":\"not-interested\"}");
    }

    [Fact]
    public async Task Idea_LaterWithFutureUntil_RowCarriesUntil()
    {
        // Act
        var (exit, console) = await _fixture.RunBriefAsync("idea", "1", "later", "--until", "2026-11-15");

        // Assert
        exit.Should().Be(0, console.Stderr);
        Rows().Should().Equal("{\"date\":\"2026-10-06\",\"kind\":\"answer\",\"id\":\"acme-renewal-offer\",\"area\":\"client\",\"answer\":\"later\",\"until\":\"2026-11-15\"}");
    }

    [Theory]
    [InlineData("later")]
    [InlineData("later", "--until", "2026-10-06")]
    [InlineData("later", "--until", "2026-10-01")]
    [InlineData("good", "--until", "2026-11-15")]
    [InlineData("maybe")]
    [InlineData("later", "--until", "soon")]
    public async Task Idea_BadAnswerOrUntil_ExitFour(params string[] rest)
    {
        // Act
        var (exit, console) = await _fixture.RunBriefAsync(["idea", "1", .. rest]);

        // Assert
        exit.Should().Be(4);
        console.Stderr.Should().StartWith("brief: ").And.EndWith(" (usage: zyggy brief idea <n> good|skip|not-interested|later|do-it [--until <YYYY-MM-DD>] [--date <YYYY-MM-DD>])\n");
        File.Exists(_fixture.Paths.IdeasLog).Should().BeFalse();
    }

    [Fact]
    public async Task Idea_UnknownNumber_ExitFive()
    {
        // Act
        var (exit, console) = await _fixture.RunBriefAsync("idea", "9", "good");

        // Assert
        exit.Should().Be(5);
        console.Stderr.Should().Be("brief: no suggestion 9 in the brief of 2026-10-06\n");
    }

    [Fact]
    public async Task Idea_NoSidecar_ExitThree()
    {
        // Act
        var (exit, console) = await _fixture.RunBriefAsync("idea", "1", "good", "--date", "2026-10-01");

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("brief: no brief for 2026-10-01\n");
    }

    [Fact]
    public async Task Idea_DateOption_ThatSidecar()
    {
        // Arrange: yesterday's item list numbers a different idea as 1
        File.WriteAllText(_fixture.Paths.Sidecar(new DateOnly(2026, 10, 5)), """{"schema":1,"date":"2026-10-05","generated":"2026-10-05T04:30:00Z","mode":"weekday","watermark":"","audit":"ok","auditReasons":[],"counts":{"urgent":0,"important":0,"other":0,"files":0,"filesOther":0},"page_exceeded":false,"items":[],"ideas":[{"n":1,"id":"home-gutter","area":"home"}]}""");

        // Act
        var (exit, console) = await _fixture.RunBriefAsync("idea", "1", "do-it", "--date", "2026-10-05");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("recorded: home-gutter do-it\n");
    }

    [Fact]
    public async Task Idea_TwoWritersAtOnce_BothRowsWhole()
    {
        // Arrange
        var history = new IdeasHistory(_fixture.Paths);

        // Act: two writers, 50 answers each, at the same time
        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 50; i++) { history.AppendAnswer(BriefFixture.Today, $"a-{i}", "home", "good", null); } }, TestContext.Current.CancellationToken),
            Task.Run(() => { for (var i = 0; i < 50; i++) { history.AppendAnswer(BriefFixture.Today, $"b-{i}", "home", "skip", null); } }, TestContext.Current.CancellationToken));

        // Assert
        var rows = history.Read();
        rows.Should().HaveCount(100);
        File.ReadAllLines(_fixture.Paths.IdeasLog).Should().OnlyContain(l => l.StartsWith('{') && l.EndsWith('}'));
    }
}
