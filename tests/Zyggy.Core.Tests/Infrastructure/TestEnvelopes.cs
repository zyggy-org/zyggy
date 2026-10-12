using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>Signed envelopes built with the production factory and signer — the unit counterpart of the integration fixture's seeder.</summary>
public static class TestEnvelopes
{
    public static DateTimeOffset FixedInstant { get; } = new(2026, 9, 27, 14, 5, 0, TimeSpan.Zero);

    /// <summary>A signed <c>probe</c> job from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static async Task<Model> SignedJobAsync(MachineName from, MachineName to, ISecretStore store, KeyId signWith, string body, CancellationToken ct, EnvelopeId? id = null)
    {
        var header = new EnvelopeHeader(signWith.Tenant, id ?? EnvelopeId.New(FixedInstant), from, to, FixedInstant);
        Model job = Model.CreateJob(header, new JobFields("probe", TimeoutMinutes: 2), Encoding.UTF8.GetBytes(body));
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(job, signWith, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        return signed.Envelope!;
    }

    /// <summary>The bytes of a signed job, optionally mutated after signing.</summary>
    public static async Task<byte[]> SignedJobBytesAsync(MachineName from, MachineName to, ISecretStore store, KeyId signWith, string body, CancellationToken ct, Func<byte[], byte[]>? mutate = null, EnvelopeId? id = null)
    {
        byte[] bytes = EnvelopeWriter.Serialize(await SignedJobAsync(from, to, store, signWith, body, ct, id));
        return mutate is null ? bytes : mutate(bytes);
    }
}
