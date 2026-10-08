using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.LinkedIn;

/// <summary>A tool call's answer: the text content, and whether it is an MCP error result.</summary>
internal sealed record ToolCallResult(bool IsError, string Text);

/// <summary>
/// The handler of the one consented tool <c>publish_post</c> (spec 36 AC-2..AC-5, AC-12, AC-20, AC-22, AC-23), with no SDK type. In
/// order (spec 36 Assumption 3): the arguments, publishing switched on, the local text checks, the token (absent, expired, scope), a
/// duplicate in the last 24 h — each before any request — then exactly one post, never retried. Every call appends exactly one action-log
/// row; a published post also leaves one <c>[observed]</c> fact, whose refusal never undoes the post. Never throws to the server.
/// </summary>
internal sealed class PublishPostTool(
    LinkedInConfiguration config,
    ISecretStore secrets,
    TenantId tenant,
    ILinkedInApi api,
    LinkedInActionLog log,
    SecretPatterns patterns,
    MemoryPaths memory,
    TimeZoneInfo zone,
    TimeProvider clock,
    TextWriter diagnostics)
{
    public const string ToolName = "publish_post";

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(24);

    public async Task<ToolCallResult> CallAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        PostArguments? post = null;
        var sent = false;
        try
        {
            if (!PostArguments.TryParse(arguments, out post, out var invalid))
            {
                return Finish(now, null, null, LinkedInFailure.Refused, invalid);
            }

            if (!config.PostEnabled)
            {
                return Finish(now, post, null, LinkedInFailure.Refused, "publishing switched off");
            }

            if (PostPolicy.Check(post.Text, config.PostMaxChars, patterns) is { } reason)
            {
                return Finish(now, post, null, LinkedInFailure.Refused, reason, keepText: !PostPolicy.IsContentRefusal(reason));
            }

            LinkedInToken? token;
            try
            {
                token = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CredentialRefusedException ex)
            {
                return Finish(now, post, null, LinkedInFailure.ConfigurationError, ex.Message);
            }

            if (token is null)
            {
                return Finish(now, post, null, LinkedInFailure.NotConnected, "say \"connect LinkedIn\"");
            }

            if (token.ExpiresAt <= now)
            {
                return Finish(now, post, null, LinkedInFailure.TokenExpired, Detail(LinkedInFailure.TokenExpired, null));
            }

            if (!token.HasScope(LinkedInToken.PostScope))
            {
                return Finish(now, post, null, LinkedInFailure.Forbidden, Detail(LinkedInFailure.Forbidden, null));
            }

            if (log.RecentOk(Sha256(post.Text), now - DuplicateWindow) is { } earlier)
            {
                var at = TimeZoneInfo.ConvertTime(earlier.Ts, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                return Finish(now, post, null, LinkedInFailure.Refused, $"duplicate of {earlier.Urn} posted {at}");
            }

            sent = true;
            var result = await api.CreatePostAsync(
                token.AccessToken, $"urn:li:person:{token.Sub}", LittleText.Escape(post.Text), post.Visibility, config.ApiVersion, cancellationToken).ConfigureAwait(false);
            if (result.Urn is not { } urn)
            {
                var failure = result.Failure ?? LinkedInFailure.OutcomeUnknown;
                return Finish(now, post, null, failure, Detail(failure, result.Detail));
            }

            var published = Finish(now, post, urn, null, null);
            return published with { Text = published.Text + RecordFact(now, urn, post) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Unexpected: the post may exist once the request was sent.
            return sent
                ? Finish(now, post, null, LinkedInFailure.OutcomeUnknown, Detail(LinkedInFailure.OutcomeUnknown, null))
                : Finish(now, post, null, LinkedInFailure.ConfigurationError, "unexpected " + ex.GetType().Name);
        }
    }

    /// <summary>The detail of a failure as the result and the row carry it, with its runbook hint (spec 36 AC-12).</summary>
    public string Detail(LinkedInFailure failure, string? detail) => failure switch
    {
        LinkedInFailure.TokenExpired => "reconnect LinkedIn — runbook \"LinkedIn token expired\"",
        LinkedInFailure.Forbidden => "reconnect LinkedIn — runbook \"Publish refused or failed\"",
        LinkedInFailure.VersionRetired => $"api_version {config.ApiVersion} retired — runbook \"LinkedIn API version retired\"",
        LinkedInFailure.RateLimited => "try again later",
        LinkedInFailure.Rejected => detail ?? "rejected by LinkedIn",
        LinkedInFailure.OutcomeUnknown => "the post may exist — check your profile before asking again",
        _ => detail ?? string.Empty,
    };

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<LinkedInToken?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var raw = await secrets.GetAsync(tenant, LinkedInSession.TokenName, cancellationToken).ConfigureAwait(false);
        if (raw is not { } bytes)
        {
            return null;
        }

        try
        {
            return LinkedInToken.TryParse(bytes.Span) ?? throw new CredentialRefusedException("linkedin: the token file is not a token file");
        }
        finally
        {
            LinkedInSession.Clear(bytes);
        }
    }

    // One row for every call; the result text is the row's status.
    private ToolCallResult Finish(DateTimeOffset now, PostArguments? post, string? urn, LinkedInFailure? failure, string? detail, bool keepText = true)
    {
        var status = failure is { } f ? $"{LinkedInFailureWire.Token(f)}: {detail}" : ActionRow.Ok;
        var text = urn is null ? status : $"published: {urn} — {LinkedInEndpoints.FeedUpdateUrl(urn)}";
        var row = new ActionRow(
            1,
            now,
            ToolName,
            urn,
            post?.Visibility.Wire() ?? string.Empty,
            post?.Characters ?? 0,
            post is null ? string.Empty : Sha256(post.Text),
            keepText ? post?.Text : null,
            status);
        try
        {
            log.Append(row);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.WriteLine($"linkedin: action log not written: {ex.GetType().Name}");
            text += "; action log not written";
        }

        return new ToolCallResult(failure is not null, text);
    }

    // The fact never undoes the post: a refusal or an IO error is one stderr line and a result suffix.
    private string RecordFact(DateTimeOffset now, string urn, PostArguments post)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        string? reason = FactValidator.Refusal(PostFact.Fact(post.Visibility, post.Text), patterns);
        if (reason is null)
        {
            try
            {
                PostFact.Write(memory, date, PostFact.Line(date, urn, post.Visibility, post.Text));
                return string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = "write failed (" + ex.GetType().Name + ")";
            }
        }

        diagnostics.WriteLine($"linkedin: fact not recorded: {reason}");
        return "; fact not recorded: " + reason;
    }
}
