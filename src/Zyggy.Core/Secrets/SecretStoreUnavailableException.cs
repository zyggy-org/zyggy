namespace Zyggy.Core.Secrets;

/// <summary>
/// The store exists but cannot be used: no logon session for the Credential Manager, a root that is not a directory, a
/// credentials file that cannot be read (spec 09 AC-2). Carries the store's name and a detail — never a byte of a secret.
/// Absence of a secret is never this exception; a store answers <see langword="null"/> or an empty list for that.
/// </summary>
public sealed class SecretStoreUnavailableException : Exception
{
    /// <summary>Creates the exception with no detail.</summary>
    public SecretStoreUnavailableException()
        : this("secret store", "unavailable")
    {
    }

    /// <summary>Creates the exception from a message; the store name is the text before the first <c>: </c>, or the whole message.</summary>
    /// <param name="message">The message.</param>
    public SecretStoreUnavailableException(string message)
        : this(StoreNameOf(message), DetailOf(message))
    {
    }

    /// <summary>Creates the exception from a message and the underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SecretStoreUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
        StoreName = StoreNameOf(message);
        Detail = DetailOf(message);
    }

    /// <summary>Creates the exception for <paramref name="storeName"/> with <paramref name="detail"/>; the message is <c>&lt;store name&gt;: &lt;detail&gt;</c>.</summary>
    /// <param name="storeName">The store's name, for example <c>credential-manager</c>.</param>
    /// <param name="detail">What could not be done, without any secret material.</param>
    public SecretStoreUnavailableException(string storeName, string detail)
        : base(storeName + ": " + detail)
    {
        StoreName = storeName;
        Detail = detail;
    }

    /// <summary>The store's name.</summary>
    public string StoreName { get; }

    /// <summary>The detail, without any secret material.</summary>
    public string Detail { get; }

    private static string StoreNameOf(string? message)
    {
        int at = message?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        return at > 0 ? message![..at] : message ?? "secret store";
    }

    private static string DetailOf(string? message)
    {
        int at = message?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        return at > 0 ? message![(at + 2)..] : "unavailable";
    }
}
