using System.Security.Cryptography;

using Zyggy.Core.Envelope;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Bus;

/// <summary>
/// Spec 61 "the same job": the SHA-256, as 64 lowercase hexadecimal characters, of the job's signed content — the canonical form
/// (<see cref="EnvelopeSigner.Canonicalize"/>, unchanged) with <c>attempt</c> set to 1 and without <c>key_id</c> and <c>sig</c>. Those three
/// are exactly what Central's sweep changes when it re-queues a claim (§4 state machine), so a re-queued copy has the fingerprint of the job
/// it re-queues; any other change — the body, <c>allowed_tools</c>, the front-matter <c>id</c>, any field — gives another fingerprint.
/// </summary>
internal static class JobFingerprint
{
    /// <summary>The fingerprint of <paramref name="job"/>.</summary>
    /// <param name="job">A parsed envelope; a non-job is fingerprinted over its canonical form without <c>sig</c>.</param>
    /// <returns>64 lowercase hexadecimal characters.</returns>
    public static string Of(Model job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Model content = job.Type == EnvelopeType.Job ? job.WithAttempt(1) : job;
        return Convert.ToHexStringLower(SHA256.HashData(EnvelopeSigner.Canonicalize(content)));
    }
}
