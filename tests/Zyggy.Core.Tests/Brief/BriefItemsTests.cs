using Zyggy.Core.Brief;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.M365;

namespace Zyggy.Core.Tests.Brief;

/// <summary>
/// Spec 35 AC-24: each selected item is resolved from the item list only and checked with one Graph read — still in the folder it was in
/// (<c>ok</c>), elsewhere (<c>moved</c>), gone or in Deleted Items (<c>deleted</c>), not in the list (<c>unknown</c>); the "file the other
/// mails" item becomes one line per mail with its own status; a Graph failure gives no line at all.
/// </summary>
public sealed class BriefItemsTests : IDisposable
{
    private readonly GraphFixture _graph = new();
    private readonly BriefSidecar _sidecar = BriefSidecar.Parse(File.ReadAllBytes(BriefFixture.Golden("items-sidecar.json")))!;

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
    }

    private async Task<(IReadOnlyList<string> Lines, string? Failure)> ResolveAsync(string selector)
    {
        ZSelector.TryParse(selector, out var selection).Should().BeTrue();
        var (lines, failure) = await BriefItems.ResolveAsync(_sidecar, selection, _graph.Reader, CancellationToken.None);
        return (lines, failure?.Message);
    }

    [Fact]
    public async Task Items_Z1Z2_TwoLinesSidecarFieldsStatusOk()
    {
        // Act
        var (lines, failure) = await ResolveAsync("Z1,Z2");

        // Assert
        failure.Should().BeNull();
        lines.Should().Equal(
            """{"n":1,"kind":"send","messageId":"m12","draftId":"d12","subject":"Q3 report draft","sender":{"name":"Dana Example","address":"dana@example.org"},"received":"2026-10-06T07:40:00Z","status":"ok"}""",
            """{"n":2,"kind":"move","messageId":"m13","destination":"archive","subject":"Team offsite dates","sender":{"name":"Erin Example","address":"erin@example.org"},"received":"2026-10-06T08:15:00Z","status":"ok"}""");
    }

    [Fact]
    public async Task Items_DiscardDraftStillInDrafts_Ok()
    {
        // Act
        var (lines, _) = await ResolveAsync("Z3");

        // Assert
        lines.Should().ContainSingle().Which.Should().EndWith("\"status\":\"ok\"}");
    }

    [Fact]
    public async Task Items_MovedByHand_Moved()
    {
        // Act
        var (lines, _) = await ResolveAsync("Z4");

        // Assert
        lines.Should().ContainSingle().Which.Should().StartWith("{\"n\":4,").And.EndWith("\"status\":\"moved\"}");
    }

    [Theory]
    [InlineData("Z5")]
    [InlineData("Z6")]
    public async Task Items_InDeletedItemsOr404_Deleted(string selector)
    {
        // Act
        var (lines, _) = await ResolveAsync(selector);

        // Assert
        lines.Should().ContainSingle().Which.Should().EndWith("\"status\":\"deleted\"}");
    }

    [Fact]
    public async Task Items_NumberNotInSidecar_Unknown_NoGraphRead()
    {
        // Act
        var (lines, _) = await ResolveAsync("Z9");

        // Assert
        lines.Should().Equal("""{"n":9,"status":"unknown"}""");
        _graph.Stub.Requests.Should().NotContain(r => r.Uri.ToString().Contains("/messages/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Items_FileOther_OneLinePerMessageId_EachOwnStatus()
    {
        // Act
        var (lines, _) = await ResolveAsync("Z7");

        // Assert: one line per mail — one move and one permission prompt each in the session; m20 was filed by hand
        lines.Should().Equal(
            """{"n":7,"kind":"move","group":"file-other","messageId":"m13","destination":"archive","subject":"Team offsite dates","senderName":"Erin Example","received":"2026-10-06T08:15:00Z","status":"ok"}""",
            """{"n":7,"kind":"move","group":"file-other","messageId":"d4","destination":"archive","subject":"RE: Moved thread","senderName":"Alice Example","received":"2026-10-04T05:00:00Z","status":"ok"}""",
            """{"n":7,"kind":"move","group":"file-other","messageId":"m20","destination":"archive","subject":"Filed by hand","senderName":"Fay Example","received":"2026-10-06T06:00:00Z","status":"moved"}""");
    }

    [Fact]
    public async Task Items_All_EveryItemInOrder_FileOtherExpanded()
    {
        // Act
        var (lines, _) = await ResolveAsync("all");

        // Assert
        lines.Select(l => l[..l.IndexOf(',', StringComparison.Ordinal)]).Should().Equal(
            "{\"n\":1", "{\"n\":2", "{\"n\":3", "{\"n\":4", "{\"n\":5", "{\"n\":6", "{\"n\":7", "{\"n\":7", "{\"n\":7");
    }

    [Fact]
    public async Task Items_GraphFailure_NoLines()
    {
        // Arrange
        _graph.Stub.Once("GET", "/messages/m13\\?", 403, File.ReadAllText(StubGraphHandler.GraphFixture("graph-forbidden.json")));

        // Act
        var (lines, failure) = await ResolveAsync("Z1,Z2");

        // Assert: the model acts on nothing
        lines.Should().BeEmpty();
        failure.Should().NotBeNull();
    }

    [Fact]
    public async Task Items_OneGraphReadPerItem_GetOnly()
    {
        // Act
        await ResolveAsync("Z1,Z2,Z4");

        // Assert: the folder list once (its top level; the reader also walks child folders), then one read per item; nothing but GET (and the token)
        var graph = _graph.Stub.Requests.Where(r => r.Uri.Host == "graph.microsoft.com").ToList();
        graph.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        graph.Count(r => r.Uri.AbsolutePath.EndsWith("/mailFolders", StringComparison.Ordinal)).Should().Be(1);
        graph.Count(r => r.Uri.ToString().Contains("/messages/", StringComparison.Ordinal)).Should().Be(3);
    }
}
