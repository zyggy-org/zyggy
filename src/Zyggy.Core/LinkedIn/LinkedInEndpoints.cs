using System.Globalization;
using System.Text.RegularExpressions;

namespace Zyggy.Core.LinkedIn;

/// <summary>The request addresses of one run: LinkedIn's, or all on the loopback stand-in of the integration tests.</summary>
/// <param name="AccessTokenUrl">The token exchange.</param>
/// <param name="UserInfoUrl">The OpenID Connect user info.</param>
/// <param name="PostsUrl">The Posts API.</param>
/// <param name="ImagesUrl">The Images API's <c>initializeUpload</c> action (plan 36b L1).</param>
/// <param name="Loopback">The loopback base when overridden, else <see langword="null"/>.</param>
internal sealed record LinkedInRoutes(string AccessTokenUrl, string UserInfoUrl, string PostsUrl, string ImagesUrl, Uri? Loopback);

/// <summary>The outcome of <see cref="LinkedInEndpoints.Resolve"/>: the routes, or the configuration error (exit 3).</summary>
internal sealed record EndpointsLoad(LinkedInRoutes? Routes, string? Error);

/// <summary>
/// The only file naming LinkedIn's hosts (spec 36 AC-9, the source-hygiene test). <c>ZYGGY_LINKEDIN_API_BASE</c> moves both request
/// hosts to the integration tests' stand-in, and only when it is exactly <c>http://127.0.0.1:&lt;port&gt;</c> (AC-15); the printed sign-in
/// link and the post link never move.
/// </summary>
internal static partial class LinkedInEndpoints
{
    public const string Www = "https://www.linkedin.com";
    public const string Api = "https://api.linkedin.com";
    public const string AuthorizationUrl = Www + "/oauth/v2/authorization";
    public const string Scopes = "openid profile w_member_social";

    private const string AccessTokenPath = "/oauth/v2/accessToken";
    private const string UserInfoPath = "/v2/userinfo";
    private const string PostsPath = "/rest/posts";
    private const string ImagesPath = "/rest/images?action=initializeUpload";
    private const string UploadPath = "/dms-uploads/";

    /// <summary>
    /// Whether an upload address LinkedIn handed out may receive the image (plan 36b D5, D10): HTTPS on <c>www.linkedin.com</c> under
    /// <c>/dms-uploads/</c>, or — only when the routes are the loopback stand-in — that stand-in's own <c>/dms-uploads/</c>.
    /// </summary>
    public static bool IsAllowedUploadUrl(string address, LinkedInRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.AbsolutePath.StartsWith(UploadPath, StringComparison.Ordinal) || uri.UserInfo.Length != 0)
        {
            return false;
        }

        return routes.Loopback is { } loopback
            ? uri.Scheme == loopback.Scheme && uri.Host == loopback.Host && uri.Port == loopback.Port
            : uri.Scheme == Uri.UriSchemeHttps && uri.Host == "www.linkedin.com" && uri.IsDefaultPort;
    }

    /// <summary>Where a published post is seen.</summary>
    public static string FeedUpdateUrl(string urn) => $"{Www}/feed/update/{urn}/";

    public static EndpointsLoad Resolve(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.TryGetValue("ZYGGY_LINKEDIN_API_BASE", out var value) || string.IsNullOrEmpty(value))
        {
            return new EndpointsLoad(new LinkedInRoutes(Www + AccessTokenPath, Api + UserInfoPath, Api + PostsPath, Api + ImagesPath, null), null);
        }

        var match = LoopbackBase().Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            return new EndpointsLoad(null, "configuration error: ZYGGY_LINKEDIN_API_BASE must be a loopback address");
        }

        return new EndpointsLoad(new LinkedInRoutes(value + AccessTokenPath, value + UserInfoPath, value + PostsPath, value + ImagesPath, new Uri(value)), null);
    }

    [GeneratedRegex(@"\Ahttp://127\.0\.0\.1:([0-9]{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex LoopbackBase();
}
