using System.Globalization;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>The one enumeration of a tenant's <c>archive/YYYY-MM/</c> folders (plan 13 A5): other directory names are ignored.</summary>
internal static class ArchiveMonths
{
    /// <summary>The month folders that exist under the tenant's archive, as <see cref="BusPaths.Archive"/> paths, ordinal order.</summary>
    public static IReadOnlyList<string> Folders(string checkoutRoot, TenantId tenant)
    {
        string root = BusPaths.Resolve(checkoutRoot, BusPaths.ArchiveRoot(tenant));
        if (!Directory.Exists(root))
        {
            return [];
        }

        var folders = new List<string>();
        foreach (string name in Directory.EnumerateDirectories(root).Select(d => Path.GetFileName(d)).Order(StringComparer.Ordinal))
        {
            if (name.Length == 7 && DateOnly.TryParseExact(name + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly month))
            {
                folders.Add(BusPaths.Archive(tenant, month.Year, month.Month));
            }
        }

        return folders;
    }
}
