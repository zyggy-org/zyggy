using Zyggy.Core.Envelope;

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
