namespace Zyggy.Core.Envelope;

/// <summary>
/// The single mapping between the envelope enums and their exact, case-sensitive wire strings (founding spec §4).
/// </summary>
public static class EnvelopeWire
{
    /// <summary>Returns the wire string of an envelope type.</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>job</c>, <c>report</c>, <c>context</c>, <c>item</c>, <c>status</c>, <c>question</c>, <c>decision</c> or <c>mandate</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(EnvelopeType value) => value switch
    {
        EnvelopeType.Job => "job",
        EnvelopeType.Report => "report",
        EnvelopeType.Context => "context",
        EnvelopeType.Item => "item",
        EnvelopeType.Status => "status",
        EnvelopeType.Question => "question",
        EnvelopeType.Decision => "decision",
        EnvelopeType.Mandate => "mandate",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a mandate grant (deliverable 18).</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>accept</c>, <c>merge</c>, <c>release</c> or <c>deploy</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(MandateGrant value) => value switch
    {
        MandateGrant.Accept => "accept",
        MandateGrant.Merge => "merge",
        MandateGrant.Release => "release",
        MandateGrant.Deploy => "deploy",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a job's delivery (deliverable 18).</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>run</c> or <c>session</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(JobDelivery value) => value switch
    {
        JobDelivery.Run => "run",
        JobDelivery.Session => "session",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a mandate job kind (deliverable 18).</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>brief</c>, <c>build</c>, <c>verify</c>, <c>correct</c>, <c>merge</c>, <c>release</c> or <c>deploy</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(MandateJobKind value) => value switch
    {
        MandateJobKind.Brief => "brief",
        MandateJobKind.Build => "build",
        MandateJobKind.Verify => "verify",
        MandateJobKind.Correct => "correct",
        MandateJobKind.Merge => "merge",
        MandateJobKind.Release => "release",
        MandateJobKind.Deploy => "deploy",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Maps a wire string to a mandate grant.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out MandateGrant value) => TryMatch(wire, Enum.GetValues<MandateGrant>(), ToWire, out value);

    /// <summary>Maps a wire string to a job's delivery.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out JobDelivery value) => TryMatch(wire, Enum.GetValues<JobDelivery>(), ToWire, out value);

    /// <summary>Maps a wire string to a mandate job kind.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out MandateJobKind value) => TryMatch(wire, Enum.GetValues<MandateJobKind>(), ToWire, out value);

    /// <summary>Returns the wire string of a decision kind (deliverable 18).</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>answer</c>, <c>accept</c>, <c>correct</c>, <c>next</c>, <c>stop</c>, <c>escalate</c>, <c>refuse</c> or <c>resume</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(DecisionKind value) => value switch
    {
        DecisionKind.Answer => "answer",
        DecisionKind.Accept => "accept",
        DecisionKind.Correct => "correct",
        DecisionKind.Next => "next",
        DecisionKind.Stop => "stop",
        DecisionKind.Escalate => "escalate",
        DecisionKind.Refuse => "refuse",
        DecisionKind.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Returns the wire string of a decision basis (deliverable 18).</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>verified</c> or <c>reported</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(DecisionBasis value) => value switch
    {
        DecisionBasis.Verified => "verified",
        DecisionBasis.Reported => "reported",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    /// <summary>Maps a wire string to a decision kind.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out DecisionKind value) => TryMatch(wire, Enum.GetValues<DecisionKind>(), ToWire, out value);

    /// <summary>Maps a wire string to a decision basis.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out DecisionBasis value) => TryMatch(wire, Enum.GetValues<DecisionBasis>(), ToWire, out value);

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

    /// <summary>Returns the wire string of an item route (founding spec §4, W41-6).</summary>
    /// <param name="value">The member.</param>
    /// <returns><c>bus</c> or <c>onedrive</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined member.</exception>
    public static string ToWire(ItemRoute value) => value switch
    {
        ItemRoute.Bus => "bus",
        ItemRoute.OneDrive => "onedrive",
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
        EnvelopeRejectionReason.WrongRecipient => "wrong_recipient",
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

    /// <summary>Maps a wire string to an item route.</summary>
    /// <param name="wire">The wire string.</param>
    /// <param name="value">The member when recognised.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> is an exact wire string.</returns>
    public static bool TryFromWire(string? wire, out ItemRoute value) => TryMatch(wire, Enum.GetValues<ItemRoute>(), ToWire, out value);

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
