namespace Zyggy.Core.Secrets.Verbs;

/// <summary>The exit codes of the secret verbs — the CLI's numbers (spec 09 CLI surface).</summary>
public static class SecretVerbExit
{
    /// <summary>Done.</summary>
    public const int Ok = 0;

    /// <summary>The name is not in the store.</summary>
    public const int NotPresent = 1;

    /// <summary>A refused option or value.</summary>
    public const int Usage = 2;

    /// <summary>The hidden prompt was aborted.</summary>
    public const int Aborted = 5;

    /// <summary>The store cannot be opened.</summary>
    public const int StoreUnavailable = 6;
}
