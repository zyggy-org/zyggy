using System.Security.Cryptography;

using Zyggy.Core.Envelope;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.Setup;

/// <summary>
/// Names every secret a laptop needs (spec 42 AC-19, plan 42 A4): the <c>signWith</c> key and <c>github-pat</c>, each present with its fingerprint
/// or missing with the exact <c>zyggy secret set</c> line — names and fingerprints only, the value copy zeroed as <c>secret list</c> does. A spike
/// file store left under <c>&lt;LocalAppData&gt;/zyggy/secrets/&lt;tenant&gt;</c> is named when the machine uses the OS store; nothing inside it is read.
/// </summary>
internal static class SecretReport
{
    private static readonly SecretName GitHubPat = SecretName.Parse("github-pat");

    public static async Task<IReadOnlyList<SetupLine>> ReportAsync(ISecretStore store, SetupRequest request, KeyId signWith, SecretStoreKind storeKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signWith);
        var lines = new List<SetupLine>();
        try
        {
            IReadOnlyList<SecretName> names = await store.ListAsync(request.Tenant, ct).ConfigureAwait(false);
            string? key = names.Contains(signWith.SecretName) ? await FingerprintAsync(store, request, signWith.SecretName, ct).ConfigureAwait(false) : null;
            string? pat = names.Contains(GitHubPat) ? await FingerprintAsync(store, request, GitHubPat, ct).ConfigureAwait(false) : null;
            lines.Add(key is null
                ? new SetupLine(SetupItem.SecretSigningKey, SetupOutcome.Missing, "run: zyggy secret set " + signWith.SecretName + " --hex")
                : new SetupLine(SetupItem.SecretSigningKey, SetupOutcome.AlreadyDone, "present (fingerprint " + key + ")"));
            lines.Add(pat is null
                ? new SetupLine(SetupItem.SecretGitHubPat, SetupOutcome.Missing, "run: zyggy secret set " + GitHubPat)
                : new SetupLine(SetupItem.SecretGitHubPat, SetupOutcome.AlreadyDone, "present (fingerprint " + pat + ")"));
        }
        catch (SecretStoreUnavailableException ex)
        {
            string detail = "secret store unavailable: " + ex.Message;
            lines.Add(new SetupLine(SetupItem.SecretSigningKey, SetupOutcome.Failed, detail));
            lines.Add(new SetupLine(SetupItem.SecretGitHubPat, SetupOutcome.Failed, detail));
        }

        string spike = SetupPaths.SpikeStore(request.LocalAppData, request.Tenant);
        if (storeKind == SecretStoreKind.Os && Directory.Exists(spike))
        {
            lines.Add(new SetupLine(SetupItem.SpikeFileStore, SetupOutcome.LeftAsIs, $"a spike file store exists at {spike}; its keys are retired (rotate-hmac.md step 0) and not imported"));
        }

        return lines;
    }

    /// <summary>The fingerprint of a stored value, or <see langword="null"/> when it is absent or empty; the copy is zeroed.</summary>
    private static async Task<string?> FingerprintAsync(ISecretStore store, SetupRequest request, SecretName name, CancellationToken ct)
    {
        ReadOnlyMemory<byte>? value = await store.GetAsync(request.Tenant, name, ct).ConfigureAwait(false);
        if (value is not { IsEmpty: false } present)
        {
            return null;
        }

        byte[] copy = present.ToArray();
        try
        {
            return SecretFingerprint.Of(copy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }
}
