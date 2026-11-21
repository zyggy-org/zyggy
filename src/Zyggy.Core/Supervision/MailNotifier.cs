using System.Globalization;
using System.Text;
using System.Text.Json;

using Zyggy.Core.M365.Graph;

namespace Zyggy.Core.Supervision;

/// <summary>
/// The first <see cref="INotifier"/> (deliverable 18, D7; founding spec §8 m365 rule as amended): one Graph <c>sendMail</c> from the
/// owner's own mailbox to that same mailbox, plain text, no copy in Sent Items. The message carries the notice's subject and lines and
/// nothing else; no model reaches this class, and the recipient is the mailbox the Graph certificate is scoped to. A refused or
/// unreachable send throws <see cref="InvalidOperationException"/> with the status or Graph error code, never the body or a token.
/// </summary>
internal sealed class MailNotifier(ITokenSource tokens, GraphHttp http, string mailbox) : INotifier
{
    private string? _token;

    public async Task NotifyAsync(OwnerNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        string url = $"{GraphEndpoints.Graph}/users/{mailbox}/sendMail";
        string json = Message(notice, mailbox);
        _token ??= await MintAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            string token = _token;
            GraphHttpResult result = await http.SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    return request;
                },
                _ => false,
                ct).ConfigureAwait(false);
            if (result.Response is not { } response)
            {
                throw new InvalidOperationException("owner mail not sent: " + result.Failure);
            }

            if (response.Status == 401 && attempt == 0)
            {
                _token = await MintAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (response.Status is not 202)
            {
                if (response.Status == 401)
                {
                    _token = null;
                }

                throw new InvalidOperationException(
                    "owner mail not sent (" + response.Status.ToString(CultureInfo.InvariantCulture) + "): " + GraphHttp.Error(response.Status, response.Body));
            }

            return;
        }
    }

    private static string Message(OwnerNotice notice, string recipient)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("message");
            writer.WriteString("subject", notice.Subject);
            writer.WriteStartObject("body");
            writer.WriteString("contentType", "Text");
            writer.WriteString("content", string.Concat(notice.Lines.Select(l => l + "\n")));
            writer.WriteEndObject();
            writer.WriteStartArray("toRecipients");
            writer.WriteStartObject();
            writer.WriteStartObject("emailAddress");
            writer.WriteString("address", recipient);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteBoolean("saveToSentItems", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<string> MintAsync(CancellationToken ct)
    {
        TokenResult minted = await tokens.MintAsync(new TokenRequest(UseNewKey: false, AssertionAlgorithm.Ps256), ct).ConfigureAwait(false);
        return minted.AccessToken
            ?? throw new InvalidOperationException("owner mail not sent: no token (" + (minted.Error ?? minted.ExitCode.ToString(CultureInfo.InvariantCulture)) + ")");
    }
}

/// <summary>
/// The node's <see cref="INotifier"/>: opens the mail edge on the first send, not at start, so a Central without an M365 instance runs
/// as before. An edge that did not open is tried again at the next send, and the send fails with the reason.
/// </summary>
internal sealed class LazyNotifier(Func<(INotifier? Notifier, string? Error)> open) : INotifier
{
    private INotifier? _notifier;

    public Task NotifyAsync(OwnerNotice notice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_notifier is null)
        {
            (INotifier? notifier, string? error) = open();
            _notifier = notifier ?? throw new InvalidOperationException("owner mail not configured: " + (error ?? "unknown"));
        }

        return _notifier.NotifyAsync(notice, ct);
    }
}
