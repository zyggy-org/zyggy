namespace Zyggy.Core.LinkedIn;

/// <summary>The token exchange's answer: the token with its lifetime and scope, or LinkedIn's error code (≤ 40 of <c>[A-Za-z0-9_.-]</c>).</summary>
internal sealed record TokenExchangeResult(string? AccessToken, int? ExpiresInSeconds, string? Scope, string? Error);

/// <summary>The user info's answer: the member's <c>sub</c> and name, or an error code.</summary>
internal sealed record UserInfoResult(string? Sub, string? Name, string? Error);

/// <summary>A post's answer: its URN, or one closed failure with a detail (LinkedIn's message for <see cref="LinkedInFailure.Rejected"/>).</summary>
internal sealed record CreatePostResult(string? Urn, LinkedInFailure? Failure, string? Detail);

/// <summary>Where an image's bytes go and the image's URN, from <c>initializeUpload</c> (plan 36b L1).</summary>
internal sealed record ImageUploadTicket(string UploadUrl, string ImageUrn);

/// <summary><c>initializeUpload</c>'s answer: the ticket, or one reason (plan 36b D5).</summary>
internal sealed record ImageUploadStart(ImageUploadTicket? Ticket, string? Error);

/// <summary>The image a post carries: its URN and an optional alt text (plan 36b L5).</summary>
internal sealed record PostMedia(string ImageUrn, string? AltText);

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

    /// <summary>Sends one <c>POST /rest/posts</c>, with <paramref name="media"/> when given; never retried. The caller's own cancellation throws.</summary>
    Task<CreatePostResult> CreatePostAsync(
        string accessToken,
        string authorUrn,
        string commentary,
        PostVisibility visibility,
        string apiVersion,
        CancellationToken cancellationToken,
        PostMedia? media = null);

    /// <summary>Registers one image upload owned by <paramref name="ownerUrn"/> (plan 36b L1); never retried.</summary>
    Task<ImageUploadStart> InitializeImageUploadAsync(string accessToken, string ownerUrn, string apiVersion, CancellationToken cancellationToken);

    /// <summary>PUTs exactly <paramref name="bytes"/> to an allowed upload address (plan 36b L2, D5); <see langword="null"/> when stored, else the reason.</summary>
    Task<string?> UploadImageAsync(string accessToken, string uploadUrl, ReadOnlyMemory<byte> bytes, string mediaType, CancellationToken cancellationToken);
}
