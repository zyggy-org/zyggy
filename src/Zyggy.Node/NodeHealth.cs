using System.Text.Json.Serialization;

namespace Zyggy.Node;

/// <summary>
/// The body of <c>GET /health</c> (founding spec §11, verbatim field list; spec 07): serialised camelCase with nulls written and
/// timestamps as <c>YYYY-MM-DDTHH:MM:SSZ</c>. There is no verdict field (spec 07 ex-Q9): <c>zyggy status</c> defines "green" from these.
/// </summary>
/// <param name="Version">The informational version of this binary.</param>
/// <param name="Tenant">The tenant from <c>node.json</c>.</param>
/// <param name="Machine">The machine from <c>node.json</c>.</param>
/// <param name="LastPollUtc">When the doorbell was last rung.</param>
/// <param name="LastEtag">The ETag the next poll will send.</param>
/// <param name="LastPullUtc">When the checkout was last pulled.</param>
/// <param name="BusHeadSha">The checkout's <c>HEAD</c> after the last pull.</param>
/// <param name="JobsRunning">Admitted jobs currently running.</param>
/// <param name="JobsClaimedByMe">Files in this machine's <c>jobs/claimed/</c> after the last pull.</param>
/// <param name="LastReportUtc">When the last report was committed.</param>
/// <param name="RateLimitRemaining">The bus host's remaining request budget as last seen.</param>
/// <param name="BackoffUntilUtc">Until when the poller backs off, if it does.</param>
/// <param name="ClaudeAuthOk">The login probe's last verdict; <see langword="null"/> until the first probe (A7-4).</param>
/// <param name="AgentCoreSha">The pinned <c>zyggy-core</c> SHA; <see langword="null"/> until deliverable 15.</param>
public sealed record NodeHealth(
    string Version,
    string Tenant,
    string Machine,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? LastPollUtc,
    string? LastEtag,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? LastPullUtc,
    string? BusHeadSha,
    int JobsRunning,
    int JobsClaimedByMe,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? LastReportUtc,
    int? RateLimitRemaining,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset? BackoffUntilUtc,
    bool? ClaudeAuthOk,
    string? AgentCoreSha);
