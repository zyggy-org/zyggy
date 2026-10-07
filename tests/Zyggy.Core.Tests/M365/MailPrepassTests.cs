using Zyggy.Core.Brief;
using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 35 AC-21 (answered mails), AC-22 (earlier reply drafts become discard items): the brief's pre-pass over the stubbed Graph.</summary>
public sealed class MailPrepassTests : IDisposable
{
    private readonly PrepassFixture _f = new();

    public void Dispose() => _f.Dispose();

    private Task<(PrepassResult? Result, GraphFailure? Failure)> RunAsync(BriefSettings? settings = null) =>
        _f.Prepass(settings).RunAsync("AQMkInbox0001", "AQMkDrafts0001", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Run_ConversationAnsweredLater_MarkedAnsweredFirstSentTime()
    {
        // Act
        var (result, failure) = await RunAsync();

        // Assert
        failure.Should().BeNull();
        result!.Watermark.Should().Be("2026-10-06T04:30:00Z");
        result.Mail.Select(m => (m.Message.Id, m.Answered)).Should().Equal(("m10", null), ("m11", "09:30"), ("m12", null), ("m13", null));
    }

    [Fact]
    public async Task Run_SentBeforeReceived_NotAnswered()
    {
        // Act: c12 has a sent mail at 07:00Z, before m12 was received at 07:40Z
        var (result, _) = await RunAsync();

        // Assert
        result!.Mail.Single(m => m.Message.Id == "m12").Answered.Should().BeNull();
    }

    [Fact]
    public async Task Run_OneSentItemsQuery_FromKeepDaysBeforeNow()
    {
        // Act
        await RunAsync();

        // Assert: one query (plus its second page); since = min(oldest listed 06:12Z today, now − 14 days)
        var sent = _f.Graph.Stub.To("sentitems/messages").ToList();
        sent.Should().HaveCount(2);
        sent[0].Uri.Query.Should().Contain("$filter=sentDateTime%20ge%202026-09-22T08:30:00Z");
    }

    [Fact]
    public async Task Run_OldestListedBeforeKeepWindow_SentQueryFromThatMail()
    {
        // Arrange: a listing whose oldest mail is 20 days old
        _f.Graph.Stub.Once("GET", "mailFolders/AQMkInbox0001/messages", 200,
            """{"value":[{"id":"m01","subject":"Old","from":{"emailAddress":{"name":"Old Sender","address":"o@example.org"}},"receivedDateTime":"2026-09-16T10:00:00Z","conversationId":"c01","hasAttachments":false}]}""");

        // Act
        await RunAsync();

        // Assert
        _f.Graph.Stub.To("sentitems/messages").First().Uri.Query.Should().Contain("$filter=sentDateTime%20ge%202026-09-16T10:00:00Z");
    }

    [Fact]
    public async Task Run_EarlierReplyDraftStillInDraftsAnswered_DiscardItem()
    {
        // Arrange: yesterday's receipt with the brief draft and two reply drafts
        _f.WriteReceipt("2026-10-05", ("d0", "brief"), ("d5", "reply"), ("d6", "reply"));

        // Act
        var (result, failure) = await RunAsync();

        // Assert
        failure.Should().BeNull();
        result!.Discard.Should().Equal(new DiscardItem("d5", "RE: Old thread", "08:00"));
        _f.Graph.Stub.To("messages/d0").Should().BeEmpty("a brief draft is never a discard candidate");
    }

    [Fact]
    public async Task Run_EarlierReplyDraftMovedSentOrNotAnswered_NoItem()
    {
        // Arrange: d4 is in the Inbox now, d7 is gone (404), d6 is in Drafts with no later sent mail
        _f.WriteReceipt("2026-10-04", ("d4", "reply"), ("d7", "reply"));
        _f.WriteReceipt("2026-10-05", ("d6", "reply"));

        // Act
        var (result, failure) = await RunAsync();

        // Assert
        failure.Should().BeNull();
        result!.Discard.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_ReceiptsOlderThanKeepDays_Ignored()
    {
        // Arrange
        _f.WriteReceipt("2026-09-20", ("d9", "reply"));

        // Act
        var (result, _) = await RunAsync();

        // Assert
        result!.Discard.Should().BeEmpty();
        _f.Graph.Stub.To("messages/d9").Should().BeEmpty();
    }

    [Fact]
    public async Task Run_NoDraftsFolder_NoLocationReads()
    {
        // Arrange
        _f.WriteReceipt("2026-10-05", ("d5", "reply"));

        // Act
        var (result, _) = await _f.Prepass().RunAsync("AQMkInbox0001", null, TestContext.Current.CancellationToken);

        // Assert
        result!.Discard.Should().BeEmpty();
        _f.Graph.Stub.To("messages/d5").Should().BeEmpty();
    }

    [Fact]
    public async Task Run_GraphFailure_PropagatesExitSix()
    {
        // Arrange
        _f.Graph.Stub.Once("GET", "mailFolders/AQMkInbox0001/messages", 500, """{"error":{"code":"InternalServerError","message":"x"}}""");

        // Act
        var (result, failure) = await RunAsync();

        // Assert
        result.Should().BeNull();
        failure!.ExitCode.Should().Be(6);
    }
}
