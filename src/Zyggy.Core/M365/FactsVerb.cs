using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 facts --kind brief|mail-backfill|files-backfill --source &lt;tag&gt; [--max &lt;n&gt;] &lt; lines</c>: the template's
/// <c>facts.sh</c> (spec 33 AC-10, AC-12). Candidate facts on stdin become <c>- [observed] &lt;date&gt; [&lt;source&gt;]: &lt;fact&gt;</c>
/// lines in <c>inbox/m365-&lt;kind&gt;-&lt;date&gt;.md</c>; refusals are counted by reason, never echoed. Stdout stays empty.
/// Exit 0 · 3 configuration · 4 usage · 5 <c>--max</c> reached. Accepts <c>ZYGGY_HOOKS=off</c>.
/// </summary>
internal sealed partial class FactsVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "facts: ";
    private const string Usage = " (usage: zyggy m365 facts --kind brief|mail-backfill|files-backfill --source <tag> [--max <n>] < lines)";
    private const int SourceCharacters = 200;
    private const int MaxCap = 99999;

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        // 1. The arguments, before anything else.
        var parsed = ParseArguments(args);
        if (parsed.Error is not null)
        {
            return await UsageAsync(io, parsed.Error).ConfigureAwait(false);
        }

        var source = parsed.Source!;
        if (source.Any(c => c < ' ' || c == '\u007f'))
        {
            return await UsageAsync(io, "--source must be one line without control characters").ConfigureAwait(false);
        }

        if (source.Contains('[', StringComparison.Ordinal) || source.Contains(']', StringComparison.Ordinal))
        {
            return await UsageAsync(io, "--source must not contain [ or ]").ConfigureAwait(false);
        }

        source = TextCollapse.Line(source);
        if (source.Length == 0)
        {
            return await UsageAsync(io, "--source is empty").ConfigureAwait(false);
        }

        if (TextCollapse.CharCount(source) > SourceCharacters)
        {
            return await UsageAsync(io, $"--source is longer than {SourceCharacters} characters").ConfigureAwait(false);
        }

        if (FactValidator.HoldsEmail(source))
        {
            return await UsageAsync(io, "--source holds an e-mail address").ConfigureAwait(false);
        }

        if (FactValidator.HoldsUrl(source))
        {
            return await UsageAsync(io, "--source holds a URL").ConfigureAwait(false);
        }

        var location = SecretPatternsLocation.Resolve(context.Environment);
        var load = location is null
            ? new SecretPatternsLoad(null, "ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)")
            : SecretPatterns.Load(location);
        if (load.Patterns is not { } patterns)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + load.Error + "\n").ConfigureAwait(false);
            return 3;
        }

        if (patterns.TryMatch(source, out var sourcePattern))
        {
            return await UsageAsync(io, "--source matches secret pattern " + sourcePattern).ConfigureAwait(false);
        }

        int? max = null;
        if (parsed.Max is not null)
        {
            if (!MaxValue().IsMatch(parsed.Max) || int.Parse(parsed.Max, CultureInfo.InvariantCulture) > MaxCap)
            {
                return await UsageAsync(io, $"--max must be 1..{MaxCap}").ConfigureAwait(false);
            }

            max = int.Parse(parsed.Max, CultureInfo.InvariantCulture);
        }

        // 2. Configuration.
        var memory = MemoryEnvironment.Resolve(context.Environment, context.FindTimeZone);
        if (memory.Error is not null)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + memory.Error + "\n").ConfigureAwait(false);
            return 3;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Clock.GetUtcNow(), memory.TimeZone!).DateTime);
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var file = memory.Paths!.InboxFile($"m365-{parsed.Kind}-{date}.md");

        // 3. Validation.
        var seen = new HashSet<string>(KnownFacts(file), StringComparer.Ordinal);
        var refusedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var refusedOrder = new List<string>();
        var accepted = new List<string>();
        int refused = 0, duplicates = 0, cut = 0;
        var capped = false;
        var input = await io.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        foreach (var raw in Lines(input))
        {
            var fact = FactValidator.Clean(raw);
            if (FactValidator.Refusal(fact, patterns) is { } reason)
            {
                refused++;
                if (refusedBy.TryAdd(reason, 1))
                {
                    refusedOrder.Add(reason);
                }
                else
                {
                    refusedBy[reason]++;
                }

                continue;
            }

            var isCut = TextCollapse.CharCount(fact) > FactValidator.MaxCharacters;
            if (isCut)
            {
                fact = FactValidator.Cut(fact);
            }

            if (seen.Contains(fact))
            {
                duplicates++;
                continue;
            }

            if (max is { } cap && accepted.Count >= cap)
            {
                capped = true;
                break;
            }

            seen.Add(fact);
            cut += isCut ? 1 : 0;
            accepted.Add($"- [observed] {date} [{source}]: {fact}");
        }

        // 4. The memory file and the counts line.
        if (accepted.Count > 0)
        {
            FactLineWriter.Append(file, $"m365 {parsed.Kind} {date}", $"facts observed by the m365 {parsed.Kind} run on {date} (facts.sh)", today, accepted);
        }

        var details = FactValidator.Reasons.Where(refusedBy.ContainsKey)
            .Concat(refusedOrder.Where(r => r.StartsWith("secret pattern ", StringComparison.Ordinal)))
            .Select(r => $"{refusedBy[r]} {r}")
            .ToList();
        var detailText = details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})";
        var noun = duplicates == 1 ? "duplicate" : "duplicates";
        await io.Error.WriteAsync(
            $"{Prefix}{accepted.Count} accepted, {refused} refused{detailText}, {duplicates} {noun} dropped, {cut} cut to {FactValidator.MaxCharacters}\n").ConfigureAwait(false);
        if (capped)
        {
            await io.Error.WriteAsync($"{Prefix}cap {max} reached\n").ConfigureAwait(false);
            return 5;
        }

        return 0;
    }

    private static async Task<int> UsageAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + message + Usage + "\n").ConfigureAwait(false);
        return 4;
    }

    private static (string? Kind, string? Source, string? Max, string? Error) ParseArguments(IReadOnlyList<string> args)
    {
        string? kind = null;
        string? source = null;
        string? max = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            var name = args[i];
            if (name is not ("--kind" or "--source" or "--max"))
            {
                return (null, null, null, $"unexpected argument '{ShellText.Prefix(name, 40)}'");
            }

            if (i + 1 >= args.Count)
            {
                return (null, null, null, $"{name} needs a value");
            }

            var value = args[i + 1];
            switch (name)
            {
                case "--kind" when !string.IsNullOrEmpty(kind):
                case "--source" when source is not null:
                case "--max" when !string.IsNullOrEmpty(max):
                    return (null, null, null, $"{name} given twice");
                case "--kind":
                    kind = value;
                    break;
                case "--source":
                    source = value;
                    break;
                default:
                    max = value;
                    break;
            }
        }

        if (string.IsNullOrEmpty(kind))
        {
            return (null, null, null, "--kind is required");
        }

        if (kind is not ("brief" or "mail-backfill" or "files-backfill"))
        {
            return (null, null, null, "--kind must be brief, mail-backfill or files-backfill");
        }

        return source is null
            ? (null, null, null, "--source is required")
            : (kind, source, string.IsNullOrEmpty(max) ? null : max, null);
    }

    // `while IFS= read -r raw || [ -n "$raw" ]`: lines split on LF, a last line without one included.
    private static string[] Lines(string input)
    {
        var lines = input.Split('\n');
        return input.EndsWith('\n') ? lines[..^1] : input.Length == 0 ? [] : lines;
    }

    // sed -n 's/^- \[observed\] [0-9-]* \[[^]]*\]: //p' over the existing file.
    private static IEnumerable<string> KnownFacts(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        var text = new UTF8Encoding(false).GetString(File.ReadAllBytes(file));
        return Lines(text).Select(line => ObservedLine().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value);
    }

    [GeneratedRegex(@"\A[1-9][0-9]{0,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MaxValue();

    [GeneratedRegex(@"\A- \[observed\] [0-9-]* \[[^\]]*\]: (.*)\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ObservedLine();
}
