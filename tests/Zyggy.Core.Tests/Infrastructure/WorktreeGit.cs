using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A scripted git for worktree runs (spec 16, slice 1): installed as the <see cref="RecordingProcessRunner.Hook"/>, it answers each
/// command of the worktree sequence from the properties below. Unscripted commands fall through to the runner's defaults.
/// </summary>
internal sealed class WorktreeGit
{
    public const string BaseSha = "1111111111111111111111111111111111111111";

    public const string TipSha = "2222222222222222222222222222222222222222";

    public WorktreeGit(RecordingProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        processes.Hook = Answer;
    }

    /// <summary>Gets or sets the <c>origin</c> URL; <see langword="null"/> = the project has no remote.</summary>
    public string? OriginUrl { get; set; } = "https://example.invalid/acme/shop.git";

    /// <summary>Gets or sets a value indicating whether <c>refs/remotes/origin/main</c> exists.</summary>
    public bool OriginMainExists { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the branch <c>zyggy/&lt;id&gt;</c> already exists before the run.</summary>
    public bool JobBranchExists { get; set; }

    /// <summary>Gets or sets the porcelain status the worktree shows after the model ran (empty = clean).</summary>
    public string StatusAfterRun { get; set; } = "?? src/new.txt\0";

    /// <summary>Gets or sets how many commits the job branch is ahead of its base after Finish's commit.</summary>
    public int Ahead { get; set; } = 1;

    /// <summary>Gets or sets the changed paths <c>git diff --name-only -z</c> prints, in git's order.</summary>
    public IReadOnlyList<string> ChangedPaths { get; set; } = ["src/new.txt"];

    /// <summary>Gets or sets a value indicating whether no default branch can be found (no origin/HEAD, no main, no master, detached HEAD).</summary>
    public bool NoDefaultBranch { get; set; }

    /// <summary>Gets the worktree steps that fail: <c>fetch</c>, <c>worktree add</c>, <c>commit</c>, <c>push</c>, <c>worktree remove</c>.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the steps whose git process times out (the runner's kill after <see cref="ProcessSpec.Timeout"/>).</summary>
    public HashSet<string> TimingOut { get; } = new(StringComparer.Ordinal);

    public static ProcessResult TimedOut() => new(null, "", "", true, false, false, TimeSpan.FromMinutes(2));

    private static bool Has(ProcessSpec spec, string argument) => spec.Arguments.Contains(argument);

    private static string Step(ProcessSpec spec) => RecordingProcessRunner.SubCommand(spec) switch
    {
        "worktree" when Has(spec, "add") => "worktree add",
        "worktree" when Has(spec, "remove") => "worktree remove",
        var sub => sub,
    };

    private ProcessResult? Answer(ProcessSpec spec)
    {
        var step = Step(spec);
        if (TimingOut.Contains(step))
        {
            return TimedOut();
        }

        if (Failing.Contains(step))
        {
            return RecordingProcessRunner.Fail(128, "fatal: " + step + " failed");
        }

        var args = spec.Arguments;
        if (NoDefaultBranch && (RecordingProcessRunner.SubCommand(spec) == "symbolic-ref" || (Has(spec, "--verify") && !args[^1].StartsWith("refs/heads/zyggy/", StringComparison.Ordinal))))
        {
            return RecordingProcessRunner.Fail(1, "");
        }

        switch (RecordingProcessRunner.SubCommand(spec))
        {
            case "config" when Has(spec, "remote.origin.url"):
                return OriginUrl is null ? RecordingProcessRunner.Fail(1, "") : RecordingProcessRunner.Ok(OriginUrl + "\n");
            case "rev-parse" when Has(spec, "--verify"):
                var reference = args[^1];
                if (reference.StartsWith("refs/heads/zyggy/", StringComparison.Ordinal))
                {
                    return JobBranchExists ? RecordingProcessRunner.Ok(TipSha + "\n") : RecordingProcessRunner.Fail(1, "");
                }

                if (reference == "refs/remotes/origin/main")
                {
                    return OriginMainExists ? RecordingProcessRunner.Ok(BaseSha + "\n") : RecordingProcessRunner.Fail(1, "");
                }

                return reference == "refs/heads/main" ? RecordingProcessRunner.Ok(BaseSha + "\n") : RecordingProcessRunner.Fail(1, "");
            case "rev-parse" when args[^1].StartsWith("refs/heads/zyggy/", StringComparison.Ordinal):
                return RecordingProcessRunner.Ok((Ahead > 0 ? TipSha : BaseSha) + "\n");
            case "symbolic-ref" when Has(spec, "refs/remotes/origin/HEAD"):
                return OriginUrl is null ? RecordingProcessRunner.Fail(1, "") : RecordingProcessRunner.Ok("origin/main\n");
            case "status":
                return RecordingProcessRunner.Ok(StatusAfterRun);
            case "rev-list":
                return RecordingProcessRunner.Ok(Ahead.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            case "diff":
                return RecordingProcessRunner.Ok(string.Concat(ChangedPaths.Select(p => p + "\0")));
            default:
                return null;
        }
    }
}
