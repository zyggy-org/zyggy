using Zyggy.Core.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>What Central's sweep (spec 17) takes from <c>node.json</c>.</summary>
/// <param name="SignWith">The key Central signs with (<c>signWith</c>); <see langword="null"/> when none is configured, and then no claim is touched.</param>
/// <param name="DefaultTimeoutMinutes">Central's own <c>claude.defaultTimeoutMinutes</c>: the limit of a claim that names no <c>timeout_minutes</c> or cannot be parsed.</param>
internal sealed record SweepOptions(KeyId? SignWith, int DefaultTimeoutMinutes = 30);
