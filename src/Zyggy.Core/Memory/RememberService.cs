using System.Globalization;

namespace Zyggy.Core.Memory;

/// <summary>A validated fact to keep: tag, scope hint (<c>""</c>, <c>" (machine)"</c>, <c>" (project:&lt;name&gt;)"</c>), source and fact, each one line.</summary>
internal sealed record RememberRequest(string Tag, string Hint, string Source, string Fact);

/// <summary>The outcome: the pattern that refused the fact or its source, or the file and the line written.</summary>
internal sealed record RememberOutcome(string? RefusedBy, string? Path, string? Line);

/// <summary>
/// Keeps an owner-stated fact as one line in <c>inbox/remember-&lt;local date&gt;.md</c> (the template's <c>remember.sh</c> after its
/// argument checks): refuse a secret-shaped fact or source by pattern name, build <c>- [&lt;tag&gt;] &lt;date&gt;&lt;hint&gt;&lt;provenance&gt;: &lt;fact&gt;</c>,
/// append through <see cref="FactLineWriter"/>.
/// </summary>
internal sealed class RememberService(MemoryPaths paths, TimeZoneInfo timeZone, TimeProvider clock, SecretPatterns patterns)
{
    public RememberOutcome Remember(RememberRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var text in new[] { request.Fact, request.Source })
        {
            if (patterns.TryMatch(text, out var name))
            {
                return new RememberOutcome(name, null, null);
            }
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone).DateTime);
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var provenance = request.Source.Length == 0 ? string.Empty : $" [{request.Source}]";
        var line = $"- [{request.Tag}] {date}{request.Hint}{provenance}: {request.Fact}";
        var path = paths.InboxFile($"remember-{date}.md");
        FactLineWriter.Append(path, $"remember {date}", $"facts stated by the owner on {date} (remember skill)", today, [line]);
        return new RememberOutcome(null, path, line);
    }
}
