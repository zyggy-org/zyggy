namespace Zyggy.Core.Setup;

/// <summary>The exit codes of <c>zyggy setup</c> (spec 42 Contracts); when several apply the priority is 3, then 4, then 6.</summary>
public static class SetupExit
{
    /// <summary>Everything is in place.</summary>
    public const int Ok = 0;

    /// <summary>A refused option, or a <c>central</c> machine (setup is for laptops).</summary>
    public const int Usage = 2;

    /// <summary><c>node.json</c> missing, unreadable, invalid, naming another tenant or machine, or without <c>signWith</c>: the node cannot start.</summary>
    public const int NodeConfig = 3;

    /// <summary>The <c>signWith</c> key is not in the secret store.</summary>
    public const int SigningKeyMissing = 4;

    /// <summary><c>zyggy-core</c>, the settings or a skill failed, an env value names a missing file, or <c>github-pat</c> is missing.</summary>
    public const int Incomplete = 6;

    /// <summary>The worst exit the lines call for: <see cref="NodeConfig"/>, then <see cref="SigningKeyMissing"/>, then <see cref="Incomplete"/>, else <see cref="Ok"/>.</summary>
    /// <param name="lines">The lines of one run.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lines"/> is null.</exception>
    public static int Worst(IReadOnlyList<SetupLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Any(l => l.Item == SetupItem.NodeJson && l.Outcome == SetupOutcome.Failed))
        {
            return NodeConfig;
        }

        if (lines.Any(l => l.Item == SetupItem.SecretSigningKey && l.Outcome == SetupOutcome.Missing))
        {
            return SigningKeyMissing;
        }

        return lines.Any(l => l.Outcome == SetupOutcome.Failed || (l.Item == SetupItem.SecretGitHubPat && l.Outcome == SetupOutcome.Missing))
            ? Incomplete
            : Ok;
    }
}
