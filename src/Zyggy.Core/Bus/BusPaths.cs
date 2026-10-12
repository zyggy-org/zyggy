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

    private const string EnvelopeExtension = ".md";

    private const string RegistryExtension = ".yaml";

    /// <summary>Returns the tenant prefix: <c>tenants/&lt;org&gt;</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tenant"/> is null.</exception>
    public static string Tenant(TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return "tenants/" + tenant.Value;
    }

    /// <summary>Returns a machine's registry file: <c>tenants/&lt;org&gt;/registry/&lt;machine&gt;.yaml</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="machine">The machine.</param>
    /// <returns>The repository-relative path.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static string Registry(TenantId tenant, MachineName machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return Tenant(tenant) + "/registry/" + machine.Value + RegistryExtension;
    }

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
        return Tenant(tenant) + "/archive/" + year.ToString("0000", CultureInfo.InvariantCulture) + "-" + month.ToString("00", CultureInfo.InvariantCulture);
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
        if (parts.Length < 4 || parts[0] != "tenants" || !TenantId.TryParse(parts[1], out TenantId? tenant))
        {
            return false;
        }

        return parts[2] switch
        {
            "registry" => TryParseRegistry(tenant, parts, out location),
            "archive" => TryParseArchive(tenant, parts, out location),
            "nodes" => TryParseNode(tenant, parts, out location),
            _ => false,
        };
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
