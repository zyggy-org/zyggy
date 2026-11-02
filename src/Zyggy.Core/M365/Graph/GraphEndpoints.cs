using System.Text.RegularExpressions;

namespace Zyggy.Core.M365.Graph;

/// <summary>The only file naming the Microsoft identity and Graph hosts (spec 33 AC-35, the source-hygiene test).</summary>
internal static partial class GraphEndpoints
{
    public const string Login = "https://login.microsoftonline.com";
    public const string Graph = "https://graph.microsoft.com/v1.0";
    public const string Scope = "https://graph.microsoft.com/.default";
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public static string Token(Guid tenantId) => $"{Login}/{tenantId:D}/oauth2/v2.0/token";

    /// <summary>
    /// Spec 41 (plan A15): whether <paramref name="uri"/> is where Graph's content redirect may send a OneDrive download — HTTPS on the
    /// tenant's personal-site host (<c>&lt;tenant&gt;-my</c> under SharePoint Online). Any other redirect target is refused.
    /// </summary>
    public static bool IsDownloadHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && DownloadHost().IsMatch(uri.Host);
    }

    [GeneratedRegex(@"\A[a-z0-9-]+-my\.sharepoint\.com\z", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadHost();
}
