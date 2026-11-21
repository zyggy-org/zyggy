using System.Text.Json;

using Zyggy.Core.Supervision;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.M365;

namespace Zyggy.Core.Tests.Supervision;

// AC-29: the mail implementation of the notifier seam posts one message to the owner's own mailbox, with the notice's subject and
// lines and nothing else; a refused or unreachable send is an exception the caller retries.
public sealed class OwnerViewMailTests : IDisposable
{
    private const string Mailbox = "alice@acme.example";

    private const string SendRoute = "/v1\\.0/users/[^/]+/sendMail$";

    private static readonly OwnerNotice Notice = new("Zyggy: 2 item(s) wait for you", ["question, home-laptop, shop, 01JDQ0000000000000000000AA, 5 min", "mandate, work-laptop, 01JDQ0000000000000000000BB, 2 h"]);

    private readonly GraphFixture _graph = new();

    public OwnerViewMailTests() => _graph.Stub.AllowOwnerMail(Mailbox);

    public void Dispose()
    {
        _graph.Stub.Violations.Should().BeEmpty();
        _graph.Dispose();
    }

    private MailNotifier Sut() => new(_graph.Tokens, _graph.Http, Mailbox);

    // AC-29
    [Fact]
    public async Task NotifyAsync_Accepted_PostsOneMessageToTheOwnersOwnMailbox()
    {
        // Act
        await Sut().NotifyAsync(Notice, CancellationToken.None);

        // Assert
        RecordedRequest send = _graph.Stub.To(SendRoute).Should().ContainSingle().Subject;
        using JsonDocument body = JsonDocument.Parse(send.Body!);
        JsonElement message = body.RootElement.GetProperty("message");
        new
        {
            Url = send.Uri.ToString(),
            Auth = send.Headers["Authorization"].StartsWith("Bearer ", StringComparison.Ordinal),
            Subject = message.GetProperty("subject").GetString(),
            ContentType = message.GetProperty("body").GetProperty("contentType").GetString(),
            Content = message.GetProperty("body").GetProperty("content").GetString(),
            To = message.GetProperty("toRecipients").EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString()).ToArray(),
            Saved = body.RootElement.GetProperty("saveToSentItems").GetBoolean(),
        }.Should().BeEquivalentTo(new
        {
            Url = "https://graph.microsoft.com/v1.0/users/" + Mailbox + "/sendMail",
            Auth = true,
            Subject = Notice.Subject,
            ContentType = "Text",
            Content = string.Join('\n', Notice.Lines) + "\n",
            To = new[] { Mailbox },
            Saved = false,
        });
    }

    // AC-29
    [Fact]
    public async Task NotifyAsync_OnlyTheNoticeTravels_NoOtherProperty()
    {
        // Act
        await Sut().NotifyAsync(Notice, CancellationToken.None);

        // Assert
        using JsonDocument body = JsonDocument.Parse(_graph.Stub.To(SendRoute).Single().Body!);
        body.RootElement.GetProperty("message").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("subject", "body", "toRecipients");
    }

    // AC-29
    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task NotifyAsync_Refused_ThrowsForTheCallerToRetry(int status)
    {
        // Arrange
        _graph.Stub.Once("POST", SendRoute, status, "{\"error\":{\"code\":\"ErrorAccessDenied\"}}");

        // Act
        Func<Task> act = () => Sut().NotifyAsync(Notice, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + "*");
    }

    // AC-29
    [Fact]
    public async Task NotifyAsync_UnauthorizedOnce_MintsAgainAndSends()
    {
        // Arrange
        _graph.Stub.Once("POST", SendRoute, 401);

        // Act
        await Sut().NotifyAsync(Notice, CancellationToken.None);

        // Assert
        _graph.Stub.To(SendRoute).Should().HaveCount(2);
    }

    // AC-29
    [Fact]
    public async Task NotifyAsync_TokenRefused_ThrowsAndSendsNothing()
    {
        // Arrange
        _graph.Stub.Once("POST", "/oauth2/v2\\.0/token$", 400, File.ReadAllText(Infrastructure.StubGraphHandler.GraphFixture("token-invalid-client.json")));

        // Act
        Func<Task> act = () => Sut().NotifyAsync(Notice, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _graph.Stub.To(SendRoute).Should().BeEmpty();
    }

    // AC-29: a mail that cannot be configured (no m365.json, no key) is a failed send, never a crash at start.
    [Fact]
    public async Task LazyNotifier_NotConfigured_ThrowsWithTheReasonEachTime()
    {
        // Arrange
        var calls = 0;
        var sut = new LazyNotifier(() =>
        {
            calls++;
            return (null, "m365.json not found");
        });

        // Act
        Func<Task> act = () => sut.NotifyAsync(Notice, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*m365.json not found*");
        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(2);
    }
}
