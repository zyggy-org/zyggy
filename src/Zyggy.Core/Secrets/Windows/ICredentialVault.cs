namespace Zyggy.Core.Secrets.Windows;

/// <summary>
/// The four calls the laptops' store makes on the Windows Credential Manager (spec 09 AC-1), behind an interface so the store
/// is unit-tested with a substitute and the vault itself is touched only by the opt-in OS facts. Passwords are the lowercase
/// hex text the store writes; the vault never interprets them.
/// </summary>
internal interface ICredentialVault
{
    /// <summary>The stored password of <paramref name="target"/>, or <see langword="null"/> when no credential has that target.</summary>
    string? Read(string target);

    /// <summary>Creates or replaces the generic credential <paramref name="target"/>.</summary>
    void Write(string target, string userName, string secret, string comment);

    /// <summary>Deletes <paramref name="target"/>; <see langword="false"/> when it did not exist.</summary>
    bool TryDelete(string target);

    /// <summary>The target names matching <paramref name="filter"/> (the Credential Manager's wildcard syntax, <c>*</c> at the end).</summary>
    IReadOnlyList<string> Enumerate(string filter);
}
