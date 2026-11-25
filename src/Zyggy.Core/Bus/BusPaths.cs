using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// The only place a bus path is built or read (founding spec §4 "Repository layout", §9 design rules). Every result is
/// repository-relative with <c>/</c> separators and tenant-prefixed (<c>tenants/&lt;org&gt;/…</c>, §14); the inputs are
/// typed, so there is no default tenant and no string overload. <see cref="Resolve"/> joins a result onto a checkout for
/// the file system.
/// </summary>
public static class BusPaths
{
    /// <summary>The protocol description at the repository root — the only file outside a tenant prefix (§4).</summary>
    public const string Protocol = "PROTOCOL.md";

    /// <summary>The root folder of every tenant subtree (§14: tenant-prefixed from day one).</summary>
    public const string Tenants = "tenants";

    private const string EnvelopeExtension = ".md";

    private const string RegistryExtension = ".yaml";

    /// <summary>Returns the tenant prefix: <c>tenants/&lt;org&gt;</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    public static string Tenant(TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return Tenants + "/" + tenant.Value;
    }

    /// <summary><c>tenants/&lt;tenant&gt;/nodes</c>: the folder holding every machine's subtree (deliverable 08's inventory and auditor walk it).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The relative path.</returns>
    public static string Nodes(TenantId tenant) => Tenant(tenant) + "/nodes";

    /// <summary><c>tenants/&lt;tenant&gt;/archive</c>: the folder holding the month folders of <see cref="Archive"/>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The relative path.</returns>
    public static string ArchiveRoot(TenantId tenant) => Tenant(tenant) + "/archive";

    /// <summary>Returns a machine's registry file: <c>tenants/&lt;org&gt;/registry/&lt;machine&gt;.yaml</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Registry(TenantId tenant, MachineName machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return RegistryFolder(tenant) + "/" + machine.Value + RegistryExtension;
    }

    /// <summary><c>tenants/&lt;tenant&gt;/registry</c>: the folder holding every machine's registry file (deliverable 08's auditor names it).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The relative path.</returns>
    public static string RegistryFolder(TenantId tenant) => Tenant(tenant) + "/registry";

    /// <summary>Returns a machine's job inbox: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/jobs</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Jobs(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/jobs";

    /// <summary>Returns a machine's claimed jobs: <c>…/jobs/claimed</c> (§4 state machine row 2).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Claimed(TenantId tenant, MachineName machine) => Jobs(tenant, machine) + "/claimed";

    /// <summary>Returns a machine's rejected envelopes: <c>…/jobs/rejected</c> (§4 "Signature").</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Rejected(TenantId tenant, MachineName machine) => Jobs(tenant, machine) + "/rejected";

    /// <summary>Returns a machine's reports: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/reports</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Reports(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/reports";

    /// <summary>Returns a machine's context inbox: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/context</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Context(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/context";

    /// <summary>Returns a machine's archive items for Central: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/items</c> (deliverable 41, W41-6).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine that writes the items.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Items(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/items";

    /// <summary>Returns a machine's live-session statuses for Central: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/status</c> (deliverable 18).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine that writes the statuses.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Status(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/status";

    /// <summary>Returns a machine's questions for Central: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/questions</c> (deliverable 18).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine that asks.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Questions(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/questions";

    /// <summary>Returns a machine's decisions folder: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/decisions</c>; only Central has one (deliverable 18).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine that decides: Central.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Decisions(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/decisions";

    /// <summary>
    /// Returns a machine's withdrawals: <c>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/withdrawals</c> (deliverable 61), written by a job's
    /// sender or Central, removed by the machine.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine that claimed the jobs.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Withdrawals(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/withdrawals";

    /// <summary>Returns the folder of the mandates in force: <c>tenants/&lt;org&gt;/mandates</c> (deliverable 18), written by Central only.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    public static string Mandates(TenantId tenant) => Tenant(tenant) + "/mandates";

    /// <summary>Returns the folder of the revoked mandates: <c>tenants/&lt;org&gt;/mandates/revoked</c> (deliverable 18).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    public static string RevokedMandates(TenantId tenant) => Mandates(tenant) + "/revoked";

    /// <summary>Returns a month's archive folder: <c>tenants/&lt;org&gt;/archive/YYYY-MM</c> (§4 sweep).</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="year">The year.</param>
    /// <param name="month">The month, 1–12.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the year or month is out of range.</exception>
    public static string Archive(TenantId tenant, int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        return ArchiveRoot(tenant) + "/" + year.ToString("0000", CultureInfo.InvariantCulture) + "-" + month.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Returns an envelope file inside a folder: <c>&lt;folder&gt;/&lt;ULID&gt;.md</c> (§4 write rules: file name = ULID).</summary>
    /// <param name="folder">A folder path returned by this class.</param>
    /// <param name="id">The envelope id.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="folder"/> is empty or ends with <c>/</c>.</exception>
    public static string EnvelopeFile(string folder, EnvelopeId id)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(id);
        if (folder.EndsWith('/'))
        {
            throw new ArgumentException("A folder path does not end with a separator.", nameof(folder));
        }

        return folder + "/" + id.Value + EnvelopeExtension;
    }

    /// <summary>The file attached to a report (spec 59, Q2): <c>&lt;folder&gt;/&lt;ULID&gt;.att</c> beside the report's own file.</summary>
    /// <param name="report">The report's file: its path as returned by <see cref="EnvelopeFile"/> and its id.</param>
    /// <returns>The repository-relative path of the attachment.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="report"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the report's path is not the envelope file of its id.</exception>
    public static string AttachmentFile(BusFile report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string fileName = "/" + report.Id.Value + EnvelopeExtension;
        if (!report.RelativePath.EndsWith(fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("An attachment sits beside the envelope file of its report.", nameof(report));
        }

        return report.RelativePath[..^EnvelopeExtension.Length] + AttachmentExtension;
    }

    /// <summary>The extension of a report's attached file (spec 59).</summary>
    public const string AttachmentExtension = ".att";

    /// <summary>Joins a repository-relative path onto a checkout directory using the OS separator.</summary>
    /// <param name="checkoutRoot">The local checkout's root directory.</param>
    /// <param name="relative">A path returned by this class.</param>
    /// <returns>The file-system path.</returns>
    /// <exception cref="ArgumentException">Thrown when an argument is null or empty.</exception>
    public static string Resolve(string checkoutRoot, string relative)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkoutRoot);
        ArgumentException.ThrowIfNullOrEmpty(relative);
        return Path.Join(checkoutRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Reads a repository-relative path back to its tenant, machine, folder and envelope id. Strict: only the exact §4
    /// shapes with lowercase labels, <c>/</c> separators and a canonical ULID file name are accepted; the tenant prefix
    /// alone, <see cref="Protocol"/>, <c>policy.yaml</c>, <c>..</c> and any other path are refused.
    /// </summary>
    /// <param name="relative">The repository-relative path.</param>
    /// <param name="location">The location when the path has a §4 shape; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="relative"/> is a §4 path.</returns>
    public static bool TryParse(string? relative, [NotNullWhen(true)] out BusLocation? location)
    {
        location = null;
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\'))
        {
            return false;
        }

        string[] parts = relative.Split('/');
        if (parts.Length < 4 || parts[0] != Tenants || !TenantId.TryParse(parts[1], out TenantId? tenant))
        {
            return false;
        }

        return parts[2] switch
        {
            "registry" => TryParseRegistry(tenant, parts, out location),
            "archive" => TryParseArchive(tenant, parts, out location),
            "nodes" => TryParseNode(tenant, parts, out location),
            "mandates" => TryParseMandate(tenant, parts, out location),
            _ => false,
        };
    }

    // Deliverable 18: mandates/<ULID>.md, mandates/revoked and mandates/revoked/<ULID>.md; nothing else under mandates/.
    private static bool TryParseMandate(TenantId tenant, string[] parts, out BusLocation? location)
    {
        location = null;
        bool revoked = parts[3] == "revoked";
        EnvelopeId? id = null;
        if (parts.Length == 4 ? !revoked && !TryParseEnvelopeFile(parts[3], out id)
            : parts.Length != 5 || !revoked || !TryParseEnvelopeFile(parts[4], out id))
        {
            return false;
        }

        location = new BusLocation(tenant, null, revoked ? BusFolder.RevokedMandates : BusFolder.Mandates, id);
        return true;
    }

    private static string Node(TenantId tenant, MachineName machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return Tenant(tenant) + "/nodes/" + machine.Value;
    }

    private static bool TryParseRegistry(TenantId tenant, string[] parts, out BusLocation? location)
    {
        location = null;
        if (parts.Length != 4 || !parts[3].EndsWith(RegistryExtension, StringComparison.Ordinal)
            || !MachineName.TryParse(parts[3][..^RegistryExtension.Length], out MachineName? machine))
        {
            return false;
        }

        location = new BusLocation(tenant, machine, BusFolder.Registry, null);
        return true;
    }

    private static bool TryParseArchive(TenantId tenant, string[] parts, out BusLocation? location)
    {
        location = null;
        if (parts.Length is not (4 or 5) || !IsMonth(parts[3]))
        {
            return false;
        }

        EnvelopeId? id = null;
        if (parts.Length == 5 && !TryParseEnvelopeFile(parts[4], out id))
        {
            return false;
        }

        location = new BusLocation(tenant, null, BusFolder.Archive, id);
        return true;
    }

    private static bool TryParseNode(TenantId tenant, string[] parts, out BusLocation? location)
    {
        location = null;
        if (parts.Length < 5 || !MachineName.TryParse(parts[3], out MachineName? machine))
        {
            return false;
        }

        // parts[4] is the folder under the machine; a jobs sub-folder (claimed, rejected) adds one segment.
        int next = 5;
        BusFolder folder;
        switch (parts[4])
        {
            case "reports":
                folder = BusFolder.Reports;
                break;
            case "context":
                folder = BusFolder.Context;
                break;
            case "items":
                folder = BusFolder.Items;
                break;
            case "status":
                folder = BusFolder.Status;
                break;
            case "questions":
                folder = BusFolder.Questions;
                break;
            case "decisions":
                folder = BusFolder.Decisions;
                break;
            case "withdrawals":
                folder = BusFolder.Withdrawals;
                break;
            case "jobs":
                folder = BusFolder.Jobs;
                if (parts.Length > 5 && parts[5] == "claimed")
                {
                    folder = BusFolder.Claimed;
                    next = 6;
                }
                else if (parts.Length > 5 && parts[5] == "rejected")
                {
                    folder = BusFolder.Rejected;
                    next = 6;
                }

                break;
            default:
                return false;
        }

        if (parts.Length == next)
        {
            location = new BusLocation(tenant, machine, folder, null);
            return true;
        }

        if (parts.Length != next + 1 || !TryParseEnvelopeFile(parts[next], out EnvelopeId? id))
        {
            return false;
        }

        location = new BusLocation(tenant, machine, folder, id);
        return true;
    }

    private static bool TryParseEnvelopeFile(string fileName, [NotNullWhen(true)] out EnvelopeId? id)
    {
        id = null;
        return fileName.EndsWith(EnvelopeExtension, StringComparison.Ordinal)
            && EnvelopeId.TryParse(fileName[..^EnvelopeExtension.Length], out id);
    }

    // YYYY-MM: four digits, a hyphen, two digits naming a month 01–12.
    private static bool IsMonth(string text) =>
        text.Length == 7
        && text[4] == '-'
        && !text.AsSpan(0, 4).ContainsAnyExceptInRange('0', '9')
        && !text.AsSpan(5, 2).ContainsAnyExceptInRange('0', '9')
        && int.Parse(text.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture) is >= 1 and <= 12;
}
