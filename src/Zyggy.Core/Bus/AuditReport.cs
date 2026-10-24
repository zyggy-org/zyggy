namespace Zyggy.Core.Bus;

/// <summary>One file's verdict (spec 08 AC-4).</summary>
/// <param name="RelativePath">The file, repository-relative with <c>/</c>.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Field">The §4 field at fault, when one is.</param>
/// <param name="Detail">Why, in the words <c>verify</c> prints; for <c>unknown_key</c> it names the key id.</param>
public sealed record AuditEntry(string RelativePath, AuditOutcome Outcome, string? Field, string? Detail);

/// <summary>The §8 audit of this tenant's subtree (spec 08 AC-4): every file judged, the other entries under <c>tenants/</c> named, never read.</summary>
public sealed class AuditReport
{
    /// <summary>Creates the report and counts its entries.</summary>
    /// <param name="headSha">The checkout's <c>HEAD</c> at the time of the read.</param>
    /// <param name="entries">Every file under this tenant's subtree, ordinal path order.</param>
    /// <param name="foreignTenants">Other valid tenant labels under <c>tenants/</c> (never read).</param>
    /// <param name="unexpectedTopLevel">Entries under <c>tenants/</c> that are not tenant labels, as <c>tenants/&lt;name&gt;/</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when a list is null.</exception>
    public AuditReport(string? headSha, IReadOnlyList<AuditEntry> entries, IReadOnlyList<string> foreignTenants, IReadOnlyList<string> unexpectedTopLevel)
    {
        HeadSha = headSha;
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        ForeignTenants = foreignTenants ?? throw new ArgumentNullException(nameof(foreignTenants));
        UnexpectedTopLevel = unexpectedTopLevel ?? throw new ArgumentNullException(nameof(unexpectedTopLevel));
        Ok = entries.Count(e => e.Outcome == AuditOutcome.Ok);
        Skipped = entries.Count(e => e.Outcome == AuditOutcome.Skipped);
        Failed = entries.Count - Ok - Skipped;
    }

    /// <summary>Gets the checkout's <c>HEAD</c>.</summary>
    public string? HeadSha { get; }

    /// <summary>Gets every file's verdict, ordinal path order.</summary>
    public IReadOnlyList<AuditEntry> Entries { get; }

    /// <summary>Gets the other tenant directories (a §13 violation of "one bus repository per tenant"; reported, never read — A8-1).</summary>
    public IReadOnlyList<string> ForeignTenants { get; }

    /// <summary>Gets the entries under <c>tenants/</c> that are no tenant label.</summary>
    public IReadOnlyList<string> UnexpectedTopLevel { get; }

    /// <summary>Gets how many files are <see cref="AuditOutcome.Ok"/>.</summary>
    public int Ok { get; }

    /// <summary>Gets how many files are drift: any outcome that is neither ok nor skipped.</summary>
    public int Failed { get; }

    /// <summary>Gets how many files were not judged.</summary>
    public int Skipped { get; }

    /// <summary>Gets a value indicating whether nothing is wrong: no drift, no foreign tenant, nothing unexpected under <c>tenants/</c>.</summary>
    public bool IsClean => Failed == 0 && ForeignTenants.Count == 0 && UnexpectedTopLevel.Count == 0;
}
