namespace Zyggy.Core.Secrets;

/// <summary>Where a credential file was found.</summary>
internal enum CredentialSource
{
    /// <summary>Not found or refused.</summary>
    None,

    /// <summary>systemd's <c>$CREDENTIALS_DIRECTORY</c> (<c>LoadCredential=</c>); 0600, 0400 or 0440.</summary>
    CredentialsDirectory,

    /// <summary>The key file under the user's configuration directory; 0600.</summary>
    File,
}

/// <summary>The shell's names of a source (<c>key: file</c>, <c>key: credentials directory</c>).</summary>
internal static class CredentialSourceText
{
    public static string ToText(this CredentialSource source) => source switch
    {
        CredentialSource.CredentialsDirectory => "credentials directory",
        CredentialSource.File => "file",
        _ => "none",
    };
}
