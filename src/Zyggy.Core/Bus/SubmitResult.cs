using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Bus;

/// <summary>What <see cref="JobSubmitter.BuildAsync"/> produced (spec 08 AC-2).</summary>
public abstract record BuildResult
{
    private protected BuildResult()
    {
    }
}

/// <summary>The signed envelope and the exact bytes a submit writes, after the round-trip self-check passed.</summary>
/// <param name="Envelope">The signed envelope.</param>
/// <param name="Bytes">Its serialised form, byte for byte what the bus will hold.</param>
public sealed record Built(Envelope.Envelope Envelope, byte[] Bytes) : BuildResult;

/// <summary>The self-check failed: the bytes would not be read back as the request. Nothing is written.</summary>
/// <param name="Field">The first field that differs (or <c>sig</c> / <c>envelope</c>).</param>
/// <param name="Detail">Why.</param>
public sealed record BuildRefused(string Field, string Detail) : BuildResult;

/// <summary>A line of the context body matches a secret pattern; nothing was signed and the text is kept nowhere (spec 12 AC-7).</summary>
/// <param name="PatternName">The pattern's name, the only thing reported.</param>
public sealed record BuildRefusedByPattern(string PatternName) : BuildResult;

/// <summary>What <c>JobSubmitter.SubmitAsync</c> did (spec 08 Contracts).</summary>
public abstract record SubmitResult
{
    private protected SubmitResult()
    {
    }
}

/// <summary>The job is on the bus and pushed.</summary>
/// <param name="Id">The job id.</param>
/// <param name="RelativePath">Its path in the checkout.</param>
/// <param name="PushAttempts">How many pushes it took.</param>
public sealed record Submitted(EnvelopeId Id, string RelativePath, int PushAttempts) : SubmitResult;

/// <summary>The job is committed locally but not pushed (the next write sequence carries it).</summary>
/// <param name="Id">The job id.</param>
/// <param name="RelativePath">Its path in the checkout.</param>
/// <param name="PushAttempts">How many pushes were tried.</param>
/// <param name="Detail">Git's redacted reason.</param>
public sealed record SubmitDeferred(EnvelopeId Id, string RelativePath, int PushAttempts, string Detail) : SubmitResult;

/// <summary>The write sequence failed at <paramref name="Stage"/> (<c>sign</c>, <c>lock</c>, <c>pull</c>, <c>add</c>, <c>commit</c>); the checkout is restored.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Detail">The redacted reason.</param>
public sealed record SubmitFailed(string Stage, string Detail) : SubmitResult;

/// <summary>The self-check refused the envelope; nothing was written.</summary>
/// <param name="Field">The field.</param>
/// <param name="Detail">Why.</param>
public sealed record SubmitRefused(string Field, string Detail) : SubmitResult;

/// <summary>A context line matched a secret pattern; nothing was signed or written (spec 12 AC-7, exit 5).</summary>
/// <param name="PatternName">The pattern's name.</param>
public sealed record ContextRefused(string PatternName) : SubmitResult;

/// <summary>A laptop check refused the file before anything was signed or written (spec 41 AC-12..AC-15, exit 5).</summary>
/// <param name="Refusal">The archive's refusal.</param>
/// <param name="Detail">Its detail token (a location word, a size, a pattern name with <c>line &lt;n&gt;</c> / <c>name</c> / <c>description</c>) — never item text.</param>
public sealed record ItemRefused(ArchiveRefusal Refusal, string? Detail) : SubmitResult;

/// <summary>A usage fault found only after the file was read: the composed index line would exceed 400 characters (spec 41 AC-9, exit 2).</summary>
/// <param name="Message">The usage message.</param>
public sealed record ItemUsage(string Message) : SubmitResult;

/// <summary>The configured OneDrive archive folder cannot be used (spec 41 AC-21, exit 6); nothing was written and there is no silent fallback.</summary>
/// <param name="Reason">The wire word: <c>missing</c>, <c>not_a_folder</c> or <c>access_denied</c>.</param>
/// <param name="Folder">The configured folder.</param>
public sealed record OneDriveUnavailable(string Reason, string Folder) : SubmitResult;

/// <summary>Writing the file into the OneDrive archive folder failed or did not read back (spec 41 AC-22, exit 6); the file this call created was removed.</summary>
/// <param name="Detail">The detail.</param>
public sealed record OneDriveWriteFailed(string Detail) : SubmitResult;
