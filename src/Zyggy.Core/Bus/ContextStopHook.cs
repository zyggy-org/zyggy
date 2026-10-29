using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>What the <c>Stop</c> hook did with the turn.</summary>
public enum StopHookResult
{
    /// <summary>One line was appended to the session's spool.</summary>
    Spooled,

    /// <summary>Nothing to note: a terminal on stdin, <c>stop_hook_active</c>, or no non-empty line.</summary>
    Ignored,

    /// <summary>The input is not a hook JSON object.</summary>
    Unparseable,

    /// <summary>The note matches a secret pattern; nothing was spooled.</summary>
    Refused,

    /// <summary>The session's cap was reached: the marker was written instead, once.</summary>
    CapReached,

    /// <summary>The session was already capped; nothing changed.</summary>
    AlreadyCapped,
}

/// <summary>The outcome of one <c>Stop</c> hook run.</summary>
/// <param name="Result">What happened.</param>
/// <param name="PatternName">The refusing pattern's name when <see cref="StopHookResult.Refused"/>.</param>
/// <param name="SessionId8">The session's first eight characters when the input was parsed.</param>
public sealed record StopHookOutcome(StopHookResult Result, string? PatternName, string? SessionId8);

/// <summary>
/// The laptop's <c>Stop</c> hook (spec 12 D2, founding spec §3 as amended): one <c>[observed]</c> line per turn into the session's
/// spool — never the bus, never memory, no key, no network, no checkout. The note is refused by pattern name before it is written.
/// </summary>
public sealed class ContextStopHook
{
    private readonly ContextSpool _spool;
    private readonly SecretPatterns _patterns;
    private readonly TimeProvider _clock;

    internal ContextStopHook(ContextSpool spool, SecretPatterns patterns, TimeProvider clock, ContextEnvelopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Options = options;
    }

    /// <summary>The limits in force.</summary>
    public ContextEnvelopeOptions Options { get; }

    /// <summary>Reads the hook JSON from <paramref name="stdin"/> and spools the turn's note.</summary>
    /// <param name="stdin">The hook's standard input; never read when <paramref name="inputRedirected"/> is false.</param>
    /// <param name="inputRedirected">Whether stdin is a pipe or file rather than a terminal.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stdin"/> is null.</exception>
    public StopHookOutcome Run(TextReader stdin, bool inputRedirected)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        if (!inputRedirected)
        {
            return new StopHookOutcome(StopHookResult.Ignored, null, null);
        }

        StopHookNote? note = StopHookInput.Read(stdin.ReadToEnd(), Options, out StopHookInput.Status status);
        if (note is null)
        {
            return new StopHookOutcome(status == StopHookInput.Status.Unparseable ? StopHookResult.Unparseable : StopHookResult.Ignored, null, null);
        }

        string id8 = note.SessionId.Length > 8 ? note.SessionId[..8] : note.SessionId;
        if (_patterns.TryMatch(note.FirstLine, out string pattern))
        {
            return new StopHookOutcome(StopHookResult.Refused, pattern, id8);
        }

        ContextScope scope = ScopeOf(note.Cwd);
        string hint = scope.Kind == ContextScopeKind.Project ? $" ({scope})" : string.Empty;
        DateTimeOffset now = _clock.GetUtcNow();
        string line = FactLine.Format(
            "observed",
            hint,
            string.Create(CultureInfo.InvariantCulture, $"session {id8} {now:HH:mm}Z"),
            now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            note.FirstLine);
        StopHookResult result = _spool.Append(note.SessionId, scope, line) switch
        {
            SpoolAppendResult.Appended => StopHookResult.Spooled,
            SpoolAppendResult.CapReached => StopHookResult.CapReached,
            _ => StopHookResult.AlreadyCapped,
        };
        return new StopHookOutcome(result, null, id8);
    }

    /// <summary>The scope of a session: <c>project:&lt;last segment of cwd, lower-cased&gt;</c> when that is a label, else <c>general</c>.</summary>
    internal static ContextScope ScopeOf(string cwd)
    {
        string normalised = cwd.Replace('\\', '/').TrimEnd('/');
        string last = normalised[(normalised.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return ContextRequest.IsProjectLabel(last) ? new ContextScope(ContextScopeKind.Project, last) : new ContextScope(ContextScopeKind.General, null);
    }
}
