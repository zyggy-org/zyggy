using Zyggy.Core.Verbs;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// What every <c>zyggy linkedin</c> verb is given: the environment, the clock and the time-zone lookup; for tests, the LinkedIn handler
/// (the stub; <see langword="null"/> is the real network), whether the credential files' Unix mode and owner are checked, and the HTTP
/// timeout (<see langword="null"/> is 30 s).
/// </summary>
internal sealed record LinkedInVerbContext(
    IReadOnlyDictionary<string, string?> Environment,
    TimeProvider Clock,
    Func<string, TimeZoneInfo> FindTimeZone,
    HttpMessageHandler? LinkedInHandler = null,
    bool CheckOwnership = true,
    TimeSpan? HttpTimeout = null);

/// <summary>One <c>zyggy linkedin …</c> verb.</summary>
internal interface ILinkedInVerb
{
    Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken);
}
