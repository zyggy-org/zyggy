namespace Zyggy.Core.Bus;

/// <summary>The outcome of a <c>pull --rebase</c> on the local checkout (founding spec §5 "Git operations").</summary>
/// <param name="Succeeded">Whether the checkout now carries the remote branch.</param>
/// <param name="Detail">A short, redacted detail (the first lines of git's error output), empty on success.</param>
public sealed record BusSyncResult(bool Succeeded, string Detail);
