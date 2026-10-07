using System.Globalization;

using Zyggy.Core.M365;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Brief;

/// <summary>
/// <c>zyggy brief idea &lt;n&gt; good|skip|not-interested|later|do-it [--until &lt;YYYY-MM-DD&gt;] [--date &lt;YYYY-MM-DD&gt;]</c> (spec 35 AC-36):
/// resolves suggestion <c>n</c> from that date's item list (default today) and appends one <c>answer</c> row to <c>ideas.jsonl</c> under the
/// lock; <c>later</c> needs a future <c>--until</c>. Exit 0 · 3 configuration or no item list · 4 usage · 5 no such suggestion.
/// </summary>
internal sealed class BriefIdeaVerb(BriefVerbContext context)
{
    private const string Prefix = "brief: ";
    private const string Usage = " (usage: zyggy brief idea <n> good|skip|not-interested|later|do-it [--until <YYYY-MM-DD>] [--date <YYYY-MM-DD>])";

    private static readonly HashSet<string> Answers = new(["good", "skip", "not-interested", "later", "do-it"], StringComparer.Ordinal);

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        int? number = null;
        string? answer = null;
        DateOnly? until = null;
        DateOnly? date = null;
        for (var i = 0; i < args.Count; i++)
        {
            var next = i + 1 < args.Count ? args[i + 1] : string.Empty;
            if (args[i] is "--until" or "--date")
            {
                if (!DateOnly.TryParseExact(next, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    || (args[i] == "--until" ? until : date) is not null)
                {
                    return await UsageAsync(io, $"{args[i]} needs one date YYYY-MM-DD").ConfigureAwait(false);
                }

                if (args[i] == "--until")
                {
                    until = parsed;
                }
                else
                {
                    date = parsed;
                }

                i++;
            }
            else if (number is null && int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 99)
            {
                number = n;
            }
            else if (number is not null && answer is null && Answers.Contains(args[i]))
            {
                answer = args[i];
            }
            else
            {
                return await UsageAsync(io, $"unexpected argument '{ShellText.Prefix(args[i], 40)}'").ConfigureAwait(false);
            }
        }

        if (number is null || answer is null)
        {
            return await UsageAsync(io, "a suggestion number and an answer are needed").ConfigureAwait(false);
        }

        var (zone, zoneError) = context.Zone();
        if (zone is null)
        {
            return await FailAsync(io, 3, zoneError!).ConfigureAwait(false);
        }

        var today = context.Today(zone);
        if (answer == "later" && (until is null || until <= today))
        {
            return await UsageAsync(io, "later needs --until with a future date").ConfigureAwait(false);
        }

        if (answer != "later" && until is not null)
        {
            return await UsageAsync(io, "--until goes with later only").ConfigureAwait(false);
        }

        var briefDate = date ?? today;
        var (sidecar, sidecarError) = context.Sidecar(briefDate);
        if (sidecar is null)
        {
            return await FailAsync(io, 3, sidecarError!).ConfigureAwait(false);
        }

        if (sidecar.Ideas.FirstOrDefault(idea => idea.N == number) is not { } chosen)
        {
            return await FailAsync(io, 5, $"no suggestion {number} in the brief of {BriefPaths.Iso(briefDate)}").ConfigureAwait(false);
        }

        var history = new IdeasHistory(new BriefPaths(context.Environment));
        try
        {
            history.AppendAnswer(today, chosen.Id, chosen.Area, answer, until);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StateDirectoryException)
        {
            return await FailAsync(io, 3, $"could not record the answer: {ex.Message}").ConfigureAwait(false);
        }

        await io.Out.WriteAsync($"recorded: {chosen.Id} {answer}\n").ConfigureAwait(false);
        return 0;
    }

    private static Task<int> UsageAsync(VerbIo io, string message) => FailAsync(io, 4, message + Usage);

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}
