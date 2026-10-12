namespace Zyggy.Core.Bus;

/// <summary>The outcome of one <c>push</c> (founding spec §5 "Git operations"). Closed: the repository layer switches over the three cases.</summary>
public abstract record PushResult
{
    private protected PushResult()
    {
    }
}

/// <summary>The local commits are on the remote branch.</summary>
public sealed record Pushed : PushResult;

/// <summary>The remote branch moved on (non-fast-forward): the §5 retry rule applies — <c>pull --rebase</c> and push again.</summary>
/// <param name="Detail">A short, redacted detail from git's error output.</param>
public sealed record Rejected(string Detail) : PushResult;

/// <summary>The push failed for another reason: authentication, network, a process that did not start or timed out.</summary>
/// <param name="Detail">A short, redacted detail; never a credential.</param>
public sealed record PushFailed(string Detail) : PushResult;
