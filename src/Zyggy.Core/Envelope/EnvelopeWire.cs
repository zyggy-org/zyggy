namespace Zyggy.Core.Envelope;

/// <summary>
/// The single mapping between the envelope enums and their exact, case-sensitive wire strings (founding spec §4).
/// </summary>
public static class EnvelopeWire
{
    /// <summary>Returns the wire string of an envelope type.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>job</c>, <c>report</c> or <c>context</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(EnvelopeType value) => value switch
    {
        EnvelopeType.Job => "job",
        EnvelopeType.Report => "report",
        EnvelopeType.Context => "context",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a priority.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>low</c>, <c>normal</c> or <c>high</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(EnvelopePriority value) => value switch
    {
        EnvelopePriority.Low => "low",
        EnvelopePriority.Normal => "normal",
        EnvelopePriority.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a report status.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>done</c>, <c>failed</c>, <c>timeout</c> or <c>rejected</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ReportStatus value) => value switch
    {
        ReportStatus.Done => "done",
        ReportStatus.Failed => "failed",
        ReportStatus.Timeout => "timeout",
        ReportStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a context scope kind.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>project</c>, <c>machine</c> or <c>general</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ContextScopeKind value) => value switch
    {
        ContextScopeKind.Project => "project",
        ContextScopeKind.Machine => "machine",
        ContextScopeKind.General => "general",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a rejection reason.</summary>
    /// <param name="value">The member.</param>
    /// <returns>The snake_case reason, for example <c>schema_unsupported</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(EnvelopeRejectionReason value) => value switch
    {
        EnvelopeRejectionReason.Malformed => "malformed",
        EnvelopeRejectionReason.TenantMismatch => "tenant_mismatch",
        EnvelopeRejectionReason.SchemaUnsupported => "schema_unsupported",
        EnvelopeRejectionReason.KeyIdTenantMismatch => "key_id_tenant_mismatch",
        EnvelopeRejectionReason.MissingField => "missing_field",
        EnvelopeRejectionReason.InvalidField => "invalid_field",
        EnvelopeRejectionReason.MissingSignature => "missing_signature",
        EnvelopeRejectionReason.UnknownKey => "unknown_key",
        EnvelopeRejectionReason.InvalidSignature => "invalid_signature",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Maps a wire string to an envelope type.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out EnvelopeType value) => TryMatch(wire, Enum.GetValues<EnvelopeType>(), ToWire, out value);

    /// <summary>Maps a wire string to a priority.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out EnvelopePriority value) => TryMatch(wire, Enum.GetValues<EnvelopePriority>(), ToWire, out value);

    /// <summary>Maps a wire string to a report status.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out ReportStatus value) => TryMatch(wire, Enum.GetValues<ReportStatus>(), ToWire, out value);

    /// <summary>Maps a wire string to a context scope kind.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out ContextScopeKind value) => TryMatch(wire, Enum.GetValues<ContextScopeKind>(), ToWire, out value);

    /// <summary>Maps a wire string to a rejection reason.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out EnvelopeRejectionReason value) =>
        TryMatch(wire, Enum.GetValues<EnvelopeRejectionReason>(), ToWire, out value);

    private static bool TryMatch<T>(string? wire, T[] members, Func<T, string> toWire, out T value)
        where T : struct, Enum
    {
        foreach (T member in members)
        {
            if (string.Equals(toWire(member), wire, StringComparison.Ordinal))
            {
                value = member;
                return true;
            }
        }

        value = default;
        return false;
    }
}
