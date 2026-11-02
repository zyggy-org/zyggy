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

    /// <summary>The bytes of a signed context envelope (spec 13, additive), optionally mutated after signing.</summary>
    public static async Task<byte[]> SignedContextBytesAsync(MachineName from, MachineName to, ISecretStore store, KeyId signWith, string body, DateTimeOffset created, CancellationToken ct, ContextScope? scope = null, EnvelopeId? id = null, Func<byte[], byte[]>? mutate = null)
    {
        var header = new EnvelopeHeader(signWith.Tenant, id ?? EnvelopeId.New(created), from, to, created);
        Model context = Model.CreateContext(header, new ContextFields(scope ?? ContextScope.Parse("project:calizr")), Encoding.UTF8.GetBytes(body));
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(context, signWith, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        return mutate is null ? bytes : mutate(bytes);
    }

    /// <summary>
    /// The bytes of a signed <c>via: bus</c> item (plan 41, additive) carrying <paramref name="file"/>, with the placeholder project
    /// <c>calizr</c>, optionally mutated after signing.
    /// </summary>
    public static async Task<byte[]> SignedItemBytesAsync(
        MachineName from,
        MachineName to,
        ISecretStore store,
        KeyId signWith,
        byte[] file,
        DateTimeOffset created,
        CancellationToken ct,
        string slug = "staging-restore-notes",
        string name = "Staging restore notes",
        string description = "How the staging database was restored",
        string sourceName = "restore-notes.md",
        EnvelopeId? id = null,
        Func<byte[], byte[]>? mutate = null)
    {
        var header = new EnvelopeHeader(signWith.Tenant, id ?? EnvelopeId.New(created), from, to, created);
        var fields = new ItemFields(
            Zyggy.Core.Memory.Slug.Parse("calizr"),
            Zyggy.Core.Memory.Slug.Parse(slug),
            name,
            description,
            sourceName,
            file.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)),
            ItemRoute.Bus,
            null);
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(Model.CreateItem(header, fields, file), signWith, ct);
        signed.IsAccepted.Should().BeTrue(signed.Rejection?.ToString());
        byte[] bytes = EnvelopeWriter.Serialize(signed.Envelope!);
        return mutate is null ? bytes : mutate(bytes);
    }

    /// <summary>The bytes of a signed OneDrive-route item pointer (plan 41, additive): <c>drive_name</c> = <c>&lt;slug&gt;.txt</c>, empty body.</summary>
    public static async Task<byte[]> SignedOneDriveItemBytesAsync(
        MachineName from, ISecretStore store, KeyId signWith, byte[] file, DateTimeOffset created, CancellationToken ct, EnvelopeId? id = null, string driveName = "staging-restore-notes.txt") =>
        EnvelopeWriter.Serialize(await SignedOneDriveItemAsync(from, store, signWith, file, created, ct, id, driveName));

    /// <summary>A signed OneDrive-route item pointer (plan 41 Step 11): <c>drive_name</c> = <paramref name="driveName"/>, empty body.</summary>
    public static async Task<Model> SignedOneDriveItemAsync(
        MachineName from, ISecretStore store, KeyId signWith, byte[] file, DateTimeOffset created, CancellationToken ct, EnvelopeId? id = null, string driveName = "staging-restore-notes.txt")
    {
        var header = new EnvelopeHeader(signWith.Tenant, id ?? EnvelopeId.New(created), from, MachineName.Central, created);
        var fields = new ItemFields(
            Zyggy.Core.Memory.Slug.Parse("calizr"),
            Zyggy.Core.Memory.Slug.Parse("staging-restore-notes"),
            "Staging restore notes",
            "How the staging database was restored",
            "restore-notes.md",
            file.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file)),
            ItemRoute.OneDrive,
            driveName);
        EnvelopeResult signed = await new EnvelopeSigner(store).SignAsync(Model.CreateItem(header, fields, ReadOnlyMemory<byte>.Empty), signWith, ct);
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
