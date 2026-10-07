namespace Zyggy.Core.LinkedIn;

/// <summary>The only file naming LinkedIn's hosts (spec 36 AC-9, the source-hygiene test).</summary>
internal static class LinkedInEndpoints
{
    public const string AuthorizationUrl = "https://www.linkedin.com/oauth/v2/authorization";
    public const string Scopes = "openid profile w_member_social";
}
