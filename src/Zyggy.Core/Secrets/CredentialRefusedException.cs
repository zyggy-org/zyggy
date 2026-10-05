namespace Zyggy.Core.Secrets;

/// <summary>A credential exists but may not be used (mode, owner, header, tenant). The message never holds a byte of it.</summary>
public sealed class CredentialRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CredentialRefusedException()
    {
    }

    /// <summary>Creates the exception with the refusal text.</summary>
    /// <param name="message">The refusal, for example <c>key: /path must be mode 0600 (is 644)</c>.</param>
    public CredentialRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the refusal text and its cause.</summary>
    /// <param name="message">The refusal.</param>
    /// <param name="innerException">The cause.</param>
    public CredentialRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
