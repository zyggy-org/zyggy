using System.Globalization;
using System.Text;

namespace Zyggy.Core.Bus.Probe;

/// <summary>The reachability matrix of one machine (deliverable 04, founding spec §1): the four rows and what git and the OS say about proxies, TLS and credentials.</summary>
/// <param name="Rows">The <c>api</c>, <c>fetch</c>, <c>push</c> and <c>ssh</c> rows, in that order.</param>
/// <param name="GitProxy">git's <c>http.proxy</c> (user information stripped) or <c>none</c>.</param>
/// <param name="GitSslBackend">git's <c>http.sslBackend</c> or <c>none</c>.</param>
/// <param name="CredentialHelper">git's <c>credential.helper</c> or <c>none</c>.</param>
/// <param name="SystemProxy">The OS proxy the API request goes through, as <c>host:port</c>, or <c>none</c>.</param>
public sealed record ReachReport(IReadOnlyList<ReachResult> Rows, string GitProxy, string GitSslBackend, string CredentialHelper, string SystemProxy)
{
    /// <summary>Gets the exit code: 0 when <c>api</c>, <c>fetch</c> and <c>push</c> are <c>ok</c>; otherwise 6. The <c>ssh</c> row never changes it.</summary>
    public int ExitCode => Rows.Where(r => r.Check is "api" or "fetch" or "push").All(r => r.Status == ReachStatus.Ok) ? 0 : 6;

    /// <summary>Renders the header lines and the Markdown table <c>check | endpoint | result | detail | proxy</c>.</summary>
    /// <returns>The text, ending with a newline.</returns>
    public string RenderMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"git http.proxy: {GitProxy}; http.sslBackend: {GitSslBackend}; credential.helper: {CredentialHelper}; system proxy for the API: {SystemProxy}\n\n");
        sb.Append("| check | endpoint | result | detail | proxy |\n");
        sb.Append("|-------|----------|--------|--------|-------|\n");
        foreach (ReachResult row in Rows)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {row.Check} | {row.Endpoint} | {ReachStatusWire.ToWire(row.Status)} | {Cell(row.Detail)} | {row.Proxy} |\n");
        }

        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}
