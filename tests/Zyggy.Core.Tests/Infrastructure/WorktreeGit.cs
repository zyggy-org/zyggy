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

    /// <summary>The tip of <c>refs/remotes/origin/feature/shop</c>, a job's <c>base</c> (spec 18).</summary>
    public const string FeatureSha = "3333333333333333333333333333333333333333";

    public WorktreeGit(RecordingProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        processes.Hook = Answer;
    }

    /// <summary>Gets or sets the <c>origin</c> URL; <see langword="null"/> = the project has no remote.</summary>
    public string? OriginUrl { get; set; } = "https://example.invalid/acme/shop.git";

    /// <summary>Gets or sets a value indicating whether <c>refs/remotes/origin/main</c> exists.</summary>
    public bool OriginMainExists { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether <c>refs/remotes/origin/feature/shop</c> exists.</summary>
    public bool OriginFeatureExists { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the origin refuses the push to <c>feature/shop</c> (it moved meanwhile).</summary>
    public bool FeaturePushRefused { get; set; }

    /// <summary>Gets or sets a value indicating whether the branch <c>zyggy/&lt;id&gt;</c> already exists before the run.</summary>
    public bool JobBranchExists { get; set; }

    /// <summary>Gets the common git directory <c>rev-parse --git-common-dir</c> prints for the project and its worktrees (spec 61 §5).</summary>
    public static string CommonDir => Path.Join(Path.GetTempPath(), "zyggy-unit-project", ".git");

    /// <summary>Gets or sets the porcelain status the worktree shows after the model ran (empty = clean).</summary>
    public string StatusAfterRun { get; set; } = "?? src/new.txt\0";

    /// <summary>Gets or sets how many commits the job branch is ahead of its base after Finish's commit.</summary>
    public int Ahead { get; set; } = 1;

    /// <summary>Gets or sets how many commits <c>origin/&lt;default&gt;</c> has that the pushed tip lacks (spec 53, the "behind" line).</summary>
    public int Behind { get; set; }

    /// <summary>Gets or sets the changed paths <c>git diff --name-only -z</c> prints, in git's order.</summary>
    public IReadOnlyList<string> ChangedPaths { get; set; } = ["src/new.txt"];

    /// <summary>Gets or sets the branch <c>refs/remotes/origin/HEAD</c> points at (git's default branch; spec 53).</summary>
    public string OriginHeadBranch { get; set; } = "main";

    /// <summary>Gets or sets a value indicating whether no default branch can be found (no origin/HEAD, no main, no master, detached HEAD).</summary>
    public bool NoDefaultBranch { get; set; }

    /// <summary>Gets the worktree steps that fail: <c>fetch</c>, <c>worktree add</c>, <c>commit</c>, <c>push</c>, <c>worktree remove</c>.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the steps whose git process times out (the runner's kill after <see cref="ProcessSpec.Timeout"/>).</summary>
    public HashSet<string> TimingOut { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets further fully qualified refs <c>rev-parse --verify</c> resolves, ref → SHA (spec 53: <c>origin/&lt;branch&gt;</c>, a local named branch).</summary>
    public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the destination refs (<c>refs/heads/&lt;name&gt;</c>) whose push <c>origin</c> refuses (spec 53: a race or a ref clash).</summary>
    public HashSet<string> RefusedPushDestinations { get; } = new(StringComparer.Ordinal);

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
        if (RecordingProcessRunner.SubCommand(spec) == "push" && RefusedPushDestinations.Any(d => args[^1].EndsWith(":" + d, StringComparison.Ordinal)))
        {
            return RecordingProcessRunner.Fail(1, " ! [rejected] (fetch first)");
        }

        if (Has(spec, "--verify") && Refs.TryGetValue(args[^1], out var known))
        {
            return RecordingProcessRunner.Ok(known + "\n");
        }

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

                if (reference == "refs/remotes/origin/feature/shop")
                {
                    // Without -q git names the missing revision on stderr (spec 59 AC-6); with -q it is silent.
                    return OriginFeatureExists
                        ? RecordingProcessRunner.Ok(FeatureSha + "\n")
                        : RecordingProcessRunner.Fail(128, Has(spec, "-q") ? "" : "fatal: Needed a single revision\n");
                }

                if (reference == "refs/remotes/origin/" + OriginHeadBranch)
                {
                    return OriginMainExists ? RecordingProcessRunner.Ok(BaseSha + "\n") : RecordingProcessRunner.Fail(1, "");
                }

                return reference == "refs/heads/main" ? RecordingProcessRunner.Ok(BaseSha + "\n") : RecordingProcessRunner.Fail(1, "");
            case "rev-parse" when Has(spec, "--git-common-dir"):
                // Spec 61 §5: the project and a leftover folder of the job share one common git directory (a worktree of the project).
                return RecordingProcessRunner.Ok(CommonDir + "\n");
            case "rev-parse" when args[^1].StartsWith("refs/heads/zyggy/", StringComparison.Ordinal):
                return RecordingProcessRunner.Ok((Ahead > 0 ? TipSha : BaseSha) + "\n");
            case "symbolic-ref" when args[^1] == "HEAD":
                // Spec 61 §5: a leftover folder <...>/<id> has its own zyggy/<id> checked out.
                return RecordingProcessRunner.Ok("refs/heads/zyggy/" + Path.GetFileName(spec.WorkingDirectory) + "\n");
            case "symbolic-ref" when Has(spec, "refs/remotes/origin/HEAD"):
                return OriginUrl is null ? RecordingProcessRunner.Fail(1, "") : RecordingProcessRunner.Ok("origin/" + OriginHeadBranch + "\n");
            case "push" when FeaturePushRefused && args[^1].EndsWith(":refs/heads/feature/shop", StringComparison.Ordinal):
                return RecordingProcessRunner.Fail(1, " ! [rejected]        zyggy/x -> feature/shop (fetch first)\n");
            case "status":
                return RecordingProcessRunner.Ok(StatusAfterRun);
            case "rev-list" when args[^1].Contains("..refs/remotes/origin/", StringComparison.Ordinal):
                return RecordingProcessRunner.Ok(Behind.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            case "rev-list":
                return RecordingProcessRunner.Ok(Ahead.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            case "diff":
                return RecordingProcessRunner.Ok(string.Concat(ChangedPaths.Select(p => p + "\0")));
            default:
                return null;
        }
    }
}
