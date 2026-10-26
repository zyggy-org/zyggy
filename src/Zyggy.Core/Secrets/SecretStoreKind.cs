namespace Zyggy.Core.Secrets;

/// <summary>Which secret store a machine uses (<c>node.json</c> <c>secrets.store</c>, spec 09 AC-4; founding spec §10 as amended by A9-3). Closed.</summary>
public enum SecretStoreKind
{
    /// <summary>The OS store: the Credential Manager on Windows; the systemd credentials directory, then the 0600 file, on Linux. The default.</summary>
    Os,

    /// <summary>The P0 file store on every OS (tests, spikes); on Windows a warning is logged once.</summary>
    File,
}

/// <summary>The wire words of <see cref="SecretStoreKind"/>: <c>os</c>, <c>file</c>; case-sensitive.</summary>
internal static class SecretStoreKindWire
{
    public static string ToWire(SecretStoreKind kind) => kind switch
    {
        SecretStoreKind.Os => "os",
        SecretStoreKind.File => "file",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown secret store kind."),
    };

    public static bool TryParse(string? word, out SecretStoreKind kind)
    {
        switch (word)
        {
            case "os":
                kind = SecretStoreKind.Os;
                return true;
            case "file":
                kind = SecretStoreKind.File;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
