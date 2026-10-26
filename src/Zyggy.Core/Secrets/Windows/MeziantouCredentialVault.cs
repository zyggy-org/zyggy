using System.ComponentModel;
using System.Runtime.Versioning;

using Meziantou.Framework.Win32;

namespace Zyggy.Core.Secrets.Windows;

/// <summary>
/// The real <see cref="ICredentialVault"/> over <c>Meziantou.Framework.Win32.CredentialManager</c> (deliverable 09's one package):
/// generic credentials persisted for the local machine, in the current user's vault. The only file that names the package.
/// </summary>
[SupportedOSPlatform("windows5.1.2600")]
internal sealed class MeziantouCredentialVault : ICredentialVault
{
    private const int ErrorNotFound = 1168;

    public string? Read(string target) => CredentialManager.ReadCredential(target)?.Password;

    public void Write(string target, string userName, string secret, string comment) =>
        CredentialManager.WriteCredential(target, userName, secret, comment, CredentialPersistence.LocalMachine);

    public bool TryDelete(string target)
    {
        try
        {
            CredentialManager.DeleteCredential(target);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNotFound)
        {
            return false;
        }
    }

    public IReadOnlyList<string> Enumerate(string filter) =>
        CredentialManager.EnumerateCredentials(filter).Select(c => c.ApplicationName).ToList();
}
