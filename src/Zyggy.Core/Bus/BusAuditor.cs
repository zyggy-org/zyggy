using Microsoft.Extensions.Logging;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>
/// The §8 audit (spec 08 AC-4, A8-2): every file under this tenant's subtree parsed and verified against the path's tenant (the §4
/// outcome first), then checked for the write rules — the file name is the <c>id</c>, the type belongs in its folder, the writer is the
/// folder's machine. Other entries under <c>tenants/</c> are named and never read (A8-1). Read-only, under the checkout's write lock,
/// never the listen lock; the progress callback fires every 500 files.
/// </summary>
public sealed partial class BusAuditor
{
    /// <summary>How many files between two progress reports.</summary>
    public const int ProgressEvery = 500;

    private const string EnvelopeExtension = ".md";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    private readonly BusRepository _bus;
    private readonly string _root;
    private readonly EnvelopeSigner _signer;
    private readonly TimeProvider _clock;
    private readonly ILogger<BusAuditor> _log;
    private readonly BusIdentity _me;

    /// <summary>Creates the auditor over the checkout at <paramref name="checkoutRoot"/>.</summary>
    /// <param name="bus">The bus repository (its identity names the tenant; its <c>HEAD</c> is reported).</param>
    /// <param name="checkoutRoot">The checkout root.</param>
    /// <param name="signer">The envelope signer.</param>
    /// <param name="clock">The clock (the lock's deadline; plan 08 A15).</param>
    /// <param name="log">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public BusAuditor(BusRepository bus, string checkoutRoot, EnvelopeSigner signer, TimeProvider clock, ILogger<BusAuditor> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        ArgumentException.ThrowIfNullOrEmpty(checkoutRoot);
        _root = checkoutRoot;
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _me = bus.Me;
    }

    /// <summary>Audits the tenant's subtree.</summary>
    /// <param name="progress">Called with the file count every <see cref="ProgressEvery"/> files; <see langword="null"/> for none.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The report.</returns>
    /// <exception cref="BusBusyException">Another zyggy process held the checkout's write lock for the whole wait.</exception>
    public async Task<AuditReport> RunAsync(IProgress<int>? progress, CancellationToken ct)
    {
        using IDisposable handle = await BusCheckoutLock.AcquireAsync(_root, BusCheckoutLock.WriteLockName, LockTimeout, _clock, ct).ConfigureAwait(false)
            ?? throw new BusBusyException();
        var foreign = new List<string>();
        var unexpected = new List<string>();
        string tenantsRoot = Path.Join(_root, BusPaths.Tenants);
        if (Directory.Exists(tenantsRoot))
        {
            foreach (string name in Directory.EnumerateDirectories(tenantsRoot).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).Order(StringComparer.Ordinal))
            {
                if (string.Equals(name, _me.Tenant.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TenantId.TryParse(name, out _))
                {
                    foreign.Add(name);
                }
                else
                {
                    unexpected.Add(BusPaths.Tenants + "/" + name + "/");
                }
            }
        }

        var entries = new List<AuditEntry>();
        string tenantDir = BusPaths.Resolve(_root, BusPaths.Tenant(_me.Tenant));
        if (Directory.Exists(tenantDir))
        {
            IEnumerable<string> files = Directory.EnumerateFiles(tenantDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal);
            var count = 0;
            foreach (string path in files)
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(await JudgeAsync(path, ct).ConfigureAwait(false));
                count++;
                if (count % ProgressEvery == 0)
                {
                    progress?.Report(count);
                }
            }
        }

        string? head = await _bus.HeadShaAsync(ct).ConfigureAwait(false);
        var report = new AuditReport(head, entries, foreign, unexpected);
        LogDone(report.Failed, report.ForeignTenants.Count);
        return report;
    }

    /// <summary>One file: a §4 location with an id is an envelope; a folder location's non-envelope name is skipped; an <c>.md</c> whose name is not a ULID is judged; the rest is unexpected (the root <c>policy.yaml</c> skipped until deliverable 18).</summary>
    private async Task<AuditEntry> JudgeAsync(string path, CancellationToken ct)
    {
        if (BusPaths.TryParse(path, out BusLocation? location))
        {
            if (location.Folder == BusFolder.Registry)
            {
                return new AuditEntry(path, AuditOutcome.Skipped, null, "registry file (unsigned in v1)");
            }

            return await JudgeEnvelopeAsync(path, location, ct).ConfigureAwait(false);
        }

        string fileName = path[(path.LastIndexOf('/') + 1)..];
        string parent = path[..path.LastIndexOf('/')];
        if (BusPaths.TryParse(parent, out BusLocation? folder) && folder.Id is null && folder.Folder != BusFolder.Registry)
        {
            if (!fileName.EndsWith(EnvelopeExtension, StringComparison.Ordinal))
            {
                return new AuditEntry(path, AuditOutcome.Skipped, null, "not an envelope name");
            }

            return await JudgeEnvelopeAsync(path, folder, ct).ConfigureAwait(false);
        }

        if (string.Equals(parent, BusPaths.Tenant(_me.Tenant), StringComparison.Ordinal) && string.Equals(fileName, "policy.yaml", StringComparison.Ordinal))
        {
            return new AuditEntry(path, AuditOutcome.Skipped, null, "policy file (signed and checked from deliverable 18)");
        }

        // The registry folder is a §4 place whose only envelopes-to-be are the machine files; a keep file or a note there is not drift.
        if (string.Equals(parent, BusPaths.RegistryFolder(_me.Tenant), StringComparison.Ordinal) && !fileName.EndsWith(EnvelopeExtension, StringComparison.Ordinal))
        {
            return new AuditEntry(path, AuditOutcome.Skipped, null, "not an envelope name");
        }

        LogUnexpected(path);
        return new AuditEntry(path, AuditOutcome.UnexpectedPath, null, "not a §4 path");
    }

    private async Task<AuditEntry> JudgeEnvelopeAsync(string path, BusLocation location, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(BusPaths.Resolve(_root, path), ct).ConfigureAwait(false);
        EnvelopeResult result = await _signer.ParseAndVerifyAsync(bytes, location.Tenant, ct).ConfigureAwait(false);
        if (!result.IsAccepted)
        {
            EnvelopeRejection rejection = result.Rejection!;
            string detail = rejection.Detail;
            if (rejection.Reason == EnvelopeRejectionReason.UnknownKey && KeyIdOf(bytes) is { } keyId)
            {
                detail = keyId + ": " + detail;
            }

            return new AuditEntry(path, AuditWire.FromRejection(rejection.Reason), rejection.Field, detail);
        }

        Envelope.Envelope envelope = result.Envelope!;
        if (location.Id is not null && envelope.Header.Id != location.Id)
        {
            return new AuditEntry(path, AuditOutcome.IdDiffers, "id", "front-matter id " + envelope.Header.Id.Value + " ≠ file name");
        }

        if (location.Id is null)
        {
            return new AuditEntry(path, AuditOutcome.IdDiffers, "id", "file name is not the envelope's id");
        }

        EnvelopeType? expected = location.Folder switch
        {
            BusFolder.Jobs or BusFolder.Claimed => EnvelopeType.Job,
            BusFolder.Reports => EnvelopeType.Report,
            BusFolder.Context => EnvelopeType.Context,
            _ => null,
        };
        if (expected is { } type && envelope.Type != type)
        {
            return new AuditEntry(path, AuditOutcome.TypeFolderMismatch, "type", EnvelopeWire.ToWire(envelope.Type) + " in " + location.Folder.ToString().ToLowerInvariant() + "/");
        }

        if (location.Machine is { } machine)
        {
            switch (location.Folder)
            {
                case BusFolder.Reports or BusFolder.Context when envelope.Header.From != machine:
                    return new AuditEntry(path, AuditOutcome.WriterMismatch, "from", "from " + envelope.Header.From.Value + " in " + machine.Value + "'s folder");
                case BusFolder.Jobs or BusFolder.Claimed or BusFolder.Rejected when envelope.Header.To != machine:
                    return new AuditEntry(path, AuditOutcome.WriterMismatch, "to", "to " + envelope.Header.To.Value + " in " + machine.Value + "'s folder");
                default:
                    break;
            }
        }

        return new AuditEntry(path, AuditOutcome.Ok, null, null);
    }

    /// <summary>The <c>key_id</c> scalar read leniently (the unknown-key detail names the key — ex-Q3).</summary>
    private static string? KeyIdOf(byte[] bytes)
    {
        string[] lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line == "---")
            {
                break;
            }

            if (line.StartsWith("key_id: ", StringComparison.Ordinal))
            {
                return line["key_id: ".Length..].Trim();
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "verify: {Failed} failed, {Foreign} foreign tenant director(ies)")]
    private partial void LogDone(int failed, int foreign);

    [LoggerMessage(Level = LogLevel.Debug, Message = "verify: unexpected path {Path}")]
    private partial void LogUnexpected(string path);
}
