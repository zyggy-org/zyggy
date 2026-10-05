namespace Zyggy.Core.M365.Graph;

/// <summary>The only file naming the Microsoft identity and Graph hosts (spec 33 AC-35, the source-hygiene test).</summary>
internal static class GraphEndpoints
{
    public const string Login = "https://login.microsoftonline.com";
    public const string Graph = "https://graph.microsoft.com/v1.0";
    public const string Scope = "https://graph.microsoft.com/.default";
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public static string Token(Guid tenantId) => $"{Login}/{tenantId:D}/oauth2/v2.0/token";
}
