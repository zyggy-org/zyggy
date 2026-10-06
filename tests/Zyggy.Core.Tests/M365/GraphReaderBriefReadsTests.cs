using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 35 Step 4: the three Graph reads of the brief's pre-pass — GET only, under <c>/users/&lt;mailbox&gt;</c>, never <c>/me</c>.</summary>
public sealed class GraphReaderBriefReadsTests : IDisposable
{
    private readonly PrepassFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task InboxSince_QueryExactAscendingTopSelect()
    {
        // Act
        var read = await _f.Graph.Reader.InboxSinceAsync("AQMkInbox0001", "2026-10-06T04:30:00Z", 60, TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value!.Select(m => m.Id).Should().Equal("m10", "m11", "m12", "m13");
        read.Value![0].Should().Be(new InboxMessage("m10", "Invoice 2026-41 due 2026-10-10", "Carol Example", "carol@example.org", "2026-10-06T06:12:00Z", "c10", true));
        var request = _f.Graph.Stub.To("mailFolders/AQMkInbox0001/messages").Should().ContainSingle().Subject;
        request.Uri.PathAndQuery.Should().Be(
            "/v1.0/users/alice@acme.example/mailFolders/AQMkInbox0001/messages?$filter=receivedDateTime%20gt%202026-10-06T04:30:00Z&$orderby=receivedDateTime%20asc&$top=60&$select=id,subject,from,receivedDateTime,conversationId,hasAttachments");
    }

    [Fact]
    public async Task SentSince_FollowsNextLinkOnlyUnderGraph()
    {
        // Act
        var read = await _f.Graph.Reader.SentSinceAsync("2026-09-22T08:30:00Z", TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().BeNull();
        read.Value.Should().Equal(
            new SentMarker("c11", "2026-10-06T07:30:00Z"),
            new SentMarker("c12", "2026-10-06T07:00:00Z"),
            new SentMarker("c11", "2026-10-06T08:00:00Z"),
            new SentMarker("cOld", "2026-10-06T06:00:00Z"));
        _f.Graph.Stub.To("sentitems/messages").Should().HaveCount(2);
        _f.Graph.Stub.To("sentitems/messages").First().Uri.Query.Should().Be("?$filter=sentDateTime%20ge%202026-09-22T08:30:00Z&$select=conversationId,sentDateTime&$top=100");
    }

    [Fact]
    public async Task SentSince_NextLinkOutsideTheMailbox_FailsExitSix()
    {
        // Arrange
        _f.Graph.Stub.Once("GET", "sentitems/messages\\?\\$filter=sentDateTime", 200,
            """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/bob@acme.example/mailFolders/sentitems/messages?$skip=100"}""");

        // Act
        var read = await _f.Graph.Reader.SentSinceAsync("2026-09-22T08:30:00Z", TestContext.Current.CancellationToken);

        // Assert
        read.Failure.Should().Be(new GraphFailure(6, "Graph returned a next page outside the mailbox"));
        _f.Graph.Stub.To("bob@acme").Should().BeEmpty();
    }

    [Fact]
    public async Task MessageLocation_PresentAnd404()
    {
        // Act
        var present = await _f.Graph.Reader.MessageLocationAsync("d5", TestContext.Current.CancellationToken);
        var absent = await _f.Graph.Reader.MessageLocationAsync("d7", TestContext.Current.CancellationToken);

        // Assert
        present.Value.Should().Be(new MessageLocation(true, "d5", "AQMkDrafts0001", "cOld", "RE: Old thread", "Alice Example", "2026-10-05T05:00:00Z"));
        absent.Failure.Should().BeNull();
        absent.Value.Should().BeSameAs(MessageLocation.Absent);
    }

    [Fact]
    public async Task AllBriefReads_GetOnly_NeverMe()
    {
        // Act
        await _f.Graph.Reader.InboxSinceAsync("AQMkInbox0001", "2026-10-06T04:30:00Z", 60, TestContext.Current.CancellationToken);
        await _f.Graph.Reader.SentSinceAsync("2026-09-22T08:30:00Z", TestContext.Current.CancellationToken);
        await _f.Graph.Reader.MessageLocationAsync("d5", TestContext.Current.CancellationToken);

        // Assert
        _f.Graph.Stub.Requests.Where(r => r.Method != HttpMethod.Post).Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.StartsWith("/v1.0/users/alice@acme.example/", StringComparison.Ordinal));
        _f.Graph.Stub.Violations.Should().BeEmpty();
    }
}
