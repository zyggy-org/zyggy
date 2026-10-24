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
    /// <remarks><paramref name="fields"/> <see langword="null"/> keeps <c>JobFields("probe", TimeoutMinutes: 2)</c> (plan 06, additive); <paramref name="priority"/> is the §4 header field (plan 07, additive).</remarks>
    public static async Task<Model> SignedJobAsync(MachineName from, MachineName to, ISecretStore store, KeyId signWith, string body, CancellationToken ct, EnvelopeId? id = null, JobFields? fields = null, EnvelopePriority priority = EnvelopePriority.Normal)
    {
        var header = new EnvelopeHeader(signWith.Tenant, id ?? EnvelopeId.New(FixedInstant), from, to, FixedInstant, priority);
        Model job = Model.CreateJob(header, fields ?? new JobFields("probe", TimeoutMinutes: 2), Encoding.UTF8.GetBytes(body));
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(job, signWith, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        return signed.Envelope!;
    }

    /// <summary>A signed report from <paramref name="from"/> to <paramref name="to"/> answering <paramref name="inReplyTo"/> (plan 08, additive).</summary>
    public static async Task<Model> SignedReportAsync(MachineName from, MachineName to, EnvelopeId inReplyTo, ISecretStore store, KeyId signWith, string body, CancellationToken ct, ReportStatus status = ReportStatus.Done, string? reason = null, decimal? costUsd = null, EnvelopeId? id = null, DateTimeOffset? created = null)
    {
        DateTimeOffset at = created ?? FixedInstant;
        var header = new EnvelopeHeader(signWith.Tenant, id ?? EnvelopeId.New(at), from, to, at, EnvelopePriority.Normal, inReplyTo);
        Model report = Model.CreateReport(header, new ReportFields(status, reason, CostUsd: costUsd), Encoding.UTF8.GetBytes(body));
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(report, signWith, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        return signed.Envelope!;
    }

    /// <summary>The bytes of a signed report, optionally mutated after signing.</summary>
    public static async Task<byte[]> SignedReportBytesAsync(MachineName from, MachineName to, EnvelopeId inReplyTo, ISecretStore store, KeyId signWith, string body, CancellationToken ct, Func<byte[], byte[]>? mutate = null, ReportStatus status = ReportStatus.Done, string? reason = null, decimal? costUsd = null, EnvelopeId? id = null, DateTimeOffset? created = null)
    {
        byte[] bytes = EnvelopeWriter.Serialize(await SignedReportAsync(from, to, inReplyTo, store, signWith, body, ct, status, reason, costUsd, id, created));
        return mutate is null ? bytes : mutate(bytes);
    }

    /// <summary>The bytes of a signed job, optionally mutated after signing.</summary>
    public static async Task<byte[]> SignedJobBytesAsync(MachineName from, MachineName to, ISecretStore store, KeyId signWith, string body, CancellationToken ct, Func<byte[], byte[]>? mutate = null, EnvelopeId? id = null)
    {
        byte[] bytes = EnvelopeWriter.Serialize(await SignedJobAsync(from, to, store, signWith, body, ct, id));
        return mutate is null ? bytes : mutate(bytes);
    }
}
