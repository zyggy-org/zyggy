using Zyggy.Core.Registry;

namespace Zyggy.Core.Bus;

/// <content>The registry files of the tenant (spec 59 slice 7): what <c>zyggy status</c> lists per machine.</content>
public sealed partial class BusInventory
{
    /// <summary>
    /// Reads every registry file of this tenant from the checkout, machine by machine in ordinal order. A file that does not parse, or
    /// names another tenant or machine than its path, is left out: <c>zyggy registry</c> is the verb that names unreadable files.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The readable documents; empty when the registry folder does not exist.</returns>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<IReadOnlyList<RegistryDocument>> RegistryAsync(CancellationToken ct)
    {
        using IDisposable handle = await LockAsync(ct).ConfigureAwait(false);
        string relativeFolder = BusPaths.RegistryFolder(_me.Tenant);
        string folder = BusPaths.Resolve(_root, relativeFolder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var documents = new List<RegistryDocument>();
        foreach (string file in Directory.EnumerateFiles(folder).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            if (!BusPaths.TryParse(relativeFolder + "/" + Path.GetFileName(file), out BusLocation? location)
                || location is not { Folder: BusFolder.Registry, Machine: { } machine })
            {
                continue;
            }

            RegistryParse parse = RegistryDocument.Parse(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false));
            if (parse.Document is { } document && document.Tenant == _me.Tenant && document.Machine == machine)
            {
                documents.Add(document);
            }
        }

        return documents;
    }
}
