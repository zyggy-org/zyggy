using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory.Mirror;

/// <summary>Why the mirror publisher refuses to start (spec 47 AC-3; publish exit 3).</summary>
internal enum MirrorRefusalReason
{
    /// <summary>A work machine's reach names a path outside <c>supervision.outboundPaths</c>.</summary>
    ReachOutsideOutbound,

    /// <summary>A work machine is among the targets and <c>supervision.outboundPaths</c> is empty.</summary>
    OutboundEmpty,

    /// <summary>A target's repository is the memory repository.</summary>
    TargetIsMemoryRepository,
}

/// <summary>A refusal of the mirror configuration: the reason, the target's machine and what is wrong (a path or a key, never a secret).</summary>
internal sealed record MirrorRefusal(MirrorRefusalReason Reason, MachineName Machine, string Detail);

/// <summary>
/// The start check of the mirror publisher (spec 47 "What each machine may read", "Publishing on Central"): a work machine reads only
/// inside <c>supervision.outboundPaths</c> (one rule, shared with spec 18), and no target is the memory repository.
/// </summary>
internal static class MirrorConfigurationCheck
{
    /// <summary>Returns the first refusal, in target order, or <see langword="null"/> when the configuration may start.</summary>
    /// <param name="targets">The configured targets.</param>
    /// <param name="workMachines"><c>supervision.workMachines</c>.</param>
    /// <param name="outboundPaths"><c>supervision.outboundPaths</c>.</param>
    /// <param name="memoryRemote">The memory checkout's <c>origin</c> URL.</param>
    public static MirrorRefusal? Check(
        IReadOnlyList<MirrorTarget> targets,
        IReadOnlyCollection<MachineName> workMachines,
        IReadOnlyList<string> outboundPaths,
        string memoryRemote)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(workMachines);
        ArgumentNullException.ThrowIfNull(outboundPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryRemote);
        var memory = RepositoryIdentity(memoryRemote);
        foreach (var target in targets)
        {
            if (RepositoryIdentity(target.Repository) == memory)
            {
                return new MirrorRefusal(MirrorRefusalReason.TargetIsMemoryRepository, target.Machine, "repository");
            }

            if (!workMachines.Contains(target.Machine))
            {
                continue;
            }

            if (outboundPaths.Count == 0)
            {
                return new MirrorRefusal(MirrorRefusalReason.OutboundEmpty, target.Machine, "supervision.outboundPaths");
            }

            if (target.Reach.Paths.FirstOrDefault(p => !outboundPaths.Any(o => MirrorReach.Covers(o, p))) is { } outside)
            {
                return new MirrorRefusal(MirrorRefusalReason.ReachOutsideOutbound, target.Machine, outside);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>host/owner/repo</c> in lower case for the SSH (<c>git@host:owner/repo.git</c>, <c>ssh://git@host/owner/repo.git</c>) and
    /// HTTPS (<c>https://host/owner/repo[.git][/]</c>) forms of one repository, so the forms compare equal.
    /// </summary>
    internal static string RepositoryIdentity(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var text = url.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }
        else
        {
            // scp-like: user@host:owner/repo
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                text = string.Concat(text.AsSpan(0, colon), "/", text.AsSpan(colon + 1));
            }
        }

        var at = text.IndexOf('@', StringComparison.Ordinal);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (at >= 0 && (slash < 0 || at < slash))
        {
            text = text[(at + 1)..];
        }

        text = text.TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        return text.ToLowerInvariant();
    }
}
