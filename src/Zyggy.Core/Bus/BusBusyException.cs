namespace Zyggy.Core.Bus;

/// <summary>A read-only verb could not take the checkout's write lock within the wait: another zyggy process held it (plan 08 A11; the verb exits 3).</summary>
public sealed class BusBusyException : Exception
{
    /// <summary>The message every caller prints.</summary>
    public const string DefaultMessage = "the bus checkout is busy: another zyggy process held its lock";

    /// <summary>Creates the exception with the default message.</summary>
    public BusBusyException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public BusBusyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public BusBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
