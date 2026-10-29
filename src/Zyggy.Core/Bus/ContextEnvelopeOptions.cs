namespace Zyggy.Core.Bus;

/// <summary>The limits of the context envelopes a node emits (spec 12 Configuration): fixed defaults, no environment or <c>node.json</c> knob.</summary>
public sealed record ContextEnvelopeOptions
{
    /// <summary>The longest fact <c>zyggy context</c> accepts, in Unicode scalars.</summary>
    public int MaxFactChars { get; init; } = 1000;

    /// <summary>The longest note the <c>Stop</c> hook spools per turn, in Unicode scalars, before the cut mark.</summary>
    public int MaxNoteChars { get; init; } = 240;

    /// <summary>How many hook lines one session may spool before the cap marker.</summary>
    public int MaxHookLinesPerSession { get; init; } = 150;

    /// <summary>A spool older than this is shipped by any flush, as the one of a crashed session.</summary>
    public TimeSpan StaleSpoolAge { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>The largest body a context envelope may carry.</summary>
    public int MaxBodyBytes { get; init; } = 65_536;

    /// <summary>The line written once when <see cref="MaxHookLinesPerSession"/> is reached.</summary>
    public string CapMarker { get; init; } = "- [observed] cap reached: no further hook lines this session";
}
