using Zyggy.Core.Bus;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A temporary directory laid out like a bus checkout for one tenant and machine through the production <see cref="BusPaths"/>
/// (no <c>.git</c>: git is recorded, not run). Tests drop files into it and read what the code under test wrote. Deleted on dispose.
/// </summary>
public sealed class TempCheckout : IDisposable
{
    public TempCheckout(TenantId tenant, MachineName machine)
    {
        Tenant = tenant;
        Machine = machine;
        Root = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"));
        foreach (string folder in new[]
        {
            BusPaths.Jobs(tenant, machine),
            BusPaths.Claimed(tenant, machine),
            BusPaths.Rejected(tenant, machine),
            BusPaths.Reports(tenant, machine),
            BusPaths.Context(tenant, machine),
        })
        {
            Directory.CreateDirectory(BusPaths.Resolve(Root, folder));
        }
    }

    public TenantId Tenant { get; }

    public MachineName Machine { get; }

    public string Root { get; }

    /// <summary>Writes <paramref name="bytes"/> at the repository-relative path, creating folders as needed.</summary>
    public void Drop(string relativePath, byte[] bytes)
    {
        string full = BusPaths.Resolve(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public bool Exists(string relativePath) => File.Exists(BusPaths.Resolve(Root, relativePath));

    public byte[] Read(string relativePath) => File.ReadAllBytes(BusPaths.Resolve(Root, relativePath));

    /// <summary>The repository-relative paths of every file under <paramref name="folder"/>, in ordinal order.</summary>
    public IReadOnlyList<string> FilesUnder(string folder)
    {
        string full = BusPaths.Resolve(Root, folder);
        return Directory.Exists(full)
            ? Directory.EnumerateFiles(full).Select(f => folder + "/" + Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
