namespace Zyggy.Core.Secrets;

/// <summary>The store names the CLI prints and the unavailable exception carries (spec 09 Output formats).</summary>
internal static class SecretStoreNames
{
    public const string CredentialManager = "credential-manager";
    public const string CredentialsDirectory = "credentials-directory";
    public const string File = "file";
    public const string Memory = "memory";
    public const string CredentialFile = "credential-file";

    /// <summary>The name of <paramref name="store"/>: its declared name, else its type name.</summary>
    public static string Of(ISecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store is IDescribedSecretStore described ? described.StoreName : store.GetType().Name;
    }
}
