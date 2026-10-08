namespace Zyggy.Core.LinkedIn;

/// <summary>The token exchange's answer: the token with its lifetime and scope, or LinkedIn's error code (≤ 40 of <c>[A-Za-z0-9_.-]</c>).</summary>
internal sealed record TokenExchangeResult(string? AccessToken, int? ExpiresInSeconds, string? Scope, string? Error);

/// <summary>The user info's answer: the member's <c>sub</c> and name, or an error code.</summary>
internal sealed record UserInfoResult(string? Sub, string? Name, string? Error);

/// <summary>A post's answer: its URN, or one closed failure with a detail (LinkedIn's message for <see cref="LinkedInFailure.Rejected"/>).</summary>
internal sealed record CreatePostResult(string? Urn, LinkedInFailure? Failure, string? Detail);

/// <summary>
/// The LinkedIn adapter (spec 36, W33-5 extended): the only code that talks to LinkedIn. Internal, one implementation, not a seam.
/// The client secret goes only into a form body, the token only into an <c>Authorization</c> header.
/// </summary>
internal interface ILinkedInApi
{
    /// <summary>Exchanges an authorization code once for an access token.</summary>
    Task<TokenExchangeResult> ExchangeCodeAsync(string code, string clientId, ReadOnlyMemory<byte> clientSecret, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Reads the member's <c>sub</c> and name once.</summary>
    Task<UserInfoResult> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>Sends one <c>POST /rest/posts</c>; never retried. The caller's own cancellation throws.</summary>
    Task<CreatePostResult> CreatePostAsync(
        string accessToken, string authorUrn, string commentary, PostVisibility visibility, string apiVersion, CancellationToken cancellationToken);
}
